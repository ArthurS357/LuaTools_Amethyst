using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Lote L1 (1.8.5): the unprompted Steam restart that killed running games, the CEF poll that threw
/// ~3,600 connection errors an hour with Steam closed, the Steamless child nobody could stop, and the
/// store-plugin add states kept for the whole session.
/// </summary>
public sealed class InfraHardeningTests
{
    // ── auto restart vs. a running game ─────────────────────────────

    [Fact]
    public void Game_counts_as_running_only_while_steam_is_alive() =>
        SteamService.IsGameRunning(730, () => true).Should().BeTrue();

    [Fact]
    public void Stale_RunningAppID_after_a_steam_crash_does_not_block_the_restart() =>
        SteamService.IsGameRunning(730, () => false).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void No_game_running_never_probes_the_process_list(int? runningAppId)
    {
        bool probed = false;
        SteamService.IsGameRunning(runningAppId, () => probed = true).Should().BeFalse();
        probed.Should().BeFalse("the process snapshot is only worth taking when the flag is set");
    }

    // ── plugin auto-update vs. a running game ───────────────────────

    [Fact]
    public void Unattended_dll_swap_waits_while_a_game_runs() =>
        PluginInstallerService.ShouldDeferDllSwap(dllNeedsUpdate: true, unattended: true, () => true).Should().BeTrue();

    [Theory]
    [InlineData(false, true, true)]   // frontend-only update: Steam is never stopped
    [InlineData(true, false, true)]   // the user clicked Install/Update: their call
    [InlineData(true, true, false)]   // no game: swap now
    public void Everything_else_installs_now(bool dllNeedsUpdate, bool unattended, bool gameRunning) =>
        PluginInstallerService.ShouldDeferDllSwap(dllNeedsUpdate, unattended, () => gameRunning).Should().BeFalse();

    /// <summary>Records whether the install it was handed ran as unattended.</summary>
    private sealed class UnattendedSpy(SettingsService settings)
        : PluginInstallerService(null!, null!, null!, null!, settings, null!, null!, null!, null!)
    {
        public bool? SawUnattended { get; private set; }

        public override Task<PluginStatus> GetStatusAsync(bool force = false, CancellationToken ct = default) =>
            Task.FromResult(new PluginStatus(FrontendInstalled: true, DllInstalled: true, DllMatches: false,
                InstalledTag: "v1", LatestTag: "v2", UpdateAvailable: true, MillenniumPresent: false,
                Offline: false, Port8080Busy: false));

        public override Task<(bool ok, string? error)> InstallAsync(IProgress<double?>? progress, CancellationToken ct = default)
        {
            SawUnattended = IsUnattendedInstall;
            return Task.FromResult<(bool ok, string? error)>((true, null));
        }
    }

    [Fact]
    public async Task Only_the_auto_update_flow_is_unattended_and_the_mark_does_not_leak()
    {
        string dir = Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var spy = new UnattendedSpy(new SettingsService(dir) { PluginAutoUpdate = true });

            (await spy.AutoUpdateAsync()).Should().BeTrue();
            spy.SawUnattended.Should().BeTrue();
            PluginInstallerService.IsUnattendedInstall.Should().BeFalse("the mark is scoped to the auto-update call");

            await spy.InstallAsync(progress: null);
            spy.SawUnattended.Should().BeFalse("an install the user starts is never deferred");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    // ── CEF injector cadence ─────────────────────────────────────────

    [Fact]
    public void Live_store_tab_keeps_the_fast_drain_tick() =>
        CefInjectorService.NextDelayMs(0, hasStoreTabs: true).Should().Be(150);

    [Fact]
    public void No_store_tab_sleeps_until_the_next_discovery() =>
        CefInjectorService.NextDelayMs(0, hasStoreTabs: false).Should().Be(1050);

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 3000)]
    [InlineData(3, 5000)]
    [InlineData(500, 5000)]
    public void Failed_passes_with_no_store_tab_back_off_and_cap_at_five_seconds(int failures, int expectedMs) =>
        CefInjectorService.NextDelayMs(failures, hasStoreTabs: false).Should().Be(expectedMs);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(500)]
    public void Failed_passes_with_a_live_store_tab_retry_inside_the_polyfill_timeout(int failures) =>
        CefInjectorService.NextDelayMs(failures, hasStoreTabs: true).Should().Be(1000);

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(50, true)]
    public void Remembered_store_tabs_are_dropped_once_cdp_stays_down_so_backoff_engages(int failures, bool dropped)
    {
        CefInjectorService.ShouldDropRememberedTabs(failures).Should().Be(dropped);
        if (dropped)
            CefInjectorService.NextDelayMs(failures, hasStoreTabs: false).Should().Be(5000);
    }

    // ── Steamless child: timeout and cancel both kill it ────────────

    /// <summary>ping itself, not through cmd: the assertion then watches the process that has to die. The
    /// tree part of the kill is not provable here without hunting a grandchild PID.</summary>
    private static Process StartSleeper() =>
        Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

    private static void KillQuietly(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
    }

    [Fact]
    public async Task Steamless_timeout_kills_the_child_and_reports_a_timeout()
    {
        using var proc = StartSleeper();
        try
        {
            Func<Task> act = () => SteamlessService.WaitOrKillAsync(proc, TimeSpan.FromMilliseconds(300), CancellationToken.None);

            await act.Should().ThrowAsync<TimeoutException>();
            proc.HasExited.Should().BeTrue("a wedged Steamless must not outlive its run");
        }
        finally { KillQuietly(proc); }
    }

    [Fact]
    public async Task Steamless_cancel_kills_the_child_and_stays_a_cancel()
    {
        using var proc = StartSleeper();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            Func<Task> act = () => SteamlessService.WaitOrKillAsync(proc, TimeSpan.FromMinutes(5), cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            proc.HasExited.Should().BeTrue();
        }
        finally { KillQuietly(proc); }
    }

    [Fact]
    public async Task Steamless_that_finishes_in_time_is_left_alone()
    {
        using var proc = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        await SteamlessService.WaitOrKillAsync(proc, TimeSpan.FromSeconds(30), CancellationToken.None);

        proc.ExitCode.Should().Be(0);
    }

    // ── store-plugin add states ──────────────────────────────────────

    private static readonly DateTime T0 = new(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);

    private static PluginAddService.AddState Settled(long appId) =>
        new() { AppId = appId, CreatedAt = T0.AddMinutes(appId) };

    [Fact]
    public void Add_states_at_or_under_the_cap_are_all_kept()
    {
        var states = new ConcurrentDictionary<long, PluginAddService.AddState>();
        for (long id = 1; id <= 3; id++) states[id] = Settled(id);

        PluginAddService.EvictFinished(states, cap: 3);

        states.Should().HaveCount(3);
    }

    [Fact]
    public void Oldest_settled_adds_go_first_and_in_flight_ones_never_do()
    {
        var states = new ConcurrentDictionary<long, PluginAddService.AddState>();
        // The in-flight ones are the oldest, so age alone would pick them.
        var checking = new PluginAddService.AddState { AppId = 1, Checking = true, CreatedAt = T0 };
        var downloading = new PluginAddService.AddState { AppId = 2, Busy = true, CreatedAt = T0 };
        states[1] = checking;
        states[2] = downloading;
        for (long id = 3; id <= 5; id++) states[id] = Settled(id);

        PluginAddService.EvictFinished(states, cap: 3);

        states.Keys.Should().BeEquivalentTo([1L, 2L, 5L]);
        checking.Cts.IsCancellationRequested.Should().BeFalse("eviction only forgets, it never cancels");
    }
}
