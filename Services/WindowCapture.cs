using System;
using System.Drawing;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Captures one window's own contents, rather than the patch of screen it happens to
    /// be sitting on.
    ///
    /// **What this fixes.** The recorder BitBlts the desktop, so anything in front of the
    /// target is recorded too, and a window that is moved walks out of frame. Asking the
    /// window to draw itself gets its content even when something is on top of it, and the
    /// frame follows it around the desktop.
    ///
    /// **What it does not fix, and this matters.** <c>PrintWindow</c> asks a window to
    /// render through its normal paint path. Content that never goes through that path —
    /// video decoded straight onto a GPU surface, hardware overlays, some accelerated
    /// canvases — comes back blank or black, with the rest of the window intact around it.
    /// That is a real limitation, not a bug to hunt: the pixels genuinely are not in the
    /// window's paint output. Capturing those needs Windows.Graphics.Capture, which pulls
    /// the frame off the compositor instead. See the note on <see cref="LooksBlank"/> for
    /// how this is detected and reported rather than silently shipped as a black video.
    ///
    /// **Minimised windows do not work at all**, and cannot be made to. A minimised window
    /// is not composited and its application usually stops rendering entirely, so there are
    /// no frames for any capture API to collect. <see cref="IsMinimised"/> exists so the
    /// caller can say so plainly.
    /// </summary>
    internal sealed class WindowCapture
    {
        private readonly IntPtr _hwnd;

        /// <summary>The size the video was started at. Frames never change size mid-file.</summary>
        private readonly Size _frameSize;

        internal WindowCapture(IntPtr hwnd, Size frameSize)
        {
            _hwnd = hwnd;
            _frameSize = frameSize;
        }

        internal bool IsAlive => _hwnd != IntPtr.Zero && Win32.IsWindow(_hwnd);

        internal bool IsMinimised => IsAlive && Win32.IsIconic(_hwnd);

        /// <summary>
        /// Draws the window into <paramref name="frame"/>, top-left aligned.
        ///
        /// The frame keeps the size the encoder was started with, because H.264 cannot
        /// change dimensions mid-stream. A window resized smaller mid-recording leaves the
        /// rest of the frame as it was cleared; resized larger is cropped. Both beat
        /// ending the take.
        /// </summary>
        internal bool Grab(Graphics g)
        {
            if (!IsAlive) return false;

            // A minimised window has no content to render. Saying so is the caller's job;
            // here it just means there is nothing to draw.
            if (Win32.IsIconic(_hwnd)) return false;

            IntPtr destDc = g.GetHdc();
            try
            {
                // PW_RENDERFULLCONTENT is what makes this work on modern windows. Without
                // it, PrintWindow predates DWM composition and returns blank for anything
                // that renders through the compositor, which today is nearly everything.
                bool ok = Win32.PrintWindow(_hwnd, destDc, Win32.PW_RENDERFULLCONTENT);

                // Some windows refuse PW_RENDERFULLCONTENT and answer the plain call. It
                // costs one retry and rescues a whole class of older applications.
                if (!ok) ok = Win32.PrintWindow(_hwnd, destDc, 0);

                return ok;
            }
            finally
            {
                g.ReleaseHdc(destDc);
            }
        }

        /// <summary>
        /// Copies whatever is on screen where the window is, as a fallback.
        ///
        /// Used when the window declines to render itself. It reintroduces the occlusion
        /// problem — anything in front is captured — but a recording of the visible window
        /// is worth more than a black rectangle, and this path only runs when the better
        /// one has already failed.
        /// </summary>
        internal bool GrabFromScreen(Graphics g)
        {
            if (!IsAlive) return false;

            var rect = WindowFinder.WindowRect(_hwnd);
            if (rect.Width <= 0 || rect.Height <= 0) return false;

            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return false;

            IntPtr destDc = g.GetHdc();
            try
            {
                return Win32.BitBlt(destDc, 0, 0,
                                    Math.Min(_frameSize.Width, rect.Width),
                                    Math.Min(_frameSize.Height, rect.Height),
                                    screenDc, rect.X, rect.Y, Win32.SRCCOPY);
            }
            finally
            {
                g.ReleaseHdc(destDc);
                Win32.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /// <summary>
        /// Whether a captured frame came back essentially empty.
        ///
        /// Checked **once, before recording starts** — never per frame. A window that
        /// renders nothing through PrintWindow will do so consistently, so one sample
        /// answers it, and scanning every frame at thirty a second to discover something
        /// already known would cost more than the capture itself.
        ///
        /// The point is to tell the user *before* they record ten minutes of black, which
        /// is the difference between a limitation and a wasted afternoon. Sampled on a
        /// coarse grid: a genuinely blank capture is uniformly blank, so a few hundred
        /// pixels settle it as well as a million would.
        /// </summary>
        internal static bool LooksBlank(Bitmap frame)
        {
            const int steps = 24;

            int stepX = Math.Max(1, frame.Width / steps);
            int stepY = Math.Max(1, frame.Height / steps);

            int? first = null;

            for (int y = 0; y < frame.Height; y += stepY)
            {
                for (int x = 0; x < frame.Width; x += stepX)
                {
                    int argb = frame.GetPixel(x, y).ToArgb();

                    // Any variation at all means the window drew something. Blank captures
                    // are one flat colour — usually black, sometimes the window background.
                    if (first == null) first = argb;
                    else if (argb != first) return false;
                }
            }

            return true;
        }
    }
}
