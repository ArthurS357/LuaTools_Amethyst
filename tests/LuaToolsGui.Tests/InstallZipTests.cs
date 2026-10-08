using System.IO;
using System.IO.Compression;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// <see cref="LuaInstaller.InstallZip"/> is the single choke point for every manifest zip the app installs
/// (Add page, store plugin, HTTP bridge, drag-and-drop, Fixes) and had no direct test. F-03: it wrote
/// <c>Path.Combine(depotcache, entry.Name)</c>, and a name with a drive letter makes Path.Combine return
/// the second argument, so a "D:x.manifest" entry escaped depotcache and the result still reported success.
/// </summary>
public sealed class InstallZipTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    private readonly string _steamRoot;
    private readonly LuaInstaller _installer;
    private readonly List<string> _possibleLeaks = [];

    public InstallZipTests()
    {
        _steamRoot = Path.Combine(_dir, "steam");
        Directory.CreateDirectory(Path.Combine(_steamRoot, "config", "stplug-in"));
        Directory.CreateDirectory(Path.Combine(_steamRoot, "depotcache"));

        var settings = new SettingsService(_dir) { SteamPathOverride = _steamRoot };
        var cache = new CacheService(Path.Combine(_dir, "cache"));
        var steam = new SteamService(settings);
        var vault = new LuaVault(() => steam.StPlugInDir, Path.Combine(_dir, "vault"));
        _installer = new LuaInstaller(steam, settings, cache, vault);
    }

    public void Dispose()
    {
        foreach (string leak in _possibleLeaks)
            try { File.Delete(leak); } catch (IOException) { /* best effort */ }
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string DepotCache => Path.Combine(_steamRoot, "depotcache");
    private string StPlugIn => Path.Combine(_steamRoot, "config", "stplug-in");

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

    private string[] DepotCacheFiles() =>
        Directory.GetFiles(DepotCache).Select(Path.GetFileName).OfType<string>().ToArray();

    // ── Malicious: names that are not <depot>_<gid>.manifest ────────────────

    [Fact]
    public void A_drive_qualified_manifest_name_is_refused_and_does_not_escape_depotcache()
    {
        // "<temp drive>:name" is drive-relative: Path.Combine returns it as-is and it resolves against the
        // process's current directory on that drive. Unique name, cleaned up in Dispose if it ever lands.
        string name = $"{Path.GetPathRoot(_dir)![0]}:zs_{Guid.NewGuid():N}.manifest";
        string wouldLandAt = Path.GetFullPath(name);
        _possibleLeaks.Add(wouldLandAt);

        var result = _installer.InstallZip(MakeZip((name, "PROBE")), 730);

        File.Exists(wouldLandAt).Should().BeFalse("the entry must not be written outside depotcache");
        DepotCacheFiles().Should().BeEmpty();
        result.AnyFailed.Should().BeTrue("the refusal must be reported, not swallowed as success");
        result.Error.Should().NotBeNullOrEmpty();
        result.ManifestCount.Should().Be(0);
    }

    [Theory]
    [InlineData("x.manifest")]                // not <depot>_<gid>
    [InlineData("1_2.manifest.manifest")]
    [InlineData("1_2_3.manifest")]
    [InlineData("_2.manifest")]
    [InlineData("1_.manifest")]
    [InlineData("١_٢.manifest")]              // Arabic-Indic digits: \d would accept these
    public void A_name_outside_the_depot_gid_shape_is_refused(string name)
    {
        var result = _installer.InstallZip(MakeZip((name, "PROBE")), 730);

        DepotCacheFiles().Should().BeEmpty();
        result.AnyFailed.Should().BeTrue();
        result.ManifestCount.Should().Be(0);
    }

    [Fact]
    public void An_entry_that_does_not_end_in_manifest_is_never_written()
    {
        // Not a manifest entry at all, so it is ignored like any other file, not extracted anywhere.
        var result = _installer.InstallZip(MakeZip(("1_2.manifest ", "X"), ("readme.txt", "X")), 730);

        DepotCacheFiles().Should().BeEmpty();
        result.ManifestCount.Should().Be(0);
    }

    [Fact]
    public void A_traversal_name_is_refused_whatever_the_runtime_does_with_the_separators()
    {
        // .NET 10 normalises ..\ so Name is just the leaf; the regex rejects it either way.
        string leaf = $"zs_{Guid.NewGuid():N}.manifest";
        _possibleLeaks.Add(Path.GetFullPath(Path.Combine(DepotCache, "..", leaf)));

        var result = _installer.InstallZip(MakeZip(($@"..\..\{leaf}", "PROBE")), 730);

        File.Exists(Path.GetFullPath(Path.Combine(DepotCache, "..", leaf))).Should().BeFalse();
        DepotCacheFiles().Should().BeEmpty();
        result.AnyFailed.Should().BeTrue();
    }

    // ── Legitimate: must not be blocked (false-positive guard) ──────────────

    [Fact]
    public void A_real_depot_gid_manifest_is_written_to_depotcache()
    {
        var result = _installer.InstallZip(MakeZip(("730_1234567890123456789.manifest", "BYTES")), 730);

        result.AnyFailed.Should().BeFalse();
        result.Error.Should().BeNull();
        result.ManifestCount.Should().Be(1);
        DepotCacheFiles().Should().Equal("730_1234567890123456789.manifest");
        File.ReadAllText(Path.Combine(DepotCache, "730_1234567890123456789.manifest")).Should().Be("BYTES");
    }

    [Fact]
    public void A_real_manifest_inside_a_subfolder_of_the_zip_is_still_installed_flat()
    {
        // Generators nest files in a folder; entry.Name is the leaf, so this stays legitimate.
        var result = _installer.InstallZip(MakeZip(("pack/depots/731_42.manifest", "BYTES")), 730);

        result.AnyFailed.Should().BeFalse();
        DepotCacheFiles().Should().Equal("731_42.manifest");
    }

    [Fact]
    public void The_lua_in_the_same_zip_is_still_installed()
    {
        var result = _installer.InstallZip(
            MakeZip(("730.lua", "addappid(730)\n"), ("730_99.manifest", "BYTES")), 730);

        result.AnyFailed.Should().BeFalse();
        result.LuaInstalled.Should().BeTrue();
        File.Exists(Path.Combine(StPlugIn, "730.lua")).Should().BeTrue();
        DepotCacheFiles().Should().Equal("730_99.manifest");
    }

    // ── Regression: one bad entry must not poison the good ones ─────────────

    [Fact]
    public void A_bad_manifest_name_is_skipped_but_the_good_entries_around_it_are_installed()
    {
        var result = _installer.InstallZip(
            MakeZip(("730.lua", "addappid(730)\n"), ("evil.manifest", "X"), ("730_99.manifest", "BYTES")), 730);

        result.LuaInstalled.Should().BeTrue();
        result.ManifestCount.Should().Be(1);
        result.Failed.Should().Contain("evil.manifest");
        result.Error.Should().NotBeNullOrEmpty("a partial refusal still has to be visible to the user");
        DepotCacheFiles().Should().Equal("730_99.manifest");
    }

    [Fact]
    public void A_successful_install_leaves_no_temp_files_in_steams_folders()
    {
        _installer.InstallZip(MakeZip(("730.lua", "addappid(730)\n"), ("730_99.manifest", "BYTES")), 730);

        Directory.GetFiles(StPlugIn).Select(Path.GetFileName).Should().Equal("730.lua");
        DepotCacheFiles().Should().Equal("730_99.manifest");
    }

    [Fact]
    public void A_lua_installed_over_an_existing_one_replaces_it_whole()
    {
        File.WriteAllText(Path.Combine(StPlugIn, "730.lua"), new string('#', 5000));

        _installer.InstallZip(MakeZip(("730.lua", "addappid(730)\n")), 730);

        File.ReadAllText(Path.Combine(StPlugIn, "730.lua")).Should().Contain("addappid(730)")
            .And.NotContain("#####");
    }

    [Fact]
    public void An_existing_manifest_is_kept_not_overwritten()
    {
        // Content-addressed names: an existing file is skipped (and counted), as before.
        string existing = Path.Combine(DepotCache, "730_99.manifest");
        File.WriteAllText(existing, "ORIGINAL");

        var result = _installer.InstallZip(MakeZip(("730_99.manifest", "NEW")), 730);

        result.AnyFailed.Should().BeFalse();
        result.ManifestCount.Should().Be(1);
        File.ReadAllText(existing).Should().Be("ORIGINAL");
    }
}
