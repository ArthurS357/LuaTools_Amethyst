using LuaToolsGui;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Tests for <see cref="AppUpdateSources"/>, which decides whether this fork may download a build of
/// itself and from where.
///
/// <para>
/// This is the single most consequential piece of policy in the fork. Everything else it removes —
/// telemetry, the DonateKeys decryption-key upload — comes straight back if the app ever installs an
/// official release over itself, and it would do so silently, in the background, without the user
/// choosing it. So the rules asserted here are: nothing configured means nothing contacted; the upstream
/// repos are refused however they are spelled; and a feed is never accepted over a transport that can be
/// rewritten in flight.
/// </para>
/// </summary>
public class AppUpdateSourcesTests
{
    private static readonly string[] Blocked = ["madoiscool/LuaTools", "mendy-tools/LuaTools"];

    // ── Default: disabled ───────────────────────────────────────────────────

    [Fact]
    public void Unconfigured_DisablesSelfUpdate()
    {
        var result = AppUpdateSources.Resolve(null, Blocked);

        Assert.True(result.IsDisabled);
        Assert.Empty(result.Repos);
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public void EmptyList_DisablesSelfUpdate()
    {
        var result = AppUpdateSources.Resolve([], Blocked);
        Assert.True(result.IsDisabled);
    }

    [Fact]
    public void CompiledInDefault_PointsAtThisForkAndIsAccepted()
    {
        // Guards the actual shipped constant, not just the resolver. The default now points at the fork's
        // own repository, so updates come from the same place the build did.
        var result = AppUpdateSources.Resolve(AppConfig.GithubReleasesRepos);

        Assert.False(result.IsDisabled);
        Assert.Empty(result.Rejected);
        Assert.Equal(["https://github.com/ArthurS357/LuaTools_Amethyst"], result.Repos);
    }

    [Fact]
    public void CompiledInDefault_IsNotAnUpstreamRepo()
    {
        // The failure this guards against is subtle: someone "restoring" the old feed, or a copy-paste
        // that lands an official repo in the compiled default, where no settings.json would reveal it.
        foreach (string repo in AppConfig.GithubReleasesRepos)
            Assert.DoesNotContain(AppConfig.UpstreamReleaseRepos,
                blocked => repo.Contains(blocked, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExplicitlyEmptySetting_TurnsSelfUpdateOff()
    {
        // An empty array in settings.json is a real choice ("never update me"), distinct from the key
        // being absent (which falls back to the compiled-in default).
        Assert.True(AppUpdateSources.Resolve(Array.Empty<string>(), Blocked).IsDisabled);
    }

    // ── The upstream block: the reason this class exists ────────────────────

    [Theory]
    [InlineData("https://github.com/madoiscool/LuaTools")]
    [InlineData("https://github.com/mendy-tools/LuaTools")]
    [InlineData("https://github.com/MadoIsCool/luatools")]        // casing
    [InlineData("https://github.com/madoiscool/LuaTools/")]       // trailing slash
    [InlineData("https://github.com/madoiscool/LuaTools.git")]    // .git suffix
    [InlineData("https://github.com/madoiscool/LuaTools.git/")]   // both
    [InlineData("https://www.github.com/madoiscool/LuaTools")]    // www host
    public void RefusesUpstreamReleaseRepos_HoweverSpelled(string url)
    {
        var result = AppUpdateSources.Resolve([url], Blocked);

        Assert.True(result.IsDisabled);
        Assert.Equal(UpdateSourceRejection.UpstreamRepo, Assert.Single(result.Rejected).Reason);
    }

    [Fact]
    public void UpstreamRejection_ExplainsTheConsequence()
    {
        var result = AppUpdateSources.Resolve(["https://github.com/madoiscool/LuaTools"], Blocked);

        // The message is the only thing a confused user will see when "updates don't work", so it has to
        // say why rather than just "invalid".
        string message = result.Rejected[0].Describe();
        Assert.Contains("telemetry", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DonateKeys", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UpstreamEntry_DoesNotPoisonOtherValidEntries()
    {
        var result = AppUpdateSources.Resolve(
            ["https://github.com/madoiscool/LuaTools", "https://github.com/someone/MyFork"], Blocked);

        Assert.Equal(["https://github.com/someone/MyFork"], result.Repos);
        Assert.Single(result.Rejected);
    }

    [Fact]
    public void ShippedBlocklist_CoversTheRealUpstreamRepos()
    {
        // Uses the shipped list rather than the test's copy, so removing an entry from AppConfig fails here.
        var result = AppUpdateSources.Resolve(["https://github.com/madoiscool/LuaTools"]);
        Assert.Equal(UpdateSourceRejection.UpstreamRepo, Assert.Single(result.Rejected).Reason);
    }

    // ── Transport and host validation ───────────────────────────────────────

    [Fact]
    public void RefusesHttpFeed()
    {
        // An update feed decides which executable replaces this one; cleartext is never acceptable for it,
        // whatever the rest of the app tolerates for metadata.
        var result = AppUpdateSources.Resolve(["http://github.com/someone/MyFork"], Blocked);

        Assert.True(result.IsDisabled);
        Assert.Equal(UpdateSourceRejection.NotHttps, Assert.Single(result.Rejected).Reason);
    }

    [Theory]
    [InlineData("https://gitlab.com/someone/MyFork", UpdateSourceRejection.NotGitHub)]
    [InlineData("https://evil.example/github.com/someone/MyFork", UpdateSourceRejection.NotGitHub)]
    [InlineData("https://github.com.evil.example/someone/MyFork", UpdateSourceRejection.NotGitHub)]
    [InlineData("https://github.com/onlyowner", UpdateSourceRejection.MalformedPath)]
    [InlineData("https://github.com/a/b/c", UpdateSourceRejection.MalformedPath)]
    [InlineData("not a url", UpdateSourceRejection.NotHttps)]
    [InlineData("   ", UpdateSourceRejection.Empty)]
    public void RefusesMalformedOrForeignSources(string url, UpdateSourceRejection expected)
    {
        var result = AppUpdateSources.Resolve([url], Blocked);

        Assert.True(result.IsDisabled);
        Assert.Equal(expected, Assert.Single(result.Rejected).Reason);
    }

    // ── Accepting a real fork feed ──────────────────────────────────────────

    [Fact]
    public void AcceptsAForkRepo_AndNormalisesIt()
    {
        var result = AppUpdateSources.Resolve(["https://github.com/someone/MyFork.git/"], Blocked);

        Assert.False(result.IsDisabled);
        Assert.Equal(["https://github.com/someone/MyFork"], result.Repos);
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public void PreservesPriorityOrder()
    {
        var result = AppUpdateSources.Resolve(
            ["https://github.com/someone/Primary", "https://github.com/someone/Backup"], Blocked);

        Assert.Equal(["https://github.com/someone/Primary", "https://github.com/someone/Backup"], result.Repos);
    }

    [Fact]
    public void DeduplicatesEquivalentEntries()
    {
        var result = AppUpdateSources.Resolve(
            ["https://github.com/someone/MyFork", "https://github.com/someone/myfork.git"], Blocked);

        Assert.Single(result.Repos);
    }
}
