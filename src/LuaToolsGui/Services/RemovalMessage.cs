namespace LuaToolsGui.Services;

/// <summary>
/// Turns a <see cref="PluginRemovalOutcome"/> into the one sentence a user is shown.
///
/// <para>
/// Shared rather than duplicated per card: the Mode page's Uninstall button and its AmethystTool card run
/// the same removal through the same policy, and two hand-written descriptions of one outcome is how
/// "removed" and "kept, because something else needs it" end up worded differently depending on which
/// button was pressed.
/// </para>
/// </summary>
public static class RemovalMessage
{
    /// <summary>
    /// One sentence for what the removal did.
    ///
    /// <para>
    /// The shared-files case gets its own message on purpose: a user told "removed" who then finds
    /// <c>dwmapi.dll</c> still sitting next to <c>steam.exe</c> has been misinformed, and the reason it
    /// stayed is exactly the thing worth saying.
    /// </para>
    /// </summary>
    public static string Describe(PluginRemovalOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome.Failed)
            return string.Format(Resources.Strings.Removal_Toast_Failed, outcome.Error);
        if (outcome.NothingRecorded)
            return Resources.Strings.Removal_Toast_NoRecord;
        if (outcome.SharedKept.Count > 0)
            return string.Format(Resources.Strings.Removal_Toast_SharedKept,
                string.Join(", ", outcome.SharedKept));
        if (outcome.NothingToDo)
            return Resources.Strings.Removal_Toast_NothingToDo;

        return outcome.SteamStopped
            ? Resources.Strings.Removal_Toast_RemovedSteamStopped
            : Resources.Strings.Removal_Toast_Removed;
    }
}
