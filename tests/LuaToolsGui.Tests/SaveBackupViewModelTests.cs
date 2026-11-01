using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Backups;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Restore with Steam open. Steam Cloud syncs over the files a restore just wrote, so the dialog asks to close
/// Steam first, and a "no" (or a Steam that will not close) must leave the saves untouched.
/// </summary>
public sealed class SaveBackupViewModelTests : IDisposable
{
    private const long App = 730;
    private const string Game = "Game";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));
    private readonly string _live;
    private readonly SaveBackupService _svc;
    private readonly SaveBackupViewModel _vm;
    private readonly List<string> _asked = [];
    private int _stopCalls;

    public SaveBackupViewModelTests()
    {
        string steam = Path.Combine(_dir, "steam");
        _live = Path.Combine(steam, "userdata", "111", App.ToString(), "slot1.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(_live)!);
        File.WriteAllText(_live, "backed up");
        _svc = new SaveBackupService(() => steam, Path.Combine(_dir, "backups"));
        _vm = new SaveBackupViewModel(_svc, new ToastService(), null!)
        {
            IsSteamRunning = () => false,
            StopSteam = () => { _stopCalls++; return SteamStopOutcome.ClosedGracefully; },
            Confirm = (body, _) => { _asked.Add(body); return Task.FromResult(true); },
        };
        _vm.Load(App, "Game");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Back the save up, then change it, so a restore is visible as "backed up" coming back.</summary>
    private async Task<SaveBackupRow> BackedUpThenChanged()
    {
        (await _svc.BackupSavesAsync(App, Game)).Outcome.Should().Be(SaveBackupOutcome.Ok);
        File.WriteAllText(_live, "changed");
        return new SaveBackupRow(_svc.ListBackups(App).Single());
    }

    [Fact]
    public async Task The_first_backup_of_a_game_asks_before_creating_its_folder_and_later_ones_do_not()
    {
        await _vm.CreateCommand.ExecuteAsync(null);

        _asked.Should().ContainSingle().Which.Should().Be(Resources.Strings.Saves_Confirm_NewFolder);
        _svc.GameFolderExists(App, "Game").Should().BeTrue();
        _svc.ListBackups(App).Should().ContainSingle();

        _asked.Clear();
        await _vm.CreateCommand.ExecuteAsync(null);

        _asked.Should().BeEmpty("the folder exists now");
        _svc.ListBackups(App).Should().HaveCount(2);
    }

    [Fact]
    public async Task Declining_the_new_folder_prompt_writes_nothing()
    {
        _vm.Confirm = (body, _) => { _asked.Add(body); return Task.FromResult(false); };

        await _vm.CreateCommand.ExecuteAsync(null);

        _svc.GameFolderExists(App, "Game").Should().BeFalse();
        _svc.ListBackups(App).Should().BeEmpty();
        Directory.Exists(Path.Combine(_dir, "backups")).Should().BeFalse();
        _vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void The_success_toast_stays_up_for_eight_seconds()
    {
        SaveBackupViewModel.SavedToastDuration.Should().Be(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task Restore_with_Steam_closed_does_not_mention_Steam()
    {
        var row = await BackedUpThenChanged();

        await _vm.RestoreCommand.ExecuteAsync(row);

        File.ReadAllText(_live).Should().Be("backed up");
        _stopCalls.Should().Be(0);
        _asked.Should().NotContain(Resources.Strings.Saves_Confirm_SteamOpen);
    }

    [Fact]
    public async Task Restore_with_Steam_open_asks_closes_it_and_then_restores()
    {
        var row = await BackedUpThenChanged();
        _vm.IsSteamRunning = () => true;

        await _vm.RestoreCommand.ExecuteAsync(row);

        _asked.Should().Contain(Resources.Strings.Saves_Confirm_SteamOpen);
        _stopCalls.Should().Be(1);
        File.ReadAllText(_live).Should().Be("backed up");
    }

    [Fact]
    public async Task Declining_the_Steam_warning_touches_nothing()
    {
        var row = await BackedUpThenChanged();
        _vm.IsSteamRunning = () => true;
        _vm.Confirm = (body, _) => { _asked.Add(body); return Task.FromResult(body != Resources.Strings.Saves_Confirm_SteamOpen); };

        await _vm.RestoreCommand.ExecuteAsync(row);

        _stopCalls.Should().Be(0, "Steam is only closed once the user agrees");
        File.ReadAllText(_live).Should().Be("changed");
        _svc.ListBackups(App).Should().ContainSingle("no safety copy for a restore that did not run");
        _vm.IsBusy.Should().BeFalse();
    }

    private string ExternalZip(string body)
    {
        string path = Path.Combine(_dir, "external.zip");
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry($"111/{App}/slot1.sav").Open());
        w.Write(body);
        return path;
    }

    [Fact]
    public async Task Import_asks_then_restores_and_keeps_a_copy_of_the_current_save()
    {
        string zip = ExternalZip("imported");
        _vm.PickZip = () => zip;

        await _vm.ImportCommand.ExecuteAsync(null);

        File.ReadAllText(_live).Should().Be("imported");
        _asked.Should().ContainSingle(a => a.Contains("external.zip"));
        _svc.ListBackups(App).Should().ContainSingle("the save that was there is zipped first");
        _vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Cancelling_the_file_picker_does_nothing()
    {
        _vm.PickZip = () => null;

        await _vm.ImportCommand.ExecuteAsync(null);

        _asked.Should().BeEmpty();
        File.ReadAllText(_live).Should().Be("backed up");
        _svc.ListBackups(App).Should().BeEmpty();
    }

    [Fact]
    public async Task Declining_the_import_confirmation_touches_nothing()
    {
        _vm.PickZip = () => ExternalZip("imported");
        _vm.Confirm = (body, _) => { _asked.Add(body); return Task.FromResult(false); };

        await _vm.ImportCommand.ExecuteAsync(null);

        File.ReadAllText(_live).Should().Be("backed up");
        _svc.ListBackups(App).Should().BeEmpty();
    }

    [Fact]
    public async Task Import_with_Steam_open_waits_for_it_to_close()
    {
        _vm.PickZip = () => ExternalZip("imported");
        _vm.IsSteamRunning = () => true;
        _vm.StopSteam = () => { _stopCalls++; return SteamStopOutcome.StillRunning; };

        await _vm.ImportCommand.ExecuteAsync(null);

        _stopCalls.Should().Be(1);
        File.ReadAllText(_live).Should().Be("backed up", "a Steam that will not close blocks the import");
    }

    [Fact]
    public async Task A_Steam_that_will_not_close_blocks_the_restore()
    {
        var row = await BackedUpThenChanged();
        _vm.IsSteamRunning = () => true;
        _vm.StopSteam = () => { _stopCalls++; return SteamStopOutcome.StillRunning; };

        await _vm.RestoreCommand.ExecuteAsync(row);

        _stopCalls.Should().Be(1);
        File.ReadAllText(_live).Should().Be("changed");
        _vm.IsBusy.Should().BeFalse();
    }
}
