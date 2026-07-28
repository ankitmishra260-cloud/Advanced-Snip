using System;
using System.IO;
using Microsoft.Win32;

namespace AdvancedSnip.Services
{
    internal enum StartupState
    {
        /// <summary>Not registered, and not meant to be.</summary>
        Off,
        /// <summary>Registered, points at this exe, and Windows has not blocked it.</summary>
        Enabled,
        /// <summary>Registered, but pointing at a different (probably moved) copy.</summary>
        PathMismatch,
        /// <summary>Registered, but the user switched it off in Task Manager → Startup apps.</summary>
        BlockedByWindows,
        /// <summary>Should be registered but isn't — policy or permissions got in the way.</summary>
        Failed
    }

    internal readonly record struct StartupStatus(StartupState State, string Detail)
    {
        internal bool IsWorking => State == StartupState.Enabled;
    }

    /// <summary>
    /// Registers the app to start with Windows, and — more usefully — can tell you
    /// whether that registration is actually going to fire.
    ///
    /// "Set a Run key and hope" is where the old version stopped, and it silently fails
    /// in three common situations that this class detects and reports instead:
    ///
    ///  1. The exe moved. A Run value is an absolute path; rebuild the app to a new
    ///     folder, or move the portable copy, and the entry quietly points at nothing.
    ///     Checked on every launch and repaired in place.
    ///
    ///  2. The user disabled it in Task Manager. That doesn't delete the Run value — it
    ///     writes a separate StartupApproved flag that overrides it. An app that only
    ///     looks at the Run key reports "enabled" forever while never starting. We read
    ///     the override and say so plainly.
    ///
    ///  3. The write silently failed under policy. Every write is now read back and
    ///     compared rather than assumed.
    ///
    /// The registered command carries a --startup switch so a boot launch goes straight
    /// to the tray instead of opening the settings window in the user's face.
    /// </summary>
    internal static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKey =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string ValueName = "AdvancedSnip";

        internal const string StartupSwitch = "--startup";

        /// <summary>The exact command we want the Run value to hold.</summary>
        private static string DesiredCommand
        {
            get
            {
                string exe = CurrentExePath();
                return string.IsNullOrEmpty(exe) ? "" : $"\"{exe}\" {StartupSwitch}";
            }
        }

        private static string CurrentExePath()
        {
            // ProcessPath is the real executable. Assembly.Location is empty for a
            // single-file publish, which is exactly the configuration where a startup
            // entry matters most, so it can't be the primary source.
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) return exe;

            try
            {
                var asm = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrEmpty(asm))
                {
                    // A framework-dependent build reports the .dll; the launcher beside
                    // it is what Windows needs to run.
                    string candidate = Path.ChangeExtension(asm, ".exe");
                    if (File.Exists(candidate)) return candidate;
                    return asm;
                }
            }
            catch { }

            return "";
        }

        /// <summary>Adds or removes the entry, then verifies the result really took.</summary>
        internal static StartupStatus SetRunAtStartup(bool enabled)
        {
            if (!enabled)
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                    if (key?.GetValue(ValueName) != null)
                        key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
                catch { }
                return new StartupStatus(StartupState.Off, "Won't start with Windows.");
            }

            string desired = DesiredCommand;
            if (string.IsNullOrEmpty(desired))
                return new StartupStatus(StartupState.Failed,
                    "Couldn't work out where this program is running from.");

            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                if (key == null)
                    return new StartupStatus(StartupState.Failed,
                        "Windows wouldn't let the app write its startup entry.");

                key.SetValue(ValueName, desired, RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                return new StartupStatus(StartupState.Failed,
                    "Couldn't write the startup entry: " + ex.Message);
            }

            return GetStatus(expectEnabled: true);
        }

        /// <summary>
        /// Reports what Windows will actually do at the next sign-in. Cheap enough to
        /// call whenever the settings page is shown.
        /// </summary>
        internal static StartupStatus GetStatus(bool expectEnabled)
        {
            string? current = null;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                current = key?.GetValue(ValueName) as string;
            }
            catch { }

            if (string.IsNullOrEmpty(current))
                return expectEnabled
                    ? new StartupStatus(StartupState.Failed,
                        "The startup entry is missing — try switching this off and on again.")
                    : new StartupStatus(StartupState.Off, "Won't start with Windows.");

            if (IsDisabledInTaskManager())
                return new StartupStatus(StartupState.BlockedByWindows,
                    "Windows has this switched off in Task Manager → Startup apps.");

            if (!PointsAtThisExe(current))
                return new StartupStatus(StartupState.PathMismatch,
                    "The startup entry points at an older copy of the app.");

            return new StartupStatus(StartupState.Enabled, "Verified — starts with Windows.");
        }

        private static bool PointsAtThisExe(string command)
        {
            string exe = CurrentExePath();
            if (string.IsNullOrEmpty(exe)) return false;

            string recorded = command.Trim();
            if (recorded.StartsWith('"'))
            {
                int close = recorded.IndexOf('"', 1);
                if (close > 0) recorded = recorded.Substring(1, close - 1);
            }
            else
            {
                int space = recorded.IndexOf(' ');
                if (space > 0) recorded = recorded.Substring(0, space);
            }

            try
            {
                return string.Equals(Path.GetFullPath(recorded), Path.GetFullPath(exe),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Task Manager records its on/off switch as a 12-byte blob rather than by
        /// touching the Run key. Byte 0 is the state: even values mean enabled, odd
        /// values (3, 5, ...) mean the user turned it off.
        /// </summary>
        private static bool IsDisabledInTaskManager()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                if (key?.GetValue(ValueName) is byte[] blob && blob.Length > 0)
                    return (blob[0] & 1) != 0;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Clears Task Manager's override. Only ever called when the user explicitly asks
        /// — silently undoing a choice they made in Windows itself would be the wrong
        /// kind of robust.
        /// </summary>
        internal static bool ClearWindowsBlock()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                if (key?.GetValue(ValueName) == null) return true;

                var enabled = new byte[12];
                enabled[0] = 0x02;          // what Task Manager writes for "enabled"
                key.SetValue(ValueName, enabled, RegistryValueKind.Binary);
                return !IsDisabledInTaskManager();
            }
            catch { return false; }
        }

        /// <summary>
        /// Run at every launch. Repairs a stale path in place so moving or rebuilding the
        /// app doesn't quietly break its own startup entry.
        /// </summary>
        internal static StartupStatus Reconcile(bool shouldRun)
        {
            var status = GetStatus(shouldRun);

            if (shouldRun && status.State is StartupState.PathMismatch or StartupState.Failed)
                return SetRunAtStartup(true);

            if (!shouldRun && status.State != StartupState.Off)
                return SetRunAtStartup(false);

            return status;
        }

        /// <summary>True when this process was launched by the Windows startup entry.</summary>
        internal static bool LaunchedAtStartup(string[] args)
        {
            foreach (var a in args)
                if (string.Equals(a, StartupSwitch, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "/startup", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        internal static void OpenTaskManagerStartup()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ms-settings:startupapps",
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
