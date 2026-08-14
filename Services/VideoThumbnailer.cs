using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Pulls a single frame and the running time out of a video file, for the gallery's
    /// video cards.
    ///
    /// This uses <see cref="MediaPlayer"/> rather than the Media Foundation interop the
    /// recorder is built on. The interop in this app is an *encoder*; adding a decode path
    /// to it for the sake of thumbnails would mean another set of hand-written IIDs and
    /// vtables, which is the single thing in this codebase that has broken most often.
    /// MediaPlayer already wraps the same platform decoders and cannot get the vtable
    /// wrong.
    ///
    /// **Everything here is best-effort by design.** A file still being written, a codec
    /// Windows will not open, a zero-length file left behind by a crash: all of them end
    /// as a null thumbnail and a card that falls back to an icon, never as an exception
    /// that takes the gallery down. Every wait is bounded, because a media object that
    /// never raises MediaOpened would otherwise hang the page forever.
    /// </summary>
    internal static class VideoThumbnailer
    {
        /// <summary>How long to wait for the file to open before giving up on it.</summary>
        private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(4);

        /// <summary>
        /// How long to let the decoder settle on the seeked frame.
        ///
        /// There is no event for "the seeked frame is ready", so this is a wait rather
        /// than a signal. It is the dominant cost of building a thumbnail and it is paid
        /// per file, which is why the result goes into <see cref="ThumbnailCache"/> and is
        /// never paid twice for the same recording.
        /// </summary>
        private static readonly TimeSpan FrameSettle = TimeSpan.FromMilliseconds(220);

        internal sealed class Probe
        {
            public BitmapSource? Frame;
            public TimeSpan Duration;
        }

        /// <summary>
        /// Opens the file, seeks a little way in, and renders whatever frame is showing.
        ///
        /// The seek matters: recordings routinely start on a countdown overlay, a desktop
        /// mid-repaint, or a black frame, so frame zero is the least representative frame
        /// in the file. A short way in is almost always the thing being demonstrated.
        ///
        /// Must be called on the UI thread — <see cref="MediaPlayer"/> has dispatcher
        /// affinity, and the returned bitmap is frozen so the caller can hand it anywhere.
        /// </summary>
        internal static async Task<Probe?> ProbeAsync(string path, int width, int height,
                                                      CancellationToken token)
        {
            MediaPlayer? player = null;
            try
            {
                player = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };

                var opened = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                player.MediaOpened += (_, _) => opened.TrySetResult(true);
                player.MediaFailed += (_, _) => opened.TrySetResult(false);

                player.Open(new Uri(path));

                // Bounded, because MediaOpened is not guaranteed to arrive at all — a file
                // that is still being written by the recorder simply never raises it.
                var finished = await Task.WhenAny(opened.Task, Task.Delay(OpenTimeout, token));
                if (finished != opened.Task || !opened.Task.Result) return null;
                token.ThrowIfCancellationRequested();

                var duration = player.NaturalDuration.HasTimeSpan
                    ? player.NaturalDuration.TimeSpan
                    : TimeSpan.Zero;

                if (player.NaturalVideoWidth <= 0 || player.NaturalVideoHeight <= 0)
                    return new Probe { Frame = null, Duration = duration };

                // A tenth of the way in, capped: far enough past a countdown or a black
                // opening frame, near enough that a long recording does not seek for ages.
                var seek = duration > TimeSpan.FromSeconds(2)
                    ? TimeSpan.FromSeconds(Math.Min(duration.TotalSeconds * 0.1, 5))
                    : TimeSpan.Zero;

                player.Position = seek;

                // ScrubbingEnabled makes the seek render a frame while paused, but the
                // decode is asynchronous and there is no event for "the frame is ready".
                await Task.Delay(FrameSettle, token);

                var (w, h) = Fit(player.NaturalVideoWidth, player.NaturalVideoHeight,
                                 width, height);

                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                    dc.DrawVideo(player, new System.Windows.Rect(0, 0, w, h));

                var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                target.Render(visual);
                target.Freeze();

                return new Probe { Frame = target, Duration = duration };
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
            finally
            {
                // Close() releases the underlying media resource. Without it a page of
                // videos leaves a decoder open per card.
                try { player?.Close(); } catch { }
            }
        }

        private static (int W, int H) Fit(int sourceW, int sourceH, int boxW, int boxH)
        {
            double scale = Math.Min((double)boxW / sourceW, (double)boxH / sourceH);
            if (scale <= 0 || double.IsInfinity(scale)) scale = 1;
            return (Math.Max(1, (int)Math.Round(sourceW * scale)),
                    Math.Max(1, (int)Math.Round(sourceH * scale)));
        }

        /// <summary>mm:ss, or h:mm:ss once it runs past an hour.</summary>
        internal static string Format(TimeSpan span) =>
            span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes}:{span.Seconds:00}";
    }
}
