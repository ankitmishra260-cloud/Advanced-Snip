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
        private readonly BitmapSource _frozen;
        private readonly byte _dimAlpha;

        private Point _start;
        private bool _dragging;
        private System.Drawing.Rectangle _hoverWindow = System.Drawing.Rectangle.Empty;

        /// <summary>The cropped image, or null when cancelled.</summary>
        public System.Drawing.Bitmap? ResultBitmap { get; private set; }

        public SnipOverlay(System.Drawing.Bitmap fullScreen, int overlayOpacity = 55)
        {
            InitializeComponent();

            _full = fullScreen;
            _virtualScreen = DisplayInfo.VirtualScreen;
            _dimAlpha = (byte)Math.Clamp((int)(overlayOpacity / 100.0 * 255), 30, 240);

            _frozen = ImageInterop.ToFrozenBitmapSourceFast(_full);
            ScreenImage.Source = _frozen;

            OverlayHost.MakeVirtualScreenOverlay(this, RootGrid);

            Loaded += (_, _) =>
            {
                Activate();
                Focus();
                RootGrid.Focus();
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
            ShowRect(rect, $"{(int)Math.Round(rect.Width)} × {(int)Math.Round(rect.Height)} px");
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

            double tagH = SizeTag.ActualHeight > 0 ? SizeTag.ActualHeight : 24;
            double tagY = rect.Y - tagH - 6 >= 0 ? rect.Y - tagH - 6 : rect.Y + 6;
            Canvas.SetLeft(SizeTag, rect.X);
            Canvas.SetTop(SizeTag, tagY);
        }

        private void RedrawDim(Rect? hole)
        {
            DimPath.Fill = new SolidColorBrush(Color.FromArgb(_dimAlpha, 0, 0, 0));

            var full = new RectangleGeometry(new Rect(0, 0, RootGrid.Width, RootGrid.Height));

            if (hole is { Width: > 0, Height: > 0 } h)
            {
                var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
                group.Children.Add(full);
                group.Children.Add(new RectangleGeometry(h));
                DimPath.Data = group;
            }
            else
            {
                DimPath.Data = full;
            }
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

            try
            {
                LoupeImage.Source = new CroppedBitmap(_frozen, new Int32Rect(x, y, srcW, srcH));
            }
            catch
            {
                Loupe.Visibility = Visibility.Collapsed;
                return;
            }

            LoupeText.Text = $"{cx + _virtualScreen.X}, {cy + _virtualScreen.Y}";
            Loupe.Visibility = Visibility.Visible;

            // Keep the loupe on the pointer's own monitor and out from under the cursor.
            var mon = DisplayInfo.FromPoint(cx + _virtualScreen.X, cy + _virtualScreen.Y);
            var monLocal = OverlayHost.ToLocalRect(mon.Bounds);

            double lw = Loupe.Width, lh = Loupe.Height;
            double lx = local.X + 26;
            double ly = local.Y + 26;

            if (lx + lw > monLocal.Right - 6) lx = local.X - lw - 22;
            if (ly + lh > monLocal.Bottom - 6) ly = local.Y - lh - 22;

            Canvas.SetLeft(Loupe, Math.Max(monLocal.Left + 6, lx));
            Canvas.SetTop(Loupe, Math.Max(monLocal.Top + 6, ly));
        }

        private void PositionHintBar()
        {
            var mon = DisplayInfo.FromCursor();
            var local = OverlayHost.ToLocalRect(mon.Bounds);
            HintBar.Margin = new Thickness(
                local.Left + Math.Max(0, (local.Width - HintBar.ActualWidth) / 2),
                local.Top + 28, 0, 0);
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

        private static Rect MakeRect(Point a, Point b) => new(
            Math.Min(a.X, b.X),
            Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X),
            Math.Abs(a.Y - b.Y));
    }
}
