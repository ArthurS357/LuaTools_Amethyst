using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LuaToolsGui.Services;

public class CefInjectorService : IHostedService
{
    private readonly SteamService _steam;
    private readonly ILogger<CefInjectorService> _log;
    private CancellationTokenSource? _cts;
    private string _luatoolsJs = "";
    private string _polyfillJs = "";

    // The script injected into a tab: polyfill + luatools.js. Built once per content change instead of on
    // every ~1 s discovery tick (it was concatenated there whether or not any tab needed it — two copies of
    // a plugin-sized string per second for the life of the app). Read and written only by InjectionLoop.
    private (string Polyfill, string Lua, string Script)? _composed;

    private string ComposedScript()
    {
        // One read of each: ReloadPluginFilesAsync may swap either field at any moment.
        string polyfill = _polyfillJs, lua = _luatoolsJs;
        if (_composed is { } c && ReferenceEquals(c.Polyfill, polyfill) && ReferenceEquals(c.Lua, lua))
            return c.Script;
        string script = polyfill + "\n" + lua;
        _composed = (polyfill, lua, script);
        return script;
    }
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Persistent CDP WebSocket per tab (keyed by tab id), reused across calls so the fast RPC-drain
    // loop doesn't pay a connect handshake every tick. Opened lazily, evicted (and reopened next call)
    // on any socket error. Touched only from the single InjectionLoop task (+ StopAsync on shutdown),
    // so no locking is needed. _cdpId hands out unique CDP command ids for response correlation.
    private readonly Dictionary<string, ClientWebSocket> _sockets = new();
    private int _cdpId;

    // Per tab id, the calls between pickup and delivery. Same single-task rule as _sockets. Kept across
    // socket evictions: forgetting an in-flight call would run it a second time.
    private readonly Dictionary<string, TabBridge> _bridges = new();

    // CDP is opened by the `.cef-enable-remote-debugging` junction PluginInstallerService manages, not by
    // the loader DLL anymore — and Steam's own internal logic hardcodes this port. Confirmed this can't be
    // changed (file content has no effect on it), so unlike the old hook-based design (which deliberately
    // picked the uncommon 42067 to dodge collisions), this is stuck with 8080 — a commonly-occupied port
    // (dev servers, Docker, etc.). PluginInstallerService.IsPort8080BusyAsync() surfaces a best-effort
    // warning for that case (probing /json to rule out Steam's own CDP server before warning — a bare bind
    // test can't tell "someone else has it" from "Steam has it and it's working correctly"); there is no
    // fallback port to switch to.
    private const string CefDebugUrl = "http://127.0.0.1:8080/json";
    /// <summary>HttpServerService's loopback address. Settable only so the suite can pair the bridge with a
    /// real server on a free port.</summary>
    internal string BaseUrl { get; init; } = "http://127.0.0.1:6767";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public CefInjectorService(SteamService steam, ILogger<CefInjectorService> logger)
    {
        _steam = steam;
        _log = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await ReloadPluginFilesAsync();

        _ = Task.Run(() => InjectionLoop(_cts.Token), _cts.Token);
        _log.LogInformation("CEF injector started");
    }

    /// <summary>(Re)reads luatools.js + the polyfill from disk into memory. Called once at startup, and
    /// again by PluginInstallerService right after install/uninstall — the file-finding paths get checked
    /// exactly once otherwise, so a plugin installed/updated/removed after launch would silently never take
    /// effect until the app was restarted (the app is commonly launched by the loader BEFORE anything is
    /// installed, so this isn't just a theoretical edge case). _luatoolsJs/_polyfillJs are plain strings —
    /// reassignment is atomic, so this is safe to call while InjectionLoop is concurrently reading them; the
    /// next poll cycle (~1s) just picks up the new content, no page reload needed.</summary>
    public async Task ReloadPluginFilesAsync()
    {
        var ct = _cts?.Token ?? CancellationToken.None;

        var jsPath = FindLuaToolsJs();
        if (jsPath is not null && File.Exists(jsPath))
        {
            _luatoolsJs = await File.ReadAllTextAsync(jsPath, ct);
            _log.LogInformation("Loaded luatools.js ({Length} bytes)", _luatoolsJs.Length);
        }
        else
        {
            _luatoolsJs = "";
            _log.LogWarning("luatools.js not found");
        }

        var polyfillPath = FindPolyfillJs();
        if (polyfillPath is not null && File.Exists(polyfillPath))
        {
            _polyfillJs = await File.ReadAllTextAsync(polyfillPath, ct);
        }
        else
        {
            _polyfillJs = BuildInlinePolyfill();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        foreach (var ws in _sockets.Values)
            try { ws.Dispose(); } catch { /* best effort on shutdown */ }
        _sockets.Clear();
        return Task.CompletedTask;
    }

    // Loop cadence: tick fast (drain queued RPCs every tick so the page's calls resolve in ~150ms rather
    // than the old ~1s), but only refresh the tab list + re-inject on every Nth tick (~1s) — injection
    // doesn't need to be fast, and the liveness/re-inject logic is unchanged, just decoupled from the drain.
    private const int TickMs = 150;
    private const int InjectEveryTicks = 7; // 7 * 150ms ≈ 1s

    /// <summary>Discovery failures tolerated before remembered store tabs are treated as gone (2 × 1 s stays well
    /// inside the polyfill's ~5 s timeout for a transient hiccup, then lets a closed Steam back off to 5 s).</summary>
    internal const int StaleTabGraceFailures = 2;

    internal static bool ShouldDropRememberedTabs(int consecutiveFailures) =>
        consecutiveFailures > StaleTabGraceFailures;

    /// <summary>
    /// How long the loop sleeps before its next pass. Only a live store tab earns the fast 150 ms drain
    /// tick; with none, the loop waits straight for the next ~1 s discovery instead of waking 7 times for
    /// nothing. A failed pass (Steam closed, CDP port down) backs off 1 s → 3 s → 5 s: with Steam closed the
    /// old flat 1 s retry threw a connection-refused HttpRequestException ~3,600 times an hour. While a store
    /// tab is still known the retry stays at 1 s: its pending calls must be drained before the polyfill's
    /// ~5 s timeout answers them with a fake success.
    /// </summary>
    internal static int NextDelayMs(int consecutiveFailures, bool hasStoreTabs) => (consecutiveFailures, hasStoreTabs) switch
    {
        (0, true) => TickMs,
        (0, false) => TickMs * InjectEveryTicks,
        (1, _) or (_, true) => 1000,
        (2, false) => 3000,
        _ => 5000,
    };

    private async Task InjectionLoop(CancellationToken ct)
    {
        // The store tabs to drain each fast tick (id + ws url), refreshed on the ~1s injection cadence.
        List<(string Id, string Ws)> storeTabs = new();
        int tick = 0;
        int failures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // ── Slow cadence (~1s): discover store tabs, ensure luatools.js is injected ──
                if (tick % InjectEveryTicks == 0)
                {
                    // Millennium may also be present and running its own plugin loader on this same page —
                    // that's fine now. The polyfill (BuildInlinePolyfill) only takes over "luatools" calls and
                    // passes everything else through to Millennium's real window.Millennium if one exists, so
                    // the two no longer need to be mutually exclusive.
                    var tabsJson = await _http.GetStringAsync(CefDebugUrl, ct);
                    failures = 0;
                    if (!string.IsNullOrWhiteSpace(tabsJson))
                    {
                        var tabs = JsonSerializer.Deserialize<List<CefTabInfo>>(tabsJson, JsonOpts) ?? new();

                        // Inject luatools.js into every store-page tab whose JS context isn't already alive.
                        // Steam reuses the same CDP tab ID across SPA-style navigation (Home <-> store pages),
                        // but navigating to a different page wipes the JS context entirely — so "already
                        // injected by tab ID" is the wrong signal. Check liveness in the CURRENT context every
                        // cycle instead (window.__LuaToolsReady, set as the last statement of luatools.js's
                        // main IIFE) and re-inject whenever it's gone.
                        var script = ComposedScript();
                        var live = new List<(string, string)>();
                        var seen = new HashSet<string>();
                        foreach (var tab in tabs)
                        {
                            // Inject into ALL store pages, not just /app/ game pages: the store-wide header
                            // icon (and the "games added since last restart" popup, which only fires on the
                            // store home) need luatools.js present on the home page too. Steam boots straight
                            // to the home page, which has no "/app/" in its URL, so the old "/app/"-only filter
                            // meant the icon/popup never appeared there. luatools.js itself gates the per-game
                            // "Add via LuaTools" button to /app/ pages internally, so injecting store-wide is safe.
                            if (IsSteamStoreUrl(tab.Url)
                                && !string.IsNullOrEmpty(tab.WebSocketDebuggerUrl)
                                && !string.IsNullOrEmpty(tab.Id))
                            {
                                seen.Add(tab.Id!);
                                // EvaluateReturnAsync's value.GetString() throws (silently, falling through to
                                // the raw response text) for a JSON boolean, so stringify explicitly rather
                                // than returning a bare boolean expression.
                                var alive = await EvaluateReturnAsync(tab.Id!, tab.WebSocketDebuggerUrl!, "String(window.__LuaToolsReady === true)", ct);
                                if (alive != "true")
                                    await EvaluateAsync(tab.Id!, tab.WebSocketDebuggerUrl!, script, ct);

                                live.Add((tab.Id!, tab.WebSocketDebuggerUrl!));
                            }
                        }

                        storeTabs = live;
                        foreach (var gone in _bridges.Keys.Where(k => !tabs.Any(t => t.Id == k)).ToList())
                            _bridges.Remove(gone);
                        // Drop persistent sockets for tabs that have gone away.
                        foreach (var stale in _sockets.Keys.Where(k => !seen.Contains(k)).ToList())
                            EvictSocket(stale);
                    }
                }

                // ── Fast cadence (every tick): drain pending CDP bridge requests ──
                foreach (var (id, ws) in storeTabs)
                    await ProcessSingleTab(id, ws, ct);

                // No tab to drain: rewind so the next pass is a discovery pass, after the longer sleep.
                tick = storeTabs.Count == 0 ? 0 : tick + 1;
                await Task.Delay(NextDelayMs(0, storeTabs.Count > 0), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                failures++;
                if (ShouldDropRememberedTabs(failures))
                {
                    storeTabs.Clear();
                    foreach (var stale in _sockets.Keys.ToList())
                        EvictSocket(stale);
                }
                _log.LogDebug("CEF cycle: {Message}", ex.Message);
                // The failure may have been the discovery fetch itself: force a fresh one next pass.
                tick = 0;
                try { await Task.Delay(NextDelayMs(failures, storeTabs.Count > 0), ct); } catch { break; }
            }
        }
    }

    /// <summary>
    /// Whether a tab is the Steam store, by parsed https host. The injected script gets the authenticated
    /// local bridge (<see cref="HttpServerService.TokenHeader"/> is added on the backend call below), so a
    /// substring match — <c>https://evil.example/?store.steampowered.com</c>,
    /// <c>https://store.steampowered.com.evil.example/</c> — would hand that bridge to any page.
    /// </summary>
    internal static bool IsSteamStoreUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("store.steampowered.com", StringComparison.OrdinalIgnoreCase);

    private Task ProcessSingleTab(string tabId, string wsUrl, CancellationToken ct)
    {
        if (!_bridges.TryGetValue(tabId, out var bridge)) _bridges[tabId] = bridge = new();
        return DrainPendingAsync(e => EvaluateReturnAsync(tabId, wsUrl, e, ct),
            (m, a) => CallBackendMethod(m, a, ct), bridge);
    }

    /// <summary>One drain pass over a tab's <c>_pending</c>: start the backend call for each new request, then
    /// hand the page whatever has finished. It never waits for the backend — a slow call (a restore) would
    /// outlive every other call's polyfill timeout — and every <paramref name="evaluate"/> stays on the
    /// caller, so CDP frames are never sent concurrently. Static, with the page and the backend passed in, so
    /// the suite can run it against the real polyfill without a CDP socket.</summary>
    internal static async Task DrainPendingAsync(Func<string, Task<string>> evaluate, Func<string, string, Task<string>> callBackend, TabBridge bridge)
    {
        try
        {
            var pendingJson = await evaluate(
                "JSON.stringify(Object.keys(window.Millennium._pending||{}).map(function(k){var r=window.Millennium._pending[k];return{id:k,m:r.method,a:JSON.stringify(r.args),t:r.ts||0}}))");

            if (!string.IsNullOrWhiteSpace(pendingJson))
                foreach (var req in JsonSerializer.Deserialize<List<PendingRequest>>(pendingJson, JsonOpts) ?? [])
                    if (bridge.InFlight.Add((req.id ?? "", req.t)))
                        _ = RunCallAsync(req, callBackend, bridge.Done); // never faults

            var done = bridge.Done.ToArray();
            if (done.Length == 0) return;

            // Paired by id, not by position, and by ts: a reloaded page counts ids from 1 again, and an answer
            // to the previous page must not land on the new call. An answer nobody is waiting for (timed out,
            // page gone) is dropped. Returns (and clears) the polyfill's timeout record.
            var respArray = JsonSerializer.Serialize(done);
            var timedOut = await evaluate(
                "(function(r){var M=window.Millennium;for(var i=0;i<r.length;i++){var id=r[i].id,p=M._pending[id];if(p&&(p.ts||0)===r[i].t){M._readyResponses[id]=r[i];delete M._pending[id];}}return JSON.stringify((M._timedOut||[]).splice(0));})(" + respArray + ");");

            // Throws when the page did not run the script (socket error, context gone): the answers then stay
            // queued for the next tick, and stay in flight so the backend is not called again.
            var expired = JsonSerializer.Deserialize<List<PendingRequest>>(timedOut, JsonOpts) ?? [];
            foreach (var r in done)
            {
                bridge.Done.TryDequeue(out _); // only this method dequeues, so these are the ones just sent
                bridge.InFlight.Remove((r.id, r.t));
            }

            // The page was told {success:true} for these; whatever the backend did never reached luatools.js.
            foreach (var t in expired)
                AppLog.Log($"CEF bridge timeout: {Esc(t.id)} ({Esc(t.m)}) resolved as success by the page polyfill timeout");
        }
        catch (Exception ex) { AppLog.Log($"CEF bridge: drain pass failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static async Task RunCallAsync(PendingRequest req, Func<string, string, Task<string>> callBackend, ConcurrentQueue<BridgeResponse> done)
    {
        var id = req.id ?? "";
        try
        {
            var result = await callBackend(req.m ?? "", req.a ?? "{}");
            done.Enqueue(new(id, req.t, v: JsonSerializer.Deserialize<object>(result) ?? result));
        }
        catch (Exception ex) { done.Enqueue(new(id, req.t, e: ex.Message)); }
    }

    internal async Task<string> CallBackendMethod(string method, string argsRaw, CancellationToken ct = default)
    {
        var methodMap = new Dictionary<string, (string HttpMethod, string Path)>
        {
            ["HasLuaToolsForApp"] = ("GET", "/has/{appid}"),
            ["CheckApisForApp"] = ("POST", "/check-sources/{appid}"),
            ["StartAddViaLuaToolsFromUrl"] = ("POST", "/download/{appid}"),
            ["GetAddViaLuaToolsStatus"] = ("GET", "/download-status/{appid}"),
            ["CancelAddViaLuaTools"] = ("POST", "/cancel/{appid}"),
            ["DeleteLuaToolsForApp"] = ("POST", "/remove/{appid}"),
            // Store-page popup's self-contained add pipeline (PluginAddService-backed).
            // Raw fetch() to these from the page context is blocked as mixed content
            // (HTTPS store page -> HTTP localhost); route through this CDP bridge instead.
            ["StartLuaToolsAdd"] = ("POST", "/add/{appid}"),
            ["GetLuaToolsAddStatus"] = ("GET", "/add-status/{appid}"),
            ["PickLuaToolsAddSource"] = ("POST", "/add-source/{appid}"),
            // Menu actions (Settings, Fixes, Restart Steam) — same mixed-content problem, same fix.
            ["OpenSettings"] = ("POST", "/open/settings"),
            ["OpenFix"] = ("POST", "/open/fix/{appid}"),
            ["RestartSteam"] = ("POST", "/restart-steam"),
            // Open an external URL (Discord link, etc.) and check-for-updates — also route
            // through the bridge (previously used a dead direct-fetch to a proxy port).
            ["OpenExternalUrl"] = ("POST", "/open-url"),
            ["CheckForUpdatesNow"] = ("POST", "/check-updates"),
            // "Games added since last Steam restart" popup: read the list, then dismiss it.
            ["ReadLoadedApps"] = ("GET", "/loaded-apps"),
            ["DismissLoadedApps"] = ("POST", "/loaded-apps"),
            ["GetApiList"] = ("GET", "/api-list"),
            ["GetIconDataUrl"] = ("GET", "/icon"),
            // Local manifest backup: discovery + explicit restore.
            ["GetBackupIndex"] = ("GET", "/backup/index"),
            ["GetBackupStatus"] = ("GET", "/backup/status?appid={appid}"),
            ["InstallFromBackup"] = ("POST", "/backup/install"),
            // NOTE: "GetGamesDatabase" used to be mapped to "/games-database", a route HttpServerService
            // has never served. A mapped-but-unrouted name is worse than an unmapped one: it returns a 404
            // error body the frontend then has to parse, instead of the documented silent no-op. Removed
            // until (and unless) that route actually exists.
            ["Logger"] = ("POST", "/log"),
        };

        if (!methodMap.TryGetValue(method, out var mapping))
        {
            AppLog.Log($"unmapped plugin method: {Esc(method)}");
            return """{"success":true}""";
        }

        var path = mapping.Path;
        if (argsRaw is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsRaw);
                var root = doc.RootElement;
                foreach (var prop in root.EnumerateObject())
                {
                    path = path.Replace($"{{{prop.Name}}}", Uri.EscapeDataString(prop.Value.ToString()));
                }
            }
            catch (Exception ex) { AppLog.Log($"CEF bridge: could not substitute path parameters: {ex.GetType().Name}: {ex.Message}"); }
        }

        var url = BaseUrl + path;

        // The backend now authorizes callers (it used to answer any origin with a wildcard CORS header).
        // This bridge is the app's own in-process client, so it authenticates with the per-launch session
        // token rather than an origin. Set per-request, not as a default header — _http also talks to
        // Steam's CDP endpoint, which has no business seeing this token.
        using var request = new HttpRequestMessage(
            mapping.HttpMethod == "GET" ? HttpMethod.Get : HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation(HttpServerService.TokenHeader, HttpServerService.SessionToken);
        if (mapping.HttpMethod != "GET")
            request.Content = new StringContent(argsRaw ?? "{}", Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(request, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Names and ids come from the page: one log line each, like HttpServerService.HandleLog.</summary>
    private static string Esc(string? s) => (s ?? "").Replace("\r", "\\r").Replace("\n", "\\n");

    /// <summary>Return the reused (or freshly opened) CDP socket for a tab. Throws if the connect fails —
    /// the caller evicts on any exception.</summary>
    private async Task<ClientWebSocket> GetSocketAsync(string tabId, string wsUrl, CancellationToken ct)
    {
        if (_sockets.TryGetValue(tabId, out var existing))
        {
            if (existing.State == WebSocketState.Open) return existing;
            EvictSocket(tabId); // stale (closed/aborted) — drop and reopen below
        }

        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(wsUrl), ct);
        _sockets[tabId] = ws;
        return ws;
    }

    private void EvictSocket(string tabId)
    {
        if (_sockets.Remove(tabId, out var ws))
            try { ws.Dispose(); } catch { /* best effort */ }
    }

    /// <summary>Send a Runtime.evaluate over the tab's persistent socket and return the evaluated string
    /// value (or the raw response text if there's no plain value). Uses a unique CDP id per call and reads
    /// frames until that id comes back, skipping any interleaved event frames. On any socket error the
    /// connection is evicted so the next call transparently reopens a fresh one (same net behavior as the
    /// old open-per-call code, minus the handshake cost on the happy path).</summary>
    private async Task<string> EvaluateReturnAsync(string tabId, string wsUrl, string expression, CancellationToken ct)
    {
        try
        {
            var ws = await GetSocketAsync(tabId, wsUrl, ct);
            int id = ++_cdpId;
            var cmd = new { id, method = "Runtime.evaluate", @params = new { expression, returnByValue = true } };
            var json = JsonSerializer.Serialize(cmd);
            await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, ct);

            var buffer = new byte[65536];
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            // Each CDP message is one WS message (possibly multi-frame). Read whole messages until we get
            // the one carrying our id. We never enable any CDP domain, so in practice the only traffic is
            // our own responses — the id check is just defensive against any stray event frames.
            while (!linked.Token.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token);
                    if (result.MessageType == WebSocketMessageType.Close) { EvictSocket(tabId); return ""; }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                var responseText = sb.ToString();
                JsonElement root;
                try { using var doc = JsonDocument.Parse(responseText); root = doc.RootElement.Clone(); }
                catch { continue; } // unparseable frame — keep reading

                if (!(root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var rid) && rid == id))
                    continue; // an event or a different id — not our response yet

                if (root.TryGetProperty("result", out var cdpResult) &&
                    cdpResult.TryGetProperty("result", out var evalResult) &&
                    evalResult.TryGetProperty("value", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    // Only a string value maps cleanly; GetString() throws on e.g. a JSON boolean, so
                    // callers that want a bool stringify it in JS (String(...)). Anything non-string
                    // falls through to the raw response text (same as the old code) rather than throwing.
                    return value.GetString() ?? responseText;
                }
                return responseText; // our response, but no plain string value (e.g. an injection eval)
            }
            return "";
        }
        catch (Exception ex)
        {
            _log.LogDebug("EvaluateReturnAsync: CDP call failed: {Message}", ex.Message);
            EvictSocket(tabId);
            return "";
        }
    }

    /// <summary>Fire-and-forget evaluate (used for injecting the polyfill + luatools.js). Shares the same
    /// persistent socket + id-correlated read path; the return value is ignored.</summary>
    private async Task EvaluateAsync(string tabId, string wsUrl, string script, CancellationToken ct)
    {
        await EvaluateReturnAsync(tabId, wsUrl, script, ct);
    }

    private string? FindLuaToolsJs()
    {
        // The frontend is no longer bundled with the app — PluginInstallerService downloads it from
        // GitHub releases into %AppData%\LuaToolsAmethyst\plugin. If it isn't installed yet, nothing injects.
        string[] candidates =
        {
            Path.Combine(AppDataMigrator.CurrentDirectory, "plugin", "public", "luatools.js"),
            Path.Combine(AppDataMigrator.CurrentDirectory, "plugin", "luatools.js"),
        };
        foreach (var path in candidates)
            if (File.Exists(path)) return path;
        return null;
    }

    private string? FindPolyfillJs()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "public", "millennium-polyfill.js"),
            Path.Combine(AppContext.BaseDirectory, "millennium-polyfill.js"),
        };
        foreach (var path in candidates)
            if (File.Exists(path)) return path;
        return null;
    }

    /// <summary>Real Millennium (if present on this page) already defines window.Millennium.callServerMethod
    /// as one shared object used by every Millennium plugin, routed server-side by the pluginName argument —
    /// it is NOT per-plugin namespaced. Blindly overwriting it (the old behavior) broke every other
    /// Millennium plugin on the page whenever both injectors were active, not just LuaTools. This only takes
    /// over "luatools" calls and passes every other pluginName through to the real callServerMethod
    /// unchanged, so this app's CDP injection can coexist with Millennium instead of requiring it be absent.
    /// _pending/_readyResponses are attached directly onto whichever object ends up as window.Millennium
    /// (fake or real) under their original names — ProcessSingleTab's polling reads/writes those regardless
    /// of which case this is.</summary>
    internal static string BuildInlinePolyfill()
    {
        // The timeout below resolves {success:true} on purpose: that bare, one-key object is the contract the
        // upstream frontend (luatools.js, madoiscool/LTSP, fetched unpinned) is written against. Its status
        // poll tells a bridge timeout from a real answer by SHAPE, `Object.keys(st).length <= 1` ("bridge
        // timeout: bare {success:true}"), and tolerates 5 in a row before showing "app not responding". A
        // success:false with an `error` field has two keys, skips that guard and lands on its `st.error`
        // branch instead. Revisit only with a pinned frontend version.
        return @"
(function(){
var real=window.Millennium;
var pending={},ready={},timedOut=[],reqId=0;
function ltCall(p,m,a){
  var i='_ltr_'+(++reqId);
  pending[i]={method:m,args:a,ts:Date.now()};
  return new Promise(function(rv,rj){
    var mx=m==='InstallFromBackup'?600:100; // 50 ms polls: 30 s for the restore, 5 s for the rest
    function ck(){
      var r=ready[i];
      if(r!==undefined){delete ready[i];if(r.e){rj(new Error(r.e))}else{rv(r.v)}return}
      if(--mx>0){setTimeout(ck,50)}else{delete pending[i];timedOut.push({id:i,m:m});rv({success:true})}
    }
    ck();
  });
}
if(real&&typeof real.callServerMethod==='function'){
  var realCall=real.callServerMethod.bind(real);
  real.callServerMethod=function(p,m,a){return p==='luatools'?ltCall(p,m,a):realCall(p,m,a)};
  real._pending=pending;
  real._readyResponses=ready;
  real._timedOut=timedOut;
  window.Millennium=real;
}else{
  window.Millennium={_pending:pending,_readyResponses:ready,_timedOut:timedOut,callServerMethod:function(p,m,a){return ltCall(p,m,a)}};
}
})();
";
    }
}

internal class CefTabInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string? Title { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("url")]
    public string? Url { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("webSocketDebuggerUrl")]
    public string? WebSocketDebuggerUrl { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal class PendingRequest
{
    public string? id { get; set; }
    public string? m { get; set; }
    public string? a { get; set; }
    public double t { get; set; } // the polyfill's ts for the call
}

/// <summary>What the page's <c>_readyResponses</c> receives; <c>t</c> picks the call it answers.</summary>
internal sealed record BridgeResponse(string id, double t, object? v = null, string? e = null);

/// <summary>One tab's calls between pickup and delivery. <see cref="InFlight"/> belongs to the drain;
/// backend tasks only enqueue into <see cref="Done"/>.</summary>
internal sealed class TabBridge
{
    public HashSet<(string Id, double Ts)> InFlight { get; } = new();
    public ConcurrentQueue<BridgeResponse> Done { get; } = new();
}
