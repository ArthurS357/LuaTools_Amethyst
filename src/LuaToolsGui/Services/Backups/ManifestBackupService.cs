using System.Globalization;
using System.IO;
using Microsoft.Extensions.Hosting;

namespace LuaToolsGui.Services.Backups;

public enum ManifestBackupRestoreFailure
{
    None,
    Disabled,
    NotFound,
    VerificationFailed,
    InstallRefused,
    InstallFailed,
}

public sealed record ManifestBackupRestoreResult(
    bool Ok,
    bool LuaInstalled,
    int ManifestCount,
    ManifestBackupRestoreFailure Failure,
    string? Error);

/// <summary>
/// The app-facing manifest backup: decides whether it is on, backs a game up after every install, runs the
/// "back up existing games" batch, stages a verified backup when a download source fails, and exposes
/// discovery + explicit restore for the local bridge.
/// </summary>
/// <remarks>
/// <para><b>Off by default</b> (<see cref="ManifestBackupMode.Disabled"/>). Nothing here ever contacts the
/// network: the backup is built from the lua in stplug-in and the manifests already in depotcache.</para>
///
/// <para><b>A backup never breaks an install.</b> The post-install pass runs on its own thread after the install
/// has finished; any failure is logged and shown as a warning toast, and the installed game is left alone.</para>
/// </remarks>
public sealed class ManifestBackupService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly SteamService _steam;
    private readonly LuaInstaller _installer;
    private readonly SteamAppListCache _appList;
    private readonly ToastService _toasts;

    /// <summary>One pass at a time: the batch and post-install passes write the same folders.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationTokenRegistration _shutdownReg;

    public ManifestBackupService(
        SettingsService settings, SteamService steam, LuaInstaller installer, SteamAppListCache appList,
        ToastService toasts, IHostApplicationLifetime? lifetime = null)
    {
        _settings = settings;
        _steam = steam;
        _installer = installer;
        _appList = appList;
        _toasts = toasts;
        _shutdownReg = lifetime?.ApplicationStopping.Register(CancelShutdown) ?? default;
    }

    private void CancelShutdown()
    {
        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        CancelShutdown();
        _shutdownReg.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>The destination when the feature is on AND the folder passes the policy; otherwise null.</summary>
    public string? ActiveFolder =>
        _settings.ManifestBackupMode == ManifestBackupMode.LocalFolder &&
        _settings.ManifestBackupFolder is { } folder && IsAcceptableFolder(folder)
            ? folder : null;

    public bool IsAcceptableFolder(string? path) =>
        ManifestBackupPolicy.IsAcceptableDestination(path, _steam.EffectivePath)
        && !ManifestBackupStore.IsLink(path!);

    /// <summary>Back up every installed game. Null when the feature is off or Steam is not located.</summary>
    public async Task<ManifestBackupSummary?> BackupAllAsync(IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        if (ActiveFolder is not { } dest || _steam.StPlugInDir is not { } plug || _steam.DepotCacheDir is not { } depot)
            return null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            return await Task.Run(
                () => ManifestBackupStore.BackupAll(plug, depot, dest, _appList.GetName, progress, linked.Token),
                linked.Token);
        }
        finally { _gate.Release(); }
    }

    /// <summary><see cref="LuaInstaller.Installed"/> handler: queue a background backup of that game.</summary>
    public void OnInstalled(long appId)
    {
        if (_shutdown.IsCancellationRequested || ActiveFolder is null) return;
        _ = Task.Run(() => BackupAfterInstallAsync(appId));
    }

    private async Task BackupAfterInstallAsync(long appId)
    {
        var ct = _shutdown.Token;
        try
        {
            // Gate FIRST (async, no thread held), then the blocking barrier: a burst of installs parks at most
            // one pool thread on the install lock instead of one per install.
            await _gate.WaitAsync(ct);
            try
            {
                _installer.WaitForInstallsToSettle(ct); // the zip's manifests land after Installed fires
                ct.ThrowIfCancellationRequested();
                if (ActiveFolder is not { } dest || _steam.StPlugInDir is not { } plug || _steam.DepotCacheDir is not { } depot)
                    return;

                var outcome = ManifestBackupStore.BackupOne(appId, plug, depot, dest, _appList.GetName(appId));
                if (outcome.Error is { } error) Warn(appId, error);
                else AppLog.Log($"manifest backup: {appId} {outcome.Change} ({outcome.ManifestCount} manifest(s))");
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException)
        {
            // App shutting down; exit cleanly without blocking exit or flashing a warning toast.
        }
        catch (Exception ex)
        {
            Warn(appId, ex.Message); // a thread-pool task: an escaping exception would go unobserved
        }
    }

    /// <summary>Root discovery catalog for the frontend; returns <c>Enabled: false</c> when off/unconfigured.</summary>
    public (bool Enabled, ManifestBackupIndex? Index) GetIndex()
    {
        if (ActiveFolder is not { } dest) return (false, null);
        return (true, ManifestBackupStore.LoadOrScanIndex(dest));
    }

    /// <summary>Lightweight availability lookup for a single game (zero zip bytes read).</summary>
    public ManifestBackupAppStatus GetAppStatus(long appId)
    {
        if (ActiveFolder is not { } dest)
            return new(Enabled: false, Available: false, HasManifests: false, ManifestCount: 0, UpdatedAtUtc: null);
        return ManifestBackupStore.GetAppStatus(appId, dest);
    }

    /// <summary>Explicit fail-closed restore from the local backup vault into Steam.</summary>
    public async Task<ManifestBackupRestoreResult> RestoreAsync(long appId, CancellationToken ct = default)
    {
        if (ActiveFolder is not { } dest)
            return new(false, false, 0, ManifestBackupRestoreFailure.Disabled, "Manifest backup is not enabled");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        DownloadedFile? staged = null;

        await _gate.WaitAsync(linked.Token);
        try
        {
            staged = await Task.Run(() => TryStageRestore(appId), linked.Token);
            if (staged is null)
            {
                string zipPath = Path.Combine(dest, appId.ToString(CultureInfo.InvariantCulture),
                    $"{appId.ToString(CultureInfo.InvariantCulture)}.zip");
                bool exists = !ManifestBackupStore.IsLink(zipPath) && File.Exists(zipPath);
                return exists
                    ? new(false, false, 0, ManifestBackupRestoreFailure.VerificationFailed, "Backup failed integrity verification")
                    : new(false, false, 0, ManifestBackupRestoreFailure.NotFound, "No backup found for this app");
            }
        }
        finally { _gate.Release(); }

        try
        {
            var result = await Task.Run(() => _installer.InstallZip(staged.FilePath, appId, source: "backup"), linked.Token);
            if (result.Error is not null)
            {
                AppLog.Log($"manifest backup restore refused for {appId}: {LogSanitizer.Sanitize(result.Error)}");
                return new(false, false, 0, ManifestBackupRestoreFailure.InstallRefused,
                    "Backup archive was refused by installer validation");
            }
            if (result.AnyFailed)
            {
                AppLog.Log($"manifest backup restore failed for {appId}: {result.Failed.Count} entry failure(s)");
                return new(false, result.LuaInstalled, result.ManifestCount, ManifestBackupRestoreFailure.InstallFailed,
                    "Failed to install backup files");
            }
            return new(true, result.LuaInstalled, result.ManifestCount, ManifestBackupRestoreFailure.None, null);
        }
        finally
        {
            staged.Discard();
        }
    }

    /// <summary>
    /// A verified copy of the game's backup zip, staged in %TEMP% for the installer to consume (and delete), or
    /// null when the feature is off, there is no backup, or it fails verification. Fail-closed by design.
    /// </summary>
    public DownloadedFile? TryStageRestore(long appId)
    {
        if (ActiveFolder is not { } dest) return null;
        try
        {
            if (ManifestBackupStore.StageVerifiedRestore(appId, dest, Path.GetTempPath()) is { } staged)
                return new DownloadedFile(staged.Path, Path.GetFileName(staged.Path), Pin: staged.Pin);

            AppLog.Log($"manifest backup: no verified backup to restore for {appId}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Log($"manifest backup: restore of {appId} failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Called from catch blocks of a fire-and-forget task, so it must never throw itself.</summary>
    private void Warn(long appId, string error)
    {
        AppLog.Log($"WARN manifest backup: {appId} failed (install kept): {error}");
        try
        {
            string name = _appList.GetName(appId) ?? appId.ToString(CultureInfo.InvariantCulture);
            _toasts.Show(Resources.Strings.ManifestBackup_Toast_Failed_Title,
                string.Format(CultureInfo.CurrentCulture, Resources.Strings.ManifestBackup_Toast_Failed, name, error),
                error: true, duration: TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            AppLog.Log($"manifest backup: warning toast failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
