using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Renders pages out of a PDF file, for the gallery's PDF tab and its viewer.
    ///
    /// Uses <c>Windows.Data.Pdf</c> — the renderer that ships with Windows and backs Edge's
    /// PDF view. Same reasoning as the OCR engine: no NuGet package, no native binary to
    /// carry, nothing leaving the machine, and no hand-written COM interop to get wrong.
    /// The project already targets a Windows 10 TFM for the OCR projections, so this costs
    /// nothing to reach.
    ///
    /// Note this app also *writes* PDFs, with a hand-rolled container in
    /// <see cref="PdfExporter"/>. That stays hand-rolled: writing needs a few hundred lines
    /// of well-specified structure, while reading one means implementing a parser, a
    /// decompressor and a font stack. Writing what you fully control and reading through
    /// the platform is the right side of that line to be on.
    ///
    /// Failures return null rather than throwing — a gallery folder can hold a PDF that is
    /// encrypted, truncated, or not a PDF at all despite the extension, and none of those
    /// is a reason to take down the page listing it. They are recorded in
    /// <see cref="LastError"/> rather than discarded; see the note there.
    /// </summary>
    internal static class PdfPreviewer
    {
        internal sealed class PdfInfo
        {
            public required uint PageCount { get; init; }
            public BitmapSource? FirstPage { get; init; }
        }

        /// <summary>
        /// The last thing that went wrong, for the viewer to show.
        ///
        /// Rendering was previously wrapped in a bare <c>catch { return null; }</c>, which
        /// is how the first version of this shipped broken and *silent*: page counts
        /// appeared, thumbnails did not, and nothing anywhere said why. A swallowed
        /// exception in code that cannot be debugged from where it was written is a dead
        /// end for whoever has to fix it.
        /// </summary>
        internal static string? LastError { get; private set; }

        /// <summary>
        /// Opens a document. <c>PdfDocument</c> is not disposable — the viewer simply holds
        /// the reference while it browses rather than reopening the file per page.
        /// </summary>
        internal static async Task<PdfDocument?> OpenAsync(string path)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                return await PdfDocument.LoadFromFileAsync(file);
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// A page's own size, in PDF points.
        ///
        /// <c>PdfPage.Size</c> is documented only as "the size of the page", which leaves the
        /// unit ambiguous — and the difference between points (1/72 in) and Windows DIPs
        /// (1/96 in) is a 33% error in the exported page size, so it is worth being explicit
        /// about why this treats it as points: rendering a page with no
        /// <c>PdfPageRenderOptions</c> produces a bitmap of exactly <c>PdfPage.Size</c>
        /// pixels, and an A4 page rendered that way comes out 595×842 — which is A4 in
        /// points. **That is a checkable claim**: if an A4 page ever reports 794×1123 here,
        /// the unit is DIPs and every size below needs a 0.75 factor.
        ///
        /// Returns null rather than throwing; a page whose size cannot be read still renders,
        /// and the caller falls back to a nominal size.
        /// </summary>
        internal static (double WidthPt, double HeightPt)? PageSize(PdfDocument document, uint index)
        {
            if (document == null || index >= document.PageCount) return null;

            try
            {
                using var page = document.GetPage(index);
                var size = page.Size;

                if (size.Width <= 0 || size.Height <= 0) return null;
                return (size.Width, size.Height);
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        /// <summary>Page count plus a rendered first page, for a gallery card.</summary>
        internal static async Task<PdfInfo?> ProbeAsync(string path, uint width,
                                                        CancellationToken token = default)
        {
            var document = await OpenAsync(path);
            if (document == null) return null;

            try
            {
                token.ThrowIfCancellationRequested();

                if (document.PageCount == 0)
                    return new PdfInfo { PageCount = 0, FirstPage = null };

                var frame = await RenderAsync(document, 0, width, token);
                return new PdfInfo { PageCount = document.PageCount, FirstPage = frame };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// Renders one page to a bitmap of the given width, height following the page's
        /// own aspect ratio.
        ///
        /// **Goes through a temporary file rather than an in-memory stream.** The first
        /// version rendered to an <c>InMemoryRandomAccessStream</c> and copied the bytes
        /// out with a <c>DataReader</c>, to avoid the <c>AsStreamForRead</c> interop shim.
        /// It produced nothing: page counts came back correctly, so the document opened and
        /// the projection resolved, but every render returned null.
        ///
        /// A file path is the pattern that already works in this codebase — OcrService
        /// routes everything through one and says why. The bridging was the clever option;
        /// this is the one with a track record here. <c>BitmapCacheOption.OnLoad</c> reads
        /// the whole file during EndInit, so deleting it immediately afterwards is safe.
        /// </summary>
        internal static async Task<BitmapSource?> RenderAsync(PdfDocument document, uint index,
                                                              uint width,
                                                              CancellationToken token = default)
        {
            if (document == null || index >= document.PageCount) return null;

            string temp = Path.Combine(Path.GetTempPath(),
                                       $"assnip_pdf_{Guid.NewGuid():N}.png");
            try
            {
                token.ThrowIfCancellationRequested();

                // The file has to exist before WinRT will hand back a StorageFile for it.
                using (File.Create(temp)) { }

                var file = await StorageFile.GetFileFromPathAsync(temp);

                using (var target = await file.OpenAsync(FileAccessMode.ReadWrite))
                {
                    using var page = document.GetPage(index);

                    // Sized render first, natural size as a fallback. If the options object
                    // is what the platform objects to — one of the suspects for the silent
                    // failure above — a page at its own size still beats no page at all.
                    try
                    {
                        await page.RenderToStreamAsync(target,
                            new PdfPageRenderOptions { DestinationWidth = Math.Max(1, width) });
                    }
                    catch (Exception ex)
                    {
                        LastError = $"sized render failed ({ex.GetType().Name}: {ex.Message})";
                        target.Size = 0;
                        await page.RenderToStreamAsync(target);
                    }

                    await target.FlushAsync();
                }

                token.ThrowIfCancellationRequested();

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.UriSource = new Uri(temp);
                bitmap.EndInit();
                bitmap.Freeze();

                LastError = null;
                return bitmap;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }
}
