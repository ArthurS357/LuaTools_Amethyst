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
        (await _svc.BackupSavesAsync(App)).Outcome.Should().Be(SaveBackupOutcome.Ok);
        File.WriteAllText(_live, "changed");
        return new SaveBackupRow(_svc.ListBackups(App).Single());
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
