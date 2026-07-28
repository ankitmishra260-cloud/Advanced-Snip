using System;
using System.Drawing;

namespace AdvancedSnip.Services
{
    internal enum ScrollMethod
    {
        /// <summary>Synthesised physical wheel at the cursor. Works nearly everywhere.</summary>
        InjectedWheel,
        /// <summary>WM_MOUSEWHEEL posted straight to the child window.</summary>
        PostedWheel,
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

        internal ScrollMethod Method { get; set; } = ScrollMethod.InjectedWheel;

        internal ScrollDriver(IntPtr targetHwnd, Rectangle region)
        {
            _targetHwnd = targetHwnd;

            // Sit slightly above centre and left of the scrollbar: dead centre can land on
            // a nested scroller (an embedded map or code block) which would scroll instead
            // of the page.
            int scrollbar = Math.Max(20, Win32.GetSystemMetrics(Win32.SM_CXVSCROLL) + 8);
            int x = region.X + Math.Max(8, (region.Width - scrollbar) / 2);
            int y = region.Y + Math.Max(8, region.Height / 3);
            _hotspot = new Point(x, y);
        }

        internal void ParkCursor()
        {
            if (!_cursorSaved && Win32.GetCursorPos(out _savedCursor))
                _cursorSaved = true;

            Win32.SetCursorPos(_hotspot.X, _hotspot.Y);
        }

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
                    Win32.SetCursorPos(_hotspot.X, _hotspot.Y);
                    // Negative wheel delta scrolls the content down.
                    Win32.SendWheel(-notches);
                    break;
            }
        }

        private void PostWheel(int notches)
        {
            IntPtr wParam = Win32.MakeWheelWParam(-notches);
            IntPtr lParam = Win32.MakeScreenLParam(_hotspot.X, _hotspot.Y);

            IntPtr hwnd = _targetHwnd != IntPtr.Zero ? _targetHwnd : Win32.GetForegroundWindow();
            for (int i = 0; i < Math.Min(Math.Abs(notches), 40); i++)
                Win32.PostMessage(hwnd, Win32.WM_MOUSEWHEEL,
                                  Win32.MakeWheelWParam(-Math.Sign(notches)), lParam);
        }

        private void SendPageKeys(int notches)
        {
            // Roughly three wheel notches to a page.
            int pages = Math.Max(1, Math.Abs(notches) / 3);
            ushort key = notches > 0 ? Win32.VK_NEXT : Win32.VK_PRIOR;
            for (int i = 0; i < Math.Min(pages, 12); i++)
                Win32.SendKeyStroke(key, extended: true);
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
            _ => "mouse wheel"
        };
    }
}
