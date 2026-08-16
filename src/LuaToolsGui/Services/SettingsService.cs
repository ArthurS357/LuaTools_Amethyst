using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LuaToolsGui.Services;

public class AppSettings
{
    public string? SteamPathOverride { get; set; }

    // ── Unlocker mode (Mode page) — user's chosen backend ────────────
    public string? SelectedMode { get; set; }                  // "SteamTools" | "OpenSteamTools"

    // ── Install behavior ─────────────────────────────────────────────
    // When true (default), installs comment out setManifestid() lines and skip copying .manifest
    // files, so games aren't pinned to a version and Steam keeps them updated. Nullable so we can
    // tell "never set" (→ default ON) from an explicit user choice.
    public bool? AutoUpdateApps { get; set; }

    // NOTE: `DonateKeys` used to live here (default ON). The feature that read it — uploading Steam
    // decryption keys over plain HTTP — was removed, so the setting went with it. A leftover value in an
    // existing settings.json is simply ignored on load.

    // Manage-page results-per-page. 0 = "All" (single infinite scroll). Nullable so "never set"
    // (→ default 24) is distinguishable from an explicit choice.
    public int? ManagePageSize { get; set; }

    // Fixes-page results-per-page. 0 = "All" (single infinite scroll). Nullable so "never set"
    // (→ default 24) is distinguishable from an explicit choice.
    public int? FixesPageSize { get; set; }

    // Builds-page game-list results-per-page. 0 = "All". Kept separate from ManagePageSize — the Builds
    // list is a narrow sidebar, so a size that suits the Manage grid rarely suits both.
    public int? BuildsPageSize { get; set; }

    // UI language as a BCP-47 tag ("en", "zh-Hans"). Null = follow the Windows display language.
    public string? Language { get; set; }

    // The user's own Hubcap (hubcapmanifest.com) API key. Null = not configured; key-gated sources stay
    // locked until set. NOTE: this is the ON-DISK form — a DPAPI-protected blob prefixed "dpapi:", not the
    // raw "smm_…" key. Always go through SettingsService.HubcapApiKey, which handles protect/unprotect and
    // the one-time migration of keys written before encryption existed; reading this field directly gets
    // you ciphertext.
    public string? HubcapApiKey { get; set; }

    // When true, register the app to launch on Windows sign-in (HKCU …\Run). Nullable so "never set"
    // (→ default OFF) is distinguishable from an explicit choice.
    public bool? StartWithWindows { get; set; }

    // When true, minimizing hides the window to the system tray instead of the taskbar. Nullable so
    // "never set" (→ default OFF) is distinguishable from an explicit choice.
    public bool? MinimizeToTray { get; set; }

    // When true, FastFetch auto-picks the first available source and downloads immediately.
    // Nullable so "never set" (→ default OFF) is distinguishable from an explicit choice.
    public bool? FastFetch { get; set; }

    // ── Cleartext source-availability lookup (see AppConfig.ManifestBackendUrl) ──────────────
    // When true (the DEFAULT), LuaTools may call GET {ManifestBackendUrl}/check_apis?appid=… to find out
    // which manifest sources have a given game.
    //
    // Default ON is a deliberate, and slightly uncomfortable, trade-off. The call is plain HTTP, so the
    // appid is visible on the network path. But it is also the DISCOVERY step for downloading: the source
    // list the user picks from IS its response. Turning it off by default would leave most users with no
    // sources at all except the key-gated Hubcap one — i.e. it would disable the app's main function to
    // hide an appid. So it stays on, is disclosed (InsecureMetadataNotice below), and can be switched off
    // by anyone who prefers the privacy to the feature.
    //
    // Set false to make LuaTools never contact that host. Downloading still works for sources that don't
    // need it (Hubcap with your own API key, drag-and-drop .lua/.zip, luatools:// links).
    public bool? EnableSourceAvailabilityChecks { get; set; }

    // How loudly to disclose that lookup: "once" (default, one notice per session), "always" (before
    // every call), or "off". Nullable so "never set" (→ "once") is distinguishable from an explicit
    // choice. Parsed by InsecureMetadataNoticeModeExtensions; an unrecognised value falls back to "once"
    // rather than silently disabling the disclosure.
    public string? InsecureMetadataNotice { get; set; }

    // LEGACY (v1.3.0 pre-release): superseded by InsecureMetadataNotice. Kept so an existing
    // "WarnOnInsecureMetadata": false keeps meaning "don't nag me" instead of silently reverting to
    // notices. Only consulted when InsecureMetadataNotice is unset.
    public bool? WarnOnInsecureMetadata { get; set; }

    // ── App self-update feed (advanced; no UI, edit settings.json by hand) ──────────────
    // GitHub repos publishing this fork's OWN Velopack releases, in priority order. EMPTY/UNSET = the app
    // never checks for updates of itself and makes no request. There is no compiled-in default on purpose:
    // upstream's feed publishes the official build, which would restore telemetry and DonateKeys.
    // Entries are validated by AppUpdateSources — https://github.com/<owner>/<repo> only, and the upstream
    // repos are refused outright. Unrelated to plugin/unlocker/manifest downloads, which have their own
    // sources and are unaffected either way.
    public string[]? AppUpdateRepos { get; set; }

    // ── Store-page plugin auto-update (advanced; no UI, edit settings.json by hand) ──
    // When true, an already-installed plugin is updated silently whenever Steam opens. That flow replaces
    // a DLL in the Steam ROOT (steam.exe loads it) and stops/restarts Steam to do it, all unattended.
    // AppUpdateRepos only governs the app updating ITSELF; before this key the only brake was a
    // `.luatools-dll-update-disabled` marker file in the Steam folder, which covers only the DLL half and
    // is documented in the code as a testing switch.
    //
    // DEFAULT OFF, which is a deliberate behaviour change from earlier builds. Placing an unreviewed
    // binary next to steam.exe and restarting Steam is the most powerful thing this app does, and it was
    // happening with no prompt and no way to decline. Opting in is a one-line edit; opting out after the
    // fact is not, because by then the DLL is already swapped. Update still happens the moment the user
    // presses Install/Update on the Plugin page, so nothing becomes unreachable — it stops being silent.
    //
    // Nullable so "never set" (→ default OFF) is distinguishable from an explicit choice.
    public bool? PluginAutoUpdate { get; set; }

    // ── GitHub mirror overrides (advanced; no UI, edit settings.json by hand) ──
    // The defaults are public third-party proxies used only when github.com is unreachable. Set either
    // list to [] to disable mirrors entirely (direct connections only), or to your own https prefixes.
    // Null = keep the compiled-in default. Each entry is a PREFIX and must end with "/".
    // See GithubMirrors for the full explanation.
    public string[]? GithubDownloadMirrors { get; set; }
    public string[]? GithubApiMirrors { get; set; }
}

public sealed class SettingsService
{
    /// <summary>The app's real storage location: %AppData%\LuaToolsGui.</summary>
    internal static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui");

    // Instance (not static) so tests can point an instance at a temp folder. These were static readonly,
    // which meant any test of the load/save/migrate logic would read and overwrite the developer's real
    // settings.json — so that logic simply went untested.
    private readonly string _dir;
    private readonly string _filePath;
    private readonly string _tmpPath;
    private readonly string _bakPath;

    /// <summary>Cached: JsonSerializerOptions is expensive to construct and was being allocated on every
    /// single Save().</summary>
    private static readonly JsonSerializerOptions JsonWriteOptions = new() { WriteIndented = true };

    private AppSettings _settings = new();

    public SettingsService() : this(DefaultDirectory) { }

    /// <summary>Test seam: use an isolated directory instead of the user's roaming profile.</summary>
    internal SettingsService(string directory)
    {
        _dir = directory;
        _filePath = Path.Combine(directory, "settings.json");
        _tmpPath = _filePath + ".tmp";
        _bakPath = _filePath + ".bak";

        Load();
        MigrateHubcapKey();

        // Publish any mirror override to the static holder GithubProxy.Candidates reads. Done here because
        // this service is constructed first in the DI graph, and because Velopack's downloader reaches
        // Candidates from a context with no DI scope. No-op when neither key is present.
        GithubMirrors.Configure(_settings.GithubDownloadMirrors, _settings.GithubApiMirrors);
    }

    /// <summary>User-chosen Steam folder. Null = auto-detect from registry. Persisted only when set.</summary>
    public string? SteamPathOverride
    {
        get => _settings.SteamPathOverride;
        set
        {
            _settings.SteamPathOverride = string.IsNullOrWhiteSpace(value) ? null : value;
            Save();
        }
    }

    /// <summary>Selected unlocker backend ("SteamTools" | "OpenSteamTools"), or null if never chosen.</summary>
    public string? SelectedMode
    {
        get => _settings.SelectedMode;
        set { _settings.SelectedMode = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>When true (default), installs don't lock manifests so apps keep auto-updating.</summary>
    public bool AutoUpdateApps
    {
        get => _settings.AutoUpdateApps ?? true; // default ON
        set { _settings.AutoUpdateApps = value; Save(); }
    }

    /// <summary>Manage-page results-per-page (default 24). 0 = "All" (single infinite scroll).</summary>
    public int ManagePageSize
    {
        get => _settings.ManagePageSize ?? 24; // default 24
        set { _settings.ManagePageSize = value; Save(); }
    }

    /// <summary>Fixes-page results-per-page (default 24). 0 = "All" (single infinite scroll).</summary>
    public int FixesPageSize
    {
        get => _settings.FixesPageSize ?? 24; // default 24
        set { _settings.FixesPageSize = value; Save(); }
    }

    /// <summary>Builds-page game-list results-per-page (default 10). 0 = "All". Smaller than the other
    /// pages' 24 — this list is a narrow sidebar next to the build detail, not a full-width grid.</summary>
    public int BuildsPageSize
    {
        get => _settings.BuildsPageSize ?? 10; // default 10
        set { _settings.BuildsPageSize = value; Save(); }
    }

    /// <summary>UI language tag ("en" | "zh-Hans"), or null to follow the Windows display language.</summary>
    public string? Language
    {
        get => _settings.Language;
        set { _settings.Language = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>
    /// The user's Hubcap API key ("smm_…"), or null if not configured.
    /// <para>
    /// Stored DPAPI-protected (CurrentUser), not as plain text. It is a live credential that authenticates
    /// paid downloads against hubcapmanifest.com, and settings.json is a world-readable file in the user's
    /// roaming profile — so it was previously readable by anything that could open that file, including
    /// anything that syncs the roaming profile off the machine. This now matches how auth.dat has always
    /// treated the Supabase session. See <see cref="MigrateHubcapKey"/> for existing plaintext keys.
    /// </para>
    /// </summary>
    public string? HubcapApiKey
    {
        get => Unprotect(_settings.HubcapApiKey);
        set
        {
            _settings.HubcapApiKey = string.IsNullOrWhiteSpace(value) ? null : Protect(value);
            Save();
        }
    }

    /// <summary>Marks a value as DPAPI-protected. Its absence is what identifies a pre-migration
    /// plaintext key, so it must never change.</summary>
    private const string ProtectedPrefix = "dpapi:";

    private static string Protect(string plain)
    {
        try
        {
            byte[] enc = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return ProtectedPrefix + Convert.ToBase64String(enc);
        }
        catch
        {
            // DPAPI unavailable (unusual, but possible on a broken profile). Storing the key as-is keeps
            // the feature working; losing the user's key would not.
            return plain;
        }
    }

    private static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (!stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            return stored; // written before the migration, or by a fallback Protect() above

        try
        {
            byte[] plain = ProtectedData.Unprotect(
                Convert.FromBase64String(stored[ProtectedPrefix.Length..]), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            // Copied from another user/machine, or corrupt — undecryptable here. Report "not configured"
            // so Settings prompts for the key again rather than sending garbage to Hubcap.
            return null;
        }
    }

    /// <summary>One-time upgrade of an existing plaintext key to the protected form, on first load after
    /// this change. Runs before anything can read the property, and is a no-op afterwards.</summary>
    private void MigrateHubcapKey()
    {
        string? stored = _settings.HubcapApiKey;
        if (string.IsNullOrEmpty(stored) || stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            return;
        _settings.HubcapApiKey = Protect(stored);
        Save();
    }

    /// <summary>When true, the app is registered to launch on Windows sign-in (default OFF).</summary>
    public bool StartWithWindows
    {
        get => _settings.StartWithWindows ?? false; // default OFF
        set { _settings.StartWithWindows = value; Save(); }
    }

    /// <summary>When true, minimizing hides the window to the system tray (default OFF).</summary>
    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray ?? false; // default OFF
        set { _settings.MinimizeToTray = value; Save(); }
    }

    /// <summary>When true, FastFetch auto-picks the first available source and downloads immediately (default OFF).</summary>
    public bool FastFetch
    {
        get => _settings.FastFetch ?? false; // default OFF
        set { _settings.FastFetch = value; Save(); }
    }

    /// <summary>
    /// Whether LuaTools may perform the cleartext source-availability lookup (default ON). Turning it off
    /// stops the app contacting that host at all, at the cost of the source list it produces — see the
    /// field comment on <see cref="AppSettings.EnableSourceAvailabilityChecks"/>.
    /// </summary>
    public bool EnableSourceAvailabilityChecks
    {
        get => _settings.EnableSourceAvailabilityChecks ?? true; // default ON — it is the download discovery step
        set { _settings.EnableSourceAvailabilityChecks = value; Save(); }
    }

    /// <summary>How often to disclose that lookup. Default <see cref="InsecureMetadataNoticeMode.Once"/>.</summary>
    public InsecureMetadataNoticeMode InsecureMetadataNotice =>
        InsecureMetadataNoticeModeExtensions.ParseNoticeMode(
            _settings.InsecureMetadataNotice, _settings.WarnOnInsecureMetadata);

    /// <summary>
    /// Repos publishing this fork's own Velopack releases. Empty = self-update disabled (the default);
    /// entries are validated and upstream repos refused by <see cref="AppUpdateSources"/>.
    /// </summary>
    public string[]? AppUpdateRepos => _settings.AppUpdateRepos;

    /// <summary>
    /// Whether the store-page plugin may update itself unattended on Steam open (default OFF). See the
    /// field comment on <see cref="AppSettings.PluginAutoUpdate"/> for why this is separate from
    /// <see cref="AppUpdateRepos"/> and why the default is off.
    /// </summary>
    public bool PluginAutoUpdate
    {
        get => _settings.PluginAutoUpdate ?? false; // default OFF — silent DLL swaps require opt-in
        set { _settings.PluginAutoUpdate = value; Save(); }
    }

    private void Load()
    {
        // Prefer the primary file; fall back to the last-good .bak. Crucially, NEVER silently reset a
        // corrupt-but-present file to defaults (a later Save would then overwrite it and lose real data) —
        // move it aside to .corrupt so it's preserved and can't be clobbered.
        if (TryLoad(_filePath)) return;
        PreserveCorrupt(_filePath);
        if (TryLoad(_bakPath)) return;
        _settings = new AppSettings();
    }

    private bool TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) is { } loaded)
            {
                _settings = loaded;
                return true;
            }
        }
        catch { /* missing / truncated / invalid JSON → caller falls through */ }
        return false;
    }

    /// <summary>A present-but-unparseable settings file is moved aside (not deleted) so its contents survive
    /// for manual recovery and a subsequent Save can't overwrite it.</summary>
    private static void PreserveCorrupt(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch { /* best effort */ }
    }

    /// <summary>Serializes writers. This is a DI SINGLETON written from several threads — the UI thread
    /// (settings toggles), and background work such as <see cref="UnlockerService"/>'s install/detect path
    /// setting SelectedMode. Two overlapping Save() calls previously raced on the same temp file and the
    /// File.Move, throwing IOException out of a plain property setter with no global handler to catch it.
    /// <para>Per-instance (not static): the lock guards <em>this</em> instance's file, and a static lock
    /// would serialise unrelated instances against each other — which in the test suite means two isolated
    /// temp directories contending for one lock.</para></summary>
    private readonly object _saveLock = new();

    private void Save()
    {
        lock (_saveLock) { SaveCore(); }
    }

    private void SaveCore()
    {
        // Nothing worth persisting → don't leave a settings file behind.
        // Every persisted field must be listed here. A field left out is treated as "nothing worth
        // keeping", so a user whose ONLY change was that setting gets the file deleted and the setting
        // silently lost on the next save. (FixesPageSize was missing.)
        bool empty = _settings.SteamPathOverride is null
            && _settings.SelectedMode is null
            && _settings.AutoUpdateApps is null
            && _settings.ManagePageSize is null
            && _settings.FixesPageSize is null
            && _settings.BuildsPageSize is null
            && _settings.Language is null
            && _settings.HubcapApiKey is null
            && _settings.StartWithWindows is null
            && _settings.MinimizeToTray is null
            && _settings.FastFetch is null
            && _settings.EnableSourceAvailabilityChecks is null
            && _settings.InsecureMetadataNotice is null
            && _settings.WarnOnInsecureMetadata is null
            && _settings.AppUpdateRepos is null
            && _settings.PluginAutoUpdate is null
            && _settings.GithubDownloadMirrors is null
            && _settings.GithubApiMirrors is null;
        if (empty)
        {
            foreach (string p in new[] { _filePath, _bakPath, _tmpPath })
                try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { /* best effort */ }
            return;
        }

        Directory.CreateDirectory(_dir);
        string json = JsonSerializer.Serialize(_settings, JsonWriteOptions);

        // Atomic write: fill a temp file, then rename it over the target. A crash/kill mid-write can only
        // ever truncate the .tmp — the live settings.json is replaced by an atomic move (same-volume rename)
        // and is therefore never left half-written. (This class of loss is exactly what a forced kill during
        // a plain WriteAllText caused.) A .bak of the last good file is kept as a second recovery source.
        //
        // Guarded: a transient lock (AV scanner, indexer, backup tool) on settings.json must not throw out
        // of what looks to callers like a plain property assignment. The value is already live in memory;
        // losing the persist is a much smaller problem than taking down the app.
        try
        {
            File.WriteAllText(_tmpPath, json);
            try { if (File.Exists(_filePath)) File.Copy(_filePath, _bakPath, overwrite: true); }
            catch (IOException) { /* best effort — the .bak is a secondary recovery source */ }
            File.Move(_tmpPath, _filePath, overwrite: true);
        }
        catch (IOException) { /* locked — keep the in-memory value, retry on the next Save */ }
        catch (UnauthorizedAccessException) { /* denied — same */ }
    }
}
