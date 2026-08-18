using AwesomeAssertions;
using LuaToolsGui;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Guards the shape of the Discord authorize request.
///
/// <para>
/// This file used to test a <c>state</c> nonce the app sent to Supabase and compared on the way back. The
/// nonce was well-intentioned and it broke sign-in completely: Supabase mints its own <c>state</c> and
/// stores the pending flow under it, carrying the redirect target across the trip to Discord. Supplying
/// ours overwrote that key, so Supabase could not resolve where to send the user afterwards, the local
/// listener was never called, and every sign-in ended as a five-minute "timed out" — the "the Discord
/// button does not actually log me in" report.
/// </para>
///
/// <para>
/// So the invariant worth testing inverted: what matters now is that the parameter is NOT there. An
/// absence is invisible in review and reads like something missing, which is exactly the kind of thing
/// someone re-adds as hardening. These tests are what make that a failing build instead of an outage.
/// </para>
/// </summary>
public class AuthStateTests
{
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [Fact]
    public void The_authorize_url_carries_no_state_parameter()
    {
        string url = AuthService.BuildAuthorizeUrl(Challenge);

        url.Should().NotContain("state=",
            "Supabase keys the pending flow on the state IT mints; supplying one breaks the redirect back");
    }

    [Fact]
    public void The_authorize_url_still_asks_discord_for_a_pkce_code()
    {
        // PKCE is what actually binds the exchange to this client now that no nonce does. Losing it would
        // leave the local listener exchangeable by anything that can reach the port.
        string url = AuthService.BuildAuthorizeUrl(Challenge);

        url.Should().Contain("provider=discord");
        url.Should().Contain($"code_challenge={Challenge}");
        url.Should().Contain("code_challenge_method=s256");
    }

    [Fact]
    public void The_authorize_url_points_back_at_the_local_listener()
    {
        // The port here and the one HttpListener binds are the same constant; if they ever diverge the
        // browser completes the flow and the app waits forever for a callback going somewhere else.
        string url = AuthService.BuildAuthorizeUrl(Challenge);

        url.Should().Contain(Uri.EscapeDataString(AppConfig.OAuthCallbackUrl));
        AppConfig.OAuthCallbackUrl.Should().Contain(AppConfig.OAuthCallbackPort.ToString());
    }

    [Fact]
    public void The_authorize_url_goes_to_the_configured_supabase_project_over_tls()
    {
        string url = AuthService.BuildAuthorizeUrl(Challenge);

        url.Should().StartWith(AppConfig.SupabaseUrl + "/auth/v1/authorize");
        url.Should().StartWith("https://", "the authorization request carries the redirect target");
    }

    [Fact]
    public void The_code_sign_in_alternative_is_still_reachable()
    {
        // The /login code path is the one that has always worked, and it is what the timed-out message now
        // sends people to. If the wording ever stops naming it, the browser failure becomes a dead end
        // again — which was half of what made this feature feel broken.
        Resources.Strings.Auth_Err_SignInTimedOut.Should().ContainEquivalentOf("/login");
        Resources.Strings.Auth_Err_SignInTimedOut.Should().ContainEquivalentOf("code");
    }
}
