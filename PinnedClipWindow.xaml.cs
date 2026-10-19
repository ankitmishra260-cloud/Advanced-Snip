using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
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
        public string CurrentStretchMode { get; private set; } = "Uniform";
        public double CurrentWindowOpacity { get; private set; } = 1.0;
        public double CurrentImageOpacity { get; private set; } = 1.0;
        public double CurrentOpacity => CurrentWindowOpacity;
        public int CurrentRotation { get; private set; } = 0;
        public bool CurrentFlipH { get; private set; } = false;
        public bool CurrentFlipV { get; private set; } = false;

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
                // Restored from desktop persistence on restart: Always initialize with Topmost = false
                existingRecord.IsTopmost = false;
                IsTopmost = false;
                Topmost = false;
                IsFrameless = existingRecord.IsDesktopPinned && existingRecord.IsFrameless;
                CurrentStretchMode = string.IsNullOrEmpty(existingRecord.StretchMode) ? "Uniform" : existingRecord.StretchMode;
                CurrentWindowOpacity = existingRecord.WindowOpacity > 0.15 ? existingRecord.WindowOpacity : 1.0;
                CurrentImageOpacity = existingRecord.ImageOpacity > 0.15 ? existingRecord.ImageOpacity : 1.0;
                CurrentRotation = existingRecord.RotationAngle;
                CurrentFlipH = existingRecord.FlipHorizontal;
                CurrentFlipV = existingRecord.FlipVertical;
            }
            else
            {
                Record = new PinnedItemRecord
                {
                    FilePath = filePath,
                    Caption = caption,
                    IsTopmost = true,
                    IsDesktopPinned = false,
                    IsFrameless = false,
                    StretchMode = "Uniform",
                    WindowOpacity = 1.0,
                    ImageOpacity = 1.0,
                    RotationAngle = 0,
                    FlipHorizontal = false,
                    FlipVertical = false
                };
                IsTopmost = true;
                Topmost = true;
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
                SetStretchMode(CurrentStretchMode);
                SetWindowOpacity(CurrentWindowOpacity);
                SetImageOpacity(CurrentImageOpacity);
                UpdateTransforms();
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
            const int WM_NCHITTEST = 0x0084;
            const int WM_GETMINMAXINFO = 0x0024;
            const int HTLEFT = 10;
            const int HTRIGHT = 11;
            const int HTTOP = 12;
            const int HTTOPLEFT = 13;
            const int HTTOPRIGHT = 14;
            const int HTBOTTOM = 15;
            const int HTBOTTOMLEFT = 16;
            const int HTBOTTOMRIGHT = 17;

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

            if (msg == Win32.WM_WINDOWPOSCHANGING && IsSlappedToDesktop)
            {
                try
                {
                    var pos = Marshal.PtrToStructure<Win32.WINDOWPOS>(lParam);
                    pos.hwndInsertAfter = Win32.HWND_BOTTOM;
                    Marshal.StructureToPtr(pos, lParam, false);
                }
                catch { }
            }

            if (msg == WM_NCHITTEST)
            {
                if (IsSlappedToDesktop)
                {
                    const int HTTRANSPARENT = -1;
                    handled = true;
                    return (IntPtr)HTTRANSPARENT;
                }

                if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0)
                    return IntPtr.Zero;

                int screenX = (short)(lParam.ToInt32() & 0xFFFF);
                int screenY = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

                Point p;
                try
                {
                    p = PointFromScreen(new Point(screenX, screenY));
                }
                catch
                {
                    return IntPtr.Zero;
                }

                // If cursor is directly over any interactive button, do not intercept so clicks work
                try
                {
                    if (VisualTreeHelper.HitTest(this, p) is PointHitTestResult hit && FindParentButton(hit.VisualHit) != null)
                    {
                        return IntPtr.Zero;
                    }
                }
                catch { }

                double w = ActualWidth;
                double h = ActualHeight;
                const double resizeBorder = 8.0;
                const double cornerBorder = 18.0;

                bool leftCorner = p.X <= cornerBorder;
                bool rightCorner = p.X >= w - cornerBorder;
                bool topCorner = p.Y <= cornerBorder;
                bool bottomCorner = p.Y >= h - cornerBorder;

                // 1. All 4 Corners (Generous 18px hit zone for smooth diagonal grabbing)
                if (topCorner && leftCorner)
                {
                    handled = true;
                    return (IntPtr)HTTOPLEFT;
                }
                if (topCorner && rightCorner)
                {
                    handled = true;
                    return (IntPtr)HTTOPRIGHT;
                }
                if (bottomCorner && leftCorner)
                {
                    handled = true;
                    return (IntPtr)HTBOTTOMLEFT;
                }
                if (bottomCorner && rightCorner)
                {
                    handled = true;
                    return (IntPtr)HTBOTTOMRIGHT;
                }

                // 2. All 4 Edges
                if (p.X <= resizeBorder)
                {
                    handled = true;
                    return (IntPtr)HTLEFT;
                }
                if (p.X >= w - resizeBorder)
                {
                    handled = true;
                    return (IntPtr)HTRIGHT;
                }
                if (p.Y <= resizeBorder)
                {
                    handled = true;
                    return (IntPtr)HTTOP;
                }
                if (p.Y >= h - resizeBorder)
                {
                    handled = true;
                    return (IntPtr)HTBOTTOM;
                }
            }
            else if (msg == WM_GETMINMAXINFO)
            {
                try
                {
                    var info = Marshal.PtrToStructure<Win32.MINMAXINFO>(lParam);
                    var monitor = DisplayInfo.FromWindow(hwnd);
                    double scale = monitor.Scale <= 0 ? 1.0 : monitor.Scale;
                    if (MinWidth > 0) info.ptMinTrackSize.X = (int)(MinWidth * scale);
                    if (MinHeight > 0) info.ptMinTrackSize.Y = (int)(MinHeight * scale);
                    Marshal.StructureToPtr(info, lParam, true);
                    handled = true;
                }
                catch { }
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

            // Restored pins always start with Topmost = false on app restart
            record.IsTopmost = false;
            IsTopmost = false;
            Topmost = false;
            IsDesktopPinned = record.IsDesktopPinned;
            IsFrameless = record.IsDesktopPinned && record.IsFrameless;

            CurrentStretchMode = string.IsNullOrEmpty(record.StretchMode) ? "Uniform" : record.StretchMode;
            SetStretchMode(CurrentStretchMode);

            CurrentWindowOpacity = record.WindowOpacity > 0.15 ? record.WindowOpacity : 1.0;
            CurrentImageOpacity = record.ImageOpacity > 0.15 ? record.ImageOpacity : 1.0;
            SetWindowOpacity(CurrentWindowOpacity);
            SetImageOpacity(CurrentImageOpacity);

            CurrentRotation = record.RotationAngle;
            CurrentFlipH = record.FlipHorizontal;
            CurrentFlipV = record.FlipVertical;
            UpdateTransforms();

            if (!string.IsNullOrEmpty(record.Caption))
            {
                DetailText.Text = record.Caption;
            }

            UpdateTopmostUI();
            UpdateDesktopPinUI();
            UpdateFramelessUI();

            WindowPlacement.SetBounds(this, new System.Drawing.Rectangle(
                (int)record.X, (int)record.Y, (int)record.Width, (int)record.Height),
                topMost: false, sendToBottom: true);

            if (record.IsSlappedToDesktop)
            {
                Dispatcher.BeginInvoke(new Action(() => SetSlappedToDesktop(true)), System.Windows.Threading.DispatcherPriority.Loaded);
            }
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
            Record.StretchMode = CurrentStretchMode;
            Record.WindowOpacity = CurrentWindowOpacity;
            Record.ImageOpacity = CurrentImageOpacity;
            Record.RotationAngle = CurrentRotation;
            Record.FlipHorizontal = CurrentFlipH;
            Record.FlipVertical = CurrentFlipV;
            Record.IsSlappedToDesktop = IsSlappedToDesktop;

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
            if (IsTopmost)
            {
                Topmost = true;
            }
            else
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    Win32.SetWindowPos(handle, Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
                        Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                }
            }
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
            if (_slapBadge != null)
            {
                _slapBadge.UpdatePosition();
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
            if (_slapBadge != null)
            {
                _slapBadge.UpdatePosition();
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
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                Win32.SetWindowPos(handle, on ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            }
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
                // Minimal mode works hand-in-hand with desktop pinning; auto-pin so action takes effect immediately
                SetDesktopPinned(true);
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
            if (WindowBorder == null || ImageBorder == null) return;

            if (IsFrameless)
            {
                if (HeaderCard != null) HeaderCard.Visibility = Visibility.Collapsed;
                ImageBorder.CornerRadius = new CornerRadius(10.5);
                WindowBorder.CornerRadius = new CornerRadius(12);
                WindowBorder.BorderBrush = IsSlappedToDesktop
                    ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(70, 0, 120, 215))
                    : (Brush)FindResource("Brush.Accent");

                if (FramelessOverlay != null)
                {
                    FramelessOverlay.Visibility = IsSlappedToDesktop ? Visibility.Collapsed : Visibility.Visible;
                    FramelessOverlay.Opacity = Math.Min(CurrentWindowOpacity, 0.45);
                }
                if (ContextFrameless != null)
                {
                    ContextFrameless.IsChecked = true;
                    ContextFrameless.Header = "Show title bar (Full controls)";
                }
            }
            else
            {
                if (HeaderCard != null) HeaderCard.Visibility = Visibility.Visible;
                ImageBorder.CornerRadius = new CornerRadius(0, 0, 10.5, 10.5);
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
            if (FramelessOverlay != null) FramelessOverlay.Opacity = Math.Min(CurrentWindowOpacity, 0.45);
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

        private void ContextSlap_Click(object sender, RoutedEventArgs e)
            => SetSlappedToDesktop(!IsSlappedToDesktop);

        private void DetailText_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            PromptRename();
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            PromptRename();
        }

        public void PromptRename()
        {
            string current = Record?.Caption ?? DetailText.Text;
            if (string.Equals(current, "Screenshot", StringComparison.OrdinalIgnoreCase))
            {
                current = "";
            }

            string? newName = ThemedDialog.Prompt(this, "Enter a new name or caption for this pinned image:", "Rename Pinned Image", current);
            if (!string.IsNullOrWhiteSpace(newName))
            {
                DetailText.Text = newName.Trim();
                if (Record != null)
                {
                    Record.Caption = newName.Trim();
                    PinnedWindowManager.UpdateRecord(Record);
                    PinnedWindowManager.SaveAll();
                }
            }
        }

        public bool IsSlappedToDesktop { get; private set; }
        private SlapExitBadgeWindow? _slapBadge;
        private bool _wasFramelessBeforeSlap;
        private bool _wasTopmostBeforeSlap;

        public void SetSlappedToDesktop(bool on)
        {
            if (IsSlappedToDesktop == on) return;
            IsSlappedToDesktop = on;

            var currentRect = WindowPlacement.GetPhysicalRect(this);
            if (currentRect.IsEmpty && Record != null)
            {
                currentRect = new System.Drawing.Rectangle((int)Record.X, (int)Record.Y, (int)Record.Width, (int)Record.Height);
            }

            if (on)
            {
                _wasFramelessBeforeSlap = IsFrameless;
                _wasTopmostBeforeSlap = IsTopmost;

                if (!IsFrameless)
                {
                    SetFrameless(true);
                }

                if (IsTopmost)
                {
                    SetTopmost(false);
                }

                UpdateLayout();

                bool slappedInWorkerW = false;
                var bmp = CreateSlapBitmap();
                if (bmp != null && Record != null)
                {
                    slappedInWorkerW = DesktopWallpaperHost.Slap(
                        Record.Id.ToString(),
                        bmp,
                        (float)CurrentImageOpacity,
                        currentRect.X,
                        currentRect.Y,
                        currentRect.Width,
                        currentRect.Height);
                }

                if (slappedInWorkerW)
                {
                    // Snappy 90ms micro-fade out into the wallpaper layer for a seamless, stutter-free handoff
                    var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(90))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    fadeOut.Completed += (_, _) =>
                    {
                        Visibility = Visibility.Collapsed;
                        Opacity = CurrentWindowOpacity;
                    };
                    BeginAnimation(UIElement.OpacityProperty, fadeOut);
                }
                else
                {
                    // Fallback to desktop layer styles if WorkerW cannot be isolated
                    Visibility = Visibility.Visible;
                    Show();
                    ApplyDesktopLayerStyles(true, currentRect);
                }

                if (_slapBadge == null)
                {
                    _slapBadge = new SlapExitBadgeWindow(this, currentRect);
                }
                else
                {
                    _slapBadge.UpdatePosition(currentRect);
                }
                _slapBadge.Show();

                if (ContextSlap != null) ContextSlap.IsChecked = true;
            }
            else
            {
                if (_slapBadge != null)
                {
                    _slapBadge.Close();
                    _slapBadge = null;
                }

                if (Record != null)
                {
                    DesktopWallpaperHost.Unslap(Record.Id.ToString(), currentRect.X, currentRect.Y, currentRect.Width, currentRect.Height);
                }

                ApplyDesktopLayerStyles(false, currentRect);

                // Restore interactive WPF window with snappy 90ms micro-fade in
                Opacity = 0.0;
                Visibility = Visibility.Visible;
                Show();

                var fadeIn = new DoubleAnimation(CurrentWindowOpacity, TimeSpan.FromMilliseconds(90))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                BeginAnimation(UIElement.OpacityProperty, fadeIn);

                if (!_wasFramelessBeforeSlap)
                {
                    SetFrameless(false);
                }

                if (_wasTopmostBeforeSlap)
                {
                    SetTopmost(true);
                }

                if (ContextSlap != null) ContextSlap.IsChecked = false;

                BringToTop();
                Activate();
            }

            if (Record != null)
            {
                Record.IsSlappedToDesktop = on;
                PinnedWindowManager.UpdateRecord(Record);
                PinnedWindowManager.SaveAll();
            }
        }

        private void ApplyDesktopLayerStyles(bool on, System.Drawing.Rectangle currentRect)
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            if (on)
            {
                if (HeaderCard != null) HeaderCard.Visibility = Visibility.Collapsed;
                if (FramelessOverlay != null) FramelessOverlay.Visibility = Visibility.Collapsed;
                ResizeMode = ResizeMode.NoResize;

                if (WindowBorder != null)
                {
                    WindowBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 255, 255, 255));
                    WindowBorder.BorderThickness = new Thickness(1);
                }

                int ex = Win32.GetWindowLong(handle, Win32.GWL_EXSTYLE);
                ex |= Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW;
                Win32.SetWindowLong(handle, Win32.GWL_EXSTYLE, ex);

                IntPtr shell = Win32.GetDesktopShellWindow();
                if (shell != IntPtr.Zero)
                {
                    Win32.SetWindowLongPtr(handle, Win32.GWLP_HWNDPARENT, shell);
                }

                Win32.SetWindowPos(handle, Win32.HWND_BOTTOM, currentRect.X, currentRect.Y, currentRect.Width, currentRect.Height,
                    Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
            }
            else
            {
                int ex = Win32.GetWindowLong(handle, Win32.GWL_EXSTYLE);
                ex &= ~Win32.WS_EX_TRANSPARENT;
                ex &= ~Win32.WS_EX_NOACTIVATE;
                Win32.SetWindowLong(handle, Win32.GWL_EXSTYLE, ex);

                Win32.SetWindowLongPtr(handle, Win32.GWLP_HWNDPARENT, IntPtr.Zero);

                ResizeMode = ResizeMode.CanResize;
                UpdateFramelessUI();

                Win32.SetWindowPos(handle, Win32.HWND_TOP, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_SHOWWINDOW);
            }
        }

        private System.Drawing.Bitmap? CreateSlapBitmap()
        {
            try
            {
                System.Drawing.Bitmap? bmp = null;
                if (!string.IsNullOrEmpty(Record?.CachedImagePath) && File.Exists(Record.CachedImagePath))
                {
                    using var stream = new FileStream(Record.CachedImagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    bmp = new System.Drawing.Bitmap(stream);
                }
                else if (!string.IsNullOrEmpty(_filePath) && File.Exists(_filePath))
                {
                    using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    bmp = new System.Drawing.Bitmap(stream);
                }
                else if (_imageSource != null)
                {
                    using var ms = new MemoryStream();
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(_imageSource));
                    encoder.Save(ms);
                    ms.Position = 0;
                    bmp = new System.Drawing.Bitmap(ms);
                }

                if (bmp != null)
                {
                    var rf = GetRotateFlipType(CurrentRotation, CurrentFlipH, CurrentFlipV);
                    if (rf != System.Drawing.RotateFlipType.RotateNoneFlipNone)
                    {
                        bmp.RotateFlip(rf);
                    }
                }

                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static System.Drawing.RotateFlipType GetRotateFlipType(int rotation, bool flipH, bool flipV)
        {
            int r = (rotation % 360 + 360) % 360;
            if (!flipH && !flipV)
            {
                return r switch
                {
                    90 => System.Drawing.RotateFlipType.Rotate90FlipNone,
                    180 => System.Drawing.RotateFlipType.Rotate180FlipNone,
                    270 => System.Drawing.RotateFlipType.Rotate270FlipNone,
                    _ => System.Drawing.RotateFlipType.RotateNoneFlipNone
                };
            }
            if (flipH && !flipV)
            {
                return r switch
                {
                    90 => System.Drawing.RotateFlipType.Rotate90FlipX,
                    180 => System.Drawing.RotateFlipType.Rotate180FlipX,
                    270 => System.Drawing.RotateFlipType.Rotate270FlipX,
                    _ => System.Drawing.RotateFlipType.RotateNoneFlipX
                };
            }
            if (!flipH && flipV)
            {
                return r switch
                {
                    90 => System.Drawing.RotateFlipType.Rotate90FlipY,
                    180 => System.Drawing.RotateFlipType.Rotate180FlipY,
                    270 => System.Drawing.RotateFlipType.Rotate270FlipY,
                    _ => System.Drawing.RotateFlipType.RotateNoneFlipY
                };
            }
            return r switch
            {
                90 => System.Drawing.RotateFlipType.Rotate90FlipXY,
                180 => System.Drawing.RotateFlipType.Rotate180FlipXY,
                270 => System.Drawing.RotateFlipType.Rotate270FlipXY,
                _ => System.Drawing.RotateFlipType.RotateNoneFlipXY
            };
        }

        protected override void OnClosed(EventArgs e)
        {
            if (Record != null && IsSlappedToDesktop)
            {
                DesktopWallpaperHost.Unslap(Record.Id.ToString(), 0, 0, 0, 0);
            }
            if (_slapBadge != null)
            {
                _slapBadge.Close();
                _slapBadge = null;
            }
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                Win32.SetWindowLongPtr(handle, Win32.GWLP_HWNDPARENT, IntPtr.Zero);
            }
            base.OnClosed(e);
        }

        // ── Unpin ─────────────────────────────────────────────────────────────

        private void Unpin_Click(object sender, RoutedEventArgs e)
        {
            if (Record != null && IsSlappedToDesktop)
            {
                DesktopWallpaperHost.Unslap(Record.Id.ToString(), 0, 0, 0, 0);
            }
            if (_slapBadge != null)
            {
                _slapBadge.Close();
                _slapBadge = null;
            }
            if (Record != null)
            {
                PinnedWindowManager.RemoveRecord(Record.Id);
            }
            Close();
        }

        // ── Copy image ────────────────────────────────────────────────────────

        public void CopyImageToClipboard()
        {
            ClipboardService.SetImage(_imageSource, _filePath);
            if (CopyStatusText != null)
            {
                CopyStatusText.Text = "Copied!";
                _feedbackTimer.Stop();
                _feedbackTimer.Start();
            }
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            CopyImageToClipboard();
        }

        // ── OCR & Save As ─────────────────────────────────────────────────────

        public async Task ExtractTextToClipboardAsync()
        {
            try
            {
                string? loadPath = null;
                if (Record != null && !string.IsNullOrEmpty(Record.CachedImagePath) && File.Exists(Record.CachedImagePath))
                {
                    loadPath = Record.CachedImagePath;
                }
                else if (!string.IsNullOrEmpty(_filePath) && File.Exists(_filePath))
                {
                    loadPath = _filePath;
                }

                if (string.IsNullOrEmpty(loadPath))
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "AdvancedSnip");
                    Directory.CreateDirectory(tempDir);
                    loadPath = Path.Combine(tempDir, $"ocr_temp_{Guid.NewGuid():N}.png");
                    using var fileStream = new FileStream(loadPath, FileMode.Create);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(_imageSource));
                    encoder.Save(fileStream);
                }

                string text = await OcrService.ReadFileAsync(loadPath);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    Clipboard.SetText(text);
                    int lines = text.Split('\n').Length;
                    ToastWindow.Show("Text Copied (OCR)", $"{lines} line(s) copied to clipboard.");
                }
                else
                {
                    ToastWindow.Show("OCR", "No text detected in this image.");
                }
            }
            catch (Exception ex)
            {
                ToastWindow.Show("OCR Error", ex.Message);
            }
        }

        public void SaveAs()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Pinned Image As",
                Filter = "PNG Image (*.png)|*.png|JPEG Image (*.jpg)|*.jpg|All Files (*.*)|*.*",
                FileName = $"Pinned_{DateTime.Now:yyyyMMdd_HHmmss}.png"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    using var stream = new FileStream(dlg.FileName, FileMode.Create);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(_imageSource));
                    encoder.Save(stream);
                    ToastWindow.Show("Image Saved", Path.GetFileName(dlg.FileName));
                }
                catch (Exception ex)
                {
                    ToastWindow.Show("Save Failed", ex.Message);
                }
            }
        }

        // ── Zoom Controls ─────────────────────────────────────────────────────

        private void SetZoom(double zoom)
        {
            _zoomFactor = Math.Clamp(zoom, 0.25, 4.0);
            if (ImageScale != null)
            {
                ImageScale.ScaleX = _zoomFactor;
                ImageScale.ScaleY = _zoomFactor;
            }

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
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                e.Handled = true;
                bool adjustImage = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                if (adjustImage)
                {
                    if (e.Delta > 0) SetImageOpacity(CurrentImageOpacity + 0.05);
                    else if (e.Delta < 0) SetImageOpacity(CurrentImageOpacity - 0.05);
                }
                else
                {
                    if (e.Delta > 0) SetWindowOpacity(CurrentWindowOpacity + 0.05);
                    else if (e.Delta < 0) SetWindowOpacity(CurrentWindowOpacity - 0.05);
                }
                return;
            }

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

            if (e.Key == Key.F2)
            {
                e.Handled = true;
                PromptRename();
                return;
            }

            if (e.Key == Key.S && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                SetSlappedToDesktop(!IsSlappedToDesktop);
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

            if (e.Key == Key.OemOpenBrackets) // '['
            {
                e.Handled = true;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    SetImageOpacity(CurrentImageOpacity - 0.10);
                else
                    SetWindowOpacity(CurrentWindowOpacity - 0.10);
                return;
            }

            if (e.Key == Key.OemCloseBrackets) // ']'
            {
                e.Handled = true;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    SetImageOpacity(CurrentImageOpacity + 0.10);
                else
                    SetWindowOpacity(CurrentWindowOpacity + 0.10);
                return;
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                switch (e.Key)
                {
                    case Key.R:
                        e.Handled = true;
                        RotateImage(90);
                        return;

                    case Key.H:
                        e.Handled = true;
                        ToggleFlipH();
                        return;

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

        // ── Canvas Fitting & Sizing ──────────────────────────────────────────

        public void SetStretchMode(string mode)
        {
            CurrentStretchMode = mode;
            switch (mode)
            {
                case "UniformToFill":
                    PinnedImage.Stretch = Stretch.UniformToFill;
                    PinnedImage.HorizontalAlignment = HorizontalAlignment.Center;
                    PinnedImage.VerticalAlignment = VerticalAlignment.Center;
                    ImageScrollViewer.HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled;
                    ImageScrollViewer.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled;
                    break;
                case "Fill":
                    PinnedImage.Stretch = Stretch.Fill;
                    PinnedImage.HorizontalAlignment = HorizontalAlignment.Stretch;
                    PinnedImage.VerticalAlignment = VerticalAlignment.Stretch;
                    ImageScrollViewer.HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled;
                    ImageScrollViewer.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled;
                    break;
                case "Actual":
                    PinnedImage.Stretch = Stretch.None;
                    PinnedImage.HorizontalAlignment = HorizontalAlignment.Center;
                    PinnedImage.VerticalAlignment = VerticalAlignment.Center;
                    ImageScrollViewer.HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
                    ImageScrollViewer.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto;
                    break;
                case "Uniform":
                default:
                    CurrentStretchMode = "Uniform";
                    PinnedImage.Stretch = Stretch.Uniform;
                    PinnedImage.HorizontalAlignment = HorizontalAlignment.Center;
                    PinnedImage.VerticalAlignment = VerticalAlignment.Center;
                    bool scrollNeeded = Math.Abs(_zoomFactor - 1.0) > 0.01;
                    ImageScrollViewer.HorizontalScrollBarVisibility = scrollNeeded ? System.Windows.Controls.ScrollBarVisibility.Auto : System.Windows.Controls.ScrollBarVisibility.Disabled;
                    ImageScrollViewer.VerticalScrollBarVisibility = scrollNeeded ? System.Windows.Controls.ScrollBarVisibility.Auto : System.Windows.Controls.ScrollBarVisibility.Disabled;
                    break;
            }

            UpdateStretchUI();

            if (Record != null)
            {
                Record.StretchMode = CurrentStretchMode;
                _saveDebounceTimer.Stop();
                _saveDebounceTimer.Start();
            }
        }

        private void UpdateStretchUI()
        {
            if (ContextFitUniform != null) ContextFitUniform.IsChecked = CurrentStretchMode == "Uniform";
            if (ContextFitFillCanvas != null) ContextFitFillCanvas.IsChecked = CurrentStretchMode == "UniformToFill";
            if (ContextFitStretch != null) ContextFitStretch.IsChecked = CurrentStretchMode == "Fill";
            if (ContextFitActual != null) ContextFitActual.IsChecked = CurrentStretchMode == "Actual";
        }

        private void ResetAllOpacity_Click(object sender, RoutedEventArgs e)
            => ResetAllOpacity();

        private void FitUniform_Click(object sender, RoutedEventArgs e) => SetStretchMode("Uniform");
        private void FitFillCanvas_Click(object sender, RoutedEventArgs e) => SetStretchMode("UniformToFill");
        private void FitStretch_Click(object sender, RoutedEventArgs e) => SetStretchMode("Fill");
        private void FitActual_Click(object sender, RoutedEventArgs e) => SetStretchMode("Actual");

        private void SnapToImage_Click(object sender, RoutedEventArgs e)
        {
            SnapWindowToImage();
        }

        public void SnapWindowToImage()
        {
            int imgW = _imageSource.PixelWidth;
            int imgH = _imageSource.PixelHeight;
            if (imgW <= 0 || imgH <= 0) return;

            // Swap dimensions if rotated by 90° or 270°
            if (CurrentRotation == 90 || CurrentRotation == 270)
            {
                int temp = imgW;
                imgW = imgH;
                imgH = temp;
            }

            var monitor = DisplayInfo.FromWindow(new WindowInteropHelper(this).Handle);
            var area = monitor.WorkArea;
            double scale = monitor.Scale <= 0 ? 1.0 : monitor.Scale;

            // Extra chrome margin based on HeaderBar visibility
            double chromeW = IsFrameless ? 6 * scale : 24 * scale;
            double chromeH = IsFrameless ? 6 * scale : 56 * scale;

            double targetW = (imgW * _zoomFactor) + chromeW;
            double targetH = (imgH * _zoomFactor) + chromeH;

            int maxW = Math.Max(260, area.Width - 40);
            int maxH = Math.Max(180, area.Height - 40);

            if (targetW > maxW || targetH > maxH)
            {
                double shrink = Math.Min((double)maxW / targetW, (double)maxH / targetH);
                targetW = Math.Max(240, targetW * shrink);
                targetH = Math.Max(160, targetH * shrink);
            }

            int w = (int)Math.Round(targetW);
            int h = (int)Math.Round(targetH);

            var currentRect = WindowPlacement.GetPhysicalRect(this);
            int x = currentRect.X;
            int y = currentRect.Y;

            x = Math.Clamp(x, area.X, area.Right - w);
            y = Math.Clamp(y, area.Y, area.Bottom - h);

            WindowPlacement.SetBounds(this, new System.Drawing.Rectangle(x, y, w, h), topMost: IsTopmost);
            UpdateRecordGeometry(x, y, w, h);
            PinnedWindowManager.SaveAll();
        }

        // ── Window & Image Translucency / Opacity ─────────────────────────────

        public void SetWindowOpacity(double opacity)
        {
            CurrentWindowOpacity = Math.Clamp(Math.Round(opacity, 2), 0.20, 1.0);
            Opacity = 1.0; // Window remains fully visible so image is NOT dimmed or faded

            if (WindowBorder != null)
            {
                WindowBorder.Opacity = CurrentWindowOpacity;
            }
            if (HeaderCard != null)
            {
                HeaderCard.Opacity = CurrentWindowOpacity;
            }
            if (FramelessOverlay != null && !FramelessOverlay.IsMouseOver)
            {
                FramelessOverlay.Opacity = Math.Min(CurrentWindowOpacity, 0.45);
            }

            UpdateOpacityUI();

            if (Record != null)
            {
                Record.WindowOpacity = CurrentWindowOpacity;
                _saveDebounceTimer.Stop();
                _saveDebounceTimer.Start();
            }
        }

        public void SetImageOpacity(double opacity)
        {
            CurrentImageOpacity = Math.Clamp(Math.Round(opacity, 2), 0.15, 1.0);
            if (PinnedImage != null)
            {
                PinnedImage.Opacity = CurrentImageOpacity;
            }

            UpdateOpacityUI();

            if (Record != null)
            {
                Record.ImageOpacity = CurrentImageOpacity;
                _saveDebounceTimer.Stop();
                _saveDebounceTimer.Start();
            }

            if (IsSlappedToDesktop && Record != null)
            {
                var currentRect = WindowPlacement.GetPhysicalRect(this);
                if (currentRect.IsEmpty)
                {
                    currentRect = new System.Drawing.Rectangle((int)Record.X, (int)Record.Y, (int)Record.Width, (int)Record.Height);
                }
                var bmp = CreateSlapBitmap();
                if (bmp != null)
                {
                    DesktopWallpaperHost.Slap(
                        Record.Id.ToString(),
                        bmp,
                        (float)CurrentImageOpacity,
                        currentRect.X,
                        currentRect.Y,
                        currentRect.Width,
                        currentRect.Height);
                }
            }
        }

        public void ResetAllOpacity()
        {
            SetWindowOpacity(1.0);
            SetImageOpacity(1.0);
        }

        private void UpdateOpacityUI()
        {
            // Pin Window Transparency items
            if (ContextWinOpacity100 != null) ContextWinOpacity100.IsChecked = Math.Abs(CurrentWindowOpacity - 1.0) < 0.05;
            if (ContextWinOpacity85 != null) ContextWinOpacity85.IsChecked = Math.Abs(CurrentWindowOpacity - 0.85) < 0.05;
            if (ContextWinOpacity70 != null) ContextWinOpacity70.IsChecked = Math.Abs(CurrentWindowOpacity - 0.70) < 0.05;
            if (ContextWinOpacity50 != null) ContextWinOpacity50.IsChecked = Math.Abs(CurrentWindowOpacity - 0.50) < 0.05;
            if (ContextWinOpacity30 != null) ContextWinOpacity30.IsChecked = Math.Abs(CurrentWindowOpacity - 0.30) < 0.05;

            // Image Transparency items
            if (ContextImgOpacity100 != null) ContextImgOpacity100.IsChecked = Math.Abs(CurrentImageOpacity - 1.0) < 0.05;
            if (ContextImgOpacity85 != null) ContextImgOpacity85.IsChecked = Math.Abs(CurrentImageOpacity - 0.85) < 0.05;
            if (ContextImgOpacity70 != null) ContextImgOpacity70.IsChecked = Math.Abs(CurrentImageOpacity - 0.70) < 0.05;
            if (ContextImgOpacity50 != null) ContextImgOpacity50.IsChecked = Math.Abs(CurrentImageOpacity - 0.50) < 0.05;
            if (ContextImgOpacity30 != null) ContextImgOpacity30.IsChecked = Math.Abs(CurrentImageOpacity - 0.30) < 0.05;
        }

        private void WinOpacity100_Click(object sender, RoutedEventArgs e) => SetWindowOpacity(1.0);
        private void WinOpacity85_Click(object sender, RoutedEventArgs e) => SetWindowOpacity(0.85);
        private void WinOpacity70_Click(object sender, RoutedEventArgs e) => SetWindowOpacity(0.70);
        private void WinOpacity50_Click(object sender, RoutedEventArgs e) => SetWindowOpacity(0.50);
        private void WinOpacity30_Click(object sender, RoutedEventArgs e) => SetWindowOpacity(0.30);

        private void ImgOpacity100_Click(object sender, RoutedEventArgs e) => SetImageOpacity(1.0);
        private void ImgOpacity85_Click(object sender, RoutedEventArgs e) => SetImageOpacity(0.85);
        private void ImgOpacity70_Click(object sender, RoutedEventArgs e) => SetImageOpacity(0.70);
        private void ImgOpacity50_Click(object sender, RoutedEventArgs e) => SetImageOpacity(0.50);
        private void ImgOpacity30_Click(object sender, RoutedEventArgs e) => SetImageOpacity(0.30);

        // ── Rotate & Flip Transformations ─────────────────────────────────────

        public void RotateImage(int deltaAngle)
        {
            CurrentRotation = (CurrentRotation + deltaAngle) % 360;
            if (CurrentRotation < 0) CurrentRotation += 360;
            UpdateTransforms();
        }

        public void ToggleFlipH()
        {
            CurrentFlipH = !CurrentFlipH;
            UpdateTransforms();
        }

        public void ToggleFlipV()
        {
            CurrentFlipV = !CurrentFlipV;
            UpdateTransforms();
        }

        public void ResetTransforms()
        {
            CurrentRotation = 0;
            CurrentFlipH = false;
            CurrentFlipV = false;
            UpdateTransforms();
        }

        private void UpdateTransforms()
        {
            if (ImageRotate != null)
            {
                ImageRotate.Angle = CurrentRotation;
            }
            if (ImageFlipScale != null)
            {
                ImageFlipScale.ScaleX = CurrentFlipH ? -1.0 : 1.0;
                ImageFlipScale.ScaleY = CurrentFlipV ? -1.0 : 1.0;
            }
            if (ImageScale != null)
            {
                ImageScale.ScaleX = _zoomFactor;
                ImageScale.ScaleY = _zoomFactor;
            }

            if (Record != null)
            {
                Record.RotationAngle = CurrentRotation;
                Record.FlipHorizontal = CurrentFlipH;
                Record.FlipVertical = CurrentFlipV;
                _saveDebounceTimer.Stop();
                _saveDebounceTimer.Start();
            }
        }

        private void Rotate90_Click(object sender, RoutedEventArgs e) => RotateImage(90);
        private void Rotate180_Click(object sender, RoutedEventArgs e) => RotateImage(180);
        private void FlipH_Click(object sender, RoutedEventArgs e) => ToggleFlipH();
        private void FlipV_Click(object sender, RoutedEventArgs e) => ToggleFlipV();
        private void ResetTransform_Click(object sender, RoutedEventArgs e) => ResetTransforms();
    }
}
