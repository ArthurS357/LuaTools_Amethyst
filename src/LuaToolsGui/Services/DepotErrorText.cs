using System.Globalization;

namespace LuaToolsGui.Services;

/// <summary>
/// Turns a <see cref="DepotFailure"/> into something a user can act on.
/// </summary>
/// <remarks>
/// Kept out of <see cref="DepotDownloaderService"/> so the service returns a structured outcome rather than
/// a pre-rendered sentence — the same split <c>HubcapErrorText</c> and <c>RemovalMessage</c> already use.
/// A service that formats UI strings cannot be tested for WHICH failure it hit, only for what it happened
/// to say, and it drags the resource table into every unit test.
/// </remarks>
internal static class DepotErrorText
{
    /// <summary>
    /// The message for a failed depot job.
    /// </summary>
    /// <param name="detail">
    /// For <see cref="DepotFailure.NotEnoughSpace"/>, "needed/free" in bytes; for
    /// <see cref="DepotFailure.DownloaderFailed"/>, the child process's last line (already redacted by
    /// <see cref="LogSanitizer"/> at the source). Null otherwise.
    /// </param>
    public static string Describe(DepotFailure failure, long? depotId, string? detail) => failure switch
    {
        DepotFailure.None => "",

        // Not "something went wrong": the feature is switched off in this build because AppConfig carries
        // no usable pin, and the user needs to know that rather than retry.
        DepotFailure.ToolNotPinned => Resources.Strings.Depot_Err_ToolNotPinned,

        DepotFailure.ToolUnavailable => Resources.Strings.Depot_Err_Tool,
        DepotFailure.SteamNotFound => Resources.Strings.Depot_Err_SteamNotFound,
        DepotFailure.NoKeys => Resources.Strings.Depot_Err_NoKeys,

        DepotFailure.NoKeyForDepot =>
            string.Format(CultureInfo.CurrentCulture, Resources.Strings.Depot_Err_NoKeyFor, depotId),
        DepotFailure.BadKey =>
            string.Format(CultureInfo.CurrentCulture, Resources.Strings.Depot_Err_BadKey, depotId),

        DepotFailure.NoManifest => Resources.Strings.Depot_Err_NoManifest,
        DepotFailure.SignInRequired => Resources.Strings.Depot_Err_SignIn,

        DepotFailure.NotEnoughSpace => DescribeSpace(detail),

        DepotFailure.DownloaderFailed =>
            string.Format(CultureInfo.CurrentCulture, Resources.Strings.Depot_Err_Failed,
                depotId, detail ?? ""),

        _ => Resources.Strings.Depot_Err_Tool,
    };

    /// <summary>
    /// "Needs X, Y free". The two numbers arrive as raw bytes so the service never has to know how this
    /// build formats sizes; a malformed pair degrades to the generic wording rather than showing "/".
    /// </summary>
    private static string DescribeSpace(string? detail)
    {
        if (detail?.Split('/') is [var neededText, var freeText]
            && long.TryParse(neededText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long needed)
            && long.TryParse(freeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long free))
        {
            return string.Format(CultureInfo.CurrentCulture, Resources.Strings.Depot_Err_NoSpace,
                ByteSize.Format(needed), ByteSize.Format(free));
        }

        return string.Format(CultureInfo.CurrentCulture, Resources.Strings.Depot_Err_NoSpace, "—", "—");
    }
}
