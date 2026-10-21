using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Backups;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Settings → Maintenance view-model over a real <see cref="BackupService"/> and a temporary Steam root.
/// The confirmation dialog and the clock are the two seams; everything else is the production path.
/// </summary>
public sealed class BackupCleanupViewModelTests : IDisposable
{
    /// <summary>"Now" for every test, in local time because backup names carry local timestamps.</summary>
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Local);

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public BackupCleanupViewModelTests()
    {
        _root = Path.Combine(_tmp, "Steam");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private BackupCleanupViewModel Vm(string steamPath, Func<int, long, Task<bool>>? confirm = null)
    {
        var settings = new SettingsService(Path.Combine(_tmp, "cfg")) { SteamPathOverride = steamPath };
        return new BackupCleanupViewModel(new BackupService(), new SteamService(settings), new ToastService())
        {
            Clock = new FixedClock(new DateTimeOffset(Now)),
            ConfirmDelete = confirm ?? ((_, _) => Task.FromResult(true)),
        };
    }

    private string Backup(string prefix, DateTime local, int bytes = 1)
    {
        string dir = Path.Combine(_root, prefix + local.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "f.bin"), new byte[bytes]);
        return dir;
    }

    [Fact]
    public async Task Empty_state_shows_message_when_no_entries()
    {
        var vm = Vm(_root);

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.IsEmpty.Should().BeTrue();
        vm.HasEntries.Should().BeFalse();
        vm.HasSteamConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task No_steam_configured_disables_list()
    {
        var vm = Vm(Path.Combine(_tmp, "no-such-steam"));

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.HasSteamConfigured.Should().BeFalse();
        vm.HasNoSteam.Should().BeTrue();
        vm.IsEmpty.Should().BeFalse("the empty message is for a Steam folder with no backups, not for no Steam");
    }

    [Fact]
    public async Task Scan_lists_the_backups_it_finds()
    {
        Backup("AmethystTool-backup-", Now.AddDays(-1));
        var vm = Vm(_root);

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.Entries.Should().ContainSingle();
        vm.IsEmpty.Should().BeFalse();
        vm.HasEntries.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_older_than_30_days_filters_correctly()
    {
        string old = Backup("Removal-backup-", Now.AddDays(-40));
        string recent = Backup("AmethystTool-backup-", Now.AddDays(-5));
        var vm = Vm(_root);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.HasOld.Should().BeTrue();

        await vm.DeleteOlderThan30DaysCommand.ExecuteAsync(null);

        Directory.Exists(old).Should().BeFalse();
        Directory.Exists(recent).Should().BeTrue();
        vm.Entries.Select(e => e.Entry.FullPath).Should().Equal(recent); // rescanned after the delete
        vm.HasOld.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_selected_removes_only_the_ticked_rows_and_confirms_what_it_will_delete()
    {
        string a = Backup("AmethystTool-backup-", Now.AddDays(-1), bytes: 7);
        string b = Backup("Removal-backup-", Now.AddDays(-2), bytes: 3);
        (int Count, long Bytes)? asked = null;
        var vm = Vm(_root, (count, bytes) => { asked = (count, bytes); return Task.FromResult(true); });
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Entries.Single(e => e.Entry.FullPath == a).IsSelected = true;

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        asked.Should().Be((1, 7L));
        Directory.Exists(a).Should().BeFalse();
        Directory.Exists(b).Should().BeTrue();
    }

    [Fact]
    public async Task Confirmation_dismissed_does_not_delete()
    {
        string a = Backup("AmethystTool-backup-", Now.AddDays(-40));
        var vm = Vm(_root, (_, _) => Task.FromResult(false));
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.DeleteOlderThan30DaysCommand.ExecuteAsync(null);

        Directory.Exists(a).Should().BeTrue();
        vm.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Two_overlapping_refreshes_do_not_duplicate_rows()
    {
        // Loaded fires on every visit to Settings; a quick leave-and-return used to run two scans that both
        // cleared and then both appended.
        Backup("AmethystTool-backup-", Now.AddDays(-1));
        var vm = Vm(_root);

        await Task.WhenAll(vm.RefreshCommand.ExecuteAsync(null), vm.RefreshCommand.ExecuteAsync(null));

        vm.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Nothing_selected_asks_nothing_and_deletes_nothing()
    {
        Backup("AmethystTool-backup-", Now.AddDays(-1));
        bool asked = false;
        var vm = Vm(_root, (_, _) => { asked = true; return Task.FromResult(true); });
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        asked.Should().BeFalse();
        vm.Entries.Should().ContainSingle();
    }
}
