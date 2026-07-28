using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace AdvancedSnip.Services
{
    /// <summary>One recognised word and where it sits, in image pixels.</summary>
    internal sealed class OcrWord
    {
        public required string Text { get; init; }
        public required System.Windows.Rect Box { get; init; }
        /// <summary>Index of the line this belongs to, for whole-line selection.</summary>
        public required int Line { get; init; }
    }

    /// <summary>
    /// Recognised text plus geometry. Words are kept in reading order, which is what lets
    /// a drag between two points select everything in between the way a PDF viewer does.
    /// </summary>
    internal sealed class OcrLayout
    {
        public List<OcrWord> Words { get; } = new();
        public string Text { get; set; } = "";
        public bool Any => Words.Count > 0;

        /// <summary>Joins a run of words back into text, breaking lines where they broke.</summary>
        public string Join(int from, int to)
        {
            if (Words.Count == 0) return "";
            from = Math.Clamp(from, 0, Words.Count - 1);
            to   = Math.Clamp(to,   0, Words.Count - 1);
            if (from > to) (from, to) = (to, from);

            var sb = new System.Text.StringBuilder();
            int line = Words[from].Line;

            for (int i = from; i <= to; i++)
            {
                if (Words[i].Line != line) { sb.Append('\n'); line = Words[i].Line; }
                else if (i > from) sb.Append(' ');
                sb.Append(Words[i].Text);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Text recognition, using the OCR engine built into Windows.
    ///
    /// Chosen over Tesseract or a cloud service for three reasons: it needs no NuGet
    /// package, no model file and no network; it runs entirely offline so captures never
    /// leave the machine; and it automatically uses whichever language packs the user has
    /// installed rather than shipping one language and pretending that's enough.
    ///
    /// Everything goes through a file path rather than an in-memory buffer. Converting a
    /// WPF BitmapSource to a WinRT SoftwareBitmap in memory needs the AsBuffer/AsStream
    /// interop extensions, whose availability varies with how the WinRT projections are
    /// referenced. StorageFile is core WinRT and always present. For gallery indexing the
    /// image is already a file, so this is the fast path anyway; the editor pays one small
    /// temp-file write, which is negligible next to the recognition itself.
    ///
    /// Note the deliberately narrow using list: WPF and WinRT both define BitmapDecoder
    /// and BitmapFrame, so importing System.Windows.Media.Imaging alongside
    /// Windows.Graphics.Imaging would make both names ambiguous. The WinRT namespace is
    /// imported (it supplies most of the types here) and the two WPF types are written
    /// out in full.
    /// </summary>
    internal static class OcrService
    {
        private static OcrEngine? _engine;
        private static bool _resolved;
        private static readonly object _gate = new();

        /// <summary>
        /// False when Windows has no OCR language pack installed. Everything that uses
        /// OCR checks this and degrades rather than failing.
        /// </summary>
        internal static bool IsAvailable => Engine != null;

        internal static string UnavailableReason =>
            "Windows has no OCR language installed. Add one under " +
            "Settings → Time & language → Language & region → your language → " +
            "Language options → Optical character recognition.";

        private static OcrEngine? Engine
        {
            get
            {
                lock (_gate)
                {
                    if (_resolved) return _engine;
                    _resolved = true;
                    try { _engine = OcrEngine.TryCreateFromUserProfileLanguages(); }
                    catch { _engine = null; }
                    return _engine;
                }
            }
        }

        /// <summary>Reads the text in an image file. Returns "" when it can't.</summary>
        internal static async Task<string> ReadFileAsync(string path, CancellationToken ct = default)
        {
            var engine = Engine;
            if (engine == null) return "";

            try
            {
                ct.ThrowIfCancellationRequested();

                var file = await StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenAsync(FileAccessMode.Read);
                var decoder = await BitmapDecoder.CreateAsync(stream);

                ct.ThrowIfCancellationRequested();

                var prepared = await DecodeForOcrAsync(decoder);
                using var bitmap = prepared.Bitmap;
                var result = await engine.RecognizeAsync(bitmap);
                return result?.Text ?? "";
            }
            catch (OperationCanceledException) { throw; }
            catch { return ""; }
        }

        /// <summary>
        /// Reads the text in an in-memory image, optionally limited to one region.
        /// Used by the editor, where the picture on screen may have unsaved edits.
        /// </summary>
        internal static async Task<string> ReadImageAsync(
            System.Windows.Media.Imaging.BitmapSource image, CancellationToken ct = default)
        {
            if (Engine == null) return "";

            string temp = Path.Combine(Path.GetTempPath(),
                $"advsnip_ocr_{Guid.NewGuid():N}.png");
            try
            {
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(
                    System.Windows.Media.Imaging.BitmapFrame.Create(image));
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
                    encoder.Save(fs);

                return await ReadFileAsync(temp, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { return ""; }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        /// <summary>
        /// Recognition with geometry, for selecting text directly on the picture.
        /// </summary>
        internal static async Task<OcrLayout> ReadLayoutAsync(
            System.Windows.Media.Imaging.BitmapSource image, CancellationToken ct = default)
        {
            if (Engine == null) return new OcrLayout();

            string temp = Path.Combine(Path.GetTempPath(),
                $"advsnip_ocr_{Guid.NewGuid():N}.png");
            try
            {
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(
                    System.Windows.Media.Imaging.BitmapFrame.Create(image));
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
                    encoder.Save(fs);

                return await ReadLayoutFileAsync(temp, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { return new OcrLayout(); }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        internal static async Task<OcrLayout> ReadLayoutFileAsync(
            string path, CancellationToken ct = default)
        {
            var engine = Engine;
            if (engine == null) return new OcrLayout();

            try
            {
                ct.ThrowIfCancellationRequested();

                var file = await StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenAsync(FileAccessMode.Read);
                var decoder = await BitmapDecoder.CreateAsync(stream);

                var prepared = await DecodeForOcrAsync(decoder);
                using var bitmap = prepared.Bitmap;

                ct.ThrowIfCancellationRequested();
                var result = await engine.RecognizeAsync(bitmap);

                // Boxes come back in the coordinates of whatever was fed to the engine.
                // If the image was scaled down to fit the size limit, they have to be
                // scaled back or every highlight lands in the wrong place.
                double back = prepared.Scale <= 0 ? 1.0 : 1.0 / prepared.Scale;

                var layout = new OcrLayout { Text = result?.Text ?? "" };
                if (result == null) return layout;

                int lineIndex = 0;
                foreach (var line in result.Lines)
                {
                    foreach (var word in line.Words)
                    {
                        var r = word.BoundingRect;
                        layout.Words.Add(new OcrWord
                        {
                            Text = word.Text,
                            Line = lineIndex,
                            Box  = new System.Windows.Rect(
                                r.X * back, r.Y * back, r.Width * back, r.Height * back)
                        });
                    }
                    lineIndex++;
                }
                return layout;
            }
            catch (OperationCanceledException) { throw; }
            catch { return new OcrLayout(); }
        }

        /// <summary>
        /// Produces the bitmap the engine wants: Bgra8, and within the engine's size
        /// limit. A tall scroll capture can easily exceed that limit, and the API fails
        /// outright rather than scaling for you.
        /// </summary>
        private static async Task<(SoftwareBitmap Bitmap, double Scale)> DecodeForOcrAsync(
            BitmapDecoder decoder)
        {
            uint w = decoder.PixelWidth, h = decoder.PixelHeight;
            uint limit = (uint)OcrEngine.MaxImageDimension;

            if (w <= limit && h <= limit)
                return (await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied), 1.0);

            double scale = (double)limit / Math.Max(w, h);
            var transform = new BitmapTransform
            {
                ScaledWidth  = Math.Max(1u, (uint)(w * scale)),
                ScaledHeight = Math.Max(1u, (uint)(h * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            var scaled = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            return (scaled, scale);
        }
    }
}
