using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>Queried state of the installed plugin vs. the latest GitHub release.</summary>
public sealed record PluginStatus(
    bool FrontendInstalled,
    bool DllInstalled,
    bool DllMatches,       // every loader slot's sha256 == its latest release asset digest
    string? InstalledTag,  // from the on-disk manifest (null if never installed / no manifest)
    string? LatestTag,
    bool UpdateAvailable,
    bool MillenniumPresent,
    bool Offline,          // couldn't reach GitHub
    bool Port8080Busy);    // something other than Steam's own CDP server is listening on CDP's fixed port — warn only, see IsPort8080BusyAsync

/// <summary>
/// Installs / updates / removes the LuaTools store-page plugin from GitHub releases (the app is the plugin
/// MANAGER — it doesn't bundle the frontend). Each release of <c>madoiscool/LTSP</c> carries
/// <c>plugin.zip</c> (the frontend, extracted to %AppData%\LuaToolsGui\plugin — where CefInjectorService
/// reads it) plus one loader DLL per <see cref="Slots"/> entry, dropped into the Steam install root
/// (steam.exe loads it). Modeled on <see cref="UnlockerService"/>: fetch release JSON, download assets
/// via <see cref="GithubProxy"/> (mirror fallback), verify each by its sha256 digest, then place. The DLL
/// is locked while Steam runs, so DLL install/uninstall stops Steam first (via <see cref="SteamService"/>)
/// and relaunches it if it was up.
///
/// CDP itself (the debug bridge <see cref="CefInjectorService"/> connects through) is NOT opened by the
/// DLL anymore — install/uninstall also manages a `.cef-enable-remote-debugging` NTFS junction next to
/// Steam's exe, which makes Steam self-enable CDP on its own (fixed port 8080, confirmed not
/// configurable; verified on both Windows 10 and 11). That leaves the DLL with one remaining job —
/// "launch LuaTools.exe when Steam opens" — with no CDP hook, no load-timing race, and no dual-slot
/// redundancy needed anymore.
/// </summary>
public class PluginInstallerService(SteamService steam, GithubProxy gh, CefInjectorService injector,
    CacheService cache, SettingsService settings, DownloadNotice notice)
{
    /// <summary>
    /// Asks the user whether LuaTools may enable Steam's remote-debugging bridge. Returns true to proceed.
    /// Wired by <c>App</c> to a modal prompt; left null in headless contexts, where the answer is "no".
    ///
    /// <para>
    /// A callback rather than a direct dialog so this service stays free of UI (it runs on background
    /// threads, during silent auto-update, and from the HTTP bridge — none of which can show a window).
    /// </para>
    /// </summary>
    public Func<bool>? ConfirmCdpExposure { get; set; }

    /// <summary>
    /// Whether the CDP marker junction may be created. Consent is asked at most once and then remembered.
    ///
    /// <para>
    /// The junction makes Steam open an <b>unauthenticated</b> DevTools Protocol server on 127.0.0.1:8080.
    /// That is what the store-page plugin needs, but it also means any process on the machine can execute
    /// JavaScript in Steam's browser — with the user's signed-in session. Previously this was enabled
    /// silently, and re-created on every status check, so a user could never have known it had happened.
    /// Now nothing creates it until the user has been told and agreed.
    /// </para>
    /// </summary>
    private bool MayEnableCdp()
    {
        if (cache.CdpConsentGranted) return true;

        // No UI available (silent install, background auto-update, HTTP bridge): do not enable it behind
        // the user's back. The next interactive launch will ask.
        if (ConfirmCdpExposure is not { } ask) return false;

        bool granted = ask();
        if (granted) cache.CdpConsentGranted = true;
        return granted;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private const string PluginZipAsset = "plugin.zip";

    /// <summary>The one DLL-proxy slot the loader ships as. <c>winmm.dll</c> is loaded dynamically (audio)
    /// by steam.exe, is never a KnownDLL on Win10 or Win11, and isn't claimed by Millennium (wsock32/
    /// version) or OpenSteamTool (dwmapi/xinput). Its old weakness — load timing isn't guaranteed relative
    /// to steamwebhelper's launch — no longer matters now that CDP is opened by the junction instead of a
    /// hook this DLL installs: there's no launch to catch a deadline for anymore, just "eventually load
    /// while Steam is running." Other slots were each dead ends: bcrypt is KnownDLLs-forced on Win10,
    /// and psapi and dbghelp don't load reliably enough.</summary>
    private sealed record LoaderSlot(string DllAsset, string RealName, string SystemSourceName)
    {
        public string SystemSourcePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), SystemSourceName);
    }

    private static readonly LoaderSlot[] Slots =
    {
        new("winmm.dll", "winmm_real.dll", "winmm.dll"),
    };

    // Old slots to clean up on install/update: if left in the Steam root they'd load and run the loader
    // payload an extra time (double LuaTools launch), and bcrypt/dbghelp specifically would still install
    // their own now-obsolete CreateProcessInternalW hook from the old shipped binary. Covers the shipped
    // bcrypt dual-slot build and the psapi/dbghelp test builds some users received during this
    // investigation. *_real are their forwarding companions.
    private static readonly string[] LegacyDllNames =
        { "bcrypt.dll", "bcrypt_real.dll", "psapi.dll", "dbghelp.dll", "dbghelp_real.dll" };

    // ── CDP marker junction ──────────────────────────────────────────────────
    // `.cef-enable-remote-debugging` next to Steam's exe, created as an NTFS JUNCTION (not a plain file)
    // pointing at a target that's deliberately chosen to never exist. Steam's own internal logic treats
    // its mere presence as "self-enable CDP debugging" (fixed port 8080) without needing to resolve the
    // target — but Millennium's cleanup code (health_check.cc) calls `std::filesystem::exists()` first,
    // which DOES try to resolve the junction, fails since the target is nonexistent, and reports false —
    // so Millennium's removal code never even runs. A plain file does not survive that cleanup; the
    // junction does. Verified live this session on both Windows 10 and 11, including with a real
    // Millennium instance loaded. Creating and removing it goes through DirectoryJunction, which drives
    // the reparse point natively — see that type for why this must stay a junction rather than become a
    // symlink, and for the command injection the previous cmd.exe form allowed.
    private const string CdpMarkerName = ".cef-enable-remote-debugging";
    private const string CdpMarkerJunctionTarget = @"C:\fuckass\folder\that\shall\never\exist\die\millennium";
    private const int CdpPort = 8080; // Steam's own fixed default — confirmed not configurable via the marker's content.
    private string? CdpMarkerPath => SteamDir is { } s ? Path.Combine(s, CdpMarkerName) : null;

    /// <summary>Idempotent, self-healing: safe to call on every status check, not just on install. If a
    /// REAL junction is already there, no-op. If something else is there — a stale plain marker file from
    /// before this session's junction fix, or any other leftover cruft that isn't actually a reparse
    /// point — it's cleared first, since a plain file at this path doesn't survive Millennium's cleanup and
    /// silently breaks CDP forever otherwise. The reparse-point test is what distinguishes the two; a bare
    /// Exists check would treat the cruft as "already present" and leave CDP broken for that install.</summary>
    private static void CreateCdpMarkerJunction(string path)
    {
        if (DirectoryJunction.Exists(path)) return;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort — Create below fails harmlessly if this didn't clear the way.
        }

        DirectoryJunction.Create(path, CdpMarkerJunctionTarget);
    }

    private static void RemoveCdpMarkerJunction(string path) => DirectoryJunction.Remove(path);

    private static readonly HttpClient PortProbeHttp = new() { Timeout = TimeSpan.FromMilliseconds(800) };

    /// <summary>Best-effort check for whether CDP's fixed port is occupied by something OTHER than Steam's
    /// own CDP server. A bare bind-test can't make that distinction: once the junction is doing its job and
    /// CDP is actually up, Steam itself is the one holding the port, so a bind fails for the exact same
    /// reason a hostile squatter would cause it to fail — the two are indistinguishable to
    /// <see cref="System.Net.Sockets.TcpListener"/>. This produced a live false positive (warning fired
    /// against Steam's own working CDP server). Fixed by, on a bind failure, asking whatever's on the port
    /// for <c>/json</c> — CDP answers with a JSON array of debug targets; only treat it as "busy" in the
    /// user-facing sense if that probe does NOT look like CDP. The port can't be changed regardless
    /// (confirmed: file content has no effect on it), so this stays detection-only — a false
    /// positive/negative here never blocks install/uninstall.</summary>
    private static async Task<bool> IsPort8080BusyAsync()
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, CdpPort);
            listener.Start();
            listener.Stop();
            return false; // nothing bound there
        }
        catch (System.Net.Sockets.SocketException)
        {
            try
            {
                var body = await PortProbeHttp.GetStringAsync($"http://127.0.0.1:{CdpPort}/json");
                return !body.TrimStart().StartsWith('['); // CDP's /json answers with a JSON array of targets
            }
            catch { return true; } // bound, but not answering like CDP -> genuinely something else
        }
        catch { return false; }
    }

    private static string FrontendDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "plugin");
    private static string LuatoolsJsPath => Path.Combine(FrontendDir, "public", "luatools.js");
    private static string ManifestPath => Path.Combine(FrontendDir, "installed.json");

    private string? SteamDir => steam.EffectivePath;
    private string? SlotPath(LoaderSlot slot) => SteamDir is { } s ? Path.Combine(s, slot.DllAsset) : null;
    private string? SlotRealPath(LoaderSlot slot) => SteamDir is { } s ? Path.Combine(s, slot.RealName) : null;
    private IEnumerable<string> LegacyDllPaths =>
        SteamDir is { } s ? LegacyDllNames.Select(n => Path.Combine(s, n)) : Enumerable.Empty<string>();

    // ── DLL-update testing switch ──
    // Drop `.luatools-dll-update-disabled` in the Steam root to stop the app from replacing any installed
    // loader slot (auto OR manual), so hand-placed test builds survive updates. Delete the file to resume
    // normal DLL updates. Frontend updates are unaffected. Re-read on every update, so toggling needs no
    // restart. Mirrors the DLL's own `.luatools-cdp-hook-disabled` marker idiom.
    private const string DllUpdateDisabledMarker = ".luatools-dll-update-disabled";
    private bool DllUpdateDisabled =>
        SteamDir is { } s && File.Exists(Path.Combine(s, DllUpdateDisabledMarker));

    private GithubRelease? _cachedLatest;

    public bool MillenniumPresent =>
        SteamDir is { } s && File.Exists(Path.Combine(s, "millennium", "lib", "millennium.dll"));

    // ── manifest (records the installed tag + asset hashes, for status/update detection) ──
    private sealed class Manifest
    {
        public string? Tag { get; set; }
        public Dictionary<string, string>? DllShas { get; set; } // DllAsset name -> sha256, one per slot
        public string? ZipSha { get; set; }
        /// <summary>Millennium config path → the exact enabledPlugins entries we removed there on install,
        /// so uninstall can restore Millennium's luatools plugin verbatim.</summary>
        public Dictionary<string, List<string>>? DisabledMillenniumEntries { get; set; }
    }

    private static Manifest? ReadManifest()
    {
        try
        {
            return File.Exists(ManifestPath)
                ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath), JsonOpts)
                : null;
        }
        catch { return null; }
    }

    private static void WriteManifest(Manifest m)
    {
        Directory.CreateDirectory(FrontendDir);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(m));
    }

    // ── GitHub ──
    public async Task<GithubRelease?> FetchLatestAsync(bool force, CancellationToken ct = default)
    {
        if (!force && _cachedLatest is not null) return _cachedLatest;
        string url = $"https://api.github.com/repos/{AppConfig.PluginReleasesOwner}/{AppConfig.PluginReleasesRepo}/releases/latest";
        try
        {
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;
            var rel = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (rel is not null) _cachedLatest = rel;
            return rel;
        }
        catch { return null; }
    }

    /// <summary>Fast, network-free check: the plugin frontend + a loader slot are both present. Used by the
    /// first-run onboarding gate (no GitHub round-trip, unlike <see cref="GetStatusAsync"/>).</summary>
    public bool IsInstalledLocally() =>
        File.Exists(LuatoolsJsPath)
        && (Slots.Any(s => SlotPath(s) is { } p && File.Exists(p)) || LegacyDllPaths.Any(File.Exists));

    public async Task<PluginStatus> GetStatusAsync(bool force = false, CancellationToken ct = default)
    {
        bool frontend = File.Exists(LuatoolsJsPath);
        // "installed" if AT LEAST ONE slot's proxy is present — a partial/mid-migration state still counts
        // as installed and eligible for auto-update, rather than showing "not installed". An OLD loader
        // (psapi/dbghelp) also still counts — otherwise a user who hasn't migrated shows "not installed"
        // and the auto-update gate (UpdateAvailable) never fires, stranding them on the dead loader.
        bool anySlotPresent = Slots.Any(slot => SlotPath(slot) is { } p && File.Exists(p));
        bool legacy = LegacyDllPaths.Any(File.Exists);
        bool loader = anySlotPresent || legacy;

        // Self-heal the CDP junction on every status check, not just when InstallAsync happens to run.
        // InstallAsync only fires on a fresh install or when a version bump makes UpdateAvailable true — once
        // the plugin is fully up to date, nothing else ever re-touches the marker. If it's ever removed after
        // that point (inconsistent Millennium-version cleanup, AV quarantining the reparse point, a Steam
        // repair, manual deletion), CDP silently stops working and the ONLY thing that used to fix it was a
        // full uninstall+reinstall (forces InstallAsync unconditionally) — exactly the workaround users have
        // been reporting. GetStatusAsync runs on every Steam-open poke, so checking here closes that gap
        // continuously instead of only at version-bump time. Cheap when already correct (single attribute
        // check, no shellout) and only touches the loader DLL is actually installed.
        // Gated on consent: this self-heal path runs on every Steam-open poke, so without the gate it was
        // the thing that silently (re-)enabled the debug bridge. Already-granted consent makes it a no-op
        // check, so the self-healing behaviour above is preserved for users who opted in.
        if (loader && CdpMarkerPath is { } liveMarkerPath && MayEnableCdp())
            CreateCdpMarkerJunction(liveMarkerPath);

        bool port8080Busy = await IsPort8080BusyAsync();
        var manifest = ReadManifest();
        var latest = await FetchLatestAsync(force, ct);

        if (latest is null)
            return new PluginStatus(frontend, loader, false, manifest?.Tag, null, UpdateAvailable: false,
                MillenniumPresent, Offline: true, port8080Busy);

        // dllMatches = true only when EVERY slot's proxy is present and matches its release asset digest.
        // AssetIntegrity.Matches also covers "file missing" and "no published digest" as non-matches, so a
        // digest-less release reports UpdateAvailable rather than silently claiming the DLL is current.
        bool dllMatches = Slots.All(slot =>
            SlotPath(slot) is { } p && AssetIntegrity.Matches(p, AssetDigest(latest, slot.DllAsset)));
        bool installed = frontend && loader;
        // `|| legacy` keeps a leftover/locked legacy dll getting swept on subsequent auto-updates until gone.
        bool updateAvailable = installed && (manifest?.Tag != latest.TagName || !dllMatches || legacy);

        return new PluginStatus(frontend, loader, dllMatches, manifest?.Tag, latest.TagName, updateAvailable,
            MillenniumPresent, Offline: false, port8080Busy);
    }

    // ── Install / update ──
    public async Task<(bool ok, string? error)> InstallAsync(IProgress<double?>? progress, CancellationToken ct = default)
    {
        if (SteamDir is not { } steamDir) return (false, Resources.Strings.Plugin_Err_SteamNotFound);

        var latest = await FetchLatestAsync(force: true, ct);
        if (latest is null) return (false, Resources.Strings.Plugin_Err_GithubUnreachable);

        var zipAsset = FindAsset(latest, PluginZipAsset);
        if (zipAsset is null)
            return (false, string.Format(Resources.Strings.Plugin_Err_MissingAssets, latest.TagName, PluginZipAsset, Slots[0].DllAsset));
        var slotAssets = new Dictionary<LoaderSlot, GithubAsset>();
        foreach (var slot in Slots)
        {
            if (FindAsset(latest, slot.DllAsset) is not { } asset)
                return (false, string.Format(Resources.Strings.Plugin_Err_MissingAssets, latest.TagName, PluginZipAsset, slot.DllAsset));
            slotAssets[slot] = asset;
        }

        string tmp = Path.Combine(Path.GetTempPath(), "luatools-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        Dictionary<string, List<string>>? disabledMillenniumEntries = null;
        try
        {
            // Pinned to the plugin repo, not merely to a GitHub host: the loader DLL below is copied into
            // the Steam root and loaded by steam.exe, and the URL comes from release JSON an API mirror may
            // have served. See GithubProxy.IsAssetUrlForRepo.
            string zipPath = Path.Combine(tmp, PluginZipAsset);
            await gh.DownloadAssetAsync(zipAsset.DownloadUrl, AppConfig.PluginReleasesOwner,
                AppConfig.PluginReleasesRepo, zipPath, progress, ct);
            var slotDlPaths = new Dictionary<LoaderSlot, string>();
            foreach (var (slot, asset) in slotAssets)
            {
                string p = Path.Combine(tmp, slot.DllAsset);
                await gh.DownloadAssetAsync(asset.DownloadUrl, AppConfig.PluginReleasesOwner,
                    AppConfig.PluginReleasesRepo, p, progress, ct);
                slotDlPaths[slot] = p;
            }

            // Verify each against its release asset digest before touching anything on disk.
            // A MISSING digest fails the check rather than skipping it: the loader DLL below is copied into
            // the Steam root and loaded by steam.exe, and the bytes can arrive from a third-party mirror
            // (GithubProxy falls back to public proxies in blocked regions). The old form
            // (`is { } zd && zipSha != zd`) treated a digest-less release JSON as "verified".
            if (!AssetIntegrity.Matches(zipPath, AssetDigest(latest, PluginZipAsset)))
                return (false, string.Format(Resources.Strings.Plugin_Err_VerifyFailed, PluginZipAsset));
            string zipSha = AssetIntegrity.Sha256OfFile(zipPath); // recorded in the manifest below

            var slotShas = new Dictionary<LoaderSlot, string>();
            foreach (var (slot, p) in slotDlPaths)
            {
                if (!AssetIntegrity.Matches(p, AssetDigest(latest, slot.DllAsset)))
                    return (false, string.Format(Resources.Strings.Plugin_Err_VerifyFailed, slot.DllAsset));
                slotShas[slot] = AssetIntegrity.Sha256OfFile(p);
            }

            if (ScreenPluginArchive(zipPath, FrontendDir) is { } rejection) return (false, rejection);

            // Disclose what is about to be installed — the frontend zip plus one loader DLL per slot, the
            // latter going into the Steam root. Cancelling here has cost nothing: not a byte has been
            // written outside the temp folder yet.
            if (!await notice.ReviewAsync(new DownloadReview(
                    AppConfig.PluginReleasesOwner, AppConfig.PluginReleasesRepo, latest.TagName,
                    PluginZipAsset, zipSha,
                    FileCount: 1 + slotShas.Count,
                    ArchiveScreened: true), ct))
                return (false, Resources.Strings.Download_Notice_Cancelled);

            // 1) Frontend → %AppData%\LuaToolsGui\plugin (fresh).
            if (Directory.Exists(FrontendDir)) Directory.Delete(FrontendDir, recursive: true);
            Directory.CreateDirectory(FrontendDir);
            ZipFile.ExtractToDirectory(zipPath, FrontendDir);
            NormalizeFrontendLayout();
            if (!File.Exists(LuatoolsJsPath))
                return (false, Resources.Strings.Plugin_Err_NoLuatoolsJs);

            // Get the frontend live in THIS running process immediately — don't wait on the Steam restart
            // below. A relaunched LuaTools.exe would hit the single-instance mutex against this very
            // process (the one the user is using right now to click Install) and exit quietly without ever
            // taking over, so nothing would otherwise pick up the new file until a manual app restart.
            await injector.ReloadPluginFilesAsync();

            // 2) Loader DLLs → Steam root — but ONLY when at least one slot actually changed, OR a legacy
            //    slot is still present and must be removed. Both slots are always installed/updated
            //    together (never partially out of date relative to each other). The DLLs are locked while
            //    Steam runs, so either condition means stopping+restarting Steam; a frontend-only update
            //    (the common case) skips all of that and applies with zero Steam disruption.
            // Testing switch: when `.luatools-dll-update-disabled` is present, never touch any on-disk DLL
            // (so hand-placed test builds aren't clobbered) — and thus never stop/restart Steam for it either.
            bool legacyPresent = LegacyDllPaths.Any(File.Exists);
            bool anySlotNeedsUpdate = Slots.Any(slot =>
                SlotPath(slot) is not { } cur || !File.Exists(cur)
                || AssetIntegrity.Sha256OfFile(cur) != slotShas[slot]);
            bool dllNeedsUpdate = !DllUpdateDisabled && (anySlotNeedsUpdate || legacyPresent);
            if (dllNeedsUpdate)
            {
                bool wasRunning = Process.GetProcessesByName("steam").Length > 0;
                steam.StopSteam();
                await Task.Delay(1200, ct); // let file handles on the loader DLLs release after the kill

                foreach (var slot in Slots)
                {
                    File.Copy(slotDlPaths[slot], Path.Combine(steamDir, slot.DllAsset), overwrite: true);
                    // Each proxy forwards to <name>_real.dll — a copy of the machine's own matching
                    // System32 file. Refresh it on every DLL update so it always matches the current OS build.
                    if (SlotRealPath(slot) is { } real && File.Exists(slot.SystemSourcePath))
                        File.Copy(slot.SystemSourcePath, real, overwrite: true);
                }
                // Remove any legacy slot (psapi/dbghelp + their _real) — leaving one would run the loader
                // payload an extra time (double LuaTools launch / CDP hook).
                foreach (var legacy in LegacyDllPaths)
                    if (File.Exists(legacy)) { try { File.Delete(legacy); } catch { /* locked/again next time */ } }

                // 3) A live Millennium luatools plugin would inject the frontend redundantly — disable it in
                //    Millennium's config (reversibly) so LuaLoader is the sole injector (leaves the Millennium
                //    mod itself alone). Steam is stopped here, so the edit can't be clobbered and takes effect
                //    on the restart below. Also migrate away any leftover folder-rename from older builds.
                if (MillenniumPresent)
                {
                    RestoreMillenniumPluginFolder(steamDir);
                    disabledMillenniumEntries = SetMillenniumLuatoolsEnabled(enable: false);
                }

                if (wasRunning) steam.StartSteam();
            }

            // Ensure the CDP marker junction exists — independent of whether the DLL itself changed (a
            // filesystem-only op, doesn't need Steam stopped). Needed here for the very first install (before
            // any GetStatusAsync call would see `loader` true); GetStatusAsync's own check is what keeps this
            // self-healing on every subsequent Steam-open, not just at install/update time. See its
            // declaration above for why this MUST be a junction, not a file.
            // Gated on the same consent as the self-heal path. Declining does not fail the install: the
            // frontend and loader are still placed, the store-page bridge just stays inactive until the
            // user agrees (the Plugin page's status surfaces that).
            if (CdpMarkerPath is { } markerPath && MayEnableCdp())
                CreateCdpMarkerJunction(markerPath);

            WriteManifest(new Manifest
            {
                Tag = latest.TagName,
                DllShas = slotShas.ToDictionary(kv => kv.Key.DllAsset, kv => kv.Value),
                ZipSha = zipSha,
                DisabledMillenniumEntries = disabledMillenniumEntries,
            });
            return (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { try { Directory.Delete(tmp, recursive: true); } catch { /* temp cleanup */ } }
    }

    /// <summary>Silent auto-update: if an update is available for an already-installed plugin, apply it
    /// (frontend silently; DLL change stops/swaps/restarts Steam). Returns true if an update was applied.
    /// Fire-and-forget safe — swallows offline/errors. Called from the app's Steam-open update flow.
    ///
    /// <para>
    /// Gated on <c>PluginAutoUpdate</c>. This path runs unattended on every Steam open and can replace a
    /// DLL in the Steam root and restart Steam, so it needs an off switch that is not a marker file in a
    /// folder the app also writes to. The check lives HERE rather than at the call sites because there are
    /// two of them (the Steam-open flow and the /check-updates HTTP handler) and a gate that has to be
    /// remembered at each caller is a gate that gets missed by the third one.
    /// </para></summary>
    public async Task<bool> AutoUpdateAsync(CancellationToken ct = default)
    {
        if (!settings.PluginAutoUpdate) return false;
        try
        {
            var st = await GetStatusAsync(force: true, ct);
            if (!st.UpdateAvailable) return false; // not installed, offline, or already current
            var (ok, _) = await InstallAsync(progress: null, ct);
            return ok;
        }
        catch { return false; }
    }

    // ── Uninstall ──
    public Task<(bool ok, string? error)> UninstallAsync(CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            try
            {
                // Read the manifest BEFORE the FrontendDir delete below wipes it — it holds the exact
                // enabledPlugins entries we stripped from Millennium's config at install time.
                var manifest = ReadManifest();

                bool wasRunning = Process.GetProcessesByName("steam").Length > 0;
                steam.StopSteam();
                await Task.Delay(1200, ct);

                foreach (var slot in Slots)
                {
                    if (SlotPath(slot) is { } bp && File.Exists(bp)) File.Delete(bp);
                    if (SlotRealPath(slot) is { } br && File.Exists(br)) File.Delete(br);
                }
                foreach (var legacy in LegacyDllPaths)
                    if (File.Exists(legacy)) File.Delete(legacy);
                if (CdpMarkerPath is { } markerPath) RemoveCdpMarkerJunction(markerPath);
                if (Directory.Exists(FrontendDir)) Directory.Delete(FrontendDir, recursive: true);

                // Give Millennium its luatools plugin back — we're the ones who disabled it. Steam is
                // stopped here, so the edit sticks and applies on the restart below.
                if (MillenniumPresent)
                    SetMillenniumLuatoolsEnabled(enable: true, restore: manifest?.DisabledMillenniumEntries);

                // Stop injecting the now-deleted content immediately, same reasoning as InstallAsync.
                await injector.ReloadPluginFilesAsync();

                if (wasRunning) steam.StartSteam();
                return (true, (string?)null);
            }
            catch (Exception ex) { return (false, (string?)ex.Message); }
        }, ct);
    }

    // ── Millennium coexistence: disable its luatools plugin via config (reversible), not folder-rename ──

    private const string MillenniumPluginName = "luatools";
    private const string MillenniumDisabledName = MillenniumPluginName + ".disabled-by-luatools";

    /// <summary>Options for rewriting Millennium's config. The <see cref="DefaultJsonTypeInfoResolver"/> is
    /// REQUIRED, not cosmetic: re-enabling adds CLR-backed <see cref="JsonValue"/> nodes (from plain strings),
    /// and serializing those through custom options without a resolver throws
    /// "JsonSerializerOptions instance must specify a TypeInfoResolver". Removal-only writes happen to work
    /// without it (all surviving nodes are JsonElement-backed from Parse), so the failure would only ever
    /// surface on uninstall — silently, inside the best-effort catch.</summary>
    private static readonly JsonSerializerOptions ConfigWriteOpts = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>Every place Millennium might keep its config.json, newest first. Legacy Millennium used
    /// <c>&lt;steam&gt;\ext\config.json</c>; current uses <c>&lt;steam&gt;\millennium\config\config.json</c>;
    /// an intermediate build used <c>&lt;steam&gt;\millennium\config.json</c>. A <c>MILLENNIUM__CONFIG_PATH</c>
    /// env var overrides the directory. Both real files can coexist, so callers edit *every* one present.</summary>
    private IEnumerable<string> MillenniumConfigPaths()
    {
        if (SteamDir is not { } s) yield break;
        yield return Path.Combine(s, "millennium", "config", "config.json");
        yield return Path.Combine(s, "millennium", "config.json");
        yield return Path.Combine(s, "ext", "config.json");
        var env = Environment.GetEnvironmentVariable("MILLENNIUM__CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(env))
            yield return Path.Combine(env, "config.json");
    }

    /// <summary>True if an enabledPlugins entry refers to our plugin. The entry's leading space-delimited
    /// token is the plugin id; Millennium versions store it as <c>luatools</c>, <c>luatools LUA</c>
    /// (name + backend), or — from the old rename — <c>luatools.disabled-by-luatools[ LUA]</c>.</summary>
    private static bool IsLuatoolsEntry(string entry)
    {
        string token = entry.Split(' ', 2)[0];
        return token.Equals(MillenniumPluginName, StringComparison.OrdinalIgnoreCase)
            || token.Equals(MillenniumDisabledName, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonArray? NestedEnabled(JsonObject root) =>
        root["plugins"] is JsonObject p ? p["enabledPlugins"] as JsonArray : null;
    private static JsonArray? FlatEnabled(JsonObject root) =>
        root["plugins.enabledPlugins"] as JsonArray; // stray dotted key some Millennium builds also write

    /// <summary>Remove (enable=false) or restore (enable=true) our plugin from <c>plugins.enabledPlugins</c>
    /// in every Millennium config that exists. On disable returns configPath → the exact entry strings
    /// removed, so uninstall can restore them verbatim (passed back in as <paramref name="restore"/>).
    /// Best-effort per file; edits a JSON DOM so all unrelated keys/plugins are preserved.</summary>
    private Dictionary<string, List<string>> SetMillenniumLuatoolsEnabled(
        bool enable, IReadOnlyDictionary<string, List<string>>? restore = null)
    {
        var removedMap = new Dictionary<string, List<string>>();
        foreach (string path in MillenniumConfigPaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) continue;
                bool changed = false;

                if (!enable)
                {
                    // Record only the nested (real) array's removals for restore; the flat dotted key is a
                    // stray duplicate some builds write — scrub it, but never restore into it.
                    var removed = new List<string>();
                    if (NestedEnabled(root) is { } nested)
                        for (int i = nested.Count - 1; i >= 0; i--)
                            if (nested[i]?.GetValue<string>() is { } v && IsLuatoolsEntry(v))
                            {
                                removed.Add(v);
                                nested.RemoveAt(i);
                                changed = true;
                            }
                    if (FlatEnabled(root) is { } flat)
                        for (int i = flat.Count - 1; i >= 0; i--)
                            if (flat[i]?.GetValue<string>() is { } v && IsLuatoolsEntry(v))
                            {
                                flat.RemoveAt(i);
                                changed = true;
                            }
                    if (removed.Count > 0)
                    {
                        removed.Reverse(); // preserve original order
                        removedMap[path] = removed;
                    }
                }
                else
                {
                    var arr = NestedEnabled(root); // restore only into the real nested array
                    if (arr is not null && !arr.Any(n => n?.GetValue<string>() is { } v && IsLuatoolsEntry(v)))
                    {
                        var toAdd = restore is not null && restore.TryGetValue(path, out var r) && r.Count > 0
                            ? r
                            : new List<string> { MillenniumPluginName };
                        foreach (string e in toAdd) arr.Add(e);
                        changed = true;
                    }
                }

                if (changed) File.WriteAllText(path, root.ToJsonString(ConfigWriteOpts));
            }
            catch { /* best effort per file — a locked/invalid config must not fail install/uninstall */ }
        }
        return removedMap;
    }

    /// <summary>Undo the previous folder-rename approach: bring <c>plugins\luatools</c> back to a normal
    /// state so only config governs enablement. If both the real and the renamed-aside folder exist
    /// (Millennium recreated it), drop the stale aside copy; otherwise rename the aside copy back.</summary>
    private static void RestoreMillenniumPluginFolder(string steamDir)
    {
        try
        {
            string dir = Path.Combine(steamDir, "millennium", "plugins");
            string real = Path.Combine(dir, MillenniumPluginName);
            string aside = Path.Combine(dir, MillenniumDisabledName);
            if (!Directory.Exists(aside)) return;
            if (Directory.Exists(real)) Directory.Delete(aside, recursive: true);
            else Directory.Move(aside, real);
        }
        catch { /* best effort */ }
    }

    /// <summary>If plugin.zip wrapped everything in a single top-level folder, hoist that folder's contents
    /// up so <c>public/luatools.js</c> sits directly under FrontendDir.</summary>
    private static void NormalizeFrontendLayout()
    {
        if (Directory.Exists(Path.Combine(FrontendDir, "public"))) return;
        foreach (var sub in Directory.GetDirectories(FrontendDir))
        {
            if (!Directory.Exists(Path.Combine(sub, "public"))) continue;
            foreach (var entry in Directory.GetFileSystemEntries(sub))
            {
                string dest = Path.Combine(FrontendDir, Path.GetFileName(entry));
                if (Directory.Exists(entry)) Directory.Move(entry, dest);
                else File.Move(entry, dest, overwrite: true);
            }
            try { Directory.Delete(sub, recursive: true); } catch { /* leftover wrapper */ }
            return;
        }
    }

    /// <summary>
    /// The plugin flow's archive gate: the same analyser the Fixes page runs, before anything is written.
    /// Returns null to proceed, or the localized refusal to show the user.
    ///
    /// <para>
    /// The digest proves the bytes are what the release published — it says nothing about what expanding
    /// them does. <see cref="ZipFile.ExtractToDirectory(string, string)"/> already refuses path escapes on
    /// its own, but nothing capped entry count or expansion, so a decompression bomb inside a legitimately
    /// published release would fill the user's %AppData%. Screening here also means Plugin and Fixes share
    /// one gate instead of one silently lacking a check the other has.
    /// </para>
    ///
    /// <para>
    /// Split out from <see cref="InstallAsync"/> so it can be tested without a network round-trip or a
    /// Steam install. <paramref name="destinationRoot"/> is a parameter for the same reason, and passing it
    /// is not optional in practice: <see cref="FixAnalyzer.AnalyzeArchive"/> skips the containment and
    /// duplicate-destination checks entirely when it is null, so a null here would quietly turn zip-slip
    /// screening off.
    /// </para>
    ///
    /// <para>
    /// <see cref="LuaScreeningProfile.ApplicationCode"/> is load-bearing. A plugin ships Lua that is a
    /// PROGRAM, not a Steam manifest, and the default manifest profile refuses ordinary language
    /// constructs — this shipped as a bug in 1.4.0, where installing BetterSteamTools failed on
    /// <c>backend/main.lua</c> for using <c>require</c>. Never drop this argument to "simplify" the call.
    /// </para>
    /// </summary>
    internal static string? ScreenPluginArchive(string zipPath, string destinationRoot) =>
        FixAnalyzer.AnalyzeArchive(zipPath, destinationRoot,
            luaProfile: LuaScreeningProfile.ApplicationCode) is { Blocked: true } analysis
            ? string.Format(Resources.Strings.Plugin_Err_ArchiveRejected, PluginZipAsset, analysis.BlockReason)
            : null;

    // ── Helpers ──
    // Hashing/digest parsing live in AssetIntegrity (fail-closed by construction); see that type.
    private static string? AssetDigest(GithubRelease r, string name) =>
        AssetIntegrity.ParseDigest(FindAsset(r, name)?.Digest);

    private static GithubAsset? FindAsset(GithubRelease r, string name) =>
        r.Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
