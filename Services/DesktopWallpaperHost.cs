using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using WinForms = System.Windows.Forms;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Hosts pinned screenshots directly on the desktop background layer behind Windows Explorer's desktop icons.
    /// Uses dedicated lightweight native host windows parented to WorkerW (Progman 0x052C message).
    /// PinnedClipWindow itself is never reparented to avoid DWM/WPF layered window composition bugs.
    /// </summary>
    public static class DesktopWallpaperHost
    {
        private const uint WM_SPAWN_WORKERW = 0x052C;
        private const uint SMTO_NORMAL = 0x0000;

        private static IntPtr _cachedWorkerW = IntPtr.Zero;
        private static readonly object _syncLock = new();
        private static readonly Dictionary<string, DesktopSlapHostForm> _activeSlaps = new();

        public static void Log(string msg)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdvancedSnip");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "slap.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
            }
            catch { }
        }

        /// <summary>
        /// Locates or triggers the WorkerW window that sits directly behind desktop icons.
        /// If no true WorkerW can be isolated (e.g. on modern Windows 11 24H2+ DirectComposition shell),
        /// returns IntPtr.Zero so the caller can seamlessly fall back to the Desktop Layer Strategy.
        /// </summary>
        public static IntPtr GetWorkerW()
        {
            lock (_syncLock)
            {
                if (_cachedWorkerW != IntPtr.Zero && Win32.IsWindow(_cachedWorkerW))
                {
                    return _cachedWorkerW;
                }

                IntPtr progman = Win32.FindWindow("Progman", null);
                Log($"GetWorkerW - Progman: 0x{progman.ToInt64():X}");
                if (progman == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                // Send 0x052C to Progman to instruct Explorer to split wallpaper and icons into separate WorkerW layers
                // Try standard 0,0 and legacy 0xD,1 parameters
                IntPtr res;
                Win32.SendMessageTimeout(progman, WM_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out res);
                Win32.SendMessageTimeout(progman, WM_SPAWN_WORKERW, (IntPtr)0x0000000D, (IntPtr)0x00000001, SMTO_NORMAL, 1000, out res);

                IntPtr workerW = IntPtr.Zero;

                // Enumerate top-level windows to locate the WorkerW behind icons
                Win32.EnumWindows((tophandle, lParam) =>
                {
                    var sb = new StringBuilder(256);
                    Win32.GetClassName(tophandle, sb, 256);
                    string cls = sb.ToString();

                    IntPtr shellView = Win32.FindWindowEx(tophandle, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (shellView != IntPtr.Zero)
                    {
                        // In classic Windows layout, WorkerW is the sibling immediately after the window containing SHELLDLL_DefView
                        IntPtr siblingWorker = Win32.FindWindowEx(IntPtr.Zero, tophandle, "WorkerW", null);
                        if (siblingWorker != IntPtr.Zero)
                        {
                            workerW = siblingWorker;
                            Log($"Found SHELLDLL_DefView in 0x{tophandle.ToInt64():X}, sibling WorkerW: 0x{workerW.ToInt64():X}");
                        }
                    }
                    else if (cls == "WorkerW")
                    {
                        // Candidate standalone WorkerW without SHELLDLL_DefView is the wallpaper host window
                        IntPtr childDef = Win32.FindWindowEx(tophandle, IntPtr.Zero, "SHELLDLL_DefView", null);
                        if (childDef == IntPtr.Zero && workerW == IntPtr.Zero)
                        {
                            workerW = tophandle;
                            Log($"Found candidate standalone WorkerW: 0x{tophandle.ToInt64():X}");
                        }
                    }
                    return true; // Continue enumeration across all windows
                }, IntPtr.Zero);

                // Progman itself must NEVER be used as WorkerW (parenting to Progman hides the window beneath SHELLDLL_DefView)
                if (workerW == progman)
                {
                    workerW = IntPtr.Zero;
                }

                if (workerW != IntPtr.Zero && Win32.IsWindow(workerW))
                {
                    _cachedWorkerW = workerW;

                    // Ensure WorkerW has WS_CLIPCHILDREN so Explorer's wallpaper redraw doesn't overwrite children
                    int style = Win32.GetWindowLong(workerW, Win32.GWL_STYLE);
                    if ((style & Win32.WS_CLIPCHILDREN) == 0)
                    {
                        Win32.SetWindowLong(workerW, Win32.GWL_STYLE, style | Win32.WS_CLIPCHILDREN);
                    }
                    Log($"Final GetWorkerW resolved to: 0x{_cachedWorkerW.ToInt64():X}");
                }
                else
                {
                    _cachedWorkerW = IntPtr.Zero;
                    Log("Final GetWorkerW: No WorkerW isolated. Will fall back to desktop layer strategy.");
                }

                return _cachedWorkerW;
            }
        }

        /// <summary>
        /// Slaps a screenshot onto the desktop background behind desktop icons.
        /// Creates a dedicated native host window parented to WorkerW.
        /// Returns false if WorkerW is not available so the caller can use the Desktop Layer Strategy.
        /// </summary>
        public static bool Slap(string id, Bitmap bitmap, float opacity, int x, int y, int width, int height)
        {
            Log($"Slap requested: id={id}, size={width}x{height} at ({x},{y}), bmp={bitmap?.Width}x{bitmap?.Height}");
            if (string.IsNullOrEmpty(id) || bitmap == null) return false;

            IntPtr workerW = GetWorkerW();
            if (workerW == IntPtr.Zero)
            {
                Log("Slap skipped native WorkerW host: workerW is IntPtr.Zero. Falling back to desktop layer strategy.");
                return false;
            }

            lock (_syncLock)
            {
                // Clean up any existing slap form with this id
                if (_activeSlaps.TryGetValue(id, out var existing))
                {
                    existing.Close();
                    existing.Dispose();
                    _activeSlaps.Remove(id);
                }

                var form = new DesktopSlapHostForm(bitmap, opacity, x, y, width, height, workerW);
                form.Show();

                // Explicitly position at HWND_BOTTOM inside WorkerW
                Win32.SetWindowPos(form.Handle, Win32.HWND_BOTTOM, x, y, width, height,
                    Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

                _activeSlaps[id] = form;
                Log($"Slap succeeded in WorkerW: form=0x{form.Handle.ToInt64():X}");
            }

            return true;
        }

        /// <summary>
        /// Removes the slapped desktop background host and invalidates the area on WorkerW.
        /// </summary>
        public static bool Unslap(string id, int x, int y, int width, int height)
        {
            Log($"Unslap requested: id={id}");
            if (string.IsNullOrEmpty(id)) return false;

            lock (_syncLock)
            {
                if (_activeSlaps.TryGetValue(id, out var form))
                {
                    form.Close();
                    form.Dispose();
                    _activeSlaps.Remove(id);
                    Log($"Unslap form closed: id={id}");
                }
            }

            // Invalidate the desktop wallpaper area so Windows Explorer repaints cleanly
            IntPtr workerW = _cachedWorkerW;
            if (workerW != IntPtr.Zero && Win32.IsWindow(workerW) && width > 0 && height > 0)
            {
                var rect = new Win32.RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
                Win32.InvalidateRect(workerW, ref rect, true);
                Win32.UpdateWindow(workerW);
            }

            return true;
        }

        /// <summary>
        /// Unslaps all active desktop wallpaper hosts (e.g. on application exit).
        /// </summary>
        public static void UnslapAll()
        {
            Log("UnslapAll called");
            lock (_syncLock)
            {
                foreach (var form in _activeSlaps.Values)
                {
                    try
                    {
                        form.Close();
                        form.Dispose();
                    }
                    catch
                    {
                    }
                }
                _activeSlaps.Clear();
            }

            IntPtr workerW = _cachedWorkerW;
            if (workerW != IntPtr.Zero && Win32.IsWindow(workerW))
            {
                Win32.InvalidateRect(workerW, IntPtr.Zero, true);
                Win32.UpdateWindow(workerW);
            }
        }

        /// <summary>
        /// Dedicated native WinForms host for rendering wallpaper screenshot behind desktop icons.
        /// Never receives mouse clicks (passes directly through to Explorer desktop icons and desktop background).
        /// </summary>
        private sealed class DesktopSlapHostForm : WinForms.Form
        {
            private readonly Bitmap _bitmap;
            private readonly float _opacity;
            private readonly IntPtr _parentWorkerW;
            private readonly int _targetX;
            private readonly int _targetY;
            private readonly int _targetW;
            private readonly int _targetH;

            public DesktopSlapHostForm(Bitmap bitmap, float opacity, int x, int y, int width, int height, IntPtr parentWorkerW)
            {
                _bitmap = bitmap;
                _opacity = Math.Clamp(opacity, 0.05f, 1.0f);
                _parentWorkerW = parentWorkerW;
                _targetX = x;
                _targetY = y;
                _targetW = Math.Max(1, width);
                _targetH = Math.Max(1, height);

                FormBorderStyle = WinForms.FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = WinForms.FormStartPosition.Manual;
                Location = new Point(x, y);
                Size = new Size(_targetW, _targetH);
                DoubleBuffered = true;
                BackColor = Color.FromArgb(18, 23, 34);

                // Rounded window region matching minimal mode
                try
                {
                    using var path = CreateRoundedRectanglePath(new Rectangle(0, 0, _targetW, _targetH), 12);
                    Region = new Region(path);
                }
                catch { }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                if (_parentWorkerW != IntPtr.Zero)
                {
                    // Clear WS_POPUP (0x80000000) so this becomes a proper WS_CHILD window inside WorkerW
                    int style = Win32.GetWindowLong(Handle, Win32.GWL_STYLE);
                    style = (style & ~unchecked((int)0x80000000)) | Win32.WS_CHILD | Win32.WS_CLIPSIBLINGS | Win32.WS_VISIBLE;
                    Win32.SetWindowLong(Handle, Win32.GWL_STYLE, style);

                    Win32.SetParent(Handle, _parentWorkerW);

                    Win32.SetWindowPos(Handle, Win32.HWND_BOTTOM, _targetX, _targetY, _targetW, _targetH,
                        Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

                    Log($"DesktopSlapHostForm OnHandleCreated: Handle=0x{Handle.ToInt64():X}, Parent=0x{_parentWorkerW.ToInt64():X}");
                }
            }

            protected override void OnPaintBackground(WinForms.PaintEventArgs e)
            {
                // Erase background suppression to prevent flicker
            }

            protected override void OnPaint(WinForms.PaintEventArgs e)
            {
                if (_bitmap == null) return;
                var g = e.Graphics;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                if (_opacity >= 0.99f)
                {
                    g.DrawImage(_bitmap, 0, 0, Width, Height);
                }
                else
                {
                    var matrix = new ColorMatrix
                    {
                        Matrix33 = _opacity
                    };
                    using var attrs = new ImageAttributes();
                    attrs.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                    g.DrawImage(_bitmap, new Rectangle(0, 0, Width, Height), 0, 0, _bitmap.Width, _bitmap.Height, GraphicsUnit.Pixel, attrs);
                }

                // Draw faint accent outline around the image just as in minimal mode
                try
                {
                    using var pen = new Pen(Color.FromArgb(180, 0, 120, 215), 1.5f);
                    using var path = CreateRoundedRectanglePath(new Rectangle(0, 0, Width - 1, Height - 1), 12);
                    g.DrawPath(pen, path);
                }
                catch { }
            }

            private static GraphicsPath CreateRoundedRectanglePath(Rectangle rect, int radius)
            {
                var path = new GraphicsPath();
                int d = radius * 2;
                if (d > rect.Width) d = rect.Width;
                if (d > rect.Height) d = rect.Height;

                path.AddArc(rect.X, rect.Y, d, d, 180, 90);
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }

            protected override void WndProc(ref WinForms.Message m)
            {
                const int WM_NCHITTEST = 0x0084;
                const int HTTRANSPARENT = -1;
                if (m.Msg == WM_NCHITTEST)
                {
                    m.Result = (IntPtr)HTTRANSPARENT;
                    return;
                }
                base.WndProc(ref m);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _bitmap?.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
