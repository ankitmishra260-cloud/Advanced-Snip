using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    /// <summary>
    /// What the pointer does on the recording surface. <see cref="None"/> means the
    /// overlay is click-through and the screen behaves normally.
    /// </summary>
    public enum RecordTool
    {
        None,
        Pen,
        Marker,
        Arrow,
        Line,
        Rectangle,
        Ellipse,
        Spotlight,
        Redact,
        Step
    }

    /// <summary>
    /// The surface the user draws on during a recording.
    ///
    /// It is deliberately NOT the thing that ends up in the video. This window is excluded
    /// from screen capture and exists purely so the user can see their own strokes as they
    /// make them; the recorded frames get the same strokes and shapes painted in by
    /// <see cref="AnnotationLayer"/> and <see cref="ShapeLayer"/> on the capture thread.
    /// Doing it that way avoids BitBlt's CAPTUREBLT flag, which is the only way to capture
    /// a layered window and which makes the whole desktop strobe when it fires thirty
    /// times a second.
    ///
    /// **This window swallows mouse input while a tool is active, and that makes it
    /// dangerous.** A topmost, click-accepting window is one mistake away from locking the
    /// user out of their own desktop, which is exactly what an earlier version did:
    /// clicking it raised it above the toolbar that turns the pen off, and with nothing
    /// else clickable the only way out was a reboot. Five independent things now prevent
    /// that, and none of them depends on any of the others:
    ///
    ///   1. WS_EX_NOACTIVATE — clicking here never activates this window, so it can never
    ///      be promoted above the HUD in the topmost band.
    ///   2. The HUD is explicitly re-asserted above this window every time a tool goes on.
    ///   3. Right-click anywhere on this surface puts the tools away.
    ///   4. Escape is registered as a system-wide hotkey for as long as a tool is active.
    ///   5. A badge at the top of the region names the active tool and how to leave it,
    ///      so nobody has to work out *why* their clicks stopped landing.
    ///
    /// It also covers only the rectangle being recorded, so on a multi-monitor desktop the
    /// other monitors keep working normally no matter what happens here.
    ///
    /// **Freehand and shapes are separate layers on purpose.** A stroke is an append-only
    /// list of points; a shape is two points that stay editable until the button comes up.
    /// Undo spans both, which is what the <see cref="_history"/> list below is for — it
    /// records which layer each item went into so they can be taken back in the order they
    /// were made rather than one layer at a time.
    /// </summary>
    public partial class RecordAnnotationWindow : Window
    {
        private enum Layer { Ink, Shape }

        private readonly AnnotationLayer _ink;
        private readonly ShapeLayer _shapes;
        private readonly System.Drawing.Rectangle _region;

        /// <summary>Which layer each committed item went into, newest last.</summary>
        private readonly List<Layer> _history = new();

        // The state captured by the last erase-everything, so one Undo puts it back. Null
        // once anything new is drawn — restoring an erase after new work would interleave
        // the two in an order nobody asked for.
        private InkSnapshot? _clearedInk;
        private ShapeSnapshot? _clearedShapes;
        private List<Layer>? _clearedHistory;
        private UIElement[]? _clearedInkVisuals;
        private UIElement[]? _clearedShapeVisuals;

        private bool _drawing;
        private Polyline? _live;
        private System.Windows.Point _shapeOrigin;
        private IntPtr _handle;
        private ZoomController? _zoom;
        private System.Windows.Threading.DispatcherTimer? _zoomTimer;

        /// <summary>
        /// What this drag is actually drawing, which is not always the selected tool — see
        /// <see cref="ResolveTool"/>. Held for the length of one press so that letting go
        /// of Shift halfway through a line does not turn it into a scribble.
        /// </summary>
        private RecordTool _activeTool = RecordTool.None;

        /// <summary>The tool the pointer would use if it were pressed right now.</summary>
        private RecordTool _hoverTool = RecordTool.None;

        /// <summary>The colours the wheel and the picker both cycle through.</summary>
        public static readonly Color[] Colours =
        {
            Color.FromRgb(0xEF, 0x44, 0x44),   // red
            Color.FromRgb(0xFA, 0xCC, 0x15),   // yellow
            Color.FromRgb(0x22, 0xC5, 0x5E),   // green
            Color.FromRgb(0x3B, 0x82, 0xF6),   // blue
            Color.FromRgb(0xA8, 0x55, 0xF7),   // purple
            Color.FromRgb(0xFF, 0xFF, 0xFF),   // white
        };

        /// <summary>The stroke widths the wheel and the picker both cycle through.</summary>
        public static readonly double[] Widths = { 2, 4, 8, 14, 22 };

        /// <summary>The active tool. <see cref="RecordTool.None"/> means click-through.</summary>
        public RecordTool Tool { get; private set; } = RecordTool.None;

        /// <summary>Whether the surface is accepting clicks at all.</summary>
        public bool PenEnabled => Tool != RecordTool.None;

        /// <summary>
        /// Raised whenever the tool changes, including when the user escapes out of one
        /// here rather than from the toolbar, so the HUD and the palette can follow.
        /// </summary>
        public event EventHandler<RecordTool>? ToolChanged;

        /// <summary>
        /// Kept for the callers that only care whether the surface is live — the Escape
        /// hotkey registration, chiefly.
        /// </summary>
        public event EventHandler<bool>? PenEnabledChanged;

        private Color _penColour = Color.FromRgb(0xEF, 0x44, 0x44);
        private double _penThickness = 4;

        /// <summary>
        /// Colour and width raise <see cref="SettingsChanged"/> because they are no longer
        /// only set from the picker: the wheel changes them too, and when it does, the
        /// picker and the toolbar have to catch up without being told individually.
        /// </summary>
        public Color PenColour
        {
            get => _penColour;
            set
            {
                if (_penColour == value) return;
                _penColour = value;
                UpdateBadge();
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public double PenThickness
        {
            get => _penThickness;
            set
            {
                if (Math.Abs(_penThickness - value) < 0.01) return;
                _penThickness = value;
                UpdateBadge();
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Shade the inside of boxes and circles.</summary>
        public bool FillShapes { get; set; }

        /// <summary>True when the active tool paints freehand rather than a shape.</summary>
        public bool Highlighter => Tool == RecordTool.Marker;

        /// <summary>Raised when the marker is toggled, so the HUD can follow.</summary>
        public event EventHandler<bool>? HighlighterChanged;

        /// <summary>Raised when the colour, the width or the fill changes.</summary>
        public event EventHandler? SettingsChanged;

        /// <summary>
        /// Raised when something is drawn, undone or erased.
        ///
        /// The toolbar uses it to decide whether Undo and Clear are worth showing. They
        /// used to appear only while a tool was active, which put them out of reach in
        /// exactly the situation that needs them: the drawings stay on screen after Escape,
        /// so someone who has finished annotating and gone back to clicking things had
        /// marks over their demo and no visible way to remove them.
        /// </summary>
        public event EventHandler? ContentChanged;

        /// <summary>
        /// True when there is anything on screen to undo or erase.
        ///
        /// Named <c>HasDrawings</c> and not <c>HasContent</c>: <see cref="Window"/> inherits
        /// a <c>HasContent</c> from <see cref="System.Windows.Controls.ContentControl"/>,
        /// and the obvious name silently hid it (CS0108). Hiding it would have been the
        /// wrong fix — the base property answers "is anything set as this control's
        /// Content", which for this window is permanently true, so anything reaching it
        /// through the base type would have got a confident wrong answer.
        /// </summary>
        public bool HasDrawings => _ink.HasInk || _shapes.HasShapes || _history.Count > 0;

        /// <summary>True when the last erase-everything can still be taken back.</summary>
        public bool CanUndoClear => _clearedInk != null;

        public RecordAnnotationWindow(AnnotationLayer ink, ShapeLayer shapes,
                                      System.Drawing.Rectangle region)
        {
            InitializeComponent();

            _ink = ink;
            _shapes = shapes;
            _region = region;

            // Same treatment the capture overlays get: one layout unit is one physical
            // pixel on every monitor, whatever its scale factor. Without it a stroke drawn
            // on a 150% display lands somewhere else in the recording.
            OverlayHost.MakeRegionOverlay(this, RootGrid, region);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _handle = new WindowInteropHelper(this).Handle;

            // Keep it out of Alt+Tab, out of the recording, and — the important one —
            // unable to take activation. Without WS_EX_NOACTIVATE the first stroke raises
            // this window to the top of the topmost band, burying the toolbar that is the
            // only way to turn the tools off again.
            int ex = NativeMethods.GetWindowLong(_handle, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(_handle, NativeMethods.GWL_EXSTYLE,
                                        ex | NativeMethods.WS_EX_TOOLWINDOW
                                           | Win32.WS_EX_NOACTIVATE);
            try { Win32.SetWindowDisplayAffinity(_handle, Win32.WDA_EXCLUDEFROMCAPTURE); } catch { }

            ApplyClickThrough();
        }

        // ── tool selection ────────────────────────────────────────────────────

        /// <summary>Switches tool, and with it whether clicks land here at all.</summary>
        public void SetTool(RecordTool tool)
        {
            if (Tool == tool) { ApplyClickThrough(); return; }

            bool wasEnabled = PenEnabled;
            bool wasMarker = Highlighter;

            // Anything half-drawn must not survive a tool change.
            EndStroke(commit: false);

            Tool = tool;
            _activeTool = tool;
            _hoverTool = RecordTool.None;

            ApplyClickThrough();
            UpdateBadge();

            ToolChanged?.Invoke(this, tool);
            if (wasEnabled != PenEnabled) PenEnabledChanged?.Invoke(this, PenEnabled);
            if (wasMarker != Highlighter) HighlighterChanged?.Invoke(this, Highlighter);
        }

        /// <summary>
        /// Kept so the existing hotkey and HUD toggle keep working unchanged. Turning the
        /// pen "on" means the pen specifically; turning it off means no tool at all.
        /// </summary>
        public void SetPenEnabled(bool enabled)
            => SetTool(enabled ? RecordTool.Pen : RecordTool.None);

        /// <summary>
        /// Marker on/off. Turning it on when it is already on falls back to the pen rather
        /// than to nothing, because someone pressing the marker key twice wants to keep
        /// drawing.
        /// </summary>
        public void ToggleHighlighter()
            => SetTool(Tool == RecordTool.Marker ? RecordTool.Pen : RecordTool.Marker);

        /// <summary>Steps through the drawing tools in toolbar order, skipping "off".</summary>
        public void CycleTool(int direction)
        {
            var order = new[]
            {
                RecordTool.Pen, RecordTool.Marker, RecordTool.Arrow, RecordTool.Line,
                RecordTool.Rectangle, RecordTool.Ellipse, RecordTool.Spotlight,
                RecordTool.Redact, RecordTool.Step
            };

            int index = Array.IndexOf(order, Tool);
            if (index < 0) { SetTool(order[0]); return; }

            index = (index + direction + order.Length) % order.Length;
            SetTool(order[index]);
        }

        /// <summary>
        /// Works out what a press right now would draw.
        ///
        /// **This is the change that removes most of the tool switching.** The complaint it
        /// answers is that annotating a demo means opening the picker, choosing, drawing one
        /// thing, and opening it again — over and over, with the pointer travelling away
        /// from the thing being pointed at every time. Almost all of that traffic is between
        /// the pen and three shapes, so the three shapes are put on modifiers instead:
        /// from the pen or marker, holding Shift draws a line, Ctrl draws an arrow, and
        /// Ctrl+Shift draws a box. Nothing is selected, nothing is dismissed, and the hand
        /// never leaves the work.
        ///
        /// Ctrl carries the arrow rather than the box because in a screen demo the arrow is
        /// the shape people reach for most, and Ctrl is the modifier they can hold without
        /// looking. Alt is deliberately unused: it opens menu bars in the application being
        /// recorded, which would be visible in the take.
        ///
        /// The modifiers are read from the OS rather than from WPF. This window is
        /// WS_EX_NOACTIVATE and never holds keyboard focus, so its own idea of what is held
        /// down is not something to build on.
        /// </summary>
        private RecordTool ResolveTool()
        {
            if (Tool is not (RecordTool.Pen or RecordTool.Marker)) return Tool;

            bool shift = Win32.ShiftDown;
            bool ctrl = Win32.CtrlDown;

            if (ctrl && shift) return RecordTool.Rectangle;
            if (ctrl) return RecordTool.Arrow;
            if (shift) return RecordTool.Line;
            return Tool;
        }

        /// <summary>
        /// Steps through the colours. Bound to Shift+wheel over the drawing surface, which
        /// is input that was previously going nowhere: while a tool is active the overlay
        /// swallows the wheel anyway, so the only question was whether to waste it.
        /// </summary>
        public void CycleColour(int direction)
        {
            int index = Array.IndexOf(Colours, PenColour);
            if (index < 0) index = 0;
            index = (index + direction + Colours.Length) % Colours.Length;
            PenColour = Colours[index];
        }

        /// <summary>Steps through the stroke widths. Bound to the plain wheel.</summary>
        public void CycleThickness(int direction)
        {
            int index = Array.IndexOf(Widths, PenThickness);
            if (index < 0) index = 1;
            index = Math.Clamp(index + direction, 0, Widths.Length - 1);
            PenThickness = Widths[index];
        }

        private void ApplyClickThrough()
        {
            if (_handle == IntPtr.Zero) return;

            int ex = NativeMethods.GetWindowLong(_handle, NativeMethods.GWL_EXSTYLE);
            ex = PenEnabled
                ? ex & ~Win32.WS_EX_TRANSPARENT
                : ex | Win32.WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLong(_handle, NativeMethods.GWL_EXSTYLE, ex);

            ApplyCursor(Tool);
        }

        private void ApplyCursor(RecordTool tool)
        {
            RootGrid.Cursor = tool switch
            {
                RecordTool.None => Cursors.Arrow,
                RecordTool.Pen or RecordTool.Marker => Cursors.Pen,
                RecordTool.Step => Cursors.Hand,
                _ => Cursors.Cross
            };
        }

        /// <summary>The short name shown on the badge and the toolbar chip.</summary>
        public static string NameOf(RecordTool tool) => tool switch
        {
            RecordTool.Pen => "Pen",
            RecordTool.Marker => "Marker",
            RecordTool.Arrow => "Arrow",
            RecordTool.Line => "Line",
            RecordTool.Rectangle => "Box",
            RecordTool.Ellipse => "Circle",
            RecordTool.Spotlight => "Spotlight",
            RecordTool.Redact => "Hide",
            RecordTool.Step => "Step",
            _ => ""
        };

        private void UpdateBadge()
        {
            if (Tool == RecordTool.None)
            {
                ToolBadge.Visibility = Visibility.Collapsed;
                return;
            }

            // While a modifier is held the badge names the shape it would draw, not the
            // tool that is selected. Announcing "Pen" to someone holding Ctrl over an
            // arrow-shaped cursor would be the one place the app disagrees with itself.
            var shown = _drawing ? _activeTool : _hoverTool;
            if (shown == RecordTool.None) shown = Tool;

            BadgeText.Text = shown == RecordTool.Step
                ? $"Step {_shapes.NextStep}"
                : NameOf(shown);

            BadgeSize.Text = $"{PenThickness:0}px";
            BadgeHint.Text = "Esc or right-click to exit";
            BadgeDot.Fill = new SolidColorBrush(PenColour);

            bool freehand = Tool is RecordTool.Pen or RecordTool.Marker;
            BadgeMods.Visibility = freehand ? Visibility.Visible : Visibility.Collapsed;
            if (freehand)
                BadgeMods.Text = "Hold Shift line · Ctrl arrow · Ctrl+Shift box   ·   wheel size, Shift+wheel colour";

            ToolBadge.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Shows the live zoom viewport on screen. The presenter needs this: the zoom only
        /// exists in the recorded frames, so without an outline they would be moving the
        /// pointer around with no idea what the viewer can actually see.
        /// </summary>
        public void AttachZoom(ZoomController zoom)
        {
            _zoom = zoom;

            _zoomTimer = new System.Windows.Threading.DispatcherTimer
            {
                // 25 Hz. The outline is a guide, not the recording — matching the capture
                // frame rate here would cost more than it shows.
                Interval = TimeSpan.FromMilliseconds(40)
            };
            _zoomTimer.Tick += (_, _) => UpdateZoomBox();
            _zoomTimer.Start();
        }

        private void UpdateZoomBox()
        {
            if (_zoom == null) return;

            if (!_zoom.IsZoomed)
            {
                if (ZoomLayer.Visibility != Visibility.Collapsed)
                    ZoomLayer.Visibility = Visibility.Collapsed;
                return;
            }

            var v = _zoom.Peek();
            ZoomLayer.Visibility = Visibility.Visible;
            Canvas.SetLeft(ZoomBox, v.X - _region.X);
            Canvas.SetTop(ZoomBox, v.Y - _region.Y);
            ZoomBox.Width = Math.Max(0, v.Width);
            ZoomBox.Height = Math.Max(0, v.Height);
        }

        // ── undo and clear, across both layers ────────────────────────────────

        /// <summary>
        /// Removes the most recent thing drawn, whichever layer it came from.
        ///
        /// Undoing per-layer would be maddening in practice: draw an arrow, scribble on
        /// it, press undo, and watch the arrow vanish while the scribble stays.
        /// </summary>
        public void Undo()
        {
            // An erase is undone before anything else, because after one there is nothing
            // else left to undo — and because a Clear that cannot be taken back is a button
            // people learn to be careful of, which is the opposite of what it is for.
            if (_history.Count == 0 && _clearedInk != null)
            {
                RestoreCleared();
                return;
            }

            if (_history.Count == 0)
            {
                // The history can only be short of the layers if something was committed
                // before this window learned about it; fall back to whichever has content.
                if (_shapes.Undo()) RemoveLastShapeVisual();
                else if (_ink.Undo()) RemoveLastInkVisual();
                UpdateBadge();
                ContentChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            var last = _history[^1];
            _history.RemoveAt(_history.Count - 1);

            if (last == Layer.Shape) { _shapes.Undo(); RemoveLastShapeVisual(); }
            else { _ink.Undo(); RemoveLastInkVisual(); }

            UpdateBadge();
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RestoreCleared()
        {
            if (_clearedInk == null || _clearedShapes == null) return;

            _ink.Restore(_clearedInk);
            _shapes.Restore(_clearedShapes);

            _history.Clear();
            if (_clearedHistory != null) _history.AddRange(_clearedHistory);

            InkLayer.Children.Clear();
            ShapeLayerCanvas.Children.Clear();

            // The visuals are the same objects that were on the canvases before the erase,
            // put back in the same order, so what returns is what was there rather than a
            // redrawn approximation of it.
            if (_clearedShapeVisuals != null)
                foreach (var v in _clearedShapeVisuals) ShapeLayerCanvas.Children.Add(v);
            if (_clearedInkVisuals != null)
                foreach (var v in _clearedInkVisuals) InkLayer.Children.Add(v);

            ForgetCleared();
            UpdateBadge();
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ForgetCleared()
        {
            _clearedInk = null;
            _clearedShapes = null;
            _clearedHistory = null;
            _clearedInkVisuals = null;
            _clearedShapeVisuals = null;
        }

        private void RemoveLastInkVisual()
        {
            if (InkLayer.Children.Count > 0)
                InkLayer.Children.RemoveAt(InkLayer.Children.Count - 1);
        }

        private void RemoveLastShapeVisual()
        {
            if (ShapeLayerCanvas.Children.Count > 0)
                ShapeLayerCanvas.Children.RemoveAt(ShapeLayerCanvas.Children.Count - 1);
        }

        /// <summary>
        /// Erases everything drawn, on both layers — and keeps a copy, so one Undo brings
        /// it all back.
        /// </summary>
        public void ClearAll()
        {
            // Clearing an empty screen must not overwrite a snapshot that is still worth
            // something: pressing the erase key twice should not cost you the first erase.
            if (!HasDrawings) return;

            EndStroke(commit: false);

            _clearedInk = _ink.Snapshot();
            _clearedShapes = _shapes.Snapshot();
            _clearedHistory = new List<Layer>(_history);
            _clearedInkVisuals = ToArray(InkLayer.Children);
            _clearedShapeVisuals = ToArray(ShapeLayerCanvas.Children);

            _ink.Clear();
            _shapes.Clear();
            _history.Clear();
            InkLayer.Children.Clear();
            ShapeLayerCanvas.Children.Clear();
            PreviewLayer.Children.Clear();

            UpdateBadge();
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        private static UIElement[] ToArray(UIElementCollection children)
        {
            var copy = new UIElement[children.Count];
            for (int i = 0; i < children.Count; i++) copy[i] = children[i];
            return copy;
        }

        /// <summary>Kept for existing callers.</summary>
        public void ClearInk() => ClearAll();

        // ── drawing ───────────────────────────────────────────────────────────

        private void Root_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!PenEnabled) return;

            var p = e.GetPosition(RootGrid);

            // Latched here and not re-read during the drag: releasing Shift halfway through
            // a line should finish the line, not turn the rest of it into a scribble.
            _activeTool = ResolveTool();

            _drawing = true;
            RootGrid.CaptureMouse();
            UpdateBadge();

            if (_activeTool is RecordTool.Pen or RecordTool.Marker) BeginStroke(p);
            else BeginShape(p);
        }

        private void Root_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_drawing)
            {
                // Not drawing, but the pointer is over a live surface: show what a press
                // would do right now. This is the only feedback a held modifier can get —
                // the window never has keyboard focus, so there is no key event to hang it
                // on, and moving the mouse is what someone about to draw is already doing.
                if (PenEnabled) UpdateHover();
                return;
            }

            // Self-heal. A non-activated window is not guaranteed to keep mouse capture,
            // and if the button came up somewhere we never saw it, the stroke would go on
            // following the pointer forever. Trusting the button's actual state costs
            // nothing and cannot get stuck.
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndStroke(commit: true);
                return;
            }

            var p = e.GetPosition(RootGrid);

            if (_activeTool is RecordTool.Pen or RecordTool.Marker) ExtendStroke(p);
            else ExtendShape(p);
        }

        private void UpdateHover()
        {
            var resolved = ResolveTool();
            if (resolved == _hoverTool) return;

            _hoverTool = resolved;
            ApplyCursor(resolved);
            UpdateBadge();
        }

        private void Root_MouseUp(object sender, MouseButtonEventArgs e)
            => EndStroke(commit: true);

        /// <summary>
        /// The wheel over a live drawing surface.
        ///
        /// While a tool is active this window swallows the wheel anyway — the application
        /// underneath cannot be scrolled until the tools are put away — so the choice was
        /// between doing nothing with it and doing something useful. Width and colour are
        /// what people reach for between strokes, and this reaches them without opening
        /// anything, which is the whole point of the exercise.
        /// </summary>
        private void Root_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!PenEnabled) return;
            e.Handled = true;

            int direction = e.Delta > 0 ? 1 : -1;

            if (Win32.CtrlDown) { if (direction > 0) _zoom?.ZoomIn(); else _zoom?.ZoomOut(); }
            else if (Win32.ShiftDown) CycleColour(direction);
            else CycleThickness(direction);
        }

        // ── freehand ──────────────────────────────────────────────────────────

        /// <summary>True when the drag in progress is painting freehand marker strokes.</summary>
        private bool ActiveHighlighter => _activeTool == RecordTool.Marker;

        private void BeginStroke(System.Windows.Point p)
        {
            bool marker = ActiveHighlighter;

            _live = new Polyline
            {
                Stroke = new SolidColorBrush(marker
                    ? Color.FromArgb(90, PenColour.R, PenColour.G, PenColour.B)
                    : PenColour),
                StrokeThickness = marker ? PenThickness * 3 : PenThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false
            };
            _live.Points.Add(p);
            InkLayer.Children.Add(_live);

            _ink.Begin(System.Drawing.Color.FromArgb(255, PenColour.R, PenColour.G, PenColour.B),
                       (float)(marker ? PenThickness * 3 : PenThickness), marker);
            _ink.AddPoint((float)(p.X + _region.X), (float)(p.Y + _region.Y));
        }

        private void ExtendStroke(System.Windows.Point p)
        {
            if (_live == null) return;
            _live.Points.Add(p);

            // The layer stores absolute desktop coordinates, because the recorder places
            // strokes against its own capture rectangle and knows nothing about this
            // window. This overlay is positioned at the capture rectangle, so its local
            // origin is that rectangle's top-left.
            _ink.AddPoint((float)(p.X + _region.X), (float)(p.Y + _region.Y));
        }

        // ── shapes ────────────────────────────────────────────────────────────

        private void BeginShape(System.Windows.Point p)
        {
            _shapeOrigin = p;

            _shapes.Begin(KindOf(_activeTool),
                          new System.Drawing.PointF((float)(p.X + _region.X),
                                                    (float)(p.Y + _region.Y)),
                          System.Drawing.Color.FromArgb(255, PenColour.R, PenColour.G, PenColour.B),
                          (float)PenThickness,
                          FillShapes);

            DrawPreview(p, p);
        }

        private void ExtendShape(System.Windows.Point p)
        {
            p = Constrain(_shapeOrigin, p);

            _shapes.Update(new System.Drawing.PointF((float)(p.X + _region.X),
                                                     (float)(p.Y + _region.Y)));
            DrawPreview(_shapeOrigin, p);
        }

        /// <summary>
        /// Shift while dragging a shape you chose yourself squares off a box, rounds a
        /// circle and snaps a line or arrow to the nearest 45°, which is what Shift does in
        /// every drawing application there has ever been.
        ///
        /// It deliberately does nothing when the shape came from a modifier in the first
        /// place: holding Shift over the pen already *means* "line", and having it also
        /// mean "snap that line to 45°" would leave no way to draw a line at any other
        /// angle without first selecting the tool the modifier exists to avoid selecting.
        /// </summary>
        private System.Windows.Point Constrain(System.Windows.Point origin,
                                               System.Windows.Point p)
        {
            if (_activeTool != Tool) return p;          // the modifier chose the shape
            if (!Win32.ShiftDown) return p;

            double dx = p.X - origin.X, dy = p.Y - origin.Y;

            if (_activeTool is RecordTool.Line or RecordTool.Arrow)
            {
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1) return p;

                double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
                return new System.Windows.Point(origin.X + Math.Cos(angle) * length,
                                                origin.Y + Math.Sin(angle) * length);
            }

            if (_activeTool is RecordTool.Rectangle or RecordTool.Ellipse
                             or RecordTool.Spotlight or RecordTool.Redact)
            {
                double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
                return new System.Windows.Point(origin.X + Math.Sign(dx == 0 ? 1 : dx) * side,
                                                origin.Y + Math.Sign(dy == 0 ? 1 : dy) * side);
            }

            return p;
        }

        private static RecordShape KindOf(RecordTool tool) => tool switch
        {
            RecordTool.Arrow => RecordShape.Arrow,
            RecordTool.Line => RecordShape.Line,
            RecordTool.Rectangle => RecordShape.Rectangle,
            RecordTool.Ellipse => RecordShape.Ellipse,
            RecordTool.Spotlight => RecordShape.Spotlight,
            RecordTool.Redact => RecordShape.Redact,
            _ => RecordShape.Step
        };

        /// <summary>
        /// Redraws the shape being dragged out.
        ///
        /// The preview is thrown away and rebuilt on every mouse-move rather than mutated,
        /// which sounds wasteful and is not: it is one shape, and the alternative is
        /// per-shape update code that has to agree with the drawing code below about
        /// exactly what an arrow looks like. One place to be wrong is better than two.
        /// </summary>
        private void DrawPreview(System.Windows.Point a, System.Windows.Point b)
        {
            PreviewLayer.Children.Clear();

            var brush = new SolidColorBrush(PenColour);
            double thickness = PenThickness;

            switch (_activeTool)
            {
                case RecordTool.Arrow:
                    AddArrowPreview(a, b, brush, thickness);
                    break;

                case RecordTool.Line:
                    PreviewLayer.Children.Add(new Line
                    {
                        X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
                        Stroke = brush, StrokeThickness = thickness,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    });
                    break;

                case RecordTool.Rectangle:
                    AddBoxPreview(a, b, brush, thickness, ellipse: false);
                    break;

                case RecordTool.Ellipse:
                    AddBoxPreview(a, b, brush, thickness, ellipse: true);
                    break;

                case RecordTool.Spotlight:
                    AddSpotlightPreview(a, b);
                    break;

                case RecordTool.Redact:
                    AddRedactPreview(a, b);
                    break;

                case RecordTool.Step:
                    AddStepPreview(a);
                    break;
            }
        }

        private void AddArrowPreview(System.Windows.Point a, System.Windows.Point b,
                                     Brush brush, double thickness)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1) return;

            double head = Math.Min(Math.Max(thickness * 4, 14), length * 0.4);
            double ux = dx / length, uy = dy / length;

            PreviewLayer.Children.Add(new Line
            {
                X1 = a.X, Y1 = a.Y,
                X2 = b.X - ux * head * 0.75, Y2 = b.Y - uy * head * 0.75,
                Stroke = brush, StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round
            });

            double wing = head * 0.45;
            double bx = b.X - ux * head, by = b.Y - uy * head;

            var tip = new Polygon { Fill = brush };
            tip.Points.Add(b);
            tip.Points.Add(new System.Windows.Point(bx - uy * wing, by + ux * wing));
            tip.Points.Add(new System.Windows.Point(bx + uy * wing, by - ux * wing));
            PreviewLayer.Children.Add(tip);
        }

        private void AddBoxPreview(System.Windows.Point a, System.Windows.Point b,
                                   Brush brush, double thickness, bool ellipse)
        {
            var r = Normalise(a, b);
            if (r.Width < 1 || r.Height < 1) return;

            Brush? fill = FillShapes
                ? new SolidColorBrush(Color.FromArgb(70, PenColour.R, PenColour.G, PenColour.B))
                : null;

            System.Windows.Shapes.Shape shape;
            if (ellipse) shape = new Ellipse { Width = r.Width, Height = r.Height };
            else shape = new System.Windows.Shapes.Rectangle { Width = r.Width, Height = r.Height };

            shape.Stroke = brush;
            shape.StrokeThickness = thickness;
            shape.Fill = fill;

            Canvas.SetLeft(shape, r.X);
            Canvas.SetTop(shape, r.Y);
            PreviewLayer.Children.Add(shape);
        }

        /// <summary>
        /// Four dimming bands and an outline — the same construction the recorder uses, so
        /// what the presenter sees is what the viewer gets.
        /// </summary>
        private void AddSpotlightPreview(System.Windows.Point a, System.Windows.Point b)
        {
            var hole = Normalise(a, b);
            if (hole.Width < 2 || hole.Height < 2) return;

            double w = _region.Width, h = _region.Height;
            double left = Math.Max(0, hole.X), top = Math.Max(0, hole.Y);
            double right = Math.Min(w, hole.Right), bottom = Math.Min(h, hole.Bottom);

            var shade = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));

            void Band(double x, double y, double bw, double bh)
            {
                if (bw <= 0 || bh <= 0) return;
                var band = new System.Windows.Shapes.Rectangle
                {
                    Width = bw, Height = bh, Fill = shade
                };
                Canvas.SetLeft(band, x);
                Canvas.SetTop(band, y);
                PreviewLayer.Children.Add(band);
            }

            Band(0, 0, w, top);
            Band(0, bottom, w, h - bottom);
            Band(0, top, left, bottom - top);
            Band(right, top, w - right, bottom - top);

            var edge = new System.Windows.Shapes.Rectangle
            {
                Width = right - left,
                Height = bottom - top,
                Stroke = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
                StrokeThickness = 1.5
            };
            Canvas.SetLeft(edge, left);
            Canvas.SetTop(edge, top);
            PreviewLayer.Children.Add(edge);
        }

        private void AddRedactPreview(System.Windows.Point a, System.Windows.Point b)
        {
            var r = Normalise(a, b);
            if (r.Width < 1 || r.Height < 1) return;

            var block = new System.Windows.Shapes.Rectangle
            {
                Width = r.Width,
                Height = r.Height,
                Fill = new SolidColorBrush(Color.FromRgb(24, 24, 27))
            };
            Canvas.SetLeft(block, r.X);
            Canvas.SetTop(block, r.Y);
            PreviewLayer.Children.Add(block);
        }

        private void AddStepPreview(System.Windows.Point at)
        {
            double radius = Math.Max(16, PenThickness * 5);

            var disc = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Fill = new SolidColorBrush(PenColour),
                Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                StrokeThickness = 2
            };
            Canvas.SetLeft(disc, at.X - radius);
            Canvas.SetTop(disc, at.Y - radius);
            PreviewLayer.Children.Add(disc);

            var text = new TextBlock
            {
                Text = _shapes.NextStep.ToString(),
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.Bold,
                FontSize = radius * 0.95,
                Width = radius * 2,
                TextAlignment = TextAlignment.Center
            };
            Canvas.SetLeft(text, at.X - radius);
            Canvas.SetTop(text, at.Y - radius * 0.75);
            PreviewLayer.Children.Add(text);
        }

        private static Rect Normalise(System.Windows.Point a, System.Windows.Point b)
            => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                   Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

        // ── finishing ─────────────────────────────────────────────────────────

        private void EndStroke(bool commit)
        {
            if (!_drawing) return;
            _drawing = false;
            try { RootGrid.ReleaseMouseCapture(); } catch { }

            if (_activeTool is RecordTool.Pen or RecordTool.Marker)
            {
                if (commit)
                {
                    _ink.End();
                    if (_live != null && _live.Points.Count > 0)
                    {
                        _history.Add(Layer.Ink);
                        // Real new work makes a previous erase unrecoverable: putting the
                        // old drawings back underneath something drawn since would restore
                        // them into a picture they were never part of. A drag that produced
                        // nothing is not new work, so a stray click does not cost you the
                        // ability to undo an erase.
                        ForgetCleared();
                    }
                    else if (_live != null) InkLayer.Children.Remove(_live);
                }
                else
                {
                    _ink.Cancel();
                    if (_live != null) InkLayer.Children.Remove(_live);
                }
                _live = null;

                _activeTool = Tool;
                _hoverTool = RecordTool.None;
                UpdateBadge();
                ContentChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            // Shapes: the preview canvas is emptied either way. On commit its contents are
            // moved onto the permanent canvas rather than redrawn, so what the user watched
            // themselves position is exactly what stays.
            bool kept = false;
            if (commit) kept = _shapes.End();
            else _shapes.Cancel();

            if (kept)
            {
                var group = new Canvas { IsHitTestVisible = false };
                while (PreviewLayer.Children.Count > 0)
                {
                    var child = PreviewLayer.Children[0];
                    PreviewLayer.Children.RemoveAt(0);
                    group.Children.Add(child);
                }
                ShapeLayerCanvas.Children.Add(group);
                _history.Add(Layer.Shape);
                ForgetCleared();          // see the note in the freehand branch above
            }
            else
            {
                PreviewLayer.Children.Clear();
            }

            // Back to whatever is actually selected: the modifier only ever borrowed the
            // pointer for one drag.
            _activeTool = Tool;
            _hoverTool = RecordTool.None;
            ApplyCursor(Tool);

            // A step badge advances the counter, so the badge has to catch up.
            UpdateBadge();
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Escape hatch number three. Right-clicking is the first thing most people try
        /// when a surface stops behaving, and it costs nothing to make that the way out.
        /// </summary>
        private void Root_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (!PenEnabled) return;
            e.Handled = true;
            SetTool(RecordTool.None);
        }

        protected override void OnClosed(EventArgs e)
        {
            // Restore click-through before the window goes away. If anything below throws,
            // the desktop must not be left with an input-swallowing window over it.
            Tool = RecordTool.None;
            try { ApplyClickThrough(); } catch { }

            try { _zoomTimer?.Stop(); } catch { }
            _zoomTimer = null;

            base.OnClosed(e);
            InkLayer.Children.Clear();
            ShapeLayerCanvas.Children.Clear();
            PreviewLayer.Children.Clear();
            _live = null;
        }
    }
}
