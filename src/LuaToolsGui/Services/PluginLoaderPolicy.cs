namespace LuaToolsGui.Services;

/// <summary>What the Plugin page's loader row can say about the installed <c>winmm.dll</c>. Exactly one of
/// these holds at any moment — which is the point of it being an enum rather than four booleans, because
/// four booleans is how the row ended up painting two icons at once.</summary>
public enum PluginLoaderState
{
    /// <summary>No loader slot on disk.</summary>
    NotInstalled,

    /// <summary>Installed, and its sha256 matches the active source's published digest.</summary>
    UpToDate,

    /// <summary>Installed, and it does NOT match a digest we actually saw. A real "there is a newer one".</summary>
    OutOfDate,

    /// <summary>
    /// Installed, and nothing came back to compare it against — offline, or the active source published
    /// nothing installable.
    ///
    /// <para>
    /// The state that did not exist before, and whose absence was a bug. Both of those cases return
    /// <c>DllMatches: false</c> from <see cref="PluginInstallerService.GetStatusAsync"/> — not because the
    /// file is stale, but because there was no digest to compare it to — so the page read them as
    /// <see cref="OutOfDate"/> and painted an amber warning next to the error box saying the source could
    /// not be reached. The file on disk may well be current; the honest answer is that it is installed and
    /// that is all that is known.
    /// </para>
    /// </summary>
    Unverifiable,
}

/// <summary>
/// What the Plugin page's status card SAYS, derived from one <see cref="PluginStatus"/>. Pure: no disk, no
/// network, no clock, no view-model. <see cref="ViewModels.PluginViewModel"/> fetches the status and hands
/// it here.
///
/// <para>
/// This exists as its own type for the same reason <see cref="PluginSourceResolver"/> and
/// <see cref="ActiveBackendPolicy"/> do. The rule it holds is small but it is easy to state WRONGLY, and
/// the wrong statement is silent: <c>UpdateAvailable</c> and <c>DllMatches</c> both come back
/// <c>false</c> when everything is current AND when nothing could be reached, so a caller that reads them
/// without also reading <c>Offline</c> and <c>ActiveSourceProblem</c> will confidently report a fact it
/// never established. Inline in a view-model that needs a live service to construct, that rule could not be
/// tested at all; here it is a function over a record.
/// </para>
/// </summary>
public static class PluginLoaderPolicy
{
    /// <summary>The plugin counts as installed only with BOTH halves present — the frontend the injector
    /// reads and a loader slot steam.exe can pick up. Either alone does nothing.</summary>
    public static bool IsInstalled(PluginStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.FrontendInstalled && status.DllInstalled;
    }

    /// <summary>
    /// Whether the active source actually told us what it publishes.
    ///
    /// <para>
    /// The single question every claim on this page depends on. False when offline, and false when the
    /// active source published nothing installable — deliberately the same answer for both, because the
    /// consequence is identical: there is no release to compare anything against, so nothing may be
    /// asserted about how current the install is. Note this asks about the ACTIVE source only; another
    /// source being healthy says nothing here, exactly as nothing falls back to it.
    /// </para>
    /// </summary>
    public static bool LatestKnown(PluginStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return !status.Offline && status.ActiveSourceProblem is PluginSourceRejection.None;
    }

    /// <summary>The loader row's state. Order matters: "is it even there" outranks "is it current", and
    /// "could we check" outranks the answer, so an unanswerable question is never resolved into an
    /// answer.</summary>
    public static PluginLoaderState Loader(PluginStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (!status.DllInstalled) return PluginLoaderState.NotInstalled;
        if (!LatestKnown(status)) return PluginLoaderState.Unverifiable;
        return status.DllMatches ? PluginLoaderState.UpToDate : PluginLoaderState.OutOfDate;
    }

    /// <summary>
    /// Whether the green "Up to date" pill may be shown on the version line.
    ///
    /// <para>
    /// <c>UpdateAvailable</c> alone is not enough and that is the whole trap: it is false both when there
    /// is nothing to update and when there was nothing to look for. Only the first of those is "up to
    /// date". Without <see cref="LatestKnown"/> the page showed the pill next to its own error message
    /// saying the source could not be reached.
    /// </para>
    /// </summary>
    public static bool ShowUpToDate(PluginStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return IsInstalled(status) && LatestKnown(status) && !status.UpdateAvailable;
    }
}
