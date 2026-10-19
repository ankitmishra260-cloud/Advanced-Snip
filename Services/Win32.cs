using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// All Win32 interop used by capture, DPI/monitor handling and input injection.
    /// Deliberately contains no WPF / WinForms / System.Drawing types so it can never
    /// suffer the System.Windows vs System.Windows.Forms name clashes.
    /// </summary>
    internal static class Win32
    {
        // ══════════════════════════════ structs ══════════════════════════════

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
            public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X, Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SCROLLINFO
        {
            public uint cbSize;
            public uint fMask;
            public int nMin, nMax, nPage, nPos, nTrackPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        // ── SendInput ────────────────────────────────────────────────────────
        //
        // NOTE: getting these layouts right matters. The union must be 8-byte
        // aligned on x64 (because of the ULONG_PTR dwExtraInfo), which makes
        // sizeof(INPUT) == 40 on x64 and 28 on x86. A hand-rolled "uint + byte[32]"
        // version measures 36 bytes, SendInput rejects the cbSize and silently
        // does nothing — which is exactly how the previous scroll capture failed.

        [StructLayout(LayoutKind.Sequential)]
        internal struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL, wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        // ── DWM: dark title bar ──────────────────────────────────────────────
        // WPF themes the client area only. Without this a dark window keeps a white
        // caption bar, which looks broken rather than merely unstyled.
        //
        // The attribute id moved during Windows 10's life: builds before 19041 used 19,
        // 20 thereafter. Trying 20 first and falling back to 19 covers both without
        // needing to query the build number.
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attr, ref int value, int size);

        internal static void SetTitleBarDark(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return;
            int flag = dark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE,
                                          ref flag, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD,
                                          ref flag, sizeof(int));
            }
            catch { /* pre-1809 Windows has no dark caption; not worth reporting */ }
        }

        // ── Shell file operations ────────────────────────────────────────────
        // Deleting to the Recycle Bin rather than unlinking matters most exactly when
        // the gallery makes it easy to select two hundred files and press Delete.

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
        }

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_NOERRORUI = 0x0400;
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_WANTNUKEWARNING = 0x4000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

        /// <summary>
        /// Sends files to the Recycle Bin. The path list is double-null terminated —
        /// a single trailing null is the most common way to get this call to fail
        /// silently or, worse, act on garbage past the end of the buffer.
        /// </summary>
        internal static bool RecycleFiles(System.Collections.Generic.IEnumerable<string> paths,
                                          IntPtr owner)
        {
            var joined = string.Join("\0", paths);
            if (joined.Length == 0) return true;

            var op = new SHFILEOPSTRUCT
            {
                hwnd = owner,
                wFunc = FO_DELETE,
                pFrom = joined + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT
            };

            try { return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted; }
            catch { return false; }
        }

        internal const uint INPUT_MOUSE = 0;
        internal const uint INPUT_KEYBOARD = 1;

        internal const uint MOUSEEVENTF_WHEEL = 0x0800;
        internal const uint MOUSEEVENTF_HWHEEL = 0x1000;
        internal const uint KEYEVENTF_KEYUP = 0x0002;
        internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

        internal const int WHEEL_DELTA = 120;

        internal const ushort VK_HOME = 0x24;
        internal const ushort VK_END = 0x23;
        internal const ushort VK_PRIOR = 0x21;   // Page Up
        internal const ushort VK_NEXT = 0x22;    // Page Down
        internal const ushort VK_CONTROL = 0x11;
        internal const ushort VK_V = 0x56;
        internal const ushort VK_SHIFT = 0x10;
        // Alt is defined but deliberately not used as a drawing modifier: pressing it
        // opens the menu bar of whatever application is being recorded, which would be
        // visible in the take. See RecordAnnotationWindow.ResolveTool.
        internal const ushort VK_MENU = 0x12;

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int vKey);

        /// <summary>
        /// True when the key is physically down right now.
        ///
        /// Used instead of WPF's <c>Keyboard.Modifiers</c> by the recording overlay, which
        /// is WS_EX_NOACTIVATE and therefore never has keyboard focus — WPF's view of the
        /// modifier state on a window that never receives key messages is not something to
        /// rely on. This asks the OS directly and does not care who has focus.
        /// </summary>
        internal static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

        internal static bool ShiftDown => IsKeyDown(VK_SHIFT);
        internal static bool CtrlDown => IsKeyDown(VK_CONTROL);

        // ══════════════════════════════ windows ══════════════════════════════

        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lp);
        internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lp);

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rc);

        [DllImport("user32.dll")]
        internal static extern bool GetClientRect(IntPtr hwnd, out RECT rc);

        [DllImport("user32.dll")]
        internal static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);

        [DllImport("user32.dll")]
        internal static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern bool IsZoomed(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetParent(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        internal static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetShellWindow();

        internal static IntPtr GetDesktopShellWindow()
        {
            IntPtr progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero) return progman;
            IntPtr shell = GetShellWindow();
            if (shell != IntPtr.Zero) return shell;
            return IntPtr.Zero;
        }

        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        internal const uint GA_ROOT = 2;

        [DllImport("user32.dll")]
        internal static extern IntPtr RealChildWindowFromPoint(IntPtr hwndParent, POINT pt);

        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(POINT pt);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetWindowLong(IntPtr hwnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int SetWindowLong(IntPtr hwnd, int nIndex, int value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        internal static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
                return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
            else
                return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        internal const int GWL_STYLE = -16;
        internal const int GWL_EXSTYLE = -20;
        internal const int GWLP_HWNDPARENT = -8;

        internal const int WS_VSCROLL = 0x00200000;
        internal const int WS_HSCROLL = 0x00100000;
        internal const int WS_THICKFRAME = 0x00040000;
        internal const int WS_MAXIMIZEBOX = 0x00010000;
        internal const int WS_CHILD = 0x40000000;
        internal const int WS_VISIBLE = 0x10000000;
        internal const int WS_CLIPSIBLINGS = 0x04000000;
        internal const int WS_CLIPCHILDREN = 0x02000000;
        internal const int WS_EX_TOOLWINDOW = 0x00000080;
        internal const int WS_EX_NOACTIVATE = 0x08000000;
        internal const int WS_EX_TRANSPARENT = 0x00000020;

        internal const int WM_WINDOWPOSCHANGING = 0x0046;

        [StructLayout(LayoutKind.Sequential)]
        internal struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        [DllImport("user32.dll")]
        internal static extern bool InvalidateRect(IntPtr hWnd, ref RECT lpRect, bool bErase);

        [DllImport("user32.dll")]
        internal static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("user32.dll")]
        internal static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        internal static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int n);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        internal static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int n);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

        internal const int SW_RESTORE = 9;
        internal const int SW_SHOW = 5;

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after,
            int x, int y, int cx, int cy, uint flags);

        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        internal static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        internal static readonly IntPtr HWND_TOP = IntPtr.Zero;
        internal static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_FRAMECHANGED = 0x0020;
        internal const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT pt);

        [DllImport("user32.dll")]
        internal static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        internal static extern int GetScrollInfo(IntPtr hwnd, int bar, ref SCROLLINFO si);

        internal const int SB_VERT = 1;
        internal const uint SIF_ALL = 0x17;

        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

        [DllImport("user32.dll")]
        internal static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        internal const uint WM_MOUSEWHEEL = 0x020A;
        internal const uint SMTO_ABORTIFHUNG = 0x0002;

        [DllImport("user32.dll")]
        internal static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        internal const uint PW_RENDERFULLCONTENT = 0x00000002;

        /// <summary>Win10 2004+: keeps a window out of screen captures entirely.</summary>
        [DllImport("user32.dll")]
        internal static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        internal const uint WDA_NONE = 0x00000000;

        // ── cursor, for screen recording ──────────────────────────────────
        // BitBlt copies the desktop surface, and the pointer is not part of it — it is
        // composited by the display driver. Anything that wants a mouse in the picture
        // has to fetch and draw it separately.

        [StructLayout(LayoutKind.Sequential)]
        internal struct CURSORINFO
        {
            internal int cbSize;
            internal int flags;
            internal IntPtr hCursor;
            internal POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ICONINFO
        {
            [MarshalAs(UnmanagedType.Bool)] internal bool fIcon;
            internal int xHotspot;
            internal int yHotspot;
            internal IntPtr hbmMask;
            internal IntPtr hbmColor;
        }

        internal const int CURSOR_SHOWING = 0x00000001;
        internal const int DI_NORMAL = 0x0003;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorInfo(ref CURSORINFO pci);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
                                               int cxWidth, int cyWidth, int istepIfAniCur,
                                               IntPtr hbrFlickerFreeDraw, int diFlags);

        // WS_EX_TRANSPARENT (click-through, used by the recording overlay) is already
        // declared with the other window styles further down this file.
        internal const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        // ── DWM (used to skip cloaked/invisible UWP shells during hit-testing) ──

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        internal const int DWMWA_CLOAKED = 14;
        internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

        // ══════════════════════════════ monitors / DPI ═══════════════════════

        [DllImport("user32.dll")]
        internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc fn, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromRect(ref RECT rc, uint flags);

        internal const uint MONITOR_DEFAULTTONULL = 0;
        internal const uint MONITOR_DEFAULTTOPRIMARY = 1;
        internal const uint MONITOR_DEFAULTTONEAREST = 2;

        /// <summary>Win10 1607+. Returns 96 for 100% scaling.</summary>
        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

        internal const int MDT_EFFECTIVE_DPI = 0;

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);

        internal const int SM_XVIRTUALSCREEN = 76;
        internal const int SM_YVIRTUALSCREEN = 77;
        internal const int SM_CXVIRTUALSCREEN = 78;
        internal const int SM_CYVIRTUALSCREEN = 79;
        internal const int SM_CXVSCROLL = 2;

        // ══════════════════════════════ GDI ══════════════════════════════════

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        internal static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
            IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

        internal const uint SRCCOPY = 0x00CC0020;

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
                                               IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc,
                                               uint rop);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        internal static extern int SetStretchBltMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr pt);

        /// <summary>
        /// COLORONCOLOR. For magnification this is the right choice: HALFTONE averages
        /// source pixels, which is what you want when shrinking, but when enlarging it
        /// only softens text that was already sharp.
        /// </summary>
        internal const int STRETCH_COLORONCOLOR = 3;

        internal const int STRETCH_HALFTONE = 4;
        internal const uint CAPTUREBLT = 0x40000000;

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteObject(IntPtr obj);

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
        internal static extern void CopyMemory(IntPtr dest, IntPtr src, UIntPtr count);

        // ══════════════════════════════ helpers ══════════════════════════════

        internal static string GetTitle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        internal static string GetClass(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        internal static bool IsCloaked(IntPtr hwnd)
        {
            try
            {
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0)
                    return cloaked != 0;
            }
            catch { }
            return false;
        }

        /// <summary>Sends one or more vertical wheel notches. Negative = scroll down.</summary>
        internal static bool SendWheel(int notches)
        {
            if (notches == 0) return true;
            int count = Math.Min(Math.Abs(notches), 40);
            int sign = Math.Sign(notches);

            var inputs = new INPUT[count];
            for (int i = 0; i < count; i++)
            {
                inputs[i] = new INPUT
                {
                    type = INPUT_MOUSE,
                    u = new INPUTUNION
                    {
                        mi = new MOUSEINPUT
                        {
                            dwFlags = MOUSEEVENTF_WHEEL,
                            mouseData = unchecked((uint)(sign * WHEEL_DELTA))
                        }
                    }
                };
            }
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
        }

        internal static void SendKeyStroke(ushort vk, bool extended = false, bool withCtrl = false)
        {
            var list = new System.Collections.Generic.List<INPUT>(4);
            if (withCtrl) list.Add(Key(VK_CONTROL, false, false));
            list.Add(Key(vk, false, extended));
            list.Add(Key(vk, true, extended));
            if (withCtrl) list.Add(Key(VK_CONTROL, true, false));

            var arr = list.ToArray();
            SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        }

        private static INPUT Key(ushort vk, bool up, bool extended) => new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = (up ? KEYEVENTF_KEYUP : 0u) | (extended ? KEYEVENTF_EXTENDEDKEY : 0u)
                }
            }
        };

        internal static IntPtr MakeWheelWParam(int notches)
        {
            // HIWORD = wheel delta, LOWORD = key state (none)
            int delta = notches * WHEEL_DELTA;
            return (IntPtr)unchecked((int)((uint)(short)delta << 16));
        }

        internal static IntPtr MakeScreenLParam(int x, int y)
            => (IntPtr)unchecked((int)(((uint)(ushort)(short)y << 16) | (ushort)(short)x));
    }
}
