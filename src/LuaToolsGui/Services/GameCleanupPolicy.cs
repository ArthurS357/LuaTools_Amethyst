using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using LuaToolsGui.Services.Backups;

namespace LuaToolsGui.Services;

/// <summary>Explicit scope for game cleanup / uninstall operations across Manage and Depots.</summary>
public enum GameCleanupScope
{
    LuaOnly,
    ManifestsOnly,
    LuaAndManifests,
    GameFilesOnly,
    Everything,
}

/// <summary>Fail-closed reason why a requested game cleanup was refused before modifying disk.</summary>
public enum GameCleanupBlockReason
{
    None,
    SteamNotFound,
    InvalidAppId,
    GameRunning,
    UnsafeInstallPath,
    SymlinkDetected,
    BackupVaultOverlap,
    UnreadableSiblingLua,
    /// <summary>A pre-flight read (stplug-in, depotcache, vault) threw; nothing was deleted.</summary>
    IoError,
    /// <summary>Another app's <c>installdir</c> in the same library is, contains or sits inside the target folder.</summary>
    SharedInstallDir,
    /// <summary>libraryfolders.vdf, a listed library or another app's ACF could not be read.</summary>
    UnreadableSteamLibrary,
}

/// <summary>Result of validating a Steam game's <c>installdir</c> inside <c>steamapps\common</c>.</summary>
public sealed record GameInstallPathCheck(
    bool IsValid,
    string? CanonicalPath,
    GameCleanupBlockReason BlockReason,
    string? Detail);

/// <summary>Pure manifest-deletion plan splitting exclusive manifests from shared ones.</summary>
public sealed record ManifestCleanupPlan(
    IReadOnlyList<string> ManifestsToDelete,
    IReadOnlyList<string> SharedManifestsPreserved);

/// <summary>Current on-disk state used to enable/disable scopes and display estimated disk savings.</summary>
public sealed record GameCleanupAvailability(
    bool HasActiveLua,
    bool HasStoredBuilds,
    bool HasInstalledGame,
    string? InstallDir,
    long? EstimatedGameBytes);

/// <summary>Structured outcome of a unified game cleanup operation.</summary>
public sealed record GameCleanupResult(
    GameCleanupScope Scope,
    bool LuaDeleted,
    int ManifestsDeleted,
    int SharedManifestsPreserved,
    bool GameFilesDeleted,
    bool AcfDeleted,
    long FreedBytes,
    GameCleanupBlockReason BlockReason,
    IReadOnlyList<string> Errors)
{
    public bool IsBlocked => BlockReason != GameCleanupBlockReason.None;
    public bool HasErrors => Errors.Count > 0;
    public bool AnyRemoved => LuaDeleted || ManifestsDeleted > 0 || GameFilesDeleted || AcfDeleted;
    public bool NothingToRemove => !IsBlocked && !HasErrors && !AnyRemoved;
    public bool Succeeded => !IsBlocked && !HasErrors;
}

/// <summary>
/// Pure, I/O-free policies for unified game cleanup and safe uninstallation:
/// scope resolution, shared-depot preservation, fail-closed path containment, symlink detection,
/// backup-vault isolation, and Valve ACF depot/size parsing.
/// </summary>
public static partial class GameCleanupPolicy
{
    private static readonly char[] PathSeparators = ['\\', '/'];

    [GeneratedRegex(@"""SizeOnDisk""\s*""(\d+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SizeOnDiskRegex();

    public static bool IncludesLua(GameCleanupScope scope) =>
        scope is GameCleanupScope.LuaOnly or GameCleanupScope.LuaAndManifests or GameCleanupScope.Everything;

    public static bool IncludesManifests(GameCleanupScope scope) =>
        scope is GameCleanupScope.ManifestsOnly or GameCleanupScope.LuaAndManifests or GameCleanupScope.Everything;

    public static bool IncludesGameFiles(GameCleanupScope scope) =>
        scope is GameCleanupScope.GameFilesOnly or GameCleanupScope.Everything;

    public static bool IsTargetGameRunning(long appId, int? runningAppId, Func<bool> isSteamAlive) =>
        SteamService.IsAppRunning(appId, runningAppId, isSteamAlive);

    /// <summary>
    /// True when <paramref name="candidatePath"/> and <paramref name="protectedRoot"/> are equal or
    /// one sits inside the other (meaning deleting <paramref name="candidatePath"/> could touch
    /// <paramref name="protectedRoot"/>).
    /// </summary>
    public static bool IsOverlappingPath(string candidatePath, string? protectedRoot)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(protectedRoot))
            return false;

        try
        {
            string candidate = Path.GetFullPath(candidatePath).TrimEnd(PathSeparators);
            string root = Path.GetFullPath(protectedRoot).TrimEnd(PathSeparators);

            if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
                return true;

            string rootPrefix = EnsureTrailingSeparator(root);
            string candidatePrefix = EnsureTrailingSeparator(candidate);
            return candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                || root.StartsWith(candidatePrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true; // fail-closed if paths cannot be canonicalized safely
        }
    }

    /// <summary>
    /// Validate that <paramref name="rawInstallDir"/> resolves strictly to a child folder inside
    /// <paramref name="commonDir"/>, has no traversal/rooting, is not the <c>common</c> folder itself
    /// or a drive root, contains no symlink/junction along its components, and does not overlap
    /// <paramref name="protectedBackupRoot"/>.
    /// </summary>
    public static GameInstallPathCheck ValidateInstallDirectory(
        string? commonDir,
        string? rawInstallDir,
        Func<string, bool> isLink,
        string? protectedBackupRoot = null)
    {
        ArgumentNullException.ThrowIfNull(isLink);

        if (string.IsNullOrWhiteSpace(commonDir) || !Path.IsPathFullyQualified(commonDir))
            return InvalidPath("Steam common directory is missing or not fully qualified.");

        string canonicalCommon;
        try
        {
            canonicalCommon = Path.GetFullPath(commonDir).TrimEnd(PathSeparators);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return InvalidPath($"Steam common directory is invalid: {ex.Message}");
        }

        if (IsDriveRoot(canonicalCommon))
            return InvalidPath("Steam common directory cannot be a drive root.");

        if (string.IsNullOrWhiteSpace(rawInstallDir))
            return InvalidPath("installdir is missing or empty.");

        if (FixAnalyzer.IsAbsoluteOrRooted(rawInstallDir)
            || rawInstallDir.Contains(':', StringComparison.Ordinal)
            || rawInstallDir.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return InvalidPath($"installdir '{rawInstallDir}' is rooted or contains invalid path syntax.");

        string[] segments = rawInstallDir.Split(PathSeparators, StringSplitOptions.None);
        // Trailing dots/spaces are stripped by Win32 normalization (".. " becomes "..", "A\B\.. " resolves to "A"),
        // so a segment ending in one means something other than what it says; no real folder name ends that way.
        if (segments.Length == 0 || segments.Any(static s => string.IsNullOrWhiteSpace(s) || s is "." or ".."
                || s.EndsWith('.') || s.EndsWith(' ')))
            return InvalidPath($"installdir '{rawInstallDir}' contains traversal or empty segments.");

        string canonicalCommonWithSep = EnsureTrailingSeparator(canonicalCommon);
        string canonicalTarget;
        try
        {
            canonicalTarget = Path.GetFullPath(Path.Combine(canonicalCommonWithSep, rawInstallDir)).TrimEnd(PathSeparators);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return InvalidPath($"installdir '{rawInstallDir}' could not be resolved: {ex.Message}");
        }

        if (IsDriveRoot(canonicalTarget)
            || string.Equals(canonicalTarget, canonicalCommon, StringComparison.OrdinalIgnoreCase)
            || !canonicalTarget.StartsWith(canonicalCommonWithSep, StringComparison.OrdinalIgnoreCase))
            return InvalidPath($"Resolved path escapes '{canonicalCommon}'.");

        if (IsOverlappingPath(canonicalTarget, protectedBackupRoot))
            return new GameInstallPathCheck(false, null, GameCleanupBlockReason.BackupVaultOverlap,
                "Resolved install path overlaps the manifest backup vault.");

        if (isLink(canonicalCommon))
            return new GameInstallPathCheck(false, null, GameCleanupBlockReason.SymlinkDetected,
                "Steam common directory is a symlink or junction.");

        string relative = Path.GetRelativePath(canonicalCommon, canonicalTarget);
        string current = canonicalCommon;
        foreach (string part in relative.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (isLink(current))
                return new GameInstallPathCheck(false, null, GameCleanupBlockReason.SymlinkDetected,
                    $"Path component '{part}' is a symlink or junction.");
        }

        return new GameInstallPathCheck(true, canonicalTarget, GameCleanupBlockReason.None, null);
    }

    /// <summary>Collect all numeric depot/app IDs declared or pinned inside a parsed <c>.lua</c>.</summary>
    public static HashSet<long> ExtractDepotIdsFromLua(LuaContents? lua)
    {
        var ids = new HashSet<long>();
        if (lua is null) return ids;

        foreach (var e in lua.Entries)
            if (e.Id is > 0 and <= uint.MaxValue) ids.Add(e.Id);
        foreach (var e in lua.DisabledEntries)
            if (e.Id is > 0 and <= uint.MaxValue) ids.Add(e.Id);
        foreach (long depotId in lua.ActivePins.Keys)
            if (depotId is > 0 and <= uint.MaxValue) ids.Add(depotId);
        foreach (long depotId in lua.CommentedPins.Keys)
            if (depotId is > 0 and <= uint.MaxValue) ids.Add(depotId);

        return ids;
    }

    /// <summary>
    /// Partition existing <c>depotcache\*.manifest</c> files matching <paramref name="targetDepotIds"/>
    /// into files safe to delete and shared manifests preserved because another game references them.
    /// </summary>
    public static ManifestCleanupPlan PlanManifestDeletions(
        IReadOnlySet<long> targetDepotIds,
        IReadOnlySet<long> otherGameDepotIds,
        IEnumerable<(string FileName, string FullPath)> depotCacheFiles)
    {
        ArgumentNullException.ThrowIfNull(targetDepotIds);
        ArgumentNullException.ThrowIfNull(otherGameDepotIds);
        ArgumentNullException.ThrowIfNull(depotCacheFiles);

        var toDelete = new List<string>();
        var preserved = new List<string>();

        if (targetDepotIds.Count == 0)
            return new ManifestCleanupPlan(toDelete, preserved);

        foreach (var (fileName, fullPath) in depotCacheFiles)
        {
            if (!ManifestBackupPolicy.TryParseManifestName(fileName, out long depotId, out _))
                continue;
            if (!targetDepotIds.Contains(depotId))
                continue;

            if (otherGameDepotIds.Contains(depotId))
                preserved.Add(fullPath);
            else
                toDelete.Add(fullPath);
        }

        return new ManifestCleanupPlan(toDelete, preserved);
    }

    /// <summary>
    /// Parse depot ids inside <c>InstalledDepots</c>, <c>MountedDepots</c>, or <c>SharedDepots</c>
    /// sections of a Valve <c>appmanifest_&lt;appid&gt;.acf</c>.
    /// </summary>
    public static IReadOnlyList<long> ParseAcfDepotIds(string? acfText)
    {
        if (string.IsNullOrWhiteSpace(acfText)) return [];

        var depots = new HashSet<long>();
        var sectionStack = new Stack<string?>();
        string? pendingKey = null;

        int i = 0;
        int len = acfText.Length;
        while (i < len)
        {
            char c = acfText[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '/' && i + 1 < len && acfText[i + 1] == '/')
            {
                while (i < len && acfText[i] != '\n') i++;
                continue;
            }

            if (c == '{')
            {
                if (sectionStack.Count > 0 && IsDepotSection(sectionStack.Peek())
                    && TryParsePositiveAppOrDepotId(pendingKey, out long depotId))
                {
                    depots.Add(depotId);
                }

                sectionStack.Push(pendingKey);
                pendingKey = null;
                i++;
                continue;
            }

            if (c == '}')
            {
                if (sectionStack.Count > 0) sectionStack.Pop();
                pendingKey = null;
                i++;
                continue;
            }

            string token = c == '"'
                ? ReadQuotedToken(acfText, ref i)
                : ReadBareToken(acfText, ref i);

            if (pendingKey is null)
            {
                pendingKey = token;
            }
            else
            {
                if (sectionStack.Count > 0 && IsDepotSection(sectionStack.Peek())
                    && TryParsePositiveAppOrDepotId(pendingKey, out long depotId))
                {
                    depots.Add(depotId);
                }
                pendingKey = null;
            }
        }

        return [.. depots.OrderBy(static id => id)];
    }

    /// <summary>Parse <c>SizeOnDisk</c> bytes from an <c>appmanifest_&lt;appid&gt;.acf</c>.</summary>
    public static long? ParseAcfSizeOnDisk(string? acfText)
    {
        if (string.IsNullOrWhiteSpace(acfText)) return null;
        var match = SizeOnDiskRegex().Match(acfText);
        return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) && bytes >= 0
            ? bytes
            : null;
    }

    private static bool IsDepotSection(string? name) =>
        string.Equals(name, "InstalledDepots", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "MountedDepots", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "SharedDepots", StringComparison.OrdinalIgnoreCase);

    private static bool TryParsePositiveAppOrDepotId(string? value, out long id)
    {
        id = 0;
        return !string.IsNullOrEmpty(value)
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id)
            && id is > 0 and <= uint.MaxValue;
    }

    private static string ReadQuotedToken(string text, ref int i)
    {
        i++; // skip opening quote
        int start = i;
        bool hasEscape = false;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                hasEscape = true;
                i += 2;
                continue;
            }
            if (c == '"')
            {
                string raw = text[start..i];
                i++;
                return hasEscape ? raw.Replace(@"\\", @"\").Replace(@"\""", "\"") : raw;
            }
            i++;
        }
        return text[start..];
    }

    private static string ReadBareToken(string text, ref int i)
    {
        int start = i;
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"'))
            i++;
        return text[start..i];
    }

    private static bool IsDriveRoot(string canonicalPath)
    {
        string? root = Path.GetPathRoot(canonicalPath)?.TrimEnd(PathSeparators);
        return root is not null && string.Equals(root, canonicalPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static GameInstallPathCheck InvalidPath(string detail) =>
        new(false, null, GameCleanupBlockReason.UnsafeInstallPath, detail);
}
