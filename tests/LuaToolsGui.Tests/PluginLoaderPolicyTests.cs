using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// What the Plugin page is allowed to CLAIM about the installed loader.
///
/// <para>
/// The bug this pins is a lie, not a crash. <c>DllMatches</c> and <c>UpdateAvailable</c> both come back
/// <c>false</c> when the install is perfectly current AND when nothing could be reached to compare it
/// against — offline, or an active source that publishes nothing installable. Reading them without also
/// reading <c>Offline</c> and <c>ActiveSourceProblem</c> made the page show an amber "Out of date" warning
/// and a green "Up to date" pill in the same card as its own error saying the source could not be reached.
/// Every one of those three statements cannot be true at once, and two of them were never established.
/// </para>
///
/// <para>
/// Tested against <see cref="PluginLoaderPolicy"/> directly rather than through the view-model: the rule is
/// a function of one record, and a test that needs a live <see cref="PluginInstallerService"/> to reach it
/// is a test that gets written as a mirror of the code instead of a check on it.
/// </para>
/// </summary>
public class PluginLoaderPolicyTests
{
    /// <summary>Every rejection reason a source can actually be refused for. <c>None</c> means healthy and
    /// is not one.</summary>
    public static TheoryData<PluginSourceRejection> RealRejections()
    {
        var data = new TheoryData<PluginSourceRejection>();
        foreach (var reason in Enum.GetValues<PluginSourceRejection>())
            if (reason != PluginSourceRejection.None)
                data.Add(reason);
        return data;
    }

    /// <summary>A status with everything healthy; each test overrides only what it is about.</summary>
    private static PluginStatus Status(
        bool frontendInstalled = true,
        bool dllInstalled = true,
        bool dllMatches = true,
        bool updateAvailable = false,
        bool offline = false,
        PluginSourceRejection problem = PluginSourceRejection.None) =>
        new(frontendInstalled, dllInstalled, dllMatches,
            InstalledTag: "v1.0.0", LatestTag: "v1.0.0", updateAvailable,
            MillenniumPresent: false, offline, Port8080Busy: false,
            ActiveSource: "ArthurS357/Front-end-Amethyst",
            ActiveSourceProblem: problem);

    // ── The regression: an unanswerable question is never resolved into an answer ──

    [Fact]
    public void Offline_reports_the_loader_as_unverifiable_not_out_of_date()
    {
        // Offline forces DllMatches false because there is no digest to compare against — NOT because the
        // file is stale. Amber here tells the user to update something that may already be current, using
        // a source that is by definition unreachable.
        var status = Status(dllMatches: false, offline: true);

        PluginLoaderPolicy.Loader(status).Should().Be(PluginLoaderState.Unverifiable);
        PluginLoaderPolicy.LatestKnown(status).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(RealRejections))]
    public void A_broken_active_source_reports_the_loader_as_unverifiable(PluginSourceRejection reason)
    {
        // Reachable, but published nothing installable — a missing asset, no digest, a redirected URL.
        // Same consequence as offline and, importantly, still not a reason to reach for the other source.
        var status = Status(dllMatches: false, problem: reason);

        PluginLoaderPolicy.Loader(status).Should().Be(PluginLoaderState.Unverifiable,
            $"{reason} means no release was established, so nothing may be said about how current the "
          + "loader is");
    }

    [Fact]
    public void The_up_to_date_pill_is_refused_when_no_release_was_found()
    {
        // UpdateAvailable is false here for the wrong reason: there was nothing to update TO. The pill
        // would otherwise appear directly beside the error box explaining that the source is broken.
        PluginLoaderPolicy.ShowUpToDate(Status(updateAvailable: false, offline: true))
            .Should().BeFalse("nothing to update to is not the same as nothing to update");

        PluginLoaderPolicy.ShowUpToDate(
                Status(updateAvailable: false, problem: PluginSourceRejection.NoDigest))
            .Should().BeFalse();
    }

    // ── The states that ARE established still report normally ──

    [Fact]
    public void A_matching_loader_against_a_real_release_is_up_to_date() =>
        PluginLoaderPolicy.Loader(Status()).Should().Be(PluginLoaderState.UpToDate);

    [Fact]
    public void A_mismatching_loader_against_a_real_release_is_out_of_date() =>
        PluginLoaderPolicy.Loader(Status(dllMatches: false)).Should().Be(PluginLoaderState.OutOfDate,
            "the fix must not neuter the genuine case — this is the one that actually offers the update");

    [Fact]
    public void No_loader_on_disk_outranks_everything_else() =>
        PluginLoaderPolicy.Loader(Status(dllInstalled: false, dllMatches: false, offline: true))
            .Should().Be(PluginLoaderState.NotInstalled,
                "\"it is not there\" is answerable offline, and is the more useful thing to say");

    [Fact]
    public void The_pill_shows_when_a_release_was_found_and_matched() =>
        PluginLoaderPolicy.ShowUpToDate(Status()).Should().BeTrue();

    [Fact]
    public void An_available_update_withholds_the_pill() =>
        PluginLoaderPolicy.ShowUpToDate(Status(updateAvailable: true)).Should().BeFalse();

    // ── Installed means both halves ───────────────────────────────────────────

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void Installed_needs_the_frontend_and_a_loader_slot(bool frontend, bool dll, bool installed) =>
        PluginLoaderPolicy.IsInstalled(Status(frontendInstalled: frontend, dllInstalled: dll))
            .Should().Be(installed);

    [Fact]
    public void A_frontend_with_no_loader_never_shows_the_pill() =>
        PluginLoaderPolicy.ShowUpToDate(Status(dllInstalled: false)).Should().BeFalse(
            "half an install is not an up-to-date install");

    // ── LatestKnown is about the ACTIVE source only ───────────────────────────

    [Fact]
    public void A_healthy_reachable_source_is_the_only_thing_that_makes_the_latest_known()
    {
        PluginLoaderPolicy.LatestKnown(Status()).Should().BeTrue();
        PluginLoaderPolicy.LatestKnown(Status(offline: true)).Should().BeFalse();
        PluginLoaderPolicy.LatestKnown(Status(problem: PluginSourceRejection.MissingAsset))
            .Should().BeFalse();
    }
}
