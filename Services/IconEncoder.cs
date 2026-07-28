using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Writes Windows .ico files. .NET has decoders for ICO but no encoder, so the
    /// container is assembled by hand — it's a short, well-documented format.
    ///
    /// Three things this does that a one-size export wouldn't:
    ///
    ///  * **Every standard size in one file.** Windows picks a different resolution for
    ///    the taskbar, Alt+Tab, the desktop and the file dialog. An icon carrying only
    ///    256×256 gets scaled down by the shell with no regard for legibility; supplying
    ///    the real sizes is the difference between a crisp icon and a smudge.
    ///
    ///  * **PNG compression for the large frames.** Vista onward reads PNG inside ICO.
    ///    A 256×256 frame stored as an uncompressed DIB costs 256 KB; as PNG it's a few
    ///    kilobytes, and the alpha channel survives intact either way.
    ///
    ///  * **Padding, not stretching, for non-square sources.** Icons must be square.
    ///    Squashing a 400×120 capture into 256×256 distorts it; centring it on a
    ///    transparent square keeps the proportions and uses the transparency the format
    ///    already supports.
    /// </summary>
    internal static class IconEncoder
    {
        /// <summary>The sizes Windows actually asks for.</summary>
        internal static readonly int[] StandardSizes = { 16, 24, 32, 48, 64, 128, 256 };

        internal static void Save(BitmapSource source, string path, IEnumerable<int>? sizes = null)
        {
            var wanted = new List<int>(sizes ?? StandardSizes);
            wanted.Sort();
            if (wanted.Count == 0) wanted.Add(256);

            var square = MakeSquare(source);
            var frames = new List<byte[]>(wanted.Count);
            var dims = new List<int>(wanted.Count);

            foreach (int size in wanted)
            {
                // Never upscale past the source: a 64px capture exported at 256 would just
                // be a blurry 64px icon claiming to be sharp.
                int actual = Math.Min(size, Math.Max(square.PixelWidth, 16));
                if (dims.Contains(actual)) continue;

                frames.Add(EncodePng(Resize(square, actual)));
                dims.Add(actual);
            }

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(fs);

            // ICONDIR
            w.Write((ushort)0);                  // reserved
            w.Write((ushort)1);                  // 1 = icon
            w.Write((ushort)frames.Count);

            int offset = 6 + 16 * frames.Count;
            for (int i = 0; i < frames.Count; i++)
            {
                // ICONDIRENTRY. 256 is written as 0 — the field is a single byte, which is
                // the historical reason 256 is the maximum icon dimension.
                w.Write((byte)(dims[i] >= 256 ? 0 : dims[i]));   // width
                w.Write((byte)(dims[i] >= 256 ? 0 : dims[i]));   // height
                w.Write((byte)0);                                // palette entries
                w.Write((byte)0);                                // reserved
                w.Write((ushort)1);                              // colour planes
                w.Write((ushort)32);                             // bits per pixel
                w.Write(frames[i].Length);
                w.Write(offset);
                offset += frames[i].Length;
            }

            foreach (var frame in frames) w.Write(frame);
        }

        /// <summary>Centres the image on a transparent square canvas.</summary>
        private static BitmapSource MakeSquare(BitmapSource src)
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            if (w == h) return src;

            int side = Math.Max(w, h);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawImage(src, new Rect((side - w) / 2.0, (side - h) / 2.0, w, h));

            var rtb = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        private static BitmapSource Resize(BitmapSource src, int size)
        {
            if (src.PixelWidth == size && src.PixelHeight == size) return src;

            var visual = new DrawingVisual();
            // Fant is the right resampler going down by a large factor: bilinear aliases
            // badly at these ratios, and icons are judged entirely on their small sizes.
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.Fant);

            using (var dc = visual.RenderOpen())
                dc.DrawImage(src, new Rect(0, 0, size, size));

            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        private static byte[] EncodePng(BitmapSource src)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
    }
}
