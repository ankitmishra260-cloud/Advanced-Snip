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
        /// <summary>Identified hyperlink destination if this word is a clickable URL.</summary>
        public string? Url { get; set; }
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

        // ── spatial index ─────────────────────────────────────────────────────
        //
        // Hit-testing used to scan every word on every mouse-move. On an ordinary
        // screenshot that is a few hundred comparisons and invisible; on a tall scroll
        // capture it is tens of thousands, sixty times a second, and the editor crawls.
        // The rows below bucket words by height band so a hit test looks at the handful
        // near the pointer instead of all of them.

        private const double BandHeight = 96;
        private Dictionary<int, List<int>>? _rows;

        private void BuildIndex()
        {
            _rows = new Dictionary<int, List<int>>();
            for (int i = 0; i < Words.Count; i++)
            {
                var box = Words[i].Box;
                int first = (int)(box.Y / BandHeight);
                int last = (int)(box.Bottom / BandHeight);

                for (int band = first; band <= last; band++)
                {
                    if (!_rows.TryGetValue(band, out var list))
                        _rows[band] = list = new List<int>();
                    list.Add(i);
                }
            }
        }

        /// <summary>
        /// Index of the word nearest a point, or -1 when there are none.
        ///
        /// Falling back to nearest rather than requiring a direct hit means the gaps
        /// between words do not swallow a drag. The search widens a band at a time and
        /// stops as soon as a further band could not hold anything closer.
        /// </summary>
        public int NearestWord(System.Windows.Point point)
        {
            if (Words.Count == 0) return -1;
            if (_rows == null) BuildIndex();

            int centre = (int)(point.Y / BandHeight);
            int best = -1;
            double bestDistance = double.MaxValue;

            for (int spread = 0; spread < 64; spread++)
            {
                bool examined = false;

                for (int side = spread == 0 ? 0 : -1; side <= 1; side += 2)
                {
                    int band = centre + spread * side;
                    if (!_rows!.TryGetValue(band, out var list)) { if (spread == 0) break; continue; }

                    examined = true;
                    foreach (int i in list)
                    {
                        var b = Words[i].Box;
                        if (b.Contains(point)) return i;

                        double dx = Math.Max(0, Math.Max(b.X - point.X, point.X - b.Right));
                        double dy = Math.Max(0, Math.Max(b.Y - point.Y, point.Y - b.Bottom));
                        // Vertical distance counts for more, so a point in the margin picks
                        // the word on its own line rather than one above or below.
                        double d = dx * dx + dy * dy * 4;
                        if (d < bestDistance) { bestDistance = d; best = i; }
                    }

                    if (spread == 0) break;
                }

                // Once something has been found, one more band out is enough to be sure
                // nothing closer is hiding: bands are taller than any line of text.
                if (best >= 0 && examined && spread > 0) break;
                if (best >= 0 && spread > 1) break;
            }

            return best;
        }

        /// <summary>Words whose boxes fall inside a rectangle, for drawing only the visible ones.</summary>
        public void WordsIn(System.Windows.Rect area, List<int> into)
        {
            into.Clear();
            if (Words.Count == 0) return;
            if (_rows == null) BuildIndex();

            int first = (int)(area.Y / BandHeight);
            int last = (int)(area.Bottom / BandHeight);

            for (int band = first; band <= last; band++)
            {
                if (!_rows!.TryGetValue(band, out var list)) continue;
                foreach (int i in list)
                    if (Words[i].Box.IntersectsWith(area) && !into.Contains(i)) into.Add(i);
            }

            into.Sort();
        }

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
                else if (i > from && SeparatedFrom(i - 1, i)) sb.Append(' ');
                sb.Append(Words[i].Text);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Whether two words on the same line had real whitespace between them.
        ///
        /// The engine splits on more than spaces: currency symbols, brackets and stray
        /// punctuation frequently come back as their own "words" even when the picture
        /// shows them pressed against what they belong to. Joining every neighbour with a
        /// space turns "$5" into "$ 5", "(3.14)" into "( 3.14 )" and "12:30" into
        /// "12 : 30" — which reads as the numbers and symbols having been mangled, and is
        /// the same complaint as losing them outright.
        ///
        /// The gap is measured against glyph height rather than a fixed pixel count, so it
        /// holds at any resolution or zoom.
        /// </summary>
        private bool SeparatedFrom(int previous, int current)
        {
            var a = Words[previous].Box;
            var b = Words[current].Box;

            double height = Math.Max(a.Height, b.Height);
            if (height <= 0) return true;

            // Boxes that overlap or nearly touch were one run of glyphs in the picture.
            return (b.X - a.Right) > height * 0.28;
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
                    try
                    {
                        var usLang = new Windows.Globalization.Language("en-US");
                        if (OcrEngine.IsLanguageSupported(usLang))
                        {
                            _engine = OcrEngine.TryCreateFromLanguage(usLang);
                        }
                        if (_engine == null)
                        {
                            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
                        }
                    }
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

                return await ReadBandedFileAsync(engine, decoder, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { return ""; }
        }

        /// <summary>
        /// Reads a file in horizontal bands, decoding one strip at a time.
        ///
        /// **This is what the gallery index was missing.** It used to hand the whole file to
        /// the engine in one go, and the engine has a hard size limit — so a tall capture
        /// was scaled down to fit it. A 1920×12000 scroll capture scales by about 0.22,
        /// which turns 14-pixel text into 3-pixel text and returns nothing at all. The
        /// editor never hit this because its path already bands; the two disagreed, and the
        /// visible symptom was a picture whose text you could read and copy in the editor
        /// and could not find by searching the gallery.
        ///
        /// Bands rather than one scaled image, and decoded per band rather than cropped
        /// from a whole one: this never materialises the full picture. Each band is at most
        /// the engine's limit square, so the memory in flight is bounded no matter how tall
        /// the capture is — which matters because the indexer runs several of these at once.
        ///
        /// **The order of `ScaledWidth`/`ScaledHeight` and `Bounds` is a trap.** WinRT
        /// applies the scale *first* and then interprets the bounds against the scaled
        /// image, so the band rectangle below is in scaled coordinates, not source ones.
        /// </summary>
        private static async Task<string> ReadBandedFileAsync(
            OcrEngine engine, BitmapDecoder decoder, CancellationToken ct)
        {
            uint sourceW = decoder.PixelWidth, sourceH = decoder.PixelHeight;
            if (sourceW == 0 || sourceH == 0) return "";

            uint limit = (uint)OcrEngine.MaxImageDimension;

            // Same budget rule as DecodeForOcrAsync: fill the engine's width, and enlarge a
            // small image rather than leaving accuracy unclaimed. Width alone decides it —
            // height is handled by banding, so a tall picture no longer drags the scale down.
            double scale = Math.Min((double)limit / sourceW, MaxUpscale);

            uint scaledW = Math.Max(1, (uint)Math.Round(sourceW * scale));
            uint scaledH = Math.Max(1, (uint)Math.Round(sourceH * scale));

            // Overlap so a line of text lying across a seam is whole inside at least one
            // band. Duplicated words are harmless here: this text is only ever searched,
            // never displayed, so a repeated line costs nothing and a lost one costs a hit.
            const uint Overlap = 240;

            uint bandH = Math.Min(scaledH, limit);
            uint step = bandH > Overlap ? bandH - Overlap : bandH;

            var text = new System.Text.StringBuilder();

            // Termination is on coverage, not on the step. Stepping until y runs past the
            // height adds a trailing sliver band the width of the overlap — for an ordinary
            // screenshot, which fits in one band, that is a second pass over the bottom
            // 240 pixels and roughly double the work for nothing.
            for (uint y = 0; y < scaledH; )
            {
                ct.ThrowIfCancellationRequested();

                uint height = Math.Min(bandH, scaledH - y);
                if (height == 0) break;

                var transform = new BitmapTransform
                {
                    ScaledWidth = scaledW,
                    ScaledHeight = scaledH,
                    InterpolationMode = scale > 1.0
                        ? BitmapInterpolationMode.Cubic
                        : BitmapInterpolationMode.Fant,
                    Bounds = new BitmapBounds { X = 0, Y = y, Width = scaledW, Height = height }
                };

                using var band = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                var result = await engine.RecognizeAsync(band);
                if (!string.IsNullOrWhiteSpace(result?.Text))
                {
                    if (text.Length > 0) text.Append('\n');
                    text.Append(NormalizeOcrText(result!.Text));
                }

                if (y + height >= scaledH) break;   // that band reached the bottom
                y += step;
            }

            return text.ToString();
        }

        /// <summary>
        /// Reads the text in an in-memory image.
        /// Used by the editor, where the picture on screen may have unsaved edits.
        /// </summary>
        internal static async Task<string> ReadImageAsync(
            System.Windows.Media.Imaging.BitmapSource image, CancellationToken ct = default,
            IProgress<double>? progress = null)
        {
            var layout = await ReadLayoutAsync(image, ct, progress);
            return layout.Text;
        }

        /// <summary>
        /// Recognition with geometry, for selecting text directly on the picture.
        ///
        /// **Everything expensive happens off the UI thread.** The previous version
        /// PNG-encoded the whole image before its first await, which for an ordinary
        /// screenshot is imperceptible and for a 20,000-pixel scroll capture is hundreds of
        /// megabytes of compression on the thread that draws the window — the editor simply
        /// stopped responding, which is exactly why cropping such a capture first "fixed"
        /// it. The work now runs on a worker thread and is fed to the engine in bands.
        /// </summary>
        internal static async Task<OcrLayout> ReadLayoutAsync(
            System.Windows.Media.Imaging.BitmapSource image, CancellationToken ct = default,
            IProgress<double>? progress = null)
        {
            if (Engine == null) return new OcrLayout();

            // A BitmapSource has thread affinity unless it is frozen. Everything the editor
            // holds is frozen already; anything that somehow is not gets copied here rather
            // than touched from a worker thread.
            var subject = image;
            if (!subject.IsFrozen)
            {
                if (!subject.CanFreeze) return new OcrLayout();
                subject = (System.Windows.Media.Imaging.BitmapSource)subject.Clone();
                subject.Freeze();
            }

            try
            {
                return await Task.Run(() => ReadLayoutBandedAsync(subject, ct, progress), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { return new OcrLayout(); }
        }

        /// <summary>
        /// The banded and tiled read. Runs on a worker thread.
        ///
        /// Captures are partitioned into overlapping tiles of ~1050px. This allows
        /// DecodeForOcrAsync to upscale each tile by 2.4x to 3.5x within the engine's 2600px limit,
        /// ensuring small UI glyphs, numbers, and symbols are rendered large enough for high-accuracy recognition.
        /// </summary>
        private static async Task<OcrLayout> ReadLayoutBandedAsync(
            System.Windows.Media.Imaging.BitmapSource image, CancellationToken ct,
            IProgress<double>? progress)
        {
            var engine = Engine;
            if (engine == null) return new OcrLayout();

            int width = image.PixelWidth;
            int height = image.PixelHeight;
            if (width <= 0 || height <= 0) return new OcrLayout();

            const int TileSize = 1050;
            const int Overlap = 180;

            if (width <= 1100 && height <= 1100)
            {
                var single = await RecogniseRegionAsync(engine, image,
                    new System.Windows.Int32Rect(0, 0, width, height), 0, 0, ct);
                progress?.Report(1);
                return single;
            }

            int cols = width <= 1100 ? 1 : (int)Math.Ceiling((double)(width - Overlap) / (TileSize - Overlap));
            int rows = height <= 1100 ? 1 : (int)Math.Ceiling((double)(height - Overlap) / (TileSize - Overlap));
            int totalTiles = cols * rows;
            int completedTiles = 0;

            var layout = new OcrLayout();
            var text = new System.Text.StringBuilder();

            double claimedX = 0;
            for (int c = 0; c < cols; c++)
            {
                int tileX = c == 0 ? 0 : Math.Min(width - TileSize, c * (TileSize - Overlap));
                if (tileX < 0) tileX = 0;
                int tileW = Math.Min(TileSize, width - tileX);

                bool lastCol = (c == cols - 1) || (tileX + tileW >= width);
                double keepFromX = claimedX;
                double keepToX = lastCol ? double.MaxValue : tileX + tileW - (Overlap / 2.0);

                double claimedY = 0;
                for (int r = 0; r < rows; r++)
                {
                    ct.ThrowIfCancellationRequested();

                    int tileY = r == 0 ? 0 : Math.Min(height - TileSize, r * (TileSize - Overlap));
                    if (tileY < 0) tileY = 0;
                    int tileH = Math.Min(TileSize, height - tileY);

                    bool lastRow = (r == rows - 1) || (tileY + tileH >= height);
                    double keepFromY = claimedY;
                    double keepToY = lastRow ? double.MaxValue : tileY + tileH - (Overlap / 2.0);

                    var region = new System.Windows.Int32Rect(tileX, tileY, tileW, tileH);
                    var tile = await RecogniseRegionAsync(engine, image, region, tileX, tileY, ct);

                    foreach (var word in tile.Words)
                    {
                        double centreX = word.Box.X + word.Box.Width / 2.0;
                        double centreY = word.Box.Y + word.Box.Height / 2.0;

                        if (centreX >= keepFromX && centreX < keepToX &&
                            centreY >= keepFromY && centreY < keepToY)
                        {
                            layout.Words.Add(word);
                        }
                    }

                    claimedY = keepToY;
                    completedTiles++;
                    progress?.Report(Math.Clamp((double)completedTiles / Math.Max(1, totalTiles), 0, 1));

                    if (lastRow) break;
                }

                claimedX = keepToX;
                if (lastCol) break;
            }

            // Sort words in reading order (lines top-to-bottom, words left-to-right)
            layout.Words.Sort((a, b) =>
            {
                double diffY = a.Box.Y - b.Box.Y;
                double avgH = Math.Max(10, (a.Box.Height + b.Box.Height) / 2.0);
                if (Math.Abs(diffY) > avgH * 0.45) return a.Box.Y.CompareTo(b.Box.Y);
                return a.Box.X.CompareTo(b.Box.X);
            });

            // Reconstruct reading lines and full text
            int currentLine = 0;
            double lastLineBottom = -1;
            for (int i = 0; i < layout.Words.Count; i++)
            {
                var w = layout.Words[i];
                double baseline = w.Box.Y + w.Box.Height * 0.75;
                if (lastLineBottom < 0 || Math.Abs(baseline - lastLineBottom) > Math.Max(8, w.Box.Height * 0.55))
                {
                    if (lastLineBottom >= 0) text.Append('\n');
                    currentLine++;
                    lastLineBottom = baseline;
                }
                else if (i > 0)
                {
                    text.Append(' ');
                }

                layout.Words[i] = new OcrWord
                {
                    Text = w.Text,
                    Box = w.Box,
                    Line = currentLine,
                    Url = w.Url
                };
                text.Append(w.Text);
            }

            layout.Text = text.ToString();
            DetectUrls(layout.Words);
            return layout;
        }

        /// <summary>
        /// Recognises one rectangle of an image, returning boxes in whole-image coordinates.
        /// </summary>
        private static async Task<OcrLayout> RecogniseRegionAsync(
            OcrEngine engine, System.Windows.Media.Imaging.BitmapSource image,
            System.Windows.Int32Rect region, int xOffset, int yOffset, CancellationToken ct)
        {
            string temp = Path.Combine(Path.GetTempPath(),
                $"advsnip_ocr_{Guid.NewGuid():N}.png");

            try
            {
                ct.ThrowIfCancellationRequested();

                System.Windows.Media.Imaging.BitmapSource slice = image;
                if (region.X != 0 || region.Y != 0
                    || region.Width != image.PixelWidth || region.Height != image.PixelHeight)
                {
                    slice = new System.Windows.Media.Imaging.CroppedBitmap(image, region);
                    slice.Freeze();
                }

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(slice));
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
                    encoder.Save(fs);

                var raw = await ReadLayoutFileAsync(temp, ct);

                if (xOffset == 0 && yOffset == 0) return raw;

                var shifted = new OcrLayout { Text = raw.Text };
                foreach (var w in raw.Words)
                    shifted.Words.Add(new OcrWord
                    {
                        Text = w.Text,
                        Line = w.Line,
                        Box = new System.Windows.Rect(w.Box.X + xOffset, w.Box.Y + yOffset,
                                                      w.Box.Width, w.Box.Height),
                        Url = w.Url
                    });
                return shifted;
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

                double back = prepared.Scale <= 0 ? 1.0 : 1.0 / prepared.Scale;

                var layout = new OcrLayout { Text = result?.Text ?? "" };
                if (result == null) return layout;

                int lineIndex = 0;
                foreach (var line in result.Lines)
                {
                    foreach (var word in line.Words)
                    {
                        var r = word.BoundingRect;
                        string text = NormalizeOcrText(word.Text);
                        layout.Words.Add(new OcrWord
                        {
                            Text = text,
                            Line = lineIndex,
                            Box  = new System.Windows.Rect(
                                r.X * back, r.Y * back, r.Width * back, r.Height * back)
                        });
                    }
                    lineIndex++;
                }

                DetectUrls(layout.Words);
                return layout;
            }
            catch (OperationCanceledException) { throw; }
            catch { return new OcrLayout(); }
        }

        /// <summary>
        /// Normalizes OCR text quirks on screenshots (e.g. slashed zeros in monospace fonts recognized as ø/Ø).
        /// </summary>
        private static string NormalizeOcrText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 1. Slashed zero (ø / Ø) replacement in numeric or isolated context
            // e.g. "294ø" -> "2940", "ø.8" -> "0.8", "3øøø" -> "3000", "ø.ø" -> "0.0", "ø%" -> "0%", "10:øø" -> "10:00"
            if (text.Contains('ø') || text.Contains('Ø'))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^[\$€£¥\(\[\{]?[øØ\d\.,:\-%/]+[\)\]\}%]?$"))
                {
                    text = text.Replace('ø', '0').Replace('Ø', '0');
                }
            }

            // 2. Trailing 'a' in decimal numbers e.g. "345.9a" -> "345.90"
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d+\.\d+[aA]$"))
            {
                text = text[..^1] + "0";
            }

            return text;
        }

        private static readonly System.Text.RegularExpressions.Regex UrlPattern = new(
            @"^(?:https?:\/\/|www\.)[^\s<>""]+|[a-zA-Z0-9.-]+\.(?:com|org|net|edu|gov|io|ai|dev|app|co|uk|de|in|me|info|biz)(?:\/[^\s<>""]*)?$",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static void DetectUrls(List<OcrWord> words)
        {
            if (words.Count == 0) return;

            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                string candidate = CleanUrlPunctuation(w.Text);
                if (UrlPattern.IsMatch(candidate))
                {
                    w.Url = NormalizeUrl(candidate);
                    continue;
                }

                // Check two adjacent words on the same line (e.g. "https://" and "github.com/...")
                if (i + 1 < words.Count && words[i + 1].Line == w.Line)
                {
                    string pair = CleanUrlPunctuation(w.Text + words[i + 1].Text);
                    if (UrlPattern.IsMatch(pair))
                    {
                        string fullUrl = NormalizeUrl(pair);
                        w.Url = fullUrl;
                        words[i + 1].Url = fullUrl;
                        i++; // skip next word
                    }
                }
            }
        }

        private static string CleanUrlPunctuation(string str)
        {
            if (string.IsNullOrEmpty(str)) return "";
            return str.Trim().TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'').TrimStart('(', '[', '{', '"', '\'');
        }

        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
            return "https://" + url;
        }

        /// <summary>
        /// How much larger than life the engine is allowed to be shown an image.
        ///
        /// Windows OCR is trained on document-scale text and reads UI-scale text badly.
        /// A screenshot at 100% has glyphs around 11–16 pixels tall, which is right at the
        /// edge of what it can resolve — and what falls off that edge first is not whole
        /// words but *single narrow glyphs*: digits, and punctuation. "1" against "l",
        /// "0" against "O", a full stop against sensor noise. Letters survive because a
        /// word of them carries enough redundancy to be guessed from context; a lone "7"
        /// carries none.
        ///
        /// That is one root cause behind two separate complaints — selections in the
        /// editor dropping numbers and symbols while keeping the letters around them, and
        /// gallery search failing to index pictures whose text looks perfectly clear.
        /// Handing the engine a larger image fixes both, and costs a resize.
        /// </summary>
        private const double MaxUpscale = 3.0;

        /// <summary>
        /// Produces the bitmap the engine wants: Bgra8, and within the engine's size
        /// limit — but also not far *below* it.
        ///
        /// A tall scroll capture exceeds the limit and the API fails outright rather than
        /// scaling for you, which is what the downscale is for. The upscale is the other
        /// half of the same problem: an image well under the limit is leaving recognition
        /// accuracy unclaimed, and the headroom is free.
        /// </summary>
        private static async Task<(SoftwareBitmap Bitmap, double Scale)> DecodeForOcrAsync(
            BitmapDecoder decoder)
        {
            uint w = decoder.PixelWidth, h = decoder.PixelHeight;
            uint limit = (uint)OcrEngine.MaxImageDimension;
            uint longest = Math.Max(w, h);
            if (longest == 0) longest = 1;

            // Fill the engine's budget, up to the upscale cap. Downscaling and upscaling
            // are the same expression: whichever side of 1.0 it lands on, the box maths
            // downstream already divides by it.
            double scale = Math.Min((double)limit / longest, MaxUpscale);

            // Not worth a resize for a few percent either way.
            if (scale > 0.98 && scale < 1.02)
                return (await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied), 1.0);

            var transform = new BitmapTransform
            {
                ScaledWidth  = Math.Max(1u, (uint)(w * scale)),
                ScaledHeight = Math.Max(1u, (uint)(h * scale)),

                // Cubic keeps glyph edges crisp when enlarging, which is the whole point
                // of enlarging. Fant is an area filter — the right choice when shrinking,
                // and a blur when growing.
                InterpolationMode = scale > 1.0
                    ? BitmapInterpolationMode.Cubic
                    : BitmapInterpolationMode.Fant
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