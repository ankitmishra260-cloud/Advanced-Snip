using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Bridges System.Drawing bitmaps to WPF <see cref="BitmapSource"/> images.
    /// Fully-qualifies the few System.Drawing / System.Windows.Interop names it needs
    /// so it doesn't have to import namespaces that clash.
    /// </summary>
    internal static class ImageInterop
    {
        /// <summary>
        /// Fast path: wraps the bitmap's HBITMAP directly. Used for the large full-screen
        /// image shown behind the selection overlay. The result is frozen and independent
        /// of the source bitmap, so the caller may dispose the bitmap afterwards.
        /// </summary>
        internal static BitmapSource ToFrozenBitmapSourceFast(System.Drawing.Bitmap bmp)
        {
            IntPtr hBitmap = bmp.GetHbitmap();
            try
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                NativeMethods.DeleteObject(hBitmap);
            }
        }

        /// <summary>
        /// Picks the right conversion for the size of image in hand. A scroll capture can
        /// easily be twenty thousand pixels tall, and round-tripping that through a PNG
        /// encoder costs seconds and a second full copy in memory for no benefit.
        /// </summary>
        internal static BitmapSource ToFrozenBitmapSourceAuto(System.Drawing.Bitmap bmp)
        {
            long pixels = (long)bmp.Width * bmp.Height;
            if (pixels > 4_000_000)
            {
                try { return ToFrozenBitmapSourceFast(bmp); }
                catch { /* fall through to the encoder path */ }
            }
            return ToFrozenBitmapSource(bmp);
        }

        /// <summary>
        /// Encodes to PNG and decodes into a frozen <see cref="BitmapImage"/>. Used for the
        /// (small) cropped snip so we get a fully independent image for the history/clipboard.
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
    }
}
