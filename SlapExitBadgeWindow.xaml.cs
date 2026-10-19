using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace AdvancedSnip
{
    public partial class SlapExitBadgeWindow : Window
    {
        private readonly PinnedClipWindow _targetWindow;

        public SlapExitBadgeWindow(PinnedClipWindow targetWindow, System.Drawing.Rectangle? initialRect = null)
        {
            _targetWindow = targetWindow ?? throw new ArgumentNullException(nameof(targetWindow));
            InitializeComponent();
            // Do not set Owner = targetWindow, so WPF does not hide this badge when targetWindow is collapsed
            Topmost = false;
            ShowInTaskbar = false;
            UpdatePosition(initialRect);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                int ex = Services.Win32.GetWindowLong(handle, Services.Win32.GWL_EXSTYLE);
                ex |= Services.Win32.WS_EX_TOOLWINDOW | Services.Win32.WS_EX_NOACTIVATE;
                Services.Win32.SetWindowLong(handle, Services.Win32.GWL_EXSTYLE, ex);

                Services.Win32.SetWindowPos(handle, Services.Win32.HWND_BOTTOM, 0, 0, 0, 0,
                    Services.Win32.SWP_NOMOVE | Services.Win32.SWP_NOSIZE | Services.Win32.SWP_NOACTIVATE);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
        }

        public void UpdatePosition(System.Drawing.Rectangle? physicalRect = null)
        {
            var rect = physicalRect ?? (_targetWindow != null ? Services.WindowPlacement.GetPhysicalRect(_targetWindow) : System.Drawing.Rectangle.Empty);
            if (!rect.IsEmpty)
            {
                var monitor = Services.DisplayInfo.FromPoint(rect.Right - 1, rect.Top + 1);
                double scale = monitor.Scale <= 0 ? 1.0 : monitor.Scale;

                // Position badge at top-right inside the slapped image
                Left = (rect.Right / scale) - Width - 6;
                Top = (rect.Top / scale) + 6;
                return;
            }

            if (_targetWindow != null && _targetWindow.IsLoaded && _targetWindow.ActualWidth > 0)
            {
                Left = _targetWindow.Left + _targetWindow.ActualWidth - Width - 6;
                Top = _targetWindow.Top + 6;
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
