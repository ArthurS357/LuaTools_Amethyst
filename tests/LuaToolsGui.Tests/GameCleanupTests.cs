using System.Globalization;
using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers the unified game cleanup / uninstall policy and I/O adapter across Manage and Depots,
/// including all four fail-closed safeguards and preservation of the Manifest Backup vault.
/// </summary>
public sealed class GameCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "luatools-cleanup-tests", Guid.NewGuid().ToString("N"));

    private readonly string _steamRoot;
    private readonly string _plugDir;
    private readonly string _depotDir;
    private readonly string _commonDir;
    private readonly string _secondLibRoot;
    private readonly string _secondCommonDir;
    private readonly string _vaultDir;
    private readonly string _cacheDir;
    private readonly string _settingsDir;
    private readonly string _backupDir;

    private readonly SettingsService _settings;
    private readonly SteamService _steam;
    private readonly SteamLibraryService _library;
    private readonly LuaVault _vault;
    private readonly CacheService _cache;

    public GameCleanupTests()
    {
        _steamRoot = Path.Combine(_root, "Steam");
        _plugDir = Path.Combine(_steamRoot, "config", "stplug-in");
        _depotDir = Path.Combine(_steamRoot, "depotcache");
        _commonDir = Path.Combine(_steamRoot, "steamapps", "common");
        _secondLibRoot = Path.Combine(_root, "SteamLibrary2");
        _secondCommonDir = Path.Combine(_secondLibRoot, "steamapps", "common");
        _vaultDir = Path.Combine(_root, "LuaVault");
        _cacheDir = Path.Combine(_root, "Cache");
        _settingsDir = Path.Combine(_root, "Settings");
        _backupDir = Path.Combine(_root, "BackupManifestVault");

        Directory.CreateDirectory(_plugDir);
        Directory.CreateDirectory(_depotDir);
        Directory.CreateDirectory(_commonDir);
        Directory.CreateDirectory(_secondCommonDir);
        Directory.CreateDirectory(_vaultDir);
        Directory.CreateDirectory(_cacheDir);
        Directory.CreateDirectory(_settingsDir);
        Directory.CreateDirectory(_backupDir);

        _settings = new SettingsService(_settingsDir)
        {
            SteamPathOverride = _steamRoot,
            ManifestBackupFolder = _backupDir,
        };
        _steam = new SteamService(_settings);
        _library = new SteamLibraryService(_steam);
        _vault = new LuaVault(() => _steam.StPlugInDir, _vaultDir);
        _cache = new CacheService(_cacheDir);
    }

    public void Dispose()
    {
        DeleteTreeHandlingJunctions(_root);
        GC.SuppressFinalize(this);
    }

    private static void DeleteTreeHandlingJunctions(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (string sub in Directory.GetDirectories(dir))
        {
            if (DirectoryJunction.Exists(sub)) DirectoryJunction.Remove(sub);
            else DeleteTreeHandlingJunctions(sub);
        }
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    private GameCleanupService NewService(
        Func<int?>? runningAppId = null,
        Func<bool>? isSteamRunning = null,
        Func<string, bool>? isLink = null) =>
        new(
            () => _steam.StPlugInDir,
            () => _steam.DepotCacheDir,
            _library,
            _vault,
            _cache,
            () => _settings.ManifestBackupFolder,
            runningAppId ?? (() => null),
            isSteamRunning ?? (() => false),
            isLink);

    private string WriteLua(long appId, string text)
    {
        string path = Path.Combine(_plugDir, $"{appId}.lua");
        File.WriteAllText(path, text);
        return path;
    }

    private string WriteManifest(long depotId, long manifestId, int size = 32)
    {
        string path = Path.Combine(_depotDir, $"{depotId}_{manifestId}.manifest");
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private string WriteAcf(long appId, string installDir, long sizeOnDisk, long[] depotIds, string? libraryRoot = null)
    {
        string root = libraryRoot ?? _steamRoot;
        string steamApps = Path.Combine(root, "steamapps");
        Directory.CreateDirectory(steamApps);
        string acfPath = Path.Combine(steamApps, $"appmanifest_{appId}.acf");

        string depotsBlock = string.Join(
            Environment.NewLine,
            depotIds.Select(d => $"\t\t\"{d}\"\n\t\t{{\n\t\t\t\"manifest\"\t\t\"123456789\"\n\t\t\t\"size\"\t\t\"100\"\n\t\t}}"));

        string escapedDir = installDir.Replace(@"\", @"\\");
        string content =
            $"\"AppState\"\n{{\n" +
            $"\t\"appid\"\t\t\"{appId}\"\n" +
            $"\t\"name\"\t\t\"Game {appId}\"\n" +
            $"\t\"StateFlags\"\t\t\"4\"\n" +
            $"\t\"installdir\"\t\t\"{escapedDir}\"\n" +
            $"\t\"SizeOnDisk\"\t\t\"{sizeOnDisk}\"\n" +
            $"\t\"InstalledDepots\"\n\t{{\n{depotsBlock}\n\t}}\n" +
            $"}}";

        File.WriteAllText(acfPath, content);
        return acfPath;
    }

    private void WriteLibraryFoldersVdf(params string[] roots)
    {
        string steamApps = Path.Combine(_steamRoot, "steamapps");
        Directory.CreateDirectory(steamApps);
        var entries = roots.Select((r, idx) =>
            $"\t\"{idx}\"\n\t{{\n\t\t\"path\"\t\t\"{r.Replace(@"\", @"\\")}\"\n\t}}");
        string vdf = "\"libraryfolders\"\n{\n" + string.Join("\n", entries) + "\n}";
        File.WriteAllText(Path.Combine(steamApps, "libraryfolders.vdf"), vdf);
    }

    private string WriteGamePayload(string installDir, string relativeFile, int bytes, string? commonOverride = null)
    {
        string common = commonOverride ?? _commonDir;
        string file = Path.Combine(common, installDir, relativeFile);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[bytes]);
        return file;
    }

    private (string ZipPath, byte[] Bytes) SeedBackupVault(string name, byte[] content)
    {
        string path = Path.Combine(_backupDir, name);
        File.WriteAllBytes(path, content);
        return (path, content);
    }

    // ── Pure Policy Tests ─────────────────────────────────────────────────────

    [Fact]
    public void ValidateInstallDirectory_AcceptsSafeChildAndNestedRelativePath()
    {
        var single = GameCleanupPolicy.ValidateInstallDirectory(_commonDir, "Portal 2", _ => false);
        var nested = GameCleanupPolicy.ValidateInstallDirectory(_commonDir, @"SubFolder\Portal 2", _ => false);

        single.IsValid.Should().BeTrue();
        single.CanonicalPath.Should().Be(Path.GetFullPath(Path.Combine(_commonDir, "Portal 2")));
        nested.IsValid.Should().BeTrue();
        nested.CanonicalPath.Should().Be(Path.GetFullPath(Path.Combine(_commonDir, "SubFolder", "Portal 2")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"..\Escape")]
    [InlineData(@"Game\..\Escape")]
    [InlineData(@"Game\.")]
    [InlineData(@"C:\Games\Escape")]
    [InlineData(@"\Rooted")]
    [InlineData(@"D:DriveRelative")]
    [InlineData(@"\\server\share\Game")]
    [InlineData(@"Game\Sub\.. ")]
    [InlineData("Game.")]
    [InlineData("Game ")]
    [InlineData("...")]
    public void ValidateInstallDirectory_RejectsUnsafeTraversalAndRootedPaths(string rawInstallDir)
    {
        var check = GameCleanupPolicy.ValidateInstallDirectory(_commonDir, rawInstallDir, _ => false);

        check.IsValid.Should().BeFalse();
        check.BlockReason.Should().Be(GameCleanupBlockReason.UnsafeInstallPath);
        check.CanonicalPath.Should().BeNull();
    }

    [Fact]
    public void ValidateInstallDirectory_RejectsDriveRootAsCommonDir_AndRejectsSymlinks()
    {
        GameCleanupPolicy.ValidateInstallDirectory(@"C:\", "Game", _ => false)
            .BlockReason.Should().Be(GameCleanupBlockReason.UnsafeInstallPath);

        GameCleanupPolicy.ValidateInstallDirectory(
            _commonDir,
            @"Parent\Game",
            p => string.Equals(Path.GetFileName(p), "Parent", StringComparison.OrdinalIgnoreCase))
            .BlockReason.Should().Be(GameCleanupBlockReason.SymlinkDetected);

        GameCleanupPolicy.ValidateInstallDirectory(
            _commonDir,
            "Game",
            p => string.Equals(Path.GetFileName(p), "common", StringComparison.OrdinalIgnoreCase))
            .BlockReason.Should().Be(GameCleanupBlockReason.SymlinkDetected);
    }

    [Fact]
    public void IsOverlappingPath_DetectsContainment_WithoutFalsePositivesOnSiblingPrefixes()
    {
        string backup = @"G:\Meu Drive\BackupManifest";

        GameCleanupPolicy.IsOverlappingPath(backup, backup).Should().BeTrue();
        GameCleanupPolicy.IsOverlappingPath(backup + @"\", backup).Should().BeTrue();
        GameCleanupPolicy.IsOverlappingPath(Path.Combine(backup, "400.zip"), backup).Should().BeTrue();
        GameCleanupPolicy.IsOverlappingPath(@"G:\Meu Drive", backup).Should().BeTrue();
        GameCleanupPolicy.IsOverlappingPath(@"G:\Meu Drive\BackupManifestOther", backup).Should().BeFalse();
    }

    [Fact]
    public void ParseAcfDepotIds_AndSizeOnDisk_ReadMountedDepotsAndIgnoreComments()
    {
        const string acf = """
            // header comment
            "AppState"
            {
                "appid" "400"
                "SizeOnDisk" "1073741824"
                "InstalledDepots"
                {
                    "401" { "manifest" "111" }
                    "228988" { "manifest" "222" }
                }
                "SharedDepots"
                {
                    "229006" "228980"
                }
            }
            """;

        GameCleanupPolicy.ParseAcfSizeOnDisk(acf).Should().Be(1073741824L);
        GameCleanupPolicy.ParseAcfDepotIds(acf).Should().Equal(401L, 228988L, 229006L);
    }

    // ── All 5 Scopes + Backup Vault Isolation ─────────────────────────────────

    [Fact]
    public async Task CleanAsync_LuaOnly_DeletesActiveAndDisabledLua_KeepsManifestsGameAndBackupVault()
    {
        string lua = WriteLua(400, "addappid(400)\naddappid(401, 1, \"key\")");
        string disabledLua = lua + ".disabled";
        File.WriteAllText(disabledLua, "-- disabled");
        string manifest = WriteManifest(401, 9001, 64);
        string acf = WriteAcf(400, "Portal", 128, [401]);
        string gameFile = WriteGamePayload("Portal", "portal.exe", 128);
        var (backupZip, backupBytes) = SeedBackupVault("400_Portal.zip", [1, 2, 3, 4]);

        var result = await NewService().CleanAsync(400, GameCleanupScope.LuaOnly);

        result.Succeeded.Should().BeTrue();
        result.LuaDeleted.Should().BeTrue();
        result.ManifestsDeleted.Should().Be(0);
        result.GameFilesDeleted.Should().BeFalse();
        result.AcfDeleted.Should().BeFalse();
        File.Exists(lua).Should().BeFalse();
        File.Exists(disabledLua).Should().BeFalse();
        File.Exists(manifest).Should().BeTrue();
        File.Exists(acf).Should().BeTrue();
        File.Exists(gameFile).Should().BeTrue();
        File.ReadAllBytes(backupZip).Should().Equal(backupBytes);
    }

    [Fact]
    public async Task CleanAsync_ManifestsOnly_DeletesExclusiveManifests_KeepsLuaGameAndBackupVault()
    {
        string lua = WriteLua(400, "addappid(400)\naddappid(401, 1, \"key\")\nsetManifestid(401, \"9001\", 0)");
        string manifest = WriteManifest(401, 9001, 48);
        string acf = WriteAcf(400, "Portal", 128, [401]);
        string gameFile = WriteGamePayload("Portal", "portal.exe", 128);
        var (backupZip, backupBytes) = SeedBackupVault("400_Portal.zip", [9, 8, 7]);

        var result = await NewService().CleanAsync(400, GameCleanupScope.ManifestsOnly);

        result.Succeeded.Should().BeTrue();
        result.LuaDeleted.Should().BeFalse();
        result.ManifestsDeleted.Should().Be(1);
        result.FreedBytes.Should().Be(48);
        File.Exists(lua).Should().BeTrue();
        File.Exists(manifest).Should().BeFalse();
        File.Exists(acf).Should().BeTrue();
        File.Exists(gameFile).Should().BeTrue();
        File.ReadAllBytes(backupZip).Should().Equal(backupBytes);
    }

    [Fact]
    public async Task CleanAsync_LuaAndManifests_KeepsInstalledGameAndBackupVault()
    {
        string lua = WriteLua(400, "addappid(400)\naddappid(401, 1, \"key\")");
        string manifest = WriteManifest(401, 9001, 40);
        string acf = WriteAcf(400, "Portal", 128, [401]);
        string gameFile = WriteGamePayload("Portal", "portal.exe", 128);
        var (backupZip, backupBytes) = SeedBackupVault("400_Portal.zip", [5, 5, 5]);

        var result = await NewService().CleanAsync(400, GameCleanupScope.LuaAndManifests);

        result.Succeeded.Should().BeTrue();
        result.LuaDeleted.Should().BeTrue();
        result.ManifestsDeleted.Should().Be(1);
        result.GameFilesDeleted.Should().BeFalse();
        result.AcfDeleted.Should().BeFalse();
        File.Exists(lua).Should().BeFalse();
        File.Exists(manifest).Should().BeFalse();
        File.Exists(acf).Should().BeTrue();
        File.Exists(gameFile).Should().BeTrue();
        File.ReadAllBytes(backupZip).Should().Equal(backupBytes);
    }

    [Fact]
    public async Task CleanAsync_GameFilesOnly_DeletesGameFolderAndAcf_KeepsLuaManifestsAndBackupVault()
    {
        string lua = WriteLua(400, "addappid(400)\naddappid(401, 1, \"key\")");
        string manifest = WriteManifest(401, 9001, 40);
        string acf = WriteAcf(400, "Portal", 256, [401]);
        string gameFile = WriteGamePayload("Portal", "bin\\client.dll", 256);
        var (backupZip, backupBytes) = SeedBackupVault("400_Portal.zip", [7, 7, 7]);

        var result = await NewService().CleanAsync(400, GameCleanupScope.GameFilesOnly);

        result.Succeeded.Should().BeTrue();
        result.GameFilesDeleted.Should().BeTrue();
        result.AcfDeleted.Should().BeTrue();
        result.LuaDeleted.Should().BeFalse();
        result.ManifestsDeleted.Should().Be(0);
        result.FreedBytes.Should().BeGreaterThanOrEqualTo(256);
        Directory.Exists(Path.Combine(_commonDir, "Portal")).Should().BeFalse();
        File.Exists(gameFile).Should().BeFalse();
        File.Exists(acf).Should().BeFalse();
        File.Exists(lua).Should().BeTrue();
        File.Exists(manifest).Should().BeTrue();
        File.ReadAllBytes(backupZip).Should().Equal(backupBytes);
    }

    [Fact]
    public async Task CleanAsync_Everything_DeletesGameAcfLuaAndExclusiveManifests_KeepsSharedAndBackupVault()
    {
        string lua = WriteLua(400, "addappid(400)\naddappid(401, 1, \"k\")\naddappid(228988, 1, \"s\")");
        WriteLua(620, "addappid(620)\naddappid(621, 1, \"k2\")\naddappid(228988, 1, \"s\")");
        string exclusiveManifest = WriteManifest(401, 9001, 40);
        string sharedManifest = WriteManifest(228988, 888, 32);
        string acf = WriteAcf(400, "Portal", 200, [401, 228988]);
        WriteGamePayload("Portal", "portal.exe", 200);
        var (backupZip, backupBytes) = SeedBackupVault("400_Portal.zip", [11, 22, 33]);

        var result = await NewService().CleanAsync(400, GameCleanupScope.Everything);

        result.Succeeded.Should().BeTrue();
        result.GameFilesDeleted.Should().BeTrue();
        result.AcfDeleted.Should().BeTrue();
        result.LuaDeleted.Should().BeTrue();
        result.ManifestsDeleted.Should().Be(1);
        result.SharedManifestsPreserved.Should().Be(1);
        Directory.Exists(Path.Combine(_commonDir, "Portal")).Should().BeFalse();
        File.Exists(acf).Should().BeFalse();
        File.Exists(lua).Should().BeFalse();
        File.Exists(exclusiveManifest).Should().BeFalse();
        File.Exists(sharedManifest).Should().BeTrue();
        File.ReadAllBytes(backupZip).Should().Equal(backupBytes);
    }

    // ── Safeguard 1: Shared Depot Protection ──────────────────────────────────

    [Fact]
    public async Task Safeguard1_PreservesSharedManifestsUsedBySiblingLua_VaultBuild_AndOtherAcf()
    {
        WriteLua(400, "addappid(400)\naddappid(401, 1, \"k\")\naddappid(228988, 1, \"s1\")\naddappid(229006, 1, \"s2\")\naddappid(228989, 1, \"s3\")");
        // Shared across: sibling active lua (228988), another game's LuaVault build (229006), another game's ACF (228989).
        WriteLua(620, "addappid(620)\naddappid(228988, 1, \"s1\")");
        _vault.SaveText(730, "addappid(730)\naddappid(229006, 1, \"s2\")", "StoredCS");
        WriteAcf(500, "OtherGame", 100, [228989]);

        string exclusiveManifest = WriteManifest(401, 1001, 16);
        string sharedLuaManifest = WriteManifest(228988, 1002, 16);
        string sharedVaultManifest = WriteManifest(229006, 1003, 16);
        string sharedAcfManifest = WriteManifest(228989, 1004, 16);

        var result = await NewService().CleanAsync(400, GameCleanupScope.ManifestsOnly);

        result.Succeeded.Should().BeTrue();
        result.ManifestsDeleted.Should().Be(1);
        result.SharedManifestsPreserved.Should().Be(3);
        File.Exists(exclusiveManifest).Should().BeFalse();
        File.Exists(sharedLuaManifest).Should().BeTrue();
        File.Exists(sharedVaultManifest).Should().BeTrue();
        File.Exists(sharedAcfManifest).Should().BeTrue();
    }

    [Fact]
    public async Task Safeguard1_UnreadableSiblingLua_FailsClosed_WithoutDeletingAnyManifest()
    {
        WriteLua(400, "addappid(400)\naddappid(401, 1, \"k\")");
        string lockedSibling = WriteLua(620, "addappid(620)\naddappid(228988, 1, \"s\")");
        string targetManifest = WriteManifest(401, 1001, 24);

        using var lockStream = new FileStream(lockedSibling, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await NewService().CleanAsync(400, GameCleanupScope.ManifestsOnly);

        result.IsBlocked.Should().BeTrue();
        result.BlockReason.Should().Be(GameCleanupBlockReason.UnreadableSiblingLua);
        result.ManifestsDeleted.Should().Be(0);
        File.Exists(targetManifest).Should().BeTrue();
    }

    [Fact]
    public async Task Safeguard1_DisabledSiblingLua_StillProtectsItsSharedManifest()
    {
        WriteLua(400, "addappid(400)\naddappid(401, 1, \"k\")\naddappid(228988, 1, \"s\")");
        File.WriteAllText(Path.Combine(_plugDir, "620.lua.disabled"), "addappid(620)\naddappid(228988, 1, \"s\")");
        string exclusive = WriteManifest(401, 1001, 16);
        string shared = WriteManifest(228988, 1002, 16);

        var result = await NewService().CleanAsync(400, GameCleanupScope.ManifestsOnly);

        result.Succeeded.Should().BeTrue();
        File.Exists(exclusive).Should().BeFalse();
        File.Exists(shared).Should().BeTrue("re-enabling the disabled game must still find its manifest");
    }

    [Fact]
    public async Task Safeguard1_UnreadableSiblingAcf_FailsClosed_WithoutDeletingAnyManifest()
    {
        WriteLua(400, "addappid(400)\naddappid(401, 1, \"k\")");
        string otherAcf = WriteAcf(500, "OtherGame", 100, [401]);
        string manifest = WriteManifest(401, 1001, 16);

        using (new FileStream(otherAcf, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var blocked = await NewService().CleanAsync(400, GameCleanupScope.ManifestsOnly);

            blocked.IsBlocked.Should().BeTrue("an unreadable ACF may be the one claiming this depot");
            blocked.BlockReason.Should().Be(GameCleanupBlockReason.UnreadableSteamLibrary);
            File.Exists(manifest).Should().BeTrue();
        }

        // Readable again: the other game's ACF claims depot 401, so the manifest is shared and kept.
        var kept = await NewService().CleanAsync(400, GameCleanupScope.ManifestsOnly);
        kept.SharedManifestsPreserved.Should().Be(1);
        File.Exists(manifest).Should().BeTrue();
    }

    [Fact]
    public async Task Safeguard1_LibraryListedButNotOnDisk_FailsClosed()
    {
        // A disconnected drive hides that library's ACFs, so their depots can't be proven unshared.
        WriteLibraryFoldersVdf(_steamRoot, Path.Combine(_root, "UnpluggedDrive"));
        WriteLua(400, "addappid(400)\naddappid(401, 1, \"k\")");
        string manifest = WriteManifest(401, 1001, 16);

        var result = await NewService().CleanAsync(400, GameCleanupScope.ManifestsOnly);

        result.BlockReason.Should().Be(GameCleanupBlockReason.UnreadableSteamLibrary);
        File.Exists(manifest).Should().BeTrue();
    }

    [Fact]
    public async Task Safeguard1_CorruptVaultIndexOfAnotherGame_FailsClosed()
    {
        WriteLua(400, "addappid(400)\naddappid(401, 1, \"k\")");
        _vault.SaveText(730, "addappid(730)\naddappid(401, 1, \"k\")\nsetManifestid(401, \"1001\", 0)", "StoredCS");
        string index = Path.Combine(_vaultDir, "730", "index.json");
        File.WriteAllText(index, "{ torn");
        if (File.Exists(index + ".bak")) File.WriteAllText(index + ".bak", "{ torn");
        string manifest = WriteManifest(401, 1001, 16);

        var result = await NewService().CleanAsync(400, GameCleanupScope.ManifestsOnly);

        result.BlockReason.Should().Be(GameCleanupBlockReason.UnreadableSiblingLua,
            "the unreadable build may be the one pinning this manifest");
        File.Exists(manifest).Should().BeTrue();
    }

    [Fact]
    public async Task LuaOnly_AdoptsNeverOpenedLooseBuildFirst_SoDepotsCannotResurrectTheLua()
    {
        string lua = WriteLua(400, "addappid(400)");
        File.WriteAllText(Path.Combine(_plugDir, "400_12345.lua"), "addappid(400)\n-- build 12345");

        var result = await NewService().CleanAsync(400, GameCleanupScope.LuaOnly);

        result.LuaDeleted.Should().BeTrue();
        File.Exists(lua).Should().BeFalse();
        _vault.GetVariants(400).Should().ContainSingle(v => v.BuildId == "12345");
        // Opening the game in Depots only auto-applies a NEWLY adopted build — nothing is new any more.
        _vault.AdoptLooseBuildLuasAndReport(400).Should().BeFalse();
    }

    // ── Safeguard 2: No active .lua support ───────────────────────────────────

    [Fact]
    public async Task Safeguard2_GameWithoutActiveLua_ResolvesDepotsFromVaultAndAcf_AndDeletesExclusiveManifests()
    {
        // No 400.lua in stplug-in; only stored build in LuaVault + mounted depot in appmanifest.
        _vault.SaveText(400, "addappid(400)\naddappid(401, 1, \"k\")\nsetManifestid(401, \"777\", 0)", "OldBuild");
        WriteAcf(400, "Portal", 128, [402]);
        string vaultManifest = WriteManifest(401, 777, 20);
        string acfManifest = WriteManifest(402, 888, 20);

        var service = NewService();
        var availability = await service.InspectAsync(400);
        availability.HasActiveLua.Should().BeFalse();
        availability.HasStoredBuilds.Should().BeTrue();
        availability.HasInstalledGame.Should().BeTrue();

        var result = await service.CleanAsync(400, GameCleanupScope.ManifestsOnly);

        result.Succeeded.Should().BeTrue();
        result.ManifestsDeleted.Should().Be(2);
        File.Exists(vaultManifest).Should().BeFalse();
        File.Exists(acfManifest).Should().BeFalse();
    }

    // ── Safeguard 3: Safe Disk Uninstall ──────────────────────────────────────

    [Fact]
    public async Task Safeguard3_MultiLibrary_UninstallsFromSecondarySteamLibrary()
    {
        WriteLibraryFoldersVdf(_steamRoot, _secondLibRoot);
        string acf = WriteAcf(990, "LibTwoGame", 512, [991], _secondLibRoot);
        string payload = WriteGamePayload("LibTwoGame", "game.pak", 512, _secondCommonDir);

        var result = await NewService().CleanAsync(990, GameCleanupScope.GameFilesOnly);

        result.Succeeded.Should().BeTrue();
        result.GameFilesDeleted.Should().BeTrue();
        result.AcfDeleted.Should().BeTrue();
        File.Exists(payload).Should().BeFalse();
        File.Exists(acf).Should().BeFalse();
    }

    [Fact]
    public async Task Safeguard3_TraversalInInstallDir_FailsClosed_PreservesTargetOutsideCommon()
    {
        string victimDir = Path.Combine(_steamRoot, "steamapps", "VictimOutsideCommon");
        Directory.CreateDirectory(victimDir);
        string victimFile = Path.Combine(victimDir, "precious.dat");
        File.WriteAllText(victimFile, "do not delete");

        string acf = WriteAcf(400, @"..\VictimOutsideCommon", 100, [401]);

        var result = await NewService().CleanAsync(400, GameCleanupScope.GameFilesOnly);

        result.IsBlocked.Should().BeTrue();
        result.BlockReason.Should().Be(GameCleanupBlockReason.UnsafeInstallPath);
        File.Exists(victimFile).Should().BeTrue();
        File.Exists(acf).Should().BeTrue();
    }

    [Fact]
    public async Task Safeguard3_JunctionAsInstallDir_FailsClosed_PreservesTargetContents()
    {
        string targetOutside = Path.Combine(_root, "VictimTarget");
        Directory.CreateDirectory(targetOutside);
        string targetFile = Path.Combine(targetOutside, "important.txt");
        File.WriteAllText(targetFile, "untouched");

        string junctionPath = Path.Combine(_commonDir, "LinkedGame");
        DirectoryJunction.Create(junctionPath, targetOutside).Should().BeTrue();
        WriteAcf(400, "LinkedGame", 100, [401]);

        var result = await NewService().CleanAsync(400, GameCleanupScope.GameFilesOnly);

        result.IsBlocked.Should().BeTrue();
        result.BlockReason.Should().Be(GameCleanupBlockReason.SymlinkDetected);
        DirectoryJunction.Exists(junctionPath).Should().BeTrue();
        File.Exists(targetFile).Should().BeTrue();
    }

    [Fact]
    public async Task Safeguard3_NestedJunctionInsideInstallDir_SeversLinkWithoutDeletingTarget()
    {
        string externalTarget = Path.Combine(_root, "ExternalModFolder");
        Directory.CreateDirectory(externalTarget);
        string keepFile = Path.Combine(externalTarget, "keep.txt");
        File.WriteAllText(keepFile, "must survive");

        string gameFile = WriteGamePayload("NormalGame", "game.exe", 64);
        string nestedLink = Path.Combine(_commonDir, "NormalGame", "mods_link");
        DirectoryJunction.Create(nestedLink, externalTarget).Should().BeTrue();
        WriteAcf(400, "NormalGame", 64, [401]);

        var result = await NewService().CleanAsync(400, GameCleanupScope.GameFilesOnly);

        result.Succeeded.Should().BeTrue();
        result.GameFilesDeleted.Should().BeTrue();
        File.Exists(gameFile).Should().BeFalse();
        Directory.Exists(Path.Combine(_commonDir, "NormalGame")).Should().BeFalse();
        File.Exists(keepFile).Should().BeTrue();
    }

    [Theory]
    [InlineData("Half-Life 2")]            // same folder (HL2 / Episode One / Two / Lost Coast)
    [InlineData(@"Half-Life 2\episodic")]  // another app nested inside the target
    public async Task Safeguard3_InstallDirSharedWithAnotherApp_FailsClosed(string otherInstallDir)
    {
        string acf = WriteAcf(380, "Half-Life 2", 100, [381]);
        string otherAcf = WriteAcf(220, otherInstallDir, 100, [221]);
        string save = WriteGamePayload(@"Half-Life 2\hl2\save", "quick.sav", 32);

        var result = await NewService().CleanAsync(380, GameCleanupScope.Everything);

        result.IsBlocked.Should().BeTrue();
        result.BlockReason.Should().Be(GameCleanupBlockReason.SharedInstallDir);
        File.Exists(save).Should().BeTrue();
        File.Exists(acf).Should().BeTrue();
        File.Exists(otherAcf).Should().BeTrue();
    }

    [Fact]
    public async Task Safeguard3_PartialFolderDelete_KeepsAcfSoSteamStillTracksTheFolder()
    {
        string acf = WriteAcf(400, "Portal", 128, [401]);
        WriteGamePayload("Portal", "free.dat", 16);
        string locked = WriteGamePayload("Portal", "in_use.dll", 16);

        GameCleanupResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = await NewService().CleanAsync(400, GameCleanupScope.GameFilesOnly);

        result.HasErrors.Should().BeTrue();
        result.GameFilesDeleted.Should().BeFalse();
        result.AcfDeleted.Should().BeFalse();
        File.Exists(acf).Should().BeTrue("Steam must keep tracking a half-deleted folder");
        File.Exists(locked).Should().BeTrue();
    }

    [Fact]
    public async Task Safeguard3_DanglingJunctionInsideInstallDir_IsSeveredNotWalked()
    {
        string vanished = Path.Combine(_root, "VanishedTarget");
        Directory.CreateDirectory(vanished);
        string gameFile = WriteGamePayload("NormalGame", "game.exe", 64);
        string link = Path.Combine(_commonDir, "NormalGame", "dangling");
        DirectoryJunction.Create(link, vanished).Should().BeTrue();
        Directory.Delete(vanished);
        WriteAcf(400, "NormalGame", 64, [401]);

        GameCleanupService.IsLink(link).Should().BeTrue("a junction whose target is gone is still a link");
        GameCleanupService.IsLink(Path.Combine(_commonDir, "NormalGame")).Should().BeFalse();
        GameCleanupService.IsLink(Path.Combine(_commonDir, "Missing")).Should().BeFalse();

        var result = await NewService().CleanAsync(400, GameCleanupScope.GameFilesOnly);

        result.Succeeded.Should().BeTrue();
        result.GameFilesDeleted.Should().BeTrue();
        File.Exists(gameFile).Should().BeFalse();
        Directory.Exists(Path.Combine(_commonDir, "NormalGame")).Should().BeFalse();
    }

    [Fact]
    public async Task Safeguard3_RunningTargetGame_BlocksUninstall_WhileOtherGameOrLuaOnlySucceeds()
    {
        string lua = WriteLua(400, "addappid(400)\naddappid(401, 1, \"k\")");
        string acf = WriteAcf(400, "Portal", 128, [401]);
        string gameFile = WriteGamePayload("Portal", "portal.exe", 128);

        var runningTarget = NewService(runningAppId: () => 400, isSteamRunning: () => true);
        var blocked = await runningTarget.CleanAsync(400, GameCleanupScope.GameFilesOnly);

        blocked.IsBlocked.Should().BeTrue();
        blocked.BlockReason.Should().Be(GameCleanupBlockReason.GameRunning);
        File.Exists(gameFile).Should().BeTrue();
        File.Exists(acf).Should().BeTrue();

        // Removing .lua only while target is running is still permitted (doesn't touch game files).
        var luaOnly = await runningTarget.CleanAsync(400, GameCleanupScope.LuaOnly);
        luaOnly.Succeeded.Should().BeTrue();
        luaOnly.LuaDeleted.Should().BeTrue();

        // Different app running does NOT block uninstalling 400.
        var otherRunning = NewService(runningAppId: () => 620, isSteamRunning: () => true);
        var allowed = await otherRunning.CleanAsync(400, GameCleanupScope.GameFilesOnly);
        allowed.Succeeded.Should().BeTrue();
        allowed.GameFilesDeleted.Should().BeTrue();
    }

    // ── Safeguard 4 + Event + Messages ────────────────────────────────────────

    [Fact]
    public async Task Safeguard4_BackupVaultOverlap_FailsClosed()
    {
        // Point backup vault inside the target game dir -> uninstall must block before deleting.
        string gameBackupOverlap = Path.Combine(_commonDir, "Portal", "backups");
        Directory.CreateDirectory(gameBackupOverlap);
        string acf = WriteAcf(400, "Portal", 64, [401]);
        WriteGamePayload("Portal", "portal.exe", 64);
        _settings.ManifestBackupFolder = gameBackupOverlap;

        var result = await NewService().CleanAsync(400, GameCleanupScope.GameFilesOnly);

        result.IsBlocked.Should().BeTrue();
        result.BlockReason.Should().Be(GameCleanupBlockReason.BackupVaultOverlap);
        File.Exists(acf).Should().BeTrue();
    }

    [Fact]
    public async Task CleanAsync_RaisesGameCleanedEventOnlyWhenItemsRemoved()
    {
        var service = NewService();
        var events = new List<long>();
        service.GameCleaned += (appId, _) => events.Add(appId);

        var emptyResult = await service.CleanAsync(400, GameCleanupScope.LuaOnly);
        emptyResult.NothingToRemove.Should().BeTrue();
        events.Should().BeEmpty();

        WriteLua(400, "addappid(400)");
        var removed = await service.CleanAsync(400, GameCleanupScope.LuaOnly);
        removed.LuaDeleted.Should().BeTrue();
        events.Should().Equal(400L);
    }

    [Fact]
    public void GameCleanupMessages_FormatsBlockedNothingDeletedSharedAndPartial()
    {
        var blocked = new GameCleanupResult(
            GameCleanupScope.GameFilesOnly, false, 0, 0, false, false, 0, GameCleanupBlockReason.GameRunning, []);
        GameCleanupMessages.Describe("Portal", blocked, CultureInfo.InvariantCulture)
            .Should().Be(string.Format(CultureInfo.InvariantCulture, Resources.Strings.Cleanup_Err_GameRunning, "Portal"));

        var nothingShared = new GameCleanupResult(
            GameCleanupScope.ManifestsOnly, false, 0, 2, false, false, 0, GameCleanupBlockReason.None, []);
        GameCleanupMessages.Describe("Portal", nothingShared, CultureInfo.InvariantCulture)
            .Should().Contain(string.Format(CultureInfo.InvariantCulture, Resources.Strings.Cleanup_Result_SharedPreserved, 2));

        var partial = new GameCleanupResult(
            GameCleanupScope.Everything, true, 1, 1, false, false, 1024, GameCleanupBlockReason.None, ["Access denied"]);
        string text = GameCleanupMessages.Describe("Portal", partial, CultureInfo.InvariantCulture);
        text.Should().Contain("Portal").And.Contain("Access denied");
    }
}
