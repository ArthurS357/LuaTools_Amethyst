namespace LuaToolsGui.Services;

/// <summary>
/// The outcome of forgetting a game, as a closed set of cases rather than a bare <c>bool</c>.
///
/// <para>
/// A game reaches the Depots list from three independent places — a live <c>&lt;appid&gt;.lua</c>, loose
/// <c>&lt;appid&gt;_&lt;buildid&gt;.lua</c> files, and a vault folder of captured variants — and any one of
/// them is enough to keep it there. "It worked" is therefore not a single fact: the user needs to know
/// whether anything was actually removed, because a game that reappears after a removal that reported
/// success is indistinguishable from a broken feature.
/// </para>
///
/// <para>
/// Sealed by a private constructor, so a <c>switch</c> over the cases is exhaustive and stays exhaustive.
/// </para>
/// </summary>
public abstract record GameRemoval
{
    private GameRemoval() { }

    /// <summary>At least one of the three sources was deleted. The counts say which, so the confirmation
    /// can be specific rather than a generic "done".</summary>
    /// <param name="Live">The <c>&lt;appid&gt;.lua</c> Steam reads was deleted.</param>
    /// <param name="LooseBuilds">How many inert <c>&lt;appid&gt;_&lt;buildid&gt;.lua</c> files were deleted.</param>
    /// <param name="Vault">The stored-variant folder was deleted.</param>
    public sealed record Removed(bool Live, int LooseBuilds, bool Vault) : GameRemoval;

    /// <summary>Nothing was there to delete. Not a failure — the list was showing a game whose files had
    /// already gone — but it must not be reported as a removal either.</summary>
    public sealed record NothingToRemove : GameRemoval;

    /// <summary>A delete threw, most often because Steam holds the file open. <paramref name="Reason"/> is
    /// the exception message, shown to the user because "try closing Steam" is only actionable if they can
    /// see it was a sharing violation.</summary>
    /// <param name="Partial">What HAD already been deleted before the failure — the operation is not
    /// atomic, and reporting a clean failure after half a purge would be a lie. Named for what it is
    /// rather than after the <see cref="Removed"/> case, whose name a positional parameter cannot
    /// reuse.</param>
    public sealed record Failed(string Reason, Removed? Partial) : GameRemoval;
}
