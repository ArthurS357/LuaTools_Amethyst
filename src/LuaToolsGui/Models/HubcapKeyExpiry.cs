using System.Globalization;

namespace LuaToolsGui.Models;

/// <summary>
/// What Hubcap's <c>api_key_expires_at</c> means for the user, as a value instead of a raw string.
///
/// <para>
/// The field was already arriving on <see cref="HubcapStats"/> and was already being rendered — but only
/// as a date glued to the end of the usage line ("3/25 today · expires 2026-08-24"). A date is not a
/// warning: the one moment it matters is the week before it passes, and that is exactly when it reads as
/// just another number on a card the user has stopped looking at. Turning it into a decision here is what
/// lets the Settings page raise it on its own, and lets that decision be tested without a view.
/// </para>
///
/// <para>
/// Hubcap sends the timestamp with NO timezone offset (<c>2026-08-24T22:17:22.453394</c>). Left to
/// <c>DateTimeOffset.TryParse</c>'s default that is read as the machine's local time, so the same key
/// expires at a different instant depending on where the user sits — up to 26 hours apart across the
/// extremes. Parsed as UTC instead, which is what the server means and what makes the answer the same
/// everywhere. The residual error is bounded by one day either way, against a seven-day threshold.
/// </para>
/// </summary>
/// <param name="ExpiresAt">The expiry instant, normalised to UTC.</param>
/// <param name="DaysRemaining">Whole days left; 0 means "sometime today", negative means already past.</param>
public readonly record struct HubcapKeyExpiry(DateTimeOffset ExpiresAt, int DaysRemaining)
{
    /// <summary>How close the expiry has to be before the user is told. A week is long enough that
    /// somebody who opens the app once over a weekend still sees it before the key dies.</summary>
    public const int WarnWithinDays = 7;

    /// <summary>The key's window has already closed. Hubcap will answer 401 for it.</summary>
    public bool HasExpired => DaysRemaining < 0;

    /// <summary>Worth putting on screen unprompted — expired, or about to be.</summary>
    public bool IsWarning => DaysRemaining <= WarnWithinDays;

    /// <summary>The expiry date for display. Invariant <c>yyyy-MM-dd</c> so it is unambiguous in every
    /// locale the app ships in, rather than 04/08 meaning two different days on two machines.</summary>
    public string ExpiresOn => ExpiresAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Read <c>api_key_expires_at</c>, or <c>null</c> when there is nothing trustworthy to read.
    ///
    /// <para>
    /// Null is returned for a missing, blank or unparseable value, and that null is the whole reason the
    /// return type is nullable: the caller must be able to say "no opinion" and show nothing. A failed
    /// stats call leaves <see cref="HubcapStats"/> null and never reaches here, so a dropped connection
    /// cannot manufacture a warning about a key that is perfectly healthy.
    /// </para>
    /// </summary>
    /// <param name="raw">The value straight off the wire.</param>
    /// <param name="now">The reference instant, passed in rather than read from the clock so the decision
    /// is a function of its inputs and can be tested at both sides of the threshold.</param>
    public static HubcapKeyExpiry? Evaluate(string? raw, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (!DateTimeOffset.TryParse(
                raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiry))
            return null;

        // Floor, not round: with 6 hours left the honest answer is "today" (0), not "tomorrow" (1). The
        // same floor puts anything already past on -1 or lower, which is what HasExpired reads.
        int days = (int)Math.Floor((expiry - now).TotalDays);
        return new HubcapKeyExpiry(expiry, days);
    }
}
