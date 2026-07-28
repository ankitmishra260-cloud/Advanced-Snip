using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Registers system-wide hotkeys with Win32 RegisterHotKey. It owns a hidden
    /// message window (a 1x1, never-shown <see cref="HwndSource"/>) that receives
    /// WM_HOTKEY on the WPF UI thread, so the callbacks run on the UI thread.
    /// </summary>
    public sealed class HotKeyManager : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly HwndSource _source;
        private readonly Dictionary<int, Action> _callbacks = new();
        private int _nextId;
        private bool _disposed;

        public HotKeyManager()
        {
            var parameters = new HwndSourceParameters("AdvancedSnip_HotkeyWindow")
            {
                Width = 1,
                Height = 1,
                PositionX = -32000,
                PositionY = -32000,
                WindowStyle = 0 // no WS_VISIBLE -> the window is never shown
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        }

        /// <summary>Registers a hotkey. Returns false if the combo is already taken globally.</summary>
        public bool TryRegister(HotkeyDef def, Action callback)
        {
            if (def == null || def.Key == 0) return false;

            uint mods = MOD_NOREPEAT;
            var m = (ModifierKeys)def.Modifiers;
            if (m.HasFlag(ModifierKeys.Alt)) mods |= MOD_ALT;
            if (m.HasFlag(ModifierKeys.Control)) mods |= MOD_CONTROL;
            if (m.HasFlag(ModifierKeys.Shift)) mods |= MOD_SHIFT;
            if (m.HasFlag(ModifierKeys.Windows)) mods |= MOD_WIN;

            uint vk = (uint)KeyInterop.VirtualKeyFromKey((Key)def.Key);
            if (vk == 0) return false;

            int id = ++_nextId;
            if (!RegisterHotKey(_source.Handle, id, mods, vk))
                return false;

            _callbacks[id] = callback;
            return true;
        }

        public void UnregisterAll()
        {
            foreach (var id in _callbacks.Keys)
                UnregisterHotKey(_source.Handle, id);
            _callbacks.Clear();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (_callbacks.TryGetValue(id, out var cb))
                {
                    handled = true;
                    cb();
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            UnregisterAll();
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
    }
}
