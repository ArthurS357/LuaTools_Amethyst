using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// Talks to Hubcap (hubcapmanifest.com) DIRECTLY with the user's own API key — no lua.tools proxy.
/// Stats and manifest downloads authenticate via <c>?api_key={key}</c>; the free status check uses a
/// <c>Bearer</c> header (per the Hubcap API).
///
/// <para>
/// Every call returns a <see cref="HubcapResult{T}"/> — a rejected key, a spent quota, a server fault and
/// a dead network are separate cases, not one shared null. See that type for why the distinction matters.
/// </para>
///
/// <para>
/// Nothing here throws for a Hubcap-side problem. The one exception is cancellation requested by the
/// CALLER, which propagates as <see cref="OperationCanceledException"/>: reporting it as a value let a
/// caller that had navigated away carry on and rebuild its UI from a stale answer. The metadata deadline
/// is not cancellation in that sense and surfaces as <see cref="HubcapResult{T}.Offline"/>.
/// </para>
/// </summary>
public partial class HubcapService
{
    // Hubcap-keyed downloads can be large manifest zips; allow a generous timeout like the lua.tools client.
    private readonly HttpClient _http;

    private readonly TimeSpan _metadataTimeout;

    private readonly TimeProvider _clock;

    /// <summary>
    /// Remembers, per endpoint, whether Hubcap accepts the key in an <c>Authorization</c> header.
    ///
    /// <para>
    /// Tracked separately for stats and manifest because they are separate endpoints and nothing
    /// guarantees they agree — and only the manifest one could ever have been verified by spending a
    /// request from the key's 25-a-day allowance.
    /// </para>
    ///
    /// <para>
    /// Process-lifetime only, never persisted: a Hubcap that later starts accepting the header is picked
    /// up on the next launch with no code change.
    /// </para>
    /// </summary>
    private sealed class AuthTransport
    {
        private volatile bool _headerRefused;

        public bool PreferHeader => !_headerRefused;

        public void HeaderRefused() => _headerRefused = true;
    }

    private readonly AuthTransport _statsTransport = new();
    private readonly AuthTransport _manifestTransport = new();

    /// <summary>
    /// Run <paramref name="send"/> with the header form, retrying once with the query-string form if
    /// Hubcap refuses it.
    ///
    /// <para>
    /// The retry fires on <see cref="HubcapResult{T}.Unauthorized"/>, which covers 401 and 403 alike (see
    /// <c>Classify</c>): an endpoint that doesn't accept header auth may refuse either way, and narrowing
    /// this to 401 would let a 403 break downloading with no fallback at all.
    /// </para>
    ///
    /// <para>
    /// The transport is latched ONLY when the fallback actually succeeds. That is the single outcome
    /// proving the header was the problem rather than the key — latching on the refusal itself would pin a
    /// user who merely had a bad key to the query-string form for the rest of the session.
    /// </para>
    /// </summary>
    private static async Task<HubcapResult<T>> WithAuthFallbackAsync<T>(
        AuthTransport transport, Func<bool, Task<HubcapResult<T>>> send)
    {
        bool triedHeader = transport.PreferHeader;
        var result = await send(triedHeader);
        if (!triedHeader || result is not HubcapResult<T>.Unauthorized) return result;

        var viaQuery = await send(false);
        if (viaQuery.IsOk) transport.HeaderRefused();
        return viaQuery;
    }

    /// <summary>
    /// How long a pooled connection may be reused before it is retired.
    ///
    /// <para>
    /// A long-lived <see cref="HttpClient"/> keeps its connections open indefinitely by default, and a
    /// connection that is never reopened never re-resolves DNS. Hubcap sits behind Cloudflare, whose
    /// addresses do move — and this app is a desktop process that a user leaves running for days, so
    /// "eventually" is not hypothetical here. Retiring connections on a timer costs one handshake every
    /// fifteen minutes of continuous use and nothing at all when idle.
    /// </para>
    /// </summary>
    private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Production constructor — the one the DI container resolves.</summary>
    public HubcapService() : this(CreateDefaultHandler(), DefaultMetadataTimeout) { }

    /// <summary><see cref="SocketsHttpHandler"/> rather than <see cref="HttpClientHandler"/> because the
    /// connection-lifetime knob lives on the former; the latter merely wraps it and hides that.</summary>
    private static HttpMessageHandler CreateDefaultHandler() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = PooledConnectionLifetime,
    };

    /// <summary>
    /// Test seam. The handler was previously baked into a field initializer, so nothing about this class
    /// could be exercised without reaching hubcapmanifest.com — which is why the timeout added above shipped
    /// without a test. A fake handler plus a millisecond-scale timeout makes both the deadline and the
    /// caller's cancellation assertable offline.
    /// </summary>
    internal HubcapService(HttpMessageHandler handler, TimeSpan metadataTimeout, TimeProvider? clock = null)
    {
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(AppConfig.HubcapBaseUrl),
            Timeout = TimeSpan.FromMinutes(5),
        };
        // Set here rather than on the production path alone, so tests exercise the same headers real
        // requests carry. Untouched by the metadata deadline — that is a per-call linked token.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(AppVersion.UserAgent);
        _metadataTimeout = metadataTimeout;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Deadline for the metadata calls (stats/status), layered on top of the client's own 5-minute timeout.
    ///
    /// <para>
    /// That 5 minutes exists for manifest zips, but the client is shared, so it also governed the two
    /// metadata calls — and those sit on the UI's critical path: <c>CheckStatusAsync</c> is awaited inside
    /// the source-list fetch, and the same pair runs in the headless plugin pipeline. A hung Hubcap host
    /// therefore stalled the source list for up to five minutes, and because both callers swallow the
    /// exception the only symptom was a spinner that never resolved.
    /// </para>
    ///
    /// <para>
    /// Applied per call via a linked token so it composes with the caller's own cancellation rather than
    /// replacing it. Observed latency for both endpoints is 190–510 ms, so 15 s is far outside the normal
    /// range while still bounding the stall at something a user will sit through.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan DefaultMetadataTimeout = TimeSpan.FromSeconds(15);

    // ── Availability cache ───────────────────────────────────────────
    //
    // The status endpoint is free — the FASE 2 probe confirmed daily_usage does not move for it — so this
    // cache is not about the 25/day allowance. It is about the round-trip: the source list calls status
    // once per app the user opens, so browsing back and forth re-asked Hubcap the same question every time.
    //
    // What can and cannot invalidate an entry is worth being precise about, because the obvious answer is
    // wrong. `file_modified` arrives INSIDE the response, so it cannot expire an entry prospectively —
    // reading it requires making the very request the cache exists to avoid. The three real triggers:
    //
    //   1. Age (StatusCacheTtl).
    //   2. A successful download of that app — we now hold the manifest that answer described.
    //   3. A change of API key — availability is answered per key, so entries from another key are void.

    /// <summary>How long an availability answer stays usable. Short enough that a manifest appearing on
    /// Hubcap shows up within a browsing session; long enough to cover a user going back and forth.</summary>
    internal static readonly TimeSpan StatusCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>Ceiling on retained entries, mirroring HttpServerService's cap on tracked downloads: a long
    /// session must not grow this without bound. Cleared wholesale rather than evicting one at a time —
    /// entries are cheap to rebuild and this only happens after hundreds of distinct apps.</summary>
    private const int MaxCachedStatuses = 256;

    private sealed record StatusCacheEntry(HubcapResult<HubcapManifestStatus> Result, DateTimeOffset FetchedAt);

    private readonly ConcurrentDictionary<string, StatusCacheEntry> _statusCache = new(StringComparer.Ordinal);

    /// <summary>Identifies WHICH key the cached answers belong to, without keeping a second copy of the key
    /// itself in memory. Compared, never transmitted or logged.</summary>
    private string? _statusCacheKeyId;

    private readonly object _statusCacheLock = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Interim staging destination, mirroring LuaToolsApiClient (downloads land here before install,
    // then are deleted once installed). Under %TEMP% so it never pollutes the user's Downloads folder.
    private static readonly string InterimDownloadsFolder =
        Path.Combine(Path.GetTempPath(), "LuaToolsGui", "downloads");

    // Every key Hubcap issues today is "smm_" plus 96 lowercase hex characters, and the check used to
    // require exactly that. Pinning a client-side gate to the current shape of someone else's credential
    // is a bet that they never rotate the format — and when they do, the failure is the worst kind: the
    // key is genuinely valid, Hubcap would accept it, and the app refuses it locally with "that doesn't
    // look like a valid key". The user has no way to tell that from a typo and no way to override it, so
    // they go hunting for a key that was never wrong.
    //
    // What this gate is actually for is narrower than validation: keep an obvious paste accident — a URL,
    // a whole JSON blob, an empty box, a truncated fragment — from being sent to Hubcap as a credential.
    // Deciding whether a well-formed key is real is Hubcap's job, and it does it on the very next line of
    // ValidateAndSaveHubcapKeyAsync. So the shape below is loose in the ways that cost nothing and strict
    // in the ways that catch the accident:
    //
    //   * The prefix is optional and generic (lowercase alnum, 2–16 chars, then "_"), so "smm_", a future
    //     "hc_", and a bare key all pass. Requiring "smm_" specifically is the pin this is removing.
    //   * The body is hex, at least 16 characters. Sixteen is far below the 96 in use — enough to reject
    //     a fragment, low enough that a shortened future key still fits.
    //   * Both cases of hex are accepted; a key that differs only by case is the same key.
    //   * The upper bound is what keeps this a guard: 256 body characters, so a pasted document or a
    //     megabyte of anything is refused before it becomes an HTTP request.
    //
    // Bounded quantifiers with no alternation and no nesting — the match is linear, so there is no
    // backtracking blowup to protect against. Anchored with \A/\z, not ^/$: in .NET `$` also matches immediately before a trailing newline, so a
    // pasted value with a second line hidden after it would pass a ^...$ pattern.
    [GeneratedRegex(@"\A(?:[a-z0-9]{2,16}_)?[0-9a-fA-F]{16,256}\z")]
    private static partial Regex KeyFormatRegex();

    /// <summary>
    /// Local sanity check on a key before it is sent anywhere — an optional short prefix followed by a run
    /// of at least 16 hex characters.
    ///
    /// <para>
    /// Deliberately NOT an authority on whether a key is real; only Hubcap can say that. Passing here
    /// means "worth one request", not "valid". See the comment above the pattern for why the exact
    /// <c>smm_</c> + 96-hex shape is no longer required.
    /// </para>
    /// </summary>
    public static bool IsValidKeyFormat(string? key) => key is not null && KeyFormatRegex().IsMatch(key);

    /// <summary>
    /// Usage stats for a key. Free — does not count against the daily allowance.
    ///
    /// <para>
    /// Prefers a <c>Bearer</c> header. This used to pass the key as <c>?api_key=</c>, which put a live
    /// credential into a URL: TLS hides it in transit, but URLs are what servers and their CDN write to
    /// access logs, and Hubcap sits behind Cloudflare. The header form is confirmed working against the
    /// live endpoint; the query-string fallback is kept only so a change on Hubcap's side degrades into a
    /// second request rather than a wrong "your key was rejected". It costs nothing to keep — this
    /// endpoint is free.
    /// </para>
    /// </summary>
    public Task<HubcapResult<HubcapStats>> GetStatsAsync(string key, CancellationToken ct = default) =>
        WithAuthFallbackAsync(_statsTransport, bearer => SendStatsAsync(key, bearer, ct));

    private async Task<HubcapResult<HubcapStats>> SendStatsAsync(string key, bool useBearer, CancellationToken ct)
    {
        try
        {
            const string path = "/api/v1/user/stats";
            using var cts = LinkedMetadataTimeout(ct);
            using var req = new HttpRequestMessage(
                HttpMethod.Get, useBearer ? path : $"{path}?api_key={Uri.EscapeDataString(key)}");
            if (useBearer) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var res = await _http.SendAsync(req, cts.Token);
            return await InterpretAsync<HubcapStats>(res, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new HubcapResult<HubcapStats>.Offline(ex); }
    }

    /// <summary>
    /// Whether a manifest exists for an app. Free — does not count against the daily allowance.
    ///
    /// <para>
    /// Served from a short-lived per-key cache; see the cache region above for what invalidates an entry.
    /// Only definitive answers are cached, so a failure is always retried rather than pinned for minutes.
    /// </para>
    /// </summary>
    public async Task<HubcapResult<HubcapManifestStatus>> CheckStatusAsync(
        string key, string appid, CancellationToken ct = default)
    {
        if (TryGetCachedStatus(key, appid) is { } hit) return hit;

        HubcapResult<HubcapManifestStatus> result;
        try
        {
            using var cts = LinkedMetadataTimeout(ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/status/{Uri.EscapeDataString(appid)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var res = await _http.SendAsync(req, cts.Token);
            result = await InterpretAsync<HubcapManifestStatus>(res, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { result = new HubcapResult<HubcapManifestStatus>.Offline(ex); }

        StoreStatus(appid, result);
        return result;
    }

    /// <summary>Forget the cached availability for one app. Called after a successful download, and
    /// available to callers that have other reason to believe the answer moved.</summary>
    public void InvalidateStatus(string appid) => _statusCache.TryRemove(appid, out _);

    /// <summary>The <c>file_modified</c> marker from the most recent cached availability answer for this
    /// app, or null if nothing is cached (expired, never checked, or the checked answer had none). A peek
    /// — does not touch the cache's TTL or contents. Callers read this BEFORE starting a download, since a
    /// successful download invalidates the entry (see <see cref="SendManifestAsync"/>).</summary>
    public string? PeekCachedFileModified(string appid) =>
        _statusCache.TryGetValue(appid, out var entry) && entry.Result is HubcapResult<HubcapManifestStatus>.Ok ok
            ? ok.Value.FileModified
            : null;

    /// <summary>Drop every cached availability answer. Use when the key changes or is cleared.</summary>
    public void ClearStatusCache()
    {
        lock (_statusCacheLock)
        {
            _statusCache.Clear();
            _statusCacheKeyId = null;
        }
    }

    /// <summary>
    /// Download the manifest zip for an app directly from Hubcap. This is the ONLY call that spends the
    /// key's daily allowance — the two metadata calls above are free.
    ///
    /// <para>
    /// Keeps the caller's full timeout (manifest zips are large); no metadata deadline applies here.
    /// </para>
    ///
    /// <para>
    /// Prefers a <c>Bearer</c> header, falling back to <c>?api_key=</c> if Hubcap refuses it. The header
    /// form is confirmed on <c>/user/stats</c> but was never verified here, and verifying it directly
    /// would have cost one of the key's 25 daily requests — so the code discovers the answer at runtime
    /// instead of assuming either way. Getting this wrong by assumption would break downloading outright,
    /// which is the app's main function.
    /// </para>
    /// </summary>
    public Task<HubcapResult<DownloadedFile>> DownloadManifestAsync(
        string appid, string key, IProgress<double?>? progress, CancellationToken ct = default) =>
        WithAuthFallbackAsync(_manifestTransport, bearer => SendManifestAsync(appid, key, bearer, progress, ct));

    /// <summary>One download attempt with a chosen auth transport. Streams straight to disk on success.</summary>
    private async Task<HubcapResult<DownloadedFile>> SendManifestAsync(
        string appid, string key, bool useBearer, IProgress<double?>? progress, CancellationToken ct)
    {
        try
        {
            string path = $"/api/v1/manifest/{Uri.EscapeDataString(appid)}";
            using var req = new HttpRequestMessage(
                HttpMethod.Get, useBearer ? path : $"{path}?api_key={Uri.EscapeDataString(key)}");
            if (useBearer) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return Classify<DownloadedFile>(res);

            var file = await SaveResponseAsync(res, $"{appid}.zip", progress, ct);
            // We now hold the manifest whose existence the cached answer described. Re-asking next time is
            // the point: this is where a stale "available" would otherwise outlive its usefulness.
            InvalidateStatus(appid);
            return new HubcapResult<DownloadedFile>.Ok(file);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new HubcapResult<DownloadedFile>.Offline(ex); }
    }

    // ── Availability cache plumbing ─────────────────────────────────

    /// <summary>A live cached answer for this key + app, or null. Also handles the key having changed
    /// since the cache was filled, which voids every entry at once.</summary>
    private HubcapResult<HubcapManifestStatus>? TryGetCachedStatus(string key, string appid)
    {
        string id = KeyId(key);
        lock (_statusCacheLock)
        {
            if (_statusCacheKeyId is null)
            {
                _statusCacheKeyId = id;
            }
            else if (!string.Equals(_statusCacheKeyId, id, StringComparison.Ordinal))
            {
                // A different key answers "is this available to you" differently, so nothing cached under
                // the old one may be reused. Cheaper and less error-prone than keying every entry by both.
                _statusCache.Clear();
                _statusCacheKeyId = id;
                return null;
            }
        }

        if (!_statusCache.TryGetValue(appid, out var entry)) return null;
        if (_clock.GetUtcNow() - entry.FetchedAt >= StatusCacheTtl)
        {
            _statusCache.TryRemove(appid, out _);
            return null;
        }
        return entry.Result;
    }

    /// <summary>Cache a DEFINITIVE answer. Failures are deliberately not cached: pinning an Offline for
    /// five minutes would keep a source dark long after the network came back, and pinning Unauthorized
    /// would survive the user pasting a corrected key.</summary>
    private void StoreStatus(string appid, HubcapResult<HubcapManifestStatus> result)
    {
        if (result is not (HubcapResult<HubcapManifestStatus>.Ok or HubcapResult<HubcapManifestStatus>.NotFound))
            return;

        if (_statusCache.Count >= MaxCachedStatuses) _statusCache.Clear();
        _statusCache[appid] = new StatusCacheEntry(result, _clock.GetUtcNow());
    }

    /// <summary>A stable, non-reversible label for a key, so the cache can tell "same key" from "different
    /// key" without holding the key. Truncated because collision resistance is not the job — telling two
    /// of the user's own keys apart is.</summary>
    private static string KeyId(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 8));

    // ── Plumbing ────────────────────────────────────────────────────

    /// <summary>Map a non-success response onto its case. Kept separate from <see cref="InterpretAsync"/>
    /// so the download path — which streams its body rather than parsing JSON — shares the same
    /// status-to-case table instead of restating it.</summary>
    private static HubcapResult<T> Classify<T>(HttpResponseMessage res) => res.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new HubcapResult<T>.Unauthorized(),
        HttpStatusCode.NotFound => new HubcapResult<T>.NotFound(),
        HttpStatusCode.TooManyRequests => new HubcapResult<T>.RateLimited(RetryAfter(res)),
        var other => new HubcapResult<T>.Failed(other),
    };

    /// <summary>Hubcap's <c>Retry-After</c>, in either of the forms RFC 9110 permits (delay seconds or an
    /// HTTP date). Null when absent — the probe runs against this API showed no such header on a healthy
    /// response, so its presence on a 429 is not something to rely on.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage res)
    {
        var header = res.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    /// <summary>Turn a completed metadata response into a result. A 2xx whose body doesn't deserialize is
    /// reported as <see cref="HubcapResult{T}.Failed"/> rather than a silent success carrying null.</summary>
    private static async Task<HubcapResult<T>> InterpretAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        if (!res.IsSuccessStatusCode) return Classify<T>(res);
        var parsed = await ReadJsonAsync<T>(res, ct);
        return parsed is null
            ? new HubcapResult<T>.Failed(res.StatusCode)
            : new HubcapResult<T>.Ok(parsed);
    }

    /// <summary>A token that trips on the metadata timeout OR when the caller cancels, whichever comes
    /// first. The caller's token stays authoritative — this only adds an upper bound.</summary>
    private CancellationTokenSource LinkedMetadataTimeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_metadataTimeout);
        return cts;
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage res, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await res.Content.ReadAsStringAsync(ct), JsonOpts);

    private static async Task<DownloadedFile> SaveResponseAsync(
        HttpResponseMessage res, string fallbackName, IProgress<double?>? progress, CancellationToken ct)
    {
        string fileName = res.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? fallbackName;
        foreach (char c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');

        Directory.CreateDirectory(InterimDownloadsFolder);
        string filePath = Path.Combine(InterimDownloadsFolder, fileName);

        long? total = res.Content.Headers.ContentLength;
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(filePath);

        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
            progress?.Report(total is > 0 ? (double)written / total.Value : null);
        }

        return new DownloadedFile(filePath, fileName);
    }
}
