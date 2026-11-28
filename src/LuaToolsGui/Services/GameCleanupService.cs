using System.Globalization;
using System.IO;

namespace LuaToolsGui.Services;

/// <summary>
/// Single shared I/O adapter for deleting a game's <c>.lua</c>, exclusive <c>.manifest</c>s,
/// and/or installed Steam game files across Manage and Depots while enforcing fail-closed
/// safeguards (shared depots, no-lua builds, path containment/symlink checks, running-game lock,
/// and zero modifications to the Manifest Backup vault).
/// </summary>
public sealed class GameCleanupService
{
    private readonly Func<string?> _stPlugInDir;
    private readonly Func<string?> _depotCacheDir;
    private readonly SteamLibraryService _library;
    private readonly LuaVault _vault;
    private readonly CacheService _cache;
    private readonly Func<string?> _manifestBackupFolder;
    private readonly Func<int?> _readRunningAppId;
    private readonly Func<bool> _isSteamRunning;
    private readonly Func<string, bool> _isLink;
    private readonly Func<long, bool> _isAppBusy;

    public GameCleanupService(
        SteamService steam,
        SteamLibraryService library,
        LuaVault vault,
        CacheService cache,
        SettingsService settings,
        Downloads.DownloadQueue downloads,
        Backups.ManifestBackupService backups,
        LuaInstaller installer)
        : this(
            () => steam.StPlugInDir,
            () => steam.DepotCacheDir,
            library,
            vault,
            cache,
            () => settings.ManifestBackupFolder,
            SteamService.ReadRunningAppId,
            SteamService.IsSteamRunning,
            isAppBusy: appId => installer.IsInstalling || downloads.HasActiveFor(appId) || backups.IsBusyWith(appId))
    {
    }

    internal GameCleanupService(
        Func<string?> stPlugInDir,
        Func<string?> depotCacheDir,
        SteamLibraryService library,
        LuaVault vault,
        CacheService cache,
        Func<string?> manifestBackupFolder,
        Func<int?> readRunningAppId,
        Func<bool> isSteamRunning,
        Func<string, bool>? isLink = null,
        Func<long, bool>? isAppBusy = null)
    {
        _stPlugInDir = stPlugInDir;
        _depotCacheDir = depotCacheDir;
        _library = library;
        _vault = vault;
        _cache = cache;
        _manifestBackupFolder = manifestBackupFolder;
        _readRunningAppId = readRunningAppId;
        _isSteamRunning = isSteamRunning;
        _isLink = isLink ?? IsLink;
        _isAppBusy = isAppBusy ?? (static _ => false);
    }

    /// <summary>Raised when at least one file or game directory was removed for an app.</summary>
    public event Action<long, GameCleanupResult>? GameCleaned;

    /// <summary>
    /// True when <paramref name="path"/> must never be walked or counted: any DIRECTORY reparse point (junction,
    /// symlink, mount point, a dangling link whose target is gone) or a file symlink. Attributes are read from
    /// the entry itself, never its target. Plain reparse-point FILES that are not links (WOF/CompactGUI-compressed
    /// game files) stay ordinary files. Missing = false; any other I/O error = true (fail-closed).
    /// </summary>
    internal static bool IsLink(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if (!attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            return attributes.HasFlag(FileAttributes.Directory) || new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>Inspect on-disk state for an app (active lua, stored builds, installed folder &amp; size).</summary>
    public Task<GameCleanupAvailability> InspectAsync(long appId, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try { return InspectCore(appId, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fail-closed: nothing selectable rather than an unhandled exception in the dialog's Loaded.
                AppLog.Log($"cleanup: inspect app={appId} failed: {LogSanitizer.SanitizePath(ex.Message)}");
                return new GameCleanupAvailability(false, false, false, null, null);
            }
        }, ct);

    private GameCleanupAvailability InspectCore(long appId, CancellationToken ct)
    {
        if (appId is <= 0 or > uint.MaxValue)
            return new GameCleanupAvailability(false, false, false, null, null);

        string? plugDir = _stPlugInDir();
        bool hasActiveLua = plugDir is not null && File.Exists(Path.Combine(plugDir, $"{appId}.lua"));
        bool hasStoredBuilds = _vault.HasStoredBuilds(appId);

        var install = _library.FindAppInstall(appId);
        if (install is null)
            return new GameCleanupAvailability(hasActiveLua, hasStoredBuilds, false, null, null);

        var gameFilesBlock = CheckGameFiles(install, appId, _manifestBackupFolder(), out string? canonicalDir);

        long? bytes = install.SizeOnDisk;
        if (bytes is null or <= 0 && canonicalDir is { } dir && Directory.Exists(dir))
        {
            long measured = ComputeDirectorySizeSafe(dir, _isLink, ct);
            if (measured > 0) bytes = measured;
        }

        return new GameCleanupAvailability(
            hasActiveLua,
            hasStoredBuilds,
            HasInstalledGame: true,
            canonicalDir,
            bytes,
            gameFilesBlock);
    }

    /// <summary>
    /// The fail-closed checks a game-files delete needs, shared by <see cref="InspectAsync"/> (so the dialog can
    /// disable those scopes and say why before the click) and <see cref="CleanAsync"/> (which re-runs them, since
    /// the disk can change while the dialog is open). <paramref name="canonicalGameDir"/> is set once the path
    /// itself is valid, even when a later check (shared folder) refuses the delete.
    /// </summary>
    private GameCleanupBlockReason CheckGameFiles(
        SteamAppInstallLocation install, long appId, string? backupRoot, out string? canonicalGameDir)
    {
        canonicalGameDir = null;
        if (_isLink(install.SteamAppsDir) || _isLink(install.AcfPath))
            return GameCleanupBlockReason.SymlinkDetected;
        if (GameCleanupPolicy.IsOverlappingPath(install.AcfPath, backupRoot))
            return GameCleanupBlockReason.BackupVaultOverlap;

        var pathCheck = GameCleanupPolicy.ValidateInstallDirectory(
            install.CommonDir, install.RawInstallDir, _isLink, backupRoot);
        if (!pathCheck.IsValid || pathCheck.CanonicalPath is not { } dir)
            return pathCheck.IsValid ? GameCleanupBlockReason.UnsafeInstallPath : pathCheck.BlockReason;
        canonicalGameDir = dir;

        // Valve ships apps sharing one folder (HL2, its episodes, Lost Coast → "Half-Life 2"): deleting
        // it would take another app's game and saves with it.
        if (_library.GetOtherInstallDirsInLibrary(install, appId) is not { } otherDirs)
            return GameCleanupBlockReason.UnreadableSteamLibrary;
        return otherDirs.Any(d => GameCleanupPolicy.IsOverlappingPath(d, dir))
            ? GameCleanupBlockReason.SharedInstallDir
            : GameCleanupBlockReason.None;
    }

    /// <summary>Execute cleanup for <paramref name="appId"/> under the given <paramref name="scope"/>.</summary>
    public Task<GameCleanupResult> CleanAsync(long appId, GameCleanupScope scope, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            // Every delete in the execute phase catches its own errors, so an exception reaching here comes from
            // the pre-flight enumerations (stplug-in, depotcache, vault) — nothing has been deleted yet.
            try { return CleanCore(appId, scope, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Log($"cleanup: app={appId} scope={scope} blocked: {LogSanitizer.SanitizePath(ex.Message)}");
                return Blocked(scope, GameCleanupBlockReason.IoError);
            }
        }, ct);

    private GameCleanupResult CleanCore(long appId, GameCleanupScope scope, CancellationToken ct)
    {
        if (appId is <= 0 or > uint.MaxValue)
            return Blocked(scope, GameCleanupBlockReason.InvalidAppId);

        ct.ThrowIfCancellationRequested();

        // Never delete under a download/install or a manifest backup/restore of the same game.
        // ponytail: probed once, before the deletes; an operation starting mid-delete is not seen. Hold a shared
        // lock across the whole cleanup if that window ever matters.
        if (IsAppBusy(appId))
            return Blocked(scope, GameCleanupBlockReason.OperationInProgress);

        string? backupRoot = _manifestBackupFolder();
        bool wantLua = GameCleanupPolicy.IncludesLua(scope);
        bool wantManifests = GameCleanupPolicy.IncludesManifests(scope);
        bool wantGame = GameCleanupPolicy.IncludesGameFiles(scope);

        string? plugDir = _stPlugInDir();
        string? depotDir = _depotCacheDir();

        // ── Pre-flight validation (fail-closed before deleting anything) ──────────
        SteamAppInstallLocation? install = null;
        string? canonicalGameDir = null;

        if (wantGame)
        {
            if (GameCleanupPolicy.IsTargetGameRunning(appId, _readRunningAppId(), _isSteamRunning))
                return Blocked(scope, GameCleanupBlockReason.GameRunning);

            install = _library.FindAppInstall(appId);
            if (install is not null &&
                CheckGameFiles(install, appId, backupRoot, out canonicalGameDir) is var gameBlock and not GameCleanupBlockReason.None)
                return Blocked(scope, gameBlock);
        }

        string? liveLuaPath = null;
        string? disabledLuaPath = null;
        if (wantLua)
        {
            if (plugDir is null)
                return Blocked(scope, GameCleanupBlockReason.SteamNotFound);

            liveLuaPath = Path.Combine(plugDir, $"{appId}.lua");
            disabledLuaPath = Path.Combine(plugDir, $"{appId}.lua.disabled");
            if (GameCleanupPolicy.IsOverlappingPath(liveLuaPath, backupRoot))
                return Blocked(scope, GameCleanupBlockReason.BackupVaultOverlap);

            // Store any never-adopted <appid>_<buildid>.lua first: otherwise the next Depots visit "discovers"
            // it as new and ApplyBuildIfNothingLive re-creates the live lua the user is deleting now.
            _vault.AdoptLooseBuildLuas(appId);
        }

        ManifestCleanupPlan? manifestPlan = null;
        if (wantManifests)
        {
            if (depotDir is null)
                return Blocked(scope, GameCleanupBlockReason.SteamNotFound);

            if (_isLink(depotDir))
                return Blocked(scope, GameCleanupBlockReason.SymlinkDetected);

            if (GameCleanupPolicy.IsOverlappingPath(depotDir, backupRoot))
                return Blocked(scope, GameCleanupBlockReason.BackupVaultOverlap);

            var acfLocation = install ?? _library.FindAppInstall(appId);
            if (!TryCollectTargetDepotIds(appId, plugDir, acfLocation, out var targetDepots))
                return Blocked(scope, GameCleanupBlockReason.UnreadableSiblingLua);

            if (!TryCollectOtherGameDepotIds(appId, plugDir, out var otherDepots))
                return Blocked(scope, GameCleanupBlockReason.UnreadableSiblingLua);

            if (_library.GetOtherAppDepotIds(appId) is not { } acfDepots)
                return Blocked(scope, GameCleanupBlockReason.UnreadableSteamLibrary);
            otherDepots.UnionWith(acfDepots);

            var cacheFiles = EnumerateDepotCacheFiles(depotDir);
            manifestPlan = GameCleanupPolicy.PlanManifestDeletions(targetDepots, otherDepots, cacheFiles);
        }

        // ── Execute deletions ─────────────────────────────────────────────────────
        var errors = new List<string>();
        long freedBytes = 0;
        bool gameFilesDeleted = false;
        bool acfDeleted = false;
        int manifestsDeleted = 0;
        bool luaDeleted = false;

        if (wantGame && install is not null)
        {
            if (canonicalGameDir is not null && Directory.Exists(canonicalGameDir))
            {
                if (_isLink(canonicalGameDir))
                {
                    errors.Add("Target install directory became a link during cleanup.");
                }
                else
                {
                    gameFilesDeleted = DeleteDirectorySafe(
                        canonicalGameDir, backupRoot, _isLink, ref freedBytes, errors, ct);
                }
            }

            if (canonicalGameDir is not null && Directory.Exists(canonicalGameDir))
            {
                // Partial delete (a file in use): keep the ACF so Steam still knows the folder and can verify
                // or uninstall it, instead of orphaning half a game it no longer tracks.
                errors.Add("Game folder was not fully removed; Steam app manifest kept.");
            }
            else if (File.Exists(install.AcfPath))
            {
                try
                {
                    if (_isLink(install.AcfPath))
                    {
                        errors.Add("App manifest became a link during cleanup.");
                    }
                    else
                    {
                        long acfLen = SafeFileLength(install.AcfPath);
                        File.Delete(install.AcfPath);
                        freedBytes += acfLen;
                        acfDeleted = true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add(LogSanitizer.SanitizePath(ex.Message));
                }
            }
        }

        if (wantManifests && manifestPlan is not null)
        {
            foreach (string manifestPath in manifestPlan.ManifestsToDelete)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (_isLink(manifestPath) || GameCleanupPolicy.IsOverlappingPath(manifestPath, backupRoot))
                        continue;
                    if (!File.Exists(manifestPath)) continue;

                    long len = SafeFileLength(manifestPath);
                    File.Delete(manifestPath);
                    freedBytes += len;
                    manifestsDeleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add(LogSanitizer.SanitizePath(ex.Message));
                }
            }
        }

        if (wantLua && liveLuaPath is not null)
        {
            // Execute phase: a failure here must not turn into "blocked, nothing done" (CleanAsync's catch).
            try { _cache.RemoveLoadedAppId(appId); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(LogSanitizer.SanitizePath(ex.Message));
            }

            if (File.Exists(liveLuaPath))
            {
                try
                {
                    if (_isLink(liveLuaPath))
                    {
                        errors.Add("Lua file is a link.");
                    }
                    else
                    {
                        long len = SafeFileLength(liveLuaPath);
                        File.Delete(liveLuaPath);
                        freedBytes += len;
                        luaDeleted = true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add(LogSanitizer.SanitizePath(ex.Message));
                }
            }

            if (disabledLuaPath is not null && File.Exists(disabledLuaPath) && !_isLink(disabledLuaPath)
                && !GameCleanupPolicy.IsOverlappingPath(disabledLuaPath, backupRoot))
            {
                try
                {
                    long len = SafeFileLength(disabledLuaPath);
                    File.Delete(disabledLuaPath);
                    freedBytes += len;
                    luaDeleted = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add(LogSanitizer.SanitizePath(ex.Message));
                }
            }
        }

        int sharedPreserved = manifestPlan?.SharedManifestsPreserved.Count ?? 0;
        var result = new GameCleanupResult(
            scope,
            luaDeleted,
            manifestsDeleted,
            sharedPreserved,
            gameFilesDeleted,
            acfDeleted,
            freedBytes,
            GameCleanupBlockReason.None,
            errors);

        AppLog.Log(
            $"cleanup: app={appId} scope={scope} lua={luaDeleted} manifests={manifestsDeleted} " +
            $"sharedKept={sharedPreserved} gameFiles={gameFilesDeleted} acf={acfDeleted} " +
            $"freed={freedBytes} errors={errors.Count}");

        if (result.AnyRemoved)
            GameCleaned?.Invoke(appId, result);

        return result;
    }

    private bool TryCollectTargetDepotIds(
        long appId, string? plugDir, SteamAppInstallLocation? install, out HashSet<long> targetDepots)
    {
        targetDepots = [];

        if (plugDir is not null)
        {
            string livePath = Path.Combine(plugDir, $"{appId}.lua");
            if (File.Exists(livePath))
            {
                if (SteamLibraryService.ReadSharedText(livePath) is not { } liveText)
                    return false;
                AddLuaDepots(targetDepots, liveText, appId);
            }

            foreach (var (looseAppId, _, loosePath) in _vault.EnumerateLooseBuildLuas())
            {
                if (looseAppId != appId) continue;
                if (SteamLibraryService.ReadSharedText(loosePath) is not { } looseText)
                    return false;
                AddLuaDepots(targetDepots, looseText, appId);
            }
        }

        foreach (var variant in _vault.GetVariants(appId))
        {
            string variantText = _vault.ReadText(appId, variant.Hash);
            if (!string.IsNullOrEmpty(variantText))
                AddLuaDepots(targetDepots, variantText, appId);

            foreach (string key in variant.ManifestIds.Keys)
                if (long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out long d) && d is > 0 and <= uint.MaxValue)
                    targetDepots.Add(d);
        }

        if (install is not null)
        {
            foreach (long d in install.MountedDepotIds)
                if (d is > 0 and <= uint.MaxValue)
                    targetDepots.Add(d);
        }

        return true;
    }

    private bool TryCollectOtherGameDepotIds(long targetAppId, string? plugDir, out HashSet<long> otherDepots)
    {
        otherDepots = [];

        if (plugDir is not null && Directory.Exists(plugDir))
        {
            foreach (var (otherAppId, path) in LuaInstaller.EnumerateInstalled(plugDir))
            {
                if (otherAppId == targetAppId) continue;
                if (SteamLibraryService.ReadSharedText(path) is not { } text)
                    return false; // fail-closed: cannot prove a sibling active lua doesn't use a depot
                AddLuaDepots(otherDepots, text, otherAppId);
            }

            // A disabled game (<appid>.lua.disabled) still owns its depots: re-enabling it must find its manifests.
            foreach (string path in Directory.EnumerateFiles(plugDir, "*.lua.disabled"))
            {
                string name = Path.GetFileName(path);
                if (!long.TryParse(name.AsSpan(0, name.Length - ".lua.disabled".Length), NumberStyles.None,
                        CultureInfo.InvariantCulture, out long disabledAppId) || disabledAppId == targetAppId)
                    continue;
                if (SteamLibraryService.ReadSharedText(path) is not { } text)
                    return false;
                AddLuaDepots(otherDepots, text, disabledAppId);
            }

            foreach (var (looseAppId, _, loosePath) in _vault.EnumerateLooseBuildLuas())
            {
                if (looseAppId == targetAppId) continue;
                if (SteamLibraryService.ReadSharedText(loosePath) is not { } text)
                    return false;
                AddLuaDepots(otherDepots, text, looseAppId);
            }
        }

        // Fail-closed: a vault folder that can't be listed, an index that is there but unreadable, or a stored
        // lua that can't be read may be the one pinning a shared manifest.
        if (!_vault.TryReadOtherVariants(targetAppId, out var stored))
            return false;
        foreach (var (otherAppId, variant, variantText) in stored)
        {
            if (!string.IsNullOrEmpty(variantText))
                AddLuaDepots(otherDepots, variantText, otherAppId);

            foreach (string key in variant.ManifestIds.Keys)
                if (long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out long d) && d is > 0 and <= uint.MaxValue)
                    otherDepots.Add(d);
        }

        return true;
    }

    private static void AddLuaDepots(HashSet<long> target, string luaText, long appId)
    {
        var parsed = LuaFileParser.ParseText(luaText, appId);
        foreach (long id in GameCleanupPolicy.ExtractDepotIdsFromLua(parsed))
            target.Add(id);
    }

    private static IEnumerable<(string FileName, string FullPath)> EnumerateDepotCacheFiles(string depotDir)
    {
        if (!Directory.Exists(depotDir)) yield break;
        foreach (string path in Directory.EnumerateFiles(depotDir, "*.manifest"))
            yield return (Path.GetFileName(path), path);
    }

    private static long SafeFileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static long ComputeDirectorySizeSafe(string rootDir, Func<string, bool> isLink, CancellationToken ct)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(rootDir);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string dir = pending.Pop();
            if (isLink(dir)) continue;

            try
            {
                var di = new DirectoryInfo(dir);
                foreach (var file in di.EnumerateFiles())
                {
                    ct.ThrowIfCancellationRequested();
                    if (!isLink(file.FullName)) total += file.Length;
                }
                foreach (var sub in di.EnumerateDirectories())
                {
                    if (!isLink(sub.FullName)) pending.Push(sub.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                /* best-effort size estimate */
            }
        }

        return total;
    }

    private static bool DeleteDirectorySafe(
        string rootDir,
        string? backupRoot,
        Func<string, bool> isLink,
        ref long freedBytes,
        List<string> errors,
        CancellationToken ct)
    {
        if (isLink(rootDir) || GameCleanupPolicy.IsOverlappingPath(rootDir, backupRoot))
            return false;

        try
        {
            var di = new DirectoryInfo(rootDir);
            foreach (var file in di.EnumerateFiles())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (isLink(file.FullName))
                    {
                        File.Delete(file.FullName); // removes only the link itself
                        continue;
                    }

                    long len = file.Length;
                    if (file.IsReadOnly) file.IsReadOnly = false;
                    file.Delete();
                    freedBytes += len;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add(LogSanitizer.SanitizePath(ex.Message));
                }
            }

            foreach (var sub in di.EnumerateDirectories())
            {
                ct.ThrowIfCancellationRequested();
                if (GameCleanupPolicy.IsOverlappingPath(sub.FullName, backupRoot))
                    continue;

                if (isLink(sub.FullName))
                {
                    // Sever reparse point non-recursively so the target directory is never touched.
                    try { Directory.Delete(sub.FullName, recursive: false); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        errors.Add(LogSanitizer.SanitizePath(ex.Message));
                    }
                    continue;
                }

                DeleteDirectorySafe(sub.FullName, backupRoot, isLink, ref freedBytes, errors, ct);
            }

            if (di.Attributes.HasFlag(FileAttributes.ReadOnly))
                di.Attributes &= ~FileAttributes.ReadOnly;

            if (!di.EnumerateFileSystemInfos().Any())
            {
                Directory.Delete(rootDir, recursive: false);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(LogSanitizer.SanitizePath(ex.Message));
        }

        return !Directory.Exists(rootDir);
    }

    /// <summary>Fail-closed: if the busy probe itself fails (dispatcher shutting down), treat the app as busy.</summary>
    private bool IsAppBusy(long appId)
    {
        try { return _isAppBusy(appId); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            return true;
        }
    }

    private static GameCleanupResult Blocked(GameCleanupScope scope, GameCleanupBlockReason reason) =>
        new(scope, LuaDeleted: false, ManifestsDeleted: 0, SharedManifestsPreserved: 0,
            GameFilesDeleted: false, AcfDeleted: false, FreedBytes: 0, BlockReason: reason, Errors: []);
}
