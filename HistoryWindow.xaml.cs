using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    public partial class HistoryWindow : Window
    {
        private readonly ClipboardHistory _history;
        private bool _refreshing;
        private bool _activated; // guard against instant-hide on first Deactivated

        // Wheel notches arrive as multiples of 120, but precision wheels and touchpads
        // send fractions of one. Accumulating means a fine-grained device advances one
        // image per notch's worth of travel instead of racing through the whole history.
        private int _wheelTravel;

        // Putting an image on the clipboard costs a full PNG encode, which for a tall
        // scroll capture is not cheap. Selecting used to do that on every single step, so
        // spinning the wheel would have queued one encode per thumbnail passed. The
        // clipboard now follows a short pause instead, and anything that actually needs
        // the image on the clipboard flushes first.
        private readonly DispatcherTimer _clipboardDelay;

        /// <summary>Set by App just before ShowAndActivate so paste knows the target.</summary>
        public IntPtr PasteTarget { get; set; }

        public HistoryWindow(ClipboardHistory history)
        {
            InitializeComponent();
            _history = history;
            _history.Changed += (_, _) => Dispatcher.Invoke(RefreshItems);

            // Only auto-hide on deactivation *after* the window has been fully activated
            // at least once — prevents the instant-dismiss race on open.
            Activated   += (_, _) => _activated = true;
            Deactivated += (_, _) => { if (_activated) HideHud(); };

            _clipboardDelay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(180)
            };
            _clipboardDelay.Tick += (_, _) => FlushClipboard();

            // Safety net for the paths that hide the window without going through
            // HideHud — the hotkey toggling it shut, for instance. Whatever the user
            // last landed on still ends up on the clipboard.
            IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is false) FlushClipboard();
            };
        }

        /// <summary>
        /// Writes the pending selection to the clipboard now. Does nothing when nothing is
        /// pending, so it's safe to call from every path that might close the HUD.
        /// </summary>
        private void FlushClipboard()
        {
            if (!_clipboardDelay.IsEnabled) return;
            _clipboardDelay.Stop();
            if (_history.Current is { } item) ClipboardService.SetImage(item.Image);
        }

        /// <summary>
        /// Closes the HUD without losing the selection the user landed on — leaving with
        /// the clipboard still holding the previous image would be worse than the delay
        /// it's meant to avoid.
        /// </summary>
        private void HideHud()
        {
            FlushClipboard();
            Hide();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // Keep the HUD out of Alt+Tab.
            var handle = new WindowInteropHelper(this).Handle;
            int ex = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE,
                ex | NativeMethods.WS_EX_TOOLWINDOW);
        }

        // ---------------------------------------------------------------- show/hide

        public void ShowAndActivate()
        {
            _activated = false; // reset so the guard works for this new show cycle
            _wheelTravel = 0;
            RefreshItems();
            Show();
            UpdateLayout();
            PositionBottomCenter();
            Activate();
            Thumbs.Focus();
        }

        /// <summary>
        /// Puts the HUD near the bottom of the display the user is actually working on.
        /// SystemParameters.WorkArea only ever describes the primary monitor, so the old
        /// version made the popup appear on the wrong screen whenever you were working on
        /// a secondary display.
        /// </summary>
        private void PositionBottomCenter()
        {
            var monitor = PasteTarget != IntPtr.Zero
                ? DisplayInfo.FromWindow(PasteTarget)
                : DisplayInfo.FromCursor();

            WindowPlacement.BottomCenterOn(this, monitor);
        }

        // ---------------------------------------------------------------- data

        public void RefreshItems()
        {
            _refreshing = true;
            var items = _history.Items.ToList();
            Thumbs.ItemsSource = items;

            bool any = items.Count > 0;
            Thumbs.Visibility  = any ? Visibility.Visible  : Visibility.Collapsed;
            EmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;

            if (any)
            {
                int idx = Math.Clamp(_history.Index, 0, items.Count - 1);
                Thumbs.SelectedIndex = idx;
                Thumbs.ScrollIntoView(Thumbs.SelectedItem);
            }
            _refreshing = false;
        }

        // ---------------------------------------------------------------- events

        private void Thumbs_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_refreshing) return;
            int i = Thumbs.SelectedIndex;
            if (i < 0) return;

            _history.Select(i);

            // Cheap part now, expensive part shortly — see _clipboardDelay.
            _clipboardDelay.Stop();
            _clipboardDelay.Start();
        }

        /// <summary>
        /// Moves the selection with the wheel, matching the arrow keys rather than merely
        /// panning the strip. Wheel up goes left, which is the direction a horizontal list
        /// scrolls everywhere else in Windows.
        /// </summary>
        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Handled unconditionally: the ListBox sits in a ScrollViewer with vertical
            // scrolling switched off, which swallows the wheel and does nothing with it.
            // Taking it during the tunnelling pass gets there first.
            e.Handled = true;
            if (_history.Count <= 1) return;

            _wheelTravel += e.Delta;

            while (Math.Abs(_wheelTravel) >= Mouse.MouseWheelDeltaForOneLine)
            {
                if (_wheelTravel > 0)
                {
                    _wheelTravel -= Mouse.MouseWheelDeltaForOneLine;
                    Navigate(-1);
                }
                else
                {
                    _wheelTravel += Mouse.MouseWheelDeltaForOneLine;
                    Navigate(+1);
                }
            }
        }

        private void Thumbs_MouseDoubleClick(object sender, MouseButtonEventArgs e)
            => PasteSelected();

        private void Thumbs_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Left:
                    // Move to the previous (older) thumbnail.
                    e.Handled = true;
                    Navigate(-1);
                    break;

                case Key.Right:
                    // Move to the next (newer) thumbnail.
                    e.Handled = true;
                    Navigate(+1);
                    break;

                case Key.Enter:
                    e.Handled = true;
                    PasteSelected();
                    break;

                case Key.Escape:
                    e.Handled = true;
                    HideHud();
                    break;
            }
        }

        private void Navigate(int delta)
        {
            int count = _history.Count;
            if (count == 0) return;
            int next = Math.Clamp(Thumbs.SelectedIndex + delta, 0, count - 1);
            Thumbs.SelectedIndex = next;
            Thumbs.ScrollIntoView(Thumbs.SelectedItem);
        }

        // ---------------------------------------------------------------- paste

        private async void PasteSelected()
        {
            if (_history.Current is not { } item) return;

            // Straight to the clipboard, no delay: a paste is about to depend on it.
            _clipboardDelay.Stop();
            ClipboardService.SetImage(item.Image);
            Hide();

            if (PasteTarget != IntPtr.Zero)
                await PasteToWindowAsync(PasteTarget);
        }

        internal static async Task PasteToWindowAsync(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
            NativeMethods.SetForegroundWindow(hwnd);
            await Task.Delay(120); // wait for focus to land before Ctrl+V
            NativeMethods.SendPaste();
        }
    }
}
