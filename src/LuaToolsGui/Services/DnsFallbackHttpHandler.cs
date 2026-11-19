using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LuaToolsGui.Services;

/// <summary>Controls how Cloudflare DNS-over-HTTPS fallback behaves for allowlisted hosts.</summary>
public enum DnsMode
{
    /// <summary>Use system DNS first; after the first connection failure in a session, fall back to
    /// Cloudflare DoH for allowlisted hosts for the remainder of the session.</summary>
    Auto,

    /// <summary>Resolve allowlisted hosts via Cloudflare DoH up front (falling back to system DNS if DoH
    /// itself is unreachable).</summary>
    Always,

    /// <summary>Never use DoH; always rely on system DNS.</summary>
    Never,
}

/// <summary>
/// Serializes <see cref="DnsMode"/> as its name (<c>"Auto"</c>, <c>"Always"</c>, <c>"Never"</c>) and
/// tolerates case differences or unknown strings on read (falling back to <see cref="DnsMode.Auto"/> so a
/// hand-edited typo in <c>settings.json</c> never corrupts the whole file).
/// </summary>
internal sealed class DnsModeJsonConverter : JsonConverter<DnsMode?>
{
    public override DnsMode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
        {
            string? raw = reader.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return Enum.TryParse(raw.Trim(), ignoreCase: true, out DnsMode parsed) && Enum.IsDefined(parsed)
                ? parsed
                : DnsMode.Auto;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int num))
        {
            var value = (DnsMode)num;
            return Enum.IsDefined(value) ? value : DnsMode.Auto;
        }

        return DnsMode.Auto;
    }

    public override void Write(Utf8JsonWriter writer, DnsMode? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.Value.ToString());
    }
}

/// <summary>Resolved IPv4 addresses bound to the host they were queried for, so an HTTP redirect to a
/// different host can never accidentally connect to the previous host's IP.</summary>
public sealed record DohResolution(string Host, IPAddress[] Addresses);

/// <summary>
/// Holds the active <see cref="DnsMode"/> and whether system DNS has failed during the current process
/// session. Production clients share <see cref="Shared"/>; unit tests construct isolated instances.
/// </summary>
public sealed class DnsFallbackSession
{
    /// <summary>Process-wide session shared by the app's outbound HTTP clients.</summary>
    public static DnsFallbackSession Shared { get; } = new();

    private int _mode = (int)DnsMode.Auto;
    private int _dnsFailed;

    public DnsMode Mode
    {
        get => (DnsMode)Volatile.Read(ref _mode);
        set => Volatile.Write(ref _mode, (int)value);
    }

    /// <summary>True once a connection failure to an allowlisted host triggered DoH fallback in this session.</summary>
    public bool IsDnsFailed => Volatile.Read(ref _dnsFailed) != 0;

    // The failure state persists until the app closes and affects EVERY host on the allowlist, not just the one
    // whose lookup failed: after one name-resolution failure, all later requests to any allowlisted host resolve
    // through Cloudflare DoH. Per-host state would need a dictionary and an expiry; session-wide is the cheap,
    // deliberate ceiling (restart the app, or set DnsMode "Never", to leave it).
    public void MarkDnsFailed() => Volatile.Write(ref _dnsFailed, 1);

    internal void Reset() => Volatile.Write(ref _dnsFailed, 0);
}

/// <summary>
/// Minimal Cloudflare DNS-over-HTTPS (JSON API, IPv4 A records) resolver with bounded TTL caching.
/// Uses its own plain <see cref="HttpClient"/> directly to <see cref="AppConfig.DohEndpointUrl"/> so it
/// can never recurse through <see cref="DnsFallbackHttpHandler"/>.
/// </summary>
public sealed class CloudflareDohResolver
{
    private static readonly TimeSpan MinCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>Shared resolver used by production <see cref="DnsFallbackHttpHandler"/> instances.</summary>
    public static CloudflareDohResolver Shared { get; } = new();

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, (IPAddress[] Addresses, DateTimeOffset ExpiresAt)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public CloudflareDohResolver() : this(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) }) { }

    /// <summary>Test seam: inject a fake handler and clock to test DoH parsing and caching offline.</summary>
    internal CloudflareDohResolver(HttpMessageHandler handler, TimeProvider? clock = null)
    {
        _http = new HttpClient(handler) { Timeout = AppConfig.DohTimeout };
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Resolve IPv4 addresses for <paramref name="host"/> via Cloudflare DoH. Returns an empty
    /// array on any network/parse error (never throws for transient resolver errors).</summary>
    public async Task<IPAddress[]> ResolveIpv4Async(string host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return [];

        var now = _clock.GetUtcNow();
        if (_cache.TryGetValue(host, out var cached) && cached.ExpiresAt > now)
            return cached.Addresses;

        string url = $"{AppConfig.DohEndpointUrl}?name={Uri.EscapeDataString(host)}&type=A";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));

        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return [];

        string body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var (addresses, ttl) = ParseDohJson(body);
        if (addresses.Length > 0)
            _cache[host] = (addresses, _clock.GetUtcNow() + ttl);

        return addresses;
    }

    /// <summary>
    /// Parse a Cloudflare DNS-JSON response for IPv4 (<c>type == 1</c>) records. Rejects non-zero DNS
    /// statuses, non-IPv4 answers, loopback addresses, <c>0.0.0.0</c>, RFC 1918 private ranges
    /// (<c>10/8</c>, <c>172.16/12</c>, <c>192.168/16</c>) and link-local <c>169.254/16</c>. Pure and directly
    /// testable.
    /// </summary>
    internal static (IPAddress[] Addresses, TimeSpan Ttl) ParseDohJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ([], MinCacheTtl);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("Status", out var statusEl)
                || statusEl.ValueKind != JsonValueKind.Number
                || statusEl.GetInt32() != 0)
                return ([], MinCacheTtl);

            if (!root.TryGetProperty("Answer", out var answerEl)
                || answerEl.ValueKind != JsonValueKind.Array)
                return ([], MinCacheTtl);

            var addresses = new List<IPAddress>();
            int minTtlSeconds = (int)MaxCacheTtl.TotalSeconds;

            foreach (var item in answerEl.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var typeEl)
                    || typeEl.ValueKind != JsonValueKind.Number
                    || typeEl.GetInt32() != 1) // 1 = IPv4 A record
                    continue;

                if (!item.TryGetProperty("data", out var dataEl)
                    || dataEl.ValueKind != JsonValueKind.String)
                    continue;

                if (!IPAddress.TryParse(dataEl.GetString(), out var ip))
                    continue;

                if (ip.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                // Never let a DoH response redirect a public host onto loopback, unspecified, private (RFC 1918)
                // or link-local addresses: a forged answer could otherwise steer the connection into the LAN.
                if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || IsPrivateOrLinkLocal(ip))
                    continue;

                addresses.Add(ip);

                if (item.TryGetProperty("TTL", out var ttlEl) && ttlEl.ValueKind == JsonValueKind.Number)
                {
                    int ttlSec = ttlEl.GetInt32();
                    if (ttlSec < minTtlSeconds) minTtlSeconds = ttlSec;
                }
            }

            if (addresses.Count == 0)
                return ([], MinCacheTtl);

            var clamped = TimeSpan.FromSeconds(
                Math.Clamp(minTtlSeconds, (int)MinCacheTtl.TotalSeconds, (int)MaxCacheTtl.TotalSeconds));
            return ([.. addresses], clamped);
        }
        catch (JsonException)
        {
            return ([], MinCacheTtl);
        }
    }

    /// <summary>RFC 1918 (<c>10/8</c>, <c>172.16/12</c>, <c>192.168/16</c>) or link-local (<c>169.254/16</c>). IPv4 only.</summary>
    private static bool IsPrivateOrLinkLocal(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }
}

/// <summary>
/// <see cref="DelegatingHandler"/> that intercepts connection failures (<see cref="HttpRequestException"/>)
/// for allowlisted hosts in <see cref="AppConfig.DohFallbackHosts"/> and retries using Cloudflare DoH
/// (<see cref="AppConfig.DohEndpointUrl"/>, IPv4).
///
/// <para>
/// Real HTTPS connections keep TLS SNI and full certificate chain validation intact: resolved IPv4 addresses
/// are attached to <see cref="HttpRequestMessage.Options"/> under <see cref="ResolvedAddressesKey"/> and
/// consumed by the inner <see cref="SocketsHttpHandler.ConnectCallback"/>, which dials the IP directly while
/// <see cref="SocketsHttpHandler"/> performs normal TLS authentication for the original hostname.
/// </para>
///
/// <para>
/// Localhost, <c>127.0.0.1</c>, IP literals, single-label names, <c>*.local</c>, <c>*.localhost</c>, Steam
/// CDP traffic, and any host outside <see cref="AppConfig.DohFallbackHosts"/> always bypass DoH.
/// </para>
/// </summary>
public sealed class DnsFallbackHttpHandler : DelegatingHandler
{
    /// <summary>
    /// Request option key carrying the DoH-resolved IPv4 addresses for the current request's host.
    /// Read by the inner <see cref="SocketsHttpHandler.ConnectCallback"/> in production and observable by
    /// mock <see cref="HttpMessageHandler"/>s in unit tests.
    /// </summary>
    public static readonly HttpRequestOptionsKey<DohResolution> ResolvedAddressesKey =
        new("LuaTools.DohResolvedAddresses");

    private static readonly HashSet<string> AllowlistedHosts =
        new(AppConfig.DohFallbackHosts, StringComparer.OrdinalIgnoreCase);

    private readonly Func<DnsMode> _getMode;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveDohAsync;
    private readonly DnsFallbackSession _session;
    private readonly Action<string> _log;

    /// <summary>Production factory: wraps a <see cref="SocketsHttpHandler"/> whose <c>ConnectCallback</c>
    /// honors DoH-resolved IPv4 addresses while preserving TLS SNI and certificate validation.</summary>
    public static DnsFallbackHttpHandler CreateDefault(
        bool allowAutoRedirect = true,
        bool useCookies = true,
        TimeSpan? pooledConnectionLifetime = null,
        DnsFallbackSession? session = null,
        Func<DnsMode>? getMode = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolveDohAsync = null)
    {
        var effectiveSession = session ?? DnsFallbackSession.Shared;
        var effectiveGetMode = getMode ?? (() => effectiveSession.Mode);
        var effectiveResolve = resolveDohAsync ?? CloudflareDohResolver.Shared.ResolveIpv4Async;

        var sockets = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            UseCookies = useCookies,
            ConnectCallback = (ctx, ct) => ConnectAsync(ctx, effectiveSession, effectiveGetMode, effectiveResolve, ct),
        };
        if (pooledConnectionLifetime is { } ttl)
            sockets.PooledConnectionLifetime = ttl;

        return new DnsFallbackHttpHandler(sockets, effectiveGetMode, effectiveResolve, effectiveSession, AppLog.Log);
    }

    /// <summary>Testable constructor accepting any inner <see cref="HttpMessageHandler"/> and resolvers.</summary>
    internal DnsFallbackHttpHandler(
        HttpMessageHandler innerHandler,
        Func<DnsMode> getMode,
        Func<string, CancellationToken, Task<IPAddress[]>> resolveDohAsync,
        DnsFallbackSession? session = null,
        Action<string>? log = null)
        : base(innerHandler)
    {
        _getMode = getMode ?? throw new ArgumentNullException(nameof(getMode));
        _resolveDohAsync = resolveDohAsync ?? throw new ArgumentNullException(nameof(resolveDohAsync));
        _session = session ?? new DnsFallbackSession();
        _log = log ?? AppLog.Log;
    }

    /// <summary>True once this handler's session has switched to DoH fallback after a connection failure.</summary>
    public bool IsDnsFailedInSession => _session.IsDnsFailed;

    /// <summary>True when <paramref name="host"/> is local, loopback, an IP literal, or otherwise ineligible
    /// for DoH resolution.</summary>
    public static bool IsLocalOrBypassHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;

        string trimmed = host.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            trimmed = trimmed[1..^1];

        if (IPAddress.TryParse(trimmed, out _))
            return true;

        string normalized = trimmed.TrimEnd('.');
        if (normalized.Length == 0) return true;

        if (normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return true;

        // Single-label internal hostnames (no dot) never belong on public DoH.
        return !normalized.Contains('.');
    }

    /// <summary>True when <paramref name="host"/> is on the compiled-in <see cref="AppConfig.DohFallbackHosts"/> allowlist.</summary>
    public static bool IsAllowlistedHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        return AllowlistedHosts.Contains(host.Trim().TrimEnd('.'));
    }

    /// <summary>True when <paramref name="host"/> is both non-local and explicitly allowlisted for DoH.</summary>
    public static bool CanUseDohForHost(string? host) =>
        !IsLocalOrBypassHost(host) && IsAllowlistedHost(host);

    /// <summary>True when <paramref name="uri"/> is eligible for Cloudflare DoH fallback. HTTPS only: a DoH-pinned
    /// address on a plain-http request would send the traffic in clear to whatever the resolver answered.</summary>
    public static bool CanUseDohForUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true, IsLoopback: false }
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && CanUseDohForHost(uri.Host);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var mode = _getMode();
        var uri = request.RequestUri;

        // Never intercept local/CDP traffic, non-allowlisted hosts, or when DNS fallback is disabled.
        if (mode == DnsMode.Never || !CanUseDohForUri(uri))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        string host = uri!.Host;

        // Always mode, or Auto mode after a previous connection failure already flipped the session flag.
        if (mode == DnsMode.Always || (mode == DnsMode.Auto && _session.IsDnsFailed))
        {
            var ips = await TryResolveDohAsync(host, cancellationToken).ConfigureAwait(false);
            if (ips.Length > 0)
                request.Options.Set(ResolvedAddressesKey, new DohResolution(host, ips));

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Auto mode, first attempt: try normal system DNS first. On a NAME-RESOLUTION failure, mark DNS as
        // failed for this session, resolve via Cloudflare DoH, and retry the request.
        var originalUri = uri;
        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (IsDnsFailure(ex) && !cancellationToken.IsCancellationRequested)
        {
            var ips = await TryResolveDohAsync(host, cancellationToken).ConfigureAwait(false);
            if (ips.Length == 0)
                throw;

            _session.MarkDnsFailed();
            SafeLog($"DnsFallback: DNS lookup for '{host}' failed; switching session to Cloudflare DoH ({ips.Length} IPv4).");
            request.Options.Set(ResolvedAddressesKey, new DohResolution(host, ips));
            // The failed attempt may have followed redirects, and SocketsHttpHandler rewrites RequestUri as it
            // goes: restart from the host the DoH addresses were resolved for.
            request.RequestUri = originalUri;
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Only a failed name lookup is DNS trouble. A reset, a refused connection, a TLS error or a timeout
    /// (a VPN or firewall dropping traffic, say) is not, and answering it with DoH would flip the whole
    /// session onto Cloudflare and send the hostnames to a third party for nothing.
    /// </summary>
    private static bool IsDnsFailure(HttpRequestException ex) =>
        ex.InnerException is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData };

    private async Task<IPAddress[]> TryResolveDohAsync(string host, CancellationToken ct)
    {
        try
        {
            var ips = await _resolveDohAsync(host, ct).ConfigureAwait(false);
            return ips ?? [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SafeLog($"DnsFallback: Cloudflare DoH lookup for '{host}' failed: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        DnsFallbackSession session,
        Func<DnsMode> getMode,
        Func<string, CancellationToken, Task<IPAddress[]>> resolveDohAsync,
        CancellationToken ct)
    {
        string targetHost = context.DnsEndPoint.Host;
        int port = context.DnsEndPoint.Port;
        IPAddress[]? addresses = null;

        // 1. Prefer the addresses already resolved by SendAsync — ONLY when they match this exact host
        //    (so an auto-followed redirect to another host never connects to the previous host's IP).
        if (context.InitialRequestMessage.Options.TryGetValue(ResolvedAddressesKey, out var resolution)
            && string.Equals(resolution.Host, targetHost, StringComparison.OrdinalIgnoreCase)
            && resolution.Addresses is { Length: > 0 })
        {
            addresses = resolution.Addresses;
        }
        // 2. If an auto-followed redirect landed on another allowlisted host while DoH is active, resolve it too.
        else if (CanUseDohForHost(targetHost))
        {
            var mode = getMode();
            if (mode == DnsMode.Always || (mode == DnsMode.Auto && session.IsDnsFailed))
            {
                try
                {
                    var redirectIps = await resolveDohAsync(targetHost, ct).ConfigureAwait(false);
                    if (redirectIps is { Length: > 0 })
                        addresses = redirectIps;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* fall through to system DNS */ }
            }
        }

        if (addresses is { Length: > 0 })
        {
            Exception? lastError = null;
            foreach (var ip in addresses)
            {
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(ip, port, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    socket.Dispose();
                    lastError = ex;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            if (lastError is not null)
                ExceptionDispatchInfo.Throw(lastError);
        }

        // Standard system DNS path.
        var defaultSocket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await defaultSocket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
            return new NetworkStream(defaultSocket, ownsSocket: true);
        }
        catch
        {
            defaultSocket.Dispose();
            throw;
        }
    }

    private void SafeLog(string message)
    {
        try { _log(message); } catch { /* logging must never fail a network request */ }
    }
}
