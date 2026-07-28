using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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

        // What the most recent "saved" notification refers to.
        private System.Windows.Media.Imaging.BitmapSource? _lastImage;
        private string? _lastPath;
        private bool           _snipping;
        private bool           _scrollCapturing;

        public AppSettings Settings    => _settings;

        /// <summary>
        /// The recognised-text cache, shared so the editor and the gallery never read the
        /// same image twice.
        /// </summary>
        internal OcrIndex  Ocr         => _ocr;
        public bool        IsExiting   { get; private set; }

        /// <summary>Raised on the UI thread after a snip is successfully saved/captured.</summary>
        public event EventHandler? SnipCompleted;

        // ─────────────────────────────── startup ──────────────────────────────

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (!createdNew) { Shutdown(); return; }

            System.Windows.Forms.Application.EnableVisualStyles();

            _settings = AppSettings.Load();
            EnsureSaveFolder();

            // Before any window exists, so the first one drawn is already correct rather
            // than flashing light and repainting.
            ThemeManager.Apply(_settings.Theme);

            _ocr.Load();
            _history = new ClipboardHistory(_settings.MaxHistory);
            _hotkeys = new HotKeyManager();
            var failed = RegisterHotkeys();

            _tray = new TrayIconManager();
            _tray.SnipRequested           += (_, _) => DoSnip();
            _tray.ScrollCaptureRequested  += (_, _) => DoScrollCapture();
            _tray.HistoryRequested        += (_, _) => ToggleHistory();
            _tray.SettingsRequested       += (_, _) => ShowSettings();
            _tray.ExitRequested           += (_, _) => ExitApp();
            _tray.EditImageRequested      += (_, _) => OpenEditorPicker();
            _tray.Show();

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
            if (_settings.ShowSettingsOnStartup && !launchedByWindows) ShowSettings();
        }

        private void ExitApp()
        {
            IsExiting = true;
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _ocr.Stop();
            _ocr.Save();
            ThemeManager.Shutdown();
            _hotkeys?.Dispose();
            _tray?.Dispose();
            _mutex?.Dispose();
            base.OnExit(e);
        }

        // ─────────────────────────────── hotkeys ──────────────────────────────

        private List<string> RegisterHotkeys()
        {
            _hotkeys.UnregisterAll();
            var failed = new List<string>();
            if (!_hotkeys.TryRegister(_settings.SnipHotkey,          DoSnip))
                failed.Add($"Snip ({_settings.SnipHotkey})");
            if (!_hotkeys.TryRegister(_settings.ScrollCaptureHotkey, DoScrollCapture))
                failed.Add($"Scroll Capture ({_settings.ScrollCaptureHotkey})");
            if (!_hotkeys.TryRegister(_settings.HistoryHotkey, ToggleHistory))
                failed.Add($"History ({_settings.HistoryHotkey})");
            if (!_hotkeys.TryRegister(_settings.NextHotkey,    () => CyclePaste(forward: true)))
                failed.Add($"Next ({_settings.NextHotkey})");
            if (!_hotkeys.TryRegister(_settings.PrevHotkey,    () => CyclePaste(forward: false)))
                failed.Add($"Previous ({_settings.PrevHotkey})");
            return failed;
        }

        // ─────────────────────────────── snip ─────────────────────────────────

        public void DoSnip()
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

                string? savedPath = TrySaveBitmap(crop);
                var image = ImageInterop.ToFrozenBitmapSourceAuto(crop);
                crop.Dispose();

                _history.Add(image, savedPath);

                if (_settings.CopyToClipboardOnSnip)
                    ClipboardService.SetImage(image);

                _lastImage = image;
                _lastPath  = savedPath;

                if (_settings.ShowTrayNotification)
                    _tray.ShowBalloon(
                        savedPath != null ? "Snip saved" : "Snip captured",
                        (savedPath != null ? Path.GetFileName(savedPath)
                                           : "Added to clipboard history")
                        + (_settings.EditOnNotificationClick ? " — click to edit" : ""),
                        tag: "capture");

                SnipCompleted?.Invoke(this, EventArgs.Empty);
            }
            finally { _snipping = false; }
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

                if (_settings.CopyToClipboardOnSnip)
                    ClipboardService.SetImage(imageSource);

                // A scroll capture is exactly the kind of image you want to trim before
                // sending, so it has to reach the editor the same way a region snip does.
                // Recording it here — and tagging the balloon — is what makes clicking the
                // notification work; without both, OnBalloonClicked has nothing to open.
                _lastImage = imageSource;
                _lastPath  = savedPath;

                if (_settings.ShowTrayNotification)
                {
                    string detail = savedPath != null
                        ? $"{outcome.Frames} screens \u2192 {Path.GetFileName(savedPath)}"
                        : $"{outcome.Frames} screens added to clipboard history";

                    if (!string.IsNullOrWhiteSpace(outcome.Note))
                        detail += "\n" + outcome.Note;
                    else if (_settings.EditOnNotificationClick)
                        detail += " \u2014 click to edit";

                    _tray.ShowBalloon(
                        savedPath != null ? "Scroll capture saved" : "Scroll capture done",
                        detail,
                        tag: "capture");
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

        // ─────────────────────────────── editor ───────────────────────────────

        /// <summary>
        /// The notification was clicked. For a capture that means "let me fix this before
        /// I paste it", which is the whole reason the balloon is clickable.
        /// </summary>
        private void OnBalloonClicked()
        {
            if (_tray.BalloonTag == "hotkeys") { ShowSettings(); return; }
            if (_tray.BalloonTag != "capture") return;
            if (!_settings.EditOnNotificationClick) { ShowSettings(); return; }

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
            // One editor at a time; re-clicking a notification should bring the existing
            // window forward rather than stack up windows on the same file.
            if (_editorWindow is { IsVisible: true })
            {
                if (_editorWindow.WindowState == WindowState.Minimized)
                    _editorWindow.WindowState = WindowState.Normal;
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
                    ClipboardService.SetImage(args.Image);

                _history.Replace(args.Path, args.Image);

                _lastImage = args.Image;
                _lastPath  = args.Path;

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
            ClipboardService.SetImage(item.Image);
            if (_historyWindow is { IsVisible: true }) _historyWindow.RefreshItems();
            _ = HistoryWindow.PasteToWindowAsync(target);
        }

        public void ClearHistory() => _history.Clear();

        /// <summary>Files text the editor already recognised into the shared index.</summary>
        internal void RememberOcr(string path, string text)
        {
            _ocr.Remember(path, text);
            _ocr.Save();
        }

        // ─────────────────────────────── settings window ──────────────────────

        public void ShowSettings()
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
            return RegisterHotkeys();
        }

        private void EnsureSaveFolder()
        {
            try { Directory.CreateDirectory(_settings.SaveFolder); } catch { }
        }
    }
}
