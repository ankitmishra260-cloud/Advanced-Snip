using System;
using System.Windows;
using System.Windows.Input;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    /// <summary>
    /// Non-intrusive floating countdown badge for timed screenshots.
    /// Excluded from Windows screen captures so it never appears in the resulting image.
    /// Does not steal focus from whatever application the user is preparing to capture.
    /// </summary>
    public partial class SnipCountdownOverlay : Window
    {
        public event Action? SnapNowRequested;
        public event Action? Cancelled;

        public SnipCountdownOverlay()
        {
            InitializeComponent();

            Loaded += (_, _) =>
            {
                WindowPlacement.ExcludeFromCapture(this, true);

                IntPtr handle = WindowPlacement.HandleOf(this);
                if (handle != IntPtr.Zero)
                {
                    int ex = Win32.GetWindowLong(handle, Win32.GWL_EXSTYLE);
                    Win32.SetWindowLong(handle, Win32.GWL_EXSTYLE,
                                        ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
                }

                WindowPlacement.TopCenterOn(this, DisplayInfo.FromCursor(), marginPx: 30);
            };
        }

        public void UpdateCount(int remainingSeconds)
        {
            CountNumber.Text = remainingSeconds.ToString();
            StatusLabel.Text = remainingSeconds == 1 ? "1 second..." : $"{remainingSeconds} seconds...";
        }

        private void Hud_Drag(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }

        private void SnapNow_Click(object sender, RoutedEventArgs e)
        {
            SnapNowRequested?.Invoke();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Cancelled?.Invoke();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Cancelled?.Invoke();
                e.Handled = true;
            }
            else if (e.Key is Key.Enter or Key.Space)
            {
                SnapNowRequested?.Invoke();
                e.Handled = true;
            }
        }
    }
}
