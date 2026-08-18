using AwesomeAssertions;
using LuaToolsGui.Models;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers the decision behind the Settings page's key-expiry warning.
///
/// <para>
/// The warning is the one piece of Hubcap UI that appears without the user asking for it, so both ways of
/// being wrong are expensive: staying silent through the last week of a key's life is the bug it exists to
/// fix, and crying wolf about a healthy key is what teaches people to ignore it. Both directions are
/// pinned here, at the boundary rather than near it.
/// </para>
/// </summary>
public class HubcapKeyExpiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    // ── Nothing to say ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("never")]
    [InlineData("2026-13-45T99:99:99")]
    public void No_usable_date_means_no_opinion(string? raw) =>
        HubcapKeyExpiry.Evaluate(raw, Now).Should().BeNull(
            "a warning the app cannot substantiate is worse than none — see the null contract on Evaluate");

    // ── The threshold ─────────────────────────────────────────────────────────

    [Fact]
    public void A_date_further_out_than_the_window_is_not_a_warning()
    {
        var expiry = HubcapKeyExpiry.Evaluate("2026-09-30T12:00:00", Now);

        expiry.Should().NotBeNull();
        expiry!.Value.IsWarning.Should().BeFalse();
        expiry.Value.HasExpired.Should().BeFalse();
        expiry.Value.DaysRemaining.Should().Be(43);
    }

    [Fact]
    public void Exactly_the_window_still_warns()
    {
        // 7.0 days out. The threshold is inclusive, so this is the last day that must warn; one hour later
        // than this instant and it would be 6 days, which warns for a different reason. Testing the far
        // side of the boundary is the only way to know the comparison is <= and not <.
        var expiry = HubcapKeyExpiry.Evaluate("2026-08-25T12:00:00", Now);

        expiry!.Value.DaysRemaining.Should().Be(HubcapKeyExpiry.WarnWithinDays);
        expiry.Value.IsWarning.Should().BeTrue();
        expiry.Value.HasExpired.Should().BeFalse();
    }

    [Fact]
    public void One_day_past_the_window_stays_quiet()
    {
        var expiry = HubcapKeyExpiry.Evaluate("2026-08-26T12:00:00", Now);

        expiry!.Value.DaysRemaining.Should().Be(HubcapKeyExpiry.WarnWithinDays + 1);
        expiry.Value.IsWarning.Should().BeFalse();
    }

    // ── Rounding at the near end ──────────────────────────────────────────────

    [Fact]
    public void Hours_left_reads_as_zero_days_not_one()
    {
        // Six hours out. Rounding up would render "expires in 1 day" for a key that dies this afternoon.
        var expiry = HubcapKeyExpiry.Evaluate("2026-08-18T18:00:00", Now);

        expiry!.Value.DaysRemaining.Should().Be(0);
        expiry.Value.IsWarning.Should().BeTrue();
        expiry.Value.HasExpired.Should().BeFalse("it has not passed yet");
    }

    [Fact]
    public void An_instant_already_past_reads_as_expired()
    {
        // One hour ago. Anything below zero must land on HasExpired, not on "0 days left".
        var expiry = HubcapKeyExpiry.Evaluate("2026-08-18T11:00:00", Now);

        expiry!.Value.HasExpired.Should().BeTrue();
        expiry.Value.IsWarning.Should().BeTrue();
        expiry.Value.DaysRemaining.Should().BeNegative();
    }

    [Fact]
    public void A_long_dead_key_is_still_only_expired() =>
        HubcapKeyExpiry.Evaluate("2020-01-01T00:00:00", Now)!.Value
            .HasExpired.Should().BeTrue();

    // ── Timezone handling ─────────────────────────────────────────────────────

    [Fact]
    public void An_offsetless_timestamp_is_read_as_utc()
    {
        // Hubcap sends exactly this shape — no offset, microsecond precision. Read as local time it would
        // land up to 26 hours away from this, which is enough to flip the answer on the boundary day and to
        // make two users disagree about the same key.
        var expiry = HubcapKeyExpiry.Evaluate("2026-08-24T22:17:22.453394", Now);

        expiry!.Value.ExpiresAt.Offset.Should().Be(TimeSpan.Zero);
        expiry.Value.ExpiresAt.Should().Be(new DateTimeOffset(2026, 8, 24, 22, 17, 22, 453, TimeSpan.Zero)
            .AddTicks(3940));
        expiry.Value.IsWarning.Should().BeTrue("it is six days out");
    }

    [Fact]
    public void An_explicit_offset_is_honoured_and_normalised()
    {
        // Not the shape Hubcap sends today. If it ever starts sending one, AssumeUniversal must step aside
        // rather than overwrite it — that is the difference between AssumeUniversal and AssumeLocal here.
        var expiry = HubcapKeyExpiry.Evaluate("2026-08-25T00:00:00+03:00", Now);

        expiry!.Value.ExpiresAt.Should().Be(new DateTimeOffset(2026, 8, 24, 21, 0, 0, TimeSpan.Zero));
        expiry.Value.ExpiresAt.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void The_display_date_is_locale_independent() =>
        HubcapKeyExpiry.Evaluate("2026-08-24T22:17:22.453394", Now)!.Value
            .ExpiresOn.Should().Be("2026-08-24",
                "04/08 means two different days depending on where the user sits");
}
