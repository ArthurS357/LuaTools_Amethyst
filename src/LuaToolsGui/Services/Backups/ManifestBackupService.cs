using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Hosting;

namespace LuaToolsGui.Services.Backups;

public enum ManifestBackupRestoreFailure
{
    None,
    Disabled,
    NotFound,
    SteamNotFound,
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

    /// <summary>The shutdown token, already cancelled once the service is disposed: a pass that starts during
    /// teardown cancels cleanly instead of throwing <see cref="ObjectDisposedException"/>.</summary>
    private CancellationToken ShutdownToken
    {
        get
        {
            try { return _shutdown.Token; }
            catch (ObjectDisposedException) { return new CancellationToken(canceled: true); }
        }
    }

    /// <summary>Restores in flight, by appid: a second request for the same game joins the first.</summary>
    private readonly ConcurrentDictionary<long, Lazy<Task<ManifestBackupRestoreResult>>> _restoring = new();

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

    /// <summary>On and pointed at a folder the policy accepts, judged from the path alone (no disk access), so
    /// the UI thread can ask it without stalling on an offline drive. <see cref="ActiveFolder"/> stays the gate.</summary>
    public bool IsConfigured =>
        _settings.ManifestBackupMode == ManifestBackupMode.LocalFolder &&
        ManifestBackupPolicy.IsAcceptableDestination(_settings.ManifestBackupFolder, _steam.EffectivePath);

    /// <summary>Back up every installed game. Null when the feature is off or Steam is not located.</summary>
    public async Task<ManifestBackupSummary?> BackupAllAsync(IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        if (ActiveFolder is not { } dest || _steam.StPlugInDir is not { } plug || _steam.DepotCacheDir is not { } depot)
            return null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, ShutdownToken);
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
        // Path-only check here: this runs inside the install, on whatever thread installed. The disk-touching
        // ActiveFolder check happens on the pool thread below.
        if (ShutdownToken.IsCancellationRequested || !IsConfigured) return;
        _ = Task.Run(() => BackupAfterInstallAsync(appId));
    }

    private async Task BackupAfterInstallAsync(long appId)
    {
        var ct = ShutdownToken;
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

    /// <summary>
    /// Read-only health check of the whole vault (<see cref="ManifestVaultVerifier"/>). Null when the feature is
    /// off. Takes the pass gate per GAME, not for the whole run: it never judges a half-written game, and a
    /// restore or post-install backup waits for one zip at most, not for the whole (possibly hydrating) vault.
    /// </summary>
    /// <exception cref="IOException">The vault folder cannot be listed (drive offline).</exception>
    public async Task<ManifestVaultReport?> VerifyVaultAsync(IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        if (ActiveFolder is not { } dest) return null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, ShutdownToken);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        ManifestVaultGame Gated(long appId)
        {
            _gate.Wait(token); // a pool thread (Task.Run below), never the UI thread
            try { return ManifestVaultVerifier.VerifyGame(dest, appId); }
            finally { _gate.Release(); }
        }
        var report = await Task.Run(() => ManifestVaultVerifier.VerifyVault(dest, progress, token, Gated), token);
        AppLog.Log($"manifest backup: verify: {report.Count(ManifestVaultHealth.Healthy)} healthy, " +
                   $"{report.Count(ManifestVaultHealth.LuaOnly)} lua only, {report.Count(ManifestVaultHealth.Corrupt)} corrupt, " +
                   $"{report.Count(ManifestVaultHealth.Unreadable)} unreadable; index readable={report.IndexReadable}, " +
                   $"missing={report.MissingFromIndex.Count}, stale={report.StaleInIndex.Count}, temps={report.OrphanTemps}");
        foreach (var g in report.Games.Where(static g => g.Problem is not null))
            AppLog.Log($"manifest backup: verify: {g.AppId} {g.Health}: {g.Problem}");
        return report;
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

    /// <summary>
    /// Explicit fail-closed restore from the local backup vault into Steam. A second request for a game whose
    /// restore is still running (a double click, or the bridge's 30 s timeout prompting a retry) joins it rather
    /// than staging and hashing the zip again; installs themselves stay serialized by the installer's lock.
    /// A joiner shares the first caller's <paramref name="ct"/> (today every caller passes the app token).
    /// </summary>
    public Task<ManifestBackupRestoreResult> RestoreAsync(long appId, CancellationToken ct = default)
    {
        var mine = new Lazy<Task<ManifestBackupRestoreResult>>(() => RestoreCoreAsync(appId, ct));
        var running = _restoring.GetOrAdd(appId, mine);
        if (ReferenceEquals(running, mine))
            _ = running.Value.ContinueWith(_ => _restoring.TryRemove(KeyValuePair.Create(appId, mine)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return running.Value;
    }

    private async Task<ManifestBackupRestoreResult> RestoreCoreAsync(long appId, CancellationToken ct)
    {
        if (ActiveFolder is not { } dest)
            return new(false, false, 0, ManifestBackupRestoreFailure.Disabled, "Manifest backup is not enabled");
        if (_steam.StPlugInDir is null || _steam.DepotCacheDir is null)
            return new(false, false, 0, ManifestBackupRestoreFailure.SteamNotFound, "Steam is not located");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, ShutdownToken);
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
                return new DownloadedFile(staged.Path, Path.GetFileName(staged.Path), Pin: staged.Pin, FromBackup: true);

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
