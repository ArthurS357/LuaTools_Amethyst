namespace LuaToolsGui.Services;

/// <summary>How a request to stop Steam ended.</summary>
public enum SteamStopOutcome
{
    /// <summary>Steam was not running. Nothing was done, and nothing needs undoing.</summary>
    NotRunning,

    /// <summary>Steam closed itself after being asked. The client got to flush its state, which is the
    /// difference between a clean exit and the "Steam did not shut down correctly" dialog next launch.</summary>
    ClosedGracefully,

    /// <summary>Steam ignored the request and was terminated. Correct when the caller is about to rewrite
    /// files Steam holds open, but it costs the client a clean shutdown, so it is never the first move.</summary>
    Killed,

    /// <summary>Steam is still up: it did not close in time and the caller did not authorise force. The
    /// caller must NOT proceed to touch Steam's files.</summary>
    StillRunning,
}

/// <summary>
/// The one Steam process this app cares about, behind an interface.
///
/// <para>
/// Exists so the shutdown POLICY can be tested. <see cref="System.Diagnostics.Process"/> is sealed with a
/// static lookup, so the only alternative is testing "does it really kill Steam" by killing Steam — which
/// nobody runs twice, so the logic ends up untested. The policy is the part that has decisions in it.
/// </para>
/// </summary>
public interface ISteamProcess
{
    /// <summary>Block until the process exits or the timeout expires. True if it exited.</summary>
    bool WaitForExit(TimeSpan timeout);

    /// <summary>Terminate the process and its children.</summary>
    void Kill();

    /// <summary>Whether it has already gone.</summary>
    bool HasExited { get; }
}

/// <summary>
/// Decides HOW Steam is stopped: ask first, terminate only if allowed and only if asking failed.
///
/// <para>
/// The app used to go straight to <c>Kill(entireProcessTree: true)</c> on every path — plugin install,
/// mode switch, onboarding. That works, and it also denies the Steam client any chance to flush its
/// config, which is what produces a "Steam did not shut down correctly" scan on the next launch and, at
/// worst, loses whatever it had not written yet. Asking costs a few seconds in the common case and gives
/// the client the exit it is built for.
/// </para>
/// </summary>
public static class SteamShutdown
{
    /// <summary>How long Steam gets to close on its own. Steam takes a few seconds to flush and drop its
    /// connections; ten is comfortably past that without being a stall the user reads as a hang.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Stop every Steam process, gracefully if it will and forcibly only when <paramref name="allowKill"/>
    /// says so.
    /// </summary>
    /// <param name="processes">The running Steam processes. Empty means nothing to do.</param>
    /// <param name="requestShutdown">
    /// Asks Steam to exit. This is a CLIENT-LEVEL request, not a window message, and the distinction is
    /// the whole reason this is a parameter instead of a call to <c>CloseMainWindow</c>.
    /// <para>
    /// Closing Steam's window does not close Steam — by default it minimises to the tray, so the process
    /// stays alive holding every file open. Measured: the app asked, waited the full grace period, and
    /// then had to prompt the user to force-kill a Steam that was working perfectly and had simply been
    /// hidden. Steam's documented clean exit is <c>steam.exe -shutdown</c>, which is what the caller
    /// supplies here.
    /// </para>
    /// </param>
    /// <param name="allowKill">Whether termination is authorised. False makes this advisory: it asks, and
    /// reports <see cref="SteamStopOutcome.StillRunning"/> rather than forcing — which is what lets the UI
    /// put the decision to the user instead of taking it for them.</param>
    /// <param name="grace">How long to wait for a graceful exit before escalating.</param>
    public static SteamStopOutcome Stop(
        IReadOnlyList<ISteamProcess> processes, Action requestShutdown, bool allowKill, TimeSpan grace)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(requestShutdown);

        var alive = processes.Where(p => !p.HasExited).ToList();
        if (alive.Count == 0) return SteamStopOutcome.NotRunning;

        // One request for the whole client, not one per process: `-shutdown` addresses Steam as a product
        // and brings its helpers down with it. Issuing it per process would just spawn duplicate helpers.
        TryRequestShutdown(requestShutdown);

        // The grace period is shared, not per-process: the deadline is how long the USER waits, and it
        // does not get longer because Steam happens to have spawned more helpers.
        var deadline = DateTime.UtcNow + grace;
        foreach (var process in alive)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            TryWait(process, remaining);
        }

        var stubborn = alive.Where(p => !HasGone(p)).ToList();
        if (stubborn.Count == 0) return SteamStopOutcome.ClosedGracefully;

        if (!allowKill) return SteamStopOutcome.StillRunning;

        foreach (var process in stubborn)
        {
            TryKill(process);
            TryWait(process, TimeSpan.FromSeconds(5));
        }

        // Report what is TRUE, not what was attempted. A kill can fail — a process owned by another user,
        // or one wedged in a driver call — and a caller told "Killed" would go on to rewrite files Steam
        // still has open.
        return alive.Any(p => !HasGone(p)) ? SteamStopOutcome.StillRunning : SteamStopOutcome.Killed;
    }

    // Every probe below is wrapped: a process can exit between any two calls, and the framework signals
    // that by throwing from members that were fine a moment earlier. A race with the thing we are trying
    // to stop is a success, not an error.
    private static bool HasGone(ISteamProcess process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static void TryRequestShutdown(Action requestShutdown)
    {
        try { requestShutdown(); }
        catch { /* Steam not launchable from where we think it is — the wait and escalation still apply */ }
    }

    private static void TryWait(ISteamProcess process, TimeSpan timeout)
    {
        try { process.WaitForExit(timeout); }
        catch { /* gone, or not waitable — HasGone is the authority */ }
    }

    private static void TryKill(ISteamProcess process)
    {
        try { process.Kill(); }
        catch { /* access denied or already gone; the recheck below reports the truth */ }
    }
}
