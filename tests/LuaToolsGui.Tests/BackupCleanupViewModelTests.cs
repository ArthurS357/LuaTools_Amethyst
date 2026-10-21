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

    private string Bak(string name, DateTime localWrite, int bytes = 1)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, localWrite.ToUniversalTime());
        return path;
    }

    [Fact]
    public async Task Delete_older_than_30_days_handles_mixed_folders_and_files()
    {
        string oldFolder = Backup("Removal-backup-", Now.AddDays(-40));
        string oldBak = Bak("AmethystTool-v1.2.0.dll.bak", Now.AddDays(-40));
        string recentBak = Bak("AmethystTool-v1.3.0.dll.bak", Now.AddDays(-5));
        string unknownOld = Bak("winmm.dll.bak", Now.AddDays(-90));
        var vm = Vm(_root);
        await vm.RefreshCommand.ExecuteAsync(null);

        await vm.DeleteOlderThan30DaysCommand.ExecuteAsync(null);

        Directory.Exists(oldFolder).Should().BeFalse();
        File.Exists(oldBak).Should().BeFalse();
        File.Exists(recentBak).Should().BeTrue();
        File.Exists(unknownOld).Should().BeTrue("an unknown .bak only goes when ticked on purpose");
    }

    [Fact]
    public async Task A_loose_file_row_says_what_it_is()
    {
        Bak("AmethystTool-v1.3.0.dll.bak", Now.AddDays(-1));
        Bak("winmm.dll.bak", Now.AddDays(-1));
        var vm = Vm(_root);

        await vm.RefreshCommand.ExecuteAsync(null);

        var known = vm.Entries.Single(e => e.Name == "AmethystTool-v1.3.0.dll.bak");
        known.IsFile.Should().BeTrue();
        known.FileCountLabel.Should().Be("—");
        known.IsUnknown.Should().BeFalse();
        vm.Entries.Single(e => e.Name == "winmm.dll.bak").IsUnknown.Should().BeTrue();
    }

    [Fact]
    public void IsSelected_toggles_correctly()
    {
        var row = new BackupEntryViewModel(new BackupEntry("x", @"D:\Steam\x", DateTime.UtcNow, 0, 0, BackupKind.Removal));
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.IsSelected = true;
        row.IsSelected = false;

        row.IsSelected.Should().BeFalse();
        raised.Should().Equal(nameof(BackupEntryViewModel.IsSelected), nameof(BackupEntryViewModel.IsSelected));
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

/// <summary>
/// Pins the row's selection box. WPF-UI's implicit CheckBox style sets MinWidth=120 and an 11 px left
/// padding; inside the row's 28 px column that pushed the box out and left a sliver of accent fill that read
/// as a solid pink block, checked or not. Read as markup, like <see cref="OnboardingManualCodeTests"/>.
/// </summary>
public class BackupCleanupMarkupTests
{
    private static System.Xml.Linq.XElement RowCheckBox()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        string root = dir?.FullName
            ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
        var doc = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src", "LuaToolsGui", "Views", "SettingsView.xaml"));
        System.Xml.Linq.XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        return doc.Descendants(wpf + "CheckBox")
            .Single(c => (string?)c.Attribute("IsChecked") == "{Binding IsSelected, Mode=TwoWay}");
    }

    [Fact]
    public void The_row_checkbox_fits_its_column()
    {
        var box = RowCheckBox();

        ((string?)box.Attribute("MinWidth")).Should().Be("0");
        ((string?)box.Attribute("Padding")).Should().Be("0");
    }

    [Fact]
    public void The_row_checkbox_is_named_for_screen_readers() =>
        ((string?)RowCheckBox().Attribute("AutomationProperties.Name")).Should().Be("{Binding Name}");
}
