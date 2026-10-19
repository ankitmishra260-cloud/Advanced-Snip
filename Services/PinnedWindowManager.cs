using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Metadata for a persistent pinned image on the desktop.
    /// </summary>
    public class PinnedItemRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string? FilePath { get; set; }
        public string? CachedImagePath { get; set; }
        public string? Caption { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double ZoomFactor { get; set; } = 1.0;
        public bool IsTopmost { get; set; } = true;
        public bool IsDesktopPinned { get; set; } = false;
        public bool IsFrameless { get; set; } = false;
        public string StretchMode { get; set; } = "Uniform";
        public double WindowOpacity { get; set; } = 1.0;
        public double ImageOpacity { get; set; } = 1.0;
        public int RotationAngle { get; set; } = 0;
        public bool FlipHorizontal { get; set; } = false;
        public bool FlipVertical { get; set; } = false;
        public bool IsSlappedToDesktop { get; set; } = false;
    }

    /// <summary>
    /// Manages pinned windows, desktop persistence across restarts,
    /// multi-monitor validation, and visual overlap detection.
    /// </summary>
    public static class PinnedWindowManager
    {
        private static readonly Dictionary<string, PinnedItemRecord> _records = new();
        private static readonly HashSet<PinnedClipWindow> _activeWindows = new();
        private static readonly object _lock = new();

        private static string StorageDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AdvancedSnip", "pins");

        private static string StorageFile => Path.Combine(StorageDir, "pins.json");
        private static string ImagesDir => Path.Combine(StorageDir, "images");

        private static bool _initialized;
        private static bool _restored;

        public static IReadOnlyList<PinnedClipWindow> ActiveWindows
        {
            get
            {
                lock (_lock) return _activeWindows.ToList();
            }
        }

        private static void EnsureLoaded()
        {
            lock (_lock)
            {
                if (_initialized) return;
                _initialized = true;

                try
                {
                    if (!File.Exists(StorageFile)) return;

                    string json = File.ReadAllText(StorageFile);
                    var list = JsonSerializer.Deserialize<List<PinnedItemRecord>>(json);
                    if (list != null)
                    {
                        foreach (var r in list)
                        {
                            if (r != null && !string.IsNullOrEmpty(r.Id))
                            {
                                _records[r.Id] = r;
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore parse errors on corrupted file
                }
            }
        }

        public static void RegisterRecord(PinnedItemRecord record)
        {
            if (record == null) return;
            EnsureLoaded();
            lock (_lock)
            {
                _records[record.Id] = record;
            }
            SaveAll();
        }

        public static void UpdateRecord(PinnedItemRecord record)
        {
            if (record == null) return;
            EnsureLoaded();
            lock (_lock)
            {
                _records[record.Id] = record;
            }
            SaveAll();
        }

        public static void Register(PinnedClipWindow win)
        {
            EnsureLoaded();
            lock (_lock)
            {
                _activeWindows.Add(win);
                if (win.Record != null)
                {
                    _records[win.Record.Id] = win.Record;
                }
            }
            CheckOverlaps();
        }

        public static void Unregister(PinnedClipWindow win)
        {
            lock (_lock)
            {
                _activeWindows.Remove(win);
            }
            CheckOverlaps();
        }

        /// <summary>
        /// Saves an image cache to disk so the screenshot can always be restored,
        /// even if the original capture is moved or deleted.
        /// </summary>
        public static string EnsureImagePersisted(string id, BitmapSource image, string? existingPath)
        {
            try
            {
                if (!Directory.Exists(ImagesDir))
                {
                    Directory.CreateDirectory(ImagesDir);
                }

                string target = Path.Combine(ImagesDir, $"{id}.png");
                if (!File.Exists(target))
                {
                    using var stream = new FileStream(target, FileMode.Create, FileAccess.Write);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(stream);
                }
                return target;
            }
            catch
            {
                return existingPath ?? "";
            }
        }

        /// <summary>
        /// Saves all desktop-pinned records to pins.json.
        /// Preserves records even when windows are closed during app shutdown.
        /// </summary>
        public static void SaveAll()
        {
            EnsureLoaded();
            lock (_lock)
            {
                try
                {
                    if (!Directory.Exists(StorageDir))
                    {
                        Directory.CreateDirectory(StorageDir);
                    }

                    var toSave = _records.Values
                        .Where(r => r.IsDesktopPinned && r.Width > 0 && r.Height > 0)
                        .ToList();

                    string json = JsonSerializer.Serialize(toSave, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                    string temp = StorageFile + ".tmp";
                    File.WriteAllText(temp, json);
                    File.Move(temp, StorageFile, overwrite: true);
                }
                catch
                {
                    // Best effort persistence
                }
            }
        }

        /// <summary>
        /// Restores all persistent desktop pins saved from previous sessions.
        /// Multi-monitor safe: ensures pins appear within visible screen work areas.
        /// </summary>
        public static void RestoreAll()
        {
            if (_restored) return;
            _restored = true;

            EnsureLoaded();

            List<PinnedItemRecord> toRestore;
            lock (_lock)
            {
                toRestore = _records.Values.Where(r => r.IsDesktopPinned).ToList();
            }

            foreach (var record in toRestore)
            {
                try
                {
                    // Restored desktop-pinned images must always have topmost set to false on app restart
                    record.IsTopmost = false;

                    string? loadPath = null;
                    if (!string.IsNullOrEmpty(record.CachedImagePath) && File.Exists(record.CachedImagePath))
                    {
                        loadPath = record.CachedImagePath;
                    }
                    else if (!string.IsNullOrEmpty(record.FilePath) && File.Exists(record.FilePath))
                    {
                        loadPath = record.FilePath;
                    }

                    if (string.IsNullOrEmpty(loadPath)) continue;

                    BitmapSource image = ImageInterop.LoadBitmap(loadPath);
                    EnsureVisibleOnScreen(record);

                    var win = new PinnedClipWindow(image, record.FilePath, record.Caption, record);
                    win.ShowActivated = false;
                    win.Show();

                    var handle = new WindowInteropHelper(win).Handle;
                    if (handle != IntPtr.Zero)
                    {
                        Win32.SetWindowPos(handle, Win32.HWND_BOTTOM, 0, 0, 0, 0,
                            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                    }

                    win.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
                    {
                        var h = new WindowInteropHelper(win).Handle;
                        if (h != IntPtr.Zero)
                        {
                            Win32.SetWindowPos(h, Win32.HWND_BOTTOM, 0, 0, 0, 0,
                                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                        }
                    }));
                }
                catch
                {
                    // Skip corrupted pin record
                }
            }

            CheckOverlaps();
            SaveAll();
        }

        /// <summary>
        /// Removes a persistent record and its cached image when unpinned.
        /// </summary>
        public static void RemoveRecord(string pinId)
        {
            EnsureLoaded();
            lock (_lock)
            {
                _records.Remove(pinId);
            }

            try
            {
                string cachedImg = Path.Combine(ImagesDir, $"{pinId}.png");
                if (File.Exists(cachedImg))
                {
                    File.Delete(cachedImg);
                }
            }
            catch
            {
                // Best effort delete
            }

            SaveAll();
        }

        /// <summary>
        /// Validates that the window rectangle intersects a visible monitor work area.
        /// If monitor setup changed (e.g. unplugged external monitor), repositions to primary screen.
        /// </summary>
        public static void EnsureVisibleOnScreen(PinnedItemRecord record)
        {
            var monitors = DisplayInfo.All();
            if (monitors.Count == 0) return;

            var windowRect = new Rectangle((int)record.X, (int)record.Y, (int)record.Width, (int)record.Height);
            bool intersectsAny = false;

            foreach (var mon in monitors)
            {
                var intersect = Rectangle.Intersect(mon.WorkArea, windowRect);
                if (intersect.Width >= 60 && intersect.Height >= 60)
                {
                    intersectsAny = true;
                    break;
                }
            }

            if (!intersectsAny)
            {
                var targetMon = DisplayInfo.All().FirstOrDefault(m => m.IsPrimary) ?? DisplayInfo.FromCursor();
                var wa = targetMon.WorkArea;
                record.Width = Math.Clamp(record.Width, 240, wa.Width * 0.85);
                record.Height = Math.Clamp(record.Height, 160, wa.Height * 0.85);
                record.X = wa.X + (wa.Width - record.Width) / 2;
                record.Y = wa.Y + (wa.Height - record.Height) / 2;
            }
        }

        /// <summary>
        /// Detects when any two pinned windows overlap and updates visual stacking cues.
        /// </summary>
        public static void CheckOverlaps()
        {
            List<PinnedClipWindow> windows;
            lock (_lock)
            {
                windows = _activeWindows.Where(w => w.IsLoaded && w.IsVisible).ToList();
            }

            if (windows.Count <= 1)
            {
                foreach (var w in windows)
                {
                    w.SetOverlapState(false, 0);
                }
                return;
            }

            var rects = new Dictionary<PinnedClipWindow, Rectangle>();
            foreach (var w in windows)
            {
                rects[w] = WindowPlacement.GetPhysicalRect(w);
            }

            for (int i = 0; i < windows.Count; i++)
            {
                var winA = windows[i];
                var rectA = rects[winA];
                if (rectA.IsEmpty) continue;

                bool isOverlapping = false;
                int overlapCount = 0;

                for (int j = 0; j < windows.Count; j++)
                {
                    if (i == j) continue;
                    var winB = windows[j];
                    var rectB = rects[winB];
                    if (rectB.IsEmpty) continue;

                    var hit = Rectangle.Intersect(rectA, rectB);
                    if (hit.Width > 24 && hit.Height > 24)
                    {
                        isOverlapping = true;
                        overlapCount++;
                    }
                }

                winA.SetOverlapState(isOverlapping, overlapCount);
            }
        }
    }
}
