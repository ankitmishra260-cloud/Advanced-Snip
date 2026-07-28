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

        private List<TargetCandidate> _candidates = new();
        private int _index;
        private System.Drawing.Point _lastCursor = new(int.MinValue, int.MinValue);

        /// <summary>The chosen target, or null when cancelled.</summary>
        public TargetCandidate? Result { get; private set; }

        public ScrollTargetOverlay(System.Drawing.Bitmap screenshot, int dimOpacity = 45)
        {
            InitializeComponent();

            _screenshot = screenshot;
            _dimAlpha = (byte)Math.Clamp((int)(dimOpacity / 100.0 * 255), 30, 200);

            ScreenImage.Source = ImageInterop.ToFrozenBitmapSourceFast(_screenshot);
            OverlayHost.MakeVirtualScreenOverlay(this, RootGrid);

            Loaded += (_, _) =>
            {
                Activate();
                Focus();
                RootGrid.Focus();
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

            double w = TipBorder.ActualWidth > 0 ? TipBorder.ActualWidth : 380;
            double h = TipBorder.ActualHeight > 0 ? TipBorder.ActualHeight : 96;

            // Keep the card fully inside the monitor the pointer is on, not just inside
            // the overall desktop bounds — otherwise it can straddle a bezel.
            var mon = DisplayInfo.FromPoint(screenX, screenY);
            var monLocal = OverlayHost.ToLocalRect(mon.Bounds);
            var pt = OverlayHost.ToLocal(screenX, screenY);

            double x = pt.X + 24;
            double y = pt.Y + 24;

            if (x + w > monLocal.Right - 8) x = pt.X - w - 18;
            if (y + h > monLocal.Bottom - 8) y = pt.Y - h - 18;

            x = Math.Max(monLocal.Left + 8, x);
            y = Math.Max(monLocal.Top + 8, y);

            System.Windows.Controls.Canvas.SetLeft(TipBorder, x);
            System.Windows.Controls.Canvas.SetTop(TipBorder, y);
        }

        private void PositionHintBar()
        {
            var mon = DisplayInfo.FromCursor();
            var local = OverlayHost.ToLocalRect(mon.Bounds);
            HintBar.Margin = new Thickness(local.Left + (local.Width - HintBar.ActualWidth) / 2,
                                           local.Top + 28, 0, 0);
        }

        private void RedrawDim(Rect? hole)
        {
            DimPath.Fill = new SolidColorBrush(Color.FromArgb(_dimAlpha, 0, 0, 0));

            var full = new RectangleGeometry(new Rect(0, 0, RootGrid.Width, RootGrid.Height));

            if (hole is { Width: > 0, Height: > 0 } h)
            {
                var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
                group.Children.Add(full);
                group.Children.Add(new RectangleGeometry(h));
                DimPath.Data = group;
            }
            else
            {
                DimPath.Data = full;
            }
        }
    }
}
