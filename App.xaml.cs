using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    public partial class App : System.Windows.Application
    {
        private const string MutexName = "AdvancedSnip_SingleInstance_5C1B7B2A";

        private Mutex?           _mutex;
        private AppSettings      _settings = null!;
        private ClipboardHistory _history  = null!;
        private HotKeyManager    _hotkeys  = null!;
        private readonly OcrIndex _ocr = new();
        private TrayIconManager  _tray     = null!;

        private HistoryWindow?  _historyWindow;
        private MainWindow?     _settingsWindow;
        private EditorWindow?   _editorWindow;
        private PdfViewWindow?  _pdfWindow;

        /// <summary>The window being recorded, or zero when recording the screen.</summary>
        private IntPtr _recordTargetWindow;

        // What the most recent "saved" notification refers to.
        private System.Windows.Media.Imaging.BitmapSource? _lastImage;
        private string? _lastPath;
        private bool           _snipping;
        private bool           _scrollCapturing;
        private CancellationTokenSource? _timerSnipCts;
        private SnipCountdownOverlay?    _timerCountdownOverlay;

        // ── recording ──
        private ScreenRecorder?          _recorder;
        private RecordHudWindow?         _recordHud;
        private RecordAnnotationWindow?  _recordInk;
        private string?                  _recordPath;

        /// <summary>Id of the Escape hotkey held only while the pen is on; 0 when not held.</summary>
        private int                      _penEscapeHotkeyId;

        private ZoomController?          _recordZoom;
        private RecordToolPalette?       _recordPalette;

        /// <summary>
        /// Muting is applied at the mixer's gain rather than by stopping capture, so the
        /// timeline keeps advancing and the muted stretch is silence of the right length
        /// instead of a hole the sound has to catch up across.
        /// </summary>
        private bool                     _recordMuted;

        private string?                  _lastVideoPath;
        private VideoReviewWindow?       _reviewWindow;
        private readonly List<int>       _recordingHotkeyIds = new();

        /// <summary>True while a recording is running, so the UI can offer Stop.</summary>
        public bool IsRecording => _recorder is { IsRunning: true };

        /// <summary>Raised when a recording starts or stops, so the Record tab can follow.</summary>
        public event EventHandler? RecordingStateChanged;

        public AppSettings Settings    => _settings;

        /// <summary>
        /// The recognised-text cache, shared so the editor and the gallery never read the
        /// same image twice.
        /// </summary>
        internal OcrIndex  Ocr         => _ocr;
        public bool        IsExiting   { get; private set; }

        /// <summary>Raised on the UI thread after a snip is successfully saved/captured.</summary>
        public event EventHandler? SnipCompleted;

        /// <summary>Raised when the clipboard history changes (items added, removed, or cleared).</summary>
        public event EventHandler? HistoryChanged;

        // ─────────────────────────────── startup ──────────────────────────────

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Register global crash and emergency handlers first thing
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledDomainCrash;
            DispatcherUnhandledException += OnDispatcherCrash;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskCrash;

            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (!createdNew) { Shutdown(); return; }

            System.Windows.Forms.Application.EnableVisualStyles();

            // Initialize desktop wallpaper composition and recover from any prior abnormal termination
            try { DesktopWallpaperHost.Initialize(); } catch { }

            _settings = AppSettings.Load();
            EnsureSaveFolder();

            // Before any window exists, so the first one drawn is already correct rather
            // than flashing light and repainting.
            ThemeManager.Apply(_settings.Theme);

            _ocr.Load();
            _history = new ClipboardHistory(_settings.MaxHistory);
            _history.Changed += (_, _) => HistoryChanged?.Invoke(this, EventArgs.Empty);
            _hotkeys = new HotKeyManager();
            var failed = RegisterHotkeys();

            _tray = new TrayIconManager();
            _tray.OpenRequested           += (_, _) => ShowGallery();
            _tray.SnipRequested           += (_, _) => DoSnip();
            _tray.OcrRequested            += (_, _) => DoRealtimeOcr();
            _tray.SettingsRequested       += (_, _) => ShowSettings();
            _tray.ExitRequested           += (_, _) => ExitApp();
            _tray.RecordRequested         += (_, _) => ToggleRecording();
            _tray.SlapAllRequested        += (_, _) => PinnedWindowManager.SlapAllToDesktop();
            _tray.UnslapAllRequested      += (_, _) => PinnedWindowManager.UnslapAllFromDesktop();
            _tray.SetShortcuts(_settings.SnipHotkey.ToString(), _settings.RecordHotkey.ToString(), _settings.OcrHotkey.ToString());
            _tray.Show();

            // The menu says "Stop recording" while one is running, and a recording can be
            // stopped from the floating toolbar or a hotkey without the tray hearing.
            RecordingStateChanged += (_, _) => _tray.SetRecording(IsRecording);

            _tray.BalloonClicked += (_, _) => OnBalloonClicked();

            // Re-verified on every launch: this repairs the entry in place if the app has
            // been moved or rebuilt to a new path since it was set.
            StartupManager.Reconcile(_settings.RunAtStartup);

            if (failed.Count > 0)
                _tray.ShowBalloon("Some hotkeys are in use",
                    string.Join(", ", failed) + " — change them in Settings.", tag: "hotkeys");

            // A sign-in launch always goes quietly to the tray. Having the settings window
            // appear over whatever you're doing every time you log in is the fastest way
            // to make someone turn the feature off.
            bool launchedByWindows = StartupManager.LaunchedAtStartup(e.Args);
            if (_settings.ShowSettingsOnStartup && !launchedByWindows) ShowGallery();

            // Restore any persistent desktop-pinned images across app and Windows restarts
            PinnedWindowManager.RestoreAll();
        }

        private void OnUnhandledDomainCrash(object sender, UnhandledExceptionEventArgs e)
        {
            EmergencyCleanup("AppDomain UnhandledException", e.ExceptionObject as Exception);
        }

        private void OnDispatcherCrash(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            EmergencyCleanup("Dispatcher UnhandledException", e.Exception);
        }

        private void OnUnobservedTaskCrash(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            EmergencyCleanup("TaskScheduler UnobservedTaskException", e.Exception);
        }

        private void EmergencyCleanup(string source, Exception? ex)
        {
            try
            {
                DesktopWallpaperHost.Log($"EmergencyCleanup triggered by {source}: {ex?.Message}");
                DesktopWallpaperHost.BeginShutdown();

                try { PinnedWindowManager.CloseAll(); } catch { }

                try
                {
                    var openWindows = new List<Window>();
                    foreach (Window win in Windows)
                    {
                        openWindows.Add(win);
                    }
                    foreach (var win in openWindows)
                    {
                        try { win.Close(); } catch { }
                    }
                }
                catch { }

                try { DesktopWallpaperHost.Shutdown(); } catch { }
                try { DesktopWallpaperHost.ForceDesktopShellRefresh(); } catch { }
            }
            catch { }
        }

        private void ExitApp()
        {
            if (IsExiting) return;
            IsExiting = true;

            try { DesktopWallpaperHost.BeginShutdown(); } catch { }
            try { _tray?.Dispose(); } catch { }
            try { PinnedWindowManager.SaveAll(); } catch { }
            try { PinnedWindowManager.CloseAll(); } catch { }

            try
            {
                var openWindows = new List<Window>();
                foreach (Window win in Windows)
                {
                    openWindows.Add(win);
                }
                foreach (var win in openWindows)
                {
                    try { win.Close(); } catch { }
                }
            }
            catch { }

            try { DesktopWallpaperHost.Shutdown(); } catch { }
            try { DesktopWallpaperHost.ForceDesktopShellRefresh(); } catch { }
            try { _recorder?.Stop(); } catch { }
            try { TearDownRecordingUi(); } catch { }
            try { _ocr.Stop(); } catch { }
            try { _ocr.Save(); } catch { }
            try { ThemeManager.Shutdown(); } catch { }
            try { _timerSnipCts?.Cancel(); } catch { }
            try { _timerCountdownOverlay?.Close(); } catch { }
            try { _hotkeys?.Dispose(); } catch { }
            try { _mutex?.Dispose(); } catch { }

            Environment.Exit(0);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (!IsExiting)
            {
                ExitApp();
                return;
            }
            base.OnExit(e);
        }

        // ─────────────────────────────── hotkeys ──────────────────────────────

        private List<string> RegisterHotkeys()
        {
            _hotkeys.UnregisterAll();
            var failed = new List<string>();
            if (!_hotkeys.TryRegister(_settings.SnipHotkey,          DoSnip))
                failed.Add($"Snip ({_settings.SnipHotkey})");
            if (!_hotkeys.TryRegister(_settings.SnipTimerHotkey,     () => DoSnipWithTimer()))
                failed.Add($"Timed Snip ({_settings.SnipTimerHotkey})");
            if (!_hotkeys.TryRegister(_settings.ScrollCaptureHotkey, DoScrollCapture))
                failed.Add($"Scroll Capture ({_settings.ScrollCaptureHotkey})");
            if (!_hotkeys.TryRegister(_settings.OcrHotkey,           DoRealtimeOcr))
                failed.Add($"OCR ({_settings.OcrHotkey})");
            if (!_hotkeys.TryRegister(_settings.HistoryHotkey, ToggleHistory))
                failed.Add($"History ({_settings.HistoryHotkey})");
            if (!_hotkeys.TryRegister(_settings.NextHotkey,    () => CyclePaste(forward: true)))
                failed.Add($"Next ({_settings.NextHotkey})");
            if (!_hotkeys.TryRegister(_settings.PrevHotkey,    () => CyclePaste(forward: false)))
                failed.Add($"Previous ({_settings.PrevHotkey})");
            if (!_hotkeys.TryRegister(_settings.RecordHotkey,  ToggleRecording))
                failed.Add($"Record ({_settings.RecordHotkey})");
            return failed;
        }

        // ─────────────────────────────── snip ─────────────────────────────────

        public void DoSnip()
        {
            if (_settings.ApplyTimerToSnipHotkey && _settings.SnipTimerSeconds > 0)
            {
                DoSnipWithTimer(_settings.SnipTimerSeconds);
                return;
            }
            DoSnipImmediate();
        }

        public void DoSnipImmediate()
        {
            if (_snipping) return;
            _snipping = true;
            try
            {
                var full = ScreenCapture.CaptureVirtualScreen(out _, out _);
                System.Drawing.Bitmap? crop = null;
                bool committed;
                try
                {
                    var overlay = new SnipOverlay(full, _settings.OverlayOpacity);
                    committed = overlay.ShowDialog() == true && overlay.ResultBitmap != null;
                    if (committed) crop = overlay.ResultBitmap;
                }
                finally { full.Dispose(); }

                if (!committed || crop == null) return;
                ProcessCapturedCrop(crop);
            }
            finally
            {
                _snipping = false;
                MemoryGuard.RequestTrim();
            }
        }

        public async void DoSnipWithTimer(int seconds = -1)
        {
            if (_snipping) return;
            if (seconds <= 0) seconds = _settings.SnipTimerSeconds;
            if (seconds <= 0) seconds = 3;

            _timerSnipCts?.Cancel();
            _timerSnipCts = new CancellationTokenSource();
            var token = _timerSnipCts.Token;

            _snipping = true;
            SnipCountdownOverlay? hud = null;

            try
            {
                hud = new SnipCountdownOverlay();
                _timerCountdownOverlay = hud;
                bool aborted = false;

                hud.SnapNowRequested += () =>
                {
                    try { _timerSnipCts?.Cancel(); } catch { }
                };

                hud.Cancelled += () =>
                {
                    aborted = true;
                    try { _timerSnipCts?.Cancel(); } catch { }
                };

                hud.Show();

                for (int rem = seconds; rem > 0; rem--)
                {
                    hud.UpdateCount(rem);
                    try
                    {
                        await Task.Delay(1000, token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }

                hud.Close();
                hud = null;
                _timerCountdownOverlay = null;

                if (aborted) return;

                // Brief settle delay for Windows DWM compositor to remove the overlay window
                await Task.Delay(60);

                var full = ScreenCapture.CaptureVirtualScreen(out _, out _);
                System.Drawing.Bitmap? crop = null;
                bool committed;
                try
                {
                    var overlay = new SnipOverlay(full, _settings.OverlayOpacity);
                    committed = overlay.ShowDialog() == true && overlay.ResultBitmap != null;
                    if (committed) crop = overlay.ResultBitmap;
                }
                finally { full.Dispose(); }

                if (!committed || crop == null) return;
                ProcessCapturedCrop(crop);
            }
            catch (Exception ex)
            {
                if (_settings.ShowTrayNotification)
                    _tray.ShowBalloon("Couldn't start timed snip", ex.Message);
            }
            finally
            {
                try { hud?.Close(); } catch { }
                _timerCountdownOverlay = null;
                _timerSnipCts = null;
                _snipping = false;
                MemoryGuard.RequestTrim();
            }
        }

        private void ProcessCapturedCrop(System.Drawing.Bitmap crop)
        {
            string? savedPath = TrySaveBitmap(crop);
            var image = ImageInterop.ToFrozenBitmapSourceAuto(crop);
            crop.Dispose();

            _history.Add(image, savedPath);

            ClipboardOutcome? clip = null;
            if (_settings.CopyToClipboardOnSnip)
                clip = ClipboardService.SetImage(image, savedPath);

            _lastPath  = savedPath;
            _lastImage = savedPath == null ? image : null;

            if (savedPath != null && _settings.GalleryOcrSearch)
                _ocr.EnqueueCapture(savedPath);

            if (_settings.ShowTrayNotification)
            {
                string detail = (savedPath != null ? Path.GetFileName(savedPath)
                                                   : "Added to clipboard history")
                    + (_settings.EditOnNotificationClick ? " — click to edit" : "");

                if (clip?.Warning is { } warning) detail += "\n" + warning;

                ShowCaptureToast(
                    savedPath != null ? "Snip saved" : "Snip captured",
                    detail, "capture", ImageInterop.MakeThumbnail(image, 128));
            }

            SnipCompleted?.Invoke(this, EventArgs.Empty);
        }

        // ─────────────────────────────── real-time OCR ─────────────────────────

        private bool _readingOcr;

        /// <summary>
        /// Real-time OCR Reader: allows selecting a screen region to extract and copy text directly,
        /// without saving any screenshots to disk or gallery.
        /// </summary>
        public async void DoRealtimeOcr()
        {
            if (_readingOcr || _snipping || _scrollCapturing) return;
            _readingOcr = true;

            try
            {
                var full = ScreenCapture.CaptureVirtualScreen(out _, out _);
                System.Drawing.Bitmap? crop = null;
                System.Drawing.Rectangle selectedRegion = System.Drawing.Rectangle.Empty;
                bool committed;

                try
                {
                    var overlay = new SnipOverlay(full, _settings.OverlayOpacity)
                    {
                        IsOcrMode = true
                    };
                    committed = overlay.ShowDialog() == true && overlay.ResultBitmap != null;
                    if (committed)
                    {
                        crop = overlay.ResultBitmap;
                        selectedRegion = overlay.SelectedRegion;
                    }
                }
                finally
                {
                    full.Dispose();
                }

                if (!committed || crop == null) return;

                var image = ImageInterop.ToFrozenBitmapSourceAuto(crop);
                crop.Dispose();

                string text = "";
                try
                {
                    text = await OcrService.ReadImageAsync(image);
                }
                catch { }

                text = text?.Trim() ?? "";

                if (!string.IsNullOrEmpty(text))
                {
                    try
                    {
                        Clipboard.SetDataObject(text, true);
                    }
                    catch { }

                    var ocrWin = new OcrResultWindow(text, selectedRegion);
                    ocrWin.OcrAgainRequested += (_, _) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() => DoRealtimeOcr()), System.Windows.Threading.DispatcherPriority.Background);
                    };
                    ocrWin.Show();
                }
                else
                {
                    if (_settings.ShowTrayNotification)
                        _tray.ShowBalloon("No text found", "Could not recognize any text in the selected area.");
                }
            }
            catch (Exception ex)
            {
                if (_settings.ShowTrayNotification)
                    _tray.ShowBalloon("OCR failed", ex.Message);
            }
            finally
            {
                _readingOcr = false;
                MemoryGuard.RequestTrim();
            }
        }

        // ─────────────────────────────── scroll capture ───────────────────────

        /// <summary>
        /// Point at a scrollable area, then let the app scroll it to the end and stitch
        /// every screenful into one tall image.
        /// </summary>
        public void DoScrollCapture()
        {
            if (_scrollCapturing || _snipping) return;
            _scrollCapturing = true;

            try
            {
                // Freeze the desktop first so the picker highlights a stable scene and
                // nothing can shuffle under the pointer while the user aims.
                TargetCandidate? target;
                var frozen = ScreenCapture.CaptureVirtualScreen(out _, out _);
                try
                {
                    var picker = new ScrollTargetOverlay(frozen, _settings.OverlayOpacity);
                    bool picked = picker.ShowDialog() == true && picker.Result != null;
                    target = picked ? picker.Result : null;
                }
                finally { frozen.Dispose(); }

                if (target == null) { _scrollCapturing = false; return; }

                string title = Win32.GetTitle(target.TopLevel);
                if (string.IsNullOrWhiteSpace(title)) title = Win32.GetClass(target.TopLevel);

                var progress = new ScrollProgressWindow(title);
                progress.Show();
                progress.KeepClearOf(target.Region);

                _ = RunScrollCaptureAsync(target, title, progress);
            }
            catch (Exception ex)
            {
                _scrollCapturing = false;
                if (_settings.ShowTrayNotification)
                    _tray.ShowBalloon("Scroll capture failed", ex.Message);
            }
        }

        private async Task RunScrollCaptureAsync(
            TargetCandidate target, string title, ScrollProgressWindow progressWin)
        {
            ScrollCaptureOutcome? outcome = null;

            try
            {
                var request = new ScrollCaptureRequest
                {
                    TopLevelHwnd = target.TopLevel,
                    TargetHwnd = target.Hwnd,
                    Region = target.Region,
                    Title = title,
                    MaxHeightPx = _settings.ScrollMaxHeight,
                    Speed = _settings.ScrollSpeedValue,
                    AutoDetectRegion = _settings.ScrollAutoDetectRegion,
                    RestoreScrollPosition = _settings.ScrollRestorePosition
                };

                var progress = new Progress<ScrollCaptureProgress>(p =>
                {
                    if (progressWin.IsVisible) progressWin.Update(p);
                });

                var token = progressWin.CancellationToken;

                // The capture loop does a lot of pixel work and blocking waits, so it runs
                // off the UI thread; Progress<T> marshals the updates back for us.
                outcome = await Task.Run(async () =>
                {
                    var engine = new ScrollCaptureEngine(request, progress, progressWin.Stop, token);
                    return await engine.RunAsync().ConfigureAwait(false);
                }, token);

                SafeClose(progressWin);

                string? savedPath = TrySaveBitmap(outcome.Image);
                var imageSource = ImageInterop.ToFrozenBitmapSourceAuto(outcome.Image);
                outcome.Image.Dispose();

                _history.Add(imageSource, savedPath);

                ClipboardOutcome? clip = null;
                if (_settings.CopyToClipboardOnSnip)
                    clip = ClipboardService.SetImage(imageSource, savedPath);

                // A scroll capture is exactly the kind of image you want to trim before
                // sending, so it has to reach the editor the same way a region snip does.
                // Recording it here — and tagging the balloon — is what makes clicking the
                // notification work; without both, OnBalloonClicked has nothing to open.
                _lastPath  = savedPath;
                _lastImage = savedPath == null ? imageSource : null;

                if (savedPath != null && _settings.GalleryOcrSearch)
                    _ocr.EnqueueCapture(savedPath);

                if (_settings.ShowTrayNotification)
                {
                    string detail = savedPath != null
                        ? $"{outcome.Frames} screens \u2192 {Path.GetFileName(savedPath)}"
                        : $"{outcome.Frames} screens added to clipboard history";

                    if (!string.IsNullOrWhiteSpace(outcome.Note))
                        detail += "\n" + outcome.Note;
                    else if (_settings.EditOnNotificationClick)
                        detail += " \u2014 click to edit";

                    // A tall capture is exactly the case where the clipboard has to
                    // degrade to PNG-only or to a file drop, and it is the case where
                    // the user most needs to be told before they try to paste it.
                    if (clip?.Warning is { } warning) detail += "\n" + warning;

                    ShowCaptureToast(
                        savedPath != null ? "Scroll capture saved" : "Scroll capture done",
                        detail, "capture", ImageInterop.MakeThumbnail(imageSource, 128));
                }

                SnipCompleted?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                outcome?.Image.Dispose();
                SafeClose(progressWin);
            }
            catch (Exception ex)
            {
                outcome?.Image.Dispose();
                SafeClose(progressWin);
                if (_settings.ShowTrayNotification)
                    _tray.ShowBalloon("Scroll capture failed", ex.Message);
            }
            finally
            {
                _scrollCapturing = false;
                MemoryGuard.RequestTrim();
            }
        }

        /// <summary>
        /// WPF throws if Close() is called on a window that has already gone. Without this
        /// guard a perfectly good capture could be reported as a failure.
        /// </summary>
        private static void SafeClose(System.Windows.Window w)
        {
            try { w.Close(); } catch { }
        }

        private string? TrySaveBitmap(System.Drawing.Bitmap bmp)
        {
            try
            {
                Directory.CreateDirectory(_settings.SaveFolder);
                bool jpeg = string.Equals(_settings.ImageFormat, "JPEG", StringComparison.OrdinalIgnoreCase);
                string ext  = jpeg ? "jpg" : "png";
                string prefix = string.IsNullOrWhiteSpace(_settings.FilenamePrefix) ? "Snip" : _settings.FilenamePrefix;
                string file = Path.Combine(_settings.SaveFolder,
                    $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.{ext}");

                if (jpeg)
                {
                    // Encode with the configured quality level.
                    var jpegCodec = GetJpegCodec();
                    if (jpegCodec != null)
                    {
                        using var ep = new System.Drawing.Imaging.EncoderParameters(1);
                        ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                            System.Drawing.Imaging.Encoder.Quality, (long)_settings.JpegQuality);
                        bmp.Save(file, jpegCodec, ep);
                    }
                    else
                        bmp.Save(file, System.Drawing.Imaging.ImageFormat.Jpeg);
                }
                else
                    bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);

                return file;
            }
            catch (Exception ex)
            {
                if (_settings.ShowTrayNotification)
                    _tray.ShowBalloon("Couldn't save the snip", ex.Message);
                return null;
            }
        }

        private static System.Drawing.Imaging.ImageCodecInfo? GetJpegCodec()
        {
            foreach (var codec in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders())
                if (codec.MimeType == "image/jpeg") return codec;
            return null;
        }

        // ─────────────────────────────── screen recording ─────────────────────

        /// <summary>
        /// One entry point for the hotkey, the tray and the Record tab. A recorder is a
        /// toggle by nature: whatever starts it has to be able to stop it, because the
        /// user may well have hidden every window by then.
        /// </summary>
        public void ToggleRecording()
        {
            if (IsRecording) { StopRecording(); return; }
            _ = StartRecordingAsync();
        }

        public async Task StartRecordingAsync()
        {
            if (IsRecording || _snipping || _scrollCapturing) return;

            try
            {
                var region = await ChooseRecordingRegionAsync();
                if (region.Width <= 0 || region.Height <= 0) return;

                string folder = _settings.VideoFolderOrDefault;
                Directory.CreateDirectory(folder);
                _recordPath = Path.Combine(folder,
                    $"{_settings.VideoPrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                var ink = new AnnotationLayer();
                var shapes = new ShapeLayer();
                var zoom = new ZoomController(region);
                _recordZoom = zoom;
                _recordMuted = false;

                // The overlay is created for every recording now, not only when the pen is
                // enabled: it also carries the zoom viewport outline. With no tool active
                // it is click-through and changes nothing.
                _recordInk = new RecordAnnotationWindow(ink, shapes, region);
                _recordInk.PenEnabledChanged += OnPenEnabledChanged;
                _recordInk.ToolChanged += OnRecordToolChanged;
                // Colour, width and the drawing itself can all now be changed from the
                // surface — the wheel does two of them — so the toolbar and the picker
                // learn about it the same way they learn about a tool change.
                _recordInk.SettingsChanged += (_, _) => SyncRecordUi();
                _recordInk.ContentChanged += (_, _) => SyncRecordUi();
                _recordInk.Show();
                // Starts click-through: no tool is active until the user asks for one, so
                // a window over the desktop changes nothing until then.
                _recordInk.SetTool(RecordTool.None);
                _recordInk.AttachZoom(zoom);

                zoom.FactorChanged += (_, f) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    _recordHud?.ShowZoom(f);
                    _recordPalette?.UpdateZoomLabel();
                }));

                _recordHud = new RecordHudWindow(_recordInk, _settings.RecordAnnotations);
                _recordHud.StopRequested += (_, _) => StopRecording();
                _recordHud.ToolsRequested += (_, _) => ShowToolPalette();
                _recordHud.PauseChanged += (_, paused) =>
                {
                    if (paused) _recorder?.Pause(); else _recorder?.Resume();
                };
                _recordHud.Show();
                _recordHud.PlaceNearCursor();
                // Shown after the overlay, so it starts above it. It re-asserts this every
                // time a tool goes on as well.
                _recordHud.RaiseAboveOverlay();

                // Built once and reused. Recreating it per invocation would lose the
                // colour and width the user picked a moment earlier, which is exactly the
                // state a quick picker exists to remember.
                if (_settings.RecordAnnotations)
                {
                    _recordPalette = new RecordToolPalette(_recordInk, zoom);
                    _recordPalette.Changed += (_, _) => SyncRecordUi();
                    _recordPalette.PinnedChanged += OnToolsPinnedChanged;

                    // Someone who pinned the picker last time wants it up now, without
                    // being asked again or having to summon it first. Restoring it here
                    // rather than on first use is the difference between a preference and
                    // a thing you have to remember to switch on.
                    if (_settings.RecordToolsPinned)
                    {
                        _recordPalette.SetPinned(true, notify: false);
                        if (_settings.RecordToolsX != int.MinValue &&
                            _settings.RecordToolsY != int.MinValue)
                            _recordPalette.MoveTo(_settings.RecordToolsX, _settings.RecordToolsY);
                        else
                            _recordPalette.ShowAtCursor();
                    }
                }

                RegisterRecordingHotkeys();

                if (_settings.RecordCountdown > 0)
                    await CountdownAsync(_settings.RecordCountdown);

                _recorder = new ScreenRecorder(new RecordingRequest
                {
                    Region = region,
                    TargetWindow = _recordTargetWindow,
                    Path = _recordPath,
                    Fps = _settings.RecordFps,
                    Quality = _settings.RecordQualityValue,
                    IncludeCursor = _settings.RecordCursor,
                    Ink = ink,
                    Shapes = shapes,
                    Zoom = zoom,
                    Audio = BuildAudioRequest()
                });

                // Both events arrive on the capture thread.
                _recorder.Progress += (_, p) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    _recordHud?.UpdateStatus(p.Elapsed, p.Bytes);
                    _recordHud?.UpdateAudio(p.AudioActive, p.AudioLevel, _recordMuted);
                }));

                _recorder.Finished += (_, error) => Dispatcher.BeginInvoke(new Action(() =>
                    OnRecordingFinished(error)));

                _recorder.Start();
                RecordingStateChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                TearDownRecordingUi();
                if (_settings.ShowTrayNotification)
                    _tray.ShowBalloon("Couldn't start recording", ex.Message);
            }
        }

        public void StopRecording()
        {
            var recorder = _recorder;
            if (recorder == null) return;

            // Stop() blocks until the encoder has closed the container, which is what
            // makes the file playable. Doing it off the UI thread keeps the HUD alive
            // while that happens.
            _ = Task.Run(() => recorder.Stop());
        }

        private void OnRecordingFinished(string? error)
        {
            string? path = _recordPath;
            // Read before the recorder is dropped: a microphone that was never found is
            // worth mentioning once, at the end, rather than as a dialog that interrupts
            // the take it is complaining about.
            string? audioWarning = _recorder?.AudioWarning;

            TearDownRecordingUi();
            _recorder = null;
            RecordingStateChanged?.Invoke(this, EventArgs.Empty);
            MemoryGuard.RequestTrim();

            // Set before the notification check: the Record tab's "Review last recording"
            // works whether or not notifications are switched on.
            if (error == null && path != null && File.Exists(path)) _lastVideoPath = path;

            if (!_settings.ShowTrayNotification) return;

            if (error != null)
            {
                _tray.ShowBalloon("Recording failed", error);
                return;
            }

            long size = 0;
            try { if (path != null) size = new FileInfo(path).Length; } catch { }

            string detail = path == null
                ? "Done."
                : $"{Path.GetFileName(path)}  \u00b7  {AdvancedSnip.MainWindow.FormatBytes(size)}  \u00b7  click to review";

            if (!string.IsNullOrEmpty(audioWarning)) detail += $"\n{audioWarning}";

            ShowCaptureToast("Recording saved", detail, "recording");
        }

        /// <summary>Opens the player on a finished recording, reusing the window if it is open.</summary>
        /// <summary>
        /// Opens a PDF from the gallery in the built-in reader.
        ///
        /// One window at a time, and — the lesson from v2.17's editor bug — an already-open
        /// one is replaced rather than merely activated, so asking for a different document
        /// gets you that document.
        /// </summary>
        public void OpenPdfViewer(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            try
            {
                if (_pdfWindow is { IsVisible: true })
                {
                    _pdfWindow.Close();
                    _pdfWindow = null;
                }

                var window = new PdfViewWindow(path);
                _pdfWindow = window;
                window.Closed += (_, _) => { if (ReferenceEquals(_pdfWindow, window)) _pdfWindow = null; };

                if (_settingsWindow is { IsVisible: true }) window.Owner = _settingsWindow;
                window.Show();
                window.Activate();
            }
            catch { }
        }

        public void OpenVideoReview(string path)
        {
            if (_reviewWindow is { IsVisible: true })
            {
                _reviewWindow.Load(path);
                if (_reviewWindow.WindowState == WindowState.Minimized)
                    _reviewWindow.WindowState = WindowState.Normal;
                _reviewWindow.Activate();
                return;
            }

            _reviewWindow = new VideoReviewWindow(path);
            _reviewWindow.Closed += (_, _) => _reviewWindow = null;
            _reviewWindow.Deleted += (_, deleted) =>
            {
                if (string.Equals(deleted, _lastVideoPath, StringComparison.OrdinalIgnoreCase))
                    _lastVideoPath = null;
            };
            _reviewWindow.Show();
            _reviewWindow.Activate();
        }

        /// <summary>The most recent recording that still exists on disk, or null.</summary>
        public string? LastVideoPath =>
            !string.IsNullOrEmpty(_lastVideoPath) && File.Exists(_lastVideoPath) ? _lastVideoPath : null;

        /// <summary>
        /// Claims Escape system-wide for exactly as long as the pen is accepting clicks,
        /// and gives it straight back afterwards.
        ///
        /// This is the escape hatch that does not require finding, seeing or hitting
        /// anything with the mouse. While the pen is on, the overlay is swallowing clicks
        /// over the recorded area, and if the user has lost track of the toolbar there has
        /// to be a way out that needs no aim. Escape is unclaimed the moment the pen goes
        /// off, so no other application loses it for longer than that.
        /// </summary>
        private void OnPenEnabledChanged(object? sender, bool enabled)
        {
            if (enabled)
            {
                _recordHud?.RaiseAboveOverlay();
                if (_penEscapeHotkeyId == 0)
                    _penEscapeHotkeyId = _hotkeys.Register(
                        new HotkeyDef { Key = (int)System.Windows.Input.Key.Escape, Modifiers = 0 },
                        () => _recordInk?.SetTool(RecordTool.None));
            }
            else if (_penEscapeHotkeyId != 0)
            {
                _hotkeys.Unregister(_penEscapeHotkeyId);
                _penEscapeHotkeyId = 0;
            }
        }

        /// <summary>
        /// Keeps the toolbar and the picker in step with the surface, whichever of the
        /// three the change came from. Everything routes through here rather than each
        /// control updating the others, so there is exactly one place where the three can
        /// disagree, and it is this one.
        /// </summary>
        private void OnRecordToolChanged(object? sender, RecordTool tool) => SyncRecordUi();

        private void SyncRecordUi()
        {
            if (_recordInk == null) return;

            _recordHud?.ShowTool(_recordInk.Tool, _recordInk.PenColour);
            _recordHud?.ShowDrawingState(_recordInk.HasDrawings, _recordInk.CanUndoClear);
            _recordPalette?.SyncFromSurface();
        }

        /// <summary>
        /// Remembers whether the picker was pinned. Written straight through rather than
        /// held until the recording ends, because the recording may end with a crash or a
        /// power cut and the preference is worth more than the write.
        /// </summary>
        private void OnToolsPinnedChanged(object? sender, bool pinned)
        {
            _settings.RecordToolsPinned = pinned;
            SavePalettePosition();
            _settings.Save();
        }

        private void SavePalettePosition()
        {
            if (_recordPalette is not { Pinned: true }) return;
            if (_recordPalette.TryGetPosition(out int x, out int y))
            {
                _settings.RecordToolsX = x;
                _settings.RecordToolsY = y;
            }
        }

        /// <summary>
        /// Opens the tool picker under the pointer.
        ///
        /// Pressing the shortcut again while it is open closes it, so the same key is both
        /// "show me the tools" and "never mind" — which is what people try first.
        /// </summary>
        private void ShowToolPalette()
        {
            if (_recordPalette == null) return;

            if (_recordPalette.IsVisible) { _recordPalette.Dismiss(); return; }

            _recordPalette.SyncFromSurface();
            _recordPalette.ShowAtCursor();
        }

        /// <summary>
        /// Turns the sound settings into a request, or null when nothing was asked for.
        /// </summary>
        private AudioRequest? BuildAudioRequest()
        {
            if (!_settings.RecordSystemAudio && !_settings.RecordMicrophone) return null;

            return new AudioRequest
            {
                System = _settings.RecordSystemAudio,
                Microphone = _settings.RecordMicrophone,
                SystemDeviceId = _settings.RecordSystemDevice,
                MicrophoneDeviceId = _settings.RecordMicDevice,
                SystemGain = _settings.RecordSystemVolume / 100f,
                MicrophoneGain = _settings.RecordMicVolume / 100f
            };
        }

        private void ToggleRecordMute()
        {
            var recorder = _recorder;
            if (recorder is not { IsRunning: true } || !recorder.HasAudio) return;

            _recordMuted = !_recordMuted;
            recorder.Muted = _recordMuted;

            // The meter only refreshes on the next progress report, which can be a third
            // of a second away — long enough for the mute to feel unacknowledged.
            _recordHud?.UpdateAudio(true, 0f, _recordMuted);
        }

        /// <summary>
        /// Keys that exist only while a recording is running.
        ///
        /// They are registered system-wide because the whole point is that they work while
        /// the user is inside whatever application they are recording — a key that only
        /// works when the HUD has focus would be useless. They are released the moment the
        /// recording stops, so nothing is claimed for longer than it is needed.
        ///
        /// Any that fail to register are ignored rather than reported: another application
        /// already owning Ctrl+Shift+M is not worth a dialog in the middle of a recording,
        /// and every one of these has a button on the HUD as well.
        /// </summary>
        private void RegisterRecordingHotkeys()
        {
            const ModifierKeys CS = ModifierKeys.Control | ModifierKeys.Shift;

            void Add(Key key, Action action)
            {
                int id = _hotkeys.Register(new HotkeyDef(CS, key), action);
                if (id != 0) _recordingHotkeyIds.Add(id);
            }

            // The picker, first because it is the one worth learning: it reaches every
            // tool without needing a key of its own for each.
            Add(Key.A, ShowToolPalette);

            // Direct keys, so a practised user never has to open the picker at all. Each
            // one toggles: pressing the key for the tool you are already using puts the
            // tools away, which is what people try and saves reaching for Escape.
            void Tool(Key key, RecordTool tool) => Add(key, () => _recordInk?.SetTool(
                _recordInk.Tool == tool ? RecordTool.None : tool));

            Tool(Key.P, RecordTool.Pen);
            Add(Key.M, () => _recordInk?.ToggleHighlighter());
            Tool(Key.X, RecordTool.Arrow);

            // The three that a modifier cannot reach. Line, box and circle are left off
            // deliberately: they are Shift, Ctrl and Ctrl+Shift while drawing, and three
            // more claimed chords for something already one held key away would be worse
            // than not having them.
            //
            // **Check this list against AppSettings before adding to it.** These share one
            // message window with the app's own global hotkeys, so a clash does not warn —
            // RegisterHotKey simply fails, the failure is ignored by design, and the key
            // goes on doing its old job. Ctrl+Shift+S was the obvious pick for Spotlight
            // and would have taken a *screenshot* in the middle of the recording. Taken by
            // the app already: S snip, W scroll capture, V history, R record, . and ,
            // cycle. Hence F for focus rather than S.
            Tool(Key.F, RecordTool.Spotlight);
            Tool(Key.B, RecordTool.Redact);
            Tool(Key.N, RecordTool.Step);

            // Step through the tools without taking the hand off the keyboard.
            Add(Key.OemCloseBrackets, () => _recordInk?.CycleTool(1));
            Add(Key.OemOpenBrackets, () => _recordInk?.CycleTool(-1));

            Add(Key.Z, () => _recordInk?.Undo());
            Add(Key.E, () => _recordInk?.ClearAll());

            Add(Key.U, ToggleRecordMute);

            Add(Key.OemPlus, () => _recordZoom?.ZoomIn());
            Add(Key.OemMinus, () => _recordZoom?.ZoomOut());
            Add(Key.D0, () => _recordZoom?.Reset());

            // The numeric keypad equivalents, so the shortcuts work wherever the hand is.
            Add(Key.Add, () => _recordZoom?.ZoomIn());
            Add(Key.Subtract, () => _recordZoom?.ZoomOut());
        }

        private void ReleaseRecordingHotkeys()
        {
            foreach (int id in _recordingHotkeyIds)
            {
                try { _hotkeys.Unregister(id); } catch { }
            }
            _recordingHotkeyIds.Clear();
        }

        private void TearDownRecordingUi()
        {
            // Read the position while the window still exists — after Close() it is a
            // rectangle of zeroes, which would quietly park the dock in a corner next time.
            //
            // Wrapped whole, and first, because everything below this releases input: a
            // throw while saving a window position must never be the reason a full-screen
            // click-swallowing overlay stays on the user's desktop.
            try
            {
                if (_recordPalette is { Pinned: true })
                {
                    int x = _settings.RecordToolsX, y = _settings.RecordToolsY;
                    SavePalettePosition();
                    if (_settings.RecordToolsX != x || _settings.RecordToolsY != y)
                        _settings.Save();
                }
            }
            catch { }

            // Order matters: the tools must be released before anything is closed, so a
            // failure further down can never leave an input-swallowing window on screen.
            try { _recordInk?.SetTool(RecordTool.None); } catch { }
            if (_penEscapeHotkeyId != 0)
            {
                try { _hotkeys.Unregister(_penEscapeHotkeyId); } catch { }
                _penEscapeHotkeyId = 0;
            }
            ReleaseRecordingHotkeys();

            try { _recordPalette?.Dismiss(); } catch { }
            try { _recordPalette?.Close(); } catch { }
            try { _recordHud?.Close(); } catch { }
            try { _recordInk?.Close(); } catch { }
            _recordPalette = null;
            _recordHud = null;
            _recordInk = null;
            _recordZoom = null;
            _recordMuted = false;
        }

        /// <summary>
        /// Works out what to record. Region uses the ordinary snip overlay in
        /// selection-only mode, so it behaves exactly like taking a screenshot does.
        /// </summary>
        private async Task<System.Drawing.Rectangle> ChooseRecordingRegionAsync()
        {
            await Task.Yield();

            _recordTargetWindow = IntPtr.Zero;

            if (string.Equals(_settings.RecordSource, "Monitor", StringComparison.OrdinalIgnoreCase))
                return DisplayInfo.FromCursor().Bounds;

            if (string.Equals(_settings.RecordSource, "Window", StringComparison.OrdinalIgnoreCase))
                return ChooseRecordingWindow();

            if (!string.Equals(_settings.RecordSource, "Region", StringComparison.OrdinalIgnoreCase))
                return DisplayInfo.VirtualScreen;

            var full = ScreenCapture.CaptureVirtualScreen(out _, out _);
            try
            {
                var overlay = new SnipOverlay(full, _settings.OverlayOpacity) { SelectionOnly = true };
                bool picked = overlay.ShowDialog() == true;
                return picked ? overlay.SelectedRegion : System.Drawing.Rectangle.Empty;
            }
            finally
            {
                full.Dispose();
                MemoryGuard.RequestTrim();
            }
        }

        /// <summary>
        /// Picks a window to record, using the same overlay that scroll capture uses to
        /// point at one — rather than growing a second window picker with its own bugs.
        ///
        /// Returns the window's rectangle, which becomes the frame size. The frame cannot
        /// change size once the encoder has started, so this is the size the whole
        /// recording keeps even if the window is resized later.
        /// </summary>
        private System.Drawing.Rectangle ChooseRecordingWindow()
        {
            // Frozen screenshot behind the picker, exactly as scroll capture does it — the
            // desktop must not move while you are pointing at something on it.
            var frozen = ScreenCapture.CaptureVirtualScreen(out _, out _);
            TargetCandidate? target;
            try
            {
                var picker = new ScrollTargetOverlay(frozen, _settings.OverlayOpacity);
                target = picker.ShowDialog() == true ? picker.Result : null;
            }
            finally { frozen.Dispose(); }

            if (target == null) return System.Drawing.Rectangle.Empty;

            // The top-level window, not the deepest child the picker may have resolved to.
            // PrintWindow renders a whole top-level window; asking it for a child control
            // gets a fragment.
            IntPtr hwnd = target.TopLevel != IntPtr.Zero ? target.TopLevel : target.Hwnd;
            if (hwnd == IntPtr.Zero) return System.Drawing.Rectangle.Empty;

            // A minimised window has no rendered content — its application has usually
            // stopped drawing altogether — so there is nothing any capture API could
            // collect. Restoring it is the only thing that makes the request answerable.
            if (Win32.IsIconic(hwnd))
            {
                Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
                System.Threading.Thread.Sleep(250);   // let it paint before measuring
            }

            var rect = WindowFinder.WindowRect(hwnd);
            if (rect.Width < 16 || rect.Height < 16) return System.Drawing.Rectangle.Empty;

            _recordTargetWindow = hwnd;
            WarnIfWindowRendersBlank(hwnd, rect);
            return rect;
        }

        /// <summary>
        /// Takes one sample frame and says so if the window renders nothing.
        ///
        /// PrintWindow asks a window to draw through its normal paint path. Content that
        /// never goes through that path — video decoded straight onto a GPU surface,
        /// hardware overlays — comes back blank, with the rest of the window intact around
        /// it. That is a genuine limitation of this capture method, not a bug.
        ///
        /// Checked once, here, rather than per frame: a window that renders nothing does so
        /// consistently, and the entire value of knowing is in knowing it *before* ten
        /// minutes of black have been recorded. A warning that arrives afterwards is just
        /// an explanation of wasted time.
        /// </summary>
        private void WarnIfWindowRendersBlank(IntPtr hwnd, System.Drawing.Rectangle rect)
        {
            try
            {
                using var probe = new System.Drawing.Bitmap(Math.Max(1, rect.Width),
                                                            Math.Max(1, rect.Height));
                using (var g = System.Drawing.Graphics.FromImage(probe))
                {
                    g.Clear(System.Drawing.Color.Black);
                    var capture = new WindowCapture(hwnd, probe.Size);
                    if (!capture.Grab(g)) return;      // fell back to screen anyway
                }

                if (!WindowCapture.LooksBlank(probe)) return;

                _tray.ShowBalloon("This window may record as blank",
                             "It draws its contents in a way this capture method cannot read — "
                           + "usually hardware-accelerated video. The screen-region source "
                           + "records it correctly, but without hiding whatever is in front.");
            }
            catch { /* a probe that fails is not a reason to block the recording */ }
        }

        /// <summary>Counts down in the HUD's clock, so there is no extra window for it.</summary>
        private async Task CountdownAsync(int seconds)
        {
            for (int i = seconds; i > 0; i--)
            {
                _recordHud?.UpdateStatus(TimeSpan.FromSeconds(i), 0);
                await Task.Delay(1000);
            }
        }

        // ─────────────────────────────── editor ───────────────────────────────

        /// <summary>
        /// The notification was clicked. For a capture that means "let me fix this before
        /// I paste it", which is the whole reason the balloon is clickable.
        /// </summary>
        /// <summary>
        /// A capture receipt, on the display the user is actually looking at.
        ///
        /// Routed through <see cref="ToastWindow"/> rather than the tray balloon it used to
        /// use: the shell always draws balloons beside the notification area on the primary
        /// monitor, and there is no API to move them. On a multi-monitor desk that put the
        /// confirmation for a capture taken on the right-hand screen somewhere the user was
        /// not looking.
        ///
        /// Errors still go to the tray. A balloon persists in the notification centre where
        /// a toast does not, and "couldn't save the snip" is exactly the message worth being
        /// able to find again after it has gone.
        /// </summary>
        private void ShowCaptureToast(string title, string detail, string tag,
                                      System.Windows.Media.Imaging.BitmapSource? thumbnail = null)
        {
            _tray.BalloonTag = tag;
            ToastWindow.Show(title, detail, thumbnail, OnBalloonClicked);
        }

        private void OnBalloonClicked()
        {
            if (_tray.BalloonTag == "hotkeys") { ShowSettings(shortcuts: true); return; }
            if (_tray.BalloonTag == "recording")
            {
                if (!string.IsNullOrEmpty(_lastVideoPath) && File.Exists(_lastVideoPath))
                    OpenVideoReview(_lastVideoPath);
                return;
            }
            if (_tray.BalloonTag != "capture") return;
            // Not editing on click still means "show me the capture", so the window opens
            // on the gallery — where it is — rather than on the settings page.
            if (!_settings.EditOnNotificationClick) { ShowGallery(); return; }

            // Prefer the file, because that's what the editor can save back over. The
            // in-memory copy is the fallback for when saving was turned off or failed.
            if (!string.IsNullOrEmpty(_lastPath) && File.Exists(_lastPath))
                OpenEditorForFile(_lastPath);
            else if (_lastImage != null)
                OpenEditor(_lastImage, null);
        }

        /// <summary>
        /// Opens the editor on an image the user picks. The editor is useful well beyond
        /// the capture it was written for, so it gets its own way in.
        /// </summary>
        public void OpenEditorPicker()
        {
            if (_editorWindow is { IsVisible: true })
            {
                if (_editorWindow.WindowState == WindowState.Minimized)
                    _editorWindow.WindowState = WindowState.Normal;
                _editorWindow.Activate();
                return;
            }

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open an image to edit",
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico;*.webp"
                       + "|All files|*.*",
                InitialDirectory = Directory.Exists(_settings.SaveFolder)
                    ? _settings.SaveFolder : null
            };

            if (dlg.ShowDialog() == true) OpenEditorForFile(dlg.FileName);
        }

        public void OpenEditorForFile(string path)
        {
            try
            {
                var img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit();
                // OnLoad plus an explicit stream means the file isn't left locked — the
                // editor has to be able to save back over the very file it opened.
                img.CacheOption  = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                img.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat;
                using (var fs = File.OpenRead(path))
                {
                    img.StreamSource = fs;
                    img.EndInit();
                }
                img.Freeze();
                OpenEditor(img, path);
            }
            catch (Exception ex)
            {
                if (_settings.ShowTrayNotification)
                    _tray.ShowBalloon("Couldn't open that image", ex.Message);
            }
        }

        public void OpenEditor(System.Windows.Media.Imaging.BitmapSource image, string? path)
        {
            // One editor at a time, so an editor that is already open takes the new picture
            // instead of a second window being stacked on top of it.
            //
            // This used to activate the existing window and return, *discarding the image
            // it had been asked to open* — so after a second capture, clicking the
            // notification brought the editor forward still showing the previous
            // screenshot. The window was doing exactly what the notification promised
            // except for the one part that mattered.
            //
            // TryAdopt answers false when the user declines to abandon unsaved edits; the
            // window is then still showing the old picture, which is what they chose, so
            // there is nothing further to do but leave it in front of them.
            if (_editorWindow is { IsVisible: true })
            {
                if (_editorWindow.WindowState == WindowState.Minimized)
                    _editorWindow.WindowState = WindowState.Normal;

                _editorWindow.TryAdopt(image, path);
                _editorWindow.Activate();
                return;
            }

            var editor = new EditorWindow(image, path, _settings);
            _editorWindow = editor;

            editor.Saved += (_, args) =>
            {
                // The edited version replaces the original everywhere the original went:
                // on the clipboard, in the cycle-and-paste history, and in the gallery.
                if (_settings.CopyToClipboardOnSnip)
                    ClipboardService.SetImage(args.Image, args.Path);

                _history.Replace(args.Path, args.Image);

                _lastPath  = args.Path;
                _lastImage = null;   // args.Path is on disk; no need to pin the pixels

                if (_settings.GalleryOcrSearch)
                    _ocr.EnqueueCapture(args.Path);

                SnipCompleted?.Invoke(this, EventArgs.Empty);
            };

            editor.Closed += (_, _) => { if (ReferenceEquals(_editorWindow, editor)) _editorWindow = null; };
            editor.Show();
            editor.Activate();
        }

        // ─────────────────────────────── history HUD ──────────────────────────

        public void ToggleHistory()
        {
            if (_historyWindow is { IsVisible: true }) { _historyWindow.Hide(); return; }

            IntPtr target = NativeMethods.GetForegroundWindow();
            _historyWindow ??= new HistoryWindow(_history);
            _historyWindow.PasteTarget = target;
            _historyWindow.ShowAndActivate();
        }

        private void CyclePaste(bool forward)
        {
            if (_history.Count == 0) return;
            IntPtr target = NativeMethods.GetForegroundWindow();
            var item = forward ? _history.MoveNext() : _history.MovePrevious();
            if (item == null) return;
            ClipboardService.SetImage(item.GetFullImage(), item.FilePath);
            if (_historyWindow is { IsVisible: true }) _historyWindow.RefreshItems();
            _ = HistoryWindow.PasteToWindowAsync(target);
        }

        public void ClearHistory()
        {
            _history.Clear();
            _lastImage = null;
            MemoryGuard.RequestTrim();
        }

        /// <summary>Files text the editor already recognised into the shared index.</summary>
        /// <summary>
        /// One route for "the user copied a picture": onto the system clipboard AND into
        /// the history HUD.
        ///
        /// The gallery and the editor used to call <see cref="ClipboardService"/> straight,
        /// so a copy reached Windows but never the HUD — open the history immediately after
        /// copying and the thing you just copied was not in it. Captures went through
        /// <c>_history.Add</c> and copies did not, which is a distinction only the code
        /// knew about.
        /// </summary>
        // Fully qualified rather than adding a using: this file already writes
        // System.Windows.Media.Imaging.BitmapSource in full everywhere, and ledger #13 is
        // what happens when a bare type name is added to a file that does not import it.
        internal ClipboardOutcome CopyImage(System.Windows.Media.Imaging.BitmapSource image,
                                            string? filePath)
        {
            var outcome = ClipboardService.SetImage(image, filePath);

            // Promote, not Add: copying the same gallery item twice must not stack two
            // identical thumbnails and push real history out of an eight-slot buffer.
            _history.Promote(image, filePath);

            if (_historyWindow is { IsVisible: true }) _historyWindow.RefreshItems();
            return outcome;
        }

        /// <summary>
        /// Replaces an image in clipboard history in-place with its permanently saved/edited version.
        /// </summary>
        internal void ReplaceInHistory(string filePath, System.Windows.Media.Imaging.BitmapSource edited)
        {
            _history.Replace(filePath, edited);
            ClipboardService.SetImage(edited, filePath);
            if (_historyWindow is { IsVisible: true }) _historyWindow.RefreshItems();
            MemoryGuard.RequestTrim();
            SnipCompleted?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Explicitly signals that a new capture or edited image has been saved to disk.</summary>
        public void NotifySnipCompleted() => SnipCompleted?.Invoke(this, EventArgs.Empty);

        /// <summary>How many pictures the clipboard history will hold.</summary>
        internal int HistoryCapacity => _history.Capacity;

        internal int HistoryCount => _history.Count;

        /// <summary>
        /// Empties the clipboard history and the Windows clipboard.
        ///
        /// Both, because "clear the clipboard" that leaves Ctrl+V still pasting the last
        /// thing you copied has not done what it says. The caller is expected to have
        /// confirmed first — this is not undoable, and the pictures held only in memory
        /// (captures taken with saving turned off) are gone for good.
        /// </summary>
        internal void ClearClipboard()
        {
            _history.Clear();

            try { System.Windows.Clipboard.Clear(); }
            catch { /* another process holds it; the history is cleared either way */ }

            if (_historyWindow is { IsVisible: true }) _historyWindow.RefreshItems();
            MemoryGuard.RequestTrim();
        }

        /// <summary>
        /// Puts a set of pictures into the clipboard history at once, newest-first.
        ///
        /// <summary>
        /// Puts a set of pictures or PDFs into the clipboard history at once, newest-first.
        ///
        /// For PDF files, page 1 is rendered as an image on demand and added with the PDF's
        /// path so it can be previewed and pasted from the history HUD.
        /// </summary>
        internal async Task<(int Added, int Promoted, int Skipped, int Failed)> AddItemsToHistoryAsync(
            IReadOnlyList<string> paths)
        {
            int added = 0, promoted = 0, failed = 0;
            if (paths == null || paths.Count == 0) return (0, 0, 0, 0);

            // Only what will fit. Adding more would push the earlier ones straight back out.
            int room = Math.Max(1, _history.Capacity);
            int take = Math.Min(paths.Count, room);
            int skipped = paths.Count - take;

            // Reversed, because Add puts each new item at the front. Walking backwards
            // leaves the first picture the user selected sitting at the front of the history.
            for (int i = take - 1; i >= 0; i--)
            {
                string path = paths[i];
                try
                {
                    if (string.Equals(System.IO.Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        var doc = await PdfPreviewer.OpenAsync(path);
                        if (doc != null && doc.PageCount > 0)
                        {
                            var frame = await PdfPreviewer.RenderAsync(doc, 0, 1600);
                            if (frame != null)
                            {
                                bool alreadyThere = _history.Contains(path);
                                _history.Promote(frame, path);
                                if (alreadyThere) promoted++; else added++;
                                continue;
                            }
                        }
                        failed++;
                        continue;
                    }

                    var image = new System.Windows.Media.Imaging.BitmapImage();
                    image.BeginInit();
                    image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    image.CreateOptions =
                        System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
                    image.UriSource = new Uri(path);
                    image.EndInit();
                    image.Freeze();

                    bool wasThere = _history.Contains(path);
                    _history.Promote(image, path);

                    if (wasThere) promoted++; else added++;
                }
                catch { failed++; }
            }

            if (_historyWindow is { IsVisible: true }) _historyWindow.RefreshItems();
            MemoryGuard.RequestTrim();

            return (added, promoted, skipped, failed);
        }

        internal (int Added, int Promoted, int Skipped, int Failed) AddImagesToHistory(
            IReadOnlyList<string> paths)
            => AddItemsToHistoryAsync(paths).GetAwaiter().GetResult();

        internal void RememberOcr(string path, string text)
        {
            _ocr.Remember(path, text);
            _ocr.Save();
        }

        // ─────────────────────────────── settings window ──────────────────────

        /// <summary>
        /// Brings the main window up on the gallery. This is what the tray icon's
        /// double-click and its default menu entry do — "show me the app" lands on the
        /// thing the app has to show, which is what has been captured.
        /// </summary>
        public void ShowGallery()
        {
            ShowMainWindow();
            _settingsWindow?.ShowGalleryPage();
        }

        /// <summary>
        /// Brings the main window up on the settings page. <paramref name="shortcuts"/>
        /// goes straight to the Shortcuts tab, which is where the "some hotkeys are in
        /// use" notification needs to land — telling someone a hotkey clashed and then
        /// dropping them on an unrelated page makes them hunt for the fix.
        /// </summary>
        public void ShowSettings(bool shortcuts = false)
        {
            ShowMainWindow();
            _settingsWindow?.ShowSettingsPage(shortcuts);
        }

        private void ShowMainWindow()
        {
            _settingsWindow ??= new MainWindow(this);
            _settingsWindow.LoadFromSettings(_settings.Clone());
            _settingsWindow.Show();
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
        }

        public List<string> UpdateSettings(AppSettings updated)
        {
            _settings = updated;
            _settings.Save();
            _history.SetCapacity(_settings.MaxHistory);
            ThemeManager.Apply(_settings.Theme);
            StartupManager.Reconcile(_settings.RunAtStartup);
            EnsureSaveFolder();
            _tray.SetShortcuts(_settings.SnipHotkey.ToString(), _settings.RecordHotkey.ToString(), _settings.OcrHotkey.ToString());
            return RegisterHotkeys();
        }

        private void EnsureSaveFolder()
        {
            try { Directory.CreateDirectory(_settings.SaveFolder); } catch { }
        }
    }
}
