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
/// When a feed IS configured, every request goes DIRECT to github.com / api.github.com through
/// <see cref="OfficialGithubDownloader"/> — no third-party mirrors, because this download replaces the
/// running executable. The package is accepted only if its SHA-256 equals the digest GitHub's API publishes
/// for that release asset; otherwise the update is refused (fail-closed) and logged. Resilience is one
/// layer: it tries each configured repo in order, so if the PRIMARY repo is unreachable or gone entirely
/// (banned / DMCA'd / account removed) it falls through to a backup repo. A package that fails verification
/// is different — that stops the check outright instead of shopping for another source.
/// </para>
/// </summary>
public class UpdateService
{
    // One UpdateManager per configured repo, in priority order (primary first), each with its own downloader
    // pinned to that repo. Built once at construction: the update feed is not something that should change
    // under a running check.
    private readonly UpdateManager[] _managers;
    private readonly string[] _repoNames; // parallel to _managers, for log lines

    /// <summary>The feed this instance resolved, for the About page to display.</summary>
    public UpdateSourceResolution Sources { get; }

    public UpdateService(SettingsService settings)
    {
        // settings.json wins when present; otherwise fall back to the compiled-in default, which is empty
        // (off). Opting in is a user entry in settings.json; an explicit empty array there is also "off".
        var resolution = AppUpdateSources.Resolve(settings.AppUpdateRepos ?? AppConfig.GithubReleasesRepos);
        Sources = resolution;

        // Refusals are logged, never silently dropped — a user who pasted the upstream URL needs to know
        // the app deliberately ignored it, otherwise "updates don't work" looks like a bug.
        foreach (var bad in resolution.Rejected)
            AppLog.Log($"app-update: ignoring configured repo — {bad.Describe()}");

        if (resolution.IsDisabled)
        {
            AppLog.Log("app-update: no usable repo configured — self-update is OFF (no request will be made)");
            _managers = [];
            _repoNames = [];
            return;
        }

        _managers = resolution.Repos
            .Select(repo =>
            {
                // Repos were normalised to https://github.com/<owner>/<repo> by AppUpdateSources.
                var (owner, name) = AppUpdateSources.OwnerRepoOf(repo);
                return new UpdateManager(
                    new GithubSource(repo, accessToken: null, prerelease: false,
                        downloader: new OfficialGithubDownloader(owner, name)));
            })
            .ToArray();
        _repoNames = [.. resolution.Repos];

        AppLog.Log($"app-update: enabled against {string.Join(", ", resolution.Repos)}");
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

        for (int i = 0; i < _managers.Length; i++)
        {
            var mgr = _managers[i];
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
            catch (Exception ex) when (UpdateIntegrityException.FindIn(ex) is { } failure)
            {
                // The package could not be proven to be what GitHub published. Refuse outright: nothing is
                // staged, and no backup repo is consulted — a failed check is not a reason to go looking
                // for a source that will say yes. The downloader has already logged the details.
                _stagedMgr = null;
                _staged = null;
                AppLog.Log($"app-update: update REFUSED (integrity check failed) — {failure.Message}");
                return;
            }
            catch (Exception ex)
            {
                // This repo is unreachable/gone (or a download failed) — fall through to the next backup.
                AppLog.Log($"app-update: {_repoNames[i]} check failed — {ex.Message}");
            }
        }
        // Every repo failed (offline, or all repos down) — retry next launch.
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
