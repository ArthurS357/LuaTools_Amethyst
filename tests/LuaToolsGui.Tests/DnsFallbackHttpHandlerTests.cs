using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers <see cref="DnsFallbackHttpHandler"/> and <see cref="CloudflareDohResolver"/>:
/// <list type="bullet">
///   <item><see cref="DnsMode.Auto"/> falls back to Cloudflare DoH on the first <see cref="HttpRequestException"/>
///   and keeps DoH active for allowlisted hosts for the rest of the session.</item>
///   <item><see cref="DnsMode.Always"/> resolves via DoH up front and degrades gracefully if DoH fails.</item>
///   <item><see cref="DnsMode.Never"/> never invokes DoH.</item>
///   <item>Localhost, <c>127.0.0.1</c> (CDP), IP literals, and non-allowlisted hosts always bypass DoH.</item>
///   <item>CDP (<see cref="CefInjectorService"/>) and F-09 (<see cref="LuaToolsApiClient.FixDownloadHttp"/>)
///   remain untouched.</item>
/// </list>
/// </summary>
public sealed class DnsFallbackHttpHandlerTests
{
    private static readonly IPAddress FakeCloudflareIp = IPAddress.Parse("104.21.10.20");
    private static readonly IPAddress FakeDbIp = IPAddress.Parse("104.21.30.40");

    [Fact]
    public async Task Auto_mode_retries_with_doh_on_first_connection_failure_and_marks_session_failed()
    {
        var session = new DnsFallbackSession();
        var dohLookups = new List<string>();
        var observedResolutions = new List<DohResolution?>();

        var inner = new StubHandler(req =>
        {
            req.Options.TryGetValue(DnsFallbackHttpHandler.ResolvedAddressesKey, out var resolved);
            observedResolutions.Add(resolved);

            // Attempt 1 has no DoH resolution -> simulate ISP DNS / connection failure.
            if (resolved is null)
                throw DnsFailure();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok-via-doh"),
            };
        });

        using var handler = new DnsFallbackHttpHandler(
            inner,
            () => DnsMode.Auto,
            (host, _) =>
            {
                dohLookups.Add(host);
                return Task.FromResult(host == "db.lua.tools" ? new[] { FakeDbIp } : new[] { FakeCloudflareIp });
            },
            session,
            _ => { });
        using var client = new HttpClient(handler);

        // 1st request: fails on system DNS, retries with DoH IPv4, marks session failed.
        using var first = await client.GetAsync("https://lua.tools/api/test");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.Content.ReadAsStringAsync()).Should().Be("ok-via-doh");
        session.IsDnsFailed.Should().BeTrue();
        dohLookups.Should().Equal("lua.tools");
        observedResolutions.Should().HaveCount(2);
        observedResolutions[0].Should().BeNull("attempt 1 must try system DNS first");
        observedResolutions[1].Should().NotBeNull("attempt 2 must carry the DoH-resolved IPv4");
        observedResolutions[1]!.Host.Should().Be("lua.tools");
        observedResolutions[1]!.Addresses.Should().Equal(FakeCloudflareIp);

        // 2nd request in the same session: session is already marked failed, so DoH is used on attempt 1.
        using var second = await client.GetAsync("https://db.lua.tools/auth/v1/health");

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        dohLookups.Should().Equal("lua.tools", "db.lua.tools");
        observedResolutions.Should().HaveCount(3, "no wasted failed attempt once the session is in DoH fallback");
        observedResolutions[2]!.Host.Should().Be("db.lua.tools");
        observedResolutions[2]!.Addresses.Should().Equal(FakeDbIp);
    }

    [Fact]
    public async Task Never_mode_does_not_fallback_to_doh_on_connection_failure()
    {
        var session = new DnsFallbackSession();
        int dohCalls = 0;
        int innerCalls = 0;

        var inner = new StubHandler(_ =>
        {
            innerCalls++;
            throw new HttpRequestException("DNS down");
        });

        using var handler = new DnsFallbackHttpHandler(
            inner,
            () => DnsMode.Never,
            (_, _) =>
            {
                dohCalls++;
                return Task.FromResult(new[] { FakeCloudflareIp });
            },
            session,
            _ => { });
        using var client = new HttpClient(handler);

        var act = () => client.GetAsync("https://lua.tools/api/test");

        await act.Should().ThrowAsync<HttpRequestException>();
        innerCalls.Should().Be(1);
        dohCalls.Should().Be(0);
        session.IsDnsFailed.Should().BeFalse();
    }

    [Fact]
    public async Task Always_mode_resolves_via_doh_before_first_attempt()
    {
        DohResolution? seen = null;
        int dohCalls = 0;

        var inner = new StubHandler(req =>
        {
            req.Options.TryGetValue(DnsFallbackHttpHandler.ResolvedAddressesKey, out seen);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var handler = new DnsFallbackHttpHandler(
            inner,
            () => DnsMode.Always,
            (_, _) =>
            {
                dohCalls++;
                return Task.FromResult(new[] { FakeCloudflareIp });
            },
            new DnsFallbackSession(),
            _ => { });
        using var client = new HttpClient(handler);

        using var res = await client.GetAsync("https://hubcapmanifest.com/api/status");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        dohCalls.Should().Be(1);
        seen.Should().NotBeNull();
        seen!.Host.Should().Be("hubcapmanifest.com");
        seen.Addresses.Should().Equal(FakeCloudflareIp);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/json")]            // Steam CDP
    [InlineData("http://127.0.0.1:6767/add")]             // Local plugin bridge
    [InlineData("http://localhost:53789/callback")]       // Local OAuth callback
    [InlineData("http://[::1]:8080/json")]                // IPv6 loopback
    [InlineData("http://printer.local/status")]           // mDNS .local
    [InlineData("http://steam.localhost/test")]           // *.localhost
    [InlineData("http://intranet/health")]                // Single-label host
    [InlineData("http://167.235.229.108/check_apis")]     // Bare IP literal
    [InlineData("https://example.com/not-allowlisted")]   // Outside AppConfig.DohFallbackHosts
    [InlineData("http://lua.tools/api/test")]             // Allowlisted host, but plain http: HTTPS only
    public async Task Local_cdp_and_non_allowlisted_hosts_never_use_doh(string url)
    {
        var session = new DnsFallbackSession();
        int dohCalls = 0;

        var inner = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        using var autoHandler = new DnsFallbackHttpHandler(
            inner, () => DnsMode.Auto, (_, _) => { dohCalls++; return Task.FromResult(new[] { FakeCloudflareIp }); },
            session, _ => { });
        using var alwaysHandler = new DnsFallbackHttpHandler(
            inner, () => DnsMode.Always, (_, _) => { dohCalls++; return Task.FromResult(new[] { FakeCloudflareIp }); },
            session, _ => { });

        using var autoClient = new HttpClient(autoHandler);
        using var alwaysClient = new HttpClient(alwaysHandler);

        await FluentActions.Awaiting(() => autoClient.GetAsync(url)).Should().ThrowAsync<HttpRequestException>();
        await FluentActions.Awaiting(() => alwaysClient.GetAsync(url)).Should().ThrowAsync<HttpRequestException>();

        dohCalls.Should().Be(0, $"DoH must never run for {url}");
        session.IsDnsFailed.Should().BeFalse();
    }

    [Fact]
    public async Task Auto_mode_does_not_mark_session_failed_when_doh_returns_no_addresses()
    {
        var session = new DnsFallbackSession();
        var inner = new StubHandler(_ => throw DnsFailure());

        using var handler = new DnsFallbackHttpHandler(
            inner, () => DnsMode.Auto, (_, _) => Task.FromResult(Array.Empty<IPAddress>()),
            session, _ => { });
        using var client = new HttpClient(handler);

        await FluentActions.Awaiting(() => client.GetAsync("https://lua.tools/api/test"))
            .Should().ThrowAsync<HttpRequestException>();

        session.IsDnsFailed.Should().BeFalse("an offline DoH lookup must not flip the session flag");
    }

    // ── Cloudflare DoH JSON parser & cache ───────────────────────────────────

    [Fact]
    public void ParseDohJson_extracts_valid_ipv4_a_records_and_ignores_loopback_cname_and_ipv6()
    {
        const string json = """
        {
          "Status": 0,
          "Answer": [
            { "name": "lua.tools", "type": 5,  "TTL": 600, "data": "alias.lua.tools." },
            { "name": "lua.tools", "type": 28, "TTL": 300, "data": "2606:4700:3030::6815:102" },
            { "name": "lua.tools", "type": 1,  "TTL": 10,  "data": "127.0.0.1" },
            { "name": "lua.tools", "type": 1,  "TTL": 10,  "data": "0.0.0.0" },
            { "name": "lua.tools", "type": 1,  "TTL": 120, "data": "104.21.10.20" },
            { "name": "lua.tools", "type": 1,  "TTL": 900, "data": "172.67.15.30" }
          ]
        }
        """;

        var (addresses, ttl) = CloudflareDohResolver.ParseDohJson(json);

        addresses.Should().Equal(IPAddress.Parse("104.21.10.20"), IPAddress.Parse("172.67.15.30"));
        ttl.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void ParseDohJson_drops_rfc1918_and_link_local_answers_but_keeps_their_public_neighbours()
    {
        // Edges on purpose: 172.15 / 172.32 and 192.169 sit just outside the private ranges and are public.
        const string json = """
        {
          "Status": 0,
          "Answer": [
            { "type": 1, "TTL": 60, "data": "10.0.0.1" },
            { "type": 1, "TTL": 60, "data": "10.255.255.254" },
            { "type": 1, "TTL": 60, "data": "172.16.0.1" },
            { "type": 1, "TTL": 60, "data": "172.31.255.254" },
            { "type": 1, "TTL": 60, "data": "192.168.1.1" },
            { "type": 1, "TTL": 60, "data": "169.254.169.254" },
            { "type": 1, "TTL": 60, "data": "172.15.0.1" },
            { "type": 1, "TTL": 60, "data": "172.32.0.1" },
            { "type": 1, "TTL": 60, "data": "192.169.0.1" },
            { "type": 1, "TTL": 60, "data": "169.253.0.1" },
            { "type": 1, "TTL": 60, "data": "104.21.10.20" }
          ]
        }
        """;

        var (addresses, _) = CloudflareDohResolver.ParseDohJson(json);

        addresses.Should().Equal(
            IPAddress.Parse("172.15.0.1"), IPAddress.Parse("172.32.0.1"), IPAddress.Parse("192.169.0.1"),
            IPAddress.Parse("169.253.0.1"), IPAddress.Parse("104.21.10.20"));

        CloudflareDohResolver.ParseDohJson("""{"Status":0,"Answer":[{"type":1,"TTL":60,"data":"192.168.0.10"}]}""")
            .Addresses.Should().BeEmpty("an answer made only of private addresses yields nothing to dial");
    }

    [Theory]
    [InlineData("https://lua.tools/api/test", true)]
    [InlineData("HTTPS://API.GITHUB.COM/repos", true)]
    [InlineData("http://lua.tools/api/test", false)]            // plain http is never eligible
    [InlineData("http://api.github.com/repos", false)]
    [InlineData("https://example.com/", false)]                 // not allowlisted
    [InlineData("https://127.0.0.1/", false)]                   // loopback
    public void CanUseDohForUri_is_https_only_and_allowlist_only(string url, bool expected) =>
        DnsFallbackHttpHandler.CanUseDohForUri(new Uri(url)).Should().Be(expected);

    [Fact]
    public void ParseDohJson_rejects_nonzero_dns_status_and_clamps_ttl()
    {
        CloudflareDohResolver.ParseDohJson("""{"Status":3,"Answer":[{"type":1,"TTL":60,"data":"104.21.1.1"}]}""")
            .Addresses.Should().BeEmpty("NXDOMAIN (Status 3) must be rejected");

        var (_, lowClamped) = CloudflareDohResolver.ParseDohJson(
            """{"Status":0,"Answer":[{"type":1,"TTL":5,"data":"104.21.1.1"}]}""");
        lowClamped.Should().Be(TimeSpan.FromSeconds(30));

        var (_, highClamped) = CloudflareDohResolver.ParseDohJson(
            """{"Status":0,"Answer":[{"type":1,"TTL":86400,"data":"104.21.1.1"}]}""");
        highClamped.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task CloudflareDohResolver_queries_cloudflare_endpoint_and_caches_within_ttl()
    {
        var requests = new List<HttpRequestMessage>();
        var clock = new FakeClock(DateTimeOffset.Parse("2026-11-19T00:00:00Z"));
        var stub = new StubHandler(req =>
        {
            requests.Add(req);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"Status":0,"Answer":[{"type":1,"TTL":60,"data":"104.21.10.20"}]}"""),
            };
        });

        var resolver = new CloudflareDohResolver(stub, clock);

        var first = await resolver.ResolveIpv4Async("lua.tools");
        var second = await resolver.ResolveIpv4Async("lua.tools");
        clock.Advance(TimeSpan.FromSeconds(61));
        var third = await resolver.ResolveIpv4Async("lua.tools");

        first.Should().Equal(FakeCloudflareIp);
        second.Should().Equal(FakeCloudflareIp);
        third.Should().Equal(FakeCloudflareIp);
        requests.Should().HaveCount(2, "second lookup within 60 s TTL must hit the cache; third after expiry re-queries");
        requests[0].RequestUri!.AbsoluteUri.Should().Be("https://1.1.1.1/dns-query?name=lua.tools&type=A");
        requests[0].Headers.Accept.ToString().Should().Contain("application/dns-json");
    }

    /// <summary>What SocketsHttpHandler throws when the system resolver fails: the SocketException is the inner.</summary>
    private static HttpRequestException DnsFailure(SocketError code = SocketError.HostNotFound) =>
        new("name resolution failed", new SocketException((int)code));

    [Theory]
    [InlineData(SocketError.ConnectionReset)]    // a VPN or firewall dropping the connection
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.TimedOut)]
    public async Task Auto_mode_ignores_connection_failures_that_are_not_dns(SocketError code)
    {
        var session = new DnsFallbackSession();
        int dohCalls = 0, innerCalls = 0;
        var inner = new StubHandler(_ => { innerCalls++; throw new HttpRequestException("connect", new SocketException((int)code)); });
        using var handler = new DnsFallbackHttpHandler(
            inner, () => DnsMode.Auto, (_, _) => { dohCalls++; return Task.FromResult(new[] { FakeCloudflareIp }); },
            session, _ => { });
        using var client = new HttpClient(handler);

        await FluentActions.Awaiting(() => client.GetAsync("https://lua.tools/api/test"))
            .Should().ThrowAsync<HttpRequestException>();

        dohCalls.Should().Be(0, "hostnames must not go to a third-party resolver for a non-DNS failure");
        innerCalls.Should().Be(1);
        session.IsDnsFailed.Should().BeFalse();
    }

    [Theory]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.TryAgain)]
    [InlineData(SocketError.NoData)]
    public async Task Auto_mode_falls_back_for_each_name_resolution_error(SocketError code)
    {
        var session = new DnsFallbackSession();
        var inner = new StubHandler(req => req.Options.TryGetValue(DnsFallbackHttpHandler.ResolvedAddressesKey, out _)
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : throw DnsFailure(code));
        using var handler = new DnsFallbackHttpHandler(
            inner, () => DnsMode.Auto, (_, _) => Task.FromResult(new[] { FakeCloudflareIp }), session, _ => { });
        using var client = new HttpClient(handler);

        (await client.GetAsync("https://lua.tools/api/test")).StatusCode.Should().Be(HttpStatusCode.OK);
        session.IsDnsFailed.Should().BeTrue();
    }

    [Fact]
    public async Task The_retry_restarts_from_the_original_uri_after_a_redirect_rewrote_it()
    {
        var session = new DnsFallbackSession();
        var seen = new List<Uri?>();
        var inner = new StubHandler(req =>
        {
            seen.Add(req.RequestUri);
            if (seen.Count > 1) return new HttpResponseMessage(HttpStatusCode.OK);
            req.RequestUri = new Uri("https://github.com/redirected/elsewhere"); // what an auto-followed redirect does
            throw DnsFailure();
        });
        using var handler = new DnsFallbackHttpHandler(
            inner, () => DnsMode.Auto, (_, _) => Task.FromResult(new[] { FakeCloudflareIp }), session, _ => { });
        using var client = new HttpClient(handler);

        await client.GetAsync("https://lua.tools/api/test");

        seen.Should().HaveCount(2);
        seen[1].Should().Be(new Uri("https://lua.tools/api/test"));
    }

    // ── CDP & F-09 isolation guards ──────────────────────────────────────────

    [Fact]
    public void Cdp_and_F09_clients_do_not_use_DnsFallbackHttpHandler_while_api_client_does()
    {
        string dir = Path.Combine(Path.GetTempPath(), "luatools-doh-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            var api = new LuaToolsApiClient(null!, null!, null!, null!, null!);
            var cdp = new CefInjectorService(new SteamService(new SettingsService(dir)), NullLogger<CefInjectorService>.Instance);

            var apiHandler = RawHandlerOf(PrivateHttp(api));
            var fixHandler = RawHandlerOf(api.FixDownloadHttp);
            var cdpHandler = RawHandlerOf(PrivateHttp(cdp));

            apiHandler.Should().BeOfType<DnsFallbackHttpHandler>();
            // The whole chain, not just the outer handler: DoH wrapped under another DelegatingHandler would
            // still re-resolve the R2 host / localhost.
            ChainOf(fixHandler).Should().NotContain(h => h is DnsFallbackHttpHandler,
                "F-09 dedicated signed-download client must stay isolated");
            ChainOf(cdpHandler).Should().NotContain(h => h is DnsFallbackHttpHandler,
                "CDP localhost client must stay isolated");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ } }
    }

    private static HttpClient PrivateHttp(object owner) =>
        (HttpClient)owner.GetType().GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;

    private static List<HttpMessageHandler> ChainOf(HttpMessageHandler? handler)
    {
        var chain = new List<HttpMessageHandler>();
        for (; handler is not null; handler = (handler as DelegatingHandler)?.InnerHandler) chain.Add(handler);
        return chain;
    }

    private static HttpMessageHandler RawHandlerOf(HttpClient http) =>
        (HttpMessageHandler)typeof(HttpMessageInvoker)
            .GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(http)!;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
