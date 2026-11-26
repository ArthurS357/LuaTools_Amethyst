using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>Skips, visibly, when node is not on PATH. The drain's pairing and the polyfill's timeout are
/// JavaScript, and only a real engine shows what the page ends up with.</summary>
public sealed class NodeFactAttribute : FactAttribute
{
    public NodeFactAttribute() { if (!NodePage.Available) Skip = "node not on PATH"; }
}

/// <summary>A store-page stand-in: one node process holding a vm context, driven line by line the way CDP's
/// Runtime.evaluate drives the tab (a string result comes back as-is, anything else as a JSON envelope).
/// <c>setTimeout</c> is a manual clock that remembers which <c>__call</c> each timer chain belongs to, so a
/// test can run one request's polyfill timeout to the end (<c>__expire</c>) or advance everyone by one
/// 50 ms poll (<c>__tick</c>).</summary>
internal sealed class NodePage : IDisposable
{
    private const string Harness = """
        const vm = require('vm'), rl = require('readline').createInterface({ input: process.stdin });
        const c = vm.createContext({});
        vm.runInContext(`var window = this, __t = [], __o = null, __res = {};
          function setTimeout(f) { __t.push({ o: __o, f: f }); }
          function __call(n, m, a) { __o = n; window.Millennium.callServerMethod('luatools', m, a).then(function (v) { __res[n] = { v: v }; }, function (e) { __res[n] = { e: e.message }; }); __o = null; }
          function __expire(n) { for (var k = 0; k < 1000; k++) { var i = __t.findIndex(function (t) { return t.o === n; }); if (i < 0) return; var t = __t.splice(i, 1)[0]; __o = n; t.f(); __o = null; } }
          function __tick() { __t.splice(0).forEach(function (t) { __o = t.o; t.f(); __o = null; }); }`, c);
        rl.on('line', async l => {
          let out;
          try { const v = vm.runInContext(JSON.parse(l), c); await new Promise(r => setImmediate(r)); out = typeof v === 'string' ? v : JSON.stringify({ result: { result: { type: typeof v } } }); }
          catch (e) { out = JSON.stringify({ exceptionDetails: String(e) }); }
          process.stdout.write(JSON.stringify(out) + '\n');
        });
        """;

    private static readonly Lazy<bool> _available = new(() =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("node", "--version")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    });

    internal static bool Available => _available.Value;

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"luatools-nodepage-{Guid.NewGuid():N}.js");
    private readonly Process _node;

    public NodePage()
    {
        File.WriteAllText(_file, Harness);
        _node = Process.Start(new ProcessStartInfo("node", $"\"{_file}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        })!;
    }

    public async Task<string> Eval(string expression)
    {
        await _node.StandardInput.WriteLineAsync(JsonSerializer.Serialize(expression));
        await _node.StandardInput.FlushAsync();
        // 30 s, not 10: the first answer waits for node to cold-start, and on a loaded CI runner (the rest of
        // the suite running in parallel, Defender scanning the fresh temp .js) that alone once took over 10 s.
        // A real hang still fails, just later.
        var line = await _node.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return JsonSerializer.Deserialize<string>(line ?? "null") ?? "";
    }

    public void Dispose()
    {
        try { _node.Kill(); } catch { /* already gone */ }
        _node.Dispose();
        try { File.Delete(_file); } catch (IOException) { /* best effort */ }
    }
}

public class CefInjectorPairingTests
{
    /// <summary>Drains the way the injection loop's ticks do, until every picked-up call has been delivered.
    /// It leaves the page's clock alone, so a slow runner cannot time a call out.</summary>
    private static async Task DrainUntilIdle(NodePage page, Func<string, string, Task<string>> backend, TabBridge bridge)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            await CefInjectorService.DrainPendingAsync(page.Eval, backend, bridge);
            await Task.Delay(10);
        }
        while (bridge.InFlight.Count > 0 && elapsed.Elapsed < TimeSpan.FromSeconds(30));
        bridge.InFlight.Should().BeEmpty();
    }

    private static async Task<JsonElement> Results(NodePage page)
    {
        await page.Eval("__tick()");
        return JsonDocument.Parse(await page.Eval("JSON.stringify(__res)")).RootElement;
    }

    /// <summary>Queues A, B and C as one batch and serves them with a backend that echoes the appid it was
    /// asked about; B's page-side timeout (the polyfill's 100 × 50 ms polls) runs out while the batch is in
    /// flight.</summary>
    private static async Task<JsonElement> DrainWithBTimingOut(NodePage page, string method)
    {
        await page.Eval(CefInjectorService.BuildInlinePolyfill());
        foreach (var n in new[] { "A", "B", "C" })
            await page.Eval($"__call('{n}', '{method}', {{ appid: '{n}' }})");

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Backend(string _, string args)
        {
            await release.Task;
            var appid = JsonDocument.Parse(args).RootElement.GetProperty("appid").GetString();
            return $$"""{"success":true,"appid":"{{appid}}"}""";
        }

        var bridge = new TabBridge();
        await CefInjectorService.DrainPendingAsync(page.Eval, Backend, bridge);
        await page.Eval("__expire('B')");
        release.SetResult();
        await DrainUntilIdle(page, Backend, bridge);

        return await Results(page);
    }

    private static string? AnsweredAppid(JsonElement results, string name) =>
        results.GetProperty(name).GetProperty("v").TryGetProperty("appid", out var a) ? a.GetString() : null;

    [NodeFact]
    public async Task A_request_that_times_out_mid_batch_does_not_shift_the_other_answers()
    {
        using var page = new NodePage();
        var results = await DrainWithBTimingOut(page, "DeleteLuaToolsForApp");

        AnsweredAppid(results, "A").Should().Be("A");
        AnsweredAppid(results, "B").Should().BeNull("B timed out, so the page resolved it as a bare {success:true}");
        AnsweredAppid(results, "C").Should().Be("C", "C must get its own answer, not the one the backend gave B");
    }

    [NodeFact]
    public async Task Without_a_timeout_every_request_gets_its_own_answer()
    {
        using var page = new NodePage();
        await page.Eval(CefInjectorService.BuildInlinePolyfill());
        foreach (var n in new[] { "A", "B", "C" })
            await page.Eval($"__call('{n}', 'HasLuaToolsForApp', {{ appid: '{n}' }})");

        await DrainUntilIdle(page, (_, args) =>
            Task.FromResult($$"""{"appid":{{JsonDocument.Parse(args).RootElement.GetProperty("appid").GetRawText()}}}"""), new TabBridge());

        var results = await Results(page);
        foreach (var n in new[] { "A", "B", "C" })
            AnsweredAppid(results, n).Should().Be(n);
        (await page.Eval("JSON.stringify(Object.keys(window.Millennium._pending))")).Should().Be("[]");
    }

    [NodeFact]
    public async Task A_polyfill_timeout_is_logged_with_its_id_and_method()
    {
        // No "_" before the guid: name_<hex> is the Hubcap key shape and LogSanitizer redacts it.
        var method = "ReproTimeout" + Guid.NewGuid().ToString("N");
        using var page = new NodePage();
        await DrainWithBTimingOut(page, method);

        // B is the second call in a fresh page, so the polyfill named it _ltr_2.
        var lines = TestLogRedirect.ReadLogLines().Where(l => l.Contains(method)).ToList();
        lines.Should().ContainSingle().Which.Should().Contain("CEF bridge timeout: _ltr_2 (" + method + ")");
    }

    /// <summary>Page → polyfill → drain → methodMap → the real HttpServerService, error statuses included:
    /// an unmapped name or a masked error would both show up as a bare {success:true}.</summary>
    [NodeFact]
    public async Task Backup_methods_pair_with_the_real_server_and_keep_its_error_bodies()
    {
        using var backend = new HttpBackupRoutesTests();
        backend.SeedAndEnableBackups();
        var dir = Path.Combine(Path.GetTempPath(), "luatools-cef-" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new CefInjectorService(new SteamService(new SettingsService(dir)),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CefInjectorService>.Instance)
            { BaseUrl = backend.BaseUrl };

            using var page = new NodePage();
            await page.Eval(CefInjectorService.BuildInlinePolyfill());
            await page.Eval("__call('index', 'GetBackupIndex', {})");
            await page.Eval("__call('status', 'GetBackupStatus', { appid: 386940 })");
            await page.Eval("__call('noAppid', 'GetBackupStatus', {})");
            await page.Eval("__call('badAppid', 'GetBackupStatus', { appid: '386940&appid=480' })");
            await page.Eval("__call('install', 'InstallFromBackup', { appid: 386940 })");
            await page.Eval("__call('installText', 'InstallFromBackup', { appid: '386940' })");
            await page.Eval("__call('absent', 'InstallFromBackup', { appid: 999999 })");

            await DrainUntilIdle(page, (m, a) => svc.CallBackendMethod(m, a), new TabBridge());

            var results = await Results(page);
            JsonElement Body(string name) => results.GetProperty(name).GetProperty("v");

            Body("index").GetProperty("games").GetArrayLength().Should().Be(2);
            Body("status").GetProperty("appId").GetInt64().Should().Be(386940);
            Body("status").GetProperty("available").GetBoolean().Should().BeTrue();
            foreach (var n in new[] { "noAppid", "badAppid" })
                Body(n).GetProperty("error").GetString().Should().Be("Invalid appid");
            foreach (var n in new[] { "install", "installText" })
                Body(n).GetProperty("luaInstalled").GetBoolean().Should().BeTrue();
            Body("absent").GetProperty("success").GetBoolean().Should().BeFalse();
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ } }
    }

    [NodeFact]
    public async Task A_slow_restore_neither_blocks_nor_drops_the_calls_around_it()
    {
        using var page = new NodePage();
        await page.Eval(CefInjectorService.BuildInlinePolyfill());
        var restore = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        Task<string> Backend(string method, string _)
        {
            calls.Add(method);
            return method == "InstallFromBackup" ? restore.Task : Task.FromResult($$"""{"method":"{{method}}"}""");
        }
        var bridge = new TabBridge();

        await page.Eval("__call('install', 'InstallFromBackup', { appid: 1 })");
        await page.Eval("__call('sameBatch', 'GetBackupStatus', { appid: 1 })");
        await CefInjectorService.DrainPendingAsync(page.Eval, Backend, bridge);
        (await Results(page)).TryGetProperty("sameBatch", out _).Should().BeTrue("it must not wait for the restore");

        await page.Eval("__call('nextBatch', 'GetBackupIndex', {})");
        await CefInjectorService.DrainPendingAsync(page.Eval, Backend, bridge);
        await page.Eval("for (var i = 0; i < 200; i++) __tick()"); // 10 s of page time, restore still running
        await CefInjectorService.DrainPendingAsync(page.Eval, Backend, bridge);

        var during = await Results(page);
        during.TryGetProperty("install", out _).Should().BeFalse();
        during.GetProperty("sameBatch").GetProperty("v").GetProperty("method").GetString().Should().Be("GetBackupStatus");
        during.GetProperty("nextBatch").GetProperty("v").GetProperty("method").GetString().Should().Be("GetBackupIndex");
        calls.Should().Equal(["InstallFromBackup", "GetBackupStatus", "GetBackupIndex"], "a call in flight is not started again");

        restore.SetResult("""{"success":true,"luaInstalled":true}""");
        await DrainUntilIdle(page, Backend, bridge);
        (await Results(page)).GetProperty("install").GetProperty("v").GetProperty("luaInstalled").GetBoolean().Should().BeTrue();
    }

    [NodeFact]
    public async Task A_delivery_the_page_did_not_run_is_retried_without_calling_the_backend_again()
    {
        using var page = new NodePage();
        await page.Eval(CefInjectorService.BuildInlinePolyfill());
        await page.Eval("__call('A', 'HasLuaToolsForApp', { appid: 1 })");
        int calls = 0;
        Task<string> Backend(string _, string __) => Task.FromResult($$"""{"call":{{++calls}}}""");
        var bridge = new TabBridge();

        // The first evaluate reads _pending; the second, the delivery, answers like a dead socket.
        int evals = 0;
        await CefInjectorService.DrainPendingAsync(
            e => ++evals == 2 ? Task.FromResult("") : page.Eval(e), Backend, bridge);
        bridge.Done.Should().ContainSingle();

        await DrainUntilIdle(page, Backend, bridge);

        (await Results(page)).GetProperty("A").GetProperty("v").GetProperty("call").GetInt32().Should().Be(1);
        calls.Should().Be(1);
    }

    [NodeFact]
    public async Task An_answer_for_the_previous_page_does_not_land_on_a_reused_id()
    {
        using var page = new NodePage();
        await page.Eval(CefInjectorService.BuildInlinePolyfill());
        await page.Eval("__call('old', 'InstallFromBackup', { appid: 1 })");
        var old = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> Backend(string method, string _) => method == "InstallFromBackup" ? old.Task : current.Task;
        var bridge = new TabBridge();
        await CefInjectorService.DrainPendingAsync(page.Eval, Backend, bridge);

        await Task.Delay(5); // a later Date.now()
        await page.Eval(CefInjectorService.BuildInlinePolyfill()); // counts ids from 1 again, like a reload
        await page.Eval("__call('new', 'GetBackupIndex', {})");
        await CefInjectorService.DrainPendingAsync(page.Eval, Backend, bridge);
        old.SetResult("""{"from":"old"}""");
        while (bridge.Done.IsEmpty) await Task.Delay(10);
        await CefInjectorService.DrainPendingAsync(page.Eval, Backend, bridge);
        (await Results(page)).TryGetProperty("new", out _).Should().BeFalse();

        current.SetResult("""{"from":"new"}""");
        await DrainUntilIdle(page, Backend, bridge);
        (await Results(page)).GetProperty("new").GetProperty("v").GetProperty("from").GetString().Should().Be("new");
    }

    [NodeFact]
    public async Task Only_InstallFromBackup_gets_the_30_s_polyfill_timeout()
    {
        using var page = new NodePage();
        await page.Eval(CefInjectorService.BuildInlinePolyfill());
        await page.Eval("__call('other', 'GetBackupStatus', { appid: 1 })");
        await page.Eval("__call('install', 'InstallFromBackup', { appid: 1 })");

        // The call itself is the first poll, so N polls take N - 1 ticks.
        async Task<string> SettledAfter(int ticks)
        {
            await page.Eval($"for (var i = 0; i < {ticks}; i++) __tick()");
            return await page.Eval("JSON.stringify(Object.keys(__res))");
        }

        (await SettledAfter(98)).Should().Be("[]");
        (await SettledAfter(1)).Should().Be("""["other"]""");                // 100 × 50 ms
        (await SettledAfter(499)).Should().Be("""["other"]""");
        (await SettledAfter(1)).Should().Be("""["other","install"]""");      // 600 × 50 ms
    }

    [Fact]
    public async Task An_unmapped_method_still_answers_success_but_is_logged_by_name_only()
    {
        var dir = Path.Combine(Path.GetTempPath(), "luatools-cef-" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new CefInjectorService(new SteamService(new SettingsService(dir)),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CefInjectorService>.Instance);
            var method = "NoSuchMethod" + Guid.NewGuid().ToString("N");

            (await svc.CallBackendMethod(method, """{"appid":"secret-arg"}""")).Should().Be("""{"success":true}""");

            var line = TestLogRedirect.ReadLogLines().Should().ContainSingle(l => l.Contains(method)).Which;
            line.Should().EndWith("unmapped plugin method: " + method).And.NotContain("secret-arg");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ } }
    }
}
