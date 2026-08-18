using LuaToolsGui.Models;
using LuaToolsGui.Resources;

namespace LuaToolsGui.Services;

/// <summary>
/// User-facing wording for a failed Hubcap download, shared by the app's download flow and the headless
/// plugin pipeline so the two can't drift apart.
///
/// <para>
/// These began as hardcoded English literals inside <see cref="HubcapService"/> and are now real resource
/// keys, so the one part of the Hubcap flow the user actually reads is translatable like the rest of the
/// app. The wait phrasing is split into its own keys rather than composed from a number and a bare noun:
/// languages that inflect the unit by count cannot be served by gluing "30" to "minutes".
/// </para>
/// </summary>
internal static class HubcapErrorText
{
    /// <summary>Wording for a non-Ok download result. Ok has no message, so it is not accepted here.</summary>
    public static string Describe<T>(HubcapResult<T> result) => result switch
    {
        HubcapResult<T>.Unauthorized => Strings.Hubcap_Err_KeyRejected,
        HubcapResult<T>.RateLimited { RetryAfter: { } wait } =>
            string.Format(Strings.Hubcap_Err_DailyLimitRetry, Humanize(wait)),
        HubcapResult<T>.RateLimited => Strings.Hubcap_Err_DailyLimit,
        HubcapResult<T>.NotFound => Strings.Hubcap_Err_NoManifest,
        HubcapResult<T>.Failed f => string.Format(Strings.Hubcap_Err_Status, (int)f.Status),
        // Previously indistinguishable from the line above, which reported an HTTP status for something
        // that never got one.
        HubcapResult<T>.Offline => Strings.Hubcap_Err_Unreachable,
        _ => Strings.Hubcap_Err_Generic,
    };

    private static string Humanize(TimeSpan wait) => wait switch
    {
        { TotalMinutes: < 1 } => string.Format(Strings.Hubcap_Wait_Seconds, Math.Max(1, (int)wait.TotalSeconds)),
        { TotalHours: < 1 } => string.Format(Strings.Hubcap_Wait_Minutes, (int)wait.TotalMinutes),
        _ => string.Format(Strings.Hubcap_Wait_Hours, (int)wait.TotalHours),
    };
}
