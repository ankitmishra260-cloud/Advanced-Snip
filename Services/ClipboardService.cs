using System;
using System.Collections.Specialized;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AdvancedSnip.Services
{
    /// <summary>What actually made it onto the clipboard.</summary>
    internal sealed class ClipboardOutcome
    {
        internal bool Png { get; set; }
        internal bool Bitmap { get; set; }
        internal bool FileDrop { get; set; }

        internal bool AnythingLanded => Png || Bitmap || FileDrop;

        /// <summary>Short note for the tray when a paste is likely to disappoint.</summary>
        internal string? Warning
        {
            get
            {
                if (!AnythingLanded) return "The clipboard refused the image.";
                if (!Bitmap && !Png) return "Copied as a file. Paste into an app that accepts files.";
                if (!Bitmap) return "Copied as PNG. Apps that only take bitmaps may not see it.";
                return null;
            }
        }
    }

    /// <summary>
    /// Puts an image on the Windows clipboard in several formats so it pastes into as many
    /// apps as possible.
    ///
    /// The size problem, stated plainly, because getting this wrong is what broke pasting
    /// tall scroll captures:
    ///
    ///   * The PNG stream is the *compressed* format. An 18 MB capture stays 18 MB.
    ///   * <c>DataObject.SetImage</c> is the *uncompressed* one. It builds a DIB at four
    ///     bytes per pixel, and <c>copy: true</c> then makes Windows take its own copy of
    ///     that. A 90-megapixel scroll capture is 360 MB per copy, and GDI will simply
    ///     refuse somewhere along the way.
    ///
    /// Version 2.6 capped the wrong one — it dropped PNG above a size threshold and kept
    /// the DIB, which is exactly backwards. Worse, all three formats were built inside one
    /// try block, so the DIB failing on a large image threw away the PNG that would have
    /// worked. Each format is now attempted independently, and the expensive one is the
    /// one that gets skipped when the image is enormous.
    ///
    /// <paramref name="filePath"/> lets a very large capture still be pasted: apps that
    /// won't take a 360 MB bitmap will happily take the file it was saved to.
    /// </summary>
    internal static class ClipboardService
    {
        /// <summary>
        /// Above this the DIB is skipped. Forty-eight megapixels is roughly 1600 x 30000 —
        /// far past any region snip, and the point where a 190 MB DIB plus Windows' copy
        /// of it starts failing rather than merely being slow.
        /// </summary>
        private const long BitmapFormatPixelLimit = 48L * 1000 * 1000;

        internal static ClipboardOutcome SetImage(BitmapSource src, string? filePath = null)
        {
            var outcome = new ClipboardOutcome();
            if (src == null) return outcome;

            long pixels = (long)src.PixelWidth * src.PixelHeight;
            MemoryStream? png = null;

            try
            {
                var data = new DataObject();

                // ── 1. PNG. Compressed, so it is the format most likely to survive a
                //    large image, and the one browsers, Slack, Discord and Word prefer.
                //    The MemoryStream must stay alive until after SetDataObject, because
                //    the DataObject holds a reference into it.
                try
                {
                    png = new MemoryStream();
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(src));
                    encoder.Save(png);
                    png.Position = 0;
                    data.SetData("PNG", png);
                    outcome.Png = true;
                }
                catch
                {
                    png?.Dispose();
                    png = null;
                }

                // ── 2. DIB. What Paint, Word and most desktop apps read. Expensive, and
                //    skipped outright once the image is large enough that offering it is
                //    more likely to fail the whole operation than to help.
                if (pixels <= BitmapFormatPixelLimit)
                {
                    try
                    {
                        data.SetImage(src);
                        outcome.Bitmap = true;
                    }
                    catch { /* PNG and the file drop can still carry it */ }
                }

                // ── 3. The file itself. This is what rescues a tall scroll capture:
                //    Explorer, Word, Outlook and every chat client accept a file drop, and
                //    a file has no size ceiling at all. Only offered for images big enough
                //    to be at risk, because apps that see both a bitmap and a file drop
                //    sometimes prefer the file, and for an ordinary snip the bitmap is the
                //    better paste.
                if (pixels > BitmapFormatPixelLimit &&
                    !string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    try
                    {
                        var files = new StringCollection { filePath };
                        data.SetFileDropList(files);
                        outcome.FileDrop = true;
                    }
                    catch { }
                }

                if (!outcome.AnythingLanded) return outcome;

                // The clipboard is a shared, single-owner resource and another app may
                // hold it for a moment — a clipboard manager or anti-virus reacting to the
                // previous copy is the usual culprit. A few short retries succeed far more
                // often than one attempt, and cost nothing when the first try works.
                if (!TrySetDataObject(data))
                {
                    outcome.Png = outcome.Bitmap = outcome.FileDrop = false;

                    // Last resort: the simple API, which publishes the bitmap alone.
                    try { Clipboard.SetImage(src); outcome.Bitmap = true; } catch { }
                }
            }
            catch
            {
                try { Clipboard.SetImage(src); outcome.Bitmap = true; } catch { }
            }
            finally
            {
                // Safe only after the OLE copy above has taken its own snapshot.
                png?.Dispose();

                // A full-size PNG buffer is always a large-object allocation, and the LOH
                // is never compacted on its own. Copying several captures in a row is
                // exactly the pattern that leaves it fragmented.
                if (pixels > 2_000_000) MemoryGuard.RequestTrim();
            }

            return outcome;
        }

        private static bool TrySetDataObject(DataObject data)
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    // copy: true flushes the data into the OLE clipboard so it persists
                    // after our process releases it.
                    Clipboard.SetDataObject(data, copy: true);
                    return true;
                }
                catch
                {
                    Thread.Sleep(40 + attempt * 25);
                }
            }
            return false;
        }
    }
}
