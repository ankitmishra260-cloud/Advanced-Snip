using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Composites pinned screenshots directly onto the Windows desktop wallpaper layer.
    /// Uses the Windows Shell IDesktopWallpaper COM interface to render per-monitor bitmaps
    /// at each display's exact native physical resolution, ensuring pixel-accurate placement,
    /// mixed-DPI fidelity, seamless cross-monitor straddling, per-monitor wallpaper preservation,
    /// and clean crash/exit recovery on both single-monitor and multi-monitor setups.
    /// </summary>
    public static class DesktopWallpaperHost
    {
        // ── Win32 Fallback & Shell Interop ────────────────────────────────────

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int SystemParametersInfo(uint uAction, uint uParam, string? lpvParam, uint fuWinIni);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ILCreateFromPathW([MarshalAs(UnmanagedType.LPWStr)] string pszPath);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        [DllImport("shell32.dll")]
        private static extern int SHCreateShellItemArrayFromIDLists(uint cidl, IntPtr[] rgpidl, out IntPtr ppsiItemArray);

        private const uint SPI_GETDESKWALLPAPER = 0x0073;
        private const uint SPI_SETDESKWALLPAPER = 0x0014;
        private const uint SPIF_UPDATEINIFILE   = 0x01;
        private const uint SPIF_SENDCHANGE      = 0x02;

        // ── IDesktopWallpaper COM Definitions ─────────────────────────────────

        private static readonly Guid CLSID_DesktopWallpaper = new("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD");

        private const int DWPOS_CENTER  = 0;
        private const int DWPOS_TILE    = 1;
        private const int DWPOS_STRETCH = 2;
        private const int DWPOS_FIT     = 3;
        private const int DWPOS_FILL    = 4;
        private const int DWPOS_SPAN    = 5;

        private const int DSS_SLIDESHOW = 0x2;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        [ComImport]
        [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDesktopWallpaper
        {
            [PreserveSig] int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
            [PreserveSig] int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, [MarshalAs(UnmanagedType.LPWStr)] out string? wallpaper);
            [PreserveSig] int GetMonitorDevicePathAt(uint monitorIndex, [MarshalAs(UnmanagedType.LPWStr)] out string? monitorID);
            [PreserveSig] int GetMonitorDevicePathCount(out uint count);
            [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out Win32.RECT displayRect);
            [PreserveSig] int SetBackgroundColor(uint color);
            [PreserveSig] int GetBackgroundColor(out uint color);
            [PreserveSig] int SetPosition(int position);
            [PreserveSig] int GetPosition(out int position);
            [PreserveSig] int SetSlideshow(IntPtr items);
            [PreserveSig] int GetSlideshow(out IntPtr items);
            [PreserveSig] int SetSlideshowOptions(int options, uint slideshowTick);
            [PreserveSig] int GetSlideshowOptions(out int options, out uint slideshowTick);
            [PreserveSig] int AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, int direction);
            [PreserveSig] int GetStatus(out int state);
            [PreserveSig] int Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IShellItem ppsi);
            [PreserveSig] int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string? ppszName);
            [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport]
        [Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int GetCount(out uint pdwNumItems);
            [PreserveSig] int GetItemAt(uint dwIndex, out IShellItem ppsi);
            [PreserveSig] int EnumItems(out IntPtr ppenumShellItems);
        }

        // ── Internal Data Models ──────────────────────────────────────────────

        private sealed class SlapEntry
        {
            public string Id { get; set; } = "";
            public Bitmap Bitmap { get; set; } = null!;
            public float Opacity { get; set; }
            public Rectangle TargetRect { get; set; }
            public long Order { get; set; }
            public long Version { get; set; }
        }

        private sealed class ActiveMonitorInfo
        {
            public string DevicePath { get; set; } = "";
            public Rectangle Bounds { get; set; }
            public double Scale { get; set; } = 1.0;
        }

        private sealed class MonitorWallpaperBackup
        {
            /// <summary>
            /// Original wallpaper path for this monitor. Empty string "" explicitly means Solid Color.
            /// Null means unknown/not yet captured.
            /// </summary>
            public string? OriginalPath { get; set; }
            public string? BackupImagePath { get; set; }
            public long LastWriteTicksUtc { get; set; }
        }

        private sealed class GenuineDesktopState
        {
            public int Position { get; set; } = DWPOS_FILL;
            public uint BackgroundColorRef { get; set; } = 0;
            public bool WasSlideshow { get; set; }
            public int SlideshowOptions { get; set; }
            public uint SlideshowTick { get; set; }
            public List<string> SlideshowPaths { get; set; } = new();
            public Dictionary<string, MonitorWallpaperBackup> Monitors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public string? GlobalFallbackWallpaperPath { get; set; }
            public string? GlobalBackupBmpPath { get; set; }
            public bool IsSlapActive { get; set; }
            public bool TemporarilyChangedPosition { get; set; }
            public bool AnimationDurationModified { get; set; }
            public int? OriginalAnimationDuration { get; set; }
        }

        // ── State Fields ──────────────────────────────────────────────────────

        private static readonly object _syncLock = new();
        private static readonly Dictionary<string, SlapEntry> _activeSlaps = new(StringComparer.Ordinal);
        private static readonly HashSet<string> _slappedMonitorIds = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _monitorTempFiles = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _monitorRenderKeys = new(StringComparer.OrdinalIgnoreCase);

        private static GenuineDesktopState _genuineState = new();
        private static long _slapOrderCounter = 0;
        private static long _slapVersionCounter = 0;

        private static volatile bool _isApplyingComposedWallpaper = false;
        private static long _lastOurChangeTimestamp = 0;

        private static bool _initialized = false;
        public static bool IsShuttingDown { get; private set; } = false;

        private static int _batchDepth = 0;
        private static bool _batchDirty = false;

        private static FileSystemWatcher? _fileWatcher = null;
        private static System.Threading.Timer? _heartbeatTimer = null;
        private static System.Threading.Timer? _displayChangeDebounceTimer = null;
        private static WallpaperNotificationWindow? _notificationWindow = null;

        /// <summary>
        /// Raised on a threadpool thread (debounced) when Windows display topology changes
        /// (monitors connected/disconnected, rearranged, resolution or DPI changed).
        /// </summary>
        public static event EventHandler? DisplayTopologyChanged;

        // ── Public Lifecycle & Utilities ──────────────────────────────────────

        public static void BeginShutdown()
        {
            IsShuttingDown = true;
        }

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

        private static string StateFilePath => Path.Combine(GetStorageDir(), "genuine_wallpaper_state.json");

        /// <summary>
        /// Batches multiple Slap/Unslap calls into a single desktop wallpaper composition or restore pass.
        /// </summary>
        public static IDisposable BeginBatch()
        {
            lock (_syncLock)
            {
                _batchDepth++;
            }
            return new BatchScope();
        }

        private sealed class BatchScope : IDisposable
        {
            private bool _disposed;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                lock (_syncLock)
                {
                    if (_batchDepth > 0) _batchDepth--;
                    if (_batchDepth == 0 && _batchDirty && !IsShuttingDown)
                    {
                        _batchDirty = false;
                        if (_activeSlaps.Count > 0)
                        {
                            UpdateDesktopWallpaperInternal();
                        }
                        else
                        {
                            RestoreOriginalWallpaperInternal();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Initializes wallpaper tracking, hooks lifecycle & display events, and recovers from any prior crash.
        /// </summary>
        public static void Initialize()
        {
            lock (_syncLock)
            {
                if (_initialized) return;
                _initialized = true;

                Log("Initializing DesktopWallpaperHost (per-monitor IDesktopWallpaper engine)...");

                try
                {
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
                    SystemEvents.SessionEnding += (_, _) => Shutdown();
                    SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                    SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                }
                catch (Exception ex)
                {
                    Log($"Failed to hook system events: {ex.Message}");
                }

                // Load persisted state if any (for crash recovery)
                var persisted = LoadPersistedState();
                bool anyMonitorSlapped = IsAnyMonitorCurrentlySlapped();

                if ((persisted != null && persisted.IsSlapActive) || anyMonitorSlapped)
                {
                    Log($"Detected stale slapped state on startup (persistedActive={persisted?.IsSlapActive}, anyMonitorSlapped={anyMonitorSlapped}). Recovering genuine wallpaper...");
                    if (persisted != null)
                    {
                        _genuineState = persisted;
                    }
                    else
                    {
                        // Recover from legacy pre-v3 cache files (genuine_wallpaper_path.txt / genuine_wallpaper_backup.bmp)
                        _genuineState = BuildLegacyRecoveryState();
                    }

                    RestoreOriginalWallpaperInternal(forceRestore: true);
                }

                // Capture fresh live state of all monitors (now guaranteed genuine)
                CaptureGenuineStateFromSystem(backupVolatileSources: false);
                SavePersistedState();

                SetupFileSystemWatcher();
                SetupNotificationWindow();

                // Heartbeat fallback timer (polls every 2.5s only while slaps are active)
                _heartbeatTimer = new System.Threading.Timer(_ =>
                {
                    bool hasSlaps;
                    lock (_syncLock)
                    {
                        hasSlaps = _activeSlaps.Count > 0 && !IsShuttingDown;
                    }
                    if (hasSlaps)
                    {
                        OnExternalChangeDetected();
                    }
                }, null, 2500, 2500);

                Log("DesktopWallpaperHost initialized successfully.");
            }
        }

        /// <summary>
        /// Cleans up monitoring resources, restores genuine per-monitor wallpapers, and restores settings.
        /// </summary>
        public static void Shutdown()
        {
            lock (_syncLock)
            {
                if (IsShuttingDown && !_initialized) return;
                IsShuttingDown = true;

                try
                {
                    Log("DesktopWallpaperHost shutdown invoked.");

                    _heartbeatTimer?.Dispose();
                    _heartbeatTimer = null;

                    _displayChangeDebounceTimer?.Dispose();
                    _displayChangeDebounceTimer = null;

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
                        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                    }
                    catch { }

                    // Clear all active slaps and restore original wallpapers in one pass
                    DisposeAllActiveSlapsLocked();
                    RestoreOriginalWallpaperInternal();

                    _initialized = false;
                }
                catch (Exception ex)
                {
                    Log($"Shutdown exception: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Slaps a screenshot onto the desktop wallpaper behind all desktop icons on the exact
        /// monitor(s) intersected by the physical virtual-desktop rectangle (x, y, width, height).
        /// </summary>
        public static bool Slap(string id, Bitmap bitmap, float opacity, int x, int y, int width, int height)
        {
            Log($"Slap requested: id={id}, rect=({x},{y},{width}x{height}), opacity={opacity:0.##}, bmp={bitmap?.Width}x{bitmap?.Height}");
            if (string.IsNullOrEmpty(id) || bitmap == null) return false;

            lock (_syncLock)
            {
                try
                {
                    if (IsShuttingDown) return false;
                    if (!_initialized) Initialize();

                    bool firstSlap = _activeSlaps.Count == 0 && !_genuineState.IsSlapActive;
                    if (firstSlap)
                    {
                        // Refresh genuine live state right before our first slap so we capture any
                        // wallpaper/style/color/slideshow changes made since app startup.
                        CaptureGenuineStateFromSystem(backupVolatileSources: true);
                        ApplyFastAnimationDurationLocked();
                        _genuineState.IsSlapActive = true;
                        SavePersistedState();
                    }

                    long order;
                    if (_activeSlaps.TryGetValue(id, out var existing))
                    {
                        order = existing.Order;
                        try { existing.Bitmap.Dispose(); } catch { }
                    }
                    else
                    {
                        order = ++_slapOrderCounter;
                    }

                    _activeSlaps[id] = new SlapEntry
                    {
                        Id = id,
                        Bitmap = new Bitmap(bitmap),
                        Opacity = Math.Clamp(opacity, 0.05f, 1.0f),
                        TargetRect = new Rectangle(x, y, Math.Max(1, width), Math.Max(1, height)),
                        Order = order,
                        Version = ++_slapVersionCounter
                    };

                    if (_batchDepth > 0)
                    {
                        _batchDirty = true;
                        return true;
                    }

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
        /// Updates only the opacity of an already-slapped screenshot without re-rendering its bitmap,
        /// preserving its exact pixel-calibrated capture.
        /// </summary>
        public static bool UpdateOpacity(string id, float opacity)
        {
            if (string.IsNullOrEmpty(id)) return false;

            lock (_syncLock)
            {
                try
                {
                    if (IsShuttingDown) return false;
                    if (!_activeSlaps.TryGetValue(id, out var entry)) return false;

                    float clamped = Math.Clamp(opacity, 0.05f, 1.0f);
                    if (Math.Abs(entry.Opacity - clamped) < 0.001f) return true;

                    entry.Opacity = clamped;
                    entry.Version = ++_slapVersionCounter;

                    if (_batchDepth > 0)
                    {
                        _batchDirty = true;
                        return true;
                    }

                    return UpdateDesktopWallpaperInternal();
                }
                catch (Exception ex)
                {
                    Log($"UpdateOpacity exception: {ex}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Removes a slapped screenshot and updates affected monitor(s) or restores genuine wallpaper.
        /// </summary>
        public static bool Unslap(string id, int x = 0, int y = 0, int width = 0, int height = 0)
        {
            Log($"Unslap requested: id={id}, isShuttingDown={IsShuttingDown}");
            if (string.IsNullOrEmpty(id)) return false;

            lock (_syncLock)
            {
                try
                {
                    if (!_activeSlaps.TryGetValue(id, out var entry))
                    {
                        return false;
                    }

                    _activeSlaps.Remove(id);
                    try { entry.Bitmap?.Dispose(); } catch { }

                    if (IsShuttingDown)
                    {
                        return true;
                    }

                    if (_batchDepth > 0)
                    {
                        _batchDirty = true;
                        return true;
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
        /// Restores the original desktop wallpaper across all monitors and clears all active slaps.
        /// </summary>
        public static void UnslapAll()
        {
            Log("UnslapAll requested");
            lock (_syncLock)
            {
                DisposeAllActiveSlapsLocked();
                if (_batchDepth > 0)
                {
                    _batchDirty = true;
                    return;
                }
                RestoreOriginalWallpaperInternal();
            }
        }

        private static void DisposeAllActiveSlapsLocked()
        {
            foreach (var entry in _activeSlaps.Values)
            {
                try { entry.Bitmap?.Dispose(); } catch { }
            }
            _activeSlaps.Clear();
        }

        // ── Animation Duration Registry Helpers (Crash-Safe) ──────────────────

        private static void ApplyFastAnimationDurationLocked()
        {
            try
            {
                if (_genuineState.AnimationDurationModified) return;
                using var readKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\Personalization\Desktop Slideshow");
                _genuineState.OriginalAnimationDuration = readKey?.GetValue("AnimationDuration") as int?;
                using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Personalization\Desktop Slideshow");
                key?.SetValue("AnimationDuration", 150, RegistryValueKind.DWord);
                _genuineState.AnimationDurationModified = true;
            }
            catch { }
        }

        private static void RestoreAnimationDurationLocked()
        {
            try
            {
                if (!_genuineState.AnimationDurationModified) return;
                using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Personalization\Desktop Slideshow");
                if (key != null)
                {
                    if (_genuineState.OriginalAnimationDuration.HasValue)
                    {
                        key.SetValue("AnimationDuration", _genuineState.OriginalAnimationDuration.Value, RegistryValueKind.DWord);
                    }
                    else
                    {
                        try { key.DeleteValue("AnimationDuration", false); } catch { }
                    }
                }
                _genuineState.AnimationDurationModified = false;
            }
            catch { }
        }

        // ── Event Listeners (Wallpaper & Display Topology Changes) ────────────

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.Desktop)
            {
                OnExternalChangeDetected();
            }
        }

        private static void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            ScheduleDisplayTopologyRefresh();
        }

        private static void ScheduleDisplayTopologyRefresh()
        {
            lock (_syncLock)
            {
                if (IsShuttingDown) return;
                _displayChangeDebounceTimer?.Dispose();
                _displayChangeDebounceTimer = new System.Threading.Timer(_ =>
                {
                    OnDisplayTopologyChanged();
                }, null, 250, Timeout.Infinite);
            }
        }

        private static void OnDisplayTopologyChanged()
        {
            bool shouldNotify = false;
            lock (_syncLock)
            {
                if (IsShuttingDown) return;
                try
                {
                    Log("Display topology change detected (WM_DISPLAYCHANGE / DisplaySettingsChanged).");

                    // Capture genuine wallpaper for any newly connected monitors not yet in state
                    EnsureNewMonitorsCapturedLocked();

                    if (_activeSlaps.Count > 0)
                    {
                        Log($"Re-compositing {_activeSlaps.Count} active slap(s) for updated display topology.");
                        UpdateDesktopWallpaperInternal();
                        shouldNotify = true;
                    }
                }
                catch (Exception ex)
                {
                    Log($"OnDisplayTopologyChanged error: {ex.Message}");
                }
            }

            if (shouldNotify)
            {
                try { DisplayTopologyChanged?.Invoke(null, EventArgs.Empty); } catch { }
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
            if (_isApplyingComposedWallpaper || (Environment.TickCount64 - _lastOurChangeTimestamp < 1200))
            {
                return;
            }

            lock (_syncLock)
            {
                if (IsShuttingDown || _isApplyingComposedWallpaper) return;

                try
                {
                    bool anyChange = false;
                    bool hasActiveSlaps = _activeSlaps.Count > 0;

                    IDesktopWallpaper? dw = CreateDesktopWallpaper();
                    if (dw != null)
                    {
                        try
                        {
                            // 1. Check wallpaper position change (ignore if we temporarily forced Fill for Span/Tile)
                            if (!_genuineState.TemporarilyChangedPosition && dw.GetPosition(out int livePos) == 0)
                            {
                                if (livePos != _genuineState.Position)
                                {
                                    Log($"External wallpaper style change detected: {_genuineState.Position} -> {livePos}");
                                    _genuineState.Position = livePos;
                                    anyChange = true;
                                }
                            }

                            // 2. Check solid desktop background color change
                            if (dw.GetBackgroundColor(out uint liveBg) == 0 && liveBg != _genuineState.BackgroundColorRef)
                            {
                                Log($"External desktop background color change detected: 0x{_genuineState.BackgroundColorRef:X6} -> 0x{liveBg:X6}");
                                _genuineState.BackgroundColorRef = liveBg;
                                anyChange = true;
                            }

                            // 3. Check slideshow activation
                            if (dw.GetStatus(out int status) == 0 && (status & DSS_SLIDESHOW) != 0)
                            {
                                var paths = ReadSlideshowPaths(dw, out int ssOpts, out uint ssTick);
                                if (!_genuineState.WasSlideshow || !paths.SequenceEqual(_genuineState.SlideshowPaths, StringComparer.OrdinalIgnoreCase))
                                {
                                    Log($"External slideshow change detected ({paths.Count} source(s)).");
                                    _genuineState.WasSlideshow = true;
                                    _genuineState.SlideshowOptions = ssOpts;
                                    _genuineState.SlideshowTick = ssTick;
                                    _genuineState.SlideshowPaths = paths;
                                    anyChange = true;
                                }
                            }

                            // 4. Check per-monitor wallpapers
                            var monitors = EnumerateActiveMonitors(dw);
                            foreach (var mon in monitors)
                            {
                                int hr = dw.GetWallpaper(mon.DevicePath, out string? liveWp);
                                if (hr < 0) continue;

                                string normalized = (liveWp ?? "").Trim();

                                // Skip if still pointing at our composed slapped file
                                if (IsOurSlappedWallpaper(normalized)) continue;

                                // CRITICAL: Never treat Windows' TranscodedWallpaper cache file as a fresh
                                // genuine change while slaps are active, because Windows overwrites
                                // TranscodedWallpaper with our slapped BMP!
                                if (hasActiveSlaps && IsTranscodedWallpaperPath(normalized)) continue;

                                _genuineState.Monitors.TryGetValue(mon.DevicePath, out var prev);

                                bool pathChanged = prev == null || !string.Equals(prev.OriginalPath, normalized, StringComparison.OrdinalIgnoreCase);
                                bool fileModified = false;
                                long writeTicks = 0;

                                if (!string.IsNullOrEmpty(normalized) && File.Exists(normalized))
                                {
                                    writeTicks = File.GetLastWriteTimeUtc(normalized).Ticks;
                                    if (!pathChanged && prev != null && writeTicks > prev.LastWriteTicksUtc + TimeSpan.TicksPerSecond)
                                    {
                                        fileModified = true;
                                    }
                                }

                                if (pathChanged || fileModified)
                                {
                                    Log($"External monitor wallpaper change on {ShortMonitorId(mon.DevicePath)}: '{prev?.OriginalPath ?? "<none>"}' -> '{normalized}'");
                                    string? backupPath = !string.IsNullOrEmpty(normalized)
                                        ? EnsureBackupCopyForSource(normalized)
                                        : null;

                                    _genuineState.Monitors[mon.DevicePath] = new MonitorWallpaperBackup
                                    {
                                        OriginalPath = normalized,
                                        BackupImagePath = backupPath,
                                        LastWriteTicksUtc = writeTicks
                                    };

                                    if (!string.IsNullOrEmpty(normalized) && !IsTranscodedWallpaperPath(normalized))
                                    {
                                        _genuineState.GlobalFallbackWallpaperPath = normalized;
                                        if (backupPath != null) _genuineState.GlobalBackupBmpPath = backupPath;
                                    }

                                    // If user changed wallpaper externally, this monitor no longer displays our slap
                                    _slappedMonitorIds.Remove(mon.DevicePath);
                                    _monitorRenderKeys.Remove(mon.DevicePath);
                                    anyChange = true;
                                }
                            }
                        }
                        finally
                        {
                            ReleaseCom(dw);
                        }
                    }

                    if (anyChange)
                    {
                        SavePersistedState();
                        if (hasActiveSlaps)
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

        // ── Genuine State Capture & Persistence ───────────────────────────────

        private static void CaptureGenuineStateFromSystem(bool backupVolatileSources)
        {
            IDesktopWallpaper? dw = CreateDesktopWallpaper();
            if (dw == null)
            {
                string? fallback = ResolveGenuineWallpaperPath();
                if (!string.IsNullOrEmpty(fallback))
                {
                    _genuineState.GlobalFallbackWallpaperPath = fallback;
                    if (backupVolatileSources)
                    {
                        _genuineState.GlobalBackupBmpPath = EnsureBackupCopyForSource(fallback);
                    }
                }
                _genuineState.Position = GetRegistryWallpaperPosition();
                _genuineState.BackgroundColorRef = GetRegistryBackgroundColorRef();
                return;
            }

            try
            {
                if (dw.GetPosition(out int pos) == 0)
                {
                    _genuineState.Position = pos;
                }
                else
                {
                    _genuineState.Position = GetRegistryWallpaperPosition();
                }

                if (dw.GetBackgroundColor(out uint bg) == 0)
                {
                    _genuineState.BackgroundColorRef = bg;
                }
                else
                {
                    _genuineState.BackgroundColorRef = GetRegistryBackgroundColorRef();
                }

                if (dw.GetStatus(out int status) == 0 && (status & DSS_SLIDESHOW) != 0)
                {
                    _genuineState.WasSlideshow = true;
                    _genuineState.SlideshowPaths = ReadSlideshowPaths(dw, out int ssOpts, out uint ssTick);
                    _genuineState.SlideshowOptions = ssOpts;
                    _genuineState.SlideshowTick = ssTick;
                }
                else
                {
                    _genuineState.WasSlideshow = false;
                    _genuineState.SlideshowPaths.Clear();
                }

                var monitors = EnumerateActiveMonitors(dw);
                foreach (var mon in monitors)
                {
                    int hr = dw.GetWallpaper(mon.DevicePath, out string? wp);
                    if (hr < 0) continue;

                    string normalized = (wp ?? "").Trim();
                    if (IsOurSlappedWallpaper(normalized))
                    {
                        // Do not overwrite an existing genuine backup with our own slapped wallpaper
                        continue;
                    }

                    long writeTicks = (!string.IsNullOrEmpty(normalized) && File.Exists(normalized))
                        ? File.GetLastWriteTimeUtc(normalized).Ticks
                        : 0;

                    string? backupPath = null;
                    if (!string.IsNullOrEmpty(normalized) && (backupVolatileSources || IsTranscodedWallpaperPath(normalized)))
                    {
                        backupPath = EnsureBackupCopyForSource(normalized);
                    }
                    else if (_genuineState.Monitors.TryGetValue(mon.DevicePath, out var existing) &&
                             string.Equals(existing.OriginalPath, normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        backupPath = existing.BackupImagePath;
                    }

                    _genuineState.Monitors[mon.DevicePath] = new MonitorWallpaperBackup
                    {
                        OriginalPath = normalized,
                        BackupImagePath = backupPath,
                        LastWriteTicksUtc = writeTicks
                    };

                    if (!string.IsNullOrEmpty(normalized) && !IsTranscodedWallpaperPath(normalized))
                    {
                        _genuineState.GlobalFallbackWallpaperPath = normalized;
                        if (backupPath != null) _genuineState.GlobalBackupBmpPath = backupPath;
                    }
                    else if (backupPath != null && string.IsNullOrEmpty(_genuineState.GlobalBackupBmpPath))
                    {
                        _genuineState.GlobalBackupBmpPath = backupPath;
                    }
                }

                if (string.IsNullOrEmpty(_genuineState.GlobalFallbackWallpaperPath))
                {
                    string? regWp = ResolveGenuineWallpaperPath();
                    if (!string.IsNullOrEmpty(regWp))
                    {
                        _genuineState.GlobalFallbackWallpaperPath = regWp;
                        if (backupVolatileSources)
                        {
                            _genuineState.GlobalBackupBmpPath = EnsureBackupCopyForSource(regWp);
                        }
                    }
                }
            }
            finally
            {
                ReleaseCom(dw);
            }
        }

        private static void EnsureNewMonitorsCapturedLocked()
        {
            IDesktopWallpaper? dw = CreateDesktopWallpaper();
            if (dw == null) return;
            try
            {
                var monitors = EnumerateActiveMonitors(dw);
                bool added = false;
                foreach (var mon in monitors)
                {
                    if (_genuineState.Monitors.ContainsKey(mon.DevicePath)) continue;
                    int hr = dw.GetWallpaper(mon.DevicePath, out string? wp);
                    if (hr < 0) continue;

                    string normalized = (wp ?? "").Trim();
                    if (IsOurSlappedWallpaper(normalized) || (_activeSlaps.Count > 0 && IsTranscodedWallpaperPath(normalized)))
                    {
                        continue;
                    }

                    string? backupPath = !string.IsNullOrEmpty(normalized) ? EnsureBackupCopyForSource(normalized) : null;
                    long writeTicks = (!string.IsNullOrEmpty(normalized) && File.Exists(normalized))
                        ? File.GetLastWriteTimeUtc(normalized).Ticks
                        : 0;

                    _genuineState.Monitors[mon.DevicePath] = new MonitorWallpaperBackup
                    {
                        OriginalPath = normalized,
                        BackupImagePath = backupPath,
                        LastWriteTicksUtc = writeTicks
                    };
                    added = true;
                }
                if (added) SavePersistedState();
            }
            finally
            {
                ReleaseCom(dw);
            }
        }

        private static string? EnsureBackupCopyForSource(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath) || IsOurSlappedWallpaper(sourcePath) || !File.Exists(sourcePath))
                return null;

            try
            {
                string dir = Path.Combine(GetStorageDir(), "wallpaper_backups");
                Directory.CreateDirectory(dir);

                string key = ComputeShortHash(sourcePath.ToLowerInvariant());
                string backupPath = Path.Combine(dir, $"genuine_{key}.bmp");

                DateTime srcWrite = File.GetLastWriteTimeUtc(sourcePath);
                if (File.Exists(backupPath) && File.GetLastWriteTimeUtc(backupPath) >= srcWrite)
                {
                    return backupPath;
                }

                using var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var src = Image.FromStream(fs);
                ApplyExifOrientation(src);
                using var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.DrawImage(src, 0, 0, src.Width, src.Height);
                }
                string tmp = backupPath + ".tmp";
                using (var outFs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    bmp.Save(outFs, ImageFormat.Bmp);
                }
                File.Move(tmp, backupPath, overwrite: true);
                return backupPath;
            }
            catch (Exception ex)
            {
                Log($"EnsureBackupCopyForSource error ({sourcePath}): {ex.Message}");
                return null;
            }
        }

        private static GenuineDesktopState BuildLegacyRecoveryState()
        {
            var state = new GenuineDesktopState
            {
                Position = GetRegistryWallpaperPosition(),
                BackgroundColorRef = GetRegistryBackgroundColorRef(),
                IsSlapActive = true
            };

            string? genuine = ResolveGenuineWallpaperPath();
            if (!string.IsNullOrEmpty(genuine))
            {
                state.GlobalFallbackWallpaperPath = genuine;
            }

            string legacyBackup = Path.Combine(GetStorageDir(), "genuine_wallpaper_backup.bmp");
            if (File.Exists(legacyBackup))
            {
                state.GlobalBackupBmpPath = legacyBackup;
            }

            return state;
        }

        private static GenuineDesktopState? LoadPersistedState()
        {
            try
            {
                if (!File.Exists(StateFilePath)) return null;
                string json = File.ReadAllText(StateFilePath);
                var state = JsonSerializer.Deserialize<GenuineDesktopState>(json);
                if (state != null)
                {
                    state.Monitors ??= new Dictionary<string, MonitorWallpaperBackup>(StringComparer.OrdinalIgnoreCase);
                    state.SlideshowPaths ??= new List<string>();
                }
                return state;
            }
            catch (Exception ex)
            {
                Log($"LoadPersistedState error: {ex.Message}");
                return null;
            }
        }

        private static void SavePersistedState()
        {
            try
            {
                string json = JsonSerializer.Serialize(_genuineState, new JsonSerializerOptions { WriteIndented = true });
                string tmp = StateFilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, StateFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log($"SavePersistedState error: {ex.Message}");
            }
        }

        // ── Per-Monitor Wallpaper Composition Engine ──────────────────────────

        private static bool UpdateDesktopWallpaperInternal()
        {
            if (_activeSlaps.Count == 0)
            {
                RestoreOriginalWallpaperInternal();
                return true;
            }

            IDesktopWallpaper? dw = CreateDesktopWallpaper();
            if (dw == null)
            {
                return UpdateDesktopWallpaperFallbackSpi();
            }

            _isApplyingComposedWallpaper = true;
            _lastOurChangeTimestamp = Environment.TickCount64;

            try
            {
                var monitors = EnumerateActiveMonitors(dw);
                if (monitors.Count == 0)
                {
                    Log("IDesktopWallpaper returned 0 active monitors; falling back to SPI.");
                    return UpdateDesktopWallpaperFallbackSpi();
                }

                Rectangle virtualScreen = ComputeVirtualBounds(monitors);
                Color bgColor = ColorTranslator.FromWin32((int)_genuineState.BackgroundColorRef);
                int genuinePosition = _genuineState.Position;
                bool virtualWideStyle = (genuinePosition == DWPOS_SPAN || genuinePosition == DWPOS_TILE);

                var orderedSlaps = _activeSlaps.Values.OrderBy(s => s.Order).ToList();
                var activeMonitorSet = new HashSet<string>(monitors.Select(m => m.DevicePath), StringComparer.OrdinalIgnoreCase);

                // Cache loaded base wallpaper images by resolved file path for this composition pass
                var loadedImages = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    // If user is in Span or Tile mode, temporarily switch to per-monitor Fill so every
                    // monitor displays its exact pre-sliced native M.Width x M.Height canvas 1:1.
                    if (virtualWideStyle && !_genuineState.TemporarilyChangedPosition)
                    {
                        int posHr = dw.SetPosition(DWPOS_FILL);
                        if (posHr == 0)
                        {
                            _genuineState.TemporarilyChangedPosition = true;
                            SavePersistedState();
                            Log($"Temporarily switched wallpaper position from {genuinePosition} to DWPOS_FILL for pixel-exact multi-monitor composition.");
                        }
                    }

                    bool anyApplied = false;

                    foreach (var mon in monitors)
                    {
                        var intersecting = orderedSlaps
                            .Where(s => mon.Bounds.IntersectsWith(s.TargetRect))
                            .ToList();

                        // In Span/Tile mode, because we switched Position -> Fill, every monitor needs
                        // its pre-sliced canvas even if it currently has 0 slaps.
                        bool mustRender = intersecting.Count > 0 || virtualWideStyle;

                        if (mustRender)
                        {
                            string? basePath = ResolveMonitorGenuineImagePath(mon.DevicePath);
                            string renderKey = BuildMonitorRenderKey(mon, virtualScreen, bgColor, basePath, genuinePosition, intersecting);

                            // Skip recompositing & flashing monitors whose rendered output hasn't changed
                            // and whose live wallpaper still matches our composed temp file.
                            if (_slappedMonitorIds.Contains(mon.DevicePath) &&
                                _monitorRenderKeys.TryGetValue(mon.DevicePath, out string? prevKey) &&
                                string.Equals(prevKey, renderKey, StringComparison.Ordinal) &&
                                _monitorTempFiles.TryGetValue(mon.DevicePath, out string? curTemp) &&
                                dw.GetWallpaper(mon.DevicePath, out string? liveWp) >= 0 &&
                                string.Equals(liveWp, curTemp, StringComparison.OrdinalIgnoreCase))
                            {
                                anyApplied = true;
                                continue;
                            }

                            Image? baseImg = LoadCachedImage(basePath, loadedImages);

                            string tempPath = GetNextTempWallpaperPath(mon.DevicePath);
                            ComposeMonitorBitmap(mon, virtualScreen, bgColor, baseImg, genuinePosition, intersecting, tempPath);

                            int hr = dw.SetWallpaper(mon.DevicePath, tempPath);
                            if (hr == 0)
                            {
                                anyApplied = true;
                                _slappedMonitorIds.Add(mon.DevicePath);
                                _monitorRenderKeys[mon.DevicePath] = renderKey;

                                if (_monitorTempFiles.TryGetValue(mon.DevicePath, out string? oldTemp) &&
                                    !string.Equals(oldTemp, tempPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    TryDeleteFile(oldTemp);
                                }
                                _monitorTempFiles[mon.DevicePath] = tempPath;

                                Log($"Applied monitor wallpaper: {ShortMonitorId(mon.DevicePath)} ({mon.Bounds.Width}x{mon.Bounds.Height} @ {mon.Bounds.X},{mon.Bounds.Y}, scale={mon.Scale:0.##}), slaps={intersecting.Count}");
                            }
                            else
                            {
                                Log($"IDesktopWallpaper.SetWallpaper failed for {ShortMonitorId(mon.DevicePath)}: hr=0x{hr:X8}");
                                TryDeleteFile(tempPath);
                            }
                        }
                        else if (_slappedMonitorIds.Contains(mon.DevicePath))
                        {
                            // This monitor previously had a slap (now moved or unslapped); restore it cleanly
                            RestoreSingleMonitorWallpaperLocked(dw, mon.DevicePath);
                            _slappedMonitorIds.Remove(mon.DevicePath);
                            _monitorRenderKeys.Remove(mon.DevicePath);

                            if (_monitorTempFiles.TryGetValue(mon.DevicePath, out string? oldTemp))
                            {
                                TryDeleteFile(oldTemp);
                                _monitorTempFiles.Remove(mon.DevicePath);
                            }
                        }
                    }

                    // Prune disconnected monitors from tracking sets
                    _slappedMonitorIds.RemoveWhere(id => !activeMonitorSet.Contains(id));
                    foreach (var staleId in _monitorRenderKeys.Keys.Where(id => !activeMonitorSet.Contains(id)).ToList())
                    {
                        _monitorRenderKeys.Remove(staleId);
                    }
                    foreach (var staleId in _monitorTempFiles.Keys.Where(id => !activeMonitorSet.Contains(id)).ToList())
                    {
                        TryDeleteFile(_monitorTempFiles[staleId]);
                        _monitorTempFiles.Remove(staleId);
                    }

                    return anyApplied || _slappedMonitorIds.Count > 0;
                }
                finally
                {
                    foreach (var img in loadedImages.Values)
                    {
                        try { img.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"UpdateDesktopWallpaperInternal error: {ex}");
                return false;
            }
            finally
            {
                ReleaseCom(dw);
                _isApplyingComposedWallpaper = false;
                _lastOurChangeTimestamp = Environment.TickCount64;
            }
        }

        private static void ComposeMonitorBitmap(
            ActiveMonitorInfo monitor,
            Rectangle virtualScreen,
            Color bgColor,
            Image? baseImg,
            int genuinePosition,
            List<SlapEntry> intersectingSlaps,
            string outputPath)
        {
            int w = Math.Max(1, monitor.Bounds.Width);
            int h = Math.Max(1, monitor.Bounds.Height);

            using var composed = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(composed))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.CompositingQuality = CompositingQuality.HighQuality;

                // 1. Fill with the user's actual desktop background color
                using (var bgBrush = new SolidBrush(bgColor))
                {
                    g.FillRectangle(bgBrush, 0, 0, w, h);
                }

                // 2. Draw this monitor's genuine base wallpaper using the user's exact Windows style
                if (baseImg != null && baseImg.Width > 0 && baseImg.Height > 0)
                {
                    DrawBaseWallpaperForMonitor(g, baseImg, monitor.Bounds, virtualScreen, genuinePosition);
                }

                // 3. Composite all active slaps intersecting this monitor in deterministic z-order
                double scale = monitor.Scale <= 0 ? 1.0 : monitor.Scale;
                int cornerRadius = Math.Max(4, (int)Math.Round(12.0 * scale));
                int shadowRadius = Math.Max(6, (int)Math.Round(14.0 * scale));
                int shadowPad = Math.Max(1, (int)Math.Round(2.0 * scale));
                int shadowOffY = Math.Max(1, (int)Math.Round(1.0 * scale));
                float borderWidth = Math.Max(1.0f, 1.5f * (float)scale);

                foreach (var slap in intersectingSlaps)
                {
                    int destX = slap.TargetRect.X - monitor.Bounds.X;
                    int destY = slap.TargetRect.Y - monitor.Bounds.Y;
                    int destW = slap.TargetRect.Width;
                    int destH = slap.TargetRect.Height;

                    var destRect = new Rectangle(destX, destY, destW, destH);
                    using var path = CreateRoundedRectanglePath(destRect, cornerRadius);

                    // Soft drop shadow behind slap
                    using (var shadowBrush = new SolidBrush(Color.FromArgb((int)Math.Round(60 * slap.Opacity), 0, 0, 0)))
                    using (var shadowPath = CreateRoundedRectanglePath(
                        new Rectangle(destX - shadowPad, destY - shadowOffY, destW + shadowPad * 2, destH + shadowPad * 2),
                        shadowRadius))
                    {
                        g.FillPath(shadowBrush, shadowPath);
                    }

                    // Clip to rounded corners and draw image
                    var state = g.Save();
                    try
                    {
                        g.SetClip(path, CombineMode.Intersect);
                        if (slap.Opacity >= 0.99f)
                        {
                            g.DrawImage(slap.Bitmap, destRect, 0, 0, slap.Bitmap.Width, slap.Bitmap.Height, GraphicsUnit.Pixel);
                        }
                        else
                        {
                            var matrix = new ColorMatrix { Matrix33 = slap.Opacity };
                            using var attrs = new ImageAttributes();
                            attrs.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                            g.DrawImage(slap.Bitmap, destRect, 0, 0, slap.Bitmap.Width, slap.Bitmap.Height, GraphicsUnit.Pixel, attrs);
                        }
                    }
                    finally
                    {
                        g.Restore(state);
                    }

                    // Subtle rounded accent outline matching minimal mode
                    using var pen = new Pen(Color.FromArgb((int)Math.Round(160 * slap.Opacity), 0, 120, 215), borderWidth);
                    g.DrawPath(pen, path);
                }
            }

            // Stream directly to disk without allocating a second copy via MemoryStream.ToArray()
            using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            composed.Save(outFs, ImageFormat.Bmp);
        }

        private static void DrawBaseWallpaperForMonitor(
            Graphics g,
            Image baseImg,
            Rectangle monBounds,
            Rectangle virtualScreen,
            int position)
        {
            int mw = monBounds.Width;
            int mh = monBounds.Height;
            int iw = baseImg.Width;
            int ih = baseImg.Height;

            switch (position)
            {
                case DWPOS_FILL:
                default:
                {
                    float monRatio = (float)mw / mh;
                    float imgRatio = (float)iw / ih;
                    float srcX = 0, srcY = 0, srcW = iw, srcH = ih;

                    if (imgRatio > monRatio)
                    {
                        srcW = ih * monRatio;
                        srcX = (iw - srcW) / 2f;
                    }
                    else
                    {
                        srcH = iw / monRatio;
                        srcY = (ih - srcH) / 2f;
                    }

                    g.DrawImage(baseImg,
                        new RectangleF(0, 0, mw, mh),
                        new RectangleF(srcX, srcY, srcW, srcH),
                        GraphicsUnit.Pixel);
                    break;
                }

                case DWPOS_FIT:
                {
                    float scale = Math.Min((float)mw / iw, (float)mh / ih);
                    float fitW = iw * scale;
                    float fitH = ih * scale;
                    float fitX = (mw - fitW) / 2f;
                    float fitY = (mh - fitH) / 2f;

                    g.DrawImage(baseImg,
                        new RectangleF(fitX, fitY, fitW, fitH),
                        new RectangleF(0, 0, iw, ih),
                        GraphicsUnit.Pixel);
                    break;
                }

                case DWPOS_STRETCH:
                {
                    g.DrawImage(baseImg,
                        new Rectangle(0, 0, mw, mh),
                        new Rectangle(0, 0, iw, ih),
                        GraphicsUnit.Pixel);
                    break;
                }

                case DWPOS_CENTER:
                {
                    int cX = (mw - iw) / 2;
                    int cY = (mh - ih) / 2;
                    g.DrawImage(baseImg, new Rectangle(cX, cY, iw, ih), new Rectangle(0, 0, iw, ih), GraphicsUnit.Pixel);
                    break;
                }

                case DWPOS_TILE:
                {
                    // Windows Tile mode anchors tiles at virtual desktop (0,0) (primary monitor origin).
                    using var tb = new TextureBrush(baseImg, WrapMode.Tile);
                    tb.TranslateTransform(-monBounds.X, -monBounds.Y);
                    g.FillRectangle(tb, 0, 0, mw, mh);
                    break;
                }

                case DWPOS_SPAN:
                {
                    // Windows Span mode fills the virtual screen bounding box (aspect-preserving cover)
                    // and displays each monitor's exact slice.
                    if (virtualScreen.Width <= 0 || virtualScreen.Height <= 0) break;

                    float vsRatio = (float)virtualScreen.Width / virtualScreen.Height;
                    float imgRatio = (float)iw / ih;
                    float coverX = 0, coverY = 0, coverW = iw, coverH = ih;

                    if (imgRatio > vsRatio)
                    {
                        coverW = ih * vsRatio;
                        coverX = (iw - coverW) / 2f;
                    }
                    else
                    {
                        coverH = iw / vsRatio;
                        coverY = (ih - coverH) / 2f;
                    }

                    // Map monitor's position within virtualScreen into source image coordinates
                    float relX = (float)(monBounds.X - virtualScreen.X) / virtualScreen.Width;
                    float relY = (float)(monBounds.Y - virtualScreen.Y) / virtualScreen.Height;
                    float relW = (float)mw / virtualScreen.Width;
                    float relH = (float)mh / virtualScreen.Height;

                    var srcSlice = new RectangleF(
                        coverX + relX * coverW,
                        coverY + relY * coverH,
                        relW * coverW,
                        relH * coverH);

                    g.DrawImage(baseImg, new RectangleF(0, 0, mw, mh), srcSlice, GraphicsUnit.Pixel);
                    break;
                }
            }
        }

        // ── Wallpaper Restoration ─────────────────────────────────────────────

        private static void RestoreOriginalWallpaperInternal(bool forceRestore = false)
        {
            try
            {
                bool needsRestore = forceRestore
                    || _genuineState.IsSlapActive
                    || _genuineState.TemporarilyChangedPosition
                    || _slappedMonitorIds.Count > 0
                    || IsAnyMonitorCurrentlySlapped();

                if (!needsRestore)
                {
                    RestoreAnimationDurationLocked();
                    CleanupTempFiles();
                    Log("RestoreOriginalWallpaperInternal: No slapped wallpaper active; nothing to restore.");
                    return;
                }

                Log($"RestoreOriginalWallpaperInternal: Restoring genuine desktop state (wasSlideshow={_genuineState.WasSlideshow}, pos={_genuineState.Position}, tempPosChanged={_genuineState.TemporarilyChangedPosition})");

                _isApplyingComposedWallpaper = true;
                _lastOurChangeTimestamp = Environment.TickCount64;
                bool restoredAnything = false;

                try
                {
                    IDesktopWallpaper? dw = CreateDesktopWallpaper();
                    if (dw != null)
                    {
                        try
                        {
                            // 1. Restore wallpaper position if we temporarily switched Span/Tile -> Fill
                            if (_genuineState.TemporarilyChangedPosition)
                            {
                                int posHr = dw.SetPosition(_genuineState.Position);
                                Log($"Restored wallpaper position to {_genuineState.Position}: hr=0x{posHr:X8}");
                                _genuineState.TemporarilyChangedPosition = false;
                                restoredAnything = true;
                            }

                            // 2. Restore Windows Slideshow if it was active prior to slapping
                            bool slideshowRestored = false;
                            if (_genuineState.WasSlideshow && _genuineState.SlideshowPaths.Count > 0)
                            {
                                slideshowRestored = TryRestoreSlideshow(dw, _genuineState.SlideshowPaths, _genuineState.SlideshowOptions, _genuineState.SlideshowTick);
                                if (slideshowRestored)
                                {
                                    restoredAnything = true;
                                    Log("Restored Windows Slideshow successfully.");
                                }
                            }

                            // 3. Otherwise restore per-monitor (or global for Span/Tile) wallpapers
                            if (!slideshowRestored)
                            {
                                bool virtualWideStyle = (_genuineState.Position == DWPOS_SPAN || _genuineState.Position == DWPOS_TILE);
                                var monitors = EnumerateActiveMonitors(dw);

                                if (virtualWideStyle)
                                {
                                    string? target = ResolveGlobalRestorePath();
                                    if (target != null)
                                    {
                                        int hr = dw.SetWallpaper(null, target);
                                        Log($"Restored global ({_genuineState.Position}) wallpaper: hr=0x{hr:X8}, path='{target}'");
                                        restoredAnything = true;
                                    }
                                }
                                else
                                {
                                    // Check if every active monitor resolves to the exact same genuine wallpaper path.
                                    // Using SetWallpaper(null, commonPath) restores all monitors in a single shell call
                                    // AND keeps HKCU\Control Panel\Desktop\Wallpaper pointing cleanly to the genuine file
                                    // instead of TranscodedWallpaper.
                                    var resolvedTargets = monitors
                                        .Select(m => ResolveMonitorRestorePath(m.DevicePath))
                                        .ToList();

                                    string? firstTarget = resolvedTargets.Count > 0 ? resolvedTargets[0] : null;
                                    bool allSame = firstTarget != null &&
                                        resolvedTargets.All(t => string.Equals(t, firstTarget, StringComparison.OrdinalIgnoreCase));

                                    if (allSame)
                                    {
                                        int hr = dw.SetWallpaper(null, firstTarget!);
                                        Log($"Restored unified genuine wallpaper across all {monitors.Count} monitor(s): hr=0x{hr:X8}, path='{(firstTarget!.Length == 0 ? "<SolidColor>" : firstTarget)}'");
                                        if (hr == 0) restoredAnything = true;
                                    }
                                    else
                                    {
                                        foreach (var mon in monitors)
                                        {
                                            bool monNeedsRestore = forceRestore || _slappedMonitorIds.Contains(mon.DevicePath);
                                            if (!monNeedsRestore)
                                            {
                                                if (dw.GetWallpaper(mon.DevicePath, out string? cur) >= 0 && IsOurSlappedWallpaper(cur))
                                                {
                                                    monNeedsRestore = true;
                                                }
                                            }

                                            if (monNeedsRestore)
                                            {
                                                if (RestoreSingleMonitorWallpaperLocked(dw, mon.DevicePath))
                                                {
                                                    restoredAnything = true;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        finally
                        {
                            ReleaseCom(dw);
                        }
                    }
                    else
                    {
                        // SPI fallback if IDesktopWallpaper is unavailable
                        string? target = ResolveGlobalRestorePath();
                        if (!string.IsNullOrEmpty(target) && File.Exists(target))
                        {
                            var restoreTask = System.Threading.Tasks.Task.Run(() =>
                                SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, target, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE));
                            bool completed = restoreTask.Wait(TimeSpan.FromSeconds(3.5));
                            int res = completed ? restoreTask.Result : 0;
                            Log($"SPI fallback restored genuine wallpaper: res={res}, completed={completed}, path={target}");
                            restoredAnything = true;
                        }
                    }
                }
                finally
                {
                    _isApplyingComposedWallpaper = false;
                    _lastOurChangeTimestamp = Environment.TickCount64;
                }

                _slappedMonitorIds.Clear();
                _monitorTempFiles.Clear();
                _monitorRenderKeys.Clear();
                _genuineState.IsSlapActive = false;
                _genuineState.TemporarilyChangedPosition = false;
                RestoreAnimationDurationLocked();
                SavePersistedState();
                CleanupTempFiles();

                if (restoredAnything)
                {
                    ForceDesktopShellRefresh();
                }
            }
            catch (Exception ex)
            {
                Log($"RestoreOriginalWallpaperInternal error: {ex}");
            }
        }

        private static bool RestoreSingleMonitorWallpaperLocked(IDesktopWallpaper dw, string monitorDevicePath)
        {
            string? target = ResolveMonitorRestorePath(monitorDevicePath);
            if (target == null)
            {
                Log($"Warning: No genuine wallpaper resolved to restore for {ShortMonitorId(monitorDevicePath)}");
                return false;
            }

            int hr = dw.SetWallpaper(monitorDevicePath, target);
            Log($"Restored monitor {ShortMonitorId(monitorDevicePath)} wallpaper: hr=0x{hr:X8}, path='{(target.Length == 0 ? "<SolidColor>" : target)}'");
            return hr == 0;
        }

        /// <summary>
        /// Resolves the genuine base image file to draw behind slaps for a monitor.
        /// Returns null when the monitor is configured as a Solid Color background.
        /// </summary>
        private static string? ResolveMonitorGenuineImagePath(string monitorDevicePath)
        {
            if (_genuineState.Monitors.TryGetValue(monitorDevicePath, out var backup))
            {
                // Empty string explicitly means Solid Color background — do NOT substitute another image!
                if (backup.OriginalPath != null && backup.OriginalPath.Length == 0)
                {
                    return null;
                }

                if (!string.IsNullOrEmpty(backup.OriginalPath) &&
                    !IsOurSlappedWallpaper(backup.OriginalPath) &&
                    !IsTranscodedWallpaperPath(backup.OriginalPath) &&
                    File.Exists(backup.OriginalPath))
                {
                    return backup.OriginalPath;
                }

                if (!string.IsNullOrEmpty(backup.BackupImagePath) && File.Exists(backup.BackupImagePath))
                {
                    return backup.BackupImagePath;
                }
            }

            if (!string.IsNullOrEmpty(_genuineState.GlobalFallbackWallpaperPath) &&
                !IsOurSlappedWallpaper(_genuineState.GlobalFallbackWallpaperPath) &&
                !IsTranscodedWallpaperPath(_genuineState.GlobalFallbackWallpaperPath) &&
                File.Exists(_genuineState.GlobalFallbackWallpaperPath))
            {
                return _genuineState.GlobalFallbackWallpaperPath;
            }

            if (!string.IsNullOrEmpty(_genuineState.GlobalBackupBmpPath) && File.Exists(_genuineState.GlobalBackupBmpPath))
            {
                return _genuineState.GlobalBackupBmpPath;
            }

            return ResolveGenuineWallpaperPath();
        }

        /// <summary>
        /// Resolves the exact string to pass to IDesktopWallpaper.SetWallpaper on restore.
        /// Returns "" for Solid Color, a valid file path for picture wallpapers, or null if unknown.
        /// </summary>
        private static string? ResolveMonitorRestorePath(string monitorDevicePath)
        {
            if (_genuineState.Monitors.TryGetValue(monitorDevicePath, out var backup))
            {
                if (backup.OriginalPath != null && backup.OriginalPath.Length == 0)
                {
                    return "";
                }

                if (!string.IsNullOrEmpty(backup.OriginalPath) &&
                    !IsOurSlappedWallpaper(backup.OriginalPath) &&
                    !IsTranscodedWallpaperPath(backup.OriginalPath) &&
                    File.Exists(backup.OriginalPath))
                {
                    return backup.OriginalPath;
                }

                if (!string.IsNullOrEmpty(backup.BackupImagePath) && File.Exists(backup.BackupImagePath))
                {
                    return backup.BackupImagePath;
                }
            }

            return ResolveGlobalRestorePath();
        }

        private static string? ResolveGlobalRestorePath()
        {
            // If every known monitor was Solid Color, restore Solid Color ("")
            if (_genuineState.Monitors.Count > 0 &&
                _genuineState.Monitors.Values.All(m => m.OriginalPath != null && m.OriginalPath.Length == 0))
            {
                return "";
            }

            if (!string.IsNullOrEmpty(_genuineState.GlobalFallbackWallpaperPath) &&
                !IsOurSlappedWallpaper(_genuineState.GlobalFallbackWallpaperPath) &&
                !IsTranscodedWallpaperPath(_genuineState.GlobalFallbackWallpaperPath) &&
                File.Exists(_genuineState.GlobalFallbackWallpaperPath))
            {
                return _genuineState.GlobalFallbackWallpaperPath;
            }

            if (!string.IsNullOrEmpty(_genuineState.GlobalBackupBmpPath) && File.Exists(_genuineState.GlobalBackupBmpPath))
            {
                return _genuineState.GlobalBackupBmpPath;
            }

            foreach (var backup in _genuineState.Monitors.Values)
            {
                if (!string.IsNullOrEmpty(backup.OriginalPath) &&
                    !IsOurSlappedWallpaper(backup.OriginalPath) &&
                    !IsTranscodedWallpaperPath(backup.OriginalPath) &&
                    File.Exists(backup.OriginalPath))
                {
                    return backup.OriginalPath;
                }
                if (!string.IsNullOrEmpty(backup.BackupImagePath) && File.Exists(backup.BackupImagePath))
                {
                    return backup.BackupImagePath;
                }
            }

            return ResolveGenuineWallpaperPath();
        }

        // ── SPI Fallback (only if IDesktopWallpaper COM is unavailable) ───────

        private static bool UpdateDesktopWallpaperFallbackSpi()
        {
            try
            {
                Rectangle vs = DisplayInfo.VirtualScreen;
                if (vs.Width <= 0 || vs.Height <= 0) return false;

                Color bgColor = ColorTranslator.FromWin32((int)_genuineState.BackgroundColorRef);
                string? sourceWp = ResolveGlobalRestorePath();
                string tempPath = GetNextTempWallpaperPath("spi");

                var loaded = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    Image? baseImg = LoadCachedImage(sourceWp, loaded);
                    var primaryMon = DisplayInfo.All().FirstOrDefault(m => m.IsPrimary);
                    var pseudoMon = new ActiveMonitorInfo
                    {
                        DevicePath = "spi",
                        Bounds = vs,
                        Scale = primaryMon?.Scale ?? 1.0
                    };
                    var orderedSlaps = _activeSlaps.Values.OrderBy(s => s.Order).ToList();
                    ComposeMonitorBitmap(pseudoMon, vs, bgColor, baseImg, _genuineState.Position, orderedSlaps, tempPath);
                }
                finally
                {
                    foreach (var img in loaded.Values) { try { img.Dispose(); } catch { } }
                }

                _isApplyingComposedWallpaper = true;
                _lastOurChangeTimestamp = Environment.TickCount64;
                try
                {
                    int res = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, tempPath, SPIF_SENDCHANGE);
                    Log($"SPI fallback applied wallpaper: res={res}, temp={Path.GetFileName(tempPath)}");
                    if (_monitorTempFiles.TryGetValue("spi", out string? oldTemp))
                    {
                        TryDeleteFile(oldTemp);
                    }
                    _monitorTempFiles["spi"] = tempPath;
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
                Log($"UpdateDesktopWallpaperFallbackSpi error: {ex}");
                return false;
            }
        }

        // ── Desktop Shell Refresh ─────────────────────────────────────────────

        /// <summary>
        /// Forces Windows Explorer to refresh desktop icons and clear stuck WorkerW transition layers.
        /// </summary>
        public static void ForceDesktopShellRefresh()
        {
            try
            {
                Log("Executing ForceDesktopShellRefresh...");

                Win32.SHChangeNotify(Win32.SHCNE_ASSOCCHANGED, Win32.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

                IntPtr progman = Win32.FindWindow("Progman", null);
                if (progman != IntPtr.Zero)
                {
                    Win32.PostMessage(progman, 0x0111 /* WM_COMMAND */, (IntPtr)28931, IntPtr.Zero);
                }

                IntPtr shellView = IntPtr.Zero;
                if (progman != IntPtr.Zero)
                {
                    shellView = Win32.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                }

                if (shellView == IntPtr.Zero)
                {
                    Win32.EnumWindows((hWnd, lParam) =>
                    {
                        IntPtr sv = Win32.FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                        if (sv != IntPtr.Zero)
                        {
                            shellView = sv;
                            return false;
                        }
                        return true;
                    }, IntPtr.Zero);
                }

                if (shellView != IntPtr.Zero)
                {
                    IntPtr listView = Win32.FindWindowEx(shellView, IntPtr.Zero, "SysListView32", null);
                    if (listView != IntPtr.Zero)
                    {
                        Win32.InvalidateRect(listView, IntPtr.Zero, true);
                        Win32.UpdateWindow(listView);
                    }
                    Win32.InvalidateRect(shellView, IntPtr.Zero, true);
                    Win32.UpdateWindow(shellView);
                    Win32.PostMessage(shellView, 0x0111 /* WM_COMMAND */, (IntPtr)28931, IntPtr.Zero);
                }

                IntPtr desktop = Win32.GetDesktopWindow();
                if (desktop != IntPtr.Zero)
                {
                    Win32.InvalidateRect(desktop, IntPtr.Zero, true);
                }

                Log("ForceDesktopShellRefresh completed.");
            }
            catch (Exception ex)
            {
                Log($"ForceDesktopShellRefresh error: {ex.Message}");
            }
        }

        // ── COM & Monitor Enumeration Helpers ─────────────────────────────────

        private static IDesktopWallpaper? CreateDesktopWallpaper()
        {
            try
            {
                Type? comType = Type.GetTypeFromCLSID(CLSID_DesktopWallpaper);
                if (comType == null) return null;
                return Activator.CreateInstance(comType) as IDesktopWallpaper;
            }
            catch (Exception ex)
            {
                Log($"CreateDesktopWallpaper failed: {ex.Message}");
                return null;
            }
        }

        private static void ReleaseCom(object? obj)
        {
            if (obj == null) return;
            try { Marshal.FinalReleaseComObject(obj); } catch { }
        }

        private static List<ActiveMonitorInfo> EnumerateActiveMonitors(IDesktopWallpaper dw)
        {
            var result = new List<ActiveMonitorInfo>();
            var displayEntries = DisplayInfo.All();

            if (dw.GetMonitorDevicePathCount(out uint count) != 0 || count == 0)
            {
                return result;
            }

            for (uint i = 0; i < count; i++)
            {
                if (dw.GetMonitorDevicePathAt(i, out string? devPath) != 0 || string.IsNullOrEmpty(devPath))
                    continue;

                // Disconnected monitors return S_FALSE or an error HRESULT here — skip them cleanly.
                if (dw.GetMonitorRECT(devPath, out var rc) != 0)
                    continue;

                var bounds = DisplayInfo.ToRect(rc);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    continue;

                // Match DPI scale from DisplayInfo by exact bounds or largest overlap
                double scale = 1.0;
                var exactMatch = displayEntries.FirstOrDefault(d => d.Bounds == bounds);
                if (exactMatch != null)
                {
                    scale = exactMatch.Scale;
                }
                else
                {
                    var fromRect = DisplayInfo.FromRect(bounds);
                    scale = fromRect.Scale > 0 ? fromRect.Scale : 1.0;
                }

                result.Add(new ActiveMonitorInfo
                {
                    DevicePath = devPath,
                    Bounds = bounds,
                    Scale = scale
                });
            }

            return result;
        }

        private static Rectangle ComputeVirtualBounds(List<ActiveMonitorInfo> monitors)
        {
            if (monitors.Count == 0) return DisplayInfo.VirtualScreen;
            int minX = monitors.Min(m => m.Bounds.Left);
            int minY = monitors.Min(m => m.Bounds.Top);
            int maxX = monitors.Max(m => m.Bounds.Right);
            int maxY = monitors.Max(m => m.Bounds.Bottom);
            return new Rectangle(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
        }

        private static bool IsAnyMonitorCurrentlySlapped()
        {
            IDesktopWallpaper? dw = CreateDesktopWallpaper();
            if (dw != null)
            {
                try
                {
                    var monitors = EnumerateActiveMonitors(dw);
                    foreach (var mon in monitors)
                    {
                        if (dw.GetWallpaper(mon.DevicePath, out string? wp) >= 0 && IsOurSlappedWallpaper(wp))
                            return true;
                    }
                }
                finally
                {
                    ReleaseCom(dw);
                }
            }

            return IsOurSlappedWallpaper(QuerySystemWallpaperDirect());
        }

        private static List<string> ReadSlideshowPaths(IDesktopWallpaper dw, out int options, out uint tick)
        {
            options = 0;
            tick = 0;
            var paths = new List<string>();
            try
            {
                dw.GetSlideshowOptions(out options, out tick);
                if (dw.GetSlideshow(out IntPtr arrPtr) == 0 && arrPtr != IntPtr.Zero)
                {
                    try
                    {
                        var arr = (IShellItemArray)Marshal.GetObjectForIUnknown(arrPtr);
                        try
                        {
                            if (arr.GetCount(out uint count) == 0)
                            {
                                for (uint i = 0; i < count; i++)
                                {
                                    if (arr.GetItemAt(i, out var item) == 0 && item != null)
                                    {
                                        try
                                        {
                                            if (item.GetDisplayName(SIGDN_FILESYSPATH, out string? path) == 0 && !string.IsNullOrWhiteSpace(path))
                                            {
                                                paths.Add(path);
                                            }
                                        }
                                        finally
                                        {
                                            ReleaseCom(item);
                                        }
                                    }
                                }
                            }
                        }
                        finally
                        {
                            ReleaseCom(arr);
                        }
                    }
                    finally
                    {
                        Marshal.Release(arrPtr);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"ReadSlideshowPaths error: {ex.Message}");
            }
            return paths;
        }

        private static bool TryRestoreSlideshow(IDesktopWallpaper dw, List<string> paths, int options, uint tick)
        {
            var pidls = new List<IntPtr>();
            IntPtr arrPtr = IntPtr.Zero;
            try
            {
                foreach (var p in paths)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    if (!Directory.Exists(p) && !File.Exists(p)) continue;
                    IntPtr pidl = ILCreateFromPathW(p);
                    if (pidl != IntPtr.Zero) pidls.Add(pidl);
                }

                if (pidls.Count == 0) return false;

                int hr = SHCreateShellItemArrayFromIDLists((uint)pidls.Count, pidls.ToArray(), out arrPtr);
                if (hr != 0 || arrPtr == IntPtr.Zero) return false;

                int setHr = dw.SetSlideshow(arrPtr);
                if (setHr == 0 && tick > 0)
                {
                    dw.SetSlideshowOptions(options, tick);
                }
                return setHr == 0;
            }
            catch (Exception ex)
            {
                Log($"TryRestoreSlideshow error: {ex.Message}");
                return false;
            }
            finally
            {
                if (arrPtr != IntPtr.Zero)
                {
                    try { Marshal.Release(arrPtr); } catch { }
                }
                foreach (var pidl in pidls)
                {
                    try { ILFree(pidl); } catch { }
                }
            }
        }

        // ── Legacy / Registry Helpers ─────────────────────────────────────────

        private static int GetRegistryWallpaperPosition()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                string? styleStr = key?.GetValue("WallpaperStyle") as string;
                string? tileStr = key?.GetValue("TileWallpaper") as string;

                if (tileStr == "1") return DWPOS_TILE;
                return styleStr switch
                {
                    "0"  => DWPOS_CENTER,
                    "2"  => DWPOS_STRETCH,
                    "6"  => DWPOS_FIT,
                    "22" => DWPOS_SPAN,
                    _    => DWPOS_FILL
                };
            }
            catch
            {
                return DWPOS_FILL;
            }
        }

        private static uint GetRegistryBackgroundColorRef()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
                if (key?.GetValue("Background") is string rgbStr)
                {
                    var parts = rgbStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 &&
                        byte.TryParse(parts[0], out byte r) &&
                        byte.TryParse(parts[1], out byte g) &&
                        byte.TryParse(parts[2], out byte b))
                    {
                        return (uint)(r | (g << 8) | (b << 16));
                    }
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Resolves a genuine non-slapped wallpaper file path from registry/cache sources.
        /// Never returns TranscodedWallpaper while slaps are active.
        /// </summary>
        public static string? ResolveGenuineWallpaperPath()
        {
            bool allowTranscoded;
            lock (_syncLock)
            {
                allowTranscoded = _activeSlaps.Count == 0 && !_genuineState.IsSlapActive;
            }

            // 1. Registry HKCU\Control Panel\Desktop\Wallpaper
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                if (key?.GetValue("Wallpaper") is string regWp)
                {
                    regWp = regWp.Trim();
                    if (regWp.Length == 0)
                    {
                        // Explicit empty string means Solid Color desktop background
                        return null;
                    }
                    if (!IsOurSlappedWallpaper(regWp) &&
                        (allowTranscoded || !IsTranscodedWallpaperPath(regWp)) &&
                        File.Exists(regWp))
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
                    string cachedPath = Encoding.Unicode.GetString(cacheBytes, 24, cacheBytes.Length - 24);
                    int nullIdx = cachedPath.IndexOf('\0');
                    if (nullIdx > 0) cachedPath = cachedPath.Substring(0, nullIdx).Trim();
                    if (!string.IsNullOrEmpty(cachedPath) &&
                        !IsOurSlappedWallpaper(cachedPath) &&
                        (allowTranscoded || !IsTranscodedWallpaperPath(cachedPath)) &&
                        File.Exists(cachedPath))
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
                            if (!IsOurSlappedWallpaper(histPath) && !IsTranscodedWallpaperPath(histPath) && File.Exists(histPath))
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
                string? spi = QuerySystemWallpaperDirect();
                if (!string.IsNullOrEmpty(spi) &&
                    !IsOurSlappedWallpaper(spi) &&
                    (allowTranscoded || !IsTranscodedWallpaperPath(spi)) &&
                    File.Exists(spi))
                {
                    return spi;
                }
            }
            catch { }

            // 5. Legacy genuine cache files
            try
            {
                string dir = GetStorageDir();
                string pathFile = Path.Combine(dir, "genuine_wallpaper_path.txt");
                if (File.Exists(pathFile))
                {
                    string savedPath = File.ReadAllText(pathFile).Trim();
                    if (!string.IsNullOrEmpty(savedPath) &&
                        !IsOurSlappedWallpaper(savedPath) &&
                        (allowTranscoded || !IsTranscodedWallpaperPath(savedPath)) &&
                        File.Exists(savedPath))
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
            if (path.StartsWith(appDir, StringComparison.OrdinalIgnoreCase) &&
                fileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) &&
                fileName.Contains("slapped", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static bool IsTranscodedWallpaperPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string fileName = Path.GetFileName(path);
            return fileName.StartsWith("TranscodedWallpaper", StringComparison.OrdinalIgnoreCase);
        }

        // ── Image & Path Helpers ──────────────────────────────────────────────

        private static Image? LoadCachedImage(string? path, Dictionary<string, Image> cache)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            if (cache.TryGetValue(path, out var existing)) return existing;

            try
            {
                // Copy into MemoryStream so we never keep a file lock on the user's wallpaper file
                byte[] bytes = File.ReadAllBytes(path);
                var ms = new MemoryStream(bytes);
                try
                {
                    using var raw = Image.FromStream(ms, useEmbeddedColorManagement: true, validateImageData: false);
                    ApplyExifOrientation(raw);
                    var copy = new Bitmap(raw.Width, raw.Height, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(copy))
                    {
                        g.DrawImage(raw, 0, 0, raw.Width, raw.Height);
                    }
                    cache[path] = copy;
                    return copy;
                }
                finally
                {
                    ms.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log($"LoadCachedImage failed ({path}): {ex.Message}");
                return null;
            }
        }

        private static void ApplyExifOrientation(Image img)
        {
            const int ExifOrientationId = 0x0112;
            try
            {
                if (img.PropertyIdList == null || !img.PropertyIdList.Contains(ExifOrientationId)) return;
                var prop = img.GetPropertyItem(ExifOrientationId);
                if (prop?.Value == null || prop.Value.Length < 2) return;

                ushort orientation = BitConverter.ToUInt16(prop.Value, 0);
                RotateFlipType rf = orientation switch
                {
                    2 => RotateFlipType.RotateNoneFlipX,
                    3 => RotateFlipType.Rotate180FlipNone,
                    4 => RotateFlipType.Rotate180FlipX,
                    5 => RotateFlipType.Rotate90FlipX,
                    6 => RotateFlipType.Rotate90FlipNone,
                    7 => RotateFlipType.Rotate270FlipX,
                    8 => RotateFlipType.Rotate270FlipNone,
                    _ => RotateFlipType.RotateNoneFlipNone
                };

                if (rf != RotateFlipType.RotateNoneFlipNone)
                {
                    img.RotateFlip(rf);
                }
                try { img.RemovePropertyItem(ExifOrientationId); } catch { }
            }
            catch { }
        }

        private static string GetNextTempWallpaperPath(string monitorKey)
        {
            string dir = GetStorageDir();
            string tag = ComputeShortHash(monitorKey);
            return Path.Combine(dir, $"slapped_desktop_wallpaper_{tag}_{Guid.NewGuid():N}.bmp");
        }

        private static string BuildMonitorRenderKey(
            ActiveMonitorInfo mon,
            Rectangle virtualScreen,
            Color bgColor,
            string? basePath,
            int genuinePosition,
            List<SlapEntry> intersectingSlaps)
        {
            var sb = new StringBuilder();
            sb.Append(mon.Bounds.X).Append(',').Append(mon.Bounds.Y).Append(',')
              .Append(mon.Bounds.Width).Append('x').Append(mon.Bounds.Height)
              .Append('|').Append(mon.Scale.ToString("0.###"))
              .Append('|').Append(virtualScreen.X).Append(',').Append(virtualScreen.Y).Append(',')
              .Append(virtualScreen.Width).Append('x').Append(virtualScreen.Height)
              .Append('|').Append(bgColor.ToArgb())
              .Append('|').Append(genuinePosition)
              .Append('|').Append(basePath ?? "");

            if (!string.IsNullOrEmpty(basePath) && File.Exists(basePath))
            {
                try { sb.Append('@').Append(File.GetLastWriteTimeUtc(basePath).Ticks); } catch { }
            }

            foreach (var s in intersectingSlaps)
            {
                sb.Append(';').Append(s.Id).Append('#').Append(s.Version)
                  .Append(':').Append(s.TargetRect.X).Append(',').Append(s.TargetRect.Y)
                  .Append(',').Append(s.TargetRect.Width).Append('x').Append(s.TargetRect.Height)
                  .Append('@').Append(s.Opacity.ToString("0.###"));
            }

            return sb.ToString();
        }

        private static string ComputeShortHash(string input)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        }

        private static string ShortMonitorId(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return "?";
            int hashIdx = devicePath.IndexOf('#');
            if (hashIdx >= 0 && hashIdx + 1 < devicePath.Length)
            {
                int second = devicePath.IndexOf('#', hashIdx + 1);
                if (second > hashIdx) return devicePath.Substring(hashIdx + 1, second - hashIdx - 1);
            }
            return devicePath.Length > 16 ? devicePath.Substring(0, 16) : devicePath;
        }

        private static void TryDeleteFile(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
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
                    TryDeleteFile(file);
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
            if (d <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }

            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ── Hidden Broadcast Listener Window ──────────────────────────────────

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
                if (m.Msg == WM_DISPLAYCHANGE)
                {
                    ScheduleDisplayTopologyRefresh();
                }
                else if (m.Msg == WM_SETTINGCHANGE)
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
