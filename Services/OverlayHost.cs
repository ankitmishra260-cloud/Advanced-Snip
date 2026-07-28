using System;
using System.Windows;
using System.Windows.Media;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Turns a WPF window into a pixel-exact overlay covering every monitor.
    ///
    /// The problem this solves: WPF lays out in device-independent units, and under
    /// per-monitor DPI awareness the conversion factor depends on which display a window
    /// is on. An overlay that spans a 150%-scaled laptop screen and a 100% external
    /// monitor has no single conversion factor, so selection rectangles drift and the
    /// captured crop doesn't match what was highlighted.
    ///
    /// The fix is to stop converting. We position the window in physical pixels with
    /// SetWindowPos, then apply the inverse of whatever scale WPF assigned so that one
    /// layout unit inside the overlay is exactly one physical pixel — everywhere, on
    /// every display. Mouse coordinates read straight out of the root element are then
    /// already in screen pixels, and the crop is pixel-perfect on any mix of displays.
    /// </summary>
    internal static class OverlayHost
    {
        /// <summary>
        /// Call from the window's constructor. Handles initial placement, the inverse
        /// scale transform, and re-applying it if the effective DPI changes.
        /// </summary>
        internal static void MakeVirtualScreenOverlay(Window window, FrameworkElement root)
        {
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.ShowInTaskbar = false;
            window.Topmost = true;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.SizeToContent = SizeToContent.Manual;

            root.HorizontalAlignment = HorizontalAlignment.Left;
            root.VerticalAlignment = VerticalAlignment.Top;

            void Apply()
            {
                var vs = DisplayInfo.VirtualScreen;

                // Physical placement first — this is the authoritative geometry.
                WindowPlacement.SetBounds(window, vs, topMost: true);

                double scale = 1.0;
                var source = PresentationSource.FromVisual(window);
                if (source?.CompositionTarget != null)
                {
                    double m11 = source.CompositionTarget.TransformToDevice.M11;
                    if (m11 > 0.01) scale = m11;
                }

                root.LayoutTransform = Math.Abs(scale - 1.0) < 0.001
                    ? Transform.Identity
                    : new ScaleTransform(1.0 / scale, 1.0 / scale);

                root.Width = vs.Width;
                root.Height = vs.Height;
            }

            window.SourceInitialized += (_, _) =>
            {
                // Keep the overlay out of Alt+Tab.
                IntPtr h = WindowPlacement.HandleOf(window);
                if (h != IntPtr.Zero)
                {
                    int ex = Win32.GetWindowLong(h, Win32.GWL_EXSTYLE);
                    Win32.SetWindowLong(h, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_TOOLWINDOW);
                }
                Apply();
            };

            window.DpiChanged += (_, _) => Apply();
            window.Loaded += (_, _) => Apply();
        }

        /// <summary>Overlay-local coordinates to absolute screen pixels.</summary>
        internal static System.Drawing.Point ToScreen(System.Windows.Point local)
        {
            var vs = DisplayInfo.VirtualScreen;
            return new System.Drawing.Point((int)Math.Round(local.X) + vs.X,
                                            (int)Math.Round(local.Y) + vs.Y);
        }

        /// <summary>Absolute screen pixels to overlay-local coordinates.</summary>
        internal static System.Windows.Point ToLocal(int screenX, int screenY)
        {
            var vs = DisplayInfo.VirtualScreen;
            return new System.Windows.Point(screenX - vs.X, screenY - vs.Y);
        }

        internal static System.Windows.Rect ToLocalRect(System.Drawing.Rectangle screenRect)
        {
            var vs = DisplayInfo.VirtualScreen;
            return new System.Windows.Rect(screenRect.X - vs.X, screenRect.Y - vs.Y,
                                           Math.Max(0, screenRect.Width), Math.Max(0, screenRect.Height));
        }
    }
}
