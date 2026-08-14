using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    /// <summary>Represents a single image item in multi-image PDF export.</summary>
    public sealed class PdfExportItem
    {
        public required BitmapSource Image { get; set; }
        public string? FilePath { get; set; }
        public required string Name { get; set; }
        public BitmapSource? Thumb { get; set; }
        public string IndexLabel { get; set; } = "";
    }

    /// <summary>
    /// Asks how a picture (or collection of pictures) should be turned into a PDF,
    /// and says what that will produce before doing it.
    /// </summary>
    public partial class PdfExportWindow : Window
    {
        /// <summary>Room the preview has to draw a page in, in device-independent pixels.</summary>
        private const double PreviewBoxWidth = 290;
        private const double PreviewBoxHeight = 392;

        private readonly List<PdfExportItem> _items = new();
        private readonly bool _isMultiImage;
        private readonly AppSettings _settings;
        private readonly string? _sourcePath;
        private bool _ready;

        private PdfLayout? _layout;
        private int _page;

        private System.Windows.Threading.DispatcherTimer _previewDelay = null!;

        /// <summary>Where the PDF ended up, or null if the user backed out.</summary>
        public string? SavedPath { get; private set; }

        /// <summary>Single-image constructor (e.g. from editor or single capture).</summary>
        public PdfExportWindow(BitmapSource image, string? sourcePath, AppSettings settings)
        {
            InitializeComponent();

            _settings = settings;
            _sourcePath = sourcePath;
            _isMultiImage = false;

            _items.Add(new PdfExportItem
            {
                Image = image,
                FilePath = sourcePath,
                Name = sourcePath != null ? Path.GetFileName(sourcePath) : "Current image",
                Thumb = ImageInterop.MakeThumbnail(image, 64),
                IndexLabel = "1"
            });

            CommonInit();

            ImageInfo.Text = $"{image.PixelWidth} × {image.PixelHeight} pixels";
            _ready = true;
            UpdateSummary();
        }

        /// <summary>Multi-image constructor (from gallery selection).</summary>
        public PdfExportWindow(IReadOnlyList<string> imagePaths, AppSettings settings)
        {
            InitializeComponent();

            _settings = settings;
            _isMultiImage = true;

            int index = 1;
            foreach (var path in imagePaths)
            {
                try
                {
                    var src = new BitmapImage();
                    src.BeginInit();
                    src.CacheOption = BitmapCacheOption.OnLoad;
                    src.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    src.UriSource = new Uri(path);
                    src.EndInit();
                    src.Freeze();

                    _items.Add(new PdfExportItem
                    {
                        Image = src,
                        FilePath = path,
                        Name = Path.GetFileName(path),
                        Thumb = ImageInterop.MakeThumbnail(src, 64),
                        IndexLabel = $"{index}"
                    });
                    index++;
                }
                catch { }
            }

            CommonInit();

            if (_items.Count > 0)
            {
                ArrangeBorder.Visibility = Visibility.Visible;
                RefreshArrangeList();

                // Multi-image layout modes:
                ModeCombo.Items.Clear();
                ModeCombo.Items.Add(new ComboBoxItem { Content = "Fit each image to page (centered with margins)", IsSelected = true });
                ModeCombo.Items.Add(new ComboBoxItem { Content = "Original size (centered on page)" });
                ModeCombo.Items.Add(new ComboBoxItem { Content = "Stretch to fill printable area" });

                ImageInfo.Text = $"{_items.Count} images selected";
            }

            _ready = true;
            UpdateSummary();
        }

        private void CommonInit()
        {
            foreach (var paper in PaperSize.All) PaperCombo.Items.Add(paper);
            PaperCombo.SelectedIndex = 0;

            ThemeManager.ApplyToWindow(this);

            _previewDelay = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(70)
            };
            _previewDelay.Tick += (_, _) => { _previewDelay.Stop(); DrawPreview(); };
        }

        private void RefreshArrangeList()
        {
            for (int i = 0; i < _items.Count; i++)
                _items[i].IndexLabel = $"{i + 1}";

            ArrangeList.ItemsSource = null;
            ArrangeList.ItemsSource = _items;
            if (_page >= 0 && _page < _items.Count)
                ArrangeList.SelectedIndex = _page;

            ArrangeCountLabel.Text = $"{_items.Count} page{(_items.Count == 1 ? "" : "s")}";
            ImageInfo.Text = $"{_items.Count} image{(_items.Count == 1 ? "" : "s")} selected";
        }

        // ── Arrangement List handlers ─────────────────────────────────────────

        private void ArrangeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ArrangeList.SelectedIndex >= 0 && ArrangeList.SelectedIndex < _items.Count)
            {
                _page = ArrangeList.SelectedIndex;
                DrawPreview();
            }
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            int sel = ArrangeList.SelectedIndex;
            if (sel <= 0 || sel >= _items.Count) return;

            var item = _items[sel];
            _items.RemoveAt(sel);
            _items.Insert(sel - 1, item);
            _page = sel - 1;
            RefreshArrangeList();
            DrawPreview();
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            int sel = ArrangeList.SelectedIndex;
            if (sel < 0 || sel >= _items.Count - 1) return;

            var item = _items[sel];
            _items.RemoveAt(sel);
            _items.Insert(sel + 1, item);
            _page = sel + 1;
            RefreshArrangeList();
            DrawPreview();
        }

        private void RemovePage_Click(object sender, RoutedEventArgs e)
        {
            if (_items.Count <= 1) return;
            int sel = ArrangeList.SelectedIndex;
            if (sel < 0 || sel >= _items.Count) sel = _page;

            _items.RemoveAt(sel);
            _page = Math.Clamp(sel, 0, _items.Count - 1);
            RefreshArrangeList();
            DrawPreview();
            UpdateSummary();
        }

        // ── options ───────────────────────────────────────────────────────────

        private PdfExportOptions BuildOptions() => new()
        {
            Paper = PaperCombo.SelectedItem as PaperSize ?? PaperSize.All[0],
            Landscape = LandscapeChk.IsChecked == true,
            Mode = _isMultiImage
                ? PdfPageMode.FitOnePage
                : ModeCombo.SelectedIndex switch
                {
                    1 => PdfPageMode.SinglePage,
                    2 => PdfPageMode.FitOnePage,
                    _ => PdfPageMode.SlicePages
                },
            HorizontalAlign = AlignOf(HAlignCombo),
            VerticalAlign = AlignOf(VAlignCombo),
            MarginMm = TagOf(MarginCombo, 10),
            OverlapPx = TagOf(OverlapCombo, 24),
            ZoomPercent = ZoomSliderCtl.Value,
            UseJpeg = QualityCombo.SelectedIndex == 1,
            JpegQuality = TagOf(JpegCombo, 88)
        };

        private static string Describe(PdfAlign horizontal, PdfAlign vertical)
        {
            string down = vertical switch
            {
                PdfAlign.Start => "at the top",
                PdfAlign.End   => "at the bottom",
                _              => "in the middle"
            };

            string across = horizontal switch
            {
                PdfAlign.Start => "to the left",
                PdfAlign.End   => "to the right",
                _              => "centred"
            };

            return horizontal == PdfAlign.Centre && vertical == PdfAlign.Centre
                ? "in the centre of the page"
                : $"{down}, {across}";
        }

        private static PdfAlign AlignOf(ComboBox combo) => combo.SelectedIndex switch
        {
            0 => PdfAlign.Start,
            2 => PdfAlign.End,
            _ => PdfAlign.Centre
        };

        private static int TagOf(ComboBox combo, int fallback)
        {
            if (combo.SelectedItem is ComboBoxItem item &&
                item.Tag is string tag &&
                int.TryParse(tag, out int value))
                return value;
            return fallback;
        }

        private void Combo_Changed(object sender, SelectionChangedEventArgs e) => UpdateSummary();

        private void Check_Changed(object sender, RoutedEventArgs e) => UpdateSummary();

        private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
            => UpdateSummary();

        private void ZoomReset_Click(object sender, RoutedEventArgs e)
            => ZoomSliderCtl.Value = 100;

        private void PrevPage_Click(object sender, RoutedEventArgs e) => StepPage(-1);

        private void NextPage_Click(object sender, RoutedEventArgs e) => StepPage(+1);

        private void StepPage(int delta)
        {
            int maxPage = _isMultiImage ? _items.Count : (_layout?.PageCount ?? 0);
            if (maxPage == 0) return;
            _page = Math.Clamp(_page + delta, 0, maxPage - 1);
            if (_isMultiImage && ArrangeList.SelectedIndex != _page)
                ArrangeList.SelectedIndex = _page;
            DrawPreview();
        }

        private void UpdateSummary()
        {
            if (!_ready) return;

            var o = BuildOptions();

            if (_isMultiImage)
            {
                OverlapRow.Visibility = Visibility.Collapsed;
                ZoomRow.Visibility = Visibility.Collapsed;
                AlignRow.Visibility = Visibility.Visible;
                JpegRow.Visibility = o.UseJpeg ? Visibility.Visible : Visibility.Collapsed;
                PaperCombo.IsEnabled = true;
                LandscapeChk.IsEnabled = true;
                VAlignCombo.IsEnabled = true;

                _page = Math.Clamp(_page, 0, Math.Max(0, _items.Count - 1));
                _previewDelay.Stop();
                _previewDelay.Start();

                string orientation = o.Landscape ? "landscape" : "portrait";
                SummaryTitle.Text = $"{_items.Count} page{(_items.Count == 1 ? "" : "s")} on {o.Paper.Name} {orientation}";
                SummaryDetail.Text = $"Each image is centered on its own page with {o.MarginMm} mm margins, placed {Describe(o.HorizontalAlign, o.VerticalAlign)}.";
                return;
            }

            // Single-image mode
            var currentImg = _items[0].Image;
            bool slicing = o.Mode == PdfPageMode.SlicePages;
            bool fitting = o.Mode == PdfPageMode.FitOnePage;

            OverlapRow.Visibility = slicing ? Visibility.Visible : Visibility.Collapsed;
            ZoomRow.Visibility = slicing || fitting ? Visibility.Visible : Visibility.Collapsed;
            AlignRow.Visibility = slicing || fitting ? Visibility.Visible : Visibility.Collapsed;
            JpegRow.Visibility = o.UseJpeg ? Visibility.Visible : Visibility.Collapsed;
            PaperCombo.IsEnabled = slicing || fitting;
            LandscapeChk.IsEnabled = slicing || fitting;
            VAlignCombo.IsEnabled = fitting;

            ZoomText.Text = $"{o.ZoomPercent:0}%";

            var layout = PdfExporter.PlanLayout(currentImg.PixelWidth, currentImg.PixelHeight, o);
            _layout = layout;
            _page = Math.Clamp(_page, 0, Math.Max(0, layout.PageCount - 1));
            _previewDelay.Stop();
            _previewDelay.Start();

            string singleOrient = o.Landscape ? "landscape" : "portrait";

            if (fitting)
            {
                double coverage = layout.Scale * currentImg.PixelWidth
                                / Math.Max(1, o.ContentWidthPt) * 100;

                SummaryTitle.Text = $"1 page on {o.Paper.Name} {singleOrient}";
                SummaryDetail.Text =
                    $"The whole image is scaled to fit, filling about {Math.Min(100, coverage):0}% of the " +
                    $"printable width, and placed {Describe(o.HorizontalAlign, o.VerticalAlign)}. " +
                    "Nothing is cut.";
                return;
            }

            if (!slicing)
            {
                double wMm = currentImg.PixelWidth / 96.0 * 25.4;
                double hMm = currentImg.PixelHeight / 96.0 * 25.4;
                SummaryTitle.Text = "1 page, custom size";
                SummaryDetail.Text =
                    $"The page will be {wMm:0} × {hMm:0} mm — the whole image at its natural size, " +
                    "nothing cut. Fine for reading on screen; most printers will shrink it to fit.";
                return;
            }

            if (layout.PageCount <= 1)
            {
                SummaryTitle.Text = $"1 page on {o.Paper.Name} {singleOrient}";
                SummaryDetail.Text = "The whole image fits on a single page at this size.";
                return;
            }

            string grid = layout.Columns > 1
                ? $"{layout.Columns} columns × {layout.Rows} rows"
                : $"{layout.PageCount} pages";

            SummaryTitle.Text = $"{grid} on {o.Paper.Name} {singleOrient}";

            string sizing = Math.Abs(o.ZoomPercent - 100) < 0.5
                ? $"The image is scaled to fit the {o.Paper.Name} width"
                : $"At {o.ZoomPercent:0}% the image is drawn " +
                  (o.ZoomPercent > 100 ? "larger" : "smaller") +
                  $" than the {o.Paper.Name} width";

            string cutting = layout.Columns > 1
                ? $", then cut into {layout.ColsPerPage:N0} × {layout.RowsPerPage:N0} pixel tiles."
                : $", then cut every {layout.StepPx:N0} pixels.";

            SummaryDetail.Text = sizing + cutting + " " +
                (o.OverlapPx > 0
                    ? $"{o.OverlapPx} pixels repeat across each break."
                    : "With no overlap, a line of text landing on a break will be split.");
        }

        // ── preview ───────────────────────────────────────────────────────────

        private void DrawPreview()
        {
            if (_items.Count == 0)
            {
                ShowEmptyPreview();
                return;
            }

            var o = BuildOptions();

            if (_isMultiImage)
            {
                _page = Math.Clamp(_page, 0, _items.Count - 1);
                var cur = _items[_page];
                var img = cur.Image;

                double pageWPt = o.PageWidthPt;
                double pageHPt = o.PageHeightPt;

                double fit = Math.Min(PreviewBoxWidth / pageWPt, PreviewBoxHeight / pageHPt);
                double paperW = Math.Max(8, pageWPt * fit);
                double paperH = Math.Max(8, pageHPt * fit);

                PageShadow.Visibility = Visibility.Visible;
                PreviewEmpty.Visibility = Visibility.Collapsed;
                PageShadow.Width = paperW;
                PageShadow.Height = paperH;
                PageCanvas.Width = paperW;
                PageCanvas.Height = paperH;

                double marginPx = o.MarginPt * fit;
                Canvas.SetLeft(MarginGuide, marginPx);
                Canvas.SetTop(MarginGuide, marginPx);
                MarginGuide.Width = Math.Max(0, paperW - 2 * marginPx);
                MarginGuide.Height = Math.Max(0, paperH - 2 * marginPx);
                MarginGuide.Visibility = o.MarginMm > 0 ? Visibility.Visible : Visibility.Collapsed;

                int multiMode = ModeCombo.SelectedIndex;
                double contentWPt = o.ContentWidthPt;
                double contentHPt = o.ContentHeightPt;

                double drawWPt, drawHPt;
                if (multiMode == 1) // Original size (72 DPI)
                {
                    drawWPt = Math.Min(contentWPt, img.PixelWidth * 72.0 / 96.0);
                    drawHPt = Math.Min(contentHPt, img.PixelHeight * 72.0 / 96.0);
                }
                else if (multiMode == 2) // Stretch
                {
                    drawWPt = contentWPt;
                    drawHPt = contentHPt;
                }
                else // Fit to page
                {
                    double imgScale = Math.Min(contentWPt / img.PixelWidth, contentHPt / img.PixelHeight);
                    drawWPt = img.PixelWidth * imgScale;
                    drawHPt = img.PixelHeight * imgScale;
                }

                double alignX = o.HorizontalAlign switch
                {
                    PdfAlign.Start => 0.0,
                    PdfAlign.End => 1.0,
                    _ => 0.5
                };
                double alignY = o.VerticalAlign switch
                {
                    PdfAlign.Start => 0.0, // Top in UI
                    PdfAlign.End => 1.0,   // Bottom in UI
                    _ => 0.5
                };

                double drawXPt = o.MarginPt + (contentWPt - drawWPt) * alignX;
                double drawYTopPt = o.MarginPt + (contentHPt - drawHPt) * alignY;

                SliceImage.Source = img;
                Canvas.SetLeft(SliceImage, drawXPt * fit);
                Canvas.SetTop(SliceImage, drawYTopPt * fit);
                SliceImage.Width = Math.Max(1, drawWPt * fit);
                SliceImage.Height = Math.Max(1, drawHPt * fit);

                PageLabel.Text = $"Page {_page + 1} of {_items.Count}  ·  {cur.Name}";
                PrevPageBtn.IsEnabled = _page > 0;
                NextPageBtn.IsEnabled = _page < _items.Count - 1;

                PageDetail.Text = $"{img.PixelWidth} × {img.PixelHeight} pixels  ·  {o.Paper.Name} ({(o.Landscape ? "Landscape" : "Portrait")})";
                return;
            }

            // Single image preview
            var layout = _layout;
            if (layout == null || layout.PageCount == 0)
            {
                ShowEmptyPreview();
                return;
            }

            var singleImg = _items[0].Image;
            var plan = PdfExporter.PlanPage(singleImg.PixelWidth, singleImg.PixelHeight,
                                            o, layout, _page);
            if (plan == null || plan.PageWidthPt <= 0 || plan.PageHeightPt <= 0)
            {
                ShowEmptyPreview();
                return;
            }

            double singleFit = Math.Min(PreviewBoxWidth / plan.PageWidthPt,
                                        PreviewBoxHeight / plan.PageHeightPt);

            double sPaperW = Math.Max(8, plan.PageWidthPt * singleFit);
            double sPaperH = Math.Max(8, plan.PageHeightPt * singleFit);

            PageShadow.Visibility = Visibility.Visible;
            PreviewEmpty.Visibility = Visibility.Collapsed;
            PageShadow.Width = sPaperW;
            PageShadow.Height = sPaperH;
            PageCanvas.Width = sPaperW;
            PageCanvas.Height = sPaperH;

            double sMarginPx = o.MarginPt * singleFit;
            Canvas.SetLeft(MarginGuide, sMarginPx);
            Canvas.SetTop(MarginGuide, sMarginPx);
            MarginGuide.Width = Math.Max(0, sPaperW - 2 * sMarginPx);
            MarginGuide.Height = Math.Max(0, sPaperH - 2 * sMarginPx);
            MarginGuide.Visibility = o.MarginMm > 0 ? Visibility.Visible : Visibility.Collapsed;

            double top = plan.PageHeightPt - plan.DrawYPt - plan.DrawHeightPt;

            try
            {
                var crop = new CroppedBitmap(singleImg, plan.Source);
                crop.Freeze();
                SliceImage.Source = crop;
            }
            catch
            {
                SliceImage.Source = null;
            }

            Canvas.SetLeft(SliceImage, plan.DrawXPt * singleFit);
            Canvas.SetTop(SliceImage, top * singleFit);
            SliceImage.Width = Math.Max(1, plan.DrawWidthPt * singleFit);
            SliceImage.Height = Math.Max(1, plan.DrawHeightPt * singleFit);

            PageLabel.Text = layout.Columns > 1
                ? $"Page {_page + 1} of {layout.PageCount}  ·  row {plan.Row + 1}, column {plan.Column + 1}"
                : $"Page {_page + 1} of {layout.PageCount}";

            PrevPageBtn.IsEnabled = _page > 0;
            NextPageBtn.IsEnabled = _page < layout.PageCount - 1;

            PageDetail.Text =
                $"Showing rows {plan.Source.Y + 1:N0}–{plan.Source.Y + plan.Source.Height:N0} " +
                $"of {singleImg.PixelHeight:N0}";
        }

        private void ShowEmptyPreview()
        {
            PageShadow.Visibility = Visibility.Collapsed;
            PreviewEmpty.Visibility = Visibility.Visible;
            SliceImage.Source = null;
            PageLabel.Text = "";
            PageDetail.Text = "";
            PrevPageBtn.IsEnabled = false;
            NextPageBtn.IsEnabled = false;
        }

        // ── export ────────────────────────────────────────────────────────────

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_items.Count == 0) return;

            var o = BuildOptions();

            string defaultName = _isMultiImage
                ? $"{_settings.FilenamePrefix}_Collection_{DateTime.Now:yyyyMMdd_HHmmss}"
                : (_sourcePath != null
                    ? Path.GetFileNameWithoutExtension(_sourcePath)
                    : $"{_settings.FilenamePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}");

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PDF document|*.pdf",
                AddExtension = true,
                DefaultExt = "pdf",
                FileName = defaultName,
                InitialDirectory = Directory.Exists(_settings.SaveFolder)
                    ? _settings.SaveFolder : null
            };

            if (dlg.ShowDialog(this) != true) return;

            SetBusy(true);

            string target = dlg.FileName;
            Exception? failure = null;

            // Prepare all data on the UI thread before background execution
            List<PdfOutputPage>? multiPages = null;
            BitmapSource? singleImage = null;

            if (_isMultiImage)
            {
                multiPages = new List<PdfOutputPage>();
                int multiMode = ModeCombo.SelectedIndex;

                foreach (var item in _items)
                {
                    var img = item.Image;
                    if (img.CanFreeze && !img.IsFrozen) img.Freeze();

                    double pageWPt = o.PageWidthPt;
                    double pageHPt = o.PageHeightPt;
                    double contentWPt = o.ContentWidthPt;
                    double contentHPt = o.ContentHeightPt;

                    double drawWPt, drawHPt;
                    if (multiMode == 1) // Original size (72 DPI)
                    {
                        drawWPt = Math.Min(contentWPt, img.PixelWidth * 72.0 / 96.0);
                        drawHPt = Math.Min(contentHPt, img.PixelHeight * 72.0 / 96.0);
                    }
                    else if (multiMode == 2) // Stretch
                    {
                        drawWPt = contentWPt;
                        drawHPt = contentHPt;
                    }
                    else // Fit to page
                    {
                        double imgScale = Math.Min(contentWPt / img.PixelWidth, contentHPt / img.PixelHeight);
                        drawWPt = img.PixelWidth * imgScale;
                        drawHPt = img.PixelHeight * imgScale;
                    }

                    double alignX = o.HorizontalAlign switch
                    {
                        PdfAlign.Start => 0.0,
                        PdfAlign.End => 1.0,
                        _ => 0.5
                    };
                    double alignY = o.VerticalAlign switch
                    {
                        PdfAlign.Start => 1.0, // Top in PDF coords (origin at bottom)
                        PdfAlign.End => 0.0,   // Bottom in PDF coords
                        _ => 0.5
                    };

                    double drawXPt = o.MarginPt + (contentWPt - drawWPt) * alignX;
                    double drawYPt = o.MarginPt + (contentHPt - drawHPt) * alignY;

                    multiPages.Add(new PdfOutputPage
                    {
                        Image = img,
                        WidthPt = pageWPt,
                        HeightPt = pageHPt,
                        DrawXPt = drawXPt,
                        DrawYPt = drawYPt,
                        DrawWidthPt = drawWPt,
                        DrawHeightPt = drawHPt
                    });
                }
            }
            else
            {
                singleImage = _items[0].Image;
                if (singleImage.CanFreeze && !singleImage.IsFrozen) singleImage.Freeze();
            }

            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        if (_isMultiImage && multiPages != null)
                        {
                            PdfExporter.SavePages(multiPages, target, 72.0, o.UseJpeg, o.JpegQuality,
                                (done, total) =>
                                    Dispatcher.BeginInvoke(new Action(() =>
                                        StatusText.Text = $"Writing page {done} of {total}…")));
                        }
                        else if (singleImage != null)
                        {
                            PdfExporter.Save(singleImage, target, o, (done, total) =>
                                Dispatcher.BeginInvoke(new Action(() =>
                                    StatusText.Text = $"Writing page {done} of {total}…")));
                        }
                    }
                    catch (Exception ex) { failure = ex; }
                });
            }
            finally
            {
                SetBusy(false);
            }

            if (failure != null)
            {
                StatusText.Text = "";
                ThemedDialog.Show(this, "Couldn't write the PDF:\n" + failure.Message,
                    "Advanced Snip", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SavedPath = target;
            DialogResult = true;
        }

        private void SetBusy(bool busy)
        {
            ExportBtn.IsEnabled = !busy;
            CancelBtn.IsEnabled = !busy;
            ModeCombo.IsEnabled = !busy;
            QualityCombo.IsEnabled = !busy;
            if (busy) StatusText.Text = "Preparing…";
        }
    }
}
