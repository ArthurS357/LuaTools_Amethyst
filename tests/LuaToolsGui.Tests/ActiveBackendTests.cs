using AwesomeAssertions;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The one slot that says which backend owns the proxy DLLs next to <c>steam.exe</c>, and the catalogue
/// that decides which Modes are offered at all.
///
/// <para>
/// Both existed as scattered conditions before, and both got the same bug: the Mode cards answered "am I
/// active?" from <c>settings.SelectedMode</c> while the AmethystTool card answered it from whether its
/// four files happened to be present, so installing one after the other left BOTH showing ACTIVE. These
/// tests pin the property that makes that unrepresentable — one value, so at most one answer.
/// </para>
/// </summary>
public class ActiveBackendTests
{
    // ── Exclusivity ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SomeModeThisBuildNeverHeardOf")]
    public void Nothing_recognisable_selects_nothing(string? persisted)
    {
        ActiveBackendPolicy.Resolve(persisted).Should().Be(ActiveBackend.None);
        ActiveBackendPolicy.ActiveMode(persisted).Should().BeNull();
        ActiveBackendPolicy.IsAmethystTool(persisted).Should().BeFalse();
    }

    [Theory]
    [InlineData(UnlockerMode.SteamTools)]
    [InlineData(UnlockerMode.OpenSteamTools)]
    [InlineData(UnlockerMode.OpenSteamToolsNightly)]
    [InlineData(UnlockerMode.CloudRedirect)]
    public void A_mode_holding_the_slot_leaves_AmethystTool_inactive(UnlockerMode mode)
    {
        string persisted = mode.ToString();

        ActiveBackendPolicy.Resolve(persisted).Should().Be(ActiveBackend.Mode);
        ActiveBackendPolicy.ActiveMode(persisted).Should().Be(mode);
        ActiveBackendPolicy.IsAmethystTool(persisted).Should()
            .BeFalse("a Mode and AmethystTool write the same dwmapi.dll — only one can own it");
    }

    [Fact]
    public void AmethystTool_holding_the_slot_leaves_every_mode_inactive()
    {
        string persisted = ActiveBackendPolicy.AmethystToolToken;

        ActiveBackendPolicy.Resolve(persisted).Should().Be(ActiveBackend.AmethystTool);
        ActiveBackendPolicy.IsAmethystTool(persisted).Should().BeTrue();
        ActiveBackendPolicy.ActiveMode(persisted).Should()
            .BeNull("AmethystTool is not an UnlockerMode, and no card may claim ACTIVE beside it");
    }

    [Fact]
    public void The_token_is_matched_case_insensitively()
    {
        // It reaches this code from a JSON file a user can edit by hand.
        ActiveBackendPolicy.Resolve("amethysttool").Should().Be(ActiveBackend.AmethystTool);
        ActiveBackendPolicy.Resolve("AMETHYSTTOOL").Should().Be(ActiveBackend.AmethystTool);
    }

    [Fact]
    public void The_token_is_not_a_mode_name()
    {
        // If it ever collided, every Modes lookup would throw on a mode with no ModeDefinition.
        Enum.GetNames<UnlockerMode>().Should()
            .NotContain(n => n.Equals(ActiveBackendPolicy.AmethystToolToken, StringComparison.OrdinalIgnoreCase));
    }

    // ── settings.json compatibility ───────────────────────────────────────────

    [Fact]
    public void A_settings_file_from_an_older_build_keeps_its_meaning()
    {
        // The field was always a free-form string parsed as an enum name. Nothing about the format moved.
        ActiveBackendPolicy.ActiveMode("OpenSteamTools").Should().Be(UnlockerMode.OpenSteamTools);
        ActiveBackendPolicy.ActiveMode("SteamTools").Should()
            .Be(UnlockerMode.SteamTools, "SteamTools is retired, not deleted — its record must stay reachable");
    }

    [Theory]
    [InlineData("42")]
    [InlineData("-1")]
    public void A_numeric_value_outside_the_enum_selects_nothing(string persisted) =>
        ActiveBackendPolicy.ActiveMode(persisted).Should()
            .BeNull("Enum.TryParse accepts any number; an undefined one is not a mode");

    // ── Which modes are offered ───────────────────────────────────────────────

    private static ModeDefinition Def(UnlockerMode mode, bool retired = false) =>
        new(mode, mode.ToString(), "desc", ModeKind.Loose, "owner", "repo", null,
            ["dwmapi.dll"], null, null, null, null, HiddenUnlessFile: null, Retired: retired);

    private static readonly ModeDefinition[] Catalogue =
    [
        Def(UnlockerMode.SteamTools, retired: true),
        Def(UnlockerMode.OpenSteamTools),
        Def(UnlockerMode.OpenSteamToolsNightly),
        Def(UnlockerMode.CloudRedirect),
    ];

    [Fact]
    public void A_retired_mode_is_not_offered()
    {
        ModeCatalog.Offered(Catalogue, active: null)
            .Select(d => d.Mode).Should()
            .Equal(UnlockerMode.OpenSteamTools, UnlockerMode.OpenSteamToolsNightly);
    }

    [Fact]
    public void A_retired_mode_that_is_still_active_keeps_its_card()
    {
        // Its files are next to steam.exe right now, and that card is the only route to Uninstall.
        ModeCatalog.Offered(Catalogue, active: UnlockerMode.SteamTools)
            .Select(d => d.Mode).Should().Contain(UnlockerMode.SteamTools);
    }

    [Fact]
    public void CloudRedirect_stays_hidden_even_when_it_is_active()
    {
        // Unlike retirement, this one is temporary and unconditional — the existing behaviour, unchanged.
        ModeCatalog.Offered(Catalogue, active: UnlockerMode.CloudRedirect)
            .Select(d => d.Mode).Should().NotContain(UnlockerMode.CloudRedirect);
    }

    [Fact]
    public void Definition_order_is_preserved()
    {
        var reordered = new[] { Def(UnlockerMode.OpenSteamToolsNightly), Def(UnlockerMode.OpenSteamTools) };

        ModeCatalog.Offered(reordered, active: null).Select(d => d.Mode).Should()
            .Equal(UnlockerMode.OpenSteamToolsNightly, UnlockerMode.OpenSteamTools);
    }

    [Fact]
    public void A_definition_is_offered_by_default()
    {
        // Retired has to be opted into: a new mode must never be born hidden.
        Def(UnlockerMode.OpenSteamTools).Retired.Should().BeFalse();
    }

    // ── The real catalogue ────────────────────────────────────────────────────

    [Fact]
    public void SteamTools_is_retired_but_still_defined()
    {
        var steamTools = UnlockerService.AllModes.Single(d => d.Mode == UnlockerMode.SteamTools);

        steamTools.Retired.Should().BeTrue("upstream no longer publishes updates for it");

        // Deleting the definition would orphan every mode-steamtools manifest record (leaving those files
        // un-uninstallable) and would drop the PlaceFiles that stop a still-active SteamTools install
        // having its proxy DLLs removed by something else. See PluginRemovalService.ClaimedByOthers.
        steamTools.PlaceFiles.Should().Contain(["dwmapi.dll", "xinput1_4.dll"]);
        PluginIds.ForMode(UnlockerMode.SteamTools).Should().Be("mode-steamtools");
    }

    [Fact]
    public void The_page_offers_only_the_two_BetterSteamTools_builds()
    {
        ModeCatalog.Offered(UnlockerService.AllModes, active: null)
            .Select(d => d.Mode).Should()
            .Equal(UnlockerMode.OpenSteamTools, UnlockerMode.OpenSteamToolsNightly);
    }

    [Fact]
    public void Every_offered_mode_shares_the_proxy_DLLs_AmethystTool_writes()
    {
        // Why the single slot has to exist at all: these are the same two files.
        foreach (var def in ModeCatalog.Offered(UnlockerService.AllModes, active: null))
            def.PlaceFiles.Should().Contain(["dwmapi.dll", "xinput1_4.dll"], def.DisplayName);

        AmethystToolPlan.PayloadFiles.Should().Contain(["dwmapi.dll", "xinput1_4.dll"]);
    }
}
