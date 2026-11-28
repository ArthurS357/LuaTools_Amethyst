using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Backups;
using LuaToolsGui.Services.Downloads;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The manifest backup: the pure decisions (<see cref="ManifestBackupPolicy"/>, the lua parser), the store
/// against throwaway Steam/backup folders, the restore gate, and the off-by-default setting.
/// </summary>
public sealed partial class ManifestBackupTests : IDisposable
{
    private const long AppId = 386940;
    private static readonly DateTime T0 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));
    private readonly string _steamRoot;
    private readonly string _dest;

    public ManifestBackupTests()
    {
        _steamRoot = Path.Combine(_dir, "steam");
        _dest = Path.Combine(_dir, "backup");
        Directory.CreateDirectory(StPlugIn);
        Directory.CreateDirectory(DepotCache);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string StPlugIn => Path.Combine(_steamRoot, "config", "stplug-in");
    private string DepotCache => Path.Combine(_steamRoot, "depotcache");
    private string AppDir => Path.Combine(_dest, AppId.ToString());
    private string ZipPath => Path.Combine(AppDir, $"{AppId}.zip");

    private void WriteLua(string text) => File.WriteAllText(Path.Combine(StPlugIn, $"{AppId}.lua"), text);

    /// <summary>A structurally real manifest (the backup refuses anything else); <paramref name="content"/> only
    /// varies its size.</summary>
    private string WriteManifest(long depot, string id, DateTime writtenUtc, string content = "manifest")
    {
        string path = Path.Combine(DepotCache, $"{depot}_{id}.manifest");
        File.WriteAllBytes(path, RealManifest(depot, id, content));
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    private static byte[] RealManifest(long depot, string id, string content = "manifest") =>
        TestManifests.Build(depot, ulong.Parse(id, System.Globalization.CultureInfo.InvariantCulture), firstFilename: content + id);

    private ManifestBackupSummary BackupAll(string? dest = null) =>
        ManifestBackupStore.BackupAll(StPlugIn, DepotCache, dest ?? _dest, _ => "Test Game", null, CancellationToken.None);

    private static DepotManifest M(long depot, string id, DateTime t) => new(depot, id, $@"C:\x\{depot}_{id}.manifest", t);

    // ── Lua parser ──────────────────────────────────────────────────

    [Fact]
    public void Parser_reads_addappid_and_both_pin_kinds_and_skips_garbage()
    {
        var lua = LuaFileParser.ParseText("""
            -- header comment
            addappid(386940)
            addappid(386941, 1, "abcd") -- Content
            setManifestid(386941, "111")
            --setManifestid(386942, '222')
            addappid(386942,1,'k')
            addappid(oops)
            setManifestid(386943 "333")
            --addappid(386944, 1, "dead")
            """, AppId);

        lua.Entries.Select(e => e.Id).Should().Equal(386940, 386941, 386942);
        lua.ActivePins.Should().ContainKey(386941).WhoseValue.Should().Be("111");
        lua.CommentedPins.Should().ContainKey(386942).WhoseValue.Should().Be("222"); // single quotes are valid Lua
        lua.ActivePins.Should().NotContainKey(386943);                               // malformed: skipped
        lua.DisabledEntries.Select(e => e.Id).Should().Equal(386944);
    }

    [Fact]
    public void Parser_requires_matching_quotes()
    {
        var lua = LuaFileParser.ParseText("""
            addappid(1, 1, "abc')
            addappid(2, 1, 'abc')
            setManifestid(2, "5')
            setManifestid(3, '6')
            """, AppId);

        lua.Entries.Select(e => e.Id).Should().Equal(2);
        lua.Entries[0].Key.Should().Be("abc");
        lua.ActivePins.Keys.Should().Equal(3);
    }

    [Fact]
    public void Parser_on_empty_text_falls_back_to_the_file_name_appid()
    {
        var lua = LuaFileParser.ParseText("", AppId);
        lua.BaseAppId.Should().Be(AppId);
        lua.Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData("228983_123.manifest", true)]
    [InlineData("228983.manifest", false)]
    [InlineData("228983_12a.manifest", false)]
    [InlineData("..\\228983_1.manifest", false)]
    public void Manifest_names_follow_the_installer_shape_rule(string name, bool ok) =>
        ManifestBackupPolicy.TryParseManifestName(name, out _, out _).Should().Be(ok);

    // ── Selection ───────────────────────────────────────────────────

    [Fact]
    public void Selection_takes_the_newest_manifest_per_depot_by_write_time_not_by_id()
    {
        var picked = ManifestBackupPolicy.SelectManifests([1, 2], new Dictionary<long, string>(),
        [
            M(1, "900", T0), M(1, "100", T0.AddDays(1)), // id 100 is the newer build
            M(2, "5", T0),
            M(3, "7", T0),                               // a depot the lua does not declare
        ]);

        picked.Select(p => (p.DepotId, p.ManifestId)).Should().Equal((1, "100"), (2, "5"));
    }

    [Fact]
    public void Selection_keeps_a_pinned_manifest_next_to_the_newest()
    {
        var picked = ManifestBackupPolicy.SelectManifests([1], new Dictionary<long, string> { [1] = "900" },
            [M(1, "900", T0), M(1, "100", T0.AddDays(1)), M(1, "50", T0.AddDays(-1))]);

        picked.Select(p => p.ManifestId).Should().BeEquivalentTo("100", "900");
    }

    // ── Change detection ────────────────────────────────────────────

    private static ManifestBackupMetadata Meta(string lua, params (long, string)[] manifests) =>
        new(1, AppId, null, T0, lua, "zip", [.. manifests.Select(m => new BackedUpManifest(m.Item1, m.Item2, "x"))]);

    [Fact]
    public void Decide_covers_new_unchanged_and_updated()
    {
        DepotManifest[] now = [M(1, "10", T0)];

        ManifestBackupPolicy.Decide(null, false, "a", now).Should().Be(ManifestBackupChange.New);
        ManifestBackupPolicy.Decide(Meta("A", (1, "10")), true, "a", now).Should().Be(ManifestBackupChange.Unchanged);
        ManifestBackupPolicy.Decide(Meta("b", (1, "10")), true, "a", now).Should().Be(ManifestBackupChange.Updated);
        ManifestBackupPolicy.Decide(Meta("a", (1, "9")), true, "a", now).Should().Be(ManifestBackupChange.Updated);
        ManifestBackupPolicy.Decide(Meta("a", (1, "10")), false, "a", now).Should().Be(ManifestBackupChange.Updated);
    }

    // ── Destination policy ──────────────────────────────────────────

    [Fact]
    public void Destination_policy_refuses_roots_system_folders_relative_paths_and_steam()
    {
        string steam = Path.Combine(_dir, "steam");
        ManifestBackupPolicy.IsAcceptableDestination(@"C:\", steam).Should().BeFalse();
        ManifestBackupPolicy.IsAcceptableDestination(Environment.GetFolderPath(Environment.SpecialFolder.Windows), steam).Should().BeFalse();
        ManifestBackupPolicy.IsAcceptableDestination(@"relative\folder", steam).Should().BeFalse();
        ManifestBackupPolicy.IsAcceptableDestination(steam, steam).Should().BeFalse();
        ManifestBackupPolicy.IsAcceptableDestination(Path.Combine(steam, "backups"), steam).Should().BeFalse();
        ManifestBackupPolicy.IsAcceptableDestination(Path.Combine(steam, "..", "steam", "x"), steam).Should().BeFalse();
        ManifestBackupPolicy.IsAcceptableDestination(_dest, steam).Should().BeTrue();
    }

    [Fact]
    public void The_store_refuses_a_destination_inside_steam()
    {
        WriteLua("addappid(386940)");
        var act = () => BackupAll(Path.Combine(_steamRoot, "backup"));
        act.Should().Throw<ArgumentException>();
        Directory.Exists(Path.Combine(_steamRoot, "backup")).Should().BeFalse();
    }

    // ── Store: new / unchanged / updated ────────────────────────────

    [Fact]
    public void First_pass_writes_a_zip_and_metadata_second_pass_skips_newer_manifest_rewrites()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\n--setManifestid(386941,\"111\")\n");
        WriteManifest(386941, "111", T0);

        BackupAll().Games.Should().ContainSingle().Which.Change.Should().Be(ManifestBackupChange.New);
        var meta = ManifestBackupStore.ReadMetadata(AppDir, AppId)!;
        meta.Name.Should().Be("Test Game");
        meta.Manifests.Should().ContainSingle().Which.ManifestId.Should().Be("111");
        meta.ZipSha256.Should().Be(FileHash.Sha256(ZipPath));

        // Unchanged: nothing is rewritten, so a synced folder has nothing to upload.
        DateTime written = File.GetLastWriteTimeUtc(ZipPath);
        File.SetLastWriteTimeUtc(ZipPath, written.AddMinutes(-5));
        BackupAll().Games.Single().Change.Should().Be(ManifestBackupChange.Unchanged);
        File.GetLastWriteTimeUtc(ZipPath).Should().Be(written.AddMinutes(-5));

        // Steam fetched a newer manifest for the depot: the backup follows it AND keeps the pinned one.
        WriteManifest(386941, "222", T0.AddDays(1));
        BackupAll().Games.Single().Change.Should().Be(ManifestBackupChange.Updated);
        ManifestBackupStore.ReadMetadata(AppDir, AppId)!.Manifests.Select(m => m.ManifestId)
            .Should().BeEquivalentTo("111", "222");
    }

    [Fact]
    public void A_changed_lua_updates_the_backup()
    {
        WriteLua("addappid(386940)");
        BackupAll();
        WriteLua("addappid(386940)\naddappid(386950)");

        BackupAll().Games.Single().Change.Should().Be(ManifestBackupChange.Updated);
    }

    [Fact]
    public void A_game_with_no_local_manifest_is_backed_up_and_reported()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");

        var summary = BackupAll();

        summary.WithoutManifests.Should().Equal(AppId);
        summary.ManifestCount.Should().Be(0);
        File.Exists(ZipPath).Should().BeTrue();
    }

    [Fact]
    public void Backing_up_never_changes_the_steam_folder()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\nsetManifestid(386941,\"111\")");
        WriteManifest(386941, "111", T0);
        string Snapshot() => string.Join("|", Directory.EnumerateFiles(_steamRoot, "*", SearchOption.AllDirectories)
            .Order().Select(f => $"{f}:{FileHash.Sha256(f)}:{File.GetLastWriteTimeUtc(f).Ticks}"));
        string before = Snapshot();

        BackupAll();

        Snapshot().Should().Be(before);
    }

    // ── DropZone / installer compatibility ──────────────────────────

    [Fact]
    public void The_backup_zip_installs_through_the_drop_zone_path()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\nsetManifestid(386941,\"111\")");
        WriteManifest(386941, "111", T0);
        BackupAll();

        // A second, empty Steam: what another machine (or a reinstall) would have.
        string other = Path.Combine(_dir, "steam2");
        Directory.CreateDirectory(Path.Combine(other, "config", "stplug-in"));
        Directory.CreateDirectory(Path.Combine(other, "depotcache"));
        var settings = new SettingsService(Path.Combine(_dir, "settings2")) { SteamPathOverride = other };
        var steam = new SteamService(settings);
        var installer = new LuaInstaller(steam, settings, new CacheService(Path.Combine(_dir, "cache2")),
            new LuaVault(() => steam.StPlugInDir, Path.Combine(_dir, "vault2")));

        LuaInstaller.IsInstallable(ZipPath).Should().BeTrue();
        LuaInstaller.AppIdForZip(ZipPath).Should().Be(AppId);
        var result = installer.InstallZip(ZipPath, AppId);

        result.AnyFailed.Should().BeFalse();
        result.LuaInstalled.Should().BeTrue();
        result.ManifestCount.Should().Be(1);
        File.Exists(Path.Combine(other, "depotcache", "386941_111.manifest")).Should().BeTrue();
        File.Exists(Path.Combine(other, "config", "stplug-in", $"{AppId}.lua")).Should().BeTrue();
    }

    // ── Restore gate (fail-closed) ──────────────────────────────────

    private string Staging => Path.Combine(_dir, "staging");

    [Fact]
    public void A_verified_backup_is_staged_as_a_copy()
    {
        WriteLua("addappid(386940)");
        BackupAll();

        var staged = ManifestBackupStore.StageVerifiedRestore(AppId, _dest, Staging);

        staged.Should().NotBeNull();
        using (var pin = staged!.Value.Pin)
        {
            staged.Value.Path.Should().StartWith(Staging);
            pin.Position.Should().Be(0);

            // The pin is read-share only: the installer can read the verified copy, nothing can replace it.
            ((Action)(() => File.WriteAllText(staged.Value.Path, "swap"))).Should().Throw<IOException>();
            ((Action)(() => File.Delete(staged.Value.Path))).Should().Throw<IOException>();
            using (ZipFile.OpenRead(staged.Value.Path)) { }
        }
        FileHash.Sha256(staged.Value.Path).Should().Be(FileHash.Sha256(ZipPath));
    }

    [Fact]
    public void A_tampered_zip_is_refused()
    {
        WriteLua("addappid(386940)");
        BackupAll();
        File.AppendAllText(ZipPath, "tamper");

        ManifestBackupStore.StageVerifiedRestore(AppId, _dest, Staging).Should().BeNull();
        Directory.EnumerateFiles(Staging).Should().BeEmpty(); // the rejected copy is not left behind
    }

    [Fact]
    public void Missing_or_corrupt_metadata_means_no_restore()
    {
        WriteLua("addappid(386940)");
        BackupAll();
        File.WriteAllText(Path.Combine(AppDir, ManifestBackupStore.MetadataFileName), "{ not json");

        ManifestBackupStore.StageVerifiedRestore(AppId, _dest, Staging).Should().BeNull();
    }

    [Theory]
    [InlineData("../evil.manifest")]
    [InlineData("sub/386941_1.manifest")]
    [InlineData("C:386941_1.manifest")]
    [InlineData("999.lua")]
    [InlineData("readme.txt")]
    public void An_archive_with_an_unexpected_entry_fails_verification_even_with_a_matching_hash(string entry)
    {
        string zip = Path.Combine(_dir, "crafted.zip");
        Directory.CreateDirectory(_dir);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry($"{AppId}.lua");
            archive.CreateEntry(entry);
        }
        // The attacker controls metadata.json too, so the hash matches — the entry rule must still refuse it.
        Verify(zip).Should().BeFalse();
    }

    /// <summary>The attacker controls metadata.json too: it is built to agree with whatever the zip holds, so only
    /// the entry and content rules can refuse it.</summary>
    private static bool Verify(string zip)
    {
        string luaSha = "x";
        var manifests = new List<BackedUpManifest>();
        using (var a = ZipFile.OpenRead(zip))
            foreach (var e in a.Entries)
            {
                using var ms = new MemoryStream();
                using (var s = e.Open()) s.CopyTo(ms);
                string sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ms.ToArray()));
                if (e.FullName == $"{AppId}.lua") luaSha = sha;
                else if (ManifestBackupPolicy.TryParseManifestName(e.FullName, out long d, out string id)) manifests.Add(new(d, id, sha));
            }
        var meta = new ManifestBackupMetadata(1, AppId, null, T0, luaSha, FileHash.Sha256(zip)!, manifests);
        using var fs = File.OpenRead(zip);
        return ManifestBackupStore.VerifyArchive(fs, AppId, meta);
    }

    private static void AddBytes(ZipArchive a, string name, byte[] bytes)
    {
        using var s = a.CreateEntry(name).Open();
        s.Write(bytes);
    }

    [Fact]
    public void An_archive_without_a_lua_fails_verification()
    {
        string zip = Path.Combine(_dir, "nolua.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntry("386941_1.manifest");

        Verify(zip).Should().BeFalse();
    }

    [Fact]
    public void A_symlink_entry_fails_verification_and_a_clean_one_passes()
    {
        string clean = Path.Combine(_dir, "clean.zip"), link = Path.Combine(_dir, "link.zip");
        using (var a = ZipFile.Open(clean, ZipArchiveMode.Create)) { a.CreateEntry($"{AppId}.lua"); AddBytes(a, "386941_1.manifest", RealManifest(386941, "1")); }
        using (var a = ZipFile.Open(link, ZipArchiveMode.Create))
        {
            a.CreateEntry($"{AppId}.lua");
            var e = a.CreateEntry("386941_1.manifest");
            e.ExternalAttributes = unchecked((int)(0xA1FFu << 16)); // S_IFLNK | 0777
            using var s = e.Open();
            s.Write(RealManifest(386941, "1"));
        }

        Verify(clean).Should().BeTrue();
        Verify(link).Should().BeFalse();
    }

    [Fact]
    public void A_damaged_backup_zip_is_rewritten_on_the_next_pass_instead_of_skipped()
    {
        WriteLua("addappid(386940)");
        BackupAll();
        File.WriteAllText(ZipPath, "torn by a sync conflict");

        BackupAll().Games.Single().Change.Should().Be(ManifestBackupChange.Updated);
        var restored = ManifestBackupStore.StageVerifiedRestore(AppId, _dest, Staging);
        restored.Should().NotBeNull(); // restorable again
        restored!.Value.Pin.Dispose();
        ManifestBackupStore.ReadMetadata(AppDir, AppId)!.ZipSha256.Should().Be(FileHash.Sha256(ZipPath));
    }

    [Fact]
    public void A_stale_temp_from_a_killed_pass_is_swept_and_a_fresh_one_kept()
    {
        WriteLua("addappid(386940)");
        BackupAll();
        string stale = Path.Combine(AppDir, $"{AppId}.zip.{Guid.NewGuid():N}.tmp");
        string fresh = Path.Combine(AppDir, $"{AppId}.zip.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(stale, "x");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

        BackupAll();

        File.Exists(stale).Should().BeFalse();
        File.Exists(fresh).Should().BeTrue();
    }

    [Fact]
    public void A_junction_as_destination_is_refused()
    {
        string target = Path.Combine(_dir, "real");
        string junction = Path.Combine(_dir, "junction");
        Directory.CreateDirectory(target);
        DirectoryJunction.Create(junction, target).Should().BeTrue();
        WriteLua("addappid(386940)");

        try
        {
            var act = () => BackupAll(junction);

            act.Should().Throw<ArgumentException>();
            Directory.EnumerateFileSystemEntries(target).Should().BeEmpty();
        }
        finally { DirectoryJunction.Remove(junction); } // a recursive delete refuses to walk a junction

    }

    [Fact]
    public void Metadata_for_another_app_is_ignored()
    {
        WriteLua("addappid(386940)");
        BackupAll();
        string path = Path.Combine(AppDir, ManifestBackupStore.MetadataFileName);
        File.WriteAllText(path, File.ReadAllText(path).Replace("386940", "1", StringComparison.Ordinal));

        ManifestBackupStore.ReadMetadata(AppDir, AppId).Should().BeNull();
    }

    // ── Settings: off by default, backward compatible ───────────────

    [Fact]
    public void The_setting_starts_disabled_and_old_settings_files_read_as_disabled()
    {
        string dir = Path.Combine(_dir, "settings");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{ "AutoUpdateApps": false }""");

        var settings = new SettingsService(dir);

        settings.ManifestBackupMode.Should().Be(ManifestBackupMode.Disabled);
        settings.ManifestBackupFolder.Should().BeNull();
        settings.AutoUpdateApps.Should().BeFalse(); // the rest of the old file is untouched
    }

    [Theory]
    [InlineData("R2")]
    [InlineData("42")]
    [InlineData("")]
    public void An_unknown_mode_reads_as_disabled_without_losing_the_file(string stored)
    {
        string dir = Path.Combine(_dir, "settings");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            JsonSerializer.Serialize(new { ManifestBackupMode = stored, AutoUpdateApps = false }));

        var settings = new SettingsService(dir);

        settings.ManifestBackupMode.Should().Be(ManifestBackupMode.Disabled);
        settings.AutoUpdateApps.Should().BeFalse();
    }

    [Fact]
    public void Mode_and_folder_persist_and_a_relative_folder_is_dropped()
    {
        string dir = Path.Combine(_dir, "settings");
        var settings = new SettingsService(dir)
        {
            ManifestBackupMode = ManifestBackupMode.LocalFolder,
            ManifestBackupFolder = _dest,
        };

        var reloaded = new SettingsService(dir);
        reloaded.ManifestBackupMode.Should().Be(ManifestBackupMode.LocalFolder);
        reloaded.ManifestBackupFolder.Should().Be(_dest);

        settings.ManifestBackupFolder = "relative";
        new SettingsService(dir).ManifestBackupFolder.Should().BeNull();
    }

    // ── Service + view model ────────────────────────────────────────

    private (SettingsService Settings, ManifestBackupService Service) CreateService()
    {
        var settings = new SettingsService(Path.Combine(_dir, "svc")) { SteamPathOverride = _steamRoot };
        var steam = new SteamService(settings);
        var installer = new LuaInstaller(steam, settings, new CacheService(Path.Combine(_dir, "svc-cache")),
            new LuaVault(() => steam.StPlugInDir, Path.Combine(_dir, "svc-vault")));
        var appList = new SteamAppListCache(Path.Combine(_dir, "applist.json"), new System.Net.Http.HttpClientHandler());
        return (settings, new ManifestBackupService(settings, steam, installer, appList, new ToastService()));
    }

    [Fact]
    public void The_service_is_inert_until_enabled_with_a_valid_folder()
    {
        var (settings, service) = CreateService();
        WriteLua("addappid(386940)");

        service.ActiveFolder.Should().BeNull();
        service.TryStageRestore(AppId).Should().BeNull();

        settings.ManifestBackupMode = ManifestBackupMode.LocalFolder;
        service.ActiveFolder.Should().BeNull(); // on, but no folder yet

        settings.ManifestBackupFolder = Path.Combine(_steamRoot, "inside");
        service.ActiveFolder.Should().BeNull(); // a folder inside Steam is never used

        settings.ManifestBackupFolder = _dest;
        service.ActiveFolder.Should().Be(_dest);
    }

    [Fact]
    public async Task The_batch_backs_up_installed_games_once_enabled()
    {
        var (settings, service) = CreateService();
        WriteLua("addappid(386940)");
        (await service.BackupAllAsync(null, CancellationToken.None)).Should().BeNull(); // off: nothing happens
        Directory.Exists(_dest).Should().BeFalse();

        settings.ManifestBackupMode = ManifestBackupMode.LocalFolder;
        settings.ManifestBackupFolder = _dest;
        var summary = await service.BackupAllAsync(null, CancellationToken.None);

        summary!.Count(ManifestBackupChange.New).Should().Be(1);
        File.Exists(ZipPath).Should().BeTrue();
    }

    [Fact]
    public void The_service_hands_back_a_pinned_verified_copy_only_when_enabled()
    {
        var (settings, service) = CreateService();
        WriteLua("addappid(386940)");
        BackupAll();
        service.TryStageRestore(AppId).Should().BeNull(); // off

        settings.ManifestBackupMode = ManifestBackupMode.LocalFolder;
        settings.ManifestBackupFolder = _dest;
        using var file = service.TryStageRestore(AppId);

        file.Should().NotBeNull();
        file!.Pin.Should().NotBeNull();
        FileHash.Sha256(file.FilePath).Should().Be(FileHash.Sha256(ZipPath));
        file.Discard();
        File.Exists(file.FilePath).Should().BeFalse();
    }

    [Fact]
    public async Task After_an_install_the_hook_backs_up_the_finished_install_including_its_manifests()
    {
        var settings = new SettingsService(Path.Combine(_dir, "hook"))
        {
            SteamPathOverride = _steamRoot,
            ManifestBackupMode = ManifestBackupMode.LocalFolder,
            ManifestBackupFolder = _dest,
        };
        var steam = new SteamService(settings);
        var installer = new LuaInstaller(steam, settings, new CacheService(Path.Combine(_dir, "hook-cache")),
            new LuaVault(() => steam.StPlugInDir, Path.Combine(_dir, "hook-vault")));
        var service = new ManifestBackupService(settings, steam, installer,
            new SteamAppListCache(Path.Combine(_dir, "hook-applist.json"), new System.Net.Http.HttpClientHandler()), new ToastService());
        installer.Installed += service.OnInstalled;
        // Widen the window between Installed and the manifest extraction (still inside the install lock), so a
        // hook that did not wait for the install to settle would back up a zip with no manifest.
        installer.Installed += _ => Thread.Sleep(500);

        // The lua comes FIRST in the zip, so Installed fires before the manifest is extracted.
        string zip = Path.Combine(_dir, "incoming.zip");
        using (var a = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(a.CreateEntry($"{AppId}.lua").Open()))
                w.Write("addappid(386940)\naddappid(386941,1,\"k\")\nsetManifestid(386941,\"777\")");
            using (var s = a.CreateEntry("386941_777.manifest").Open()) s.Write(RealManifest(386941, "777"));
        }
        installer.InstallZip(zip, AppId).AnyFailed.Should().BeFalse();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        ManifestBackupMetadata? meta = null;
        while ((meta = ManifestBackupStore.ReadMetadata(AppDir, AppId)) is null && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        meta.Should().NotBeNull();
        meta!.Manifests.Select(m => m.ManifestId).Should().Equal("777");
    }

    [Fact]
    public void The_view_model_starts_off_and_refuses_an_unusable_folder()
    {
        var (settings, service) = CreateService();
        var vm = new ManifestBackupViewModel(settings, service);

        vm.SelectedMode.Mode.Should().Be(ManifestBackupMode.Disabled);
        vm.IsOn.Should().BeFalse();
        vm.BackupAllCommand.CanExecute(null).Should().BeFalse();

        vm.SelectedMode = vm.ModeOptions[1];
        vm.TrySetFolder(_steamRoot).Should().BeFalse();
        vm.FolderError.Should().NotBeNull();
        settings.ManifestBackupFolder.Should().BeNull();

        vm.TrySetFolder(_dest).Should().BeTrue();
        vm.FolderError.Should().BeNull();
        settings.ManifestBackupMode.Should().Be(ManifestBackupMode.LocalFolder);
        vm.BackupAllCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void The_summary_text_carries_every_count()
    {
        var summary = new ManifestBackupSummary(
        [
            new(1, ManifestBackupChange.New, 2, null),
            new(2, ManifestBackupChange.Updated, 1, null),
            new(3, ManifestBackupChange.Unchanged, 0, null),
            new(4, null, 0, "boom"),
        ]);

        summary.WithoutManifests.Should().Equal(3);
        ManifestBackupViewModel.Describe(summary).Should().Be(string.Format(
            System.Globalization.CultureInfo.CurrentCulture, LuaToolsGui.Resources.Strings.Settings_ManifestBackup_Result,
            1, 1, 1, 1, 1));
    }

    // ── Frente 1 #1: FetchOrRestoreCoreAsync fallback vs cancellation ───

    [Fact]
    public async Task FetchOrRestore_returns_fetched_file_without_touching_backup_on_success()
    {
        using var fetched = new DownloadedFile(Path.Combine(_dir, "fetched.zip"), "fetched.zip");
        bool restoreCalled = false;
        bool callbackCalled = false;

        var result = await ManifestJobFactory.FetchOrRestoreCoreAsync(
            _ => Task.FromResult(fetched),
            () => { restoreCalled = true; return null; },
            _ => callbackCalled = true,
            CancellationToken.None);

        result.Should().BeSameAs(fetched);
        restoreCalled.Should().BeFalse();
        callbackCalled.Should().BeFalse();
    }

    [Fact]
    public async Task FetchOrRestore_falls_back_on_http_error_and_httpclient_timeout()
    {
        using var fallback1 = new DownloadedFile(Path.Combine(_dir, "b1.zip"), "b1.zip");
        using var fallback2 = new DownloadedFile(Path.Combine(_dir, "b2.zip"), "b2.zip");
        using var fallback3 = new DownloadedFile(Path.Combine(_dir, "b3.zip"), "b3.zip");

        int callbacks = 0;
        var fromHttp = await ManifestJobFactory.FetchOrRestoreCoreAsync(
            _ => Task.FromException<DownloadedFile>(new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable)),
            () => fallback1,
            _ => callbacks++,
            CancellationToken.None);
        var fromApi = await ManifestJobFactory.FetchOrRestoreCoreAsync(
            _ => Task.FromException<DownloadedFile>(new ApiException("down", HttpStatusCode.BadGateway)),
            () => fallback2,
            _ => callbacks++,
            CancellationToken.None);
        // HttpClient timeout surfaces as TaskCanceledException while the caller's ct is NOT cancelled.
        var fromTimeout = await ManifestJobFactory.FetchOrRestoreCoreAsync(
            _ => Task.FromException<DownloadedFile>(new TaskCanceledException("HttpClient.Timeout")),
            () => fallback3,
            _ => callbacks++,
            CancellationToken.None);

        fromHttp.Should().BeSameAs(fallback1);
        fromApi.Should().BeSameAs(fallback2);
        fromTimeout.Should().BeSameAs(fallback3);
        callbacks.Should().Be(3);
    }

    [Fact]
    public async Task FetchOrRestore_rethrows_original_error_when_no_verified_backup_exists()
    {
        var act = () => ManifestJobFactory.FetchOrRestoreCoreAsync(
            _ => Task.FromException<DownloadedFile>(new HttpRequestException("offline")),
            () => null,
            _ => throw new InvalidOperationException("must not run"),
            CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("offline");
    }

    [Fact]
    public async Task FetchOrRestore_does_not_fall_back_on_user_cancel_or_pause()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool restoreCalled = false;

        var actOp = () => ManifestJobFactory.FetchOrRestoreCoreAsync(
            token => Task.FromCanceled<DownloadedFile>(token),
            () => { restoreCalled = true; return new DownloadedFile("x", "x"); },
            _ => { },
            cts.Token);

        await actOp.Should().ThrowAsync<OperationCanceledException>();
        restoreCalled.Should().BeFalse();

        var actAbortedCancel = () => ManifestJobFactory.FetchOrRestoreCoreAsync(
            _ => Task.FromException<DownloadedFile>(new DownloadAbortedException("paused", isCancellation: true)),
            () => { restoreCalled = true; return new DownloadedFile("x", "x"); },
            _ => { },
            CancellationToken.None);

        await actAbortedCancel.Should().ThrowAsync<DownloadAbortedException>();
        restoreCalled.Should().BeFalse();
    }

    // ── Frente 1 #2: Cloud-streaming skip-check vs fail-closed restore ─

    [Fact]
    public void Unchanged_skip_check_does_not_open_or_rehash_the_zip()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\nsetManifestid(386941,\"111\")");
        WriteManifest(386941, "111", T0);
        BackupAll();

        // Hold an exclusive handle (FileShare.None) on the zip, simulating an unhydrated cloud placeholder
        // that must not be read during an unchanged scan.
        using (new FileStream(ZipPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var second = BackupAll();
            second.Games.Should().ContainSingle().Which.Change.Should().Be(ManifestBackupChange.Unchanged);
        }
    }

    [Fact]
    public void Same_length_tampered_zip_is_still_rejected_fail_closed_on_restore()
    {
        WriteLua("addappid(386940)");
        BackupAll();
        byte[] bytes = File.ReadAllBytes(ZipPath);
        bytes[^1] ^= 0xFF; // same byte length, invalid SHA-256
        File.WriteAllBytes(ZipPath, bytes);

        ManifestBackupStore.StageVerifiedRestore(AppId, _dest, Staging).Should().BeNull();
        Directory.EnumerateFiles(Staging).Should().BeEmpty();
    }

    [Fact]
    public void Legacy_L3_metadata_without_new_fields_deserializes_and_restores()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\nsetManifestid(386941,\"111\")");
        WriteManifest(386941, "111", T0);
        BackupAll();

        var full = ManifestBackupStore.ReadMetadata(AppDir, AppId)!;
        string legacyJson = JsonSerializer.Serialize(new
        {
            schema = 1,
            appId = full.AppId,
            name = full.Name,
            updatedAtUtc = full.UpdatedAtUtc,
            luaSha256 = full.LuaSha256,
            zipSha256 = full.ZipSha256,
            manifests = full.Manifests,
        });
        File.WriteAllText(Path.Combine(AppDir, ManifestBackupStore.MetadataFileName), legacyJson);

        var reloaded = ManifestBackupStore.ReadMetadata(AppDir, AppId)!;
        reloaded.ZipSizeBytes.Should().BeNull();
        reloaded.EffectiveManifestCount.Should().Be(1);
        reloaded.EffectiveHasManifests.Should().BeTrue();

        using var restored = ManifestBackupStore.StageVerifiedRestore(AppId, _dest, Staging)?.Pin;
        restored.Should().NotBeNull();
    }

    // ── Frente 1 #3: Preserve manifest LastWriteTimeUtc on restore ───

    [Fact]
    public void Restoring_an_older_backup_preserves_manifest_timestamp_so_a_newer_steam_patch_wins()
    {
        DateTime older = new(2025, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        DateTime newer = new(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\n");
        WriteManifest(386941, "111", older);
        BackupAll();

        // Install the old backup into a second Steam root that receives a newer Steam patch afterwards.
        string other = Path.Combine(_dir, "steam-ts");
        Directory.CreateDirectory(Path.Combine(other, "config", "stplug-in"));
        Directory.CreateDirectory(Path.Combine(other, "depotcache"));
        var settings = new SettingsService(Path.Combine(_dir, "settings-ts")) { SteamPathOverride = other };
        var steam = new SteamService(settings);
        var installer = new LuaInstaller(steam, settings, new CacheService(Path.Combine(_dir, "cache-ts")),
            new LuaVault(() => steam.StPlugInDir, Path.Combine(_dir, "vault-ts")));

        var res = installer.InstallZip(ZipPath, AppId);
        res.AnyFailed.Should().BeFalse();

        string restoredManifest = Path.Combine(other, "depotcache", "386941_111.manifest");
        DateTime restoredUtc = File.GetLastWriteTimeUtc(restoredManifest);
        Math.Abs((restoredUtc - older).TotalSeconds).Should().BeLessThanOrEqualTo(2);

        // Steam later downloads a newer manifest (still older than wall-clock Today): it must win selection.
        string newerPath = Path.Combine(other, "depotcache", "386941_222.manifest");
        File.WriteAllText(newerPath, "newer");
        File.SetLastWriteTimeUtc(newerPath, newer);

        var indexed = ManifestBackupStore.IndexDepotCache(Path.Combine(other, "depotcache"));
        var picked = ManifestBackupPolicy.SelectManifests([386941], new Dictionary<long, string>(), indexed[386941]);
        picked.Should().ContainSingle().Which.ManifestId.Should().Be("222");
    }

    [Fact]
    public void Future_manifest_timestamp_in_zip_is_clamped_to_now()
    {
        string path = Path.Combine(_dir, "future.manifest");
        File.WriteAllText(path, "x");
        LuaInstaller.PreserveManifestLastWriteTime(path, new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero));

        File.GetLastWriteTimeUtc(path).Should().BeOnOrBefore(DateTime.UtcNow.AddSeconds(2));
    }

    // ── Frente 1 #4: Shutdown cancellation & Cloud Drive path checks ─

    [Fact]
    public async Task WaitForInstallsToSettle_honors_cancellation_during_shutdown()
    {
        var settings = new SettingsService(Path.Combine(_dir, "cancel-settle")) { SteamPathOverride = _steamRoot };
        var steam = new SteamService(settings);
        var installer = new LuaInstaller(steam, settings, new CacheService(Path.Combine(_dir, "cancel-cache")),
            new LuaVault(() => steam.StPlugInDir, Path.Combine(_dir, "cancel-vault")));

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        installer.Installed += _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        };

        string srcLua = Path.Combine(_dir, $"{AppId}.lua");
        File.WriteAllText(srcLua, "addappid(386940)");
        var installTask = Task.Run(() => installer.InstallLua(srcLua, AppId));
        entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var act = () => installer.WaitForInstallsToSettle(cts.Token);
        act.Should().Throw<OperationCanceledException>();

        release.Set();
        await installTask;
    }

    [Fact]
    public void Destination_policy_accepts_google_drive_subfolders_and_service_refuses_junctions()
    {
        string steam = Path.Combine(_dir, "steam");
        ManifestBackupPolicy.IsAcceptableDestination(@"G:\Meu Drive\LuaTools_Manifests", steam).Should().BeTrue();
        ManifestBackupPolicy.IsAcceptableDestination(@"G:\", steam).Should().BeFalse();

        string target = Path.Combine(_dir, "j-target");
        string junction = Path.Combine(_dir, "j-link");
        Directory.CreateDirectory(target);
        DirectoryJunction.Create(junction, target).Should().BeTrue();
        try
        {
            var (settings, service) = CreateService();
            var vm = new ManifestBackupViewModel(settings, service);
            service.IsAcceptableFolder(junction).Should().BeFalse();
            vm.TrySetFolder(junction).Should().BeFalse();
            vm.FolderError.Should().NotBeNull();

            // Switching back to Disabled clears FolderError cleanly.
            vm.SelectedMode = vm.ModeOptions[1];
            vm.SelectedMode = vm.ModeOptions[0];
            vm.FolderError.Should().BeNull();
        }
        finally { DirectoryJunction.Remove(junction); }
    }

    // ── Frente 2 #1: Root index.json & completeness indicator ────────

    [Fact]
    public void BackupAll_writes_root_index_with_completeness_and_avoids_redundant_rewrites()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\nsetManifestid(386941,\"111\")");
        WriteManifest(386941, "111", T0);
        long luaOnlyId = 480;
        File.WriteAllText(Path.Combine(StPlugIn, $"{luaOnlyId}.lua"), "addappid(480)\naddappid(481,1,\"k\")");

        BackupAll();

        string indexPath = Path.Combine(_dest, ManifestBackupStore.IndexFileName);
        File.Exists(indexPath).Should().BeTrue();
        Directory.EnumerateFiles(_dest, "*.tmp").Should().BeEmpty();

        var index = ManifestBackupStore.ReadIndex(_dest)!;
        index.Schema.Should().Be(ManifestBackupIndex.CurrentSchema);
        index.Games.Should().HaveCount(2);

        var complete = index.Games.Single(g => g.AppId == AppId);
        complete.HasManifests.Should().BeTrue();
        complete.ManifestCount.Should().Be(1);
        complete.ZipFile.Should().Be($"{AppId}/{AppId}.zip");
        complete.ZipSha256.Should().Be(FileHash.Sha256(ZipPath));

        var partial = index.Games.Single(g => g.AppId == luaOnlyId);
        partial.HasManifests.Should().BeFalse();
        partial.ManifestCount.Should().Be(0);
        partial.ZipFile.Should().Be($"{luaOnlyId}/{luaOnlyId}.zip");

        // Second pass with no changes must not rewrite index.json.
        DateTime oldWrite = File.GetLastWriteTimeUtc(indexPath).AddMinutes(-10);
        File.SetLastWriteTimeUtc(indexPath, oldWrite);
        BackupAll();
        File.GetLastWriteTimeUtc(indexPath).Should().Be(oldWrite);

        // BackupOne for another game upserts without dropping existing games.
        long extraId = 730;
        File.WriteAllText(Path.Combine(StPlugIn, $"{extraId}.lua"), "addappid(730)");
        ManifestBackupStore.BackupOne(extraId, StPlugIn, DepotCache, _dest, "Counter-Strike 2").Failed.Should().BeFalse();
        var updatedIndex = ManifestBackupStore.ReadIndex(_dest)!;
        updatedIndex.Games.Select(g => g.AppId).Should().Equal(luaOnlyId, extraId, AppId);
    }

    // ── A backup never loses a manifest it cannot replace ───────────

    private string[] ZipManifestNames()
    {
        using var zip = ZipFile.OpenRead(ZipPath);
        return [.. zip.Entries.Select(e => e.Name).Where(n => n.EndsWith(".manifest", StringComparison.Ordinal)).Order()];
    }

    [Fact]
    public void Stored_manifests_survive_a_depotcache_that_no_longer_has_them()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\nsetManifestid(386941,\"111\")");
        string path = WriteManifest(386941, "111", T0);
        BackupAll();
        File.Delete(path); // Steam cleared depotcache, or another PC shares the folder

        BackupAll().Games.Single().Change.Should().Be(ManifestBackupChange.Unchanged);

        // A source that delivers the lua only changes the lua but brings no manifest.
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        var outcome = BackupAll().Games.Single();

        outcome.Change.Should().Be(ManifestBackupChange.Updated);
        outcome.ManifestCount.Should().Be(1);
        ZipManifestNames().Should().Equal("386941_111.manifest");
        ManifestBackupStore.ReadMetadata(AppDir, AppId)!.EffectiveManifestCount.Should().Be(1);
        using var restored = ManifestBackupStore.StageVerifiedRestore(AppId, _dest, Staging)?.Pin;
        restored.Should().NotBeNull();
    }

    [Fact]
    public void A_pinned_stored_manifest_is_carried_next_to_a_newer_one_and_an_undeclared_depot_is_dropped()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\naddappid(386942,1,\"k\")\nsetManifestid(386941,\"111\")");
        string pinned = WriteManifest(386941, "111", T0);
        string other = WriteManifest(386942, "333", T0);
        BackupAll();
        File.Delete(pinned);
        File.Delete(other);
        WriteManifest(386941, "222", T0.AddDays(1));

        // The lua no longer declares 386942: its stored manifest goes with it.
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\nsetManifestid(386941,\"111\")");
        BackupAll();

        ZipManifestNames().Should().Equal("386941_111.manifest", "386941_222.manifest");
    }

    [Fact]
    public void An_unreadable_stored_zip_is_kept_when_it_holds_the_only_copy_of_a_manifest()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\nsetManifestid(386941,\"111\")");
        string path = WriteManifest(386941, "111", T0);
        BackupAll();
        File.Delete(path);
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");

        File.WriteAllText(ZipPath, "torn by a sync conflict");
        var outcome = BackupAll().Games.Single();

        outcome.Failed.Should().BeTrue();
        File.ReadAllText(ZipPath).Should().Be("torn by a sync conflict");
    }

    [Theory]
    [InlineData("metadata-missing")]
    [InlineData("metadata-locked")]
    [InlineData("metadata-corrupt")]
    [InlineData("zip-not-synced")]
    [InlineData("zip-absent")]
    public void An_unusable_stored_state_never_replaces_the_only_copy_of_a_manifest(string state)
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        string older = WriteManifest(386941, "000", T0);
        BackupAll();
        byte[] oldZip = File.ReadAllBytes(ZipPath);
        File.Delete(older);
        string meta = Path.Combine(AppDir, ManifestBackupStore.MetadataFileName);
        if (state is "zip-not-synced" or "zip-absent")
        {
            // A longer body, so the two zips differ in size: an equal-size one reads as Unchanged and is not touched.
            string newer = WriteManifest(386941, "111", T0.AddDays(1), "a newer and visibly longer manifest body ");
            BackupAll(); // metadata now records 111
            File.Delete(newer);
        }
        if (state == "metadata-missing") File.Delete(meta);
        if (state == "metadata-corrupt") File.WriteAllText(meta, "{ torn");
        if (state == "zip-not-synced") File.WriteAllBytes(ZipPath, oldZip); // newer metadata, older zip
        if (state == "zip-absent") File.Delete(ZipPath);
        byte[]? before = File.Exists(ZipPath) ? File.ReadAllBytes(ZipPath) : null;

        ManifestBackupOutcome outcome;
        using (state == "metadata-locked"
                   ? new FileStream(meta, FileMode.Open, FileAccess.Read, FileShare.None) : null)
            outcome = BackupAll().Games.Single();

        outcome.Failed.Should().BeTrue();
        if (before is null) File.Exists(ZipPath).Should().BeFalse();
        else File.ReadAllBytes(ZipPath).Should().Equal(before);
    }

    [Fact]
    public void Missing_metadata_is_rebuilt_when_depotcache_still_has_everything()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        WriteManifest(386941, "111", T0);
        BackupAll();
        File.Delete(Path.Combine(AppDir, ManifestBackupStore.MetadataFileName));

        BackupAll().Games.Single().Change.Should().Be(ManifestBackupChange.New);

        ManifestBackupStore.ReadMetadata(AppDir, AppId)!.EffectiveManifestCount.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-- addappid(386940)")]
    [InlineData("garbage")]
    public void A_lua_that_declares_nothing_keeps_the_stored_backup(string lua)
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        WriteManifest(386941, "111", T0);
        BackupAll();
        byte[] before = File.ReadAllBytes(ZipPath);
        WriteLua(lua);

        BackupAll().Games.Single().Failed.Should().BeTrue();

        File.ReadAllBytes(ZipPath).Should().Equal(before);
    }

    [Fact]
    public void A_lua_that_never_declared_anything_stays_unchanged_instead_of_failing()
    {
        WriteLua("-- nothing here");
        BackupAll().Games.Single().Change.Should().Be(ManifestBackupChange.New);

        BackupAll().Games.Single().Change.Should().Be(ManifestBackupChange.Unchanged);
    }

    [Fact]
    public async Task A_cancel_that_lands_while_the_fallback_stages_installs_nothing()
    {
        using var cts = new CancellationTokenSource();
        string staged = Path.Combine(_dir, "staged.zip");
        File.WriteAllText(staged, "x");
        bool announced = false;

        var act = () => ManifestJobFactory.FetchOrRestoreCoreAsync(
            _ => throw new HttpRequestException("source down"),
            () => { cts.Cancel(); return new DownloadedFile(staged, "staged.zip"); },
            _ => announced = true,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        announced.Should().BeFalse();
        File.Exists(staged).Should().BeFalse();
    }

    [Fact]
    public void Backing_up_right_after_a_restore_rewrites_nothing()
    {
        // The post-install hook fires for a restore too. Same lua + same manifests (timestamps rounded to the
        // zip's 2-second granularity) must read as Unchanged.
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\n-- setManifestid(386941,\"111\")");
        string path = WriteManifest(386941, "111", T0.AddMilliseconds(1999));
        BackupAll();
        File.Delete(path);
        var settings = new SettingsService(Path.Combine(_dir, "settings-rt")) { SteamPathOverride = _steamRoot };
        var steam = new SteamService(settings);
        var installer = new LuaInstaller(steam, settings, new CacheService(Path.Combine(_dir, "cache-rt")),
            new LuaVault(() => steam.StPlugInDir, Path.Combine(_dir, "vault-rt")));
        installer.InstallZip(ZipPath, AppId).AnyFailed.Should().BeFalse();
        DateTime old = DateTime.UtcNow.AddDays(-3);
        File.SetLastWriteTimeUtc(ZipPath, old);

        ManifestBackupStore.BackupOne(AppId, StPlugIn, DepotCache, _dest, "Test Game")
            .Change.Should().Be(ManifestBackupChange.Unchanged);
        File.GetLastWriteTimeUtc(ZipPath).Should().Be(old);
    }

    [Fact]
    public void An_installed_manifest_with_no_zip_timestamp_is_stamped_now()
    {
        string zip = Path.Combine(_dir, "epoch.zip");
        using (var a = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(a.CreateEntry($"{AppId}.lua").Open())) w.Write("addappid(386940)");
            var m = a.CreateEntry("386941_888.manifest");
            m.LastWriteTime = new DateTimeOffset(new DateTime(1980, 1, 1)); // what a zip with no timestamp reads as
            using (var w = new StreamWriter(m.Open())) w.Write("m");
        }
        var settings = new SettingsService(Path.Combine(_dir, "settings-ep")) { SteamPathOverride = _steamRoot };
        var steam = new SteamService(settings);
        var installer = new LuaInstaller(steam, settings, new CacheService(Path.Combine(_dir, "cache-ep")),
            new LuaVault(() => steam.StPlugInDir, Path.Combine(_dir, "vault-ep")));

        installer.InstallZip(zip, AppId).AnyFailed.Should().BeFalse();

        File.GetLastWriteTimeUtc(Path.Combine(DepotCache, "386941_888.manifest"))
            .Should().BeAfter(DateTime.UtcNow.AddMinutes(-5));
    }

    // ── index.json: derived catalog, serialized writers ─────────────

    [Fact]
    public void A_held_index_never_fails_a_backup_that_landed()
    {
        WriteLua("addappid(386940)");
        BackupAll();
        string indexPath = Path.Combine(_dest, ManifestBackupStore.IndexFileName);

        // A reader (the bridge, a cloud client) holding the catalog blocks both the rename and the rewrite.
        using (new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            WriteLua("addappid(386940)\naddappid(386950)");
            ManifestBackupStore.BackupOne(AppId, StPlugIn, DepotCache, _dest, "Test Game").Failed.Should().BeFalse();
            WriteLua("addappid(386940)\naddappid(386951)");
            BackupAll().Failed.Should().Be(0);
        }

        BackupAll();
        ManifestBackupStore.ReadIndex(_dest)!.Games.Single().ZipSha256.Should().Be(FileHash.Sha256(ZipPath));
    }

    [Fact]
    public async Task Concurrent_post_install_and_batch_passes_keep_every_game_in_the_index()
    {
        var (settings, service) = CreateService();
        settings.ManifestBackupMode = ManifestBackupMode.LocalFolder;
        settings.ManifestBackupFolder = _dest;
        long[] ids = [.. Enumerable.Range(1000, 12).Select(i => (long)i)];
        foreach (long id in ids)
            File.WriteAllText(Path.Combine(StPlugIn, $"{id}.lua"), $"addappid({id})");

        var batch = service.BackupAllAsync(null, CancellationToken.None);
        Parallel.ForEach(ids, service.OnInstalled);
        WriteLua("addappid(386940)"); // added while the passes run
        service.OnInstalled(AppId);
        await batch;

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (ManifestBackupStore.ReadIndex(_dest)?.Games.Count != ids.Length + 1 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        ManifestBackupStore.ReadIndex(_dest)!.Games.Select(g => g.AppId).Should().BeEquivalentTo([.. ids, AppId]);
    }

    // ── Anti-downgrade protection ───────────────────────────────────

    [Fact]
    public void AntiDowngrade_unpinned_depot_with_older_local_manifest_preserves_newer_stored_manifest()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        string newer = WriteManifest(386941, "222", T0.AddDays(5));
        BackupAll().Games.Single().Change.Should().Be(ManifestBackupChange.New);

        // Steam / another PC only has an older unpinned manifest in depotcache.
        File.Delete(newer);
        WriteManifest(386941, "111", T0);

        DateTime zipBefore = File.GetLastWriteTimeUtc(ZipPath).AddMinutes(-10);
        File.SetLastWriteTimeUtc(ZipPath, zipBefore);

        var firstPass = BackupAll().Games.Single();
        firstPass.Change.Should().Be(ManifestBackupChange.Unchanged);
        File.GetLastWriteTimeUtc(ZipPath).Should().Be(zipBefore);
        ZipManifestNames().Should().Equal("386941_222.manifest");

        // Even when the lua changes (forcing a zip rewrite), the newer stored manifest is carried and 111 is never written.
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\n-- updated lua comment");
        var secondPass = BackupAll().Games.Single();
        secondPass.Change.Should().Be(ManifestBackupChange.Updated);
        secondPass.ManifestCount.Should().Be(1);
        ZipManifestNames().Should().Equal("386941_222.manifest");
        ManifestBackupStore.ReadMetadata(AppDir, AppId)!.Manifests.Single().LastWriteUtc
            .Should().Be(T0.AddDays(5));
        using var restored = ManifestBackupStore.StageVerifiedRestore(AppId, _dest, Staging)?.Pin;
        restored.Should().NotBeNull();
    }

    [Fact]
    public void AntiDowngrade_commented_pin_autoupdate_preserves_newer_stored_manifest_alongside_pin()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\n--setManifestid(386941,\"111\")");
        string pinned = WriteManifest(386941, "111", T0);
        string newer = WriteManifest(386941, "222", T0.AddDays(5));
        BackupAll();
        ZipManifestNames().Should().Equal("386941_111.manifest", "386941_222.manifest");

        // Second pass where depotcache only holds an older unpinned file (050).
        File.Delete(pinned);
        File.Delete(newer);
        WriteManifest(386941, "050", T0.AddDays(-2));
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\n--setManifestid(386941,\"111\")\n-- extra");

        BackupAll().Games.Single().Change.Should().Be(ManifestBackupChange.Updated);
        ZipManifestNames().Should().Equal("386941_111.manifest", "386941_222.manifest");
    }

    [Fact]
    public void AntiDowngrade_unpinned_depot_with_newer_local_manifest_updates_backup()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        string older = WriteManifest(386941, "111", T0);
        BackupAll();

        File.Delete(older);
        DateTime newerTime = T0.AddDays(3);
        WriteManifest(386941, "222", newerTime);

        var outcome = BackupAll().Games.Single();
        outcome.Change.Should().Be(ManifestBackupChange.Updated);
        outcome.ManifestCount.Should().Be(1);
        ZipManifestNames().Should().Equal("386941_222.manifest");
        ManifestBackupStore.ReadMetadata(AppDir, AppId)!.Manifests.Single().LastWriteUtc.Should().Be(newerTime);
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(1.999, false)]
    [InlineData(2.0, false)]
    [InlineData(2.001, true)]
    [InlineData(3.0, true)]
    public void IsStoredNewer_respects_dos_two_second_granularity(double leadSeconds, bool expected)
    {
        DateTime local = T0;
        DateTime stored = T0.AddSeconds(leadSeconds);

        ManifestBackupPolicy.IsStoredNewer(stored, local).Should().Be(expected);
    }

    [Fact]
    public void AntiDowngrade_within_two_second_dos_tolerance_does_not_block_or_falsely_downgrade()
    {
        DateTime storedTime = T0.AddSeconds(1.999);
        var stored = new[] { new BackedUpManifest(386941, "222", "abc", storedTime) };
        var localWithinTolerance = new[] { M(386941, "333", T0) };

        var result = ManifestBackupPolicy.Reconcile(
            [386941],
            new Dictionary<long, string>(),
            new Dictionary<long, string>(),
            localWithinTolerance,
            stored,
            allowAntiDowngrade: true);

        result.PreventedDowngrade.Should().BeFalse();
        result.Selected.Select(m => m.ManifestId).Should().Equal("333");
        result.Carried.Should().BeEmpty();
    }

    [Fact]
    public void AntiDowngrade_active_setManifestid_pin_is_respected_even_when_pinned_manifest_is_older()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        string newer = WriteManifest(386941, "222", T0.AddDays(5));
        BackupAll();

        // User explicitly freezes the build to older manifest 111 via active setManifestid.
        File.Delete(newer);
        WriteManifest(386941, "111", T0);
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\nsetManifestid(386941,\"111\")");

        var outcome = BackupAll().Games.Single();
        outcome.Change.Should().Be(ManifestBackupChange.Updated);
        ZipManifestNames().Should().Equal("386941_111.manifest");
    }

    [Fact]
    public void AntiDowngrade_corrupt_stored_zip_does_not_block_local_manifest()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        string newer = WriteManifest(386941, "222", T0.AddDays(5));
        BackupAll();

        // Corrupt the stored zip without changing length so SHA-256 fails fail-closed.
        byte[] tampered = File.ReadAllBytes(ZipPath);
        tampered[^1] ^= 0xFF;
        File.WriteAllBytes(ZipPath, tampered);

        File.Delete(newer);
        WriteManifest(386941, "111", T0);

        var outcome = BackupAll().Games.Single();
        outcome.Change.Should().Be(ManifestBackupChange.Updated);
        ZipManifestNames().Should().Equal("386941_111.manifest");
        using var restored = ManifestBackupStore.StageVerifiedRestore(AppId, _dest, Staging)?.Pin;
        restored.Should().NotBeNull();
    }

    [Fact]
    public void AntiDowngrade_legacy_metadata_without_timestamp_protects_against_older_local_manifest()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        string newer = WriteManifest(386941, "222", T0.AddDays(5));
        BackupAll();

        // Rewrite metadata.json in legacy format (omitting lastWriteUtc on manifest entries).
        var full = ManifestBackupStore.ReadMetadata(AppDir, AppId)!;
        string legacyJson = JsonSerializer.Serialize(new
        {
            schema = 1,
            appId = full.AppId,
            name = full.Name,
            updatedAtUtc = full.UpdatedAtUtc,
            luaSha256 = full.LuaSha256,
            zipSha256 = full.ZipSha256,
            manifests = full.Manifests.Select(m => new { depotId = m.DepotId, manifestId = m.ManifestId, sha256 = m.Sha256 }),
            zipSizeBytes = full.ZipSizeBytes,
            manifestCount = full.ManifestCount,
            hasManifests = full.HasManifests,
        });
        File.WriteAllText(Path.Combine(AppDir, ManifestBackupStore.MetadataFileName), legacyJson);

        File.Delete(newer);
        WriteManifest(386941, "111", T0);

        var outcome = BackupAll().Games.Single();
        outcome.Change.Should().Be(ManifestBackupChange.Unchanged);
        ZipManifestNames().Should().Equal("386941_222.manifest");
    }

    [Fact]
    public void AntiDowngrade_true_L3_metadata_upgrades_once_to_L4_without_losing_newer_manifest()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        string newer = WriteManifest(386941, "222", T0.AddDays(5));
        BackupAll();

        // True legacy L3 metadata: omits lastWriteUtc, zipSizeBytes, manifestCount and hasManifests.
        var full = ManifestBackupStore.ReadMetadata(AppDir, AppId)!;
        string legacyL3Json = JsonSerializer.Serialize(new
        {
            schema = 1,
            appId = full.AppId,
            name = full.Name,
            updatedAtUtc = full.UpdatedAtUtc,
            luaSha256 = full.LuaSha256,
            zipSha256 = full.ZipSha256,
            manifests = full.Manifests.Select(m => new { depotId = m.DepotId, manifestId = m.ManifestId, sha256 = m.Sha256 }),
        });
        File.WriteAllText(Path.Combine(AppDir, ManifestBackupStore.MetadataFileName), legacyL3Json);

        File.Delete(newer);
        WriteManifest(386941, "111", T0);

        // First pass preserves 222 and upgrades metadata.json to L4 once.
        var first = BackupAll().Games.Single();
        first.Change.Should().Be(ManifestBackupChange.Updated);
        ZipManifestNames().Should().Equal("386941_222.manifest");
        var upgraded = ManifestBackupStore.ReadMetadata(AppDir, AppId)!;
        upgraded.ZipSizeBytes.Should().BeGreaterThan(0);
        upgraded.Manifests.Single().LastWriteUtc.Should().NotBeNull();

        // Subsequent pass skips with Unchanged and does not re-open the zip.
        var second = BackupAll().Games.Single();
        second.Change.Should().Be(ManifestBackupChange.Unchanged);
        ZipManifestNames().Should().Equal("386941_222.manifest");
    }

    [Fact]
    public void AntiDowngrade_locked_stored_zip_fails_pass_safely_without_downgrade_overwrite()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        string newer = WriteManifest(386941, "222", T0.AddDays(5));
        BackupAll();
        byte[] before = File.ReadAllBytes(ZipPath);

        File.Delete(newer);
        WriteManifest(386941, "111", T0);

        using (new FileStream(ZipPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var outcome = BackupAll().Games.Single();
            outcome.Failed.Should().BeTrue();
        }

        File.ReadAllBytes(ZipPath).Should().Equal(before);
        ManifestBackupStore.ReadMetadata(AppDir, AppId)!.Manifests.Single().ManifestId.Should().Be("222");
    }

    [Fact]
    public void AntiDowngrade_mixed_depots_updates_newer_depot_and_preserves_older_depot()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\naddappid(386942,1,\"k\")");
        string d1Initial = WriteManifest(386941, "200", T0.AddDays(5));
        string d2Initial = WriteManifest(386942, "300", T0);
        BackupAll();

        // Depot 386941 regresses locally (older); depot 386942 advances locally (newer).
        File.Delete(d1Initial);
        File.Delete(d2Initial);
        WriteManifest(386941, "100", T0);
        WriteManifest(386942, "400", T0.AddDays(5));

        var outcome = BackupAll().Games.Single();
        outcome.Change.Should().Be(ManifestBackupChange.Updated);
        outcome.ManifestCount.Should().Be(2);
        ZipManifestNames().Should().Equal("386941_200.manifest", "386942_400.manifest");
    }

    [Fact]
    public void AntiDowngrade_future_local_timestamp_is_clamped_to_now_so_updates_are_not_frozen()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        WriteManifest(386941, "111", new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        BackupAll();

        var storedTime = ManifestBackupStore.ReadMetadata(AppDir, AppId)!.Manifests.Single().LastWriteUtc;
        storedTime.Should().NotBeNull();
        storedTime!.Value.Should().BeOnOrBefore(DateTime.UtcNow.AddSeconds(2));
    }

    [Fact]
    public void IsStoredNewer_normalizes_local_datetime_kind_before_comparing()
    {
        DateTime utcLocalRef = T0;
        DateTime storedAsLocalKind = DateTime.SpecifyKind(T0.AddHours(1), DateTimeKind.Utc).ToLocalTime();

        ManifestBackupPolicy.IsStoredNewer(storedAsLocalKind, utcLocalRef).Should().BeTrue();
        ManifestBackupPolicy.IsStoredNewer(DateTime.SpecifyKind(T0, DateTimeKind.Utc).ToLocalTime(), utcLocalRef).Should().BeFalse();
    }

    // ── 1.8.9: cancel, busy probe, creation_time anti-downgrade ─────────────────

    private ManifestBackupViewModel ConfiguredViewModel(out ManifestBackupService service, int games)
    {
        var (settings, svc) = CreateService();
        settings.ManifestBackupMode = ManifestBackupMode.LocalFolder;
        settings.ManifestBackupFolder = _dest;
        for (int i = 1; i <= games; i++)
            File.WriteAllText(Path.Combine(StPlugIn, $"{10000 + i}.lua"), $"addappid({10000 + i})");
        service = svc;
        return new ManifestBackupViewModel(settings, svc);
    }

    private int CommittedGames() =>
        Directory.Exists(_dest)
            ? Directory.EnumerateFiles(_dest, ManifestBackupStore.MetadataFileName, SearchOption.AllDirectories).Count()
            : 0;

    [Fact]
    public async Task Cancelling_the_batch_stops_cleanly_keeps_index_in_sync_and_frees_the_buttons()
    {
        var vm = ConfiguredViewModel(out _, games: 150);

        var run = vm.BackupAllCommand.ExecuteAsync(null);
        vm.IsRunning.Should().BeTrue();
        vm.CancelRunCommand.CanExecute(null).Should().BeTrue();
        vm.CancelRunCommand.Execute(null);
        await run;

        vm.Status.Should().Be(Resources.Strings.Settings_ManifestBackup_Cancelled);
        vm.IsRunning.Should().BeFalse();
        vm.CancelRunCommand.CanExecute(null).Should().BeFalse();
        vm.BackupAllCommand.CanExecute(null).Should().BeTrue();

        int committed = CommittedGames();
        committed.Should().BeLessThan(150, "the pass stopped early");
        (ManifestBackupStore.ReadIndex(_dest)?.Games.Count ?? 0).Should().Be(committed, "finally re-syncs index.json");
        Directory.EnumerateFiles(_dir, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelling_verify_reports_cancelled_not_an_error_and_changes_nothing()
    {
        var vm = ConfiguredViewModel(out _, games: 150);
        await vm.BackupAllCommand.ExecuteAsync(null);
        var before = Directory.EnumerateFiles(_dest, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));

        var run = vm.VerifyCommand.ExecuteAsync(null);
        vm.CancelRunCommand.Execute(null);
        await run;

        vm.Status.Should().Be(Resources.Strings.Settings_ManifestBackup_Cancelled);
        vm.IsRunning.Should().BeFalse();
        vm.VerifyCommand.CanExecute(null).Should().BeTrue();
        Directory.EnumerateFiles(_dest, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)))
            .Should().BeEquivalentTo(before, "verify is read-only, cancelled or not");
    }

    [Fact]
    public async Task The_service_reports_a_game_busy_from_install_until_its_backup_lands()
    {
        var vm = ConfiguredViewModel(out var service, games: 0);
        WriteLua("addappid(386940)");

        service.IsBusyWith(AppId).Should().BeFalse();
        service.OnInstalled(AppId);
        service.IsBusyWith(AppId).Should().BeTrue("marked before the background hop");
        service.IsBusyWith(620).Should().BeFalse();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (service.IsBusyWith(AppId) && DateTime.UtcNow < deadline) await Task.Delay(25);
        service.IsBusyWith(AppId).Should().BeFalse();
        vm.IsRunning.Should().BeFalse();
    }

    private static readonly DateTime C0 = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc); // Steam creation_time base

    [Fact]
    public void Anti_downgrade_trusts_creation_time_over_a_clock_that_ran_ahead()
    {
        // PC A's clock was a year ahead when it backed up build 111; PC B now has the real newer build 222.
        var stored = new BackedUpManifest(1, "111", "sha", LastWriteUtc: T0.AddYears(1), CreationTimeUtc: C0);
        var local = new DepotManifest(1, "222", @"C:\x\1_222.manifest", T0, CreationTimeUtc: C0.AddDays(3));
        var empty = new Dictionary<long, string>();

        var result = ManifestBackupPolicy.Reconcile([1], empty, empty, [local], [stored]);

        result.PreventedDowngrade.Should().BeFalse();
        result.Selected.Should().ContainSingle(m => m.ManifestId == "222");
        ManifestBackupPolicy.IsStoredNewer(stored, local).Should().BeFalse();
    }

    [Fact]
    public void Anti_downgrade_still_holds_when_creation_time_says_the_backup_is_newer()
    {
        var stored = new BackedUpManifest(1, "111", "sha", LastWriteUtc: T0, CreationTimeUtc: C0.AddDays(5));
        var local = new DepotManifest(1, "222", @"C:\x\1_222.manifest", T0.AddDays(9), CreationTimeUtc: C0);
        var empty = new Dictionary<long, string>();

        var result = ManifestBackupPolicy.Reconcile([1], empty, empty, [local], [stored]);

        result.PreventedDowngrade.Should().BeTrue("Steam built 111 later, whatever the write times say");
        result.Carried.Should().ContainSingle(m => m.ManifestId == "111");
    }

    [Fact]
    public void Legacy_entries_without_creation_time_fall_back_to_write_times()
    {
        var empty = new Dictionary<long, string>();
        var local = new DepotManifest(1, "222", @"C:\x\1_222.manifest", T0, CreationTimeUtc: C0.AddDays(3));
        var legacyNewer = new BackedUpManifest(1, "111", "sha", LastWriteUtc: T0.AddDays(1));
        var legacyOlder = new BackedUpManifest(1, "111", "sha", LastWriteUtc: T0.AddDays(-1));

        ManifestBackupPolicy.Reconcile([1], empty, empty, [local], [legacyNewer]).PreventedDowngrade.Should().BeTrue();
        ManifestBackupPolicy.Reconcile([1], empty, empty, [local], [legacyOlder]).PreventedDowngrade.Should().BeFalse();
        ManifestBackupPolicy.IsStoredNewer(legacyNewer, local with { CreationTimeUtc = null }).Should().BeTrue();
    }

    [Fact]
    public void The_newest_local_manifest_is_chosen_by_creation_time_when_present()
    {
        var olderBuildTouchedLater = new DepotManifest(1, "999", @"C:\x\1_999.manifest", T0.AddDays(5), C0);
        var newerBuild = new DepotManifest(1, "111", @"C:\x\1_111.manifest", T0, C0.AddDays(1));

        ManifestBackupPolicy.SelectManifests([1], new Dictionary<long, string>(), [olderBuildTouchedLater, newerBuild])
            .Should().ContainSingle().Which.ManifestId.Should().Be("111");
    }

    [Fact]
    public void A_group_missing_any_creation_time_is_ranked_by_write_time_only()
    {
        // 999 lacks creation_time and was written later; ranking it by write time against 111's (earlier)
        // creation time would be comparing two clocks. With a mixed group, write time decides for both.
        var noCreation = new DepotManifest(1, "999", @"C:\x\1_999.manifest", T0.AddDays(2));
        var withCreation = new DepotManifest(1, "111", @"C:\x\1_111.manifest", T0.AddDays(1), T0.AddYears(1)); // a creation time far past 999's write time
        var empty = new Dictionary<long, string>();

        ManifestBackupPolicy.SelectManifests([1], empty, [withCreation, noCreation])
            .Should().ContainSingle().Which.ManifestId.Should().Be("999");

        // Stored side, same rule: a legacy entry without creation_time makes the group compare write times.
        var legacy = new BackedUpManifest(1, "555", "sha", LastWriteUtc: T0.AddDays(10));
        var modern = new BackedUpManifest(1, "444", "sha", LastWriteUtc: T0, CreationTimeUtc: C0.AddYears(5));
        var local = new DepotManifest(1, "222", @"C:\x\1_222.manifest", T0.AddDays(5));
        var result = ManifestBackupPolicy.Reconcile([1], empty, empty, [local], [legacy, modern]);
        result.PreventedDowngrade.Should().BeTrue();
        result.Carried.Should().ContainSingle(m => m.ManifestId == "555");
    }

    [Fact]
    public void A_carried_legacy_entry_gains_its_creation_time_on_the_next_rewrite()
    {
        WriteLua("addappid(386940)\naddappid(386941, 1, \"k\")\naddappid(386942, 1, \"k\")");
        ulong created = (ulong)(C0 - DateTime.UnixEpoch).TotalSeconds;
        File.WriteAllBytes(Path.Combine(DepotCache, "386941_501.manifest"),
            TestManifests.Build(386941, 501, firstFilename: "a", creationTime: created));
        string carriedPath = Path.Combine(DepotCache, "386942_601.manifest");
        File.WriteAllBytes(carriedPath, TestManifests.Build(386942, 601, firstFilename: "b", creationTime: created + 60));
        BackupAll();

        // Make it a pre-1.8.9 vault, then lose 386942 locally and move 386941 on: the rewrite carries 601.
        string metaPath = Path.Combine(AppDir, ManifestBackupStore.MetadataFileName);
        File.WriteAllText(metaPath, File.ReadAllText(metaPath)
            .Replace("\"creationTimeUtc\"", "\"unusedLegacyField\"", StringComparison.Ordinal));
        File.Delete(carriedPath);
        File.Delete(Path.Combine(DepotCache, "386941_501.manifest"));
        File.WriteAllBytes(Path.Combine(DepotCache, "386941_502.manifest"),
            TestManifests.Build(386941, 502, firstFilename: "c", creationTime: created + 120));

        BackupAll().Games.Single().Change.Should().Be(ManifestBackupChange.Updated);

        var carried = ManifestBackupStore.ReadMetadata(AppDir, AppId)!.Manifests.Single(m => m.ManifestId == "601");
        carried.CreationTimeUtc.Should().Be(C0.AddSeconds(60));
    }

    [Fact]
    public void The_batch_records_each_manifests_creation_time_and_legacy_metadata_still_reads()
    {
        WriteLua("addappid(386940)\naddappid(386941, 1, \"k\")");
        string path = Path.Combine(DepotCache, "386941_555.manifest");
        ulong created = (ulong)(C0 - DateTime.UnixEpoch).TotalSeconds;
        File.WriteAllBytes(path, TestManifests.Build(386941, 555, firstFilename: "f555", creationTime: created));

        BackupAll();

        var meta = ManifestBackupStore.ReadMetadata(AppDir, AppId);
        meta!.Manifests.Should().ContainSingle().Which.CreationTimeUtc.Should().Be(C0);
        File.ReadAllText(Path.Combine(AppDir, ManifestBackupStore.MetadataFileName)).Should().Contain("\"creationTimeUtc\"");
        meta.Schema.Should().Be(ManifestBackupMetadata.CurrentSchema);

        // A pre-1.8.9 metadata.json has no such field: it still parses, with null.
        string legacy = File.ReadAllText(Path.Combine(AppDir, ManifestBackupStore.MetadataFileName))
            .Replace("\"creationTimeUtc\"", "\"unusedLegacyField\"", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(AppDir, ManifestBackupStore.MetadataFileName), legacy);
        ManifestBackupStore.ReadMetadata(AppDir, AppId)!.Manifests.Single().CreationTimeUtc.Should().BeNull();
    }
}
