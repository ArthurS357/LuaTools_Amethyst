using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// <see cref="LuaInstaller.InstallManifestFile"/> is the drag-and-drop path for a loose manifest. F-03 only
/// hardened the zip path, so a dropped "1234.manifest" (no gid — Steam never reads it) or a browser duplicate
/// "123_456 (1).manifest" was copied into depotcache as a dead file. It now applies the same
/// <c>ManifestNameRegex</c> as <see cref="LuaInstaller.InstallZip"/>: refuse, never rename.
/// </summary>
public sealed class LuaInstallerManifestFileTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    private readonly string _steamRoot;
    private readonly string _dropDir;
    private readonly LuaInstaller _installer;

    public LuaInstallerManifestFileTests()
    {
        _steamRoot = Path.Combine(_dir, "steam");
        _dropDir = Path.Combine(_dir, "downloads");
        Directory.CreateDirectory(Path.Combine(_steamRoot, "config", "stplug-in"));
        Directory.CreateDirectory(Path.Combine(_steamRoot, "depotcache"));
        Directory.CreateDirectory(_dropDir);

        var settings = new SettingsService(_dir) { SteamPathOverride = _steamRoot };
        var cache = new CacheService(Path.Combine(_dir, "cache"));
        var steam = new SteamService(settings);
        var vault = new LuaVault(() => steam.StPlugInDir, Path.Combine(_dir, "vault"));
        _installer = new LuaInstaller(steam, settings, cache, vault);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string DepotCache => Path.Combine(_steamRoot, "depotcache");

    private string Drop(string name)
    {
        string path = Path.Combine(_dropDir, name);
        File.WriteAllText(path, "BYTES");
        return path;
    }

    // ── Legitimate: installs ────────────────────────────────────────────────

    [Theory]
    [InlineData("1234_567.manifest")]
    [InlineData("990080_20773316.manifest")] // the real Hogwarts Legacy depot manifest
    public void A_canonical_manifest_is_installed_under_its_own_name(string name)
    {
        var result = _installer.InstallManifestFile(Drop(name));

        result.Error.Should().BeNull();
        result.ManifestCount.Should().Be(1);
        File.Exists(Path.Combine(DepotCache, name)).Should().BeTrue();
    }

    // ── Malicious / wrong: refused with a readable error, nothing written ───

    [Theory]
    [InlineData("1234.manifest")]            // no gid: Steam never reads it
    [InlineData("123_456 (1).manifest")]     // browser duplicate (false-positive-looking but a different file)
    [InlineData("1234_abc.manifest")]
    [InlineData("_567.manifest")]
    [InlineData("1234_567.manifest.txt")]
    [InlineData("١٢٣_٤٥٦.manifest")]         // Arabic-Indic digits
    public void A_manifest_not_named_depot_gid_is_refused_and_nothing_lands_in_depotcache(string name)
    {
        var result = _installer.InstallManifestFile(Drop(name));

        result.Error.Should().NotBeNullOrWhiteSpace().And.Contain(name);
        result.ManifestCount.Should().Be(0);
        Directory.GetFiles(DepotCache).Should().BeEmpty();
    }

    [Fact]
    public void A_traversal_looking_path_is_refused_by_name_not_trusted_to_GetFileName()
    {
        string path = Path.Combine(_dropDir, "..", "etc", "passwd");

        var result = _installer.InstallManifestFile(path);

        result.Error.Should().NotBeNullOrWhiteSpace();
        Directory.GetFiles(DepotCache).Should().BeEmpty();
    }

    // ── Regression: a refused drop does not poison the next legitimate one ──

    [Fact]
    public void A_refused_drop_does_not_block_a_later_valid_one()
    {
        _installer.InstallManifestFile(Drop("1234.manifest")).Error.Should().NotBeNull();

        var ok = _installer.InstallManifestFile(Drop("1234_567.manifest"));

        ok.Error.Should().BeNull();
        Directory.GetFiles(DepotCache).Select(Path.GetFileName).Should().Equal("1234_567.manifest");
    }
}
