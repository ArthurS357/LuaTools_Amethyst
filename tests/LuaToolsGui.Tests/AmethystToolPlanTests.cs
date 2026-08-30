using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Pins the decisions <see cref="AmethystToolPlan"/> makes before anything is written next to steam.exe.
///
/// <para>
/// Three of them carry real consequence. <b>Only four names may ever be installed</b> — the release archive
/// also carries README/INSTALL/TESTING/RELEASE_NOTES, and the Steam root is not a documentation folder.
/// <b>Nothing that is not a plain file name is accepted</b>, because a name with a separator or a root in
/// it stops being "a file in the Steam root" the moment it is combined with one. And <b>an existing file is
/// never overwritten without a backup step</b>: <c>dwmapi.dll</c> and <c>xinput1_4.dll</c> are proxy DLLs
/// that steam.exe loads by name, so silently replacing one that belongs to another tool breaks Steam in a
/// way the user cannot undo.
/// </para>
///
/// <para>
/// All of it is exercised with plain strings and a temp folder — no Steam install, no network.
/// </para>
/// </summary>
public class AmethystToolPlanTests
{
    private const string SteamRoot = @"C:\Program Files (x86)\Steam";
    private const string Staged = @"C:\Temp\staged";

    private static readonly DateTimeOffset Now =
        new(2026, 8, 26, 14, 30, 5, TimeSpan.Zero);

    /// <summary>Every name the release archive actually produces, payload plus documentation.</summary>
    private static readonly string[] FullArchive =
    [
        "AmethystTool.dll", "amethysttool.toml", "dwmapi.dll", "xinput1_4.dll",
        "INSTALL.txt", "README.md", "RELEASE_NOTES.md", "TESTING.md",
    ];

    private static HashSet<string> Existing(params string[] names) =>
        new(names, StringComparer.OrdinalIgnoreCase);

    /// <summary>A plan over the full archive unless <paramref name="staged"/> says otherwise.</summary>
    private static AmethystInstallPlan Plan(
        IEnumerable<string>? staged, params string[] alreadyInSteamRoot) =>
        AmethystToolPlan.Create(SteamRoot, Staged, staged ?? FullArchive,
            Existing(alreadyInSteamRoot), Now);

    // ── The allow-list ────────────────────────────────────────────────────────

    [Fact]
    public void Installs_exactly_the_four_payload_files()
    {
        var plan = Plan(null);

        plan.Rejected.Should().BeFalse();
        plan.Steps.Select(s => s.FileName).Should().BeEquivalentTo(
            "AmethystTool.dll", "amethysttool.toml", "dwmapi.dll", "xinput1_4.dll");
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("INSTALL.txt")]
    [InlineData("RELEASE_NOTES.md")]
    [InlineData("TESTING.md")]
    public void Documentation_is_never_copied_into_the_steam_root(string doc) =>
        Plan(null).Steps.Should().NotContain(s => s.FileName == doc);

    [Fact]
    public void A_file_the_archive_gains_later_is_ignored_rather_than_installed()
    {
        // Allow-list, not exclusion list: an unrecognised name defaults to "not installed", so a future
        // release cannot widen what lands in the Steam root without a change to this app.
        var plan = Plan([.. FullArchive, "something-new.dll", "payload.exe"]);

        plan.Rejected.Should().BeFalse();
        plan.Steps.Should().HaveCount(4);
        plan.Steps.Should().NotContain(s =>
            s.FileName == "something-new.dll" || s.FileName == "payload.exe");
    }

    [Fact]
    public void Every_destination_is_directly_inside_the_steam_root() =>
        Plan(null).Steps.Should().OnlyContain(s =>
            s.DestinationPath == Path.Combine(SteamRoot, s.FileName));

    // ── Names that are not plain file names ───────────────────────────────────

    [Theory]
    [InlineData(@"..\..\Windows\System32\evil.dll")]  // traversal
    [InlineData(@"C:\Windows\System32\evil.dll")]     // rooted — Path.Combine would return it outright
    [InlineData("sub/AmethystTool.dll")]              // forward slash
    [InlineData(@"sub\AmethystTool.dll")]             // backslash
    [InlineData("AmethystTool.dll:stream")]           // NTFS alternate data stream
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_name_that_is_not_a_plain_file_name_rejects_the_whole_plan(string name)
    {
        // Defence in depth: FixAnalyzer already refuses the archive for these, but this is the last place
        // a name can be stopped before it becomes a path next to steam.exe.
        var plan = Plan([.. FullArchive, name]);

        plan.Rejected.Should().BeTrue();
        plan.Steps.Should().BeEmpty();
    }

    [Fact]
    public void A_rejection_never_leaks_the_whole_offending_name()
    {
        string huge = new string('x', 400) + ".dll";

        Plan([.. FullArchive, huge]).Rejection.Should().NotContain(huge);
    }

    [Fact]
    public void The_same_name_twice_rejects_the_plan() =>
        Plan([.. FullArchive, "dwmapi.dll"]).Rejected.Should().BeTrue();

    // ── Incomplete payloads ───────────────────────────────────────────────────

    [Theory]
    [InlineData("AmethystTool.dll")]
    [InlineData("amethysttool.toml")]
    [InlineData("dwmapi.dll")]
    [InlineData("xinput1_4.dll")]
    public void A_missing_payload_file_rejects_the_plan_rather_than_installing_part_of_it(string absent)
    {
        // A proxy DLL without AmethystTool.dll beside it makes steam.exe load a forwarder whose target
        // isn't there. Half an install is worse than none.
        var plan = Plan(FullArchive.Where(n => n != absent));

        plan.Rejected.Should().BeTrue();
        plan.Rejection.Should().Contain(absent);
        plan.Steps.Should().BeEmpty();
    }

    [Fact]
    public void An_empty_archive_rejects_the_plan() =>
        Plan([]).Rejected.Should().BeTrue();

    // ── Backups ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_first_install_plans_no_backup()
    {
        var plan = Plan(null);

        plan.HasBackups.Should().BeFalse();
        plan.BackupDirectory.Should().BeNull();
        plan.Steps.Should().OnlyContain(s => s.BackupPath == null);
    }

    [Fact]
    public void An_existing_proxy_dll_is_backed_up_before_it_is_replaced()
    {
        // The case that breaks a Steam install if it goes wrong: dwmapi.dll already belongs to something.
        var plan = Plan(null, "dwmapi.dll");

        plan.HasBackups.Should().BeTrue();
        plan.Steps.Single(s => s.FileName == "dwmapi.dll").BackupPath.Should().NotBeNull();
    }

    [Fact]
    public void Only_files_that_are_actually_there_get_a_backup_step()
    {
        var plan = Plan(null, "dwmapi.dll");

        plan.Steps.Where(s => s.BackupPath is not null)
            .Select(s => s.FileName).Should().BeEquivalentTo("dwmapi.dll");
    }

    [Fact]
    public void Reinstalling_over_a_full_install_backs_every_payload_file_up()
    {
        var plan = Plan(null, [.. AmethystToolPlan.PayloadFiles]);

        plan.Steps.Should().OnlyContain(s => s.BackupPath != null);
    }

    [Fact]
    public void Backups_go_to_one_timestamped_folder_inside_the_steam_root()
    {
        // One folder per install, not a litter of ".bak" files next to steam.exe: a stray "dwmapi.dll.bak"
        // is indistinguishable from another tool's leftovers, and repeated reinstalls would pile them up.
        var plan = Plan(null, "dwmapi.dll");

        plan.BackupDirectory.Should().Be(
            Path.Combine(SteamRoot, AmethystToolPlan.BackupDirectoryPrefix + "20260826-143005"));
        plan.Steps.Single(s => s.FileName == "dwmapi.dll").BackupPath
            .Should().Be(Path.Combine(plan.BackupDirectory!, "dwmapi.dll"));
    }

    [Fact]
    public void Two_installs_at_different_times_do_not_share_a_backup_folder()
    {
        var first = AmethystToolPlan.Create(SteamRoot, Staged, FullArchive,
            Existing("dwmapi.dll"), Now);
        var second = AmethystToolPlan.Create(SteamRoot, Staged, FullArchive,
            Existing("dwmapi.dll"), Now.AddSeconds(1));

        second.BackupDirectory.Should().NotBe(first.BackupDirectory);
    }

    // ── IsInstalled ───────────────────────────────────────────────────────────

    [Fact]
    public void IsInstalled_requires_every_payload_file() =>
        AmethystToolPlan.IsInstalled(Existing([.. AmethystToolPlan.PayloadFiles])).Should().BeTrue();

    [Theory]
    [InlineData("AmethystTool.dll")]
    [InlineData("xinput1_4.dll")]
    public void IsInstalled_is_false_while_any_payload_file_is_absent(string absent) =>
        AmethystToolPlan.IsInstalled(
            Existing([.. AmethystToolPlan.PayloadFiles.Where(n => n != absent)])).Should().BeFalse();

    [Fact]
    public void IsInstalled_ignores_case_the_way_windows_does() =>
        AmethystToolPlan.IsInstalled(
            Existing("AMETHYSTTOOL.DLL", "AmethystTool.toml", "DWMAPI.DLL", "XInput1_4.dll"))
            .Should().BeTrue();

    [Fact]
    public void An_empty_steam_root_is_not_installed() =>
        AmethystToolPlan.IsInstalled(Existing()).Should().BeFalse();

    // ── Quarantine ────────────────────────────────────────────────────────────
    //
    // The payload overwrites dwmapi.dll and xinput1_4.dll, so a Mode's proxy DLLs stop being loaded on
    // their own. OpenSteamTool.dll is the one that does NOT get overwritten — AmethystTool is a fork of
    // BetterSteamTools, so its loader can still find it, and Steam comes back up with two engines hooked
    // into it. That is the state behind the reported download failure.

    [Theory]
    [InlineData("OpenSteamTool.dll")]
    [InlineData("opensteamtool.toml")]
    public void The_displaced_backends_files_are_quarantined_into_the_backup_folder(string leftover)
    {
        var plan = Plan(null, leftover);

        plan.Rejected.Should().BeFalse();
        var step = plan.Quarantine.Should().ContainSingle().Subject;
        step.FileName.Should().Be(leftover);
        step.SourcePath.Should().Be(Path.Combine(SteamRoot, leftover));
        step.BackupPath.Should().Be(Path.Combine(plan.BackupDirectory!, leftover));
    }

    [Fact]
    public void A_quarantine_on_its_own_creates_the_backup_folder()
    {
        // No payload file is being overwritten here, so nothing else would have asked for one — and a
        // quarantine step with nowhere to move to would have to delete instead.
        var plan = Plan(null, "OpenSteamTool.dll");

        plan.HasBackups.Should().BeTrue();
        plan.Steps.Should().OnlyContain(s => s.BackupPath == null);
    }

    [Fact]
    public void Nothing_is_quarantined_from_a_clean_steam_root() =>
        Plan(null).Quarantine.Should().BeEmpty();

    [Fact]
    public void The_cloud_redirect_addon_is_left_where_it_is()
    {
        // Deliberate: nothing loads cloud_redirect.dll by name — OpenSteamTool does — so once that DLL is
        // quarantined this one is already inert, and moving it would be touching a separate add-on.
        var plan = Plan(null, "cloud_redirect.dll");

        plan.Quarantine.Should().BeEmpty();
        plan.HasBackups.Should().BeFalse();
    }

    [Fact]
    public void Quarantine_never_names_a_file_the_payload_already_overwrites() =>
        AmethystToolPlan.ConflictingFiles.Should().NotIntersectWith(AmethystToolPlan.PayloadFiles);

    // ── First-run detection abstains ──────────────────────────────────────────
    //
    // With an empty slot (fresh or lost settings.json) DetectActiveModeAsync adopts a Mode by hashing
    // dwmapi.dll and xinput1_4.dll against published releases. AmethystTool is a fork, so on ITS root
    // those two can hash-match BetterSteamTools exactly — and the ACTIVE badge would land on the wrong
    // card. IsAmethystRoot is the check that makes detection abstain instead.

    [Fact]
    public void A_root_whose_shared_dlls_are_indistinguishable_is_still_recognised_as_AmethystTools()
    {
        // Exactly the ambiguous case: dwmapi.dll and xinput1_4.dll here are byte-identical to
        // BetterSteamTools', so every hash check downstream says "BetterSteamTools". The two names no Mode
        // places are what settle it, and detection abstains.
        AmethystToolPlan.IsAmethystRoot(Existing([.. AmethystToolPlan.PayloadFiles]))
            .Should().BeTrue();
    }

    [Fact]
    public void A_root_that_is_plainly_a_modes_is_left_to_normal_detection()
    {
        // BetterSteamTools' three files and nothing else — no abstention, detection proceeds and adopts
        // the Mode as it always did.
        AmethystToolPlan.IsAmethystRoot(
            Existing("dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll")).Should().BeFalse();
    }

    [Theory]
    [InlineData("AmethystTool.dll")]
    [InlineData("amethysttool.toml")]
    public void One_exclusive_file_on_its_own_is_not_enough_to_abstain(string present)
    {
        // A single leftover is not an AmethystTool root — abstaining on it would disable first-run
        // detection for a Mode user who has one stale file lying around.
        AmethystToolPlan.IsAmethystRoot(Existing(present, "dwmapi.dll", "xinput1_4.dll"))
            .Should().BeFalse();
    }

    [Fact]
    public void An_empty_root_leaves_detection_alone() =>
        AmethystToolPlan.IsAmethystRoot(Existing()).Should().BeFalse();

    [Fact]
    public void The_exclusive_files_are_payload_files_no_mode_places()
    {
        AmethystToolPlan.ExclusiveFiles.Should().BeSubsetOf(AmethystToolPlan.PayloadFiles);

        // The property the abstention rests on: if a Mode ever starts placing one of these, presence stops
        // pointing at AmethystTool and this test fails before the detection quietly starts guessing.
        foreach (var mode in UnlockerService.AllModes)
            mode.PlaceFiles.Should().NotIntersectWith(AmethystToolPlan.ExclusiveFiles,
                "no Mode may place a file AmethystTool is identified by");
    }
}
