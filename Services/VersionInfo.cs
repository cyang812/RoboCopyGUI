using System;
using System.Reflection;

namespace RoboCopyGUI.Services;

/// <summary>
/// Read the running app's version + build timestamp, and format them for
/// display in the title bar. Pulled out of <c>MainWindow</c> so it can be
/// unit-tested without WinUI.
/// </summary>
public static class VersionInfo
{
    private const string DefaultBaseTitle = "WinUI 3 File Copier";

    /// <summary>
    /// Best-effort version of the currently-running entry assembly. Reads
    /// <see cref="AssemblyInformationalVersionAttribute"/> first (carries the
    /// SemVer + optional <c>+commitsha</c> SourceLink suffix), then falls back
    /// to <see cref="AssemblyName.Version"/>. Returns <c>null</c> when nothing
    /// is discoverable (very rare; happens in some test hosts).
    /// </summary>
    public static string? GetAssemblyVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) return info;
        return asm.GetName().Version?.ToString();
    }

    /// <summary>
    /// Best-effort last-write-time of the running assembly file. <c>null</c>
    /// for single-file / bundled scenarios where the assembly has no on-disk
    /// location (PublishSingleFile=true, etc.). Returns the timestamp formatted
    /// in local time as <c>yyyy-MM-dd HH:mm:ss</c>.
    /// </summary>
    public static string? GetBuildTimeStamp()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            string? loc = asm.Location;
            if (string.IsNullOrEmpty(loc)) return null;
            return System.IO.File.GetLastWriteTime(loc).ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Convenience wrapper for the title bar: uses the default base title and
    /// the live version + build timestamp.
    /// </summary>
    public static string GetWindowTitle() =>
        FormatWindowTitle(DefaultBaseTitle, GetAssemblyVersion(), GetBuildTimeStamp());

    /// <summary>
    /// Pure helper — composes the window title from the supplied parts so it
    /// can be unit-tested deterministically. Format:
    /// <c>{base} v{version} \u2014 built {ts}</c>, with each segment dropped
    /// gracefully when null. Strips an optional <c>+commitmetadata</c> SemVer
    /// suffix and any leading <c>v</c>/<c>V</c> so the title shows a clean
    /// <c>v0.1.0</c> even when the source version is <c>0.1.0+abc123</c>.
    /// </summary>
    public static string FormatWindowTitle(string baseName, string? version, string? buildTime)
    {
        if (string.IsNullOrWhiteSpace(baseName))
            throw new ArgumentException("Base title must not be empty.", nameof(baseName));

        string title = baseName;

        string? display = NormalizeVersionForDisplay(version);
        if (display is not null) title += $" v{display}";

        if (!string.IsNullOrWhiteSpace(buildTime))
        {
            // Em-dash separator matches the style used elsewhere in the UI.
            title += $" \u2014 built {buildTime}";
        }

        return title;
    }

    /// <summary>
    /// Strip a leading <c>v</c>/<c>V</c> and any <c>+buildmetadata</c> suffix.
    /// Returns null when <paramref name="version"/> is null/empty so callers
    /// can decide whether to omit the version segment entirely.
    /// </summary>
    internal static string? NormalizeVersionForDisplay(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;

        string s = version.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);

        // SemVer build metadata starts with '+' and is non-semantic; we drop
        // it for display. Pre-release tags (after '-') ARE semantic ("rc1"
        // vs "beta") so we keep them.
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s.Substring(0, plus);

        return string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
