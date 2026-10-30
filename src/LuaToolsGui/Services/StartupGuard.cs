namespace LuaToolsGui.Services;

/// <summary>What the user picked when told Steam is already running.</summary>
public enum StartupGuardChoice { Restart, Exit, ContinueAnyway }

/// <summary>What startup must do about a Steam that was already up.</summary>
public enum StartupGuardOutcome
{
    /// <summary>Steam is not running; nothing to ask.</summary>
    SteamNotRunning,
    /// <summary>Close Steam and bring it back (the user asked for it by name).</summary>
    Restart,
    /// <summary>Quit LuaTools.</summary>
    Exit,
    /// <summary>Leave Steam alone and warn that the hook may not attach.</summary>
    ContinueAnyway,
}

/// <summary>
/// The hook only attaches to a Steam that LuaTools started (or that was closed when it launched), so a Steam
/// that is already up is asked about instead of being closed silently. The decision is split from the
/// dialog and the process lookup so a build can check it without a real Steam or a real window.
/// </summary>
internal static class StartupGuard
{
    public static async Task<StartupGuardOutcome> RunAsync(
        Func<bool> isSteamRunning, Func<Task<StartupGuardChoice>> ask)
    {
        ArgumentNullException.ThrowIfNull(isSteamRunning);
        ArgumentNullException.ThrowIfNull(ask);

        if (!isSteamRunning()) return StartupGuardOutcome.SteamNotRunning;

        return await ask() switch
        {
            StartupGuardChoice.Restart => StartupGuardOutcome.Restart,
            StartupGuardChoice.ContinueAnyway => StartupGuardOutcome.ContinueAnyway,
            _ => StartupGuardOutcome.Exit,
        };
    }
}
