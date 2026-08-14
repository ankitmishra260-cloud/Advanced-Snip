using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    /// <summary>
    /// The tool picker used during a recording. It works two ways, and which one is right
    /// depends entirely on how much drawing a given recording involves.
    ///
    /// **Summoned (default).** Ctrl+Shift+A opens it under the pointer; picking a tool puts
    /// it away again and hands focus back. Good for a recording with two or three
    /// annotations in it, where a permanent panel would just be something else on screen.
    ///
    /// **Pinned.** It stays where it is put, every tool is one click away, and choosing one
    /// neither closes it nor takes focus from the application being demonstrated. This is
    /// the answer to the complaint that started this revision: opening the picker once per
    /// tool change is three actions for one decision, repeated all recording long. The pin
    /// is remembered between recordings and between sessions, so it is decided once.
    ///
    /// Pinning also shrinks it. A panel that is up for an hour should be a strip of icons,
    /// not a labelled grid — the labels are for learning the tools, and by the time someone
    /// pins the panel they have learned them.
    ///
    /// **Focus differs between the two modes, deliberately.** Summoned, it takes focus, and
    /// that is what lets 1–9 pick a tool without claiming the digit keys system-wide.
    /// Pinned, it is WS_EX_NOACTIVATE: clicking a tool must not pull focus out of the
    /// application being recorded, and a panel that is up permanently would otherwise do
    /// that on every click. Global Ctrl+Shift shortcuts work in both modes.
    ///
    /// It is excluded from capture, so neither mode puts it in the video.
    /// </summary>
    public partial class RecordToolPalette : Window
    {
        private readonly RecordAnnotationWindow _surface;
        private readonly ZoomController? _zoom;

        private readonly List<TextBlock> _tileLabels = new();
        private readonly List<TextBlock> _tileKeys = new();

        private IntPtr _handle;
        private IntPtr _restoreFocusTo;
        private bool _populating;

        /// <summary>True once the panel has been put somewhere deliberate.</summary>
        private bool _placed;

        /// <summary>Raised whenever anything here changes, so the HUD can mirror it.</summary>
        public event EventHandler? Changed;

        /// <summary>Raised when the panel is pinned or unpinned, so the choice can be saved.</summary>
        public event EventHandler<bool>? PinnedChanged;

        /// <summary>True while the panel stays open on its own.</summary>
        public bool Pinned { get; private set; }

        public RecordToolPalette(RecordAnnotationWindow surface, ZoomController? zoom)
        {
            InitializeComponent();

            _surface = surface;
            _zoom = zoom;

            CollectTileText();
            BuildSwatches();
            SyncFromSurface();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _handle = new WindowInteropHelper(this).Handle;
            int ex = Win32.GetWindowLong(_handle, Win32.GWL_EXSTYLE);
            Win32.SetWindowLong(_handle, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_TOOLWINDOW);

            ApplyActivation();

            // The picker must not appear in the recording it is being used to make.
            try { Win32.SetWindowDisplayAffinity(_handle, Win32.WDA_EXCLUDEFROMCAPTURE); } catch { }
        }

        // ── pinning ───────────────────────────────────────────────────────────

        /// <summary>
        /// Sets the pin without going through the button, for restoring the saved choice
        /// at the start of a recording.
        /// </summary>
        public void SetPinned(bool pinned, bool notify = true)
        {
            Pinned = pinned;
            PinBtn.IsChecked = pinned;
            HeaderText.Text = pinned ? "Tools — pinned" : "Tools";
            HintText.Visibility = pinned ? Visibility.Collapsed : Visibility.Visible;
            ZoomCaption.Visibility = pinned ? Visibility.Collapsed : Visibility.Visible;

            ApplyCompact(pinned);
            ApplyActivation();

            if (pinned) ReleaseFocus();
            if (notify) PinnedChanged?.Invoke(this, pinned);
        }

        /// <summary>
        /// Shrinks the tiles and drops the labels once the panel is going to be up
        /// permanently. Local values are set on the tiles rather than swapping styles,
        /// because a local value beats a style setter and reverting is a matter of putting
        /// the style's own numbers back.
        /// </summary>
        private void ApplyCompact(bool compact)
        {
            foreach (var tile in AllTiles())
            {
                tile.Width = compact ? 40 : 62;
                tile.Height = compact ? 36 : 56;
                tile.Margin = new Thickness(compact ? 2 : 3);
            }

            var hidden = compact ? Visibility.Collapsed : Visibility.Visible;
            foreach (var label in _tileLabels) label.Visibility = hidden;
            foreach (var key in _tileKeys) key.Visibility = hidden;

            UpdateLayout();
            ClampOnScreen();
        }

        /// <summary>
        /// A pinned panel must not steal focus from the application being recorded, so it
        /// stops accepting activation entirely — the same treatment, and for the same
        /// reason, that the drawing overlay gets. Unpinned it activates normally, which is
        /// what makes its own 1–9 keys work without claiming them system-wide.
        /// </summary>
        private void ApplyActivation()
        {
            if (_handle == IntPtr.Zero) return;

            int ex = Win32.GetWindowLong(_handle, Win32.GWL_EXSTYLE);
            ex = Pinned ? ex | Win32.WS_EX_NOACTIVATE : ex & ~Win32.WS_EX_NOACTIVATE;
            Win32.SetWindowLong(_handle, Win32.GWL_EXSTYLE, ex);
        }

        private void Pin_Click(object sender, RoutedEventArgs e)
            => SetPinned(PinBtn.IsChecked == true);

        private void Close_Click(object sender, RoutedEventArgs e) => Dismiss();

        private void Header_Drag(object sender, MouseButtonEventArgs e)
        {
            // Somewhere to grab. Without it a pinned panel is stuck wherever it first
            // opened, which for a panel that stays up all recording is most of the problem.
            try { DragMove(); _placed = true; } catch { }
        }

        // ── showing and hiding ────────────────────────────────────────────────

        /// <summary>
        /// Opens the panel: under the pointer when it is a quick visit, or back where the
        /// user left it when it is pinned.
        ///
        /// It is offset down and right of the cursor rather than centred on it, so the
        /// pointer starts just outside the panel: a palette that opens *under* the mouse
        /// arms the first click on whatever tile happens to be there.
        /// </summary>
        public void ShowAtCursor()
        {
            if (!Pinned) _restoreFocusTo = Win32.GetForegroundWindow();

            bool wasVisible = IsVisible;
            Show();
            UpdateLayout();

            if (Pinned && _placed)
            {
                // A pinned panel keeps its place. Re-raising it is still worth doing: it
                // shares the topmost band with the toolbar and the overlay.
                if (!wasVisible) ClampOnScreen();
                RaiseToTop();
                return;
            }

            Win32.GetCursorPos(out var cursor);
            var monitor = DisplayInfo.FromPoint(cursor.X, cursor.Y);
            var size = WindowPlacement.GetPhysicalRect(this);
            if (size.Width <= 0) { if (!Pinned) Activate(); return; }

            const int gap = 18;
            int x = cursor.X + gap;
            int y = cursor.Y + gap;

            var area = monitor.WorkArea;

            // Flip to the other side rather than merely clamping: a panel clamped against
            // the right edge would sit under the pointer, which is the thing being avoided.
            if (x + size.Width > area.Right) x = cursor.X - gap - size.Width;
            if (y + size.Height > area.Bottom) y = cursor.Y - gap - size.Height;

            x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - size.Width));
            y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - size.Height));

            WindowPlacement.MoveTo(this, x, y);
            _placed = true;

            if (Pinned) { RaiseToTop(); return; }

            Activate();
            Focus();
        }

        /// <summary>Puts the panel at a remembered position, if it is still on a screen.</summary>
        public void MoveTo(int x, int y)
        {
            Show();
            UpdateLayout();
            WindowPlacement.MoveTo(this, x, y);
            _placed = true;
            ClampOnScreen();
        }

        /// <summary>Where the panel is now, in physical pixels, so it can be saved.</summary>
        public bool TryGetPosition(out int x, out int y)
        {
            var rect = WindowPlacement.GetPhysicalRect(this);
            x = rect.X;
            y = rect.Y;
            return rect.Width > 0;
        }

        /// <summary>
        /// Drags it back onto a work area if it has ended up hanging off one — after a
        /// resize, or after being restored to a position from a monitor that is no longer
        /// attached.
        /// </summary>
        private void ClampOnScreen()
        {
            var rect = WindowPlacement.GetPhysicalRect(this);
            if (rect.Width <= 0) return;

            var area = DisplayInfo.FromPoint(rect.X + rect.Width / 2,
                                             rect.Y + rect.Height / 2).WorkArea;

            int x = Math.Clamp(rect.X, area.X, Math.Max(area.X, area.Right - rect.Width));
            int y = Math.Clamp(rect.Y, area.Y, Math.Max(area.Y, area.Bottom - rect.Height));

            if (x != rect.X || y != rect.Y) WindowPlacement.MoveTo(this, x, y);
        }

        private void RaiseToTop()
        {
            if (_handle == IntPtr.Zero) return;
            try
            {
                Win32.SetWindowPos(_handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                                   Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            }
            catch { }
        }

        /// <summary>
        /// Losing focus closes the panel — but only when it was summoned. Pinned, that
        /// would close it the instant the user clicked back into what they were recording,
        /// which is the one thing a pinned panel must survive.
        /// </summary>
        private void Palette_Deactivated(object sender, EventArgs e)
        {
            if (!Pinned) Dismiss();
        }

        /// <summary>
        /// Hides the panel and gives the foreground back to whatever had it.
        ///
        /// Restoring focus matters more than it looks: without it, a demo that involves
        /// typing would find the keystrokes going nowhere after every tool change.
        /// </summary>
        public void Dismiss()
        {
            if (!IsVisible) return;
            Hide();
            ReleaseFocus();
        }

        private void ReleaseFocus()
        {
            if (_restoreFocusTo == IntPtr.Zero || !Win32.IsWindow(_restoreFocusTo))
            {
                _restoreFocusTo = IntPtr.Zero;
                return;
            }

            try
            {
                NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                Win32.SetForegroundWindow(_restoreFocusTo);
            }
            catch { }

            _restoreFocusTo = IntPtr.Zero;
        }

        // ── keeping up with the surface ───────────────────────────────────────

        /// <summary>Reflects the surface's current tool, colour and width onto the tiles.</summary>
        public void SyncFromSurface()
        {
            _populating = true;
            try
            {
                var tile = _surface.Tool switch
                {
                    RecordTool.Pen => TilePen,
                    RecordTool.Marker => TileMarker,
                    RecordTool.Arrow => TileArrow,
                    RecordTool.Line => TileLine,
                    RecordTool.Rectangle => TileRect,
                    RecordTool.Ellipse => TileEllipse,
                    RecordTool.Spotlight => TileSpotlight,
                    RecordTool.Step => TileStep,
                    RecordTool.Redact => TileRedact,
                    _ => TileOff
                };
                tile.IsChecked = true;

                ThicknessBtn.Content = $"{_surface.PenThickness:0} px";
                FillToggle.IsChecked = _surface.FillShapes;
                PaintSwatchSelection();
                UpdateZoomLabel();

                bool any = _surface.HasDrawings;
                UndoBtn.IsEnabled = any || _surface.CanUndoClear;
                ClearBtn.IsEnabled = any;
            }
            finally { _populating = false; }
        }

        public void UpdateZoomLabel()
        {
            double f = _zoom?.TargetFactor ?? 1.0;
            ZoomLabel.Text = f % 1 == 0 ? $"{f:0}x" : $"{f:0.0}x";
        }

        // ── tool choice ───────────────────────────────────────────────────────

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (_populating) return;
            if (sender is not RadioButton rb || rb.Tag is not string tag) return;

            _surface.SetTool(tag switch
            {
                "Pen" => RecordTool.Pen,
                "Marker" => RecordTool.Marker,
                "Arrow" => RecordTool.Arrow,
                "Line" => RecordTool.Line,
                "Rectangle" => RecordTool.Rectangle,
                "Ellipse" => RecordTool.Ellipse,
                "Spotlight" => RecordTool.Spotlight,
                "Step" => RecordTool.Step,
                "Redact" => RecordTool.Redact,
                _ => RecordTool.None
            });

            Changed?.Invoke(this, EventArgs.Empty);

            // Summoned, picking a tool is the whole reason the panel is open, so it gets
            // out of the way. Pinned, it stays — but focus still goes back, or the next
            // keystroke of the demo would land here instead of in the application.
            if (Pinned) ReleaseFocus(); else Dismiss();
        }

        private void Palette_KeyDown(object sender, KeyEventArgs e)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            // Shift+1…6 recolours. The digits already mean "tool", and this keeps one
            // mental model — the row of numbers picks something — rather than scattering
            // colour onto letters nobody would guess.
            if (shift)
            {
                int slot = e.Key switch
                {
                    Key.D1 or Key.NumPad1 => 0,
                    Key.D2 or Key.NumPad2 => 1,
                    Key.D3 or Key.NumPad3 => 2,
                    Key.D4 or Key.NumPad4 => 3,
                    Key.D5 or Key.NumPad5 => 4,
                    Key.D6 or Key.NumPad6 => 5,
                    _ => -1
                };

                if (slot >= 0 && slot < RecordAnnotationWindow.Colours.Length)
                {
                    e.Handled = true;
                    _surface.PenColour = RecordAnnotationWindow.Colours[slot];
                    PaintSwatchSelection();
                    Changed?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }

            RadioButton? tile = e.Key switch
            {
                Key.D1 or Key.NumPad1 => TilePen,
                Key.D2 or Key.NumPad2 => TileMarker,
                Key.D3 or Key.NumPad3 => TileArrow,
                Key.D4 or Key.NumPad4 => TileLine,
                Key.D5 or Key.NumPad5 => TileRect,
                Key.D6 or Key.NumPad6 => TileEllipse,
                Key.D7 or Key.NumPad7 => TileSpotlight,
                Key.D8 or Key.NumPad8 => TileStep,
                Key.D9 or Key.NumPad9 => TileRedact,
                Key.D0 or Key.NumPad0 => TileOff,
                _ => null
            };

            if (tile != null)
            {
                e.Handled = true;
                if (tile.IsChecked == true) Tool_Checked(tile, new RoutedEventArgs());
                else tile.IsChecked = true;      // fires Tool_Checked
                return;
            }

            switch (e.Key)
            {
                case Key.Escape:
                    e.Handled = true;
                    Dismiss();
                    break;

                case Key.P:
                    e.Handled = true;
                    SetPinned(!Pinned);
                    break;

                case Key.OemPlus or Key.Add:
                    e.Handled = true;
                    ZoomIn_Click(this, new RoutedEventArgs());
                    break;

                case Key.OemMinus or Key.Subtract:
                    e.Handled = true;
                    ZoomOut_Click(this, new RoutedEventArgs());
                    break;

                case Key.OemOpenBrackets:
                    e.Handled = true;
                    _surface.CycleThickness(-1);
                    SyncFromSurface();
                    Changed?.Invoke(this, EventArgs.Empty);
                    break;

                case Key.OemCloseBrackets:
                    e.Handled = true;
                    _surface.CycleThickness(1);
                    SyncFromSurface();
                    Changed?.Invoke(this, EventArgs.Empty);
                    break;

                case Key.Z:
                    e.Handled = true;
                    Undo_Click(this, new RoutedEventArgs());
                    break;

                case Key.C:
                    e.Handled = true;
                    Clear_Click(this, new RoutedEventArgs());
                    break;
            }
        }

        // ── colour, width, fill ───────────────────────────────────────────────

        /// <summary>
        /// Finds the label and shortcut-number text on each tile once, so compact mode can
        /// hide them without twenty more names in the XAML. If the markup ever changes
        /// shape this quietly finds nothing and compact mode simply keeps the labels, which
        /// is a worse-looking panel rather than a crash mid-recording.
        /// </summary>
        private void CollectTileText()
        {
            foreach (var tile in AllTiles())
            {
                if (tile.Content is not Grid grid) continue;
                CollectFrom(grid);
            }

            void CollectFrom(Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is TextBlock text)
                    {
                        if (text.Tag as string == "label") _tileLabels.Add(text);
                        else if (text.Tag as string == "key") _tileKeys.Add(text);
                    }
                    else if (child is Panel nested) CollectFrom(nested);
                }
            }
        }

        private RadioButton[] AllTiles() => new[]
        {
            TilePen, TileMarker, TileArrow, TileLine, TileRect,
            TileEllipse, TileSpotlight, TileStep, TileRedact, TileOff
        };

        private void BuildSwatches()
        {
            int index = 0;
            foreach (var colour in RecordAnnotationWindow.Colours)
            {
                index++;
                var dot = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Background = new SolidColorBrush(colour),
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(2.5),
                    Margin = new Thickness(0, 0, 5, 0),
                    Cursor = Cursors.Hand,
                    Tag = colour,
                    ToolTip = $"Use this colour (Shift+{index}, or Shift+wheel over the screen)"
                };

                dot.MouseLeftButtonDown += (s, _) =>
                {
                    if (s is not Border b || b.Tag is not Color c) return;
                    _surface.PenColour = c;
                    PaintSwatchSelection();
                    Changed?.Invoke(this, EventArgs.Empty);
                };

                Swatches.Items.Add(dot);
            }
        }

        private void PaintSwatchSelection()
        {
            foreach (var item in Swatches.Items)
                if (item is Border b && b.Tag is Color c)
                    b.BorderBrush = c == _surface.PenColour
                        ? Brushes.White : Brushes.Transparent;
        }

        private void Thickness_Click(object sender, RoutedEventArgs e)
        {
            _surface.CycleThickness(1);
            ThicknessBtn.Content = $"{_surface.PenThickness:0} px";
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void Fill_Click(object sender, RoutedEventArgs e)
        {
            _surface.FillShapes = FillToggle.IsChecked == true;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // ── zoom and editing ──────────────────────────────────────────────────

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            _zoom?.ZoomIn();
            UpdateZoomLabel();
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            _zoom?.ZoomOut();
            UpdateZoomLabel();
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            _surface.Undo();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _surface.ClearAll();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
