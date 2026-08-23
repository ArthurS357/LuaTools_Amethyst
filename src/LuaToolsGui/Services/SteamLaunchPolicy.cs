namespace LuaToolsGui.Services;

/// <summary>Whether a game's files are on disk. <see cref="Unknown"/> is a real third answer, not a
/// synonym for <see cref="NotInstalled"/> — it means the library could not be read at all (Steam not
/// located), which is a different situation from "read it, the game isn't there".</summary>
public enum SteamGameInstallState
{
    Installed,
    NotInstalled,
    Unknown,
}

/// <summary>What the Play button should actually do for a game.</summary>
public enum SteamLaunchIntent
{
    /// <summary>Hand the game to Steam to run (<c>steam://rungameid/</c>).</summary>
    Play,

    /// <summary>Open Steam's install dialog for it (<c>steam://install/</c>).</summary>
    Install,
}

/// <param name="Intent">Run the game, or put it in to download.</param>
/// <param name="StartSteamFirst">Steam is not up, so it has to be launched and ready before the
/// protocol URL is fired — a <c>steam://</c> URL sent at a dead client is silently dropped.</param>
public sealed record SteamLaunchPlan(SteamLaunchIntent Intent, bool StartSteamFirst);

/// <summary>
/// Decides what pressing Play does, given only what is already known: whether the game is on disk and
/// whether Steam is up. Pure — no process list, no filesystem, no registry. The adapter
/// (<see cref="SteamGameLauncher"/>) gathers those two facts and carries out the plan.
/// </summary>
public static class SteamLaunchPolicy
{
    /// <summary>
    /// The plan for one game.
    ///
    /// <para>
    /// <see cref="SteamGameInstallState.Unknown"/> resolves to <see cref="SteamLaunchIntent.Play"/> rather
    /// than to a refusal, because <c>steam://rungameid/</c> is self-correcting: Steam answers it with its
    /// own install prompt when the game is owned but absent, and with the store page when it is not owned.
    /// Refusing instead would strand the user behind a dead button in exactly the case where the app is the
    /// one that is unsure. Choosing <see cref="SteamLaunchIntent.Install"/> for Unknown would be the worse
    /// guess — it forces the install dialog in front of a game that may already be installed and ready.
    /// </para>
    /// </summary>
    public static SteamLaunchPlan Decide(SteamGameInstallState state, bool steamRunning) =>
        new(IntentFor(state), StartSteamFirst: !steamRunning);

    /// <summary>Which verb applies to a game, ignoring Steam's own state. Exists so the button's LABEL and
    /// the action it performs are read from the same rule — a VM that re-derived "say Install when not
    /// installed" separately is one edit away from promising one thing and doing the other.</summary>
    public static SteamLaunchIntent IntentFor(SteamGameInstallState state) =>
        state == SteamGameInstallState.NotInstalled ? SteamLaunchIntent.Install : SteamLaunchIntent.Play;
}

/// <summary>
/// Builds the <c>steam://</c> URLs this app hands to the shell, and refuses to build one for an appid it
/// cannot vouch for.
///
/// <para>
/// This is the security boundary for the Play button. The URL reaches <c>Process.Start</c> with
/// <c>UseShellExecute = true</c>, so whatever is in it is handed to the Windows shell to resolve — the
/// reason the appid is validated as a <see cref="long"/> in a bounded range rather than passed through as
/// a string. A validated <see cref="long"/> can only ever render as digits, so the interpolated result is
/// provably a well-formed <c>steam://</c> URL: there is no string an attacker could route in that survives
/// the parse into a number and still carries a quote, a space, a scheme change, or a second argument.
/// Accepting a string here and pattern-checking it afterwards would be the version of this that eventually
/// gets a bypass.
/// </para>
/// </summary>
public static class SteamProtocolUri
{
    /// <summary>
    /// Upper bound on a real appid, matching <see cref="SteamLinkParser"/>'s own guard. Ids above this are
    /// the composite 64-bit values <c>rungameid</c> uses for non-Steam shortcuts and mods, which are not
    /// appids — building a launch URL from one would target something other than the game on the card.
    /// </summary>
    private const long MaxAppId = 2_000_000_000;

    /// <summary>True for an appid this class will build a URL for. Zero and negatives are rejected: they
    /// are what an unparsed or defaulted id looks like, never a real game.</summary>
    public static bool IsValidAppId(long appId) => appId > 0 && appId < MaxAppId;

    /// <summary>Launch URL for a game, or null when <paramref name="appId"/> is not one this class will
    /// vouch for. Null means "do not start a process" — never fall back to a raw string.</summary>
    public static string? RunGame(long appId) =>
        IsValidAppId(appId) ? $"steam://rungameid/{appId}" : null;

    /// <summary>Install-dialog URL for a game, or null on an appid that fails validation.</summary>
    public static string? Install(long appId) =>
        IsValidAppId(appId) ? $"steam://install/{appId}" : null;

    /// <summary>The URL carrying out <paramref name="intent"/>, or null on an invalid appid.</summary>
    public static string? For(SteamLaunchIntent intent, long appId) => intent switch
    {
        SteamLaunchIntent.Play => RunGame(appId),
        SteamLaunchIntent.Install => Install(appId),
        _ => null,
    };
}
