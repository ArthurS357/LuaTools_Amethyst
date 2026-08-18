using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers the local shape check a Hubcap key has to pass before the app will spend a request on it.
///
/// <para>
/// The check used to be <c>^smm_[0-9a-f]{96}$</c> — the exact shape of every key Hubcap issues today. That
/// is a bet on someone else's format never changing, and losing it fails in the most confusing way
/// available: Hubcap would accept the key, the app refuses it locally, and the message the user reads
/// ("that doesn't look like a valid key") is indistinguishable from a typo. There is no override.
/// </para>
///
/// <para>
/// So the contract these tests pin is narrower than "valid". Passing means "worth one request"; deciding
/// whether a key is real stays with Hubcap, one line later in ValidateAndSaveHubcapKeyAsync. What must
/// still be rejected is the paste accident — a URL, a JSON blob, an empty box, a truncated fragment.
/// </para>
/// </summary>
public class HubcapKeyFormatTests
{
    private const string Hex96 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // ── Today's keys ──────────────────────────────────────────────────────────

    [Fact]
    public void The_key_shape_hubcap_issues_today_is_accepted() =>
        HubcapService.IsValidKeyFormat("smm_" + Hex96).Should().BeTrue();

    [Fact]
    public void Uppercase_hex_is_the_same_key() =>
        HubcapService.IsValidKeyFormat("smm_" + Hex96.ToUpperInvariant()).Should().BeTrue();

    // ── Shapes Hubcap might move to ───────────────────────────────────────────

    [Theory]
    [InlineData("hc_0123456789abcdef0123456789abcdef")]        // a different prefix
    [InlineData("smmv2_0123456789abcdef0123456789abcdef")]     // a versioned prefix
    [InlineData("0123456789abcdef0123456789abcdef")]           // no prefix at all
    [InlineData("smm_0123456789abcdef")]                       // a shorter body (16 hex)
    [InlineData("smm_0123456789ABCDEF0123456789abcdef")]       // mixed case
    public void A_plausible_future_key_is_not_refused_locally(string key) =>
        HubcapService.IsValidKeyFormat(key).Should().BeTrue(
            "only Hubcap can say a well-formed key is wrong");

    // ── The accidents this still has to catch ─────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("smm_")]                                        // prefix, no body
    [InlineData("smm_0123456789abcde")]                         // 15 hex — one short of the floor
    [InlineData("smm_0123456789abcdefg")]                       // g is not hex
    [InlineData("https://hubcapmanifest.com/dashboard")]        // pasted the page, not the key
    [InlineData("{\"api_key\":\"smm_0123456789abcdef0123456789abcdef\"}")]  // pasted the whole JSON
    [InlineData("smm_0123456789abcdef0123456789abcdef extra")]  // pasted a line, not a token
    [InlineData(" smm_0123456789abcdef0123456789abcdef")]       // untrimmed
    [InlineData("Bearer smm_0123456789abcdef0123456789abcdef")] // pasted the header
    public void An_obvious_paste_accident_is_still_refused(string? key) =>
        HubcapService.IsValidKeyFormat(key).Should().BeFalse();

    [Fact]
    public void An_absurdly_long_value_never_becomes_a_request()
    {
        // The upper bound is what keeps this a guard rather than a formality: a pasted document made
        // entirely of hex characters would otherwise sail through and be sent to Hubcap as a credential.
        string tooLong = new('a', 257);

        HubcapService.IsValidKeyFormat(tooLong).Should().BeFalse();
        HubcapService.IsValidKeyFormat("smm_" + tooLong).Should().BeFalse();
        HubcapService.IsValidKeyFormat(new string('a', 256)).Should().BeTrue("256 is the last accepted length");
    }

    [Fact]
    public void A_newline_cannot_smuggle_a_second_line_past_the_anchors()
    {
        // $ in .NET matches before a trailing newline, so an unanchored-feeling pattern would accept a
        // value with a second line hidden after it.
        HubcapService.IsValidKeyFormat("smm_" + Hex96 + "\n").Should().BeFalse();
        HubcapService.IsValidKeyFormat("smm_" + Hex96 + "\njunk").Should().BeFalse();
    }
}
