using System;
using System.Reflection;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// What build this is. One place, because two places disagreed for twenty-nine
    /// releases and nobody could tell.
    ///
    /// The sidebar read "v1.0" the whole time. Not a stale binary — a build setting:
    /// <c>&lt;Version&gt;</c> does not feed <c>AssemblyVersion</c>, <c>VersionPrefix</c>
    /// does, and MSBuild's default for it is 1.0.0. So
    /// <c>Assembly.GetName().Version</c> was honestly reporting 1.0.0.0. The csproj now
    /// sets all four, and this reads the informational version first, which carries the
    /// literal string from <c>&lt;Version&gt;</c> and cannot drift from it.
    /// </summary>
    internal static class AppInfo
    {
        private static string? _version;

        /// <summary>e.g. "2.0.0". Empty only if the assembly refuses to say.</summary>
        internal static string Version => _version ??= Resolve();

        private static string Resolve()
        {
            var asm = Assembly.GetExecutingAssembly();

            try
            {
                string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                                  ?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    // Strip the source-revision suffix the SDK appends ("2.0.0+abc123").
                    int plus = info.IndexOf('+');
                    if (plus > 0) info = info.Substring(0, plus);

                    return info.Trim();
                }
            }
            catch { }

            try
            {
                var v = asm.GetName().Version;
                if (v != null)
                    return $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch { }

            return "";
        }
    }
}
