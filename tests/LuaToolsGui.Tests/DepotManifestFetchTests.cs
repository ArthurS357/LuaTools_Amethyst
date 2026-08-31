using System.IO;
using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Pins the screening a fetched depot manifest goes through before it is allowed into
/// <c>config\depotcache</c>.
///
/// <para>
/// This is the fail-closed gate on a network response. Order is the whole property: a zip wrapper is
/// unwrapped to a name this code COMPUTES (never the archive entry's, so no zip-slip), the bytes are proved
/// to be a Steam manifest, and only then is anything written. A bad file that reaches depotcache is sticky —
/// <see cref="LuaInstaller.InstallManifestFile"/> skips an existing destination, so every later run would
/// resolve the bad copy locally and fail identically with no way back short of deleting it by hand.
/// </para>
/// </summary>
public class DepotManifestFetchTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    private readonly string _steamRoot;
    private readonly string _depotCache;
    private readonly string _staging;

    public DepotManifestFetchTests()
    {
        _steamRoot = Path.Combine(_dir, "steam");
        _depotCache = Path.Combine(_steamRoot, "config", "depotcache");
        _staging = Path.Combine(_dir, "staging");
        Directory.CreateDirectory(_depotCache);
        Directory.CreateDirectory(Path.Combine(_steamRoot, "config", "stplug-in"));
        Directory.CreateDirectory(_staging);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>The service wired to an isolated Steam root. The API client is never reached from here —
    /// <see cref="DepotDownloaderService.InstallFetchedManifest"/> is the network-free half by design.</summary>
    private DepotDownloaderService NewService()
    {
        var settings = new SettingsService(_dir) { SteamPathOverride = _steamRoot };
        var cache = new CacheService(Path.Combine(_dir, "cache"));
        var steam = new SteamService(settings);
        var vault = new LuaVault(() => steam.StPlugInDir, Path.Combine(_dir, "vault"));
        var installer = new LuaInstaller(steam, settings, cache, vault);

        return new DepotDownloaderService(new GithubProxy(), steam, new AuthService(), cache, null!, installer);
    }

    private string Stage(string name, byte[] bytes)
    {
        string path = Path.Combine(_staging, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string CachedPath(long depotId, string gid) =>
        Path.Combine(_depotCache, $"{depotId}_{gid}.manifest");

    // ── The happy paths ───────────────────────────────────────────────────────

    [Fact]
    public void Installs_raw_manifest_bytes_under_the_computed_name()
    {
        // The staged name is a fallback and is deliberately NOT what gets installed: what reaches depotcache
        // is always <depot>_<manifest>.manifest, recomputed here.
        string staged = Stage("whatever-the-server-called-it.bin", TestManifests.Build(depotId: 1001, gid: 555));

        var (path, failure) = NewService().InstallFetchedManifest(1001, "555", staged);

        failure.Should().Be(DepotFailure.None);
        path.Should().Be(CachedPath(1001, "555"));
        File.Exists(CachedPath(1001, "555")).Should().BeTrue();
    }

    [Fact]
    public void Unwraps_a_zip_carrying_a_single_manifest_entry()
    {
        // The API is expected to serve raw bytes but has been observed wrapping them in a zip with one entry
        // named "z". Writing the WRAPPER into depotcache yields a file SteamKit rejects with
        // "Unrecognized magic value 4034B50" — and it is sticky once written.
        string staged = Stage("2002_42.manifest",
            TestManifests.Zipped(TestManifests.Build(depotId: 2002, gid: 42)));

        var (path, failure) = NewService().InstallFetchedManifest(2002, "42", staged);

        failure.Should().Be(DepotFailure.None);
        path.Should().Be(CachedPath(2002, "42"));

        // What landed is the INNER manifest, not the zip.
        ManifestFile.TryRead(CachedPath(2002, "42"))!.Value.DepotId.Should().Be(2002);
    }

    [Fact]
    public void The_installed_file_is_the_one_the_download_is_then_handed()
    {
        string staged = Stage("m", TestManifests.Build(depotId: 1001, gid: 555));
        var service = NewService();

        service.InstallFetchedManifest(1001, "555", staged);

        // The round trip that matters: what was installed must satisfy the same content-addressed check the
        // resolver applies before every run.
        service.ResolveManifestPath(1001, "555").Should().Be(CachedPath(1001, "555"));
    }

    // ── Nothing that fails a check may reach depotcache ───────────────────────

    [Fact]
    public void A_response_that_is_not_a_manifest_is_refused_and_nothing_is_written()
    {
        // The realistic shape: an error page, a JSON body, an HTML redirect notice.
        string staged = Stage("1001_555.manifest",
            Encoding.UTF8.GetBytes("<!DOCTYPE html><h1>502 Bad Gateway</h1>"));

        var (path, failure) = NewService().InstallFetchedManifest(1001, "555", staged);

        failure.Should().Be(DepotFailure.NoManifest);
        path.Should().BeNull();
        Directory.EnumerateFiles(_depotCache).Should().BeEmpty();
    }

    [Fact]
    public void A_zip_whose_contents_are_not_a_manifest_is_refused_and_nothing_is_written()
    {
        // A zip wrapper alone must not be enough to get bytes past the gate.
        string staged = Stage("1001_555.manifest",
            TestManifests.Zipped(Encoding.UTF8.GetBytes("not a manifest at all")));

        var (path, failure) = NewService().InstallFetchedManifest(1001, "555", staged);

        failure.Should().Be(DepotFailure.NoManifest);
        path.Should().BeNull();
        Directory.EnumerateFiles(_depotCache).Should().BeEmpty();
    }

    [Fact]
    public void An_empty_zip_is_refused()
    {
        using var buf = new MemoryStream();
        using (var _ = new ZipArchive(buf, ZipArchiveMode.Create, leaveOpen: true)) { }
        string staged = Stage("1001_555.manifest", buf.ToArray());

        NewService().InstallFetchedManifest(1001, "555", staged).Failure.Should().Be(DepotFailure.NoManifest);
        Directory.EnumerateFiles(_depotCache).Should().BeEmpty();
    }

    [Fact]
    public void A_manifest_for_a_different_depot_is_refused_after_the_write_check()
    {
        // It parses as a manifest, so IsSteamManifest passes — but its own depot id disagrees with the name
        // it was fetched under, and the re-resolve at the end catches that.
        string staged = Stage("m", TestManifests.Build(depotId: 9999, gid: 555));

        NewService().InstallFetchedManifest(1001, "555", staged).Failure.Should().Be(DepotFailure.NoManifest);
    }

    [Fact]
    public void A_truncated_manifest_is_refused()
    {
        byte[] full = TestManifests.Build(depotId: 1001, gid: 555);
        string staged = Stage("m", full[..6]);

        NewService().InstallFetchedManifest(1001, "555", staged).Failure.Should().Be(DepotFailure.NoManifest);
        Directory.EnumerateFiles(_depotCache).Should().BeEmpty();
    }

    // ── Zip-slip ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_zip_entry_naming_a_traversal_path_cannot_choose_where_the_file_lands()
    {
        // The entry name is IGNORED: extraction targets a path built from a long and a digits-only gid. If
        // the entry name were honoured, this archive would write outside the unzip directory.
        using var buf = new MemoryStream();
        using (var zip = new ZipArchive(buf, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry(@"..\..\..\evil.manifest").Open();
            entry.Write(TestManifests.Build(depotId: 1001, gid: 555));
        }
        string staged = Stage("1001_555.manifest", buf.ToArray());

        var (path, failure) = NewService().InstallFetchedManifest(1001, "555", staged);

        failure.Should().Be(DepotFailure.None);
        path.Should().Be(CachedPath(1001, "555"));
        File.Exists(Path.Combine(_dir, "evil.manifest")).Should().BeFalse();
        File.Exists(Path.Combine(_staging, "evil.manifest")).Should().BeFalse();
    }

    // ── Housekeeping ──────────────────────────────────────────────────────────

    [Fact]
    public void The_staged_download_is_deleted_whether_it_was_accepted_or_refused()
    {
        string good = Stage("good", TestManifests.Build(depotId: 1001, gid: 555));
        string bad = Stage("bad", Encoding.UTF8.GetBytes("nope"));
        var service = NewService();

        service.InstallFetchedManifest(1001, "555", good);
        service.InstallFetchedManifest(2002, "42", bad);

        File.Exists(good).Should().BeFalse();
        File.Exists(bad).Should().BeFalse();
    }

    [Fact]
    public void The_temporary_unzip_directory_does_not_survive()
    {
        string staged = Stage("z", TestManifests.Zipped(TestManifests.Build(depotId: 1001, gid: 555)));

        NewService().InstallFetchedManifest(1001, "555", staged);

        Directory.EnumerateDirectories(_staging).Should().BeEmpty();
    }

    [Fact]
    public void An_existing_good_manifest_is_left_alone_rather_than_overwritten()
    {
        // Content-addressed name: identical bytes if it already exists. Steam may hold the file open, so
        // skipping is both safe and correct.
        string existing = CachedPath(1001, "555");
        File.WriteAllBytes(existing, TestManifests.Build(depotId: 1001, gid: 555));
        var stamp = File.GetLastWriteTimeUtc(existing);

        string staged = Stage("m", TestManifests.Build(depotId: 1001, gid: 555));
        var (path, failure) = NewService().InstallFetchedManifest(1001, "555", staged);

        failure.Should().Be(DepotFailure.None);
        path.Should().Be(existing);
        File.GetLastWriteTimeUtc(existing).Should().Be(stamp);
    }
}
