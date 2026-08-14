using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdvancedSnip.Services
{
    /// <summary>A named paper size in PDF points (1 pt = 1/72 inch).</summary>
    internal sealed class PaperSize
    {
        internal required string Name { get; init; }
        internal required double WidthPt { get; init; }
        internal required double HeightPt { get; init; }

        internal double WidthMm => WidthPt / 72.0 * 25.4;
        internal double HeightMm => HeightPt / 72.0 * 25.4;

        public override string ToString() =>
            $"{Name}  ({WidthMm:0} × {HeightMm:0} mm)";

        /// <summary>The sizes offered in the export dialog, common ones first.</summary>
        internal static readonly PaperSize[] All =
        {
            new() { Name = "A4",       WidthPt = 595.28, HeightPt = 841.89 },
            new() { Name = "Letter",   WidthPt = 612.00, HeightPt = 792.00 },
            new() { Name = "Legal",    WidthPt = 612.00, HeightPt = 1008.00 },
            new() { Name = "A3",       WidthPt = 841.89, HeightPt = 1190.55 },
            new() { Name = "A5",       WidthPt = 419.53, HeightPt = 595.28 },
            new() { Name = "Tabloid",  WidthPt = 792.00, HeightPt = 1224.00 },
        };
    }

    internal enum PdfPageMode
    {
        /// <summary>Scale to the page width and slice the height across pages.</summary>
        SlicePages,
        /// <summary>One page exactly as tall as the image. No slicing, no scaling.</summary>
        SinglePage,

        /// <summary>
        /// The whole image shrunk to fit one page of the chosen paper, and placed on it.
        ///
        /// The gap the other two left: SlicePages keeps the image readable and spends
        /// pages to do it, SinglePage keeps one page and lets the paper size follow the
        /// image. Neither answers "put this on an A4 and let me say where" — which is what
        /// you want for a screenshot going into a report or an email attachment.
        /// </summary>
        FitOnePage
    }

    /// <summary>
    /// Where the image sits when it is smaller than the space available.
    ///
    /// One enum for both axes: horizontally Start is left and End is right, vertically
    /// Start is the top and End the bottom. Naming them Left/Right/Top/Bottom would mean
    /// two enums that can be passed to the wrong axis, and the compiler would not care.
    /// </summary>
    internal enum PdfAlign
    {
        Start,
        Centre,
        End
    }

    internal sealed class PdfExportOptions
    {
        internal PaperSize Paper { get; set; } = PaperSize.All[0];
        internal bool Landscape { get; set; }
        internal PdfPageMode Mode { get; set; } = PdfPageMode.SlicePages;

        /// <summary>Margin on all four sides, in millimetres.</summary>
        internal double MarginMm { get; set; } = 10;

        /// <summary>
        /// Rows repeated at the top of the next page. A slice that lands mid-sentence is
        /// unreadable across the break, and a small overlap makes the join obvious instead
        /// of losing it.
        /// </summary>
        internal int OverlapPx { get; set; } = 24;

        /// <summary>
        /// How big the image is drawn, as a percentage of "exactly the printable width".
        ///
        /// 100 means one page holds the full width of the image, which is the sensible
        /// default and the only thing the first version could do. Below 100 the image is
        /// smaller, so more of it fits on each page and the whole thing takes fewer,
        /// denser pages. Above 100 it is larger and more readable, and once it is wider
        /// than the page the export starts producing columns as well as rows.
        /// </summary>
        internal double ZoomPercent { get; set; } = 100;

        internal double Zoom => Math.Clamp(ZoomPercent, 10, 800) / 100.0;

        /// <summary>
        /// Where the image sits across the page when there is room to spare.
        ///
        /// Applies in every mode that can leave slack: <see cref="PdfPageMode.FitOnePage"/>
        /// always, and <see cref="PdfPageMode.SlicePages"/> when a single column is
        /// narrower than the printable area. Tiled columns ignore it — a partial last
        /// column drifting to the middle of the page would break the reading flow the
        /// tiling exists to keep.
        /// </summary>
        internal PdfAlign HorizontalAlign { get; set; } = PdfAlign.Centre;

        /// <summary>
        /// Where the image sits down the page.
        ///
        /// Honoured in <see cref="PdfPageMode.FitOnePage"/>. Sliced pages stay anchored to
        /// the top whatever this says: a slice floated to the middle of its page would put
        /// a gap in the middle of a sentence that continues on the next one.
        /// </summary>
        internal PdfAlign VerticalAlign { get; set; } = PdfAlign.Centre;

        /// <summary>JPEG rather than lossless Flate. Much smaller, softer on text.</summary>
        internal bool UseJpeg { get; set; }
        internal int JpegQuality { get; set; } = 88;

        internal double MarginPt => MarginMm / 25.4 * 72.0;
        internal double PageWidthPt => Landscape ? Paper.HeightPt : Paper.WidthPt;
        internal double PageHeightPt => Landscape ? Paper.WidthPt : Paper.HeightPt;
        internal double ContentWidthPt => Math.Max(1, PageWidthPt - 2 * MarginPt);
        internal double ContentHeightPt => Math.Max(1, PageHeightPt - 2 * MarginPt);
    }

    /// <summary>How a given image will be cut up, so the dialog can say so before writing.</summary>
    internal sealed class PdfLayout
    {
        internal int PageCount { get; init; }
        internal int RowsPerPage { get; init; }
        internal int StepPx { get; init; }
        internal double Scale { get; init; }

        /// <summary>Pixels of image width on one page, and how far each column advances.</summary>
        internal int ColsPerPage { get; init; }
        internal int ColStepPx { get; init; }

        /// <summary>Grid size. Columns is 1 unless the zoom pushes the image past the page width.</summary>
        internal int Columns { get; init; } = 1;
        internal int Rows { get; init; } = 1;
    }

    /// <summary>
    /// Everything about one page: which part of the image it shows and where that lands.
    ///
    /// This exists so the preview and the writer cannot disagree. A preview computed from
    /// its own copy of the arithmetic is worse than no preview at all — it looks
    /// authoritative right up until the PDF comes out different.
    /// </summary>
    internal sealed class PdfPagePlan
    {
        internal int Index { get; init; }
        internal int Column { get; init; }
        internal int Row { get; init; }

        /// <summary>The region of the source image drawn on this page, in pixels.</summary>
        internal System.Windows.Int32Rect Source { get; init; }

        internal double PageWidthPt { get; init; }
        internal double PageHeightPt { get; init; }

        /// <summary>Where the image sits, in PDF points, origin at the bottom-left.</summary>
        internal double DrawXPt { get; init; }
        internal double DrawYPt { get; init; }
        internal double DrawWidthPt { get; init; }
        internal double DrawHeightPt { get; init; }
    }

    /// <summary>
    /// Writes a PDF by hand.
    ///
    /// .NET has no PDF writer, and every library that does the job is a dependency this
    /// app has so far managed without — the same reasoning that produced the hand-written
    /// ICO container in <see cref="IconEncoder"/>. A PDF that contains nothing but images
    /// needs a very small subset of the format: a catalogue, a page tree, one image
    /// XObject and one content stream per page, and a cross-reference table. That is
    /// perfectly writable in a few hundred lines and produces files every reader opens.
    ///
    /// Images are stored either Flate-compressed 24-bit RGB (lossless, the default,
    /// because screenshots are mostly text and JPEG makes text mushy) or as embedded JPEG
    /// via DCTDecode, which is simply the encoder's own bytes handed to the PDF unaltered.
    ///
    /// Memory matters here: a 20000-pixel-tall capture must never be materialised twice.
    /// Each page slice is copied out of the source a row-band at a time into one reused
    /// buffer, compressed, and written straight to the output stream.
    /// </summary>
    /// <summary>
    /// One finished page on its way into a PDF: the picture, and how big that picture is
    /// meant to be on paper.
    ///
    /// The size is carried per page rather than derived from a single DPI, because deriving
    /// it was wrong: a fixed render width plus a fixed DPI gives every page in the document
    /// the same physical width, so a Letter page comes out A4-width and a landscape page in
    /// a portrait document is squeezed to portrait width. The page knows its own size; the
    /// writer should be told it, not made to guess.
    /// </summary>
    internal sealed class PdfOutputPage
    {
        public required BitmapSource Image { get; init; }

        /// <summary>Page width in points. 0 falls back to the writer's nominal DPI.</summary>
        public double WidthPt { get; init; }

        public double HeightPt { get; init; }

        /// <summary>Drawn rectangle on the page in points. 0 width/height draws across the full page.</summary>
        public double DrawXPt { get; init; }
        public double DrawYPt { get; init; }
        public double DrawWidthPt { get; init; }
        public double DrawHeightPt { get; init; }
    }

    internal static class PdfExporter
    {
        /// <summary>Works out the page split without doing any of the work.</summary>
        internal static PdfLayout PlanLayout(int imageWidth, int imageHeight, PdfExportOptions o)
        {
            if (imageWidth <= 0 || imageHeight <= 0)
                return new PdfLayout { PageCount = 0, RowsPerPage = 0, StepPx = 0, Scale = 1 };

            if (o.Mode == PdfPageMode.SinglePage)
                return new PdfLayout
                {
                    PageCount = 1,
                    RowsPerPage = imageHeight,
                    StepPx = imageHeight,
                    Scale = 1
                };

            if (o.Mode == PdfPageMode.FitOnePage)
                return new PdfLayout
                {
                    PageCount = 1,
                    RowsPerPage = imageHeight,
                    StepPx = imageHeight,
                    ColsPerPage = imageWidth,
                    ColStepPx = imageWidth,
                    Rows = 1,
                    Columns = 1,
                    Scale = FitScale(imageWidth, imageHeight, o)
                };

            // Points of page per pixel of image. At 100% zoom the image's full width is
            // exactly the printable width, which is where the first version stopped.
            double scale = o.ContentWidthPt / imageWidth * o.Zoom;

            // How much of the image fits on one page, in each direction.
            int rowsPerPage = Math.Max(1, (int)Math.Floor(o.ContentHeightPt / scale));
            int colsPerPage = Math.Max(1, (int)Math.Floor(o.ContentWidthPt / scale));

            // Overlap must never eat a whole page, or the step falls to zero and the
            // export never terminates.
            int rowOverlap = Math.Clamp(o.OverlapPx, 0, Math.Max(0, rowsPerPage - 1));
            int colOverlap = Math.Clamp(o.OverlapPx, 0, Math.Max(0, colsPerPage - 1));

            int rowStep = Math.Max(1, rowsPerPage - rowOverlap);
            int colStep = Math.Max(1, colsPerPage - colOverlap);

            int rows = rowsPerPage >= imageHeight
                ? 1
                : Math.Max(1, (int)Math.Ceiling((double)(imageHeight - rowOverlap) / rowStep));

            int columns = colsPerPage >= imageWidth
                ? 1
                : Math.Max(1, (int)Math.Ceiling((double)(imageWidth - colOverlap) / colStep));

            return new PdfLayout
            {
                PageCount = Math.Max(1, rows * columns),
                RowsPerPage = Math.Min(rowsPerPage, imageHeight),
                StepPx = rowStep,
                ColsPerPage = Math.Min(colsPerPage, imageWidth),
                ColStepPx = colStep,
                Rows = rows,
                Columns = columns,
                Scale = scale
            };
        }

        /// <summary>
        /// Points per pixel that make the whole image fit inside the printable area.
        ///
        /// Zoom shrinks it further — 100% means "as large as it will go", so the slider
        /// keeps meaning something in this mode instead of being dead. It is clamped
        /// rather than allowed past the fit, because a zoom that overflowed would push the
        /// image off a page whose whole purpose is to hold all of it.
        /// </summary>
        private static double FitScale(int imageWidth, int imageHeight, PdfExportOptions o)
        {
            double fit = Math.Min(o.ContentWidthPt / imageWidth,
                                  o.ContentHeightPt / imageHeight);
            return fit * Math.Min(1.0, o.Zoom);
        }

        /// <summary>
        /// Turns an alignment into an offset within the slack available.
        ///
        /// Slack is clamped at zero so an image larger than its box is pinned to the start
        /// rather than pulled off the opposite edge by a negative offset.
        /// </summary>
        private static double Offset(PdfAlign align, double available, double used)
        {
            double slack = Math.Max(0, available - used);
            return align switch
            {
                PdfAlign.Centre => slack / 2,
                PdfAlign.End    => slack,
                _               => 0
            };
        }

        /// <summary>
        /// Describes one page. Pages run left-to-right then top-to-bottom, so a wide
        /// image reads the way a printed spread does.
        /// </summary>
        internal static PdfPagePlan? PlanPage(int imageWidth, int imageHeight,
                                              PdfExportOptions o, PdfLayout layout, int index)
        {
            if (index < 0 || index >= layout.PageCount) return null;
            if (imageWidth <= 0 || imageHeight <= 0) return null;

            if (o.Mode == PdfPageMode.SinglePage)
            {
                // The page is the image, at 96 dpi expressed in points.
                double dw = imageWidth * 72.0 / 96.0;
                double dh = imageHeight * 72.0 / 96.0;
                return new PdfPagePlan
                {
                    Index = 0,
                    Column = 0,
                    Row = 0,
                    Source = new System.Windows.Int32Rect(0, 0, imageWidth, imageHeight),
                    PageWidthPt = dw + 2 * o.MarginPt,
                    PageHeightPt = dh + 2 * o.MarginPt,
                    DrawXPt = o.MarginPt,
                    DrawYPt = o.MarginPt,
                    DrawWidthPt = dw,
                    DrawHeightPt = dh
                };
            }

            if (o.Mode == PdfPageMode.FitOnePage)
            {
                double fw = imageWidth * layout.Scale;
                double fh = imageHeight * layout.Scale;

                // Vertical alignment is expressed against the *top* of the printable area
                // and then flipped, because PDF measures from the bottom-left. Doing it the
                // other way round makes "Top" mean bottom, which is the kind of bug that
                // only shows up once someone prints it.
                double fromTop = Offset(o.VerticalAlign, o.ContentHeightPt, fh);

                return new PdfPagePlan
                {
                    Index = 0,
                    Column = 0,
                    Row = 0,
                    Source = new System.Windows.Int32Rect(0, 0, imageWidth, imageHeight),
                    PageWidthPt = o.PageWidthPt,
                    PageHeightPt = o.PageHeightPt,
                    DrawXPt = o.MarginPt + Offset(o.HorizontalAlign, o.ContentWidthPt, fw),
                    DrawYPt = o.PageHeightPt - o.MarginPt - fromTop - fh,
                    DrawWidthPt = fw,
                    DrawHeightPt = fh
                };
            }

            int col = index % Math.Max(1, layout.Columns);
            int row = index / Math.Max(1, layout.Columns);

            int x0 = col * layout.ColStepPx;
            int y0 = row * layout.StepPx;
            if (x0 >= imageWidth || y0 >= imageHeight) return null;

            int sw = Math.Min(layout.ColsPerPage, imageWidth - x0);
            int sh = Math.Min(layout.RowsPerPage, imageHeight - y0);
            if (sw <= 0 || sh <= 0) return null;

            double pageW = o.PageWidthPt, pageH = o.PageHeightPt;
            double drawW = sw * layout.Scale;
            double drawH = sh * layout.Scale;

            // A single column narrower than the printable area follows the chosen
            // horizontal alignment — that is the zoomed-out case, and a strip hugging the
            // left margin looks like a mistake unless it was asked for. Tiled columns are
            // left-aligned regardless, so a partial last column continues the reading flow
            // rather than drifting into the middle of the page.
            //
            // Vertically, a short last row is pushed up under the top margin rather than
            // left floating at the bottom. PDF's origin is the bottom-left corner, which
            // is why that placement is written as a subtraction.
            double drawX = o.MarginPt + (layout.Columns <= 1
                ? Offset(o.HorizontalAlign, o.ContentWidthPt, drawW)
                : 0);
            double drawY = pageH - o.MarginPt - drawH;

            return new PdfPagePlan
            {
                Index = index,
                Column = col,
                Row = row,
                Source = new System.Windows.Int32Rect(x0, y0, sw, sh),
                PageWidthPt = pageW,
                PageHeightPt = pageH,
                DrawXPt = drawX,
                DrawYPt = drawY,
                DrawWidthPt = drawW,
                DrawHeightPt = drawH
            };
        }

        /// <summary>
        /// Writes <paramref name="image"/> to <paramref name="path"/>. Reports progress as
        /// (pageDone, pageTotal) so a long export can show something.
        /// </summary>
        internal static void Save(BitmapSource image, string path, PdfExportOptions o,
                                  Action<int, int>? progress = null,
                                  CancellationToken ct = default)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));

            // Straight alpha, so partially transparent pixels can be composited onto white
            // below. PDF has no alpha channel without a soft mask, and a printed page is
            // white anyway.
            var source = image.Format == PixelFormats.Bgra32
                ? image
                : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            if (source.CanFreeze && !source.IsFrozen) source.Freeze();

            int w = source.PixelWidth, h = source.PixelHeight;
            var layout = PlanLayout(w, h, o);

            // Temp file then move, matching how the editor writes images: a failure
            // part-way through must not leave a half-written PDF where the user expects
            // a finished one.
            string temp = path + ".tmp";

            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
            {
                var pdf = new PdfBuilder(fs);
                pdf.WriteHeader();

                string filter = o.UseJpeg ? "/DCTDecode" : "/FlateDecode";

                for (int page = 0; page < layout.PageCount; page++)
                {
                    ct.ThrowIfCancellationRequested();

                    // Exactly the same planner the preview draws from, so what the user
                    // approved is what gets written.
                    var plan = PdfExporter.PlanPage(w, h, o, layout, page);
                    if (plan == null) continue;

                    byte[] payload = BuildPayload(source, plan.Source,
                                                  o.UseJpeg, o.JpegQuality);

                    pdf.AddImagePage(payload, filter,
                                     plan.Source.Width, plan.Source.Height,
                                     plan.PageWidthPt, plan.PageHeightPt,
                                     plan.DrawXPt, plan.DrawYPt,
                                     plan.DrawWidthPt, plan.DrawHeightPt);

                    progress?.Invoke(page + 1, layout.PageCount);
                }

                pdf.Finish();
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);

            MemoryGuard.RequestTrim();
        }

        /// <summary>
        /// Writes a document of already-composed page images — one image, one page.
        ///
        /// The other <see cref="Save"/> takes a single tall picture and slices it across
        /// pages; this takes finished pages. That is what the PDF editor needs: each page
        /// is a rendered PDF page with the user's markup burned into it, and the page must
        /// come out the size and shape it went in rather than being fitted to A4.
        ///
        /// Each page carries its own size in points, so a page that was A4 in the original
        /// is A4 in the output and a mixed-size document keeps its mixture. An earlier
        /// version derived that from one DPI and one render width, which silently gave every
        /// page in the document identical dimensions — this comment described the intent and
        /// the wiring did not deliver it.
        ///
        /// It shares <see cref="BuildPayload"/> and <c>PdfBuilder</c> with the slicing path
        /// deliberately — a second PDF writer is a second place for the container structure
        /// to be subtly wrong, and this one has been exercised since v2.7.
        /// </summary>
        internal static void SavePages(IReadOnlyList<PdfOutputPage> pages, string path,
                                       double nominalDpi, bool jpeg, int jpegQuality,
                                       Action<int, int>? progress = null,
                                       CancellationToken ct = default)
        {
            if (pages == null || pages.Count == 0)
                throw new ArgumentException("A PDF needs at least one page.", nameof(pages));

            if (nominalDpi < 36) nominalDpi = 36;   // below this the arithmetic stops being sensible

            string temp = path + ".tmp";

            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
            {
                var pdf = new PdfBuilder(fs);
                pdf.WriteHeader();

                string filter = jpeg ? "/DCTDecode" : "/FlateDecode";

                for (int i = 0; i < pages.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var entry = pages[i];
                    if (entry?.Image == null) continue;

                    // Straight alpha so partly transparent pixels composite onto white;
                    // PDF has no alpha without a soft mask, and a page is white anyway.
                    var source = entry.Image.Format == PixelFormats.Bgra32
                        ? entry.Image
                        : new FormatConvertedBitmap(entry.Image, PixelFormats.Bgra32, null, 0);
                    if (source.CanFreeze && !source.IsFrozen) source.Freeze();

                    int w = source.PixelWidth, h = source.PixelHeight;
                    if (w <= 0 || h <= 0) continue;

                    // The page's own size where it is known. 72 points to the inch is the
                    // definition of the unit, so the fallback is only a guess about what
                    // resolution the caller rendered at — never about the paper.
                    double pageW = entry.WidthPt > 0 ? entry.WidthPt : w * 72.0 / nominalDpi;
                    double pageH = entry.HeightPt > 0 ? entry.HeightPt : h * 72.0 / nominalDpi;

                    double drawX = entry.DrawWidthPt > 0 ? entry.DrawXPt : 0;
                    double drawY = entry.DrawHeightPt > 0 ? entry.DrawYPt : 0;
                    double drawW = entry.DrawWidthPt > 0 ? entry.DrawWidthPt : pageW;
                    double drawH = entry.DrawHeightPt > 0 ? entry.DrawHeightPt : pageH;

                    byte[] payload = BuildPayload(source,
                        new System.Windows.Int32Rect(0, 0, w, h), jpeg, jpegQuality);

                    pdf.AddImagePage(payload, filter, w, h, pageW, pageH,
                                     drawX, drawY, drawW, drawH);

                    progress?.Invoke(i + 1, pages.Count);
                }

                pdf.Finish();
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);

            MemoryGuard.RequestTrim();
        }

        /// <summary>How many rows are converted at a time. Keeps the scratch buffers small.</summary>
        private const int BandRows = 128;

        /// <summary>
        /// Produces the bytes that go into the image XObject.
        ///
        /// The lossless path never materialises the whole slice: rows are pulled out of
        /// the source a band at a time, converted to RGB, and pushed straight into the
        /// compressor. For a single-page export of a 20000-pixel capture that is the
        /// difference between two 100 MB scratch buffers and two of about a megabyte.
        /// JPEG cannot work that way — the encoder needs a complete image — so that path
        /// still builds the slice in full, which is part of why lossless is the default.
        /// </summary>
        private static byte[] BuildPayload(BitmapSource source, System.Windows.Int32Rect rect,
                                           bool jpeg, int quality)
        {
            int w = rect.Width, rows = rect.Height;

            if (jpeg)
            {
                var whole = new byte[checked(w * 3 * rows)];
                int written = 0;
                ForEachBand(source, rect, (band, count) =>
                {
                    Buffer.BlockCopy(band, 0, whole, written, count);
                    written += count;
                });
                return EncodeJpeg(whole, w, rows, quality);
            }

            using var ms = new MemoryStream(64 * 1024);
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                var stream = z;
                ForEachBand(source, rect, (band, count) => stream.Write(band, 0, count));
            }
            return ms.ToArray();
        }

        /// <summary>
        /// Walks the slice in bands, handing each one to <paramref name="consume"/> as
        /// packed 24-bit RGB with any transparency composited onto white.
        /// </summary>
        private static void ForEachBand(BitmapSource source, System.Windows.Int32Rect rect,
                                        Action<byte[], int> consume)
        {
            int x0 = rect.X, y0 = rect.Y, w = rect.Width, rows = rect.Height;

            int band = Math.Min(BandRows, Math.Max(1, rows));
            int srcStride = w * 4;
            var bgra = new byte[checked(srcStride * band)];
            var rgb = new byte[checked(w * 3 * band)];

            for (int y = 0; y < rows; y += band)
            {
                int n = Math.Min(band, rows - y);
                source.CopyPixels(new Int32Rect(x0, y0 + y, w, n), bgra, srcStride, 0);

                int si = 0, di = 0;
                for (int row = 0; row < n; row++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        byte b = bgra[si], g = bgra[si + 1], r = bgra[si + 2], a = bgra[si + 3];
                        si += 4;

                        if (a == 255)
                        {
                            rgb[di++] = r; rgb[di++] = g; rgb[di++] = b;
                        }
                        else
                        {
                            // Straight (non-premultiplied) alpha composited over white.
                            int inv = 255 - a;
                            rgb[di++] = (byte)((r * a + 255 * inv) / 255);
                            rgb[di++] = (byte)((g * a + 255 * inv) / 255);
                            rgb[di++] = (byte)((b * a + 255 * inv) / 255);
                        }
                    }
                }

                consume(rgb, di);
            }
        }

        private static byte[] EncodeJpeg(byte[] rgb, int w, int h, int quality)
        {
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Rgb24, null, rgb, w * 3);
            bmp.Freeze();

            var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
            encoder.Frames.Add(BitmapFrame.Create(bmp));

            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        // ── the container ─────────────────────────────────────────────────────

        /// <summary>
        /// Assembles the PDF object by object, tracking byte offsets as it goes because
        /// the cross-reference table at the end has to name the exact position of every
        /// object in the file.
        /// </summary>
        private sealed class PdfBuilder
        {
            private readonly Stream _out;
            private readonly List<long> _offsets = new();   // by object number - 1
            private readonly List<int> _pageObjects = new();
            private long _pos;

            // Object 1 is the catalogue and object 2 the page tree. Both are only written
            // at the end (the page tree needs the full list of children), so numbering
            // starts at 3 for everything else.
            private int _next = 3;

            internal PdfBuilder(Stream output)
            {
                _out = output;
                _offsets.Add(0);   // placeholder for object 1
                _offsets.Add(0);   // placeholder for object 2
            }

            internal void WriteHeader()
            {
                // The binary comment on line 2 is what tells transfer tools the file is
                // not text and must not have its line endings rewritten.
                WriteRaw(Encoding.ASCII.GetBytes("%PDF-1.4\n"));
                WriteRaw(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
            }

            internal void AddImagePage(byte[] payload, string filter, int pxW, int pxH,
                                       double pageW, double pageH,
                                       double x, double y, double drawW, double drawH)
            {
                int imgObj = BeginObject();
                WriteAscii($"<< /Type /XObject /Subtype /Image /Width {pxW} /Height {pxH} " +
                           $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter {filter} " +
                           $"/Length {payload.Length} >>\nstream\n");
                WriteRaw(payload);
                WriteAscii("\nendstream\n");
                EndObject();

                // "cm" sets the transform, so the unit square the image is drawn into
                // becomes a rectangle of the size and position we want.
                string content =
                    "q\n" +
                    $"{Num(drawW)} 0 0 {Num(drawH)} {Num(x)} {Num(y)} cm\n" +
                    "/Im0 Do\n" +
                    "Q\n";
                byte[] contentBytes = Encoding.ASCII.GetBytes(content);

                int contentObj = BeginObject();
                WriteAscii($"<< /Length {contentBytes.Length} >>\nstream\n");
                WriteRaw(contentBytes);
                WriteAscii("\nendstream\n");
                EndObject();

                int pageObj = BeginObject();
                WriteAscii($"<< /Type /Page /Parent 2 0 R " +
                           $"/MediaBox [0 0 {Num(pageW)} {Num(pageH)}] " +
                           $"/Resources << /XObject << /Im0 {imgObj} 0 R >> /ProcSet [/PDF /ImageC] >> " +
                           $"/Contents {contentObj} 0 R >>\n");
                EndObject();

                _pageObjects.Add(pageObj);
            }

            internal void Finish()
            {
                // Page tree (object 2) and catalogue (object 1), now that every page is
                // known. Their offsets go into the slots reserved in the constructor.
                _offsets[1] = _pos;
                WriteAscii("2 0 obj\n<< /Type /Pages /Count " + _pageObjects.Count + " /Kids [");
                foreach (int p in _pageObjects) WriteAscii(p + " 0 R ");
                WriteAscii("] >>\nendobj\n");

                _offsets[0] = _pos;
                WriteAscii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

                long xref = _pos;
                int count = _offsets.Count + 1;   // +1 for the mandatory free entry

                WriteAscii($"xref\n0 {count}\n");
                WriteAscii("0000000000 65535 f \n");
                foreach (long off in _offsets)
                    WriteAscii(off.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");

                WriteAscii($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
                _out.Flush();
            }

            private int BeginObject()
            {
                int id = _next++;
                while (_offsets.Count < id) _offsets.Add(0);
                _offsets[id - 1] = _pos;
                WriteAscii($"{id} 0 obj\n");
                return id;
            }

            private void EndObject() => WriteAscii("endobj\n");

            private void WriteAscii(string s) => WriteRaw(Encoding.ASCII.GetBytes(s));

            private void WriteRaw(byte[] bytes)
            {
                _out.Write(bytes, 0, bytes.Length);
                _pos += bytes.Length;
            }

            /// <summary>PDF numbers are always invariant and never in exponent form.</summary>
            private static string Num(double v) =>
                Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

        }
    }
}
