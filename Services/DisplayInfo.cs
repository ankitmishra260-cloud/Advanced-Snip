using System;
using System.Collections.Generic;
using System.Drawing;

namespace AdvancedSnip.Services
{
    /// <summary>One physical display, in true physical pixels.</summary>
    internal sealed class MonitorEntry
    {
        public IntPtr Handle;
        public Rectangle Bounds;    // full monitor rect
        public Rectangle WorkArea;  // minus taskbar / appbars
        public bool IsPrimary;
        public uint Dpi = 96;
        public double Scale => Dpi / 96.0;
    }

    /// <summary>
    /// Monitor + DPI queries. The app runs Per-Monitor-DPI-V2 aware (see app.manifest),
    /// so every Win32 rectangle here is a genuine physical pixel rectangle across the
    /// whole virtual desktop — no scaling fudge factors, on any mix of displays.
    /// </summary>
    internal static class DisplayInfo
    {
        /// <summary>The bounding box of every monitor, in physical pixels.</summary>
        internal static Rectangle VirtualScreen
        {
            get
            {
                int x = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
                int y = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
                int w = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN));
                int h = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN));
                return new Rectangle(x, y, w, h);
            }
        }

        internal static List<MonitorEntry> All()
        {
            var list = new List<MonitorEntry>();
            Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr h, IntPtr hdc, ref Win32.RECT r, IntPtr d) =>
                {
                    list.Add(Describe(h));
                    return true;
                }, IntPtr.Zero);

            if (list.Count == 0)
            {
                var vs = VirtualScreen;
                list.Add(new MonitorEntry { Bounds = vs, WorkArea = vs, IsPrimary = true, Dpi = 96 });
            }
            return list;
        }

        internal static MonitorEntry Describe(IntPtr hMonitor)
        {
            var mi = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
            var entry = new MonitorEntry { Handle = hMonitor };

            if (Win32.GetMonitorInfo(hMonitor, ref mi))
            {
                entry.Bounds = ToRect(mi.rcMonitor);
                entry.WorkArea = ToRect(mi.rcWork);
                entry.IsPrimary = (mi.dwFlags & 1) != 0;
            }
            else
            {
                entry.Bounds = entry.WorkArea = VirtualScreen;
                entry.IsPrimary = true;
            }

            entry.Dpi = DpiOf(hMonitor);
            return entry;
        }

        internal static uint DpiOf(IntPtr hMonitor)
        {
            try
            {
                if (hMonitor != IntPtr.Zero &&
                    Win32.GetDpiForMonitor(hMonitor, Win32.MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0)
                    return dx;
            }
            catch { /* shcore missing on very old builds */ }
            return 96;
        }

        internal static MonitorEntry FromPoint(int x, int y)
            => Describe(Win32.MonitorFromPoint(new Win32.POINT(x, y), Win32.MONITOR_DEFAULTTONEAREST));

        internal static MonitorEntry FromWindow(IntPtr hwnd)
            => Describe(Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST));

        internal static MonitorEntry FromRect(Rectangle r)
        {
            var rc = ToRECT(r);
            return Describe(Win32.MonitorFromRect(ref rc, Win32.MONITOR_DEFAULTTONEAREST));
        }

        /// <summary>The monitor the mouse is currently on — the "active" display.</summary>
        internal static MonitorEntry FromCursor()
        {
            if (Win32.GetCursorPos(out var p)) return FromPoint(p.X, p.Y);
            return FromPoint(0, 0);
        }

        internal static Rectangle ToRect(Win32.RECT r)
            => new Rectangle(r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top));

        internal static Win32.RECT ToRECT(Rectangle r)
            => new Win32.RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };

        /// <summary>Clamps a rect to the part of it that is actually on some monitor.</summary>
        internal static Rectangle ClampToDesktop(Rectangle r)
        {
            var vs = VirtualScreen;
            r.Intersect(vs);
            return r;
        }
    }

    /// <summary>
    /// Positions WPF windows using physical pixels. WPF's Left/Top/Width/Height are
    /// device-independent units whose meaning changes with the DPI of whichever monitor
    /// the window happens to be on, which makes them unusable for precise multi-monitor
    /// placement. SetWindowPos + GetWindowRect are unambiguous, so we use those.
    /// </summary>
    internal static class WindowPlacement
    {
        internal static IntPtr HandleOf(System.Windows.Window w)
            => new System.Windows.Interop.WindowInteropHelper(w).Handle;

        internal static Rectangle GetPhysicalRect(System.Windows.Window w)
        {
            IntPtr h = HandleOf(w);
            if (h != IntPtr.Zero && Win32.GetWindowRect(h, out var rc))
                return DisplayInfo.ToRect(rc);
            return Rectangle.Empty;
        }

        internal static void MoveTo(System.Windows.Window w, int x, int y)
        {
            IntPtr h = HandleOf(w);
            if (h == IntPtr.Zero) return;
            Win32.SetWindowPos(h, IntPtr.Zero, x, y, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }

        internal static void SetBounds(System.Windows.Window w, Rectangle r, bool topMost)
        {
            IntPtr h = HandleOf(w);
            if (h == IntPtr.Zero) return;
            Win32.SetWindowPos(h, topMost ? Win32.HWND_TOPMOST : IntPtr.Zero,
                r.X, r.Y, r.Width, r.Height,
                topMost ? 0 : Win32.SWP_NOZORDER);
        }

        /// <summary>
        /// Centres a window on the monitor under the mouse. Runs twice because moving a
        /// window to a monitor with a different scale factor makes Windows resize it,
        /// so the first placement's measurements go stale.
        /// </summary>
        internal static void CenterOnActiveMonitor(System.Windows.Window w)
        {
            var mon = DisplayInfo.FromCursor();
            for (int pass = 0; pass < 2; pass++)
            {
                var size = GetPhysicalRect(w);
                if (size.Width <= 0) return;
                int x = mon.WorkArea.X + (mon.WorkArea.Width - size.Width) / 2;
                int y = mon.WorkArea.Y + (mon.WorkArea.Height - size.Height) / 2;
                MoveTo(w, x, y);
            }
        }

        /// <summary>Places a window near the bottom-centre of the given monitor's work area.</summary>
        internal static void BottomCenterOn(System.Windows.Window w, MonitorEntry mon, int marginPx = 24)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var size = GetPhysicalRect(w);
                if (size.Width <= 0) return;
                int scaledMargin = (int)Math.Round(marginPx * mon.Scale);
                int x = mon.WorkArea.X + (mon.WorkArea.Width - size.Width) / 2;
                int y = mon.WorkArea.Bottom - size.Height - scaledMargin;
                x = Math.Max(mon.WorkArea.X, x);
                y = Math.Max(mon.WorkArea.Y, y);
                MoveTo(w, x, y);
            }
        }

        /// <summary>
        /// Parks a window somewhere that does not overlap <paramref name="avoid"/> —
        /// preferring a different monitor, then the largest free corner.
        /// </summary>
        internal static void PlaceAwayFrom(System.Windows.Window w, Rectangle avoid)
        {
            var size = GetPhysicalRect(w);
            if (size.Width <= 0) return;

            var monitors = DisplayInfo.All();

            // 1) A monitor that the captured region doesn't touch at all.
            foreach (var m in monitors)
            {
                if (m.Bounds.IntersectsWith(avoid)) continue;
                MoveTo(w,
                    m.WorkArea.X + (m.WorkArea.Width - size.Width) / 2,
                    m.WorkArea.Y + (m.WorkArea.Height - size.Height) / 2);
                return;
            }

            // 2) Otherwise a corner of the region's own monitor with the most clearance.
            var host = DisplayInfo.FromRect(avoid);
            var wa = host.WorkArea;
            var corners = new[]
            {
                new Point(wa.Right - size.Width - 16, wa.Bottom - size.Height - 16),
                new Point(wa.X + 16,                  wa.Bottom - size.Height - 16),
                new Point(wa.Right - size.Width - 16, wa.Y + 16),
                new Point(wa.X + 16,                  wa.Y + 16),
            };

            Point best = corners[0];
            long bestOverlap = long.MaxValue;
            foreach (var c in corners)
            {
                var candidate = new Rectangle(c.X, c.Y, size.Width, size.Height);
                var hit = Rectangle.Intersect(candidate, avoid);
                long area = (long)hit.Width * hit.Height;
                if (area < bestOverlap) { bestOverlap = area; best = c; }
            }
            MoveTo(w, best.X, best.Y);
        }

        /// <summary>
        /// Asks Windows to leave this window out of screen captures (Win10 2004+).
        /// Best effort: on older builds it simply does nothing.
        /// </summary>
        internal static void ExcludeFromCapture(System.Windows.Window w, bool exclude = true)
        {
            try
            {
                IntPtr h = HandleOf(w);
                if (h != IntPtr.Zero)
                    Win32.SetWindowDisplayAffinity(h, exclude ? Win32.WDA_EXCLUDEFROMCAPTURE : Win32.WDA_NONE);
            }
            catch { }
        }
    }
}
