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

    // CloudRedirect (Selectively11) — the Mode page "Manage" button downloads the latest CloudRedirect.exe
    // GUI manager from here and launches it. (Separate from the CLI fixer used by the mode install flow.)
    public const string CloudRedirectRepo = "Selectively11/CloudRedirect";
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
    /// Compiled-in default for the app's own update feed: DELIBERATELY EMPTY, which disables self-update.
    ///
    /// <para>
    /// This fork used to inherit upstream's list (madoiscool/LuaTools, mendy-tools/LuaTools). That is a
    /// privacy hole disguised as a convenience: those repos publish the OFFICIAL build, so the updater
    /// would eventually download and silently install a version that has Umami telemetry and DonateKeys —
    /// the key upload that is ON BY DEFAULT upstream — putting back exactly what this fork exists to
    /// remove, without the user ever choosing it.
    /// </para>
    ///
    /// <para>
    /// Rather than point it at a fork repo that does not exist yet, the default is "no feed at all":
    /// <see cref="Services.UpdateService"/> no-ops on an empty list, so an unconfigured build makes NO
    /// update request whatsoever. Whoever publishes this fork sets their own repo in settings.json
    /// (<c>AppUpdateRepos</c>) — see <see cref="Services.AppUpdateSources"/>, which validates the entries
    /// and refuses the upstream repos outright.
    /// </para>
    /// </summary>
    public static readonly string[] GithubReleasesRepos = [];

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
    // Separate from the app's own Velopack self-update repo above. Each release of this repo carries
    // `plugin.zip` (the frontend) + `winmm.dll` (the loader); the tag is the version (e.g. "v1.2").
    // Fetched + verified (by asset sha256 digest) through GithubProxy like everything else.
    public const string PluginReleasesOwner = "madoiscool";
    public const string PluginReleasesRepo = "LTSP";

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
