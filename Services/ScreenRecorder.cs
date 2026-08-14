using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AdvancedSnip.Services
{
    internal sealed class RecordingRequest
    {
        /// <summary>The part of the virtual desktop to record, in physical pixels.</summary>
        internal Rectangle Region { get; init; }
        internal string Path { get; init; } = "";
        internal int Fps { get; init; } = 30;
        internal VideoQuality Quality { get; init; } = VideoQuality.Balanced;
        internal bool IncludeCursor { get; init; } = true;
        internal AnnotationLayer? Ink { get; init; }

        /// <summary>Live zoom, or null to always record the region 1:1.</summary>
        internal ZoomController? Zoom { get; init; }

        /// <summary>What to record for sound, or null for a silent recording.</summary>
        internal AudioRequest? Audio { get; init; }

        /// <summary>Shapes drawn during the recording, painted into each frame.</summary>
        internal ShapeLayer? Shapes { get; init; }

        /// <summary>
        /// A specific window to record, or null to record the screen.
        ///
        /// When set, frames come from the window's own paint output rather than from the
        /// desktop, so whatever sits in front of it is not in the take and moving it does
        /// not walk it out of frame.
        /// </summary>
        internal IntPtr TargetWindow { get; init; }
    }

    internal sealed class RecordingProgress
    {
        internal TimeSpan Elapsed { get; init; }
        internal long Bytes { get; init; }
        internal int Frames { get; init; }
        internal int Dropped { get; init; }

        /// <summary>Loudest audio sample since the last report, 0..1. Zero when silent.</summary>
        internal float AudioLevel { get; init; }

        /// <summary>True when sound is being recorded, for the HUD's meter.</summary>
        internal bool AudioActive { get; init; }
    }

    /// <summary>
    /// The capture loop: grab the screen at a steady rate, paint the pen strokes and the
    /// cursor into each frame, hand it to the encoder.
    ///
    /// Two decisions shape this class.
    ///
    /// **It runs on its own MTA thread.** Media Foundation objects belong to the thread
    /// that created them, and the WPF UI thread is STA and busy. A dedicated thread also
    /// means a slow encode stalls the recording's frame rate rather than the user's mouse.
    ///
    /// **Every frame is encoded.** An earlier version fingerprinted each frame and skipped
    /// the ones that matched the last, on the theory that a screen recording is mostly a
    /// still picture. That was a mistake twice over: H.264 already spends almost nothing on
    /// an unchanged frame — that is what inter-frame prediction is for — so the saving was
    /// small, and a sparse fingerprint misses small changes like a blinking caret, which
    /// makes a perfectly good recording look frozen. Encoding everything is simpler, and
    /// the codec does the compressing.
    /// </summary>
    internal sealed class ScreenRecorder
    {
        /// <summary>
        /// Stopwatch ticks to Media Foundation's hundred-nanosecond units. Stopwatch has
        /// its own frequency and a TimeSpan tick is already 100 ns, so the two units are
        /// easy to conflate — getting this wrong scales the whole timeline and the video
        /// plays back as a single flash.
        /// </summary>
        private static readonly double HnsPerTick = 10_000_000.0 / Stopwatch.Frequency;

        private static long ToHns(long stopwatchTicks) => (long)(stopwatchTicks * HnsPerTick);

        private readonly RecordingRequest _req;

        /// <summary>Non-null when recording one window rather than the screen.</summary>
        private WindowCapture? _window;
        private Thread? _thread;
        private volatile bool _stop;
        private volatile bool _paused;

        private int _frames, _dropped;
        private long _bytes;
        private AudioMixer? _audio;

        internal string? Error { get; private set; }

        /// <summary>
        /// Set when the video recorded but something about the sound did not — no
        /// microphone, a refused format, a device unplugged. Reported after the fact
        /// rather than as a failure, because the recording itself is fine.
        /// </summary>
        internal string? AudioWarning { get; private set; }
        internal bool IsRunning { get; private set; }
        internal bool IsPaused => _paused;

        /// <summary>Raised on the capture thread; marshal before touching UI.</summary>
        internal event EventHandler<RecordingProgress>? Progress;

        /// <summary>Raised once the file is closed, successfully or not.</summary>
        internal event EventHandler<string?>? Finished;

        internal ScreenRecorder(RecordingRequest request) => _req = request;

        internal void Start()
        {
            if (IsRunning) return;
            IsRunning = true;
            _stop = false;

            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "AdvancedSnip.Recorder",
                // Above normal so the pacing stays even while the desktop is busy, but not
                // so high that we starve whatever the user is actually recording.
                Priority = ThreadPriority.AboveNormal
            };
            // Media Foundation wants an MTA thread. Creating the sink writer from an STA
            // thread works but marshals every call through a proxy, which at thirty frames
            // a second is a cost for nothing.
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        internal void Pause() => _paused = true;
        internal void Resume() => _paused = false;

        /// <summary>
        /// Silences the recording from here on. Safe at any time, including before the
        /// audio devices have opened — the flag is re-applied on every pump.
        /// </summary>
        internal bool Muted { get; set; }

        /// <summary>True once sound is actually being captured.</summary>
        internal bool HasAudio => _audio != null;

        internal void Stop()
        {
            _stop = true;
            try { _thread?.Join(4000); } catch { }
        }

        private void Run()
        {
            var region = DisplayInfo.ClampToDesktop(_req.Region);

            if (_req.TargetWindow != IntPtr.Zero)
                _window = new WindowCapture(_req.TargetWindow,
                                            new Size(region.Width, region.Height));
            int w = Math.Max(2, region.Width & ~1);
            int h = Math.Max(2, region.Height & ~1);
            region = new Rectangle(region.X, region.Y, w, h);

            var encoder = new VideoRecorder();
            Bitmap? frame = null;

            try
            {
                // Sound is opened before the encoder because the encoder needs to know the
                // sample rate to negotiate its AAC stream, and the rate is whatever the
                // playback device is actually running at.
                VideoRecorder.AudioSpec? audioSpec = null;
                if (_req.Audio is { Any: true })
                {
                    var mixer = new AudioMixer(_req.Audio);
                    if (mixer.Start(_req.Audio))
                    {
                        _audio = mixer;
                        audioSpec = new VideoRecorder.AudioSpec
                        {
                            SampleRate = mixer.SampleRate,
                            Channels = AudioMixer.Channels
                        };
                    }
                    else
                    {
                        mixer.Dispose();
                    }
                    AudioWarning = mixer.Warning;
                }

                if (!encoder.Start(_req.Path, w, h, _req.Fps, _req.Quality, audioSpec))
                {
                    Error = encoder.LastError ?? "The recorder could not start.";
                    return;
                }

                // The stream can still be refused after the mixer opened successfully — an
                // N edition without the media pack, for instance. Stop capturing sound
                // nobody will hear.
                if (_audio != null && !encoder.HasAudio)
                {
                    AudioWarning ??= "This copy of Windows has no AAC encoder, so the "
                                   + "recording has no sound.";
                    _audio.Dispose();
                    _audio = null;
                }

                frame = new Bitmap(w, h, PixelFormat.Format32bppRgb);

                // Stopwatch counts in its own units, Media Foundation wants
                // hundred-nanosecond units. These are not the same thing, and on most
                // machines Stopwatch.Frequency happens to be 10 MHz, which makes a wrong
                // conversion look right until it isn't. Convert explicitly.
                long frameTicks = Stopwatch.Frequency / Math.Max(1, _req.Fps);
                var clock = Stopwatch.StartNew();

                // Paused time is subtracted rather than paused-over, so the finished video
                // has no gap where the pause was.
                long pausedTicks = 0;
                long pauseStarted = 0;

                long nextDue = 0;
                long lastWritten = -1;

                while (!_stop)
                {
                    if (_paused)
                    {
                        if (pauseStarted == 0) pauseStarted = clock.ElapsedTicks;
                        Thread.Sleep(40);
                        continue;
                    }

                    if (pauseStarted != 0)
                    {
                        pausedTicks += clock.ElapsedTicks - pauseStarted;
                        pauseStarted = 0;

                        // Everything the microphone and speakers produced during the pause
                        // belongs to a moment that is not in the video. Throw it away and
                        // re-base the audio clock, or the rest of the recording plays with
                        // the sound running ahead by exactly the length of the pause.
                        _audio?.Resync((clock.ElapsedTicks - pausedTicks) * HnsPerTick / 10_000_000.0);
                    }

                    long now = clock.ElapsedTicks - pausedTicks;

                    // Audio is pumped on the frame loop rather than its own timer: it needs
                    // the same paused-adjusted clock the video uses, and at 30 fps this
                    // runs every 33 ms — well inside the buffer's headroom.
                    PumpAudio(encoder, now);

                    if (now < nextDue)
                    {
                        // Sleep the bulk of the wait and spin the last millisecond.
                        // Thread.Sleep alone is only accurate to about 15 ms, which at
                        // 30 fps is half a frame of jitter.
                        long waitMs = (nextDue - now) * 1000 / Stopwatch.Frequency;
                        if (waitMs > 2) Thread.Sleep((int)(waitMs - 1));
                        continue;
                    }

                    nextDue += frameTicks;
                    // If we have fallen far behind — a heavy encode, a busy machine — give
                    // up on the frames we missed rather than sprinting to catch up, which
                    // would only fall further behind.
                    if (now - nextDue > frameTicks * 4)
                    {
                        _dropped += (int)((now - nextDue) / frameTicks);
                        nextDue = now + frameTicks;
                    }

                    Grab(frame, region);

                    long timeHns = ToHns(now);
                    long durHns = Math.Max(1, ToHns(frameTicks));

                    // Never write two samples at the same timestamp; the muxer treats that
                    // as a malformed stream and some players stop at that point.
                    if (timeHns <= lastWritten) timeHns = lastWritten + 1;

                    var data = frame.LockBits(new Rectangle(0, 0, w, h),
                                              ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        encoder.WriteFrame(data.Scan0, data.Stride, timeHns, durHns);
                    }
                    finally { frame.UnlockBits(data); }

                    lastWritten = timeHns;
                    _frames++;

                    if (_frames % 10 == 0) Report(clock, pausedTicks);
                }

                // Stopping while paused leaves the current pause unaccounted for. Absorb
                // it before the final pump, or the last thing written to the audio track
                // is a block of silence exactly as long as the pause.
                if (pauseStarted != 0)
                {
                    pausedTicks += clock.ElapsedTicks - pauseStarted;
                    pauseStarted = 0;
                }

                // One last pump so the sound does not stop short of the final frame.
                PumpAudio(encoder, clock.ElapsedTicks - pausedTicks);
                Report(clock, pausedTicks);
            }
            catch (Exception ex)
            {
                Error ??= ex.Message;
            }
            finally
            {
                // Sound first: the devices have to be released before the container is
                // closed, and the encoder must outlive the last audio sample written.
                try { _audio?.Stop(); } catch { }

                frame?.Dispose();
                encoder.Stop();

                try { _audio?.Dispose(); } catch { }
                _audio = null;

                try { _bytes = new FileInfo(_req.Path).Length; } catch { }

                IsRunning = false;
                MemoryGuard.RequestTrim();
                Finished?.Invoke(this, Error);
            }
        }

        /// <summary>
        /// Hands the encoder whatever audio should exist by <paramref name="nowTicks"/>.
        ///
        /// The mixer, not this loop, decides how much that is — it pads with silence for
        /// any device that has fallen behind, so the audio timeline stays pinned to the
        /// same clock as the video no matter how the sound cards behave.
        /// </summary>
        private void PumpAudio(VideoRecorder encoder, long nowTicks)
        {
            var audio = _audio;
            if (audio == null) return;

            try
            {
                audio.Muted = Muted;

                double seconds = nowTicks * HnsPerTick / 10_000_000.0;
                var pcm = audio.Fill(seconds, out int bytes, out long startFrame);
                if (pcm != null && bytes > 0) encoder.WriteAudio(pcm, bytes, startFrame);
            }
            catch
            {
                // A device pulled out mid-recording. Carry on silently rather than ending
                // the take — the video is the thing that cannot be recovered.
            }
        }

        /// <summary>Screen, then pen strokes, then cursor — in that painting order.</summary>
        private void Grab(Bitmap frame, Rectangle region)
        {
            // The zoom, if any, decides which slice of the region the frame actually shows.
            // Everything downstream works against this rectangle rather than the region, so
            // strokes and the cursor land in the right place at any zoom level.
            Rectangle src = region;
            if (_req.Zoom != null)
            {
                Win32.GetCursorPos(out var pt);
                src = _req.Zoom.Sample(pt.X, pt.Y);
            }

            float scale = src.Width <= 0 ? 1f : (float)region.Width / src.Width;

            using var g = Graphics.FromImage(frame);

            if (_window != null)
            {
                GrabWindow(g, frame);
            }
            else
            {
                GrabScreen(g, region, src);
            }

            PaintOverlays(g, frame, region, src, scale);
        }

        /// <summary>
        /// Frames from the window itself.
        ///
        /// The frame is cleared first because a window smaller than the frame — resized
        /// mid-recording, or never as large as it was when recording started — would
        /// otherwise leave the previous frame's pixels showing round the edges, which
        /// looks like a smear rather than a border.
        ///
        /// Zoom is not applied here. It samples a rectangle of the *desktop*, and a window
        /// being recorded on its own has no fixed place on the desktop to sample.
        /// </summary>
        private void GrabWindow(Graphics g, Bitmap frame)
        {
            g.Clear(Color.Black);

            if (_window!.Grab(g)) return;

            // The window declined to draw itself: minimised, closing, or one of the few
            // that refuse PrintWindow. Falling back to the screen reintroduces occlusion,
            // which is worse than the good path and much better than a black video.
            _window.GrabFromScreen(g);
        }

        private void GrabScreen(Graphics g, Rectangle region, Rectangle src)
        {
            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            if (screenDc != IntPtr.Zero)
            {
                IntPtr destDc = g.GetHdc();
                try
                {
                    // No CAPTUREBLT. It would pull in layered windows, but it also forces
                    // every one of them to repaint, and at thirty frames a second that
                    // reads as the whole screen strobing. The overlay is excluded from
                    // capture and its strokes are painted in below instead.
                    if (src.Width == region.Width && src.Height == region.Height)
                    {
                        Win32.BitBlt(destDc, 0, 0, region.Width, region.Height,
                                     screenDc, src.X, src.Y, Win32.SRCCOPY);
                    }
                    else
                    {
                        Win32.SetStretchBltMode(destDc, Win32.STRETCH_COLORONCOLOR);
                        Win32.SetBrushOrgEx(destDc, 0, 0, IntPtr.Zero);
                        Win32.StretchBlt(destDc, 0, 0, region.Width, region.Height,
                                         screenDc, src.X, src.Y, src.Width, src.Height,
                                         Win32.SRCCOPY);
                    }
                }
                finally
                {
                    g.ReleaseHdc(destDc);
                    Win32.ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }

        /// <summary>Pen strokes, shapes and the cursor, painted over whatever was captured.</summary>
        private void PaintOverlays(Graphics g, Bitmap frame, Rectangle region,
                                   Rectangle src, float scale)
        {
            // Strokes are stored in desktop pixels. Scaling the world transform rather than
            // the points means the pen also gets thicker as you zoom in, which is what a
            // viewer expects — the stroke was drawn at that size on screen.
            if (_req.Ink != null || _req.Shapes != null)
            {
                var saved = g.Save();
                try
                {
                    if (Math.Abs(scale - 1f) > 0.001f) g.ScaleTransform(scale, scale);

                    // Shapes first, ink on top. Someone who circles a button and then
                    // scribbles on it expects the scribble to be visible; the reverse
                    // order would put a filled box over their own handwriting.
                    _req.Shapes?.DrawOnto(g, src);
                    _req.Ink?.DrawOnto(g, src);
                }
                finally { g.Restore(saved); }
            }

            if (_req.IncludeCursor) DrawCursor(g, src, scale);
        }

        /// <summary>
        /// Draws the pointer. BitBlt never includes it — the cursor is not part of the
        /// desktop surface, it is composited by the driver — so a recording without this
        /// has no mouse in it at all, which for a tutorial is the whole point missing.
        /// </summary>
        private static void DrawCursor(Graphics g, Rectangle src, float scale)
        {
            try
            {
                var info = new Win32.CURSORINFO { cbSize = Marshal.SizeOf<Win32.CURSORINFO>() };
                if (!Win32.GetCursorInfo(ref info)) return;
                if (info.flags != Win32.CURSOR_SHOWING || info.hCursor == IntPtr.Zero) return;

                if (!Win32.GetIconInfo(info.hCursor, out var icon)) return;

                try
                {
                    // ptScreenPos is the hotspot, not the top-left corner, so the icon's
                    // own hotspot offset has to come back off or the arrow sits low and
                    // right of where it really is.
                    //
                    // The position scales with the zoom but the icon does not: DrawIconEx
                    // writes to the raw HDC and never sees the GDI+ world transform. That
                    // is also what ZoomIt does, and a pointer that stayed the same size
                    // while everything around it grew reads as deliberate rather than
                    // broken.
                    int x = (int)Math.Round((info.ptScreenPos.X - src.X) * scale) - icon.xHotspot;
                    int y = (int)Math.Round((info.ptScreenPos.Y - src.Y) * scale) - icon.yHotspot;

                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        Win32.DrawIconEx(hdc, x, y, info.hCursor, 0, 0, 0,
                                         IntPtr.Zero, Win32.DI_NORMAL);
                    }
                    finally { g.ReleaseHdc(hdc); }
                }
                finally
                {
                    if (icon.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(icon.hbmColor);
                    if (icon.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(icon.hbmMask);
                }
            }
            catch { /* a missing cursor is not worth failing a recording over */ }
        }

        private void Report(Stopwatch clock, long pausedTicks)
        {
            long size = 0;
            try { size = new FileInfo(_req.Path).Length; } catch { }
            _bytes = size;

            var audio = _audio;

            Progress?.Invoke(this, new RecordingProgress
            {
                Elapsed = TimeSpan.FromTicks(Math.Max(0, ToHns(clock.ElapsedTicks - pausedTicks))),
                Bytes = size,
                Frames = _frames,
                Dropped = _dropped,
                AudioActive = audio != null,
                AudioLevel = audio?.ReadPeak() ?? 0f
            });
        }
    }
}
