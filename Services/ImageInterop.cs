using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Bridges System.Drawing bitmaps to WPF <see cref="BitmapSource"/> images.
    /// Fully-qualifies the few System.Drawing names it needs so it doesn't have to import
    /// namespaces that clash.
    ///
    /// Memory note, because this file is on the hot path of every single capture:
    ///
    /// The obvious way to do this — <c>Bitmap.GetHbitmap()</c> followed by
    /// <c>Imaging.CreateBitmapSourceFromHBitmap</c> — makes *two* extra full-size copies
    /// of the image. GetHbitmap allocates a fresh GDI bitmap and blits into it, then WIC
    /// allocates its own buffer and copies again. For a dual-monitor desktop grab that is
    /// two spare 30 MB buffers per snip, one of which is a GDI object living outside the
    /// .NET heap entirely. <see cref="ToFrozenBitmapSourceFast"/> now locks the source
    /// bits and copies once, straight into the WIC buffer.
    ///
    /// The pixel format is deliberately <see cref="PixelFormats.Bgr32"/>: that is exactly
    /// what CreateBitmapSourceFromHBitmap produced before, because GetHbitmap discards
    /// alpha information. Keeping it identical means nothing downstream changes
    /// appearance — and it also sidesteps the classic screen-capture trap where BitBlt
    /// leaves the alpha byte at zero and a Bgra32 reading renders the whole shot invisible.
    /// </summary>
    public static class ImageInterop
    {
        /// <summary>
        /// One locked copy from GDI+ into WIC. The result is frozen and fully independent
        /// of the source bitmap, so the caller may dispose the bitmap immediately after.
        /// </summary>
        internal static BitmapSource ToFrozenBitmapSourceFast(System.Drawing.Bitmap bmp)
        {
            var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
            System.Drawing.Imaging.BitmapData? data = null;
            try
            {
                data = bmp.LockBits(rect,
                                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                int stride = Math.Abs(data.Stride);
                long size = (long)stride * bmp.Height;

                // BitmapSource.Create copies the buffer into WIC's own storage, so the
                // scan pointer does not need to outlive the unlock below.
                var src = BitmapSource.Create(
                    bmp.Width, bmp.Height,
                    96, 96,
                    PixelFormats.Bgr32,
                    null,
                    data.Scan0,
                    (int)Math.Min(int.MaxValue, size),
                    stride);

                src.Freeze();
                return src;
            }
            finally
            {
                if (data != null)
                {
                    try { bmp.UnlockBits(data); } catch { }
                }
            }
        }

        /// <summary>
        /// The conversion every capture goes through. The old version routed anything
        /// under four megapixels through a PNG encode-then-decode, which cost a full
        /// compression pass plus two more transient copies of the image to arrive at the
        /// same pixels the direct path produces. There is no size at which that is the
        /// better choice, so the direct copy is now used for everything, with the encoder
        /// kept only as a fallback for the rare bitmap that refuses to lock.
        /// </summary>
        internal static BitmapSource ToFrozenBitmapSourceAuto(System.Drawing.Bitmap bmp)
        {
            try { return ToFrozenBitmapSourceFast(bmp); }
            catch { return ToFrozenBitmapSource(bmp); }
        }

        /// <summary>
        /// Fallback path: encodes to PNG and decodes back. Slower and heavier than the
        /// direct copy; kept because it goes through an entirely different code path in
        /// GDI+ and so survives cases where LockBits does not.
        /// </summary>
        internal static BitmapSource ToFrozenBitmapSource(System.Drawing.Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;

            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }

        /// <summary>
        /// Decodes an image file from disk without holding a file lock.
        /// </summary>
        public static BitmapSource LoadBitmap(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }

        /// <summary>
        /// Builds a small, frozen, standalone copy for display in a list.
        ///
        /// This exists because binding a full-resolution image straight into a 110-pixel
        /// tall <c>Image</c> element does not make it cheap: WPF keeps the whole decoded
        /// source alive for as long as anything references it, and a high-quality scale of
        /// a 4K source into a thumbnail slot builds an intermediate at close to source
        /// resolution every time the element re-renders. Eight history entries shown that
        /// way cost more than the eight images themselves.
        ///
        /// <paramref name="maxEdge"/> caps the longest side. Scroll captures are the
        /// reason it caps the *longest* rather than the width — a 1200 x 20000 capture
        /// scaled to 320 wide would still be 5300 pixels tall.
        /// </summary>
        internal static BitmapSource MakeThumbnail(BitmapSource source, int maxEdge = 320)
        {
            try
            {
                int w = source.PixelWidth, h = source.PixelHeight;
                if (w <= 0 || h <= 0) return source;

                double scale = Math.Min(1.0, (double)maxEdge / Math.Max(w, h));
                if (scale >= 1.0)
                {
                    // Already small enough to show as-is.
                    if (source.IsFrozen) return source;
                    var clone = source.Clone();
                    clone.Freeze();
                    return clone;
                }

                var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));

                // TransformedBitmap is a *view*: it holds a reference to the full-size
                // source and re-scales on every render, which would defeat the whole
                // point. Baking it into a cache detaches it from the original.
                var cached = new CachedBitmap(scaled, BitmapCreateOptions.None,
                                              BitmapCacheOption.OnLoad);
                cached.Freeze();
                return cached;
            }
            catch
            {
                return source;
            }
        }
    
        /// <summary>
        /// A 64-bit content hash of a small bitmap, for "is this the same picture".
        ///
        /// Not a cryptographic hash and not trying to be: it exists to stop the clipboard
        /// history stacking identical thumbnails when the same gallery item is copied
        /// twice. FNV-1a over the raw pixels of an already-small image — a collision would
        /// mean one duplicate suppressed that should not have been, which is the cheap
        /// direction to be wrong in.
        ///
        /// Returns 0 when the pixels cannot be read, which callers treat as "unknown, so
        /// assume new" rather than as a hash value.
        /// </summary>
        internal static long Fingerprint(BitmapSource image)
        {
            if (image == null) return 0;

            try
            {
                var source = image;
                if (!source.IsFrozen)
                {
                    if (!source.CanFreeze) return 0;
                    source = (BitmapSource)source.Clone();
                    source.Freeze();
                }

                int stride = (source.PixelWidth * source.Format.BitsPerPixel + 7) / 8;
                long total = (long)stride * source.PixelHeight;

                // A thumbnail is small by construction; anything large enough to be worth
                // guarding against is not a thumbnail and is not what this is for.
                if (total <= 0 || total > 8 * 1024 * 1024) return 0;

                var bytes = new byte[total];
                source.CopyPixels(bytes, stride, 0);

                unchecked
                {
                    ulong hash = 14695981039346656037UL;      // FNV-1a offset basis
                    foreach (byte b in bytes)
                    {
                        hash ^= b;
                        hash *= 1099511628211UL;              // FNV prime
                    }

                    // Fold the dimensions in, so two different pictures that somehow hash
                    // alike still differ when their shapes do.
                    hash ^= (ulong)source.PixelWidth * 2654435761UL;
                    hash ^= (ulong)source.PixelHeight * 40503UL;

                    long result = (long)hash;
                    return result == 0 ? 1 : result;          // 0 is reserved for "unknown"
                }
            }
            catch { return 0; }
        }
    }
}
