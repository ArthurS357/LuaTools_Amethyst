namespace LuaToolsGui;

/// <summary>
/// Compiled-in client configuration. The Supabase URL and anon key are public
/// client values (they also ship in the lua.tools web bundle).
/// </summary>
public static class AppConfig
{
    public const string SupabaseUrl = "https://db.lua.tools";

    public const string SupabaseAnonKey =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE3NzYwMzkzNzYsImV4cCI6MTg5MzQ1NjAwMCwicm9sZSI6ImFub24iLCJpc3MiOiJzdXBhYmFzZSJ9.f_-K38u3odjltP-g_67FVmG32Vg-_-k-lNBvIaVUVBM";

    public const string ApiBaseUrl = "https://lua.tools";

    // Bot-provisioned (Discord /login placeholder) accounts use this email domain. Detecting it on
    // startup lets the app prompt the user to re-link their full lua.tools account.
    public const string BotAccountEmailDomain = "@bot.lua.tools";

    // Hubcap (hubcapmanifest.com) — the app talks to this directly with the user's own API key
    // (no lua.tools proxy). Key + stats are managed in Settings; key-gated source downloads hit it.
    public const string HubcapBaseUrl = "https://hubcapmanifest.com";

    /// <summary>Must be registered in Supabase Auth → Redirect URLs.</summary>
    public const int OAuthCallbackPort = 53789;
    public const string OAuthCallbackUrl = "http://localhost:53789/callback";

    // The standard lua.tools daily download cap (Hubcap-keyed downloads are exempt). Hardcoded here
    // because the web app enforces it inline with no API field exposing it; change in one place if it moves.
    public const int DailyDownloadLimit = 25;

    // Public upstream APIs the app calls directly (no lua.tools proxy needed for guest browsing).
    public const string SteamStoreSearchUrl = "https://store.steampowered.com/api/storesearch/";
    // Steam's storefront "featured categories" (top sellers, new releases, etc.) — drives the Add page's
    // featured strips. Public, no auth.
    public const string SteamFeaturedUrl = "https://store.steampowered.com/api/featuredcategories";

    // Community list of Steam "hardware" appids (Steam Deck, Index, controllers, VR headsets). Fetched
    // via GithubProxy (raw.githubusercontent.com → mirror fallback) and cached ~14 days, to filter
    // hardware out of featured/search. Array of { "appid": <long>, "name": ... } objects.
    public const string HardwareAppIdListUrl =
        "https://raw.githubusercontent.com/jsnli/steamappidlist/master/data/hardware_appid.json";

    // Steamless (atom0s) — strips SteamStub DRM from a game's .exe. Downloaded via GithubProxy and
    // cached locally; the "Remove Steam DRM" Manage action runs Steamless.CLI.exe against the game's exe.
    public const string SteamlessRepo = "atom0s/Steamless";

    // ── Steamless pinned digest ──────────────────────────────────────
    // GitHub only started populating the release-asset `digest` field around mid-2025. Steamless
    // v3.1.0.5 was published 2024-03-30 and its asset reports `"digest": null`, so the (deliberately
    // fail-closed) verification in AssetIntegrity has nothing to compare against and the whole
    // "Remove Steam DRM" feature refuses to run.
    //
    // Rather than weaken the check, the expected hash is pinned HERE, in the app's own source. That moves
    // the trust anchor from "whatever the release metadata says" to "what this build was compiled with" —
    // strictly stronger than a digest served alongside the download, and immune to the mirror-controls-
    // both-fields problem, because a mirror cannot change a compiled-in constant.
    //
    // Verified by two independent direct-TLS downloads from github.com on 2026-08-15 (610,646 bytes).
    // SteamlessService uses this ONLY when the API reports no digest at all — a digest that is present
    // but wrong or malformed still fails, and it applies to this one asset name only.
    // When atom0s publishes a newer release, update BOTH fields together (or drop them once the new
    // release carries a real digest).
    public const string SteamlessPinnedAssetName = "Steamless.v3.1.0.5.-.by.atom0s.zip";
    public const string SteamlessPinnedSha256 =
        "e3e2d22e098ff3fb359b2876aa2bed9596f0501e6ff588cbffae90a76d2dc4f5";

    // ── Mode (unlocker) sources ──────────────────────────────────────
    // Every repository a Mode card downloads a Steam-root DLL or an executed CLI from. They live here, not
    // inline in UnlockerService, because each one is now also the PIN that GithubProxy.IsAssetUrlForRepo
    // checks a download URL against — a repo name that appears in two places is a repo name that can drift
    // out of step with the thing verifying it. "verynotsusdllsthataredefnotstrelated" was written out three
    // separate times before this.
    public const string SteamToolsOwner = "mendy-tools";
    public const string SteamToolsRepo = "verynotsusdllsthataredefnotstrelated";
    public const string OpenSteamToolsOwner = "OpenSteam001";
    public const string OpenSteamToolsRepo = "OpenSteamTool";
    public const string OstNightlyOwner = "madoiscool";
    public const string OstNightlyRepo = "OST-Nightly";

    // CloudRedirect (Selectively11) — the Mode page "Manage" button downloads the latest CloudRedirect.exe
    // GUI manager from here and launches it. (Separate from the CLI fixer used by the mode install flow.)
    public const string CloudRedirectOwner = "Selectively11";
    public const string CloudRedirectRepoName = "CloudRedirect";
    public const string CloudRedirectRepo = CloudRedirectOwner + "/" + CloudRedirectRepoName;
    // ── Manifest backend: HTTP only, and it has to stay that way ─────
    // WARNING: this is plain HTTP to a bare IP. It is used by exactly one call —
    // LuaToolsApiClient.CheckSourcesAsync → GET /check_apis?appid=<id> — which carries NO credential and
    // NO secret. The residual risk is a METADATA LEAK: an observer on the network path (or anyone able to
    // spoof the reply) learns which Steam appid the user is checking, and can forge the availability
    // answer. That is a privacy wart, not a key-disclosure bug, which is why the call is allowed to stay
    // while DonateKeys below is not. Never send anything secret over this constant.
    //
    // TLS probe, 2026-08-16 (re-probed the same day before release — unchanged) — HTTPS is NOT available:
    //   • :443 completes a TLS1.3 handshake but serves Traefik's built-in placeholder certificate
    //     (CN=TRAEFIK DEFAULT CERT, self-signed, SAN "<hash>.<hash>.traefik.default"). That is what
    //     Traefik returns when NO certificate is provisioned for the requested host, so there is nothing
    //     to validate against and no way to authenticate the peer.
    //   • The application routes are not even bound to the TLS entrypoint: /check_apis and /<appid>
    //     answer 200 over http:// and 404 over https://. HTTPS does not serve this API at all.
    //   • No alternate TLS port is open (8443, 4443, 8080 all refuse the connection).
    //   • The host is a bare Hetzner box (static.108.229.235.167.clients.your-server.de) reached by IP
    //     with no domain name, so a publicly trusted certificate cannot simply be pointed at it either.
    // Moving this constant to https:// would therefore break source-availability checks outright. Re-probe
    // if the operator ever puts a real certificate and a domain in front of it; until then it stays http://.
    //
    // Because it cannot be fixed, it is at least made VISIBLE: LuaToolsApiClient.CheckSourcesAsync raises
    // InsecureTransportNotice once per session before the request, so the metadata cost is disclosed rather
    // than silent. The notice never blocks the call, and users can opt out with
    // "WarnOnInsecureMetadata": false in settings.json.
    public const string ManifestBackendUrl = "http://167.235.229.108";
    public const string ManifestBackendUserAgent = "secretgoonpoon";

    // ── Removed: outbound data collection ────────────────────────────
    // Two upstream features that sent data off this machine were removed in this fork:
    //
    //   * DonateKeys — uploaded per-depot DecryptionKeys scraped from Steam's config.vdf to
    //     <ManifestBackendUrl>/donatekeys/send. It was ON BY DEFAULT and the transport was plain HTTP to
    //     a bare IP, so live decryption keys crossed the network unencrypted and readable by any observer
    //     on the path. (Its DonateKeysUserAgent constant went with it.)
    //   * Umami telemetry — an unconditional per-launch ping to analytics.lua.tools with no opt-out
    //     anywhere in the UI, deliberately sending a spoofed Chrome User-Agent to get past Umami's own
    //     bot filter.
    //
    // Nothing replaced them: this build makes no analytics or key-donation request at all.
    //
    // ── Why DonateKeys was NOT reinstated over HTTPS (re-evaluated 2026-08-16) ──
    // Reinstating it was considered and rejected on evidence, not on preference. The plan was "HTTPS only,
    // no HTTP fallback"; the server cannot hold up its end of that:
    //
    //   1. There is no usable TLS here at all — see the probe recorded above. /donatekeys/send answers
    //      401 over http:// but 404 over https://, i.e. the endpoint exists only on the cleartext
    //      entrypoint. There is no HTTPS URL that would reach it.
    //   2. The only way to "use https://" against this host would be to pin or ignore that self-signed
    //      placeholder certificate. Both defeat the point: without a trusted chain the client cannot tell
    //      the real server from anyone able to intercept the connection, so the decryption keys would be
    //      handed to whoever answers. That is strictly worse than plain HTTP, because the padlock implies
    //      a protection that is not there.
    //   3. The payload is the exact category this fork refuses to put on the wire unauthenticated: live
    //      per-depot DecryptionKeys read out of the user's own Steam config.vdf.
    //
    // So the feature stays removed, along with its UI toggle and its DonateKeysUserAgent constant. This is
    // a server capability gap, not a decision that needs revisiting in code: if the operator publishes the
    // endpoint on a domain with a CA-issued certificate, this can be reopened as opt-in (default OFF,
    // explicit consent, https:// with full validation and no fallback). Do not re-add it before then, and
    // do not re-add it over HTTP under any circumstance.

    // ══ APP SELF-UPDATE (Velopack) ═══════════════════════════════════════════════════════════════
    // NOTE the scope of this section: it governs ONLY the app updating ITSELF. It has nothing to do with
    // downloading manifests, plugins, unlockers or Steamless — those resolve their own sources
    // (PluginReleasesOwner/Repo below, UnlockerService, SteamlessRepo, ManifestBackendUrl) and are
    // deliberately untouched by everything here.

    /// <summary>
    /// Compiled-in default for the app's own update feed: this fork's OWN repository.
    ///
    /// <para>
    /// It used to inherit upstream's list (madoiscool/LuaTools, mendy-tools/LuaTools), which is a privacy
    /// hole disguised as a convenience: those repos publish the OFFICIAL build, so the updater would
    /// eventually download and silently install a version with Umami telemetry and DonateKeys — the key
    /// upload that is ON BY DEFAULT upstream — putting back exactly what this fork removes, without the
    /// user ever choosing it. It was then emptied while the fork had no published home.
    /// </para>
    ///
    /// <para>
    /// Now that LuaTools Amethyst is published, pointing at it is both safe and useful: updates ship from
    /// the same source as the build the user installed. A user can still override or disable this
    /// entirely with <c>AppUpdateRepos</c> in settings.json (an empty array turns self-update off, and
    /// then no update request is made at all).
    /// </para>
    ///
    /// <para>
    /// Whatever the source, <see cref="Services.AppUpdateSources"/> validates it — https + github.com
    /// only — and refuses <see cref="UpstreamReleaseRepos"/> outright, so no configuration can point the
    /// updater back at an official build.
    /// </para>
    /// </summary>
    public static readonly string[] GithubReleasesRepos =
    [
        "https://github.com/ArthurS357/LuaTools_Amethyst",
    ];

    /// <summary>
    /// Repos known to publish the OFFICIAL build. <see cref="Services.AppUpdateSources"/> refuses to build
    /// an updater against these no matter what settings.json says.
    ///
    /// <para>
    /// This is the backstop for the failure that actually matters: not malice, but someone copying the
    /// upstream release URL into their config to "get updates working" and silently reinstating telemetry
    /// and the decryption-key upload. Matched on owner/repo, case-insensitively, so URL spelling variants
    /// (trailing slash, .git suffix, www., http vs https) cannot slip past.
    /// </para>
    /// </summary>
    public static readonly string[] UpstreamReleaseRepos =
    [
        "madoiscool/LuaTools",
        "mendy-tools/LuaTools",
    ];

    // ── Plugin releases (the store-page plugin manager fetches these) ──────────────
    // Separate from the app's own Velopack self-update repo above. Each release of these repos carries
    // `plugin.zip` (the frontend) + `winmm.dll` (the loader); the tag is the version (e.g. "v1.2").
    // Fetched + verified (by asset sha256 digest) through GithubProxy like everything else.
    //
    // TWO sources, and this list is a CATALOGUE, not a priority chain. The Plugin page shows each one as
    // its own card and the user picks which is active; the choice is persisted (SettingsService.
    // PluginSource) and nothing ever changes it on the user's behalf. If the active source publishes
    // nothing installable, the page says so and the install fails — the OTHER source is never reached for.
    // See Services.PluginSourceResolver for why the automatic fallback that used to live here was removed:
    // it handed the choice of what gets installed to whoever could make the first source fail.
    //
    // Each source is judged independently and fail-closed: its release must carry every required asset,
    // each asset's browser_download_url must be pinned to THAT source's own repository, and each must
    // carry a parseable sha256 digest. One source's hashes are never consulted for another's bytes.
    //
    // Compiled in on purpose: NOT settings-driven, for the same reason as AmethystToolOwner below. The
    // loader DLL lands next to steam.exe. settings.json selects BY SLUG from this list and can name
    // nothing outside it (see Services.PluginSourceSelection), so a user-writable file can pick a source
    // but can never introduce one.

    /// <summary>Default plugin source: this fork's own frontend. What a fresh install uses when the user
    /// has not chosen otherwise.</summary>
    public const string PluginPrimaryOwner = "ArthurS357";
    public const string PluginPrimaryRepo = "Front-end-Amethyst";

    /// <summary>The original upstream frontend. Selectable like any other source, and the pin every
    /// pre-existing install was made against — which is why an install recording no source at all is
    /// treated as having come from here rather than being moved somewhere else.</summary>
    public const string PluginReleasesOwner = "madoiscool";
    public const string PluginReleasesRepo = "LTSP";

    /// <summary>Every plugin source the user may choose between. Order is presentation order, and the
    /// first entry is the default for a fresh install — it is NOT a fallback chain.</summary>
    public static readonly PluginSource[] PluginSources =
    [
        new(PluginPrimaryOwner, PluginPrimaryRepo),
        new(PluginReleasesOwner, PluginReleasesRepo),
    ];

    /// <summary>What a fresh install uses when the user has expressed no preference.</summary>
    public static PluginSource DefaultPluginSource => PluginSources[0];

    /// <summary>What an install that predates source recording must have come from — upstream's repo was
    /// the only plugin source that ever existed before this catalogue did. Recorded so those users stay
    /// on the source they actually have instead of being migrated by an app update.</summary>
    public static PluginSource LegacyPluginSource => new(PluginReleasesOwner, PluginReleasesRepo);

    // ── AmethystTool (BetterSteamTools fork) ──────────────────────────────────────
    // A NATIVE injection plugin: its release archive carries AmethystTool.dll plus two proxy DLLs
    // (dwmapi/xinput1_4) that steam.exe loads by name, and all of it goes into the Steam ROOT. That is the
    // most consequential place this app writes, so the owner/repo below are not merely where the download
    // starts — they are the PIN that GithubProxy.IsAssetUrlForRepo checks the asset URL against, which is
    // what stops a hostile API mirror from naming some other github.com repository's payload (and its
    // matching digest) and having every later check pass. Compiled in on purpose: NOT settings-driven, so
    // no configuration file can redirect an install that lands next to steam.exe.
    //
    // The asset is matched by prefix + ".zip" (e.g. "AmethystTool-v1.0.0.zip") rather than by an exact
    // name, so a new release installs without an app rebuild while still refusing every other asset in the
    // release. Verification stays fail-closed either way: the release's published sha256 digest is
    // required, and v1.0.0 carries one (unlike Steamless above, no pinned hash is needed here).
    public const string AmethystToolOwner = "ArthurS357";
    public const string AmethystToolRepo = "BetterSteamTools-Amethyst";
    public const string AmethystToolAssetPrefix = "AmethystTool-";

    // ── GitHub proxy mirrors (for blocked/throttled regions, e.g. China) ──────────────
    // github.com / api.github.com are often unreachable in some countries. Any GitHub request is tried
    // DIRECT first, then prefixed onto the MATCHING mirrors ("<mirror>https://<github-url>") until one works.
    // Two capability classes — GithubProxy.Candidates picks by URL so we never make a guaranteed-wasted hop
    // (an API mirror 400s a download; a download mirror 403s the API):
    //   • API metadata (api.github.com): ONLY our self-hosted lua.tools/gh proxy can serve it — server-side
    //     PAT (60→5000/hr) + cache. No PUBLIC proxy serves the REST API (they all 403 it), so there's no
    //     public backup here. Fixes the plugin release-metadata lookup in China / under rate-limit. 404s
    //     harmlessly until the /api/gh route is deployed, then lights up automatically.
    //   • Downloads (github.com releases / raw / objects): the public download proxies. lua.tools/gh is
    //     API-only (its route 400s downloads) so it is deliberately NOT in this list.
    public static readonly string[] GithubApiMirrors =
    [
        "https://lua.tools/api/gh/",   // self-hosted route (src/app/api/gh/[...rest]): proxies api.github.com with our PAT
    ];
    public static readonly string[] GithubDownloadMirrors =
    [
        "https://ghproxy.net/",    // download-only (verified live 2026-07)
        "https://ghfast.top/",     // download-only
        "https://gh.ddlc.top/",    // download-only
    ];
}
