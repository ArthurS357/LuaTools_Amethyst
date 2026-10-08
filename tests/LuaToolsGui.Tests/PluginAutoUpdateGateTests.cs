using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// F-01: the Steam-open update flow swapped the plugin DLL in the Steam root without consulting
/// <c>PluginAutoUpdate</c>, because it called GetStatusAsync/InstallAsync inline instead of going through
/// <see cref="PluginInstallerService.AutoUpdateAsync"/>, the only place the gate lives.
/// </summary>
public sealed class PluginAutoUpdateGateTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    public PluginAutoUpdateGateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static PluginStatus Status(bool updateAvailable, bool dllMatches = false) =>
        new(FrontendInstalled: true, DllInstalled: true, DllMatches: dllMatches, InstalledTag: "v1",
            LatestTag: "v2", UpdateAvailable: updateAvailable, MillenniumPresent: true, Offline: false,
            Port8080Busy: false);

    /// <summary>Counts the two calls that matter; everything else is unused on this path.</summary>
    private sealed class Spy(SettingsService settings, PluginStatus status)
        : PluginInstallerService(null!, null!, null!, null!, settings, null!, null!, null!, null!)
    {
        public int StatusCalls { get; private set; }
        public int InstallCalls { get; private set; }

        public override Task<PluginStatus> GetStatusAsync(bool force = false, CancellationToken ct = default)
        {
            StatusCalls++;
            return Task.FromResult(status);
        }

        public override Task<(bool ok, string? error)> InstallAsync(
            IProgress<double?>? progress, CancellationToken ct = default)
        {
            InstallCalls++;
            return Task.FromResult<(bool ok, string? error)>((true, null));
        }
    }

    private SettingsService Settings(bool autoUpdate) =>
        new(_dir) { PluginAutoUpdate = autoUpdate };

    [Fact]
    public async Task Gate_off_with_an_update_available_neither_checks_nor_installs()
    {
        var spy = new Spy(Settings(autoUpdate: false), Status(updateAvailable: true));

        bool applied = await spy.AutoUpdateAsync();

        applied.Should().BeFalse();
        spy.InstallCalls.Should().Be(0, "the Steam-root DLL must not change without opt-in");
        spy.StatusCalls.Should().Be(0, "an opted-out user should not even trigger the GitHub lookup");
    }

    [Fact]
    public async Task Gate_on_with_an_update_available_installs_once_and_announces_it_first()
    {
        var spy = new Spy(Settings(autoUpdate: true), Status(updateAvailable: true));
        PluginStatus? announced = null;

        bool applied = await spy.AutoUpdateAsync(beforeInstall: s => announced = s);

        applied.Should().BeTrue();
        spy.InstallCalls.Should().Be(1);
        announced.Should().NotBeNull();
        announced!.UpdateAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task Gate_on_but_already_current_does_not_install()
    {
        var spy = new Spy(Settings(autoUpdate: true), Status(updateAvailable: false));
        bool announced = false;

        bool applied = await spy.AutoUpdateAsync(beforeInstall: _ => announced = true);

        applied.Should().BeFalse();
        spy.InstallCalls.Should().Be(0);
        announced.Should().BeFalse();
    }

    [Fact]
    public void The_steam_open_flow_does_not_install_the_plugin_by_itself()
    {
        // Guard against the original shape coming back: App must go through AutoUpdateAsync.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        string src = File.ReadAllText(Path.Combine(dir!.FullName, "src", "LuaToolsGui", "App.xaml.cs"));

        src.Should().Contain("AutoUpdateAsync(");
        src.Should().NotContain("installer.InstallAsync");
        src.Should().NotContain(".InstallAsync(progress: null)");
    }
}
