using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Keeps gallery thumbnails on disk so a folder is only decoded once.
    ///
    /// For a recording, producing one costs an open, a seek and a decode through MediaPlayer,
    /// which is a fraction of a second per file and cannot be parallelised — it has
    /// dispatcher affinity, so the whole page is decoded one video at a time on the UI
    /// thread. Thirty recordings is therefore an unpleasant wait, and it was being paid
    /// again on every visit to the tab, every page turn, and every return from another
    /// page. Reading a small JPEG back instead is a few milliseconds and runs on a worker.
    ///
    /// **Cached against the file, not the path.** The key mixes the full path with the
    /// file's length and last-write time, so a recording that is edited, re-exported or
    /// replaced by another file of the same name gets a new key and a fresh frame rather
    /// than a stale picture of what used to be there. Old entries are not deleted when
    /// this happens — they simply stop being asked for, and the prune below clears them
    /// out in time.
    ///
    /// The duration lives in the file name rather than in a sidecar or a database. It is
    /// one number, it is already known when the frame is produced, and a naming scheme
    /// cannot go out of sync with the thing it describes. Pictures store zero and simply
    /// never show the badge.
    ///
    /// **Pictures use it too.** Decoding a capture at reduced size still parses the whole
    /// PNG — a 4K screenshot is several megabytes of decompression to produce a 360px
    /// thumbnail, and the gallery does that for every item on every page. Reading back a
    /// small JPEG instead is a fraction of the work, which is what makes a folder of
    /// thousands open at the same speed the second time as the hundredth.
    /// </summary>
    internal static class ThumbnailCache
    {
        /// <summary>
        /// Bumping this abandons every existing thumbnail. Change it when the frame that
        /// gets captured changes — a different seek position or a different size would
        /// otherwise leave a mix of old and new pictures with no way to tell them apart.
        /// </summary>
        private const string Version = "v1";

        /// <summary>Roughly how many thumbnails to keep before pruning the oldest.</summary>
        private const int MaxEntries = 900;

        private static readonly object _pruneGate = new();
        private static bool _pruned;

        private static string CacheDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AdvancedSnip", "thumbs");

        /// <summary>
        /// Looks for a cached frame. Returns null when there is none, when the source has
        /// changed since it was written, or when anything at all goes wrong — a thumbnail
        /// cache that throws is worse than no cache.
        ///
        /// Safe to call from a worker thread; the bitmap comes back frozen.
        /// </summary>
        internal static (BitmapSource Frame, TimeSpan Duration)? TryLoad(string videoPath)
        {
            try
            {
                string? hit = FindEntry(videoPath);
                if (hit == null) return null;

                var frame = new BitmapImage();
                frame.BeginInit();
                frame.CacheOption = BitmapCacheOption.OnLoad;   // release the file handle
                frame.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                frame.UriSource = new Uri(hit);
                frame.EndInit();
                frame.Freeze();

                return (frame, DurationFromName(hit));
            }
            catch { return null; }
        }

        /// <summary>
        /// Writes a frame out for next time. Failures are ignored: the picture is already
        /// on screen by this point, and a cache miss next time is not worth an error.
        /// </summary>
        internal static void Store(string videoPath, BitmapSource frame, TimeSpan duration)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);

                string path = Path.Combine(CacheDir,
                    $"{Key(videoPath)}_{(long)duration.TotalMilliseconds}.jpg");

                // JPEG, not PNG: these are photographic frames at 360px, where the size
                // difference is several-fold and the artefacts are invisible.
                var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
                encoder.Frames.Add(BitmapFrame.Create(frame));

                // Written to a temporary name and moved into place, so a cache read that
                // happens mid-write never sees a half-finished JPEG.
                string temp = path + ".tmp";
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
                    encoder.Save(fs);

                File.Move(temp, path, overwrite: true);

                Prune();
            }
            catch { }
        }

        // ── keys ──────────────────────────────────────────────────────────────

        private static string Key(string videoPath)
        {
            string stamp;
            try
            {
                var info = new FileInfo(videoPath);
                stamp = $"{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            }
            catch { stamp = "0|0"; }

            // The path is lower-cased because Windows file names are case-insensitive and
            // the same file reached by differently-cased paths must not cache twice.
            string material = $"{Version}|{videoPath.ToLowerInvariant()}|{stamp}";

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }

        private static string? FindEntry(string videoPath)
        {
            string dir = CacheDir;
            if (!Directory.Exists(dir)) return null;

            // The duration is part of the name and is not known at lookup time, so the
            // key is a prefix rather than a whole file name.
            var matches = Directory.GetFiles(dir, Key(videoPath) + "_*.jpg");
            return matches.Length > 0 ? matches[0] : null;
        }

        private static TimeSpan DurationFromName(string path)
        {
            try
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                int split = stem.LastIndexOf('_');
                if (split < 0) return TimeSpan.Zero;

                return long.TryParse(stem[(split + 1)..], NumberStyles.Integer,
                                     CultureInfo.InvariantCulture, out long ms)
                    ? TimeSpan.FromMilliseconds(ms)
                    : TimeSpan.Zero;
            }
            catch { return TimeSpan.Zero; }
        }

        // ── housekeeping ──────────────────────────────────────────────────────

        /// <summary>
        /// Drops the least recently written entries once the folder grows past the cap.
        ///
        /// Runs at most once per session, and only after a write, because the cost of
        /// checking is a directory enumeration and the folder cannot grow quickly — one
        /// file per recording ever thumbnailed. A cache that silently accumulates for
        /// years is a bug report waiting to happen; one that re-enumerates on every miss
        /// is a slow gallery, which is the thing being fixed.
        /// </summary>
        private static void Prune()
        {
            lock (_pruneGate)
            {
                if (_pruned) return;
                _pruned = true;
            }

            try
            {
                var files = new DirectoryInfo(CacheDir).GetFiles("*.jpg");
                if (files.Length <= MaxEntries) return;

                foreach (var stale in files.OrderBy(f => f.LastWriteTimeUtc)
                                           .Take(files.Length - MaxEntries))
                    try { stale.Delete(); } catch { }
            }
            catch { }
        }

        /// <summary>Forgets everything. Offered so a wrong-looking cache can be cleared.</summary>
        internal static void Clear()
        {
            try
            {
                if (!Directory.Exists(CacheDir)) return;
                foreach (var f in Directory.GetFiles(CacheDir, "*.jpg"))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }
    }
}
