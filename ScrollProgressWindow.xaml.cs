using System;
using System.Drawing;
using System.Threading;
using System.Windows;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    /// <summary>
    /// Shows what the scroll capture is doing, and stays out of its way.
    ///
    /// Two things matter here beyond the progress bar. First, this window must never end
    /// up inside the captured image: it asks Windows to exclude it from screen capture and
    /// also parks itself on a different monitor (or the emptiest corner) from the region
    /// being captured. Second, "Stop &amp; keep" is separate from "Cancel" — on an
    /// infinite-scrolling page you want to end the run and still keep what was collected.
    /// </summary>
    public partial class ScrollProgressWindow : Window
    {
        private readonly CancellationTokenSource _cts = new();

        public CancellationToken CancellationToken => _cts.Token;
        public StopSignal Stop { get; } = new();

        public ScrollProgressWindow(string windowTitle)
        {
            InitializeComponent();
            TitleText.Text = Truncate(windowTitle, 58);
            TitleText.ToolTip = windowTitle;

            SourceInitialized += (_, _) =>
            {
                WindowPlacement.ExcludeFromCapture(this);

                IntPtr h = WindowPlacement.HandleOf(this);
                if (h != IntPtr.Zero)
                {
                    int ex = Win32.GetWindowLong(h, Win32.GWL_EXSTYLE);
                    Win32.SetWindowLong(h, Win32.GWL_EXSTYLE,
                        ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
                }
            };
        }

        /// <summary>Moves this window well clear of the area about to be captured.</summary>
        public void KeepClearOf(Rectangle capturedRegion)
        {
            UpdateLayout();
            WindowPlacement.PlaceAwayFrom(this, capturedRegion);
        }

        public void Update(ScrollCaptureProgress p)
        {
            Bar.Value = Math.Clamp(p.Percent, 0, 100);
            StatusText.Text = p.Message;

            StatsText.Text = p.Frames > 0
                ? $"{p.Frames} frame{(p.Frames == 1 ? "" : "s")}  ·  {p.Height:N0} px tall"
                : "";

            if (p.Preview != null)
                PreviewImage.Source = p.Preview;
        }

        public void ShowFinished(string message)
        {
            Bar.Value = 100;
            StatusText.Text = message;
            StopBtn.IsEnabled = false;
            CancelBtn.Content = "Close";
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            Stop.Request();
            StopBtn.IsEnabled = false;
            StatusText.Text = "Finishing up with what's been captured so far\u2026";
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            _cts.Cancel();
            CancelBtn.IsEnabled = false;
            StopBtn.IsEnabled = false;
            StatusText.Text = "Cancelling\u2026";
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
            base.OnClosed(e);
        }

        private static string Truncate(string t, int max)
            => string.IsNullOrEmpty(t) ? "(untitled window)"
             : t.Length > max ? t[..(max - 1)] + "\u2026" : t;
    }
}
