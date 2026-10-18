using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    /// <summary>
    /// An always-on-top / desktop-pinnable draggable floating window for a screenshot.
    ///
    /// Supports:
    /// - Always-on-top toggle (float on top of all apps vs desktop widget level)
    /// - Pin on Desktop (persistent save system across app & Windows restarts)
    /// - Multi-monitor compliance with screen safety clamping
    /// - Visual cues (elevation shadow + stack badge) when overlapping other pinned images
    /// - Zooming with mouse wheel, copying, and unpinning without modifying clipboard
    /// </summary>
    public partial class PinnedClipWindow : Window
    {
        private readonly BitmapSource _imageSource;
        private readonly string? _filePath;
        private double _zoomFactor = 1.0;
        private readonly DispatcherTimer _feedbackTimer;
        private readonly DispatcherTimer _saveDebounceTimer;

        public PinnedItemRecord Record { get; private set; }
        public bool IsDesktopPinned { get; private set; } = false;
        public bool IsFrameless { get; private set; } = false;
        public bool IsTopmost { get; private set; } = true;

        public PinnedClipWindow(BitmapSource image, string? filePath = null, string? caption = null, PinnedItemRecord? existingRecord = null)
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

            _saveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _saveDebounceTimer.Tick += (_, _) =>
            {
                _saveDebounceTimer.Stop();
                PinnedWindowManager.SaveAll();
            };

            if (existingRecord != null)
            {
                Record = existingRecord;
                IsDesktopPinned = existingRecord.IsDesktopPinned;
                IsTopmost = existingRecord.IsTopmost;
                IsFrameless = existingRecord.IsDesktopPinned && existingRecord.IsFrameless;
            }
            else
            {
                Record = new PinnedItemRecord
                {
                    FilePath = filePath,
                    Caption = caption,
                    IsTopmost = true,
                    IsDesktopPinned = false,
                    IsFrameless = false
                };
                Record.CachedImagePath = PinnedWindowManager.EnsureImagePersisted(Record.Id, image, filePath);
                PinnedWindowManager.RegisterRecord(Record);
            }

            Loaded += (_, _) =>
            {
                PinnedWindowManager.Register(this);
                if (existingRecord != null && existingRecord.Width > 0 && existingRecord.Height > 0)
                {
                    ApplyRestoredBounds(existingRecord);
                }
                else
                {
                    PositionCenter();
                }

                UpdateTopmostUI();
                UpdateDesktopPinUI();
                UpdateFramelessUI();
            };

            Closed += (_, _) =>
            {
                PinnedWindowManager.Unregister(this);
            };
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

            // Remove WS_MAXIMIZEBOX to completely prevent Windows Aero Snap from maximizing the window
            int style = Win32.GetWindowLong(handle, Win32.GWL_STYLE);
            Win32.SetWindowLong(handle, Win32.GWL_STYLE, style & ~Win32.WS_MAXIMIZEBOX);
            Win32.SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED | Win32.SWP_NOACTIVATE);

            // Hook WndProc to block SC_MAXIMIZE and double click titlebar maximize
            var source = HwndSource.FromHwnd(handle);
            source?.AddHook(WndProc);

            if (Record != null && Record.Width > 0 && Record.Height > 0)
            {
                ApplyRestoredBounds(Record);
            }
            else
            {
                PositionCenter();
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_SYSCOMMAND = 0x0112;
            const int SC_MAXIMIZE = 0xF030;
            const int WM_NCLBUTTONDBLCLK = 0x00A3;

            if (msg == WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == SC_MAXIMIZE)
            {
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WM_NCLBUTTONDBLCLK)
            {
                handled = true;
                return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        private void Window_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }
        }

        private void ApplyRestoredBounds(PinnedItemRecord record)
        {
            _zoomFactor = record.ZoomFactor > 0 ? record.ZoomFactor : 1.0;
            ImageScale.ScaleX = _zoomFactor;
            ImageScale.ScaleY = _zoomFactor;
            ZoomText.Text = $"{Math.Round(_zoomFactor * 100):0}%";

            bool zoomed = Math.Abs(_zoomFactor - 1.0) > 0.01;
            ImageScrollViewer.HorizontalScrollBarVisibility = zoomed ? System.Windows.Controls.ScrollBarVisibility.Auto : System.Windows.Controls.ScrollBarVisibility.Disabled;
            ImageScrollViewer.VerticalScrollBarVisibility = zoomed ? System.Windows.Controls.ScrollBarVisibility.Auto : System.Windows.Controls.ScrollBarVisibility.Disabled;

            IsTopmost = record.IsTopmost;
            Topmost = record.IsTopmost;
            IsDesktopPinned = record.IsDesktopPinned;
            IsFrameless = record.IsDesktopPinned && record.IsFrameless;

            UpdateTopmostUI();
            UpdateDesktopPinUI();
            UpdateFramelessUI();

            WindowPlacement.SetBounds(this, new System.Drawing.Rectangle(
                (int)record.X, (int)record.Y, (int)record.Width, (int)record.Height),
                topMost: record.IsTopmost);
        }

        private void PositionCenter()
        {
            var monitor = DisplayInfo.FromCursor();
            var area = monitor.WorkArea;

            int imgW = _imageSource.PixelWidth;
            int imgH = _imageSource.PixelHeight;

            if (imgW <= 0 || imgH <= 0 || area.Width <= 0 || area.Height <= 0) return;

            int chromeW = (int)Math.Round(24 * monitor.Scale);
            int chromeH = (int)Math.Round(56 * monitor.Scale);

            int maxW = Math.Max(240, (int)(area.Width * 0.55) - chromeW);
            int maxH = Math.Max(180, (int)(area.Height * 0.55) - chromeH);

            double scale = Math.Min((double)maxW / imgW, (double)maxH / imgH);
            scale = Math.Min(scale, 1.0);

            int w = Math.Max(1, (int)Math.Round(imgW * scale)) + chromeW;
            int h = Math.Max(1, (int)Math.Round(imgH * scale)) + chromeH;

            int minW = (int)Math.Round(320 * monitor.Scale);
            int minH = (int)Math.Round(200 * monitor.Scale);
            w = Math.Clamp(w, minW, Math.Max(minW, area.Width - 40));
            h = Math.Clamp(h, minH, Math.Max(minH, area.Height - 40));

            int x = area.X + (area.Width - w) / 2;
            int y = area.Y + (area.Height - h) / 2;

            WindowPlacement.SetBounds(this, new System.Drawing.Rectangle(x, y, w, h), topMost: IsTopmost);

            UpdateRecordGeometry(x, y, w, h);
            PinnedWindowManager.SaveAll();
        }

        private void UpdateRecordGeometry(double x, double y, double w, double h)
        {
            if (Record == null) return;
            Record.X = x;
            Record.Y = y;
            Record.Width = w;
            Record.Height = h;
            Record.ZoomFactor = _zoomFactor;
            Record.IsTopmost = IsTopmost;
            Record.IsDesktopPinned = IsDesktopPinned;
            Record.IsFrameless = IsFrameless;

            PinnedWindowManager.UpdateRecord(Record);
            _saveDebounceTimer.Stop();
            _saveDebounceTimer.Start();
        }

        // ── Overlap & Stacking Visual Cues ────────────────────────────────────

        public void SetOverlapState(bool isOverlapping, int overlapCount)
        {
            if (isOverlapping)
            {
                WindowShadow.BlurRadius = 38;
                WindowShadow.ShadowDepth = 6;
                WindowShadow.Opacity = 0.65;
                WindowBorder.BorderBrush = (Brush)FindResource("Brush.Accent");

                StackBadge.Visibility = Visibility.Visible;
                StackBadgeText.Text = overlapCount > 1 ? $"{overlapCount + 1} stacked" : "Stacked";
            }
            else
            {
                WindowShadow.BlurRadius = 26;
                WindowShadow.ShadowDepth = 4;
                WindowShadow.Opacity = 0.45;
                WindowBorder.BorderBrush = (Brush)FindResource("Brush.BorderStrong");

                StackBadge.Visibility = Visibility.Collapsed;
            }
        }

        private void StackBadge_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            BringToTop();
        }

        private void BringToTop()
        {
            Topmost = true;
            Topmost = IsTopmost;
            Activate();
            Focus();
            PinnedWindowManager.CheckOverlaps();
        }

        // ── Dragging & Repositioning ──────────────────────────────────────────

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                var btn = FindParentButton(dep);
                if (btn != null) return;
            }

            if (e.ButtonState == MouseButtonState.Pressed)
            {
                BringToTop();
                DragMove();
                if (WindowState == WindowState.Maximized)
                {
                    WindowState = WindowState.Normal;
                }
                var rect = WindowPlacement.GetPhysicalRect(this);
                if (!rect.IsEmpty)
                {
                    UpdateRecordGeometry(rect.X, rect.Y, rect.Width, rect.Height);
                    PinnedWindowManager.SaveAll();
                }
            }
        }

        private void Window_LocationChanged(object sender, EventArgs e)
        {
            if (!IsLoaded) return;
            var rect = WindowPlacement.GetPhysicalRect(this);
            if (!rect.IsEmpty)
            {
                UpdateRecordGeometry(rect.X, rect.Y, rect.Width, rect.Height);
            }
            PinnedWindowManager.CheckOverlaps();
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!IsLoaded) return;
            var rect = WindowPlacement.GetPhysicalRect(this);
            if (!rect.IsEmpty)
            {
                UpdateRecordGeometry(rect.X, rect.Y, rect.Width, rect.Height);
            }
            PinnedWindowManager.CheckOverlaps();
        }

        private static System.Windows.Controls.Button? FindParentButton(DependencyObject? current)
        {
            while (current != null)
            {
                if (current is System.Windows.Controls.Button btn) return btn;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // ── Toggles: Always on Top & Desktop Pin ──────────────────────────────

        private void TopmostToggle_Click(object sender, RoutedEventArgs e)
            => SetTopmost(!IsTopmost);

        public void SetTopmost(bool on)
        {
            IsTopmost = on;
            Topmost = on;
            if (Record != null)
            {
                Record.IsTopmost = on;
                PinnedWindowManager.UpdateRecord(Record);
            }
            UpdateTopmostUI();
            PinnedWindowManager.SaveAll();
        }

        private void UpdateTopmostUI()
        {
            if (TopmostBtn != null)
            {
                if (IsTopmost)
                {
                    TopmostBtn.Background = (Brush)FindResource("Brush.AccentSoft");
                    TopmostBtn.BorderBrush = (Brush)FindResource("Brush.Accent");
                    TopmostIcon.Foreground = (Brush)FindResource("Brush.Accent");
                    TopmostLabel.Foreground = (Brush)FindResource("Brush.TextHeading");
                    TopmostBtn.ToolTip = "Always on Top: ON · Click to allow windows over it";
                }
                else
                {
                    TopmostBtn.Background = Brushes.Transparent;
                    TopmostBtn.BorderBrush = Brushes.Transparent;
                    TopmostIcon.Foreground = (Brush)FindResource("Brush.TextMuted");
                    TopmostLabel.Foreground = (Brush)FindResource("Brush.TextMuted");
                    TopmostBtn.ToolTip = "Always on Top: OFF · Click to keep always on top";
                }
            }

            if (OverlayTopmostIcon != null)
            {
                OverlayTopmostIcon.Foreground = IsTopmost ? (Brush)FindResource("Brush.Accent") : (Brush)FindResource("Brush.TextMuted");
            }
            if (OverlayTopmostBtn != null)
            {
                OverlayTopmostBtn.ToolTip = IsTopmost ? "Always on Top: ON · Click to toggle" : "Always on Top: OFF · Click to toggle";
            }
            if (ContextTopmost != null)
            {
                ContextTopmost.IsChecked = IsTopmost;
            }
        }

        private void DesktopPin_Click(object sender, RoutedEventArgs e)
            => SetDesktopPinned(!IsDesktopPinned);

        public void SetDesktopPinned(bool on)
        {
            IsDesktopPinned = on;
            if (Record != null)
            {
                Record.IsDesktopPinned = on;
                if (on && string.IsNullOrEmpty(Record.CachedImagePath))
                {
                    Record.CachedImagePath = PinnedWindowManager.EnsureImagePersisted(Record.Id, _imageSource, _filePath);
                }
                if (!on && IsFrameless)
                {
                    // Exit minimal mode when unpinned from desktop
                    SetFrameless(false);
                }
                PinnedWindowManager.UpdateRecord(Record);
            }
            UpdateDesktopPinUI();
            PinnedWindowManager.SaveAll();
        }

        private void UpdateDesktopPinUI()
        {
            if (DesktopPinBtn != null)
            {
                if (IsDesktopPinned)
                {
                    DesktopPinBtn.Background = (Brush)FindResource("Brush.AccentSoft");
                    DesktopPinBtn.BorderBrush = (Brush)FindResource("Brush.Accent");
                    DesktopIcon.Foreground = (Brush)FindResource("Brush.Accent");
                    DesktopLabel.Foreground = (Brush)FindResource("Brush.TextHeading");
                    DesktopPinBtn.ToolTip = "Pinned to Desktop (Saved across restarts) · Click to make temporary";

                    if (FramelessBtn != null) FramelessBtn.Visibility = Visibility.Visible;
                }
                else
                {
                    DesktopPinBtn.Background = Brushes.Transparent;
                    DesktopPinBtn.BorderBrush = Brushes.Transparent;
                    DesktopIcon.Foreground = (Brush)FindResource("Brush.TextMuted");
                    DesktopLabel.Foreground = (Brush)FindResource("Brush.TextMuted");
                    DesktopPinBtn.ToolTip = "Pin to Desktop (Keep persistently across restarts)";

                    if (FramelessBtn != null) FramelessBtn.Visibility = Visibility.Collapsed;
                }
            }

            if (ContextDesktop != null)
            {
                ContextDesktop.IsChecked = IsDesktopPinned;
            }

            if (ContextFrameless != null)
            {
                ContextFrameless.Visibility = IsDesktopPinned ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ── Title-bar-less / Minimal Mode ─────────────────────────────────────

        private void FramelessToggle_Click(object sender, RoutedEventArgs e)
            => SetFrameless(!IsFrameless);

        private void ExitFrameless_Click(object sender, RoutedEventArgs e)
            => SetFrameless(false);

        public void SetFrameless(bool on)
        {
            if (!IsDesktopPinned && on)
            {
                // Minimal mode only available when pinned to desktop
                return;
            }

            IsFrameless = on;
            if (Record != null)
            {
                Record.IsFrameless = on;
                PinnedWindowManager.UpdateRecord(Record);
            }
            UpdateFramelessUI();
            PinnedWindowManager.SaveAll();
        }

        private void UpdateFramelessUI()
        {
            if (HeaderBar == null || WindowBorder == null) return;

            if (IsFrameless)
            {
                HeaderBar.Visibility = Visibility.Collapsed;
                WindowBorder.Padding = new Thickness(2.5);
                WindowBorder.CornerRadius = new CornerRadius(8);
                WindowBorder.BorderBrush = (Brush)FindResource("Brush.Accent");

                if (FramelessOverlay != null)
                {
                    FramelessOverlay.Visibility = Visibility.Visible;
                    FramelessOverlay.Opacity = 0.45;
                }
                if (ContextFrameless != null)
                {
                    ContextFrameless.IsChecked = true;
                    ContextFrameless.Header = "Show title bar (Full controls)";
                }
            }
            else
            {
                HeaderBar.Visibility = Visibility.Visible;
                WindowBorder.Padding = new Thickness(10, 8, 10, 10);
                WindowBorder.CornerRadius = new CornerRadius(12);
                WindowBorder.BorderBrush = (Brush)FindResource("Brush.BorderStrong");

                if (FramelessOverlay != null)
                {
                    FramelessOverlay.Visibility = Visibility.Collapsed;
                }
                if (ContextFrameless != null)
                {
                    ContextFrameless.IsChecked = false;
                    ContextFrameless.Header = "Minimal mode (Frameless)";
                }
            }

            if (FramelessBtn != null)
            {
                FramelessBtn.Background = IsFrameless ? (Brush)FindResource("Brush.AccentSoft") : Brushes.Transparent;
                FramelessBtn.BorderBrush = IsFrameless ? (Brush)FindResource("Brush.Accent") : Brushes.Transparent;
                FramelessIcon.Foreground = IsFrameless ? (Brush)FindResource("Brush.Accent") : (Brush)FindResource("Brush.TextMuted");
                FramelessLabel.Foreground = IsFrameless ? (Brush)FindResource("Brush.TextHeading") : (Brush)FindResource("Brush.TextMuted");
            }
        }

        private void FramelessOverlay_MouseEnter(object sender, MouseEventArgs e)
        {
            if (FramelessOverlay != null) FramelessOverlay.Opacity = 1.0;
        }

        private void FramelessOverlay_MouseLeave(object sender, MouseEventArgs e)
        {
            if (FramelessOverlay != null) FramelessOverlay.Opacity = 0.45;
        }

        private void OverlayMenu_Click(object sender, RoutedEventArgs e)
        {
            if (ImageContextMenu != null)
            {
                ImageContextMenu.PlacementTarget = sender as UIElement;
                ImageContextMenu.IsOpen = true;
            }
        }

        private void ContextFrameless_Click(object sender, RoutedEventArgs e)
            => SetFrameless(!IsFrameless);

        private void ContextDesktop_Click(object sender, RoutedEventArgs e)
            => SetDesktopPinned(!IsDesktopPinned);

        private void ContextTopmost_Click(object sender, RoutedEventArgs e)
            => SetTopmost(!IsTopmost);

        // ── Unpin ─────────────────────────────────────────────────────────────

        private void Unpin_Click(object sender, RoutedEventArgs e)
        {
            if (Record != null)
            {
                PinnedWindowManager.RemoveRecord(Record.Id);
            }
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

            if (Record != null)
            {
                Record.ZoomFactor = _zoomFactor;
                _saveDebounceTimer.Stop();
                _saveDebounceTimer.Start();
            }
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

            if (e.Key == Key.T && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                SetTopmost(!IsTopmost);
                return;
            }

            if (e.Key == Key.D && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                SetDesktopPinned(!IsDesktopPinned);
                return;
            }

            if (e.Key == Key.F && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (IsDesktopPinned)
                {
                    e.Handled = true;
                    SetFrameless(!IsFrameless);
                    return;
                }
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                switch (e.Key)
                {
                    case Key.F:
                        if (IsDesktopPinned)
                        {
                            e.Handled = true;
                            SetFrameless(!IsFrameless);
                            return;
                        }
                        break;
                    case Key.C:
                        e.Handled = true;
                        Copy_Click(sender, e);
                        return;

                    case Key.T:
                        e.Handled = true;
                        SetTopmost(!IsTopmost);
                        return;

                    case Key.D:
                        e.Handled = true;
                        SetDesktopPinned(!IsDesktopPinned);
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
