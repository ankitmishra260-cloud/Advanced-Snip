using System;
using Microsoft.Win32;

namespace AdvancedSnip.Services
{
    internal enum ThemeChoice { System, Light, Dark }

    /// <summary>
    /// Owns the app's light/dark appearance.
    ///
    /// The whole mechanism is one merged ResourceDictionary swapped at the Application
    /// level. Because every themed control binds with DynamicResource, replacing that
    /// dictionary repaints open windows immediately — no restart, no per-window plumbing.
    ///
    /// Two things this handles that a naive implementation misses:
    ///
    ///  * The non-client area. WPF doesn't theme the title bar, so a dark window keeps a
    ///    white caption unless we ask DWM for the dark one explicitly. That has to be
    ///    re-applied per window, and only once the HWND exists.
    ///
    ///  * Following the OS. "System" isn't resolved once at launch — Windows raises a
    ///    preference change when the user flips the setting (or when night-mode
    ///    scheduling does it for them), and the app follows live.
    /// </summary>
    internal static class ThemeManager
    {
        private const string PersonalizeKey =
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private static ThemeChoice _choice = ThemeChoice.System;
        private static bool _hooked;

        /// <summary>True when dark resources are currently loaded.</summary>
        internal static bool IsDark { get; private set; }

        /// <summary>Raised after the applied theme actually changes.</summary>
        internal static event EventHandler? ThemeChanged;

        internal static ThemeChoice Parse(string? value) => value switch
        {
            "Light" => ThemeChoice.Light,
            "Dark"  => ThemeChoice.Dark,
            _       => ThemeChoice.System
        };

        internal static void Apply(string? settingValue) => Apply(Parse(settingValue));

        internal static void Apply(ThemeChoice choice)
        {
            _choice = choice;
            HookSystemChanges();

            bool dark = choice switch
            {
                ThemeChoice.Dark  => true,
                ThemeChoice.Light => false,
                _                 => SystemPrefersDark()
            };

            var app = System.Windows.Application.Current;
            if (app == null) return;

            // Swapping even when the value hasn't changed would throw away brush
            // instances for no reason and make every open window re-render.
            bool alreadyLoaded = _loadedDictionary != null && IsDark == dark;
            if (alreadyLoaded) return;

            IsDark = dark;

            // Full pack URI rather than a relative one. A bare relative Source depends on
            // an ambient base URI that isn't reliably present when loading from code, and
            // the assembly name is read rather than hard-coded so renaming the output
            // can't silently break the theme.
            string asm = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name
                         ?? "AdvancedSnip";
            string leaf = dark ? "Dark" : "Light";

            var dict = new System.Windows.ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/{asm};component/Themes/{leaf}.xaml",
                                 UriKind.Absolute)
            };

            if (_loadedDictionary != null)
                app.Resources.MergedDictionaries.Remove(_loadedDictionary);

            // Insert at index 0 so anything the app merges later can still override.
            app.Resources.MergedDictionaries.Insert(0, dict);
            _loadedDictionary = dict;

            foreach (System.Windows.Window w in app.Windows)
                ApplyToWindow(w);

            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        private static System.Windows.ResourceDictionary? _loadedDictionary;

        /// <summary>
        /// Paints a window's title bar to match. Safe to call before the window is
        /// shown — it defers until the handle exists, because DWM has nothing to act on
        /// until then.
        /// </summary>
        internal static void ApplyToWindow(System.Windows.Window window)
        {
            if (window == null) return;

            var helper = new System.Windows.Interop.WindowInteropHelper(window);
            if (helper.Handle == IntPtr.Zero)
            {
                window.SourceInitialized -= DeferredApply;
                window.SourceInitialized += DeferredApply;
                return;
            }

            Win32.SetTitleBarDark(helper.Handle, IsDark);
        }

        private static void DeferredApply(object? sender, EventArgs e)
        {
            if (sender is System.Windows.Window w)
            {
                w.SourceInitialized -= DeferredApply;
                ApplyToWindow(w);
            }
        }

        /// <summary>
        /// Reads the OS preference. AppsUseLightTheme is the one that governs app
        /// surfaces; SystemUsesLightTheme governs the taskbar and is a different setting,
        /// which is why apps that read the wrong key end up out of step with Explorer.
        /// Missing value means light — that's the pre-1809 default.
        /// </summary>
        internal static bool SystemPrefersDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
            }
            catch { }
            return false;
        }

        private static UserPreferenceChangedEventHandler? _handler;

        private static void HookSystemChanges()
        {
            if (_hooked) return;
            _hooked = true;
            try
            {
                _handler = (_, e) =>
                {
                    if (e.Category != UserPreferenceCategory.General) return;
                    if (_choice != ThemeChoice.System) return;

                    // The registry value can lag the notification slightly; re-applying
                    // on the dispatcher gives it time to settle and keeps the resource
                    // swap on the UI thread where it belongs.
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                        new Action(() => Apply(ThemeChoice.System)),
                        System.Windows.Threading.DispatcherPriority.Background);
                };
                SystemEvents.UserPreferenceChanged += _handler;
            }
            catch { /* SystemEvents needs a message pump; ignore if unavailable */ }
        }

        internal static void Shutdown()
        {
            if (_handler == null) return;
            try { SystemEvents.UserPreferenceChanged -= _handler; } catch { }
            _handler = null;
        }
    }
}
