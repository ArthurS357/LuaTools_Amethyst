using System.Globalization;
using System.IO;

namespace LuaToolsGui.Services.Backups;

/// <summary>Which flow left a backup folder in the Steam root.</summary>
public enum BackupKind { AmethystTool, Removal }

/// <summary>One backup folder found directly under the Steam root.</summary>
/// <param name="CreatedUtc">From the timestamp in the folder's own name, not the filesystem: a folder copied
/// or restored elsewhere keeps the moment it was made.</param>
public sealed record BackupEntry(
    string Name,
    string FullPath,
    DateTime CreatedUtc,
    long SizeBytes,
    int FileCount,
    BackupKind Kind);

/// <summary>
/// What counts as a backup folder the cleanup screen may offer, decided from the name and path alone. Pure,
/// so the rule that gates a recursive delete is testable without a disk.
///
/// <para>
/// Two shapes exist, both written by the app into the Steam root as <c>&lt;prefix&gt;yyyyMMdd-HHmmss</c> in
/// local time: <see cref="AmethystToolPlan.BackupDirectoryPrefix"/> (files an AmethystTool install overwrote
/// or quarantined) and <see cref="PluginRemoval.BackupDirectoryPrefix"/> (files an uninstall moved aside).
/// The prefixes are read from those plans, so a rename there cannot leave this screen matching a stale name.
/// A folder whose suffix is not that exact timestamp is not ours, and is never offered.
/// </para>
/// </summary>
public static class BackupInventory
{
    private const string StampFormat = "yyyyMMdd-HHmmss";

    private static readonly (string Prefix, BackupKind Kind)[] Shapes =
    [
        (AmethystToolPlan.BackupDirectoryPrefix, BackupKind.AmethystTool),
        (PluginRemoval.BackupDirectoryPrefix, BackupKind.Removal),
    ];

    /// <summary>The kind of backup <paramref name="folderName"/> is, or null when it is not one of ours.</summary>
    public static BackupKind? ClassifyFolder(string folderName) =>
        TryParse(folderName, out var kind, out _) ? kind : null;

    /// <summary>Classify <paramref name="folderName"/> and read the creation time out of its timestamp.</summary>
    public static bool TryParse(string folderName, out BackupKind kind, out DateTime createdUtc)
    {
        foreach (var (prefix, k) in Shapes)
        {
            if (folderName.StartsWith(prefix, StringComparison.Ordinal)
                && DateTime.TryParseExact(folderName[prefix.Length..], StampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var local))
            {
                kind = k;
                createdUtc = local.ToUniversalTime();
                return true;
            }
        }

        kind = default;
        createdUtc = default;
        return false;
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> resolves strictly inside <paramref name="steamRoot"/>. Same check
    /// as the fix extractor's (<see cref="FixAnalyzer.IsContained"/>): <c>..</c> collapsed by
    /// <see cref="Path.GetFullPath(string)"/>, root compared WITH its trailing separator so <c>D:\Steam2</c>
    /// is not inside <c>D:\Steam</c>. String-only: whether the folder is a junction is checked on disk by
    /// <see cref="BackupService"/>.
    /// </summary>
    public static bool IsInsideSteamRoot(string fullPath, string steamRoot) =>
        FixAnalyzer.IsContained(steamRoot, fullPath, out _);

    /// <summary>
    /// The full gate for deleting <paramref name="entry"/>: a recognised backup name, a direct child of the
    /// Steam root, and inside it. Anything else is refused, however it got into the list.
    /// </summary>
    public static bool MayDelete(BackupEntry entry, string steamRoot)
    {
        string? parent;
        try { parent = Path.GetDirectoryName(Path.GetFullPath(entry.FullPath)); }
        catch { return false; }

        return ClassifyFolder(Path.GetFileName(entry.FullPath)) is not null
            && IsInsideSteamRoot(entry.FullPath, steamRoot)
            && string.Equals(parent, Path.TrimEndingDirectorySeparator(Path.GetFullPath(steamRoot)),
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The entries older than <paramref name="days"/> at <paramref name="nowUtc"/>.</summary>
    public static IReadOnlyList<BackupEntry> OlderThan(IEnumerable<BackupEntry> entries, DateTime nowUtc, int days) =>
        [.. entries.Where(e => e.CreatedUtc < nowUtc.AddDays(-days))];
}
