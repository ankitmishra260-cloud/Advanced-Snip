using System;
using System.Drawing;

namespace AdvancedSnip.Services
{
    internal enum ScrollMethod
    {
        /// <summary>
        /// WM_MOUSEWHEEL posted straight to the window. Tried first because it is the
        /// only method that does not touch the pointer, so the mouse stays yours for the
        /// whole capture.
        /// </summary>
        PostedWheel,
        /// <summary>
        /// Synthesised physical wheel. Works nearly everywhere, but a real wheel event
        /// goes to whatever is under the cursor, so the cursor has to be held over the
        /// target — which means taking it away from the user. Second choice for exactly
        /// that reason.
        /// </summary>
        InjectedWheel,
        /// <summary>Page Down keystrokes. Last resort — needs keyboard focus.</summary>
        Keyboard
    }

    /// <summary>
    /// Moves the target's content. Nothing here reports how far anything scrolled — the
    /// distance is always measured from the pixels afterwards, so it doesn't matter that
    /// a wheel notch means different things in different apps.
    /// </summary>
    internal sealed class ScrollDriver
    {
        private readonly IntPtr _targetHwnd;
        private readonly Point _hotspot;      // where the cursor sits while scrolling
        private Win32.POINT _savedCursor;
        private bool _cursorSaved;

        /// <summary>
        /// Hardware-injected wheel by default. Direct input injection works universally across
        /// Microsoft Word, Office, Electron, Chromium, WPF, UWP/WinUI and standard Win32 windows.
        /// </summary>
        internal ScrollMethod Method { get; set; } = ScrollMethod.InjectedWheel;

        /// <summary>True once this driver has actually taken the pointer over.</summary>
        internal bool CursorTaken => _cursorSaved;

        internal ScrollDriver(IntPtr targetHwnd, Rectangle region)
        {
            _targetHwnd = targetHwnd;

            // Sit comfortably inside the region, slightly above vertical centre and clear of right scrollbars.
            int scrollbar = Math.Max(24, Win32.GetSystemMetrics(Win32.SM_CXVSCROLL) + 12);
            int x = region.X + Math.Max(12, (region.Width - scrollbar) / 2);
            int y = region.Y + Math.Max(12, region.Height / 3);
            _hotspot = new Point(x, y);
        }

        /// <summary>
        /// Moves the pointer over the target and remembers where it was.
        ///
        /// Called lazily, only from the injected-wheel path. The previous version parked
        /// unconditionally at the start of every capture and then re-parked on every
        /// single scroll step, so the pointer was pinned for the whole run even when the
        /// method in use didn't need it at all. That is what made the mouse feel frozen.
        /// </summary>
        private void ParkCursor()
        {
            if (!_cursorSaved && Win32.GetCursorPos(out _savedCursor))
                _cursorSaved = true;

            Win32.SetCursorPos(_hotspot.X, _hotspot.Y);
        }

        /// <summary>Puts the pointer back. Does nothing if we never took it.</summary>
        internal void RestoreCursor()
        {
            if (_cursorSaved)
                Win32.SetCursorPos(_savedCursor.X, _savedCursor.Y);
        }

        /// <summary>Scrolls down by the given number of notches (or up when negative).</summary>
        internal void Scroll(int notches)
        {
            if (notches == 0) return;

            switch (Method)
            {
                case ScrollMethod.PostedWheel:
                    PostWheel(notches);
                    break;

                case ScrollMethod.Keyboard:
                    SendPageKeys(notches);
                    break;

                default:
                    // An injected wheel is delivered to whatever sits under the pointer,
                    // so there is no alternative to putting it there. Only re-parked when
                    // it has actually drifted, so a user who leaves the mouse alone isn't
                    // fighting a SetCursorPos on every step.
                    ParkCursor();
                    // Negative wheel delta scrolls the content down.
                    Win32.SendWheel(-notches);
                    break;
            }
        }

        private void PostWheel(int notches)
        {
            IntPtr lParam = Win32.MakeScreenLParam(_hotspot.X, _hotspot.Y);

            // The message has to reach the control that actually scrolls, which for a
            // browser or an Electron app is a child several levels below the frame. The
            // window physically under the hotspot is the best available guess, and it is
            // the same one a real wheel event would have gone to.
            IntPtr hwnd = Win32.WindowFromPoint(new Win32.POINT(_hotspot.X, _hotspot.Y));
            if (hwnd == IntPtr.Zero) hwnd = _targetHwnd;
            if (hwnd == IntPtr.Zero) hwnd = Win32.GetForegroundWindow();

            int step = Math.Sign(notches);
            for (int i = 0; i < Math.Min(Math.Abs(notches), 40); i++)
                Win32.PostMessage(hwnd, Win32.WM_MOUSEWHEEL,
                                  Win32.MakeWheelWParam(-step), lParam);
        }

        private void SendPageKeys(int notches)
        {
            EnsureFocus();
            // Roughly three wheel notches to a page.
            int pages = Math.Max(1, Math.Abs(notches) / 3);
            ushort key = notches > 0 ? Win32.VK_NEXT : Win32.VK_PRIOR;
            for (int i = 0; i < Math.Min(pages, 12); i++)
                Win32.SendKeyStroke(key, extended: true);
        }

        private void EnsureFocus()
        {
            if (_targetHwnd != IntPtr.Zero && Win32.IsWindow(_targetHwnd))
            {
                try
                {
                    NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                    Win32.SetForegroundWindow(_targetHwnd);
                }
                catch { }
            }
        }

        /// <summary>Jumps to the very top using whichever method is active.</summary>
        internal void ScrollToTopBurst()
        {
            if (Method == ScrollMethod.Keyboard)
            {
                Win32.SendKeyStroke(Win32.VK_HOME, extended: true, withCtrl: true);
                return;
            }
            Scroll(-25);
        }

        /// <summary>Steps to the next fallback. Returns false once they're exhausted.</summary>
        internal bool TryNextMethod()
        {
            switch (Method)
            {
                case ScrollMethod.InjectedWheel:
                    Method = ScrollMethod.PostedWheel;
                    return true;
                case ScrollMethod.PostedWheel:
                    Method = ScrollMethod.Keyboard;
                    return true;
                default:
                    return false;
            }
        }

        internal string MethodName => Method switch
        {
            ScrollMethod.PostedWheel => "window messages",
            ScrollMethod.Keyboard => "keyboard",
            _ => "the mouse wheel (pointer in use)"
        };
    }
}
