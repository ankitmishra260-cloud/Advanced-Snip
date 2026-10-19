using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
    public enum AnnotationType
    {
        Arrow,
        Circle,
        Text,
        Hyperlink
    }

    public class AnnotationItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public AnnotationType Type { get; set; }
        public double StartX { get; set; }
        public double StartY { get; set; }
        public double EndX { get; set; }
        public double EndY { get; set; }
        public string ColorHex { get; set; } = "#FFFF4D4F";
        public double StrokeThickness { get; set; } = 3.0;
        public string? Text { get; set; }
        public string? Url { get; set; }
        public string? Label { get; set; }
    }

    public enum AnnotateTool
    {
        Select,
        Arrow,
        Circle,
        Text,
        Hyperlink
    }
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
        private static readonly Brush WordPlainBrush = FreezeBrush(new SolidColorBrush(Color.FromArgb(0x22, 0x3B, 0x82, 0xF6)));
        private static readonly Brush WordPlainBorderBrush = FreezeBrush(new SolidColorBrush(Color.FromArgb(0x55, 0x3B, 0x82, 0xF6)));
        private static readonly Brush WordLinkBrush = FreezeBrush(new SolidColorBrush(Color.FromArgb(0x35, 0x00, 0xD2, 0xFF)));
        private static readonly Brush WordLinkBorderBrush = FreezeBrush(new SolidColorBrush(Color.FromArgb(0xD0, 0x00, 0xD2, 0xFF)));
        private static readonly Brush WordSelectedBrush = FreezeBrush(new SolidColorBrush(Color.FromArgb(0x80, 0xF5, 0x9E, 0x0B)));
        private static readonly Brush WordSelectedBorderBrush = FreezeBrush(new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B)));
        private static readonly Brush WordLinkSelectedBrush = FreezeBrush(new SolidColorBrush(Color.FromArgb(0x90, 0x00, 0xD2, 0xFF)));
        private static readonly Brush WordLinkSelectedBorderBrush = FreezeBrush(new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0xD2, 0xFF)));

        private static Brush FreezeBrush(Brush brush) { brush.Freeze(); return brush; }

        private readonly BitmapSource _imageSource;
        private readonly string? _filePath;
        private double _zoomFactor = 1.0;
        private readonly DispatcherTimer _feedbackTimer;
        private readonly DispatcherTimer _saveDebounceTimer;

        private OcrLayout? _ocrLayout;
        private bool _isOcrModeActive = false;
        private bool _isOcrSelecting = false;
        private int _ocrSelFrom = -1;
        private int _ocrSelTo = -1;
        private readonly List<System.Windows.Shapes.Rectangle> _ocrWordBoxes = new();
        private bool _wasSlappedBeforeOcr = false;
        private Point _ocrMouseDownPt;
        private int _ocrMouseDownWordIdx = -1;
        private string? _detectedSelectedUrl = null;

        // Annotations and Hyperlinks state (Normal Mode only)
        private readonly List<AnnotationItem> _annotations = new();
        private readonly List<AnnotationItem> _hyperlinks = new();
        private bool _isAnnotateModeActive = false;
        private AnnotateTool _currentAnnotateTool = AnnotateTool.Arrow;
        private string _activeAnnotateColor = "#FFFF4D4F";
        private double _activeAnnotateThickness = 3.0;
        private bool _isAnnotateDrawing = false;
        private Point _annotateDrawStart;
        private Point _annotateDrawStartImg;
        private Point _annotateTextClickImg;
        private Border? _draggedLinkBorder;
        private AnnotationItem? _draggedLinkItem;
        private Point _draggedLinkStartMouse;
        private Point _draggedLinkStartPos;
        private bool _isDraggingLink = false;

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

                if (ZoomResetBtn != null)
                {
                    ZoomResetBtn.Visibility = Math.Abs(_zoomFactor - 1.0) > 0.01 ? Visibility.Visible : Visibility.Collapsed;
                }

                LoadAnnotationsFromRecord();
                RelayoutAnnotations();
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
            if (_isOcrModeActive && _ocrLayout != null)
            {
                Point ptOnImage = e.GetPosition(PinnedImage);
                var imgPt = OverlayPointToImage(ptOnImage);
                if (imgPt != null)
                {
                    int idx = _ocrLayout.NearestWord(imgPt.Value);
                    if (idx >= 0)
                    {
                        int line = _ocrLayout.Words[idx].Line;
                        int from = idx, to = idx;
                        while (from > 0 && _ocrLayout.Words[from - 1].Line == line) from--;
                        while (to < _ocrLayout.Words.Count - 1 && _ocrLayout.Words[to + 1].Line == line) to++;
                        _ocrSelFrom = from;
                        _ocrSelTo = to;
                        PaintOcrSelection();
                        Point curPt = e.GetPosition(OcrInteractionLayer);
                        ShowFloatingCopyAt(curPt);
                    }
                }
            }
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
            if (ZoomResetBtn != null)
            {
                ZoomResetBtn.Visibility = zoomed ? Visibility.Visible : Visibility.Collapsed;
            }
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

            if (_isOcrModeActive || _isAnnotateModeActive)
            {
                // In OCR or Annotate mode, clicking on the image interacts with tools, so only allow dragging from HeaderCard
                if (e.OriginalSource is DependencyObject depObj && !IsDescendantOf(depObj, HeaderCard))
                    return;
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
            if (_isOcrModeActive)
            {
                RelayoutOcrWordBoxes();
            }
            RelayoutAnnotations();
            PinnedWindowManager.CheckOverlaps();
        }

        private static bool IsDescendantOf(DependencyObject? node, DependencyObject? parent)
        {
            if (parent == null) return false;
            while (node != null)
            {
                if (ReferenceEquals(node, parent)) return true;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
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

        private void VerticalMenuBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ImageContextMenu != null)
            {
                ImageContextMenu.PlacementTarget = sender as UIElement ?? this;
                ImageContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                ImageContextMenu.IsOpen = true;
            }
        }

        private void OverlayMenu_Click(object sender, RoutedEventArgs e)
        {
            if (ImageContextMenu != null)
            {
                ImageContextMenu.PlacementTarget = sender as UIElement;
                ImageContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
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
                if (_isAnnotateModeActive)
                {
                    ExitAnnotateMode();
                }
                if (AnnotateBtn != null) AnnotateBtn.Visibility = Visibility.Collapsed;
                if (ContextAnnotateHeader != null) ContextAnnotateHeader.Visibility = Visibility.Collapsed;

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

                if (AnnotateBtn != null) AnnotateBtn.Visibility = Visibility.Visible;
                if (ContextAnnotateHeader != null) ContextAnnotateHeader.Visibility = Visibility.Visible;
                RelayoutAnnotations();

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
                if (_annotations.Count > 0 || _hyperlinks.Count > 0)
                {
                    var annotated = GetAnnotatedImageBitmap();
                    using var ms = new MemoryStream();
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(annotated));
                    encoder.Save(ms);
                    ms.Position = 0;
                    bmp = new System.Drawing.Bitmap(ms);
                }
                else if (!string.IsNullOrEmpty(Record?.CachedImagePath) && File.Exists(Record.CachedImagePath))
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
            var src = (_annotations.Count > 0 || _hyperlinks.Count > 0) ? GetAnnotatedImageBitmap() : _imageSource;
            string? path = (_annotations.Count > 0 || _hyperlinks.Count > 0) ? null : (_filePath ?? Record?.CachedImagePath);
            if (Application.Current is App app)
            {
                app.CopyImage(src, path);
            }
            else
            {
                ClipboardService.SetImage(src, path);
            }

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
                    var src = (_annotations.Count > 0 || _hyperlinks.Count > 0) ? GetAnnotatedImageBitmap() : _imageSource;
                    encoder.Frames.Add(BitmapFrame.Create(src));
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

            if (ZoomText != null)
            {
                ZoomText.Text = $"{Math.Round(_zoomFactor * 100):0}%";
            }

            bool zoomed = Math.Abs(_zoomFactor - 1.0) > 0.01;
            if (ZoomResetBtn != null)
            {
                ZoomResetBtn.Visibility = zoomed ? Visibility.Visible : Visibility.Collapsed;
            }

            ImageScrollViewer.HorizontalScrollBarVisibility = zoomed ? System.Windows.Controls.ScrollBarVisibility.Auto : System.Windows.Controls.ScrollBarVisibility.Disabled;
            ImageScrollViewer.VerticalScrollBarVisibility = zoomed ? System.Windows.Controls.ScrollBarVisibility.Auto : System.Windows.Controls.ScrollBarVisibility.Disabled;

            if (_isOcrModeActive)
            {
                RelayoutOcrWordBoxes();
            }
            RelayoutAnnotations();

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
            if (AnnotationInputBox != null && AnnotationInputBox.IsFocused)
            {
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (_isAnnotateModeActive)
                {
                    ExitAnnotateMode();
                    e.Handled = true;
                    return;
                }
                if (_isOcrModeActive)
                {
                    ExitOcrMode();
                    e.Handled = true;
                    return;
                }
                e.Handled = true;
                Unpin_Click(sender, e);
                return;
            }

            if (_isAnnotateModeActive && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (e.Key == Key.A)
                {
                    e.Handled = true;
                    _currentAnnotateTool = AnnotateTool.Arrow;
                    SyncToolbarRadios();
                    UpdateToolCursor();
                    return;
                }
                if (e.Key == Key.C)
                {
                    e.Handled = true;
                    _currentAnnotateTool = AnnotateTool.Circle;
                    SyncToolbarRadios();
                    UpdateToolCursor();
                    return;
                }
                if (e.Key == Key.T)
                {
                    e.Handled = true;
                    _currentAnnotateTool = AnnotateTool.Text;
                    SyncToolbarRadios();
                    UpdateToolCursor();
                    return;
                }
                if (e.Key == Key.L)
                {
                    e.Handled = true;
                    _currentAnnotateTool = AnnotateTool.Hyperlink;
                    SyncToolbarRadios();
                    UpdateToolCursor();
                    return;
                }
            }

            if (e.Key == Key.T && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                SetTopmost(!IsTopmost);
                return;
            }

            if (e.Key == Key.O && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                if (_isOcrModeActive) ExitOcrMode();
                else _ = EnterOcrModeAsync();
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
                        if (_isOcrModeActive && _ocrSelFrom >= 0)
                        {
                            CopySelectedOcrText();
                            return;
                        }
                        Copy_Click(sender, e);
                        return;

                    case Key.E:
                        e.Handled = true;
                        ToggleAnnotateMode();
                        return;

                    case Key.Z:
                        if (_isAnnotateModeActive || _annotations.Count > 0 || _hyperlinks.Count > 0)
                        {
                            e.Handled = true;
                            AnnotateUndo_Click(sender, e);
                            return;
                        }
                        break;

                    case Key.O:
                        e.Handled = true;
                        if (_isOcrModeActive) ExitOcrMode();
                        else _ = EnterOcrModeAsync();
                        return;

                    case Key.S:
                        e.Handled = true;
                        SaveAs();
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

            if (_isOcrModeActive)
            {
                RelayoutOcrWordBoxes();
            }
            RelayoutAnnotations();

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
            if (OcrRotate != null)
            {
                OcrRotate.Angle = CurrentRotation;
            }
            if (ImageFlipScale != null)
            {
                ImageFlipScale.ScaleX = CurrentFlipH ? -1.0 : 1.0;
                ImageFlipScale.ScaleY = CurrentFlipV ? -1.0 : 1.0;
            }
            if (OcrFlipScale != null)
            {
                OcrFlipScale.ScaleX = CurrentFlipH ? -1.0 : 1.0;
                OcrFlipScale.ScaleY = CurrentFlipV ? -1.0 : 1.0;
            }
            if (AnnotationRotate != null)
            {
                AnnotationRotate.Angle = CurrentRotation;
            }
            if (AnnotationFlipScale != null)
            {
                AnnotationFlipScale.ScaleX = CurrentFlipH ? -1.0 : 1.0;
                AnnotationFlipScale.ScaleY = CurrentFlipV ? -1.0 : 1.0;
            }
            if (HyperlinkRotate != null)
            {
                HyperlinkRotate.Angle = CurrentRotation;
            }
            if (HyperlinkFlipScale != null)
            {
                HyperlinkFlipScale.ScaleX = CurrentFlipH ? -1.0 : 1.0;
                HyperlinkFlipScale.ScaleY = CurrentFlipV ? -1.0 : 1.0;
            }
            if (ImageScale != null)
            {
                ImageScale.ScaleX = _zoomFactor;
                ImageScale.ScaleY = _zoomFactor;
            }

            RelayoutAnnotations();

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

        // ── Interactive OCR Selection Mode ────────────────────────────────────

        public Rect GetDisplayedImageRect()
        {
            if (_imageSource == null || PinnedImage == null || PinnedImage.ActualWidth <= 0 || PinnedImage.ActualHeight <= 0)
                return Rect.Empty;

            double imgW = _imageSource.PixelWidth;
            double imgH = _imageSource.PixelHeight;
            if (imgW <= 0 || imgH <= 0) return Rect.Empty;

            double containerW = PinnedImage.ActualWidth;
            double containerH = PinnedImage.ActualHeight;

            switch (CurrentStretchMode)
            {
                case "Fill":
                    return new Rect(0, 0, containerW, containerH);

                case "UniformToFill":
                {
                    double scale = Math.Max(containerW / imgW, containerH / imgH);
                    double rw = imgW * scale;
                    double rh = imgH * scale;
                    return new Rect((containerW - rw) / 2.0, (containerH - rh) / 2.0, rw, rh);
                }

                case "Actual":
                {
                    return new Rect((containerW - imgW) / 2.0, (containerH - imgH) / 2.0, imgW, imgH);
                }

                case "Uniform":
                default:
                {
                    double scale = Math.Min(containerW / imgW, containerH / imgH);
                    double rw = imgW * scale;
                    double rh = imgH * scale;
                    return new Rect((containerW - rw) / 2.0, (containerH - rh) / 2.0, rw, rh);
                }
            }
        }

        private Point? OverlayPointToImage(Point overlayPt)
        {
            if (_imageSource == null) return null;
            var rect = GetDisplayedImageRect();
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return null;

            double imgX = (overlayPt.X - rect.X) * (_imageSource.PixelWidth / rect.Width);
            double imgY = (overlayPt.Y - rect.Y) * (_imageSource.PixelHeight / rect.Height);

            return new Point(
                Math.Clamp(imgX, 0, _imageSource.PixelWidth),
                Math.Clamp(imgY, 0, _imageSource.PixelHeight));
        }

        public async Task EnterOcrModeAsync()
        {
            if (_isOcrModeActive) return;

            if (IsSlappedToDesktop)
            {
                _wasSlappedBeforeOcr = true;
                var currentRect = WindowPlacement.GetPhysicalRect(this);
                if (currentRect.IsEmpty && Record != null)
                {
                    currentRect = new System.Drawing.Rectangle((int)Record.X, (int)Record.Y, (int)Record.Width, (int)Record.Height);
                }
                if (!currentRect.IsEmpty)
                {
                    WindowPlacement.SetBounds(this, currentRect, topMost: true);
                }
                Visibility = Visibility.Visible;
                Topmost = true;
                Show();
                BringToTop();
                Activate();
            }

            if (CopyStatusText != null) CopyStatusText.Text = "Scanning text...";

            try
            {
                if (_ocrLayout == null)
                {
                    _ocrLayout = await OcrService.ReadLayoutAsync(_imageSource);
                }

                if (_ocrLayout == null || !_ocrLayout.Any)
                {
                    if (CopyStatusText != null) CopyStatusText.Text = "No text found";
                    ToastWindow.Show("OCR", "No readable text or numbers detected in this image.");
                    if (_wasSlappedBeforeOcr || IsSlappedToDesktop)
                    {
                        _wasSlappedBeforeOcr = false;
                        Visibility = Visibility.Collapsed;
                        Topmost = false;
                    }
                    return;
                }

                _isOcrModeActive = true;
                _isOcrSelecting = false;
                _ocrSelFrom = _ocrSelTo = -1;
                _detectedSelectedUrl = null;

                if (OcrTextCanvas != null) OcrTextCanvas.Visibility = Visibility.Visible;
                if (OcrInteractionLayer != null) OcrInteractionLayer.Visibility = Visibility.Visible;
                if (OcrFloatingCopyPopup != null) OcrFloatingCopyPopup.Visibility = Visibility.Collapsed;

                if (CopyStatusText != null)
                {
                    CopyStatusText.Text = $"{_ocrLayout.Words.Count} words selectable";
                    _feedbackTimer.Stop();
                    _feedbackTimer.Start();
                }

                RelayoutOcrWordBoxes();
            }
            catch (Exception ex)
            {
                _isOcrModeActive = false;
                if (_wasSlappedBeforeOcr || IsSlappedToDesktop)
                {
                    _wasSlappedBeforeOcr = false;
                    Visibility = Visibility.Collapsed;
                    Topmost = false;
                }
                ToastWindow.Show("OCR Error", ex.Message);
            }
        }

        public void ExitOcrMode()
        {
            if (!_isOcrModeActive) return;

            _isOcrModeActive = false;
            _isOcrSelecting = false;
            _ocrSelFrom = _ocrSelTo = -1;
            _detectedSelectedUrl = null;

            if (OcrTextCanvas != null)
            {
                OcrTextCanvas.Children.Clear();
                OcrTextCanvas.Visibility = Visibility.Collapsed;
            }
            if (OcrInteractionLayer != null)
            {
                OcrInteractionLayer.Visibility = Visibility.Collapsed;
                OcrInteractionLayer.Cursor = Cursors.IBeam;
            }
            if (OcrFloatingCopyPopup != null)
            {
                OcrFloatingCopyPopup.Visibility = Visibility.Collapsed;
            }
            _ocrWordBoxes.Clear();

            if (CopyStatusText != null) CopyStatusText.Text = "";

            if (_wasSlappedBeforeOcr || IsSlappedToDesktop)
            {
                _wasSlappedBeforeOcr = false;
                Visibility = Visibility.Collapsed;
                Topmost = false;
                _slapBadge?.UpdatePosition();
            }
        }

        private void ExitOcrMode_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ExitOcrMode();
        }

        private void RelayoutOcrWordBoxes()
        {
            if (OcrTextCanvas == null) return;
            OcrTextCanvas.Children.Clear();
            _ocrWordBoxes.Clear();

            if (_ocrLayout == null || !_ocrLayout.Any || !_isOcrModeActive) return;

            var rect = GetDisplayedImageRect();
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return;

            double scaleX = rect.Width / _imageSource.PixelWidth;
            double scaleY = rect.Height / _imageSource.PixelHeight;

            for (int i = 0; i < _ocrLayout.Words.Count; i++)
            {
                var word = _ocrLayout.Words[i];
                var box = word.Box;

                double left = rect.X + (box.X * scaleX);
                double top = rect.Y + (box.Y * scaleY);
                double width = Math.Max(2, box.Width * scaleX);
                double height = Math.Max(2, box.Height * scaleY);

                bool isLink = !string.IsNullOrEmpty(word.Url);
                var r = new System.Windows.Shapes.Rectangle
                {
                    Width = width,
                    Height = height,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = isLink ? WordLinkBrush : WordPlainBrush,
                    Stroke = isLink ? WordLinkBorderBrush : WordPlainBorderBrush,
                    StrokeThickness = isLink ? 1.5 : 1,
                    IsHitTestVisible = false
                };
                if (isLink)
                {
                    r.ToolTip = $"Click to open link:\n{word.Url}";
                }
                Canvas.SetLeft(r, left);
                Canvas.SetTop(r, top);
                OcrTextCanvas.Children.Add(r);
                _ocrWordBoxes.Add(r);
            }

            PaintOcrSelection();
        }

        private void PaintOcrSelection()
        {
            int lo = Math.Min(_ocrSelFrom, _ocrSelTo);
            int hi = Math.Max(_ocrSelFrom, _ocrSelTo);

            for (int i = 0; i < _ocrWordBoxes.Count; i++)
            {
                bool selected = (_ocrSelFrom >= 0 && i >= lo && i <= hi);
                bool isLink = _ocrLayout != null && i < _ocrLayout.Words.Count && !string.IsNullOrEmpty(_ocrLayout.Words[i].Url);

                Brush targetFill, targetStroke;
                if (selected)
                {
                    targetFill = isLink ? WordLinkSelectedBrush : WordSelectedBrush;
                    targetStroke = isLink ? WordLinkSelectedBorderBrush : WordSelectedBorderBrush;
                }
                else
                {
                    targetFill = isLink ? WordLinkBrush : WordPlainBrush;
                    targetStroke = isLink ? WordLinkBorderBrush : WordPlainBorderBrush;
                }

                if (!ReferenceEquals(_ocrWordBoxes[i].Fill, targetFill))
                    _ocrWordBoxes[i].Fill = targetFill;

                if (!ReferenceEquals(_ocrWordBoxes[i].Stroke, targetStroke))
                    _ocrWordBoxes[i].Stroke = targetStroke;
            }
        }

        private void OcrLayer_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            if (e.ChangedButton == MouseButton.Right)
            {
                ExitOcrMode();
                return;
            }

            if (e.ChangedButton == MouseButton.Left && _ocrLayout != null)
            {
                _ocrMouseDownPt = e.GetPosition(OcrInteractionLayer);
                Point ptOnImage = e.GetPosition(PinnedImage);
                var imgPt = OverlayPointToImage(ptOnImage);
                if (imgPt != null)
                {
                    int wordIdx = _ocrLayout.NearestWord(imgPt.Value);
                    _ocrMouseDownWordIdx = wordIdx;
                    if (wordIdx >= 0)
                    {
                        _isOcrSelecting = true;
                        _ocrSelFrom = _ocrSelTo = wordIdx;
                        OcrInteractionLayer.CaptureMouse();
                        PaintOcrSelection();
                        if (OcrFloatingCopyPopup != null) OcrFloatingCopyPopup.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        _ocrSelFrom = _ocrSelTo = -1;
                        _ocrMouseDownWordIdx = -1;
                        PaintOcrSelection();
                        if (OcrFloatingCopyPopup != null) OcrFloatingCopyPopup.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }

        private void OcrLayer_MouseMove(object sender, MouseEventArgs e)
        {
            if (_ocrLayout == null) return;

            Point ptOnImage = e.GetPosition(PinnedImage);
            var imgPt = OverlayPointToImage(ptOnImage);

            if (_isOcrSelecting)
            {
                e.Handled = true;
                if (imgPt != null)
                {
                    int wordIdx = _ocrLayout.NearestWord(imgPt.Value);
                    if (wordIdx >= 0 && wordIdx != _ocrSelTo)
                    {
                        _ocrSelTo = wordIdx;
                        PaintOcrSelection();
                    }
                }
            }
            else
            {
                // Dynamic cursor: if hovering directly over a recognized hyperlink box, show Hand
                if (imgPt != null)
                {
                    int wordIdx = _ocrLayout.NearestWord(imgPt.Value);
                    if (wordIdx >= 0 && !string.IsNullOrEmpty(_ocrLayout.Words[wordIdx].Url))
                    {
                        var b = _ocrLayout.Words[wordIdx].Box;
                        if (b.Contains(imgPt.Value))
                        {
                            OcrInteractionLayer.Cursor = Cursors.Hand;
                            return;
                        }
                    }
                }
                OcrInteractionLayer.Cursor = Cursors.IBeam;
            }
        }

        private void OcrLayer_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isOcrSelecting)
            {
                _isOcrSelecting = false;
                OcrInteractionLayer.ReleaseMouseCapture();
                e.Handled = true;

                Point curPt = e.GetPosition(OcrInteractionLayer);
                double moveDist = (curPt - _ocrMouseDownPt).Length;

                // Check for single click on a hyperlink (mouse barely moved)
                if (moveDist < 6 && _ocrMouseDownWordIdx >= 0 && _ocrLayout != null &&
                    _ocrMouseDownWordIdx < _ocrLayout.Words.Count)
                {
                    var word = _ocrLayout.Words[_ocrMouseDownWordIdx];
                    if (!string.IsNullOrEmpty(word.Url))
                    {
                        OpenUrlInBrowser(word.Url);
                        return;
                    }
                }

                if (_ocrSelFrom >= 0 && _ocrLayout != null)
                {
                    string text = _ocrLayout.Join(_ocrSelFrom, _ocrSelTo).Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        _detectedSelectedUrl = FindUrlInText(text);
                        ShowFloatingCopyAt(curPt);
                    }
                    else if (OcrFloatingCopyPopup != null)
                    {
                        OcrFloatingCopyPopup.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }

        private static string? FindUrlInText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(
                text,
                @"(?:https?:\/\/|www\.)[^\s<>""]+|[a-zA-Z0-9.-]+\.(?:com|org|net|edu|gov|io|ai|dev|app|co|uk|de|in|me)(?:\/[^\s<>""]*)?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string url = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'');
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url;
                }
                return url;
            }
            return null;
        }

        private static void OpenUrlInBrowser(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                string displayUrl = url.Length > 50 ? url[..47] + "..." : url;
                ToastWindow.Show("Opening Link", displayUrl);
            }
            catch (Exception ex)
            {
                ToastWindow.Show("Failed to Open Link", ex.Message);
            }
        }

        private void OcrFloatingOpenLink_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!string.IsNullOrEmpty(_detectedSelectedUrl))
            {
                OpenUrlInBrowser(_detectedSelectedUrl);
            }
        }

        private void ShowFloatingCopyAt(Point curPt)
        {
            if (OcrFloatingCopyPopup == null || OcrInteractionLayer == null) return;

            if (OcrFloatingOpenLinkBtn != null)
            {
                OcrFloatingOpenLinkBtn.Visibility = !string.IsNullOrEmpty(_detectedSelectedUrl)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            OcrFloatingCopyPopup.UpdateLayout();
            double btnW = OcrFloatingCopyPopup.ActualWidth > 0 ? OcrFloatingCopyPopup.ActualWidth : 120;
            double btnH = OcrFloatingCopyPopup.ActualHeight > 0 ? OcrFloatingCopyPopup.ActualHeight : 32;

            double x = curPt.X + 12;
            double y = curPt.Y - 14;

            x = Math.Clamp(x, 6, Math.Max(6, OcrInteractionLayer.ActualWidth - btnW - 6));
            y = Math.Clamp(y, 6, Math.Max(6, OcrInteractionLayer.ActualHeight - btnH - 6));

            Canvas.SetLeft(OcrFloatingCopyPopup, x);
            Canvas.SetTop(OcrFloatingCopyPopup, y);
            OcrFloatingCopyPopup.Visibility = Visibility.Visible;
        }

        private void OcrFloatingCopy_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            CopySelectedOcrText();
        }

        public void CopySelectedOcrText()
        {
            if (_ocrLayout == null || _ocrSelFrom < 0) return;

            string text = _ocrLayout.Join(_ocrSelFrom, _ocrSelTo).Trim();
            if (string.IsNullOrEmpty(text)) return;

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Clipboard.SetText(text);
                    break;
                }
                catch
                {
                    System.Threading.Thread.Sleep(20);
                }
            }

            if (OcrFloatingCopyPopup != null)
            {
                OcrFloatingCopyPopup.Visibility = Visibility.Collapsed;
            }

            if (CopyStatusText != null)
            {
                CopyStatusText.Text = "Text Copied!";
                _feedbackTimer.Stop();
                _feedbackTimer.Start();
            }

            string preview = text.Replace('\n', ' ');
            if (preview.Length > 48) preview = preview[..48] + "…";
            ToastWindow.Show("Text Copied (OCR)", $"“{preview}”");
        }

        private async void ContextSelectTextOcr_Click(object sender, RoutedEventArgs e)
            => await EnterOcrModeAsync();

        private async void ContextCopyAllOcr_Click(object sender, RoutedEventArgs e)
            => await ExtractTextToClipboardAsync();

        private void SaveAs_Click(object sender, RoutedEventArgs e)
            => SaveAs();

        // ═════════════════════════════════════════════════════════════════════
        // ── Annotations (Arrow, Circle, Text) & Interactive Hyperlink Buttons ──
        // ═════════════════════════════════════════════════════════════════════

        private void LoadAnnotationsFromRecord()
        {
            if (Record == null) return;
            try
            {
                if (!string.IsNullOrEmpty(Record.AnnotationsJson))
                {
                    var items = JsonSerializer.Deserialize<List<AnnotationItem>>(Record.AnnotationsJson);
                    if (items != null)
                    {
                        _annotations.Clear();
                        _annotations.AddRange(items);
                    }
                }
                if (!string.IsNullOrEmpty(Record.HyperlinksJson))
                {
                    var links = JsonSerializer.Deserialize<List<AnnotationItem>>(Record.HyperlinksJson);
                    if (links != null)
                    {
                        _hyperlinks.Clear();
                        _hyperlinks.AddRange(links);
                    }
                }
            }
            catch { }
        }

        private void SaveAnnotationsToRecord()
        {
            if (Record == null) return;
            try
            {
                Record.AnnotationsJson = _annotations.Count > 0 ? JsonSerializer.Serialize(_annotations) : null;
                Record.HyperlinksJson = _hyperlinks.Count > 0 ? JsonSerializer.Serialize(_hyperlinks) : null;
                PinnedWindowManager.UpdateRecord(Record);
                _saveDebounceTimer.Stop();
                _saveDebounceTimer.Start();
            }
            catch { }
        }

        public void RelayoutAnnotations()
        {
            if (AnnotationCanvas == null || HyperlinkCanvas == null || _imageSource == null) return;

            AnnotationCanvas.Children.Clear();
            HyperlinkCanvas.Children.Clear();

            var rect = GetDisplayedImageRect();
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return;

            double scaleX = rect.Width / _imageSource.PixelWidth;
            double scaleY = rect.Height / _imageSource.PixelHeight;

            // 1. Render Shapes (Arrow, Circle, Text)
            foreach (var item in _annotations)
            {
                switch (item.Type)
                {
                    case AnnotationType.Arrow:
                        RenderArrowOnCanvas(item, rect, scaleX, scaleY);
                        break;
                    case AnnotationType.Circle:
                        RenderCircleOnCanvas(item, rect, scaleX, scaleY);
                        break;
                    case AnnotationType.Text:
                        RenderTextOnCanvas(item, rect, scaleX, scaleY);
                        break;
                }
            }

            // 2. Render Interactive Hyperlink Buttons
            foreach (var link in _hyperlinks)
            {
                RenderHyperlinkOnCanvas(link, rect, scaleX, scaleY);
            }
        }

        private void RenderArrowOnCanvas(AnnotationItem item, Rect rect, double scaleX, double scaleY)
        {
            double x1 = rect.X + item.StartX * scaleX;
            double y1 = rect.Y + item.StartY * scaleY;
            double x2 = rect.X + item.EndX * scaleX;
            double y2 = rect.Y + item.EndY * scaleY;

            var brush = GetBrushFromHex(item.ColorHex);
            double thickness = Math.Max(1.5, item.StrokeThickness * (scaleX + scaleY) / 2.0);

            var path = CreateArrowPath(x1, y1, x2, y2, brush, thickness);
            path.IsHitTestVisible = false;
            AnnotationCanvas.Children.Add(path);
        }

        private static System.Windows.Shapes.Path CreateArrowPath(double x1, double y1, double x2, double y2, Brush brush, double thickness)
        {
            double dx = x2 - x1;
            double dy = y2 - y1;
            double len = Math.Sqrt(dx * dx + dy * dy);

            var pathGeom = new PathGeometry();

            if (len < 2)
            {
                return new System.Windows.Shapes.Path { Data = pathGeom, Stroke = brush, Fill = brush };
            }

            double ux = dx / len;
            double uy = dy / len;
            double px = -uy;
            double py = ux;

            double headLen = Math.Min(thickness * 4.2 + 8, len * 0.45);
            double headWidth = headLen * 0.55;

            double bx = x2 - ux * headLen;
            double by = y2 - uy * headLen;

            double w1x = bx + px * headWidth;
            double w1y = by + py * headWidth;
            double w2x = bx - px * headWidth;
            double w2y = by - py * headWidth;

            // Shaft line
            var shaftFig = new PathFigure { StartPoint = new Point(x1, y1), IsClosed = false };
            shaftFig.Segments.Add(new LineSegment(new Point(bx, by), true));
            pathGeom.Figures.Add(shaftFig);

            // Arrowhead polygon
            var headFig = new PathFigure { StartPoint = new Point(x2, y2), IsClosed = true, IsFilled = true };
            headFig.Segments.Add(new LineSegment(new Point(w1x, w1y), true));
            headFig.Segments.Add(new LineSegment(new Point(w2x, w2y), true));
            pathGeom.Figures.Add(headFig);

            return new System.Windows.Shapes.Path
            {
                Data = pathGeom,
                Stroke = brush,
                Fill = brush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
        }

        private void RenderCircleOnCanvas(AnnotationItem item, Rect rect, double scaleX, double scaleY)
        {
            double x1 = rect.X + item.StartX * scaleX;
            double y1 = rect.Y + item.StartY * scaleY;
            double x2 = rect.X + item.EndX * scaleX;
            double y2 = rect.Y + item.EndY * scaleY;

            double minX = Math.Min(x1, x2);
            double minY = Math.Min(y1, y2);
            double w = Math.Max(2, Math.Abs(x2 - x1));
            double h = Math.Max(2, Math.Abs(y2 - y1));

            var brush = GetBrushFromHex(item.ColorHex);
            double thickness = Math.Max(1.5, item.StrokeThickness * (scaleX + scaleY) / 2.0);

            var ellipse = new System.Windows.Shapes.Ellipse
            {
                Width = w,
                Height = h,
                Stroke = brush,
                StrokeThickness = thickness,
                Fill = Brushes.Transparent,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(ellipse, minX);
            Canvas.SetTop(ellipse, minY);
            AnnotationCanvas.Children.Add(ellipse);
        }

        private void RenderTextOnCanvas(AnnotationItem item, Rect rect, double scaleX, double scaleY)
        {
            double x = rect.X + item.StartX * scaleX;
            double y = rect.Y + item.StartY * scaleY;

            var brush = GetBrushFromHex(item.ColorHex);

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 16, 20, 32)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                IsHitTestVisible = false
            };
            border.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 8,
                ShadowDepth = 2,
                Opacity = 0.6,
                Color = Colors.Black
            };

            var tb = new TextBlock
            {
                Text = item.Text ?? "",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = brush
            };
            border.Child = tb;

            Canvas.SetLeft(border, x);
            Canvas.SetTop(border, y);
            AnnotationCanvas.Children.Add(border);
        }

        private void RenderHyperlinkOnCanvas(AnnotationItem link, Rect rect, double scaleX, double scaleY)
        {
            double x = rect.X + link.StartX * scaleX;
            double y = rect.Y + link.StartY * scaleY;

            var border = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DD121724")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6000D2FF")),
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8, 3, 6, 3),
                Cursor = Cursors.Hand,
                Tag = link,
                ToolTip = $"URL: {link.Url}\n• Left-Click: Open website in default browser\n• Drag: Move button position\n• Right-Click: Context menu"
            };
            border.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 2,
                Opacity = 0.6,
                Color = Colors.Black
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // Link glyph
            var icon = new TextBlock
            {
                Text = "\uE71B",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF00D2FF")),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            sp.Children.Add(icon);

            // Label text
            var label = new TextBlock
            {
                Text = string.IsNullOrEmpty(link.Label) ? link.Url ?? "Link" : link.Label,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Brush.TextHeading"),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            sp.Children.Add(label);

            // Launch arrow glyph
            var extIcon = new TextBlock
            {
                Text = " \u2197",
                FontSize = 11,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8000D2FF")),
                Margin = new Thickness(2, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            sp.Children.Add(extIcon);

            // Hover delete button
            var delBtn = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = Brushes.Transparent,
                Margin = new Thickness(2, 0, 0, 0),
                Cursor = Cursors.Hand,
                Visibility = Visibility.Collapsed,
                ToolTip = "Delete this link button",
                Child = new TextBlock
                {
                    Text = "\uE711",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 8,
                    Foreground = (Brush)FindResource("Brush.Danger"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            delBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                _hyperlinks.Remove(link);
                SaveAnnotationsToRecord();
                RelayoutAnnotations();
                ToastWindow.Show("Link Removed", $"Removed link to {link.Url}");
            };
            sp.Children.Add(delBtn);

            border.Child = sp;

            // Hover interactions
            border.MouseEnter += (s, e) =>
            {
                border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F2161E30"));
                border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF00D2FF"));
                delBtn.Visibility = Visibility.Visible;
            };
            border.MouseLeave += (s, e) =>
            {
                border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DD121724"));
                border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6000D2FF"));
                delBtn.Visibility = Visibility.Collapsed;
            };

            // Drag and Click handling
            border.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                _draggedLinkBorder = border;
                _draggedLinkItem = link;
                _draggedLinkStartMouse = e.GetPosition(ImageViewportGrid);
                _draggedLinkStartPos = new Point(link.StartX, link.StartY);
                _isDraggingLink = false;
                border.CaptureMouse();
            };

            border.MouseMove += (s, e) =>
            {
                if (_draggedLinkBorder == border && e.LeftButton == MouseButtonState.Pressed)
                {
                    var curMouse = e.GetPosition(ImageViewportGrid);
                    double dist = (curMouse - _draggedLinkStartMouse).Length;
                    if (dist > 4 || _isDraggingLink)
                    {
                        _isDraggingLink = true;
                        Point? curImgPt = OverlayPointToImage(curMouse);
                        Point? startImgPt = OverlayPointToImage(_draggedLinkStartMouse);
                        if (curImgPt.HasValue && startImgPt.HasValue)
                        {
                            double dx = curImgPt.Value.X - startImgPt.Value.X;
                            double dy = curImgPt.Value.Y - startImgPt.Value.Y;
                            link.StartX = Math.Clamp(_draggedLinkStartPos.X + dx, 0, _imageSource.PixelWidth);
                            link.StartY = Math.Clamp(_draggedLinkStartPos.Y + dy, 0, _imageSource.PixelHeight);

                            var r = GetDisplayedImageRect();
                            double sx = r.Width / _imageSource.PixelWidth;
                            double sy = r.Height / _imageSource.PixelHeight;
                            Canvas.SetLeft(border, r.X + link.StartX * sx);
                            Canvas.SetTop(border, r.Y + link.StartY * sy);
                        }
                    }
                }
            };

            border.MouseLeftButtonUp += (s, e) =>
            {
                if (_draggedLinkBorder == border)
                {
                    border.ReleaseMouseCapture();
                    _draggedLinkBorder = null;
                    if (_isDraggingLink)
                    {
                        _isDraggingLink = false;
                        SaveAnnotationsToRecord();
                        RelayoutAnnotations();
                    }
                    else
                    {
                        OpenUrl(link.Url);
                    }
                }
            };

            // Right-click context menu
            border.MouseRightButtonDown += (s, e) =>
            {
                e.Handled = true;
                ShowLinkContextMenu(border, link);
            };

            Canvas.SetLeft(border, x);
            Canvas.SetTop(border, y);
            HyperlinkCanvas.Children.Add(border);
        }

        private void OpenUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                ToastWindow.Show("Opening Link", url);
            }
            catch (Exception ex)
            {
                ToastWindow.Show("Cannot Open Link", ex.Message);
            }
        }

        private void ShowLinkContextMenu(Border border, AnnotationItem link)
        {
            var cm = new ContextMenu();
            var openItem = new MenuItem { Header = $"Open Link ({link.Url})" };
            openItem.Click += (_, _) => OpenUrl(link.Url);
            cm.Items.Add(openItem);

            var copyItem = new MenuItem { Header = "Copy URL to Clipboard" };
            copyItem.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(link.Url ?? "");
                    ToastWindow.Show("Copied URL", link.Url ?? "");
                }
                catch { }
            };
            cm.Items.Add(copyItem);

            cm.Items.Add(new Separator());

            var editItem = new MenuItem { Header = "Edit Link URL..." };
            editItem.Click += (_, _) =>
            {
                string? newUrl = ThemedDialog.Prompt(this, "Enter destination website URL:", "Edit Hyperlink URL", link.Url ?? "");
                if (!string.IsNullOrWhiteSpace(newUrl))
                {
                    if (TryValidateUrl(newUrl, out string validUrl, out string displayName))
                    {
                        link.Url = validUrl;
                        if (string.IsNullOrEmpty(link.Label) || link.Label == link.Url)
                        {
                            link.Label = displayName;
                        }
                        SaveAnnotationsToRecord();
                        RelayoutAnnotations();
                    }
                    else
                    {
                        ThemedDialog.Show(this, $"\"{newUrl}\" is not a valid web URL.\nExample: https://github.com", "Invalid URL");
                    }
                }
            };
            cm.Items.Add(editItem);

            var deleteItem = new MenuItem { Header = "Delete Button" };
            deleteItem.Click += (_, _) =>
            {
                _hyperlinks.Remove(link);
                SaveAnnotationsToRecord();
                RelayoutAnnotations();
            };
            cm.Items.Add(deleteItem);

            cm.PlacementTarget = border;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }

        public static bool TryValidateUrl(string input, out string validUrl, out string displayName)
        {
            validUrl = "";
            displayName = "";
            if (string.IsNullOrWhiteSpace(input)) return false;

            string trimmed = input.Trim();
            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "https://" + trimmed;
            }

            if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                !string.IsNullOrWhiteSpace(uri.Host) &&
                uri.Host.Contains('.') &&
                !uri.Host.EndsWith('.'))
            {
                validUrl = uri.AbsoluteUri;
                string host = uri.Host;
                if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                    host = host.Substring(4);

                string path = uri.AbsolutePath.TrimEnd('/');
                if (path.Length > 1 && path.Length < 25)
                    displayName = host + path;
                else
                    displayName = host;

                return true;
            }

            return false;
        }

        public void PromptAddHyperlink(Point? clickImgPoint = null)
        {
            if (IsSlappedToDesktop) return;

            string? input = ThemedDialog.Prompt(
                this,
                "Enter destination website URL:\n(e.g. https://github.com or google.com)",
                "Add Hyperlink Button",
                "https://");

            if (string.IsNullOrWhiteSpace(input)) return;

            if (!TryValidateUrl(input, out string validUrl, out string defaultLabel))
            {
                ThemedDialog.Show(
                    this,
                    $"\"{input}\" is not a valid web URL.\n\nPlease enter a complete web address, for example:\n• https://github.com\n• google.com\n• docs.microsoft.com",
                    "Invalid URL");
                return;
            }

            string? label = ThemedDialog.Prompt(
                this,
                $"Enter button display text (or keep '{defaultLabel}'):",
                "Button Label",
                defaultLabel);

            if (string.IsNullOrWhiteSpace(label))
            {
                label = defaultLabel;
            }

            Point pt;
            if (clickImgPoint.HasValue)
            {
                pt = clickImgPoint.Value;
            }
            else
            {
                pt = new Point(_imageSource.PixelWidth * 0.15, _imageSource.PixelHeight * 0.15);
            }

            var linkItem = new AnnotationItem
            {
                Type = AnnotationType.Hyperlink,
                StartX = pt.X,
                StartY = pt.Y,
                Url = validUrl,
                Label = label.Trim()
            };
            _hyperlinks.Add(linkItem);
            SaveAnnotationsToRecord();
            RelayoutAnnotations();

            ToastWindow.Show("Link Added", $"Added button: {label} \u2197");
        }

        public void EnterAnnotateMode(AnnotateTool tool = AnnotateTool.Arrow)
        {
            if (IsSlappedToDesktop) return; // Strict constraint: NOT available in slap mode

            if (_isOcrModeActive) ExitOcrMode();

            _isAnnotateModeActive = true;
            _currentAnnotateTool = tool;

            if (AnnotationInteractionLayer != null)
            {
                AnnotationInteractionLayer.Visibility = Visibility.Visible;
                UpdateToolCursor();
            }
            if (AnnotateToolbar != null)
            {
                AnnotateToolbar.Visibility = Visibility.Visible;
            }

            SyncToolbarRadios();

            if (AnnotateBtn != null)
            {
                AnnotateBtn.Background = (Brush)FindResource("Brush.AccentSoft");
                AnnotateBtn.BorderBrush = (Brush)FindResource("Brush.Accent");
                AnnotateIcon.Foreground = (Brush)FindResource("Brush.Accent");
            }

            if (CopyStatusText != null)
            {
                CopyStatusText.Text = "Annotation Mode (Esc to exit)";
                _feedbackTimer.Stop();
                _feedbackTimer.Start();
            }
        }

        public void ExitAnnotateMode()
        {
            if (!_isAnnotateModeActive) return;

            _isAnnotateModeActive = false;
            _isAnnotateDrawing = false;

            if (AnnotationInteractionLayer != null)
            {
                AnnotationInteractionLayer.Visibility = Visibility.Collapsed;
            }
            if (AnnotateToolbar != null)
            {
                AnnotateToolbar.Visibility = Visibility.Collapsed;
            }
            if (AnnotationTextHost != null)
            {
                AnnotationTextHost.Visibility = Visibility.Collapsed;
            }
            if (DrawPreviewPath != null) DrawPreviewPath.Visibility = Visibility.Collapsed;
            if (DrawPreviewEllipse != null) DrawPreviewEllipse.Visibility = Visibility.Collapsed;

            if (AnnotateBtn != null)
            {
                AnnotateBtn.Background = Brushes.Transparent;
                AnnotateBtn.BorderBrush = Brushes.Transparent;
                AnnotateIcon.Foreground = (Brush)FindResource("Brush.Accent");
            }

            if (CopyStatusText != null) CopyStatusText.Text = "";
        }

        public void ToggleAnnotateMode()
        {
            if (_isAnnotateModeActive) ExitAnnotateMode();
            else EnterAnnotateMode();
        }

        private void AnnotationLayer_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Right)
            {
                ExitAnnotateMode();
                return;
            }

            if (e.ChangedButton != MouseButton.Left) return;

            Point viewportPt = e.GetPosition(ImageViewportGrid);
            Point? imgPt = OverlayPointToImage(viewportPt);
            if (!imgPt.HasValue) return;

            _annotateDrawStart = viewportPt;
            _annotateDrawStartImg = imgPt.Value;

            if (_currentAnnotateTool == AnnotateTool.Hyperlink)
            {
                PromptAddHyperlink(imgPt.Value);
                return;
            }

            if (_currentAnnotateTool == AnnotateTool.Text)
            {
                _annotateTextClickImg = imgPt.Value;
                ShowInPlaceTextBox(viewportPt);
                return;
            }

            if (_currentAnnotateTool == AnnotateTool.Arrow || _currentAnnotateTool == AnnotateTool.Circle)
            {
                _isAnnotateDrawing = true;
                AnnotationInteractionLayer.CaptureMouse();

                var brush = GetBrushFromHex(_activeAnnotateColor);
                if (_currentAnnotateTool == AnnotateTool.Arrow)
                {
                    if (DrawPreviewPath != null)
                    {
                        DrawPreviewPath.Stroke = brush;
                        DrawPreviewPath.Fill = brush;
                        DrawPreviewPath.StrokeThickness = _activeAnnotateThickness;
                        DrawPreviewPath.Visibility = Visibility.Visible;
                        UpdatePreviewArrow(viewportPt, viewportPt);
                    }
                }
                else if (_currentAnnotateTool == AnnotateTool.Circle)
                {
                    if (DrawPreviewEllipse != null)
                    {
                        DrawPreviewEllipse.Stroke = brush;
                        DrawPreviewEllipse.StrokeThickness = _activeAnnotateThickness;
                        DrawPreviewEllipse.Visibility = Visibility.Visible;
                        UpdatePreviewEllipse(viewportPt, viewportPt);
                    }
                }
            }
        }

        private void AnnotationLayer_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isAnnotateDrawing) return;

            Point curPt = e.GetPosition(ImageViewportGrid);
            if (_currentAnnotateTool == AnnotateTool.Arrow)
            {
                UpdatePreviewArrow(_annotateDrawStart, curPt);
            }
            else if (_currentAnnotateTool == AnnotateTool.Circle)
            {
                UpdatePreviewEllipse(_annotateDrawStart, curPt);
            }
        }

        private void AnnotationLayer_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isAnnotateDrawing) return;

            _isAnnotateDrawing = false;
            AnnotationInteractionLayer.ReleaseMouseCapture();

            if (DrawPreviewPath != null) DrawPreviewPath.Visibility = Visibility.Collapsed;
            if (DrawPreviewEllipse != null) DrawPreviewEllipse.Visibility = Visibility.Collapsed;

            Point curPt = e.GetPosition(ImageViewportGrid);
            Point? endImgPt = OverlayPointToImage(curPt);
            if (!endImgPt.HasValue) return;

            double dist = (curPt - _annotateDrawStart).Length;
            if (dist < 4) return;

            if (_currentAnnotateTool == AnnotateTool.Arrow)
            {
                _annotations.Add(new AnnotationItem
                {
                    Type = AnnotationType.Arrow,
                    StartX = _annotateDrawStartImg.X,
                    StartY = _annotateDrawStartImg.Y,
                    EndX = endImgPt.Value.X,
                    EndY = endImgPt.Value.Y,
                    ColorHex = _activeAnnotateColor,
                    StrokeThickness = _activeAnnotateThickness
                });
                SaveAnnotationsToRecord();
                RelayoutAnnotations();
            }
            else if (_currentAnnotateTool == AnnotateTool.Circle)
            {
                _annotations.Add(new AnnotationItem
                {
                    Type = AnnotationType.Circle,
                    StartX = _annotateDrawStartImg.X,
                    StartY = _annotateDrawStartImg.Y,
                    EndX = endImgPt.Value.X,
                    EndY = endImgPt.Value.Y,
                    ColorHex = _activeAnnotateColor,
                    StrokeThickness = _activeAnnotateThickness
                });
                SaveAnnotationsToRecord();
                RelayoutAnnotations();
            }
        }

        private void UpdatePreviewArrow(Point p1, Point p2)
        {
            if (DrawPreviewPath == null) return;
            var brush = GetBrushFromHex(_activeAnnotateColor);
            var p = CreateArrowPath(p1.X, p1.Y, p2.X, p2.Y, brush, _activeAnnotateThickness);
            DrawPreviewPath.Data = p.Data;
        }

        private void UpdatePreviewEllipse(Point p1, Point p2)
        {
            if (DrawPreviewEllipse == null) return;
            double minX = Math.Min(p1.X, p2.X);
            double minY = Math.Min(p1.Y, p2.Y);
            double w = Math.Max(2, Math.Abs(p2.X - p1.X));
            double h = Math.Max(2, Math.Abs(p2.Y - p1.Y));

            Canvas.SetLeft(DrawPreviewEllipse, minX);
            Canvas.SetTop(DrawPreviewEllipse, minY);
            DrawPreviewEllipse.Width = w;
            DrawPreviewEllipse.Height = h;
        }

        private void ShowInPlaceTextBox(Point viewportPt)
        {
            if (AnnotationTextHost == null || AnnotationInputBox == null) return;
            Canvas.SetLeft(AnnotationTextHost, viewportPt.X);
            Canvas.SetTop(AnnotationTextHost, viewportPt.Y);
            AnnotationTextHost.Visibility = Visibility.Visible;
            AnnotationInputBox.Text = "";
            AnnotationInputBox.Focus();
            Keyboard.Focus(AnnotationInputBox);
        }

        private void CommitInPlaceText()
        {
            if (AnnotationTextHost == null || AnnotationTextHost.Visibility != Visibility.Visible || AnnotationInputBox == null) return;

            string text = AnnotationInputBox.Text.Trim();
            AnnotationTextHost.Visibility = Visibility.Collapsed;

            if (!string.IsNullOrEmpty(text))
            {
                _annotations.Add(new AnnotationItem
                {
                    Type = AnnotationType.Text,
                    StartX = _annotateTextClickImg.X,
                    StartY = _annotateTextClickImg.Y,
                    Text = text,
                    ColorHex = _activeAnnotateColor
                });
                SaveAnnotationsToRecord();
                RelayoutAnnotations();
            }
        }

        private void AnnotationInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitInPlaceText();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (AnnotationTextHost != null) AnnotationTextHost.Visibility = Visibility.Collapsed;
            }
        }

        private void AnnotationInputBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitInPlaceText();
        }

        private void ToolRadio_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string tag)
            {
                switch (tag)
                {
                    case "Arrow":
                        _currentAnnotateTool = AnnotateTool.Arrow;
                        break;
                    case "Circle":
                        _currentAnnotateTool = AnnotateTool.Circle;
                        break;
                    case "Text":
                        _currentAnnotateTool = AnnotateTool.Text;
                        break;
                    case "Hyperlink":
                        _currentAnnotateTool = AnnotateTool.Hyperlink;
                        break;
                }
                UpdateToolCursor();
            }
        }

        private void UpdateToolCursor()
        {
            if (AnnotationInteractionLayer == null) return;
            switch (_currentAnnotateTool)
            {
                case AnnotateTool.Text:
                    AnnotationInteractionLayer.Cursor = Cursors.IBeam;
                    break;
                case AnnotateTool.Hyperlink:
                    AnnotationInteractionLayer.Cursor = Cursors.Hand;
                    break;
                case AnnotateTool.Arrow:
                case AnnotateTool.Circle:
                default:
                    AnnotationInteractionLayer.Cursor = Cursors.Cross;
                    break;
            }
        }

        private void SyncToolbarRadios()
        {
            if (ToolArrowRadio != null) ToolArrowRadio.IsChecked = (_currentAnnotateTool == AnnotateTool.Arrow);
            if (ToolCircleRadio != null) ToolCircleRadio.IsChecked = (_currentAnnotateTool == AnnotateTool.Circle);
            if (ToolTextRadio != null) ToolTextRadio.IsChecked = (_currentAnnotateTool == AnnotateTool.Text);
            if (ToolLinkRadio != null) ToolLinkRadio.IsChecked = (_currentAnnotateTool == AnnotateTool.Hyperlink);
        }

        private void ColorSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.Tag is string hex)
            {
                _activeAnnotateColor = hex;
                if (ColorSwatchRed != null) ColorSwatchRed.BorderBrush = hex == "#FFFF4D4F" ? Brushes.White : Brushes.Transparent;
                if (ColorSwatchBlue != null) ColorSwatchBlue.BorderBrush = hex == "#FF3B82F6" ? Brushes.White : Brushes.Transparent;
                if (ColorSwatchGreen != null) ColorSwatchGreen.BorderBrush = hex == "#FF10B981" ? Brushes.White : Brushes.Transparent;
                if (ColorSwatchAmber != null) ColorSwatchAmber.BorderBrush = hex == "#FFF59E0B" ? Brushes.White : Brushes.Transparent;
                if (ColorSwatchWhite != null) ColorSwatchWhite.BorderBrush = hex == "#FFFFFFFF" ? (Brush)FindResource("Brush.Accent") : Brushes.Transparent;
            }
        }

        private void AnnotateUndo_Click(object sender, RoutedEventArgs e)
        {
            if (_annotations.Count > 0)
            {
                _annotations.RemoveAt(_annotations.Count - 1);
                SaveAnnotationsToRecord();
                RelayoutAnnotations();
                ToastWindow.Show("Undo", "Removed last annotation");
            }
            else if (_hyperlinks.Count > 0)
            {
                _hyperlinks.RemoveAt(_hyperlinks.Count - 1);
                SaveAnnotationsToRecord();
                RelayoutAnnotations();
                ToastWindow.Show("Undo", "Removed last link");
            }
        }

        private void AnnotateClear_Click(object sender, RoutedEventArgs e)
        {
            if (_annotations.Count == 0 && _hyperlinks.Count == 0) return;
            _annotations.Clear();
            _hyperlinks.Clear();
            SaveAnnotationsToRecord();
            RelayoutAnnotations();
            ToastWindow.Show("Clear", "Cleared all annotations and links");
        }

        private void AnnotateDone_Click(object sender, RoutedEventArgs e)
        {
            ExitAnnotateMode();
        }

        private void AnnotateToggle_Click(object sender, RoutedEventArgs e)
        {
            ToggleAnnotateMode();
        }

        private void ContextAnnotateToolbar_Click(object sender, RoutedEventArgs e) => EnterAnnotateMode();
        private void ContextDrawArrow_Click(object sender, RoutedEventArgs e) => EnterAnnotateMode(AnnotateTool.Arrow);
        private void ContextDrawCircle_Click(object sender, RoutedEventArgs e) => EnterAnnotateMode(AnnotateTool.Circle);
        private void ContextAddText_Click(object sender, RoutedEventArgs e) => EnterAnnotateMode(AnnotateTool.Text);
        private void ContextAddLink_Click(object sender, RoutedEventArgs e)
        {
            if (IsSlappedToDesktop) return;
            PromptAddHyperlink();
        }
        private void ContextClearAnnotations_Click(object sender, RoutedEventArgs e) => AnnotateClear_Click(sender, e);

        private static Brush GetBrushFromHex(string? hex)
        {
            if (string.IsNullOrEmpty(hex)) return Brushes.Red;
            try
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            }
            catch
            {
                return Brushes.Red;
            }
        }

        public BitmapSource GetAnnotatedImageBitmap()
        {
            if (_imageSource == null || (_annotations.Count == 0 && _hyperlinks.Count == 0))
            {
                return _imageSource!;
            }

            int width = _imageSource.PixelWidth;
            int height = _imageSource.PixelHeight;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(_imageSource, new Rect(0, 0, width, height));

                foreach (var item in _annotations)
                {
                    var brush = GetBrushFromHex(item.ColorHex);
                    switch (item.Type)
                    {
                        case AnnotationType.Arrow:
                        {
                            double dx = item.EndX - item.StartX;
                            double dy = item.EndY - item.StartY;
                            double len = Math.Sqrt(dx * dx + dy * dy);
                            if (len >= 2)
                            {
                                double ux = dx / len;
                                double uy = dy / len;
                                double px = -uy;
                                double py = ux;
                                double headLen = Math.Min(item.StrokeThickness * 4.2 + 8, len * 0.45);
                                double headWidth = headLen * 0.55;

                                double bx = item.EndX - ux * headLen;
                                double by = item.EndY - uy * headLen;

                                double w1x = bx + px * headWidth;
                                double w1y = by + py * headWidth;
                                double w2x = bx - px * headWidth;
                                double w2y = by - py * headWidth;

                                var pen = new Pen(brush, item.StrokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                                dc.DrawLine(pen, new Point(item.StartX, item.StartY), new Point(bx, by));

                                var geom = new StreamGeometry();
                                using (var ctx = geom.Open())
                                {
                                    ctx.BeginFigure(new Point(item.EndX, item.EndY), true, true);
                                    ctx.LineTo(new Point(w1x, w1y), true, false);
                                    ctx.LineTo(new Point(w2x, w2y), true, false);
                                }
                                geom.Freeze();
                                dc.DrawGeometry(brush, null, geom);
                            }
                            break;
                        }
                        case AnnotationType.Circle:
                        {
                            double cx = (item.StartX + item.EndX) / 2.0;
                            double cy = (item.StartY + item.EndY) / 2.0;
                            double rx = Math.Abs(item.EndX - item.StartX) / 2.0;
                            double ry = Math.Abs(item.EndY - item.StartY) / 2.0;
                            var pen = new Pen(brush, item.StrokeThickness);
                            dc.DrawEllipse(null, pen, new Point(cx, cy), rx, ry);
                            break;
                        }
                        case AnnotationType.Text:
                        {
                            var ft = new FormattedText(
                                item.Text ?? "",
                                System.Globalization.CultureInfo.InvariantCulture,
                                FlowDirection.LeftToRight,
                                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                                16,
                                brush,
                                96.0);
                            var bgRect = new Rect(item.StartX - 4, item.StartY - 2, ft.Width + 8, ft.Height + 4);
                            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 16, 20, 32)), null, bgRect, 4, 4);
                            dc.DrawText(ft, new Point(item.StartX, item.StartY));
                            break;
                        }
                    }
                }

                foreach (var link in _hyperlinks)
                {
                    string label = string.IsNullOrEmpty(link.Label) ? link.Url ?? "Link" : link.Label;
                    var linkBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF00D2FF"));
                    var ft = new FormattedText(
                        label + " \u2197",
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                        13,
                        linkBrush,
                        96.0);

                    var rect = new Rect(link.StartX, link.StartY, ft.Width + 18, ft.Height + 8);
                    var pillBrush = new SolidColorBrush(Color.FromArgb(235, 18, 23, 36));
                    var pillPen = new Pen(linkBrush, 1.2);
                    dc.DrawRoundedRectangle(pillBrush, pillPen, rect, 11, 11);
                    dc.DrawText(ft, new Point(link.StartX + 9, link.StartY + 4));
                }
            }

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }
    }
}
