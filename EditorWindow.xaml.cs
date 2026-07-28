using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;   // Polyline, Line, Ellipse — note this makes the bare
                               // name `Path` ambiguous with System.IO.Path, so file
                               // paths are fully qualified below.
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    internal enum EditTool { Crop, Pen, Highlight, Rect, Ellipse, Arrow, Redact, TextSelect }

    /// <summary>
    /// A small image editor: crop, rotate, flip, annotate, redact.
    ///
    /// Two decisions shape the whole file.
    ///
    /// **Everything happens in image pixels, never in screen units.** The stage is sized
    /// to exactly (pixelWidth × zoom), so a pointer position divides by zoom to give an
    /// image pixel with no rounding drift. Edits are rasterised through a
    /// RenderTargetBitmap fixed at 96 DPI and the source's pixel dimensions, which means
    /// a capture from a 150%-scaled monitor edits at its true resolution instead of being
    /// quietly resampled to the editor's own DPI.
    ///
    /// **Undo holds whole frames, not a command list.** A screenshot is a few megabytes
    /// and the stack is capped, so the memory is cheap and the correctness is free —
    /// there's no way for a replayed command to land differently than it did the first
    /// time.
    /// </summary>
    public partial class EditorWindow : Window
    {
        private const int MaxUndo = 24;

        private BitmapSource _image = null!;
        private readonly Stack<BitmapSource> _undo = new();
        private readonly Stack<BitmapSource> _redo = new();

        private EditTool _tool = EditTool.Crop;
        private double _zoom = 1.0;
        private bool _fitOnLoad = true;

        private Color _colour = Color.FromRgb(0xEF, 0x44, 0x44);   // red
        private double _thickness = 4;

        private bool _dirty;
        private bool _dragging;
        private System.Windows.Point _dragStart;
        private System.Windows.Shapes.Shape? _preview;
        private Polyline? _freehand;
        private System.Windows.Shapes.Rectangle? _cropBox;
        private Int32Rect _pendingCrop;

        /// <summary>The file this was opened from, when it came from disk.</summary>
        public string? FilePath { get; private set; }

        /// <summary>The edited image, once the user has saved. Null if they didn't.</summary>
        public BitmapSource? SavedImage { get; private set; }

        /// <summary>
        /// Raised after a successful save so the app can refresh the clipboard, the
        /// capture history and the gallery from one place.
        /// </summary>
        public event EventHandler<EditorSavedEventArgs>? Saved;

        private readonly AppSettings _settings;

        public EditorWindow(BitmapSource image, string? path, AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;
            FilePath = path;
            _image = image;

            Title = path == null
                ? "Edit capture"
                : "Edit — " + System.IO.Path.GetFileName(path);

            BuildSwatches();
            ToolCrop.IsChecked = true;

            ThemeManager.ApplyToWindow(this);
            SourceInitialized += (_, _) => WindowPlacement.CenterOnActiveMonitor(this);
            Loaded += (_, _) => { if (_fitOnLoad) ZoomToFit(); Refresh(); };
        }

        // ─────────────────────────────── image plumbing ───────────────────────

        private int PixelW => _image.PixelWidth;
        private int PixelH => _image.PixelHeight;

        /// <summary>Replaces the working image, recording the previous one for undo.</summary>
        private void Commit(BitmapSource next)
        {
            if (next == null) return;
            if (!next.IsFrozen && next.CanFreeze) next.Freeze();

            _undo.Push(_image);
            while (_undo.Count > MaxUndo)
            {
                // Stack has no bounded mode; rebuild without the oldest frame.
                var keep = _undo.ToArray();          // newest first
                _undo.Clear();
                for (int i = Math.Min(keep.Length, MaxUndo) - 1; i >= 0; i--)
                    _undo.Push(keep[i]);
                break;
            }

            _redo.Clear();
            _image = next;

            // The picture changed, so the word positions no longer describe it.
            InvalidateLayout();
            _dirty = true;

            Refresh();
            if (TextMode) EnterTextMode();
        }

        private void Refresh()
        {
            Canvas1.Source = _image;
            ApplyZoom();

            SizeText.Text = $"{PixelW} × {PixelH} px";
            UndoBtn.IsEnabled = _undo.Count > 0;
            RedoBtn.IsEnabled = _redo.Count > 0;
            HintText.Text = _tool switch
            {
                EditTool.Crop    => "Drag a box, then Enter to crop",
                EditTool.Redact  => "Drag over anything that should be covered",
                EditTool.Pen     => "Drag to draw",
                EditTool.Highlight => "Drag to highlight",
                EditTool.TextSelect => "Drag across words to select and copy them",
                _                => "Drag to place"
            };
        }

        /// <summary>
        /// Rasterises whatever <paramref name="draw"/> puts on the context on top of the
        /// current image. Fixed at 96 DPI so the output pixel grid matches the input's
        /// exactly whatever the source image or the monitor claims.
        /// </summary>
        private BitmapSource RenderOnto(Action<DrawingContext> draw)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(_image, new Rect(0, 0, PixelW, PixelH));
                draw(dc);
            }

            var rtb = new RenderTargetBitmap(PixelW, PixelH, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        // ─────────────────────────────── zoom ─────────────────────────────────

        private void ApplyZoom()
        {
            Stage.Width  = Math.Max(1, PixelW * _zoom);
            Stage.Height = Math.Max(1, PixelH * _zoom);
            ZoomText.Text = $"{_zoom * 100:F0}%";

            // Word highlights are positioned in screen units, so they have to be rebuilt
            // whenever the scale changes.
            if (TextMode && _layout != null) LayoutWordBoxes();
        }

        private void ZoomToFit()
        {
            double availW = Math.Max(64, Scroller.ActualWidth  - 60);
            double availH = Math.Max(64, Scroller.ActualHeight - 60);
            // Never enlarge on open: a 200×80 snip blown up to fill the window looks
            // broken and hides how small it really is.
            _zoom = Math.Min(1.0, Math.Min(availW / PixelW, availH / PixelH));
            if (_zoom <= 0 || double.IsNaN(_zoom)) _zoom = 1.0;
            _fitOnLoad = false;
            ApplyZoom();
        }

        private void SetZoom(double z, System.Windows.Point? anchor = null)
        {
            _zoom = Math.Clamp(z, 0.05, 8.0);
            ApplyZoom();
            if (anchor.HasValue)
            {
                Scroller.ScrollToHorizontalOffset(anchor.Value.X * _zoom - Scroller.ViewportWidth / 2);
                Scroller.ScrollToVerticalOffset(anchor.Value.Y * _zoom - Scroller.ViewportHeight / 2);
            }
        }

        private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            e.Handled = true;

            var overStage = e.GetPosition(Overlay);
            var imagePoint = new System.Windows.Point(overStage.X / _zoom, overStage.Y / _zoom);
            SetZoom(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), imagePoint);
        }

        // ─────────────────────────────── tools ────────────────────────────────

        private void BuildSwatches()
        {
            var colours = new[]
            {
                Color.FromRgb(0xEF, 0x44, 0x44), // red
                Color.FromRgb(0xF9, 0x73, 0x16), // orange
                Color.FromRgb(0xFA, 0xCC, 0x15), // yellow
                Color.FromRgb(0x22, 0xC5, 0x5E), // green
                Color.FromRgb(0x3B, 0x82, 0xF6), // blue
                Color.FromRgb(0xA8, 0x55, 0xF7), // purple
                Colors.White,
                Colors.Black
            };

            foreach (var c in colours)
            {
                var swatch = new System.Windows.Shapes.Rectangle
                {
                    Width = 20, Height = 20, RadiusX = 5, RadiusY = 5,
                    Fill = new SolidColorBrush(c),
                    Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
                    StrokeThickness = 1,
                    Margin = new Thickness(3, 0, 3, 0),
                    Cursor = Cursors.Hand,
                    Tag = c
                };
                swatch.MouseLeftButtonDown += Swatch_Click;
                SwatchList.Items.Add(swatch);
            }
            HighlightSwatch();
        }

        private void Swatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is Color c)
            {
                _colour = c;
                HighlightSwatch();
            }
        }

        private void HighlightSwatch()
        {
            foreach (var obj in SwatchList.Items)
                if (obj is System.Windows.Shapes.Rectangle r && r.Tag is Color c)
                {
                    bool active = c == _colour;
                    r.StrokeThickness = active ? 3 : 1;
                    r.Stroke = active
                        ? new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6))
                        : new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0));
                }
        }

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (Overlay == null) return;   // fires during InitializeComponent
            ClearPreview();

            _tool = sender == ToolPen      ? EditTool.Pen
                  : sender == ToolMark     ? EditTool.Highlight
                  : sender == ToolRect     ? EditTool.Rect
                  : sender == ToolEllipse  ? EditTool.Ellipse
                  : sender == ToolArrow    ? EditTool.Arrow
                  : sender == ToolRedact   ? EditTool.Redact
                  : sender == ToolText     ? EditTool.TextSelect
                  : EditTool.Crop;

            if (TextMode) { EnterTextMode(); }
            else
            {
                ClearWordBoxes();
                Overlay.Cursor = _tool == EditTool.Crop ? Cursors.Cross : Cursors.Pen;
            }
            Refresh();
        }

        private void Thickness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _thickness = e.NewValue;
            if (ThicknessLabel != null) ThicknessLabel.Text = $"{_thickness:F0} px";
        }

        // ─────────────────────────────── drawing ──────────────────────────────

        /// <summary>Overlay position → image pixel, clamped to the image.</summary>
        private System.Windows.Point ToImage(System.Windows.Point p) => new(
            Math.Clamp(p.X / _zoom, 0, PixelW),
            Math.Clamp(p.Y / _zoom, 0, PixelH));

        private void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // While removing a background, a click means "this bit is background too" —
            // the escape hatch for a region the flood fill couldn't reach because it isn't
            // connected to the border.
            if (BgPanelOpen)
            {
                var seed = ToImage(e.GetPosition(Overlay));
                _bgSeeds.Add(seed);
                RunBackgroundRemoval();
                return;
            }

            if (TextMode)
            {
                if (e.ClickCount >= 2) SelectLineAt(e.GetPosition(Overlay));
                else                   BeginTextSelection(e.GetPosition(Overlay));
                return;
            }

            Overlay.CaptureMouse();
            _dragging = true;
            _dragStart = e.GetPosition(Overlay);
            ClearPreview();

            var stroke = new SolidColorBrush(_colour);
            double onScreen = Math.Max(1, _thickness * _zoom);

            switch (_tool)
            {
                case EditTool.Crop:
                    _cropBox = new System.Windows.Shapes.Rectangle
                    {
                        Stroke = new SolidColorBrush(Colors.White),
                        StrokeThickness = 1.5,
                        StrokeDashArray = new DoubleCollection { 4, 3 },
                        Fill = new SolidColorBrush(Color.FromArgb(0x33, 0x3B, 0x82, 0xF6))
                    };
                    _preview = _cropBox;
                    break;

                case EditTool.Pen:
                case EditTool.Highlight:
                    _freehand = new Polyline
                    {
                        Stroke = _tool == EditTool.Highlight
                            ? new SolidColorBrush(Color.FromArgb(0x66, _colour.R, _colour.G, _colour.B))
                            : stroke,
                        StrokeThickness = _tool == EditTool.Highlight ? onScreen * 3 : onScreen,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    _freehand.Points.Add(_dragStart);
                    _preview = _freehand;
                    break;

                case EditTool.Rect:
                    _preview = new System.Windows.Shapes.Rectangle
                        { Stroke = stroke, StrokeThickness = onScreen };
                    break;

                case EditTool.Ellipse:
                    _preview = new Ellipse { Stroke = stroke, StrokeThickness = onScreen };
                    break;

                case EditTool.Arrow:
                    _preview = new Line
                    {
                        Stroke = stroke, StrokeThickness = onScreen,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    break;

                case EditTool.Redact:
                    _preview = new System.Windows.Shapes.Rectangle
                        { Fill = new SolidColorBrush(Colors.Black), Opacity = 0.85 };
                    break;
            }

            if (_preview != null) Overlay.Children.Add(_preview);
        }

        private void Overlay_MouseMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(Overlay);

            if (_selecting) { ExtendTextSelection(p); return; }

            if (!_dragging)
            {
                var ip = ToImage(p);
                ZoomText.Text = $"{_zoom * 100:F0}%   ({ip.X:F0}, {ip.Y:F0})";
                return;
            }

            if (_freehand != null) { _freehand.Points.Add(p); return; }
            if (_preview is Line line)
            {
                line.X1 = _dragStart.X; line.Y1 = _dragStart.Y;
                line.X2 = p.X;          line.Y2 = p.Y;
                return;
            }
            if (_preview != null)
            {
                var r = new Rect(_dragStart, p);
                Canvas.SetLeft(_preview, r.X);
                Canvas.SetTop(_preview, r.Y);
                _preview.Width  = r.Width;
                _preview.Height = r.Height;
            }
        }

        private void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_selecting) { EndTextSelection(); return; }
            if (!_dragging) return;
            _dragging = false;
            Overlay.ReleaseMouseCapture();

            var end = e.GetPosition(Overlay);
            var a = ToImage(_dragStart);
            var b = ToImage(end);

            switch (_tool)
            {
                case EditTool.Crop:
                    StagePendingCrop(a, b);
                    return;   // keep the box on screen until Enter

                case EditTool.Pen:
                case EditTool.Highlight:
                    CommitFreehand();
                    break;

                case EditTool.Rect:
                    CommitShape(a, b, filled: false, ellipse: false);
                    break;

                case EditTool.Ellipse:
                    CommitShape(a, b, filled: false, ellipse: true);
                    break;

                case EditTool.Arrow:
                    CommitArrow(a, b);
                    break;

                case EditTool.Redact:
                    CommitRedaction(a, b);
                    break;
            }

            ClearPreview();
        }

        private void Overlay_MouseLeave(object sender, MouseEventArgs e)
        {
            // Only relevant when the button was released outside the window; the mouse
            // capture normally keeps events coming.
            if (_dragging && e.LeftButton == MouseButtonState.Released)
            {
                _dragging = false;
                Overlay.ReleaseMouseCapture();
                ClearPreview();
            }
        }

        private void ClearPreview()
        {
            // The word highlights live on their own layer, so clearing drawing previews
            // leaves them alone.
            Overlay.Children.Clear();
            _preview = null;
            _freehand = null;
            _cropBox = null;
            _pendingCrop = default;
        }

        // ─────────────────────────────── edits ────────────────────────────────

        private void StagePendingCrop(System.Windows.Point a, System.Windows.Point b)
        {
            var r = new Rect(a, b);
            int x = (int)Math.Round(r.X), y = (int)Math.Round(r.Y);
            int w = (int)Math.Round(r.Width), h = (int)Math.Round(r.Height);

            x = Math.Clamp(x, 0, PixelW - 1);
            y = Math.Clamp(y, 0, PixelH - 1);
            w = Math.Clamp(w, 0, PixelW - x);
            h = Math.Clamp(h, 0, PixelH - y);

            if (w < 2 || h < 2) { ClearPreview(); return; }

            _pendingCrop = new Int32Rect(x, y, w, h);
            HintText.Text = $"Enter to crop to {w} × {h}, Esc to cancel";
        }

        private void ApplyPendingCrop()
        {
            if (_pendingCrop.Width <= 0 || _pendingCrop.Height <= 0) return;

            // CroppedBitmap keeps a reference to its source, so a chain of crops would
            // pin every intermediate frame in memory. Flattening through the render path
            // gives an independent bitmap and a uniform 96-DPI result.
            var cropped = new CroppedBitmap(_image, _pendingCrop);
            var flat = Flatten(cropped);

            _pendingCrop = default;
            ClearPreview();
            Commit(flat);
        }

        private static BitmapSource Flatten(BitmapSource src)
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawImage(src, new Rect(0, 0, w, h));

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        private void CommitFreehand()
        {
            if (_freehand == null || _freehand.Points.Count < 2) return;

            var pts = new List<System.Windows.Point>(_freehand.Points.Count);
            foreach (var p in _freehand.Points) pts.Add(ToImage(p));

            bool highlight = _tool == EditTool.Highlight;
            var colour = highlight
                ? Color.FromArgb(0x66, _colour.R, _colour.G, _colour.B)
                : _colour;
            double width = highlight ? _thickness * 3 : _thickness;

            Commit(RenderOnto(dc =>
            {
                var pen = new Pen(new SolidColorBrush(colour), width)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap   = PenLineCap.Round,
                    LineJoin     = PenLineJoin.Round
                };

                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(pts[0], false, false);
                    ctx.PolyLineTo(pts.GetRange(1, pts.Count - 1), true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);
            }));
        }

        private void CommitShape(System.Windows.Point a, System.Windows.Point b, bool filled, bool ellipse)
        {
            var r = new Rect(a, b);
            if (r.Width < 2 || r.Height < 2) return;

            Commit(RenderOnto(dc =>
            {
                var pen = new Pen(new SolidColorBrush(_colour), _thickness);
                Brush? fill = filled ? new SolidColorBrush(_colour) : null;

                if (ellipse)
                    dc.DrawEllipse(fill, pen,
                        new System.Windows.Point(r.X + r.Width / 2, r.Y + r.Height / 2),
                        r.Width / 2, r.Height / 2);
                else
                    dc.DrawRectangle(fill, pen, r);
            }));
        }

        private void CommitArrow(System.Windows.Point a, System.Windows.Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 4) return;

            Commit(RenderOnto(dc =>
            {
                var brush = new SolidColorBrush(_colour);
                var pen = new Pen(brush, _thickness) { EndLineCap = PenLineCap.Round,
                                                       StartLineCap = PenLineCap.Round };

                // Head scales with the stroke so a thin arrow doesn't get a huge point,
                // but is capped against the shaft so a short drag doesn't become all head.
                double head = Math.Min(_thickness * 4 + 6, len * 0.4);
                double ux = dx / len, uy = dy / len;

                var tip  = b;
                var back = new System.Windows.Point(b.X - ux * head, b.Y - uy * head);
                var left = new System.Windows.Point(back.X - uy * head * 0.45,
                                                    back.Y + ux * head * 0.45);
                var right= new System.Windows.Point(back.X + uy * head * 0.45,
                                                    back.Y - ux * head * 0.45);

                dc.DrawLine(pen, a, new System.Windows.Point(b.X - ux * head * 0.7,
                                                            b.Y - uy * head * 0.7));

                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(tip, true, true);
                    ctx.LineTo(left, true, true);
                    ctx.LineTo(right, true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(brush, null, geo);
            }));
        }

        private void CommitRedaction(System.Windows.Point a, System.Windows.Point b)
        {
            var r = new Rect(a, b);
            if (r.Width < 2 || r.Height < 2) return;

            // Deliberately a solid block rather than a blur or pixelation. Both of those
            // are reversible to a determined attacker; painting over the pixels is not.
            Commit(RenderOnto(dc =>
                dc.DrawRectangle(new SolidColorBrush(Colors.Black), null, r)));
        }

        // ─────────────────────────────── transforms ───────────────────────────

        private void Rotate(double degrees)
        {
            var t = new TransformedBitmap(_image, new RotateTransform(degrees));
            Commit(Flatten(t));
        }

        private void RotateLeft_Click(object sender, RoutedEventArgs e)  => Rotate(-90);
        private void RotateRight_Click(object sender, RoutedEventArgs e) => Rotate(90);

        private void FlipH_Click(object sender, RoutedEventArgs e)
            => Commit(Flatten(new TransformedBitmap(_image, new ScaleTransform(-1, 1))));

        private void FlipV_Click(object sender, RoutedEventArgs e)
            => Commit(Flatten(new TransformedBitmap(_image, new ScaleTransform(1, -1))));

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_undo.Count == 0) return;
            ClearPreview();
            _redo.Push(_image);
            _image = _undo.Pop();
            InvalidateLayout();
            Refresh();
            if (TextMode) EnterTextMode();
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            if (_redo.Count == 0) return;
            ClearPreview();
            _undo.Push(_image);
            _image = _redo.Pop();
            InvalidateLayout();
            Refresh();
            if (TextMode) EnterTextMode();
        }

        // ─────────────────────────────── opening other images ─────────────────
        //
        // The editor started life as "fix the screenshot you just took", but there's no
        // reason it should only accept those. Anything WPF can decode can be dropped on
        // the window or opened from the toolbar.

        private static readonly string[] ReadableExtensions =
            { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp" };

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open an image",
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico;*.webp"
                       + "|All files|*.*",
                InitialDirectory = Directory.Exists(_settings.SaveFolder)
                    ? _settings.SaveFolder : null
            };

            if (dlg.ShowDialog(this) == true) OpenPath(dlg.FileName);
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DroppedImagePath(e) != null || e.Data.GetDataPresent(DataFormats.Bitmap)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;

            string? path = DroppedImagePath(e);
            if (path != null) { OpenPath(path); return; }

            // Dragging out of a browser or another image app often carries the bitmap
            // itself rather than a file, so accept that too — it just has nowhere to
            // save back to until the user picks a destination.
            if (e.Data.GetDataPresent(DataFormats.Bitmap) &&
                e.Data.GetData(DataFormats.Bitmap) is BitmapSource dropped)
            {
                if (!ConfirmDiscard()) return;
                Adopt(Flatten(dropped), null);
            }
        }

        private static string? DroppedImagePath(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;

            foreach (var f in files)
            {
                string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                if (Array.IndexOf(ReadableExtensions, ext) >= 0 && File.Exists(f)) return f;
            }
            return null;
        }

        private void OpenPath(string path)
        {
            if (!ConfirmDiscard()) return;

            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                // Reading through an explicit stream releases the file immediately, so the
                // editor can later save straight back over the image it opened.
                using (var fs = File.OpenRead(path))
                {
                    img.StreamSource = fs;
                    img.EndInit();
                }
                img.Freeze();
                Adopt(img, path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't open that image:\n" + ex.Message,
                    "Advanced Snip", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>Replaces everything the window is holding with a different picture.</summary>
        private void Adopt(BitmapSource image, string? path)
        {
            _undo.Clear();
            _redo.Clear();
            InvalidateLayout();
            CloseBgPanel();
            ClearPreview();

            _image = image;
            FilePath = path;
            _dirty = false;
            SavedImage = null;

            TextPanel.Visibility = Visibility.Collapsed;
            OcrTextBox.Text = "";

            Title = path == null
                ? "Edit image"
                : "Edit — " + System.IO.Path.GetFileName(path);

            ZoomToFit();
            Refresh();
            if (TextMode) EnterTextMode();

            HintText.Text = path == null
                ? "Dropped image — use Export to save it"
                : "Opened " + System.IO.Path.GetFileName(path);
        }

        /// <summary>
        /// Asks before throwing away edits. Only when there actually are some — prompting
        /// on an untouched image would be noise.
        /// </summary>
        private bool ConfirmDiscard()
        {
            if (!_dirty || _undo.Count == 0) return true;

            var answer = MessageBox.Show(this,
                "This image has unsaved edits. Open the new one anyway?",
                "Advanced Snip", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return answer == MessageBoxResult.Yes;
        }

        // ─────────────────────────────── text selection on the image ──────────
        //
        // The bottom panel gives you everything the image says; this gives you the one
        // line you actually wanted. Words are laid out over the picture in reading order,
        // so dragging between two points selects the run between them the way selecting
        // in a PDF does, rather than picking out a rectangle of unrelated words.

        private OcrLayout? _layout;
        private CancellationTokenSource? _layoutCts;
        private int _selFrom = -1, _selTo = -1;
        private bool _selecting;
        private readonly List<System.Windows.Shapes.Rectangle> _wordBoxes = new();

        /// <summary>Guards against showing boxes recognised from a since-edited picture.</summary>
        private BitmapSource? _layoutFor;

        private bool TextMode => _tool == EditTool.TextSelect;

        private async void EnterTextMode()
        {
            Overlay.Cursor = Cursors.IBeam;

            if (!OcrService.IsAvailable)
            {
                HintText.Text = "No OCR language installed — see Copy text for details";
                return;
            }

            if (_layout != null && ReferenceEquals(_layoutFor, _image))
            {
                LayoutWordBoxes();
                return;
            }

            try { _layoutCts?.Cancel(); } catch { }
            _layoutCts?.Dispose();
            _layoutCts = new CancellationTokenSource();
            var token = _layoutCts.Token;

            ClearWordBoxes();
            HintText.Text = "Finding text…";

            var subject = _image;
            try
            {
                var layout = await OcrService.ReadLayoutAsync(subject, token);
                if (token.IsCancellationRequested) return;

                _layout = layout;
                _layoutFor = subject;
                _selFrom = _selTo = -1;

                if (!layout.Any)
                {
                    HintText.Text = "No text found in this image";
                    return;
                }

                LayoutWordBoxes();
                HintText.Text = $"{layout.Words.Count:N0} words — drag across them to copy";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { HintText.Text = "Couldn't read this image: " + ex.Message; }
        }

        private void ClearWordBoxes()
        {
            TextLayer.Children.Clear();
            _wordBoxes.Clear();
        }

        /// <summary>
        /// Positions a highlight over every word. Rebuilt rather than transformed on zoom
        /// so the outlines stay a constant thickness on screen instead of scaling into
        /// thick slabs when you zoom in.
        /// </summary>
        private void LayoutWordBoxes()
        {
            ClearWordBoxes();
            if (_layout == null || !TextMode) return;

            // A dense page of text is still only a few thousand words; beyond that the
            // highlights stop being useful anyway and the shape count starts to cost.
            int limit = Math.Min(_layout.Words.Count, 4000);

            for (int i = 0; i < limit; i++)
            {
                var box = _layout.Words[i].Box;
                var r = new System.Windows.Shapes.Rectangle
                {
                    Width  = Math.Max(1, box.Width  * _zoom),
                    Height = Math.Max(1, box.Height * _zoom),
                    Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x3B, 0x82, 0xF6)),
                    RadiusX = 2, RadiusY = 2,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(r, box.X * _zoom);
                Canvas.SetTop(r, box.Y * _zoom);
                TextLayer.Children.Add(r);
                _wordBoxes.Add(r);
            }

            PaintSelection();
        }

        private void PaintSelection()
        {
            var plain    = new SolidColorBrush(Color.FromArgb(0x22, 0x3B, 0x82, 0xF6));
            var selected = new SolidColorBrush(Color.FromArgb(0x88, 0x3B, 0x82, 0xF6));

            int lo = Math.Min(_selFrom, _selTo), hi = Math.Max(_selFrom, _selTo);
            for (int i = 0; i < _wordBoxes.Count; i++)
                _wordBoxes[i].Fill = (_selFrom >= 0 && i >= lo && i <= hi) ? selected : plain;
        }

        /// <summary>
        /// Nearest word to a point. Falling back to nearest rather than requiring a direct
        /// hit means the gaps between words don't swallow the drag.
        /// </summary>
        private int WordAt(System.Windows.Point imagePoint)
        {
            if (_layout == null || _layout.Words.Count == 0) return -1;

            int best = -1;
            double bestDistance = double.MaxValue;

            for (int i = 0; i < _layout.Words.Count; i++)
            {
                var b = _layout.Words[i].Box;
                if (b.Contains(imagePoint)) return i;

                double dx = Math.Max(0, Math.Max(b.X - imagePoint.X, imagePoint.X - b.Right));
                double dy = Math.Max(0, Math.Max(b.Y - imagePoint.Y, imagePoint.Y - b.Bottom));
                // Vertical distance counts for more, so a point in the margin picks the
                // word on its own line rather than one above or below.
                double d = dx * dx + dy * dy * 4;
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            return best;
        }

        private void BeginTextSelection(System.Windows.Point overlayPoint)
        {
            int i = WordAt(ToImage(overlayPoint));
            if (i < 0) return;

            _selecting = true;
            _selFrom = _selTo = i;
            Overlay.CaptureMouse();
            PaintSelection();
        }

        private void ExtendTextSelection(System.Windows.Point overlayPoint)
        {
            int i = WordAt(ToImage(overlayPoint));
            if (i < 0 || i == _selTo) return;
            _selTo = i;
            PaintSelection();
        }

        private void EndTextSelection()
        {
            _selecting = false;
            Overlay.ReleaseMouseCapture();
            CopySelectedText(announce: true);
        }

        private void CopySelectedText(bool announce)
        {
            if (_layout == null || _selFrom < 0) return;

            string text = _layout.Join(_selFrom, _selTo).Trim();
            if (text.Length == 0) return;

            TrySetClipboardText(text);
            if (!announce) return;

            string preview = text.Replace('\n', ' ');
            if (preview.Length > 48) preview = preview[..48] + "…";
            HintText.Text = $"Copied “{preview}”";
        }

        /// <summary>Double-click takes the whole line, which is usually what's wanted.</summary>
        private void SelectLineAt(System.Windows.Point overlayPoint)
        {
            if (_layout == null) return;
            int i = WordAt(ToImage(overlayPoint));
            if (i < 0) return;

            int line = _layout.Words[i].Line;
            int from = i, to = i;
            while (from > 0 && _layout.Words[from - 1].Line == line) from--;
            while (to < _layout.Words.Count - 1 && _layout.Words[to + 1].Line == line) to++;

            _selFrom = from; _selTo = to;
            PaintSelection();
            CopySelectedText(announce: true);
        }

        private void InvalidateLayout()
        {
            _layout = null;
            _layoutFor = null;
            _selFrom = _selTo = -1;
            ClearWordBoxes();
        }

        // ─────────────────────────────── background removal ───────────────────

        // The image as it was before the panel opened, so every slider change re-runs
        // from the original rather than eating further into an already-cut result.
        private BitmapSource? _bgOriginal;
        private CancellationTokenSource? _bgCts;
        private readonly List<System.Windows.Point> _bgSeeds = new();

        private bool BgPanelOpen => BgPanel.Visibility == Visibility.Visible;

        private void RemoveBg_Click(object sender, RoutedEventArgs e)
        {
            if (BgPanelOpen) { BgCancel_Click(sender, e); return; }

            _bgOriginal = _image;
            _bgSeeds.Clear();
            BgPanel.Visibility = Visibility.Visible;
            HintText.Text = "Click any leftover background to remove that too";
            RunBackgroundRemoval();
        }

        private void BgSetting_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BgPanel == null || !BgPanelOpen) return;
            RunBackgroundRemoval();
        }

        /// <summary>
        /// Recomputes the preview. Runs off the UI thread and cancels any previous pass,
        /// so dragging a slider stays responsive instead of queueing up whole-image work.
        /// </summary>
        private async void RunBackgroundRemoval()
        {
            if (_bgOriginal == null) return;

            try { _bgCts?.Cancel(); } catch { }
            _bgCts?.Dispose();
            _bgCts = new CancellationTokenSource();
            var token = _bgCts.Token;

            var options = new BackgroundRemovalOptions
            {
                Tolerance = (int)BgTolerance.Value,
                Feather   = (int)BgFeather.Value
            };
            options.ExtraSeeds.AddRange(_bgSeeds);

            var source = _bgOriginal;
            BgStatus.Text = "Working…";

            try
            {
                var result = await Task.Run(
                    () => BackgroundRemover.Remove(source, options), token);

                if (token.IsCancellationRequested) return;

                _image = result.Image;
                Canvas1.Source = _image;
                BgStatus.Text = result.Note;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                BgStatus.Text = "Couldn't process this image: " + ex.Message;
            }
        }

        private void BgApply_Click(object sender, RoutedEventArgs e)
        {
            if (_bgOriginal == null) { CloseBgPanel(); return; }

            // The preview already replaced _image directly, bypassing the undo stack so
            // that dragging a slider doesn't record twenty steps. Committing means putting
            // the pre-removal frame back and going through Commit once.
            var produced = _image;
            _image = _bgOriginal;
            CloseBgPanel();
            Commit(produced);
        }

        private void BgCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_bgOriginal != null) _image = _bgOriginal;
            CloseBgPanel();
            Refresh();
        }

        private void CloseBgPanel()
        {
            try { _bgCts?.Cancel(); } catch { }
            _bgCts?.Dispose();
            _bgCts = null;
            _bgOriginal = null;
            _bgSeeds.Clear();
            BgPanel.Visibility = Visibility.Collapsed;
        }

        // ─────────────────────────────── text recognition ─────────────────────

        private CancellationTokenSource? _ocrCts;

        private async void CopyText_Click(object sender, RoutedEventArgs e)
        {
            if (!OcrService.IsAvailable)
            {
                MessageBox.Show(this, OcrService.UnavailableReason, "Advanced Snip",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try { _ocrCts?.Cancel(); } catch { }
            _ocrCts?.Dispose();
            _ocrCts = new CancellationTokenSource();
            var token = _ocrCts.Token;

            // A pending crop selection doubles as "read just this part", which is usually
            // what you want when a screenshot has one line worth copying.
            BitmapSource subject = _image;
            bool region = _pendingCrop.Width > 1 && _pendingCrop.Height > 1;
            if (region)
            {
                try { subject = Flatten(new CroppedBitmap(_image, _pendingCrop)); }
                catch { region = false; subject = _image; }
            }

            TextPanel.Visibility = Visibility.Visible;
            TextPanelTitle.Text = region ? "Text in the selected area" : "Text found in this image";
            OcrTextBox.Text = "Reading…";
            CopyTextBtn.IsEnabled = false;

            try
            {
                string text = await OcrService.ReadImageAsync(subject, token);
                if (token.IsCancellationRequested) return;

                text = text.Trim();
                if (text.Length == 0)
                {
                    OcrTextBox.Text = "";
                    TextPanelTitle.Text = "No text found in this image";
                    HintText.Text = "Nothing recognised";
                    return;
                }

                OcrTextBox.Text = text;
                TrySetClipboardText(text);
                HintText.Text = $"Copied {text.Length:N0} characters";

                // Feed it back to the gallery's index so the same file doesn't get read
                // twice, and so it becomes searchable immediately.
                if (!region && !string.IsNullOrEmpty(FilePath))
                    (System.Windows.Application.Current as App)?.RememberOcr(FilePath!, text);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OcrTextBox.Text = "Couldn't read this image: " + ex.Message;
            }
            finally
            {
                CopyTextBtn.IsEnabled = true;
            }
        }

        private void TextCopyAll_Click(object sender, RoutedEventArgs e)
        {
            if (OcrTextBox.Text.Length == 0) return;
            TrySetClipboardText(OcrTextBox.Text);
            HintText.Text = "Copied to clipboard";
        }

        private void TextClose_Click(object sender, RoutedEventArgs e)
            => TextPanel.Visibility = Visibility.Collapsed;

        private static void TrySetClipboardText(string text)
        {
            // The clipboard is shared and single-owner; another app can hold it briefly.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { System.Windows.Clipboard.SetText(text); return; }
                catch { Thread.Sleep(60); }
            }
        }

        // ─────────────────────────────── output ───────────────────────────────

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            ClipboardService.SetImage(_image);
            HintText.Text = "Copied to clipboard";
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(FilePath)) { SaveAs_Click(sender, e); return; }
            if (WriteTo(FilePath)) Finish(FilePath);
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            bool jpeg = string.Equals(_settings.ImageFormat, "JPEG", StringComparison.OrdinalIgnoreCase);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG image (transparency)|*.png"
                       + "|JPEG image|*.jpg"
                       + "|Windows icon|*.ico"
                       + "|Bitmap|*.bmp"
                       + "|TIFF image|*.tif",
                FilterIndex = jpeg ? 2 : 1,
                AddExtension = true,
                DefaultExt = jpeg ? "jpg" : "png",
                FileName = FilePath != null
                    ? System.IO.Path.GetFileNameWithoutExtension(FilePath) + "_edited"
                    : $"{_settings.FilenamePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}",
                InitialDirectory = Directory.Exists(_settings.SaveFolder)
                    ? _settings.SaveFolder : null
            };

            if (dlg.ShowDialog(this) != true) return;
            if (WriteTo(dlg.FileName)) { FilePath = dlg.FileName; Finish(dlg.FileName); }
        }

        private bool WriteTo(string path)
        {
            try
            {
                // Always through a temp file, moved into place at the end. Encoding
                // straight over the destination means a failure part-way through leaves
                // the original capture truncated — and this method routinely overwrites
                // the very file the editor was opened from.
                string temp = path + ".tmp";
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

                if (ext == ".ico")
                {
                    IconEncoder.Save(_image, temp);
                }
                else
                {
                    BitmapEncoder encoder = ext switch
                    {
                        ".jpg" or ".jpeg" => new JpegBitmapEncoder
                            { QualityLevel = Math.Clamp(_settings.JpegQuality, 1, 100) },
                        ".bmp"            => new BmpBitmapEncoder(),
                        ".tif" or ".tiff" => new TiffBitmapEncoder(),
                        _                 => new PngBitmapEncoder()
                    };

                    var frame = _image;

                    // JPEG and BMP have no alpha. Silently writing a transparent image to
                    // either yields black where the transparency was, so flatten onto
                    // white first — that's what the user expects to see.
                    if (encoder is JpegBitmapEncoder or BmpBitmapEncoder)
                        frame = FlattenOnto(_image, Colors.White);

                    encoder.Frames.Add(BitmapFrame.Create(frame));
                    using var fs = new FileStream(temp, FileMode.Create, FileAccess.Write);
                    encoder.Save(fs);
                }

                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't save the image:\n" + ex.Message,
                    "Advanced Snip", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        /// <summary>Composites a possibly-transparent image onto a solid colour.</summary>
        private static BitmapSource FlattenOnto(BitmapSource src, Color background)
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, w, h));
                dc.DrawImage(src, new Rect(0, 0, w, h));
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        private void Finish(string path)
        {
            _dirty = false;
            SavedImage = _image;
            Saved?.Invoke(this, new EditorSavedEventArgs(_image, path));
            Close();
        }

        // ─────────────────────────────── keyboard ─────────────────────────────

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (ctrl)
            {
                switch (e.Key)
                {
                    case Key.Z: Undo_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.Y: Redo_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.S: Save_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.C:
                        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                            CopyText_Click(this, new RoutedEventArgs());
                        else if (TextMode && _selFrom >= 0)
                            CopySelectedText(announce: true);      // selection beats image
                        else
                            Copy_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                        return;

                    case Key.O: Open_Click(this, new RoutedEventArgs()); e.Handled = true; return;

                    case Key.A:
                        if (TextMode && _layout != null && _layout.Any)
                        {
                            _selFrom = 0;
                            _selTo = _layout.Words.Count - 1;
                            PaintSelection();
                            CopySelectedText(announce: true);
                            e.Handled = true;
                        }
                        return;
                    case Key.D0:
                    case Key.NumPad0: ZoomToFit(); e.Handled = true; return;
                    case Key.OemPlus:
                    case Key.Add: SetZoom(_zoom * 1.25); e.Handled = true; return;
                    case Key.OemMinus:
                    case Key.Subtract: SetZoom(_zoom / 1.25); e.Handled = true; return;
                }
                return;
            }

            switch (e.Key)
            {
                case Key.Enter:
                    if (_tool == EditTool.Crop) { ApplyPendingCrop(); e.Handled = true; }
                    break;

                case Key.Escape:
                    // First Escape abandons an in-progress selection; a second closes.
                    if (_pendingCrop.Width > 0 || Overlay.Children.Count > 0)
                    { ClearPreview(); Refresh(); }
                    else Close();
                    e.Handled = true;
                    break;

                case Key.C: ToolCrop.IsChecked = true; break;
                case Key.P: ToolPen.IsChecked = true; break;
                case Key.H: ToolMark.IsChecked = true; break;
                case Key.R: ToolRect.IsChecked = true; break;
                case Key.E: ToolEllipse.IsChecked = true; break;
                case Key.A: ToolArrow.IsChecked = true; break;
                case Key.B: ToolRedact.IsChecked = true; break;
                case Key.T: ToolText.IsChecked = true; break;
            }
        }
    }

    public sealed class EditorSavedEventArgs : EventArgs
    {
        public BitmapSource Image { get; }
        public string Path { get; }
        public EditorSavedEventArgs(BitmapSource image, string path) { Image = image; Path = path; }
    }
}
