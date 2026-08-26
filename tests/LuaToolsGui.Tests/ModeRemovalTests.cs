using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Uninstalling a Mode: the identity a Mode records under, the claim arithmetic that decides which of its
/// files may go, and the removal carried out against a SIMULATED Steam root — a temp folder, never a real
/// install, never the network.
///
/// <para>
/// The Mode page had no uninstall at all before this: a Mode overwrote files next to <c>steam.exe</c> and
/// the only trace of it was <c>settings.SelectedMode</c>. Everything here exists so removal is driven by
/// what an install RECORDED, and so that the two names Modes share with AmethystTool
/// (<c>dwmapi.dll</c>, <c>xinput1_4.dll</c>) survive an uninstall of whichever one is not being removed.
/// </para>
/// </summary>
public class ModeRemovalTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    private readonly string _steam;

    public ModeRemovalTests()
    {
        _steam = Path.Combine(_root, "Steam");
        Directory.CreateDirectory(_steam);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 26, 14, 30, 5, TimeSpan.Zero);

    private string SteamFile(string name) => Path.Combine(_steam, name);

    private void Place(params string[] names)
    {
        foreach (string name in names) File.WriteAllText(SteamFile(name), "contents:" + name);
    }

    private PluginRemovalPlan PlanFor(string pluginId, string[] recorded, params string[] claimedByOthers) =>
        PluginRemoval.Create(
            _steam, pluginId, recorded,
            Directory.EnumerateFiles(_steam).Select(Path.GetFileName).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(claimedByOthers, StringComparer.OrdinalIgnoreCase),
            Now);

    // ── The id a Mode records under ───────────────────────────────────────────

    [Theory]
    [InlineData(UnlockerMode.SteamTools, "mode-steamtools")]
    [InlineData(UnlockerMode.OpenSteamTools, "mode-opensteamtools")]
    [InlineData(UnlockerMode.OpenSteamToolsNightly, "mode-opensteamtoolsnightly")]
    [InlineData(UnlockerMode.CloudRedirect, "mode-cloudredirect")]
    public void Each_mode_has_its_own_stable_id(UnlockerMode mode, string expected) =>
        PluginIds.ForMode(mode).Should().Be(expected);

    [Fact]
    public void Mode_ids_are_recognisable_as_modes_and_the_plugin_ids_are_not()
    {
        PluginIds.IsMode(PluginIds.ForMode(UnlockerMode.SteamTools)).Should().BeTrue();
        PluginIds.IsMode(PluginIds.AmethystTool).Should().BeFalse();
        PluginIds.IsMode(PluginIds.StorePage).Should().BeFalse();
    }

    [Theory]
    [InlineData(UnlockerMode.SteamTools)]
    [InlineData(UnlockerMode.OpenSteamTools)]
    [InlineData(UnlockerMode.OpenSteamToolsNightly)]
    [InlineData(UnlockerMode.CloudRedirect)]
    public void A_mode_id_is_usable_as_a_backup_folder_name(UnlockerMode mode)
    {
        // The id becomes a directory inside the user's Steam root. A separator the removal policy refuses
        // (a colon, say) would turn every Mode uninstall into a rejection nobody could act on.
        Place("dwmapi.dll");

        var plan = PlanFor(PluginIds.ForMode(mode), ["dwmapi.dll"]);

        plan.Rejected.Should().BeFalse();
        plan.BackupDirectory.Should().EndWith(PluginIds.ForMode(mode));
    }

    // ── Claims: who still needs which file ────────────────────────────────────

    [Fact]
    public void The_active_mode_does_not_claim_its_own_files_against_itself()
    {
        // The failure this prevents: every file the active Mode placed reads as "still needed by another
        // install", the removal takes nothing out, and the user is told it succeeded.
        string modeId = PluginIds.ForMode(UnlockerMode.OpenSteamTools);

        var claims = PluginRemoval.CombineClaims(
            manifestClaims: [],
            pluginId: modeId,
            activeModePluginId: modeId,
            activeModeFiles: ["dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"]);

        claims.Should().BeEmpty();
    }

    [Fact]
    public void The_active_modes_files_are_claimed_against_a_different_plugin()
    {
        var claims = PluginRemoval.CombineClaims(
            manifestClaims: [],
            pluginId: PluginIds.AmethystTool,
            activeModePluginId: PluginIds.ForMode(UnlockerMode.SteamTools),
            activeModeFiles: ["dwmapi.dll", "xinput1_4.dll"]);

        claims.Should().BeEquivalentTo("dwmapi.dll", "xinput1_4.dll");
    }

    [Fact]
    public void With_no_mode_active_only_the_manifest_claims_anything()
    {
        var claims = PluginRemoval.CombineClaims(
            manifestClaims: ["winmm.dll"],
            pluginId: PluginIds.AmethystTool,
            activeModePluginId: null,
            activeModeFiles: ["dwmapi.dll"]);

        claims.Should().BeEquivalentTo("winmm.dll");
    }

    [Fact]
    public void A_mode_being_removed_is_still_blocked_by_what_the_manifest_says_others_own()
    {
        // Self-exclusion applies to the active-mode fallback only. AmethystTool's recorded claim on
        // dwmapi.dll must still stop the Mode's uninstall from taking it.
        string modeId = PluginIds.ForMode(UnlockerMode.SteamTools);

        var claims = PluginRemoval.CombineClaims(
            manifestClaims: ["dwmapi.dll"],
            pluginId: modeId,
            activeModePluginId: modeId,
            activeModeFiles: ["dwmapi.dll", "xinput1_4.dll"]);

        claims.Should().BeEquivalentTo("dwmapi.dll");
    }

    [Fact]
    public void A_mode_and_amethysttool_each_claim_the_other_in_the_manifest()
    {
        var manifest = InstallManifest.Empty
            .With(new InstalledPlugin(PluginIds.ForMode(UnlockerMode.SteamTools), "st-1",
                DateTimeOffset.UnixEpoch,
                [new InstalledFile("dwmapi.dll", null), new InstalledFile("xinput1_4.dll", null)]))
            .With(new InstalledPlugin(PluginIds.AmethystTool, "v1", DateTimeOffset.UnixEpoch,
                [new InstalledFile("AmethystTool.dll", null)]));

        manifest.FilesClaimedByOthers(PluginIds.AmethystTool)
            .Should().BeEquivalentTo("dwmapi.dll", "xinput1_4.dll");
        manifest.FilesClaimedByOthers(PluginIds.ForMode(UnlockerMode.SteamTools))
            .Should().BeEquivalentTo("AmethystTool.dll");
    }

    // ── Carrying the removal out ──────────────────────────────────────────────

    [Fact]
    public void A_modes_recorded_files_leave_the_steam_root()
    {
        Place("dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll", "steam.exe");

        PluginRemovalService.ApplyPlan(PlanFor(
            PluginIds.ForMode(UnlockerMode.OpenSteamTools),
            ["dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"]));

        File.Exists(SteamFile("dwmapi.dll")).Should().BeFalse();
        File.Exists(SteamFile("xinput1_4.dll")).Should().BeFalse();
        File.Exists(SteamFile("OpenSteamTool.dll")).Should().BeFalse();
        File.Exists(SteamFile("steam.exe")).Should().BeTrue();
    }

    [Fact]
    public void A_file_amethysttool_still_claims_survives_the_modes_uninstall()
    {
        // The whole point of the shared-file rule, pointed the other way round from the Plugin page: Steam
        // would otherwise be left loading a proxy whose partner had just been taken away.
        Place("dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll");

        var plan = PlanFor(
            PluginIds.ForMode(UnlockerMode.OpenSteamTools),
            ["dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"],
            "dwmapi.dll", "xinput1_4.dll");

        PluginRemovalService.ApplyPlan(plan);

        File.Exists(SteamFile("dwmapi.dll")).Should().BeTrue();
        File.Exists(SteamFile("xinput1_4.dll")).Should().BeTrue();
        File.Exists(SteamFile("OpenSteamTool.dll")).Should().BeFalse();
        plan.SharedKept.Should().BeEquivalentTo("dwmapi.dll", "xinput1_4.dll");
    }

    [Fact]
    public void Removed_mode_files_are_moved_to_a_backup_folder_not_deleted()
    {
        Place("dwmapi.dll");

        var plan = PlanFor(PluginIds.ForMode(UnlockerMode.SteamTools), ["dwmapi.dll"]);
        PluginRemovalService.ApplyPlan(plan);

        File.Exists(Path.Combine(plan.BackupDirectory!, "dwmapi.dll")).Should().BeTrue();
        File.ReadAllText(Path.Combine(plan.BackupDirectory!, "dwmapi.dll"))
            .Should().Be("contents:dwmapi.dll");
    }

    [Fact]
    public void A_mode_whose_files_are_all_shared_removes_nothing_and_creates_no_backup_folder()
    {
        Place("dwmapi.dll", "xinput1_4.dll");

        var plan = PlanFor(
            PluginIds.ForMode(UnlockerMode.SteamTools),
            ["dwmapi.dll", "xinput1_4.dll"],
            "dwmapi.dll", "xinput1_4.dll");

        plan.IsNoOp.Should().BeTrue();
        plan.BackupDirectory.Should().BeNull();
        Directory.EnumerateDirectories(_steam).Should().BeEmpty();
    }

    [Fact]
    public void Files_a_previous_mode_left_behind_are_removable_once_carried_into_the_record()
    {
        // Switching from BetterSteamTools to SteamTools overwrites two files and abandons the third. The
        // install folds the survivor into the new mode's record, so one uninstall clears the whole chain.
        Place("dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll");

        PluginRemovalService.ApplyPlan(PlanFor(
            PluginIds.ForMode(UnlockerMode.SteamTools),
            ["dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"]));

        File.Exists(SteamFile("OpenSteamTool.dll")).Should().BeFalse();
    }

    [Fact]
    public void A_recorded_name_that_is_already_gone_is_reported_rather_than_failing()
    {
        Place("dwmapi.dll");

        var plan = PlanFor(PluginIds.ForMode(UnlockerMode.SteamTools), ["dwmapi.dll", "xinput1_4.dll"]);

        plan.Rejected.Should().BeFalse();
        plan.Skipped.Should().ContainSingle(s => s.FileName == "xinput1_4.dll"
                                                 && s.Reason == RemovalSkipReason.Absent);
    }

    [Theory]
    [InlineData("dwmapi.dll", true)]
    [InlineData("OpenSteamTool.dll", true)]
    [InlineData(@"..\..\Windows\System32\evil.dll", false)]
    [InlineData(@"sub\dwmapi.dll", false)]
    [InlineData("C:dwmapi.dll", false)]
    [InlineData("dwmapi.dll:stream", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    public void Only_a_plain_file_name_may_be_carried_forward_into_a_new_modes_record(
        string name, bool allowed)
    {
        // Switching modes reads names back out of the manifest — a file a user can edit. The shape check
        // has to happen before the name becomes a path, not only when removal later refuses it.
        PluginRemoval.IsPlainFileName(name).Should().Be(allowed);
    }

    [Fact]
    public void A_record_naming_a_path_instead_of_a_file_is_refused_outright()
    {
        Place("dwmapi.dll");

        var plan = PlanFor(PluginIds.ForMode(UnlockerMode.SteamTools),
            ["dwmapi.dll", @"..\..\Windows\System32\evil.dll"]);

        plan.Rejected.Should().BeTrue();
        File.Exists(SteamFile("dwmapi.dll")).Should().BeTrue();
    }
}
