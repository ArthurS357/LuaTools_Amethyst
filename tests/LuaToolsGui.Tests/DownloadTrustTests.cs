using System.Linq;
using AwesomeAssertions;
using LuaToolsGui;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers the download trust boundary: which hosts binaries may come from, and the pinned-digest fallback.
///
/// <para>
/// Context for the host allow-list: asset URLs are read out of release JSON, and that JSON can itself be
/// served by a mirror when api.github.com is unreachable. Without the check, one compromised metadata
/// source could point the download anywhere AND supply the matching digest — hash verification would then
/// compare the attacker's file to the attacker's hash and pass.
/// </para>
/// </summary>
public class DownloadTrustTests
{
    // ── Host allow-list ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://github.com/atom0s/Steamless/releases/download/v3.1.0.5/x.zip")]
    [InlineData("https://api.github.com/repos/a/b/releases/latest")]
    [InlineData("https://objects.githubusercontent.com/foo")]
    [InlineData("https://raw.githubusercontent.com/a/b/main/c.json")]
    [InlineData("https://release-assets.githubusercontent.com/x")]
    [InlineData("https://GITHUB.COM/a/b")] // host comparison is case-insensitive
    public void Accepts_github_owned_https_hosts(string url) =>
        GithubProxy.IsTrustedDownloadUrl(url).Should().BeTrue();

    [Theory]
    [InlineData("https://evil.example/payload.exe")]
    [InlineData("https://github.com.evil.example/a/b")]        // suffix confusion
    [InlineData("https://evil.example/https://github.com/a/b")] // path confusion
    [InlineData("https://raw.githubusercontent.com.evil.example/x")]
    [InlineData("not a url at all")]
    [InlineData("")]
    public void Rejects_everything_else(string url) =>
        GithubProxy.IsTrustedDownloadUrl(url).Should().BeFalse();

    [Theory]
    [InlineData("http://github.com/a/b")]
    [InlineData("ftp://github.com/a/b")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    public void Rejects_non_https_schemes(string url) =>
        GithubProxy.IsTrustedDownloadUrl(url).Should()
            .BeFalse("plaintext or local schemes are modifiable regardless of any later hash check");

    [Fact]
    public async Task DownloadAsync_refuses_an_untrusted_host_before_making_a_request()
    {
        var proxy = new GithubProxy();

        var act = async () => await proxy.DownloadAsync(
            "https://evil.example/payload.exe", System.IO.Path.GetTempFileName(), progress: null);

        (await act.Should().ThrowAsync<System.Net.Http.HttpRequestException>())
            .WithMessage("*untrusted host*");
    }

    [Fact]
    public async Task DownloadAsync_error_does_not_echo_the_full_untrusted_url()
    {
        // The rejected URL is attacker-influenced; its path/query must not land in a message that gets
        // logged or shown.
        var proxy = new GithubProxy();

        var act = async () => await proxy.DownloadAsync(
            "https://evil.example/secret-path?tok=abc", System.IO.Path.GetTempFileName(), progress: null);

        (await act.Should().ThrowAsync<System.Net.Http.HttpRequestException>())
            .Which.Message.Should().NotContain("secret-path").And.NotContain("tok=abc");
    }

    // ── Mirror candidates ─────────────────────────────────────────────────────

    [Fact]
    public void Direct_url_is_always_tried_first()
    {
        const string url = "https://github.com/a/b/releases/download/v1/x.zip";

        GithubProxy.Candidates(url).First().Should().Be(url);
    }

    [Fact]
    public void Api_urls_get_api_mirrors_and_downloads_get_download_mirrors()
    {
        var api = GithubProxy.Candidates("https://api.github.com/repos/a/b/releases/latest").Skip(1).ToList();
        var dl = GithubProxy.Candidates("https://github.com/a/b/releases/download/v1/x.zip").Skip(1).ToList();

        // Wrong-class mirrors are a guaranteed wasted hop (an API mirror 400s a download and vice versa).
        api.Should().OnlyContain(c => AppConfig.GithubApiMirrors.Any(m => c.StartsWith(m)));
        dl.Should().OnlyContain(c => AppConfig.GithubDownloadMirrors.Any(m => c.StartsWith(m)));
    }

    [Fact]
    public void A_non_github_url_gets_no_mirrors() =>
        GithubProxy.Candidates("https://evil.example/x").Should().ContainSingle();

    // ── Steamless pinned digest ───────────────────────────────────────────────

    [Fact]
    public void The_pinned_steamless_digest_is_a_well_formed_sha256() =>
        AssetIntegrity.ParseDigest(AppConfig.SteamlessPinnedSha256)
            .Should().Be(AppConfig.SteamlessPinnedSha256.ToLowerInvariant(),
                "a malformed pin would silently disable the fallback it exists to provide");

    [Fact]
    public void The_pinned_asset_name_is_set() =>
        AppConfig.SteamlessPinnedAssetName.Should().EndWith(".zip");
}
