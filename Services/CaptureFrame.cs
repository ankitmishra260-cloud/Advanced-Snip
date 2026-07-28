using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// One captured frame plus a compact per-row fingerprint.
    ///
    /// The fingerprint is what makes scroll stitching accurate. Each row is reduced to
    /// <see cref="Cols"/> averaged luminance cells, so two rows can be compared with 64
    /// byte subtractions instead of a few thousand pixel reads. Averaging over a cell
    /// (rather than sampling single pixels) keeps the comparison stable against sub-pixel
    /// text antialiasing, which shifts slightly whenever content moves.
    /// </summary>
    internal sealed class CaptureFrame : IDisposable
    {
        internal const int Cols = 64;

        /// <summary>Rows whose summed cell difference is below this count as identical.</summary>
        internal const int RowTolerance = 3 * Cols;

        internal Bitmap Bitmap { get; }
        internal int Width { get; }
        internal int Height { get; }

        private readonly byte[] _sig;
        private bool _disposed;

        internal CaptureFrame(Bitmap bmp)
        {
            Bitmap = bmp;
            Width = bmp.Width;
            Height = bmp.Height;
            _sig = new byte[Math.Max(1, Height) * Cols];
            BuildSignature();
        }

        internal static CaptureFrame Grab(Rectangle region)
            => new CaptureFrame(ScreenCapture.CaptureScreenRect(region));

        private void BuildSignature()
        {
            if (Width <= 0 || Height <= 0) return;

            var rect = new Rectangle(0, 0, Width, Height);
            BitmapData? data = null;
            try
            {
                data = Bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

                int stride = Math.Abs(data.Stride);
                var row = new byte[stride];

                // Pre-compute cell boundaries once.
                var bounds = new int[Cols + 1];
                for (int c = 0; c <= Cols; c++)
                    bounds[c] = (int)((long)c * Width / Cols);

                for (int y = 0; y < Height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, stride);
                    int baseIdx = y * Cols;

                    for (int c = 0; c < Cols; c++)
                    {
                        int x0 = bounds[c];
                        int x1 = Math.Max(x0 + 1, bounds[c + 1]);
                        if (x1 > Width) x1 = Width;

                        int step = Math.Max(1, (x1 - x0) / 6);
                        long sum = 0;
                        int n = 0;
                        for (int x = x0; x < x1; x += step)
                        {
                            int i = x * 4;
                            if (i + 2 >= stride) break;
                            // BGRA in memory; standard luma weights.
                            sum += (row[i + 2] * 77 + row[i + 1] * 151 + row[i] * 28) >> 8;
                            n++;
                        }
                        _sig[baseIdx + c] = (byte)(n > 0 ? sum / n : 0);
                    }
                }
            }
            catch
            {
                // A locked or torn-down bitmap just yields a zero signature; callers
                // degrade to "frames look identical" rather than crashing.
            }
            finally
            {
                if (data != null)
                {
                    try { Bitmap.UnlockBits(data); } catch { }
                }
            }
        }

        /// <summary>Summed absolute cell difference between row <paramref name="y"/> here and row <paramref name="oy"/> there.</summary>
        internal int RowDistance(int y, CaptureFrame other, int oy)
        {
            if (y < 0 || y >= Height || oy < 0 || oy >= other.Height) return int.MaxValue;

            int a = y * Cols, b = oy * Cols;
            int sum = 0;
            for (int c = 0; c < Cols; c++)
                sum += Math.Abs(_sig[a + c] - other._sig[b + c]);
            return sum;
        }

        internal bool RowsSimilar(int y, CaptureFrame other, int oy, int tolerance = RowTolerance)
            => RowDistance(y, other, oy) <= tolerance;

        /// <summary>
        /// Difference of a single cell against a possibly different row in another frame.
        /// Comparing a cell here against the row it should have come from lets us tell
        /// which columns actually travel with the content and which don't.
        /// </summary>
        internal int CellDistance(int y, int col, CaptureFrame other, int otherY)
        {
            if (y < 0 || y >= Height || otherY < 0 || otherY >= other.Height) return 0;
            if (col < 0 || col >= Cols) return 0;
            return Math.Abs(_sig[y * Cols + col] - other._sig[otherY * Cols + col]);
        }

        /// <summary>Left edge, in pixels, of signature cell <paramref name="col"/>.</summary>
        internal int CellLeft(int col) => (int)((long)Math.Clamp(col, 0, Cols) * Width / Cols);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Bitmap.Dispose();
        }
    }
}
