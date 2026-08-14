using System;
using System.Windows;
using System.Windows.Media;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Turns a WPF window into a pixel-exact overlay covering every monitor.
    ///
    /// The problem this solves: WPF lays out in device-independent units, and under
    /// per-monitor DPI awareness the conversion factor depends on which display a window
    /// is on. An overlay that spans a 125%-scaled laptop screen and a 100% external
    /// monitor has no single conversion factor, so selection rectangles drift and the
    /// captured crop doesn't match what was highlighted.
    ///
    /// The fix is to stop converting. We position the window in physical pixels with
    /// SetWindowPos, then apply the inverse of whatever scale WPF assigned so that one
    /// layout unit inside the overlay is exactly one physical pixel — everywhere, on
    /// every display. Mouse coordinates read straight out of the root element are then
    /// already in screen pixels, and the crop is pixel-perfect on any mix of displays.
    ///
    /// **How the correction is arrived at is the whole story of this file.** Two previous
    /// versions asked WPF what scale it was using and inverted the answer. That is an
    /// open loop: every source of that number — the composition target's transform, the
    /// DPI carried by the DpiChanged event, the DPI the OS reports for the window — is
    /// correct only at particular moments, and each of them is stale at some other
    /// moment. Picking a better source, or a better moment, only moves the window in
    /// which it is wrong. So this version stops trusting any of them: it applies a first
    /// guess, then **measures the result and corrects it** until one overlay unit really
    /// does land on one screen pixel. That is the same "never assume, always measure"
    /// rule the scroll-capture engine is built on, applied to DPI.
    /// </summary>
    internal static class OverlayHost
    {
        /// <summary>Distance between the two probe points used to measure the transform.</summary>
        private const double ProbeSpan = 1000.0;

        /// <summary>Tolerance on the measurement, in pixels per unit.</summary>
        private const double Tolerance = 0.002;

        /// <summary>Outside this range a scale reading is nonsense and gets ignored.</summary>
        private const double MinScale = 0.05, MaxScale = 20.0;

        /// <summary>
        /// Call from the window's constructor. Handles initial placement, the inverse
        /// scale transform, and re-applying it if the effective DPI changes.
        /// </summary>
        internal static void MakeVirtualScreenOverlay(Window window, FrameworkElement root,
                                                      Action<string>? report = null)
            => MakeOverlay(window, root, () => DisplayInfo.VirtualScreen, report);

        /// <summary>
        /// The same treatment, but covering one rectangle of the desktop instead of all of
        /// it. Used by the recording pen: an overlay that accepts clicks is a serious thing
        /// to put over someone's screen, so it should cover no more than it has to. When a
        /// single monitor is being recorded, the other monitors stay completely untouched.
        /// </summary>
        internal static void MakeRegionOverlay(Window window, FrameworkElement root,
                                               System.Drawing.Rectangle region)
            => MakeOverlay(window, root, () => region, null);

        private static void MakeOverlay(Window window, FrameworkElement root,
                                        Func<System.Drawing.Rectangle> bounds,
                                        Action<string>? report)
        {
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.ShowInTaskbar = false;
            window.Topmost = true;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.SizeToContent = SizeToContent.Manual;

            root.HorizontalAlignment = HorizontalAlignment.Left;
            root.VerticalAlignment = VerticalAlignment.Top;

            bool syncing = false;      // Sync can trigger the events that call Sync
            bool verifyQueued = false; // at most one pending measurement
            bool settling = false;     // the post-Loaded timer is created once
            int verifyBudget = 0;      // stops the correction loop chasing a moving target
            double applied = 1.0;      // the scale currently cancelled out by the transform
            string lastReport = "";    // so the readout only updates when it changes

            // Self-checking, because the previous version called SetWindowPos on every
            // SizeChanged — and a SetWindowPos that spans a mixed-scale desktop can itself
            // provoke the DPI change that resizes the window that raises SizeChanged. A
            // placement that is already correct must not restart that loop.
            void Place()
            {
                var want = bounds();
                if (WindowPlacement.GetPhysicalRect(window) == want) return;
                WindowPlacement.SetBounds(window, want, topMost: true);
            }

            // Bakes in the inverse of `scale`. Everything the overlay draws or reads is in
            // the space this produces, so this one call defines "one unit = one pixel".
            void Apply(double scale)
            {
                if (double.IsNaN(scale) || double.IsInfinity(scale) ||
                    scale < MinScale || scale > MaxScale)
                    scale = 1.0;

                applied = scale;

                root.LayoutTransform = Math.Abs(scale - 1.0) < 0.0005
                    ? Transform.Identity
                    : new ScaleTransform(1.0 / scale, 1.0 / scale);

                var r = bounds();
                root.Width = r.Width;
                root.Height = r.Height;
            }

            // Somewhere to start from, so the first frame is usually right rather than
            // visibly converging. GetDpiForWindow is asked first because it is the OS's
            // own answer for this window and it is synchronous — after a SetWindowPos that
            // moves the window across a DPI boundary it already reflects the new display,
            // where WPF has yet to process the message that tells it the same thing.
            //
            // It is only a starting point. If it disagrees with what WPF is actually
            // rendering, the measurement below is what settles the argument.
            double FirstGuess()
            {
                // Best answer when there is one: it is the scale WPF is laying out with,
                // which is the thing being cancelled.
                if (LayoutScale() is double ls) return ls;

                IntPtr h = WindowPlacement.HandleOf(window);
                if (h != IntPtr.Zero)
                {
                    try
                    {
                        uint dpi = Win32.GetDpiForWindow(h);
                        if (dpi >= 48) return dpi / 96.0;
                    }
                    catch { /* pre-1607: fall through */ }

                    try
                    {
                        uint dpi = DisplayInfo.DpiOf(
                            Win32.MonitorFromWindow(h, Win32.MONITOR_DEFAULTTONEAREST));
                        if (dpi >= 48) return dpi / 96.0;
                    }
                    catch { }
                }

                var source = PresentationSource.FromVisual(window);
                if (source?.CompositionTarget != null)
                {
                    double m11 = source.CompositionTarget.TransformToDevice.M11;
                    if (m11 > 0.01) return m11;
                }

                // Nothing to go on — keep what is already applied rather than throwing a
                // correct value away for a guess of 1.0. That guess is exactly what the
                // old code fell back to at SourceInitialized, where PresentationSource
                // has no answer yet because the window is not its root visual until
                // later; on a 125% screen it left the overlay rendering at 1.25×.
                return applied;
            }

            // The scale WPF is really laying out at — from two facts that do NOT pass
            // through TransformToDevice: the window's true size in pixels, and the size
            // WPF believes that same window is, in DIPs. Their ratio is WPF's operating
            // scale by definition.
            //
            // This is the source v2.29 was missing. Every earlier attempt, including
            // v2.29's PointToScreen probe, ultimately read TransformToDevice — so if that
            // matrix disagrees with what is being rendered, a loop built on it measures
            // its own mistake and reports success. ActualWidth comes out of the layout
            // pass that draws the content, so it cannot disagree with it.
            double? LayoutScale()
            {
                IntPtr h = WindowPlacement.HandleOf(window);
                if (h == IntPtr.Zero) return null;
                if (!Win32.GetWindowRect(h, out var rc)) return null;

                double px = rc.Right - rc.Left;
                double dip = window.ActualWidth;
                if (px < 1 || dip < 1) return null;

                double s = px / dip;
                if (s < MinScale || s > MaxScale) return null;
                return s;
            }

            // Measures how many physical pixels one overlay unit currently covers.
            // PointToScreen runs the real composed transform — the LayoutTransform we
            // applied and whatever scale WPF is rendering at — out to device pixels, so
            // this measures the finished article rather than a number that is supposed to
            // predict it.
            double? MeasurePixelsPerUnit()
            {
                try
                {
                    if (PresentationSource.FromVisual(root) == null) return null;

                    var a = root.PointToScreen(new Point(0, 0));
                    var b = root.PointToScreen(new Point(ProbeSpan, 0));

                    double dx = b.X - a.X;
                    if (dx <= 0) return null;

                    double perUnit = dx / ProbeSpan;
                    if (perUnit < MinScale || perUnit > MaxScale) return null;
                    return perUnit;
                }
                catch { return null; }   // not connected to a source yet
            }

            void Verify()
            {
                if (verifyBudget <= 0) return;
                verifyBudget--;

                // Windows can move or resize the overlay under us: a DPI change makes it
                // suggest a rectangle of its own choosing and WPF applies it. Put it back.
                // This does NOT short-circuit the scale check the way v2.29's did — the two
                // are independent, and one persistent placement disagreement was enough
                // there to starve the correction of every pass it had.
                Place();

                double? target = LayoutScale();
                if (target == null && MeasurePixelsPerUnit() is double perUnit)
                    target = applied * perUnit;    // fall back to the composed transform

                Emit();

                if (target is not double want) return;
                if (Math.Abs(want - applied) <= Tolerance) return;   // already pixel-exact

                Apply(want);
                QueueVerify();
            }

            // Puts the numbers somewhere a person can read them. Three fixes for this have
            // now been reasoned out and shipped blind; this is what ends that.
            void Emit()
            {
                if (report == null) return;

                var want = bounds();
                var now = WindowPlacement.GetPhysicalRect(window);
                double os = 0;
                try
                {
                    IntPtr h = WindowPlacement.HandleOf(window);
                    if (h != IntPtr.Zero) os = Win32.GetDpiForWindow(h) / 96.0;
                }
                catch { }

                double m11 = 0;
                var src = PresentationSource.FromVisual(window);
                if (src?.CompositionTarget != null) m11 = src.CompositionTarget.TransformToDevice.M11;

                string line =
                    $"v{AppInfo.Version} · desktop {want.Width}×{want.Height} @{want.X},{want.Y}" +
                    $" · window {now.Width}×{now.Height} @{now.X},{now.Y}" +
                    $" · os {os:0.###}× · layout {Fmt(LayoutScale())}× · m11 {m11:0.###}×" +
                    $" · applied {applied:0.###}× · {Fmt(MeasurePixelsPerUnit())} px/unit";

                if (line == lastReport) return;
                lastReport = line;
                report(line);
            }

            // DispatcherPriority.Loaded is below Render, so this runs *after* the layout
            // pass that the LayoutTransform just invalidated. Measuring before that pass
            // would read the previous transform and correct in the wrong direction.
            void QueueVerify()
            {
                if (verifyQueued) return;
                verifyQueued = true;
                window.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(() => { verifyQueued = false; Verify(); }));
            }

            void Sync(double? knownScale = null)
            {
                if (!syncing)
                {
                    syncing = true;
                    try { Apply(knownScale ?? FirstGuess()); }
                    finally { syncing = false; }
                }

                verifyBudget = 4;
                QueueVerify();
            }

            window.SourceInitialized += (_, _) =>
            {
                // Keep the overlay out of Alt+Tab.
                IntPtr h = WindowPlacement.HandleOf(window);
                if (h != IntPtr.Zero)
                {
                    int ex = Win32.GetWindowLong(h, Win32.GWL_EXSTYLE);
                    Win32.SetWindowLong(h, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_TOOLWINDOW);
                }

                Place();
                Sync();
            };

            // NewDpi rather than re-reading the transform: inside this handler the
            // composition target can still report the old scale.
            window.DpiChanged += (_, e) => { Place(); Sync(e.NewDpi.DpiScaleX); };

            // A DPI change also makes Windows resize the window to its suggested rectangle,
            // which is not the rectangle we want — put it back and re-measure.
            window.SizeChanged += (_, _) => { Place(); Sync(); };

            window.Loaded += (_, _) =>
            {
                Place();
                Sync();

                if (settling) return;
                settling = true;

                // Runs for as long as the overlay is open, not for a fixed number of
                // passes. v2.29 gave it eight ticks on the assumption that the window
                // settles quickly; the assumption is unnecessary. A pass is two Win32 calls
                // and a division, it does nothing when the overlay is already exact, and
                // the overlay lives for a few seconds. In exchange, no sequence of DPI and
                // size messages — however long it goes on — can leave it wrong.
                var timer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Loaded, window.Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(150)
                };

                timer.Tick += (_, _) =>
                {
                    if (verifyBudget < 2) verifyBudget = 2;
                    Verify();
                };

                window.Closed += (_, _) => timer.Stop();
                timer.Start();
            };
        }

        /// <summary>A number for the readout, or "?" when nothing could be measured.</summary>
        private static string Fmt(double? v) => v is double d ? d.ToString("0.###") : "?";

        /// <summary>
        /// Sizes a piece of overlay furniture — the hint bar, the magnifier, the size
        /// readout — for the display it is currently sitting on.
        ///
        /// One overlay unit is one physical pixel, which is what makes the capture exact,
        /// but it also means a 132-unit magnifier is 132 physical pixels: correct on a
        /// 100% monitor, four fifths of the size on a 125% laptop panel, and a third of it
        /// at 300%. The geometry must stay in pixels; the furniture must not. A
        /// RenderTransform scales what is drawn without touching layout, so ActualWidth
        /// and ActualHeight stay in overlay units and the positioning arithmetic that
        /// reads them keeps working — callers multiply by the same factor to get the size
        /// on screen.
        /// </summary>
        internal static void ScaleChrome(FrameworkElement element, double scale)
        {
            if (element == null) return;

            if (double.IsNaN(scale) || double.IsInfinity(scale) ||
                scale < MinScale || scale > MaxScale)
                scale = 1.0;

            if (Math.Abs(scale - 1.0) < 0.001)
            {
                if (element.RenderTransform is ScaleTransform)
                    element.RenderTransform = Transform.Identity;
                return;
            }

            // Mutated in place, not replaced: this is called from mouse-move handlers, and
            // the rest of both overlays goes to some trouble not to allocate there.
            if (element.RenderTransform is ScaleTransform existing && !existing.IsFrozen)
            {
                if (Math.Abs(existing.ScaleX - scale) > 0.001)
                {
                    existing.ScaleX = scale;
                    existing.ScaleY = scale;
                }
                return;
            }

            element.RenderTransformOrigin = new Point(0, 0);
            element.RenderTransform = new ScaleTransform(scale, scale);
        }

        /// <summary>Overlay-local coordinates to absolute screen pixels.</summary>
        internal static System.Drawing.Point ToScreen(System.Windows.Point local)
        {
            var vs = DisplayInfo.VirtualScreen;
            return new System.Drawing.Point((int)Math.Round(local.X) + vs.X,
                                            (int)Math.Round(local.Y) + vs.Y);
        }

        /// <summary>Absolute screen pixels to overlay-local coordinates.</summary>
        internal static System.Windows.Point ToLocal(int screenX, int screenY)
        {
            var vs = DisplayInfo.VirtualScreen;
            return new System.Windows.Point(screenX - vs.X, screenY - vs.Y);
        }

        internal static System.Windows.Rect ToLocalRect(System.Drawing.Rectangle screenRect)
        {
            var vs = DisplayInfo.VirtualScreen;
            return new System.Windows.Rect(screenRect.X - vs.X, screenRect.Y - vs.Y,
                                           Math.Max(0, screenRect.Width), Math.Max(0, screenRect.Height));
        }
    }
}
