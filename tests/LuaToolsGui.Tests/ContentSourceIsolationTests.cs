using LuaToolsGui;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Pins the separation between the app's SELF-UPDATE feed and the sources it downloads CONTENT from.
///
/// <para>
/// Disabling self-update and pointing it at a fork repo is a change to how the app replaces its own
/// binary. It must not touch how it fetches plugins, unlockers, Steamless or manifests — those are the
/// app's entire purpose. The two concerns look similar (both are "GitHub repos in AppConfig"), which is
/// exactly why a well-meant cleanup could collapse them; these tests fail loudly if that happens.
/// </para>
/// </summary>
public class ContentSourceIsolationTests
{
    [Fact]
    public void PluginReleaseSource_IsStillConfigured()
    {
        // The store-page plugin is fetched from its own repo, unrelated to the (now empty) self-update
        // feed. Blanking these while disabling self-update would break plugin install outright.
        Assert.False(string.IsNullOrWhiteSpace(AppConfig.PluginReleasesOwner));
        Assert.False(string.IsNullOrWhiteSpace(AppConfig.PluginReleasesRepo));
    }

    [Fact]
    public void SelfUpdateFeed_IsSeparateFromEveryContentSource()
    {
        // The self-update feed is the fork's own repo...
        var selfUpdate = AppUpdateSources.Resolve(AppConfig.GithubReleasesRepos);
        Assert.False(selfUpdate.IsDisabled);

        // ...and must not be any of the content sources: collapsing the two would mean a change to how
        // the app updates itself could silently redirect where plugins come from.
        // EVERY catalogued plugin source, not just upstream's: the fork's own frontend repo lives under the
        // same owner as the self-update feed, which is precisely the pair a careless edit would merge.
        foreach (string feed in selfUpdate.Repos)
        {
            foreach (var source in AppConfig.PluginSources)
                Assert.DoesNotContain(source.Slug, feed, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(AppConfig.SteamlessRepo, feed, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(AppConfig.CloudRedirectRepo, feed, StringComparison.OrdinalIgnoreCase);
        }

        // ...while every content source stays configured.
        Assert.False(string.IsNullOrWhiteSpace(AppConfig.PluginReleasesRepo));
        Assert.False(string.IsNullOrWhiteSpace(AppConfig.SteamlessRepo));
        Assert.False(string.IsNullOrWhiteSpace(AppConfig.CloudRedirectRepo));
        Assert.False(string.IsNullOrWhiteSpace(AppConfig.ManifestBackendUrl));
        Assert.False(string.IsNullOrWhiteSpace(AppConfig.ApiBaseUrl));
    }

    [Fact]
    public void ContentDownloadsGoOverHttps()
    {
        // ManifestBackendUrl is the documented exception (metadata only, no TLS available on that host);
        // everything that transfers CONTENT must be https.
        Assert.StartsWith("https://", AppConfig.ApiBaseUrl, StringComparison.Ordinal);
        Assert.StartsWith("https://", AppConfig.HubcapBaseUrl, StringComparison.Ordinal);
        Assert.StartsWith("https://", AppConfig.SupabaseUrl, StringComparison.Ordinal);

        foreach (string mirror in AppConfig.GithubDownloadMirrors)
            Assert.StartsWith("https://", mirror, StringComparison.Ordinal);
        foreach (string mirror in AppConfig.GithubApiMirrors)
            Assert.StartsWith("https://", mirror, StringComparison.Ordinal);
    }

    [Fact]
    public void UpstreamBlocklist_DoesNotCoverContentRepos()
    {
        // The blocklist must target only the repos that publish the official APP build. If it ever caught
        // a content repo (the plugin lives under the same owner), disabling self-update would silently
        // take plugin downloads with it. Checked for every selectable source: madoiscool publishes both
        // the official app AND a plugin the user may legitimately choose, so the blocklist has to tell
        // "madoiscool/LuaTools" from "madoiscool/LTSP" rather than matching on the owner.
        foreach (var source in AppConfig.PluginSources)
            Assert.DoesNotContain(source.Slug, AppConfig.UpstreamReleaseRepos, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(AppConfig.SteamlessRepo, AppConfig.UpstreamReleaseRepos, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(AppConfig.CloudRedirectRepo, AppConfig.UpstreamReleaseRepos, StringComparer.OrdinalIgnoreCase);
    }
}
