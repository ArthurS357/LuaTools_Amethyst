using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// <see cref="SteamLaunchPolicy"/> and <see cref="SteamProtocolUri"/> — both pure, no Steam, no processes,
/// no filesystem. The policy is the whole of what the Play button decides; the URI builder is the security
/// boundary between an appid and a string handed to the Windows shell.
/// </summary>
public class SteamLaunchPolicyTests
{
    private const long RealAppId = 730; // Counter-Strike 2

    // ── The four combinations the button exists to cover ──────────────

    [Fact]
    public void Installed_game_with_Steam_running_just_plays()
    {
        var plan = SteamLaunchPolicy.Decide(SteamGameInstallState.Installed, steamRunning: true);

        plan.Intent.Should().Be(SteamLaunchIntent.Play);
        plan.StartSteamFirst.Should().BeFalse();
    }

    [Fact]
    public void Installed_game_with_Steam_closed_starts_Steam_first()
    {
        var plan = SteamLaunchPolicy.Decide(SteamGameInstallState.Installed, steamRunning: false);

        plan.Intent.Should().Be(SteamLaunchIntent.Play);
        plan.StartSteamFirst.Should().BeTrue("a steam:// URL sent at a dead client is silently dropped");
    }

    [Fact]
    public void Missing_game_with_Steam_running_installs_instead_of_playing()
    {
        var plan = SteamLaunchPolicy.Decide(SteamGameInstallState.NotInstalled, steamRunning: true);

        plan.Intent.Should().Be(SteamLaunchIntent.Install);
        plan.StartSteamFirst.Should().BeFalse();
    }

    [Fact]
    public void Missing_game_with_Steam_closed_starts_Steam_then_installs()
    {
        var plan = SteamLaunchPolicy.Decide(SteamGameInstallState.NotInstalled, steamRunning: false);

        plan.Intent.Should().Be(SteamLaunchIntent.Install);
        plan.StartSteamFirst.Should().BeTrue();
    }

    // ── Unknown: the case where the app is the one that is unsure ──────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_unknown_install_state_plays_rather_than_refusing(bool steamRunning)
    {
        // Unknown means the library could not be read (Steam not located), NOT that the game is absent.
        // rungameid is self-correcting — Steam answers with its own install prompt if the game isn't
        // there — so playing is the graceful degradation. Refusing would strand the user behind a dead
        // button precisely when the app cannot tell; choosing Install would shove an install dialog in
        // front of a game that may already be sitting on disk, ready to run.
        var plan = SteamLaunchPolicy.Decide(SteamGameInstallState.Unknown, steamRunning);

        plan.Intent.Should().Be(SteamLaunchIntent.Play);
        plan.StartSteamFirst.Should().Be(!steamRunning);
    }

    [Fact]
    public void Steams_own_state_never_changes_which_verb_is_chosen()
    {
        // Whether Steam happens to be up is about SEQUENCING, not about play-vs-install. Coupling the two
        // is how "I closed Steam, so now it wants to reinstall my game" gets shipped.
        foreach (var state in Enum.GetValues<SteamGameInstallState>())
        {
            SteamLaunchPolicy.Decide(state, steamRunning: true).Intent
                .Should().Be(SteamLaunchPolicy.Decide(state, steamRunning: false).Intent, $"state {state}");
        }
    }

    [Fact]
    public void The_button_label_and_the_action_come_from_the_same_rule()
    {
        // The label is rendered from IntentFor and the launch is performed from Decide. If those two ever
        // disagree the button promises one verb and does the other.
        foreach (var state in Enum.GetValues<SteamGameInstallState>())
        {
            SteamLaunchPolicy.IntentFor(state)
                .Should().Be(SteamLaunchPolicy.Decide(state, steamRunning: true).Intent, $"state {state}");
        }
    }

    // ── URI building: the happy path ──────────────────────────────────

    [Fact]
    public void A_real_appid_builds_the_documented_steam_urls()
    {
        SteamProtocolUri.RunGame(RealAppId).Should().Be("steam://rungameid/730");
        SteamProtocolUri.Install(RealAppId).Should().Be("steam://install/730");
    }

    [Fact]
    public void For_routes_each_intent_to_its_own_verb()
    {
        SteamProtocolUri.For(SteamLaunchIntent.Play, RealAppId).Should().Be("steam://rungameid/730");
        SteamProtocolUri.For(SteamLaunchIntent.Install, RealAppId).Should().Be("steam://install/730");
    }

    // ── URI building: the malicious / malformed input it exists to stop ──

    [Theory]
    [InlineData(0)]              // an unparsed or defaulted id
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    [InlineData(2_000_000_000)]  // the bound itself is exclusive
    [InlineData(long.MaxValue)]  // a rungameid composite, not an appid
    public void An_appid_outside_the_valid_range_produces_no_url_at_all(long appId)
    {
        // Null is the refusal, and it has to stay null rather than degrading to a raw string: the caller
        // reads null as "start no process". A fallback here would be the bypass.
        SteamProtocolUri.IsValidAppId(appId).Should().BeFalse();
        SteamProtocolUri.RunGame(appId).Should().BeNull();
        SteamProtocolUri.Install(appId).Should().BeNull();
        SteamProtocolUri.For(SteamLaunchIntent.Play, appId).Should().BeNull();
        SteamProtocolUri.For(SteamLaunchIntent.Install, appId).Should().BeNull();
    }

    [Fact]
    public void A_built_url_can_only_ever_contain_the_scheme_the_verb_and_digits()
    {
        // The regression this guards: the appid is taken as a validated `long`, never as a string, so
        // there is no input that survives the parse into a number and still carries a quote, a space, an
        // "&", a second argument, or a different scheme. Assert the SHAPE rather than one example, across
        // the whole valid range including its boundaries.
        foreach (long appId in new long[] { 1, 730, 12345, 1_999_999_999 })
        {
            foreach (string? url in new[] { SteamProtocolUri.RunGame(appId), SteamProtocolUri.Install(appId) })
            {
                url.Should().NotBeNull();
                url.Should().MatchRegex(@"\Asteam://(rungameid|install)/\d+\z");
            }
        }
    }

    [Fact]
    public void The_valid_range_is_exactly_the_one_the_link_parser_already_enforces()
    {
        // SteamLinkParser refuses the same band for the same reason (ids at/above 2e9 are the composite
        // 64-bit values rungameid uses for non-Steam shortcuts). Two different answers to "is this an
        // appid" in one app is how a link that cannot be installed still gets a launch URL built for it.
        SteamProtocolUri.IsValidAppId(1).Should().BeTrue();
        SteamProtocolUri.IsValidAppId(1_999_999_999).Should().BeTrue();

        SteamLinkParser.AppIdFrom("steam://install/1999999999").Should().Be(1_999_999_999);
        SteamLinkParser.AppIdFrom("steam://install/2000000000").Should().BeNull();
        SteamProtocolUri.IsValidAppId(2_000_000_000).Should().BeFalse();
    }
}
