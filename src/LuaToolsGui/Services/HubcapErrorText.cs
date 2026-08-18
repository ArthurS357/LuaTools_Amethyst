using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// User-facing wording for a failed Hubcap download, shared by the app's download flow and the headless
/// plugin pipeline so the two can't drift apart.
///
/// <para>
/// These strings are English-only, which is not new: they were hardcoded literals inside
/// <see cref="HubcapService"/> before the result type existed, and moving them here keeps that scope
/// unchanged rather than quietly adding three keys across 29 translation files. Promoting them to real
/// resource keys is a worthwhile follow-up, not part of this change.
/// </para>
/// </summary>
internal static class HubcapErrorText
{
    /// <summary>Wording for a non-Ok download result. Ok has no message, so it is not accepted here.</summary>
    public static string Describe<T>(HubcapResult<T> result) => result switch
    {
        HubcapResult<T>.Unauthorized => "Your Hubcap key is invalid or expired.",
        HubcapResult<T>.RateLimited { RetryAfter: { } wait } =>
            $"Your Hubcap daily limit has been reached. Try again in about {Humanize(wait)}.",
        HubcapResult<T>.RateLimited => "Your Hubcap daily limit has been reached.",
        HubcapResult<T>.NotFound => "No Hubcap manifest is available for this app.",
        HubcapResult<T>.Failed f => $"Hubcap download failed ({(int)f.Status}).",
        // Previously indistinguishable from the line above, which reported an HTTP status for something
        // that never got one.
        HubcapResult<T>.Offline => "Couldn't reach Hubcap. Check your connection and try again.",
        _ => "Hubcap download failed.",
    };

    private static string Humanize(TimeSpan wait) => wait switch
    {
        { TotalMinutes: < 1 } => $"{Math.Max(1, (int)wait.TotalSeconds)} seconds",
        { TotalHours: < 1 } => $"{(int)wait.TotalMinutes} minutes",
        _ => $"{(int)wait.TotalHours} hours",
    };
}
