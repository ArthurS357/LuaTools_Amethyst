namespace LuaToolsGui.Services;

/// <summary>Display layout mode for the Manage and Depots game libraries.</summary>
public enum LibraryViewMode
{
    Grid,
    List,
}

/// <summary>
/// Lenient parser for persisted view modes — unknown, numeric or corrupt values safely fall back to
/// <see cref="LibraryViewMode.Grid"/>.
/// </summary>
public static class LibraryViewModeParser
{
    public static LibraryViewMode Parse(string? raw) =>
        string.Equals(raw?.Trim(), nameof(LibraryViewMode.List), StringComparison.OrdinalIgnoreCase)
            ? LibraryViewMode.List
            : LibraryViewMode.Grid;
}
