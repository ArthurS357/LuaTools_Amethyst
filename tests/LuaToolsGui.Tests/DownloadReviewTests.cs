using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The pre-install disclosure shown for every Mode/Plugin artifact. The record is pure — it only restates
/// facts already established by the pinning, digest and archive checks — so it is tested directly, while
/// the toast that renders it is not.
/// </summary>
public class DownloadReviewTests
{
    private const string Sha = "e3e2d22e098ff3fb359b2876aa2bed9596f0501e6ff588cbffae90a76d2dc4f5";

    private static DownloadReview AReview(
        string? tag = "v1.2", int files = 1, bool screened = false, string sha = Sha) =>
        new("madoiscool", "LTSP", tag, "winmm.dll", sha, files, screened);

    [Fact]
    public void Source_is_the_owner_repo_form_the_readme_uses() =>
        AReview().Source.Should().Be("madoiscool/LTSP");

    [Fact]
    public void Version_shows_the_release_tag() =>
        AReview(tag: "v1.2").Version.Should().Be("v1.2");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Version_falls_back_when_the_source_publishes_no_tag(string? tag) =>
        AReview(tag: tag).Version.Should().Be("—", "SteamTools publishes per-timestamp assets with no usable tag");

    [Fact]
    public void ShortHash_is_the_first_sixteen_characters() =>
        AReview().ShortHash.Should().Be("e3e2d22e098ff3fb").And.HaveLength(16);

    [Fact]
    public void ShortHash_does_not_overrun_a_shorter_value() =>
        AReview(sha: "abc123").ShortHash.Should().Be("abc123");

    // ── Which checks are claimed ──────────────────────────────────────────────
    // The notice must never claim a check that did not run: the whole point is that a user can trust what
    // it says. Pinning and the digest are true by construction (the review is only built after both
    // passed); archive screening only applies to zips.

    [Fact]
    public void A_loose_binary_claims_pinning_and_digest_only()
    {
        var checks = AReview(screened: false).Checks;

        checks.Should().Contain(LuaToolsGui.Resources.Strings.Download_Notice_Check_Pinned)
              .And.Contain(LuaToolsGui.Resources.Strings.Download_Notice_Check_Digest)
              .And.NotContain(LuaToolsGui.Resources.Strings.Download_Notice_Check_Archive);
    }

    [Fact]
    public void An_archive_also_claims_the_screen() =>
        AReview(screened: true).Checks.Should()
            .Contain(LuaToolsGui.Resources.Strings.Download_Notice_Check_Archive);

    [Fact]
    public void Body_carries_the_facts_a_user_would_check_against_the_release()
    {
        var body = AReview(files: 3).Body;

        body.Should().Contain("winmm.dll").And.Contain("v1.2").And.Contain("3")
            .And.Contain("e3e2d22e098ff3fb");
        body.Should().NotContain(Sha, "the full 64-character digest does not belong in a toast");
    }

    [Fact]
    public void Title_names_the_source() =>
        AReview().Title.Should().Contain("madoiscool/LTSP");

    // ── The gate itself ───────────────────────────────────────────────────────

    [Fact]
    public async Task Proceeds_when_there_is_no_presenter() =>
        (await new DownloadNotice().ReviewAsync(AReview())).Should()
            .BeTrue("headless installs and silent auto-update have no window; the notice is advisory");

    [Fact]
    public async Task Stops_when_the_user_cancels()
    {
        var notice = new DownloadNotice { Present = (_, _) => Task.FromResult(false) };

        (await notice.ReviewAsync(AReview())).Should().BeFalse();
    }

    [Fact]
    public async Task Proceeds_when_the_user_lets_the_window_lapse()
    {
        var notice = new DownloadNotice { Present = (_, _) => Task.FromResult(true) };

        (await notice.ReviewAsync(AReview())).Should().BeTrue();
    }

    [Fact]
    public async Task A_broken_presenter_does_not_block_the_install()
    {
        // Deliberately fail-OPEN, unlike every other check here. A UI fault must not become a functional
        // outage: the decisions that protect the user already ran and already refused what they could not
        // prove. If this ever flips to false, a toast bug silently disables Mode and Plugin installs.
        var notice = new DownloadNotice { Present = (_, _) => throw new InvalidOperationException("no presenter") };

        (await notice.ReviewAsync(AReview())).Should().BeTrue();
    }

    [Fact]
    public async Task An_already_cancelled_token_stops_before_showing_anything()
    {
        bool shown = false;
        var notice = new DownloadNotice { Present = (_, _) => { shown = true; return Task.FromResult(true); } };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        (await notice.ReviewAsync(AReview(), cts.Token)).Should().BeFalse();
        shown.Should().BeFalse();
    }

    [Fact]
    public void The_grace_period_is_long_enough_to_read_and_short_enough_not_to_nag() =>
        DownloadNotice.GracePeriod.Should().BeGreaterThan(TimeSpan.FromSeconds(3))
            .And.BeLessThan(TimeSpan.FromSeconds(15),
                "a Cancel button nobody has time to press is decoration, and a long wait becomes a prompt");
}
