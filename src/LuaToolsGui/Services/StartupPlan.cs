namespace LuaToolsGui.Services;

/// <summary>
/// What the app does to Steam on launch, decided once from the state it finds.
///
/// <para>
/// The startup used to have no plan at all: Steam was left running, the first-run overlay appeared over
/// it, and Steam was stopped and restarted deep inside whichever installer happened to run. A returning
/// user with everything already installed saw a sequence of prompts that led nowhere, which is the
/// "confusing, and I do not know what to do" the flow is being rebuilt for.
/// </para>
///
/// <para>
/// A record rather than branching inside the startup method, because these three answers are the whole
/// decision and they are worth being able to test without an Application, an installer, or a real Steam.
/// </para>
/// </summary>
/// <param name="CloseSteam">Stop Steam before doing anything else. Every install path rewrites files
/// Steam holds open, and doing it up front means one stop instead of one per installer.</param>
/// <param name="ShowSetup">Show the first-run setup overlay.</param>
/// <param name="OfferReopen">Offer to start Steam again once the app is done. Only ever offered for a
/// Steam this app closed — reopening one the user had already quit would be the app deciding something
/// it was not asked to decide.</param>
public readonly record struct StartupPlan(bool CloseSteam, bool ShowSetup, bool OfferReopen)
{
    /// <summary>
    /// Work out the launch sequence.
    /// </summary>
    /// <param name="steamRunning">Whether a Steam client is up right now.</param>
    /// <param name="setupAlreadyDone">The first-run overlay has been completed before.</param>
    /// <param name="toolsInstalled">The unlocker mode and the plugin are both already in place.</param>
    /// <remarks>
    /// Sign-in deliberately does NOT feed into this. Browsing as a guest is supported everywhere else in
    /// the app, so gating setup on an account would put the installer in front of a guest whose tools are
    /// already installed and who has nothing to do there — the exact nag this is removing.
    /// </remarks>
    public static StartupPlan Decide(bool steamRunning, bool setupAlreadyDone, bool toolsInstalled)
    {
        // Setup earns its place only when there is something to set up. Someone who has been through it,
        // or who simply already has the tools (a reinstall, a second machine, a dev box), goes straight
        // through — for them the whole launch is "Steam went down, do you want it back".
        bool showSetup = !setupAlreadyDone && !toolsInstalled;

        return new StartupPlan(
            CloseSteam: steamRunning,
            ShowSetup: showSetup,
            // Not offered when setup runs: the setup flow finishes by starting Steam itself, and two
            // things racing to launch Steam is how you end up with the "Steam is already running" dialog.
            OfferReopen: steamRunning && !showSetup);
    }

    /// <summary>True when the launch has nothing to say to the user at all — no Steam to stop, no setup to
    /// run. The app should come up silently rather than announcing that it did nothing.</summary>
    public bool IsSilent => !CloseSteam && !ShowSetup;
}
