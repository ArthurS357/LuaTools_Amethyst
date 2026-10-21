using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Backups;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The disk side of the backup cleanup, against a temporary "Steam root". Junctions are used for the
/// link cases because, unlike symlinks, they need no admin rights or developer mode.
/// </summary>
public sealed class BackupServiceTests : IDisposable
{
    private const string Stamp = "20261020-141123";

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly BackupService _service = new();

    public BackupServiceTests()
    {
        _root = Path.Combine(_tmp, "Steam");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // Detach the junctions first: a recursive delete must never be pointed at a tree that holds one.
        foreach (string link in _links) DirectoryJunction.Remove(link);
        try { Directory.Delete(_tmp, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private readonly List<string> _links = [];

    private void Junction(string path, string target)
    {
        DirectoryJunction.Create(path, target).Should().BeTrue("junctions need no admin rights on NTFS");
        _links.Add(path);
    }

    private static string MakeFolder(string parent, string name, params (string File, int Bytes)[] files)
    {
        string dir = Path.Combine(parent, name);
        Directory.CreateDirectory(dir);
        foreach (var (file, bytes) in files)
        {
            string path = Path.Combine(dir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
        }
        return dir;
    }

    private async Task<BackupEntry> ScanOne(string name) =>
        (await _service.ScanAsync(_root)).Single(e => e.Name == name);

    // ── Scan ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Scan_finds_only_backup_folders()
    {
        MakeFolder(_root, $"AmethystTool-backup-{Stamp}", ("dwmapi.dll", 10));
        MakeFolder(_root, $"Removal-backup-{Stamp}", (@"luatools\winmm.dll", 10));
        MakeFolder(_root, "config");
        MakeFolder(_root, "userdata");
        MakeFolder(_root, "depotcache");
        File.WriteAllText(Path.Combine(_root, "dwmapi.dll"), "x");

        var found = await _service.ScanAsync(_root);

        found.Select(e => e.Name).Should().BeEquivalentTo(
            [$"AmethystTool-backup-{Stamp}", $"Removal-backup-{Stamp}"]);
    }

    [Fact]
    public async Task Scan_ignores_invalid_timestamp()
    {
        MakeFolder(_root, "AmethystTool-backup-invalid");
        MakeFolder(_root, "Removal-backup-2026");

        (await _service.ScanAsync(_root)).Should().BeEmpty();
    }

    [Fact]
    public async Task Scan_reports_size_and_file_count_including_subfolders()
    {
        MakeFolder(_root, $"Removal-backup-{Stamp}", (@"luatools\winmm.dll", 100), (@"luatools\x\y.bin", 50));

        var entry = await ScanOne($"Removal-backup-{Stamp}");

        entry.SizeBytes.Should().Be(150);
        entry.FileCount.Should().Be(2);
        entry.Kind.Should().Be(BackupKind.Removal);
    }

    [Fact]
    public async Task Scan_skips_a_junction_named_like_a_backup()
    {
        // Malicious: a link in the Steam root dressed as a backup, pointing at something that must survive.
        string target = MakeFolder(_tmp, "precious", ("keep.txt", 5));
        Junction(Path.Combine(_root, $"AmethystTool-backup-{Stamp}"), target);

        (await _service.ScanAsync(_root)).Should().BeEmpty();
    }

    [Fact]
    public async Task Compute_size_does_not_follow_junction()
    {
        string outside = MakeFolder(_tmp, "outside", ("big.bin", 1000));
        string backup = MakeFolder(_root, $"Removal-backup-{Stamp}", ("small.bin", 10));
        Junction(Path.Combine(backup, "link"), outside);

        var entry = await ScanOne($"Removal-backup-{Stamp}");

        entry.SizeBytes.Should().Be(10, "the 1000 bytes behind the junction are not part of the backup");
        entry.FileCount.Should().Be(1);
    }

    // ── Delete ───────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_removes_only_selected()
    {
        string a = MakeFolder(_root, $"AmethystTool-backup-{Stamp}", ("a", 7));
        string b = MakeFolder(_root, $"Removal-backup-{Stamp}", ("b", 3));
        var target = await ScanOne($"AmethystTool-backup-{Stamp}");

        var result = await _service.DeleteAsync([target], _root);

        result.Should().Be(result with { DeletedCount = 1, BytesFreed = 7, Refused = false });
        result.Errors.Should().BeEmpty();
        Directory.Exists(a).Should().BeFalse();
        Directory.Exists(b).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_refuses_any_outside_steam_root()
    {
        // False-positive guard: the sibling "Steam2" shares the prefix and carries a real backup-shaped
        // folder. One bad entry must refuse the WHOLE batch, including the legitimate one before it.
        string good = MakeFolder(_root, $"AmethystTool-backup-{Stamp}", ("a", 1));
        string evil = MakeFolder(Path.Combine(_tmp, "Steam2"), $"AmethystTool-backup-{Stamp}", ("victim", 1));
        var goodEntry = await ScanOne($"AmethystTool-backup-{Stamp}");
        var evilEntry = goodEntry with { FullPath = evil };

        var result = await _service.DeleteAsync([goodEntry, evilEntry], _root);

        result.Refused.Should().BeTrue();
        result.DeletedCount.Should().Be(0);
        Directory.Exists(good).Should().BeTrue();
        Directory.Exists(evil).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_refuses_a_non_backup_folder_inside_the_root()
    {
        string config = MakeFolder(_root, "config", ("loginusers.vdf", 1));
        var forged = new BackupEntry("config", config, DateTime.UtcNow, 1, 1, BackupKind.Removal);

        var result = await _service.DeleteAsync([forged], _root);

        result.Refused.Should().BeTrue();
        Directory.Exists(config).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_refuses_a_backup_named_junction()
    {
        string target = MakeFolder(_tmp, "precious", ("keep.txt", 5));
        string link = Path.Combine(_root, $"Removal-backup-{Stamp}");
        Junction(link, target);
        var forged = new BackupEntry(Path.GetFileName(link), link, DateTime.UtcNow, 0, 0, BackupKind.Removal);

        var result = await _service.DeleteAsync([forged], _root);

        result.Refused.Should().BeTrue();
        File.Exists(Path.Combine(target, "keep.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_does_not_follow_a_junction_nested_in_a_backup()
    {
        string outside = MakeFolder(_tmp, "outside", ("keep.txt", 5));
        string backup = MakeFolder(_root, $"Removal-backup-{Stamp}", ("a", 1));
        Junction(Path.Combine(backup, "link"), outside);
        var entry = await ScanOne($"Removal-backup-{Stamp}");

        var result = await _service.DeleteAsync([entry], _root);

        result.DeletedCount.Should().Be(1);
        Directory.Exists(backup).Should().BeFalse();
        File.Exists(Path.Combine(outside, "keep.txt")).Should().BeTrue("deleting the link must not delete its target");
    }

    [Fact]
    public void A_folder_swapped_for_a_junction_after_validation_is_not_entered()
    {
        // TOCTOU: the batch passed validation, then the folder was replaced by a junction before its turn.
        // DeleteTree re-reads the disk and refuses; what the junction points at survives.
        string target = MakeFolder(_tmp, "precious", ("keep.txt", 5));
        string swapped = Path.Combine(_root, $"Removal-backup-{Stamp}");
        Junction(swapped, target);

        var act = () => BackupService.DeleteTree(new DirectoryInfo(swapped));

        act.Should().Throw<IOException>();
        File.Exists(Path.Combine(target, "keep.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_handles_folder_already_deleted()
    {
        string a = MakeFolder(_root, $"AmethystTool-backup-{Stamp}", ("a", 1));
        var entry = await ScanOne($"AmethystTool-backup-{Stamp}");
        Directory.Delete(a, recursive: true); // the user removed it by hand after the scan

        var result = await _service.DeleteAsync([entry], _root);

        result.Should().Be(result with { DeletedCount = 0, BytesFreed = 0, Refused = false });
        result.Errors.Should().BeEmpty();
    }
}
