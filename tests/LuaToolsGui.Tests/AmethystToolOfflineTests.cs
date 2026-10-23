using System.Diagnostics;
using System.IO;
using System.Net.Http;
using AwesomeAssertions;
using LuaToolsGui.Resources;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The AmethystTool card with no usable internet — the 1.7.9.1 hotfix. A PC that boots on a network that is
/// connected but has no internet does not refuse the release lookup, it STALLS, and the card used to sit on
/// its defaults ("Install", not installed) for the whole wait even though the files were on disk.
///
/// <para>
/// The services are stubbed by subclass rather than built for real: the constructor graph is heavy and none
/// of it matters here. The stubs hand the test the one thing it needs to control — when, and whether, the
/// network answers.
/// </para>
/// </summary>
public class AmethystToolOfflineTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    public AmethystToolOfflineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private sealed class FakeService : AmethystToolService
    {
        public FakeService() : base(null!, null!, null!, null!, null!, null!) { }

        public bool Local { get; init; }
        public TaskCompletionSource<AmethystToolStatus> Status { get; } = new();
        public (bool ok, string? error) Install { get; init; } = (true, null);

        public override bool IsInstalledLocally() => Local;
        public override bool IsActive => false;
        public override bool CanUninstall => false;
        public override string? LastBackupDirectory => null;
        public override Task<AmethystToolStatus> GetStatusAsync(bool force = false, CancellationToken ct = default) =>
            Status.Task;
        public override Task<(bool ok, string? error)> InstallAsync(
            IProgress<double?>? progress, CancellationToken ct = default) => Task.FromResult(Install);
    }

    /// <summary>A proxy that never answers — what a connected-but-offline network looks like.</summary>
    private sealed class HangingProxy : GithubProxy
    {
        public override async Task<HttpResponseMessage?> SendAsync(string url, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    private sealed class DeadProxy : GithubProxy
    {
        public override Task<HttpResponseMessage?> SendAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<HttpResponseMessage?>(null);
    }

    private static AmethystToolStatus Answer(bool installed, bool offline, bool update = false) =>
        new(installed, installed ? "v1.3.2" : null, offline ? null : "v1.3.2", update, offline);

    private static AmethystToolViewModel Card(FakeService svc) => new(svc, new ToastService());

    // ── Local state is painted before the network answers ──

    [Fact]
    public async Task Installed_files_are_shown_as_installed_while_the_network_is_still_pending()
    {
        var svc = new FakeService { Local = true };
        var vm = Card(svc);

        var load = vm.LoadAsync();

        load.IsCompleted.Should().BeFalse("the stubbed network has not answered");
        vm.IsInstalled.Should().BeTrue();
        vm.StatusNotInstalled.Should().BeFalse();
        vm.InstallButtonText.Should().Be(Strings.Mode_Btn_Reinstall);

        svc.Status.SetResult(Answer(installed: true, offline: false));
        await load;
    }

    [Fact]
    public async Task Missing_files_are_not_painted_as_installed_while_pending()
    {
        // False-positive guard: the pre-paint must follow the disk, not assume "installed".
        var svc = new FakeService { Local = false };
        var vm = Card(svc);

        var load = vm.LoadAsync();

        vm.IsInstalled.Should().BeFalse();
        vm.InstallButtonText.Should().Be(Strings.Mode_Btn_Install);

        svc.Status.SetResult(Answer(installed: false, offline: false));
        await load;
    }

    // ── What the card says once the lookup has failed ──

    [Fact]
    public async Task Offline_with_files_present_says_local_version_and_shows_the_offline_hint()
    {
        var svc = new FakeService { Local = true };
        var vm = Card(svc);
        svc.Status.SetResult(Answer(installed: true, offline: true));

        await vm.LoadAsync();

        vm.IsInstalled.Should().BeTrue();
        vm.StatusOk.Should().BeTrue();
        vm.UpdateAvailable.Should().BeFalse();
        vm.Status.Should().Be(Strings.Settings_AmethystTool_OfflineStatus)
            .And.NotBe(Strings.Mode_UpToDate, "up to date is a claim nothing checked");
        vm.StatusLine.Should().Be(Strings.Settings_AmethystTool_OfflineHint);
        vm.InstallButtonText.Should().Be(Strings.Mode_Btn_Reinstall);
    }

    [Fact]
    public async Task Offline_with_no_files_stays_not_installed_and_keeps_the_generic_hint()
    {
        var svc = new FakeService { Local = false };
        var vm = Card(svc);
        svc.Status.SetResult(Answer(installed: false, offline: true));

        await vm.LoadAsync();

        vm.IsInstalled.Should().BeFalse();
        vm.Status.Should().Be(Strings.Mode_NotInstalled);
        vm.StatusLine.Should().Be(Strings.Plugin_Status_OfflineCheck);
    }

    [Fact]
    public async Task Online_and_current_still_says_up_to_date_with_no_warning()
    {
        // Regression: the offline wording must not leak into the healthy case.
        var svc = new FakeService { Local = true };
        var vm = Card(svc);
        svc.Status.SetResult(Answer(installed: true, offline: false));

        await vm.LoadAsync();

        vm.Status.Should().Be(Strings.Mode_UpToDate);
        vm.StatusLine.Should().BeNull();
    }

    // ── The lookup itself is bounded ──

    [Fact]
    public async Task A_stalled_release_lookup_gives_up_after_the_status_timeout_not_the_http_timeout()
    {
        var svc = new AmethystToolService(null!, new HangingProxy(), null!, null!, null!, null!)
        {
            StatusTimeout = TimeSpan.FromMilliseconds(150),
        };

        var sw = Stopwatch.StartNew();
        var latest = await svc.FetchLatestAsync(force: true);

        latest.Should().BeNull("a timeout is 'we don't know', which the card reports as offline");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Cancelling_the_caller_still_propagates_instead_of_reading_as_offline()
    {
        var svc = new AmethystToolService(null!, new HangingProxy(), null!, null!, null!, null!)
        {
            StatusTimeout = TimeSpan.FromSeconds(30),
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => svc.FetchLatestAsync(force: true, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Installing without internet ──

    [Fact]
    public async Task Install_without_internet_reports_the_offline_download_error()
    {
        string steamRoot = Path.Combine(_dir, "steam");
        Directory.CreateDirectory(steamRoot);
        var steam = new SteamService(new SettingsService(_dir) { SteamPathOverride = steamRoot });
        var svc = new AmethystToolService(steam, new DeadProxy(), null!, null!, null!, null!);

        var (ok, error) = await svc.InstallAsync(progress: null);

        ok.Should().BeFalse();
        error.Should().Be(Strings.Settings_AmethystTool_OfflineDownloadError);
    }

    [Fact]
    public async Task A_failed_install_frees_the_card_and_leaves_it_installed()
    {
        var svc = new FakeService
        {
            Local = true,
            Install = (false, Strings.Settings_AmethystTool_OfflineDownloadError),
        };
        svc.Status.SetResult(Answer(installed: true, offline: true));
        var vm = Card(svc);

        await vm.InstallConfirmedAsync();

        vm.IsBusy.Should().BeFalse("a stuck 'Installing…' is the failure this guards");
        vm.IsInstalled.Should().BeTrue();
    }
}
