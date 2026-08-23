namespace LuaToolsGui.Services;

/// <summary>What actually happened when the Play button was pressed. Distinct cases rather than a bool so
/// the UI can say something useful — "Steam could not be found" and "that game id is not valid" are
/// different problems with different fixes.</summary>
public enum SteamLaunchOutcome
{
    /// <summary>Handed to Steam to run.</summary>
    Launched,

    /// <summary>Handed to Steam's install dialog.</summary>
    Installing,

    /// <summary>Steam was not running and could not be started (not located, or it never came up).</summary>
    SteamUnavailable,

    /// <summary>The appid failed validation, so no URL was built and no process was started.</summary>
    InvalidAppId,

    /// <summary>The shell refused the URL — Steam not installed, or the protocol handler not registered.</summary>
    Failed,
}

/// <summary>
/// Carries out a <see cref="SteamLaunchPlan"/>: reads the two facts the policy needs, then starts Steam if
/// it has to and fires the protocol URL. All the I/O the Play button does lives here; the decision itself
/// is in <see cref="SteamLaunchPolicy"/>.
/// </summary>
public class SteamGameLauncher(SteamService steam, SteamLibraryService library)
{
    /// <summary>
    /// How long Steam gets to come up before the launch is abandoned.
    ///
    /// <para>
    /// A cold Steam start is slow — sign-in, library scan — and firing the protocol URL at a client that is
    /// still booting loses it silently, which reads to the user as "the button did nothing". So this waits
    /// rather than racing. It never blocks the UI: the whole method is awaited off the dispatcher and the
    /// caller's token cancels it.
    /// </para>
    /// </summary>
    private static readonly TimeSpan SteamStartTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Gap between checks while waiting for Steam's process to appear.</summary>
    private static readonly TimeSpan SteamPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Settle time after Steam's process appears, before the URL is sent.
    ///
    /// <para>
    /// The process existing is not the same as the client being able to service a <c>steam://</c> URL —
    /// steam.exe registers its IPC endpoint some seconds after start. Without this pause the URL lands in
    /// the gap and is dropped. Only paid when Steam had to be started; an already-running Steam skips it.
    /// </para>
    /// </summary>
    private static readonly TimeSpan SteamReadyDelay = TimeSpan.FromSeconds(3);

    /// <summary>Whether this game's files are on disk. <see cref="SteamGameInstallState.Unknown"/> when
    /// Steam itself is not located, since the library is unreadable rather than merely missing the game.</summary>
    public SteamGameInstallState GetInstallState(long appId)
    {
        if (!SteamProtocolUri.IsValidAppId(appId)) return SteamGameInstallState.Unknown;
        if (steam.EffectivePath is null) return SteamGameInstallState.Unknown;
        return library.GetInstallDir(appId) is not null
            ? SteamGameInstallState.Installed
            : SteamGameInstallState.NotInstalled;
    }

    /// <summary>
    /// Run or install a game, starting Steam first if it is not up.
    ///
    /// <para>
    /// Never throws for a Steam-side problem; the outcome is the return value. The one exception is
    /// cancellation by the caller, which propagates — a user who navigated away should not have a launch
    /// complete behind them.
    /// </para>
    /// </summary>
    public async Task<SteamLaunchOutcome> LaunchAsync(long appId, CancellationToken ct = default)
    {
        // Validate at the boundary, before any state is read or any process is touched. Everything below
        // this line can assume the id is one SteamProtocolUri will build a URL for.
        if (!SteamProtocolUri.IsValidAppId(appId)) return SteamLaunchOutcome.InvalidAppId;

        // Both inputs to the policy are blocking I/O — a library scan and a process enumeration — and this
        // is awaited straight off the dispatcher by the Play command, so gathering them inline would hitch
        // the UI on a slow or network-mounted library.
        var plan = await Task.Run(
            () => SteamLaunchPolicy.Decide(GetInstallState(appId), SteamService.IsSteamRunning()), ct);

        if (plan.StartSteamFirst && !await StartSteamAndWaitAsync(ct))
            return SteamLaunchOutcome.SteamUnavailable;

        if (SteamProtocolUri.For(plan.Intent, appId) is not { } url)
            return SteamLaunchOutcome.InvalidAppId;

        try
        {
            SteamService.OpenUrl(url);
        }
        catch (Exception ex)
        {
            // The exception message can carry the shell's own view of the path it tried, so it goes to the
            // log the user is already asked to send rather than into a toast. The URL itself is just an
            // appid and carries nothing sensitive.
            AppLog.Log($"steam launch: shell refused {url} ({ex.GetType().Name})");
            return SteamLaunchOutcome.Failed;
        }

        return plan.Intent == SteamLaunchIntent.Install
            ? SteamLaunchOutcome.Installing
            : SteamLaunchOutcome.Launched;
    }

    /// <summary>Start Steam and wait until its process is up and settled. False if it cannot be located or
    /// does not appear within <see cref="SteamStartTimeout"/>.</summary>
    private async Task<bool> StartSteamAndWaitAsync(CancellationToken ct)
    {
        // ConfigureAwait(false) throughout: without it every poll resumes on the dispatcher, so a cold
        // Steam start would run ~90 process enumerations on the UI thread over the timeout window.
        if (!await Task.Run(steam.StartSteam, ct).ConfigureAwait(false)) return false;

        var deadline = DateTimeOffset.UtcNow + SteamStartTimeout;
        while (!SteamService.IsSteamRunning())
        {
            if (DateTimeOffset.UtcNow >= deadline) return false;
            await Task.Delay(SteamPollInterval, ct).ConfigureAwait(false);
        }

        await Task.Delay(SteamReadyDelay, ct).ConfigureAwait(false);
        return true;
    }
}
