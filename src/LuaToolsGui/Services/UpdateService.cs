using Velopack;
using Velopack.Sources;

namespace LuaToolsGui.Services;

/// <summary>
/// Silent background auto-update of THE APP ITSELF via Velopack + GitHub Releases. Checks on launch,
/// downloads the (delta) update in the background, and stages it to apply on next exit.
///
/// <para>
/// DISABLED BY DEFAULT in this fork. The feed is not compiled in: it comes from <c>AppUpdateRepos</c> in
/// settings.json, validated by <see cref="AppUpdateSources"/>, and the compiled-in default is empty. With
/// no configured repo this class builds zero managers and <see cref="CheckAndStageAsync"/> returns
/// immediately, so the app makes NO update request at all. The reason is on
/// <see cref="AppConfig.GithubReleasesRepos"/>: inheriting upstream's feed would have the fork quietly
/// replace itself with the official build, restoring telemetry and the DonateKeys upload.
/// </para>
///
/// <para>
/// Scope: this affects only the app's own binary. Manifests, plugins, unlockers and Steamless resolve
/// their own sources elsewhere and are unaffected whether self-update is on or off.
/// </para>
///
/// <para>
/// When a feed IS configured, resilience is two-layered: (1) <see cref="ProxiedFileDownloader"/> routes
/// each repo's feed + package downloads through GitHub mirrors for blocked/throttled regions (e.g. China);
/// (2) it tries each configured repo in order, so if the PRIMARY repo is gone entirely (banned / DMCA'd /
/// account removed — something the mirrors can't fix) it falls through to a backup repo.
/// </para>
/// </summary>
public class UpdateService
{
    // One UpdateManager per configured repo, in priority order (primary first). All share the proxied
    // downloader so every repo is also mirror-resilient. Built once at construction: the update feed is
    // not something that should change under a running check.
    private readonly UpdateManager[] _managers;

    public UpdateService(SettingsService settings)
    {
        var resolution = AppUpdateSources.Resolve(settings.AppUpdateRepos);

        // Refusals are logged, never silently dropped — a user who pasted the upstream URL needs to know
        // the app deliberately ignored it, otherwise "updates don't work" looks like a bug.
        foreach (var bad in resolution.Rejected)
            PluginLog.Log($"app-update: ignoring configured repo — {bad.Describe()}");

        if (resolution.IsDisabled)
        {
            PluginLog.Log("app-update: no usable repo configured — self-update is OFF (no request will be made)");
            _managers = [];
            return;
        }

        _managers = resolution.Repos
            .Select(repo => new UpdateManager(
                new GithubSource(repo, accessToken: null, prerelease: false,
                    downloader: new ProxiedFileDownloader())))
            .ToArray();

        PluginLog.Log($"app-update: enabled against {string.Join(", ", resolution.Repos)}");
    }

    // The manager whose repo actually produced the staged update — apply against this same one.
    private UpdateManager? _stagedMgr;
    private UpdateInfo? _staged;

    /// <summary>Raised on the thread pool when an update has finished downloading and is ready.</summary>
    public event Action? UpdateReady;

    /// <summary>True once an update is downloaded and waiting to be applied.</summary>
    public bool HasStagedUpdate => _staged is not null;

    /// <summary>Check for, download, and stage an update. Tries each repo in order until one yields a
    /// usable update; the first success wins. No-op for un-installed (dev) builds.</summary>
    public async Task CheckAndStageAsync()
    {
        // IsInstalled is a property of the Velopack install, not the repo, so any manager answers it.
        if (_managers.Length == 0 || !_managers[0].IsInstalled) return; // `dotnet run` / unpacked builds

        foreach (var mgr in _managers)
        {
            try
            {
                var info = await mgr.CheckForUpdatesAsync();
                // A reachable repo returning null means we're already up to date — STOP. Don't fall
                // through to a backup (it may lag behind the primary and would offer no/older update).
                // Backups exist for an UNreachable primary, which surfaces as an exception below.
                if (info is null) return;

                await mgr.DownloadUpdatesAsync(info);
                _stagedMgr = mgr;
                _staged = info;
                UpdateReady?.Invoke();
                return; // staged from this repo — done
            }
            catch
            {
                // This repo is unreachable/gone (or a download failed) — fall through to the next backup.
            }
        }
        // Every repo failed (offline, or all repos down) — fail silently, retry next launch.
    }

    /// <summary>Apply the staged update now and relaunch into the new version. <paramref name="restartArgs"/>
    /// are passed to the relaunched process (e.g. the loader's --minimized --tray-locked) so the new
    /// instance keeps its session semantics.</summary>
    public void ApplyAndRestart(string[]? restartArgs = null)
    {
        if (_stagedMgr is not null && _staged is not null)
            _stagedMgr.ApplyUpdatesAndRestart(_staged, restartArgs);
    }

    /// <summary>Apply the staged update after the app exits (no forced restart).</summary>
    public void ApplyOnExit()
    {
        if (_stagedMgr is not null && _staged is not null)
            _stagedMgr.WaitExitThenApplyUpdates(_staged, silent: true, restart: false);
    }
}
