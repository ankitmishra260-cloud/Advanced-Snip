using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace AdvancedSnip.Services
{
    /// <summary>What a shape tool draws. Order matches the toolbar.</summary>
    public enum RecordShape
    {
        Arrow,
        Line,
        Rectangle,
        Ellipse,
        /// <summary>Dims everything except a rectangle, to point at one part of the screen.</summary>
        Spotlight,
        /// <summary>Solid block, for covering a password or a name mid-demo.</summary>
        Redact,
        /// <summary>Sequential 1, 2, 3 badges for a numbered walkthrough.</summary>
        Step
    }

    /// <summary>One finished or in-progress shape, in virtual-screen pixels.</summary>
    public sealed class DrawnShape
    {
        internal RecordShape Kind { get; init; }
        internal PointF Start { get; set; }
        internal PointF End { get; set; }
        internal Color Colour { get; init; } = Color.Red;
        internal float Thickness { get; init; } = 4f;
        internal bool Filled { get; init; }

        /// <summary>The number shown on a Step badge.</summary>
        internal int Number { get; init; }
    }

    /// <summary>
    /// A copy of the committed shapes and the step counter, taken before an
    /// erase-everything so it can be taken back.
    /// </summary>
    public sealed class ShapeSnapshot
    {
        internal DrawnShape[] Shapes = Array.Empty<DrawnShape>();
        internal int StepCounter;
    }

    /// <summary>
    /// Shapes drawn on screen while recording — arrows, boxes, circles, spotlights,
    /// numbered steps.
    ///
    /// This is the sibling of <see cref="AnnotationLayer"/> and shares its central
    /// decision: the overlay the user draws on is excluded from screen capture, and the
    /// same shapes are painted into each frame here with GDI+ on the capture thread. The
    /// reasoning is unchanged — capturing a layered window needs BitBlt's CAPTUREBLT,
    /// which repaints every layered window on the desktop, and at thirty frames a second
    /// that reads as the whole screen strobing.
    ///
    /// Shapes are kept separate from freehand ink rather than folded into one list
    /// because they behave differently: a shape is defined by two points and stays
    /// editable while the mouse is down, where a stroke is an append-only list of points.
    /// Undo spans both, which is handled by the overlay that owns them.
    ///
    /// One lock guards everything. The UI thread moves the shape being drawn as the
    /// pointer moves; the capture thread walks the list once per frame.
    /// </summary>
    public sealed class ShapeLayer
    {
        private readonly object _gate = new();
        private readonly List<DrawnShape> _shapes = new();
        private DrawnShape? _live;
        private int _stepCounter;

        internal bool HasShapes
        {
            get { lock (_gate) return _shapes.Count > 0 || _live != null; }
        }

        /// <summary>The number the next Step badge will carry.</summary>
        internal int NextStep { get { lock (_gate) return _stepCounter + 1; } }

        internal void Begin(RecordShape kind, PointF at, Color colour, float thickness, bool filled)
        {
            lock (_gate)
            {
                _live = new DrawnShape
                {
                    Kind = kind,
                    Start = at,
                    End = at,
                    Colour = colour,
                    Thickness = thickness,
                    Filled = filled,
                    Number = kind == RecordShape.Step ? _stepCounter + 1 : 0
                };
            }
        }

        internal void Update(PointF to)
        {
            lock (_gate)
            {
                if (_live != null) _live.End = to;
            }
        }

        /// <summary>Finishes the current shape. Returns false when it was too small to keep.</summary>
        internal bool End()
        {
            lock (_gate)
            {
                if (_live == null) return false;

                // A click that never became a drag is a mis-click for every shape except a
                // step badge, which is placed by a single click and has no size.
                bool tiny = Math.Abs(_live.End.X - _live.Start.X) < 4
                         && Math.Abs(_live.End.Y - _live.Start.Y) < 4;

                if (tiny && _live.Kind != RecordShape.Step)
                {
                    _live = null;
                    return false;
                }

                if (_live.Kind == RecordShape.Step) _stepCounter++;

                _shapes.Add(_live);
                _live = null;
                return true;
            }
        }

        /// <summary>
        /// Throws away the shape being dragged.
        ///
        /// Not the same as <see cref="End"/>: ending commits, and for a step badge also
        /// advances the counter. A tool change mid-drag has to discard.
        /// </summary>
        internal void Cancel()
        {
            lock (_gate) _live = null;
        }

        internal bool Undo()
        {
            lock (_gate)
            {
                if (_shapes.Count == 0) return false;

                var last = _shapes[^1];
                _shapes.RemoveAt(_shapes.Count - 1);

                // Removing the most recent badge should hand its number back, or the next
                // one drawn skips a digit.
                if (last.Kind == RecordShape.Step && _stepCounter > 0) _stepCounter--;
                return true;
            }
        }

        internal void Clear()
        {
            lock (_gate)
            {
                _shapes.Clear();
                _live = null;
                _stepCounter = 0;
            }
        }

        /// <summary>
        /// Copies the committed shapes out so a Clear can be taken back. The step counter
        /// goes with them — restoring five badges and then numbering the next one 1 would
        /// be worse than not restoring at all.
        /// </summary>
        internal ShapeSnapshot Snapshot()
        {
            lock (_gate)
                return new ShapeSnapshot { Shapes = _shapes.ToArray(), StepCounter = _stepCounter };
        }

        internal void Restore(ShapeSnapshot snapshot)
        {
            lock (_gate)
            {
                _shapes.Clear();
                _shapes.AddRange(snapshot.Shapes);
                _stepCounter = snapshot.StepCounter;
                _live = null;
            }
        }

        /// <summary>
        /// Paints every shape onto a captured frame. <paramref name="region"/> is the part
        /// of the desktop the frame covers, so shapes land in the right place at any zoom.
        /// </summary>
        internal void DrawOnto(Graphics g, Rectangle region)
        {
            DrawnShape[] snapshot;
            DrawnShape? live;
            PointF liveStart = default, liveEnd = default;

            lock (_gate)
            {
                if (_shapes.Count == 0 && _live == null) return;
                snapshot = _shapes.ToArray();
                live = _live;
                if (live != null) { liveStart = live.Start; liveEnd = live.End; }
            }

            var oldSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            try
            {
                foreach (var s in snapshot) Draw(g, s, s.Start, s.End, region);
                if (live != null) Draw(g, live, liveStart, liveEnd, region);
            }
            finally { g.SmoothingMode = oldSmoothing; }
        }

        private static void Draw(Graphics g, DrawnShape shape, PointF start, PointF end,
                                 Rectangle region)
        {
            var a = new PointF(start.X - region.X, start.Y - region.Y);
            var b = new PointF(end.X - region.X, end.Y - region.Y);

            switch (shape.Kind)
            {
                case RecordShape.Arrow: DrawArrow(g, shape, a, b); break;
                case RecordShape.Line: DrawLine(g, shape, a, b); break;
                case RecordShape.Rectangle: DrawRectangle(g, shape, a, b); break;
                case RecordShape.Ellipse: DrawEllipse(g, shape, a, b); break;
                case RecordShape.Spotlight: DrawSpotlight(g, a, b, region); break;
                case RecordShape.Redact: DrawRedact(g, a, b); break;
                case RecordShape.Step: DrawStep(g, shape, a); break;
            }
        }

        private static void DrawLine(Graphics g, DrawnShape shape, PointF a, PointF b)
        {
            using var pen = new Pen(shape.Colour, shape.Thickness)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawLine(pen, a, b);
        }

        /// <summary>
        /// An arrow with a solid head, drawn as a filled polygon rather than with GDI+'s
        /// own arrow cap. The built-in cap scales with pen width in a way that turns thin
        /// arrows into pinpricks and thick ones into blobs; a head sized from the pen
        /// width by an explicit factor stays proportional at every thickness.
        /// </summary>
        private static void DrawArrow(Graphics g, DrawnShape shape, PointF a, PointF b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1) return;

            float head = Math.Max(shape.Thickness * 4f, 14f);
            // A short arrow must not be all head. Cap it at 40% of the total length.
            head = (float)Math.Min(head, length * 0.4);

            double ux = dx / length, uy = dy / length;

            // Stop the shaft short of the tip so the line does not show through the head's
            // point, which looks like a burr at large thicknesses.
            var shaftEnd = new PointF((float)(b.X - ux * head * 0.75),
                                      (float)(b.Y - uy * head * 0.75));

            using (var pen = new Pen(shape.Colour, shape.Thickness)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Flat
            })
            {
                g.DrawLine(pen, a, shaftEnd);
            }

            float wing = head * 0.45f;
            var basePoint = new PointF((float)(b.X - ux * head), (float)(b.Y - uy * head));
            // Perpendicular to the shaft.
            var left = new PointF((float)(basePoint.X - uy * wing), (float)(basePoint.Y + ux * wing));
            var right = new PointF((float)(basePoint.X + uy * wing), (float)(basePoint.Y - ux * wing));

            using var brush = new SolidBrush(shape.Colour);
            g.FillPolygon(brush, new[] { b, left, right });
        }

        private static void DrawRectangle(Graphics g, DrawnShape shape, PointF a, PointF b)
        {
            var r = Normalise(a, b);
            if (r.Width < 1 || r.Height < 1) return;

            if (shape.Filled)
            {
                using var fill = new SolidBrush(Color.FromArgb(70, shape.Colour));
                g.FillRectangle(fill, r);
            }

            using var pen = new Pen(shape.Colour, shape.Thickness) { LineJoin = LineJoin.Round };
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        }

        private static void DrawEllipse(Graphics g, DrawnShape shape, PointF a, PointF b)
        {
            var r = Normalise(a, b);
            if (r.Width < 1 || r.Height < 1) return;

            if (shape.Filled)
            {
                using var fill = new SolidBrush(Color.FromArgb(70, shape.Colour));
                g.FillEllipse(fill, r);
            }

            using var pen = new Pen(shape.Colour, shape.Thickness);
            g.DrawEllipse(pen, r);
        }

        /// <summary>
        /// Dims the whole frame except one rectangle. Drawn as four bands rather than a
        /// full-frame fill with a cut-out region: a GraphicsPath with an exclusion is
        /// slower per frame and the bands are exact.
        /// </summary>
        private static void DrawSpotlight(Graphics g, PointF a, PointF b, Rectangle region)
        {
            var hole = Normalise(a, b);
            if (hole.Width < 2 || hole.Height < 2) return;

            int w = region.Width, h = region.Height;
            float left = Math.Max(0, hole.X);
            float top = Math.Max(0, hole.Y);
            float right = Math.Min(w, hole.Right);
            float bottom = Math.Min(h, hole.Bottom);

            using var shade = new SolidBrush(Color.FromArgb(140, 0, 0, 0));

            if (top > 0) g.FillRectangle(shade, 0, 0, w, top);
            if (bottom < h) g.FillRectangle(shade, 0, bottom, w, h - bottom);
            if (left > 0) g.FillRectangle(shade, 0, top, left, bottom - top);
            if (right < w) g.FillRectangle(shade, right, top, w - right, bottom - top);

            using var edge = new Pen(Color.FromArgb(190, 255, 255, 255), 1.5f);
            g.DrawRectangle(edge, left, top, right - left, bottom - top);
        }

        /// <summary>
        /// A solid block, the same choice the editor's redact tool makes: blurring and
        /// pixelating are both reversible to some degree, and a demo that shows a real
        /// password behind a blur has not hidden it.
        /// </summary>
        private static void DrawRedact(Graphics g, PointF a, PointF b)
        {
            var r = Normalise(a, b);
            if (r.Width < 1 || r.Height < 1) return;

            using var brush = new SolidBrush(Color.FromArgb(255, 24, 24, 27));
            g.FillRectangle(brush, r);
        }

        /// <summary>A numbered badge, for walking through steps in order.</summary>
        private static void DrawStep(Graphics g, DrawnShape shape, PointF at)
        {
            float radius = Math.Max(16f, shape.Thickness * 5f);

            using (var fill = new SolidBrush(shape.Colour))
                g.FillEllipse(fill, at.X - radius, at.Y - radius, radius * 2, radius * 2);

            using (var ring = new Pen(Color.FromArgb(220, 255, 255, 255), 2f))
                g.DrawEllipse(ring, at.X - radius, at.Y - radius, radius * 2, radius * 2);

            string text = shape.Number.ToString();
            using var font = new Font("Segoe UI", radius * 0.95f, FontStyle.Bold,
                                      GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            g.DrawString(text, font, textBrush,
                         new RectangleF(at.X - radius, at.Y - radius, radius * 2, radius * 2),
                         format);
        }

        private static RectangleF Normalise(PointF a, PointF b)
            => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                   Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }
}
