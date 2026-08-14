using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    /// <summary>
    /// An enlarged look at one clipboard image, opened by right-clicking a thumbnail in
    /// the history HUD.
    ///
    /// Allows temporary drawing (D to draw, D / Esc to cancel, Save / S to commit permanently),
    /// permanent in-place saving, and cursor-anchored zooming.
    /// </summary>
    public partial class ClipPreviewWindow : Window
    {
        private ClipItem? _currentItem;
        private int _totalCount;
        private int _wheelTravel;
        private double _zoomFactor = 1.0;
        private string _baseDetail = "";
        private bool _stayOpen;

        /// <summary>Raised for each notch of travel. The HUD decides what it means.</summary>
        public event EventHandler<int>? StepRequested;

        /// <summary>Raised on Enter — paste the image being previewed.</summary>
        public event EventHandler? PasteRequested;

        /// <summary>Raised when the user deliberately closes the preview.</summary>
        public event EventHandler? CloseRequested;

        private bool _activated;
        private bool _drawing;

        /// <summary>The pen for drawing annotations on preview.</summary>
        private static readonly DrawingAttributes RedPen = new()
        {
            Color = Colors.Red,
            Width = 3.4,
            Height = 3.4,
            FitToCurve = true,
            IgnorePressure = false
        };

        public ClipPreviewWindow()
        {
            InitializeComponent();

            Scribble.DefaultDrawingAttributes = RedPen;
            Scribble.Strokes.StrokesChanged += (_, _) => UpdateSaveButtonState();

            ThemeManager.ApplyToWindow(this);

            Activated += (_, _) => _activated = true;
            Deactivated += (_, _) => { if (_activated) Hide(); };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var handle = new WindowInteropHelper(this).Handle;
            int ex = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE,
                ex | NativeMethods.WS_EX_TOOLWINDOW);
        }

        internal void ShowItem(ClipItem item, int index, int count, MonitorEntry monitor)
        {
            _totalCount = count;

            if (_currentItem != item)
            {
                _currentItem = item;
                _stayOpen = false;
                PreviewImage.Source = item.GetFullImage();

                ClearScribble();
                if (_drawing) SetDrawing(false);
                SetZoom(1.0);
            }

            var image = item.GetFullImage();
            CountText.Text = $"{index + 1} of {count}";
            _baseDetail = $"{image.PixelWidth} × {image.PixelHeight}   ·   copied {item.Caption}";
            UpdateDetailText();

            if (!IsVisible) { _activated = false; Show(); }
            Resize(image.PixelWidth, image.PixelHeight, monitor);
            _wheelTravel = 0;
        }

        internal void MoveToMonitor(MonitorEntry monitor)
        {
            if (!IsVisible || PreviewImage.Source is not BitmapSource image)
                return;

            Resize(image.PixelWidth, image.PixelHeight, monitor);
        }

        private void Resize(int imageW, int imageH, MonitorEntry monitor)
        {
            var area = monitor.WorkArea;
            if (imageW <= 0 || imageH <= 0 || area.Width <= 0 || area.Height <= 0) return;

            const int chromeW = 30;
            const int chromeH = 64;

            int availW = Math.Max(200, (int)(area.Width * 0.86) - chromeW);
            int availH = Math.Max(200, (int)(area.Height * 0.86) - chromeH);

            double scale = Math.Min((double)availW / imageW, (double)availH / imageH);
            scale = Math.Min(scale, 3.0);

            int w = Math.Max(1, (int)Math.Round(imageW * scale)) + chromeW;
            int h = Math.Max(1, (int)Math.Round(imageH * scale)) + chromeH;

            w = Math.Max(w, 480);

            int x = area.X + (area.Width - w) / 2;
            int y = area.Y + (area.Height - h) / 2;

            WindowPlacement.SetBounds(this, new System.Drawing.Rectangle(x, y, w, h), topMost: true);
        }

        // ── Zoom support (Ctrl + MouseWheel) ──────────────────────────────────

        private void SetZoom(double zoom, Point? anchorInScrollViewer = null)
        {
            double oldZoom = _zoomFactor;
            _zoomFactor = Math.Clamp(zoom, 1.0, 5.0);

            if (Math.Abs(_zoomFactor - oldZoom) < 0.0001) return;

            Point anchor = anchorInScrollViewer ?? new Point(
                ImageScrollViewer.ViewportWidth > 0 ? ImageScrollViewer.ViewportWidth / 2 : ImageScrollViewer.ActualWidth / 2,
                ImageScrollViewer.ViewportHeight > 0 ? ImageScrollViewer.ViewportHeight / 2 : ImageScrollViewer.ActualHeight / 2);

            double contentX = anchor.X + ImageScrollViewer.HorizontalOffset;
            double contentY = anchor.Y + ImageScrollViewer.VerticalOffset;
            double factor = _zoomFactor / oldZoom;

            ImageScale.ScaleX = _zoomFactor;
            ImageScale.ScaleY = _zoomFactor;

            bool zoomed = _zoomFactor > 1.001;
            ImageScrollViewer.HorizontalScrollBarVisibility = zoomed ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            ImageScrollViewer.VerticalScrollBarVisibility = zoomed ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;

            if (zoomed)
            {
                ImageScrollViewer.UpdateLayout();

                double targetX = contentX * factor - anchor.X;
                double targetY = contentY * factor - anchor.Y;

                ImageScrollViewer.ScrollToHorizontalOffset(Math.Max(0, targetX));
                ImageScrollViewer.ScrollToVerticalOffset(Math.Max(0, targetY));
            }
            else
            {
                ImageScrollViewer.ScrollToHome();
            }

            UpdateDetailText();
        }

        private void UpdateDetailText()
        {
            if (_zoomFactor > 1.001)
                DetailText.Text = $"{_zoomFactor * 100:0}% zoom   ·   {_baseDetail}";
            else
                DetailText.Text = _baseDetail;
        }

        private void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Ctrl + Wheel: Zoom in / Zoom out (zoom out only works if already zoomed in)
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                Point mousePos = e.GetPosition(ImageScrollViewer);

                if (e.Delta > 0)
                {
                    SetZoom(_zoomFactor + 0.25, mousePos);
                }
                else if (e.Delta < 0 && _zoomFactor > 1.001)
                {
                    SetZoom(Math.Max(1.0, _zoomFactor - 0.25), mousePos);
                }
                return;
            }

            // If zoomed in and scrolling without Ctrl, let the ScrollViewer scroll the image
            if (_zoomFactor > 1.001)
                return;

            // If there's only 1 image in the clipboard, scrolling doesn't navigate and does NOT clear drawings
            if (_totalCount <= 1)
                return;

            // Normal wheel travel between history items
            e.Handled = true;
            _wheelTravel += e.Delta;

            while (Math.Abs(_wheelTravel) >= Mouse.MouseWheelDeltaForOneLine)
            {
                if (_wheelTravel > 0)
                {
                    _wheelTravel -= Mouse.MouseWheelDeltaForOneLine;
                    StepRequested?.Invoke(this, -1);
                }
                else
                {
                    _wheelTravel += Mouse.MouseWheelDeltaForOneLine;
                    StepRequested?.Invoke(this, +1);
                }
            }
        }

        // ── Drawing & Saving ──────────────────────────────────────────────────

        private void SetDrawing(bool on)
        {
            _drawing = on;
            if (on) _stayOpen = true;

            Scribble.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            Scribble.EditingMode = on ? InkCanvasEditingMode.Ink : InkCanvasEditingMode.None;
            Scribble.IsHitTestVisible = on;

            if (!on)
            {
                ClearScribble();
            }

            UpdateSaveButtonState();

            HintText.Text = on
                ? "E erase  ·  C clear  ·  S save  ·  D done"
                : "← → scroll  ·  D draw  ·  Ctrl+wheel zoom  ·  Enter paste  ·  Esc back";
        }

        private void UpdateSaveButtonState()
        {
            bool hasActiveStrokes = Scribble.Strokes.Count > 0;

            if (_drawing || hasActiveStrokes)
            {
                SaveBtn.Visibility = Visibility.Visible;
                SaveBtn.IsEnabled = hasActiveStrokes;
            }
            else
            {
                SaveBtn.Visibility = Visibility.Collapsed;
                SaveBtn.IsEnabled = false;
            }
        }

        private void ClearScribble()
        {
            if (Scribble.Strokes.Count > 0) Scribble.Strokes.Clear();
            UpdateSaveButtonState();
        }

        private void Save_Click(object sender, RoutedEventArgs e) => SaveDrawingPermanently();

        /// <summary>
        /// Permanently saves annotations into the same image file in-place (no duplicate files created)
        /// and updates both the clipboard history and Windows clipboard.
        /// </summary>
        private void SaveDrawingPermanently()
        {
            if (PreviewImage.Source is not BitmapSource baseImg || Scribble.Strokes.Count == 0)
                return;

            try
            {
                double viewW = ImageHost.ActualWidth > 0 ? ImageHost.ActualWidth : baseImg.PixelWidth;
                double viewH = ImageHost.ActualHeight > 0 ? ImageHost.ActualHeight : baseImg.PixelHeight;

                var composited = CompositeImageWithStrokes(baseImg, Scribble.Strokes, viewW, viewH);

                var app = Application.Current as App;
                string? targetPath = _currentItem?.FilePath;

                bool isPdf = !string.IsNullOrEmpty(targetPath) &&
                             string.Equals(Path.GetExtension(targetPath), ".pdf", StringComparison.OrdinalIgnoreCase);

                if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath) || isPdf)
                {
                    string folder = app?.Settings?.SaveFolder ?? "";
                    if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                        folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

                    string prefix = app?.Settings?.FilenamePrefix ?? "Snip";
                    string baseName = isPdf && !string.IsNullOrEmpty(targetPath)
                        ? Path.GetFileNameWithoutExtension(targetPath) + "_edited"
                        : $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}";

                    string filename = $"{baseName}.png";
                    targetPath = Path.Combine(folder, filename);
                }

                // Overwrite the image file on disk permanently (atomic temp write + replace)
                string tempPath = targetPath + ".tmp";
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                {
                    BitmapEncoder encoder;
                    if (string.Equals(Path.GetExtension(targetPath), ".jpg", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetExtension(targetPath), ".jpeg", StringComparison.OrdinalIgnoreCase))
                    {
                        encoder = new JpegBitmapEncoder { QualityLevel = 95 };
                    }
                    else
                    {
                        encoder = new PngBitmapEncoder();
                    }
                    encoder.Frames.Add(BitmapFrame.Create(composited));
                    encoder.Save(fs);
                }

                File.Move(tempPath, targetPath, overwrite: true);

                // Update the current item's file path to the newly saved image
                if (_currentItem != null)
                {
                    _currentItem.FilePath = targetPath;
                }

                // Update clipboard history in-place and Windows system clipboard
                if (app != null)
                {
                    app.ReplaceInHistory(targetPath, composited);
                }
                else
                {
                    ClipboardService.SetImage(composited, targetPath);
                }

                PreviewImage.Source = composited;
                ClearScribble();
                UpdateSaveButtonState();

                DetailText.Text = "Saved as image (" + Path.GetFileName(targetPath) + ")";
            }
            catch (Exception ex)
            {
                DetailText.Text = "Couldn't save: " + ex.Message;
            }
        }

        private static BitmapSource CompositeImageWithStrokes(BitmapSource baseImage, StrokeCollection strokes, double viewWidth, double viewHeight)
        {
            int pixelWidth = baseImage.PixelWidth;
            int pixelHeight = baseImage.PixelHeight;

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(baseImage, new Rect(0, 0, pixelWidth, pixelHeight));

                if (strokes.Count > 0 && viewWidth > 0 && viewHeight > 0)
                {
                    double scaleX = pixelWidth / viewWidth;
                    double scaleY = pixelHeight / viewHeight;

                    var transform = new Matrix();
                    transform.Scale(scaleX, scaleY);

                    var scaledStrokes = strokes.Clone();
                    scaledStrokes.Transform(transform, false);

                    scaledStrokes.Draw(dc);
                }
            }

            var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        // ── Keyboard handling ─────────────────────────────────────────────────

        private void Preview_KeyDown(object sender, KeyEventArgs e)
        {
            // Escape always immediately exits preview, discarding unsaved scribbles
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Dismiss();
                return;
            }

            if (_drawing)
            {
                switch (e.Key)
                {
                    case Key.D:
                        e.Handled = true;
                        SetDrawing(false);
                        return;

                    case Key.C:
                        e.Handled = true;
                        ClearScribble();
                        return;

                    case Key.S:
                        if (Scribble.Strokes.Count > 0)
                        {
                            e.Handled = true;
                            SaveDrawingPermanently();
                        }
                        return;

                    case Key.E:
                        e.Handled = true;
                        Scribble.EditingMode =
                            Scribble.EditingMode == InkCanvasEditingMode.EraseByStroke
                                ? InkCanvasEditingMode.Ink
                                : InkCanvasEditingMode.EraseByStroke;
                        HintText.Text = Scribble.EditingMode == InkCanvasEditingMode.EraseByStroke
                            ? "click stroke to erase  ·  E pen  ·  C clear  ·  S save  ·  D done"
                            : "E erase  ·  C clear  ·  S save  ·  D done";
                        return;
                }
            }

            if (e.Key == Key.D)
            {
                e.Handled = true;
                SetDrawing(true);
                return;
            }

            switch (e.Key)
            {
                case Key.Left:
                    if (_totalCount > 1)
                    {
                        e.Handled = true;
                        StepRequested?.Invoke(this, -1);
                    }
                    break;

                case Key.Right:
                    if (_totalCount > 1)
                    {
                        e.Handled = true;
                        StepRequested?.Invoke(this, +1);
                    }
                    break;

                case Key.Enter:
                    e.Handled = true;
                    PasteRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        private void Preview_RightClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_drawing || _stayOpen) return;
            Dismiss();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Dismiss();

        private void Dismiss()
        {
            ClearScribble();
            if (_drawing) SetDrawing(false);
            SetZoom(1.0);

            CloseRequested?.Invoke(this, EventArgs.Empty);
            Hide();
        }
    }
}
