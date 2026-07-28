using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdvancedSnip.Services
{
    internal sealed class BackgroundRemovalOptions
    {
        /// <summary>How different from the background a pixel may be and still count as
        /// background. 0–100; the useful range for screenshots is about 8–25.</summary>
        public int Tolerance { get; set; } = 14;

        /// <summary>Width in pixels of the soft transition at the edge of the subject.</summary>
        public int Feather { get; set; } = 2;

        /// <summary>Extra seed points the user clicked, in image pixels.</summary>
        public List<System.Windows.Point> ExtraSeeds { get; } = new();
    }

    internal sealed class BackgroundRemovalResult
    {
        public required BitmapSource Image { get; init; }
        public required double RemovedFraction { get; init; }
        public required string Note { get; init; }
    }

    /// <summary>
    /// Removes a flat background and replaces it with transparency.
    ///
    /// ── What this is, honestly ──────────────────────────────────────────────────
    ///
    /// This is not a segmentation neural network. Photoshop's "Remove Background" and
    /// tools like rembg run a trained model (U²-Net and relatives) that understands what a
    /// subject *is*, which is what lets them cut a person's hair out of a photographed
    /// crowd. Doing that here would mean shipping a model file of a couple of hundred
    /// megabytes plus an inference runtime.
    ///
    /// For this app's actual input, that would be the wrong trade. Screenshots are not
    /// photographs: their backgrounds are flat, synthetic and near-noiseless, and for that
    /// class of image a well-built classical segmentation is not a poor approximation of a
    /// model — it is exact, and it's instant, offline and deterministic. It cuts UI
    /// elements, dialogs, logos, diagrams and product shots on plain backdrops cleanly.
    /// It will not separate a person from a busy photographic background; nothing without
    /// a model will.
    ///
    /// ── Why this beats a plain magic wand ───────────────────────────────────────
    ///
    /// Three things, and the last two are where naive implementations produce the
    /// tell-tale cut-out look:
    ///
    /// 1. **Seeded from the border, not from a colour.** Flood filling inward from the
    ///    edges means a colour that appears both in the background and inside the subject
    ///    is only removed where it's actually connected to the outside. A white page
    ///    behind a window keeps the window's own white toolbar.
    ///
    /// 2. **Anti-aliased edges get partial alpha.** A screenshot's edges are blends of
    ///    subject and background. Hard 0-or-255 alpha turns those into a jagged fringe.
    ///    Pixels between the two thresholds get proportional alpha instead, so the cut-out
    ///    keeps the smooth outline it had.
    ///
    /// 3. **Colour decontamination.** A half-transparent edge pixel still holds half the
    ///    old background's colour. Composite it on a new backdrop and you get a halo of
    ///    the colour you thought you removed. Un-mixing the background out of partial
    ///    pixels is what makes the result survive being placed on a dark slide.
    /// </summary>
    internal static class BackgroundRemover
    {
        internal static BackgroundRemovalResult Remove(BitmapSource source,
                                                       BackgroundRemovalOptions options)
        {
            int w = source.PixelWidth, h = source.PixelHeight;
            if (w < 2 || h < 2)
                return new BackgroundRemovalResult
                {
                    Image = source, RemovedFraction = 0,
                    Note = "The image is too small to work on."
                };

            // Work in straight (non-premultiplied) BGRA so colour maths is meaningful.
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = w * 4;
            var px = new byte[stride * h];
            bgra.CopyPixels(px, stride, 0);

            var background = SampleBorderColours(px, w, h, stride);
            if (background.Count == 0)
                return new BackgroundRemovalResult
                {
                    Image = source, RemovedFraction = 0,
                    Note = "Couldn't identify a background colour."
                };

            // Two thresholds: inside `near` is certainly background, beyond `far` is
            // certainly subject, and the band between them is the anti-aliased edge that
            // earns partial alpha.
            double near = options.Tolerance * options.Tolerance * 3.0;
            double far  = near * 4.0;

            var alpha = FloodFillFromEdges(px, w, h, stride, background,
                                           near, far, options.ExtraSeeds);

            if (options.Feather > 0) Feather(alpha, w, h, options.Feather);

            long cleared = 0;
            for (int i = 0; i < alpha.Length; i++) if (alpha[i] < 128) cleared++;

            Decontaminate(px, alpha, w, h, stride, background);

            var output = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
            output.Freeze();

            double fraction = (double)cleared / (w * (long)h);
            string note = fraction switch
            {
                < 0.01 => "Almost nothing was removed — try raising the tolerance.",
                > 0.97 => "Nearly everything was removed — try lowering the tolerance.",
                _      => $"Removed {fraction * 100:F0}% of the image."
            };

            return new BackgroundRemovalResult
            {
                Image = output, RemovedFraction = fraction, Note = note
            };
        }

        // ── background identification ─────────────────────────────────────────

        /// <summary>
        /// Collects the distinct colours around the border. A list rather than a single
        /// average, because a capture often sits on two backdrops at once — a window over
        /// a desktop, or a light panel meeting a dark one — and averaging them produces a
        /// colour that matches neither.
        /// </summary>
        private static List<(byte B, byte G, byte R)> SampleBorderColours(
            byte[] px, int w, int h, int stride)
        {
            var counts = new Dictionary<int, int>();

            void Sample(int x, int y)
            {
                int o = y * stride + x * 4;
                if (px[o + 3] < 16) return;                 // already transparent
                // Quantise to 5 bits per channel so near-identical shades group together.
                int key = ((px[o + 2] >> 3) << 10) | ((px[o + 1] >> 3) << 5) | (px[o] >> 3);
                counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
            }

            for (int x = 0; x < w; x++) { Sample(x, 0); Sample(x, h - 1); }
            for (int y = 0; y < h; y++) { Sample(0, y); Sample(w - 1, y); }

            int border = 2 * (w + h);
            var result = new List<(byte, byte, byte)>();

            foreach (var kv in counts)
            {
                // A colour has to hold a real share of the border to count as background;
                // this keeps a stray icon poking into the edge from being treated as one.
                if (kv.Value * 100 < border * 4) continue;

                int r = ((kv.Key >> 10) & 31) << 3;
                int g = ((kv.Key >> 5) & 31) << 3;
                int b = (kv.Key & 31) << 3;
                result.Add(((byte)b, (byte)g, (byte)r));
                if (result.Count >= 4) break;
            }

            if (result.Count == 0 && counts.Count > 0)
            {
                int best = 0, bestKey = 0;
                foreach (var kv in counts) if (kv.Value > best) { best = kv.Value; bestKey = kv.Key; }
                result.Add((
                    (byte)((bestKey & 31) << 3),
                    (byte)(((bestKey >> 5) & 31) << 3),
                    (byte)(((bestKey >> 10) & 31) << 3)));
            }

            return result;
        }

        private static double Distance(byte b, byte g, byte r,
                                       List<(byte B, byte G, byte R)> palette)
        {
            double best = double.MaxValue;
            foreach (var c in palette)
            {
                double db = b - c.B, dg = g - c.G, dr = r - c.R;
                // Weighted toward green, roughly matching how the eye judges difference,
                // so a tolerance that looks right on grey also behaves on colour.
                double d = dr * dr * 0.9 + dg * dg * 1.4 + db * db * 0.7;
                if (d < best) best = d;
            }
            return best;
        }

        // ── flood fill ────────────────────────────────────────────────────────

        /// <summary>
        /// Fills inward from every border pixel. Iterative with an explicit stack: a
        /// recursive flood fill overflows on any real screenshot.
        /// </summary>
        private static byte[] FloodFillFromEdges(byte[] px, int w, int h, int stride,
                                                 List<(byte B, byte G, byte R)> palette,
                                                 double near, double far,
                                                 List<System.Windows.Point> extraSeeds)
        {
            var alpha = new byte[w * h];
            for (int i = 0; i < alpha.Length; i++) alpha[i] = 255;

            var visited = new bool[w * h];
            var stack = new Stack<int>(Math.Max(1024, w * 2));

            void Seed(int x, int y)
            {
                if (x < 0 || y < 0 || x >= w || y >= h) return;
                int i = y * w + x;
                if (visited[i]) return;
                int o = y * stride + x * 4;
                if (Distance(px[o], px[o + 1], px[o + 2], palette) > far) return;
                visited[i] = true;
                stack.Push(i);
            }

            for (int x = 0; x < w; x++) { Seed(x, 0); Seed(x, h - 1); }
            for (int y = 0; y < h; y++) { Seed(0, y); Seed(w - 1, y); }
            foreach (var p in extraSeeds) Seed((int)p.X, (int)p.Y);

            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int x = i % w, y = i / w;
                int o = y * stride + x * 4;

                double d = Distance(px[o], px[o + 1], px[o + 2], palette);

                if (d <= near)
                {
                    alpha[i] = 0;
                }
                else
                {
                    // In the transition band: alpha rises with distance from the
                    // background, which is what preserves an anti-aliased outline.
                    double t = (d - near) / (far - near);
                    alpha[i] = (byte)Math.Clamp(t * 255.0, 0, 255);
                    continue;   // edge pixels don't propagate; stop the fill here
                }

                if (x > 0)     Seed(x - 1, y);
                if (x < w - 1) Seed(x + 1, y);
                if (y > 0)     Seed(x, y - 1);
                if (y < h - 1) Seed(x, y + 1);
            }

            return alpha;
        }

        // ── edge treatment ────────────────────────────────────────────────────

        /// <summary>
        /// Softens the mask with a separable box blur, applied only near the boundary so
        /// solid interior and solid background stay crisp.
        /// </summary>
        private static void Feather(byte[] alpha, int w, int h, int radius)
        {
            radius = Math.Clamp(radius, 1, 8);
            var temp = new byte[alpha.Length];
            int span = radius * 2 + 1;

            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int sum = 0;
                for (int x = -radius; x <= radius; x++)
                    sum += alpha[row + Math.Clamp(x, 0, w - 1)];

                for (int x = 0; x < w; x++)
                {
                    temp[row + x] = (byte)(sum / span);
                    sum -= alpha[row + Math.Clamp(x - radius, 0, w - 1)];
                    sum += alpha[row + Math.Clamp(x + radius + 1, 0, w - 1)];
                }
            }

            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int y = -radius; y <= radius; y++)
                    sum += temp[Math.Clamp(y, 0, h - 1) * w + x];

                for (int y = 0; y < h; y++)
                {
                    int i = y * w + x;
                    byte blurred = (byte)(sum / span);

                    // Only let the blur act where there's actually an edge; blurring a
                    // solid region would eat into the subject.
                    byte original = alpha[i];
                    if (original > 8 && original < 247) alpha[i] = blurred;
                    else if (Math.Abs(original - blurred) > 24) alpha[i] = blurred;

                    sum -= temp[Math.Clamp(y - radius, 0, h - 1) * w + x];
                    sum += temp[Math.Clamp(y + radius + 1, 0, h - 1) * w + x];
                }
            }
        }

        /// <summary>
        /// Removes the background's colour from partially transparent pixels.
        ///
        /// An edge pixel is a mix: observed = a·subject + (1-a)·background. Writing it out
        /// unchanged leaves the background's colour smeared through every soft edge, which
        /// is the halo you see when a cut-out is pasted onto a different backdrop. Solving
        /// for the subject's own colour removes it.
        /// </summary>
        private static void Decontaminate(byte[] px, byte[] alpha, int w, int h, int stride,
                                          List<(byte B, byte G, byte R)> palette)
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * stride, arow = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = arow + x, o = row + x * 4;
                    byte a = alpha[i];

                    if (a == 0) { px[o] = px[o + 1] = px[o + 2] = px[o + 3] = 0; continue; }

                    if (a < 250)
                    {
                        var bg = Nearest(px[o], px[o + 1], px[o + 2], palette);
                        double f = a / 255.0;

                        px[o]     = Unmix(px[o],     bg.B, f);
                        px[o + 1] = Unmix(px[o + 1], bg.G, f);
                        px[o + 2] = Unmix(px[o + 2], bg.R, f);
                    }

                    px[o + 3] = a;
                }
            }
        }

        private static byte Unmix(byte observed, byte background, double a)
        {
            if (a <= 0.02) return observed;
            double v = (observed - background * (1 - a)) / a;
            return (byte)Math.Clamp(v, 0, 255);
        }

        private static (byte B, byte G, byte R) Nearest(byte b, byte g, byte r,
                                                        List<(byte B, byte G, byte R)> palette)
        {
            double best = double.MaxValue;
            var pick = palette[0];
            foreach (var c in palette)
            {
                double db = b - c.B, dg = g - c.G, dr = r - c.R;
                double d = dr * dr + dg * dg + db * db;
                if (d < best) { best = d; pick = c; }
            }
            return pick;
        }
    }
}
