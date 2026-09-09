using System.IO;
using System.Text;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Pins the rescue pass that moves manifests out of the old, wrong <c>config\depotcache</c> into the real
/// <c>depotcache</c> Steam actually reads.
///
/// <para>
/// The bug it repairs was silent: a manifest in the wrong folder is invisible to Steam, so a pinned depot
/// resolves to nothing and the download never starts, with no error shown anywhere. Fixing
/// <see cref="SteamService.DepotCacheDir"/> only helps future writes — everything already installed stays
/// stranded until it is moved.
/// </para>
///
/// <para>
/// This pass runs unattended on every launch, so the properties that matter are all about restraint: it
/// never overwrites, never moves a file it cannot validate, never deletes a non-empty folder, and never
/// throws.
/// </para>
/// </summary>
public class DepotCacheMigrationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    private readonly string _legacy;
    private readonly string _real;

    public DepotCacheMigrationTests()
    {
        _legacy = Path.Combine(_dir, "steam", "config", "depotcache");
        _real = Path.Combine(_dir, "steam", "depotcache");
        Directory.CreateDirectory(_legacy);
        Directory.CreateDirectory(_real);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Legacy(string name, byte[] bytes)
    {
        string path = Path.Combine(_legacy, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private DepotCacheMigrationResult Run() => DepotCacheMigrationService.Migrate(_legacy, _real);

    // ── The happy path ────────────────────────────────────────────────────────

    [Fact]
    public void A_stranded_manifest_is_moved_into_the_folder_Steam_reads()
    {
        Legacy("1001_555.manifest", TestManifests.Build(depotId: 1001, gid: 555));

        var result = Run();

        result.Moved.Should().Be(1);
        File.Exists(Path.Combine(_real, "1001_555.manifest")).Should().BeTrue();
        File.Exists(Path.Combine(_legacy, "1001_555.manifest")).Should().BeFalse();
    }

    [Fact]
    public void The_pass_is_idempotent_so_running_it_every_launch_costs_nothing()
    {
        Legacy("1001_555.manifest", TestManifests.Build(depotId: 1001, gid: 555));

        Run().Moved.Should().Be(1);
        var second = Run();

        second.IsEmpty.Should().BeTrue();
    }

    // ── Restraint ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_file_the_real_folder_already_has_is_left_where_it_is_rather_than_overwriting()
    {
        // The name is content-addressed, so a file already there IS this file. Reclaiming the disk is not
        // worth a destructive step in a silent startup task.
        byte[] bytes = TestManifests.Build(depotId: 1001, gid: 555);
        Legacy("1001_555.manifest", bytes);
        string dest = Path.Combine(_real, "1001_555.manifest");
        File.WriteAllBytes(dest, bytes);
        var stamp = File.GetLastWriteTimeUtc(dest);

        var result = Run();

        result.Moved.Should().Be(0);
        result.AlreadyPresent.Should().Be(1);
        File.GetLastWriteTimeUtc(dest).Should().Be(stamp);
        File.Exists(Path.Combine(_legacy, "1001_555.manifest")).Should().BeTrue();
    }

    [Fact]
    public void A_file_whose_bytes_are_not_the_manifest_its_name_claims_is_never_moved()
    {
        // A bad file that reaches the real depotcache is sticky: LuaInstaller skips a destination that
        // exists, so it would survive every later fetch and break that depot permanently. Better to strand
        // it here, where it already was and where it harms nothing.
        Legacy("1001_555.manifest", TestManifests.Build(depotId: 9999, gid: 42));

        var result = Run();

        result.Moved.Should().Be(0);
        result.Rejected.Should().Be(1);
        File.Exists(Path.Combine(_real, "1001_555.manifest")).Should().BeFalse();
        File.Exists(Path.Combine(_legacy, "1001_555.manifest")).Should().BeTrue();
    }

    [Fact]
    public void Truncated_bytes_under_a_valid_name_are_rejected_too()
    {
        Legacy("1001_555.manifest", Encoding.UTF8.GetBytes("not a manifest at all"));

        Run().Rejected.Should().Be(1);
        File.Exists(Path.Combine(_real, "1001_555.manifest")).Should().BeFalse();
    }

    [Theory]
    [InlineData("nodelimiter.manifest")]
    [InlineData("_555.manifest")]
    [InlineData("1001_.manifest")]
    [InlineData("notanumber_555.manifest")]
    [InlineData("1001_notagid.manifest")]
    public void A_name_that_cannot_be_parsed_cannot_be_validated_so_it_stays_put(string name)
    {
        Legacy(name, TestManifests.Build(depotId: 1001, gid: 555));

        Run().Rejected.Should().Be(1);
        File.Exists(Path.Combine(_real, name)).Should().BeFalse();
    }

    [Fact]
    public void Files_that_are_not_manifests_are_not_even_considered()
    {
        File.WriteAllText(Path.Combine(_legacy, "readme.txt"), "hello");

        Run().IsEmpty.Should().BeTrue();
        File.Exists(Path.Combine(_legacy, "readme.txt")).Should().BeTrue();
    }

    [Fact]
    public void One_bad_file_does_not_strand_the_good_ones_beside_it()
    {
        Legacy("1001_555.manifest", TestManifests.Build(depotId: 1001, gid: 555));
        Legacy("2002_42.manifest", Encoding.UTF8.GetBytes("junk"));
        Legacy("3003_77.manifest", TestManifests.Build(depotId: 3003, gid: 77));

        var result = Run();

        result.Moved.Should().Be(2);
        result.Rejected.Should().Be(1);
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    [Fact]
    public void An_emptied_legacy_folder_is_removed_so_later_launches_stop_scanning_it()
    {
        Legacy("1001_555.manifest", TestManifests.Build(depotId: 1001, gid: 555));

        Run();

        Directory.Exists(_legacy).Should().BeFalse();
    }

    [Fact]
    public void A_legacy_folder_still_holding_something_is_kept()
    {
        Legacy("1001_555.manifest", Encoding.UTF8.GetBytes("junk")); // rejected, so it stays

        Run();

        Directory.Exists(_legacy).Should().BeTrue();
    }

    // ── Guards ────────────────────────────────────────────────────────────────

    [Fact]
    public void No_legacy_folder_is_the_common_case_and_does_nothing()
    {
        Directory.Delete(_legacy);

        Run().IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Two_paths_resolving_to_the_same_folder_are_refused_rather_than_moving_files_onto_themselves()
    {
        // Paranoia, not a real case — but File.Move onto itself deletes, so it is worth a guard and a test.
        Legacy("1001_555.manifest", TestManifests.Build(depotId: 1001, gid: 555));

        var result = DepotCacheMigrationService.Migrate(_legacy, Path.Combine(_legacy, "."));

        result.IsEmpty.Should().BeTrue();
        File.Exists(Path.Combine(_legacy, "1001_555.manifest")).Should().BeTrue();
    }

    [Fact]
    public void The_service_does_nothing_when_Steam_is_not_located()
    {
        var settings = new SettingsService(Path.Combine(_dir, "settings"));
        var steam = new SteamService(settings);
        settings.SteamPathOverride = Path.Combine(_dir, "nowhere");

        // Must not throw, and must report nothing done.
        new DepotCacheMigrationService(steam).Run().IsEmpty.Should().BeTrue();
    }
}
