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

    // ── Repository pinning ────────────────────────────────────────────────────
    //
    // The host allow-list above is necessary but NOT sufficient, and this is the gap these tests exist for:
    // every repository on GitHub shares the github.com host. A hostile API mirror serving the release JSON
    // controls browser_download_url AND digest together, so it only has to name a different github.com
    // repository and supply that payload's hash — host check passes, hash check passes, and the bytes it
    // chose get copied into the Steam root where steam.exe loads them. Pinning owner/repo is what removes
    // the choice of repository.

    private const string Owner = AppConfig.PluginReleasesOwner;
    private const string Repo = AppConfig.PluginReleasesRepo;

    [Theory]
    [InlineData("https://github.com/madoiscool/LTSP/releases/download/v1.2/winmm.dll")]
    [InlineData("https://github.com/madoiscool/LTSP/releases/download/v1.2/plugin.zip")]
    // Owner/repo comparison is case-insensitive, as GitHub itself is.
    [InlineData("https://github.com/MADOISCOOL/ltsp/releases/download/v1.2/winmm.dll")]
    // A dotted/multi-part asset name is ordinary and must not be mistaken for extra path segments.
    [InlineData("https://github.com/madoiscool/LTSP/releases/download/v3.1.0.5/a.b.-.c.zip")]
    // The redirect chain appends a query; only the path decides.
    [InlineData("https://github.com/madoiscool/LTSP/releases/download/v1/x.dll?token=abc")]
    public void Accepts_the_pinned_repositorys_own_release_assets(string url) =>
        GithubProxy.IsAssetUrlForRepo(url, Owner, Repo).Should()
            .BeTrue("a legitimate release asset must not be blocked by the pin");

    [Theory]
    // THE regression case: a different repository, same trusted host. This passed every check before.
    [InlineData("https://github.com/attacker/payload/releases/download/v1/winmm.dll")]
    [InlineData("https://github.com/madoiscool/payload/releases/download/v1/winmm.dll")]  // right owner, wrong repo
    [InlineData("https://github.com/attacker/LTSP/releases/download/v1/winmm.dll")]       // right repo, wrong owner
    [InlineData("https://github.com/madoiscool/LTSP-evil/releases/download/v1/x.dll")]    // prefix confusion
    [InlineData("https://github.com/madoiscool/LTSP/../attacker/payload/releases/download/v1/x.dll")]
    // Not a release-asset path at all: /archive/ serves whatever a branch currently holds.
    [InlineData("https://github.com/madoiscool/LTSP/archive/refs/heads/main.zip")]
    [InlineData("https://github.com/madoiscool/LTSP")]
    // Trusted hosts that carry no owner in their path are refused — they only ever occur as redirect
    // targets HttpClient follows itself, never as a URL handed to this check.
    [InlineData("https://raw.githubusercontent.com/madoiscool/LTSP/main/winmm.dll")]
    [InlineData("https://objects.githubusercontent.com/madoiscool/LTSP/x")]
    [InlineData("http://github.com/madoiscool/LTSP/releases/download/v1/x.dll")]
    [InlineData("https://github.com.evil.example/madoiscool/LTSP/releases/download/v1/x.dll")]
    [InlineData("https://evil.example/github.com/madoiscool/LTSP/releases/download/v1/x.dll")]
    [InlineData("not a url at all")]
    [InlineData("")]
    public void Rejects_assets_that_are_not_the_pinned_repositorys(string url) =>
        GithubProxy.IsAssetUrlForRepo(url, Owner, Repo).Should()
            .BeFalse("only the pinned repository's own release assets may be placed next to steam.exe");

    [Fact]
    public async Task DownloadAssetAsync_refuses_a_foreign_repository_before_making_a_request()
    {
        var proxy = new GithubProxy();

        var act = async () => await proxy.DownloadAssetAsync(
            "https://github.com/attacker/payload/releases/download/v1/winmm.dll",
            Owner, Repo, System.IO.Path.GetTempFileName(), progress: null);

        (await act.Should().ThrowAsync<System.Net.Http.HttpRequestException>())
            .WithMessage("*other than*");
    }

    [Fact]
    public void Every_pinned_mode_source_is_a_complete_owner_and_repo() =>
        new[]
        {
            AppConfig.SteamToolsOwner, AppConfig.SteamToolsRepo,
            AppConfig.OpenSteamToolsOwner, AppConfig.OpenSteamToolsRepo,
            AppConfig.OstNightlyOwner, AppConfig.OstNightlyRepo,
            AppConfig.CloudRedirectOwner, AppConfig.CloudRedirectRepoName,
            AppConfig.PluginReleasesOwner, AppConfig.PluginReleasesRepo,
        }.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s) && !s.Contains('/'),
            "a blank or slash-bearing half would make the pin match nothing, or the wrong thing");

    [Fact]
    public void The_combined_cloudredirect_slug_matches_its_two_halves() =>
        AppConfig.CloudRedirectRepo.Should()
            .Be($"{AppConfig.CloudRedirectOwner}/{AppConfig.CloudRedirectRepoName}");

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
