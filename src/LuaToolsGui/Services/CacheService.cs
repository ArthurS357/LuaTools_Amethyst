using System.IO;
using System.Text.Json;

namespace LuaToolsGui.Services;

/// <summary>App-tracked bookkeeping (not user settings) — install fingerprints, cached digests, etc.</summary>
public class CacheData
{
    // ── OpenSteamTools install fingerprint (Mode page up-to-date check) ──
    public string? OpenSteamToolsInstalledVersion { get; set; }
    public string? OpenSteamToolsInstalledZipDigest { get; set; }

    // ── DepotDownloaderMod install fingerprint ──
    // The release tag of the tool currently extracted under %AppData%\LuaToolsGui\depotdownloader, and when
    // its pin was last checked. The tag is what makes an existing exe REUSABLE: it has to equal
    // AppConfig.DepotDownloaderPinnedTag, so an exe left over from a previous pin is treated as absent
    // rather than run. The timestamp only throttles the GitHub lookup (EnsureToolAsync runs once per depot).
    public string? DepotDownloaderVersion { get; set; }
    public long DepotDownloaderCheckedAtMs { get; set; }

    // ── Steam appdetails rate-limit window (rolling ~200 req / ~200s per IP) ──
    // Unix-ms timestamps of recent requests, so the sliding window survives restarts and we don't
    // burst fresh into a still-counting window.
    public List<long> SteamApiRequestTimes { get; set; } = [];

    // NOTE: `DonatedAppIds` (the dedup list for the key-donation feature) was removed along with the
    // feature itself. A leftover array in an existing cache.json is ignored on load.

    // ── Hardware appid blacklist (refreshed from GitHub, ~14-day TTL) ──
    // Steam "hardware" appids (Deck, Index, controllers, VR) to hide from featured/search.
    public List<long> HardwareAppIds { get; set; } = [];
    public long HardwareAppIdsFetchedAtMs { get; set; } // Unix-ms of last successful fetch; 0 = never

    // ── Loaded-apps notification list (dismissable) ──────────────────
    // Appids the plugin surfaces as "recently loaded" on the store page; cleared on dismiss.
    // Replaces the old Lua backend's loadedappids.txt.
    public List<long> LoadedAppIds { get; set; } = [];

    // ── First-run onboarding ─────────────────────────────────────────
    // True once the user has seen (and dismissed) the welcome overlay, or the app decided they're
    // already set up. Kept in cache (not settings) — it's app bookkeeping, not a user preference.
    public bool OnboardingComplete { get; set; }

    // ── CDP exposure consent ─────────────────────────────────────────
    // True once the user has been told what enabling Steam's remote-debugging bridge means and agreed to
    // it. Null/false = never asked or declined; the marker junction is not created in that case.
    public bool? CdpConsentGranted { get; set; }

    // ── Hubcap manifest freshness tracking ───────────────────────────
    // appid (string) → the source's `file_modified` marker at the time we last downloaded that appid's
    // manifest via Hubcap. An opaque change token, not a parsed date — see ManifestFreshnessPolicy for why.
    public Dictionary<string, string> InstalledManifestFileModified { get; set; } = [];

    // ── Downloads page history ───────────────────────────────────────
    // Finished downloads, newest first, capped by DownloadQueue.MaxHistory. Kept here rather than in
    // settings.json because it is app bookkeeping, not a user choice — and because settings.json is
    // already distributed and its shape must not change. An older cache.json simply has no such array
    // and loads as an empty history.
    public List<Downloads.DownloadHistoryRecord> DownloadHistory { get; set; } = [];
}

/// <summary>
/// Persists internal app state to %AppData%\LuaToolsGui\cache.json — distinct from user-facing
/// settings (SettingsService). Use this for values the app records about itself (versions/hashes
/// of installed components, cached lookups), not for choices the user makes.
/// </summary>
public sealed class CacheService
{
    // Instance paths (were static readonly) so tests can isolate to a temp directory instead of reading
    // and overwriting the developer's real cache.json — see SettingsService for the same reasoning.
    private readonly string _dir;
    private readonly string _filePath;
    private readonly string _tmpPath;

    private static readonly JsonSerializerOptions JsonWriteOptions = new() { WriteIndented = true };

    private CacheData _cache = new();

    public CacheService() : this(SettingsService.DefaultDirectory) { }

    /// <summary>Test seam: use an isolated directory instead of the user's roaming profile.</summary>
    internal CacheService(string directory)
    {
        _dir = directory;
        _filePath = Path.Combine(directory, "cache.json");
        _tmpPath = _filePath + ".tmp";
        Load();
    }

    /// <summary>OpenSteamTools: the release tag last installed (for the up-to-date check).</summary>
    public string? OpenSteamToolsInstalledVersion
    {
        get => _cache.OpenSteamToolsInstalledVersion;
        set { _cache.OpenSteamToolsInstalledVersion = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>OpenSteamTools: sha256 of the release zip last installed (inner DLL hashes aren't published).</summary>
    public string? OpenSteamToolsInstalledZipDigest
    {
        get => _cache.OpenSteamToolsInstalledZipDigest;
        set { _cache.OpenSteamToolsInstalledZipDigest = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>DepotDownloaderMod: the release tag last extracted, checked against the compiled-in pin.</summary>
    public string? DepotDownloaderVersion
    {
        get => _cache.DepotDownloaderVersion;
        set { _cache.DepotDownloaderVersion = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>DepotDownloaderMod: Unix-ms of the last pin check (0 = never), for the lookup throttle.</summary>
    public long DepotDownloaderCheckedAtMs
    {
        get => _cache.DepotDownloaderCheckedAtMs;
        set { _cache.DepotDownloaderCheckedAtMs = value; Save(); }
    }

    // The collection accessors below all hand back a COPY and mutate under SaveLock. Previously they
    // exposed the live List<T> and swapped it outside any lock, so a background writer (the plugin's
    // headless add, the hardware-appid refresh) could mutate a list while another thread — or
    // JsonSerializer inside Save() — was walking it, which surfaces as
    // "Collection was modified; enumeration operation may not execute".

    /// <summary>Unix-ms timestamps of recent Steam appdetails requests (rolling rate-limit window).</summary>
    public IReadOnlyList<long> GetSteamApiRequestTimes()
    {
        lock (SaveLock) return _cache.SteamApiRequestTimes.ToList();
    }

    /// <summary>Persist the current rate-limit window (called periodically by the limiter, not per request).</summary>
    public void SaveSteamApiRequestTimes(IEnumerable<long> times)
    {
        var snapshot = times.ToList(); // materialize OUTSIDE the lock — the caller's sequence may be lazy
        lock (SaveLock)
        {
            _cache.SteamApiRequestTimes = snapshot;
            SaveCore();
        }
    }

    // ── Hardware appid blacklist ─────────────────────────────────────

    /// <summary>Cached hardware appids to filter out of featured/search.</summary>
    public IReadOnlyList<long> GetHardwareAppIds()
    {
        lock (SaveLock) return _cache.HardwareAppIds.ToList();
    }

    /// <summary>Unix-ms of the last successful blacklist fetch (0 = never), for the TTL check.</summary>
    public long GetHardwareAppIdsFetchedAt() => _cache.HardwareAppIdsFetchedAtMs;

    public void SaveHardwareAppIds(IEnumerable<long> ids, long fetchedAtMs)
    {
        var snapshot = ids.Distinct().ToList();
        lock (SaveLock)
        {
            _cache.HardwareAppIds = snapshot;
            _cache.HardwareAppIdsFetchedAtMs = fetchedAtMs;
            SaveCore();
        }
    }

    // ── Loaded-apps notification list ────────────────────────────────

    /// <summary>Appids surfaced as "recently loaded" on the store page (dismissable).</summary>
    public IReadOnlyList<long> GetLoadedAppIds()
    {
        lock (SaveLock) return _cache.LoadedAppIds.ToList();
    }

    public void SaveLoadedAppIds(IEnumerable<long> ids)
    {
        var snapshot = ids.Distinct().ToList();
        lock (SaveLock)
        {
            _cache.LoadedAppIds = snapshot;
            SaveCore();
        }
    }

    /// <summary>Clear the loaded-apps notification list (ReadLoadedApps → DismissLoadedApps).</summary>
    public void ClearLoadedAppIds()
    {
        lock (SaveLock)
        {
            _cache.LoadedAppIds = [];
            SaveCore();
        }
    }

    /// <summary>Remove a single appid from the loaded-apps notification list (e.g. after its Lua is
    /// removed) so a removed game doesn't linger in the "recently added" popup.</summary>
    public void RemoveLoadedAppId(long id)
    {
        lock (SaveLock)
        {
            if (_cache.LoadedAppIds.Remove(id))
                SaveCore();
        }
    }

    /// <summary>True once the first-run onboarding overlay has been completed (or auto-skipped because the
    /// user is already set up). Persisted so it shows at most once.</summary>
    public bool OnboardingComplete
    {
        get => _cache.OnboardingComplete;
        set { _cache.OnboardingComplete = value; Save(); }
    }

    /// <summary>True once the user has explicitly agreed to LuaTools enabling Steam's remote-debugging
    /// bridge. See <see cref="PluginInstallerService"/> for what that exposes and why consent is required
    /// before the marker junction is created.</summary>
    public bool CdpConsentGranted
    {
        get => _cache.CdpConsentGranted ?? false; // default: NOT granted
        set { _cache.CdpConsentGranted = value; Save(); }
    }

    // ── Hubcap manifest freshness tracking ───────────────────────────

    /// <summary>The `file_modified` token recorded for this appid's last Hubcap download, or null if
    /// nothing is on record. Feeds <see cref="ManifestFreshnessPolicy.IsStale"/>.</summary>
    public string? GetInstalledManifestFileModified(string appid)
    {
        lock (SaveLock) return _cache.InstalledManifestFileModified.GetValueOrDefault(appid);
    }

    /// <summary>Record the `file_modified` token describing what was just downloaded for this appid.</summary>
    public void SaveInstalledManifestFileModified(string appid, string fileModified)
    {
        lock (SaveLock)
        {
            _cache.InstalledManifestFileModified[appid] = fileModified;
            SaveCore();
        }
    }

    // ── Downloads page history ───────────────────────────────────────

    /// <summary>Finished downloads from previous sessions, for the Downloads page's history list.</summary>
    public IReadOnlyList<Downloads.DownloadHistoryRecord> GetDownloadHistory()
    {
        lock (SaveLock) return _cache.DownloadHistory.ToList();
    }

    /// <summary>
    /// Replace the whole history. Called on every finish, clear and per-row removal, so both kinds of
    /// clearing reach disk through the same path rather than only pruning the in-memory list.
    /// </summary>
    /// <remarks>
    /// Each record is sanitized on the way in — see <see cref="Downloads.DownloadHistoryRecord.Sanitized"/>
    /// for why a failure message is not trusted to be free of credentials.
    /// </remarks>
    public void SaveDownloadHistory(IEnumerable<Downloads.DownloadHistoryRecord> records)
    {
        var snapshot = records.Select(r => r.Sanitized()).ToList(); // materialize OUTSIDE the lock
        lock (SaveLock)
        {
            _cache.DownloadHistory = snapshot;
            SaveCore();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
                _cache = JsonSerializer.Deserialize<CacheData>(File.ReadAllText(_filePath)) ?? new CacheData();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Truncated/corrupt/unreadable — start from defaults rather than failing construction. The
            // atomic write in SaveCore is what keeps this from happening in the first place.
            _cache = new CacheData();
        }
    }

    /// <summary>Serializes writers. Like <see cref="SettingsService"/> this is a DI singleton written from
    /// multiple threads at once — <c>LuaInstaller.RecordLoaded</c> (background, from the plugin's headless
    /// add), <c>HardwareAppIdService.EnsureFreshAsync</c> (background), the Steam appdetails rate limiter,
    /// and the UI thread. Unsynchronised File.WriteAllText calls to the same path threw IOException.
    /// <para>Per-instance, so isolated instances (tests) don't contend on one another's lock.</para></summary>
    private readonly object SaveLock = new();

    private void Save()
    {
        lock (SaveLock) { SaveCore(); }
    }

    private void SaveCore()
    {
        bool empty = _cache.OpenSteamToolsInstalledVersion is null
            && _cache.OpenSteamToolsInstalledZipDigest is null
            && _cache.DepotDownloaderVersion is null
            && _cache.DepotDownloaderCheckedAtMs == 0
            && _cache.SteamApiRequestTimes.Count == 0
            && _cache.HardwareAppIds.Count == 0
            && _cache.HardwareAppIdsFetchedAtMs == 0
            && _cache.LoadedAppIds.Count == 0
            && !_cache.OnboardingComplete
            && _cache.CdpConsentGranted is null
            && _cache.InstalledManifestFileModified.Count == 0
            && _cache.DownloadHistory.Count == 0;
        if (empty)
        {
            foreach (string p in new[] { _filePath, _tmpPath })
                try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { /* best effort */ }
            return;
        }

        Directory.CreateDirectory(_dir);

        // Atomic write (temp file + rename), matching SettingsService. A plain WriteAllText here meant a
        // kill mid-write left a truncated cache.json — Load() then swallowed the parse error and silently
        // reset everything, losing the permanent DonatedAppIds dedup list (so keys get re-donated) along
        // with the Steam API rate-limit window. Guarded for the same reason Save() is over there.
        try
        {
            string json = JsonSerializer.Serialize(_cache, JsonWriteOptions);
            File.WriteAllText(_tmpPath, json);
            File.Move(_tmpPath, _filePath, overwrite: true);
        }
        catch (IOException) { /* locked — in-memory state stands, retry on the next Save */ }
        catch (UnauthorizedAccessException) { /* denied — same */ }
    }
}
