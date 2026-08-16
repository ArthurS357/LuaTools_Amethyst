using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers the OAuth anti-forgery <c>state</c> check.
///
/// <para>
/// The sign-in flow redirects to a plain local HTTP listener, which previously accepted any request
/// bearing a <c>?code=</c> — so a web page could hand the app an attacker's authorization code and get the
/// user silently signed into someone else's account. PKCE does not cover that; a round-tripped
/// <c>state</c> does.
/// </para>
/// </summary>
public class AuthStateTests
{
    private const string Expected = "yhL3mQ2fR8sT1vW4xY7zA0bC5dE6fG9h";

    [Fact]
    public void Accepts_the_state_we_generated() =>
        AuthService.StateMatches(Expected, Expected).Should().BeTrue();

    [Theory]
    [InlineData(null)]                                   // callback with no state at all
    [InlineData("")]
    [InlineData("some-other-state")]
    [InlineData("yhL3mQ2fR8sT1vW4xY7zA0bC5dE6fG9")]      // one char short
    [InlineData("yhL3mQ2fR8sT1vW4xY7zA0bC5dE6fG9hi")]    // one char long
    [InlineData("YHL3MQ2FR8ST1VW4XY7ZA0BC5DE6FG9H")]     // case must matter — it's an opaque nonce
    [InlineData(" yhL3mQ2fR8sT1vW4xY7zA0bC5dE6fG9h")]    // whitespace must not be tolerated
    public void Rejects_a_callback_that_is_not_ours(string? received) =>
        AuthService.StateMatches(received, Expected).Should().BeFalse();

    [Fact]
    public void An_empty_expected_state_is_never_matched()
    {
        // Defensive: a bug that left the expected state blank must not turn into "accept everything".
        AuthService.StateMatches("", "").Should().BeFalse();
        AuthService.StateMatches("anything", "").Should().BeFalse();
    }
}
