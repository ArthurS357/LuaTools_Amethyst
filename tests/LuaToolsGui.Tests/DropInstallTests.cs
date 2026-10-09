using System.IO;
using System.IO.Compression;
using System.Reflection;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// F-07. (a) A drop that lands while another batch is in flight — installing, or paused on the overwrite
/// confirm — must join that batch, not replace its queue and tally. (b) The lua the overwrite diff shows
/// and the lua <see cref="LuaInstaller.InstallZip"/> writes come from one selection, and only canonical
/// <c>&lt;appid&gt;[_&lt;build&gt;].lua</c> names count.
/// </summary>
public sealed class DropInstallTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly QueueHost _host = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));
    private readonly string _steamRoot;
    private readonly CacheService _cache;
    private readonly LuaInstaller _installer;

    public DropInstallTests()
    {
        _steamRoot = Path.Combine(_dir, "steam");
        Directory.CreateDirectory(Path.Combine(_steamRoot, "config", "stplug-in"));
        Directory.CreateDirectory(Path.Combine(_steamRoot, "depotcache"));

        var settings = new SettingsService(_dir) { SteamPathOverride = _steamRoot };
        _cache = new CacheService(Path.Combine(_dir, "cache"));
        var steam = new SteamService(settings);
        var vault = new LuaVault(() => steam.StPlugInDir, Path.Combine(_dir, "vault"));
        _installer = new LuaInstaller(steam, settings, _cache, vault);
    }

    public void Dispose()
    {
        _host.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string StPlugIn => Path.Combine(_steamRoot, "config", "stplug-in");

    private string Loose(string name, string content)
    {
        string path = Path.Combine(_dir, "in", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string MakeZip(params (string Name, string Content)[] entries)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(content);
        }
        return path;
    }

    private DropInstallViewModel NewVm() =>
        _host.On(() => new DropInstallViewModel(_installer, new SteamAppListCache(),
            new SteamAppInfoCache(_cache), new SteamDepotInfo()));

    /// <summary>Run on the VM's own dispatcher: nothing the VM awaits can resume until the lambda yields.</summary>
    private Task<T> OnUi<T>(Func<Task<T>> work) => _host.Dispatcher.InvokeAsync(work).Task.Unwrap().WaitAsync(Timeout);

    private static void SetField(object target, string name, object? value) =>
        typeof(DropInstallViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static object NewTally() =>
        Activator.CreateInstance(typeof(DropInstallViewModel)
            .GetField("_tally", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType)!;

    // ── F-07a: drop vs. an in-flight batch ──────────────────────────────────

    [Fact]
    public async Task A_drop_while_a_batch_is_installing_joins_it_instead_of_replacing_it()
    {
        var vm = NewVm();
        string a = Loose("730.lua", "addappid(730)\n");
        string b = Loose("440.lua", "addappid(440)\n");

        bool secondReturnedAtOnce = await OnUi(async () =>
        {
            var first = vm.HandleDropAsync([a]);   // parked on the off-UI install
            var second = vm.HandleDropAsync([b]);  // lands mid-batch
            bool joined = second.IsCompleted;
            await first;
            return joined;
        });

        secondReturnedAtOnce.Should().BeTrue("the second drop is appended to the running batch, not run as its own");
        File.Exists(Path.Combine(StPlugIn, "730.lua")).Should().BeTrue();
        File.Exists(Path.Combine(StPlugIn, "440.lua")).Should().BeTrue();
        _host.On(() => vm.ResultText).Should().Contain(string.Format(Resources.Strings.Drop_Count_Luas, 2),
            "both files are counted in one tally");
    }

    [Fact]
    public async Task A_drop_during_the_overwrite_confirm_waits_for_the_answer()
    {
        var vm = NewVm();
        string b = Loose("440.lua", "addappid(440)\n");

        // The state ConfirmIfOverwriteAsync leaves behind: batch open, overlay up. Reaching it for real needs
        // SteamAppListCache, which has no seam (AppData + network).
        _host.On(() =>
        {
            SetField(vm, "_tally", NewTally());
            SetField(vm, "_queue", new Queue<string>());
            vm.IsConfirming = true;
            return 0;
        });

        await OnUi(async () => { await vm.HandleDropAsync([b]); return 0; });

        File.Exists(Path.Combine(StPlugIn, "440.lua")).Should().BeFalse("nothing installs under an open confirm");
        _host.On(() => vm.IsConfirming).Should().BeTrue();

        await OnUi(async () => { await vm.CancelOverwriteCommand.ExecuteAsync(null); return 0; });

        File.Exists(Path.Combine(StPlugIn, "440.lua")).Should().BeTrue("the joined drop runs once the confirm resolves");
    }

    [Fact]
    public async Task A_drop_with_no_batch_in_flight_installs_normally()
    {
        var vm = NewVm();
        string a = Loose("990080_20773316.lua", "addappid(990080)\n");

        await OnUi(async () => { await vm.HandleDropAsync([a]); return 0; });

        File.Exists(Path.Combine(StPlugIn, "990080.lua")).Should().BeTrue();
        _host.On(() => vm.ResultFailed).Should().BeFalse();
    }

    // ── F-07b: one lua selection for the diff and the install ───────────────

    [Fact]
    public void A_zip_with_one_valid_lua_shows_and_installs_the_same_file()
    {
        string zip = MakeZip(("730.lua", "addappid(730)\naddappid(731)\n"), ("730_99.manifest", "BYTES"));

        var shown = LuaInstaller.ParseZipLua(zip, 730);
        var result = _installer.InstallZip(zip, 730);

        result.AnyFailed.Should().BeFalse();
        shown.Should().NotBeNull();
        shown.Should().BeEquivalentTo(LuaFileParser.Parse(Path.Combine(StPlugIn, "730.lua"), 730));
    }

    [Fact]
    public void A_zip_with_several_valid_luas_picks_the_highest_build_on_both_sides()
    {
        // Numeric, not ordinal: "730_99" sorts after "730_100" as text.
        string zip = MakeZip(
            ("730.lua", "addappid(730)\naddappid(731)\n"),
            ("730_100.lua", "addappid(730)\naddappid(732)\n"),
            ("730_99.lua", "addappid(730)\naddappid(733)\n"));

        var shown = LuaInstaller.ParseZipLua(zip, 730);
        var result = _installer.InstallZip(zip, 730);

        result.AnyFailed.Should().BeFalse();
        string installed = File.ReadAllText(Path.Combine(StPlugIn, "730.lua"));
        installed.Should().Contain("addappid(732)").And.NotContain("addappid(731)").And.NotContain("addappid(733)");
        shown.Should().BeEquivalentTo(LuaFileParser.Parse(Path.Combine(StPlugIn, "730.lua"), 730));
    }

    [Theory]
    [InlineData("game.lua")]
    [InlineData("480.lua")]          // canonical, but another game's
    [InlineData("730_abc.lua")]
    public void A_zip_with_no_valid_lua_for_the_app_fails_and_writes_nothing(string luaName)
    {
        string zip = MakeZip((luaName, "addappid(730)\n"));

        var result = _installer.InstallZip(zip, 730);

        result.AnyFailed.Should().BeTrue();
        result.Error.Should().NotBeNullOrEmpty();
        result.LuaInstalled.Should().BeFalse();
        Directory.GetFiles(StPlugIn).Should().BeEmpty();
        LuaInstaller.ParseZipLua(zip, 730).Should().BeNull("the diff must not show a lua that will not be installed");
    }

    [Theory]
    [InlineData("1234_567.lua", 1234L)]
    [InlineData("990080_20773316.lua", 990080L)]  // the real Hogwarts Legacy build lua
    [InlineData("386940.lua", 386940L)]
    [InlineData(@"C:\dl\480.LUA", 480L)]
    [InlineData("4294967295.lua", 4294967295L)]    // uint.MaxValue
    public void AppIdFromFileName_accepts_canonical_names(string name, long expected) =>
        LuaInstaller.AppIdFromFileName(name).Should().Be(expected);

    [Theory]
    [InlineData("1234_abc.lua")]
    [InlineData("abc_567.lua")]
    [InlineData("_567.lua")]
    [InlineData("1234_567.manifest")]
    [InlineData("3768760 (1).lua")]
    [InlineData(" 480.lua")]
    [InlineData("0.lua")]
    [InlineData("4294967296.lua")]                  // uint.MaxValue + 1
    [InlineData("١٢٣.lua")]                         // Arabic-Indic digits
    [InlineData("480.lua\n")]
    public void AppIdFromFileName_refuses_anything_else(string name) =>
        LuaInstaller.AppIdFromFileName(name).Should().BeNull();
}
