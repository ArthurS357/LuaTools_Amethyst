using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Uninstall carried out against a SIMULATED Steam root — a temp folder, never a real install, never the
/// network — plus the pinned-handle mechanism both installers now rely on.
/// </summary>
public class PluginRemovalApplyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    private readonly string _steam;

    public PluginRemovalApplyTests()
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

    private PluginRemovalPlan BuildPlan(string[] recorded, params string[] claimedByOthers) =>
        PluginRemoval.Create(
            _steam, "amethysttool", recorded,
            Directory.EnumerateFiles(_steam).Select(p => Path.GetFileName(p))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(claimedByOthers, StringComparer.OrdinalIgnoreCase),
            Now);

    // ── Applying a removal ────────────────────────────────────────────────────

    [Fact]
    public void Recorded_files_leave_the_steam_root()
    {
        Place("AmethystTool.dll", "amethysttool.toml", "steam.exe");

        PluginRemovalService.ApplyPlan(BuildPlan(["AmethystTool.dll", "amethysttool.toml"]));

        File.Exists(SteamFile("AmethystTool.dll")).Should().BeFalse();
        File.Exists(SteamFile("amethysttool.toml")).Should().BeFalse();
    }

    [Fact]
    public void Files_the_record_does_not_name_are_left_exactly_where_they_are()
    {
        Place("AmethystTool.dll", "steam.exe", "winmm.dll");

        PluginRemovalService.ApplyPlan(BuildPlan(["AmethystTool.dll"]));

        File.ReadAllText(SteamFile("steam.exe")).Should().Be("contents:steam.exe");
        File.ReadAllText(SteamFile("winmm.dll")).Should().Be("contents:winmm.dll");
    }

    [Fact]
    public void Nothing_is_deleted_outright_it_is_moved_into_the_backup_folder()
    {
        // An uninstall the user regrets — or one that took a proxy another tool actually needed — has to be
        // recoverable by moving files back, not by reinstalling and guessing at the version.
        Place("dwmapi.dll");

        var plan = BuildPlan(["dwmapi.dll"]);
        PluginRemovalService.ApplyPlan(plan);

        File.ReadAllText(Path.Combine(plan.BackupDirectory!, "dwmapi.dll"))
            .Should().Be("contents:dwmapi.dll");
    }

    [Fact]
    public void A_shared_file_stays_on_disk()
    {
        // The Mode-page collision, end to end: an active Mode still needs dwmapi.dll.
        Place("AmethystTool.dll", "dwmapi.dll");

        var plan = BuildPlan(["AmethystTool.dll", "dwmapi.dll"], claimedByOthers: "dwmapi.dll");
        PluginRemovalService.ApplyPlan(plan);

        File.ReadAllText(SteamFile("dwmapi.dll")).Should().Be("contents:dwmapi.dll");
        File.Exists(SteamFile("AmethystTool.dll")).Should().BeFalse();
    }

    [Fact]
    public void A_recorded_file_that_vanished_between_planning_and_applying_is_skipped()
    {
        // The plan is built from a listing taken before Steam is stopped; a file can legitimately go away
        // in between, and that must not abort the removal of the others.
        Place("AmethystTool.dll", "dwmapi.dll");
        var plan = BuildPlan(["AmethystTool.dll", "dwmapi.dll"]);
        File.Delete(SteamFile("dwmapi.dll"));

        var act = () => PluginRemovalService.ApplyPlan(plan);

        act.Should().NotThrow();
        File.Exists(SteamFile("AmethystTool.dll")).Should().BeFalse();
    }

    [Fact]
    public void Applying_twice_is_harmless()
    {
        Place("AmethystTool.dll");
        var plan = BuildPlan(["AmethystTool.dll"]);
        PluginRemovalService.ApplyPlan(plan);

        var act = () => PluginRemovalService.ApplyPlan(plan);

        act.Should().NotThrow();
    }

    [Fact]
    public void A_no_op_plan_creates_no_folder_in_the_steam_root()
    {
        var plan = BuildPlan(["AmethystTool.dll"]); // recorded, but never placed

        PluginRemovalService.ApplyPlan(plan);

        Directory.EnumerateDirectories(_steam).Should().BeEmpty();
    }

    [Fact]
    public void Applying_a_rejected_plan_throws_rather_than_removing_anything()
    {
        Place("AmethystTool.dll");
        var rejected = BuildPlan(["AmethystTool.dll", @"..\..\evil.dll"]);

        var act = () => PluginRemovalService.ApplyPlan(rejected);

        act.Should().Throw<InvalidOperationException>();
        File.Exists(SteamFile("AmethystTool.dll")).Should().BeTrue();
    }

    // ── What the user is told ─────────────────────────────────────────────────

    [Fact]
    public void A_shared_file_is_reported_as_kept_not_as_removed()
    {
        // A user told "removed" who then finds dwmapi.dll still next to steam.exe has been misinformed.
        var outcome = new PluginRemovalOutcome(
            Removed: ["AmethystTool.dll"], SharedKept: ["dwmapi.dll"], AlreadyGone: [],
            BackupDirectory: null, SteamStopped: true, Error: null);

        RemovalMessage.Describe(outcome).Should().Contain("dwmapi.dll");
    }

    [Fact]
    public void A_missing_record_is_reported_as_such_rather_than_as_success()
    {
        var outcome = new PluginRemovalOutcome([], [], [], null, SteamStopped: false, Error: null)
        {
            NothingRecorded = true,
        };

        RemovalMessage.Describe(outcome)
            .Should().Be(LuaToolsGui.Resources.Strings.Removal_Toast_NoRecord);
    }

    [Fact]
    public void A_successful_removal_that_stopped_steam_says_so()
    {
        // Steam is deliberately not relaunched, so a user whose client vanished needs to be told why.
        var outcome = new PluginRemovalOutcome(
            ["AmethystTool.dll"], [], [], @"C:\Steam\Removal-backup-x", SteamStopped: true, Error: null);

        RemovalMessage.Describe(outcome)
            .Should().Be(LuaToolsGui.Resources.Strings.Removal_Toast_RemovedSteamStopped);
    }

    [Fact]
    public void A_failure_carries_the_reason()
    {
        var outcome = PluginRemovalOutcome.Fail("access denied");

        RemovalMessage.Describe(outcome).Should().Contain("access denied");
    }

    // ── The pinned handle (TOCTOU) ────────────────────────────────────────────
    //
    // Both installers hold AssetIntegrity.OpenPinned over verify → screen → extract/copy. These pin the
    // property that makes that worth doing: while the handle is open the staged file cannot be replaced,
    // truncated, deleted or renamed, but CAN still be read by the verifier, the archive screen and the
    // extractor. Lose either half and the hardening is either useless or breaks the install.

    private string StagedFile()
    {
        string path = Path.Combine(_root, "staged.zip");
        File.WriteAllText(path, "verified-bytes");
        return path;
    }

    [Fact]
    public void A_pinned_file_cannot_be_opened_for_writing()
    {
        string path = StagedFile();
        using var pinned = AssetIntegrity.OpenPinned(path);

        var act = () => File.OpenWrite(path);

        act.Should().Throw<IOException>();
    }

    [Fact]
    public void A_pinned_file_cannot_be_deleted()
    {
        string path = StagedFile();
        using var pinned = AssetIntegrity.OpenPinned(path);

        var act = () => File.Delete(path);

        act.Should().Throw<IOException>();
    }

    [Fact]
    public void A_pinned_file_cannot_be_renamed_out_from_under_the_handle()
    {
        // The substitution that a FileShare.Delete-granting handle would still allow: move the verified
        // file aside and drop a different one at the same path.
        string path = StagedFile();
        using var pinned = AssetIntegrity.OpenPinned(path);

        var act = () => File.Move(path, Path.Combine(_root, "moved.zip"));

        act.Should().Throw<IOException>();
    }

    [Fact]
    public void A_pinned_file_can_still_be_read_by_path()
    {
        // AssetIntegrity, FixAnalyzer and ZipFile all open staged files by path. If the pin denied readers
        // too, the hardening would break every install rather than protecting it.
        string path = StagedFile();
        using var pinned = AssetIntegrity.OpenPinned(path);

        File.ReadAllText(path).Should().Be("verified-bytes");
        AssetIntegrity.Sha256OfFile(path).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Releasing_the_handle_restores_normal_access()
    {
        // The staging folder is deleted in a finally; a handle that outlived it would leak the temp dir.
        string path = StagedFile();
        AssetIntegrity.OpenPinned(path).Dispose();

        var act = () => File.Delete(path);

        act.Should().NotThrow();
    }
}
