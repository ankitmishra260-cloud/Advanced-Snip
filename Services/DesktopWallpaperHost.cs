using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Composites pinned screenshots directly onto the Windows desktop wallpaper layer.
    /// This guarantees that slapped images sit:
    ///   1. Physically and natively BEHIND all desktop icons (SysListView32/FolderView).
    ///   2. Above the desktop wallpaper background.
    ///   3. 100% interactive for all desktop icons on top (clickable, draggable, selectable).
    ///   4. Fully functional across all Windows versions including modern Windows 11 24H2+.
    /// </summary>
    public static class DesktopWallpaperHost
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int SystemParametersInfo(uint uAction, uint uParam, string lpvParam, uint fuWinIni);

        private const uint SPI_GETDESKWALLPAPER = 0x0073;
        private const uint SPI_SETDESKWALLPAPER = 0x0014;
        private const uint SPIF_SENDCHANGE = 0x02;

        private sealed class SlapEntry
        {
            public Bitmap Bitmap { get; set; } = null!;
            public float Opacity { get; set; }
            public Rectangle TargetRect { get; set; }
        }

        private static readonly object _syncLock = new();
        private static readonly Dictionary<string, SlapEntry> _activeSlaps = new();
        private static string? _originalWallpaperPath = null;
        private static string? _backupWallpaperCopy = null;

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
        /// Slaps a screenshot onto the desktop wallpaper behind all desktop icons.
        /// </summary>
        public static bool Slap(string id, Bitmap bitmap, float opacity, int x, int y, int width, int height)
        {
            Log($"Slap requested: id={id}, size={width}x{height} at ({x},{y}), bmp={bitmap?.Width}x{bitmap?.Height}");
            if (string.IsNullOrEmpty(id) || bitmap == null) return false;

            lock (_syncLock)
            {
                try
                {
                    // Ensure the original wallpaper is identified and backed up
                    if (string.IsNullOrEmpty(_originalWallpaperPath))
                    {
                        _originalWallpaperPath = ResolveCurrentWallpaperPath();
                        Log($"Original wallpaper resolved to: {_originalWallpaperPath}");

                        if (!string.IsNullOrEmpty(_originalWallpaperPath) && File.Exists(_originalWallpaperPath))
                        {
                            try
                            {
                                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdvancedSnip");
                                Directory.CreateDirectory(dir);
                                _backupWallpaperCopy = Path.Combine(dir, "original_wallpaper_backup" + Path.GetExtension(_originalWallpaperPath));
                                File.Copy(_originalWallpaperPath, _backupWallpaperCopy, true);
                            }
                            catch (Exception ex)
                            {
                                Log($"Backup wallpaper failed: {ex.Message}");
                            }
                        }
                    }

                    // Dispose any existing bitmap for this id
                    if (_activeSlaps.TryGetValue(id, out var existing))
                    {
                        existing.Bitmap.Dispose();
                    }

                    _activeSlaps[id] = new SlapEntry
                    {
                        Bitmap = new Bitmap(bitmap),
                        Opacity = Math.Clamp(opacity, 0.05f, 1.0f),
                        TargetRect = new Rectangle(x, y, Math.Max(1, width), Math.Max(1, height))
                    };

                    return UpdateDesktopWallpaperInternal();
                }
                catch (Exception ex)
                {
                    Log($"Slap exception: {ex}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Removes a slapped screenshot and updates or restores the desktop wallpaper.
        /// </summary>
        public static bool Unslap(string id, int x = 0, int y = 0, int width = 0, int height = 0)
        {
            Log($"Unslap requested: id={id}");
            lock (_syncLock)
            {
                try
                {
                    if (_activeSlaps.TryGetValue(id, out var entry))
                    {
                        entry.Bitmap.Dispose();
                        _activeSlaps.Remove(id);
                    }

                    if (_activeSlaps.Count > 0)
                    {
                        return UpdateDesktopWallpaperInternal();
                    }
                    else
                    {
                        RestoreOriginalWallpaperInternal();
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log($"Unslap exception: {ex}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Restores the original desktop wallpaper and cleans up all active slaps.
        /// </summary>
        public static void UnslapAll()
        {
            Log("UnslapAll requested");
            lock (_syncLock)
            {
                try
                {
                    foreach (var entry in _activeSlaps.Values)
                    {
                        try { entry.Bitmap.Dispose(); } catch { }
                    }
                    _activeSlaps.Clear();

                    RestoreOriginalWallpaperInternal();
                }
                catch (Exception ex)
                {
                    Log($"UnslapAll exception: {ex}");
                }
            }
        }

        private static void RestoreOriginalWallpaperInternal()
        {
            try
            {
                string? restorePath = _originalWallpaperPath;
                if (string.IsNullOrEmpty(restorePath) || !File.Exists(restorePath))
                {
                    restorePath = _backupWallpaperCopy;
                }

                if (!string.IsNullOrEmpty(restorePath) && File.Exists(restorePath))
                {
                    SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, restorePath, SPIF_SENDCHANGE);
                    Log($"Restored original wallpaper: {restorePath}");
                }

                try
                {
                    string tempPath = GetSlappedWallpaperTempPath();
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch { }

                _originalWallpaperPath = null;
            }
            catch (Exception ex)
            {
                Log($"RestoreOriginalWallpaperInternal error: {ex}");
            }
        }

        private static bool UpdateDesktopWallpaperInternal()
        {
            try
            {
                Rectangle vs = WinForms.SystemInformation.VirtualScreen;
                if (vs.Width <= 0 || vs.Height <= 0) return false;

                string? sourceWp = _originalWallpaperPath;
                if (string.IsNullOrEmpty(sourceWp) || !File.Exists(sourceWp))
                {
                    sourceWp = _backupWallpaperCopy;
                }

                string tempPath = GetSlappedWallpaperTempPath();

                using (var composed = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppRgb))
                {
                    using (var g = Graphics.FromImage(composed))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.SmoothingMode = SmoothingMode.AntiAlias;

                        // 1. Draw base wallpaper using Fill mode (aspect ratio preserved, crop center)
                        bool drewBase = false;
                        if (!string.IsNullOrEmpty(sourceWp) && File.Exists(sourceWp))
                        {
                            try
                            {
                                using var baseImg = Image.FromFile(sourceWp);
                                float screenRatio = (float)vs.Width / vs.Height;
                                float imgRatio = (float)baseImg.Width / baseImg.Height;

                                int srcX = 0, srcY = 0, srcW = baseImg.Width, srcH = baseImg.Height;
                                if (imgRatio > screenRatio)
                                {
                                    srcW = (int)(baseImg.Height * screenRatio);
                                    srcX = (baseImg.Width - srcW) / 2;
                                }
                                else
                                {
                                    srcH = (int)(baseImg.Width / screenRatio);
                                    srcY = (baseImg.Height - srcH) / 2;
                                }
                                g.DrawImage(baseImg, new Rectangle(0, 0, vs.Width, vs.Height),
                                    new Rectangle(srcX, srcY, srcW, srcH), GraphicsUnit.Pixel);
                                drewBase = true;
                            }
                            catch (Exception ex)
                            {
                                Log($"Failed to load/draw base wallpaper: {ex.Message}");
                            }
                        }

                        if (!drewBase)
                        {
                            using var bgBrush = new SolidBrush(Color.FromArgb(18, 23, 34));
                            g.FillRectangle(bgBrush, 0, 0, vs.Width, vs.Height);
                        }

                        // 2. Composite all active slaps
                        foreach (var slap in _activeSlaps.Values)
                        {
                            int destX = slap.TargetRect.X - vs.Left;
                            int destY = slap.TargetRect.Y - vs.Top;
                            int destW = slap.TargetRect.Width;
                            int destH = slap.TargetRect.Height;

                            using var path = CreateRoundedRectanglePath(new Rectangle(destX, destY, destW, destH), 12);

                            // Soft drop shadow behind slap
                            using (var shadowBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                            {
                                using var shadowPath = CreateRoundedRectanglePath(new Rectangle(destX - 2, destY - 1, destW + 4, destH + 4), 14);
                                g.FillPath(shadowBrush, shadowPath);
                            }

                            // Clip to rounded corners
                            var prevClip = g.Clip;
                            g.SetClip(path, CombineMode.Replace);

                            if (slap.Opacity >= 0.99f)
                            {
                                g.DrawImage(slap.Bitmap, new Rectangle(destX, destY, destW, destH), 0, 0, slap.Bitmap.Width, slap.Bitmap.Height, GraphicsUnit.Pixel);
                            }
                            else
                            {
                                var matrix = new ColorMatrix { Matrix33 = slap.Opacity };
                                using var attrs = new ImageAttributes();
                                attrs.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                                g.DrawImage(slap.Bitmap, new Rectangle(destX, destY, destW, destH), 0, 0, slap.Bitmap.Width, slap.Bitmap.Height, GraphicsUnit.Pixel, attrs);
                            }

                            g.Clip = prevClip;

                            // Subtle rounded accent outline matching minimal mode
                            using var pen = new Pen(Color.FromArgb(160, 0, 120, 215), 1.5f);
                            g.DrawPath(pen, path);
                        }
                    }

                    // Save as BMP for instant, zero-transcoding OS loading
                    composed.Save(tempPath, ImageFormat.Bmp);
                }

                // Apply via SPI_SETDESKWALLPAPER with SPIF_SENDCHANGE
                int res = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, tempPath, SPIF_SENDCHANGE);
                Log($"Applied composed desktop wallpaper: res={res}, slapsCount={_activeSlaps.Count}");
                return res != 0;
            }
            catch (Exception ex)
            {
                Log($"UpdateDesktopWallpaperInternal error: {ex}");
                return false;
            }
        }

        private static string? ResolveCurrentWallpaperPath()
        {
            try
            {
                string wp = new string('\0', 512);
                if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)wp.Length, wp, 0) != 0)
                {
                    int nullIdx = wp.IndexOf('\0');
                    if (nullIdx > 0)
                    {
                        string path = wp.Substring(0, nullIdx);
                        if (File.Exists(path)) return path;
                    }
                }
            }
            catch { }

            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string transcoded = Path.Combine(appData, @"Microsoft\Windows\Themes\TranscodedWallpaper");
                if (File.Exists(transcoded)) return transcoded;
            }
            catch { }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                if (key?.GetValue("Wallpaper") is string regWp && File.Exists(regWp))
                {
                    return regWp;
                }
            }
            catch { }

            return null;
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

        private static string GetSlappedWallpaperTempPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdvancedSnip");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "slapped_desktop_wallpaper.bmp");
        }
    }
}
