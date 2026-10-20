using System.IO;
using System.Reflection;
using AwesomeAssertions;
using LuaToolsGui.Models;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The Mode page's card badges, checked from both ends: which template in ModeView.xaml carries each
/// badge, and which card the view-model flag lights it on.
///
/// <para>
/// AmethystTool is the fork this app ships and maintains, so it is RECOMMENDED; BetterSteamTools is the
/// project it forks, so it is UPSTREAM; the Nightly is EXPERIMENTAL only — its "CloudRedirect support"
/// badge went with the CloudRedirect repo. The AmethystTool card has its own template, so its badge lives
/// there unconditionally; the other three share the ItemsControl template and are told apart by flags.
/// </para>
/// </summary>
public class ModeBadgeTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
    }

    private static string Xaml() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "LuaToolsGui", "Views", "ModeView.xaml"));

    /// <summary>The AmethystTool card: from its DataContext to the start of the mode cards.</summary>
    private static string AmethystCard()
    {
        string xaml = Xaml();
        int start = xaml.IndexOf("DataContext=\"{Binding Amethyst}\"", StringComparison.Ordinal);
        int end = xaml.IndexOf("<ItemsControl", StringComparison.Ordinal);
        start.Should().BePositive();
        end.Should().BeGreaterThan(start);
        return xaml[start..end];
    }

    /// <summary>The template every other mode card renders through.</summary>
    private static string ModeCardTemplate()
    {
        string xaml = Xaml();
        int start = xaml.IndexOf("<ItemsControl", StringComparison.Ordinal);
        int end = xaml.IndexOf("</ItemsControl>", StringComparison.Ordinal);
        start.Should().BePositive();
        end.Should().BeGreaterThan(start);
        return xaml[start..end];
    }

    private static ModeCardViewModel Card(UnlockerMode mode) => new(mode, mode.ToString(), "desc");

    private static bool HasPublicMember(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not null;

    // ── AmethystTool ──────────────────────────────────────────────────────────

    [Fact]
    public void AmethystTool_has_recommended_badge() =>
        AmethystCard().Should().Contain("res:Strings.Mode_Recommended");

    [Fact]
    public void AmethystTool_not_marked_upstream()
    {
        AmethystCard().Should().NotContain("Mode_Badge_Upstream");
        AmethystCard().Should().NotContain("IsUpstream");
    }

    // ── BetterSteamTools ──────────────────────────────────────────────────────

    [Fact]
    public void BetterSteamTools_has_upstream_badge()
    {
        ModeCardTemplate().Should().Contain("Visibility=\"{Binding IsUpstream,");
        ModeCardTemplate().Should().Contain("res:Strings.Mode_Badge_Upstream");
        Card(UnlockerMode.OpenSteamTools).IsUpstream.Should().BeTrue();
    }

    [Fact]
    public void BetterSteamTools_no_longer_has_recommended_badge() =>
        // The shared template carries no Recommended badge at all, so no flag can put it on a mode card.
        ModeCardTemplate().Should().NotContain("Mode_Recommended");

    [Fact]
    public void BetterSteamTools_not_marked_recommended()
    {
        HasPublicMember(typeof(ModeCardViewModel), "IsRecommended").Should().BeFalse();
        Xaml().Should().NotContain("IsRecommended");
    }

    // ── BetterSteamTools Nightly ──────────────────────────────────────────────

    [Fact]
    public void BstNightly_has_no_cloudredirect_badge()
    {
        HasPublicMember(typeof(ModeCardViewModel), "SupportsCloudRedirect").Should().BeFalse();
        Xaml().Should().NotContain("SupportsCloudRedirect");
        Xaml().Should().NotContain("Mode_CloudRedirectSupport");
    }

    [Fact]
    public void BstNightly_keeps_experimental_badge()
    {
        ModeCardTemplate().Should().Contain("Visibility=\"{Binding IsExperimental,");
        Card(UnlockerMode.OpenSteamToolsNightly).IsExperimental.Should().BeTrue();
        Card(UnlockerMode.OpenSteamToolsNightly).IsUpstream.Should().BeFalse("the Nightly is not the upstream release");
    }

    // ── Wrong card, and state that must not move a badge ──────────────────────

    [Theory]
    [InlineData(UnlockerMode.SteamTools, false, false)]
    [InlineData(UnlockerMode.OpenSteamTools, true, false)]
    [InlineData(UnlockerMode.CloudRedirect, false, false)]
    [InlineData(UnlockerMode.OpenSteamToolsNightly, false, true)]
    public void Each_badge_lights_only_its_own_card(UnlockerMode mode, bool upstream, bool experimental)
    {
        var card = Card(mode);

        card.IsUpstream.Should().Be(upstream);
        card.IsExperimental.Should().Be(experimental);
    }

    [Theory]
    [InlineData(UnlockerMode.OpenSteamTools)]
    [InlineData(UnlockerMode.OpenSteamToolsNightly)]
    public void Installing_or_uninstalling_does_not_move_a_badge(UnlockerMode mode)
    {
        var card = Card(mode);
        bool upstream = card.IsUpstream, experimental = card.IsExperimental;

        card.IsActive = true;
        card.HasInstallRecord = true;
        card.IsUpstream.Should().Be(upstream);
        card.IsExperimental.Should().Be(experimental);

        card.IsActive = false;
        card.HasInstallRecord = false;
        card.IsUpstream.Should().Be(upstream);
        card.IsExperimental.Should().Be(experimental);
    }
}
