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
            Owner = targetWindow;
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
            }
        }

        public void UpdatePosition(System.Drawing.Rectangle? physicalRect = null)
        {
            if (_targetWindow != null && _targetWindow.IsLoaded && _targetWindow.ActualWidth > 0)
            {
                Left = _targetWindow.Left + _targetWindow.ActualWidth - Width - 6;
                Top = _targetWindow.Top + 6;
                return;
            }

            var rect = physicalRect ?? (_targetWindow != null ? Services.WindowPlacement.GetPhysicalRect(_targetWindow) : System.Drawing.Rectangle.Empty);
            if (!rect.IsEmpty)
            {
                var monitor = Services.DisplayInfo.FromPoint(rect.Right - 1, rect.Top + 1);
                double scale = monitor.Scale <= 0 ? 1.0 : monitor.Scale;

                // Position badge at top-right inside the slapped image
                Left = (rect.Right / scale) - Width - 6;
                Top = (rect.Top / scale) + 6;
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _targetWindow.SetSlappedToDesktop(false);
        }

        private void Restore_Click(object sender, RoutedEventArgs e)
        {
            _targetWindow.SetSlappedToDesktop(false);
        }

        private void Unpin_Click(object sender, RoutedEventArgs e)
        {
            _targetWindow.Close();
        }

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            // Transition from faint transparent (0.25) to full vibrant color (1.0)
            var opacityAnim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            BadgeRoot.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

            var scaleAnim = new DoubleAnimation(1.18, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            BadgeScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleAnim);
            BadgeScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleAnim);

            var glowAnim = new DoubleAnimation(0.75, TimeSpan.FromMilliseconds(180));
            GlowEllipse.BeginAnimation(UIElement.OpacityProperty, glowAnim);

            var blurAnim = new DoubleAnimation(12, TimeSpan.FromMilliseconds(180));
            GlowBlur.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            // Fade back down to faint transparent (0.25)
            var opacityAnim = new DoubleAnimation(0.25, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            BadgeRoot.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

            var scaleAnim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            BadgeScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleAnim);
            BadgeScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleAnim);

            var glowAnim = new DoubleAnimation(0.12, TimeSpan.FromMilliseconds(240));
            GlowEllipse.BeginAnimation(UIElement.OpacityProperty, glowAnim);

            var blurAnim = new DoubleAnimation(6, TimeSpan.FromMilliseconds(240));
            GlowBlur.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);
        }
    }
}
