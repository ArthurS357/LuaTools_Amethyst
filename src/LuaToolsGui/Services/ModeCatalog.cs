using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// Which Modes the page offers, and in what order — decided from the definitions alone.
///
/// <para>
/// Pure, and separate from <see cref="UnlockerService"/> for that reason: the list it filters is the same
/// one install, status and removal all key off, so "is this mode offered?" must never be answerable only
/// by looking at rendered UI. The Mode page calls this and renders the result in order.
/// </para>
/// </summary>
public static class ModeCatalog
{
    /// <summary>
    /// The definitions to show, in page order.
    /// </summary>
    /// <param name="all">Every known definition (<see cref="UnlockerService.Modes"/>).</param>
    /// <param name="active">
    /// The active mode, or null. A RETIRED mode that is active is still offered — that card is the only
    /// route to its Uninstall button, and <see cref="UnlockerService.InstallAsync"/> refuses to install it
    /// regardless, so showing it cannot put a user back onto a backend that is no longer maintained.
    /// </param>
    public static IReadOnlyList<ModeDefinition> Offered(
        IEnumerable<ModeDefinition> all, UnlockerMode? active)
    {
        ArgumentNullException.ThrowIfNull(all);

        return
        [
            .. all
                // CloudRedirect is TEMPORARILY hidden (the mode is currently broken) and is expected back,
                // so it is hidden unconditionally rather than retired — including when it is active.
                .Where(d => d.Mode != UnlockerMode.CloudRedirect)
                .Where(d => !d.Retired || active == d.Mode)
        ];
    }
}
