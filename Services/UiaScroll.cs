using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// An optional accessibility-based view of the target's scroll state.
    ///
    /// This is a bonus, never a dependency. When an app exposes a ScrollPattern we get an
    /// exact "how far down the page are we" figure, which turns the progress bar from a
    /// guess into a real percentage and gives us a definitive end-of-content signal and a
    /// clean way to put the user's scroll position back afterwards.
    ///
    /// When it isn't available — and it often isn't, because some apps expose nothing and
    /// Chromium only wakes its accessibility engine on demand — the capture runs entirely
    /// on measured pixels and loses nothing but the precise percentage. Every call is
    /// wrapped and time-limited because UI Automation reaches into other processes and
    /// can block on an app that's busy.
    /// </summary>
    internal sealed class UiaScroll
    {
        private readonly ScrollPattern _pattern;

        private UiaScroll(ScrollPattern pattern) => _pattern = pattern;

        /// <summary>Where we are, 0-100. Negative means "unknown".</summary>
        internal double VerticalPercent
        {
            get
            {
                try
                {
                    double v = _pattern.Current.VerticalScrollPercent;
                    return v < 0 ? -1 : v;
                }
                catch { return -1; }
            }
        }

        internal bool AtBottom
        {
            get
            {
                double p = VerticalPercent;
                return p >= 0 && p >= 99.5;
            }
        }

        internal bool TrySetPercent(double percent)
        {
            try
            {
                if (!_pattern.Current.VerticallyScrollable) return false;
                _pattern.SetScrollPercent(ScrollPattern.NoScroll, Math.Clamp(percent, 0, 100));
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Looks for the smallest scrollable element containing the point, searching only
        /// inside the target window's own subtree. Times out rather than hanging.
        /// </summary>
        internal static async Task<UiaScroll?> TryResolveAsync(
            IntPtr topLevelHwnd, System.Drawing.Point screenPoint,
            int timeoutMs, CancellationToken token)
        {
            if (topLevelHwnd == IntPtr.Zero) return null;

            try
            {
                var work = Task.Run(() => Resolve(topLevelHwnd, screenPoint), token);
                var finished = await Task.WhenAny(work, Task.Delay(timeoutMs, token))
                                         .ConfigureAwait(false);

                if (finished != work) return null;      // timed out; carry on without it
                return await work.ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        private static UiaScroll? Resolve(IntPtr hwnd, System.Drawing.Point pt)
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null) return null;

                var condition = new PropertyCondition(
                    AutomationElement.IsScrollPatternAvailableProperty, true);

                AutomationElementCollection matches;
                try
                {
                    matches = root.FindAll(TreeScope.Subtree, condition);
                }
                catch
                {
                    return null;
                }

                AutomationElement? best = null;
                double bestArea = double.MaxValue;

                foreach (AutomationElement el in matches)
                {
                    try
                    {
                        var r = el.Current.BoundingRectangle;
                        if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;
                        if (!r.Contains(new System.Windows.Point(pt.X, pt.Y))) continue;

                        double area = r.Width * r.Height;
                        if (area < bestArea) { bestArea = area; best = el; }
                    }
                    catch { }
                }

                // If nothing contained the point, fall back to the window's own scroller.
                best ??= root.TryGetCurrentPattern(ScrollPattern.Pattern, out _) ? root : null;
                if (best == null) return null;

                if (!best.TryGetCurrentPattern(ScrollPattern.Pattern, out object patternObj))
                    return null;

                if (patternObj is not ScrollPattern sp) return null;
                if (!sp.Current.VerticallyScrollable) return null;

                return new UiaScroll(sp);
            }
            catch
            {
                return null;
            }
        }
    }
}
