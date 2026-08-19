using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using LuaToolsGui.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

public record DownloadState
{
    public string Status { get; set; } = "queued"; // queued, downloading, processing, done, error, cancelled
    public long BytesRead { get; set; }
    public long TotalBytes { get; set; }
    public string? CurrentApi { get; set; }
    public Dictionary<string, object> ApiErrors { get; set; } = new();
    public string? Error { get; set; }
    public string? InstalledPath { get; set; }
    public bool Success { get; set; }
    public string? Api { get; set; }
    public CancellationTokenSource? Cts { get; set; }

    /// <summary>When this state was created — used to evict the oldest finished entries so the tracking
    /// dictionary can't grow without bound over a long session.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>True once the download has reached a terminal state and the entry is safe to evict.</summary>
    public bool IsFinished => Status is "done" or "failed" or "error" or "cancelled";
}

public class HttpServerService : IHostedService
{
    private readonly LuaInstaller _installer;
    private readonly SteamService _steam;
    private readonly CacheService _cache;
    private readonly IServiceProvider _services;
    private readonly ILogger<HttpServerService> _log;
    private HttpListener? _listener;
    private CancellationTokenSource? _appCts;

    private readonly ConcurrentDictionary<long, DownloadState> _downloads = new();
    private List<ApiSource> _apiSources = new();
    private bool _apiSourcesLoaded = false;

    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "LuaTools", "downloads");

    // ── Access control ────────────────────────────────────────────────
    // This server exposes real side effects (install/remove a lua, launch a URL, restart Steam), so it
    // must not be drivable by any web page the user happens to have open. It used to answer every request
    // with `Access-Control-Allow-Origin: *` and no other check, which is exactly that hole: a browser
    // preflight succeeded, so any origin could POST to /open-url, /remove/{appid}, /add/{appid}, etc.
    //
    // Three layers now gate every request (see IsAuthorized):
    //   1. Host must be literal loopback — stops DNS rebinding (an attacker name resolving to 127.0.0.1
    //      arrives with its own Host header, so it no longer reaches the handlers).
    //   2. The app's OWN client (CefInjectorService, which is what actually issues these calls — the store
    //      page can't fetch localhost from an HTTPS page, that's why the CDP bridge exists) authenticates
    //      with a per-launch token instead of relying on origin at all.
    //   3. A browser request carrying an Origin is only served if that origin is an allow-listed Steam
    //      surface, and the CORS header echoes that one origin rather than "*".
    //
    // The decision itself lives in LocalApiAccessPolicy so it is unit-testable; this class only supplies
    // the transport (reading headers, writing the 403, emitting the CORS headers).

    /// <summary>Per-launch shared secret. Read by <see cref="CefInjectorService"/>, which sends it on every
    /// bridge call; never written to disk and never leaves the machine.</summary>
    public static readonly string SessionToken = Guid.NewGuid().ToString("N");

    /// <summary>Header carrying <see cref="SessionToken"/>.</summary>
    public const string TokenHeader = "X-LuaTools-Token";

    /// <summary>Cap on retained per-app download states, so a long session can't grow _downloads without
    /// bound. Finished entries are evicted oldest-first once the cap is passed.</summary>
    private const int MaxTrackedDownloads = 256;

    public HttpServerService(LuaInstaller installer, SteamService steam, CacheService cache,
        IServiceProvider services, ILogger<HttpServerService> logger)
    {
        _installer = installer;
        _steam = steam;
        _cache = cache;
        _services = services;
        _log = logger;
        Directory.CreateDirectory(TempDir);
    }

    private void LoadApiSources()
    {
        if (_apiSourcesLoaded) return;
        _apiSourcesLoaded = true;

        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "public", "api.json"),
            Path.Combine(AppContext.BaseDirectory, "api.json"),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("api_list", out var list))
                    {
                        _apiSources = new();
                        foreach (var entry in list.EnumerateArray())
                        {
                            var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            // `url` is still REQUIRED to accept an entry, so which entries load is
                            // unchanged, but it is deliberately not stored — see the ApiSource remarks.
                            var url = entry.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                            var enabled = !entry.TryGetProperty("enabled", out var en) || en.GetBoolean();
                            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url) && enabled)
                                _apiSources.Add(new ApiSource(name));
                        }
                    }
                    _log.LogInformation("Loaded {Count} API sources from api.json", _apiSources.Count);
                    return;
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Failed to parse api.json: {Message}", ex.Message);
                }
            }
        }
        _log.LogWarning("api.json not found — using fallback sources");
        _apiSources = new() { new("Ryuu"), new("Sushi") };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _appCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new HttpListener();
        _listener.Prefixes.Add("http://127.0.0.1:6767/");
        try { _listener.Start(); }
        catch (HttpListenerException)
        {
            _log.LogWarning("HttpListener could not start on 127.0.0.1:6767 — attempting netsh reservation");
            try
            {
                // Reserve for THIS user only. `user=Everyone` handed the reservation to every account on
                // the machine, including other (possibly less trusted) logins — the app only ever needs to
                // listen as the user running it.
                string account = $"{Environment.UserDomainName}\\{Environment.UserName}";
                var psi = new System.Diagnostics.ProcessStartInfo("netsh", $"http add urlacl url=http://127.0.0.1:6767/ user=\"{account}\"")
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                };
                System.Diagnostics.Process.Start(psi)?.WaitForExit(3000);
                _listener.Start();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to start HTTP server on :6767");
                return Task.CompletedTask;
            }
        }

        _log.LogInformation("HTTP server listening on http://127.0.0.1:6767");
        _ = Task.Run(() => ListenLoop(_appCts.Token), _appCts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _appCts?.Cancel();
        try { _listener?.Stop(); } catch { }
        return Task.CompletedTask;
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequest(ctx), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch { }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;

        resp.ContentType = "application/json; charset=utf-8";

        // Gate BEFORE any routing: an unauthorized caller must not be able to reach a handler, and must
        // not receive CORS headers that would let it read the response either.
        var access = Authorize(req);
        if (!access.Allowed)
        {
            resp.StatusCode = 403;
            var denied = Encoding.UTF8.GetBytes(JsonErr("Forbidden"));
            try { await resp.OutputStream.WriteAsync(denied); }
            catch (HttpListenerException) { /* client hung up before we could answer */ }
            catch (IOException) { /* same */ }
            resp.Close();
            return;
        }

        SetCors(resp, access.AllowOrigin);

        try
        {
            string? path = req.Url?.AbsolutePath.TrimEnd('/');
            // Log everything except the noisy status poll.
            if (path is not null && !path.StartsWith("/add-status/") && !path.StartsWith("/has/"))
                AppLog.Log($"HTTP {req.HttpMethod} {path}");
            (int status, string body) = path switch
            {
                // Answer CORS preflight FIRST — otherwise it matches a POST route (the
                // matchers ignore method) and returns non-2xx, so the browser blocks the
                // real request (this is why JSON POSTs like /add-source did nothing).
                _ when req.HttpMethod == "OPTIONS" => (204, ""),
                var p when MatchGet(p, "/has/{appid}", out var id) => await HandleHas(id),
                // Steam-plugin headless add: reflects the app's real DownloadViewModel.
                var p when MatchPost(p, "/add/{appid}", out var id) => await HandleAdd(id, req),
                var p when MatchGet(p, "/add-status/{appid}", out var id) => HandleAddStatus(id),
                var p when MatchPost(p, "/add-source/{appid}", out var id) => await HandleAddSource(id, req),
                var p when MatchPost(p, "/check-sources/{appid}", out var id) => await HandleCheckSources(id),
                var p when MatchPost(p, "/download/{appid}", out var id) => await HandleDownload(id, req),
                var p when MatchGet(p, "/download-status/{appid}", out var id) => HandleStatus(id),
                var p when MatchPost(p, "/cancel/{appid}", out var id) => HandleCancel(id),
                var p when MatchPost(p, "/remove/{appid}", out var id) => HandleRemove(id),
                var p when MatchPost(p, "/open/fix/{appid}", out var id) => HandleOpenFix(id),
                "/open/settings" when req.HttpMethod == "POST" => HandleOpenSettings(),
                "/open-url" when req.HttpMethod == "POST" => await HandleOpenUrl(req),
                "/restart-steam" when req.HttpMethod == "POST" => HandleRestartSteam(),
                "/check-updates" when req.HttpMethod == "POST" => await HandleCheckUpdates(),
                "/loaded-apps" when req.HttpMethod == "GET" => await HandleReadLoadedApps(),
                "/loaded-apps" when req.HttpMethod == "POST" => HandleDismissLoadedApps(),
                "/api-list" when req.HttpMethod == "GET" => HandleApiList(),
                "/icon" when req.HttpMethod == "GET" => HandleIcon(),
                // CefInjectorService's methodMap routes the plugin's "Logger" here. Without this route the
                // bridge call 404s and the frontend's diagnostics are silently dropped.
                "/log" when req.HttpMethod == "POST" => await HandleLog(req),
                _ => (404, JsonErr("Not found")),
            };

            resp.StatusCode = status;
            var bytes = Encoding.UTF8.GetBytes(body);
            await resp.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            // The exception text can carry local paths and other internals. Log it locally; tell the
            // caller only that the request failed.
            _log.LogWarning(ex, "Unhandled error serving {Method} {Path}", req.HttpMethod, req.Url?.AbsolutePath);
            resp.StatusCode = 500;
            var body = Encoding.UTF8.GetBytes(JsonErr("Internal error"));
            await resp.OutputStream.WriteAsync(body);
        }
        finally
        {
            resp.Close();
        }
    }

    /// <summary>Match a "/segment/{appid}" pattern and parse the appid. Parsing here (rather than
    /// <c>long.Parse</c> at each call site) means a non-numeric appid simply fails to match and falls
    /// through to 404, instead of throwing and surfacing as a 500.</summary>
    private static bool MatchGet(string? path, string pattern, out long id)
    {
        id = 0;
        if (path is null) return false;
        var parts = pattern.TrimEnd('/').Split('/');
        var pathParts = path.Split('/');
        if (parts.Length != pathParts.Length) return false;
        string raw = "";
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith("{")) { raw = pathParts[i]; continue; }
            if (!string.Equals(parts[i], pathParts[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return long.TryParse(raw, out id);
    }

    private static bool MatchPost(string? path, string pattern, out long id) =>
        MatchGet(path, pattern, out id);

    /// <summary>Transport adapter: pull the three headers the policy needs off the request and let
    /// <see cref="LocalApiAccessPolicy"/> decide. The decision itself lives there so it can be unit-tested
    /// (<see cref="HttpListenerRequest"/> is sealed and cannot be constructed by a test).</summary>
    private static AccessDecision Authorize(HttpListenerRequest req) =>
        LocalApiAccessPolicy.Evaluate(
            req.Headers["Host"], req.Headers[TokenHeader], req.Headers["Origin"], SessionToken);

    // ── Endpoint handlers ─────────────────────────────────────────────

    private Task<(int, string)> HandleHas(long appId)
    {
        var exists = _installer.ReadInstalledLua(appId) != null;
        return Task.FromResult((200, Json(new { success = true, exists })));
    }

    // ── Steam-plugin add: drive + reflect the real DownloadViewModel ──

    /// <summary>Trigger the fully headless add (PluginAddService — dynamic sources, Hubcap, key-gating,
    /// usage, FastFetch auto-download). Uses services only; the app window is never touched.</summary>
    private async Task<(int, string)> HandleAdd(long appId, HttpListenerRequest req)
    {
        // The store page passes the game name it already displays, so PluginAddService can skip a
        // lua.tools /details lookup. Best-effort: a missing/blank name just falls back to a fetch.
        string? name = null;
        try
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var json = JsonSerializer.Deserialize<JsonElement>(await reader.ReadToEndAsync());
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("name", out var n))
                name = n.GetString();
        }
        catch { }
        _services.GetRequiredService<PluginAddService>().Start(appId, name);
        return (200, Json(new { success = true }));
    }

    /// <summary>Serialize the headless add state so the plugin popup mirrors what the app would show.</summary>
    private (int, string) HandleAddStatus(long appId)
    {
        var svc = _services.GetRequiredService<PluginAddService>();
        var st = svc.GetState(appId);
        bool installed = _installer.ReadInstalledLua(appId) != null;
        if (st is null)
            return (200, Json(new { success = true, checking = false, sourcesLoaded = false, sources = Array.Empty<object>(), installed }));

        var sources = st.Sources.Select(s => (object)new
        {
            name = s.Name,
            displayName = s.DisplayName,
            status = s.Status,
            available = s.Available,
            canDownload = s.CanDownload,
            locked = s.Locked,
            needsKey = s.NeedsKey,
            stats = s.Stats,
            downloading = s.Downloading,
            progress = s.Progress,
            indeterminate = s.Indeterminate,
        }).ToList();

        return (200, Json(new
        {
            success = true,
            appid = st.AppId,
            checking = st.Checking,
            fastFetch = st.FastFetch,
            sourcesLoaded = st.SourcesLoaded,
            sources,
            installStatus = st.InstallStatus,
            installFailed = st.InstallFailed,
            error = st.Error,
            installed,
        }));
    }

    /// <summary>Plugin picked a source by name (FastFetch-off path) → download+install it headlessly.</summary>
    private async Task<(int, string)> HandleAddSource(long appId, HttpListenerRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
            body = await reader.ReadToEndAsync();
        string source = "";
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("source", out var s))
                source = s.GetString() ?? "";
        }
        catch { }
        AppLog.Log($"/add-source/{appId} body='{body}' parsed source='{source}'");
        if (string.IsNullOrWhiteSpace(source)) return (400, JsonErr("source is required"));

        _services.GetRequiredService<PluginAddService>().Pick(appId, source);
        return (200, Json(new { success = true }));
    }

    private async Task<(int, string)> HandleCheckSources(long appId)
    {
        // Dynamic source list from the app's real manifest backend (same call the app's
        // DownloadViewModel uses). Sources have no per-source URL — downloads go through
        // the app's authenticated proxy by source NAME (see HandleDownload).
        try
        {
            var api = _services.GetRequiredService<LuaToolsApiClient>();
            var statuses = await api.CheckSourcesAsync(appId.ToString());
            var results = statuses
                .Select(kv => (object)new { name = kv.Key, available = kv.Value == "available", url = (string?)null })
                .ToList();
            return (200, Json(new { success = true, results }));
        }
        catch (Exception ex)
        {
            return (200, Json(new { success = false, error = ex.Message, results = Array.Empty<object>() }));
        }
    }

    private async Task<(int, string)> HandleDownload(long appId, HttpListenerRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
            body = await reader.ReadToEndAsync();

        var json = JsonSerializer.Deserialize<JsonElement>(body);
        // Download is by source NAME (the app's authenticated proxy resolves it). Accept
        // "source" or legacy "apiName".
        string source = json.TryGetProperty("source", out var s) ? s.GetString() ?? ""
            : json.TryGetProperty("apiName", out var a) ? a.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(source))
            return (400, JsonErr("source is required"));

        if (_downloads.TryGetValue(appId, out var existing) && existing.Status is "downloading" or "processing")
            return (409, JsonErr("Download already in progress for this app"));

        var cts = new CancellationTokenSource();
        var state = new DownloadState
        {
            Status = "queued",
            CurrentApi = source,
            Cts = cts,
        };
        _downloads[appId] = state;
        EvictFinishedDownloads();

        _ = DownloadAndInstallAsync(appId, source, cts.Token);

        return (200, Json(new { success = true }));
    }

    /// <summary>Drop the oldest FINISHED download states once the tracking dictionary passes its cap. States
    /// were previously kept forever, so a long-running session accumulated one entry per app touched.
    /// In-flight downloads are never evicted — the plugin is still polling those.</summary>
    private void EvictFinishedDownloads()
    {
        if (_downloads.Count <= MaxTrackedDownloads) return;
        foreach (var kv in _downloads
                     .Where(kv => kv.Value.IsFinished)
                     .OrderBy(kv => kv.Value.CreatedAt)
                     .Take(_downloads.Count - MaxTrackedDownloads)
                     .ToList())
            _downloads.TryRemove(kv.Key, out _);
    }

    private (int, string) HandleStatus(long appId)
    {
        if (!_downloads.TryGetValue(appId, out var state))
            return (200, Json(new { success = true, state = (object?)null }));

        var payload = new
        {
            status = state.Status,
            bytesRead = state.BytesRead,
            totalBytes = state.TotalBytes,
            currentApi = state.CurrentApi,
            apiErrors = state.ApiErrors.Count > 0 ? state.ApiErrors : null,
            error = state.Error,
            installedPath = state.InstalledPath,
            success = state.Success,
            api = state.Api,
        };
        return (200, Json(new { success = true, state = payload }));
    }

    private (int, string) HandleCancel(long appId)
    {
        if (_downloads.TryGetValue(appId, out var state) && state.Status is "queued" or "downloading" or "processing")
        {
            state.Cts?.Cancel();
            state.Status = "cancelled";
            state.Error = "Cancelled by user";
            _downloads[appId] = state;
            return (200, Json(new { success = true }));
        }
        return (200, Json(new { success = true, message = "Nothing to cancel" }));
    }

    private (int, string) HandleRemove(long appId)
    {
        try
        {
            _cache.RemoveLoadedAppId(appId); // also drop it from the "recently added" popup list
            var path = _installer.ReadInstalledLua(appId);
            if (path is not null)
            {
                File.Delete(path);
                var disabled = Path.Combine(Path.GetDirectoryName(path)!, $"{appId}.lua.disabled");
                if (File.Exists(disabled)) File.Delete(disabled);
                return (200, Json(new { success = true, deleted = new[] { path }, count = 1 }));
            }
            return (200, Json(new { success = true, deleted = Array.Empty<string>(), count = 0 }));
        }
        catch (Exception ex)
        {
            return (500, JsonErr(ex.Message));
        }
    }

    // ── App-owned actions (surface the LuaTools GUI window; it does the real work) ──

    /// <summary>Open the Fixes page for a game (same as the fix:// protocol).</summary>
    private (int, string) HandleOpenFix(long appId)
    {
        return OnUiThread(() =>
        {
            var window = _services.GetRequiredService<MainWindow>();
            var fixes = _services.GetRequiredService<FixesViewModel>();
            window.RestoreFromTray();
            window.NavigateToFixes();
            _ = fixes.OpenForAppIdAsync(appId);
        });
    }

    /// <summary>Surface the app's own Settings page (replaces the plugin's settings panel).</summary>
    private (int, string) HandleOpenSettings()
    {
        return OnUiThread(() =>
        {
            var window = _services.GetRequiredService<MainWindow>();
            window.RestoreFromTray();
            window.NavigateToSettings();
        });
    }

    private (int, string) HandleRestartSteam()
    {
        var ok = _steam.RestartSteam();
        return (200, Json(ok
            ? new { success = true, error = (string?)null }
            : new { success = false, error = (string?)"Failed to restart Steam" }));
    }

    private async Task<(int, string)> HandleOpenUrl(HttpListenerRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
            body = await reader.ReadToEndAsync();

        string url = "";
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("url", out var u))
                url = u.GetString() ?? "";
        }
        catch { /* fall through to validation */ }

        // This hands a string to ShellExecute, so validate it as a real absolute http/https URI rather
        // than prefix-matching the text. A prefix check alone accepts things that are not the URL they
        // look like; parsing and then re-emitting Uri.AbsoluteUri means only a well-formed web address
        // ever reaches the shell.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return (400, JsonErr("Invalid URL"));

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
            return (200, Json(new { success = true }));
        }
        catch (Exception ex)
        {
            return (500, JsonErr(ex.Message));
        }
    }

    private Task<(int, string)> HandleCheckUpdates()
    {
        try
        {
            // Frontend "Check for updates" → run the exact same update flow as Steam-open (app + plugin,
            // with the sync app-restart), so the button can't leave the backend out of sync with a freshly
            // updated plugin. Fire-and-forget: the flow may restart Steam and/or the app, so don't block the
            // HTTP response on it. Fall back to the plain checks if the app flow isn't wired yet.
            if (App.RunUpdateFlow is { } flow)
                _ = flow();
            else
            {
                _ = _services.GetRequiredService<UpdateService>().CheckAndStageAsync();
                _ = _services.GetRequiredService<PluginInstallerService>().AutoUpdateAsync();
            }
            return Task.FromResult((200, Json(new { success = true })));
        }
        catch (Exception ex)
        {
            return Task.FromResult((200, Json(new { success = false, error = ex.Message })));
        }
    }

    private async Task<(int, string)> HandleReadLoadedApps()
    {
        var ids = _cache.GetLoadedAppIds();
        // Resolve appid → game name so the plugin's "Added Games" popup shows names, not just numbers
        // (it renders item.name || item.appid). Names are best-effort — a missing one falls back to the id.
        var names = _services.GetRequiredService<SteamAppListCache>();
        try { await names.EnsureLoadedAsync(); } catch { /* offline / not cached yet → ids only */ }
        var apps = ids.Select(id => new { appid = id, name = names.GetName(id) }).ToList();
        return (200, Json(new { success = true, apps }));
    }

    private (int, string) HandleDismissLoadedApps()
    {
        _cache.ClearLoadedAppIds();
        return (200, Json(new { success = true }));
    }

    /// <summary>Diagnostics sink for the store-page frontend (its "Logger" bridge method). Writes into the
    /// same AppLog the rest of the bridge uses. Truncated so a runaway logger can't fill the file.</summary>
    private static async Task<(int, string)> HandleLog(HttpListenerRequest req)
    {
        string body;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
            body = await reader.ReadToEndAsync();

        string message = body;
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("message", out var m))
                message = m.GetString() ?? body;
        }
        catch { /* not JSON — log the raw body */ }

        if (message.Length > 2000) message = message[..2000] + "…";
        AppLog.Log("[plugin] " + message);
        return (200, Json(new { success = true }));
    }

    /// <summary>Marshal a fire-and-forget UI action onto the WPF dispatcher and ack immediately.</summary>
    private (int, string) OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return (500, JsonErr("App not ready"));
        dispatcher.InvokeAsync(() =>
        {
            try { action(); }
            catch (Exception ex) { _log.LogWarning("UI action failed: {Message}", ex.Message); }
        });
        return (200, Json(new { success = true }));
    }

    private (int, string) HandleApiList()
    {
        LoadApiSources();
        var apis = _apiSources.Select((s, i) => new { name = s.Name, index = i }).ToList();
        return (200, Json(new { success = true, apis }));
    }

    private (int, string) HandleIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "luatools-icon.png");
            if (!File.Exists(iconPath))
            {
                var alt = Path.Combine(AppContext.BaseDirectory, "icon.ico");
                if (File.Exists(alt))
                    iconPath = alt;
                else
                    return (200, Json(new { success = false, dataUrl = "" }));
            }
            var bytes = File.ReadAllBytes(iconPath);
            var b64 = Convert.ToBase64String(bytes);
            var mime = iconPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/x-icon";
            return (200, Json(new { success = true, dataUrl = $"data:{mime};base64,{b64}" }));
        }
        catch
        {
            return (200, Json(new { success = false, dataUrl = "" }));
        }
    }

    // ── Download worker ───────────────────────────────────────────────

    private async Task DownloadAndInstallAsync(long appId, string source, CancellationToken ct)
    {
        var state = _downloads[appId];
        try
        {
            state.Status = "downloading";
            state.BytesRead = 0;
            state.TotalBytes = 100; // progress reported as a 0..100 percentage

            var api = _services.GetRequiredService<LuaToolsApiClient>();
            var progress = new Progress<double?>(p =>
            {
                if (p is not null)
                {
                    state.TotalBytes = 100;
                    state.BytesRead = (long)(p.Value * 100);
                }
            });

            // Download through the app's authenticated lua.tools proxy BY SOURCE NAME
            // (same path as DownloadViewModel.DownloadFromSourceAsync). Works for every
            // dynamic source, not just ones with a public URL.
            var download = await api.DownloadManifestAsync(appId.ToString(), source, null, progress, ct);

            state.Status = "processing";
            var result = _installer.InstallZip(download.FilePath, appId);
            try { if (File.Exists(download.FilePath)) File.Delete(download.FilePath); } catch { }

            if (result.Error is not null)
            {
                state.Status = "failed"; // frontend startPolling shows failure UI on "failed"
                state.Error = result.Error;
                return;
            }

            state.Status = "done";
            state.Success = true;
            state.Api = source;
        }
        catch (OperationCanceledException)
        {
            state.Status = "cancelled";
            state.Error = "Cancelled by user";
        }
        catch (Exception ex)
        {
            state.Status = "failed"; // frontend startPolling shows failure UI on "failed"
            state.Error = ex.Message;
        }
        finally
        {
            state.Cts?.Dispose();
            state.Cts = null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>Emit CORS headers only for an allow-listed browser origin, echoing that single origin.
    /// A non-browser caller (<paramref name="allowOrigin"/> null) gets no CORS headers at all — it doesn't
    /// need them, and a wildcard here is what previously made this server reachable from any web page.</summary>
    private static void SetCors(HttpListenerResponse resp, string? allowOrigin)
    {
        if (allowOrigin is null) return;
        resp.AddHeader("Access-Control-Allow-Origin", allowOrigin);
        resp.AddHeader("Vary", "Origin");
        resp.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        resp.AddHeader("Access-Control-Allow-Headers", "Content-Type, " + TokenHeader);
    }

    private static string Json(object obj) => JsonSerializer.Serialize(obj);
    private static string JsonErr(string msg) => JsonSerializer.Serialize(new { success = false, error = msg });
}

/// <summary>
/// A manifest source, identified by NAME only.
/// <para>
/// This record used to carry <c>Url</c> and <c>SuccessCode</c> as well, and the built-in fallback list
/// hardcoded <c>http://167.235.229.108/&lt;appid&gt;</c> for the "Ryuu" source — plain HTTP to a bare IP,
/// for a payload that gets installed. Both fields were WRITE-ONLY: nothing ever read them.
/// <c>HandleApiList</c> projects just <c>{ name, index }</c> for the store-page plugin, and the actual
/// download runs through <see cref="LuaToolsApiClient.DownloadManifestAsync"/>, which resolves the source
/// BY NAME against lua.tools and fetches a signed HTTPS URL.
/// </para>
/// <para>
/// So the HTTP URL never produced a request — it was dead data that merely looked like an active
/// cleartext download path, and would have become one the moment somebody wired it up. Deleting the
/// fields removes the hazard outright, which is strictly better than gating a fetch that does not happen.
/// If direct-URL downloading is ever genuinely wanted, add it back deliberately and HTTPS-only, with the
/// same trust checks <see cref="GithubProxy.IsTrustedDownloadUrl"/> applies to release assets.
/// </para>
/// </summary>
internal record ApiSource(string Name);
