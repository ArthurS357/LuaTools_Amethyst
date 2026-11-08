using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>A game Steam reports as fully installed. <see cref="Name"/> is empty when the manifest has none.</summary>
public sealed record InstalledGame(long AppId, string Name);

/// <summary>One catalog game, flagged with whether it is installed in the Steam library.</summary>
public sealed record CatalogGame(DenuvoGameListing Listing, bool IsInstalled)
{
    public string Name => Listing.Name;
    public string AppId => Listing.AppId;
}

/// <summary>Why the Fixes grid has nothing to show. <see cref="None"/> means it has something.</summary>
public enum FixesEmptyState { None, LoadFailed, NoCatalog }

/// <summary>What the Fixes page shows for one combination of catalog result and library result.</summary>
public sealed record FixesViewState(FixesEmptyState Empty, bool ShowBadges, bool ShowBanner, bool ShowLibraryWarning);

/// <summary>
/// The pure half of the Fixes page: the joined catalog, the installed games it lacks, and the state matrix.
/// No I/O, no Steam, no network — the caller supplies the catalog and the installed games.
/// </summary>
public static class InstalledFixes
{
    /// <summary>The whole catalog, installed games first, then A–Z within each group (same order as Manage).</summary>
    public static IReadOnlyList<CatalogGame> JoinCatalogWithInstalled(
        IReadOnlyList<DenuvoGameListing> catalog, IReadOnlySet<long> installedIds) =>
        catalog
            .Select(g => new CatalogGame(g, long.TryParse(g.AppId, out long id) && installedIds.Contains(id)))
            .OrderByDescending(c => c.IsInstalled)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>The installed games the catalog has no fix for — what the banner counts and lists.</summary>
    public static IReadOnlyList<InstalledGame> FindInstalledWithoutFix(
        IReadOnlyList<InstalledGame> installed, IReadOnlySet<long> catalogIds) =>
        installed
            .Where(g => !catalogIds.Contains(g.AppId))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// The four-way matrix. A failed catalog suppresses everything (a list, a banner and badges built on
    /// nothing would be lies); a failed library keeps the list but drops the badges and the banner, because
    /// "installed" is unknown rather than empty.
    /// </summary>
    public static FixesViewState Resolve(
        bool catalogFailed, int catalogCount, bool libraryFailed, int installedWithoutFixCount)
    {
        if (catalogFailed) return new(FixesEmptyState.LoadFailed, false, false, false);
        if (catalogCount == 0) return new(FixesEmptyState.NoCatalog, false, false, false);
        bool libraryOk = !libraryFailed;
        return new(FixesEmptyState.None, libraryOk, libraryOk && installedWithoutFixCount > 0, libraryFailed);
    }

    public static string EmptyMessage(FixesEmptyState state) => state switch
    {
        FixesEmptyState.LoadFailed => Resources.Strings.Fixes_Err_Load,
        FixesEmptyState.NoCatalog => Resources.Strings.Fixes_Empty_None,
        _ => "",
    };
}
