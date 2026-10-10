using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The loopback bridge end to end: a real <see cref="HttpListener"/> on a free port driving the real
/// <see cref="HttpServerService.HandleRequest"/>. F-02: every side-effect route also answered GET (the verb
/// was never checked) and requests with no Origin and no token were served, so any web page could delete a
/// lua or start an install with a GET by navigation or &lt;img&gt;. F-12: none of these routes had a test.
/// </summary>
public sealed class HttpServerRoutesTests : IDisposable
{
    private const long AppId = 730;
    private const string TokenHeader = HttpServerService.TokenHeader;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    private readonly string _stPlugIn;
    private readonly HttpListener _listener = new();
    private readonly HttpClient _http = new();
    private readonly string _baseUrl;
    private readonly string _luaPath;

    public HttpServerRoutesTests()
    {
        string steamRoot = Path.Combine(_dir, "steam");
        _stPlugIn = Path.Combine(steamRoot, "config", "stplug-in");
        Directory.CreateDirectory(_stPlugIn);
        Directory.CreateDirectory(Path.Combine(steamRoot, "depotcache"));
        _luaPath = Path.Combine(_stPlugIn, $"{AppId}.lua");

        var settings = new SettingsService(_dir) { SteamPathOverride = steamRoot };
        var cache = new CacheService(Path.Combine(_dir, "cache"));
        var steam = new SteamService(settings);
        var vault = new LuaVault(() => steam.StPlugInDir, Path.Combine(_dir, "vault"));
        var installer = new LuaInstaller(steam, settings, cache, vault);
        var server = new HttpServerService(installer, steam, cache,
            new ServiceCollection().BuildServiceProvider(), NullLogger<HttpServerService>.Instance);

        int port = FreePort();
        _baseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(_baseUrl + "/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (_listener.IsListening)
                    _ = server.HandleRequest(await _listener.GetContextAsync());
            }
            catch (Exception) { /* listener stopped */ }
        });
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        _http.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task<HttpResponseMessage> Send(
        HttpMethod method, string path, bool token = false, string? origin = null, string? host = null)
    {
        using var req = new HttpRequestMessage(method, _baseUrl + path);
        if (token) req.Headers.TryAddWithoutValidation(TokenHeader, HttpServerService.SessionToken);
        if (origin is not null) req.Headers.TryAddWithoutValidation("Origin", origin);
        if (host is not null) req.Headers.Host = host;
        if (method == HttpMethod.Post) req.Content = new StringContent("{}");
        return await _http.SendAsync(req);
    }

    // Every route with a side effect, with the verb it requires. The first four of the seven the audit named
    // are the destructive/expensive ones; /add-source and /check-sources are the other two POSTs.
    public static TheoryData<string> SideEffectPaths() => new()
    {
        "/add/730", "/add-source/730", "/check-sources/730", "/download/730",
        "/cancel/730", "/remove/730", "/open/fix/730",
    };

    // ── Malicious: no credential ──────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(SideEffectPaths))]
    public async Task A_GET_without_a_token_is_forbidden(string path)
    {
        // What a navigation or <img> from any web page produces: a GET with no Origin and no token.
        using var res = await Send(HttpMethod.Get, path);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [MemberData(nameof(SideEffectPaths))]
    public async Task A_POST_without_a_token_is_forbidden(string path)
    {
        using var res = await Send(HttpMethod.Post, path);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("https://store.steampowered.com")]  // an allow-listed Steam origin no longer buys a pass
    [InlineData("https://steamcommunity.com")]
    public async Task A_browser_origin_without_the_token_is_forbidden(string origin)
    {
        using var res = await Send(HttpMethod.Post, "/remove/730", origin: origin);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        res.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse("there is no CORS any more");
    }

    [Fact]
    public async Task A_valid_token_from_a_rebound_host_is_forbidden()
    {
        using var res = await Send(HttpMethod.Get, "/has/730", token: true, host: "evil.example");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_GET_without_a_token_does_not_delete_the_lua()
    {
        File.WriteAllText(_luaPath, "addappid(730)\n");

        using var res = await Send(HttpMethod.Get, "/remove/730");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        File.Exists(_luaPath).Should().BeTrue("the regression this guards is a web page deleting a lua");
    }

    // ── Verb: a token does not make a side-effect route answer GET ────────────

    [Theory]
    [MemberData(nameof(SideEffectPaths))]
    public async Task A_GET_with_a_valid_token_does_not_reach_a_side_effect_route(string path)
    {
        using var res = await Send(HttpMethod.Get, path, token: true);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_GET_with_a_valid_token_does_not_delete_the_lua()
    {
        File.WriteAllText(_luaPath, "addappid(730)\n");

        using var res = await Send(HttpMethod.Get, "/remove/730", token: true);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        File.Exists(_luaPath).Should().BeTrue();
    }

    // ── Legitimate: the app's own bridge must keep working (false-positive guard) ──

    [Fact]
    public async Task A_POST_with_a_valid_token_removes_the_lua()
    {
        File.WriteAllText(_luaPath, "addappid(730)\n");

        using var res = await Send(HttpMethod.Post, "/remove/730", token: true);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        File.Exists(_luaPath).Should().BeFalse();
    }

    [Fact]
    public async Task A_POST_with_a_valid_token_and_no_origin_is_processed()
    {
        using var res = await Send(HttpMethod.Post, "/cancel/999999", token: true);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain("\"success\":true");
    }

    [Theory]
    [InlineData("/has/730")]
    [InlineData("/add-status/730")]
    [InlineData("/download-status/730")]
    public async Task Read_routes_still_answer_GET_with_a_valid_token(string path)
    {
        // /add-status needs DI services the test provider lacks, so only "reached the route" is asserted:
        // it must not be 403 (auth) or 404 (verb).
        using var res = await Send(HttpMethod.Get, path, token: true);

        res.StatusCode.Should().NotBe(HttpStatusCode.Forbidden).And.NotBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_read_route_reports_whether_the_lua_exists()
    {
        File.WriteAllText(_luaPath, "addappid(730)\n");

        using var res = await Send(HttpMethod.Get, "/has/730", token: true);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain("\"exists\":true");
    }

    [Fact]
    public async Task A_GET_on_a_POST_route_is_not_a_route_even_for_a_valid_caller()
    {
        // The verb is part of the route, so POST-only routes can't be reached by GET and vice versa.
        using var wrongVerb = await Send(HttpMethod.Post, "/has/730", token: true);

        wrongVerb.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── F-11: log injection / error leakage ───────────────────────────────────

    private async Task PostLog(string body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/log") { Content = new StringContent(body) };
        req.Headers.TryAddWithoutValidation(TokenHeader, HttpServerService.SessionToken);
        using var res = await _http.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Log_body_with_newlines_cannot_forge_a_log_line()
    {
        string id = Guid.NewGuid().ToString("N");

        await PostLog($"{{\"message\":\"a-{id}\\r\\n[2026-01-01 00:00:00.000] FORGED-{id}\"}}");

        var lines = File.ReadAllLines(AppLog.FilePath);
        lines.Should().NotContain(l => l.StartsWith("[2026-01-01") && l.Contains("FORGED-" + id));
        lines.Should().ContainSingle(l => l.Contains("a-" + id) && l.Contains("FORGED-" + id));
    }

    [Fact]
    public async Task A_plain_log_line_is_kept_and_timestamped_with_the_date()
    {
        string id = Guid.NewGuid().ToString("N");

        await PostLog($"{{\"message\":\"hello-{id}\"}}");

        File.ReadAllLines(AppLog.FilePath).Should().ContainSingle(l =>
            l.Contains("[plugin] hello-" + id)
            && System.Text.RegularExpressions.Regex.IsMatch(l, @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] "));
    }

    [Fact]
    public async Task Remove_failure_does_not_hand_the_exception_text_to_the_caller()
    {
        File.WriteAllText(_luaPath, "addappid(730)");
        File.SetAttributes(_luaPath, FileAttributes.ReadOnly);   // File.Delete throws with the full path
        try
        {
            using var res = await Send(HttpMethod.Post, "/remove/730", token: true);

            res.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            string body = await res.Content.ReadAsStringAsync();
            body.Should().Contain("Internal error").And.NotContain(_dir).And.NotContain("luatools-tests");
        }
        finally { File.SetAttributes(_luaPath, FileAttributes.Normal); }
    }

    [Fact]
    public async Task CheckSources_failure_does_not_hand_the_exception_text_to_the_caller()
    {
        // The empty service provider makes GetRequiredService throw, naming the internal type.
        using var res = await Send(HttpMethod.Post, "/check-sources/730", token: true);

        string body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("Internal error").And.NotContain("LuaToolsApiClient");
    }

    // ── Routing in isolation ──────────────────────────────────────────────────

    [Fact]
    public void MatchPost_requires_POST_and_MatchGet_requires_GET()
    {
        HttpServerService.MatchPost("POST", "/remove/730", "/remove/{appid}", out long id).Should().BeTrue();
        id.Should().Be(730);
        HttpServerService.MatchPost("GET", "/remove/730", "/remove/{appid}", out _).Should().BeFalse();
        HttpServerService.MatchGet("GET", "/has/730", "/has/{appid}", out _).Should().BeTrue();
        HttpServerService.MatchGet("POST", "/has/730", "/has/{appid}", out _).Should().BeFalse();
        HttpServerService.MatchPost("POST", "/remove/abc", "/remove/{appid}", out _)
            .Should().BeFalse("a non-numeric appid is a 404, not a 500");
    }
}
