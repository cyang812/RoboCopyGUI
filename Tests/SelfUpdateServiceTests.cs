using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RoboCopyGUI.Services;
using Xunit;

namespace RoboCopyGUI.Tests;

/// <summary>
/// Tests for <see cref="SelfUpdateService"/>. Pure helpers (semver parse +
/// compare, JSON tag extraction) are unit-tested directly; CheckAsync is
/// exercised end-to-end via an injected <see cref="HttpClient"/> backed by
/// a programmable fake handler so no real network call is made.
/// </summary>
public class SelfUpdateServiceTests
{
    // ---------- TryParseSemver ----------

    [Theory]
    [InlineData("1.2.3",  1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("1.2",    1, 2, 0)]
    [InlineData("1",      1, 0, 0)]
    [InlineData("1.2.3-beta",  1, 2, 3)]
    [InlineData("1.2.3+abc",   1, 2, 3)]
    [InlineData("v1.2.3-rc.1+sha.0123", 1, 2, 3)]
    [InlineData("  v1.2.3  ", 1, 2, 3)]
    public void TryParseSemver_accepts_common_forms(string input, int major, int minor, int patch)
    {
        Assert.True(SelfUpdateService.TryParseSemver(input, out var v));
        Assert.Equal((major, minor, patch), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("not-a-version")]
    [InlineData("vNext")]
    public void TryParseSemver_rejects_garbage(string input)
    {
        Assert.False(SelfUpdateService.TryParseSemver(input, out _));
    }

    // ---------- IsNewer ----------

    [Theory]
    [InlineData("v1.2.3", "1.2.2", true)]   // patch bump
    [InlineData("v1.3.0", "1.2.99", true)]  // minor bump beats higher patch
    [InlineData("v2.0.0", "1.99.99", true)] // major bump
    [InlineData("v1.2.3", "1.2.3", false)]  // equal
    [InlineData("v1.2.2", "1.2.3", false)]  // older
    [InlineData("v1.0.0", "1.0.0+devcommit", false)]  // build metadata ignored
    public void IsNewer_compares_semver_components(string latest, string current, bool expected)
    {
        Assert.Equal(expected, SelfUpdateService.IsNewer(latest, current));
    }

    [Fact]
    public void IsNewer_returns_false_when_either_version_unparseable()
    {
        // Conservative: if we can't tell, don't pester the user.
        Assert.False(SelfUpdateService.IsNewer("vNext", "1.0.0"));
        Assert.False(SelfUpdateService.IsNewer("1.2.3", "bogus"));
    }

    // ---------- ParseTagName ----------

    [Fact]
    public void ParseTagName_reads_tag_from_real_shape_payload()
    {
        const string json = """
            {
              "tag_name": "v1.2.3",
              "name": "RoboCopyGUI v1.2.3",
              "html_url": "https://github.com/cyang812/RoboCopyGUI/releases/tag/v1.2.3"
            }
            """;
        Assert.Equal("v1.2.3", SelfUpdateService.ParseTagName(json));
        Assert.Equal("https://github.com/cyang812/RoboCopyGUI/releases/tag/v1.2.3",
            SelfUpdateService.ParseHtmlUrl(json));
    }

    [Fact]
    public void ParseTagName_returns_null_for_missing_field()
    {
        Assert.Null(SelfUpdateService.ParseTagName("{ \"name\": \"no tag here\" }"));
    }

    [Fact]
    public void ParseTagName_returns_null_for_malformed_json()
    {
        Assert.Null(SelfUpdateService.ParseTagName("not json at all"));
    }

    // ---------- CheckAsync end-to-end with fake handler ----------

    [Fact]
    public async Task CheckAsync_reports_HasUpdate_when_remote_tag_is_newer()
    {
        var client = NewFake(HttpStatusCode.OK, """
            { "tag_name": "v2.0.0", "html_url": "https://example/x" }
            """);

        var result = await SelfUpdateService.CheckAsync("1.0.0", client: client);

        Assert.True(result.HasUpdate);
        Assert.Equal("v2.0.0", result.LatestVersion);
        Assert.Equal("https://example/x", result.ReleaseUrl);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CheckAsync_reports_NoUpdate_when_remote_equals_current()
    {
        var client = NewFake(HttpStatusCode.OK, """
            { "tag_name": "v1.0.0", "html_url": "https://example/x" }
            """);

        var result = await SelfUpdateService.CheckAsync("1.0.0", client: client);

        Assert.False(result.HasUpdate);
        Assert.Equal("v1.0.0", result.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_returns_Failed_on_HTTP_error()
    {
        var client = NewFake(HttpStatusCode.NotFound, "");
        var result = await SelfUpdateService.CheckAsync("1.0.0", client: client);
        Assert.False(result.HasUpdate);
        Assert.Contains("404", result.Error);
    }

    [Fact]
    public async Task CheckAsync_returns_Failed_on_malformed_payload()
    {
        var client = NewFake(HttpStatusCode.OK, "{ not valid json");
        var result = await SelfUpdateService.CheckAsync("1.0.0", client: client);
        Assert.False(result.HasUpdate);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task CheckAsync_returns_Failed_when_network_throws()
    {
        var client = NewFake(throwOnSend: new HttpRequestException("DNS exploded"));
        var result = await SelfUpdateService.CheckAsync("1.0.0", client: client);
        Assert.False(result.HasUpdate);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task CheckAsync_propagates_OperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = NewFake(HttpStatusCode.OK, "{}");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SelfUpdateService.CheckAsync("1.0.0", ct: cts.Token, client: client));
    }

    // ---------- helpers ----------

    private static HttpClient NewFake(HttpStatusCode status, string body) =>
        new(new FakeHandler(req => new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
        }));

    private static HttpClient NewFake(Exception throwOnSend) =>
        new(new FakeHandler(_ => throw throwOnSend));

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_respond(request));
        }
    }
}
