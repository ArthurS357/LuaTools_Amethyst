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
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
        })!;
    }

    public async Task<string> Eval(string expression)
    {
        await _node.StandardInput.WriteLineAsync(JsonSerializer.Serialize(expression));
        await _node.StandardInput.FlushAsync();
        var line = await _node.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
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
    /// <summary>Queues A, B and C as one batch and serves them with a backend that echoes the appid it was
    /// asked about; B's page-side timeout (the polyfill's 100 × 50 ms polls) runs out while A is being
    /// served.</summary>
    private static async Task<JsonElement> DrainWithBTimingOut(NodePage page, string method)
    {
        await page.Eval(CefInjectorService.BuildInlinePolyfill());
        foreach (var n in new[] { "A", "B", "C" })
            await page.Eval($"__call('{n}', '{method}', {{ appid: '{n}' }})");

        await CefInjectorService.DrainPendingAsync(page.Eval, async (_, args) =>
        {
            var appid = JsonDocument.Parse(args).RootElement.GetProperty("appid").GetString();
            if (appid == "A") await page.Eval("__expire('B')");
            return $$"""{"success":true,"appid":"{{appid}}"}""";
        });
        await page.Eval("__tick()");

        return JsonDocument.Parse(await page.Eval("JSON.stringify(__res)")).RootElement;
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

        await CefInjectorService.DrainPendingAsync(page.Eval, (_, args) =>
            Task.FromResult($$"""{"appid":{{JsonDocument.Parse(args).RootElement.GetProperty("appid").GetRawText()}}}"""));
        await page.Eval("__tick()");

        var results = JsonDocument.Parse(await page.Eval("JSON.stringify(__res)")).RootElement;
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
