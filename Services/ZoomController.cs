using System;
using System.Diagnostics;
using System.Drawing;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Live zoom for a recording in progress.
    ///
    /// The zoom is applied when the frame is composed, not afterwards: the capture thread
    /// asks for a source rectangle and stretches that onto the full frame. So the viewer
    /// sees the zoom, the file is no larger, and there is no post-processing pass.
    ///
    /// Three things make it usable rather than nauseating:
    ///
    /// **It eases.** Snapping instantly from 1x to 3x between two frames is jarring to
    /// watch. Every change is animated over a fraction of a second with a smoothstep
    /// curve, and because the capture thread samples the curve per frame, the motion is
    /// recorded at the full frame rate rather than as a jump cut.
    ///
    /// **It follows the pointer, with a dead zone.** A viewport locked to the cursor
    /// swims constantly, because the hand is never quite still. This one only moves when
    /// the pointer leaves the middle 50% of the zoomed view, and then it moves smoothly.
    ///
    /// **It never shows anything outside the recorded area.** The source rectangle is
    /// clamped, so the edges of the capture region behave like walls.
    ///
    /// Every member is safe to touch from any thread: the hotkeys arrive on the UI thread
    /// and the sampling happens on the capture thread.
    /// </summary>
    public sealed class ZoomController
    {
        /// <summary>Zoom steps the hotkeys move between. 1.0 must stay first.</summary>
        private static readonly double[] Steps = { 1.0, 1.5, 2.0, 3.0, 4.0 };

        /// <summary>How long a zoom change takes to play out, in seconds.</summary>
        private const double EaseSeconds = 0.35;

        /// <summary>How long a pan chases the pointer once it leaves the dead zone.</summary>
        private const double PanSeconds = 0.25;

        /// <summary>
        /// Fraction of the zoomed viewport the pointer can wander inside before the view
        /// starts following it.
        /// </summary>
        private const double DeadZone = 0.5;

        private readonly object _gate = new();
        private readonly Rectangle _region;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private int _stepIndex;

        // Zoom factor: eases from _fromFactor to _toFactor starting at _factorStarted.
        private double _fromFactor = 1.0, _toFactor = 1.0;
        private double _factorStarted;

        // Viewport centre in desktop pixels, eased the same way.
        private double _fromCx, _fromCy, _toCx, _toCy;
        private double _centreStarted;

        internal ZoomController(Rectangle region)
        {
            _region = region;
            _fromCx = _toCx = region.X + region.Width / 2.0;
            _fromCy = _toCy = region.Y + region.Height / 2.0;
            _factorStarted = _centreStarted = -EaseSeconds;
        }

        /// <summary>The step the user has asked for, ignoring the animation. 1.0 = off.</summary>
        internal double TargetFactor { get { lock (_gate) return _toFactor; } }

        internal bool IsZoomed => TargetFactor > 1.0001;

        /// <summary>Raised when the requested level changes, so the HUD can show it.</summary>
        internal event EventHandler<double>? FactorChanged;

        internal void ZoomIn() => SetStep(_stepIndex + 1);
        internal void ZoomOut() => SetStep(_stepIndex - 1);
        internal void Reset() => SetStep(0);

        private void SetStep(int index)
        {
            index = Math.Clamp(index, 0, Steps.Length - 1);
            double target = Steps[index];

            lock (_gate)
            {
                if (Math.Abs(target - _toFactor) < 0.0001) return;

                _stepIndex = index;
                // Ease from wherever the animation currently is, not from the last target,
                // so zooming twice quickly doesn't jerk backwards.
                _fromFactor = CurrentFactor(Now());
                _toFactor = target;
                _factorStarted = Now();

                if (target <= 1.0001)
                {
                    // Returning to 1x always recentres, otherwise the un-zoom slides
                    // sideways for no visible reason.
                    var (cx, cy) = CurrentCentre(Now());
                    _fromCx = cx; _fromCy = cy;
                    _toCx = _region.X + _region.Width / 2.0;
                    _toCy = _region.Y + _region.Height / 2.0;
                    _centreStarted = Now();
                }
            }

            FactorChanged?.Invoke(this, target);
        }

        /// <summary>
        /// The rectangle of the desktop the next frame should show. Called once per frame
        /// on the capture thread.
        /// </summary>
        internal Rectangle Sample(int pointerX, int pointerY)
        {
            lock (_gate)
            {
                double now = Now();
                if (CurrentFactor(now) <= 1.0001) return _region;

                double w = _region.Width / CurrentFactor(now);
                double h = _region.Height / CurrentFactor(now);
                Follow(pointerX, pointerY, w, h, now);
                return Compute(now);
            }
        }

        /// <summary>
        /// The same rectangle, read without advancing the pan. The on-screen indicator uses
        /// this: if it called <see cref="Sample"/> the viewport would chase the pointer
        /// twice as fast, once per caller.
        /// </summary>
        internal Rectangle Peek()
        {
            lock (_gate) return Compute(Now());
        }

        /// <summary>Caller holds the lock.</summary>
        private Rectangle Compute(double now)
        {
            double factor = CurrentFactor(now);
            if (factor <= 1.0001) return _region;

            double w = _region.Width / factor;
            double h = _region.Height / factor;

            var (cx, cy) = CurrentCentre(now);

            // Clamp so the viewport never runs off the recorded area.
            double x = Math.Clamp(cx - w / 2, _region.X, _region.Right - w);
            double y = Math.Clamp(cy - h / 2, _region.Y, _region.Bottom - h);

            int iw = Math.Max(2, (int)Math.Round(w));
            int ih = Math.Max(2, (int)Math.Round(h));
            return new Rectangle((int)Math.Round(x), (int)Math.Round(y), iw, ih);
        }

        /// <summary>Starts a pan if the pointer has left the dead zone. Caller holds the lock.</summary>
        private void Follow(int px, int py, double w, double h, double now)
        {
            if (px < _region.X || px > _region.Right || py < _region.Y || py > _region.Bottom)
                return;

            var (cx, cy) = CurrentCentre(now);

            double slackX = w * DeadZone / 2;
            double slackY = h * DeadZone / 2;

            double wantX = _toCx, wantY = _toCy;
            if (Math.Abs(px - cx) > slackX) wantX = px;
            if (Math.Abs(py - cy) > slackY) wantY = py;

            if (Math.Abs(wantX - _toCx) < 0.5 && Math.Abs(wantY - _toCy) < 0.5) return;

            _fromCx = cx; _fromCy = cy;
            _toCx = wantX; _toCy = wantY;
            _centreStarted = now;
        }

        private double Now() => _clock.Elapsed.TotalSeconds;

        private double CurrentFactor(double now)
            => Lerp(_fromFactor, _toFactor, Ease(now - _factorStarted, EaseSeconds));

        private (double, double) CurrentCentre(double now)
        {
            double t = Ease(now - _centreStarted, PanSeconds);
            return (Lerp(_fromCx, _toCx, t), Lerp(_fromCy, _toCy, t));
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        /// <summary>Smoothstep: starts and ends at rest, which is what stops it looking mechanical.</summary>
        private static double Ease(double elapsed, double duration)
        {
            if (elapsed <= 0) return 0;
            if (elapsed >= duration) return 1;
            double t = elapsed / duration;
            return t * t * (3 - 2 * t);
        }
    }
}
