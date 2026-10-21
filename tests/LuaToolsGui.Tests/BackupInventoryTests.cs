using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services.Backups;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The pure rule behind Settings → Maintenance: which folder names are backups this app made, and which
/// paths a recursive delete may touch. Everything here is string-only; the disk side is
/// <see cref="BackupServiceTests"/>.
/// </summary>
public class BackupInventoryTests
{
    private const string Root = @"D:\Steam";

    // ── Classification ───────────────────────────────────────────────

    [Theory]
    [InlineData("AmethystTool-backup-20261020-141123", BackupKind.AmethystTool)]
    [InlineData("Removal-backup-20261020-141123", BackupKind.Removal)]
    public void A_backup_folder_name_is_recognised(string name, BackupKind kind) =>
        BackupInventory.ClassifyFolder(name).Should().Be(kind);

    [Theory]
    [InlineData("AmethystTool-backup-invalid")]          // prefix right, no timestamp
    [InlineData("AmethystTool-backup-20261320-141123")]  // month 13
    [InlineData("AmethystTool-backup-20261020-141123x")] // trailing junk
    [InlineData("AmethystTool-backup-20261020")]         // date only
    [InlineData("amethysttool-backup-20261020-141123")]  // case: the app never writes it this way
    [InlineData("Removal-backup-")]
    [InlineData("dwmapi.dll")]
    [InlineData("config")]
    [InlineData("depotcache")]
    [InlineData("steamapps")]
    [InlineData("Something-backup-20261020-141123")]     // generic "*-backup-*" is NOT ours
    public void Anything_else_is_not_a_backup(string name) =>
        BackupInventory.ClassifyFolder(name).Should().BeNull();

    [Fact]
    public void The_creation_time_comes_from_the_name_in_local_time()
    {
        BackupInventory.TryParse("Removal-backup-20261020-141123", out _, out var createdUtc).Should().BeTrue();

        createdUtc.Should().Be(new DateTime(2026, 10, 20, 14, 11, 23, DateTimeKind.Local).ToUniversalTime());
    }

    // ── Containment ──────────────────────────────────────────────────

    [Theory]
    [InlineData(@"D:\Steam\AmethystTool-backup-x", @"D:\Steam", true)]
    [InlineData(@"D:\Steam\AmethystTool-backup-x", @"D:\Steam\", true)]     // root given with its separator
    [InlineData(@"d:\steam\Removal-backup-x", @"D:\Steam", true)]           // Windows paths are case-insensitive
    [InlineData(@"D:\Steam2\AmethystTool-backup-x", @"D:\Steam", false)]    // prefix "Steam2" must not pass
    [InlineData(@"D:\Windows", @"D:\Steam", false)]
    [InlineData(@"D:\Steam\..\Windows", @"D:\Steam", false)]                // normalised before comparing
    [InlineData(@"D:\Steam", @"D:\Steam", false)]                           // the root itself is not inside it
    [InlineData(@"\\server\share\Steam\x", @"D:\Steam", false)]
    public void Only_paths_strictly_inside_the_Steam_root_pass(string path, string root, bool inside) =>
        BackupInventory.IsInsideSteamRoot(path, root).Should().Be(inside);

    // ── The delete gate ──────────────────────────────────────────────

    private static BackupEntry Entry(string fullPath) =>
        new(Path.GetFileName(fullPath), fullPath, DateTime.UtcNow, 0, 0, BackupKind.AmethystTool);

    [Fact]
    public void A_backup_folder_directly_under_the_root_may_be_deleted() =>
        BackupInventory.MayDelete(Entry(@"D:\Steam\AmethystTool-backup-20261020-141123"), Root).Should().BeTrue();

    [Theory]
    [InlineData(@"D:\Steam2\AmethystTool-backup-20261020-141123")]                 // sibling with a lookalike name
    [InlineData(@"D:\Steam\steamapps\common\Game\AmethystTool-backup-20261020-141123")] // inside, but not a child
    [InlineData(@"D:\Steam\config")]                                              // child, but not a backup
    [InlineData(@"D:\Steam\Removal-backup-20261020-141123\..\config")]            // resolves to a non-backup
    public void Anything_else_is_refused(string fullPath) =>
        BackupInventory.MayDelete(Entry(fullPath), Root).Should().BeFalse();

    // ── Age filter ───────────────────────────────────────────────────

    [Fact]
    public void Older_than_means_strictly_older_than_the_cutoff()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        BackupEntry At(DateTime t, string n) => new(n, $@"D:\Steam\{n}", t, 0, 0, BackupKind.Removal);
        var entries = new[]
        {
            At(now.AddDays(-30).AddSeconds(-1), "old"),
            At(now.AddDays(-30), "exactly-30-days"),
            At(now.AddDays(-5), "recent"),
        };

        BackupInventory.OlderThan(entries, now, 30).Select(e => e.Name).Should().Equal("old");
    }
}
