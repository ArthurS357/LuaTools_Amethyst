using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers the manifest-lua screen. These files are interpreted by the unlocker INSIDE steam.exe, so their
/// contents are code in the Steam process — and they arrive from web APIs, zips and drag-and-drop.
///
/// <para>
/// The bias matters: a false positive breaks legitimate installs, a false negative only misses an unusual
/// attack. So the "legitimate content is accepted" cases below are as important as the rejections.
/// </para>
/// </summary>
public class LuaManifestValidatorTests
{
    // ── Real manifest content must keep working ───────────────────────────────

    [Fact]
    public void Accepts_a_typical_manifest()
    {
        const string lua = """
            -- Generated manifest
            addappid(480)
            addappid(481, 0, "a1b2c3d4e5f60718293a4b5c6d7e8f90")
            setManifestid(481, "7028871210043332028", 0)
            adddlc(480, 123)
            """;

        var result = LuaManifestValidator.Screen(lua);

        result.Rejected.Should().BeFalse();
        result.UnrecognizedLines.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    [InlineData("-- only a comment")]
    public void Accepts_empty_or_comment_only_content(string lua) =>
        LuaManifestValidator.Screen(lua).Rejected.Should().BeFalse();

    [Fact]
    public void An_unknown_directive_is_counted_but_not_rejected()
    {
        // Upstream generators add new directives over time. Those must not break installs — they are only
        // recorded so the behaviour is visible in the log.
        var result = LuaManifestValidator.Screen("addappid(480)\nsomeFutureDirective(1, 2)");

        result.Rejected.Should().BeFalse("an unrecognised line is not evidence of anything malicious");
        result.UnrecognizedLines.Should().Be(1);
    }

    [Fact]
    public void A_commented_out_dangerous_call_is_not_rejected()
    {
        // Rejecting a file over its own documentation would be a false positive.
        LuaManifestValidator.Screen("-- do not use os.execute here\naddappid(480)")
            .Rejected.Should().BeFalse();
    }

    [Fact]
    public void A_dangerous_name_inside_a_longer_identifier_is_not_rejected() =>
        LuaManifestValidator.Screen("addappid(480) -- see studio.opened notes\nmyio.opener(1)")
            .Rejected.Should().BeFalse("substring matches would reject innocent content");

    // ── The constructs that have no business in a manifest ────────────────────

    [Theory]
    [InlineData("os.execute(\"calc.exe\")")]
    [InlineData("os.remove(\"C:/Windows/System32/drivers/etc/hosts\")")]
    [InlineData("io.open(\"secrets.txt\", \"w\")")]
    [InlineData("io.popen(\"powershell -enc ...\")")]
    [InlineData("loadstring(payload)()")]
    [InlineData("dofile(\"stage2.lua\")")]
    [InlineData("require(\"socket\")")]
    [InlineData("package.loadlib(\"evil.dll\", \"Start\")")]
    [InlineData("debug.sethook(spy)")]
    [InlineData("_G.addappid = hijack")]
    public void Rejects_process_file_and_code_loading_constructs(string line)
    {
        var result = LuaManifestValidator.Screen($"addappid(480)\n{line}");

        result.Rejected.Should().BeTrue();
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Rejects_dynamic_load_even_when_the_rest_looks_legitimate() =>
        LuaManifestValidator.Screen("addappid(480)\nload(\"return 1\")()")
            .Rejected.Should().BeTrue();

    [Fact]
    public void Matching_is_case_insensitive() =>
        LuaManifestValidator.Screen("OS.EXECUTE(\"cmd\")").Rejected.Should().BeTrue();

    [Fact]
    public void Rejection_reason_names_the_offending_construct() =>
        LuaManifestValidator.Screen("io.popen(\"cmd\")").Reason.Should().Contain("io.popen");
}
