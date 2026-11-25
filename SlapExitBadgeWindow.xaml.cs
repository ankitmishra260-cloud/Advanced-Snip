using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace AdvancedSnip
{
    public partial class SlapExitBadgeWindow : Window
    {
        private readonly PinnedClipWindow _targetWindow;
        private System.Drawing.Rectangle _targetPhysicalRect;
        private bool _isPositioning;

        public SlapExitBadgeWindow(PinnedClipWindow targetWindow, System.Drawing.Rectangle? initialRect = null)
        {
            _targetWindow = targetWindow ?? throw new ArgumentNullException(nameof(targetWindow));
            _targetPhysicalRect = initialRect ?? System.Drawing.Rectangle.Empty;

            InitializeComponent();
            // Do not set Owner = targetWindow, so WPF does not hide this badge when targetWindow is collapsed
            WindowStartupLocation = WindowStartupLocation.Manual;
            Topmost = false;
            ShowInTaskbar = false;

            Loaded += (_, _) => ApplyPhysicalPosition();
            DpiChanged += (_, _) => ApplyPhysicalPosition();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                int ex = Services.Win32.GetWindowLong(handle, Services.Win32.GWL_EXSTYLE);
                ex |= Services.Win32.WS_EX_TOOLWINDOW | Services.Win32.WS_EX_NOACTIVATE;
                Services.Win32.SetWindowLong(handle, Services.Win32.GWL_EXSTYLE, ex);

                var source = HwndSource.FromHwnd(handle);
                source?.AddHook(WndProc);

                ApplyPhysicalPosition();
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Keep the badge pinned at desktop level (below regular app windows) even after
            // right-clicking its context menu or interacting with it.
            if (msg == Services.Win32.WM_WINDOWPOSCHANGING)
            {
                try
                {
                    var pos = Marshal.PtrToStructure<Services.Win32.WINDOWPOS>(lParam);
                    pos.hwndInsertAfter = Services.Win32.HWND_BOTTOM;
                    Marshal.StructureToPtr(pos, lParam, false);
                }
                catch { }
            }
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
        }

        public void UpdatePosition(System.Drawing.Rectangle? physicalRect = null)
        {
            if (physicalRect.HasValue && !physicalRect.Value.IsEmpty)
            {
                _targetPhysicalRect = physicalRect.Value;
            }
            else if (_targetWindow != null)
            {
                var r = Services.WindowPlacement.GetPhysicalRect(_targetWindow);
                if (!r.IsEmpty)
                {
                    _targetPhysicalRect = r;
                }
                else if (_targetWindow.Record != null && _targetWindow.Record.Width > 0 && _targetWindow.Record.Height > 0)
                {
                    _targetPhysicalRect = new System.Drawing.Rectangle(
                        (int)_targetWindow.Record.X,
                        (int)_targetWindow.Record.Y,
                        (int)_targetWindow.Record.Width,
                        (int)_targetWindow.Record.Height);
                }
            }

            ApplyPhysicalPosition();
        }

        private void ApplyPhysicalPosition()
        {
            if (_isPositioning || _targetPhysicalRect.IsEmpty) return;

            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            _isPositioning = true;
            try
            {
                var rect = _targetPhysicalRect;
                // Resolve the monitor owning the top-right corner of the slapped image
                var monitor = Services.DisplayInfo.FromPoint(rect.Right - 1, rect.Top + 1);
                double scale = monitor.Scale <= 0 ? 1.0 : monitor.Scale;

                const double badgeDip = 36.0;
                const double marginDip = 6.0;

                // Two passes handle crossing a mixed-DPI boundary: pass 1 moves onto the target
                // monitor (letting Windows apply WM_DPICHANGED), pass 2 locks exact physical pixels.
                for (int pass = 0; pass < 2; pass++)
                {
                    int badgePx = Math.Max(24, (int)Math.Round(badgeDip * scale));
                    int marginPx = Math.Max(2, (int)Math.Round(marginDip * scale));

                    int x = rect.Right - badgePx - marginPx;
                    int y = rect.Top + marginPx;

                    // Keep the exit badge reachable inside the monitor bounds even if the image
                    // was dragged partially past a screen edge.
                    int maxX = Math.Max(monitor.Bounds.Left, monitor.Bounds.Right - badgePx);
                    int maxY = Math.Max(monitor.Bounds.Top, monitor.Bounds.Bottom - badgePx);
                    x = Math.Clamp(x, monitor.Bounds.Left, maxX);
                    y = Math.Clamp(y, monitor.Bounds.Top, maxY);

                    Services.Win32.SetWindowPos(handle, Services.Win32.HWND_BOTTOM,
                        x, y, badgePx, badgePx,
                        Services.Win32.SWP_NOACTIVATE);
                }
            }
            finally
            {
                _isPositioning = false;
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            // Snappy tactile feedback squeeze before restoring
            var pressAnim = new DoubleAnimation(0.85, TimeSpan.FromMilliseconds(50))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            pressAnim.Completed += (_, _) => _targetWindow.SetSlappedToDesktop(false);
            BadgeScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pressAnim);
            BadgeScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pressAnim);
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                e.Handled = true;
                _targetWindow.CopyImageToClipboard();
                ToastWindow.Show("Image Copied", "Pinned screenshot copied to clipboard.");
            }
        }

        private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                e.Handled = true;
                _targetWindow.SetSlappedToDesktop(false);
            }
        }

        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            _targetWindow.SetSlappedToDesktop(false);
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            _targetWindow.CopyImageToClipboard();
            ToastWindow.Show("Image Copied", "Pinned screenshot copied to clipboard.");
        }

        private void Opacity100_Click(object sender, RoutedEventArgs e) => _targetWindow.SetImageOpacity(1.0);
        private void Opacity75_Click(object sender, RoutedEventArgs e)  => _targetWindow.SetImageOpacity(0.75);
        private void Opacity50_Click(object sender, RoutedEventArgs e)  => _targetWindow.SetImageOpacity(0.50);
        private void Opacity25_Click(object sender, RoutedEventArgs e)  => _targetWindow.SetImageOpacity(0.25);

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            _targetWindow.SaveAs();
        }

        private void Unpin_Click(object sender, RoutedEventArgs e)
        {
            _targetWindow.Close();
        }

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            // Snappy 90ms transition to full vibrant color
            var opacityAnim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(90))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            BadgeRoot.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

            var scaleAnim = new DoubleAnimation(1.15, TimeSpan.FromMilliseconds(90))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            BadgeScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleAnim);
            BadgeScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleAnim);

            var glowAnim = new DoubleAnimation(0.75, TimeSpan.FromMilliseconds(90));
            GlowEllipse.BeginAnimation(UIElement.OpacityProperty, glowAnim);

            var blurAnim = new DoubleAnimation(12, TimeSpan.FromMilliseconds(90));
            GlowBlur.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            // Smooth 120ms fade back down to faint transparent (0.25)
            var opacityAnim = new DoubleAnimation(0.25, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            BadgeRoot.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

            var scaleAnim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            BadgeScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleAnim);
            BadgeScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleAnim);

            var glowAnim = new DoubleAnimation(0.12, TimeSpan.FromMilliseconds(120));
            GlowEllipse.BeginAnimation(UIElement.OpacityProperty, glowAnim);

            var blurAnim = new DoubleAnimation(6, TimeSpan.FromMilliseconds(120));
            GlowBlur.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);
        }
    }
}
