using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// The tray presence: a NotifyIcon with a right-click menu. It shows the application's
    /// own icon, so the thing in the tray is recognisably the thing on the taskbar and in
    /// the Start menu. Uses only WinForms + System.Drawing to sidestep WPF name clashes;
    /// talks to the app via events.
    ///
    /// Note that this file imports <c>System.Drawing</c>, so <c>Font</c>, <c>Color</c> and
    /// <c>Brush</c> here are the GDI+ ones. Nothing WPF may be referenced unqualified.
    /// </summary>
    public sealed class TrayIconManager : IDisposable
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr handle);

        private readonly WinForms.NotifyIcon _icon;
        private readonly WinForms.ContextMenuStrip _menu;
        private bool _disposed;

        /// <summary>Open the main window. The default action, and what a double-click does.</summary>
        public event EventHandler? OpenRequested;

        public event EventHandler? SnipRequested;
        public event EventHandler? OcrRequested;
        public event EventHandler? SettingsRequested;
        public event EventHandler? ExitRequested;

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
        /// <summary>
        /// What the last notification was about, so a click on it knows what to open.
        ///
        /// Settable from outside since v2.35: capture receipts are drawn by
        /// <c>ToastWindow</c> now (the shell will not put a balloon on anything but the
        /// primary monitor), but they still route their click through the same
        /// <c>OnBalloonClicked</c>, so the tag has to be set by whoever raised the message.
        /// </summary>
        public string? BalloonTag { get; set; }

        /// <summary>Start or stop a screen recording.</summary>
        public event EventHandler? RecordRequested;

        /// <summary>Slap all open pinned windows to the desktop background.</summary>
        public event EventHandler? SlapAllRequested;

        /// <summary>Restore all slapped pinned windows back to interactive floating windows.</summary>
        public event EventHandler? UnslapAllRequested;

        private readonly WinForms.ToolStripMenuItem _snipItem;
        private readonly WinForms.ToolStripMenuItem _ocrItem;
        private readonly WinForms.ToolStripMenuItem _recordItem;
        private readonly WinForms.ToolStripMenuItem _slapAllItem;
        private readonly WinForms.ToolStripMenuItem _unslapAllItem;
        private readonly WinForms.ToolStripSeparator _pinSeparator;
        private Font? _boldFont;

        /// <summary>
        /// Builds the menu.
        ///
        /// It is shorter than it was, and the cut follows one rule: **the tray holds the
        /// way in, the way out, and the things that cannot wait for a window to open.**
        /// Since the main window grew a row of quick actions, scroll capture, clipboard
        /// history and "edit an image" are all one click away once it is up, and all three
        /// still have their global hotkeys — so a flat list of seven unrelated commands was
        /// paying menu space for doors that already exist elsewhere.
        ///
        /// Snip and Record stay because they are the two that are urgent: whatever you
        /// wanted to capture may not still be on screen by the time a window has opened.
        ///
        /// Each carries the user's *configured* hotkey rather than a hardcoded default, so
        /// the menu teaches the shortcut that will actually work on this machine.
        /// </summary>
        public TrayIconManager()
        {
            _menu = new WinForms.ContextMenuStrip();

            var openItem = new WinForms.ToolStripMenuItem("Open Advanced Snip",
                null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));

            // Bold marks the default action, which is the convention Windows uses to say
            // "this is what double-clicking does". Without it the double-click behaviour is
            // undiscoverable — you either know it or you don't.
            _boldFont = new Font(_menu.Font, FontStyle.Bold);
            openItem.Font = _boldFont;

            _snipItem = new WinForms.ToolStripMenuItem("Snip now",
                null, (_, _) => SnipRequested?.Invoke(this, EventArgs.Empty));

            _ocrItem = new WinForms.ToolStripMenuItem("Extract text (OCR)",
                null, (_, _) => OcrRequested?.Invoke(this, EventArgs.Empty));

            _recordItem = new WinForms.ToolStripMenuItem("Record the screen",
                null, (_, _) => RecordRequested?.Invoke(this, EventArgs.Empty));

            _slapAllItem = new WinForms.ToolStripMenuItem("Slap all pins to desktop",
                null, (_, _) => SlapAllRequested?.Invoke(this, EventArgs.Empty));

            _unslapAllItem = new WinForms.ToolStripMenuItem("Restore all slapped pins",
                null, (_, _) => UnslapAllRequested?.Invoke(this, EventArgs.Empty));

            _pinSeparator = new WinForms.ToolStripSeparator();

            _menu.Items.Add(openItem);
            _menu.Items.Add(new WinForms.ToolStripSeparator());
            _menu.Items.Add(_snipItem);
            _menu.Items.Add(_ocrItem);
            _menu.Items.Add(_recordItem);
            _menu.Items.Add(_pinSeparator);
            _menu.Items.Add(_slapAllItem);
            _menu.Items.Add(_unslapAllItem);
            _menu.Items.Add(new WinForms.ToolStripSeparator());
            _menu.Items.Add(new WinForms.ToolStripMenuItem("Settings\u2026",
                null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty)));
            _menu.Items.Add(new WinForms.ToolStripMenuItem("Exit",
                null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));

            _menu.Opening += (_, _) =>
            {
                var windows = PinnedWindowManager.ActiveWindows;
                bool anySlapped = windows.Any(w => w.IsSlappedToDesktop);
                bool anyFloating = windows.Any(w => !w.IsSlappedToDesktop);

                _slapAllItem.Visible = anyFloating;
                _unslapAllItem.Visible = anySlapped;
                _pinSeparator.Visible = anyFloating || anySlapped;
            };

            UpdateTheme();
            ThemeManager.ThemeChanged += (_, _) => UpdateTheme();

            _icon = new WinForms.NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "Advanced Snip",
                Visible = false,
                ContextMenuStrip = _menu
            };

            // Double-click opens the window rather than taking a snip. Taking a capture is
            // a destructive-ish, immediate thing to do on a gesture people trigger by
            // accident, and every other tray application in Windows opens on double-click —
            // so the gesture already means "show me the app" before this app has any say.
            _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

            _icon.BalloonTipClicked += (_, _) => BalloonClicked?.Invoke(this, EventArgs.Empty);
            _icon.BalloonTipClosed  += (_, _) => BalloonTag = null;
        }

        private void UpdateTheme()
        {
            try
            {
                bool isDark = ThemeManager.IsDark;
                var colorTable = new ThemedColorTable(isDark);
                _menu.Renderer = new ThemedMenuRenderer(colorTable);
                _menu.BackColor = colorTable.ToolStripDropDownBackground;
                _menu.ForeColor = colorTable.MenuText;
                foreach (WinForms.ToolStripItem item in _menu.Items)
                {
                    item.ForeColor = colorTable.MenuText;
                }
            }
            catch { }
        }

        /// <summary>
        /// Puts the live hotkeys beside the menu items. Setting
        /// <c>ShortcutKeyDisplayString</c> without a real <c>ShortcutKeys</c> is how a
        /// menu displays a shortcut it does not itself own — these are global hotkeys
        /// registered elsewhere, and letting the menu claim them would fight the
        /// registration.
        /// </summary>
        public void SetShortcuts(string? snip, string? record, string? ocr = null)
        {
            _snipItem.ShortcutKeyDisplayString = snip ?? "";
            _recordItem.ShortcutKeyDisplayString = record ?? "";
            if (_ocrItem != null)
                _ocrItem.ShortcutKeyDisplayString = ocr ?? "";
        }

        /// <summary>
        /// Switches the record entry between starting and stopping. The recording can be
        /// ended from the floating toolbar or a hotkey, so this follows the app's state
        /// rather than remembering what the menu last did.
        ///
        /// Guarded because it is called from the recording teardown path. Both callers are
        /// on the UI thread today, and a WinForms item touched from anywhere else throws —
        /// a cosmetic menu label must never be the reason a recording fails to finish
        /// cleanly.
        /// </summary>
        public void SetRecording(bool running)
        {
            try { _recordItem.Text = running ? "Stop recording" : "Record the screen"; }
            catch { }
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

        /// <summary>
        /// The application's own icon, at the size the tray actually wants.
        ///
        /// Read from the packed <c>ASico.ico</c> resource rather than extracted from the
        /// executable: an .ico holds several sizes, and handing the stream to
        /// <see cref="Icon"/> with a target size lets it pick the right frame instead of
        /// scaling whichever one it found. <c>SmallIconSize</c> rather than a hardcoded
        /// 16×16, because that is 16 at 100% scaling and 24 or 32 further up — a fixed
        /// size is the usual reason a tray icon looks soft on a high-DPI display.
        /// ASico.ico carries 16, 24, 32, 48, 64, 128 and 256, so every one of those lands
        /// on a real frame rather than a resample. Its frames are PNG-compressed, which
        /// Windows has understood since Vista and the icon APIs behind this handle
        /// directly — worth knowing before anyone "fixes" the icon by flattening it to BMP.
        ///
        /// Falls back to the drawn icon if anything goes wrong. A tray application whose
        /// icon fails to load is one the user cannot get back to, so this is the one place
        /// in the class where a silent catch is the right answer rather than a lazy one.
        /// </summary>
        private static Icon LoadAppIcon()
        {
            try
            {
                var uri = new Uri("pack://application:,,,/ASico.ico", UriKind.Absolute);
                var info = System.Windows.Application.GetResourceStream(uri);

                if (info?.Stream != null)
                {
                    using var stream = info.Stream;
                    return new Icon(stream, WinForms.SystemInformation.SmallIconSize);
                }
            }
            catch { }

            return BuildIcon();
        }

        /// <summary>
        /// The old runtime-drawn mark, kept as the fallback for
        /// <see cref="LoadAppIcon"/>. Not dead code — it is what stands between a missing
        /// resource and an application with no way back into it.
        /// </summary>
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
            _boldFont?.Dispose();
            _boldFont = null;
        }

        private sealed class ThemedColorTable : WinForms.ProfessionalColorTable
        {
            public Color MenuBackground { get; }
            public Color MenuBorderColor { get; }
            public Color MenuItemHover { get; }
            public Color MenuItemHoverBorder { get; }
            public Color MenuItemPressed { get; }
            public Color MenuText { get; }
            public Color ShortcutText { get; }
            public Color SeparatorLine { get; }

            public ThemedColorTable(bool isDark)
            {
                UseSystemColors = false;

                // Extract colors live from active theme resources
                var app = System.Windows.Application.Current;
                if (app != null)
                {
                    MenuBackground      = ToDrawingColor(app.TryFindResource("Brush.Card"), isDark ? Color.FromArgb(20, 24, 34) : Color.FromArgb(255, 255, 255));
                    MenuBorderColor     = ToDrawingColor(app.TryFindResource("Brush.BorderStrong"), isDark ? Color.FromArgb(60, 75, 105) : Color.FromArgb(203, 213, 225));
                    MenuItemHover       = ToDrawingColor(app.TryFindResource("Brush.Hover"), isDark ? Color.FromArgb(56, 69, 91) : Color.FromArgb(224, 235, 250));
                    MenuItemHoverBorder = ToDrawingColor(app.TryFindResource("Brush.BorderStrong"), isDark ? Color.FromArgb(91, 99, 113) : Color.FromArgb(180, 200, 230));
                    MenuItemPressed     = ToDrawingColor(app.TryFindResource("Brush.AccentSoft"), isDark ? Color.FromArgb(30, 53, 85) : Color.FromArgb(191, 219, 254));
                    MenuText            = ToDrawingColor(app.TryFindResource("Brush.TextHeading"), isDark ? Color.FromArgb(248, 250, 252) : Color.FromArgb(15, 23, 42));
                    ShortcutText        = ToDrawingColor(app.TryFindResource("Brush.TextMuted"), isDark ? Color.FromArgb(148, 163, 184) : Color.FromArgb(100, 116, 139));
                    SeparatorLine       = ToDrawingColor(app.TryFindResource("Brush.Border"), isDark ? Color.FromArgb(45, 55, 75) : Color.FromArgb(226, 232, 240));
                }
                else
                {
                    MenuBackground      = isDark ? Color.FromArgb(20, 24, 34) : Color.FromArgb(255, 255, 255);
                    MenuBorderColor     = isDark ? Color.FromArgb(60, 75, 105) : Color.FromArgb(203, 213, 225);
                    MenuItemHover       = isDark ? Color.FromArgb(56, 69, 91) : Color.FromArgb(224, 235, 250);
                    MenuItemHoverBorder = isDark ? Color.FromArgb(91, 99, 113) : Color.FromArgb(180, 200, 230);
                    MenuItemPressed     = isDark ? Color.FromArgb(30, 53, 85) : Color.FromArgb(191, 219, 254);
                    MenuText            = isDark ? Color.FromArgb(248, 250, 252) : Color.FromArgb(15, 23, 42);
                    ShortcutText        = isDark ? Color.FromArgb(148, 163, 184) : Color.FromArgb(100, 116, 139);
                    SeparatorLine       = isDark ? Color.FromArgb(45, 55, 75) : Color.FromArgb(226, 232, 240);
                }
            }

            private static Color ToDrawingColor(object? resource, Color fallback)
            {
                if (resource is System.Windows.Media.SolidColorBrush scb)
                    return Color.FromArgb(scb.Color.A, scb.Color.R, scb.Color.G, scb.Color.B);
                if (resource is System.Windows.Media.GradientBrush gb && gb.GradientStops.Count > 0)
                {
                    var c = gb.GradientStops[0].Color;
                    return Color.FromArgb(c.A, c.R, c.G, c.B);
                }
                return fallback;
            }

            public override Color ToolStripDropDownBackground => MenuBackground;
            public override Color MenuBorder => MenuBorderColor;
            public override Color MenuItemBorder => Color.Transparent;
            public override Color MenuItemSelected => MenuItemHover;
            public override Color MenuItemSelectedGradientBegin => MenuItemHover;
            public override Color MenuItemSelectedGradientEnd => MenuItemHover;
            public override Color MenuItemPressedGradientBegin => MenuItemPressed;
            public override Color MenuItemPressedGradientEnd => MenuItemPressed;
            public override Color ImageMarginGradientBegin => MenuBackground;
            public override Color ImageMarginGradientMiddle => MenuBackground;
            public override Color ImageMarginGradientEnd => MenuBackground;
            public override Color SeparatorDark => SeparatorLine;
            public override Color SeparatorLight => Color.Transparent;
        }

        private sealed class ThemedMenuRenderer : WinForms.ToolStripProfessionalRenderer
        {
            private readonly ThemedColorTable _table;

            public ThemedMenuRenderer(ThemedColorTable table) : base(table)
            {
                _table = table;
                RoundedEdges = true;
            }

            protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = _table.MenuText;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
            {
                if (e.Item.Selected || e.Item.Pressed)
                {
                    var rect = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
                    using var brush = new SolidBrush(e.Item.Pressed ? _table.MenuItemPressed : _table.MenuItemHover);
                    using var borderPen = new Pen(_table.MenuItemHoverBorder, 1);
                    using var path = GetRoundedRect(rect, 5);
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.FillPath(brush, path);
                    e.Graphics.DrawPath(borderPen, path);
                }
            }

            protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using var pen = new Pen(_table.SeparatorDark, 1);
                e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
            }

            protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
            {
                using var pen = new Pen(_table.MenuBorder, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }

            private static GraphicsPath GetRoundedRect(Rectangle bounds, int radius)
            {
                int diameter = radius * 2;
                var path = new GraphicsPath();
                path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
                path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
                path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                return path;
            }
        }
    }
}
