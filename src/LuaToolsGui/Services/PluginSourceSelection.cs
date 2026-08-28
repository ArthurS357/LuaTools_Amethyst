namespace LuaToolsGui.Services;

/// <summary>
/// Which plugin source is ACTIVE — the one the Plugin page installs, updates and reports on. Pure policy:
/// no network, no disk, no settings file. The installer reads the persisted choice and the on-disk
/// manifest and hands both here.
///
/// <para>
/// This exists as its own type for the same reason <see cref="PluginSourceResolver"/> does: it is the
/// decision that used to be made implicitly (position in a list, plus a silent fallback), and an implicit
/// decision about which repository's DLL lands next to <c>steam.exe</c> is one nobody can review. The rule
/// is now three lines long and testable.
/// </para>
///
/// <para>
/// <b>Every candidate is validated against the compiled-in catalog.</b> The persisted value comes out of
/// <c>settings.json</c> — a file anything running as the user can write — and the manifest value comes out
/// of a file in the same profile. Neither is trusted to NAME a repository: they can only select one the
/// build already ships, and anything else is ignored in favour of the default. That is what keeps a
/// user-writable file from redirecting an install, which is precisely the risk that made the old chain
/// compiled-in in the first place.
/// </para>
/// </summary>
public static class PluginSourceSelection
{
    /// <summary>The catalogued source with this slug ("owner/repo"), or null when the slug names none.
    /// Case-insensitive, as GitHub owner/repo comparison is everywhere else in this app.</summary>
    public static PluginSource? Find(IReadOnlyList<PluginSource> catalog, string? slug)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(slug)) return null;

        foreach (var source in catalog)
            if (source.Slug.Equals(slug.Trim(), StringComparison.OrdinalIgnoreCase))
                return source;

        return null;
    }

    /// <summary>Whether this exact source is one the build ships. The gate every install goes through.</summary>
    public static bool IsKnown(IReadOnlyList<PluginSource> catalog, PluginSource source) =>
        Find(catalog, source.Slug) is not null;

    /// <summary>
    /// The active source, in precedence order:
    /// <list type="number">
    /// <item>the user's own persisted choice, when it names a catalogued source;</item>
    /// <item>otherwise the source the current install actually came from — so an existing install keeps
    /// its own source rather than being silently moved to a different repository by an app update;</item>
    /// <item>otherwise the catalogue's first entry, which is the default for a fresh install.</item>
    /// </list>
    /// </summary>
    /// <param name="catalog">The compiled-in sources. Must not be empty.</param>
    /// <param name="chosen">Persisted user choice ("owner/repo"), or null when never chosen.</param>
    /// <param name="installed">Source the on-disk install records, or null when nothing is installed.</param>
    public static PluginSource Resolve(IReadOnlyList<PluginSource> catalog, string? chosen, string? installed)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Count == 0)
            throw new ArgumentException("The plugin source catalogue is empty.", nameof(catalog));

        return Find(catalog, chosen) ?? Find(catalog, installed) ?? catalog[0];
    }
}
