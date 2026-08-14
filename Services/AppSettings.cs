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
        /// <summary>"BlueNeonLight" by default, or "System", "Light", "Dark", "BlueNeon", etc.</summary>
        public string Theme { get; set; } = "BlueNeonLight";

        // ---- gallery ----
        /// <summary>One of the SortKey values below; see GallerySortValue.</summary>
        public string GallerySort { get; set; } = "NewestFirst";
        /// <summary>How many thumbnails to hold on screen at once.</summary>
        public int GalleryPageSize { get; set; } = 150;
        /// <summary>Send deleted captures to the Recycle Bin instead of erasing them.</summary>
        public bool GalleryUseRecycleBin { get; set; } = true;
        /// <summary>Search the words inside captures, not just their file names.</summary>
        public bool GalleryOcrSearch { get; set; } = true;

        // ---- screen recording ----
        /// <summary>Where videos go. Empty means "next to the screenshots".</summary>
        public string VideoFolder { get; set; } = "";
        public string VideoPrefix { get; set; } = "Recording";
        /// <summary>"Efficient", "Balanced", "High" or "Maximum".</summary>
        public string RecordQuality { get; set; } = "Balanced";
        public int  RecordFps { get; set; } = 30;
        /// <summary>"Screen", "Monitor" or "Region" - what the next recording captures.</summary>
        /// <summary>
        /// "Monitor" (the display under the pointer), "Screen" (the whole virtual desktop
        /// across every monitor) or "Region". Monitor is the default deliberately: on a
        /// single-monitor machine it is identical to Screen, and on a multi-monitor one it
        /// is almost always what was meant. "Everything" stitches every display into one
        /// very wide frame, which H.264 may refuse outright and which nobody wants by
        /// accident.
        /// </summary>
        public string RecordSource { get; set; } = "Monitor";
        public bool RecordCursor { get; set; } = true;
        /// <summary>Show the drawing overlay and its pen tools while recording.</summary>
        public bool RecordAnnotations { get; set; } = true;
        /// <summary>Seconds of countdown before recording actually begins. 0 disables it.</summary>
        public int  RecordCountdown { get; set; } = 3;

        /// <summary>
        /// Keep the tool picker open during a recording instead of summoning it per change.
        ///
        /// Saved rather than reset each time on purpose: how much annotating someone does
        /// is a property of the person, not of the recording, so it should be decided once
        /// and then left alone.
        /// </summary>
        public bool RecordToolsPinned { get; set; } = false;

        /// <summary>
        /// Where the pinned picker was left, in physical pixels. <see cref="int.MinValue"/>
        /// means "never placed" — a real coordinate can legitimately be negative on a
        /// multi-monitor desktop, so 0 would not do as the unset value.
        /// </summary>
        public int RecordToolsX { get; set; } = int.MinValue;
        public int RecordToolsY { get; set; } = int.MinValue;

        // ---- recording sound ----
        /// <summary>Record what the machine is playing, via WASAPI loopback.</summary>
        public bool RecordSystemAudio { get; set; } = true;
        /// <summary>Record the microphone as well.</summary>
        public bool RecordMicrophone { get; set; } = false;

        /// <summary>
        /// Endpoint ids, or empty for "whatever Windows is currently using". Empty is the
        /// better default by a wide margin: a saved id stops working the moment a headset
        /// is unplugged, while the default follows the user's own choice of device.
        /// </summary>
        public string RecordSystemDevice { get; set; } = "";
        public string RecordMicDevice { get; set; } = "";

        /// <summary>Levels in percent, 0-200. 100 is unity gain.</summary>
        public int RecordSystemVolume { get; set; } = 100;
        public int RecordMicVolume { get; set; } = 100;

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
        public HotkeyDef OcrHotkey           { get; set; } = new(ModifierKeys.Control | ModifierKeys.Shift, Key.O);
        public HotkeyDef HistoryHotkey       { get; set; } = new(ModifierKeys.Control | ModifierKeys.Shift, Key.V);
        public HotkeyDef NextHotkey          { get; set; } = new(ModifierKeys.Control | ModifierKeys.Shift, Key.OemPeriod);
        public HotkeyDef PrevHotkey          { get; set; } = new(ModifierKeys.Control | ModifierKeys.Shift, Key.OemComma);
        public HotkeyDef RecordHotkey        { get; set; } = new(ModifierKeys.Control | ModifierKeys.Shift, Key.R);

        /// <summary>The quality preset as the recorder's own enum.</summary>
        public VideoQuality RecordQualityValue => RecordQuality switch
        {
            "Efficient" => VideoQuality.Efficient,
            "High"      => VideoQuality.High,
            "Maximum"   => VideoQuality.Maximum,
            _           => VideoQuality.Balanced
        };

        /// <summary>Where recordings go, falling back to the screenshot folder.</summary>
        public string VideoFolderOrDefault =>
            string.IsNullOrWhiteSpace(VideoFolder) ? SaveFolder : VideoFolder;

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
            RecordHotkey        = new HotkeyDef { Modifiers = RecordHotkey.Modifiers,        Key = RecordHotkey.Key        },
            VideoFolder           = VideoFolder,
            VideoPrefix           = VideoPrefix,
            RecordQuality         = RecordQuality,
            RecordFps             = RecordFps,
            RecordSource          = RecordSource,
            RecordCursor          = RecordCursor,
            RecordAnnotations     = RecordAnnotations,
            RecordCountdown       = RecordCountdown,
            RecordToolsPinned     = RecordToolsPinned,
            RecordToolsX          = RecordToolsX,
            RecordToolsY          = RecordToolsY,
            RecordSystemAudio     = RecordSystemAudio,
            RecordMicrophone      = RecordMicrophone,
            RecordSystemDevice    = RecordSystemDevice,
            RecordMicDevice       = RecordMicDevice,
            RecordSystemVolume    = RecordSystemVolume,
            RecordMicVolume       = RecordMicVolume,
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
            if (RecordFps < 5 || RecordFps > 60)         RecordFps     = 30;
            if (RecordCountdown < 0 || RecordCountdown > 10) RecordCountdown = 3;
            if (string.IsNullOrWhiteSpace(VideoPrefix))  VideoPrefix   = "Recording";
            if (RecordQuality is not ("Efficient" or "Balanced" or "High" or "Maximum"))
                RecordQuality = "Balanced";
            if (RecordSystemVolume < 0 || RecordSystemVolume > 200) RecordSystemVolume = 100;
            if (RecordMicVolume    < 0 || RecordMicVolume    > 200) RecordMicVolume    = 100;
            if (MaxHistory < 5)   MaxHistory   = 5;
            if (OverlayOpacity < 0 || OverlayOpacity > 100) OverlayOpacity = 55;
            if (ScrollMaxHeight < 2000 || ScrollMaxHeight > 60000) ScrollMaxHeight = 20000;
            // Every value ThemeManager.Parse understands has to be listed here, or a valid
            // choice is quietly reset to System on the next launch and the user's setting
            // appears not to have stuck.
            if (Theme is not ("System" or "Light" or "Dark" or "Cyberpunk" or "Sunset" or "NeonDark" or "DarkNeon" or "LightNeon" or "BlueNeon" or "BlueNeonLight" or "PrismAurora" or "EmeraldMatrix" or "SolarFlare" or "RoseQuartz" or "MidnightAmethyst")) Theme = "BlueNeonLight";
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
