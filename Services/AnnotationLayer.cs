using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace AdvancedSnip.Services
{
    /// <summary>One drawn stroke, in virtual-screen pixels.</summary>
    public sealed class InkStroke
    {
        internal List<PointF> Points { get; } = new();
        internal Color Colour { get; init; } = Color.Red;
        internal float Thickness { get; init; } = 4f;
        /// <summary>Highlighter strokes are wide, semi-transparent and sit under the cursor.</summary>
        internal bool Highlighter { get; init; }
    }

    /// <summary>
    /// A copy of the committed strokes, taken before an erase-everything so it can be
    /// taken back. Opaque on purpose — only <see cref="AnnotationLayer"/> reads it.
    /// </summary>
    public sealed class InkSnapshot
    {
        internal InkStroke[] Strokes = Array.Empty<InkStroke>();
    }

    /// <summary>
    /// The pen drawings, shared between the overlay the user draws on and the capture
    /// thread that has to get them into the video.
    ///
    /// The obvious approach — put a transparent window on screen and let the screen
    /// capture pick it up — does not survive contact with reality. Capturing layered
    /// windows needs BitBlt's CAPTUREBLT flag, which forces a repaint of every layered
    /// window on the desktop; at one screenshot that is invisible, at thirty a second it
    /// is a strobe. So the overlay is excluded from capture entirely and exists only to
    /// show the user what they are drawing, while the recorded frames get the same strokes
    /// painted into them here with GDI+.
    ///
    /// That split has a second benefit worth having: the ink in the video is drawn at the
    /// frame's own resolution rather than composited from a screen grab, so it is exactly
    /// as sharp as the encoder can carry.
    ///
    /// Every member is guarded by one lock. The UI thread appends points as the pointer
    /// moves; the capture thread walks the list once per frame. Neither waits long.
    /// </summary>
    public sealed class AnnotationLayer
    {
        private readonly object _gate = new();
        private readonly List<InkStroke> _strokes = new();
        private InkStroke? _live;

        internal bool HasInk
        {
            get { lock (_gate) return _strokes.Count > 0 || _live != null; }
        }

        internal void Begin(Color colour, float thickness, bool highlighter)
        {
            lock (_gate)
            {
                _live = new InkStroke
                {
                    Colour = colour,
                    Thickness = thickness,
                    Highlighter = highlighter
                };
            }
        }

        internal void AddPoint(float x, float y)
        {
            lock (_gate)
            {
                if (_live == null) return;

                // Skip points the pointer barely moved to. A mouse reports far more
                // positions than a stroke needs, and every one of them is a line segment
                // the capture thread redraws on every single frame.
                if (_live.Points.Count > 0)
                {
                    var last = _live.Points[^1];
                    if (Math.Abs(last.X - x) < 1.2f && Math.Abs(last.Y - y) < 1.2f) return;
                }
                _live.Points.Add(new PointF(x, y));
            }
        }

        internal void End()
        {
            lock (_gate)
            {
                if (_live != null && _live.Points.Count > 0) _strokes.Add(_live);
                _live = null;
            }
        }

        /// <summary>
        /// Throws away the stroke in progress.
        ///
        /// This exists because the obvious substitute is wrong: End() followed by Undo()
        /// looks equivalent, but End() only commits a stroke that has points, so a stroke
        /// abandoned before the pointer moved would leave Undo() deleting the previous
        /// good stroke instead.
        /// </summary>
        internal void Cancel()
        {
            lock (_gate) _live = null;
        }

        /// <summary>Removes the most recent stroke. Returns false when there is nothing left.</summary>
        internal bool Undo()
        {
            lock (_gate)
            {
                if (_strokes.Count == 0) return false;
                _strokes.RemoveAt(_strokes.Count - 1);
                return true;
            }
        }

        internal void Clear()
        {
            lock (_gate) { _strokes.Clear(); _live = null; }
        }

        /// <summary>
        /// Copies the committed strokes out, so a Clear can be taken back.
        ///
        /// Erasing everything mid-demo used to be the one action with no way back, which
        /// made people hesitate over a button whose entire job is to be pressed without
        /// hesitating. The list is copied rather than referenced; the strokes themselves
        /// are finished and immutable, so sharing them is safe.
        /// </summary>
        internal InkSnapshot Snapshot()
        {
            lock (_gate) return new InkSnapshot { Strokes = _strokes.ToArray() };
        }

        internal void Restore(InkSnapshot snapshot)
        {
            lock (_gate)
            {
                _strokes.Clear();
                _strokes.AddRange(snapshot.Strokes);
                _live = null;
            }
        }

        /// <summary>
        /// Paints every stroke onto a captured frame.
        ///
        /// <paramref name="region"/> is the part of the virtual desktop the frame covers,
        /// so a region recording gets the strokes that fall inside it, in the right place,
        /// with no extra bookkeeping at the drawing end.
        /// </summary>
        internal void DrawOnto(Graphics g, Rectangle region)
        {
            InkStroke[] snapshot;
            InkStroke? live;
            PointF[]? livePoints = null;

            // Finished strokes are never mutated again, so their references can leave the
            // lock safely. The one still being drawn is growing under the UI thread's
            // hand, so its points are copied here and drawn from the copy. Drawing itself
            // happens outside the lock — holding it through a frame render would make the
            // pointer stutter.
            lock (_gate)
            {
                if (_strokes.Count == 0 && _live == null) return;
                snapshot = _strokes.ToArray();
                live = _live;
                if (live != null) livePoints = live.Points.ToArray();
            }

            var oldSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            try
            {
                foreach (var stroke in snapshot)
                    DrawStroke(g, stroke, stroke.Points.ToArray(), region);
                if (live != null && livePoints != null)
                    DrawStroke(g, live, livePoints, region);
            }
            finally
            {
                g.SmoothingMode = oldSmoothing;
            }
        }

        private static void DrawStroke(Graphics g, InkStroke stroke, PointF[] source, Rectangle region)
        {
            if (source.Length == 0) return;

            var pts = new PointF[source.Length];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new PointF(source[i].X - region.X, source[i].Y - region.Y);

            var colour = stroke.Highlighter
                ? Color.FromArgb(90, stroke.Colour)
                : stroke.Colour;

            using var pen = new Pen(colour, stroke.Thickness)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            if (pts.Length == 1)
            {
                // A tap still has to leave a dot, or clicking the pen looks broken.
                float r = stroke.Thickness / 2f;
                using var brush = new SolidBrush(colour);
                g.FillEllipse(brush, pts[0].X - r, pts[0].Y - r, r * 2, r * 2);
                return;
            }

            g.DrawLines(pen, pts);
        }
    }
}
