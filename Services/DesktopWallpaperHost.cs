using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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
    ///   4. Independent of wallpaper changes (Bing Wallpaper, Windows spotlight, slideshows, Settings).
    ///   5. Cleanly restored on app exit or crash recovery.
    /// </summary>
    public static class DesktopWallpaperHost
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int SystemParametersInfo(uint uAction, uint uParam, string? lpvParam, uint fuWinIni);

        private const uint SPI_GETDESKWALLPAPER = 0x0073;
        private const uint SPI_SETDESKWALLPAPER = 0x0014;
        private const uint SPIF_UPDATEINIFILE   = 0x01;
        private const uint SPIF_SENDCHANGE      = 0x02;

        private sealed class SlapEntry
        {
            public Bitmap Bitmap { get; set; } = null!;
            public float Opacity { get; set; }
            public Rectangle TargetRect { get; set; }
        }

        private enum WallpaperStyleMode
        {
            Fill,
            Fit,
            Stretch,
            Center,
            Tile,
            Span
        }

        private static readonly object _syncLock = new();
        private static readonly Dictionary<string, SlapEntry> _activeSlaps = new();

        private static string? _genuineWallpaperPath = null;
        private static DateTime _genuineWallpaperLastWrite = DateTime.MinValue;

        private static volatile bool _isApplyingComposedWallpaper = false;
        private static long _lastOurChangeTimestamp = 0;

        private static bool _initialized = false;

        private static FileSystemWatcher? _fileWatcher = null;
        private static System.Threading.Timer? _heartbeatTimer = null;
        private static WallpaperNotificationWindow? _notificationWindow = null;

        public static void Log(string msg)
        {
            try
            {
                string dir = GetStorageDir();
                File.AppendAllText(Path.Combine(dir, "slap.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
            }
            catch { }
        }

        public static string GetStorageDir()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdvancedSnip");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Initializes wallpaper tracking, hooks lifecycle exit events, and recovers from any previous crash.
        /// </summary>
        public static void Initialize()
        {
            lock (_syncLock)
            {
                if (_initialized) return;
                _initialized = true;

                Log("Initializing DesktopWallpaperHost...");

                // Register process exit and session ending handlers so desktop is never left with slapped images
                try
                {
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
                    SystemEvents.SessionEnding += (_, _) => Shutdown();
                    SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                }
                catch (Exception ex)
                {
                    Log($"Failed to hook system events: {ex.Message}");
                }

                // Check if previous app instance terminated abnormally while slapped
                string? current = QuerySystemWallpaperDirect();
                if (IsOurSlappedWallpaper(current))
                {
                    Log($"Detected stale slapped wallpaper ({current}) on startup! Recovering genuine wallpaper...");
                    RestoreOriginalWallpaperInternal();
                }
                else
                {
                    string? genuine = ResolveGenuineWallpaperPath();
                    if (!string.IsNullOrEmpty(genuine))
                    {
                        CacheGenuineWallpaper(genuine);
                    }
                }

                // Setup FileSystemWatcher on Windows Themes directory (catches TranscodedWallpaper updates)
                SetupFileSystemWatcher();

                // Setup hidden top-level window to receive WM_SETTINGCHANGE and WM_DISPLAYCHANGE broadcasts
                SetupNotificationWindow();

                // Optimize wallpaper crossfade animation speed for crisp, snappy responsiveness without stutter
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Personalization\Desktop Slideshow");
                    key?.SetValue("AnimationDuration", 150, RegistryValueKind.DWord);
                }
                catch { }

                // Heartbeat timer (polls every 2.5s only when slaps are active as a fallback)
                _heartbeatTimer = new System.Threading.Timer(_ =>
                {
                    if (_activeSlaps.Count > 0)
                    {
                        OnExternalChangeDetected();
                    }
                }, null, 2500, 2500);

                Log("DesktopWallpaperHost initialized successfully.");
            }
        }

        /// <summary>
        /// Cleans up monitoring resources and restores the desktop wallpaper.
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                Log("DesktopWallpaperHost shutdown invoked.");
                _heartbeatTimer?.Dispose();
                _heartbeatTimer = null;

                _fileWatcher?.Dispose();
                _fileWatcher = null;

                if (_notificationWindow != null)
                {
                    try { _notificationWindow.Dispose(); } catch { }
                    _notificationWindow = null;
                }

                try
                {
                    SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                }
                catch { }

                UnslapAll();
            }
            catch (Exception ex)
            {
                Log($"Shutdown exception: {ex.Message}");
            }
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
                    if (!_initialized)
                    {
                        Initialize();
                    }

                    // Ensure genuine wallpaper is resolved and cached
                    if (string.IsNullOrEmpty(_genuineWallpaperPath) || IsOurSlappedWallpaper(_genuineWallpaperPath) || !File.Exists(_genuineWallpaperPath))
                    {
                        string? genuine = ResolveGenuineWallpaperPath();
                        if (!string.IsNullOrEmpty(genuine))
                        {
                            CacheGenuineWallpaper(genuine);
                        }
                    }

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
                    if (!_activeSlaps.TryGetValue(id, out var entry))
                    {
                        // Item was not slapped to desktop; leave the desktop wallpaper completely untouched
                        return false;
                    }

                    entry.Bitmap.Dispose();
                    _activeSlaps.Remove(id);

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
        /// Returns true if the screenshot with the given id is currently slapped to the desktop.
        /// </summary>
        public static bool IsSlapped(string id)
        {
            lock (_syncLock)
            {
                return _activeSlaps.ContainsKey(id);
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

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.Desktop)
            {
                OnExternalChangeDetected();
            }
        }

        private static void SetupFileSystemWatcher()
        {
            try
            {
                string themesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes");
                if (Directory.Exists(themesDir))
                {
                    _fileWatcher = new FileSystemWatcher(themesDir)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };

                    _fileWatcher.Changed += (_, _) => OnExternalChangeDetected();
                    _fileWatcher.Created += (_, _) => OnExternalChangeDetected();
                    _fileWatcher.Renamed += (_, _) => OnExternalChangeDetected();
                }
            }
            catch (Exception ex)
            {
                Log($"SetupFileSystemWatcher error: {ex.Message}");
            }
        }

        private static void SetupNotificationWindow()
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app != null && app.Dispatcher != null && !app.Dispatcher.HasShutdownStarted)
                {
                    if (app.Dispatcher.CheckAccess())
                    {
                        _notificationWindow = new WallpaperNotificationWindow();
                    }
                    else
                    {
                        app.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try { _notificationWindow = new WallpaperNotificationWindow(); } catch { }
                        }));
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"SetupNotificationWindow error: {ex.Message}");
            }
        }

        private static void OnExternalChangeDetected()
        {
            // Ignore updates triggered by our own wallpaper composition
            if (_isApplyingComposedWallpaper || (Environment.TickCount64 - _lastOurChangeTimestamp < 1200))
            {
                return;
            }

            lock (_syncLock)
            {
                try
                {
                    string? newGenuine = ResolveGenuineWallpaperPath();
                    if (string.IsNullOrEmpty(newGenuine) || IsOurSlappedWallpaper(newGenuine))
                    {
                        return;
                    }

                    bool pathChanged = !string.Equals(newGenuine, _genuineWallpaperPath, StringComparison.OrdinalIgnoreCase);
                    bool fileModified = false;

                    if (!pathChanged && File.Exists(newGenuine))
                    {
                        DateTime writeTime = File.GetLastWriteTimeUtc(newGenuine);
                        if (writeTime > _genuineWallpaperLastWrite.AddSeconds(1))
                        {
                            fileModified = true;
                        }
                    }

                    if (pathChanged || fileModified)
                    {
                        Log($"External wallpaper change detected! New genuine: {newGenuine} (pathChanged={pathChanged}, fileModified={fileModified})");
                        CacheGenuineWallpaper(newGenuine);

                        // If active slaps exist, immediately re-composite onto the new wallpaper so images never vanish!
                        if (_activeSlaps.Count > 0)
                        {
                            UpdateDesktopWallpaperInternal();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"OnExternalChangeDetected error: {ex.Message}");
                }
            }
        }

        private static void CacheGenuineWallpaper(string path)
        {
            if (IsOurSlappedWallpaper(path) || !File.Exists(path)) return;

            try
            {
                _genuineWallpaperPath = path;
                _genuineWallpaperLastWrite = File.GetLastWriteTimeUtc(path);

                string dir = GetStorageDir();
                File.WriteAllText(Path.Combine(dir, "genuine_wallpaper_path.txt"), path);

                // Keep a persistent fallback bitmap copy using MemoryStream to prevent GDI+ file lock errors
                string backupBmp = Path.Combine(dir, "genuine_wallpaper_backup.bmp");
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var src = Image.FromStream(fs))
                using (var bmp = new Bitmap(src))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Bmp);
                    File.WriteAllBytes(backupBmp, ms.ToArray());
                }
                Log($"Cached genuine wallpaper: {path}");
            }
            catch (Exception ex)
            {
                Log($"CacheGenuineWallpaper error: {ex.Message}");
            }
        }

        private static void RestoreOriginalWallpaperInternal()
        {
            try
            {
                string? restorePath = ResolveGenuineWallpaperPath();
                Log($"RestoreOriginalWallpaperInternal: Target = {restorePath}");

                if (!string.IsNullOrEmpty(restorePath) && File.Exists(restorePath))
                {
                    _isApplyingComposedWallpaper = true;
                    _lastOurChangeTimestamp = Environment.TickCount64;
                    try
                    {
                        int res = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, restorePath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                        Log($"Restored genuine wallpaper: res={res}, path={restorePath}");
                    }
                    finally
                    {
                        _isApplyingComposedWallpaper = false;
                        _lastOurChangeTimestamp = Environment.TickCount64;
                    }
                }
                else
                {
                    Log("Warning: No valid genuine wallpaper found to restore!");
                }

                CleanupTempFiles();
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

                string? sourceWp = _genuineWallpaperPath;
                if (string.IsNullOrEmpty(sourceWp) || !File.Exists(sourceWp) || IsOurSlappedWallpaper(sourceWp))
                {
                    sourceWp = ResolveGenuineWallpaperPath();
                }

                string tempPath = GetNextTempWallpaperPath();
                WallpaperStyleMode style = GetCurrentWallpaperStyle();

                using (var composed = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppRgb))
                {
                    using (var g = Graphics.FromImage(composed))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.SmoothingMode = SmoothingMode.AntiAlias;

                        // 1. Draw base wallpaper using current system style
                        bool drewBase = false;
                        if (!string.IsNullOrEmpty(sourceWp) && File.Exists(sourceWp))
                        {
                            try
                            {
                                using var fs = new FileStream(sourceWp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                using var baseImg = Image.FromStream(fs);
                                DrawBaseWallpaper(g, baseImg, vs, style);
                                drewBase = true;
                            }
                            catch (Exception ex)
                            {
                                Log($"Failed to load/draw base wallpaper ({sourceWp}): {ex.Message}");
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

                    // Save to unique temporary BMP file using MemoryStream to avoid GDI+ lock errors
                    using (var ms = new MemoryStream())
                    {
                        composed.Save(ms, ImageFormat.Bmp);
                        File.WriteAllBytes(tempPath, ms.ToArray());
                    }
                }

                // Apply composed wallpaper without updating ini/registry so system registry stays pointing to genuine wallpaper
                _isApplyingComposedWallpaper = true;
                _lastOurChangeTimestamp = Environment.TickCount64;
                try
                {
                    int res = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, tempPath, SPIF_SENDCHANGE);
                    Log($"Applied composed wallpaper: res={res}, slapsCount={_activeSlaps.Count}, tempPath={Path.GetFileName(tempPath)}");
                    CleanupOldTempFiles(tempPath);
                    return res != 0;
                }
                finally
                {
                    _isApplyingComposedWallpaper = false;
                    _lastOurChangeTimestamp = Environment.TickCount64;
                }
            }
            catch (Exception ex)
            {
                Log($"UpdateDesktopWallpaperInternal error: {ex}");
                return false;
            }
        }

        private static void DrawBaseWallpaper(Graphics g, Image baseImg, Rectangle vs, WallpaperStyleMode style)
        {
            switch (style)
            {
                case WallpaperStyleMode.Fill:
                default:
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
                    break;

                case WallpaperStyleMode.Fit:
                    float scale = Math.Min((float)vs.Width / baseImg.Width, (float)vs.Height / baseImg.Height);
                    int fitW = (int)(baseImg.Width * scale);
                    int fitH = (int)(baseImg.Height * scale);
                    int fitX = (vs.Width - fitW) / 2;
                    int fitY = (vs.Height - fitH) / 2;
                    g.DrawImage(baseImg, new Rectangle(fitX, fitY, fitW, fitH),
                        new Rectangle(0, 0, baseImg.Width, baseImg.Height), GraphicsUnit.Pixel);
                    break;

                case WallpaperStyleMode.Stretch:
                    g.DrawImage(baseImg, new Rectangle(0, 0, vs.Width, vs.Height),
                        new Rectangle(0, 0, baseImg.Width, baseImg.Height), GraphicsUnit.Pixel);
                    break;

                case WallpaperStyleMode.Center:
                    int cX = (vs.Width - baseImg.Width) / 2;
                    int cY = (vs.Height - baseImg.Height) / 2;
                    g.DrawImage(baseImg, cX, cY, baseImg.Width, baseImg.Height);
                    break;

                case WallpaperStyleMode.Tile:
                    using (var tb = new TextureBrush(baseImg, WrapMode.Tile))
                    {
                        g.FillRectangle(tb, 0, 0, vs.Width, vs.Height);
                    }
                    break;
            }
        }

        private static WallpaperStyleMode GetCurrentWallpaperStyle()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                string? styleStr = key?.GetValue("WallpaperStyle") as string;
                string? tileStr = key?.GetValue("TileWallpaper") as string;

                if (tileStr == "1") return WallpaperStyleMode.Tile;
                if (styleStr == "6") return WallpaperStyleMode.Fit;
                if (styleStr == "2") return WallpaperStyleMode.Stretch;
                if (styleStr == "0") return WallpaperStyleMode.Center;
                if (styleStr == "22") return WallpaperStyleMode.Span;
                return WallpaperStyleMode.Fill;
            }
            catch
            {
                return WallpaperStyleMode.Fill;
            }
        }

        public static string? ResolveGenuineWallpaperPath()
        {
            // 1. Registry HKCU\Control Panel\Desktop\Wallpaper (Primary source of truth for user wallpaper)
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                if (key?.GetValue("Wallpaper") is string regWp && !string.IsNullOrWhiteSpace(regWp))
                {
                    regWp = regWp.Trim();
                    if (!IsOurSlappedWallpaper(regWp) && File.Exists(regWp))
                    {
                        return regWp;
                    }
                }
            }
            catch { }

            // 2. Registry TranscodedImageCache
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                if (key?.GetValue("TranscodedImageCache") is byte[] cacheBytes && cacheBytes.Length > 24)
                {
                    string cachedPath = System.Text.Encoding.Unicode.GetString(cacheBytes, 24, cacheBytes.Length - 24);
                    int nullIdx = cachedPath.IndexOf('\0');
                    if (nullIdx > 0) cachedPath = cachedPath.Substring(0, nullIdx).Trim();
                    if (!IsOurSlappedWallpaper(cachedPath) && File.Exists(cachedPath))
                    {
                        return cachedPath;
                    }
                }
            }
            catch { }

            // 3. Registry Explorer Wallpapers BackgroundHistory
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers");
                if (key != null)
                {
                    for (int i = 0; i < 5; i++)
                    {
                        if (key.GetValue($"BackgroundHistoryPath{i}") is string histPath && !string.IsNullOrWhiteSpace(histPath))
                        {
                            histPath = histPath.Trim();
                            if (!IsOurSlappedWallpaper(histPath) && File.Exists(histPath))
                            {
                                return histPath;
                            }
                        }
                    }
                }
            }
            catch { }

            // 4. In-memory desktop wallpaper via SPI_GETDESKWALLPAPER
            try
            {
                string wp = new string('\0', 512);
                if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)wp.Length, wp, 0) != 0)
                {
                    int nullIdx = wp.IndexOf('\0');
                    if (nullIdx > 0)
                    {
                        string path = wp.Substring(0, nullIdx).Trim();
                        if (!IsOurSlappedWallpaper(path) && File.Exists(path))
                        {
                            return path;
                        }
                    }
                }
            }
            catch { }

            // 5. TranscodedWallpaper file (only valid if NO active slaps exist)
            try
            {
                if (_activeSlaps.Count == 0)
                {
                    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string transcoded = Path.Combine(appData, @"Microsoft\Windows\Themes\TranscodedWallpaper");
                    if (File.Exists(transcoded) && !IsOurSlappedWallpaper(transcoded))
                    {
                        return transcoded;
                    }
                }
            }
            catch { }

            // 6. Our persistent genuine wallpaper path cache
            try
            {
                string dir = GetStorageDir();
                string pathFile = Path.Combine(dir, "genuine_wallpaper_path.txt");
                if (File.Exists(pathFile))
                {
                    string savedPath = File.ReadAllText(pathFile).Trim();
                    if (!IsOurSlappedWallpaper(savedPath) && File.Exists(savedPath))
                    {
                        return savedPath;
                    }
                }

                string backupBmp = Path.Combine(dir, "genuine_wallpaper_backup.bmp");
                if (File.Exists(backupBmp))
                {
                    return backupBmp;
                }
            }
            catch { }

            // 7. Windows standard wallpaper fallback
            string defaultWp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Web\Wallpaper\Windows\img0.jpg");
            if (File.Exists(defaultWp)) return defaultWp;

            return null;
        }

        private static string? QuerySystemWallpaperDirect()
        {
            try
            {
                string wp = new string('\0', 512);
                if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)wp.Length, wp, 0) != 0)
                {
                    int nullIdx = wp.IndexOf('\0');
                    if (nullIdx > 0)
                    {
                        string path = wp.Substring(0, nullIdx).Trim();
                        if (!string.IsNullOrEmpty(path)) return path;
                    }
                }
            }
            catch { }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                if (key?.GetValue("Wallpaper") is string regWp && !string.IsNullOrWhiteSpace(regWp))
                {
                    return regWp.Trim();
                }
            }
            catch { }

            return null;
        }

        private static bool IsOurSlappedWallpaper(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            string fileName = Path.GetFileName(path);
            if (fileName.StartsWith("slapped_desktop_wallpaper", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdvancedSnip");
            if (path.StartsWith(appDir, StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) && fileName.Contains("slapped"))
            {
                return true;
            }

            return false;
        }

        private static string GetNextTempWallpaperPath()
        {
            string dir = GetStorageDir();
            return Path.Combine(dir, $"slapped_desktop_wallpaper_{Guid.NewGuid():N}.bmp");
        }

        private static void CleanupOldTempFiles(string currentPath)
        {
            try
            {
                string dir = GetStorageDir();
                foreach (var file in Directory.GetFiles(dir, "slapped_desktop_wallpaper_*.bmp"))
                {
                    if (string.Equals(file, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(file); } catch { }
                }
            }
            catch { }
        }

        private static void CleanupTempFiles()
        {
            try
            {
                string dir = GetStorageDir();
                foreach (var file in Directory.GetFiles(dir, "slapped_desktop_wallpaper*.bmp"))
                {
                    try { File.Delete(file); } catch { }
                }
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

        /// <summary>
        /// Invisible top-level window listening for WM_SETTINGCHANGE and WM_DISPLAYCHANGE broadcasts.
        /// </summary>
        private sealed class WallpaperNotificationWindow : WinForms.NativeWindow, IDisposable
        {
            private const int WM_SETTINGCHANGE = 0x001A;
            private const int WM_DISPLAYCHANGE = 0x007E;
            private const int WS_POPUP         = unchecked((int)0x80000000);

            public WallpaperNotificationWindow()
            {
                var cp = new WinForms.CreateParams
                {
                    Caption = "AdvancedSnip_WallpaperWatcher",
                    Style = WS_POPUP,
                    Width = 0,
                    Height = 0,
                    X = 0,
                    Y = 0
                };
                CreateHandle(cp);
            }

            protected override void WndProc(ref WinForms.Message m)
            {
                if (m.Msg == WM_SETTINGCHANGE || m.Msg == WM_DISPLAYCHANGE)
                {
                    OnExternalChangeDetected();
                }
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                try
                {
                    DestroyHandle();
                }
                catch { }
            }
        }
    }
}
