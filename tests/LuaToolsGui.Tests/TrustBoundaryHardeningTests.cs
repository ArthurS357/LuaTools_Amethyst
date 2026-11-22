using System.IO;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Security batch 1: three places where text that arrives from a third party (release JSON served by an API
/// mirror, or the URL of a Steam CEF tab) was trusted more than it should have been. No network, no CEF.
/// </summary>
public class TrustBoundaryHardeningTests
{
    // ── Steamless: the compiled-in pin beats any digest in the release JSON ───────────────────────────

    private static readonly string PinnedSha =
        AssetIntegrity.ParseDigest(AppConfig.SteamlessPinnedSha256)!;

    private static GithubAsset Asset(string name, string? digest) => new() { Name = name, Digest = digest };

    [Fact]
    public void The_pinned_asset_with_no_published_digest_uses_the_pin() =>
        SteamlessService.ResolveExpectedDigest(Asset(AppConfig.SteamlessPinnedAssetName, null))
            .Should().Be(PinnedSha);

    [Fact]
    public void The_pinned_asset_with_a_matching_published_digest_uses_the_pin() =>
        SteamlessService.ResolveExpectedDigest(
                Asset(AppConfig.SteamlessPinnedAssetName, "sha256:" + AppConfig.SteamlessPinnedSha256))
            .Should().Be(PinnedSha);

    [Theory]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // a mirror's own file hash
    [InlineData("sha256:not-hex")]
    [InlineData("")]
    public void The_pinned_asset_ignores_whatever_digest_a_mirror_publishes(string mirrorDigest) =>
        // Regression: a present digest used to be taken as-is, so a mirror naming its own file and its
        // hash passed the check. The pin must win in every case.
        SteamlessService.ResolveExpectedDigest(Asset(AppConfig.SteamlessPinnedAssetName, mirrorDigest))
            .Should().Be(PinnedSha);

    [Fact]
    public void The_pinned_asset_name_is_matched_without_regard_to_case() =>
        SteamlessService.ResolveExpectedDigest(
                Asset(AppConfig.SteamlessPinnedAssetName.ToUpperInvariant(), "sha256:" + new string('a', 64)))
            .Should().Be(PinnedSha);

    [Fact]
    public void A_newer_asset_outside_the_pin_still_works_with_its_own_valid_digest()
    {
        // Must not break: a future Steamless release that carries a real digest.
        string hex = new('b', 64);

        SteamlessService.ResolveExpectedDigest(Asset("Steamless.v3.2.0.0.-.by.atom0s.zip", "sha256:" + hex))
            .Should().Be(hex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256:short")]
    [InlineData("md5:d41d8cd98f00b204e9800998ecf8427e")]
    public void An_asset_outside_the_pin_without_a_well_formed_digest_is_refused(string? digest) =>
        SteamlessService.ResolveExpectedDigest(Asset("Steamless.v3.2.0.0.-.by.atom0s.zip", digest))
            .Should().BeNull("nothing trustworthy to verify an unpinned download against");

    [Fact]
    public void The_steamless_repo_splits_into_an_owner_and_a_repo_for_the_asset_pin() =>
        // SteamlessService.EnsureToolAsync splits this on '/' and hands both halves to DownloadAssetAsync.
        AppConfig.SteamlessRepo.Split('/').Should().HaveCount(2).And.OnlyContain(s => s.Length > 0);

    [Theory]
    [InlineData("https://github.com/atom0s/Steamless/releases/download/v3.1.0.5/Steamless.v3.1.0.5.-.by.atom0s.zip", true)]
    [InlineData("https://github.com/attacker/payload/releases/download/v1/Steamless.v3.1.0.5.-.by.atom0s.zip", false)]
    public void The_steamless_download_url_must_belong_to_the_steamless_repo(string url, bool allowed)
    {
        string[] repo = AppConfig.SteamlessRepo.Split('/');

        GithubProxy.IsAssetUrlForRepo(url, repo[0], repo[1]).Should().Be(allowed);
    }

    // ── CEF: only the real Steam store gets the authenticated bridge ──────────────────────────────────

    [Theory]
    [InlineData("https://store.steampowered.com/")]
    [InlineData("https://store.steampowered.com/app/12345/")]
    [InlineData("https://store.steampowered.com/app/12345/Some_Game/?l=english&cc=us")] // query string is fine
    [InlineData("https://STORE.steampowered.com/app/1")]                               // host is case-insensitive
    [InlineData("https://store.steampowered.com:443/search/?term=a")]
    public void Real_store_pages_are_injected(string url) =>
        CefInjectorService.IsSteamStoreUrl(url).Should().BeTrue();

    [Theory]
    // THE regression cases: the old Contains() matched every one of these.
    [InlineData("https://evil.example/?store.steampowered.com")]
    [InlineData("https://evil.example/store.steampowered.com/app/1")]
    [InlineData("https://evil.example/#store.steampowered.com")]
    [InlineData("https://store.steampowered.com.evil.example/")]
    [InlineData("https://store.steampowered.com@evil.example/")]
    [InlineData("file:///C:/store.steampowered.com/index.html")]
    // Plain http is modifiable in transit; the store is https.
    [InlineData("http://store.steampowered.com/")]
    // Other Steam hosts never got the bridge and must not start to.
    [InlineData("https://steamcommunity.com/")]
    [InlineData("https://help.steampowered.com/")]
    [InlineData("https://notstore.steampowered.com/")]
    [InlineData("about:blank")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Anything_else_is_not_injected(string url) =>
        CefInjectorService.IsSteamStoreUrl(url).Should().BeFalse();

    [Fact]
    public void A_tab_without_a_url_is_not_injected() =>
        CefInjectorService.IsSteamStoreUrl(null).Should().BeFalse();

    // ── Staging paths: a remote asset name never decides where a file lands ───────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
    }

    [Fact]
    public void No_service_builds_a_local_path_from_a_release_asset_name()
    {
        // Regression guard for the bug class, not one call: Path.Combine(dir, asset.Name) lets a release JSON
        // served by a mirror choose the file's location ("..\..\x.zip"), and drops dir altogether for a
        // rooted name ("C:\x\Release.zip"). The remote name is for display; local names are fixed.
        var offenders = Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "src", "LuaToolsGui", "Services"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, line, n: i + 1)))
            .Where(x => Regex.IsMatch(x.line, @"Path\.Combine\([^;]*\basset\.Name\b"))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.n}")
            .ToList();

        offenders.Should().BeEmpty("a remote asset name must not decide a local path");
    }

    [Theory]
    [InlineData(@"AmethystTool-..\..\..\evil.zip")]
    [InlineData(@"..\..\Release.zip")]
    [InlineData(@"C:\Users\victim\Documents\Release.zip")]
    public void The_hazard_is_real_a_hostile_name_escapes_a_staging_folder_through_path_combine(string hostile)
    {
        // Documents WHY the guard above matters: this is what the old code did with asset.Name.
        string staging = Path.Combine(Path.GetTempPath(), "luatools-staging-demo");

        string full = Path.GetFullPath(Path.Combine(staging, hostile));

        full.Should().NotStartWith(staging, "the fixed local names exist because this escape is possible");
    }
}
