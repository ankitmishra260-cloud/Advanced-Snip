using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace AdvancedSnip.Services
{
    public enum ScrollSpeed { Fast, Balanced, Thorough }

    /// <summary>What the picker handed us plus the user's preferences.</summary>
    public sealed class ScrollCaptureRequest
    {
        public IntPtr TopLevelHwnd { get; set; }
        public IntPtr TargetHwnd { get; set; }
        public Rectangle Region { get; set; }
        public string Title { get; set; } = "";
        public int MaxHeightPx { get; set; } = 20000;
        public ScrollSpeed Speed { get; set; } = ScrollSpeed.Balanced;
        public bool AutoDetectRegion { get; set; } = true;
        public bool RestoreScrollPosition { get; set; } = true;
    }

    public sealed class ScrollCaptureProgress
    {
        public int Frames { get; init; }
        public int Height { get; init; }
        public int Percent { get; init; }
        public string Message { get; init; } = "";
        public System.Windows.Media.Imaging.BitmapSource? Preview { get; init; }
    }

    public sealed class ScrollCaptureOutcome
    {
        public required Bitmap Image { get; init; }
        public required int Frames { get; init; }
        public required Rectangle Region { get; init; }
        public string Note { get; init; } = "";
        public bool Truncated { get; init; }
    }

    /// <summary>Lets the progress window say "that's enough, keep what you have".</summary>
    public sealed class StopSignal
    {
        private volatile bool _stop;
        public bool Requested => _stop;
        public void Request() => _stop = true;
    }

    /// <summary>
    /// Captures everything a scrollable region contains by scrolling it and stitching the
    /// frames together.
    ///
    /// The design rule throughout: never assume, always measure. We don't assume how far a
    /// scroll went, we don't assume where the scrolling area is, and we don't assume how
    /// long a repaint takes — each is determined from the pixels. That's what makes it
    /// work the same in Chrome, Explorer, Notepad, a PDF viewer and an Electron app.
    /// </summary>
    public sealed class ScrollCaptureEngine
    {
        private readonly ScrollCaptureRequest _req;
        private readonly IProgress<ScrollCaptureProgress>? _progress;
        private readonly StopSignal _stop;
        private readonly CancellationToken _token;

        private readonly int _settlePollMs;
        private readonly int _settleMaxMs;
        private readonly double _stepFraction;

        private const int MaxFrames = 400;

        public ScrollCaptureEngine(ScrollCaptureRequest request,
                                   IProgress<ScrollCaptureProgress>? progress,
                                   StopSignal stop,
                                   CancellationToken token)
        {
            _req = request;
            _progress = progress;
            _stop = stop;
            _token = token;

            (_settlePollMs, _settleMaxMs, _stepFraction) = request.Speed switch
            {
                ScrollSpeed.Fast => (30, 320, 0.85),
                ScrollSpeed.Thorough => (55, 1400, 0.60),
                _ => (40, 700, 0.75)
            };
        }

        public async Task<ScrollCaptureOutcome> RunAsync()
        {
            var region = DisplayInfo.ClampToDesktop(_req.Region);
            if (region.Width < 40 || region.Height < 40)
                throw new InvalidOperationException("That region is too small to scroll-capture.");

            Report(0, 0, 0, "Bringing the window forward\u2026");

            bool focused = WindowFinder.BringToFront(_req.TopLevelHwnd);
            await Task.Delay(220, _token).ConfigureAwait(false);

            var driver = new ScrollDriver(_req.TargetHwnd, region);
            driver.ParkCursor();

            UiaScroll? uia = null;
            double originalPercent = -1;

            try
            {
                // Accessibility is a nice-to-have; give it a short window and move on.
                Report(0, 0, 0, "Inspecting the scroll area\u2026");
                var hotspot = new Point(region.X + region.Width / 2, region.Y + region.Height / 3);
                uia = await UiaScroll.TryResolveAsync(_req.TopLevelHwnd, hotspot, 1500, _token)
                                     .ConfigureAwait(false);
                if (uia != null) originalPercent = uia.VerticalPercent;

                await ScrollToTopAsync(driver, uia).ConfigureAwait(false);

                var outcome = await CaptureLoopAsync(region, driver, uia, focused).ConfigureAwait(false);

                if (_req.RestoreScrollPosition)
                    await RestorePositionAsync(driver, uia, originalPercent).ConfigureAwait(false);

                return outcome;
            }
            finally
            {
                driver.RestoreCursor();
            }
        }

        // ── phase 1: go to the top ────────────────────────────────────────────

        private async Task ScrollToTopAsync(ScrollDriver driver, UiaScroll? uia)
        {
            Report(0, 0, 0, "Rewinding to the top\u2026");

            if (uia != null && uia.TrySetPercent(0))
            {
                await Task.Delay(350, _token).ConfigureAwait(false);
                return;
            }

            // No accessibility support: wheel upwards until the picture stops changing.
            var probe = DisplayInfo.ClampToDesktop(_req.Region);
            using var before = CaptureFrame.Grab(probe);
            CaptureFrame? last = null;

            try
            {
                for (int i = 0; i < 60; i++)
                {
                    _token.ThrowIfCancellationRequested();
                    if (_stop.Requested) break;

                    driver.Scroll(-20);
                    await Task.Delay(_settlePollMs + 25, _token).ConfigureAwait(false);

                    var now = CaptureFrame.Grab(probe);
                    if (last != null && ScrollMatcher.BandUnchanged(last, now, 0, now.Height))
                    {
                        now.Dispose();
                        break;
                    }
                    last?.Dispose();
                    last = now;
                }
            }
            finally
            {
                last?.Dispose();
            }

            await Task.Delay(150, _token).ConfigureAwait(false);
        }

        private async Task RestorePositionAsync(ScrollDriver driver, UiaScroll? uia, double originalPercent)
        {
            try
            {
                if (uia != null && originalPercent >= 0 && uia.TrySetPercent(originalPercent))
                    return;

                driver.ScrollToTopBurst();
                for (int i = 0; i < 6; i++)
                {
                    driver.Scroll(-25);
                    await Task.Delay(30, _token).ConfigureAwait(false);
                }
            }
            catch { /* best effort only */ }
        }

        // ── phase 2 & 3: probe, then the main loop ────────────────────────────

        private async Task<ScrollCaptureOutcome> CaptureLoopAsync(
            Rectangle region, ScrollDriver driver, UiaScroll? uia, bool focused)
        {
            string note = focused ? "" : "The window wouldn't come to the front, so parts may be covered. ";

            Report(0, 0, 2, "Capturing the first screen\u2026");
            var first = await CaptureSettledAsync(region).ConfigureAwait(false);

            // ── Probe: one small scroll tells us three things at once — whether this
            //    thing scrolls at all, how far one notch moves it, and exactly which rows
            //    are the scrolling viewport.
            CaptureFrame? probe = null;
            int pxPerNotch = 0;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                _token.ThrowIfCancellationRequested();

                driver.Scroll(1);
                probe?.Dispose();
                probe = await CaptureSettledAsync(region).ConfigureAwait(false);

                var shift = ScrollMatcher.MeasureShift(first, probe, 0, region.Height, -1);
                if (shift.Confident && shift.Delta > 0)
                {
                    pxPerNotch = shift.Delta;
                    break;
                }

                // Nothing moved. Either this region doesn't scroll, or this app ignores
                // the way we're sending the scroll — try the next delivery method.
                if (!driver.TryNextMethod()) break;
                Report(0, 0, 3, $"Trying {driver.MethodName}\u2026");
                await Task.Delay(120, _token).ConfigureAwait(false);
            }

            if (pxPerNotch <= 0)
            {
                // Genuinely nothing to scroll — hand back a plain single-screen capture.
                probe?.Dispose();
                var single = (Bitmap)first.Bitmap.Clone();
                first.Dispose();
                return new ScrollCaptureOutcome
                {
                    Image = single,
                    Frames = 1,
                    Region = region,
                    Note = note + "This area doesn't scroll, so it was captured as a single image."
                };
            }

            // ── Work out where the scrolling viewport actually is.
            int bandTop = 0, bandBottom = region.Height;
            var refinedRegion = region;

            if (_req.AutoDetectRegion && probe != null)
            {
                if (ScrollMatcher.TryFindScrollBand(first, probe, out int t, out int b))
                {
                    bandTop = t;
                    bandBottom = b;
                }

                int trim = ScrollMatcher.DetectRightGutter(
                    first, probe, bandTop, bandBottom, pxPerNotch, region.Width);

                if (trim > 0 && region.Width - trim > 160)
                    refinedRegion = new Rectangle(region.X, region.Y, region.Width - trim, region.Height);
            }

            probe?.Dispose();

            // Probing left the target scrolled down a notch, and it may have narrowed the
            // region, so rewind and take the real first frame. Row indices are unaffected
            // by a width change, which is why the band we just measured stays valid.
            region = refinedRegion;
            first.Dispose();

            await ScrollToTopAsync(driver, uia).ConfigureAwait(false);
            first = await CaptureSettledAsync(region).ConfigureAwait(false);

            bandBottom = Math.Min(bandBottom, region.Height);
            bandTop = Math.Clamp(bandTop, 0, Math.Max(0, bandBottom - 32));
            int bandHeight = Math.Max(32, bandBottom - bandTop);

            // ── Stitch.
            var canvas = new StitchCanvas(region.Width,
                                          Math.Min(_req.MaxHeightPx, Math.Max(region.Height * 4, 2048)),
                                          _req.MaxHeightPx);

            var prev = first;
            int frames = 1;
            int zeroStreak = 0;
            int notchLimit = 30;
            bool truncated = false;
            bool seamWarning = false;

            try
            {
                // Frame one contributes everything down to the bottom of the scroll band;
                // the sticky footer, if any, gets added from the final frame at the end so
                // it lands in the right place.
                canvas.Append(prev.Bitmap, 0, bandBottom);
                ReportFrame(canvas, frames, uia, "Capturing\u2026");

                var watchdog = Stopwatch.StartNew();

                while (frames < MaxFrames)
                {
                    _token.ThrowIfCancellationRequested();
                    if (_stop.Requested) { note += "Stopped early at your request. "; break; }
                    if (canvas.ReachedLimit) { truncated = true; break; }

                    // Aim each step at a fraction of the band so consecutive frames always
                    // overlap enough to align, and never overshoot the whole viewport.
                    int notches = (int)Math.Round(bandHeight * _stepFraction / Math.Max(1, pxPerNotch));
                    notches = Math.Clamp(notches, 1, notchLimit);

                    driver.Scroll(notches);
                    var cur = await CaptureSettledAsync(region).ConfigureAwait(false);

                    // After a scroll that didn't move anything, the reference frame is
                    // several attempts old, so the usual one-step estimate would be a
                    // misleading hint. Drop the hint rather than bias the match wrongly.
                    int expected = zeroStreak > 0 ? -1 : notches * pxPerNotch;
                    var shift = ScrollMatcher.MeasureShift(prev, cur, bandTop, bandBottom, expected);

                    if (!shift.Confident)
                    {
                        // We probably jumped further than one screenful (momentum, or a
                        // page that snaps). Ease back up and re-measure against the same
                        // reference frame rather than guessing an offset.
                        int backOff = Math.Max(1, notches / 2);
                        driver.Scroll(-backOff);
                        cur.Dispose();
                        cur = await CaptureSettledAsync(region).ConfigureAwait(false);
                        shift = ScrollMatcher.MeasureShift(prev, cur, bandTop, bandBottom, -1);

                        notchLimit = Math.Max(1, notches / 2);

                        if (!shift.Confident)
                        {
                            // Still no alignment. Append a conservative screenful and mark
                            // the result so the user knows this one isn't pixel-perfect.
                            shift = new ShiftResult { Delta = bandHeight, Confident = true, Cost = 0 };
                            seamWarning = true;
                        }
                    }

                    if (shift.Delta <= 0)
                    {
                        cur.Dispose();
                        zeroStreak++;

                        bool definitelyDone = uia?.AtBottom ?? false;
                        if (definitelyDone || zeroStreak >= 2) break;

                        await Task.Delay(120, _token).ConfigureAwait(false);
                        continue;
                    }

                    zeroStreak = 0;

                    int newRows = Math.Min(shift.Delta, bandHeight);
                    canvas.Append(cur.Bitmap, bandBottom - newRows, newRows);

                    // Keep the notch estimate current: zoom changes and variable line
                    // heights make it drift over a long page.
                    pxPerNotch = Math.Max(1, (int)Math.Round(
                        pxPerNotch * 0.6 + ((double)shift.Delta / notches) * 0.4));

                    prev.Dispose();
                    prev = cur;
                    frames++;

                    ReportFrame(canvas, frames, uia, "Capturing\u2026");

                    if (watchdog.Elapsed > TimeSpan.FromMinutes(5))
                    {
                        note += "Stopped after five minutes. ";
                        break;
                    }
                }

                // Whatever sits below the scroll band (a sticky footer, a status bar)
                // belongs at the very bottom of the finished image.
                if (bandBottom < region.Height)
                    canvas.Append(prev.Bitmap, bandBottom, region.Height - bandBottom);

                if (canvas.ReachedLimit) truncated = true;
                if (truncated)
                    note += $"Reached the {_req.MaxHeightPx:N0} px height limit — raise it in Settings if you need more. ";
                if (seamWarning)
                    note += "Some frames couldn't be aligned exactly, so a seam may be visible. ";

                Report(frames, canvas.Height, 100, "Finishing\u2026");

                return new ScrollCaptureOutcome
                {
                    Image = canvas.ToBitmap(),
                    Frames = frames,
                    Region = region,
                    Note = note.Trim(),
                    Truncated = truncated
                };
            }
            finally
            {
                prev.Dispose();
                canvas.Dispose();
            }
        }

        // ── settle detection ──────────────────────────────────────────────────

        /// <summary>
        /// Captures once the picture stops changing, instead of waiting a fixed delay.
        /// Smooth scrolling, lazy-loaded images and fade-in animations all take wildly
        /// different amounts of time; a fixed sleep is either too slow everywhere or too
        /// fast exactly where it matters. Bounded so a blinking caret or a video can't
        /// stall us forever.
        /// </summary>
        private async Task<CaptureFrame> CaptureSettledAsync(Rectangle region)
        {
            var frame = CaptureFrame.Grab(region);
            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < _settleMaxMs)
            {
                _token.ThrowIfCancellationRequested();
                await Task.Delay(_settlePollMs, _token).ConfigureAwait(false);

                var next = CaptureFrame.Grab(region);
                if (ScrollMatcher.BandUnchanged(frame, next, 0, next.Height))
                {
                    frame.Dispose();
                    return next;
                }
                frame.Dispose();
                frame = next;
            }
            return frame;
        }

        // ── progress ──────────────────────────────────────────────────────────

        private double _cachedUiaPercent = -1;

        private void ReportFrame(StitchCanvas canvas, int frames, UiaScroll? uia, string message)
        {
            int percent;

            // Each read crosses a process boundary, so sample it every few frames rather
            // than on every one.
            if (uia != null && (frames % 3 == 0 || frames <= 2))
                _cachedUiaPercent = uia.VerticalPercent;
            double uiaPercent = uia == null ? -1 : _cachedUiaPercent;

            if (uiaPercent >= 0)
            {
                percent = (int)Math.Clamp(uiaPercent, 1, 99);
            }
            else
            {
                // No real percentage available — show progress against the height cap so
                // the bar still means something rather than pretending to know.
                percent = (int)Math.Clamp(canvas.Height * 95.0 / Math.Max(1, _req.MaxHeightPx), 1, 95);
            }

            System.Windows.Media.Imaging.BitmapSource? preview = null;
            if (frames % 3 == 0 || frames <= 2)
            {
                using var thumb = canvas.CreatePreview(200, 420);
                if (thumb != null)
                {
                    try { preview = ImageInterop.ToFrozenBitmapSourceFast(thumb); }
                    catch { }
                }
            }

            _progress?.Report(new ScrollCaptureProgress
            {
                Frames = frames,
                Height = canvas.Height,
                Percent = percent,
                Message = message,
                Preview = preview
            });
        }

        private void Report(int frames, int height, int percent, string message)
            => _progress?.Report(new ScrollCaptureProgress
            {
                Frames = frames,
                Height = height,
                Percent = percent,
                Message = message
            });
    }
}
