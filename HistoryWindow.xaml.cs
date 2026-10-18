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

        // Created on first use and then kept, because the HUD is opened and closed dozens
        // of times a session and a window is not cheap to build.
        private ClipPreviewWindow? _preview;

        private bool PreviewOpen => _preview is { IsVisible: true };

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

            // The HUD closes when it loses focus, which would make it impossible to open
            // anything from it — the preview taking focus is precisely the HUD losing it.
            // While the preview is up, the preview's own lifetime governs instead.
            Deactivated += (_, _) => { if (_activated && !PreviewOpen) HideHud(); };

            _clipboardDelay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(180)
            };
            _clipboardDelay.Tick += (_, _) => FlushClipboard();


            // Polled rather than event-driven on purpose. There is no "the cursor entered
            // another monitor" notification for a window that is not under the cursor, and
            // the alternatives are worse: a global mouse hook is a system-wide cost for a
            // window that is open for a couple of seconds, and WM_DISPLAYCHANGE fires for
            // monitor changes, not pointer movement. A quarter-second poll of
            // GetCursorPos costs nothing and only does any work when the answer changes.
            _followCursor = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _followCursor.Tick += (_, _) => FollowCursor();

            // Safety net for the paths that hide the window without going through
            // HideHud — the hotkey toggling it shut, for instance. Whatever the user
            // last landed on still ends up on the clipboard.
            IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is false) { FlushClipboard(); _followCursor.Stop(); }
                else _followCursor.Start();
            };
        }

        private readonly DispatcherTimer _followCursor;

        /// <summary>The monitor the HUD is currently placed on, to spot a change.</summary>
        private System.Drawing.Rectangle _placedOn;

        /// <summary>
        /// Moves the HUD to whichever display the pointer is on now.
        ///
        /// Compared by monitor bounds rather than by a handle, because a handle is only
        /// stable while the monitor set is: unplugging a display and plugging it back in
        /// yields a different HMONITOR for the same screen, which would move the HUD for
        /// no reason.
        /// </summary>
        private void FollowCursor()
        {
            if (!IsVisible) return;

            var monitor = DisplayInfo.FromCursor();
            if (monitor.Bounds == _placedOn) return;

            _placedOn = monitor.Bounds;
            WindowPlacement.BottomCenterOn(this, monitor);

            // The preview is a second window anchored to the same display; leaving it
            // behind on the old screen would split the HUD across two monitors.
            if (PreviewOpen) _preview!.MoveToMonitor(monitor);
        }

        /// <summary>
        /// Writes the pending selection to the clipboard now. Does nothing when nothing is
        /// pending, so it's safe to call from every path that might close the HUD.
        /// </summary>
        private void FlushClipboard()
        {
            if (!_clipboardDelay.IsEnabled) return;
            _clipboardDelay.Stop();
            if (_history.Current is { } item) ClipboardService.SetImage(item.GetFullImage(), item.FilePath);
        }

        /// <summary>
        /// Closes the HUD without losing the selection the user landed on — leaving with
        /// the clipboard still holding the previous image would be worse than the delay
        /// it's meant to avoid.
        /// </summary>
        private void HideHud()
        {
            // The preview belongs to the HUD; leaving it on screen after the strip has
            // gone would strand a window with no way back to the thing that owns it.
            if (PreviewOpen) _preview!.Hide();

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
            if (PreviewOpen) _preview!.Hide();
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
        ///
        /// **The cursor decides, not the paste target.** It used to prefer the monitor
        /// holding the window that would receive the paste, on the reasoning that the HUD
        /// belongs next to its destination. In practice the two disagree constantly on a
        /// multi-monitor desktop — clicking a browser on the left screen and then moving to
        /// the right one leaves the foreground window behind, and the HUD appeared on a
        /// screen the user was not looking at. The pointer is where the attention is.
        /// </summary>
        private void PositionBottomCenter()
        {
            var monitor = DisplayInfo.FromCursor();
            _placedOn = monitor.Bounds;
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

        /// <summary>
        /// Right-click enlarges the thumbnail under the pointer.
        ///
        /// The click has to select first. A left-click selects on its own; a right-click
        /// does not, so without this the preview would open on whatever happened to be
        /// selected already rather than on the picture that was clicked — which looks
        /// exactly like a bug and is worse than no feature.
        /// </summary>
        private void Thumbs_RightClick(object sender, MouseButtonEventArgs e)
        {
            int index = IndexUnder(e.OriginalSource as DependencyObject);
            if (index < 0) return;

            e.Handled = true;
            Thumbs.SelectedIndex = index;
            OpenPreview();
        }

        /// <summary>
        /// Finds the row containing whatever was actually clicked — an Image, a TextBlock
        /// or a Border inside the item template.
        ///
        /// <c>ContainerFromElement</c> rather than a hand-rolled parent walk: it already
        /// crosses template boundaries, and <c>VisualTreeHelper.GetParent</c> throws on
        /// anything that is not a Visual, which a walk would eventually meet.
        /// </summary>
        private int IndexUnder(DependencyObject? source)
        {
            if (source == null) return -1;

            return Thumbs.ContainerFromElement(source) is System.Windows.Controls.ListBoxItem row
                ? Thumbs.ItemContainerGenerator.IndexFromContainer(row)
                : -1;
        }

        private void OpenPreview()
        {
            if (_history.Current is not { } item) return;

            if (_preview == null)
            {
                _preview = new ClipPreviewWindow { Owner = this };
                _preview.StepRequested += (_, delta) => Navigate(delta);
                _preview.PasteRequested += (_, _) => { _preview!.Hide(); PasteSelected(); };

                // Escape and the × mean "back to the strip", so take focus back before the
                // check below runs — otherwise it cannot tell this apart from the user
                // having clicked away into another application.
                _preview.CloseRequested += (_, _) => { Activate(); Thumbs.Focus(); };

                // When the preview goes away, the HUD has to decide whether it should have
                // gone away too. Escape means "back to the strip", so the HUD stays and
                // takes focus; a click on another application means the whole thing should
                // be gone, and the Deactivated that would have closed the HUD was
                // suppressed while the preview was up. Asking after focus has settled is
                // what tells the two apart.
                _preview.IsVisibleChanged += (_, args) =>
                {
                    if (args.NewValue is not false) return;

                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        if (!IsVisible) return;
                        if (IsActive) { Thumbs.Focus(); return; }
                        HideHud();
                    }));
                };
            }

            _preview.ShowItem(item, Thumbs.SelectedIndex, _history.Count, PreviewMonitor());
            _preview.Activate();
        }

        private MonitorEntry PreviewMonitor() => DisplayInfo.FromCursor();

        private void ItemPin_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement { Tag: ClipItem item })
            {
                PinnedClipWindow.ShowForClip(item);
            }
        }

        private void Thumbs_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.P:
                    e.Handled = true;
                    if (_history.Current is { } curItem)
                    {
                        PinnedClipWindow.ShowForClip(curItem);
                    }
                    break;

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

                case Key.Space:
                    // The HUD is otherwise entirely keyboard-drivable; leaving the preview
                    // on right-click alone would make it the one thing you must reach for
                    // the mouse to see.
                    e.Handled = true;
                    OpenPreview();
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

            // One source of truth: the strip moves, and the preview is told. The preview
            // never advances itself, so the two cannot drift.
            if (PreviewOpen && _history.Current is { } item)
                _preview!.ShowItem(item, next, count, PreviewMonitor());
        }

        // ---------------------------------------------------------------- paste

        private async void PasteSelected()
        {
            if (_history.Current is not { } item) return;

            // Straight to the clipboard, no delay: a paste is about to depend on it.
            _clipboardDelay.Stop();
            ClipboardService.SetImage(item.GetFullImage(), item.FilePath);
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
