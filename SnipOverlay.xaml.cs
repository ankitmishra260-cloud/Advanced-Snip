using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    /// <summary>
    /// The region selector: a frozen full-desktop snapshot you drag a rectangle on.
    ///
    /// Thanks to <see cref="OverlayHost"/> one layout unit in here is exactly one physical
    /// screen pixel on every display, so the crop is pixel-accurate even when a 150%
    /// laptop panel sits next to a 100% external monitor — the previous version scaled
    /// everything by the primary display's factor and drifted on the others.
    ///
    /// A click without dragging grabs whatever window is under the pointer, and a
    /// magnifier follows the cursor so edges can be placed exactly.
    /// </summary>
    public partial class SnipOverlay : Window
    {
        private readonly System.Drawing.Bitmap _full;
        private readonly System.Drawing.Rectangle _virtualScreen;
        private BitmapSource? _frozen;
        private long _frozenBytes;
        private readonly byte _dimAlpha;

        // The dim layer and the magnifier are both rebuilt on every single mouse move.
        // Allocating fresh brushes, geometries and bitmaps at that rate produced tens of
        // thousands of short-lived objects during one drag across a 4K screen — enough
        // gen-0 pressure to make the selection rectangle visibly lag the cursor. Both are
        // now built once and mutated in place.
        private readonly SolidColorBrush _dimBrush;
        private readonly RectangleGeometry _dimOuter = new();
        private readonly RectangleGeometry _dimHole = new();
        private readonly GeometryGroup _dimGroup;

        private WriteableBitmap? _loupeBuffer;
        private byte[]? _loupePixels;
        private int _loupeStride;
        private bool _loupeFastPath = true;

        private Point _start;
        private bool _dragging;
        private System.Drawing.Rectangle _hoverWindow = System.Drawing.Rectangle.Empty;

        // One overlay unit is one physical pixel, which is what makes the crop exact — but
        // it also means every piece of furniture drawn on the overlay is measured in
        // pixels. Beside a 100% monitor, a 125% laptop panel gets a magnifier and a hint
        // bar four fifths the size they are next door; at 300%, a third. The geometry has
        // to stay in pixels, so the furniture is scaled to whichever display the pointer
        // is on instead.
        private double _uiScale = 1.0;
        private System.Drawing.Rectangle _uiMonitor;

        /// <summary>The cropped image, or null when cancelled.</summary>
        public System.Drawing.Bitmap? ResultBitmap { get; private set; }

        /// <summary>
        /// When set, the overlay reports the rectangle instead of cropping it. The screen
        /// recorder wants a region, not a picture, and reusing this window means region
        /// recording gets the window highlighting, the magnifier and the pixel-exact
        /// multi-monitor arithmetic for free rather than growing a second implementation
        /// that drifts away from this one.
        /// </summary>
        public bool SelectionOnly { get; set; }

        /// <summary>
        /// When true, styles hints and selection frame specifically for Real-Time OCR Text Reader.
        /// </summary>
        public bool IsOcrMode { get; set; }

        /// <summary>The chosen rectangle in virtual-desktop pixels. Empty when cancelled.</summary>
        public System.Drawing.Rectangle SelectedRegion { get; private set; }

        public SnipOverlay(System.Drawing.Bitmap fullScreen, int overlayOpacity = 55)
        {
            InitializeComponent();

            _full = fullScreen;
            _virtualScreen = DisplayInfo.VirtualScreen;
            _dimAlpha = (byte)Math.Clamp((int)(overlayOpacity / 100.0 * 255), 30, 240);

            _frozen = ImageInterop.ToFrozenBitmapSourceAuto(_full);
            // A full-desktop grab is 30-50 MB of pixels behind a managed object the
            // collector thinks weighs nothing. Telling it the truth is what stops three
            // snips in a row from stacking up uncollected.
            _frozenBytes = MemoryGuard.Track(_frozen);
            ScreenImage.Source = _frozen;

            _dimBrush = new SolidColorBrush(Color.FromArgb(_dimAlpha, 0, 0, 0));
            _dimBrush.Freeze();
            _dimGroup = new GeometryGroup { FillRule = FillRule.EvenOdd };
            _dimGroup.Children.Add(_dimOuter);
            _dimGroup.Children.Add(_dimHole);
            DimPath.Fill = _dimBrush;
            DimPath.Data = _dimGroup;

            OverlayHost.MakeVirtualScreenOverlay(this, RootGrid, line =>
            {
                // The grab's own dimensions, which nothing has checked until now. The
                // backdrop is drawn with Stretch="Fill" into a grid sized to the virtual
                // desktop, so if the grab came back smaller it is silently magnified to
                // fit — visually identical to the overlay rendering at the wrong scale,
                // and the reason three fixes aimed at the overlay changed nothing. If
                // "bmp" and "desktop" disagree, the fault is in the capture, not the
                // overlay.
                DiagText.Text = line + $"  ·  bmp {_full.Width}×{_full.Height}";
                HintBar.UpdateLayout();      // the bar just changed width
                PositionHintBar();
            });

            Loaded += (_, _) =>
            {
                if (IsOcrMode)
                {
                    HintText.Text = "🔤 Select text on screen to extract & copy  ·  Click a window  ·  Esc to cancel";
                    SelBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(99, 102, 241));
                }
                Activate();
                Focus();
                RootGrid.Focus();
                UpdateUiScale();
                RedrawDim(null);
                UpdateHover();
                PositionHintBar();
            };

            KeyDown += OnKeyDown;
        }

        // ── input ────────────────────────────────────────────────────────────

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Cancel();
        }

        private void Root_Cancel(object sender, MouseButtonEventArgs e) => Cancel();

        private void Root_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _start = e.GetPosition(RootGrid);
            _dragging = true;
            RootGrid.CaptureMouse();

            SelBorder.Visibility = Visibility.Visible;
            SizeTag.Visibility = Visibility.Visible;
            UpdateSelection(_start, _start);
        }

        private void Root_MouseMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(RootGrid);
            UpdateUiScale();

            if (_dragging)
            {
                UpdateSelection(_start, p);
            }
            else
            {
                UpdateHover();
                PositionHintBar();
            }

            UpdateLoupe(p);
        }

        private void Root_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            RootGrid.ReleaseMouseCapture();

            var end = e.GetPosition(RootGrid);
            var rect = MakeRect(_start, end);

            // A click rather than a drag means "grab the window under the pointer".
            if (rect.Width < 4 || rect.Height < 4)
            {
                if (_hoverWindow.Width > 0 && _hoverWindow.Height > 0)
                {
                    CropAndClose(OverlayHost.ToLocalRect(_hoverWindow));
                    return;
                }
                Cancel();
                return;
            }

            CropAndClose(rect);
        }

        /// <summary>
        /// Re-points the overlay furniture at the display under the cursor. Cheap enough
        /// for a mouse-move handler: one monitor lookup, and everything past the scale
        /// comparison is skipped unless the pointer has actually crossed onto a display
        /// with a different scale factor.
        /// </summary>
        private void UpdateUiScale()
        {
            var mon = DisplayInfo.FromCursor();
            _uiMonitor = mon.Bounds;

            double s = mon.Scale;
            if (double.IsNaN(s) || s < 0.05 || s > 20) s = 1.0;
            if (Math.Abs(s - _uiScale) < 0.001) return;

            _uiScale = s;
            OverlayHost.ScaleChrome(HintBar, s);
            OverlayHost.ScaleChrome(SizeTag, s);
            OverlayHost.ScaleChrome(Loupe, s);

            // The selection outline is geometry and is deliberately not scaled — it has to
            // sit on the pixels it is going to cut. Only its weight follows the display,
            // because a 2px line on a 300% panel is a hairline.
            SelBorder.BorderThickness = new Thickness(Math.Max(1, Math.Round(2 * s)));
        }

        /// <summary>The monitor the furniture is currently sized for, in overlay units.</summary>
        private Rect ChromeMonitor() => OverlayHost.ToLocalRect(
            _uiMonitor.Width > 0 ? _uiMonitor : DisplayInfo.FromCursor().Bounds);

        // ── hover highlight ──────────────────────────────────────────────────

        private void UpdateHover()
        {
            if (!Win32.GetCursorPos(out var cur)) return;

            IntPtr top = WindowFinder.HitTestTopLevel(cur.X, cur.Y);
            var rect = top != IntPtr.Zero
                ? DisplayInfo.ClampToDesktop(WindowFinder.WindowRect(top))
                : System.Drawing.Rectangle.Empty;

            if (rect == _hoverWindow) return;
            _hoverWindow = rect;

            if (rect.Width <= 0)
            {
                SelBorder.Visibility = Visibility.Collapsed;
                SizeTag.Visibility = Visibility.Collapsed;
                RedrawDim(null);
                return;
            }

            var local = OverlayHost.ToLocalRect(rect);
            ShowRect(local, $"{rect.Width} × {rect.Height} px  ·  click to capture window");
        }

        // ── selection rendering ──────────────────────────────────────────────

        private void UpdateSelection(Point a, Point b)
        {
            var rect = MakeRect(a, b);
            // One unit is one physical pixel, so the readout needs no conversion.
            string tag = IsOcrMode
                ? $"{(int)Math.Round(rect.Width)} × {(int)Math.Round(rect.Height)} px · OCR"
                : $"{(int)Math.Round(rect.Width)} × {(int)Math.Round(rect.Height)} px";
            ShowRect(rect, tag);
        }

        private void ShowRect(Rect rect, string label)
        {
            Canvas.SetLeft(SelBorder, rect.X);
            Canvas.SetTop(SelBorder, rect.Y);
            SelBorder.Width = rect.Width;
            SelBorder.Height = rect.Height;
            SelBorder.Visibility = Visibility.Visible;

            RedrawDim(rect);

            SizeText.Text = label;
            SizeTag.Visibility = Visibility.Visible;
            SizeTag.UpdateLayout();

            // ActualHeight is in overlay units; the tag is drawn through a RenderTransform,
            // so its height on screen is that times the display's scale.
            double tagH = (SizeTag.ActualHeight > 0 ? SizeTag.ActualHeight : 24) * _uiScale;
            double tagGap = 6 * _uiScale;
            double tagY = rect.Y - tagH - tagGap >= 0 ? rect.Y - tagH - tagGap : rect.Y + tagGap;
            Canvas.SetLeft(SizeTag, rect.X);
            Canvas.SetTop(SizeTag, tagY);
        }

        /// <summary>
        /// Updates the two rectangles the dim layer is made of. Nothing is allocated: the
        /// brush is frozen and shared, and the geometries were created in the constructor,
        /// so a mouse move costs two property writes and a repaint of one Path.
        /// </summary>
        private void RedrawDim(Rect? hole)
        {
            _dimOuter.Rect = new Rect(0, 0, RootGrid.Width, RootGrid.Height);
            _dimHole.Rect = hole is { Width: > 0, Height: > 0 } h ? h : Rect.Empty;
        }

        // ── magnifier ────────────────────────────────────────────────────────

        private void UpdateLoupe(Point local)
        {
            const int srcW = 30, srcH = 22;

            int cx = (int)Math.Round(local.X);
            int cy = (int)Math.Round(local.Y);

            int x = Math.Clamp(cx - srcW / 2, 0, Math.Max(0, _full.Width - srcW));
            int y = Math.Clamp(cy - srcH / 2, 0, Math.Max(0, _full.Height - srcH));

            if (_full.Width < srcW || _full.Height < srcH)
            {
                Loupe.Visibility = Visibility.Collapsed;
                return;
            }

            if (!UpdateLoupePixels(x, y, srcW, srcH))
            {
                Loupe.Visibility = Visibility.Collapsed;
                return;
            }

            LoupeText.Text = $"{cx + _virtualScreen.X}, {cy + _virtualScreen.Y}";
            Loupe.Visibility = Visibility.Visible;

            // Keep the loupe on the pointer's own monitor and out from under the cursor.
            // Its size on screen is its layout size times the display's scale, so every
            // distance in here is scaled to match — otherwise it would tuck itself into a
            // corner using clearances a third of the size of the thing being placed.
            var monLocal = ChromeMonitor();
            double s = _uiScale;

            double lw = Loupe.Width * s, lh = Loupe.Height * s;
            double lx = local.X + 26 * s;
            double ly = local.Y + 26 * s;

            if (lx + lw > monLocal.Right - 6 * s) lx = local.X - lw - 22 * s;
            if (ly + lh > monLocal.Bottom - 6 * s) ly = local.Y - lh - 22 * s;

            Canvas.SetLeft(Loupe, Math.Max(monLocal.Left + 6 * s, lx));
            Canvas.SetTop(Loupe, Math.Max(monLocal.Top + 6 * s, ly));
        }

        /// <summary>
        /// Copies the magnified region into a single reusable bitmap.
        ///
        /// The previous version built a <c>CroppedBitmap</c> for every mouse move. Each
        /// one is a live WIC object holding a reference back to the full-desktop image,
        /// and the pointer generates them faster than the finalizer thread retires them,
        /// so a few seconds of aiming left a long tail of them behind. One writeable
        /// buffer, written in place, allocates nothing after the first move.
        /// </summary>
        private bool UpdateLoupePixels(int x, int y, int srcW, int srcH)
        {
            var frozen = _frozen;
            if (frozen == null) return false;

            if (_loupeFastPath)
            {
                try
                {
                    if (_loupeBuffer == null || _loupePixels == null)
                    {
                        // Only 32-bit formats can be copied straight across. Anything else
                        // falls back to the old path rather than corrupting the view.
                        if (frozen.Format.BitsPerPixel != 32) throw new NotSupportedException();

                        _loupeStride = srcW * 4;
                        _loupePixels = new byte[_loupeStride * srcH];
                        _loupeBuffer = new WriteableBitmap(srcW, srcH, 96, 96, frozen.Format, null);
                    }

                    // Locals, so the compiler can see these are non-null across the calls.
                    var buffer = _loupeBuffer;
                    var pixels = _loupePixels;

                    frozen.CopyPixels(new Int32Rect(x, y, srcW, srcH), pixels, _loupeStride, 0);
                    buffer.WritePixels(new Int32Rect(0, 0, srcW, srcH), pixels, _loupeStride, 0);

                    if (!ReferenceEquals(LoupeImage.Source, buffer))
                        LoupeImage.Source = buffer;
                    return true;
                }
                catch
                {
                    _loupeFastPath = false;
                    _loupeBuffer = null;
                    _loupePixels = null;
                }
            }

            try
            {
                LoupeImage.Source = new CroppedBitmap(frozen, new Int32Rect(x, y, srcW, srcH));
                return true;
            }
            catch { return false; }
        }

        private void PositionHintBar()
        {
            var local = ChromeMonitor();
            double drawnWidth = HintBar.ActualWidth * _uiScale;
            HintBar.Margin = new Thickness(
                local.Left + Math.Max(0, (local.Width - drawnWidth) / 2),
                local.Top + 28 * _uiScale, 0, 0);
        }

        // ── commit ───────────────────────────────────────────────────────────

        private void CropAndClose(Rect rect)
        {
            // rect is already in physical pixels relative to the virtual screen origin,
            // which is exactly the coordinate space of the captured bitmap.
            int px = (int)Math.Round(rect.X);
            int py = (int)Math.Round(rect.Y);
            int pw = (int)Math.Round(rect.Width);
            int ph = (int)Math.Round(rect.Height);

            px = Math.Clamp(px, 0, Math.Max(0, _full.Width - 1));
            py = Math.Clamp(py, 0, Math.Max(0, _full.Height - 1));
            pw = Math.Clamp(pw, 1, _full.Width - px);
            ph = Math.Clamp(ph, 1, _full.Height - py);

            SelectedRegion = new System.Drawing.Rectangle(
                px + _virtualScreen.X, py + _virtualScreen.Y, pw, ph);

            if (SelectionOnly)
            {
                ResultBitmap = null;
                DialogResult = true;
                return;
            }

            var crop = new System.Drawing.Bitmap(pw, ph,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            using (var g = System.Drawing.Graphics.FromImage(crop))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(_full,
                    new System.Drawing.Rectangle(0, 0, pw, ph),
                    new System.Drawing.Rectangle(px, py, pw, ph),
                    System.Drawing.GraphicsUnit.Pixel);
            }

            ResultBitmap = crop;
            DialogResult = true;
        }

        private void Cancel()
        {
            ResultBitmap = null;
            DialogResult = false;
        }

        /// <summary>
        /// Drops everything the window was holding the moment it closes.
        ///
        /// WPF keeps a rendered copy of whatever an <c>Image</c> element is showing, and a
        /// full-screen element means a full-screen copy. Closing a window does not by
        /// itself release that — the visual tree is only torn down when the window object
        /// is finally collected, which for a 50 MB backdrop the collector has no reason to
        /// hurry along. Clearing the sources here makes it immediate.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            ScreenImage.Source = null;
            LoupeImage.Source = null;
            DimPath.Data = null;
            DimPath.Fill = null;

            _frozen = null;
            _loupeBuffer = null;
            _loupePixels = null;

            MemoryGuard.Release(_frozenBytes);
            _frozenBytes = 0;
            MemoryGuard.RequestTrim();
        }

        private static Rect MakeRect(Point a, Point b) => new(
            Math.Min(a.X, b.X),
            Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X),
            Math.Abs(a.Y - b.Y));
    }
}
