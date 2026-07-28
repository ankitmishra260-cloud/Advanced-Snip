using System;
using System.Collections.Generic;
using System.Drawing;

namespace AdvancedSnip.Services
{
    /// <summary>A pickable region: a window (or part of one) the user can point at.</summary>
    public sealed class TargetCandidate
    {
        public IntPtr Hwnd;
        public IntPtr TopLevel;
        public Rectangle Region;      // physical pixels
        public string Label = "";     // short description shown in the picker
        public bool LikelyScrollable;
    }

    /// <summary>
    /// Finds what the user is pointing at.
    ///
    /// Hit-testing deliberately does NOT use WindowFromPoint: the picker overlay sits
    /// on top of everything, so WindowFromPoint would always return the overlay itself.
    /// Instead we enumerate top-level windows in Z-order (EnumWindows returns them
    /// front-to-back) and take the first visible, non-cloaked one containing the point.
    /// From there RealChildWindowFromPoint walks down into the target's own child tree,
    /// which the overlay is not part of.
    /// </summary>
    internal static class WindowFinder
    {
        private static readonly uint OwnPid = (uint)Environment.ProcessId;

        // Chromium hosts web contents in this child HWND. Its rectangle is exactly the
        // page area — no tab strip, no address bar — which is what "capture this tab"
        // should mean. Covers Chrome, Edge, Brave, Opera, Vivaldi and Electron apps.
        private const string ChromiumContent = "Chrome_RenderWidgetHostHWND";

        internal static Rectangle WindowRect(IntPtr hwnd)
            => Win32.GetWindowRect(hwnd, out var r) ? DisplayInfo.ToRect(r) : Rectangle.Empty;

        /// <summary>The client area of a window, expressed in screen coordinates.</summary>
        internal static Rectangle ClientRectOnScreen(IntPtr hwnd)
        {
            if (!Win32.GetClientRect(hwnd, out var rc)) return Rectangle.Empty;
            var tl = new Win32.POINT(rc.Left, rc.Top);
            if (!Win32.ClientToScreen(hwnd, ref tl)) return Rectangle.Empty;
            return new Rectangle(tl.X, tl.Y, Math.Max(0, rc.Width), Math.Max(0, rc.Height));
        }

        /// <summary>Top-level windows, front-most first, with junk filtered out.</summary>
        internal static IntPtr HitTestTopLevel(int x, int y)
        {
            IntPtr found = IntPtr.Zero;

            Win32.EnumWindows((hwnd, _) =>
            {
                if (!IsPickable(hwnd)) return true;
                var r = WindowRect(hwnd);
                if (r.Width <= 1 || r.Height <= 1) return true;
                if (!r.Contains(x, y)) return true;

                found = hwnd;
                return false;   // EnumWindows is Z-ordered, so the first hit is the top one
            }, IntPtr.Zero);

            return found;
        }

        private static bool IsPickable(IntPtr hwnd)
        {
            if (!Win32.IsWindowVisible(hwnd)) return false;
            if (Win32.IsIconic(hwnd)) return false;
            if (Win32.GetParent(hwnd) != IntPtr.Zero) return false;

            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == OwnPid) return false;                 // never pick our own overlays

            if (Win32.IsCloaked(hwnd)) return false;         // invisible UWP shells

            int ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
            if ((ex & Win32.WS_EX_TOOLWINDOW) != 0)
            {
                // Tool windows are usually helpers, but some real panels use the style.
                // Keep the ones that are big enough to hold content.
                var rr = WindowRect(hwnd);
                if (rr.Width < 200 || rr.Height < 150) return false;
            }
            return true;
        }

        /// <summary>Walks down the child tree to the deepest real window under the point.</summary>
        internal static IntPtr DeepestChildAt(IntPtr topLevel, int screenX, int screenY)
        {
            IntPtr cur = topLevel;
            for (int depth = 0; depth < 16; depth++)
            {
                var p = new Win32.POINT(screenX, screenY);
                if (!Win32.ScreenToClient(cur, ref p)) break;

                IntPtr child = Win32.RealChildWindowFromPoint(cur, p);
                if (child == IntPtr.Zero || child == cur) break;
                cur = child;
            }
            return cur;
        }

        internal static bool HasVerticalScrollBar(IntPtr hwnd)
        {
            int style = Win32.GetWindowLong(hwnd, Win32.GWL_STYLE);
            if ((style & Win32.WS_VSCROLL) != 0) return true;

            var si = new Win32.SCROLLINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.SCROLLINFO>(),
                fMask = Win32.SIF_ALL
            };
            if (Win32.GetScrollInfo(hwnd, Win32.SB_VERT, ref si) != 0)
                return si.nMax - si.nMin > si.nPage && si.nPage > 0;

            return false;
        }

        private static bool IsChromiumContent(IntPtr hwnd)
            => Win32.GetClass(hwnd).IndexOf("RenderWidgetHost", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Builds the list of things the user could mean by pointing here, ordered most
        /// specific first: the deepest child, each ancestor up the chain, then the whole
        /// window's content area, then the window including its title bar. The picker
        /// lets the mouse wheel step through these.
        /// </summary>
        internal static List<TargetCandidate> BuildCandidates(int screenX, int screenY)
        {
            var result = new List<TargetCandidate>();

            IntPtr top = HitTestTopLevel(screenX, screenY);
            if (top == IntPtr.Zero) return result;

            IntPtr deepest = DeepestChildAt(top, screenX, screenY);

            // Walk from the deepest child back up to (but not including) the top level.
            var chain = new List<IntPtr>();
            IntPtr cur = deepest;
            while (cur != IntPtr.Zero && cur != top && chain.Count < 16)
            {
                chain.Add(cur);
                cur = Win32.GetParent(cur);
            }

            foreach (var h in chain)
            {
                var rect = ClientRectOnScreen(h);
                if (rect.Width < 60 || rect.Height < 60) continue;

                bool chromium = IsChromiumContent(h);
                bool scrollable = chromium || HasVerticalScrollBar(h);

                Add(result, new TargetCandidate
                {
                    Hwnd = h,
                    TopLevel = top,
                    Region = rect,
                    LikelyScrollable = scrollable,
                    Label = chromium ? "Page content" : DescribeClass(Win32.GetClass(h))
                });
            }

            var clientRect = ClientRectOnScreen(top);
            if (clientRect.Width > 0)
            {
                Add(result, new TargetCandidate
                {
                    Hwnd = top,
                    TopLevel = top,
                    Region = clientRect,
                    LikelyScrollable = HasVerticalScrollBar(top),
                    Label = "Window content"
                });
            }

            var windowRect = WindowRect(top);
            if (windowRect.Width > 0)
            {
                Add(result, new TargetCandidate
                {
                    Hwnd = top,
                    TopLevel = top,
                    Region = windowRect,
                    LikelyScrollable = false,
                    Label = "Whole window"
                });
            }

            return result;
        }

        private static void Add(List<TargetCandidate> list, TargetCandidate c)
        {
            var clamped = DisplayInfo.ClampToDesktop(c.Region);
            if (clamped.Width < 60 || clamped.Height < 60) return;
            c.Region = clamped;

            foreach (var existing in list)
            {
                // Treat near-identical rectangles as the same candidate.
                if (Math.Abs(existing.Region.X - c.Region.X) <= 2 &&
                    Math.Abs(existing.Region.Y - c.Region.Y) <= 2 &&
                    Math.Abs(existing.Region.Width - c.Region.Width) <= 2 &&
                    Math.Abs(existing.Region.Height - c.Region.Height) <= 2)
                {
                    existing.LikelyScrollable |= c.LikelyScrollable;
                    return;
                }
            }
            list.Add(c);
        }

        /// <summary>Picks the candidate we think the user most likely means.</summary>
        internal static int DefaultCandidateIndex(List<TargetCandidate> candidates)
        {
            for (int i = 0; i < candidates.Count; i++)
                if (candidates[i].LikelyScrollable) return i;

            // No obvious scroll region — fall back to the window's content area.
            for (int i = 0; i < candidates.Count; i++)
                if (candidates[i].Label == "Window content") return i;

            return candidates.Count > 0 ? candidates.Count - 1 : 0;
        }

        private static string DescribeClass(string cls)
        {
            if (string.IsNullOrWhiteSpace(cls)) return "Pane";
            return cls switch
            {
                "SysListView32" => "List view",
                "SysTreeView32" => "Tree view",
                "DirectUIHWND" => "Explorer view",
                "RICHEDIT50W" or "RichEdit20W" => "Rich text",
                "Edit" => "Text box",
                "LISTBOX" => "List box",
                "Scintilla" => "Editor",
                "MozillaWindowClass" or "MozillaCompositorWindowClass" => "Page content",
                _ => cls.Length > 28 ? cls[..28] + "\u2026" : cls
            };
        }

        /// <summary>
        /// Brings a window forward and waits for it to actually get there. Returns false
        /// if Windows refused the foreground change (some apps block it).
        /// </summary>
        internal static bool BringToFront(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return false;

            if (Win32.IsIconic(hwnd))
            {
                Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
                System.Threading.Thread.Sleep(180);
            }

            NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
            Win32.SetForegroundWindow(hwnd);

            for (int i = 0; i < 12; i++)
            {
                IntPtr fg = Win32.GetForegroundWindow();
                if (fg == hwnd || Win32.GetAncestor(fg, Win32.GA_ROOT) == hwnd) return true;
                System.Threading.Thread.Sleep(40);
            }
            return false;
        }
    }
}
