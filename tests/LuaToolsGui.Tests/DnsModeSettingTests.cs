using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>The Settings > Network picker: starts on the saved mode, and picking one saves it.</summary>
public class DnsModeSettingTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    public DnsModeSettingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static SettingsViewModel Vm(SettingsService store) =>
        new(store, new AuthService(), new SteamService(store), new HubcapService());

    [Fact]
    public void Starts_on_Auto_and_does_not_write_settings_on_construction()
    {
        var store = new SettingsService(_dir);

        var vm = Vm(store);

        vm.SelectedDnsMode.Mode.Should().Be(DnsMode.Auto);
        File.Exists(Path.Combine(_dir, "settings.json")).Should().BeFalse("init must not save");
    }

    [Theory]
    [InlineData(DnsMode.Always)]
    [InlineData(DnsMode.Never)]
    public void Picking_a_mode_persists_it_and_survives_reload(DnsMode mode)
    {
        var store = new SettingsService(_dir);
        var vm = Vm(store);

        vm.SelectedDnsMode = vm.DnsModeOptions.First(o => o.Mode == mode);

        store.DnsMode.Should().Be(mode);
        new SettingsService(_dir).DnsMode.Should().Be(mode);
        Vm(new SettingsService(_dir)).SelectedDnsMode.Mode.Should().Be(mode);
    }
}
