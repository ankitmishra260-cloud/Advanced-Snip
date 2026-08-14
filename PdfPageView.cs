using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    /// <summary>
    /// One page in the continuous scroll: the rendered image, plus the layer the text
    /// selection and find highlights are drawn on.
    ///
    /// **Why a class per page rather than one shared surface.** The viewer used to show one
    /// page at a time, so there was exactly one bitmap, one word list and one set of
    /// highlight rectangles. Scrolling through a whole document means many pages alive at
    /// once, each at a different scroll position, each with its own OCR result that arrives
    /// whenever it arrives. Keeping that state next to the page it belongs to is what stops
    /// a result for page 12 painting itself over page 3.
    ///
    /// A page reserves its correct size from the moment the document opens, before anything
    /// is rendered, because the alternative is a scrollbar that grows as pages load and a
    /// view that jumps under the reader. The size comes from the PDF's own page dimensions,
    /// so it is right the first time rather than corrected later.
    /// </summary>
    internal sealed class PdfPageView : Border
    {
        /// <summary>Index into the document.</summary>
        internal int Index { get; }

        /// <summary>The page's natural size in PDF points, for aspect and for print size.</summary>
        internal double WidthPt { get; }

        internal double HeightPt { get; }

        /// <summary>The rendered bitmap, or null when this page is not currently resident.</summary>
        internal BitmapSource? Frame { get; private set; }

        /// <summary>OCR words for the resident render, or null if it has not been read.</summary>
        internal OcrLayout? Layout { get; set; }

        /// <summary>Set while a render or a read is in flight, so it is not started twice.</summary>
        internal bool Rendering { get; set; }

        internal bool Reading { get; set; }

        /// <summary>The pixel width the resident bitmap was rendered at; 0 when not resident.</summary>
        internal int RenderedWidth { get; private set; }

        /// <summary>DIPs per rendered pixel — how page-space maps to what is on screen.</summary>
        internal double TextScale { get; private set; } = 1;

        internal readonly Image Picture = new()
        {
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true
        };

        internal readonly Canvas Overlay = new()
        {
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.IBeam
        };

        private readonly TextBlock _placeholder = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))
        };

        internal PdfPageView(int index, double widthPt, double heightPt)
        {
            Index = index;
            WidthPt = widthPt > 0 ? widthPt : 612;
            HeightPt = heightPt > 0 ? heightPt : 792;

            Background = Brushes.White;
            HorizontalAlignment = HorizontalAlignment.Center;
            Margin = new Thickness(0, 0, 0, 18);
            SnapsToDevicePixels = true;

            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Opacity = 0.22,
                Color = Colors.Black
            };

            _placeholder.Text = $"Page {index + 1}";

            var grid = new Grid();
            grid.Children.Add(_placeholder);
            grid.Children.Add(Picture);
            grid.Children.Add(Overlay);
            Child = grid;
        }

        /// <summary>
        /// Recomputes DIPs-per-rendered-pixel after the page has been resized.
        ///
        /// The viewer owns Width and Height — it is the only thing that knows the zoom and
        /// the view rotation — so this deliberately does not set them. It exists because
        /// everything the text layer does is in rendered-pixel space, and this is the single
        /// number that converts that space to what is on screen.
        /// </summary>
        internal void RecomputeTextScale()
        {
            TextScale = Frame != null && RenderedWidth > 0
                ? Width / RenderedWidth
                : 1;
        }

        /// <summary>Attaches a freshly rendered bitmap.</summary>
        internal void Attach(BitmapSource frame)
        {
            Frame = frame;
            RenderedWidth = frame.PixelWidth;
            Picture.Source = frame;
            _placeholder.Visibility = Visibility.Collapsed;

            RecomputeTextScale();
        }

        /// <summary>
        /// Drops the bitmap and everything derived from it, keeping the reserved size.
        ///
        /// Called when a page scrolls far enough out of view. A hundred-page document at
        /// 150 DPI is several gigabytes of decoded bitmaps if nothing is ever released —
        /// this app has already been bitten once by exactly that (v2.6), and a PDF viewer
        /// that grows without bound while you scroll is the same bug wearing a hat.
        /// </summary>
        internal void Release()
        {
            Frame = null;
            RenderedWidth = 0;
            Layout = null;
            Picture.Source = null;
            Overlay.Children.Clear();
            _placeholder.Visibility = Visibility.Visible;
        }
    }
}
