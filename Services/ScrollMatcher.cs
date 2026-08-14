using System;
using System.Collections.Generic;

namespace AdvancedSnip.Services
{
    internal readonly struct ShiftResult
    {
        /// <summary>How far the content moved up, in pixels. 0 means nothing moved.</summary>
        public int Delta { get; init; }
        /// <summary>False when no offset explained the change well enough to trust.</summary>
        public bool Confident { get; init; }
        /// <summary>Average per-cell mismatch of the winning offset (lower is better).</summary>
        public double Cost { get; init; }

        public static ShiftResult None => new() { Delta = 0, Confident = true, Cost = 0 };
        public static ShiftResult Failed => new() { Delta = 0, Confident = false, Cost = double.MaxValue };
    }

    /// <summary>
    /// The heart of the scroll capture: it works out how far content actually moved
    /// between two frames instead of assuming a step size.
    ///
    /// The old implementation scrolled and then pasted each frame at a fixed offset of
    /// "viewport height minus overlap". That assumption is wrong almost everywhere —
    /// Page Down scrolls by the viewport minus a couple of lines, a wheel notch is three
    /// lines of whatever the app's line height happens to be, list views scroll in whole
    /// items, and smooth scrolling can land anywhere. Every one of those mismatches shows
    /// up as duplicated or missing bands in the stitched image.
    ///
    /// Measuring the real displacement makes the stitch exact no matter what moved the
    /// content, and it doubles as end-of-page detection: when nothing moves, we're done.
    /// </summary>
    internal static class ScrollMatcher
    {
        /// <summary>Rows sampled when testing a candidate offset.</summary>
        private const int SampleRows = 50;

        /// <summary>Above this average per-cell mismatch we don't believe the match.</summary>
        private const double MaxAcceptableCost = 24.0;

        /// <summary>Is the band identical in both frames (i.e. nothing scrolled)?</summary>
        internal static bool BandUnchanged(CaptureFrame a, CaptureFrame b, int top, int bottom)
        {
            top = Math.Max(0, top);
            bottom = Math.Min(Math.Min(a.Height, b.Height), bottom);
            if (bottom - top < 4) return true;

            int step = Math.Max(1, (bottom - top) / 80);
            int checkedRows = 0, differing = 0;

            for (int y = top; y < bottom; y += step)
            {
                checkedRows++;
                if (!a.RowsSimilar(y, b, y)) differing++;
                if (differing > 2) return false;   // more than stray rows changed
            }
            return checkedRows == 0 || differing <= 2;
        }

        /// <summary>
        /// Finds how far content moved up between <paramref name="prev"/> and
        /// <paramref name="cur"/> within [top, bottom).
        ///
        /// Content that sat at row y+d in the previous frame is at row y now, so we score
        /// every plausible d and keep the best. When several offsets score nearly the same
        /// — which happens with repetitive content like tables or file lists — we pick the
        /// one nearest the expected step rather than the arbitrary global minimum.
        /// </summary>
        internal static ShiftResult MeasureShift(CaptureFrame prev, CaptureFrame cur,
                                                 int top, int bottom, int expectedDelta)
        {
            top = Math.Max(0, top);
            bottom = Math.Min(Math.Min(prev.Height, cur.Height), bottom);
            int bandH = bottom - top;
            if (bandH < 16) return ShiftResult.Failed;

            if (BandUnchanged(prev, cur, top, bottom))
                return ShiftResult.None;

            // Sample rows from the upper-to-middle part of the band: after scrolling down,
            // this content came from lower in the previous frame, so a match is available.
            int sampleSpan = Math.Max(12, (int)(bandH * 0.65));
            int sampleStep = Math.Max(1, sampleSpan / SampleRows);

            var rows = new List<int>(SampleRows + 4);
            for (int y = top; y < top + sampleSpan && rows.Count < SampleRows; y += sampleStep)
                rows.Add(y);
            if (rows.Count == 0) return ShiftResult.Failed;

            int maxDelta = bandH - 8;
            var costs = new double[maxDelta + 1];
            for (int i = 0; i < costs.Length; i++) costs[i] = double.MaxValue;

            for (int d = 1; d <= maxDelta; d++)
            {
                long sum = 0;
                int n = 0;
                foreach (int y in rows)
                {
                    int src = y + d;
                    if (src >= bottom) break;
                    sum += prev.RowDistance(src, cur, y);
                    n++;
                }
                if (n < 4) break;
                costs[d] = (double)sum / n / CaptureFrame.Cols;
            }

            // Best offset overall.
            int best = -1;
            double bestCost = double.MaxValue;
            for (int d = 1; d <= maxDelta; d++)
            {
                if (costs[d] < bestCost) { bestCost = costs[d]; best = d; }
            }
            if (best < 0 || bestCost > MaxAcceptableCost) return ShiftResult.Failed;

            // Gather every offset that's within a whisker of the best, then prefer the one
            // closest to what we expected. Repetitive layouts produce many near-equal
            // minima and blindly taking the global best is how a stitch skips a screenful.
            double threshold = Math.Max(bestCost * 1.30, bestCost + 1.0);
            int chosen = best;

            if (expectedDelta > 0)
            {
                int bestDistance = Math.Abs(best - expectedDelta);
                for (int d = 1; d <= maxDelta; d++)
                {
                    if (costs[d] > threshold) continue;
                    int distance = Math.Abs(d - expectedDelta);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        chosen = d;
                    }
                }
            }

            return new ShiftResult
            {
                Delta = chosen,
                Confident = true,
                Cost = costs[chosen]
            };
        }

        /// <summary>
        /// Works out which rows actually scroll, by looking at what changed between two
        /// frames taken one small scroll apart.
        ///
        /// This is what keeps browser chrome, sticky navigation bars, sticky table headers,
        /// Explorer column headers, status bars and fixed cookie footers out of the stitched
        /// result: they don't move during scrolling, so they are isolated from the scrolling
        /// band and never repeated.
        /// </summary>
        internal static bool TryFindScrollBand(CaptureFrame a, CaptureFrame b,
                                               out int top, out int bottom)
        {
            top = 0;
            bottom = 0;

            int h = Math.Min(a.Height, b.Height);
            if (h < 32) return false;

            var changed = new bool[h];
            int totalChanged = 0;
            for (int y = 0; y < h; y++)
            {
                changed[y] = !a.RowsSimilar(y, b, y);
                if (changed[y]) totalChanged++;
            }

            // If virtually nothing changed, scrolling hasn't occurred.
            if (totalChanged < 4) return false;

            // 1. Identify top sticky elements (fixed headers, navbars, table headers, toolbars).
            // Scan down to find the first sustained changed row (at least 2 changed rows in a 4-row window).
            int firstChanged = 0;
            for (int y = 0; y < h - 4; y++)
            {
                int count = (changed[y] ? 1 : 0) + (changed[y + 1] ? 1 : 0) +
                            (changed[y + 2] ? 1 : 0) + (changed[y + 3] ? 1 : 0);
                if (count >= 2)
                {
                    firstChanged = y;
                    break;
                }
            }

            // 2. Identify bottom sticky elements (fixed footers, cookie banners, status bars).
            // Scan up from the bottom to find the last sustained changed row.
            int lastChanged = h;
            for (int y = h - 1; y >= 3; y--)
            {
                int count = (changed[y] ? 1 : 0) + (changed[y - 1] ? 1 : 0) +
                            (changed[y - 2] ? 1 : 0) + (changed[y - 3] ? 1 : 0);
                if (count >= 2)
                {
                    lastChanged = y + 1;
                    break;
                }
            }

            if (lastChanged <= firstChanged) return false;

            int span = lastChanged - firstChanged;
            if (span < Math.Max(24, h / 10)) return false;

            top = firstChanged;
            bottom = lastChanged;
            return true;
        }

        /// <summary>
        /// Finds the strip on the right-hand edge that doesn't travel with the content —
        /// in practice, the scrollbar.
        ///
        /// A guess based on "did these pixels change" doesn't work: a scrollbar thumb
        /// changes as you scroll, so it looks exactly like content. The reliable test is
        /// whether a column moved by the same amount everything else did. Real content at
        /// row y now sat at row y+delta before; a scrollbar doesn't obey that, so the
        /// columns it occupies fail the check while every content column passes.
        ///
        /// Returns the number of pixels to trim from the right, or 0 when there's nothing
        /// to trim. Capped at a plausible scrollbar width so a column of content that
        /// happens to look self-similar can never be cut off.
        /// </summary>
        internal static int DetectRightGutter(CaptureFrame prev, CaptureFrame cur,
                                              int top, int bottom, int delta, int width)
        {
            if (delta <= 0 || width < 240) return 0;

            top = Math.Max(0, top);
            bottom = Math.Min(Math.Min(prev.Height, cur.Height), bottom);
            if (bottom - top - delta < 40) return 0;

            int step = Math.Max(1, (bottom - top - delta) / 30);
            int firstBadCol = CaptureFrame.Cols;

            for (int col = CaptureFrame.Cols - 1; col >= 0; col--)
            {
                int total = 0, mismatched = 0;

                for (int y = top; y + delta < bottom; y += step)
                {
                    total++;
                    if (cur.CellDistance(y, col, prev, y + delta) > 12) mismatched++;
                }

                if (total < 6) return 0;

                bool followsTheContent = mismatched <= total * 0.35;
                if (followsTheContent) break;      // reached real content; stop here

                firstBadCol = col;
            }

            if (firstBadCol >= CaptureFrame.Cols) return 0;

            int keepWidth = cur.CellLeft(firstBadCol);
            int trim = width - keepWidth;

            int maxTrim = Math.Max(60, Win32.GetSystemMetrics(Win32.SM_CXVSCROLL) * 2);
            return trim > 0 && trim <= maxTrim ? trim : 0;
        }
    }
}
