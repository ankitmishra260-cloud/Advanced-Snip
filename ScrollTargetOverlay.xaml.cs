using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    /// <summary>
    /// The "point at what you want" picker.
    ///
    /// It shows a frozen snapshot of the desktop and highlights whatever sits under the
    /// pointer. Rather than only offering whole windows, it resolves the actual scrollable
    /// area — a browser's page content rather than its tab strip and address bar, a file
    /// list rather than Explorer's whole frame — and the wheel steps outwards to the parent
    /// pane or the full window if that guess isn't what you meant.
    /// </summary>
    public partial class ScrollTargetOverlay : Window
    {
        private readonly System.Drawing.Bitmap _screenshot;
        private readonly byte _dimAlpha;
        private long _frozenBytes;

        // Same reasoning as SnipOverlay: the picker redraws its dim layer on every mouse
        // move, so the brush and the two rectangles are built once and mutated after that.
        private readonly SolidColorBrush _dimBrush;
        private readonly RectangleGeometry _dimOuter = new();
        private readonly RectangleGeometry _dimHole = new();
        private readonly GeometryGroup _dimGroup;

        private List<TargetCandidate> _candidates = new();
        private int _index;
        private System.Drawing.Point _lastCursor = new(int.MinValue, int.MinValue);

        // Same reasoning as SnipOverlay: one overlay unit is one physical pixel, so the
        // furniture drawn on top is measured in pixels and shrinks on a higher-scaled
        // display. It is scaled to whichever monitor the pointer is on.
        private double _uiScale = 1.0;
        private System.Drawing.Rectangle _uiMonitor;

        /// <summary>The chosen target, or null when cancelled.</summary>
        public TargetCandidate? Result { get; private set; }

        public ScrollTargetOverlay(System.Drawing.Bitmap screenshot, int dimOpacity = 45)
        {
            InitializeComponent();

            _screenshot = screenshot;
            _dimAlpha = (byte)Math.Clamp((int)(dimOpacity / 100.0 * 255), 30, 200);

            var frozen = ImageInterop.ToFrozenBitmapSourceAuto(_screenshot);
            _frozenBytes = MemoryGuard.Track(frozen);
            ScreenImage.Source = frozen;

            _dimBrush = new SolidColorBrush(Color.FromArgb(_dimAlpha, 0, 0, 0));
            _dimBrush.Freeze();
            _dimGroup = new GeometryGroup { FillRule = FillRule.EvenOdd };
            _dimGroup.Children.Add(_dimOuter);
            _dimGroup.Children.Add(_dimHole);
            DimPath.Fill = _dimBrush;
            DimPath.Data = _dimGroup;

            OverlayHost.MakeVirtualScreenOverlay(this, RootGrid);

            Loaded += (_, _) =>
            {
                Activate();
                Focus();
                RootGrid.Focus();
                UpdateUiScale();
                RedrawDim(null);
                PositionHintBar();
                RefreshFromCursor(force: true);
            };

            KeyDown += OnKeyDown;
        }

        // ── input ────────────────────────────────────────────────────────────

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Cancel();
                    break;

                case Key.Up:
                case Key.OemCloseBrackets:
                    e.Handled = true;
                    StepCandidate(+1);
                    break;

                case Key.Down:
                case Key.OemOpenBrackets:
                    e.Handled = true;
                    StepCandidate(-1);
                    break;

                case Key.Enter:
                case Key.Space:
                    e.Handled = true;
                    Commit();
                    break;
            }
        }

        private void Root_MouseMove(object sender, MouseEventArgs e) => RefreshFromCursor(force: false);

        private void Root_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            StepCandidate(e.Delta > 0 ? +1 : -1);
        }

        private void Root_Click(object sender, MouseButtonEventArgs e) => Commit();

        private void Root_Cancel(object sender, MouseButtonEventArgs e) => Cancel();

        private void StepCandidate(int delta)
        {
            if (_candidates.Count == 0) return;
            _index = Math.Clamp(_index + delta, 0, _candidates.Count - 1);
            UpdateVisuals();
        }

        private void Commit()
        {
            if (_candidates.Count == 0 || _index >= _candidates.Count) return;
            Result = _candidates[_index];
            DialogResult = true;
        }

        private void Cancel()
        {
            Result = null;
            DialogResult = false;
        }

        // ── hover resolution ─────────────────────────────────────────────────

        private void RefreshFromCursor(bool force)
        {
            if (!Win32.GetCursorPos(out var cur)) return;

            UpdateUiScale();

            // Only re-resolve when the pointer has actually moved a few pixels; hit-testing
            // on every mouse message would enumerate windows far more often than needed.
            if (!force &&
                Math.Abs(cur.X - _lastCursor.X) < 3 &&
                Math.Abs(cur.Y - _lastCursor.Y) < 3)
            {
                PlaceTip(cur.X, cur.Y);
                return;
            }

            _lastCursor = new System.Drawing.Point(cur.X, cur.Y);

            var candidates = WindowFinder.BuildCandidates(cur.X, cur.Y);

            if (candidates.Count == 0)
            {
                _candidates = candidates;
                HoverBorder.Visibility = Visibility.Collapsed;
                TipBorder.Visibility = Visibility.Collapsed;
                RedrawDim(null);
                return;
            }

            // Keep the user's manual widen/narrow choice while they stay on the same
            // window, so the selection doesn't snap back on every small mouse movement.
            bool sameWindow = _candidates.Count > 0 &&
                              candidates.Count > 0 &&
                              _candidates[0].TopLevel == candidates[0].TopLevel &&
                              _candidates.Count == candidates.Count;

            _candidates = candidates;
            _index = sameWindow
                ? Math.Clamp(_index, 0, candidates.Count - 1)
                : WindowFinder.DefaultCandidateIndex(candidates);

            UpdateVisuals();
            PositionHintBar();
        }

        /// <summary>
        /// Re-points the picker's furniture at the display under the pointer. Everything
        /// past the comparison is skipped unless the scale factor actually changed, so
        /// this costs one comparison on the overwhelming majority of mouse moves.
        /// </summary>
        private void UpdateUiScale()
        {
            var mon = DisplayInfo.FromCursor();
            _uiMonitor = mon.Bounds;

            double s = mon.Scale;
            if (double.IsNaN(s) || s < 0.05 || s > 20) s = 1.0;
            if (Math.Abs(s - _uiScale) < 0.001) return;

            _uiScale = s;
            OverlayHost.ScaleChrome(HintBar, s);
            OverlayHost.ScaleChrome(TipBorder, s);

            // The highlight traces the target's real edges, so it is not scaled — only the
            // weight of the line it is traced with.
            HoverBorder.BorderThickness = new Thickness(Math.Max(1, Math.Round(3 * s)));
        }

        /// <summary>The monitor the furniture is currently sized for, in overlay units.</summary>
        private Rect ChromeMonitor() => OverlayHost.ToLocalRect(
            _uiMonitor.Width > 0 ? _uiMonitor : DisplayInfo.FromCursor().Bounds);

        private void UpdateVisuals()
        {
            if (_candidates.Count == 0 || _index >= _candidates.Count) return;

            var c = _candidates[_index];
            var local = OverlayHost.ToLocalRect(c.Region);

            System.Windows.Controls.Canvas.SetLeft(HoverBorder, local.X);
            System.Windows.Controls.Canvas.SetTop(HoverBorder, local.Y);
            HoverBorder.Width = local.Width;
            HoverBorder.Height = local.Height;
            HoverBorder.Visibility = Visibility.Visible;

            RedrawDim(local);

            string title = Win32.GetTitle(c.TopLevel);
            if (string.IsNullOrWhiteSpace(title)) title = Win32.GetClass(c.TopLevel);
            TipTitle.Text = string.IsNullOrWhiteSpace(title) ? "(untitled window)" : title;

            TipBadgeText.Text = c.Label;
            TipBadge.Background = new SolidColorBrush(c.LikelyScrollable
                ? Color.FromRgb(0x25, 0x63, 0xEB)
                : Color.FromRgb(0x64, 0x74, 0x8B));

            string position = _candidates.Count > 1 ? $"  ·  target {_index + 1} of {_candidates.Count}" : "";
            TipDetail.Text = $"{c.Region.Width} × {c.Region.Height} px{position}";

            TipHint.Text = c.LikelyScrollable
                ? "Click to capture  ·  Wheel to widen or narrow  ·  Esc to cancel"
                : "No scrollbar detected here — wheel to pick a different target, or click to try anyway";

            PlaceTip(_lastCursor.X, _lastCursor.Y);
        }

        private void PlaceTip(int screenX, int screenY)
        {
            if (_candidates.Count == 0) return;

            TipBorder.Visibility = Visibility.Visible;
            TipBorder.UpdateLayout();

            // ActualWidth/Height are in overlay units; the card is drawn through a
            // RenderTransform, so its size on screen is that times the display's scale, and
            // every clearance here is scaled with it.
            double s = _uiScale;
            double w = (TipBorder.ActualWidth > 0 ? TipBorder.ActualWidth : 380) * s;
            double h = (TipBorder.ActualHeight > 0 ? TipBorder.ActualHeight : 96) * s;

            // Keep the card fully inside the monitor the pointer is on, not just inside
            // the overall desktop bounds — otherwise it can straddle a bezel.
            var monLocal = ChromeMonitor();
            var pt = OverlayHost.ToLocal(screenX, screenY);

            double x = pt.X + 24 * s;
            double y = pt.Y + 24 * s;

            if (x + w > monLocal.Right - 8 * s) x = pt.X - w - 18 * s;
            if (y + h > monLocal.Bottom - 8 * s) y = pt.Y - h - 18 * s;

            x = Math.Max(monLocal.Left + 8 * s, x);
            y = Math.Max(monLocal.Top + 8 * s, y);

            System.Windows.Controls.Canvas.SetLeft(TipBorder, x);
            System.Windows.Controls.Canvas.SetTop(TipBorder, y);
        }

        private void PositionHintBar()
        {
            var local = ChromeMonitor();
            double drawnWidth = HintBar.ActualWidth * _uiScale;
            HintBar.Margin = new Thickness(local.Left + Math.Max(0, (local.Width - drawnWidth) / 2),
                                           local.Top + 28 * _uiScale, 0, 0);
        }

        private void RedrawDim(Rect? hole)
        {
            _dimOuter.Rect = new Rect(0, 0, RootGrid.Width, RootGrid.Height);
            _dimHole.Rect = hole is { Width: > 0, Height: > 0 } h ? h : Rect.Empty;
        }

        /// <summary>
        /// Releases the desktop snapshot as soon as the picker is dismissed instead of
        /// waiting for the window object to be collected. See SnipOverlay.OnClosed.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            ScreenImage.Source = null;
            DimPath.Data = null;
            DimPath.Fill = null;

            MemoryGuard.Release(_frozenBytes);
            _frozenBytes = 0;
            MemoryGuard.RequestTrim();
        }
    }
}
