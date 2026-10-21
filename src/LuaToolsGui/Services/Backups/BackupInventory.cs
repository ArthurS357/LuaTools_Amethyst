using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace LuaToolsGui.Services.Backups;

/// <summary>What left a backup in the Steam root.</summary>
/// <remarks>
/// <see cref="AmethystTool"/> and <see cref="Removal"/> are FOLDERS this app writes.
/// <see cref="AmethystToolFile"/> is a loose <c>AmethystTool-v&lt;version&gt;[-stamp].dll.bak</c> that
/// AmethystTool's own updater leaves next to steam.exe (this app never writes one).
/// <see cref="UnknownFile"/> is any other <c>*.bak</c> in the root: listed, flagged, never bulk-deleted.
/// </remarks>
public enum BackupKind { AmethystTool, Removal, AmethystToolFile, UnknownFile }

/// <summary>One backup found directly under the Steam root: a folder, or a loose .bak file.</summary>
/// <param name="CreatedUtc">Folders: from the timestamp in their own name, so a copied folder keeps the moment
/// it was made. Files: the file's last-write time (a rename to .bak keeps it).</param>
/// <param name="FileCount">Files inside a folder; null for a loose file.</param>
public sealed record BackupEntry(
    string Name,
    string FullPath,
    DateTime CreatedUtc,
    long SizeBytes,
    int? FileCount,
    BackupKind Kind)
{
    public bool IsFile => Kind is BackupKind.AmethystToolFile or BackupKind.UnknownFile;
}

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

    private static readonly Regex AmethystToolBak =
        new(@"^AmethystTool-v[0-9.]+(-[0-9-]+)?\.dll\.bak\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// The kind of loose file <paramref name="fileName"/> is: an AmethystTool updater leftover, some other
    /// <c>.bak</c> (shown with a warning), or null for anything that is not a .bak at all.
    /// </summary>
    public static BackupKind? ClassifyFile(string fileName) =>
        AmethystToolBak.IsMatch(fileName) ? BackupKind.AmethystToolFile
        : fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) && fileName.Length > ".bak".Length
            ? BackupKind.UnknownFile
            : null;

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
    /// The full gate for deleting <paramref name="entry"/>: its name classifies as the kind it claims (a folder
    /// name for a folder entry, a .bak name for a file entry), it is a direct child of the Steam root, and
    /// inside it. Anything else is refused, however it got into the list.
    /// </summary>
    public static bool MayDelete(BackupEntry entry, string steamRoot)
    {
        string? parent;
        try { parent = Path.GetDirectoryName(Path.GetFullPath(entry.FullPath)); }
        catch { return false; }

        string name = Path.GetFileName(entry.FullPath);
        BackupKind? actual = entry.IsFile ? ClassifyFile(name) : ClassifyFolder(name);

        return actual == entry.Kind
            && IsInsideSteamRoot(entry.FullPath, steamRoot)
            && string.Equals(parent, Path.TrimEndingDirectorySeparator(Path.GetFullPath(steamRoot)),
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The entries older than <paramref name="days"/> at <paramref name="nowUtc"/>. An
    /// <see cref="BackupKind.UnknownFile"/> is never included: it is not ours, so it goes only when the user
    /// ticks it on purpose.
    /// </summary>
    public static IReadOnlyList<BackupEntry> OlderThan(IEnumerable<BackupEntry> entries, DateTime nowUtc, int days) =>
        [.. entries.Where(e => e.Kind != BackupKind.UnknownFile && e.CreatedUtc < nowUtc.AddDays(-days))];
}
