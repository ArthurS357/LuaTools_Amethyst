using System.IO;
using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Services.AppInfo;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Engine v1.5.1 integration: the new <c>[manifest] failover</c> / <c>[shadow] record</c> keys, the
/// "clear content cache" rescue (appinfo.vdf), and the <c>tools\</c> CLIs the release archive now carries.
/// Steam process control is stubbed: a unit test must never close the developer's Steam.
/// </summary>
public class AmethystEngine151Tests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    public AmethystEngine151Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ── TOML ──────────────────────────────────────────────────────────────────

    private const string PreV151 =
        "[manifest]\n" +
        "url = \"wudrm\"\n" +
        "\n" +
        "[compatibility]\n" +
        "denuvo_aliases = false\n";

    private const string V151 =
        "[manifest]\n" +
        "url = \"wudrm\"\n" +
        "failover = false # off by hand\n" +
        "\n" +
        "[compatibility]\n" +
        "denuvo_aliases = false\n" +
        "\n" +
        "[shadow]\n" +
        "record = true\n";

    [Fact]
    public void A_config_from_before_v151_reads_the_engine_defaults()
    {
        AmethystToolConfig.ReadManifestFailover(PreV151).Should().BeTrue();
        AmethystToolConfig.ReadShadowRecord(PreV151).Should().BeFalse();
        AmethystToolConfig.ReadManifestFailover("").Should().BeTrue();
    }

    [Fact]
    public void Explicit_values_are_read_including_a_trailing_comment()
    {
        AmethystToolConfig.ReadManifestFailover(V151).Should().BeFalse();
        AmethystToolConfig.ReadShadowRecord(V151).Should().BeTrue();
    }

    [Theory]
    [InlineData("[manifest]\nfailover = \"yes\"\n")]          // not a boolean
    [InlineData("[manifest.extra]\nfailover = false\n")]     // a sub-table, not [manifest]
    [InlineData("[other]\nfailover = false\n")]              // another table's key
    [InlineData("[manifest]\nfailover_budget = false\n")]    // a longer key with the same prefix
    [InlineData("x = \"\"\"\n[manifest]\nfailover = false\n\"\"\"\n")] // inside a multi-line string
    public void Anything_but_a_real_manifest_failover_reads_as_the_default(string toml) =>
        AmethystToolConfig.ReadManifestFailover(toml).Should().BeTrue();

    [Fact]
    public void A_dotted_root_key_is_read() =>
        AmethystToolConfig.ReadManifestFailover("manifest.failover = false\n[log]\n").Should().BeFalse();

    [Fact]
    public void Toggling_denuvo_mode_leaves_the_new_sections_byte_for_byte()
    {
        string updated = AmethystToolConfig.SetDenuvoAliases(V151, true);

        updated.Should().Be(V151.Replace("denuvo_aliases = false", "denuvo_aliases = true"));
        AmethystToolConfig.ReadManifestFailover(updated).Should().BeFalse();
        AmethystToolConfig.ReadShadowRecord(updated).Should().BeTrue();
    }

    // ── Clear content cache (appinfo.vdf) ─────────────────────────────────────

    private int _stops, _starts;
    private bool _running;
    private bool _stopCloses = true; // false: a Steam that ignores the stop

    private (LaunchOptionsService Service, string AppInfo, string BackupDir) ClearSetup(byte[] content)
    {
        string appInfo = Path.Combine(_dir, "appcache", "appinfo.vdf");
        string backupDir = Path.Combine(_dir, "appcache", "luatools-backup");
        Directory.CreateDirectory(Path.GetDirectoryName(appInfo)!);
        File.WriteAllBytes(appInfo, content);
        var service = new LaunchOptionsService(() => appInfo, () => _running,
            () => { _stops++; if (_stopCloses) _running = false; }, () => _starts++, new LaunchModStore(Path.Combine(_dir, "launchmods.json")), backupDir);
        return (service, appInfo, backupDir);
    }

    [Fact]
    public void Clearing_appinfo_keeps_its_exact_bytes_in_the_backup_folder()
    {
        byte[] original = Encoding.UTF8.GetBytes("appinfo-bytes");
        var (service, appInfo, backupDir) = ClearSetup(original);

        var result = service.ClearAppInfo();

        result.Ok.Should().BeTrue();
        File.Exists(appInfo).Should().BeFalse();
        var backups = Directory.GetFiles(backupDir, "appinfo-*.vdf");
        backups.Should().ContainSingle();
        File.ReadAllBytes(backups[0]).Should().Equal(original);
    }

    [Fact]
    public void A_running_steam_is_closed_first_and_restarted_after()
    {
        var (service, _, _) = ClearSetup([1, 2, 3]);
        _running = true;

        var result = service.ClearAppInfo();

        result.Ok.Should().BeTrue();
        result.SteamWasRunning.Should().BeTrue();
        _stops.Should().Be(1);
        _starts.Should().Be(1);
    }

    [Fact]
    public void A_steam_that_will_not_close_leaves_appinfo_untouched()
    {
        byte[] original = [9, 8, 7];
        var (service, appInfo, backupDir) = ClearSetup(original);
        _running = true;
        _stopCloses = false;

        var result = service.ClearAppInfo();

        result.Ok.Should().BeFalse();
        _stops.Should().Be(1);
        _starts.Should().Be(0);
        File.ReadAllBytes(appInfo).Should().Equal(original);
        Directory.Exists(backupDir).Should().BeFalse();
    }

    [Fact]
    public void A_locked_appinfo_is_a_reported_failure_with_the_file_intact()
    {
        byte[] original = [4, 5, 6];
        var (service, appInfo, _) = ClearSetup(original);

        ApplyResult result;
        using (File.Open(appInfo, FileMode.Open, FileAccess.Read, FileShare.None))
            result = service.ClearAppInfo();

        result.Ok.Should().BeFalse();
        File.ReadAllBytes(appInfo).Should().Equal(original);
    }

    [Fact]
    public void Clearing_twice_keeps_one_backup_and_never_evicts_it_for_a_stray_file()
    {
        var (service, appInfo, backupDir) = ClearSetup([1]);
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, "appinfo-old.vdf"), "stray");
        File.WriteAllText(Path.Combine(backupDir, "appinfo-20200101-000000.vdf"), "ancient");

        service.ClearAppInfo().Ok.Should().BeTrue();
        File.WriteAllBytes(appInfo, [2]);
        service.ClearAppInfo().Ok.Should().BeTrue();

        var names = Directory.GetFiles(backupDir).Select(Path.GetFileName).ToList();
        names.Should().Contain("appinfo-old.vdf");                     // no stamp: never pruned
        names.Should().NotContain("appinfo-20200101-000000.vdf");      // older stamped backup: pruned
        names.Where(n => LaunchOptionsService.BackupStamp(n!) is not null).Should().ContainSingle();
        File.ReadAllBytes(Directory.GetFiles(backupDir, "appinfo-2*.vdf").Single()).Should().Equal([2]);
    }

    [Fact]
    public void A_later_release_without_a_cli_does_not_leave_the_old_copy()
    {
        string toolsDir = Path.Combine(_dir, "state", "tools");
        Directory.CreateDirectory(toolsDir);
        File.WriteAllText(Path.Combine(toolsDir, "version_pin.exe"), "stale");
        string staged = Path.Combine(_dir, "no-tools");
        Directory.CreateDirectory(staged);

        AmethystToolService.InstallTools(staged, toolsDir);

        Directory.Exists(toolsDir).Should().BeFalse();
    }

    [Fact]
    public void No_appinfo_is_a_reported_failure_not_a_crash()
    {
        var (service, appInfo, _) = ClearSetup([0]);
        File.Delete(appInfo);

        service.ClearAppInfo().Ok.Should().BeFalse();
        _stops.Should().Be(0);
    }

    // ── tools\ CLIs from the release archive ──────────────────────────────────

    /// <summary>The v1.5.1 archive's real shape: payload at the root, CLIs under <c>tools\</c> written with
    /// BACKSLASH separators, plus docs that must never be installed.</summary>
    private string Release(string prefix = "")
    {
        string zip = Path.Combine(_dir, "AmethystTool-v1.5.1.zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (string name in new[]
                 {
                     "AmethystTool.dll", "amethysttool.toml", "dwmapi.dll", "xinput1_4.dll", "README.md",
                     @"tools\version_pin.exe", @"tools\pattern_migrate.exe", @"tools\unexpected.exe",
                 })
        {
            using var w = new StreamWriter(archive.CreateEntry(prefix + name).Open());
            w.Write("bytes of " + name);
        }
        return zip;
    }

    private string Extract(string zip)
    {
        string staged = Path.Combine(_dir, "staged");
        Directory.CreateDirectory(staged);
        ZipFile.ExtractToDirectory(zip, staged);
        AmethystToolService.FlattenStagedLayout(staged);
        return staged;
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"AmethystTool-v1.5.1\")] // a wrapping folder: the unwrap must bring tools\ up too
    public void The_allow_listed_clis_survive_extraction_and_land_in_the_tools_folder(string prefix)
    {
        string staged = Extract(Release(prefix));
        string toolsDir = Path.Combine(_dir, "state", "tools");

        var copied = AmethystToolService.InstallTools(staged, toolsDir);

        copied.Should().BeEquivalentTo("version_pin.exe", "pattern_migrate.exe");
        Directory.GetFiles(toolsDir).Select(Path.GetFileName)
            .Should().BeEquivalentTo("version_pin.exe", "pattern_migrate.exe");
        File.ReadAllText(Path.Combine(toolsDir, "version_pin.exe")).Should().Be(@"bytes of tools\version_pin.exe");
    }

    [Fact]
    public void The_payload_plan_is_unchanged_by_the_tools_folder()
    {
        string staged = Extract(Release());

        var plan = AmethystToolPlan.Create(Path.Combine(_dir, "steam"), staged,
            Directory.EnumerateFiles(staged, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).OfType<string>(),
            new HashSet<string>(), DateTimeOffset.Now);

        plan.Rejected.Should().BeFalse();
        plan.Steps.Select(s => s.FileName).Should().BeEquivalentTo(AmethystToolPlan.PayloadFiles);
    }

    [Fact]
    public void A_release_without_tools_installs_no_clis_and_does_not_throw()
    {
        string staged = Path.Combine(_dir, "old-release");
        Directory.CreateDirectory(staged);
        string toolsDir = Path.Combine(_dir, "state", "tools");

        AmethystToolService.InstallTools(staged, toolsDir).Should().BeEmpty();
        Directory.Exists(toolsDir).Should().BeFalse();
    }
}
