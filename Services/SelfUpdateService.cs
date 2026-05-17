using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace RoboCopyGUI.Services;

/// <summary>Outcome of a single <see cref="SelfUpdateService.CheckAsync"/> call.</summary>
public sealed record UpdateCheckResult(bool HasUpdate, string? LatestVersion, string? ReleaseUrl, string? Error)
{
    public static UpdateCheckResult NoUpdate(string? latest = null) => new(false, latest, null, null);
    public static UpdateCheckResult Available(string latest, string url) => new(true, latest, url, null);
    public static UpdateCheckResult Failed(string error) => new(false, null, null, error);
}

/// <summary>
/// Best-effort "is there a newer release on GitHub?" check, run once per launch.
/// All network failures degrade silently to <see cref="UpdateCheckResult.NoUpdate"/>
/// equivalent — an update-check error must never bother the user.
/// </summary>
public static class SelfUpdateService
{
    public const string ReleasesApiUrl =
        "https://api.github.com/repos/cyang812/RoboCopyGUI/releases/latest";

    public const string ReleasesPageUrl =
        "https://github.com/cyang812/RoboCopyGUI/releases/latest";

    /// <summary>
    /// Lazy singleton — HttpClient is expensive to construct and intended for re-use
    /// (per Microsoft guidance). For tests, pass an explicit client to <see cref="CheckAsync"/>.
    /// </summary>
    private static readonly Lazy<HttpClient> _defaultClient = new(() =>
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // GitHub returns 403 without a User-Agent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("RoboCopyGUI-SelfUpdateCheck");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    });

    /// <summary>
    /// Ask the GitHub Releases API whether a release newer than <paramref name="currentVersion"/>
    /// exists. Never throws; any failure (HTTP, JSON, semver-parse) is reported as a
    /// non-update result with the error captured for logging.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(
        string currentVersion,
        CancellationToken ct = default,
        HttpClient? client = null)
    {
        try
        {
            var http = client ?? _defaultClient.Value;
            using var resp = await http.GetAsync(ReleasesApiUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed($"HTTP {(int)resp.StatusCode}");
            }

            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string? tag = ParseTagName(body);
            if (string.IsNullOrWhiteSpace(tag))
            {
                return UpdateCheckResult.Failed("tag_name missing in API response");
            }
            string? htmlUrl = ParseHtmlUrl(body) ?? ReleasesPageUrl;

            return IsNewer(tag, currentVersion)
                ? UpdateCheckResult.Available(tag, htmlUrl)
                : UpdateCheckResult.NoUpdate(tag);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled (e.g. app closing) — not a real error.
            throw;
        }
        catch (Exception ex)
        {
            // Network down, DNS, TLS, JSON parse, etc. Update checks must never
            // disturb the user — swallow and report internally.
            try { Log.Debug(ex, "Self-update check failed (silent)."); }
            catch { }
            return UpdateCheckResult.Failed(ex.GetType().Name);
        }
    }

    /// <summary>
    /// Extract the <c>tag_name</c> field from a GitHub /releases/latest payload.
    /// Returns null if absent or malformed.
    /// </summary>
    internal static string? ParseTagName(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tag_name", out var tag) &&
                tag.ValueKind == JsonValueKind.String)
            {
                return tag.GetString();
            }
        }
        catch { /* malformed JSON => null */ }
        return null;
    }

    /// <summary>Extract the <c>html_url</c> field (release page link).</summary>
    internal static string? ParseHtmlUrl(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("html_url", out var url) &&
                url.ValueKind == JsonValueKind.String)
            {
                return url.GetString();
            }
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>
    /// Returns true iff <paramref name="latest"/> is strictly higher than
    /// <paramref name="current"/> by semver-major/minor/patch comparison.
    /// Tolerates an optional <c>v</c> prefix on either operand and strips
    /// any <c>+commit</c> build-metadata or <c>-prerelease</c> suffix so
    /// dev builds like <c>1.0.0+abc123</c> compare cleanly against
    /// release tags like <c>v1.0.0</c>.
    /// </summary>
    internal static bool IsNewer(string latest, string current)
    {
        if (!TryParseSemver(latest, out var L)) return false;
        if (!TryParseSemver(current, out var C))
        {
            // Couldn't read the running version — be conservative; don't pester.
            return false;
        }
        if (L.major != C.major) return L.major > C.major;
        if (L.minor != C.minor) return L.minor > C.minor;
        return L.patch > C.patch;
    }

    /// <summary>
    /// Parse "v1.2.3", "1.2.3", "1.2.3-beta", "1.2.3+abc" into (1, 2, 3).
    /// Missing components default to 0. Returns false if no parseable triplet.
    /// </summary>
    internal static bool TryParseSemver(string raw, out (int major, int minor, int patch) parsed)
    {
        parsed = (0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // Strip leading 'v' / 'V'.
        string s = raw.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);

        // Strip the first '-' (prerelease) or '+' (build metadata) suffix.
        var m = Regex.Match(s, @"^(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        if (!m.Success || m.Groups[1].Length == 0) return false;

        int major = int.Parse(m.Groups[1].Value);
        int minor = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        int patch = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        parsed = (major, minor, patch);
        return true;
    }
}
