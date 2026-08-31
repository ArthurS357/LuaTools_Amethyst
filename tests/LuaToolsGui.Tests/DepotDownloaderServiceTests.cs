using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using AwesomeAssertions;
using LuaToolsGui;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Pins the parts of <see cref="DepotDownloaderService"/> that can go wrong quietly: the containment check
/// that stops a hostile line in a child process's stdout from deleting files outside the download folder,
/// the depot-keys file's scope and lifetime, and the content-addressed manifest lookup that must not trust
/// a file just because it exists under the right name.
/// </summary>
public class DepotDownloaderServiceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    public DepotDownloaderServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        DepotDownloaderService.SweepKeyFiles();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A service wired to an isolated Steam root.
    /// </summary>
    /// <remarks>
    /// <c>api</c> is deliberately null: every test here exercises local disk only (key resolution, manifest
    /// lookup, cleanup). The one member that touches it — the manifest fetch — needs a signed-in session and
    /// the network, and is covered by <see cref="DepotManifestFetchTests"/> at the level it can be.
    /// </remarks>
    private DepotDownloaderService NewService(string steamRoot)
    {
        var settings = new SettingsService(_dir) { SteamPathOverride = steamRoot };
        var cache = new CacheService(Path.Combine(_dir, "cache"));
        var steam = new SteamService(settings);
        var vault = new LuaVault(() => steam.StPlugInDir, Path.Combine(_dir, "vault"));
        var installer = new LuaInstaller(steam, settings, cache, vault);

        return new DepotDownloaderService(new GithubProxy(), steam, new AuthService(), cache, null!, installer);
    }

    private string NewSteamRoot()
    {
        string root = Path.Combine(_dir, "steam");
        Directory.CreateDirectory(Path.Combine(root, "config", "stplug-in"));
        Directory.CreateDirectory(Path.Combine(root, "config", "depotcache"));
        return root;
    }

    // ── The pin is the whole trust anchor ─────────────────────────────────────

    [Fact]
    public void The_shipped_pin_is_usable()
    {
        // If this fails the depot downloader is disabled in this build — which is the SAFE outcome, but it
        // has to be a deliberate one, so the test says so out loud.
        DepotDownloaderService.PinIsUsable.Should().BeTrue();
    }

    [Fact]
    public void The_pinned_digest_is_a_well_formed_sha256() =>
        AssetIntegrity.ParseDigest(AppConfig.DepotDownloaderPinnedSha256).Should().NotBeNull();

    [Fact]
    public void The_pinned_asset_name_and_tag_are_both_set()
    {
        AppConfig.DepotDownloaderPinnedAssetName.Should().NotBeNullOrWhiteSpace();
        AppConfig.DepotDownloaderPinnedTag.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_pinned_repo_is_the_one_the_downloads_are_scoped_to()
    {
        // GithubProxy.DownloadAssetAsync refuses a URL that isn't this owner/repo's own asset, so the three
        // constants have to agree or the refusal fires on every legitimate download.
        AppConfig.DepotDownloaderRepo.Should()
            .Be($"{AppConfig.DepotDownloaderOwner}/{AppConfig.DepotDownloaderRepoName}");
    }

    // ── Filename construction cannot be steered ───────────────────────────────

    [Theory]
    [InlineData(1001L, "555", "1001_555.manifest")]
    [InlineData(228990L, "7777777777", "228990_7777777777.manifest")]
    public void The_depotcache_filename_is_computed_from_typed_values(long depotId, string gid, string expected) =>
        DepotDownloaderService.ManifestFileName(depotId, gid).Should().Be(expected);

    [Theory]
    [InlineData("123456")]
    [InlineData("1")]
    public void A_digits_only_manifest_id_is_accepted(string id) =>
        DepotDownloaderService.IsValidManifestId(id).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../../evil")]
    [InlineData(@"..\..\evil")]
    [InlineData("12/34")]
    [InlineData("12.34")]
    [InlineData("abc")]
    [InlineData("999999999999999999999999")] // longer than any gid
    public void A_manifest_id_that_could_steer_a_path_is_refused(string? id) =>
        DepotDownloaderService.IsValidManifestId(id).Should().BeFalse();

    // ── TryDeleteCreatedFiles: containment ────────────────────────────────────

    [Fact]
    public void Deletes_only_the_files_the_run_reported_creating()
    {
        string root = Path.Combine(_dir, "out");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "created.pak"), "new");
        File.WriteAllText(Path.Combine(root, "preexisting.pak"), "theirs");

        DepotDownloaderService.TryDeleteCreatedFiles(root, ["created.pak"]).Should().BeTrue();

        File.Exists(Path.Combine(root, "created.pak")).Should().BeFalse();
        // The output folder can be an existing game directory being repaired. Anything the downloader did
        // not report pre-allocating is the user's, and must survive a cancel.
        File.Exists(Path.Combine(root, "preexisting.pak")).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"..\victim.txt")]
    [InlineData("../victim.txt")]
    [InlineData(@"..\..\victim.txt")]
    [InlineData(@"sub\..\..\victim.txt")]
    public void Refuses_to_delete_outside_the_output_folder_via_a_relative_path(string hostile)
    {
        // The threat: paths come from the child process's stdout ("Pre-allocating <path>"), which is not a
        // channel this app controls.
        string root = Path.Combine(_dir, "out");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        string victim = Path.Combine(_dir, "victim.txt");
        File.WriteAllText(victim, "must survive");

        DepotDownloaderService.TryDeleteCreatedFiles(root, [hostile]);

        File.Exists(victim).Should().BeTrue();
    }

    [Fact]
    public void Refuses_to_delete_an_absolute_path_outside_the_output_folder()
    {
        string root = Path.Combine(_dir, "out");
        Directory.CreateDirectory(root);
        string victim = Path.Combine(_dir, "victim.txt");
        File.WriteAllText(victim, "must survive");

        DepotDownloaderService.TryDeleteCreatedFiles(root, [victim]);

        File.Exists(victim).Should().BeTrue();
    }

    [Fact]
    public void Refuses_a_path_that_only_shares_a_name_prefix_with_the_root()
    {
        // "C:\out-evil\x" starts with "C:\out" as a STRING. The separator in the containment check is what
        // stops that from counting as "inside".
        string root = Path.Combine(_dir, "out");
        Directory.CreateDirectory(root);
        string sibling = Path.Combine(_dir, "out-evil");
        Directory.CreateDirectory(sibling);
        string victim = Path.Combine(sibling, "x.pak");
        File.WriteAllText(victim, "must survive");

        DepotDownloaderService.TryDeleteCreatedFiles(root, [victim]);

        File.Exists(victim).Should().BeTrue();
    }

    [Fact]
    public void Deletes_the_downloaders_own_marker_directory()
    {
        string root = Path.Combine(_dir, "out");
        Directory.CreateDirectory(Path.Combine(root, ".DepotDownloader"));
        File.WriteAllText(Path.Combine(root, ".DepotDownloader", "state.json"), "{}");

        DepotDownloaderService.TryDeleteCreatedFiles(root, []).Should().BeTrue();

        Directory.Exists(Path.Combine(root, ".DepotDownloader")).Should().BeFalse();
    }

    [Fact]
    public void Prunes_directories_it_emptied_but_never_the_root()
    {
        string root = Path.Combine(_dir, "out");
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        File.WriteAllText(Path.Combine(root, "a", "b", "f.pak"), "x");

        DepotDownloaderService.TryDeleteCreatedFiles(root, [Path.Combine("a", "b", "f.pak")]);

        Directory.Exists(Path.Combine(root, "a", "b")).Should().BeFalse();
        Directory.Exists(Path.Combine(root, "a")).Should().BeFalse();
        Directory.Exists(root).Should().BeTrue();
    }

    [Fact]
    public void Keeps_a_directory_that_still_holds_something()
    {
        string root = Path.Combine(_dir, "out");
        Directory.CreateDirectory(Path.Combine(root, "a"));
        File.WriteAllText(Path.Combine(root, "a", "mine.pak"), "theirs");
        File.WriteAllText(Path.Combine(root, "a", "ours.pak"), "new");

        DepotDownloaderService.TryDeleteCreatedFiles(root, [Path.Combine("a", "ours.pak")]);

        Directory.Exists(Path.Combine(root, "a")).Should().BeTrue();
        File.Exists(Path.Combine(root, "a", "mine.pak")).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_output_folder_deletes_nothing(string? dir) =>
        DepotDownloaderService.TryDeleteCreatedFiles(dir, ["x"]).Should().BeFalse();

    [Fact]
    public void HasDownloadedContent_is_true_only_when_the_marker_directory_exists()
    {
        string root = Path.Combine(_dir, "out");
        Directory.CreateDirectory(root);

        DepotDownloaderService.HasDownloadedContent(root).Should().BeFalse();
        DepotDownloaderService.HasDownloadedContent(null).Should().BeFalse();

        Directory.CreateDirectory(Path.Combine(root, ".DepotDownloader"));
        DepotDownloaderService.HasDownloadedContent(root).Should().BeTrue();
    }

    // ── Disk budget helpers ───────────────────────────────────────────────────

    [Fact]
    public void FreeSpaceFor_reports_the_volume_holding_the_path() =>
        DepotDownloaderService.FreeSpaceFor(_dir).Should().NotBeNull().And.BeGreaterThan(0);

    [Fact]
    public void FreeSpaceFor_returns_null_rather_than_throwing_on_an_unresolvable_path() =>
        DepotDownloaderService.FreeSpaceFor(@"\\no-such-host\no-such-share\x").Should().BeNull();

    [Fact]
    public void DriveOf_reports_the_volume_root() =>
        DepotDownloaderService.DriveOf(_dir).Should().Be(Path.GetPathRoot(Path.GetFullPath(_dir)));

    [Fact]
    public void DriveOf_returns_empty_rather_than_throwing_on_junk() =>
        DepotDownloaderService.DriveOf("\0invalid").Should().BeEmpty();

    // ── The depot-keys file: scope and lifetime ───────────────────────────────

    [Fact]
    public void Writes_the_keys_file_in_the_format_the_tool_expects()
    {
        var keys = new Dictionary<long, string> { [1001] = new('a', 64), [1002] = new('b', 64) };

        using var file = DepotDownloaderService.WriteKeysFile(keys);

        File.ReadAllText(file.Path).Should().Be($"1001;{new string('a', 64)}\n1002;{new string('b', 64)}\n");
    }

    [Fact]
    public void The_keys_file_is_readable_only_by_the_current_user()
    {
        // Depot keys are plaintext by necessity — a separate process has to read them — so scope and
        // lifetime are the whole protection. The DACL is tighter than the containing per-user temp
        // directory, which also grants SYSTEM and the local Administrators group.
        using var file = DepotDownloaderService.WriteKeysFile(new Dictionary<long, string> { [1] = new('a', 64) });

        var security = new FileInfo(file.Path).GetAccessControl();
        security.AreAccessRulesProtected.Should().BeTrue();

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        var sids = rules.Cast<FileSystemAccessRule>().Select(r => r.IdentityReference.Value).Distinct();
        sids.Should().ContainSingle().Which.Should().Be(WindowsIdentity.GetCurrent().User!.Value);
    }

    [Fact]
    public void The_keys_file_is_deleted_when_the_download_finishes()
    {
        string path;
        using (var file = DepotDownloaderService.WriteKeysFile(new Dictionary<long, string> { [1] = new('a', 64) }))
        {
            path = file.Path;
            File.Exists(path).Should().BeTrue();
        }

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void The_startup_sweep_removes_keys_a_killed_process_left_behind()
    {
        // Dispose covers every path that unwinds. It does NOT cover a kill, which is exactly the case where
        // live decryption keys would otherwise sit on disk until something else happened to clean up.
        var file = DepotDownloaderService.WriteKeysFile(new Dictionary<long, string> { [1] = new('a', 64) });
        File.Exists(file.Path).Should().BeTrue();

        DepotDownloaderService.SweepKeyFiles();

        File.Exists(file.Path).Should().BeFalse();
    }

    [Fact]
    public void Each_keys_file_gets_an_unguessable_name()
    {
        using var a = DepotDownloaderService.WriteKeysFile(new Dictionary<long, string> { [1] = new('a', 64) });
        using var b = DepotDownloaderService.WriteKeysFile(new Dictionary<long, string> { [1] = new('a', 64) });

        a.Path.Should().NotBe(b.Path);
    }

    // ── ResolveKeys ───────────────────────────────────────────────────────────

    [Fact]
    public void Reads_depot_keys_from_the_installed_lua()
    {
        string root = NewSteamRoot();
        string key = new('a', 64);
        File.WriteAllText(Path.Combine(root, "config", "stplug-in", "480.lua"),
            $"addappid(480)\naddappid(1001, 1, \"{key}\")\n");

        var keys = NewService(root).ResolveKeys(480);

        keys.Should().ContainKey(1001).WhoseValue.Should().Be(key);
    }

    [Fact]
    public void Reads_a_depot_key_from_a_switched_off_lua_line()
    {
        // A depot switched off on the Depots page still has a valid key, and the user explicitly picked it.
        string root = NewSteamRoot();
        string key = new('c', 64);
        File.WriteAllText(Path.Combine(root, "config", "stplug-in", "480.lua"),
            $"addappid(480)\n--addappid(1002, 1, \"{key}\")\n");

        NewService(root).ResolveKeys(480).Should().ContainKey(1002).WhoseValue.Should().Be(key);
    }

    [Fact]
    public void Falls_back_to_config_vdf_for_depots_the_lua_does_not_carry()
    {
        string root = NewSteamRoot();
        string luaKey = new('a', 64);
        string vdfKey = new('b', 64);
        File.WriteAllText(Path.Combine(root, "config", "stplug-in", "480.lua"),
            $"addappid(480)\naddappid(1001, 1, \"{luaKey}\")\n");
        File.WriteAllText(Path.Combine(root, "config", "config.vdf"), $$"""
            "InstallConfigStore"
            {
                "depots"
                {
                    "2002"
                    {
                        "DecryptionKey"     "{{vdfKey}}"
                    }
                }
            }
            """);

        var keys = NewService(root).ResolveKeys(480);

        keys.Should().ContainKey(1001).WhoseValue.Should().Be(luaKey);
        keys.Should().ContainKey(2002).WhoseValue.Should().Be(vdfKey);
    }

    [Fact]
    public void The_lua_wins_when_both_sources_carry_the_same_depot()
    {
        // The lua is where LuaTools itself puts keys, so it is the more specific source.
        string root = NewSteamRoot();
        string luaKey = new('a', 64);
        string vdfKey = new('b', 64);
        File.WriteAllText(Path.Combine(root, "config", "stplug-in", "480.lua"),
            $"addappid(480)\naddappid(1001, 1, \"{luaKey}\")\n");
        File.WriteAllText(Path.Combine(root, "config", "config.vdf"), $$"""
            "depots"
            {
                "1001"
                {
                    "DecryptionKey"     "{{vdfKey}}"
                }
            }
            """);

        NewService(root).ResolveKeys(480).Should().ContainKey(1001).WhoseValue.Should().Be(luaKey);
    }

    [Fact]
    public void Resolves_no_keys_when_neither_source_exists() =>
        NewService(NewSteamRoot()).ResolveKeys(480).Should().BeEmpty();

    // ── ResolveManifestPath ───────────────────────────────────────────────────

    [Fact]
    public void Resolves_a_cached_manifest_whose_content_matches_its_name()
    {
        string root = NewSteamRoot();
        string path = Path.Combine(root, "config", "depotcache", "1001_555.manifest");
        File.WriteAllBytes(path, TestManifests.Build(depotId: 1001, gid: 555));

        NewService(root).ResolveManifestPath(1001, "555").Should().Be(path);
    }

    [Fact]
    public void Refuses_a_truncated_manifest_sitting_under_the_right_name()
    {
        // A killed download or a full disk leaves exactly this: the right name over unusable bytes. Treating
        // it as a cache MISS is what lets the fetch path repair it; trusting it would fail every run.
        string root = NewSteamRoot();
        byte[] full = TestManifests.Build(depotId: 1001, gid: 555);
        File.WriteAllBytes(Path.Combine(root, "config", "depotcache", "1001_555.manifest"), full[..8]);

        NewService(root).ResolveManifestPath(1001, "555").Should().BeNull();
    }

    [Fact]
    public void Refuses_a_manifest_whose_content_belongs_to_another_depot()
    {
        string root = NewSteamRoot();
        File.WriteAllBytes(Path.Combine(root, "config", "depotcache", "1001_555.manifest"),
            TestManifests.Build(depotId: 9999, gid: 555));

        NewService(root).ResolveManifestPath(1001, "555").Should().BeNull();
    }

    [Fact]
    public void Resolves_nothing_when_the_manifest_is_absent() =>
        NewService(NewSteamRoot()).ResolveManifestPath(1001, "555").Should().BeNull();

    [Fact]
    public void Discards_a_cached_manifest_so_a_refetch_can_replace_it()
    {
        // Mandatory before re-fetching: InstallManifestFile skips an existing destination, so a corrupt
        // entry would survive the fetch and fail again on every attempt.
        string root = NewSteamRoot();
        string path = Path.Combine(root, "config", "depotcache", "1001_555.manifest");
        File.WriteAllBytes(path, [1, 2, 3]);

        var service = NewService(root);

        service.DiscardCachedManifest(1001, "555").Should().BeTrue();
        File.Exists(path).Should().BeFalse();
        service.DiscardCachedManifest(1001, "555").Should().BeFalse(); // nothing left to discard
    }

    // ── Guests ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_guest_cannot_fetch_missing_manifests()
    {
        // Guests can still download depots whose manifest Steam already has; the picker greys out the rest
        // without needing a request to decide.
        NewService(NewSteamRoot()).CanFetchManifests.Should().BeFalse();
    }
}
