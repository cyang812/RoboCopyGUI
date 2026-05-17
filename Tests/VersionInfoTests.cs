using RoboCopyGUI.Services;
using Xunit;

namespace RoboCopyGUI.Tests;

/// <summary>
/// Pure-functional tests for the title-bar formatting + version normalisation
/// helpers in <see cref="VersionInfo"/>. The live <c>GetAssemblyVersion</c> /
/// <c>GetBuildTimeStamp</c> readers depend on the test-host process and aren't
/// covered here — the deterministic logic is what we care about.
/// </summary>
public class VersionInfoTests
{
    private const string Base = "WinUI 3 File Copier";

    // ---------- NormalizeVersionForDisplay ----------

    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("V0.1.0", "0.1.0")]
    [InlineData("0.1.0+abc123", "0.1.0")]                   // build metadata dropped
    [InlineData("v0.1.0+sha.0123def", "0.1.0")]             // 'v' + metadata
    [InlineData("1.2.3-rc1", "1.2.3-rc1")]                  // pre-release retained (semantic)
    [InlineData("1.2.3-rc1+build.42", "1.2.3-rc1")]         // pre-release retained, metadata dropped
    [InlineData("  v0.1.0  ", "0.1.0")]                     // trimmed
    public void NormalizeVersionForDisplay_strips_prefix_and_buildmeta(string input, string expected)
    {
        Assert.Equal(expected, VersionInfo.NormalizeVersionForDisplay(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]      // only the prefix
    [InlineData("v+abc")]  // prefix + metadata, no digits
    public void NormalizeVersionForDisplay_returns_null_for_empty_or_pure_metadata(string? input)
    {
        Assert.Null(VersionInfo.NormalizeVersionForDisplay(input));
    }

    // ---------- FormatWindowTitle ----------

    [Fact]
    public void FormatWindowTitle_includes_version_and_build_time_when_both_present()
    {
        string title = VersionInfo.FormatWindowTitle(Base, "0.1.0", "2026-05-17 11:03:05");
        Assert.Equal($"{Base} v0.1.0 \u2014 built 2026-05-17 11:03:05", title);
    }

    [Fact]
    public void FormatWindowTitle_strips_commit_metadata_from_displayed_version()
    {
        string title = VersionInfo.FormatWindowTitle(Base, "0.1.0+abc1234", "2026-05-17 11:03:05");
        Assert.Equal($"{Base} v0.1.0 \u2014 built 2026-05-17 11:03:05", title);
    }

    [Fact]
    public void FormatWindowTitle_omits_version_segment_when_unparseable_or_missing()
    {
        Assert.Equal($"{Base} \u2014 built 2026-05-17 11:03:05",
            VersionInfo.FormatWindowTitle(Base, null, "2026-05-17 11:03:05"));
        Assert.Equal($"{Base} \u2014 built 2026-05-17 11:03:05",
            VersionInfo.FormatWindowTitle(Base, "", "2026-05-17 11:03:05"));
    }

    [Fact]
    public void FormatWindowTitle_omits_build_segment_when_missing()
    {
        Assert.Equal($"{Base} v0.1.0",
            VersionInfo.FormatWindowTitle(Base, "0.1.0", null));
        Assert.Equal($"{Base} v0.1.0",
            VersionInfo.FormatWindowTitle(Base, "0.1.0", ""));
    }

    [Fact]
    public void FormatWindowTitle_returns_just_base_when_both_missing()
    {
        Assert.Equal(Base, VersionInfo.FormatWindowTitle(Base, null, null));
    }

    [Fact]
    public void FormatWindowTitle_keeps_prerelease_label_in_displayed_version()
    {
        string title = VersionInfo.FormatWindowTitle(Base, "1.2.3-rc1", null);
        Assert.Equal($"{Base} v1.2.3-rc1", title);
    }

    [Fact]
    public void FormatWindowTitle_throws_for_empty_base()
    {
        Assert.Throws<System.ArgumentException>(() =>
            VersionInfo.FormatWindowTitle("", "0.1.0", null));
    }
}
