using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    /// <summary>
    /// An always-on-top, draggable floating window for a pinned screenshot.
    ///
    /// Can be repositioned anywhere on any monitor, zoomed with the mouse wheel,
    /// copied to clipboard, and unpinned. Unpinning simply closes this window
    /// without removing the screenshot from clipboard history.
    /// </summary>
    public partial class PinnedClipWindow : Window
    {
        private readonly BitmapSource _imageSource;
        private readonly string? _filePath;
        private double _zoomFactor = 1.0;
        private readonly DispatcherTimer _feedbackTimer;

        public PinnedClipWindow(BitmapSource image, string? filePath = null, string? caption = null)
        {
            InitializeComponent();
            _imageSource = image;
            _filePath = filePath;

            PinnedImage.Source = image;

            string info = $"{image.PixelWidth} × {image.PixelHeight}";
            if (!string.IsNullOrWhiteSpace(caption))
            {
                info += $"   ·   {caption}";
            }
            DetailText.Text = info;

            ThemeManager.ApplyToWindow(this);

            _feedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
            _feedbackTimer.Tick += (_, _) =>
            {
                CopyStatusText.Text = "";
                _feedbackTimer.Stop();
            };

            Loaded += (_, _) => PositionInitial();
        }

        public static PinnedClipWindow ShowForClip(ClipItem item)
        {
            var win = new PinnedClipWindow(item.GetFullImage(), item.FilePath, item.Caption);
            win.Show();
            return win;
        }

        public static PinnedClipWindow ShowForImage(BitmapSource image, string? filePath = null, string? caption = null)
        {
            var win = new PinnedClipWindow(image, filePath, caption);
            win.Show();
            return win;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var handle = new WindowInteropHelper(this).Handle;
            int ex = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE,
                ex | NativeMethods.WS_EX_TOOLWINDOW);
        }

        private void PositionInitial()
        {
            var monitor = DisplayInfo.FromCursor();
            var area = monitor.WorkArea;

            int imgW = _imageSource.PixelWidth;
            int imgH = _imageSource.PixelHeight;

            if (imgW <= 0 || imgH <= 0) return;

            const int chromeW = 24;
            const int chromeH = 54;

            // Target a neat floating reference size (at most ~480x360 or 40% of work area)
            double maxW = Math.Min(520, area.Width * 0.45);
            double maxH = Math.Min(380, area.Height * 0.45);

            double scale = Math.Min(maxW / imgW, maxH / imgH);
            scale = Math.Min(scale, 1.0); // Don't upscale tiny snips initially

            double targetW = Math.Max(260, (imgW * scale) + chromeW);
            double targetH = Math.Max(160, (imgH * scale) + chromeH);

            Width = targetW;
            Height = targetH;

            // Place near cursor but clamped within work area
            var mouse = System.Windows.Forms.Cursor.Position;
            double left = mouse.X + 24;
            double top = mouse.Y + 24;

            if (left + targetW > area.Right)
                left = Math.Max(area.Left, area.Right - targetW - 20);

            if (top + targetH > area.Bottom)
                top = Math.Max(area.Top, area.Bottom - targetH - 20);

            Left = left;
            Top = top;
        }

        // ── Dragging & Repositioning ──────────────────────────────────────────

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Allow dragging from anywhere except when clicking interactive buttons
            if (e.OriginalSource is DependencyObject dep)
            {
                var btn = FindParentButton(dep);
                if (btn != null) return;
            }

            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private static System.Windows.Controls.Button? FindParentButton(DependencyObject? current)
        {
            while (current != null)
            {
                if (current is System.Windows.Controls.Button btn) return btn;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // ── Unpin (Closes the floating window, keeps item in clipboard) ────────

        private void Unpin_Click(object sender, RoutedEventArgs e)
        {
            // Non-destructive: simply closes this pinned view window.
            // Screenshot remains safely in clipboard and clipboard history.
            Close();
        }

        // ── Copy image ────────────────────────────────────────────────────────

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            ClipboardService.SetImage(_imageSource, _filePath);
            CopyStatusText.Text = "Copied!";
            _feedbackTimer.Stop();
            _feedbackTimer.Start();
        }

        // ── Zoom Controls ─────────────────────────────────────────────────────

        private void SetZoom(double zoom)
        {
            _zoomFactor = Math.Clamp(zoom, 0.25, 4.0);
            ImageScale.ScaleX = _zoomFactor;
            ImageScale.ScaleY = _zoomFactor;

            ZoomText.Text = $"{Math.Round(_zoomFactor * 100):0}%";

            bool zoomed = Math.Abs(_zoomFactor - 1.0) > 0.01;
            ImageScrollViewer.HorizontalScrollBarVisibility = zoomed ? System.Windows.Controls.ScrollBarVisibility.Auto : System.Windows.Controls.ScrollBarVisibility.Disabled;
            ImageScrollViewer.VerticalScrollBarVisibility = zoomed ? System.Windows.Controls.ScrollBarVisibility.Auto : System.Windows.Controls.ScrollBarVisibility.Disabled;
        }

        private void ZoomReset_Click(object sender, RoutedEventArgs e)
            => SetZoom(1.0);

        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            if (e.Delta > 0)
            {
                SetZoom(_zoomFactor + 0.15);
            }
            else if (e.Delta < 0)
            {
                SetZoom(_zoomFactor - 0.15);
            }
        }

        // ── Hotkeys ───────────────────────────────────────────────────────────

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Unpin_Click(sender, e);
                return;
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                switch (e.Key)
                {
                    case Key.C:
                        e.Handled = true;
                        Copy_Click(sender, e);
                        return;

                    case Key.D0:
                    case Key.NumPad0:
                        e.Handled = true;
                        SetZoom(1.0);
                        return;

                    case Key.OemPlus:
                    case Key.Add:
                        e.Handled = true;
                        SetZoom(_zoomFactor + 0.2);
                        return;

                    case Key.OemMinus:
                    case Key.Subtract:
                        e.Handled = true;
                        SetZoom(_zoomFactor - 0.2);
                        return;
                }
            }
        }
    }
}
