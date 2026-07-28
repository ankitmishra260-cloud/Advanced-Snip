using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace AdvancedSnip.Services
{
    public sealed class HotkeyDef
    {
        public int Modifiers { get; set; }
        public int Key       { get; set; }

        public HotkeyDef() { }
        public HotkeyDef(ModifierKeys modifiers, System.Windows.Input.Key key)
        {
            Modifiers = (int)modifiers;
            Key = (int)key;
        }

        [JsonIgnore]
        public bool IsValid => Key != 0 && Modifiers != 0;

        public override string ToString()
        {
            if (Key == 0) return "(none)";
            var m = (ModifierKeys)Modifiers;
            var parts = new List<string>();
            if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (m.HasFlag(ModifierKeys.Alt))     parts.Add("Alt");
            if (m.HasFlag(ModifierKeys.Shift))   parts.Add("Shift");
            if (m.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(KeyLabel((System.Windows.Input.Key)Key));
            return string.Join(" + ", parts);
        }

        private static string KeyLabel(System.Windows.Input.Key key) => key switch
        {
            System.Windows.Input.Key.OemComma        => ",",
            System.Windows.Input.Key.OemPeriod       => ".",
            System.Windows.Input.Key.OemQuestion     => "/",
            System.Windows.Input.Key.OemSemicolon    => ";",
            System.Windows.Input.Key.OemOpenBrackets => "[",
            System.Windows.Input.Key.OemCloseBrackets=> "]",
            System.Windows.Input.Key.OemMinus        => "-",
            System.Windows.Input.Key.OemPlus         => "=",
            System.Windows.Input.Key.OemTilde        => "`",
            System.Windows.Input.Key.OemBackslash    => "\\",
            System.Windows.Input.Key.OemPipe         => "\\",
            >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9
                => ((int)(key - System.Windows.Input.Key.D0)).ToString(),
            _ => key.ToString()
        };
    }

    public sealed class AppSettings
    {
        // ---- capture ----
        public string SaveFolder   { get; set; } = DefaultFolder();
        public string ImageFormat  { get; set; } = "PNG";   // "PNG" or "JPEG"
        public int    JpegQuality  { get; set; } = 90;      // 1-100, only used when JPEG
        public string FilenamePrefix { get; set; } = "Snip";

        // ---- clipboard / history ----
        public int  MaxHistory            { get; set; } = 8;
        public bool CopyToClipboardOnSnip { get; set; } = true;

        // ---- notifications ----
        public bool ShowTrayNotification  { get; set; } = true;

        // ---- overlay ----
        public int OverlayOpacity { get; set; } = 55;   // 0-100, maps to dim alpha

        // ---- scroll capture ----
        /// <summary>Safety cap on the stitched image height, in pixels.</summary>
        public int ScrollMaxHeight { get; set; } = 20000;
        /// <summary>"Fast", "Balanced" or "Thorough" - trades speed against settle time.</summary>
        public string ScrollSpeed { get; set; } = "Balanced";
        /// <summary>Detect the real scrolling viewport instead of capturing the whole window.</summary>
        public bool ScrollAutoDetectRegion { get; set; } = true;
        /// <summary>Put the target back where it was scrolled to when we started.</summary>
        public bool ScrollRestorePosition { get; set; } = true;

        // ---- appearance ----
        /// <summary>"System", "Light" or "Dark".</summary>
        public string Theme { get; set; } = "System";

        // ---- gallery ----
        /// <summary>One of the SortKey values below; see GallerySortValue.</summary>
        public string GallerySort { get; set; } = "NewestFirst";
        /// <summary>How many thumbnails to hold on screen at once.</summary>
        public int GalleryPageSize { get; set; } = 150;
        /// <summary>Send deleted captures to the Recycle Bin instead of erasing them.</summary>
        public bool GalleryUseRecycleBin { get; set; } = true;
        /// <summary>Search the words inside captures, not just their file names.</summary>
        public bool GalleryOcrSearch { get; set; } = true;

        // ---- editor ----
        /// <summary>Clicking the "saved" notification opens the capture for editing.</summary>
        public bool EditOnNotificationClick { get; set; } = true;

        // ---- startup / ui ----
        public bool RunAtStartup          { get; set; } = false;
        public bool ShowSettingsOnStartup { get; set; } = true;
        public bool MinimiseToTrayOnClose { get; set; } = true;

        // ---- hotkeys ----
        public HotkeyDef SnipHotkey          { get; set; } = new(ModifierKeys.Control | ModifierKeys.Shift, Key.S);
        public HotkeyDef ScrollCaptureHotkey { get; set; } = new(ModifierKeys.Control | ModifierKeys.Shift, Key.W);
        public HotkeyDef HistoryHotkey       { get; set; } = new(ModifierKeys.Control | ModifierKeys.Shift, Key.V);
        public HotkeyDef NextHotkey          { get; set; } = new(ModifierKeys.Control | ModifierKeys.Shift, Key.OemPeriod);
        public HotkeyDef PrevHotkey          { get; set; } = new(ModifierKeys.Control | ModifierKeys.Shift, Key.OemComma);

        // ---- persistence ----
        private static string ConfigDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AdvancedSnip");
        private static string ConfigPath => Path.Combine(ConfigDir, "settings.json");

        public static string DefaultFolder() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AdvancedSnips");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath));
                    if (loaded != null) { loaded.FillDefaults(); return loaded; }
                }
            }
            catch { }
            var fresh = new AppSettings();
            fresh.Save();
            return fresh;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                File.WriteAllText(ConfigPath,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public AppSettings Clone() => new()
        {
            SaveFolder            = SaveFolder,
            ImageFormat           = ImageFormat,
            JpegQuality           = JpegQuality,
            FilenamePrefix        = FilenamePrefix,
            MaxHistory            = MaxHistory,
            CopyToClipboardOnSnip = CopyToClipboardOnSnip,
            ShowTrayNotification  = ShowTrayNotification,
            OverlayOpacity        = OverlayOpacity,
            Theme                 = Theme,
            GallerySort           = GallerySort,
            GalleryPageSize       = GalleryPageSize,
            GalleryUseRecycleBin  = GalleryUseRecycleBin,
            GalleryOcrSearch      = GalleryOcrSearch,
            EditOnNotificationClick = EditOnNotificationClick,
            ScrollMaxHeight        = ScrollMaxHeight,
            ScrollSpeed            = ScrollSpeed,
            ScrollAutoDetectRegion = ScrollAutoDetectRegion,
            ScrollRestorePosition  = ScrollRestorePosition,
            RunAtStartup          = RunAtStartup,
            ShowSettingsOnStartup = ShowSettingsOnStartup,
            MinimiseToTrayOnClose = MinimiseToTrayOnClose,
            SnipHotkey          = new HotkeyDef { Modifiers = SnipHotkey.Modifiers,          Key = SnipHotkey.Key          },
            ScrollCaptureHotkey = new HotkeyDef { Modifiers = ScrollCaptureHotkey.Modifiers, Key = ScrollCaptureHotkey.Key },
            HistoryHotkey       = new HotkeyDef { Modifiers = HistoryHotkey.Modifiers,       Key = HistoryHotkey.Key       },
            NextHotkey          = new HotkeyDef { Modifiers = NextHotkey.Modifiers,          Key = NextHotkey.Key          },
            PrevHotkey          = new HotkeyDef { Modifiers = PrevHotkey.Modifiers,          Key = PrevHotkey.Key          },
        };

        /// <summary>
        /// Maps the stored speed string onto the engine's enum. Fully qualified because
        /// the string property above shares its name with the enum type.
        /// </summary>
        public AdvancedSnip.Services.ScrollSpeed ScrollSpeedValue => ScrollSpeed switch
        {
            "Fast"     => AdvancedSnip.Services.ScrollSpeed.Fast,
            "Thorough" => AdvancedSnip.Services.ScrollSpeed.Thorough,
            _          => AdvancedSnip.Services.ScrollSpeed.Balanced
        };

        private void FillDefaults()
        {
            if (string.IsNullOrWhiteSpace(SaveFolder))    SaveFolder    = DefaultFolder();
            if (string.IsNullOrWhiteSpace(ImageFormat))   ImageFormat   = "PNG";
            if (string.IsNullOrWhiteSpace(FilenamePrefix))FilenamePrefix= "Snip";
            if (JpegQuality < 1 || JpegQuality > 100)    JpegQuality   = 90;
            if (MaxHistory < 5)   MaxHistory   = 5;
            if (OverlayOpacity < 0 || OverlayOpacity > 100) OverlayOpacity = 55;
            if (ScrollMaxHeight < 2000 || ScrollMaxHeight > 60000) ScrollMaxHeight = 20000;
            if (Theme != "System" && Theme != "Light" && Theme != "Dark") Theme = "System";
            if (GalleryPageSize < 50 || GalleryPageSize > 500) GalleryPageSize = 150;
            if (string.IsNullOrWhiteSpace(GallerySort)) GallerySort = "NewestFirst";
            if (string.IsNullOrWhiteSpace(ScrollSpeed) ||
                (ScrollSpeed != "Fast" && ScrollSpeed != "Balanced" && ScrollSpeed != "Thorough"))
                ScrollSpeed = "Balanced";
            SnipHotkey          ??= new HotkeyDef(ModifierKeys.Control | ModifierKeys.Shift, Key.S);
            ScrollCaptureHotkey ??= new HotkeyDef(ModifierKeys.Control | ModifierKeys.Shift, Key.W);
            HistoryHotkey       ??= new HotkeyDef(ModifierKeys.Control | ModifierKeys.Shift, Key.V);
            NextHotkey          ??= new HotkeyDef(ModifierKeys.Control | ModifierKeys.Shift, Key.OemPeriod);
            PrevHotkey          ??= new HotkeyDef(ModifierKeys.Control | ModifierKeys.Shift, Key.OemComma);
        }
    }
}
