using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Pins the decisions <see cref="PluginRemoval"/> makes before anything is taken OUT of the Steam root.
///
/// <para>
/// Uninstall is the direction where a mistake is unrecoverable by the user, and the specific mistake this
/// guards against is concrete: <c>dwmapi.dll</c> and <c>xinput1_4.dll</c> are installed by AmethystTool AND
/// by three of the Mode page's unlockers. "Remove what AmethystTool installs" would take the running Mode's
/// proxies with it. So removal is driven by the install RECORD, refuses any name another install still
/// claims, and moves rather than deletes.
/// </para>
/// </summary>
public class PluginRemovalPlanTests
{
    private const string SteamRoot = @"C:\Program Files (x86)\Steam";
    private const string PluginId = "amethysttool";

    private static readonly DateTimeOffset Now = new(2026, 8, 26, 14, 30, 5, TimeSpan.Zero);

    private static readonly string[] Recorded =
        ["AmethystTool.dll", "amethysttool.toml", "dwmapi.dll", "xinput1_4.dll"];

    private static HashSet<string> Set(params string[] names) =>
        new(names, StringComparer.OrdinalIgnoreCase);

    private static PluginRemovalPlan Plan(
        IEnumerable<string>? recorded = null,
        string[]? onDisk = null,
        string[]? claimedByOthers = null) =>
        PluginRemoval.Create(
            SteamRoot, PluginId,
            recorded ?? Recorded,
            Set(onDisk ?? Recorded),
            Set(claimedByOthers ?? []),
            Now);

    // ── The happy path ────────────────────────────────────────────────────────

    [Fact]
    public void Removes_exactly_what_the_record_names()
    {
        var plan = Plan();

        plan.Rejected.Should().BeFalse();
        plan.Steps.Select(s => s.FileName).Should().BeEquivalentTo(Recorded);
    }

    [Fact]
    public void A_file_the_record_does_not_name_is_never_touched()
    {
        // The whole point of working from the record: steam.exe and everything else in that folder is
        // invisible to this plan, even though it is sitting right next to the files being removed.
        var plan = Plan(onDisk: [.. Recorded, "steam.exe", "OpenSteamTool.dll", "winmm.dll"]);

        plan.Steps.Select(s => s.FileName).Should().BeEquivalentTo(Recorded);
    }

    [Fact]
    public void An_empty_record_removes_nothing()
    {
        var plan = Plan(recorded: []);

        plan.Rejected.Should().BeFalse();
        plan.IsNoOp.Should().BeTrue();
        plan.Steps.Should().BeEmpty();
    }

    [Fact]
    public void Every_source_path_is_directly_inside_the_steam_root() =>
        Plan().Steps.Should().OnlyContain(s => s.SourcePath == Path.Combine(SteamRoot, s.FileName));

    // ── Shared files: the case that breaks a Steam install ────────────────────

    [Fact]
    public void A_file_another_install_still_claims_is_kept()
    {
        // An active Mode places dwmapi.dll + xinput1_4.dll under the same names. Removing them here would
        // leave that Mode loading a proxy whose partner is gone.
        var plan = Plan(claimedByOthers: ["dwmapi.dll", "xinput1_4.dll"]);

        plan.Steps.Select(s => s.FileName).Should()
            .BeEquivalentTo("AmethystTool.dll", "amethysttool.toml");
        plan.SharedKept.Should().BeEquivalentTo("dwmapi.dll", "xinput1_4.dll");
    }

    [Fact]
    public void Sharing_is_reported_so_the_user_is_not_told_it_was_removed()
    {
        var plan = Plan(claimedByOthers: ["dwmapi.dll"]);

        plan.Skipped.Should().ContainSingle(s =>
            s.FileName == "dwmapi.dll" && s.Reason == RemovalSkipReason.ClaimedByAnotherInstall);
    }

    [Fact]
    public void A_fully_shared_record_removes_nothing_at_all()
    {
        var plan = Plan(claimedByOthers: Recorded);

        plan.IsNoOp.Should().BeTrue();
        plan.BackupDirectory.Should().BeNull();
        plan.SharedKept.Should().BeEquivalentTo(Recorded);
    }

    [Fact]
    public void Sharing_beats_presence()
    {
        // Present on disk AND claimed elsewhere: the claim wins. Any other order and the shared-file
        // protection would be decorative.
        var plan = Plan(onDisk: ["dwmapi.dll"], claimedByOthers: ["dwmapi.dll"]);

        plan.Steps.Should().BeEmpty();
        plan.SharedKept.Should().BeEquivalentTo("dwmapi.dll");
    }

    [Fact]
    public void Claim_matching_ignores_case_the_way_windows_does() =>
        Plan(claimedByOthers: ["DWMAPI.DLL"]).SharedKept.Should().BeEquivalentTo("dwmapi.dll");

    // ── Files already gone ────────────────────────────────────────────────────

    [Fact]
    public void A_recorded_file_that_is_no_longer_there_is_skipped_not_failed()
    {
        var plan = Plan(onDisk: ["AmethystTool.dll", "amethysttool.toml"]);

        plan.Rejected.Should().BeFalse();
        plan.Steps.Select(s => s.FileName).Should()
            .BeEquivalentTo("AmethystTool.dll", "amethysttool.toml");
        plan.Skipped.Where(s => s.Reason == RemovalSkipReason.Absent).Select(s => s.FileName)
            .Should().BeEquivalentTo("dwmapi.dll", "xinput1_4.dll");
    }

    [Fact]
    public void Removing_twice_is_a_no_op_the_second_time()
    {
        // Idempotence: after the first removal nothing is on disk, and the plan says so rather than erroring.
        var plan = Plan(onDisk: []);

        plan.Rejected.Should().BeFalse();
        plan.IsNoOp.Should().BeTrue();
    }

    // ── Names that are not plain file names ───────────────────────────────────

    [Theory]
    [InlineData(@"..\..\Windows\System32\evil.dll")]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData("sub/AmethystTool.dll")]
    [InlineData(@"sub\AmethystTool.dll")]
    [InlineData("AmethystTool.dll:stream")]
    [InlineData("..")]
    [InlineData("")]
    public void A_record_naming_something_that_is_not_a_plain_file_name_rejects_the_plan(string name)
    {
        // The manifest is app-written but lives on disk where a user (or anything else) can edit it, and
        // every name in it becomes a path next to steam.exe. A hand-edited record must not become a way to
        // have the app delete an arbitrary file.
        var plan = Plan(recorded: [.. Recorded, name]);

        plan.Rejected.Should().BeTrue();
        plan.Steps.Should().BeEmpty();
    }

    [Fact]
    public void A_rejection_does_not_echo_the_whole_offending_name()
    {
        string huge = new string('x', 400) + ".dll";

        Plan(recorded: [huge]).Rejection.Should().NotContain(huge);
    }

    [Fact]
    public void A_plugin_id_that_is_not_a_plain_folder_name_rejects_the_plan()
    {
        // The id becomes a directory under the backup folder.
        var plan = PluginRemoval.Create(SteamRoot, @"..\..\evil", Recorded, Set(Recorded), Set(), Now);

        plan.Rejected.Should().BeTrue();
    }

    [Fact]
    public void A_name_recorded_twice_produces_one_removal()
    {
        var plan = Plan(recorded: ["dwmapi.dll", "dwmapi.dll"]);

        plan.Rejected.Should().BeFalse();
        plan.Steps.Should().ContainSingle();
    }

    // ── Backups ───────────────────────────────────────────────────────────────

    [Fact]
    public void Every_removal_is_a_move_into_a_backup_folder()
    {
        var plan = Plan();

        plan.BackupDirectory.Should().Be(
            Path.Combine(SteamRoot, PluginRemoval.BackupDirectoryPrefix + "20260826-143005", PluginId));
        plan.Steps.Should().OnlyContain(s => s.BackupPath == Path.Combine(plan.BackupDirectory!, s.FileName));
    }

    [Fact]
    public void A_plan_that_removes_nothing_names_no_backup_folder()
    {
        // Otherwise every no-op uninstall would leave an empty directory in the user's Steam folder.
        Plan(onDisk: []).BackupDirectory.Should().BeNull();
    }

    [Fact]
    public void Backups_from_different_plugins_do_not_collide()
    {
        var a = PluginRemoval.Create(SteamRoot, "amethysttool", ["dwmapi.dll"], Set("dwmapi.dll"), Set(), Now);
        var b = PluginRemoval.Create(SteamRoot, "store-page", ["dwmapi.dll"], Set("dwmapi.dll"), Set(), Now);

        b.BackupDirectory.Should().NotBe(a.BackupDirectory);
    }

    [Fact]
    public void Two_removals_at_different_times_do_not_share_a_backup_folder()
    {
        var later = PluginRemoval.Create(
            SteamRoot, PluginId, Recorded, Set(Recorded), Set(), Now.AddSeconds(1));

        later.BackupDirectory.Should().NotBe(Plan().BackupDirectory);
    }
}
