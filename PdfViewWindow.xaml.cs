using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AdvancedSnip.Services;
using Windows.Data.Pdf;

namespace AdvancedSnip
{
    /// <summary>
    /// Reads a PDF from the gallery: all pages, continuously scrollable.
    ///
    /// **On sharpness.** The renderer produces a bitmap of a given pixel width, and WPF lays
    /// out in device-independent units, so the two have to be reconciled explicitly or the
    /// page gets resampled. The rule here is that a page is rendered at
    /// <c>displayWidthInDips × dpiScale</c> physical pixels and displayed at exactly
    /// <c>displayWidthInDips</c> — one bitmap pixel per physical screen pixel, no resampling.
    /// Getting that wrong is invisible at 100% and soft on every scaled display, which is
    /// precisely the old bug: it rendered at physical pixels and then sized the frame as
    /// though those pixels were DIPs, so on a 125% panel every page was drawn 25% larger
    /// than it had been rendered and blurred to fit.
    ///
    /// **On text.** <c>Windows.Data.Pdf</c> renders pages and exposes no text at all — no
    /// extraction API, no character positions. Reaching a PDF's own text layer means a
    /// parser, a decompressor and a font stack, the line <see cref="PdfPreviewer"/> declines
    /// to cross. So selectable text is read off the rendered page by the OCR engine, reusing
    /// <see cref="OcrLayout"/>. It therefore reads what the page *looks* like rather than
    /// what it contains: near-perfect on a clean digital PDF, fallible on a poor scan, and
    /// genuinely improved by zooming in, because the page is re-rendered larger and re-read.
    /// </summary>
    public partial class PdfViewWindow : Window
    {
        private readonly string _path;
        private PdfDocument? _document;

        private readonly List<PdfPageView> _pages = new();

        /// <summary>The page the reader is on — the one filling most of the viewport.</summary>
        private int _current;

        /// <summary>0, 90, 180 or 270. A view setting; the file is never touched.</summary>
        private int _rotation;

        /// <summary>DIPs per PDF point. 1.0 is actual printed size.</summary>
        private double _zoom = 1.0;
        private ZoomMode _zoomMode = ZoomMode.FitWidth;

        private enum ZoomMode { FitWidth, FitPage, Fixed }

        private static readonly double[] ZoomStops =
            { 0.25, 0.33, 0.5, 0.67, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0, 6.0 };

        private CancellationTokenSource? _workCts;
        private bool _pumping;
        private bool _refreshQueued;

        /// <summary>Bumped when the zoom or rotation changes, so a late render is discarded.</summary>
        private int _generation;

        // ── text selection ────────────────────────────────────────────────────

        private PdfPageView? _selPage;
        private int _selFrom = -1, _selTo = -1;
        private bool _selecting;

        private readonly List<System.Windows.Shapes.Rectangle> _wordBoxes = new();
        private readonly List<int> _boxWordIndex = new();
        private readonly List<int> _visibleWords = new();

        // ── find ──────────────────────────────────────────────────────────────

        private readonly List<(int From, int To)> _matches = new();
        private int _matchIndex = -1;
        private CancellationTokenSource? _searchCts;

        private static readonly Brush WordPlain = Frozen(Brushes.Transparent);
        private static readonly Brush WordSelected =
            Frozen(new SolidColorBrush(Color.FromArgb(0x66, 0x3B, 0x82, 0xF6)));
        private static readonly Brush WordMatch =
            Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xF5, 0x9E, 0x0B)));
        private static readonly Brush WordCurrentMatch =
            Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0xF5, 0x9E, 0x0B)));

        private static Brush Frozen(Brush brush)
        {
            if (brush.CanFreeze && !brush.IsFrozen) brush.Freeze();
            return brush;
        }

        public PdfViewWindow(string path)
        {
            InitializeComponent();

            _path = path;
            FileLabel.Text = System.IO.Path.GetFileName(path);
            Title = "PDF — " + System.IO.Path.GetFileName(path);

            ThemeManager.ApplyToWindow(this);
            BuildZoomBox();

            Loaded += async (_, _) => await OpenAsync();

            SizeChanged += (_, _) =>
            {
                if (_zoomMode != ZoomMode.Fixed) ApplyZoom();
                QueueRefresh();
            };
        }

        // ── opening ───────────────────────────────────────────────────────────

        private async Task OpenAsync()
        {
            _document = await PdfPreviewer.OpenAsync(_path);

            if (_document == null || _document.PageCount == 0)
            {
                string why = PdfPreviewer.LastError is { Length: > 0 } detail ? $"\n\n{detail}" : "";

                StatusText.Text = _document == null
                    ? "This file could not be opened — it may be encrypted, damaged, or not a PDF." + why
                    : "This PDF has no pages.";
                UpdateChrome();
                return;
            }

            // Every page is created up front, sized from the PDF's own dimensions, but
            // nothing is rendered yet. That gives a scrollbar which is correct from the
            // first frame — a viewer that gains height as pages load moves the page out
            // from under whoever is already reading it.
            for (uint i = 0; i < _document.PageCount; i++)
            {
                var size = PdfPreviewer.PageSize(_document, i);
                var view = new PdfPageView((int)i, size?.WidthPt ?? 612, size?.HeightPt ?? 792);

                var captured = view;
                view.Overlay.MouseLeftButtonDown += (_, e) => Overlay_MouseDown(captured, e);
                view.Overlay.MouseMove += (_, e) => Overlay_MouseMove(captured, e);
                view.Overlay.MouseLeftButtonUp += (_, e) => Overlay_MouseUp(captured, e);

                _pages.Add(view);
                PagesHost.Items.Add(view);
            }

            StatusText.Visibility = Visibility.Collapsed;

            ApplyZoom();
            UpdateChrome();
            QueueRefresh();

            SetStatus($"{_pages.Count} page(s) — scroll through them, drag to select text, Ctrl+F to find.");
        }

        // ── zoom ──────────────────────────────────────────────────────────────

        private void BuildZoomBox()
        {
            ZoomBox.ItemsSource = new[]
            {
                "Fit width", "Fit page", "25%", "50%", "75%", "100%",
                "125%", "150%", "200%", "300%", "400%"
            };
            ZoomBox.SelectedIndex = 0;
        }

        private void ZoomBox_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ZoomBox.SelectedItem is not string choice) return;

            if (choice == "Fit width") _zoomMode = ZoomMode.FitWidth;
            else if (choice == "Fit page") _zoomMode = ZoomMode.FitPage;
            else
            {
                _zoomMode = ZoomMode.Fixed;
                if (int.TryParse(choice.TrimEnd('%'), out int percent)) _zoom = percent / 100.0;
            }

            ApplyZoom();
            QueueRefresh();
        }

        /// <summary>
        /// Works out DIPs-per-point for the current mode and applies it to every page.
        ///
        /// A fit mode resolves against the widest page in the document rather than the
        /// current one, so a file mixing portrait and landscape does not grow a horizontal
        /// scrollbar the moment it reaches the landscape page.
        /// </summary>
        private void ApplyZoom()
        {
            if (_pages.Count == 0) return;

            if (_zoomMode != ZoomMode.Fixed)
            {
                bool quarter = _rotation == 90 || _rotation == 270;

                double widest = _pages.Max(p => quarter ? p.HeightPt : p.WidthPt);
                double tallest = _pages.Max(p => quarter ? p.WidthPt : p.HeightPt);

                double availableW = Math.Max(120, PageScroller.ViewportWidth - 56);
                double availableH = Math.Max(120, PageScroller.ViewportHeight - 40);

                _zoom = _zoomMode == ZoomMode.FitWidth
                    ? availableW / widest
                    : Math.Min(availableW / widest, availableH / tallest);
            }

            _zoom = Math.Clamp(_zoom, 0.08, 8.0);
            _generation++;                 // anything in flight was for the previous size

            foreach (var page in _pages) ApplyPageZoom(page);

            ClearWordBoxes();
            UpdateChrome();
        }

        private void ApplyPageZoom(PdfPageView page)
        {
            bool quarter = _rotation == 90 || _rotation == 270;

            page.Width = Math.Max(24, (quarter ? page.HeightPt : page.WidthPt) * _zoom);
            page.Height = Math.Max(24, (quarter ? page.WidthPt : page.HeightPt) * _zoom);

            page.RecomputeTextScale();
        }

        private void Zoom(int direction, Point? mousePosInScrollViewer = null)
        {
            // Steps through the same stops the dropdown offers, so the keyboard, the wheel
            // and the menu cannot disagree about what 150% means.
            double current = _zoom;

            double next = direction > 0
                ? ZoomStops.FirstOrDefault(z => z > current + 0.001, ZoomStops[^1])
                : ZoomStops.LastOrDefault(z => z < current - 0.001, ZoomStops[0]);

            if (Math.Abs(next - current) < 0.0001) return;

            Point anchor = mousePosInScrollViewer ?? new Point(
                PageScroller.ViewportWidth > 0 ? PageScroller.ViewportWidth / 2 : PageScroller.ActualWidth / 2,
                PageScroller.ViewportHeight > 0 ? PageScroller.ViewportHeight / 2 : PageScroller.ActualHeight / 2);

            double contentX = anchor.X + PageScroller.HorizontalOffset;
            double contentY = anchor.Y + PageScroller.VerticalOffset;
            double factor = next / current;

            _zoom = next;
            _zoomMode = ZoomMode.Fixed;
            SyncZoomBox();

            ApplyZoom();

            PageScroller.UpdateLayout();

            double targetX = contentX * factor - anchor.X;
            double targetY = contentY * factor - anchor.Y;

            PageScroller.ScrollToHorizontalOffset(Math.Max(0, targetX));
            PageScroller.ScrollToVerticalOffset(Math.Max(0, targetY));

            QueueRefresh();
        }

        private void SyncZoomBox()
        {
            string label = $"{Math.Round(_zoom * 100)}%";

            if (ZoomBox.ItemsSource is IEnumerable<string> items && items.Contains(label))
            {
                if (!Equals(ZoomBox.SelectedItem, label)) ZoomBox.SelectedItem = label;
            }
            else
            {
                // Off the list: clear the selection rather than leave it claiming a
                // percentage that is not what is on screen.
                ZoomBox.SelectedItem = null;
                ZoomBox.Text = label;
            }
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(+1);

        private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(-1);

        private void PageScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;

            e.Handled = true;                       // otherwise the view scrolls as well
            Point mousePos = e.GetPosition(PageScroller);
            Zoom(e.Delta > 0 ? +1 : -1, mousePos);
        }

        // ── rendering ─────────────────────────────────────────────────────────

        private void PageScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            UpdateCurrentPage();
            QueueRefresh();
        }

        /// <summary>
        /// Asks for a pass over the visible pages, collapsing a burst into one. Scrolling
        /// raises this every frame; rendering per event would queue pictures for positions
        /// the reader has already left.
        /// </summary>
        private void QueueRefresh()
        {
            if (_refreshQueued) return;
            _refreshQueued = true;

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => { _refreshQueued = false; _ = PumpAsync(); }));
        }

        /// <summary>
        /// Renders what is on screen, reads it, and releases what is far away.
        ///
        /// One pass at a time: a second pass started while the first is awaiting a render
        /// would fight it for the same pages.
        /// </summary>
        private async Task PumpAsync()
        {
            if (_pumping || _document == null || _pages.Count == 0) return;

            _pumping = true;
            try
            {
                _workCts?.Cancel();
                _workCts?.Dispose();
                _workCts = new CancellationTokenSource();
                var token = _workCts.Token;

                int generation = _generation;

                double viewTop = PageScroller.VerticalOffset;
                double viewHeight = Math.Max(1, PageScroller.ViewportHeight);
                double viewBottom = viewTop + viewHeight;

                // A screenful either side stays resident, so a steady scroll meets pages
                // that are already drawn rather than a blank frame followed by a pop-in.
                double keepTop = viewTop - viewHeight;
                double keepBottom = viewBottom + viewHeight;

                // Two and a half screenfuls before anything is thrown away, so scrolling
                // back and forth across a boundary does not thrash the same page.
                double dropTop = viewTop - viewHeight * 2.5;
                double dropBottom = viewBottom + viewHeight * 2.5;

                var offsets = PageOffsets();
                double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                if (dpiScale <= 0.1) dpiScale = 1;

                for (int i = 0; i < _pages.Count; i++)
                {
                    if (token.IsCancellationRequested || generation != _generation) return;

                    var page = _pages[i];
                    double top = offsets[i];
                    double bottom = top + page.Height;

                    if (bottom < dropTop || top > dropBottom)
                    {
                        if (page.Frame != null && page != _selPage) page.Release();
                        continue;
                    }

                    if (bottom < keepTop || top > keepBottom) continue;
                    if (page.Rendering) continue;

                    // The whole sharpness rule, in one line: physical pixels, not DIPs.
                    int wanted = (int)Math.Clamp(page.Width * dpiScale, 60, 6000);

                    bool needsRender = page.Frame == null
                        || Math.Abs(page.RenderedWidth - wanted) > wanted * 0.02;

                    if (!needsRender) continue;

                    page.Rendering = true;
                    try
                    {
                        var frame = await PdfPreviewer.RenderAsync(
                            _document, (uint)page.Index, (uint)wanted, token);

                        if (token.IsCancellationRequested || generation != _generation) return;
                        if (frame == null) continue;

                        page.Attach(Rotated(frame, _rotation));
                        page.Layout = null;              // the old words were for the old size

                        if (page == CurrentPage()) ClearWordBoxes();
                    }
                    finally { page.Rendering = false; }

                    // Yield between pages so a long document does not lock the interface
                    // while it catches up with a fast scroll.
                    //
                    // Fully qualified because `Dispatcher` is BOTH a type and an instance
                    // property that every Window inherits, and `Yield` is static on the
                    // type. Unqualified, the property wins name resolution and the compiler
                    // rejects it (CS0176) rather than falling through to the type.
                    await System.Windows.Threading.Dispatcher.Yield(
                        System.Windows.Threading.DispatcherPriority.Background);
                }

                if (generation == _generation) await ReadCurrentAsync(generation);
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                _pumping = false;
                MemoryGuard.RequestTrim();
            }
        }

        /// <summary>
        /// The top of each page within the scroller's content, in DIPs.
        ///
        /// Computed from the sizes rather than measured with TranslatePoint: this runs inside
        /// the scroll handler, and asking the layout system where a hundred elements are on
        /// every scroll frame is an expensive way to learn something the sizes already say.
        /// </summary>
        private double[] PageOffsets()
        {
            var offsets = new double[_pages.Count];
            double y = PagesHost.Margin.Top;

            for (int i = 0; i < _pages.Count; i++)
            {
                offsets[i] = y;
                y += _pages[i].Height + _pages[i].Margin.Bottom;
            }
            return offsets;
        }

        /// <summary>OCRs the page the reader is on, if it has not been read at this size.</summary>
        private async Task ReadCurrentAsync(int generation)
        {
            if (!OcrService.IsAvailable) return;

            var page = CurrentPage();
            if (page?.Frame == null || page.Layout != null || page.Reading) return;

            page.Reading = true;
            try
            {
                SetStatus($"Reading page {page.Index + 1}…");
                var layout = await OcrService.ReadLayoutAsync(page.Frame);

                if (generation != _generation) return;
                page.Layout = layout;

                if (page == CurrentPage())
                {
                    LayoutWordBoxes(page);
                    RebuildMatches();
                    UpdateChrome();

                    SetStatus(layout.Any
                        ? $"Page {page.Index + 1}: {layout.Words.Count:N0} words — drag to select, double-click a line, Ctrl+A for the page"
                        : $"No text found on page {page.Index + 1}.");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (generation == _generation)
                    SetStatus($"The page text could not be read ({ex.GetType().Name}: {ex.Message}).");
            }
            finally { page.Reading = false; }
        }

        private static BitmapSource Rotated(BitmapSource source, int degrees)
        {
            degrees = ((degrees % 360) + 360) % 360;
            if (degrees == 0) return source;

            var rotated = new TransformedBitmap(source, new RotateTransform(degrees));
            rotated.Freeze();
            return rotated;
        }

        // ── current page ──────────────────────────────────────────────────────

        private PdfPageView? CurrentPage()
            => _current >= 0 && _current < _pages.Count ? _pages[_current] : null;

        /// <summary>
        /// Decides which page the reader is on: the one covering most of the viewport.
        ///
        /// "Whichever page the top edge is inside" is the obvious rule and reads badly — a
        /// page whose last sliver is still on screen keeps claiming to be current while the
        /// next one fills the window.
        /// </summary>
        private void UpdateCurrentPage()
        {
            if (_pages.Count == 0) return;

            double top = PageScroller.VerticalOffset;
            double bottom = top + PageScroller.ViewportHeight;

            var offsets = PageOffsets();
            int best = _current;
            double bestCover = -1;

            for (int i = 0; i < _pages.Count; i++)
            {
                double pageTop = offsets[i];
                double pageBottom = pageTop + _pages[i].Height;

                double cover = Math.Min(bottom, pageBottom) - Math.Max(top, pageTop);
                if (cover > bestCover) { bestCover = cover; best = i; }
            }

            if (best == _current) return;
            _current = best;

            // Word boxes and find results belong to the page they were built for.
            ClearWordBoxes();
            _matches.Clear();
            _matchIndex = -1;

            var page = CurrentPage();
            if (page?.Layout != null)
            {
                LayoutWordBoxes(page);
                RebuildMatches();
            }

            UpdateChrome();
        }

        private void ScrollToPage(int index)
        {
            if (_pages.Count == 0) return;
            index = Math.Clamp(index, 0, _pages.Count - 1);

            var offsets = PageOffsets();
            PageScroller.ScrollToVerticalOffset(Math.Max(0, offsets[index] - 8));

            _current = index;
            UpdateChrome();
        }

        // ── chrome ────────────────────────────────────────────────────────────

        private void UpdateChrome()
        {
            bool loaded = _pages.Count > 0;
            var page = CurrentPage();
            bool hasFrame = page?.Frame != null;

            PageCountLabel.Text = loaded ? $"of {_pages.Count}" : "of —";
            if (!PageBox.IsKeyboardFocusWithin)
                PageBox.Text = loaded ? (_current + 1).ToString() : "";
            PageBox.IsEnabled = loaded;

            PrevBtn.IsEnabled = loaded && _current > 0;
            NextBtn.IsEnabled = loaded && _current + 1 < _pages.Count;

            ZoomBox.IsEnabled = loaded;
            ZoomInBtn.IsEnabled = loaded && _zoom < 7.9;
            ZoomOutBtn.IsEnabled = loaded && _zoom > 0.09;
            OpenExternalBtn.IsEnabled = loaded;

            RotateBtn.IsEnabled = loaded;
            CopyImageBtn.IsEnabled = hasFrame;
            SavePageBtn.IsEnabled = hasFrame;
            FindBtn.IsEnabled = loaded;
            CopyTextBtn.IsEnabled = page?.Layout is { Any: true };
        }

        private void SetStatus(string text) => ViewerStatus.Text = text;

        // ── word boxes and selection ──────────────────────────────────────────

        private void ClearWordBoxes()
        {
            foreach (var page in _pages)
                if (page.Overlay.Children.Count > 0) page.Overlay.Children.Clear();

            _wordBoxes.Clear();
            _boxWordIndex.Clear();
        }

        /// <summary>
        /// Builds a rectangle per word on one page.
        ///
        /// Scoped to a single page rather than the document: a hundred-page file holds far
        /// too many words to give each one a framework element, and only the current page
        /// can be selected or searched anyway.
        /// </summary>
        private void LayoutWordBoxes(PdfPageView page)
        {
            ClearWordBoxes();
            if (page.Layout is not { Any: true } layout) return;

            double scale = page.TextScale <= 0 ? 1 : page.TextScale;

            var band = new Rect(0, 0,
                Math.Max(1, page.Width / scale), Math.Max(1, page.Height / scale));

            layout.WordsIn(band, _visibleWords);

            foreach (int i in _visibleWords)
            {
                var box = layout.Words[i].Box;
                var r = new System.Windows.Shapes.Rectangle
                {
                    Width = Math.Max(1, box.Width * scale),
                    Height = Math.Max(1, box.Height * scale),
                    Fill = WordPlain,
                    RadiusX = 2,
                    RadiusY = 2,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(r, box.X * scale);
                Canvas.SetTop(r, box.Y * scale);

                page.Overlay.Children.Add(r);
                _wordBoxes.Add(r);
                _boxWordIndex.Add(i);
            }

            PaintSelection();
        }

        private void PaintSelection()
        {
            int lo = Math.Min(_selFrom, _selTo), hi = Math.Max(_selFrom, _selTo);
            bool onSelPage = _selPage != null && _selPage == CurrentPage();

            for (int i = 0; i < _wordBoxes.Count; i++)
            {
                int word = _boxWordIndex[i];
                Brush want = WordPlain;

                if (onSelPage && _selFrom >= 0 && word >= lo && word <= hi) want = WordSelected;
                else if (MatchAt(word) is int m)
                    want = m == _matchIndex ? WordCurrentMatch : WordMatch;

                if (!ReferenceEquals(_wordBoxes[i].Fill, want)) _wordBoxes[i].Fill = want;
            }
        }

        private int? MatchAt(int word)
        {
            for (int m = 0; m < _matches.Count; m++)
                if (word >= _matches[m].From && word <= _matches[m].To) return m;
            return null;
        }

        private static int WordAt(PdfPageView page, Point local)
        {
            if (page.Layout is not { Any: true } layout) return -1;

            double scale = page.TextScale <= 0 ? 1 : page.TextScale;
            return layout.NearestWord(new Point(local.X / scale, local.Y / scale));
        }

        private void Overlay_MouseDown(PdfPageView page, MouseButtonEventArgs e)
        {
            if (page.Layout is not { Any: true }) return;

            if (e.ClickCount == 2) { SelectLineAt(page, e.GetPosition(page.Overlay)); return; }

            int i = WordAt(page, e.GetPosition(page.Overlay));
            if (i < 0) return;

            _selPage = page;
            _selecting = true;
            _selFrom = _selTo = i;
            page.Overlay.CaptureMouse();
            PaintSelection();
        }

        private void Overlay_MouseMove(PdfPageView page, MouseEventArgs e)
        {
            // A drag that wanders onto another page stays on the page it began on. Selecting
            // across pages would need two independently-produced OCR results stitched into
            // one word order, and they are read out of order and at different times.
            if (!_selecting || page != _selPage) return;

            int i = WordAt(page, e.GetPosition(page.Overlay));
            if (i < 0 || i == _selTo) return;

            _selTo = i;
            PaintSelection();
        }

        private void Overlay_MouseUp(PdfPageView page, MouseButtonEventArgs e)
        {
            if (!_selecting) return;

            _selecting = false;
            page.Overlay.ReleaseMouseCapture();
            CopySelection();
        }

        private void SelectLineAt(PdfPageView page, Point local)
        {
            if (page.Layout is not { Any: true } layout) return;

            int i = WordAt(page, local);
            if (i < 0) return;

            int line = layout.Words[i].Line;
            int from = i, to = i;
            while (from > 0 && layout.Words[from - 1].Line == line) from--;
            while (to < layout.Words.Count - 1 && layout.Words[to + 1].Line == line) to++;

            _selPage = page;
            _selFrom = from;
            _selTo = to;
            PaintSelection();
            CopySelection();
        }

        private string SelectedText()
            => _selPage?.Layout == null || _selFrom < 0
                ? ""
                : _selPage.Layout.Join(_selFrom, _selTo).Trim();

        private void CopySelection()
        {
            string text = SelectedText();
            if (text.Length == 0) return;

            TrySetClipboardText(text);

            string preview = text.Replace('\n', ' ');
            if (preview.Length > 60) preview = preview[..60] + "…";
            SetStatus($"Copied “{preview}”");
        }

        private void CopyText_Click(object sender, RoutedEventArgs e)
        {
            var page = CurrentPage();
            if (page?.Layout is not { Any: true } layout) return;

            string text = SelectedText();
            bool whole = text.Length == 0;

            if (whole) text = layout.Join(0, layout.Words.Count - 1).Trim();
            if (text.Length == 0) return;

            TrySetClipboardText(text);
            SetStatus(whole
                ? $"Copied page {page.Index + 1} — {text.Length:N0} characters"
                : $"Copied {text.Length:N0} characters");
        }

        private void SelectAllWords()
        {
            var page = CurrentPage();
            if (page?.Layout is not { Any: true } layout) return;

            _selPage = page;
            _selFrom = 0;
            _selTo = layout.Words.Count - 1;
            PaintSelection();
        }

        private static void TrySetClipboardText(string text)
        {
            // The clipboard is shared and single-owner; another app can hold it briefly.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { System.Windows.Clipboard.SetText(text); return; }
                catch { Thread.Sleep(60); }
            }
        }

        // ── find ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Whitespace- and case-insensitive comparison text.
        ///
        /// The OCR engine splits on more than spaces — brackets, currency symbols and
        /// punctuation frequently come back as their own words, so <c>$1,240.00</c> is
        /// routinely four of them. Searching the joined text would miss it nearly every time.
        /// </summary>
        private static string Squash(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (!char.IsWhiteSpace(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private void RebuildMatches()
        {
            _matches.Clear();
            _matchIndex = -1;

            string needle = Squash(FindBox.Text);
            var page = CurrentPage();

            if (page?.Layout is not { Any: true } layout || needle.Length == 0)
            {
                PaintSelection();
                UpdateFindChrome();
                return;
            }

            var words = layout.Words;
            var sb = new StringBuilder();

            for (int i = 0; i < words.Count; i++)
            {
                sb.Clear();
                int firstLength = Squash(words[i].Text).Length;

                int limit = Math.Min(words.Count - 1, i + 11);

                for (int j = i; j <= limit; j++)
                {
                    sb.Append(Squash(words[j].Text));

                    int at = sb.ToString().IndexOf(needle, StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        // Only if it *starts* inside word i. Without that test, searching
                        // "world" over "hello|world" also reports a match beginning at
                        // "hello", because the joined run contains it — so every hit would
                        // be reported once per preceding word.
                        if (at < firstLength) _matches.Add((i, j));
                        break;
                    }

                    // Stop once the needle could no longer start inside word i. Testing only
                    // at the first j where the run is long enough — the obvious way to write
                    // this — misses every match straddling a word boundary when the first
                    // word is already longer than the query.
                    if (sb.Length >= firstLength + needle.Length - 1) break;
                }
            }

            if (_matches.Count > 0) _matchIndex = 0;
            ShowMatch(scroll: false);
        }

        private void ShowMatch(bool scroll)
        {
            var page = CurrentPage();

            if (_matchIndex >= 0 && _matchIndex < _matches.Count && page != null)
            {
                var (from, to) = _matches[_matchIndex];
                _selPage = page;
                _selFrom = from;
                _selTo = to;

                if (scroll) ScrollWordIntoView(page, from);
            }

            PaintSelection();
            UpdateFindChrome();
        }

        private void ScrollWordIntoView(PdfPageView page, int word)
        {
            if (page.Layout is not { } layout || word < 0 || word >= layout.Words.Count) return;
            if (PageScroller.ViewportHeight <= 0) return;

            double scale = page.TextScale <= 0 ? 1 : page.TextScale;
            var box = layout.Words[word].Box;

            var offsets = PageOffsets();
            double target = offsets[page.Index] + box.Y * scale - PageScroller.ViewportHeight / 3;

            PageScroller.ScrollToVerticalOffset(Math.Max(0, target));

            if (PageScroller.ScrollableWidth > 0)
                PageScroller.ScrollToHorizontalOffset(
                    Math.Max(0, box.X * scale - PageScroller.ViewportWidth / 3));
        }

        private void UpdateFindChrome()
        {
            bool has = _matches.Count > 0;

            MatchLabel.Text = FindBox.Text.Trim().Length == 0
                ? ""
                : has ? $"{_matchIndex + 1} of {_matches.Count} on page {_current + 1}"
                      : $"not on page {_current + 1}";

            FindPrevBtn.IsEnabled = has;
            FindNextBtn.IsEnabled = has;
            FindAllPagesBtn.IsEnabled = _document != null && FindBox.Text.Trim().Length > 0;
        }

        private void StepMatch(int delta)
        {
            if (_matches.Count == 0) return;
            _matchIndex = (_matchIndex + delta + _matches.Count) % _matches.Count;
            ShowMatch(scroll: true);
        }

        private void Find_Click(object sender, RoutedEventArgs e) => OpenFind();

        private void OpenFind()
        {
            FindBar.Visibility = Visibility.Visible;
            FindBox.Focus();
            FindBox.SelectAll();
            UpdateFindChrome();
        }

        private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind();

        private void CloseFind()
        {
            CancelSearch();
            FindBar.Visibility = Visibility.Collapsed;
            _matches.Clear();
            _matchIndex = -1;
            PaintSelection();
        }

        private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildMatches();

        private void FindBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (_matches.Count > 0) StepMatch(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : +1);
                else _ = SearchOtherPagesAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseFind();
            }
        }

        private void FindNext_Click(object sender, RoutedEventArgs e) => StepMatch(+1);

        private void FindPrev_Click(object sender, RoutedEventArgs e) => StepMatch(-1);

        private void FindAllPages_Click(object sender, RoutedEventArgs e) => _ = SearchOtherPagesAsync();

        private void CancelSearch()
        {
            try { _searchCts?.Cancel(); } catch { }
        }

        /// <summary>
        /// Walks the rest of the document and stops on the first page containing the query.
        ///
        /// Every page has to be rendered and read to be searched, so this is genuinely slow
        /// on a long document and says so while it works. It renders at a fixed modest width
        /// rather than the current zoom: this is a search, not a view, and a smaller page is
        /// several times quicker to read.
        /// </summary>
        private async Task SearchOtherPagesAsync()
        {
            if (_document == null || _pages.Count <= 1) return;

            string needle = Squash(FindBox.Text);
            if (needle.Length == 0) return;

            if (!OcrService.IsAvailable)
            {
                SetStatus("Searching other pages needs the Windows OCR engine — "
                          + OcrService.UnavailableReason);
                return;
            }

            CancelSearch();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            FindAllPagesBtn.IsEnabled = false;
            int from = _current;

            try
            {
                for (int step = 1; step < _pages.Count; step++)
                {
                    token.ThrowIfCancellationRequested();

                    int index = (from + step) % _pages.Count;
                    SetStatus($"Searching page {index + 1} of {_pages.Count} — Esc to stop");

                    var frame = await PdfPreviewer.RenderAsync(_document, (uint)index, 1400, token);
                    if (frame == null) continue;

                    var layout = await OcrService.ReadLayoutAsync(frame, token);
                    if (!layout.Any) continue;

                    if (!Squash(layout.Join(0, layout.Words.Count - 1))
                            .Contains(needle, StringComparison.Ordinal))
                        continue;

                    ScrollToPage(index);
                    QueueRefresh();
                    SetStatus($"Found on page {index + 1}");
                    return;
                }

                SetStatus($"“{FindBox.Text.Trim()}” is not on the other pages.");
            }
            catch (OperationCanceledException) { SetStatus("Search stopped."); }
            catch (Exception ex) { SetStatus($"Search failed ({ex.GetType().Name}: {ex.Message})."); }
            finally { UpdateFindChrome(); }
        }

        // ── page actions ──────────────────────────────────────────────────────

        private void CopyImage_Click(object sender, RoutedEventArgs e)
        {
            var page = CurrentPage();
            if (page?.Frame == null) return;

            // Through App so it lands in the clipboard history as well, like every other
            // copy in the app since v2.33.
            var outcome = (System.Windows.Application.Current as App)?.CopyImage(page.Frame, null);
            SetStatus(outcome?.Warning ?? $"Copied page {page.Index + 1} as an image");
        }

        private void SavePage_Click(object sender, RoutedEventArgs e)
        {
            var page = CurrentPage();
            if (page?.Frame == null) return;

            var app = System.Windows.Application.Current as App;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG image|*.png|JPEG image|*.jpg",
                AddExtension = true,
                DefaultExt = "png",
                FileName = System.IO.Path.GetFileNameWithoutExtension(_path) + $"_p{page.Index + 1}",
                InitialDirectory = Directory.Exists(app?.Settings.SaveFolder)
                    ? app!.Settings.SaveFolder
                    : null
            };

            if (dlg.ShowDialog(this) != true) return;

            try
            {
                // Through a temp file and moved into place, so a failure part-way through
                // cannot leave a half-written image where the person asked for one.
                string temp = dlg.FileName + ".tmp";
                bool jpeg = System.IO.Path.GetExtension(dlg.FileName)
                                          .Equals(".jpg", StringComparison.OrdinalIgnoreCase);

                BitmapEncoder encoder = jpeg
                    ? new JpegBitmapEncoder { QualityLevel = 92 }
                    : new PngBitmapEncoder();

                encoder.Frames.Add(BitmapFrame.Create(page.Frame));

                using (var stream = File.Create(temp)) encoder.Save(stream);

                File.Move(temp, dlg.FileName, overwrite: true);
                SetStatus("Saved " + System.IO.Path.GetFileName(dlg.FileName));
                app?.NotifySnipCompleted();
            }
            catch (Exception ex)
            {
                SetStatus($"Could not save that ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        private void Rotate_Click(object sender, RoutedEventArgs e)
        {
            _rotation = (_rotation + 90) % 360;

            // Every resident bitmap was rotated the old way, so they all have to go.
            foreach (var page in _pages) page.Release();

            _selPage = null;
            _selFrom = _selTo = -1;

            ApplyZoom();
            QueueRefresh();
        }

        // ── navigation ────────────────────────────────────────────────────────

        private void Prev_Click(object sender, RoutedEventArgs e) => ScrollToPage(_current - 1);

        private void Next_Click(object sender, RoutedEventArgs e) => ScrollToPage(_current + 1);

        private void PageBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            e.Handled = true;
            CommitPageBox();
            PageScroller.Focus();
        }

        private void PageBox_LostFocus(object sender, RoutedEventArgs e) => CommitPageBox();

        private void CommitPageBox()
        {
            if (int.TryParse(PageBox.Text.Trim(), out int wanted)) ScrollToPage(wanted - 1);
            PageBox.Text = _pages.Count > 0 ? (_current + 1).ToString() : "";
        }

        private void OpenExternal_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true }); }
            catch { /* no PDF handler registered; nothing useful to say about it */ }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            bool typing = Keyboard.FocusedElement is TextBox;

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                switch (e.Key)
                {
                    case Key.C: e.Handled = true; CopyText_Click(sender, e); return;
                    case Key.A when !typing: e.Handled = true; SelectAllWords(); return;
                    case Key.F: e.Handled = true; OpenFind(); return;
                    case Key.OemPlus or Key.Add: e.Handled = true; Zoom(+1); return;
                    case Key.OemMinus or Key.Subtract: e.Handled = true; Zoom(-1); return;
                    case Key.D0 or Key.NumPad0:
                        e.Handled = true;
                        ZoomBox.SelectedItem = "100%";
                        return;
                }
            }

            switch (e.Key)
            {
                case Key.F3:
                    e.Handled = true;
                    StepMatch(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : +1);
                    return;

                case Key.R when !typing:
                    e.Handled = true;
                    Rotate_Click(sender, e);
                    return;

                case Key.Escape:
                    e.Handled = true;
                    if (FindBar.Visibility == Visibility.Visible) CloseFind();
                    else { CancelSearch(); Close(); }
                    return;
            }

            // Page navigation, but not while a number or a search term is being typed.
            if (typing) return;

            switch (e.Key)
            {
                case Key.PageUp: e.Handled = true; ScrollToPage(_current - 1); break;
                case Key.PageDown: e.Handled = true; ScrollToPage(_current + 1); break;
                case Key.Home: e.Handled = true; ScrollToPage(0); break;
                case Key.End: e.Handled = true; ScrollToPage(_pages.Count - 1); break;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            _workCts?.Cancel();
            _workCts?.Dispose();
            _workCts = null;

            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;

            foreach (var page in _pages) page.Release();
            _pages.Clear();
            PagesHost.Items.Clear();

            _document = null;
            MemoryGuard.RequestTrim();
        }
    }
}
