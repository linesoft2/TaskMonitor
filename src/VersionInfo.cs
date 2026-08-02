using System;
using System.Reflection;

namespace task_monitor
{
    /// <summary>
    /// The single source of truth for the user-facing version:
    /// <c>&lt;Version&gt;</c> in task_monitor.csproj ("1.0.0"), which the SDK bakes into the
    /// generated <c>AssemblyInformationalVersion</c> attribute this class reads.
    /// Shown in 设置 → 关于, compared against release tags by <see cref="UpdateChecker"/>,
    /// and release.yml FAILS the publish pipeline when the pushed tag v&lt;X&gt; ≠ &lt;X&gt;
    /// there — the three can never drift apart. Bump it together with the release tag.
    /// </summary>
    public static class VersionInfo
    {
        /// <summary>The informational version string, e.g. "1.0.0" (no v prefix).</summary>
        public static readonly string Current =
            typeof(VersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? typeof(VersionInfo).Assembly.GetName().Version?.ToString()   // fallback: AssemblyVersion
            ?? "0.0.0";

        /// <summary><see cref="Current"/> parsed for comparison (unparsable → 0.0.0).</summary>
        public static readonly Version CurrentVersion =
            Version.TryParse(Current, out Version v) ? v : new Version(0, 0, 0);
    }
}
