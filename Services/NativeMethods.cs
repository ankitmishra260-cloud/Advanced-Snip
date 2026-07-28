using System;
using System.Runtime.InteropServices;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Small interop surface used by the clipboard / paste path.
    ///
    /// Everything that is *also* needed by capture now lives in <see cref="Win32"/>,
    /// which is the single source of truth for P/Invoke signatures. The members below
    /// either forward to Win32 (so the two files can never drift apart) or are unique
    /// to this file.
    ///
    /// No WPF/WinForms types are referenced here, so it never suffers the
    /// System.Windows vs System.Windows.Forms name clashes.
    /// </summary>
    internal static class NativeMethods
    {
        // ----- Window focus (forwarded to Win32) -----
        internal static IntPtr GetForegroundWindow() => Win32.GetForegroundWindow();

        internal static bool SetForegroundWindow(IntPtr hWnd) => Win32.SetForegroundWindow(hWnd);

        // ----- Extended window styles (forwarded to Win32) -----
        internal static int GetWindowLong(IntPtr hWnd, int nIndex) => Win32.GetWindowLong(hWnd, nIndex);

        internal static int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong)
            => Win32.SetWindowLong(hWnd, nIndex, dwNewLong);

        internal const int GWL_EXSTYLE = Win32.GWL_EXSTYLE;
        internal const int WS_EX_TOOLWINDOW = Win32.WS_EX_TOOLWINDOW;
        internal const int WS_EX_NOACTIVATE = Win32.WS_EX_NOACTIVATE;

        // ----- Unique to this file -----

        /// <summary>
        /// Grants another process the right to steal focus, so the paste target can be
        /// raised without Windows' foreground lock silently swallowing the request.
        /// </summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AllowSetForegroundWindow(int dwProcessId);

        internal const int ASFW_ANY = -1;

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr hObject);

        /// <summary>
        /// Sends Ctrl+V to whatever window currently has keyboard focus.
        ///
        /// This used to declare its own INPUT struct, which measured 32 bytes on x64
        /// instead of the 40 the OS expects. SendInput validates cbSize and rejects
        /// anything else, so the paste was being dropped without any error surfacing.
        /// It now shares Win32's correctly-laid-out struct.
        /// </summary>
        internal static void SendPaste() => Win32.SendKeyStroke(Win32.VK_V, withCtrl: true);
    }
}
