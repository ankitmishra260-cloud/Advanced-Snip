using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    /// <summary>
    /// A capture notification, shown on the monitor the pointer is on.
    ///
    /// **Why this exists rather than a tray balloon.** Everything used to go through
    /// <c>NotifyIcon.ShowBalloonTip</c>, and the shell decides where those appear: next to
    /// the notification area, on the primary monitor, always. On a three-monitor desk that
    /// means the confirmation for a capture you just took on the right-hand screen pops up
    /// somewhere you are not looking. There is no API to move a shell balloon — the only way
    /// to put the message where the user is, is to stop asking the shell to draw it.
    ///
    /// Toasts stack upward from the bottom-right of whichever display holds the cursor at
    /// the moment they appear. They do not follow the pointer afterwards, unlike the
    /// clipboard HUD: the HUD is something you are actively working in, while a toast is a
    /// receipt that is on its way out. Chasing the mouse with a message that is already
    /// fading would be motion for its own sake.
    /// </summary>
    public partial class ToastWindow : Window
    {
        /// <summary>Live toasts, newest last. Static because they have to stack against each other.</summary>
        private static readonly List<ToastWindow> Live = new();

        private const double Gap = 10;
        private const int DefaultSeconds = 4;

        private readonly DispatcherTimer _life;
        private Action? _onClick;
        private bool _closing;

        private ToastWindow()
        {
            InitializeComponent();

            _life = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _life.Tick += (_, _) => Dismiss();

            // Nothing here should ever take focus — a receipt that steals the caret out of
            // whatever you were typing into is worse than no receipt.
            Loaded += (_, _) =>
            {
                var handle = WindowPlacement.HandleOf(this);
                if (handle == IntPtr.Zero) return;

                int ex = Win32.GetWindowLong(handle, Win32.GWL_EXSTYLE);
                Win32.SetWindowLong(handle, Win32.GWL_EXSTYLE,
                                    ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
            };
        }

        /// <summary>
        /// Shows a toast on the cursor's display.
        /// </summary>
        /// <param name="onClick">Run when the toast is clicked — usually "open what I just made".</param>
        internal static void Show(string title, string? body = null,
                                  BitmapSource? thumbnail = null,
                                  Action? onClick = null,
                                  int seconds = DefaultSeconds)
        {
            if (Application.Current == null) return;

            try
            {
                var toast = new ToastWindow();
                toast.TitleText.Text = title;

                if (!string.IsNullOrWhiteSpace(body))
                {
                    toast.BodyText.Text = body;
                    toast.BodyText.Visibility = Visibility.Visible;
                }

                if (thumbnail != null)
                {
                    toast.ThumbImage.Source = thumbnail;
                    toast.ThumbFrame.Visibility = Visibility.Visible;
                }

                toast._onClick = onClick;
                toast.Cursor = onClick != null ? Cursors.Hand : Cursors.Arrow;
                toast._life.Interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30));

                // Four at a time. Beyond that they crawl up past the top of the screen, and
                // a stack of receipts nobody can read is just an obstruction.
                while (Live.Count >= 4) Live[0].Dismiss();

                var monitor = DisplayInfo.FromCursor();
                var area = monitor.WorkArea.Width > 0 ? monitor.WorkArea : monitor.Bounds;
                double scale = monitor.Scale <= 0 ? 1 : monitor.Scale;

                // Position on target monitor initially so WPF creates the window with the correct DPI
                toast.Left = area.X / scale;
                toast.Top = area.Y / scale;

                toast.Loaded += (_, _) => Restack();
                toast.SizeChanged += (_, _) => Restack();

                Live.Add(toast);
                toast.Show();

                Restack();

                toast._life.Start();
                toast.Fade(0, 1, 140);
            }
            catch { /* a notification that fails must never take the capture down with it */ }
        }

        /// <summary>Lays the live toasts up from the bottom-right of the cursor's monitor.</summary>
        private static void Restack()
        {
            if (Live.Count == 0) return;

            var monitor = DisplayInfo.FromCursor();
            var area = monitor.WorkArea.Width > 0 ? monitor.WorkArea : monitor.Bounds;
            double scale = monitor.Scale <= 0 ? 1 : monitor.Scale;

            // Physical-pixel margin, scaled so it stays the same visual size at any DPI.
            int gap = (int)Math.Round(Gap * scale);
            int y = area.Bottom - gap;

            for (int i = Live.Count - 1; i >= 0; i--)
            {
                var toast = Live[i];
                if (toast._closing) continue;

                var rect = WindowPlacement.GetPhysicalRect(toast);
                int w = rect.Width;
                int h = rect.Height;

                if (w <= 0)
                {
                    double dipW = toast.ActualWidth > 0 ? toast.ActualWidth : 360;
                    w = (int)Math.Ceiling(dipW * scale);
                }
                if (h <= 0)
                {
                    double dipH = toast.ActualHeight > 0 ? toast.ActualHeight : 80;
                    h = (int)Math.Ceiling(dipH * scale);
                }

                int posX = area.Right - w - gap;
                int posY = y - h;

                // Explicit clamp to ensure toast stays strictly within this monitor's work area
                posX = Math.Max(area.X + gap, Math.Min(posX, area.Right - w - gap));
                posY = Math.Max(area.Y + gap, Math.Min(posY, area.Bottom - h - gap));

                WindowPlacement.MoveTo(toast, posX, posY);
                y = posY - gap;
            }
        }

        private void Fade(double from, double to, int ms, Action? then = null)
        {
            var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            if (then != null) animation.Completed += (_, _) => then();
            BeginAnimation(OpacityProperty, animation);
        }

        private void Dismiss()
        {
            if (_closing) return;
            _closing = true;

            _life.Stop();
            Live.Remove(this);

            Fade(Opacity, 0, 160, () =>
            {
                try { Close(); } catch { }
                Restack();
            });
        }

        private void Toast_Click(object sender, MouseButtonEventArgs e)
        {
            var action = _onClick;
            Dismiss();

            // After Dismiss, so a handler that opens a window does not race the fade.
            try { action?.Invoke(); } catch { }
        }
    }
}
