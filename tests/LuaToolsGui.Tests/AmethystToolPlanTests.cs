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
}
