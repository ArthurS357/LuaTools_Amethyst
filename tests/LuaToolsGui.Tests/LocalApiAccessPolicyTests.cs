using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Guards the access rules for the loopback HTTP API on port 6767.
///
/// <para>
/// These endpoints install and delete lua files, launch URLs through ShellExecute and restart Steam. The
/// server first answered everything with <c>Access-Control-Allow-Origin: *</c>; the first fix then served
/// any request that carried no <c>Origin</c> (and any allow-listed Steam origin) without a token, on the
/// premise that a browser always sends Origin. It does not: a <c>GET</c> by navigation or
/// <c>&lt;img&gt;</c> sends none (F-02). The token is now required for every request, and the rules below
/// pin that, because a regression is silent — the app keeps working while being open again.
/// </para>
/// </summary>
public class LocalApiAccessPolicyTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    // ── The hole: a request with no credential at all ─────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-token")]
    [InlineData("0123456789abcdef0123456789abcde")]   // one char short
    [InlineData("0123456789abcdef0123456789abcdefx")] // one char long
    public void A_loopback_request_without_the_right_token_is_rejected(string? presented) =>
        LocalApiAccessPolicy.Evaluate("127.0.0.1:6767", presented, Token).Should().BeFalse(
            "no Origin header proves nothing — a GET by navigation or <img> sends none");

    [Fact]
    public void An_empty_expected_token_is_never_matched()
    {
        // Defensive: if the session token were ever left unset, an empty presented token must not
        // accidentally equal it and hand out blanket access.
        LocalApiAccessPolicy.Evaluate("127.0.0.1:6767", "", "").Should().BeFalse();
    }

    // ── DNS rebinding ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("evil.example")]
    [InlineData("evil.example:6767")]
    [InlineData("127.0.0.1.evil.example:6767")]
    [InlineData("localtest.me:6767")]              // public name that resolves to 127.0.0.1
    [InlineData("")]
    [InlineData(null)]
    public void A_non_loopback_host_header_is_rejected_even_with_a_valid_token(string? host)
    {
        // The socket is bound to 127.0.0.1, but a hostile DNS name pointing there still reaches it —
        // carrying its own Host header. Host is checked before the token so a rebound name can't use it.
        LocalApiAccessPolicy.Evaluate(host, Token, Token).Should().BeFalse();
    }

    // ── The app's own bridge client (false-positive guard) ────────────────────

    [Theory]
    [InlineData("127.0.0.1:6767")]
    [InlineData("127.0.0.1")]
    [InlineData("localhost:6767")]
    [InlineData("LOCALHOST:6767")]
    [InlineData("[::1]:6767")]                     // IPv6 literal: brackets keep its colons out of the port split
    [InlineData("[::1]")]
    public void The_correct_token_on_a_loopback_host_is_accepted(string host) =>
        LocalApiAccessPolicy.Evaluate(host, Token, Token).Should().BeTrue();
}
