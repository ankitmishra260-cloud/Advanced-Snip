using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AdvancedSnip.Services;

namespace AdvancedSnip
{
    public partial class OcrResultWindow : Window
    {
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public Win32.POINT ptReserved;
            public Win32.POINT ptMaxSize;
            public Win32.POINT ptMaxPosition;
            public Win32.POINT ptMinTrackSize;
            public Win32.POINT ptMaxTrackSize;
        }

        private bool _isPinned = false;
        private bool _isMonospace = false;
        private readonly Rectangle _sourceRegion;
        private string? _detectedUrl;

        public event EventHandler? OcrAgainRequested;

        private static readonly System.Windows.Media.FontFamily StandardFont = new("Segoe UI Variable, Segoe UI, sans-serif");
        private static readonly System.Windows.Media.FontFamily MonoFont = new("Cascadia Code, Consolas, Courier New, monospace");

        public OcrResultWindow(string text, Rectangle sourceRegion)
        {
            InitializeComponent();
            _sourceRegion = sourceRegion;
            OcrTextBox.Text = text ?? "";

            UpdateCountsAndLinks();

            SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    HwndSource.FromHwnd(handle)?.AddHook(WndProc);
                }
            };

            Loaded += (_, _) =>
            {
                PositionWindow();
                OcrTextBox.Focus();
                OcrTextBox.SelectAll();
            };

            KeyDown += OnKeyDown;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_NCHITTEST)
            {
                int screenX = (short)(lParam.ToInt32() & 0xFFFF);
                int screenY = (short)((lParam.ToInt32() >> 16) & 0xFFFF);

                var p = PointFromScreen(new System.Windows.Point(screenX, screenY));
                double w = ActualWidth;
                double h = ActualHeight;
                const double resizeBorder = 12.0;

                // Corners
                if (p.X <= resizeBorder && p.Y <= resizeBorder)
                {
                    handled = true;
                    return (IntPtr)HTTOPLEFT;
                }
                if (p.X >= w - resizeBorder && p.Y <= resizeBorder)
                {
                    handled = true;
                    return (IntPtr)HTTOPRIGHT;
                }
                if (p.X <= resizeBorder && p.Y >= h - resizeBorder)
                {
                    handled = true;
                    return (IntPtr)HTBOTTOMLEFT;
                }
                if (p.X >= w - resizeBorder && p.Y >= h - resizeBorder)
                {
                    handled = true;
                    return (IntPtr)HTBOTTOMRIGHT;
                }

                // Edges
                if (p.X <= resizeBorder)
                {
                    handled = true;
                    return (IntPtr)HTLEFT;
                }
                if (p.X >= w - resizeBorder)
                {
                    handled = true;
                    return (IntPtr)HTRIGHT;
                }
                if (p.Y <= resizeBorder)
                {
                    handled = true;
                    return (IntPtr)HTTOP;
                }
                if (p.Y >= h - resizeBorder)
                {
                    handled = true;
                    return (IntPtr)HTBOTTOM;
                }
            }
            else if (msg == WM_GETMINMAXINFO)
            {
                try
                {
                    var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    var monitor = DisplayInfo.FromWindow(hwnd);
                    double scale = monitor.Scale <= 0 ? 1.0 : monitor.Scale;

                    if (MinWidth > 0) info.ptMinTrackSize.X = (int)(MinWidth * scale);
                    if (MinHeight > 0) info.ptMinTrackSize.Y = (int)(MinHeight * scale);

                    Marshal.StructureToPtr(info, lParam, true);
                    handled = true;
                }
                catch { }
            }

            return IntPtr.Zero;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
            else if (e.Key == Key.F5 || (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control))
            {
                OcrAgain_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                FormatSingleLine_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Copy_Click(this, new RoutedEventArgs());
                Close();
                e.Handled = true;
            }
        }

        private void PositionWindow()
        {
            try
            {
                var mon = _sourceRegion.Width > 0 && _sourceRegion.Height > 0
                    ? DisplayInfo.FromRect(_sourceRegion)
                    : DisplayInfo.FromCursor();

                double scale = mon.Scale > 0 ? mon.Scale : 1.0;
                var wa = mon.WorkArea;

                double w = Width;
                double h = Height;

                double left, top;

                if (_sourceRegion.Width > 0 && _sourceRegion.Height > 0)
                {
                    double selRight = _sourceRegion.Right / scale;
                    double selTop = _sourceRegion.Top / scale;
                    double selBottom = _sourceRegion.Bottom / scale;

                    double waLeft = wa.Left / scale;
                    double waTop = wa.Top / scale;
                    double waRight = wa.Right / scale;
                    double waBottom = wa.Bottom / scale;

                    if (selRight + w + 16 <= waRight)
                    {
                        left = selRight + 12;
                        top = Math.Clamp(selTop, waTop + 10, waBottom - h - 10);
                    }
                    else if (selBottom + h + 16 <= waBottom)
                    {
                        left = Math.Clamp(_sourceRegion.Left / scale, waLeft + 10, waRight - w - 10);
                        top = selBottom + 12;
                    }
                    else
                    {
                        left = Math.Max(waLeft + 20, (waLeft + waRight - w) / 2);
                        top = Math.Max(waTop + 20, (waTop + waBottom - h) / 2);
                    }
                }
                else
                {
                    left = (wa.Left + wa.Right) / (2 * scale) - w / 2;
                    top = (wa.Top + wa.Bottom) / (2 * scale) - h / 2;
                }

                Left = left;
                Top = top;
            }
            catch
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        private void UpdateCountsAndLinks()
        {
            string t = OcrTextBox.Text ?? "";
            int chars = t.Length;
            int words = string.IsNullOrWhiteSpace(t) ? 0 : t.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;

            if (CountText != null)
                CountText.Text = words == 1 ? "1 word" : $"{words} words · {chars} chars";

            // Check for URL
            _detectedUrl = FindFirstUrl(t);
            if (OpenLinkBtn != null)
            {
                if (!string.IsNullOrEmpty(_detectedUrl))
                {
                    OpenLinkBtn.Visibility = Visibility.Visible;
                    OpenLinkBtn.ToolTip = $"Open {_detectedUrl} in default browser";
                }
                else
                {
                    OpenLinkBtn.Visibility = Visibility.Collapsed;
                }
            }
        }

        private static string? FindFirstUrl(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var match = Regex.Match(text, @"(https?:\/\/[^\s]+|www\.[^\s]+|[a-zA-Z0-9.-]+\.(com|org|net|edu|gov|io|ai|dev|co|in|uk|app)\b[^\s]*)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string url = match.Value.TrimEnd('.', ',', ')', ']', ';', ':', '"', '\'');
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url;
                }
                return url;
            }
            return null;
        }

        private void OcrTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateCountsAndLinks();
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void OcrAgain_Click(object sender, RoutedEventArgs e)
        {
            Close();
            OcrAgainRequested?.Invoke(this, EventArgs.Empty);
        }

        private void MonoToggle_Click(object sender, RoutedEventArgs e)
        {
            _isMonospace = !_isMonospace;
            OcrTextBox.FontFamily = _isMonospace ? MonoFont : StandardFont;
            MonoToggleBtn.Content = _isMonospace ? "</> Prose" : "</> Monospace";
        }

        private void PinBtn_Click(object sender, RoutedEventArgs e)
        {
            _isPinned = !_isPinned;
            Topmost = _isPinned;
            PinBtn.Content = _isPinned ? "\uE77A" : "\uE718";
            PinBtn.ToolTip = _isPinned ? "Unpin window (currently pinned on top)" : "Pin window (keep on top)";
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            string text = OcrTextBox.SelectedText.Length > 0 ? OcrTextBox.SelectedText : OcrTextBox.Text;
            if (!string.IsNullOrEmpty(text))
            {
                Clipboard.SetDataObject(text, true);
                ShowNotice("✓ Copied to clipboard");
            }
        }

        private void FormatSingleLine_Click(object sender, RoutedEventArgs e)
        {
            string raw = OcrTextBox.Text ?? "";
            string singleLine = string.Join(" ", raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                                   .Select(s => s.Trim())
                                                   .Where(s => !string.IsNullOrEmpty(s)));

            if (!string.IsNullOrEmpty(singleLine))
            {
                OcrTextBox.Text = singleLine;
                Clipboard.SetDataObject(singleLine, true);
                ShowNotice("✓ Copied single line");
            }
        }

        private void FormatNoSpaces_Click(object sender, RoutedEventArgs e)
        {
            string raw = OcrTextBox.Text ?? "";
            string noSpaces = Regex.Replace(raw, @"\s+", "");

            if (!string.IsNullOrEmpty(noSpaces))
            {
                OcrTextBox.Text = noSpaces;
                Clipboard.SetDataObject(noSpaces, true);
                ShowNotice("✓ Copied (no spaces)");
            }
        }

        private void FormatUpper_Click(object sender, RoutedEventArgs e)
        {
            string raw = OcrTextBox.Text ?? "";
            if (!string.IsNullOrEmpty(raw))
            {
                string upper = raw.ToUpperInvariant();
                OcrTextBox.Text = upper;
                Clipboard.SetDataObject(upper, true);
                ShowNotice("✓ Converted to UPPERCASE");
            }
        }

        private void FormatLower_Click(object sender, RoutedEventArgs e)
        {
            string raw = OcrTextBox.Text ?? "";
            if (!string.IsNullOrEmpty(raw))
            {
                string lower = raw.ToLowerInvariant();
                OcrTextBox.Text = lower;
                Clipboard.SetDataObject(lower, true);
                ShowNotice("✓ Converted to lowercase");
            }
        }

        private void FormatTitle_Click(object sender, RoutedEventArgs e)
        {
            string raw = OcrTextBox.Text ?? "";
            if (!string.IsNullOrEmpty(raw))
            {
                var textInfo = CultureInfo.CurrentCulture.TextInfo;
                string title = textInfo.ToTitleCase(raw.ToLower());
                OcrTextBox.Text = title;
                Clipboard.SetDataObject(title, true);
                ShowNotice("✓ Converted to Title Case");
            }
        }

        private void OpenLink_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_detectedUrl))
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = _detectedUrl, UseShellExecute = true });
                    ShowNotice("↗ Opening link…");
                }
                catch { }
            }
        }

        private void SearchWeb_Click(object sender, RoutedEventArgs e)
        {
            string query = (OcrTextBox.SelectedText.Length > 0 ? OcrTextBox.SelectedText : OcrTextBox.Text).Trim();
            if (string.IsNullOrWhiteSpace(query)) return;

            try
            {
                string url = "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch { }
        }

        private void Translate_Click(object sender, RoutedEventArgs e)
        {
            string query = (OcrTextBox.SelectedText.Length > 0 ? OcrTextBox.SelectedText : OcrTextBox.Text).Trim();
            if (string.IsNullOrWhiteSpace(query)) return;

            try
            {
                string url = "https://translate.google.com/?sl=auto&tl=en&text=" + Uri.EscapeDataString(query);
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch { }
        }

        private void ShowNotice(string message)
        {
            if (StatusNotice != null)
            {
                StatusNotice.Text = message;
                StatusNotice.Visibility = Visibility.Visible;
            }
        }
    }
}
