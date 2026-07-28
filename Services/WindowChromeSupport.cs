using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Support for a window that draws its own title bar.
    ///
    /// The app uses <c>WindowChrome</c> rather than <c>WindowStyle="None"</c>. That
    /// distinction matters: WindowStyle="None" throws away the whole non-client frame, and
    /// with it Aero Snap, snap layouts on the maximize button, drag-to-edge tiling,
    /// double-click-to-maximize, the resize grips and the window shadow — all of which
    /// then have to be reimplemented badly. WindowChrome keeps the real frame and simply
    /// extends the client area over it, so every one of those behaviours still comes from
    /// Windows.
    ///
    /// One thing WindowChrome doesn't fix on its own is maximizing. A window whose frame
    /// has been extended maximizes to the full monitor rectangle, sliding its edges under
    /// the taskbar and off the screen. Windows asks the window how large it may become via
    /// WM_GETMINMAXINFO, so answering that message with the monitor's *work area* is what
    /// makes maximize land correctly — including on a secondary display, and including
    /// when the taskbar is on the side.
    /// </summary>
    internal static class WindowChromeSupport
    {
        private const int WM_GETMINMAXINFO = 0x0024;

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public Win32.POINT ptReserved;
            public Win32.POINT ptMaxSize;
            public Win32.POINT ptMaxPosition;
            public Win32.POINT ptMinTrackSize;
            public Win32.POINT ptMaxTrackSize;
        }

        /// <summary>
        /// Hooks the window so it maximizes to the work area of whichever monitor it's on.
        /// Safe to call before the handle exists — it waits.
        /// </summary>
        internal static void Attach(Window window)
        {
            if (window == null) return;

            void Hook(object? sender, EventArgs e)
            {
                window.SourceInitialized -= Hook;
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;
                HwndSource.FromHwnd(handle)?.AddHook(
                    (IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                        WndProc(window, hwnd, msg, l, ref handled));
            }

            if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Hook(window, EventArgs.Empty);
            else window.SourceInitialized += Hook;
        }

        private static IntPtr WndProc(Window window, IntPtr hwnd, int msg, IntPtr lParam,
                                      ref bool handled)
        {
            if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

            try
            {
                var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                var monitor = DisplayInfo.FromWindow(hwnd);

                // Everything here is in physical pixels relative to the monitor's own
                // origin, which is why the work area is offset by the monitor bounds
                // rather than used as absolute desktop coordinates.
                info.ptMaxPosition.X = monitor.WorkArea.Left - monitor.Bounds.Left;
                info.ptMaxPosition.Y = monitor.WorkArea.Top - monitor.Bounds.Top;
                info.ptMaxSize.X = monitor.WorkArea.Width;
                info.ptMaxSize.Y = monitor.WorkArea.Height;

                // MinWidth/MinHeight are in WPF units; the message wants pixels, and on a
                // scaled display those aren't the same number.
                double scale = monitor.Scale <= 0 ? 1.0 : monitor.Scale;
                if (window.MinWidth  > 0) info.ptMinTrackSize.X = (int)(window.MinWidth  * scale);
                if (window.MinHeight > 0) info.ptMinTrackSize.Y = (int)(window.MinHeight * scale);

                Marshal.StructureToPtr(info, lParam, true);
                handled = true;
            }
            catch
            {
                // Leave Windows to its default sizing rather than breaking the window.
            }

            return IntPtr.Zero;
        }
    }
}
