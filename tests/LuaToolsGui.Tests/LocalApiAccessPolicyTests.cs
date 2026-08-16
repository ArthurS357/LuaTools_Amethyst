using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Guards the access rules for the loopback HTTP API on port 6767.
///
/// <para>
/// These endpoints install and delete lua files, launch URLs through ShellExecute and restart Steam. The
/// server used to answer every request with <c>Access-Control-Allow-Origin: *</c> and no other check, so
/// any page the user had open in any browser could drive all of it. The cases below pin down the rules
/// that closed that, because a regression here is silent — the app keeps working perfectly while being
/// wide open again.
/// </para>
/// </summary>
public class LocalApiAccessPolicyTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const string SteamStore = "https://store.steampowered.com";

    // ── The hole that was open: an arbitrary web page ──────────────────────────

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:3000")]          // a local dev server is still not us
    [InlineData("https://store.steampowered.com.evil.example")] // suffix-confusion attempt
    [InlineData("https://notstore.steampowered.com")]
    [InlineData("null")]                            // sandboxed iframe / file:// origin
    public void Rejects_a_browser_request_from_an_unknown_origin(string origin)
    {
        var decision = LocalApiAccessPolicy.Evaluate("127.0.0.1:6767", null, origin, Token);

        decision.Allowed.Should().BeFalse("any web page could otherwise drive install/delete/open-url");
        decision.AllowOrigin.Should().BeNull();
    }

    [Fact]
    public void Never_echoes_a_wildcard_origin()
    {
        var decision = LocalApiAccessPolicy.Evaluate("127.0.0.1:6767", null, SteamStore, Token);

        decision.AllowOrigin.Should().NotBe("*").And.Be(SteamStore);
    }

    // ── DNS rebinding ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("evil.example")]
    [InlineData("evil.example:6767")]
    [InlineData("127.0.0.1.evil.example:6767")]
    [InlineData("localtest.me:6767")]              // public name that resolves to 127.0.0.1
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_a_non_loopback_host_header(string? host)
    {
        // The socket is bound to 127.0.0.1, but a hostile DNS name pointing there still reaches it —
        // carrying its own Host header. Pinning Host is what stops the rebound name from being usable.
        LocalApiAccessPolicy.Evaluate(host, Token, null, Token)
            .Allowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("127.0.0.1:6767")]
    [InlineData("127.0.0.1")]
    [InlineData("localhost:6767")]
    [InlineData("LOCALHOST:6767")]
    [InlineData("[::1]:6767")]                     // IPv6 literal: brackets keep its colons out of the port split
    [InlineData("[::1]")]
    public void Accepts_a_loopback_host_header(string host) =>
        LocalApiAccessPolicy.Evaluate(host, Token, null, Token)
            .Allowed.Should().BeTrue();

    // ── The app's own bridge client ───────────────────────────────────────────

    [Fact]
    public void Allows_the_session_token_without_emitting_cors()
    {
        // CefInjectorService is the real caller of these endpoints; it authenticates by token, so it must
        // work regardless of origin — and needs no CORS headers, since it isn't a browser.
        var decision = LocalApiAccessPolicy.Evaluate("127.0.0.1:6767", Token, "https://evil.example", Token);

        decision.Allowed.Should().BeTrue();
        decision.AllowOrigin.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("wrong-token")]
    [InlineData("0123456789abcdef0123456789abcde")]  // one char short
    [InlineData("0123456789abcdef0123456789abcdefx")] // one char long
    public void A_wrong_token_does_not_bypass_the_origin_check(string? presented)
    {
        LocalApiAccessPolicy.Evaluate("127.0.0.1:6767", presented, "https://evil.example", Token)
            .Allowed.Should().BeFalse();
    }

    [Fact]
    public void An_empty_expected_token_is_never_matched()
    {
        // Defensive: if the session token were ever left unset, an empty presented token must not
        // accidentally equal it and hand out blanket access.
        LocalApiAccessPolicy.Evaluate("127.0.0.1:6767", "", "https://evil.example", "")
            .Allowed.Should().BeFalse();
    }

    // ── Non-browser callers ───────────────────────────────────────────────────

    [Fact]
    public void Allows_a_loopback_caller_that_sends_no_origin()
    {
        // A browser always sends Origin on a cross-origin fetch, so its absence means this isn't one.
        var decision = LocalApiAccessPolicy.Evaluate("127.0.0.1:6767", null, null, Token);

        decision.Allowed.Should().BeTrue();
        decision.AllowOrigin.Should().BeNull("a non-browser caller needs no CORS headers");
    }

    // ── Allow-listed Steam surfaces ───────────────────────────────────────────

    [Fact]
    public void Allows_every_configured_steam_origin_and_echoes_it_back()
    {
        foreach (string origin in LocalApiAccessPolicy.AllowedOrigins)
        {
            var decision = LocalApiAccessPolicy.Evaluate("127.0.0.1:6767", null, origin, Token);

            decision.Allowed.Should().BeTrue();
            decision.AllowOrigin.Should().Be(origin);
        }
    }

    [Fact]
    public void Origin_matching_is_case_insensitive_on_scheme_and_host()
    {
        var decision = LocalApiAccessPolicy.Evaluate(
            "127.0.0.1:6767", null, "HTTPS://STORE.STEAMPOWERED.COM", Token);

        decision.Allowed.Should().BeTrue();
        // Echo the canonical allow-listed spelling, not whatever casing the caller sent.
        decision.AllowOrigin.Should().Be(SteamStore);
    }

    [Fact]
    public void Host_check_runs_before_the_token_check()
    {
        // Order matters: a valid token from a rebound host must still be rejected, otherwise the token
        // (which any local process could observe) would defeat the rebinding guard.
        LocalApiAccessPolicy.Evaluate("evil.example", Token, null, Token)
            .Allowed.Should().BeFalse();
    }
}
