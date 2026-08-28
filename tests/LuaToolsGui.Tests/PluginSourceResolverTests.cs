using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using LuaToolsGui;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers the plugin source GATE — whether one configured source may be installed from, and on what
/// grounds. No network and no disk: <see cref="PluginSourceResolver"/> is the whole of that decision, and
/// it is split out from <see cref="PluginInstallerService"/> precisely so this can be pinned.
///
/// <para>
/// The property under test throughout is that every source faces the same fail-closed check, and that a
/// source failing it produces an ERROR rather than a different source. The build this replaced tried the
/// next source automatically; that made "break the first source" — which anyone able to rate-limit or
/// block a host can do — into "choose what gets installed instead". Which source is active is now the
/// user's persisted choice (see <see cref="PluginSourceSelectionTests"/>), and this type never picks one.
/// </para>
/// </summary>
public class PluginSourceResolverTests
{
    private static readonly PluginSource Amethyst = new("ArthurS357", "Front-end-Amethyst");
    private static readonly PluginSource Upstream = new("madoiscool", "LTSP");

    private const string Zip = "plugin.zip";
    private const string Dll = "winmm.dll";
    private static readonly string[] Required = [Zip, Dll];

    private const string ShaA = "sha256:b534f77daf0d62e2df94f42c2c1cf2892ac69b3b13f8f8c50af43d80708a38b8";
    private const string ShaB = "sha256:dc1594774d3003f7c82fbcdaac4cc9bbc81d7ee2ab82dd5f47c4f04cc3bd8236";

    private static string AssetUrl(PluginSource s, string tag, string name) =>
        $"https://github.com/{s.Owner}/{s.Repo}/releases/download/{tag}/{name}";

    /// <summary>A release that passes every check, for the given source.</summary>
    private static GithubRelease Good(PluginSource s, string tag = "v1.0.0") => new()
    {
        TagName = tag,
        Assets =
        [
            new GithubAsset { Name = Zip, DownloadUrl = AssetUrl(s, tag, Zip), Digest = ShaA },
            new GithubAsset { Name = Dll, DownloadUrl = AssetUrl(s, tag, Dll), Digest = ShaB },
        ],
    };

    /// <summary>A release with one asset altered — the single-variable form every rejection test uses, so a
    /// failure names the one thing that differed.</summary>
    private static GithubRelease With(PluginSource s, string assetName,
        Func<GithubAsset, GithubAsset> mutate, string tag = "v1.0.0")
    {
        var release = Good(s, tag);
        return new GithubRelease
        {
            TagName = release.TagName,
            Assets = release.Assets
                .Select(a => a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase) ? mutate(a) : a)
                .ToList(),
        };
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public void A_fully_verifiable_release_is_accepted() =>
        PluginSourceResolver.Verify(Amethyst, Good(Amethyst), Required).Should().BeNull();

    [Fact]
    public void Both_configured_sources_pass_the_same_check_on_their_own_releases()
    {
        PluginSourceResolver.IsUsable(Amethyst, Good(Amethyst), Required).Should().BeTrue();
        PluginSourceResolver.IsUsable(Upstream, Good(Upstream), Required).Should().BeTrue(
            "neither source is privileged — the gate is identical for both");
    }

    // ── Every way a source can fail ───────────────────────────────────────────

    public static TheoryData<string, GithubRelease?, PluginSourceRejection> BrokenReleases() => new()
    {
        // No release at all: repo publishes none, or GitHub and every mirror were unreachable for it.
        { "no release", null, PluginSourceRejection.NoRelease },
        // A release with no tag can never be recorded or compared, so it can never be "installed".
        { "no tag", new GithubRelease { TagName = "  ", Assets = Good(Amethyst).Assets }, PluginSourceRejection.NoTag },
        // Missing the frontend archive.
        { "missing zip", new GithubRelease
            {
                TagName = "v1.0.0",
                Assets = Good(Amethyst).Assets.Where(a => a.Name != Zip).ToList(),
            }, PluginSourceRejection.MissingAsset },
        // Missing the loader DLL.
        { "missing dll", new GithubRelease
            {
                TagName = "v1.0.0",
                Assets = Good(Amethyst).Assets.Where(a => a.Name != Dll).ToList(),
            }, PluginSourceRejection.MissingAsset },
        // Fail-closed: a release that publishes no digest cannot be proven, so it is refused — not waved through.
        { "no digest", With(Amethyst, Dll, a => new GithubAsset
            { Name = a.Name, DownloadUrl = a.DownloadUrl, Digest = null }), PluginSourceRejection.NoDigest },
        { "blank digest", With(Amethyst, Zip, a => new GithubAsset
            { Name = a.Name, DownloadUrl = a.DownloadUrl, Digest = "" }), PluginSourceRejection.NoDigest },
        { "truncated digest", With(Amethyst, Zip, a => new GithubAsset
            { Name = a.Name, DownloadUrl = a.DownloadUrl, Digest = "sha256:abc123" }), PluginSourceRejection.NoDigest },
        // The metadata points at somebody else's repository. See ForeignAssetUrl tests below for why this
        // is the case that matters most.
        { "foreign asset url", With(Amethyst, Dll, a => new GithubAsset
            {
                Name = a.Name,
                DownloadUrl = AssetUrl(new PluginSource("attacker", "payload"), "v1.0.0", Dll),
                Digest = a.Digest,
            }), PluginSourceRejection.ForeignAssetUrl },
    };

    [Theory]
    [MemberData(nameof(BrokenReleases))]
    public void A_source_that_cannot_be_verified_is_refused_with_a_named_reason(
        string because, GithubRelease? release, PluginSourceRejection expected)
    {
        var problem = PluginSourceResolver.Verify(Amethyst, release, Required);

        problem.Should().NotBeNull(because);
        problem!.Reason.Should().Be(expected);
        problem.Source.Should().Be(Amethyst, "the reason belongs to the source it is about");
    }

    [Theory]
    [MemberData(nameof(BrokenReleases))]
    public void The_health_of_the_other_source_never_rescues_a_broken_one(
        string because, GithubRelease? release, PluginSourceRejection expected)
    {
        _ = expected;

        // The regression this exists to catch: a broken source used to become "install the other one".
        // Verify answers about ONE source and has no way to see, or reach for, any other.
        PluginSourceResolver.IsUsable(Amethyst, release, Required).Should().BeFalse(because);
        PluginSourceResolver.IsUsable(Upstream, Good(Upstream), Required).Should().BeTrue(
            "the other source being perfectly healthy changes nothing about the broken one");
    }

    [Fact]
    public void The_reported_asset_is_the_one_that_actually_failed()
    {
        var problem = Verify(
            With(Amethyst, Dll, a => new GithubAsset { Name = a.Name, DownloadUrl = a.DownloadUrl, Digest = null }),
            Required);

        problem!.AssetName.Should().Be(Dll, "an error that names the wrong file sends the user hunting");
    }

    private static PluginSourceProblem? Verify(GithubRelease release, IReadOnlyList<string> required) =>
        PluginSourceResolver.Verify(Amethyst, release, required);

    // ── Origin validation: the pin is per-source, and applies to every source ──

    [Theory]
    // A different repository on the same trusted host — what a hostile API mirror would name, supplying
    // that payload's own matching digest so the later hash check passes too.
    [InlineData("https://github.com/attacker/payload/releases/download/v1.0.0/winmm.dll")]
    [InlineData("https://github.com/ArthurS357/payload/releases/download/v1.0.0/winmm.dll")]
    [InlineData("https://github.com/attacker/Front-end-Amethyst/releases/download/v1.0.0/winmm.dll")]
    // Prefix confusion.
    [InlineData("https://github.com/ArthurS357/Front-end-Amethyst-evil/releases/download/v1.0.0/winmm.dll")]
    // Not a release-asset path: /archive/ serves whatever a branch currently holds.
    [InlineData("https://github.com/ArthurS357/Front-end-Amethyst/archive/refs/heads/main.zip")]
    // Plain http would be modifiable in transit regardless of any later hash check.
    [InlineData("http://github.com/ArthurS357/Front-end-Amethyst/releases/download/v1.0.0/winmm.dll")]
    // Look-alike hosts.
    [InlineData("https://github.com.evil.example/ArthurS357/Front-end-Amethyst/releases/download/v1.0.0/winmm.dll")]
    [InlineData("https://evil.example/github.com/ArthurS357/Front-end-Amethyst/releases/download/v1.0.0/winmm.dll")]
    [InlineData("not a url at all")]
    [InlineData("")]
    public void An_asset_url_outside_the_sources_own_repo_disqualifies_it(string url)
    {
        var release = With(Amethyst, Dll, a => new GithubAsset
        { Name = a.Name, DownloadUrl = url, Digest = a.Digest });

        PluginSourceResolver.IsUsable(Amethyst, release, Required).Should().BeFalse(
            "the pin is what stops hostile metadata choosing which repository's bytes get loaded by steam.exe");
    }

    [Fact]
    public void The_same_url_rule_applies_to_every_configured_source()
    {
        // Neither source is a relaxed path. A redirected upstream release is refused exactly like a
        // redirected Amethyst one — which, since nothing falls back any more, means no install at all.
        var redirected = With(Upstream, Dll, a => new GithubAsset
        {
            Name = a.Name,
            DownloadUrl = AssetUrl(new PluginSource("attacker", "payload"), "v1.0.0", Dll),
            Digest = a.Digest,
        });

        PluginSourceResolver.Verify(Upstream, redirected, Required)!
            .Reason.Should().Be(PluginSourceRejection.ForeignAssetUrl);
    }

    [Fact]
    public void A_source_may_not_borrow_the_other_sources_asset_urls()
    {
        // One source's release naming the OTHER's (entirely legitimate) assets. Both repos are ones this
        // app trusts, which is exactly why owner/repo is compared per-source rather than against a set:
        // otherwise whichever source an attacker could break would let it serve the other's payload under
        // its own tag and manifest entry.
        var borrowed = new GithubRelease
        {
            TagName = "v9.9.9",
            Assets = Good(Upstream).Assets,
        };

        PluginSourceResolver.IsUsable(Amethyst, borrowed, Required).Should().BeFalse();
        PluginSourceResolver.IsUsable(Upstream, borrowed, Required).Should().BeTrue(
            "the very same release is fine for the source it actually belongs to");
    }

    [Fact]
    public void A_release_is_judged_whole_never_partially()
    {
        // Half a release must not survive: the zip verifies, the DLL does not. Taking the good half and
        // sourcing the rest elsewhere is the mixed-provenance install this refuses to produce.
        var halfBroken = With(Amethyst, Dll, a => new GithubAsset
        { Name = a.Name, DownloadUrl = a.DownloadUrl, Digest = null });

        PluginSourceResolver.IsUsable(Amethyst, halfBroken, Required).Should().BeFalse();
    }

    [Fact]
    public void Required_assets_are_what_decide_usability()
    {
        // A release carrying extra assets is ordinary and must not be penalised...
        var extra = Good(Amethyst);
        extra.Assets.Add(new GithubAsset
        { Name = "README.md", DownloadUrl = AssetUrl(Amethyst, "v1.0.0", "README.md"), Digest = null });

        PluginSourceResolver.IsUsable(Amethyst, extra, Required).Should().BeTrue(
            "only the assets an install actually places are checked");

        // ...but every required asset is checked, not just the first.
        Verify(Good(Amethyst), [Zip, Dll, "loader.dll"])!
            .Reason.Should().Be(PluginSourceRejection.MissingAsset);
    }

    [Fact]
    public void Asset_names_match_case_insensitively_as_github_does() =>
        PluginSourceResolver.IsUsable(Amethyst, Good(Amethyst), ["PLUGIN.ZIP", "WinMM.DLL"])
            .Should().BeTrue();

    // ── The configured catalogue itself ───────────────────────────────────────

    [Fact]
    public void The_catalogue_offers_the_fork_and_upstream()
    {
        AppConfig.PluginSources.Should().HaveCount(2);

        AppConfig.PluginSources.Should().Contain(new PluginSource(
            AppConfig.PluginPrimaryOwner, AppConfig.PluginPrimaryRepo));

        // Upstream stays in the catalogue and stays EXACTLY upstream's own plugin repo. This is the check
        // that matters if the list is ever edited: an entry pointing anywhere else would be a repository
        // the user can select — and therefore load next to steam.exe — that nobody vetted.
        AppConfig.PluginSources.Should().Contain(new PluginSource("madoiscool", "LTSP"));
        AppConfig.PluginSources.Should().Contain(new PluginSource(
            AppConfig.PluginReleasesOwner, AppConfig.PluginReleasesRepo));
    }

    [Fact]
    public void The_default_is_the_forks_own_frontend() =>
        AppConfig.DefaultPluginSource.Should().Be(new PluginSource(
            AppConfig.PluginPrimaryOwner, AppConfig.PluginPrimaryRepo));

    [Fact]
    public void The_legacy_source_is_upstream_because_that_is_all_it_could_have_been() =>
        AppConfig.LegacyPluginSource.Should().Be(new PluginSource(
            AppConfig.PluginReleasesOwner, AppConfig.PluginReleasesRepo),
            "installs written before the catalogue existed can only have come from upstream's repo, and "
          + "reading them as anything else would migrate those users on an app update");

    [Fact]
    public void Every_configured_source_is_a_complete_owner_and_repo() =>
        AppConfig.PluginSources.Should().OnlyContain(
            s => !string.IsNullOrWhiteSpace(s.Owner) && !s.Owner.Contains('/')
              && !string.IsNullOrWhiteSpace(s.Repo) && !s.Repo.Contains('/'),
            "a blank or slash-bearing half would make the pin match nothing, or the wrong thing");

    [Fact]
    public void No_source_appears_twice() =>
        AppConfig.PluginSources.Should().OnlyHaveUniqueItems(
            "two cards for one repository is a selection the user cannot reason about");

    [Fact]
    public void No_plugin_source_is_a_repo_that_publishes_the_official_app_build() =>
        AppConfig.PluginSources.Select(s => s.Slug).Should()
            .NotIntersectWith(AppConfig.UpstreamReleaseRepos,
                "the plugin catalogue must stay separate from the self-update feed's blocklist");
}
