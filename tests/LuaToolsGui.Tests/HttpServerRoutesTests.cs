using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Backups;
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
            new ServiceCollection().BuildServiceProvider(), NullLogger<HttpServerService>.Instance)
        {
            BaseDirectory = Path.Combine(_dir, "app-bin"),
        };
        Directory.CreateDirectory(server.BaseDirectory);

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

        var lines = TestLogRedirect.ReadLogLines();
        lines.Should().NotContain(l => l.StartsWith("[2026-01-01") && l.Contains("FORGED-" + id));
        lines.Should().ContainSingle(l => l.Contains("a-" + id) && l.Contains("FORGED-" + id));
    }

    [Fact]
    public async Task A_plain_log_line_is_kept_and_timestamped_with_the_date()
    {
        string id = Guid.NewGuid().ToString("N");

        await PostLog($"{{\"message\":\"hello-{id}\"}}");

        TestLogRedirect.ReadLogLines().Should().ContainSingle(l =>
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

    // ── /icon: embedded resource fallback + memory cache ─────────────────────

    [Fact]
    public async Task Icon_route_serves_the_embedded_icon_when_no_loose_file_exists_next_to_the_exe()
    {
        using var forbidden = await Send(HttpMethod.Get, "/icon");
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var wrongVerb = await Send(HttpMethod.Post, "/icon", token: true);
        wrongVerb.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // BaseDirectory is empty (matching win-x64 publish, which ships no loose icon file).
        using var res = await Send(HttpMethod.Get, "/icon", token: true);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        string dataUrl = doc.RootElement.GetProperty("dataUrl").GetString()!;
        const string prefix = "data:image/x-icon;base64,";
        dataUrl.Should().StartWith(prefix);

        byte[] served = Convert.FromBase64String(dataUrl[prefix.Length..]);
        byte[]? embedded = HttpServerService.ReadEmbeddedIcon();
        embedded.Should().NotBeNull();
        served.Should().Equal(embedded!);
        served[..4].Should().Equal([0x00, 0x00, 0x01, 0x00], "valid ICO header");
    }

    [Fact]
    public async Task Icon_route_caches_the_first_payload_in_memory_without_re_reading_disk()
    {
        string iconDir = Path.Combine(_dir, "custom-icon");
        Directory.CreateDirectory(iconDir);
        string pngPath = Path.Combine(iconDir, "luatools-icon.png");
        byte[] customBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        File.WriteAllBytes(pngPath, customBytes);

        var settings = new SettingsService(_dir) { SteamPathOverride = Path.Combine(_dir, "steam") };
        var cache = new CacheService(Path.Combine(_dir, "cache"));
        var steam = new SteamService(settings);
        var vault = new LuaVault(() => steam.StPlugInDir, Path.Combine(_dir, "vault"));
        var installer = new LuaInstaller(steam, settings, cache, vault);
        var customServer = new HttpServerService(installer, steam, cache,
            new ServiceCollection().BuildServiceProvider(), NullLogger<HttpServerService>.Instance)
        {
            BaseDirectory = iconDir,
        };

        int port = FreePort();
        using var customListener = new HttpListener();
        string url = $"http://127.0.0.1:{port}";
        customListener.Prefixes.Add(url + "/");
        customListener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (customListener.IsListening)
                    _ = customServer.HandleRequest(await customListener.GetContextAsync());
            }
            catch (Exception) { /* stopped */ }
        });

        try
        {
            async Task<string> GetIconBodyAsync()
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url + "/icon");
                req.Headers.TryAddWithoutValidation(TokenHeader, HttpServerService.SessionToken);
                using var r = await _http.SendAsync(req);
                r.StatusCode.Should().Be(HttpStatusCode.OK);
                return await r.Content.ReadAsStringAsync();
            }

            string first = await GetIconBodyAsync();
            string expected = $"data:image/png;base64,{Convert.ToBase64String(customBytes)}";
            first.Should().Contain(expected);

            // Delete the file on disk: a non-cached second read would fall through to the embedded .ico.
            File.Delete(pngPath);
            string second = await GetIconBodyAsync();
            second.Should().Be(first, "subsequent GET /icon calls must reuse the cached JSON payload");
        }
        finally
        {
            try { customListener.Stop(); } catch (ObjectDisposedException) { }
        }
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

/// <summary>
/// Lote L4 backend: authenticated discovery (<c>GET /backup/index</c>, <c>GET /backup/status</c>) and
/// fail-closed explicit restore (<c>POST /backup/install</c>) through the real loopback listener.
/// </summary>
public sealed class HttpBackupRoutesTests : IDisposable
{
    private const long CompleteAppId = 386940;
    private const long LuaOnlyAppId = 480;
    private const string TokenHeader = HttpServerService.TokenHeader;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    private readonly string _steamRoot;
    private readonly string _dest;
    private readonly SettingsService _settings;
    private readonly HttpListener _listener = new();
    private readonly HttpClient _http = new();
    private readonly string _baseUrl;

    internal string BaseUrl => _baseUrl;

    internal void SeedAndEnableBackups()
    {
        SeedBackups();
        _settings.ManifestBackupMode = ManifestBackupMode.LocalFolder;
        _settings.ManifestBackupFolder = _dest;
    }

    public HttpBackupRoutesTests()
    {
        _steamRoot = Path.Combine(_dir, "steam");
        _dest = Path.Combine(_dir, "backup");
        Directory.CreateDirectory(Path.Combine(_steamRoot, "config", "stplug-in"));
        Directory.CreateDirectory(Path.Combine(_steamRoot, "depotcache"));

        _settings = new SettingsService(Path.Combine(_dir, "settings")) { SteamPathOverride = _steamRoot };
        var cache = new CacheService(Path.Combine(_dir, "cache"));
        var steam = new SteamService(_settings);
        var vault = new LuaVault(() => steam.StPlugInDir, Path.Combine(_dir, "vault"));
        var installer = new LuaInstaller(steam, _settings, cache, vault);
        var appList = new SteamAppListCache(Path.Combine(_dir, "applist.json"), new HttpClientHandler());
        var backups = new ManifestBackupService(_settings, steam, installer, appList, new ToastService());

        var services = new ServiceCollection()
            .AddSingleton(backups)
            .BuildServiceProvider();

        var server = new HttpServerService(installer, steam, cache, services, NullLogger<HttpServerService>.Instance)
        {
            BaseDirectory = Path.Combine(_dir, "app-bin"),
        };
        Directory.CreateDirectory(server.BaseDirectory);

        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

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

    private async Task<HttpResponseMessage> Send(
        HttpMethod method, string path, bool token = false, string? body = null, string? host = null)
    {
        using var req = new HttpRequestMessage(method, _baseUrl + path);
        if (token) req.Headers.TryAddWithoutValidation(TokenHeader, HttpServerService.SessionToken);
        if (host is not null) req.Headers.Host = host;
        if (method == HttpMethod.Post) req.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
        return await _http.SendAsync(req);
    }

    private void SeedBackups()
    {
        string sourceSteam = Path.Combine(_dir, "source-steam");
        string plug = Path.Combine(sourceSteam, "config", "stplug-in");
        string depot = Path.Combine(sourceSteam, "depotcache");
        Directory.CreateDirectory(plug);
        Directory.CreateDirectory(depot);

        File.WriteAllText(Path.Combine(plug, $"{CompleteAppId}.lua"),
            $"addappid({CompleteAppId})\naddappid(386941, 1, \"abcd\")\nsetManifestid(386941, \"111\")\n");
        File.WriteAllText(Path.Combine(depot, "386941_111.manifest"), "MANIFEST_BYTES");
        File.WriteAllText(Path.Combine(plug, $"{LuaOnlyAppId}.lua"), $"addappid({LuaOnlyAppId})\n");

        ManifestBackupStore.BackupAll(plug, depot, _dest, id => id == CompleteAppId ? "Complete Game" : "Lua Only",
            null, CancellationToken.None);
    }

    [Theory]
    [InlineData("GET", "/backup/index")]
    [InlineData("GET", "/backup/status?appid=386940")]
    [InlineData("POST", "/backup/install")]
    [InlineData("GET", "/backup/install")]
    public async Task Backup_routes_forbid_requests_without_token(string method, string path)
    {
        using var res = await Send(new HttpMethod(method), path);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        res.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Backup_routes_forbid_rebound_host_even_with_valid_token()
    {
        using var res = await Send(HttpMethod.Get, "/backup/index", token: true, host: "evil.example");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("POST", "/backup/index")]
    [InlineData("POST", "/backup/status?appid=386940")]
    [InlineData("GET", "/backup/install")]
    public async Task Backup_routes_enforce_exact_http_verb(string method, string path)
    {
        using var res = await Send(new HttpMethod(method), path, token: true);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("/backup/status")]
    [InlineData("/backup/status?appid=")]
    [InlineData("/backup/status?appid=0")]
    [InlineData("/backup/status?appid=-1")]
    [InlineData("/backup/status?appid=4294967296")]
    [InlineData("/backup/status?appid=../386940")]
    [InlineData("/backup/status?appid=386940/x")]
    [InlineData("/backup/status?appid=abc")]
    [InlineData("/backup/status?appid=386940&appid=480")]
    public async Task Backup_status_rejects_invalid_or_traversal_appid(string path)
    {
        using var res = await Send(HttpMethod.Get, path, token: true);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"appid":0}""")]
    [InlineData("""{"appid":-5}""")]
    [InlineData("""{"appid":4294967296}""")]
    [InlineData("""{"appid":"../386940"}""")]
    [InlineData("""{"appid":"abc"}""")]
    [InlineData("""{"appid":386940,"appId":480}""")]
    [InlineData("{not json")]
    public async Task Backup_install_rejects_invalid_appid_payload(string body)
    {
        using var res = await Send(HttpMethod.Post, "/backup/install", token: true, body: body);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Backup_routes_report_disabled_when_feature_is_off()
    {
        using var indexRes = await Send(HttpMethod.Get, "/backup/index", token: true);
        indexRes.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await indexRes.Content.ReadAsStringAsync()))
        {
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("enabled").GetBoolean().Should().BeFalse();
            doc.RootElement.GetProperty("games").GetArrayLength().Should().Be(0);
        }

        using var statusRes = await Send(HttpMethod.Get, $"/backup/status?appid={CompleteAppId}", token: true);
        statusRes.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await statusRes.Content.ReadAsStringAsync()))
        {
            doc.RootElement.GetProperty("enabled").GetBoolean().Should().BeFalse();
            doc.RootElement.GetProperty("available").GetBoolean().Should().BeFalse();
        }

        using var installRes = await Send(HttpMethod.Post, "/backup/install", token: true, body: $"{{\"appid\":{CompleteAppId}}}");
        installRes.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Backup_index_status_and_install_work_end_to_end_and_reject_tampered_zips()
    {
        SeedBackups();
        _settings.ManifestBackupMode = ManifestBackupMode.LocalFolder;
        _settings.ManifestBackupFolder = _dest;

        // 1. Index reports both complete (hasManifests: true) and lua-only (hasManifests: false), no local path leaked.
        using (var res = await Send(HttpMethod.Get, "/backup/index", token: true))
        {
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            string raw = await res.Content.ReadAsStringAsync();
            raw.Should().NotContain(_dest);
            using var doc = JsonDocument.Parse(raw);
            doc.RootElement.GetProperty("enabled").GetBoolean().Should().BeTrue();
            var games = doc.RootElement.GetProperty("games").EnumerateArray().ToList();
            games.Should().HaveCount(2);

            var luaOnly = games.Single(g => g.GetProperty("appId").GetInt64() == LuaOnlyAppId);
            luaOnly.GetProperty("hasManifests").GetBoolean().Should().BeFalse();
            luaOnly.GetProperty("manifestCount").GetInt32().Should().Be(0);

            var complete = games.Single(g => g.GetProperty("appId").GetInt64() == CompleteAppId);
            complete.GetProperty("hasManifests").GetBoolean().Should().BeTrue();
            complete.GetProperty("manifestCount").GetInt32().Should().Be(1);
        }

        // 2. Status differentiates complete, lua-only, and absent games.
        using (var res = await Send(HttpMethod.Get, $"/backup/status?appid={CompleteAppId}", token: true))
        {
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("available").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("hasManifests").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("manifestCount").GetInt32().Should().Be(1);
        }
        using (var res = await Send(HttpMethod.Get, "/backup/status?appid=999999", token: true))
        {
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("enabled").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("available").GetBoolean().Should().BeFalse();
        }

        // 3. Valid POST /backup/install restores lua + manifest into Steam.
        using (var res = await Send(HttpMethod.Post, "/backup/install", token: true, body: $"{{\"appid\":{CompleteAppId}}}"))
        {
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("luaInstalled").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("manifestCount").GetInt32().Should().Be(1);
        }
        File.Exists(Path.Combine(_steamRoot, "config", "stplug-in", $"{CompleteAppId}.lua")).Should().BeTrue();
        File.Exists(Path.Combine(_steamRoot, "depotcache", "386941_111.manifest")).Should().BeTrue();

        // 4. Missing appid returns 404.
        using (var res = await Send(HttpMethod.Post, "/backup/install", token: true, body: "{\"appid\":999999}"))
            res.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 5. Tampered zip is refused fail-closed (422) and writes nothing to Steam.
        string luaOnlyZip = Path.Combine(_dest, LuaOnlyAppId.ToString(), $"{LuaOnlyAppId}.zip");
        File.AppendAllText(luaOnlyZip, "tamper");
        using (var res = await Send(HttpMethod.Post, "/backup/install", token: true, body: $"{{\"appid\":{LuaOnlyAppId}}}"))
        {
            res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            (await res.Content.ReadAsStringAsync()).Should().NotContain(_dest);
        }
        File.Exists(Path.Combine(_steamRoot, "config", "stplug-in", $"{LuaOnlyAppId}.lua")).Should().BeFalse();
    }
}
