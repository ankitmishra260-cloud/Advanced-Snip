using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media.Imaging;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Keeps the process's memory honest.
    ///
    /// The problem this exists to solve is specific and worth writing down, because it is
    /// invisible from a memory profiler that only shows the managed heap:
    ///
    /// A <see cref="BitmapSource"/> is a tiny managed object — a few dozen bytes — in front
    /// of a WIC pixel buffer that lives in *unmanaged* memory. A 4K screenshot is
    /// 3840 x 2160 x 4 = 33 MB of that buffer. The garbage collector budgets its work
    /// against the managed heap, so from its point of view the app allocated almost
    /// nothing and there is no reason to run a collection. Meanwhile the working set
    /// climbs by 33 MB a time and nothing ever comes back.
    ///
    /// The fix is the pair of APIs the runtime provides for exactly this situation:
    /// <see cref="GC.AddMemoryPressure"/> tells the collector "this small object is really
    /// holding N bytes", and the matching Remove call takes it back. With the pressure
    /// registered, three or four screen captures are enough to trigger a gen-2 collection
    /// on their own, which is the behaviour you would naively have expected all along.
    ///
    /// <see cref="RequestTrim"/> is the second half: after a capture finishes the app is
    /// idle by definition (the user is looking at a notification, not at us), so that is
    /// a free moment to compact the large object heap — where every pixel buffer and PNG
    /// encode lands, and which is never compacted otherwise — and hand the pages back.
    /// </summary>
    internal static class MemoryGuard
    {
        /// <summary>
        /// Below this, a trim is not worth the pause. A WPF app with a couple of windows
        /// open sits around 120-150 MB before any capture happens.
        /// </summary>
        private const long TrimThresholdBytes = 220L * 1024 * 1024;

        private static readonly object _gate = new();
        private static Timer? _timer;
        private static int _trimming;

        // ── pressure accounting ───────────────────────────────────────────────

        /// <summary>Pixel bytes an image really occupies, whatever the managed object says.</summary>
        internal static long BytesOf(BitmapSource? src)
        {
            if (src == null) return 0;
            try
            {
                int bpp = Math.Max(8, src.Format.BitsPerPixel);
                return (long)src.PixelWidth * src.PixelHeight * (bpp / 8);
            }
            catch { return 0; }
        }

        /// <summary>
        /// Registers an image's real cost with the collector. Returns the number of bytes
        /// registered so the caller can hand the same figure back to <see cref="Release"/> —
        /// AddMemoryPressure and RemoveMemoryPressure must balance exactly, and reading the
        /// size off the image a second time is not safe if it has since been replaced.
        /// </summary>
        internal static long Track(BitmapSource? src)
        {
            long bytes = BytesOf(src);
            if (bytes <= 0) return 0;
            try { GC.AddMemoryPressure(bytes); } catch { return 0; }
            return bytes;
        }

        /// <summary>Undoes a <see cref="Track"/>. Pass the value Track returned, not a fresh measurement.</summary>
        internal static void Release(long bytes)
        {
            if (bytes <= 0) return;
            try { GC.RemoveMemoryPressure(bytes); } catch { }
        }

        // ── trimming ──────────────────────────────────────────────────────────

        /// <summary>
        /// Asks for a compaction shortly after things go quiet. Debounced, so calling it
        /// from every teardown path in the app costs nothing — a burst of five captures
        /// still produces one trim.
        /// </summary>
        internal static void RequestTrim(int delayMs = 1500)
        {
            lock (_gate)
            {
                if (_timer == null)
                    _timer = new Timer(_ => Trim(), null, delayMs, Timeout.Infinite);
                else
                    _timer.Change(delayMs, Timeout.Infinite);
            }
        }

        private static void Trim()
        {
            // Never let two trims overlap; a compacting gen-2 collection is not cheap and
            // queuing them up would turn a background tidy into a visible stall.
            if (Interlocked.Exchange(ref _trimming, 1) == 1) return;

            try
            {
                if (GC.GetTotalMemory(false) < TrimThresholdBytes &&
                    !AboveWorkingSetThreshold())
                    return;

                // Compacting matters more than collecting here. Every pixel buffer and
                // every PNG encode buffer is well over the 85 KB large-object threshold,
                // so they all land on the LOH, which by default is swept but never
                // compacted — freed space stays reserved and fragmented, which is why the
                // number in Task Manager never falls back on its own.
                GCSettings.LargeObjectHeapCompactionMode =
                    GCLargeObjectHeapCompactionMode.CompactOnce;

                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

                // Bitmap and Icon both hold their real memory behind a finalizer. Without
                // this second pass the pixels of anything collected above are still held
                // until the finalizer thread gets round to them.
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

                ReleaseWorkingSet();
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _trimming, 0);
            }
        }

        private static bool AboveWorkingSetThreshold()
        {
            try
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                return p.WorkingSet64 > TrimThresholdBytes;
            }
            catch { return false; }
        }

        /// <summary>
        /// Hands the freed pages back to Windows.
        ///
        /// Honest caveat: this does not free anything the collection above did not already
        /// free — it only stops the process sitting on pages it no longer needs, which
        /// Windows would otherwise reclaim lazily and only under pressure. For a tool that
        /// lives in the tray all day and is idle between captures that is the right trade;
        /// for a busy app it would just cause page faults on the next use. It is called
        /// only after a capture completes, never on a hot path.
        /// </summary>
        private static void ReleaseWorkingSet()
        {
            try { SetProcessWorkingSetSizeEx(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1), 0); }
            catch { }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessWorkingSetSizeEx(
            IntPtr hProcess, IntPtr min, IntPtr max, uint flags);
    }
}
