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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb)));
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
                return $"{when}  ·  {SizeLabel}";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
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

            // WindowStartupLocation="CenterScreen" always means the primary display; on a
            // multi-monitor desk that is rarely the one being worked on.
            SourceInitialized += (_, _) => WindowPlacement.CenterOnActiveMonitor(this);

            // Answers WM_GETMINMAXINFO so a self-drawn title bar still maximises to the
            // work area of the monitor it's on, rather than sliding under the taskbar.
            WindowChromeSupport.Attach(this);

            // The gallery's ListBox has its own ScrollViewer, which swallows the wheel
            // even though it has nothing to scroll. See Gallery_PreviewMouseWheel.
            GalleryScroller.PreviewMouseWheel += Gallery_PreviewMouseWheel;

            foreach (var box in new[] { SnipHotkeyBox, ScrollCaptureHotkeyBox, HistoryHotkeyBox, NextHotkeyBox, PrevHotkeyBox })
                box.LostFocus += Hotkey_LostFocus;

            // Re-load gallery whenever a new snip is taken while this window is open.
            app.Ocr.Progress += OnOcrProgress;

            app.SnipCompleted += (_, _) =>
            {
                if (IsVisible && PageGallery.Visibility == Visibility.Visible)
                    _ = LoadGalleryAsync();
            };
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
        }

        // ─────────────────────────────── navigation ───────────────────────────

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PageGallery == null) return; // called before InitializeComponent finishes

            PageGallery.Visibility  = Visibility.Collapsed;
            PageSettings.Visibility = Visibility.Collapsed;
            PageAbout.Visibility    = Visibility.Collapsed;

            if (sender == NavGallery)
            {
                PageGallery.Visibility = Visibility.Visible;
                _ = LoadGalleryAsync();
            }
            else if (sender == NavSettings)
                PageSettings.Visibility = Visibility.Visible;
            else
                PageAbout.Visibility = Visibility.Visible;
        }

        // ─────────────────────────────── LoadFromSettings ─────────────────────

        public void LoadFromSettings(AppSettings working)
        {
            _working = working;
            _populating = true;
            try
            {
                FolderBox.Text          = working.SaveFolder;
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

                SelectFormat(working.ImageFormat);
                UpdateJpegRowVisibility();
                PopulateHotkeyTexts();
                RefreshStartupStatus();
                StatusText.Text = "";
            }
            finally { _populating = false; }

            // NavGallery carries IsChecked="True" in XAML, so Nav_Checked fires during
            // InitializeComponent while PageGallery is still null and returns early — the
            // gallery was never loading on first open, and with it neither was OCR
            // indexing. Loading here also refreshes the list every time the window is
            // reopened, which is wanted anyway.
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

        private void Theme_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_populating) return;
            if ((ThemeCombo.SelectedItem as ComboBoxItem)?.Tag is not string tag) return;

            // Applied immediately rather than on Save: picking a theme you can't see
            // until you commit it is a poor way to choose one.
            ThemeManager.Apply(tag);
            _working.Theme = tag;
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

            StartupStatusText.Foreground = status.State switch
            {
                StartupState.Enabled          => (System.Windows.Media.Brush)FindResource("Brush.Success"),
                StartupState.Off              => (System.Windows.Media.Brush)FindResource("Brush.TextMuted"),
                StartupState.BlockedByWindows => (System.Windows.Media.Brush)FindResource("Brush.Warning"),
                _                             => (System.Windows.Media.Brush)FindResource("Brush.Danger")
            };
        }

        private void StartupFix_Click(object sender, RoutedEventArgs e)
        {
            var before = StartupManager.GetStatus(true);

            if (before.State == StartupState.BlockedByWindows)
            {
                if (!StartupManager.ClearWindowsBlock())
                {
                    MessageBox.Show(this,
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
                string folder = _app.Settings.SaveFolder;
                _allItems = await Task.Run(() => ScanFolder(folder));
                _page = 0;
                ApplyFilterAndSort();

                // New captures since the last pass need reading too.
                if (OcrSearchChk.IsChecked == true && OcrService.IsAvailable)
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
        private static List<GalleryItem> ScanFolder(string folder)
        {
            var result = new List<GalleryItem>();
            if (!Directory.Exists(folder)) return result;

            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (!(path.EndsWith(".png",  StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".jpg",  StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)))
                    continue;

                try
                {
                    var info = new FileInfo(path);
                    result.Add(new GalleryItem
                    {
                        FilePath = path,
                        Bytes    = info.Length,
                        Captured = CaptureTimeOf(info)
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
            var m = Regex.Match(info.Name, @"(\d{8})[_-](\d{6})");
            if (m.Success &&
                DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value,
                    "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                return parsed;

            var written = info.LastWriteTime;
            var created = info.CreationTime;
            return created < written && created.Year > 1980 ? created : written;
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

            GalleryList.ItemsSource = slice;
            GalleryEmpty.Visibility = _viewItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (_viewItems.Count == 0)
            {
                bool searching = !string.IsNullOrWhiteSpace(_search);
                GalleryEmptyTitle.Text = searching ? "Nothing matches" : "No snips yet";
                GalleryEmptyHint.Text  = searching
                    ? $"No file name contains “{_search}”."
                    : "Press your snip hotkey to capture a region.";
            }

            GalleryScroller.ScrollToTop();

            string total = _viewItems.Count == _allItems.Count
                ? $"{_allItems.Count:N0} capture{(_allItems.Count == 1 ? "" : "s")}"
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

            try
            {
                await Task.Run(() =>
                {
                    foreach (var item in items)
                    {
                        if (token.IsCancellationRequested) return;
                        if (item.Thumb != null) continue;

                        BitmapSource? thumb = null;
                        try
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
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
                        Dispatcher.BeginInvoke(new Action(() => item.Thumb = thumb),
                                               DispatcherPriority.Background);
                    }
                }, token);
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

            GalleryDeleteBtn.IsEnabled = n > 0;
            GalleryEditBtn.IsEnabled   = n == 1;   // editing is a single-image operation
            GalleryCopyBtn.IsEnabled   = n == 1;

            GallerySelectionText.Text = n switch
            {
                0 => "",
                1 => "1 selected",
                _ => $"{n} selected  ·  {FormatBytes(SelectedItems().Sum(i => i.Bytes))}"
            };
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
            else if (e.Key == Key.Enter)  { GalleryEdit_Click(sender, e);   e.Handled = true; }
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
                MessageBox.Show(this, OcrService.UnavailableReason, "Advanced Snip",
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
        private void StartIndexing()
        {
            if (_allItems.Count == 0) return;

            var files = _allItems.Select(i => (i.FilePath, i.Captured)).ToList();
            _ = _app.Ocr.IndexAsync(files);
        }

        private void OnOcrProgress(object? sender, OcrIndexProgress p)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (OcrStatusText == null) return;

                if (p.Finished)
                {
                    OcrStatusText.Text = p.Total == 0
                        ? "All captures indexed"
                        : $"Indexed {p.Total:N0} capture{(p.Total == 1 ? "" : "s")}";

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
        private void Gallery_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;

            // Three lines per notch, matching the rest of Windows.
            double step = e.Delta / 120.0 * 3 * 16;
            GalleryScroller.ScrollToVerticalOffset(GalleryScroller.VerticalOffset - step);
            e.Handled = true;
        }

        private void GalleryRefresh_Click(object sender, RoutedEventArgs e)
            => _ = LoadGalleryAsync();

        private void GalleryOpenFolder_Click(object sender, RoutedEventArgs e)
            => OpenPath(_app.Settings.SaveFolder);

        private void GalleryList_DoubleClick(object sender, MouseButtonEventArgs e)
            => GalleryEdit_Click(sender, e);

        private void GalleryEdit_Click(object sender, RoutedEventArgs e)
        {
            if (GalleryList.SelectedItem is not GalleryItem item) return;
            _app.OpenEditorForFile(item.FilePath);
        }

        private void GalleryCopy_Click(object sender, RoutedEventArgs e)
        {
            if (GalleryList.SelectedItem is not GalleryItem item) return;
            try
            {
                var src = new BitmapImage();
                src.BeginInit();
                src.CacheOption = BitmapCacheOption.OnLoad;
                src.UriSource   = new Uri(item.FilePath);
                src.EndInit();
                src.Freeze();
                ClipboardService.SetImage(src);
                GalleryStatus.Text = "Copied " + item.Name;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't copy image:\n" + ex.Message, "Advanced Snip",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

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

        private void GalleryDelete_Click(object sender, RoutedEventArgs e)
        {
            var chosen = SelectedItems();
            if (chosen.Count == 0) return;

            bool recycle = _app.Settings.GalleryUseRecycleBin;
            string what = chosen.Count == 1
                ? chosen[0].Name
                : $"{chosen.Count} captures ({FormatBytes(chosen.Sum(i => i.Bytes))})";

            var answer = MessageBox.Show(this,
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

        private void SnipNowBtn_Click(object sender, RoutedEventArgs e) => _app.DoSnip();
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
                MessageBox.Show(this, "Please choose a save folder.", "Advanced Snip",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try { Directory.CreateDirectory(folder); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "That folder can't be used:\n" + ex.Message, "Advanced Snip",
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
