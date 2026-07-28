using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// The growing output image. Rows are appended as raw memory copies rather than
    /// Graphics.DrawImage so the result is bit-exact — no resampling, no colour shift
    /// along the seams.
    /// </summary>
    internal sealed class StitchCanvas : IDisposable
    {
        private Bitmap _bmp;
        private readonly int _width;
        private readonly int _maxHeight;
        private int _used;
        private bool _disposed;

        internal int Height => _used;
        internal int Width => _width;
        internal bool ReachedLimit { get; private set; }

        internal StitchCanvas(int width, int initialHeight, int maxHeight)
        {
            _width = Math.Max(1, width);
            _maxHeight = Math.Max(initialHeight, maxHeight);
            _bmp = new Bitmap(_width, Math.Max(1, Math.Min(initialHeight, _maxHeight)),
                              PixelFormat.Format32bppArgb);
        }

        /// <summary>Appends <paramref name="rows"/> rows starting at <paramref name="srcY"/>.</summary>
        internal int Append(Bitmap src, int srcY, int rows)
        {
            if (rows <= 0 || src.Width <= 0) return 0;

            srcY = Math.Max(0, srcY);
            rows = Math.Min(rows, src.Height - srcY);
            if (rows <= 0) return 0;

            if (_used + rows > _maxHeight)
            {
                rows = _maxHeight - _used;
                ReachedLimit = true;
                if (rows <= 0) return 0;
            }

            EnsureCapacity(_used + rows);

            int copyWidth = Math.Min(_width, src.Width);

            BitmapData? srcData = null, dstData = null;
            try
            {
                srcData = src.LockBits(new Rectangle(0, srcY, src.Width, rows),
                                       ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                dstData = _bmp.LockBits(new Rectangle(0, _used, _bmp.Width, rows),
                                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                var byteCount = (UIntPtr)(uint)(copyWidth * 4);
                for (int y = 0; y < rows; y++)
                {
                    Win32.CopyMemory(dstData.Scan0 + y * dstData.Stride,
                                     srcData.Scan0 + y * srcData.Stride,
                                     byteCount);
                }
            }
            catch
            {
                return 0;
            }
            finally
            {
                if (srcData != null) { try { src.UnlockBits(srcData); } catch { } }
                if (dstData != null) { try { _bmp.UnlockBits(dstData); } catch { } }
            }

            _used += rows;
            return rows;
        }

        private void EnsureCapacity(int needed)
        {
            if (needed <= _bmp.Height) return;

            int grown = Math.Min(_maxHeight, Math.Max(needed, _bmp.Height + _bmp.Height / 2 + 256));
            var bigger = new Bitmap(_width, grown, PixelFormat.Format32bppArgb);

            using (var g = Graphics.FromImage(bigger))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                g.DrawImage(_bmp, new Rectangle(0, 0, _width, _used),
                                  new Rectangle(0, 0, _width, _used), GraphicsUnit.Pixel);
            }

            _bmp.Dispose();
            _bmp = bigger;
        }

        /// <summary>Hands back the finished image, cropped to the rows actually written.</summary>
        internal Bitmap ToBitmap()
        {
            int h = Math.Max(1, _used);
            if (h == _bmp.Height)
            {
                var whole = _bmp;
                _bmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
                _used = 0;
                return whole;
            }

            var cropped = new Bitmap(_width, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(cropped))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(_bmp, new Rectangle(0, 0, _width, h),
                                  new Rectangle(0, 0, _width, h), GraphicsUnit.Pixel);
            }
            return cropped;
        }

        /// <summary>A small thumbnail of what's been stitched so far, for the progress UI.</summary>
        internal Bitmap? CreatePreview(int maxWidth, int maxHeight)
        {
            if (_used <= 0) return null;
            try
            {
                double scale = Math.Min((double)maxWidth / _width, (double)maxHeight / _used);
                scale = Math.Min(scale, 1.0);
                int w = Math.Max(1, (int)(_width * scale));
                int h = Math.Max(1, (int)(_used * scale));

                var preview = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(preview);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(_bmp, new Rectangle(0, 0, w, h),
                                  new Rectangle(0, 0, _width, _used), GraphicsUnit.Pixel);
                return preview;
            }
            catch { return null; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bmp.Dispose();
        }
    }
}
