using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

public class ApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// A staged download. <see cref="Pin"/> is set only for an orphan fix, whose bytes are verified against a
/// published SHA-256: it is the handle <see cref="AssetIntegrity.OpenPinned"/> took BEFORE hashing, kept so
/// the checked file cannot be swapped before it is screened and applied. The holder must call
/// <see cref="Dispose"/> (or <see cref="Discard"/>) — a held pin blocks deleting the file.
/// </summary>
// FromBackup: staged from the user's verified manifest backup, not fetched from a source (status wording only).
public sealed record DownloadedFile(
    string FilePath, string FileName, IDisposable? Pin = null, string? OwnedDirectory = null, bool FromBackup = false) : IDisposable
{
    public void Dispose() => Pin?.Dispose();

    /// <summary>Release the pin, then remove the staged file and the per-download folder it lived in.</summary>
    public void Discard()
    {
        Dispose();
        try { File.Delete(FilePath); } catch { /* best effort */ }
        if (OwnedDirectory is not null)
            try { Directory.Delete(OwnedDirectory); } catch { /* best effort; non-recursive, so only when empty */ }
    }
}

/// <summary>Typed client for the lua.tools web API, authenticated with a Supabase bearer token.</summary>
public class LuaToolsApiClient(
    AuthService auth, SteamAppInfoCache appInfo, CoverCache covers,
    InsecureTransportNotice notice, SettingsService settings)
{
    // Interim staging destination: downloads land here, get installed into Steam, then are deleted.
    // Under %TEMP% (not the user's Downloads) so nothing accumulates in a user-visible folder.
    private static readonly string InterimDownloadsFolder =
        Path.Combine(Path.GetTempPath(), "LuaToolsGui", "downloads");

    private readonly HttpClient _http = new(
        DnsFallbackHttpHandler.CreateDefault(getMode: () => settings?.DnsMode ?? DnsMode.Auto))
    {
        BaseAddress = new Uri(AppConfig.ApiBaseUrl),
        Timeout = TimeSpan.FromMinutes(5), // large manifest zips on slow connections
    };

    // Signed fix downloads only. Redirects are followed by hand (SendCheckingRedirectsAsync) so every hop
    // is screened like the first URL; _http keeps auto-redirect for the API endpoints. No Timeout here: one
    // budget covers all hops, so a redirect chain cannot multiply it. No cookies: a host in one chain must
    // not get them replayed on a later download.
    internal readonly HttpClient FixDownloadHttp =
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly TimeSpan SignedDownloadHeadersBudget = TimeSpan.FromMinutes(5); // same as _http

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // ── Endpoints ───────────────────────────────────────────────────

    public async Task<List<SteamSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        // Calls Steam's public store-search directly (no lua.tools, no auth) — guests can search.
        var url = $"{AppConfig.SteamStoreSearchUrl}?term={Uri.EscapeDataString(query)}&l=english&cc=US";
        var res = await _http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return [];

        var data = await ReadJsonAsync<SteamStoreSearchResponse>(res, ct);
        return (data?.Items ?? [])
            .Take(8)
            .Select(i => new SteamSearchResult
            {
                AppId = i.Id,
                Name = i.Name,
                Icon = i.TinyImage ?? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{i.Id}/capsule_sm_120.jpg",
            })
            .ToList();
    }

    /// <summary>Steam's featured "top sellers" + "new releases" lists for the Add page strips. Public,
    /// no auth. Returns empty lists on any failure (the strips just don't show). Each list keeps only real
    /// games (type 0) that have a capsule image, capped to keep the strips light.</summary>
    public async Task<(List<SteamFeaturedItem> TopSellers, List<SteamFeaturedItem> NewReleases)> GetFeaturedAsync(
        CancellationToken ct = default)
    {
        try
        {
            var res = await _http.GetAsync($"{AppConfig.SteamFeaturedUrl}?cc=us&l=english", ct);
            if (!res.IsSuccessStatusCode) return ([], []);
            var data = await ReadJsonAsync<SteamFeaturedResponse>(res, ct);

            // Steam's featuredcategories genuinely repeats appids within a list (e.g. top_sellers returns
            // the same game 2–3×), so DistinctBy the appid — keeps the first, preserving Steam's order.
            static List<SteamFeaturedItem> Clean(SteamFeaturedCategory? c) =>
                (c?.Items ?? [])
                    .Where(i => i.Type == 0 && i.Id > 0 && !string.IsNullOrEmpty(i.LargeCapsuleImage))
                    .DistinctBy(i => i.Id)
                    .Take(20)
                    .ToList();

            return (Clean(data?.TopSellers), Clean(data?.NewReleases));
        }
        catch { return ([], []); }
    }

    /// <summary>Public endpoint — no auth required.</summary>
    /// <summary>Game metadata straight from Steam's appdetails (cached to details\&lt;appid&gt;.json via the
    /// throttle, interactive priority) — no lua.tools proxy. ANY fetch path funnels through here (normal /
    /// DLC / fast / plugin add), so this is also where the header image gets warmed into covers\.</summary>
    public async Task<GameDetails?> GetDetailsAsync(string appid, CancellationToken ct = default)
    {
        if (!long.TryParse(appid, out long id)) return null;
        var details = await appInfo.ResolveGameDetailsAsync(id, ct);
        if (details is { HeaderImage: { Length: > 0 } img })
            _ = covers.EnsureAsync(id, img, CancellationToken.None); // warm the cover cache (best-effort)
        return details;
    }

    /// <summary>Source name → "available" | "unavailable" | other status.</summary>
    public async Task<Dictionary<string, string>> CheckSourcesAsync(string appid, CancellationToken ct = default)
    {
        // Calls the manifest backend directly (no lua.tools, no auth) — the backend is gated
        // by a fixed User-Agent rather than a token, so guests can check availability.
        //
        // This is the ONE remaining cleartext request in the app. It carries no credential, but the appid
        // is visible to (and forgeable by) anything on the network path.
        //
        // Opting out returns an EMPTY result rather than throwing. Callers already treat "no statuses" as
        // a normal outcome (offline, backend down), so the app degrades to whatever sources don't depend
        // on this lookup — Hubcap with the user's own key, drag-and-drop, luatools:// links — instead of
        // erroring. Downloading itself never goes through this host: DownloadManifestAsync resolves a
        // source by NAME against lua.tools and fetches a signed HTTPS URL.
        //
        // Every empty return is logged with its cause: an empty list is indistinguishable, in the UI, from
        // "no source has this game" — the picker just shows Hubcap alone.
        if (!settings.EnableSourceAvailabilityChecks)
        {
            AppLog.Log($"CheckSources appid={appid} -> EMPTY: EnableSourceAvailabilityChecks is off in settings.json");
            return [];
        }

        // Disclosure before the request, at the configured cadence. Never blocks, never gates.
        notice.NotifyMetadataOverHttp(new Uri(AppConfig.ManifestBackendUrl).Host);

        var req = new HttpRequestMessage(HttpMethod.Get, $"{AppConfig.ManifestBackendUrl}/check_apis?appid={appid}");
        req.Headers.TryAddWithoutValidation("User-Agent", AppConfig.ManifestBackendUserAgent);
        var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            AppLog.Log($"CheckSources appid={appid} -> EMPTY: backend answered HTTP {(int)res.StatusCode}");
            return [];
        }
        var statuses = await ReadJsonAsync<Dictionary<string, string>>(res, ct) ?? [];
        if (statuses.Count == 0)
            AppLog.Log($"CheckSources appid={appid} -> EMPTY: HTTP 200 but no sources in the body");
        return statuses;
    }

    /// <summary>
    /// The standard lua.tools daily download usage (25/day), counted live from the user_downloads
    /// table via Supabase REST — the same source the website reads. RLS scopes it to the signed-in
    /// user, so no user id is needed. Null on failure / not signed in.
    /// </summary>
    public async Task<StandardUsage?> GetStandardUsageAsync(CancellationToken ct = default)
    {
        try
        {
            // Count today's rows without fetching them: HEAD + Prefer: count=exact → Content-Range header.
            var todayUtc = DateTime.UtcNow.Date.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            string url = $"{AppConfig.SupabaseUrl}/rest/v1/user_downloads" +
                         $"?select=appid&downloaded_at=gte.{Uri.EscapeDataString(todayUtc)}";

            var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.TryAddWithoutValidation("apikey", AppConfig.SupabaseAnonKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetValidAccessTokenAsync());
            req.Headers.TryAddWithoutValidation("Prefer", "count=exact");

            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;

            // Content-Range: "0-24/25"  (or "*/0" when empty) — the count is after the slash.
            string? range = res.Content.Headers.TryGetValues("Content-Range", out var v) ? v.FirstOrDefault()
                          : (res.Headers.TryGetValues("Content-Range", out var hv) ? hv.FirstOrDefault() : null);
            int used = 0;
            if (range is not null && range.Split('/') is [_, var countStr] && int.TryParse(countStr, out int c))
                used = c;
            return new StandardUsage(used, AppConfig.DailyDownloadLimit);
        }
        catch
        {
            return null; // decorative — never block on it
        }
    }

    public async Task<SupporterStatus?> GetSupporterStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var res = await SendAsync(HttpMethod.Get, "/api/me/supporter-status", ct);
            return await ReadJsonAsync<SupporterStatus>(res, ct);
        }
        catch { return null; }
    }

    public async Task<DlcInfo?> GetDlcInfoAsync(string appid, string baseAppId, CancellationToken ct = default)
    {
        using var res = await SendAsync(HttpMethod.Get, $"/api/dlc/info?appid={appid}&base={baseAppId}", ct);
        return await ReadJsonAsync<DlcInfo>(res, ct);
    }

    /// <summary>
    /// A game's manifest bundle from a named source. Reports BYTE counts, not a fraction, because this is
    /// the one download the Downloads queue schedules — size, speed and ETA are derivable only from bytes.
    /// </summary>
    public Task<DownloadedFile> DownloadManifestAsync(
        string appid, string source, string? gameName,
        IProgress<Downloads.DownloadProgress>? progress, CancellationToken ct = default)
    {
        string url = $"/api/manifest/download?appid={appid}&source={Uri.EscapeDataString(source)}";
        if (!string.IsNullOrEmpty(gameName)) url += $"&game_name={Uri.EscapeDataString(gameName)}";
        return DownloadFileAsync(url, $"{appid}.zip", progress, ct);
    }

    /// <summary>
    /// Auth — one depot's <c>.manifest</c>, for a depot Steam's own <c>depotcache</c> doesn't have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needed because a game added with "Auto Update Apps" on has its <c>setManifestid</c> pins commented
    /// out and its manifest files skipped, so the depot downloader has nothing to feed
    /// <c>-manifestfile</c>. Signed-in only: guests can still download depots whose manifest is already on
    /// disk, they just cannot pull new ones.
    /// </para>
    /// <para>
    /// The staged filename here is a FALLBACK, not the installed name. What actually reaches depotcache is
    /// recomputed as <c>&lt;depot&gt;_&lt;manifest&gt;.manifest</c> by the caller, and screened by
    /// <see cref="ManifestFile.IsSteamManifest"/> first — so a <c>Content-Disposition</c> header cannot
    /// choose what this file is called once installed.
    /// </para>
    /// </remarks>
    public Task<DownloadedFile> DownloadDepotManifestAsync(
        long depotId, string manifestId, IProgress<double?>? progress, CancellationToken ct = default) =>
        DownloadFileAsync($"/api/givemethemanifestpunk/{depotId}/{manifestId}",
                          $"{depotId}_{manifestId}.manifest", progress, ct);

    public Task<DownloadedFile> GenerateDlcAsync(
        string appid, string baseAppId, string? gameName,
        IProgress<Downloads.DownloadProgress>? progress, CancellationToken ct = default)
    {
        string url = $"/api/dlc/generate?appid={appid}&base={baseAppId}";
        if (!string.IsNullOrEmpty(gameName)) url += $"&game_name={Uri.EscapeDataString(gameName)}";
        return DownloadFileAsync(url, $"{appid}.lua", progress, ct);
    }

    // ── Denuvo fixes ────────────────────────────────────────────────

    /// <summary>Where the offline copy of the Fixes catalog lives. Null = <c>%AppData%\LuaToolsAmethyst\cache</c>;
    /// tests point it at a temp folder.</summary>
    internal string? CacheDirectory { get; init; }

    /// <summary>Where staged downloads land before install. Null = <see cref="InterimDownloadsFolder"/>;
    /// tests point it at their own temp folder so parallel runs never share staging state.</summary>
    internal string? StagingDirectory { get; init; }

    /// <summary>How long the catalog repository gets before lua.tools is tried: the shared client's own
    /// timeout is 5 minutes, which would turn an unreachable GitHub into a five-minute blank tab.</summary>
    internal TimeSpan CatalogTimeout { get; init; } = TimeSpan.FromSeconds(15);

    // The orphan fixes of the catalog last served (live or from disk), swapped as a whole. Empty when the
    // listing came from lua.tools, which has none.
    private IReadOnlyDictionary<string, OrphanGame> _orphans = new Dictionary<string, OrphanGame>();

    // The orphan fixes GetDenuvoFixesAsync actually listed per game. Only these may take the orphan
    // download path: an orphan fix that lost a duplicate-id tie to an official one was not listed, so its id
    // still means the official fix and must go through lua.tools.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<string, OrphanFix>>
        _listedOrphans = new();

    /// <summary>
    /// Public — every game that has at least one Denuvo fix, plus the tag catalogue.
    ///
    /// <para>
    /// Three tiers. (1) The LuaTools-Catalog <c>catalog.json</c> on GitHub, with a short timeout. (2) lua.tools'
    /// own listing, the behaviour before the catalog existed. (3) The copy saved by the last good answer, newest
    /// of the two sources, returned with <see cref="DenuvoListingsResult.CachedAt"/> set so the page can say it
    /// is not live. A tier fails on an exception, a non-2xx (a 503 during maintenance, a 404 for a catalog that
    /// moved) or a body that does not parse. With nothing usable anywhere the behaviour is what it always was:
    /// null for a bad status, lua.tools' exception otherwise. A cancelled call is not a failure and never
    /// reads the cache.
    /// </para>
    /// </summary>
    public async Task<DenuvoListingsResult?> GetDenuvoListingsAsync(CancellationToken ct = default)
    {
        string dir = CacheDirectory ?? Path.Combine(SettingsService.DefaultDirectory, "cache");
        ExceptionDispatchInfo? failure = null;
        var tiers = new (string Url, string CacheFile, TimeSpan? Limit)[]
        {
            (AppConfig.CatalogUrl, DenuvoListingsCache.CatalogFileName, CatalogTimeout),
            ("/api/denuvo/listings", DenuvoListingsCache.FileName, null),
        };
        foreach (var (url, cacheFile, limit) in tiers)
        {
            failure = null; // only the last tier's failure is ever rethrown
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (limit is { } span) budget.CancelAfter(span);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (limit is not null) request.Headers.UserAgent.ParseAdd(AppVersion.UserAgent); // no bearer, no cookies of ours: a third-party host
                using var res = await _http.SendAsync(request, budget.Token);
                if (!res.IsSuccessStatusCode) continue;

                string body = await res.Content.ReadAsStringAsync(budget.Token);
                var fresh = JsonSerializer.Deserialize<DenuvoListingsResponse>(body, JsonOpts);
                if (fresh is null) continue;

                // An empty catalog is a server hiccup far more often than the truth: do not let it
                // overwrite a good copy.
                if (fresh.Games.Count > 0) DenuvoListingsCache.Save(dir, body, cacheFile);
                _orphans = OrphanFixCatalog.Parse(body);
                return new DenuvoListingsResult(fresh, null);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }

        if (DenuvoListingsCache.TryLoadNewest(dir, JsonOpts) is { } saved)
        {
            _orphans = OrphanFixCatalog.Parse(saved.Json);
            return saved.Result;
        }
        failure?.Throw();
        return null;
    }

    /// <summary>
    /// Public — one game's fixes (id/title/desc/tags + which download slots exist): lua.tools' own, then the
    /// catalog's orphan fixes for the game, official first. A repeated id keeps the official one. A game
    /// lua.tools does not know (404, or unreachable) still lists its orphan fixes.
    /// </summary>
    public async Task<DenuvoFixesResponse?> GetDenuvoFixesAsync(string appid, CancellationToken ct = default)
    {
        DenuvoFixesResponse? official = null;
        ExceptionDispatchInfo? failure = null;
        try
        {
            var res = await _http.GetAsync($"/api/denuvo/fixes?appid={Uri.EscapeDataString(appid)}", ct);
            if (res.IsSuccessStatusCode) official = await ReadJsonAsync<DenuvoFixesResponse>(res, ct); // 404 = none
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        if (!_orphans.TryGetValue(appid, out var orphan))
        {
            _listedOrphans.TryRemove(appid, out _);
            failure?.Throw();
            return official;
        }

        var fixes = new List<DenuvoFix>(official?.Fixes ?? []);
        var ids = fixes.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var listed = new Dictionary<string, OrphanFix>(StringComparer.Ordinal);
        foreach (var candidate in orphan.Fixes)
        {
            if (!ids.Add(candidate.Fix.Id)) continue;
            fixes.Add(candidate.Fix);
            listed[candidate.Fix.Id] = candidate;
        }
        _listedOrphans[appid] = listed;

        return new DenuvoFixesResponse
        {
            AppId = appid,
            Name = official?.Name ?? orphan.Name,
            HeaderImage = official?.HeaderImage ?? orphan.HeaderImage,
            Fixes = fixes,
        };
    }

    /// <summary>
    /// Download a fix's "manifest" or "fix" slot. A fix that <see cref="GetDenuvoFixesAsync"/> listed from the
    /// catalog (pass its <paramref name="appid"/>) comes from the public link in <c>catalog.json</c> and must
    /// match the SHA-256 published beside it. Any other fix takes the lua.tools path: auth, the daily limit,
    /// and a short-lived signed R2 URL; the caller must be signed in.
    /// </summary>
    public async Task<DownloadedFile> DownloadDenuvoAsync(
        string fixId, string slot, string fallbackName, IProgress<Downloads.DownloadProgress>? progress,
        CancellationToken ct = default, string? appid = null)
    {
        if (appid is not null && _listedOrphans.TryGetValue(appid, out var listed) && listed.TryGetValue(fixId, out var orphan))
        {
            if (!orphan.Downloads.TryGetValue(slot, out var link))
                throw new ApiException(Resources.Strings.Api_Err_LinkEmpty);
            return await DownloadVerifiedAsync(link, fallbackName, progress, ct);
        }

        // 1. Ask the API for a signed URL (auth + daily-limit gate live here).
        string signedUrl = await RequestDenuvoDownloadUrlAsync(fixId, slot, ct);

        // 2. Fetch the file from R2 (no auth header — the signed URL carries its own credentials).
        return await DownloadFromUrlAsync(signedUrl, fallbackName, progress, ct);
    }

    /// <summary>
    /// Request a short-lived signed download URL for a Denuvo fix slot. Executed inside the queue worker's
    /// download callback so a queued job cannot outlive its signed link.
    /// </summary>
    internal async Task<string> RequestDenuvoDownloadUrlAsync(string fixId, string slot, CancellationToken ct = default)
    {
        using var res = await SendAsync(HttpMethod.Get,
            $"/api/denuvo/download?fix={Uri.EscapeDataString(fixId)}&slot={Uri.EscapeDataString(slot)}", ct);
        var signed = await ReadJsonAsync<DenuvoDownloadResponse>(res, ct);
        if (string.IsNullOrWhiteSpace(signed?.Url))
            throw new ApiException(Resources.Strings.Api_Err_LinkEmpty);
        return signed.Url;
    }

    /// <summary>
    /// Download an orphan fix's file and keep it only if it hashes to the digest the catalog published.
    /// Fail-closed: a mismatch (or a file that cannot be read back) deletes the staged file and throws, so
    /// nothing downstream ever sees bytes that were not the ones the catalog named.
    /// </summary>
    internal async Task<DownloadedFile> DownloadVerifiedAsync(
        OrphanDownload link, string fallbackName, IProgress<Downloads.DownloadProgress>? progress, CancellationToken ct)
    {
        // A folder of its own (the file keeps its real name: that name is build identity), so the staging
        // path is not predictable and two downloads cannot share one.
        string folder = Path.Combine(StagingDirectory ?? InterimDownloadsFolder, Guid.NewGuid().ToString("N"));
        var file = await DownloadFromUrlAsync(link.Url, fallbackName, progress, ct, folder);

        // Pin first, hash through the pin, and hand the pin to the caller: nothing can replace the file
        // between this check and the screening/extraction that follows (see AssetIntegrity.OpenPinned).
        FileStream? pin = null;
        try
        {
            pin = AssetIntegrity.OpenPinned(file.FilePath);
            if (AssetIntegrity.MatchesStream(pin, link.Sha256))
                return file with { Pin = pin, OwnedDirectory = folder };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be opened or read back cannot be verified: same outcome as a mismatch.
        }

        pin?.Dispose();
        (file with { OwnedDirectory = folder }).Discard();
        AppLog.Log($"Orphan fix {file.FileName} failed its SHA-256 check and was discarded.");
        throw new ApiException(Resources.Strings.Api_Err_FixHashMismatch);
    }

    // ── Plumbing ────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        // All callers of SendAsync are login-gated endpoints; the caller ensures the user is signed in.
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetValidAccessTokenAsync());

        var res = await _http.SendAsync(req, completion, ct);
        if (res.IsSuccessStatusCode) return res;

        // A failure is never handed back, so nobody else will dispose it: with ResponseHeadersRead an
        // undisposed non-2xx response holds its connection until the finalizer runs.
        using (res)
        {
            string message = $"Request failed ({(int)res.StatusCode})";
            try
            {
                var err = JsonSerializer.Deserialize<ApiError>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
                if (!string.IsNullOrWhiteSpace(err?.Error)) message = err.Error;
            }
            catch { /* non-JSON error body */ }

            if (res.StatusCode == HttpStatusCode.Unauthorized) message = "Session expired — please sign in again.";
            throw new ApiException(message, res.StatusCode);
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage res, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await res.Content.ReadAsStringAsync(ct), JsonOpts);

    /// <summary>
    /// Adapt a fraction-reporting caller onto the byte-level sink below.
    /// </summary>
    /// <remarks>
    /// The copy loop reports bytes now, but most callers here (the depot manifest fetch, DLC generation,
    /// Denuvo slots) feed a plain progress bar and have no use for the counts. Rather than fork the loop or
    /// churn every one of their call sites, they keep <c>IProgress&lt;double?&gt;</c> and get folded down
    /// here — a fraction is derivable from bytes, which is why the queue's contract is the wider one.
    /// </remarks>
    private static IProgress<Downloads.DownloadProgress>? AsFraction(IProgress<double?>? progress) =>
        progress is null ? null : new Downloads.ProgressRelay<Downloads.DownloadProgress>(
            p => progress.Report(p.Fraction));

    private Task<DownloadedFile> DownloadFileAsync(
        string url, string fallbackName, IProgress<double?>? progress, CancellationToken ct) =>
        DownloadFileAsync(url, fallbackName, AsFraction(progress), ct);

    private async Task<DownloadedFile> DownloadFileAsync(
        string url, string fallbackName, IProgress<Downloads.DownloadProgress>? progress, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Get, url, ct, HttpCompletionOption.ResponseHeadersRead);
        return await SaveResponseAsync(res, fallbackName, progress, ct, StagingDirectory);
    }

    /// <summary>
    /// Whether a signed download URL handed back by the API is one we follow: HTTPS to a named host.
    /// </summary>
    /// <remarks>
    /// No host allow-list. The links are signed R2 URLs, but nothing in this repository pins WHICH R2
    /// hostname, and guessing one would break every fix download the day it differs. What is refused is
    /// what no legitimate signed link is: plain http (the zip is extracted into the game folder), an IP
    /// literal (loopback, link-local, LAN), localhost, or any other scheme.
    /// </remarks>
    // ponytail: checks the name (on every redirect hop, see ValidateRedirectHop), not what it resolves to,
    // so a public name that RESOLVES to a LAN address (DNS rebinding) still passes. A connect-time check
    // would need its own SocketsHttpHandler.ConnectCallback, which the DoH fallback handler already owns.
    // Pin the R2 hostname once it is known.
    internal static bool IsFollowableSignedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.HostNameType == UriHostNameType.Dns
        && !uri.IsLoopback
        && !DnsFallbackHttpHandler.IsLocalOrBypassHost(uri.IdnHost);
    // IsLocalOrBypassHost is the DoH handler's policy: it also covers what IsLoopback misses ("localhost."
    // with a trailing dot, the *.localhost names RFC 6761 reserves) plus *.local and single-label names.

    /// <summary>
    /// Redirects followed for one signed download. A presigned R2 link answers directly; a CDN or custom
    /// domain in front may add one or two. 5 leaves headroom and still ends a redirect loop fast.
    /// </summary>
    internal const int MaxRedirectHops = 5;

    // No EmptyHost: System.Uri refuses an http(s) URI without a host, so none reaches this check.
    internal enum HopVerdict { Ok, NotHttps, InvalidScheme, LoopbackOrIpLiteral, TooManyHops }

    /// <summary>
    /// Whether redirect number <paramref name="hop"/> (1-based) to the absolute <paramref name="next"/> may
    /// be followed. The verdict is <see cref="IsFollowableSignedUrl"/>'s; the rest only names the refusal.
    /// </summary>
    internal static HopVerdict ValidateRedirectHop(Uri next, int hop)
    {
        if (hop > MaxRedirectHops) return HopVerdict.TooManyHops;
        if (IsFollowableSignedUrl(next.AbsoluteUri)) return HopVerdict.Ok;
        if (next.Scheme == Uri.UriSchemeHttp) return HopVerdict.NotHttps;
        return next.Scheme == Uri.UriSchemeHttps ? HopVerdict.LoopbackOrIpLiteral : HopVerdict.InvalidScheme;
    }

    /// <summary>
    /// GET <paramref name="url"/>, following redirects by hand: each hop goes through
    /// <see cref="ValidateRedirectHop"/> before any request to it, and one refused hop fails the whole
    /// download. <paramref name="headersBudget"/> covers every hop until the final headers arrive; the body
    /// is the stall timeout's job. <paramref name="http"/> must not auto-redirect.
    /// </summary>
    internal static async Task<HttpResponseMessage> SendCheckingRedirectsAsync(
        HttpClient http, Uri url, TimeSpan headersBudget, CancellationToken ct,
        Action<HttpRequestMessage>? configure = null)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(headersBudget);

        for (int hop = 1; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            configure?.Invoke(request); // applied to every hop: headers are not carried over by hand
            var res = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            if ((int)res.StatusCode is not (300 or 301 or 302 or 303 or 307 or 308)
                || res.Headers.Location is not { } location)
                return res;
            res.Dispose();

            var next = location.IsAbsoluteUri ? location : new Uri(url, location);
            var verdict = ValidateRedirectHop(next, hop);
            if (verdict != HopVerdict.Ok)
            {
                AppLog.Log($"Signed download refused: {GithubProxy.Redact(next.AbsoluteUri)} (redirect {hop}: {verdict})");
                throw new ApiException(Resources.Strings.Api_Err_LinkRefused);
            }
            AppLog.Log($"Signed download redirect {hop}: {GithubProxy.Redact(next.AbsoluteUri)}");
            url = next;
        }
    }

    /// <summary>Download a file from an absolute URL with NO auth header (e.g. a signed R2 link).</summary>
    internal async Task<DownloadedFile> DownloadFromUrlAsync(
        string url, string fallbackName, IProgress<Downloads.DownloadProgress>? progress, CancellationToken ct,
        string? folder = null)
    {
        if (!IsFollowableSignedUrl(url))
        {
            // The host is logged (path and query are not: they carry the signature) so a user can report
            // a refusal that turns out to be a legitimate link.
            AppLog.Log($"Signed download refused: {GithubProxy.Redact(url)}");
            throw new ApiException(Resources.Strings.Api_Err_LinkRefused);
        }

        // Not via SendAsync: no Bearer header, the absolute URL isn't prefixed, and redirects are screened.
        using var res = await SendCheckingRedirectsAsync(FixDownloadHttp, new Uri(url), SignedDownloadHeadersBudget, ct);
        if (!res.IsSuccessStatusCode)
            throw new ApiException(string.Format(Resources.Strings.Api_Err_DownloadFailed, (int)res.StatusCode), res.StatusCode);
        return await SaveResponseAsync(res, fallbackName, progress, ct, folder);
    }

    /// <summary>
    /// Stream a response body into the staging folder. <paramref name="folder"/> is a per-download folder
    /// (<see cref="DownloadVerifiedAsync"/>; tests too), null for the shared staging root; <paramref name="stallTimeout"/>
    /// exists for tests only.
    /// </summary>
    internal static async Task<DownloadedFile> SaveResponseAsync(
        HttpResponseMessage res, string fallbackName,
        IProgress<Downloads.DownloadProgress>? progress, CancellationToken ct,
        string? folder = null, TimeSpan? stallTimeout = null)
    {
        string fileName = StagedFileName(res.Content.Headers.ContentDisposition?.FileName, fallbackName);

        // A per-download folder this call creates (DownloadVerifiedAsync passes a fresh GUID one) goes again if the
        // body fails, or every failed download leaves an empty folder in %TEMP%. The shared root is never removed.
        bool ownsFolder = folder is not null && !Directory.Exists(folder);
        folder ??= InterimDownloadsFolder;
        Directory.CreateDirectory(folder);
        // ponytail: two downloads with the same name share this path (the name is build identity, so it
        // can't be prefixed), and there is no size cap (no known maximum for a legitimate fix). Per-download
        // subfolder + matching DeleteStaged cleanup if a collision ever shows up outside the queue's dedupe.
        string filePath = Path.Combine(folder, fileName);

        // HttpClient.Timeout stops covering the request once the headers arrive (ResponseHeadersRead), so
        // a server that goes quiet mid-body would hang here until the user cancels. Each read gets its own
        // deadline instead.
        TimeSpan stall = stallTimeout ?? AppConfig.DownloadStallTimeout;
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        long? total = res.Content.Headers.ContentLength;
        long written = 0;
        try
        {
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(filePath);

            var buffer = new byte[81920];
            while (true)
            {
                readCts.CancelAfter(stall);
                int read = await src.ReadAsync(buffer, readCts.Token);
                if (read == 0) break;
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                progress?.Report(new Downloads.DownloadProgress(written, total));
            }
        }
        catch (Exception ex)
        {
            // The caller never gets a DownloadedFile for a body that failed or was cancelled, so nothing
            // downstream knows this path to clean it up. The streams are already closed by now.
            try { File.Delete(filePath); } catch { /* best effort */ }
            if (ownsFolder)
                try { Directory.Delete(folder); } catch { /* best effort; non-recursive, so only when empty */ }

            if (ex is OperationCanceledException && !ct.IsCancellationRequested)
                throw new ApiException(string.Format(Resources.Strings.Api_Err_DownloadStalled, stall.TotalSeconds.ToString("0")));
            throw;
        }

        // One final report so a zero-length or single-chunk body still settles the bar at 100%.
        progress?.Report(new Downloads.DownloadProgress(written, total ?? written));

        return new DownloadedFile(filePath, fileName);
    }

    private static readonly Regex DeviceName =
        new(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The name a download is staged under: the server's <c>Content-Disposition</c> name when Windows
    /// would treat it as a plain file in the staging folder, else <paramref name="fallbackName"/>.
    /// </summary>
    /// <remarks>
    /// The server's name is kept, not replaced, because it carries meaning:
    /// <see cref="LuaInstaller.BuildIdFromFileName"/> reads the build from a staged
    /// <c>&lt;appid&gt;_&lt;buildid&gt;.lua</c>. Separators are replaced, so what is left to refuse is a name
    /// that resolves to something other than a file here: <c>.</c>/<c>..</c> (this folder or its parent,
    /// once Windows strips trailing dots) and device names (<c>CON</c>, <c>NUL.zip</c>).
    /// </remarks>
    internal static string StagedFileName(string? serverName, string fallbackName)
    {
        string name = (serverName ?? "").Trim('"').TrimEnd('.', ' ');
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length == 0 || DeviceName.IsMatch(name.Split('.')[0].TrimEnd()) ? fallbackName : name;
    }
}
