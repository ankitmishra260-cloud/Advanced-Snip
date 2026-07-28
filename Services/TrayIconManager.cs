using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// The tray presence: a NotifyIcon with a right-click menu. The icon is drawn at
    /// runtime so the project ships without any binary asset. Uses only WinForms +
    /// System.Drawing to sidestep WPF name clashes; talks to the app via events.
    /// </summary>
    public sealed class TrayIconManager : IDisposable
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr handle);

        private readonly WinForms.NotifyIcon _icon;
        private bool _disposed;

        public event EventHandler? SnipRequested;
        public event EventHandler? ScrollCaptureRequested;
        public event EventHandler? HistoryRequested;
        public event EventHandler? SettingsRequested;
        public event EventHandler? ExitRequested;
        public event EventHandler? EditImageRequested;

        /// <summary>
        /// Raised when the user clicks the "snip saved" notification itself. On Windows 10
        /// and 11 that balloon is surfaced as a toast, and clicking the toast body — not
        /// just the tray icon — is what fires this.
        /// </summary>
        public event EventHandler? BalloonClicked;

        /// <summary>
        /// Which notification is currently on screen. A balloon click carries no payload,
        /// so the tag tells the app whether the click means "open the capture I just
        /// saved" or refers to something else entirely, like a hotkey warning.
        /// </summary>
        public string? BalloonTag { get; private set; }

        public TrayIconManager()
        {
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Snip now",              null, (_, _) => SnipRequested?.Invoke(this, EventArgs.Empty));
            menu.Items.Add("Scroll capture window", null, (_, _) => ScrollCaptureRequested?.Invoke(this, EventArgs.Empty));
            menu.Items.Add("Clipboard history",     null, (_, _) => HistoryRequested?.Invoke(this, EventArgs.Empty));
            menu.Items.Add("Edit an image\u2026",    null, (_, _) => EditImageRequested?.Invoke(this, EventArgs.Empty));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Settings\u2026", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
            menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

            _icon = new WinForms.NotifyIcon
            {
                Icon = BuildIcon(),
                Text = "Advanced Snip",
                Visible = false,
                ContextMenuStrip = menu
            };

            // Double-clicking the tray icon takes a snip.
            _icon.DoubleClick += (_, _) => SnipRequested?.Invoke(this, EventArgs.Empty);

            _icon.BalloonTipClicked += (_, _) => BalloonClicked?.Invoke(this, EventArgs.Empty);
            _icon.BalloonTipClosed  += (_, _) => BalloonTag = null;
        }

        public void Show() => _icon.Visible = true;

        public void ShowBalloon(string title, string text, string? tag = null)
        {
            BalloonTag = tag;
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;

            // The timeout argument has been ignored since Vista — the OS decides how long
            // a notification stays up. Passing a value only documents the intent.
            _icon.ShowBalloonTip(3000);
        }

        private static Icon BuildIcon()
        {
            using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.Clear(Color.Transparent);

                using var path = RoundedRect(new RectangleF(2, 2, 28, 28), 7f);
                using var fill = new SolidBrush(Color.FromArgb(59, 130, 246)); // blue
                g.FillPath(fill, path);

                using var font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var text = new SolidBrush(Color.White);
                using var fmt = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString("S", font, text, new RectangleF(2, 1, 28, 28), fmt);
            }

            IntPtr hIcon = bmp.GetHicon();
            try
            {
                using var tmp = Icon.FromHandle(hIcon);
                return (Icon)tmp.Clone(); // independent icon; safe after DestroyIcon
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _icon.Visible = false;
            _icon.Icon?.Dispose();
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
        }
    }
}
