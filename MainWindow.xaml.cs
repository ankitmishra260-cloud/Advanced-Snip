using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    // ── View model for one thumbnail card in the gallery ──────────────────────
    /// <summary>
    /// One capture in the gallery. Created from metadata alone; the thumbnail arrives
    /// later on the UI thread, which is why Thumb raises a change notification and the
    /// rest of the properties don't need to.
    /// </summary>
    /// <summary>What a gallery card stands for. One list per kind.</summary>
    internal enum GalleryKind { Image, Video, Pdf }

    internal sealed class GalleryItem : INotifyPropertyChanged
    {
        public required string   FilePath { get; init; }
        public required long     Bytes    { get; init; }
        public required DateTime Captured { get; init; }

        private BitmapSource? _thumb;
        public BitmapSource? Thumb
        {
            get => _thumb;
            set
            {
                if (ReferenceEquals(_thumb, value)) return;
                _thumb = value;
                Raise(nameof(Thumb));
            }
        }

        /// <summary>What sort of file this card stands for.</summary>
        public required GalleryKind Kind { get; init; }

        public bool IsVideo => Kind == GalleryKind.Video;
        public bool IsPdf => Kind == GalleryKind.Pdf;

        private TimeSpan _duration;
        /// <summary>
        /// Running time, filled in when the thumbnail is probed — reading it needs the
        /// file opened anyway, so it comes free with the frame rather than costing a
        /// second pass over the folder.
        /// </summary>
        public TimeSpan Duration
        {
            get => _duration;
            set
            {
                if (_duration == value) return;
                _duration = value;
                Raise(nameof(Duration));
                Raise(nameof(DurationLabel));
                Raise(nameof(DurationVisibility));
                Raise(nameof(SubtitleLabel));
            }
        }

        public string DurationLabel =>
            IsPdf
                ? (_pages > 0 ? $"{_pages} page{(_pages == 1 ? "" : "s")}" : "")
                : _duration > TimeSpan.Zero ? VideoThumbnailer.Format(_duration) : "";

        /// <summary>
        /// The corner time badge. Bound as a Visibility rather than a bool so the template
        /// needs no converter, and keyed off the duration rather off IsVideo so a file
        /// whose length could not be read shows no badge instead of an empty one.
        /// </summary>
        public Visibility DurationVisibility =>
            DurationLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>The play glyph over the thumbnail. Every video gets one, frame or not.</summary>
        public Visibility PlayBadgeVisibility =>
            IsVideo ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Page count for a PDF, shown in the corner badge where a video shows its length.
        /// </summary>
        private int _pages;
        public int Pages
        {
            get => _pages;
            set
            {
                if (_pages == value) return;
                _pages = value;
                Raise(nameof(Pages));
                Raise(nameof(DurationLabel));
                Raise(nameof(DurationVisibility));
                Raise(nameof(SubtitleLabel));
            }
        }

        public string Name => Path.GetFileName(FilePath);

        public string SizeLabel => MainWindow.FormatBytes(Bytes);

        /// <summary>
        /// Date plus size on one line. Recent captures read as "Today 14:30" because
        /// that's how someone hunting for the screenshot they took an hour ago thinks
        /// about it; older ones get the full date.
        /// </summary>
        public string SubtitleLabel
        {
            get
            {
                var today = DateTime.Today;
                string when =
                      Captured.Date == today            ? "Today "     + Captured.ToString("HH:mm")
                    : Captured.Date == today.AddDays(-1)? "Yesterday " + Captured.ToString("HH:mm")
                    : Captured.Year == today.Year       ? Captured.ToString("d MMM HH:mm")
                    :                                     Captured.ToString("d MMM yyyy");
                return DurationLabel.Length > 0
                    ? $"{when}  ·  {DurationLabel}  ·  {SizeLabel}"
                    : $"{when}  ·  {SizeLabel}";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ── Main window (gallery + settings + about) ───────────────────────────────
    public partial class MainWindow : Window
    {
        private readonly App _app;
        private AppSettings _working = new();
        private bool _loadingGallery;

        // Populating the controls fires their change events. Without this the app would
        // save settings and re-sort the gallery several times just from opening the
        // window, before the user has touched anything.
        private bool _populating;

        public MainWindow(App app)
        {
            InitializeComponent();
            _app = app;

            FitDefaultSizeToScreen();

            // Read from the assembly rather than typed into the XAML. Two places said
            // "v1.0" while the project was on 2.9, because a hardcoded version is only
            // ever correct on the day it is written.
            ShowVersion();

            // The HUD can stop a recording without this window knowing, so the button
            // follows the app's state rather than its own memory of what it started.
            _app.RecordingStateChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateRecordUi();

                // A recording that has just stopped is a file the video tab does not know
                // about yet. Refreshing only when that tab is the one on screen keeps this
                // from scanning a folder nobody is looking at.
                if (VideoMode && !_app.IsRecording && PageGallery.Visibility == Visibility.Visible)
                    _ = LoadGalleryAsync();
            }));

            // WindowStartupLocation="CenterScreen" always means the primary display; on a
            // multi-monitor desk that is rarely the one being worked on.
            SourceInitialized += (_, _) => WindowPlacement.CenterOnActiveMonitor(this);

            // Answers WM_GETMINMAXINFO so a self-drawn title bar still maximises to the
            // work area of the monitor it's on, rather than sliding under the taskbar.
            WindowChromeSupport.Attach(this);

            // The gallery's ListBox has its own ScrollViewer, which swallows the wheel
            // The wheel workaround that used to live here is gone with the outer
            // ScrollViewer: the gallery list now owns its own scrolling, so there is no
            // nested scroller left to swallow the wheel.

            foreach (var box in new[] { SnipHotkeyBox, SnipTimerHotkeyBox, OcrHotkeyBox, RecordHotkeyBox, ScrollCaptureHotkeyBox, HistoryHotkeyBox, NextHotkeyBox, PrevHotkeyBox })
                box.LostFocus += Hotkey_LostFocus;

            // Re-load gallery whenever a new snip is taken while this window is open.
            app.Ocr.Progress += OnOcrProgress;

            app.SnipCompleted += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsVisible && PageGallery.Visibility == Visibility.Visible)
                {
                    if (_mode != GalleryKind.Image && GalleryTabImages != null)
                    {
                        GalleryTabImages.IsChecked = true;
                    }
                    else
                    {
                        _ = LoadGalleryAsync();
                    }
                }
            }));

            ThemeManager.ThemeChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_populating && ThemeCombo != null)
                {
                    SelectComboByTag(ThemeCombo, _app.Settings.Theme);
                }
            }));

            // The history and clear buttons are only meaningful when there is something in history.
            _app.HistoryChanged += (_, _) => Dispatcher.BeginInvoke(new Action(UpdateClipboardButtonsState));
            IsVisibleChanged += (_, _) => UpdateClipboardButtonsState();
            UpdateClipboardButtonsState();
        }

        // ─────────────────────────────── title bar ────────────────────────────

        private void Minimise_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void MaximiseRestore_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (MaxBtn == null) return;

            bool max = WindowState == WindowState.Maximized;
            MaxBtn.Content = max ? "\uE923" : "\uE922";   // restore / maximise
            MaxBtn.ToolTip = max ? "Restore" : "Maximise";

            // Maximised, the window edge *is* the screen edge, so an outline there only
            // costs a pixel of content and draws a line against the taskbar.
            WindowEdge.BorderThickness = new Thickness(max ? 0 : 1);
        }

        // ───────────────────────── page targeting ─────────────────────────────

        // Callers outside this window say which page they mean rather than trusting
        // whichever one happened to be showing last. Clicking "some hotkeys are in use"
        // and landing on the gallery is the sort of small betrayal that teaches people to
        // stop trusting notifications.

        /// <summary>Brings the gallery to the front.</summary>
        public void ShowGalleryPage() => NavGallery.IsChecked = true;

        /// <summary>
        /// Brings the settings page to the front, optionally on the Shortcuts tab.
        /// </summary>
        public void ShowSettingsPage(bool shortcuts = false)
        {
            NavSettings.IsChecked = true;
            if (shortcuts) SetTabHotkeys.IsChecked = true;
        }

        // ───────────────────────── quick actions (title bar) ──────────────────

        // These do the same work as the global hotkeys. They exist because a hotkey only
        // helps someone who already knows it, and the window open in front of them is where
        // they will look first.

        private void QuickSnip_Click(object sender, RoutedEventArgs e)
        {
            if (_working.SnipTimerSeconds > 0)
                _app.DoSnipWithTimer(_working.SnipTimerSeconds);
            else
                _app.DoSnip();
        }

        private void QuickSnipTimer_Click(object sender, RoutedEventArgs e)
        {
            if (QuickSnipTimerBtn.ContextMenu != null)
            {
                UpdateTimerMenuChecks();
                QuickSnipTimerBtn.ContextMenu.PlacementTarget = QuickSnipTimerBtn;
                QuickSnipTimerBtn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                QuickSnipTimerBtn.ContextMenu.IsOpen = true;
            }
        }

        private void SnipTimerPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && int.TryParse(mi.Tag?.ToString(), out int seconds))
            {
                SetSnipTimerSeconds(seconds);
            }
        }

        private void SnipTimerNow_Click(object sender, RoutedEventArgs e)
        {
            int s = _working.SnipTimerSeconds > 0 ? _working.SnipTimerSeconds : 3;
            _app.DoSnipWithTimer(s);
        }

        private void SetSnipTimerSeconds(int seconds)
        {
            _working.SnipTimerSeconds = seconds;
            _app.Settings.SnipTimerSeconds = seconds;
            _app.Settings.Save();
            UpdateSnipTimerUi();
        }

        private void UpdateSnipTimerUi()
        {
            int s = _working.SnipTimerSeconds;
            QuickSnipTimerText.Text = s == 0 ? "Off" : $"{s}s";
            QuickSnipBtn.ToolTip = s == 0
                ? "Capture a region of the screen (Ctrl+Shift+S)"
                : $"Capture a region of the screen after {s}s (Ctrl+Shift+S)";
            UpdateTimerMenuChecks();
        }

        private void UpdateTimerMenuChecks()
        {
            int s = _working.SnipTimerSeconds;
            if (SnipTimerNone != null) SnipTimerNone.IsChecked = s == 0;
            if (SnipTimer3s != null) SnipTimer3s.IsChecked = s == 3;
            if (SnipTimer10s != null) SnipTimer10s.IsChecked = s == 10;
            if (SnipTimer20s != null) SnipTimer20s.IsChecked = s == 20;
        }

        private void QuickScroll_Click(object sender, RoutedEventArgs e) => _app.DoScrollCapture();

        private void QuickRecord_Click(object sender, RoutedEventArgs e) => _app.ToggleRecording();

        private void QuickOcr_Click(object sender, RoutedEventArgs e) => _app.DoRealtimeOcr();

        /// <summary>
        /// Shrinks the default window if it would not fit the screen it is opening on.
        ///
        /// The default went landscape in v2.38 — 1180x760, which is a better shape for a
        /// sidebar plus a thumbnail grid. It is also taller than the work area of a
        /// 1366x768 laptop, and a window that opens with its buttons under the taskbar is
        /// worse than a slightly cramped one. The work area is read from the monitor the
        /// window is actually on rather than SystemParameters, which only ever describes
        /// the primary display.
        /// </summary>
        private void FitDefaultSizeToScreen()
        {
            var monitor = DisplayInfo.FromCursor();
            var area = monitor.WorkArea.Width > 0 ? monitor.WorkArea : monitor.Bounds;
            double scale = monitor.Scale <= 0 ? 1 : monitor.Scale;

            // Win32 reports physical pixels; Width and Height are DIPs.
            double maxW = area.Width / scale - 40;
            double maxH = area.Height / scale - 40;

            if (maxW > 200 && Width > maxW) Width = Math.Max(MinWidth, maxW);
            if (maxH > 200 && Height > maxH) Height = Math.Max(MinHeight, maxH);
        }

        private void QuickHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_app.HistoryCount == 0) return;
            _app.ToggleHistory();
        }

        /// <summary>
        /// Empties the clipboard history and the Windows clipboard.
        ///
        /// Asks first. It is not undoable, and captures taken with saving switched off exist
        /// only in that list — clearing it is the one action in this app that can destroy a
        /// picture which was never anywhere else. The prompt names both effects rather than
        /// saying "are you sure", because "Clear" beside "History" could reasonably be read
        /// as either one alone.
        /// </summary>
        private void QuickClearClip_Click(object sender, RoutedEventArgs e)
        {
            int held = _app.HistoryCount;

            string message =
                $"Clear the clipboard?\n\nThis removes {held} item(s) from the clipboard history "
                + "and empties the Windows clipboard.\n\nSaved files in the gallery are not "
                + "touched, but anything captured without saving exists only here.";

            var answer = ThemedDialog.Show(this, message, "Advanced Snip",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.OK) return;

            _app.ClearClipboard();
            GalleryStatus.Text = $"Cleared {held} item(s) from the clipboard.";
        }

        private void UpdateClipboardButtonsState()
        {
            bool hasItems = _app.HistoryCount > 0;
            QuickClearClipBtn.IsEnabled = hasItems;
            QuickHistoryBtn.IsEnabled = hasItems;
        }

        /// <summary>
        /// Puts every selected image into the clipboard history.
        ///
        /// The history is a fixed-size ring, so a selection bigger than it cannot all go in.
        /// Rather than adding everything and letting the earliest fall straight back out —
        /// work done and thrown away, with no way to tell which survived — only what fits is
        /// added, and the report says exactly how many did not.
        /// </summary>
        private async void GalleryAddToHistory_Click(object sender, RoutedEventArgs e)
        {
            var paths = GalleryList.SelectedItems
                                   .OfType<GalleryItem>()
                                   .Where(i => !i.IsVideo)
                                   .Select(i => i.FilePath)
                                   .ToList();

            if (paths.Count == 0)
            {
                GalleryStatus.Text = "Select one or more images or PDFs first.";
                return;
            }

            int capacity = _app.HistoryCapacity;

            if (paths.Count > capacity)
            {
                var answer = ThemedDialog.Show(this,
                    $"The clipboard history holds {capacity} items, and you have selected "
                    + $"{paths.Count}.\n\nThe first {capacity} will be added and the remaining "
                    + $"{paths.Count - capacity} will be skipped.\n\nYou can raise the limit in "
                    + "Settings.",
                    "Advanced Snip", MessageBoxButton.OKCancel, MessageBoxImage.Information);

                if (answer != MessageBoxResult.OK) return;
            }

            GalleryStatus.Text = "Adding to clipboard history…";
            var (added, promoted, skipped, failed) = await _app.AddItemsToHistoryAsync(paths);

            var parts = new List<string>();
            if (added > 0) parts.Add($"{added} added");
            if (promoted > 0) parts.Add($"{promoted} already there, moved to the front");
            if (skipped > 0) parts.Add($"{skipped} didn't fit");
            if (failed > 0) parts.Add($"{failed} couldn't be read");

            GalleryStatus.Text = parts.Count > 0
                ? "Clipboard: " + string.Join(" · ", parts)
                : "Nothing added.";
        }

        /// <summary>
        /// Keeps the title-bar record button honest about what pressing it will do. The
        /// recording can be stopped from the floating toolbar or by a hotkey without this
        /// window hearing about it any other way.
        /// </summary>
        private void UpdateQuickBar()
        {
            if (QuickRecordText == null) return;

            bool running = _app.IsRecording;
            QuickRecordText.Text = running ? "Stop" : "Record";
            QuickRecordBtn.ToolTip = running
                ? "Stop the recording in progress (Ctrl+Shift+R)"
                : "Start recording the screen (Ctrl+Shift+R)";

            // The dot is always red — a grey dot on a button labelled "Record" reads as
            // disabled. It is set once in XAML with a DynamicResource and left alone here;
            // assigning it from code would replace that reference with a frozen brush and
            // stop it following the theme.
            //
            // The label turns red for "Stop", and **must use SetResourceReference rather
            // than FindResource**. FindResource returns the brush as it is *now* and writes
            // it as a local value, which severs the theme link — switching from a dark
            // theme to Light then left this label painted in the dark theme's pale grey on
            // a pale pink chip, at 1.4:1. That is the bug that made the Record button
            // unreadable in Light. ClearValue puts the label back to inheriting the
            // button's own themed Foreground.
            if (running)
                QuickRecordText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
            else
                QuickRecordText.ClearValue(TextBlock.ForegroundProperty);
        }

        // ───────────────────────── settings sub-tabs ──────────────────────────

        private void SettingsTab_Checked(object sender, RoutedEventArgs e)
        {
            // Fires during InitializeComponent, before the panes exist.
            if (SetPaneCapture == null) return;

            SetPaneCapture.Visibility   = Visibility.Collapsed;
            SetPaneRecording.Visibility = Visibility.Collapsed;
            SetPaneGallery.Visibility   = Visibility.Collapsed;
            SetPaneHotkeys.Visibility   = Visibility.Collapsed;
            SetPaneGeneral.Visibility   = Visibility.Collapsed;

            if (sender == SetTabRecording)    SetPaneRecording.Visibility = Visibility.Visible;
            else if (sender == SetTabGallery) SetPaneGallery.Visibility   = Visibility.Visible;
            else if (sender == SetTabHotkeys) SetPaneHotkeys.Visibility   = Visibility.Visible;
            else if (sender == SetTabGeneral) SetPaneGeneral.Visibility   = Visibility.Visible;
            else                              SetPaneCapture.Visibility   = Visibility.Visible;

            // A tab switch that keeps the previous tab's scroll offset lands the reader
            // halfway down a page they have not seen the top of.
            SettingsScroller?.ScrollToTop();
        }

        /// <summary>Opens Settings on a chosen tab. Used by the Record page's shortcut.</summary>
        private void ShowSettingsTab(RadioButton tab)
        {
            NavSettings.IsChecked = true;
            tab.IsChecked = true;
        }

        private void RecordSettings_Click(object sender, RoutedEventArgs e)
            => ShowSettingsTab(SetTabRecording);

        // ───────────────────────── gallery: images / videos ───────────────────

        private void GalleryMode_Checked(object sender, RoutedEventArgs e)
        {
            if (GalleryList == null) return;      // during InitializeComponent

            var kind = sender == GalleryTabVideos ? GalleryKind.Video
                     : sender == GalleryTabPdfs   ? GalleryKind.Pdf
                     :                              GalleryKind.Image;

            if (kind == _mode && _allItems.Count > 0) return;

            _mode = kind;
            _page = 0;

            ApplyGalleryModeUi();
            _ = LoadGalleryAsync();
        }

        /// <summary>
        /// Shows the controls that mean something for the current list and hides the ones
        /// that do not. Searching for words inside a picture is meaningless for an MP4, and
        /// "Edit" and "Play" are different enough verbs that offering both at once would
        /// leave people guessing which one their selection answers to.
        /// </summary>
        /// <summary>
        /// Shows only the controls that mean something for the tab in view.
        ///
        /// **Every mode-specific control in the gallery must be listed here.** The bug this
        /// replaced was not a wrong decision, it was an omission: "Open image…" was added
        /// to the toolbar in v2.14 and simply never wired into this method, so it sat on
        /// the Videos and PDFs tabs offering to open a picture. The context menu had never
        /// been wired in at all and was offering "Play / trim…" on a PDF.
        ///
        /// `EnsureModeControlsCovered` guards against the same omission happening again.
        /// </summary>
        private void ApplyGalleryModeUi()
        {
            bool images = _mode == GalleryKind.Image;

            var forImages = images ? Visibility.Visible : Visibility.Collapsed;
            var forOthers = images ? Visibility.Collapsed : Visibility.Visible;

            OcrSearchChk.Visibility       = forImages;

            GalleryEditBtn.Visibility     = forImages;

            // Shown for PDFs as well, where it renders the first page. Not for recordings:
            // there is no one frame that "copy as image" could honestly mean.
            GalleryCopyBtn.Visibility     = images || PdfMode ? Visibility.Visible : Visibility.Collapsed;
            GalleryOpenImageBtn.Visibility = forImages;
            GalleryPlayBtn.Visibility     = forOthers;

            // The verb differs by kind, and offering the wrong one is worse than offering
            // none: "Edit" on a PDF would promise something the editor cannot do.
            GalleryPlayBtn.Content = PdfMode ? "▶ View" : "▶ Open";
            GalleryPlayBtn.ToolTip = PdfMode
                ? "Read this PDF page by page"
                : "Play, trim and export this recording";

            // The context menu is the same set of verbs reached a different way, so it
            // follows the same rules. A menu that disagrees with the toolbar above it is
            // worse than either being wrong on its own.
            MenuEdit.Visibility = forImages;
            MenuConvertToPdf.Visibility = Visibility.Collapsed;
            MenuAddToHistory.Visibility = images || PdfMode ? Visibility.Visible : Visibility.Collapsed;
            MenuOpenWith.Visibility = forOthers;
            MenuOpenWith.Header = PdfMode ? "View…" : "Play / trim…";

            if (!images)
            {
                TextSearchBox.Visibility = Visibility.Collapsed;
                _textSearch = "";

                // Cleared on the way into a tab, not on the way out of one: the PDF loader
                // borrows this label to report a renderer failure, and wiping it after that
                // has been written would hide the one message explaining an empty grid.
                OcrStatusText.Text = "";
            }
            else if (OcrSearchChk.IsChecked == true)
            {
                TextSearchBox.Visibility = Visibility.Visible;
            }

            GallerySearchBox.ToolTip = _mode switch
            {
                GalleryKind.Video => "Recording name",
                GalleryKind.Pdf   => "Document name",
                _                 => "File name"
            };
        }

        /// <summary>
        /// Every button in the gallery's action row is either mode-specific and handled by
        /// <see cref="ApplyGalleryModeUi"/>, or listed here as deliberately universal.
        ///
        /// This exists because the class of bug it catches is invisible by construction: a
        /// control added to the markup and forgotten in the mode logic simply shows up
        /// everywhere, looking like a decision rather than an oversight. Nobody reviewing
        /// the diff that adds a button is thinking about the three tabs it will appear on.
        ///
        /// Checked by the build sweep rather than at runtime — a name in this list that no
        /// longer exists, or a button in neither place, is a mistake to catch before
        /// shipping, not a dialog to show a user.
        /// </summary>
        private static readonly string[] GalleryUniversalButtons =
        {
            "SearchClearBtn",        // clears the file-name search, which every tab has
            "GalleryRefreshBtn",     // reloads whichever list is showing
            "GallerySelectAllBtn",
            "GalleryClearSelBtn",
            "GalleryOpenFolderBtn",  // every kind lives in a folder
            "GalleryDeleteBtn",      // every kind can be deleted
            "GalleryPrevBtn",
            "GalleryNextBtn"
        };

        /// <summary>
        /// Buttons whose visibility follows a container that
        /// <see cref="ApplyGalleryModeUi"/> already hides, so gating them individually
        /// would be redundant rather than missing.
        ///
        /// Separate from the universal list on purpose. Calling these "universal" would be
        /// false — they are mode-gated, just one level up — and the next person reading the
        /// list would believe it.
        /// </summary>
        private static readonly string[] GalleryContainerGatedButtons =
        {
            "TextSearchClearBtn"     // inside TextSearchBox, which is hidden off the Images tab
        };

        private void GalleryPlay_Click(object sender, RoutedEventArgs e) => OpenSelected();

        /// <summary>
        /// Opens a picture from anywhere on the machine, not just the save folder. This
        /// used to be a tray menu entry and was the only way to reach the picker; it now
        /// sits with the other open-a-picture verbs, where someone might actually find it.
        /// </summary>
        private void GalleryOpenExternal_Click(object sender, RoutedEventArgs e)
            => _app.OpenEditorPicker();

        // ─────────────────────────────── navigation ───────────────────────────

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PageGallery == null) return; // called before InitializeComponent finishes

            PageGallery.Visibility  = Visibility.Collapsed;
            PageRecord.Visibility   = Visibility.Collapsed;
            PageSettings.Visibility = Visibility.Collapsed;
            PageHelp.Visibility     = Visibility.Collapsed;

            if (sender == NavGallery)
            {
                PageGallery.Visibility = Visibility.Visible;
                ApplyGalleryModeUi();
                _ = LoadGalleryAsync();
            }
            else if (sender == NavRecord)
            {
                PageRecord.Visibility = Visibility.Visible;
                UpdateRecordUi();
            }
            else if (sender == NavSettings)
                PageSettings.Visibility = Visibility.Visible;
            else
                PageHelp.Visibility = Visibility.Visible;
        }

        // ─────────────────────────────── LoadFromSettings ─────────────────────

        public void LoadFromSettings(AppSettings working)
        {
            _working = working;
            _populating = true;
            try
            {
                FolderBox.Text          = working.SaveFolder;
                VideoFolderBox.Text     = working.VideoFolderOrDefault;
                VideoPrefixBox.Text     = working.VideoPrefix;
                SelectByTag(RecordSourceCombo,    working.RecordSource);
                SelectByTag(RecordQualityCombo,   working.RecordQuality);
                SelectByTag(RecordFpsCombo,       working.RecordFps.ToString());
                SelectByTag(RecordCountdownCombo, working.RecordCountdown.ToString());
                RecordCursorChk.IsChecked = working.RecordCursor;
                RecordInkChk.IsChecked    = working.RecordAnnotations;

                RecordSystemAudioChk.IsChecked = working.RecordSystemAudio;
                RecordMicChk.IsChecked         = working.RecordMicrophone;
                LoadAudioDevices(working);
                SelectByTag(SystemVolumeCombo, working.RecordSystemVolume.ToString());
                SelectByTag(MicVolumeCombo,    working.RecordMicVolume.ToString());
                PrefixBox.Text          = working.FilenamePrefix;
                HistorySlider.Value     = Math.Clamp(working.MaxHistory, 5, 30);
                JpegSlider.Value        = Math.Clamp(working.JpegQuality, 30, 100);
                OpacitySlider.Value     = Math.Clamp(working.OverlayOpacity, 20, 90);
                CopyChk.IsChecked       = working.CopyToClipboardOnSnip;
                NotifyChk.IsChecked     = working.ShowTrayNotification;
                StartupChk.IsChecked    = working.RunAtStartup;
                ShowOnStartChk.IsChecked= working.ShowSettingsOnStartup;
                MinimiseChk.IsChecked   = working.MinimiseToTrayOnClose;

                ScrollHeightSlider.Value    = Math.Clamp(working.ScrollMaxHeight, 2000, 60000);
                ScrollAutoRegionChk.IsChecked = working.ScrollAutoDetectRegion;
                ScrollRestoreChk.IsChecked    = working.ScrollRestorePosition;
                SelectCombo(ScrollSpeedCombo, working.ScrollSpeed);

                SelectComboByTag(ThemeCombo, working.Theme);
                SelectComboByTag(GallerySortCombo, working.GallerySort);
                PageSizeSlider.Value  = Math.Clamp(working.GalleryPageSize, 50, 500);
                RecycleChk.IsChecked  = working.GalleryUseRecycleBin;
                EditOnClickChk.IsChecked = working.EditOnNotificationClick;
                OcrSearchChk.IsChecked   = working.GalleryOcrSearch && OcrService.IsAvailable;
                ShowTextSearch(OcrSearchChk.IsChecked == true);
                // Reset the range with the combo, or the filter would still be applied
                // while the control claims "Any time".
                SelectComboByTag(DateRangeCombo, "Any");
                CustomRangePanel.Visibility = Visibility.Collapsed;
                _from = null;
                _to = null;

                ApplyTimerToSnipHotkeyChk.IsChecked = working.ApplyTimerToSnipHotkey;
                SelectFormat(working.ImageFormat);
                UpdateJpegRowVisibility();
                PopulateHotkeyTexts();
                UpdateSnipTimerUi();
                RefreshStartupStatus();
                StatusText.Text = "";
            }
            finally { _populating = false; }

            UpdateAudioHint();

            // NavGallery carries IsChecked="True" in XAML, so Nav_Checked fires during
            // InitializeComponent while PageGallery is still null and returns early — the
            // gallery was never loading on first open, and with it neither was OCR
            // indexing. Loading here also refreshes the list every time the window is
            // reopened, which is wanted anyway.
            // The title-bar record button and the gallery's mode chrome are both derived
            // state; neither has fired its own event by this point.
            UpdateQuickBar();
            ApplyGalleryModeUi();
            UpdateRecordUi();

            if (PageGallery.Visibility == Visibility.Visible)
                _ = LoadGalleryAsync();
        }

        private static void SelectCombo(ComboBox combo, string value)
        {
            foreach (var obj in combo.Items)
                if (obj is ComboBoxItem item &&
                    string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                { combo.SelectedItem = item; return; }
            combo.SelectedIndex = 0;
        }

        /// <summary>
        /// Selects by Tag rather than by displayed text, so the stored value stays stable
        /// if a label is ever reworded or localised.
        /// </summary>
        private static void SelectComboByTag(ComboBox combo, string? tag)
        {
            foreach (var obj in combo.Items)
                if (obj is ComboBoxItem item &&
                    string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                { combo.SelectedItem = item; return; }
            combo.SelectedIndex = 0;
        }

        // ─────────────────────────────── appearance ───────────────────────────

        // ─────────────────────────────── record tab ───────────────────────────

        private static void SelectByTag(ComboBox combo, string tag)
        {
            foreach (var item in combo.Items)
                if (item is ComboBoxItem ci && (ci.Tag as string) == tag)
                { combo.SelectedItem = ci; return; }
            if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private static string TagOf(ComboBox combo, string fallback)
            => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

        private void RecordSource_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_populating) return;
            _working.RecordSource = TagOf(RecordSourceCombo, "Monitor");
            UpdateRecordUi();
        }

        private void RecordQuality_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_populating) return;
            _working.RecordQuality = TagOf(RecordQualityCombo, "Balanced");
            UpdateRecordUi();
        }

        private void RecordFps_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_populating) return;
            _working.RecordFps = int.TryParse(TagOf(RecordFpsCombo, "30"), out int f) ? f : 30;
            UpdateRecordUi();
        }

        private void RecordCountdown_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_populating) return;
            _working.RecordCountdown =
                int.TryParse(TagOf(RecordCountdownCombo, "3"), out int c) ? c : 3;
        }

        /// <summary>Puts the real assembly version into the sidebar and the About page.</summary>
        private void ShowVersion()
        {
            // Was reading GetName().Version, which is AssemblyVersion, which the csproj
            // was never setting — so it said 1.0 for twenty-nine releases while claiming to
            // be read from the assembly. See AppInfo.
            string version = AppInfo.Version;
            if (version.Length == 0) return;

            VersionText.Text = $"v{version} by Ankit Mishra";
            HelpVersion.Text = $"Version {version} by Ankit Mishra";
        }

        // ─────────────────────────────── recording sound ──────────────────────

        /// <summary>
        /// Fills the two device lists.
        ///
        /// Enumeration talks to the Windows audio service, which can be slow or absent, so
        /// the whole thing is guarded: a machine with no sound hardware shows the default
        /// entry and nothing else rather than an error the user can do nothing about.
        /// </summary>
        private void LoadAudioDevices(AppSettings working)
        {
            FillDeviceCombo(SystemAudioCombo, AudioDevices.Playback(), working.RecordSystemDevice);
            FillDeviceCombo(MicAudioCombo, AudioDevices.Recording(), working.RecordMicDevice);
        }

        private static void FillDeviceCombo(ComboBox combo, System.Collections.Generic.List<AudioDeviceInfo> devices,
                                            string selectedId)
        {
            combo.Items.Clear();

            foreach (var device in devices)
                combo.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });

            // A device saved from a previous session may have been unplugged since. Falling
            // back to the default entry is right: it is what will actually be recorded.
            foreach (var item in combo.Items)
                if (item is ComboBoxItem ci && (ci.Tag as string) == selectedId)
                {
                    combo.SelectedItem = item;
                    return;
                }

            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private void RecordAudio_Changed(object sender, RoutedEventArgs e)
        {
            if (_populating || _working == null) return;
            _working.RecordSystemAudio = RecordSystemAudioChk.IsChecked == true;
            _working.RecordMicrophone  = RecordMicChk.IsChecked == true;
            UpdateAudioHint();
            UpdateRecordUi();
        }

        private void AudioDevice_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_populating || _working == null) return;
            _working.RecordSystemDevice = TagOf(SystemAudioCombo, "");
            _working.RecordMicDevice    = TagOf(MicAudioCombo, "");
        }

        private void AudioVolume_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_populating || _working == null) return;
            _working.RecordSystemVolume =
                int.TryParse(TagOf(SystemVolumeCombo, "100"), out int sv) ? sv : 100;
            _working.RecordMicVolume =
                int.TryParse(TagOf(MicVolumeCombo, "100"), out int mv) ? mv : 100;
        }

        private void UpdateAudioHint()
        {
            if (AudioHintText == null || _working == null) return;

            AudioHintText.Text = !_working.RecordSystemAudio && !_working.RecordMicrophone
                ? "Recordings will be silent. Turn on one of the two above to capture sound."
                : "Sound is recorded into the same MP4 as AAC. Mute mid-recording with "
                + "Ctrl+Shift+U. Leaving a device set to the default means it follows "
                + "whatever Windows is using, which keeps working when a headset is "
                + "unplugged.";
        }

        private void RecordToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_populating) return;
            _working.RecordCursor = RecordCursorChk.IsChecked == true;
            _working.RecordAnnotations = RecordInkChk.IsChecked == true;
        }

        private void VideoFolder_Changed(object sender, TextChangedEventArgs e)
        {
            if (_populating) return;
            _working.VideoFolder = VideoFolderBox.Text;
        }

        private void VideoPrefix_Changed(object sender, TextChangedEventArgs e)
        {
            if (_populating) return;
            _working.VideoPrefix = VideoPrefixBox.Text;
        }

        private void VideoFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Where should recordings be saved?",
                SelectedPath = Directory.Exists(VideoFolderBox.Text)
                    ? VideoFolderBox.Text : _working.SaveFolder
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                VideoFolderBox.Text = dlg.SelectedPath;
        }

        /// <summary>
        /// Opens the player on the most recent recording. Notifications are the usual way
        /// in, but they time out and can be switched off entirely, so the tab keeps a
        /// permanent route to the same window.
        /// </summary>
        private void ReviewLast_Click(object sender, RoutedEventArgs e)
        {
            string? path = _app.LastVideoPath;
            if (path == null)
            {
                RecordStatusText.Text = "No recording from this session yet.";
                return;
            }
            _app.OpenVideoReview(path);
        }

        private void RecordStart_Click(object sender, RoutedEventArgs e)
        {
            // Settings are applied before starting, so a preset the user just picked is
            // the one that gets recorded rather than the one they last pressed Save on.
            _app.UpdateSettings(_working);
            _working = _app.Settings.Clone();

            _app.ToggleRecording();
            UpdateRecordUi();
        }

        /// <summary>
        /// Keeps the button honest about what it will do, and estimates the file size.
        ///
        /// The estimate is deliberately a range rather than a number. Screen content
        /// compresses according to how much of it moves, and the gap between "a document
        /// on screen" and "a video playing full screen" is close to tenfold — quoting one
        /// figure would be precise and wrong.
        /// </summary>
        private void UpdateRecordUi()
        {
            // Reachable from the app's state event before LoadFromSettings has run, and
            // from Nav_Checked during InitializeComponent. Both would find nothing here.
            if (RecordStartBtn == null || _working == null) return;

            bool running = _app.IsRecording;
            RecordStartBtn.Content = running ? "Stop recording" : "Start recording";
            RecordStatusText.Text = running ? "Recording…" : "";

            UpdateQuickBar();

            // Was always enabled, and answered a click with "No recording from this session
            // yet." A button whose only job is to refuse is a worse answer than one that
            // visibly cannot be pressed — the second tells you before you spend the click.
            // Recording state changes re-run this, so it lights up the moment a take ends.
            ReviewLastBtn.IsEnabled = _app.LastVideoPath != null;
            ReviewLastBtn.ToolTip = ReviewLastBtn.IsEnabled
                ? "Open the most recent recording to play, trim and export"
                : "Nothing recorded yet this session";

            // The launcher no longer shows the settings themselves, so it has to say what
            // they add up to. A page whose only control is "Start" and which does not tell
            // you what it is about to capture is a page you cannot trust without leaving it.
            string source = _working.RecordSource switch
            {
                "Region"     => "A region you drag out",
                "Window"     => "A window you pick — its own contents, not the screen",
                "Everything" => "Every display, stitched into one frame",
                _            => "The monitor under the pointer"
            };

            string sound =
                _working.RecordSystemAudio && _working.RecordMicrophone ? "system sound + microphone"
              : _working.RecordSystemAudio                              ? "system sound"
              : _working.RecordMicrophone                               ? "microphone"
              :                                                          "no sound";

            RecordSummaryLine.Text =
                $"{source}  ·  {_working.RecordFps} fps, {_working.RecordQuality.ToLowerInvariant()}  ·  {sound}";

            string folder = _working.VideoFolderOrDefault;
            RecordDestinationLine.Text =
                $"Saves as {_working.VideoPrefix}_<date>.mp4 in {folder}"
              + (_working.RecordCountdown > 0
                    ? $"  ·  {_working.RecordCountdown}s countdown first."
                    : "  ·  Starts immediately.");

            var bounds = string.Equals(_working.RecordSource, "Monitor", StringComparison.OrdinalIgnoreCase)
                ? DisplayInfo.FromCursor().Bounds
                : DisplayInfo.VirtualScreen;

            bool region = string.Equals(_working.RecordSource, "Region", StringComparison.OrdinalIgnoreCase);

            int w = Math.Max(2, bounds.Width & ~1);
            int h = Math.Max(2, bounds.Height & ~1);

            double perMinute = VideoRecorder.MegabytesPerMinute(
                w, h, _working.RecordFps, _working.RecordQualityValue);

            // The same sentence in two places: on the launcher it is part of saying what
            // Start will do, and beside the quality picker it is what makes the choice
            // mean anything. Written once here so the two can never drift apart.
            string estimate = region
                ? "File size depends on the region you pick."
                : $"Expect about {perMinute * 0.35:0} to {perMinute:0} MB per minute.";

            RecordEstimateTitle.Text = estimate;
            QualityEstimateTitle.Text = estimate;

            // Sound is a rounding error next to video — 192 kbps AAC is about 1.4 MB a
            // minute — but saying so stops anyone assuming it doubles the file.
            string audio = _working.RecordSystemAudio || _working.RecordMicrophone
                ? " Sound adds about 1.4 MB a minute on top."
                : " Recording silently.";

            string detail = region
                ? $"At {_working.RecordFps} fps on the {_working.RecordQuality.ToLowerInvariant()} preset. "
                  + "You'll drag out the area when the recording starts." + audio
                : $"{w} × {h} at {_working.RecordFps} fps. A screen that is mostly still "
                  + "compresses far better than one full of motion, which is what the range "
                  + "covers." + audio;

            RecordEstimateDetail.Text = detail;
            QualityEstimateDetail.Text = detail;
        }

        private void Theme_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_populating) return;
            if ((ThemeCombo.SelectedItem as ComboBoxItem)?.Tag is not string tag) return;

            // Applied immediately and persisted so the settings dropdown and app appearance never drift apart
            ThemeManager.Apply(tag);
            _working.Theme = tag;
            _app.Settings.Theme = tag;
            _app.Settings.Save();
        }

        private void PageSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (PageSizeValue != null) PageSizeValue.Text = ((int)e.NewValue).ToString();
        }

        // ─────────────────────────────── startup status ───────────────────────

        private void Startup_Toggled(object sender, RoutedEventArgs e)
        {
            if (_populating) return;

            // Write and verify now rather than at Save, so the status line below is
            // reporting on something real.
            var status = StartupManager.SetRunAtStartup(StartupChk.IsChecked == true);
            _working.RunAtStartup = StartupChk.IsChecked == true;
            ShowStartupStatus(status);
        }

        private void RefreshStartupStatus()
            => ShowStartupStatus(StartupManager.GetStatus(StartupChk.IsChecked == true));

        private void ShowStartupStatus(StartupStatus status)
        {
            if (StartupStatusText == null) return;

            StartupStatusText.Text = status.Detail;
            StartupFixBtn.Visibility = status.State is StartupState.BlockedByWindows
                                                    or StartupState.PathMismatch
                                                    or StartupState.Failed
                ? Visibility.Visible : Visibility.Collapsed;

            StartupFixBtn.Content = status.State == StartupState.BlockedByWindows
                ? "Re-enable" : "Repair";

            // A resource *reference*, not a resolved brush. Same reason as the Record label
            // above: FindResource here would freeze whichever theme happened to be loaded
            // when the settings page was last populated.
            StartupStatusText.SetResourceReference(TextBlock.ForegroundProperty,
                status.State switch
                {
                    StartupState.Enabled          => "Brush.Success",
                    StartupState.Off              => "Brush.TextMuted",
                    StartupState.BlockedByWindows => "Brush.Warning",
                    _                             => "Brush.Danger"
                });
        }

        private void StartupFix_Click(object sender, RoutedEventArgs e)
        {
            var before = StartupManager.GetStatus(true);

            if (before.State == StartupState.BlockedByWindows)
            {
                if (!StartupManager.ClearWindowsBlock())
                {
                    ThemedDialog.Show(this,
                        "Windows wouldn't let the app clear that setting.\n\n" +
                        "Open Startup Apps and switch Advanced Snip on there instead.",
                        "Advanced Snip", MessageBoxButton.OK, MessageBoxImage.Information);
                    StartupManager.OpenTaskManagerStartup();
                    return;
                }
            }

            ShowStartupStatus(StartupManager.SetRunAtStartup(true));
        }

        private void SelectFormat(string format)
        {
            foreach (var obj in FormatCombo.Items)
                if (obj is ComboBoxItem item &&
                    string.Equals(item.Content?.ToString(), format, StringComparison.OrdinalIgnoreCase))
                { FormatCombo.SelectedItem = item; return; }
            FormatCombo.SelectedIndex = 0;
        }

        private void PopulateHotkeyTexts()
        {
            SnipHotkeyBox.Text          = _working.SnipHotkey.ToString();
            SnipTimerHotkeyBox.Text     = _working.SnipTimerHotkey.ToString();
            OcrHotkeyBox.Text           = _working.OcrHotkey.ToString();
            RecordHotkeyBox.Text        = _working.RecordHotkey.ToString();
            ScrollCaptureHotkeyBox.Text = _working.ScrollCaptureHotkey.ToString();
            HistoryHotkeyBox.Text       = _working.HistoryHotkey.ToString();
            NextHotkeyBox.Text          = _working.NextHotkey.ToString();
            PrevHotkeyBox.Text          = _working.PrevHotkey.ToString();
        }

        private void UpdateJpegRowVisibility()
        {
            bool isJpeg = (FormatCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() == "JPEG";
            JpegRow.Visibility = isJpeg ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => UpdateJpegRowVisibility();

        // ─────────────────────────────── gallery ──────────────────────────────
        //
        // Built on the assumption that the folder holds thousands of files, because for
        // anyone using this daily it eventually will.
        //
        // Three things follow from that. Scanning reads metadata only — a directory of
        // ten thousand captures enumerates in milliseconds, where decoding ten thousand
        // thumbnails would take minutes and hundreds of megabytes. Filtering and sorting
        // then run over that cheap in-memory list. Only the page actually on screen gets
        // decoded, and that decode is cancelled the moment the user pages, sorts or
        // searches again, so holding an arrow key doesn't queue up work nobody wants.

        private List<GalleryItem> _allItems = new();     // every file, metadata only
        private List<GalleryItem> _viewItems = new();    // after search + sort
        private int _page;
        private CancellationTokenSource? _thumbCts;
        private string _search = "";      // file name
        private string _textSearch = "";  // words inside the picture

        /// <summary>
        /// Which list the gallery is showing. The three never mix — the questions you ask
        /// of them differ, and so do the verbs that apply.
        /// </summary>
        private GalleryKind _mode = GalleryKind.Image;

        private bool VideoMode => _mode == GalleryKind.Video;
        private bool PdfMode => _mode == GalleryKind.Pdf;

        /// <summary>
        /// The folder the current mode reads. Recordings can be sent somewhere else
        /// entirely, and when they are not, VideoFolderOrDefault falls back to the capture
        /// folder — in which case both tabs read one directory and are told apart by
        /// extension, which is exactly what the extension lists below are for.
        /// </summary>
        // PDFs live with the captures: the editor's export dialog starts in the save
        // folder, so that is where they land unless someone deliberately puts them
        // elsewhere — and "PDFs in the Advanced Snip folder" is the whole ask.
        private string CurrentFolder => VideoMode
            ? _app.Settings.VideoFolderOrDefault
            : _app.Settings.SaveFolder;

        private static readonly string[] ImageExtensions =
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".ico", ".webp", ".gif"
        };

        // .mp4 is all this app writes. The others are here because a folder people point at
        // their own recordings is a folder that will contain other people's formats, and
        // listing a file we cannot thumbnail is still better than pretending it is absent.
        private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".mov", ".avi", ".webm" };

        private static readonly string[] PdfExtensions = { ".pdf" };

        private static string[] ExtensionsFor(GalleryKind kind) => kind switch
        {
            GalleryKind.Video => VideoExtensions,
            GalleryKind.Pdf   => PdfExtensions,
            _                 => ImageExtensions
        };

        private int PageSize => Math.Clamp(_app.Settings.GalleryPageSize, 50, 500);
        private int PageCount => Math.Max(1, (_viewItems.Count + PageSize - 1) / PageSize);

        private async Task LoadGalleryAsync()
        {
            if (_loadingGallery) return;
            _loadingGallery = true;

            CancelThumbnails();
            GalleryStatus.Text = "Scanning…";
            GalleryList.ItemsSource = null;
            UpdateSelectionUi();

            try
            {
                string folder = CurrentFolder;
                var kind = _mode;
                _allItems = await Task.Run(() => ScanFolder(folder, kind));
                _page = 0;
                ApplyFilterAndSort();

                // New captures since the last pass need reading too. Videos are never
                // indexed — there are no words in an MP4 to find.
                if (!VideoMode && OcrSearchChk.IsChecked == true && OcrService.IsAvailable)
                    StartIndexing();
            }
            catch (Exception ex)
            {
                GalleryStatus.Text = "Error: " + ex.Message;
            }
            finally
            {
                _loadingGallery = false;
            }
        }

        /// <summary>
        /// Metadata pass. Deliberately decodes nothing — the point is that this stays
        /// fast no matter how big the folder gets.
        /// </summary>
        private static List<GalleryItem> ScanFolder(string folder, GalleryKind kind)
        {
            var result = new List<GalleryItem>();
            if (!Directory.Exists(folder)) return result;

            bool videos = kind == GalleryKind.Video;
            var wanted = ExtensionsFor(kind);

            foreach (var path in Directory.EnumerateFiles(folder))
            {
                string ext = Path.GetExtension(path);
                bool match = false;
                foreach (var w in wanted)
                    if (string.Equals(ext, w, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                if (!match) continue;

                try
                {
                    var info = new FileInfo(path);

                    // A recording still being written has a real name and a length that is
                    // meaningless, and opening it for a thumbnail would block. It will
                    // appear on the next refresh, which the stop-recording path triggers.
                    if (videos && info.Length == 0) continue;

                    result.Add(new GalleryItem
                    {
                        FilePath = path,
                        Bytes    = info.Length,
                        Captured = CaptureTimeOf(info),
                        Kind     = kind
                    });
                }
                catch { /* file vanished mid-scan; skip it */ }
            }
            return result;
        }

        /// <summary>
        /// When the capture was taken. The filename is the most trustworthy source for
        /// our own files — copying or syncing a folder rewrites the timestamps, but
        /// "Snip_20260728_143005_221" still says exactly when it was taken.
        /// </summary>
        private static DateTime CaptureTimeOf(FileInfo info)
        {
            // If the file is an edited copy or export, its modification time is when the edit was performed
            if (info.Name.Contains("_edited", StringComparison.OrdinalIgnoreCase) ||
                info.Name.Contains("_page", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(info.Name, @"_p\d+", RegexOptions.IgnoreCase))
            {
                var editTime = info.LastWriteTime;
                return editTime.Year > 1980 ? editTime : info.CreationTime;
            }

            var m = Regex.Match(info.Name, @"(\d{8})[_-](\d{6})");
            if (m.Success &&
                DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value,
                    "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
            {
                // If modified significantly after the name timestamp (re-edited), use LastWriteTime
                if (info.LastWriteTime > parsed.AddMinutes(5))
                    return info.LastWriteTime;

                return parsed;
            }

            var written = info.LastWriteTime;
            var created = info.CreationTime;
            return written > created ? written : (created.Year > 1980 ? created : written);
        }

        private DateTime? _from, _to;

        private void ApplyFilterAndSort()
        {
            IEnumerable<GalleryItem> q = _allItems;

            if (_from.HasValue) q = q.Where(i => i.Captured >= _from.Value);
            if (_to.HasValue)   q = q.Where(i => i.Captured <  _to.Value);

            // The two boxes are independent filters, and both narrow. Searching a name
            // and a phrase together means "this file, containing that" rather than some
            // ambiguous blend of the two.
            if (!string.IsNullOrWhiteSpace(_search))
            {
                string name = _search;
                q = q.Where(i => i.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(_textSearch))
            {
                string words = _textSearch;
                q = q.Where(i => _app.Ocr.Matches(i.FilePath, words));
            }

            q = _app.Settings.GallerySort switch
            {
                "OldestFirst" => q.OrderBy(i => i.Captured),
                "NameAsc"     => q.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
                "NameDesc"    => q.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase),
                "SizeDesc"    => q.OrderByDescending(i => i.Bytes),
                "SizeAsc"     => q.OrderBy(i => i.Bytes),
                _             => q.OrderByDescending(i => i.Captured)
            };

            _viewItems = q.ToList();
            _page = Math.Clamp(_page, 0, PageCount - 1);
            ShowPage();
        }

        private void ShowPage()
        {
            CancelThumbnails();

            int from = _page * PageSize;
            int take = Math.Min(PageSize, Math.Max(0, _viewItems.Count - from));
            var slice = take > 0 ? _viewItems.GetRange(from, take) : new List<GalleryItem>();

            // Thumbnails used to be decoded once and then kept forever, because the
            // GalleryItem objects in _allItems outlive the page they were shown on. Paging
            // through a folder of two thousand captures therefore accumulated two thousand
            // decoded thumbnails — roughly a quarter of a gigabyte — none of which were on
            // screen. They cost a few milliseconds each to rebuild, so releasing them is
            // free in practice.
            ReleaseOffscreenThumbnails(slice);

            GalleryList.ItemsSource = slice;
            GalleryEmpty.Visibility = _viewItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_viewItems.Count == 0)
            {
                bool searching = !string.IsNullOrWhiteSpace(_search);
                GalleryEmptyTitle.Text = searching
                    ? "Nothing matches"
                    : _mode switch
                    {
                        GalleryKind.Video => "No recordings yet",
                        GalleryKind.Pdf   => "No PDFs here yet",
                        _                 => "No snips yet"
                    };
                GalleryEmptyHint.Text = searching
                    ? $"No file name contains “{_search}”."
                    : _mode switch
                    {
                        GalleryKind.Video => "Press Record in the title bar, or Ctrl+Shift+R, to record the screen.",
                        GalleryKind.Pdf   => "Open a capture in the editor and press Ctrl+P to save it as a PDF.",
                        _                 => "Press Snip in the title bar, or your snip hotkey, to capture a region."
                    };
            }

            // Back to the top for a new page of results. The list owns its own scrolling
            // now, and an empty list has nothing to scroll to.
            if (GalleryList.Items.Count > 0) GalleryList.ScrollIntoView(GalleryList.Items[0]);

            string noun = _mode switch
            {
                GalleryKind.Video => "recording",
                GalleryKind.Pdf   => "PDF",
                _                 => "capture"
            };
            string total = _viewItems.Count == _allItems.Count
                ? $"{_allItems.Count:N0} {noun}{(_allItems.Count == 1 ? "" : "s")}"
                : $"{_viewItems.Count:N0} of {_allItems.Count:N0}";

            GalleryStatus.Text = _viewItems.Count == 0
                ? "Nothing to show."
                : $"Showing {from + 1:N0}–{from + take:N0} of {total}";

            bool paged = PageCount > 1;
            GalleryPager.Visibility  = paged ? Visibility.Visible : Visibility.Collapsed;
            GalleryPageText.Text     = $"Page {_page + 1} of {PageCount}";
            GalleryPrevBtn.IsEnabled = _page > 0;
            GalleryNextBtn.IsEnabled = _page < PageCount - 1;

            UpdateSelectionUi();
            _ = LoadThumbnailsAsync(slice);
        }

        /// <summary>Drops decoded thumbnails for every item that isn't on the visible page.</summary>
        private void ReleaseOffscreenThumbnails(List<GalleryItem> keep)
        {
            if (_allItems.Count <= keep.Count) return;

            var onPage = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in keep) onPage.Add(item.FilePath);

            foreach (var item in _allItems)
                if (item.Thumb != null && !onPage.Contains(item.FilePath))
                    item.Thumb = null;

            MemoryGuard.RequestTrim();
        }

        private void CancelThumbnails()
        {
            try { _thumbCts?.Cancel(); } catch { }
            _thumbCts?.Dispose();
            _thumbCts = null;
        }

        /// <summary>
        /// Decodes the visible page in the background, publishing each thumbnail as it
        /// lands so the grid fills in progressively instead of appearing all at once
        /// after a stall.
        /// </summary>
        private async Task LoadThumbnailsAsync(List<GalleryItem> items)
        {
            if (items.Count == 0) return;

            _thumbCts = new CancellationTokenSource();
            var token = _thumbCts.Token;

            // Video frames come from MediaPlayer, which has dispatcher affinity and so
            // cannot be moved to a worker the way image decoding is. It is awaited one file
            // at a time on the UI thread instead — each probe yields between awaits, so the
            // window stays responsive, and cancelling on a page change stops the queue.
            if (VideoMode)
            {
                await LoadVideoThumbnailsAsync(items, token);
                return;
            }

            if (PdfMode)
            {
                await LoadPdfThumbnailsAsync(items, token);
                return;
            }

            try
            {
                await Task.Run(() =>
                {
                    foreach (var item in items)
                    {
                        if (token.IsCancellationRequested) return;
                        if (item.Thumb != null) continue;

                        // Cache first. Decoding a capture at reduced size still parses the
                        // whole PNG, so a 4K screenshot is megabytes of decompression for a
                        // 360px picture — and the gallery pays it again on every visit to
                        // the folder. A small JPEG read back is a fraction of that.
                        var cached = ThumbnailCache.TryLoad(item.FilePath);
                        if (cached.HasValue)
                        {
                            var hit = cached.Value.Frame;
                            if (token.IsCancellationRequested) return;
                            Dispatcher.BeginInvoke(new Action(() => item.Thumb = hit),
                                                   DispatcherPriority.Background);
                            continue;
                        }

                        BitmapSource? thumb = null;
                        try
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            // IgnoreImageCache opts out of WPF's process-wide decoded-image
                            // cache. That cache is keyed by URI and holds a strong
                            // reference, so without this every capture the gallery has ever
                            // rendered stays in memory for the life of the process no
                            // matter what this window does with its own references.
                            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                            // Decoding straight to thumbnail size is what keeps a page of
                            // 4K captures to a few megabytes instead of a few hundred.
                            bmp.DecodePixelWidth = 360;
                            bmp.UriSource = new Uri(item.FilePath);
                            bmp.EndInit();
                            bmp.Freeze();
                            thumb = bmp;
                        }
                        catch { /* corrupt or locked; leave the placeholder */ }

                        if (thumb == null || token.IsCancellationRequested) continue;

                        // Already on a worker, so the encode happens here rather than being
                        // handed off. Failures are ignored: the picture is about to be on
                        // screen either way, and a cache miss next time is not worth an error.
                        ThumbnailCache.Store(item.FilePath, thumb, TimeSpan.Zero);

                        Dispatcher.BeginInvoke(new Action(() => item.Thumb = thumb),
                                               DispatcherPriority.Background);
                    }
                }, token);
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        /// <summary>
        /// Renders the first page of each PDF, and counts the pages.
        ///
        /// Sequential and awaited on the UI thread for the same reason the video loader is:
        /// the platform renderer is asynchronous but not free, and firing thirty at once
        /// buys nothing except contention. Cached exactly like the others, so a folder is
        /// only rendered the first time it is looked at.
        /// </summary>
        private async Task LoadPdfThumbnailsAsync(List<GalleryItem> items,
                                                  CancellationToken token)
        {
            int attempted = 0, failed = 0;

            // Cleared per pass, so a refresh after the underlying problem is fixed does not
            // keep showing an error that is no longer true.
            OcrStatusText.Text = "";

            try
            {
                foreach (var item in items)
                {
                    if (token.IsCancellationRequested) return;
                    if (item.Thumb != null) continue;

                    var cached = ThumbnailCache.TryLoad(item.FilePath);
                    if (cached.HasValue)
                    {
                        // Page count rides in the same slot the video duration uses —
                        // whole milliseconds, so it survives the round trip intact.
                        item.Pages = (int)cached.Value.Duration.TotalMilliseconds;
                        item.Thumb = cached.Value.Frame;
                        continue;
                    }

                    attempted++;

                    var probe = await PdfPreviewer.ProbeAsync(item.FilePath, 360, token);
                    if (token.IsCancellationRequested) return;
                    if (probe == null) { failed++; continue; }   // encrypted, truncated, not a PDF

                    item.Pages = (int)probe.PageCount;
                    if (probe.FirstPage == null) { failed++; continue; }

                    item.Thumb = probe.FirstPage;

                    var frame = probe.FirstPage;
                    string path = item.FilePath;
                    var pages = TimeSpan.FromMilliseconds(probe.PageCount);
                    _ = Task.Run(() => ThumbnailCache.Store(path, frame, pages));
                }

                // Every single one failing is a broken renderer, not a folder of broken
                // files — and that is worth saying out loud. The previous version failed
                // exactly this way and said nothing, so the tab just looked empty-ish and
                // there was no thread to pull.
                if (failed > 0 && failed == attempted && PdfPreviewer.LastError is { Length: > 0 } why)
                    OcrStatusText.Text = $"Page previews unavailable — {why}";
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        /// <summary>
        /// Fills in a frame and a running time for each video card.
        ///
        /// Deliberately sequential. Opening several media files at once contends for the
        /// same decoders and, on a page of thirty recordings, is a good way to make the
        /// window stutter for the sake of thumbnails nobody is looking at yet. One at a
        /// time, cancelled on the next page turn, keeps the cost proportional to what is
        /// actually on screen.
        /// </summary>
        private async Task LoadVideoThumbnailsAsync(List<GalleryItem> items,
                                                    CancellationToken token)
        {
            try
            {
                // Pass one: everything already on disk, read on a worker in one go. This
                // is the pass that matters — after the first visit to a folder it is
                // usually all of them, and the page fills at the speed of reading small
                // JPEGs instead of the speed of decoding video.
                var pending = new List<GalleryItem>();

                var cached = await Task.Run(() =>
                {
                    var found = new List<(GalleryItem Item, BitmapSource Frame, TimeSpan Duration)>();
                    foreach (var item in items)
                    {
                        if (token.IsCancellationRequested) break;
                        if (item.Thumb != null) continue;

                        var hit = ThumbnailCache.TryLoad(item.FilePath);
                        if (hit.HasValue) found.Add((item, hit.Value.Frame, hit.Value.Duration));
                        else pending.Add(item);
                    }
                    return found;
                }, token);

                if (token.IsCancellationRequested) return;

                foreach (var (item, frame, duration) in cached)
                {
                    if (duration > TimeSpan.Zero) item.Duration = duration;
                    item.Thumb = frame;
                }

                // Pass two: the ones that have never been seen. Still sequential, because
                // MediaPlayer has dispatcher affinity and opening several at once contends
                // for the same decoders — but now it only runs for genuinely new files.
                foreach (var item in pending)
                {
                    if (token.IsCancellationRequested) return;

                    var probe = await VideoThumbnailer.ProbeAsync(item.FilePath, 360, 240, token);
                    if (token.IsCancellationRequested) return;
                    if (probe == null) continue;      // unreadable: the card keeps its icon

                    if (probe.Duration > TimeSpan.Zero) item.Duration = probe.Duration;
                    if (probe.Frame == null) continue;

                    item.Thumb = probe.Frame;

                    // Written on a worker: the frame is frozen, and the encode plus the
                    // file write have no business blocking the page that is still filling.
                    var frame = probe.Frame;
                    var duration = probe.Duration;
                    string path = item.FilePath;
                    _ = Task.Run(() => ThumbnailCache.Store(path, frame, duration));
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        // ── selection ─────────────────────────────────────────────────────────

        private List<GalleryItem> SelectedItems() =>
            GalleryList.SelectedItems.Cast<GalleryItem>().ToList();

        private void UpdateSelectionUi()
        {
            int n = GalleryList.SelectedItems?.Count ?? 0;
            var selected = SelectedItems();
            bool isAllImages = selected.Count > 0 && selected.All(i => !i.IsVideo && !i.IsPdf);
            bool isAllNonVideos = selected.Count > 0 && selected.All(i => !i.IsVideo);

            GalleryDeleteBtn.IsEnabled = n > 0;
            GalleryClearSelBtn.IsEnabled = n > 0;
            GallerySelectAllBtn.IsEnabled = GalleryList.Items.Count > 0;
            GalleryEditBtn.IsEnabled   = n == 1 && isAllImages;
            GalleryCopyBtn.IsEnabled   = n > 0 && isAllNonVideos;
            GalleryPlayBtn.IsEnabled   = n == 1;

            // Multi-image PDF conversion button
            GalleryConvertToPdfBtn.Visibility = (n >= 2 && isAllImages) ? Visibility.Visible : Visibility.Collapsed;
            GalleryConvertToPdfBtn.IsEnabled = n >= 2 && isAllImages;

            GallerySelectionText.Text = n switch
            {
                0 => "",
                1 => "1 selected",
                _ => $"{n} selected  ·  {FormatBytes(SelectedItems().Sum(i => i.Bytes))}"
            };
        }

        private void GalleryContextMenu_Opening(object sender, ContextMenuEventArgs e)
        {
            var selected = SelectedItems();
            int n = selected.Count;
            if (n == 0)
            {
                MenuPin.Visibility = Visibility.Collapsed;
                MenuEdit.Visibility = Visibility.Collapsed;
                MenuConvertToPdf.Visibility = Visibility.Collapsed;
                MenuOpenWith.Visibility = Visibility.Collapsed;
                MenuAddToHistory.Visibility = Visibility.Collapsed;
                MenuOpenFile.Visibility = Visibility.Collapsed;
                return;
            }

            bool isAllImages = selected.All(i => !i.IsVideo && !i.IsPdf);
            bool isAllNonVideos = selected.All(i => !i.IsVideo);

            // Pin to screen: visible when exactly 1 image is selected
            MenuPin.Visibility = (n == 1 && isAllImages) ? Visibility.Visible : Visibility.Collapsed;

            // Edit option: ONLY visible when exactly 1 image is selected (hidden for multi-selection)
            MenuEdit.Visibility = (n == 1 && isAllImages) ? Visibility.Visible : Visibility.Collapsed;

            // Convert to PDF option: visible when 2 or more images are selected
            MenuConvertToPdf.Visibility = (n >= 2 && isAllImages) ? Visibility.Visible : Visibility.Collapsed;

            // Add to clipboard history: visible for images and PDFs
            MenuAddToHistory.Visibility = isAllNonVideos ? Visibility.Visible : Visibility.Collapsed;

            // Open file: ONLY visible when a single item is selected (hidden for multi-selection)
            MenuOpenFile.Visibility = n == 1 ? Visibility.Visible : Visibility.Collapsed;

            // Play / view: single video or PDF
            MenuOpenWith.Visibility = (n == 1 && (selected[0].IsVideo || selected[0].IsPdf)) ? Visibility.Visible : Visibility.Collapsed;
            if (n == 1 && selected[0].IsPdf) MenuOpenWith.Header = "View…";
            else if (n == 1 && selected[0].IsVideo) MenuOpenWith.Header = "Play / trim…";
        }

        internal static string FormatBytes(long bytes) =>
            bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824.0:F2} GB"
          : bytes >= 1_048_576     ? $"{bytes / 1_048_576.0:F1} MB"
          :                          $"{bytes / 1024.0:F0} KB";

        private void GalleryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => UpdateSelectionUi();

        private void GallerySelectAll_Click(object sender, RoutedEventArgs e)
            => GalleryList.SelectAll();

        private void GalleryClearSelection_Click(object sender, RoutedEventArgs e)
            => GalleryList.UnselectAll();

        private void GalleryList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)      { GalleryDelete_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.Enter)  { OpenSelected();                  e.Handled = true; }
            else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            { GalleryList.SelectAll(); e.Handled = true; }
        }

        // ── search / sort / paging ────────────────────────────────────────────

        private void GallerySearch_Changed(object sender, TextChangedEventArgs e)
        {
            _search = GallerySearchBox.Text ?? "";
            SearchClearBtn.Visibility = _search.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _page = 0;
            ApplyFilterAndSort();
        }

        private void SearchClear_Click(object sender, RoutedEventArgs e)
            => GallerySearchBox.Text = "";

        private void TextSearch_Changed(object sender, TextChangedEventArgs e)
        {
            _textSearch = OcrSearchInput.Text ?? "";
            TextSearchClearBtn.Visibility =
                _textSearch.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _page = 0;
            ApplyFilterAndSort();
        }

        private void TextSearchClear_Click(object sender, RoutedEventArgs e)
            => OcrSearchInput.Text = "";

        private void GallerySort_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_populating) return;
            if ((GallerySortCombo.SelectedItem as ComboBoxItem)?.Tag is not string tag) return;

            _app.Settings.GallerySort = tag;
            _working.GallerySort = tag;
            _app.Settings.Save();

            _page = 0;
            ApplyFilterAndSort();
        }

        private void GalleryPrev_Click(object sender, RoutedEventArgs e)
        {
            if (_page == 0) return;
            _page--;
            ShowPage();
        }

        private void GalleryNext_Click(object sender, RoutedEventArgs e)
        {
            if (_page >= PageCount - 1) return;
            _page++;
            ShowPage();
        }

        // ── date range ────────────────────────────────────────────────────────

        private void DateRange_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_populating) return;
            if ((DateRangeCombo.SelectedItem as ComboBoxItem)?.Tag is not string tag) return;

            bool custom = tag == "Custom";
            CustomRangePanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;

            var today = DateTime.Today;
            switch (tag)
            {
                case "Today":     _from = today;                  _to = today.AddDays(1);  break;
                case "Yesterday": _from = today.AddDays(-1);      _to = today;             break;
                case "Week":      _from = today.AddDays(-6);      _to = today.AddDays(1);  break;
                case "Month":     _from = today.AddDays(-29);     _to = today.AddDays(1);  break;
                case "Year":      _from = new DateTime(today.Year, 1, 1); _to = today.AddDays(1); break;

                case "Custom":
                    DateFrom.SelectedDate ??= today.AddDays(-7);
                    DateTo.SelectedDate   ??= today;
                    ApplyCustomRange();
                    return;

                default:          _from = null;                   _to = null;              break;
            }

            _page = 0;
            ApplyFilterAndSort();
        }

        private void CustomDate_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_populating) return;
            ApplyCustomRange();
        }

        private void ApplyCustomRange()
        {
            var a = DateFrom.SelectedDate?.Date;
            var b = DateTo.SelectedDate?.Date;

            // Tolerate the dates being entered the wrong way round rather than silently
            // showing nothing.
            if (a.HasValue && b.HasValue && a > b) (a, b) = (b, a);

            _from = a;
            _to   = b?.AddDays(1);      // inclusive of the whole end day

            _page = 0;
            ApplyFilterAndSort();
        }

        // ── text search inside images ─────────────────────────────────────────

        private void OcrSearch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_populating) return;

            bool on = OcrSearchChk.IsChecked == true;
            _working.GalleryOcrSearch = on;
            _app.Settings.GalleryOcrSearch = on;
            _app.Settings.Save();

            ShowTextSearch(on);

            if (!on)
            {
                _app.Ocr.Stop();
                OcrStatusText.Text = "";
                _textSearch = "";
                _page = 0;
                ApplyFilterAndSort();
                return;
            }

            if (!OcrService.IsAvailable)
            {
                OcrSearchChk.IsChecked = false;
                ThemedDialog.Show(this, OcrService.UnavailableReason, "Advanced Snip",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            StartIndexing();
        }

        private void ShowTextSearch(bool on)
        {
            TextSearchBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) OcrSearchInput.Text = "";
        }

        /// <summary>
        /// Kicks off recognition for anything not already cached. Deliberately only when
        /// the user asks for text search — reading every picture in the folder is real
        /// CPU work and shouldn't happen behind their back.
        /// </summary>
        /// <summary>
        /// Re-reads every capture from scratch, ignoring anything already cached.
        ///
        /// The ordinary pass skips whatever the cache has an answer for, and a cached
        /// "no text" is indistinguishable from a cached result — so a picture that reads
        /// perfectly in the editor and never appears in search has no other way back into
        /// the queue. This is the user saying "read it again anyway".
        /// </summary>
        private async void Reindex_Click(object sender, RoutedEventArgs e)
        {
            if (!OcrService.IsAvailable)
            {
                ThemedDialog.Show(this, OcrService.UnavailableReason, "Advanced Snip",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Turning it on is implied by asking for the index to be built.
            if (OcrSearchChk.IsChecked != true) OcrSearchChk.IsChecked = true;

            // Reached from Settings, where the gallery's current tab and page mean nothing.
            // Scan the capture folder directly rather than reusing whatever the gallery
            // happens to be showing — "rebuild the index" means all of it.
            var folder = _app.Settings.SaveFolder;
            var files = await Task.Run(() => ScanFolder(folder, GalleryKind.Image))
                                  .ConfigureAwait(true);

            if (files.Count == 0)
            {
                ReindexStatusText.Text = "No captures to read.";
                return;
            }

            ReindexStatusText.Text =
                $"Re-reading {files.Count:N0} capture{(files.Count == 1 ? "" : "s")}…";

            _ = _app.Ocr.IndexAsync(files.Select(i => (i.FilePath, i.Captured)).ToList(),
                                    force: true);
        }

        private void ReindexCancel_Click(object sender, RoutedEventArgs e)
        {
            // Stop() flushes before cancelling, so everything read so far is kept.
            _app.Ocr.Stop();
            ShowIndexingChrome(false);
            ReindexStatusText.Text = "Stopped. What was read is kept.";
        }

        /// <summary>Swaps Rebuild for Stop, and shows the bar, while a pass is running.</summary>
        /// <summary>
        /// Swaps Rebuild for Stop in Settings, and shows the gallery's bar, while a pass
        /// runs. Both, because the pass can be started from either place and watched from
        /// the other — starting a rebuild in Settings and then going to the gallery to
        /// watch it is the obvious thing to do.
        /// </summary>
        private void ShowIndexingChrome(bool running)
        {
            ReindexBtn.IsEnabled = !running;
            ReindexCancelBtn.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            OcrProgressBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        }

        private void StartIndexing()
        {
            if (_mode != GalleryKind.Image) return;   // only pictures carry words to index
            if (_allItems.Count == 0) return;

            var files = _allItems.Select(i => (i.FilePath, i.Captured)).ToList();
            _ = _app.Ocr.IndexAsync(files);
        }

        private void OnOcrProgress(object? sender, OcrIndexProgress p)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (OcrStatusText == null) return;

                ShowIndexingChrome(!p.Finished);

                if (!p.Finished && p.Total > 0)
                    OcrProgressBar.Value = Math.Clamp((double)p.Done / p.Total, 0, 1);

                if (p.Finished)
                {
                    // Naming the ones that came back empty separates "the search is
                    // broken" from "those pictures have no words in them" — two complaints
                    // that look identical from the outside, and the difference is the
                    // whole diagnosis.
                    string done = p.Total == 0
                        ? "All captures indexed"
                        : $"Indexed {p.Total:N0} capture{(p.Total == 1 ? "" : "s")}";

                    OcrStatusText.Text = p.Empty > 0
                        ? $"{done} · {p.Empty:N0} had no readable text"
                        : done;

                    // Settings shows the same answer, because whoever pressed Rebuild is
                    // probably still looking at the button they pressed.
                    ReindexStatusText.Text = OcrStatusText.Text;

                    // Results found while indexing was still running are now complete.
                    if (!string.IsNullOrWhiteSpace(_textSearch)) ApplyFilterAndSort();
                    return;
                }

                OcrStatusText.Text = $"Reading images… {p.Done:N0} of {p.Total:N0}";

                // Refresh periodically so matches appear as they're discovered rather
                // than all at once at the end.
                if (p.Done % 25 == 0 && !string.IsNullOrWhiteSpace(_textSearch))
                    ApplyFilterAndSort();
            }), DispatcherPriority.Background);
        }

        // ── actions ───────────────────────────────────────────────────────────

        /// <summary>
        /// Sends the wheel to the gallery's own scroll viewer.
        ///
        /// A ListBox carries a ScrollViewer inside its template. Sitting inside another
        /// ScrollViewer it's given unlimited height, so its inner viewer has nothing to
        /// scroll — but WPF's ScrollViewer marks the wheel event handled regardless, so
        /// the outer one never hears about it. That's why scrolling only worked with the
        /// pointer over the scrollbar itself. Handling the tunnelling preview means this
        /// runs before the ListBox gets a chance to swallow it.
        /// </summary>

        private void GalleryRefresh_Click(object sender, RoutedEventArgs e)
            => _ = LoadGalleryAsync();

        private void GalleryOpenFolder_Click(object sender, RoutedEventArgs e)
            => OpenPath(_app.Settings.SaveFolder);

        private void GalleryList_DoubleClick(object sender, MouseButtonEventArgs e)
            => OpenSelected();

        /// <summary>
        /// Opens the selected item in whatever the right thing is for its kind.
        ///
        /// Every route in — double-click, Enter, the Edit and Open buttons, both context
        /// menu entries — comes through here. They used to open the image editor
        /// unconditionally, which was correct while the gallery only ever held images and
        /// would hand an MP4 to a bitmap decoder now that it does not.
        /// </summary>
        private void OpenSelected()
        {
            if (GalleryList.SelectedItem is not GalleryItem item) return;

            if (item.IsVideo) _app.OpenVideoReview(item.FilePath);
            else if (item.IsPdf) _app.OpenPdfViewer(item.FilePath);
            else _app.OpenEditorForFile(item.FilePath);
        }

        private void GalleryEdit_Click(object sender, RoutedEventArgs e) => OpenSelected();

        private void GalleryPin_Click(object sender, RoutedEventArgs e)
        {
            if (GalleryList.SelectedItem is not GalleryItem item || item.IsVideo || item.IsPdf) return;

            try
            {
                var bmp = ImageInterop.LoadBitmap(item.FilePath);
                PinnedClipWindow.ShowForImage(bmp, item.FilePath, item.Name);
            }
            catch (Exception ex)
            {
                ThemedDialog.Show(this, "Could not pin image: " + ex.Message, "Pin Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void GalleryConvertToPdf_Click(object sender, RoutedEventArgs e)
        {
            var paths = GalleryList.SelectedItems
                                   .OfType<GalleryItem>()
                                   .Where(i => !i.IsVideo && !i.IsPdf)
                                   .Select(i => i.FilePath)
                                   .ToList();

            if (paths.Count < 2)
            {
                GalleryStatus.Text = "Select two or more images to combine into a PDF.";
                return;
            }

            var dlg = new PdfExportWindow(paths, _app.Settings) { Owner = this };
            if (dlg.ShowDialog() == true && dlg.SavedPath is { Length: > 0 } saved)
            {
                GalleryStatus.Text = $"Created PDF with {paths.Count} images: {System.IO.Path.GetFileName(saved)}";
                if (PdfMode)
                    _ = LoadGalleryAsync();
            }
        }

        private void GalleryCopy_Click(object sender, RoutedEventArgs e)
            => GalleryAddToHistory_Click(sender, e);

        private void GalleryOpenFile_Click(object sender, RoutedEventArgs e)
        {
            if (GalleryList.SelectedItem is GalleryItem item)
                OpenPath(item.FilePath);
        }

        private void GalleryShowInExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (GalleryList.SelectedItem is GalleryItem item)
                Process.Start("explorer.exe", $"/select,\"{item.FilePath}\"");
        }

        /// <summary>
        /// Renders a PDF's first page and copies it as a picture.
        ///
        /// Rendered at 1600 px wide rather than at the page's natural size: this is going
        /// onto a clipboard, and something legible when pasted matters more here than
        /// matching the paper's dimensions.
        /// </summary>
        private async Task CopyPdfAsImageAsync(GalleryItem item)
        {
            try
            {
                GalleryStatus.Text = "Rendering " + item.Name + "…";

                var document = await PdfPreviewer.OpenAsync(item.FilePath);
                if (document == null || document.PageCount == 0)
                {
                    GalleryStatus.Text = PdfPreviewer.LastError is { Length: > 0 } why
                        ? "Couldn't read that PDF — " + why
                        : "Couldn't read that PDF.";
                    return;
                }

                var frame = await PdfPreviewer.RenderAsync(document, 0, 1600);
                if (frame == null)
                {
                    GalleryStatus.Text = "That page couldn't be rendered.";
                    return;
                }

                // No file path: the PNG on the clipboard is not the PDF on disk, and
                // handing over the .pdf path would make a paste-as-file produce a document
                // where a picture was asked for.
                var outcome = (System.Windows.Application.Current as App)?.CopyImage(frame, null);

                GalleryStatus.Text = outcome?.Warning
                    ?? $"Copied page 1 of {item.Name} as an image"
                       + (document.PageCount > 1 ? $" ({document.PageCount} pages — open it to copy another)" : "");
            }
            catch (Exception ex)
            {
                GalleryStatus.Text = $"Couldn't copy that PDF ({ex.GetType().Name}: {ex.Message}).";
            }
            finally { MemoryGuard.RequestTrim(); }
        }

        private void GalleryDelete_Click(object sender, RoutedEventArgs e)
        {
            var chosen = SelectedItems();
            if (chosen.Count == 0) return;

            bool recycle = _app.Settings.GalleryUseRecycleBin;
            string plural = _mode switch
            {
                GalleryKind.Video => "recordings",
                GalleryKind.Pdf   => "PDFs",
                _                 => "captures"
            };
            string what = chosen.Count == 1
                ? chosen[0].Name
                : $"{chosen.Count} {plural} ({FormatBytes(chosen.Sum(i => i.Bytes))})";

            var answer = ThemedDialog.Show(this,
                recycle
                    ? $"Move {what} to the Recycle Bin?"
                    : $"Permanently delete {what}?\nThis cannot be undone.",
                "Advanced Snip", MessageBoxButton.YesNo,
                recycle ? MessageBoxImage.Question : MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes) return;

            var paths = chosen.Select(i => i.FilePath).ToList();
            int failed = 0;

            if (recycle)
            {
                if (!Win32.RecycleFiles(paths,
                        new System.Windows.Interop.WindowInteropHelper(this).Handle))
                    failed = paths.Count(File.Exists);
            }
            else
            {
                foreach (var path in paths)
                {
                    try { File.Delete(path); }
                    catch { failed++; }
                }
            }

            // Drop the gone files from the in-memory list rather than re-scanning: on a
            // folder of thousands that keeps the page the user was looking at intact.
            var removed = paths.Where(p => !File.Exists(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _allItems.RemoveAll(i => removed.Contains(i.FilePath));
            ApplyFilterAndSort();

            if (failed > 0)
                GalleryStatus.Text = $"{failed} file{(failed == 1 ? "" : "s")} couldn't be deleted.";
        }

        private static void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
            catch { }
        }

        // ─────────────────────────────── hotkey capture ───────────────────────

        private void Hotkey_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb) tb.Text = "Press keys\u2026";
        }

        private void Hotkey_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb) tb.Text = DefForTag(tb.Tag as string).ToString();
        }

        private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox tb) return;
            e.Handled = true;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape) { tb.Text = DefForTag(tb.Tag as string).ToString(); Keyboard.ClearFocus(); return; }
            if (IsModifierKey(key)) return;

            ModifierKeys mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.None) { tb.Text = "Add Ctrl / Alt / Shift / Win\u2026"; return; }

            var def = DefForTag(tb.Tag as string);
            def.Modifiers = (int)mods;
            def.Key       = (int)key;
            tb.Text       = def.ToString();
        }

        private HotkeyDef DefForTag(string? tag) => tag switch
        {
            "Snip"          => _working.SnipHotkey,
            "SnipTimer"     => _working.SnipTimerHotkey,
            "Ocr"           => _working.OcrHotkey,
            "Record"        => _working.RecordHotkey,
            "ScrollCapture" => _working.ScrollCaptureHotkey,
            "History"       => _working.HistoryHotkey,
            "Next"          => _working.NextHotkey,
            "Prev"          => _working.PrevHotkey,
            _               => _working.SnipHotkey
        };

        private static bool IsModifierKey(Key key) => key
            is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin
            or Key.System or Key.None
            or Key.DeadCharProcessed or Key.ImeProcessed;

        // ─────────────────────────────── folder buttons ───────────────────────

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where snips are saved" };
            if (!string.IsNullOrWhiteSpace(FolderBox.Text) && Directory.Exists(FolderBox.Text))
                dlg.InitialDirectory = FolderBox.Text;
            if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
            => OpenPath(FolderBox.Text.Trim());

        // ─────────────────────────────── footer actions ───────────────────────

        private void HideBtn_Click(object sender, RoutedEventArgs e)    => Hide();

        private void ClearBtn_Click(object sender, RoutedEventArgs e)
        {
            _app.ClearHistory();
            StatusText.Text = "Clipboard history cleared.";
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            string folder = FolderBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                ThemedDialog.Show(this, "Please choose a save folder.", "Advanced Snip",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try { Directory.CreateDirectory(folder); }
            catch (Exception ex)
            {
                ThemedDialog.Show(this, "That folder can't be used:\n" + ex.Message, "Advanced Snip",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _working.SaveFolder            = folder;
            _working.FilenamePrefix        = string.IsNullOrWhiteSpace(PrefixBox.Text) ? "Snip" : PrefixBox.Text.Trim();
            _working.ImageFormat           = (FormatCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "PNG";
            _working.JpegQuality           = (int)JpegSlider.Value;
            _working.OverlayOpacity        = (int)OpacitySlider.Value;
            _working.MaxHistory            = Math.Max(5, (int)HistorySlider.Value);
            _working.ScrollMaxHeight        = (int)ScrollHeightSlider.Value;
            _working.ScrollSpeed            = (ScrollSpeedCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Balanced";
            _working.ScrollAutoDetectRegion = ScrollAutoRegionChk.IsChecked == true;
            _working.ScrollRestorePosition  = ScrollRestoreChk.IsChecked == true;
            _working.CopyToClipboardOnSnip = CopyChk.IsChecked == true;
            _working.ShowTrayNotification  = NotifyChk.IsChecked == true;
            _working.RunAtStartup          = StartupChk.IsChecked == true;
            _working.ShowSettingsOnStartup = ShowOnStartChk.IsChecked == true;
            _working.MinimiseToTrayOnClose = MinimiseChk.IsChecked == true;
            _working.Theme                 = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "System";
            _working.GalleryPageSize       = (int)PageSizeSlider.Value;
            _working.GalleryUseRecycleBin  = RecycleChk.IsChecked == true;
            _working.EditOnNotificationClick = EditOnClickChk.IsChecked == true;
            _working.GalleryOcrSearch      = OcrSearchChk.IsChecked == true;
            _working.GallerySort           = (GallerySortCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "NewestFirst";
            _working.ApplyTimerToSnipHotkey = ApplyTimerToSnipHotkeyChk.IsChecked == true;

            var failed = _app.UpdateSettings(_working);
            _working = _app.Settings.Clone();
            PopulateHotkeyTexts();

            StatusText.Text = failed.Count == 0
                ? "Saved."
                : "Saved — hotkeys already in use: " + string.Join(", ", failed);
        }

        // ─────────────────────────────── window close ─────────────────────────

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_app.IsExiting && _app.Settings.MinimiseToTrayOnClose)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnClosing(e);
        }
    }
}
