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
    /// Allows temporary drawing/annotation (Pen, Arrow, Circle, Erasing, Undo),
    /// permanent in-place saving, screen pinning (always on top, draggable), and cursor-anchored zooming.
    /// </summary>
    public partial class ClipPreviewWindow : Window
    {
        private enum EditTool
        {
            Pen,
            Arrow,
            Circle,
            Eraser
        }

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
        private EditTool _currentTool = EditTool.Pen;
        private Color _currentColor = (Color)ColorConverter.ConvertFromString("#EF4444");

        // Shape drawing state
        private Point _dragStart;
        private bool _isDraggingShape;
        private Stroke? _tempShapeStroke;

        public ClipPreviewWindow()
        {
            InitializeComponent();

            Scribble.DefaultDrawingAttributes = CreateDrawingAttributes();
            Scribble.Strokes.StrokesChanged += (_, _) => UpdateSaveButtonState();

            ThemeManager.ApplyToWindow(this);

            Activated += (_, _) => _activated = true;
            Deactivated += (_, _) => { if (_activated) Hide(); };
        }

        private DrawingAttributes CreateDrawingAttributes() => new()
        {
            Color = _currentColor,
            Width = 3.6,
            Height = 3.6,
            FitToCurve = true,
            IgnorePressure = false,
            StylusTip = StylusTip.Ellipse
        };

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

            const int chromeW = 32;
            const int chromeH = 80;

            int availW = Math.Max(200, (int)(area.Width * 0.86) - chromeW);
            int availH = Math.Max(200, (int)(area.Height * 0.86) - chromeH);

            double scale = Math.Min((double)availW / imageW, (double)availH / imageH);
            scale = Math.Min(scale, 3.0);

            int w = Math.Max(1, (int)Math.Round(imageW * scale)) + chromeW;
            int h = Math.Max(1, (int)Math.Round(imageH * scale)) + chromeH;

            w = Math.Max(w, 560);

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
            // Ctrl + Wheel: Zoom in / Zoom out
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

            if (_zoomFactor > 1.001)
                return;

            if (_totalCount <= 1)
                return;

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

        // ── Drawing & Annotations ─────────────────────────────────────────────

        private void DrawToggle_Click(object sender, RoutedEventArgs e)
            => SetDrawing(!_drawing);

        private void SetDrawing(bool on)
        {
            _drawing = on;
            if (on) _stayOpen = true;

            ToolBarStrip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            DrawBtnText.Text = on ? "Done" : "Annotate";

            Scribble.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            Scribble.IsHitTestVisible = on;

            if (on)
            {
                SetTool(_currentTool);
            }
            else
            {
                Scribble.EditingMode = InkCanvasEditingMode.None;
                ClearScribble();
            }

            UpdateSaveButtonState();
            UpdateHintText();
        }

        private void SetTool(EditTool tool)
        {
            _currentTool = tool;

            if (!_drawing)
            {
                SetDrawing(true);
                return;
            }

            switch (tool)
            {
                case EditTool.Pen:
                    Scribble.EditingMode = InkCanvasEditingMode.Ink;
                    Scribble.Cursor = Cursors.Pen;
                    break;
                case EditTool.Eraser:
                    Scribble.EditingMode = InkCanvasEditingMode.EraseByStroke;
                    Scribble.Cursor = Cursors.Hand;
                    break;
                case EditTool.Arrow:
                case EditTool.Circle:
                    Scribble.EditingMode = InkCanvasEditingMode.None;
                    Scribble.Cursor = Cursors.Cross;
                    break;
            }

            UpdateToolButtons();
            UpdateHintText();
        }

        private void UpdateToolButtons()
        {
            HighlightToolButton(ToolPenBtn, _currentTool == EditTool.Pen);
            HighlightToolButton(ToolArrowBtn, _currentTool == EditTool.Arrow);
            HighlightToolButton(ToolCircleBtn, _currentTool == EditTool.Circle);
            HighlightToolButton(ToolEraserBtn, _currentTool == EditTool.Eraser);

            UpdateColorIndicators();
        }

        private void HighlightToolButton(Button? btn, bool active)
        {
            if (btn == null) return;
            if (active)
            {
                btn.Background = (Brush)FindResource("Brush.AccentSoft");
                btn.BorderBrush = (Brush)FindResource("Brush.Accent");
            }
            else
            {
                btn.Background = Brushes.Transparent;
                btn.BorderBrush = Brushes.Transparent;
            }
        }

        private void UpdateColorIndicators()
        {
            HighlightColorIndicator(ColorRedIndicator, _currentColor == (Color)ColorConverter.ConvertFromString("#EF4444"));
            HighlightColorIndicator(ColorOrangeIndicator, _currentColor == (Color)ColorConverter.ConvertFromString("#F97316"));
            HighlightColorIndicator(ColorYellowIndicator, _currentColor == (Color)ColorConverter.ConvertFromString("#EAB308"));
            HighlightColorIndicator(ColorGreenIndicator, _currentColor == (Color)ColorConverter.ConvertFromString("#10B981"));
            HighlightColorIndicator(ColorBlueIndicator, _currentColor == (Color)ColorConverter.ConvertFromString("#3B82F6"));
            HighlightColorIndicator(ColorWhiteIndicator, _currentColor == Colors.White);
        }

        private static void HighlightColorIndicator(Border? indicator, bool selected)
        {
            if (indicator == null) return;
            indicator.BorderBrush = selected ? Brushes.White : Brushes.Transparent;
            indicator.BorderThickness = new Thickness(selected ? 2.0 : 1.0);
        }

        private void UpdateHintText()
        {
            if (!_drawing)
            {
                HintText.Text = "← → scroll  ·  D draw  ·  P pin  ·  Ctrl+wheel zoom  ·  Enter paste  ·  Esc back";
                return;
            }

            HintText.Text = _currentTool switch
            {
                EditTool.Arrow => "Drag to draw arrow  ·  P pen  ·  C circle  ·  E erase  ·  Ctrl+Z undo  ·  S save",
                EditTool.Circle => "Drag to draw circle (Shift for round)  ·  P pen  ·  A arrow  ·  E erase  ·  S save",
                EditTool.Eraser => "Click stroke to erase  ·  P pen  ·  A arrow  ·  C circle  ·  S save  ·  D done",
                _ => "Freehand draw  ·  A arrow  ·  C circle  ·  E erase  ·  Ctrl+Z undo  ·  S save"
            };
        }

        private void ToolPen_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Pen);
        private void ToolArrow_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Arrow);
        private void ToolCircle_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Circle);
        private void ToolEraser_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Eraser);

        private void ToolUndo_Click(object sender, RoutedEventArgs e) => UndoLastStroke();
        private void ToolClear_Click(object sender, RoutedEventArgs e) => ClearScribble();

        private void UndoLastStroke()
        {
            if (Scribble.Strokes.Count > 0)
            {
                Scribble.Strokes.RemoveAt(Scribble.Strokes.Count - 1);
                UpdateSaveButtonState();
            }
        }

        private void Color_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string hex })
            {
                try
                {
                    _currentColor = (Color)ColorConverter.ConvertFromString(hex);
                    Scribble.DefaultDrawingAttributes = CreateDrawingAttributes();
                    UpdateColorIndicators();
                }
                catch
                {
                    // Ignore parse error
                }
            }
        }

        // ── Mouse shape drawing (Arrow & Circle) ──────────────────────────────

        private void Scribble_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_currentTool != EditTool.Arrow && _currentTool != EditTool.Circle) return;

            _dragStart = e.GetPosition(Scribble);
            _isDraggingShape = true;
            _tempShapeStroke = null;
            Scribble.CaptureMouse();
            e.Handled = true;
        }

        private void Scribble_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingShape) return;

            var current = e.GetPosition(Scribble);

            if (_tempShapeStroke != null)
            {
                Scribble.Strokes.Remove(_tempShapeStroke);
            }

            var da = CreateDrawingAttributes();
            if (_currentTool == EditTool.Arrow)
            {
                _tempShapeStroke = CreateArrowStroke(_dragStart, current, da);
            }
            else if (_currentTool == EditTool.Circle)
            {
                bool forceCircle = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                _tempShapeStroke = CreateCircleStroke(_dragStart, current, da, forceCircle);
            }

            if (_tempShapeStroke != null)
            {
                Scribble.Strokes.Add(_tempShapeStroke);
            }
            e.Handled = true;
        }

        private void Scribble_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingShape) return;

            Scribble.ReleaseMouseCapture();
            _isDraggingShape = false;

            var end = e.GetPosition(Scribble);
            double dist = Math.Sqrt(Math.Pow(end.X - _dragStart.X, 2) + Math.Pow(end.Y - _dragStart.Y, 2));

            // If dragged less than 4 pixels, remove as accidental click
            if (dist < 4.0 && _tempShapeStroke != null)
            {
                Scribble.Strokes.Remove(_tempShapeStroke);
            }

            _tempShapeStroke = null;
            UpdateSaveButtonState();
            e.Handled = true;
        }

        private void Scribble_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_isDraggingShape)
            {
                if (_tempShapeStroke != null)
                {
                    Scribble.Strokes.Remove(_tempShapeStroke);
                    _tempShapeStroke = null;
                }
                _isDraggingShape = false;
                UpdateSaveButtonState();
            }
        }

        public static Stroke CreateArrowStroke(Point p1, Point p2, DrawingAttributes da)
        {
            var pts = new StylusPointCollection();
            double dx = p2.X - p1.X;
            double dy = p2.Y - p1.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 2) length = 2;

            double ux = dx / length;
            double uy = dy / length;
            double px = -uy;
            double py = ux;

            double headLen = Math.Clamp(length * 0.28, 14.0, 28.0);
            double headWidth = headLen * 0.52;

            Point basePt = new Point(p2.X - ux * headLen, p2.Y - uy * headLen);
            Point wing1 = new Point(basePt.X + px * headWidth, basePt.Y + py * headWidth);
            Point wing2 = new Point(basePt.X - px * headWidth, basePt.Y - py * headWidth);

            int steps = Math.Max(2, (int)(length / 10));
            for (int i = 0; i <= steps; i++)
            {
                double t = (double)i / steps;
                pts.Add(new StylusPoint(p1.X + dx * t, p1.Y + dy * t));
            }

            pts.Add(new StylusPoint(wing1.X, wing1.Y));
            pts.Add(new StylusPoint(p2.X, p2.Y));
            pts.Add(new StylusPoint(wing2.X, wing2.Y));

            var strokeDa = da.Clone();
            strokeDa.FitToCurve = false;
            return new Stroke(pts, strokeDa);
        }

        public static Stroke CreateCircleStroke(Point p1, Point p2, DrawingAttributes da, bool forceCircle = false)
        {
            var pts = new StylusPointCollection();
            double cx = (p1.X + p2.X) / 2.0;
            double cy = (p1.Y + p2.Y) / 2.0;
            double rx = Math.Abs(p2.X - p1.X) / 2.0;
            double ry = Math.Abs(p2.Y - p1.Y) / 2.0;

            if (forceCircle)
            {
                double r = Math.Max(rx, ry);
                rx = r;
                ry = r;
            }

            if (rx < 2) rx = 2;
            if (ry < 2) ry = 2;

            int steps = 64;
            for (int i = 0; i <= steps; i++)
            {
                double theta = i * 2.0 * Math.PI / steps;
                double x = cx + rx * Math.Cos(theta);
                double y = cy + ry * Math.Sin(theta);
                pts.Add(new StylusPoint(x, y));
            }

            var strokeDa = da.Clone();
            strokeDa.FitToCurve = false;
            return new Stroke(pts, strokeDa);
        }

        // ── Pin to Screen ─────────────────────────────────────────────────────

        private void Pin_Click(object sender, RoutedEventArgs e) => PinCurrentItem();

        private void PinCurrentItem()
        {
            if (_currentItem == null && PreviewImage.Source == null) return;

            BitmapSource toPin;
            if (PreviewImage.Source is BitmapSource baseImg && Scribble.Strokes.Count > 0)
            {
                double viewW = ImageHost.ActualWidth > 0 ? ImageHost.ActualWidth : baseImg.PixelWidth;
                double viewH = ImageHost.ActualHeight > 0 ? ImageHost.ActualHeight : baseImg.PixelHeight;
                toPin = CompositeImageWithStrokes(baseImg, Scribble.Strokes, viewW, viewH);
            }
            else if (_currentItem != null)
            {
                toPin = _currentItem.GetFullImage();
            }
            else
            {
                toPin = (BitmapSource)PreviewImage.Source;
            }

            PinnedClipWindow.ShowForImage(toPin, _currentItem?.FilePath, _currentItem?.Caption);
        }

        // ── Save & Clear ──────────────────────────────────────────────────────

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

                if (_currentItem != null)
                {
                    _currentItem.FilePath = targetPath;
                }

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
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Dismiss();
                return;
            }

            // Undo
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Z)
            {
                e.Handled = true;
                UndoLastStroke();
                return;
            }

            // Pin shortcut (Ctrl+P or P when not drawing)
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.P)
            {
                e.Handled = true;
                PinCurrentItem();
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

                    case Key.P:
                    case Key.D1:
                    case Key.NumPad1:
                        e.Handled = true;
                        SetTool(EditTool.Pen);
                        return;

                    case Key.A:
                    case Key.D2:
                    case Key.NumPad2:
                        e.Handled = true;
                        SetTool(EditTool.Arrow);
                        return;

                    case Key.C:
                    case Key.O:
                    case Key.D3:
                    case Key.NumPad3:
                        e.Handled = true;
                        SetTool(EditTool.Circle);
                        return;

                    case Key.E:
                    case Key.D4:
                    case Key.NumPad4:
                        e.Handled = true;
                        SetTool(EditTool.Eraser);
                        return;

                    case Key.Delete:
                    case Key.Back:
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
                }
            }
            else
            {
                if (e.Key == Key.D)
                {
                    e.Handled = true;
                    SetDrawing(true);
                    return;
                }

                if (e.Key == Key.P)
                {
                    e.Handled = true;
                    PinCurrentItem();
                    return;
                }
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
