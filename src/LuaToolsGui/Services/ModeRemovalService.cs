using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// Uninstalls the active Mode: takes the files it put in the Steam root back out, then returns the app to
/// "no mode selected".
///
/// <para>
/// A thin orchestrator over the two halves that already exist — <see cref="UnlockerService"/> owns which
/// Mode is active, <see cref="PluginRemovalService"/> owns removal — and it is a separate type for a
/// concrete reason: <see cref="PluginRemovalService"/> already depends on <see cref="UnlockerService"/> so
/// that an active Mode's proxy DLLs are never deleted out from under it. Putting this method on either of
/// them closes that into a dependency cycle the container rejects at build time.
/// </para>
///
/// <para>
/// <b>The selection is cleared only after the files are actually gone.</b> Clearing first would leave a
/// failed uninstall reporting "no mode" while the Mode's DLLs are still being loaded by <c>steam.exe</c>,
/// and the record that says which files those are would no longer be reachable from the UI.
/// </para>
/// </summary>
public sealed class ModeRemovalService(UnlockerService unlocker, PluginRemovalService removal)
{
    /// <summary>The active Mode, or null when none is selected.</summary>
    public UnlockerMode? ActiveMode => unlocker.SelectedMode;

    /// <summary>
    /// Whether Uninstall has something provable to work from — what gates the button.
    ///
    /// <para>
    /// Gated on the install RECORD, not on the files being present. A Mode adopted by
    /// <see cref="UnlockerService.DetectActiveModeAsync"/> matched a published hash, which says what those
    /// DLLs are and nothing at all about who placed them; removing them on that basis is how a copy some
    /// other tool installed gets taken away. Those users see the no-record hint instead of a dead button.
    /// </para>
    /// </summary>
    public bool CanUninstall =>
        ActiveMode is { } mode && removal.HasRecordFor(PluginIds.ForMode(mode));

    /// <summary>
    /// Remove the active Mode's recorded files and deselect it. Idempotent: with no Mode active, or
    /// nothing recorded for it, this reports a no-op rather than failing.
    /// </summary>
    public async Task<PluginRemovalOutcome> UninstallActiveModeAsync(CancellationToken ct = default)
    {
        if (ActiveMode is not { } mode)
            return new PluginRemovalOutcome([], [], [], null, SteamStopped: false, Error: null)
            {
                NothingRecorded = true,
            };

        var outcome = await removal.RemoveAsync(PluginIds.ForMode(mode), stopSteam: true, ct);

        // A removal that kept every file because something else still claims it is still a removal: the
        // record is gone, this Mode no longer owns anything, and leaving it selected would show the user
        // an active Mode with no install behind it. Only a real failure — or nothing recorded to begin
        // with — leaves the selection alone.
        if (!outcome.Failed && !outcome.NothingRecorded) unlocker.ClearSelectedMode();

        return outcome;
    }
}
