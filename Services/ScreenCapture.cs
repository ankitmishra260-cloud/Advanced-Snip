using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Grabs pixels off the screen. Everything here is in physical pixels — the app is
    /// Per-Monitor-DPI-V2 aware, so a rectangle means the same thing on every display
    /// regardless of its scale factor.
    /// </summary>
    internal static class ScreenCapture
    {
        /// <summary>Captures the whole virtual desktop. originX/Y are its top-left corner.</summary>
        internal static Bitmap CaptureVirtualScreen(out int originX, out int originY)
        {
            var vs = DisplayInfo.VirtualScreen;
            originX = vs.X;
            originY = vs.Y;
            // CAPTUREBLT pulls in layered windows (menus, tooltips, drop shadows) which a
            // user almost always wants included in a manual snip.
            return CaptureScreenRect(vs, includeLayered: true);
        }

        /// <summary>
        /// Captures an arbitrary screen rectangle.
        /// </summary>
        /// <param name="includeLayered">
        /// Adds CAPTUREBLT. Worth it for one-shot snips, but it can make the screen flicker
        /// so the scroll-capture loop leaves it off.
        /// </param>
        internal static Bitmap CaptureScreenRect(Rectangle r, bool includeLayered = false)
        {
            r = DisplayInfo.ClampToDesktop(r);
            int w = Math.Max(1, r.Width);
            int h = Math.Max(1, r.Height);

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return bmp;

            try
            {
                using var g = Graphics.FromImage(bmp);
                IntPtr destDc = g.GetHdc();
                try
                {
                    uint rop = Win32.SRCCOPY | (includeLayered ? Win32.CAPTUREBLT : 0u);
                    Win32.BitBlt(destDc, 0, 0, w, h, screenDc, r.X, r.Y, rop);
                }
                finally
                {
                    g.ReleaseHdc(destDc);
                }
            }
            finally
            {
                Win32.ReleaseDC(IntPtr.Zero, screenDc);
            }

            return bmp;
        }

        /// <summary>
        /// Renders a window through PrintWindow instead of reading the screen. Used only as
        /// a fallback: it works for windows that are covered up, but many GPU-composited
        /// apps (Chromium, Electron, anything on DirectComposition) hand back blank frames,
        /// which is why the scroll capture reads real screen pixels by default.
        /// </summary>
        internal static Bitmap? TryPrintWindow(IntPtr hwnd)
        {
            var rect = WindowFinder.WindowRect(hwnd);
            if (rect.Width <= 0 || rect.Height <= 0) return null;

            var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            try
            {
                using var g = Graphics.FromImage(bmp);
                IntPtr hdc = g.GetHdc();
                bool ok;
                try { ok = Win32.PrintWindow(hwnd, hdc, Win32.PW_RENDERFULLCONTENT); }
                finally { g.ReleaseHdc(hdc); }

                if (!ok) { bmp.Dispose(); return null; }
                return bmp;
            }
            catch
            {
                bmp.Dispose();
                return null;
            }
        }
    }
}
