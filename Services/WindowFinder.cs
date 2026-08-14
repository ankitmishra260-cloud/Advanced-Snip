using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Automation;

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
        {
            try
            {
                if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT frame, System.Runtime.InteropServices.Marshal.SizeOf<Win32.RECT>()) == 0)
                {
                    var r = DisplayInfo.ToRect(frame);
                    if (r.Width > 0 && r.Height > 0) return r;
                }
            }
            catch { }
            return Win32.GetWindowRect(hwnd, out var rc) ? DisplayInfo.ToRect(rc) : Rectangle.Empty;
        }

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

        private static bool IsContentPane(IntPtr hwnd, string cls)
        {
            if (IsChromiumContent(hwnd)) return true;
            if (cls.StartsWith("_Ww", StringComparison.OrdinalIgnoreCase)) return true; // Microsoft Word (_WwG, _WwB, _WwF)
            if (cls.Equals("EXCEL7", StringComparison.OrdinalIgnoreCase)) return true;  // Microsoft Excel
            if (cls.IndexOf("Scintilla", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (cls.IndexOf("RichEdit", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (cls.IndexOf("Mozilla", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (cls.IndexOf("Document", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (cls.IndexOf("AVL_AVView", StringComparison.OrdinalIgnoreCase) >= 0) return true; // Adobe Acrobat
            if (cls.IndexOf("Acrobat", StringComparison.OrdinalIgnoreCase) >= 0) return true;   // Adobe Acrobat
            if (cls.IndexOf("SUMATRA", StringComparison.OrdinalIgnoreCase) >= 0) return true;   // SumatraPDF
            if (cls.IndexOf("Foxit", StringComparison.OrdinalIgnoreCase) >= 0) return true;     // Foxit Reader
            if (cls.IndexOf("PDF", StringComparison.OrdinalIgnoreCase) >= 0) return true;       // PDF viewers
            if (cls.IndexOf("OneNote", StringComparison.OrdinalIgnoreCase) >= 0) return true;   // OneNote
            if (cls.IndexOf("SUPERGRID", StringComparison.OrdinalIgnoreCase) >= 0) return true; // Outlook
            if (cls.IndexOf("SunAwt", StringComparison.OrdinalIgnoreCase) >= 0) return true;    // JetBrains IDEs
            if (cls.IndexOf("CASCADIA", StringComparison.OrdinalIgnoreCase) >= 0) return true;  // Windows Terminal
            return false;
        }

        /// <summary>
        /// Probes UI Automation for specific nested scrollable elements under the cursor
        /// (e.g. scrollable tables, data grids, code snippets, overflow divs on a webpage).
        /// </summary>
        private static TargetCandidate? TryGetUiaScrollCandidate(int screenX, int screenY, IntPtr targetHwnd, IntPtr topLevel, Rectangle maxBounds)
        {
            if (targetHwnd == IntPtr.Zero || !Win32.IsWindow(targetHwnd)) return null;

            try
            {
                var root = AutomationElement.FromHandle(targetHwnd);
                if (root == null) return null;

                var condition = new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true);
                var matches = root.FindAll(TreeScope.Element | TreeScope.Children | TreeScope.Descendants, condition);
                if (matches == null || matches.Count == 0) return null;

                var pt = new System.Windows.Point(screenX, screenY);
                AutomationElement? best = null;
                double bestArea = double.MaxValue;
                Rectangle bestRect = Rectangle.Empty;

                foreach (AutomationElement el in matches)
                {
                    try
                    {
                        var rect = el.Current.BoundingRectangle;
                        if (rect.IsEmpty || rect.Width < 60 || rect.Height < 40) continue;
                        if (!rect.Contains(pt)) continue;

                        var r = new Rectangle((int)Math.Round(rect.X), (int)Math.Round(rect.Y),
                                              (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));

                        // Only consider true nested scrollable elements (smaller than the outer viewport)
                        if (r.Width < maxBounds.Width - 12 || r.Height < maxBounds.Height - 12)
                        {
                            if (el.TryGetCurrentPattern(ScrollPattern.Pattern, out object patObj) &&
                                patObj is ScrollPattern sp &&
                                sp.Current.VerticallyScrollable)
                            {
                                double area = rect.Width * rect.Height;
                                if (area < bestArea)
                                {
                                    bestArea = area;
                                    best = el;
                                    bestRect = r;
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (best != null && bestRect.Width > 0 && bestRect.Height > 0)
                {
                    string typeName = best.Current.ControlType.ProgrammaticName;
                    string label = "Scrollable container";
                    if (typeName.Contains("Table") || typeName.Contains("DataGrid") || typeName.Contains("Grid"))
                        label = "Scrollable table";
                    else if (typeName.Contains("List"))
                        label = "Scrollable list";
                    else if (typeName.Contains("Document"))
                        label = "Document area";
                    else if (!string.IsNullOrWhiteSpace(best.Current.LocalizedControlType))
                        label = $"Scrollable {best.Current.LocalizedControlType.ToLowerInvariant()}";

                    return new TargetCandidate
                    {
                        Hwnd = targetHwnd,
                        TopLevel = topLevel,
                        Region = bestRect,
                        LikelyScrollable = true,
                        Label = label
                    };
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Builds the list of things the user could mean by pointing here, ordered most
        /// specific first: nested scrollable containers (tables/grids), deepest child,
        /// each ancestor up the chain, then the whole window's content area, then the
        /// window including its title bar. The picker lets the mouse wheel step through these.
        /// </summary>
        internal static List<TargetCandidate> BuildCandidates(int screenX, int screenY)
        {
            var result = new List<TargetCandidate>();

            IntPtr top = HitTestTopLevel(screenX, screenY);
            if (top == IntPtr.Zero) return result;

            var clientRect = ClientRectOnScreen(top);
            IntPtr deepest = DeepestChildAt(top, screenX, screenY);
            IntPtr targetHwnd = deepest != IntPtr.Zero ? deepest : top;

            // 1. Probe for nested scrollable elements (e.g. scrollable tables, data grids on webpages).
            if (clientRect.Width > 0 && clientRect.Height > 0)
            {
                var uia = TryGetUiaScrollCandidate(screenX, screenY, targetHwnd, top, clientRect);
                if (uia != null) Add(result, uia);
            }

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

                string cls = Win32.GetClass(h);
                bool contentPane = IsContentPane(h, cls);
                bool scrollable = contentPane || HasVerticalScrollBar(h);

                Add(result, new TargetCandidate
                {
                    Hwnd = h,
                    TopLevel = top,
                    Region = rect,
                    LikelyScrollable = scrollable,
                    Label = IsChromiumContent(h) ? "Page content" : DescribeClass(cls)
                });
            }

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
            if (cls.StartsWith("_Ww", StringComparison.OrdinalIgnoreCase)) return "Word document";
            if (cls.IndexOf("Acrobat", StringComparison.OrdinalIgnoreCase) >= 0 || cls.IndexOf("AVL_AVView", StringComparison.OrdinalIgnoreCase) >= 0 || cls.IndexOf("SUMATRA", StringComparison.OrdinalIgnoreCase) >= 0 || cls.IndexOf("PDF", StringComparison.OrdinalIgnoreCase) >= 0) return "PDF document";
            if (cls.IndexOf("OneNote", StringComparison.OrdinalIgnoreCase) >= 0) return "OneNote page";
            if (cls.IndexOf("SUPERGRID", StringComparison.OrdinalIgnoreCase) >= 0) return "Outlook view";
            if (cls.IndexOf("SunAwt", StringComparison.OrdinalIgnoreCase) >= 0) return "Editor canvas";
            if (cls.IndexOf("CASCADIA", StringComparison.OrdinalIgnoreCase) >= 0) return "Terminal";
            return cls switch
            {
                "EXCEL7" => "Spreadsheet",
                "SysListView32" => "List view",
                "SysTreeView32" => "Tree view",
                "DirectUIHWND" => "Explorer view",
                "RICHEDIT50W" or "RichEdit20W" or "RichEdit60W" => "Rich text",
                "Edit" => "Text box",
                "LISTBOX" => "List box",
                "Scintilla" => "Code editor",
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
