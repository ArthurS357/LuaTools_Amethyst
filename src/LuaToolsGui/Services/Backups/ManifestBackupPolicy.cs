using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;

namespace LuaToolsGui.Services.Backups;

/// <summary>Where game manifests are backed up. Persisted by NAME in settings.json (see
/// <see cref="SettingsService.ManifestBackupMode"/>), so the order of these values may change freely.</summary>
public enum ManifestBackupMode
{
    /// <summary>The default: nothing is backed up and nothing is restored.</summary>
    Disabled,

    /// <summary>A folder the user picked, typically one a cloud client (Google Drive, OneDrive) syncs.</summary>
    LocalFolder,
}

/// <summary>One <c>&lt;depot&gt;_&lt;manifest&gt;.manifest</c> sitting in Steam's depotcache.</summary>
public readonly record struct DepotManifest(long DepotId, string ManifestId, string Path, DateTime LastWriteUtc);

/// <summary>A manifest recorded in a backup's metadata.json.</summary>
public sealed record BackedUpManifest(long DepotId, string ManifestId, string Sha256, DateTime? LastWriteUtc = null);

/// <summary>Result of reconciling local depotcache candidates against a stored backup.</summary>
public sealed record ManifestReconcileResult(
    IReadOnlyList<DepotManifest> Selected,
    IReadOnlyList<BackedUpManifest> Carried,
    bool PreventedDowngrade);

/// <summary>On-disk shape of <c>&lt;dest&gt;\&lt;appid&gt;\metadata.json</c>. <see cref="ZipSha256"/> is what a
/// restore checks the zip against before anything is extracted into Steam.</summary>
public sealed record ManifestBackupMetadata(
    int Schema,
    long AppId,
    string? Name,
    DateTime UpdatedAtUtc,
    string LuaSha256,
    string ZipSha256,
    IReadOnlyList<BackedUpManifest> Manifests,
    long? ZipSizeBytes = null,
    int? ManifestCount = null,
    bool? HasManifests = null)
{
    public const int CurrentSchema = 1;

    /// <summary>Effective manifest count, staying backward-compatible with L3 metadata.json files.</summary>
    [JsonIgnore]
    public int EffectiveManifestCount => ManifestCount is >= 0 ? ManifestCount.Value : Manifests.Count;

    /// <summary>True when at least one depot manifest is present (false = lua-only backup).</summary>
    [JsonIgnore]
    public bool EffectiveHasManifests => HasManifests ?? (EffectiveManifestCount > 0);
}

/// <summary>One game entry in the root <c>index.json</c> catalog of a manifest backup vault.</summary>
public sealed record ManifestBackupIndexEntry(
    long AppId,
    string? Name,
    DateTime UpdatedAtUtc,
    int ManifestCount,
    bool HasManifests,
    string ZipFile,
    string ZipSha256);

/// <summary>On-disk shape of <c>&lt;dest&gt;\index.json</c> for fast frontend discovery without hydrating zips.</summary>
public sealed record ManifestBackupIndex(
    int Schema,
    DateTime UpdatedAtUtc,
    IReadOnlyList<ManifestBackupIndexEntry> Games)
{
    public const int CurrentSchema = 1;
}

/// <summary>Lightweight per-app availability snapshot for the frontend "Add via Backup" button.</summary>
public sealed record ManifestBackupAppStatus(
    bool Enabled,
    bool Available,
    bool HasManifests,
    int ManifestCount,
    DateTime? UpdatedAtUtc);

/// <summary>What a backup pass decided for one game.</summary>
public enum ManifestBackupChange
{
    /// <summary>No backup existed for this game.</summary>
    New,

    /// <summary>The lua changed or depotcache holds a different manifest set: the zip was rewritten.</summary>
    Updated,

    /// <summary>Identical to what is stored: nothing was written (so a synced folder does not re-upload).</summary>
    Unchanged,
}

/// <summary>
/// The decisions of the manifest backup, free of I/O so they can be tested directly: which manifests belong
/// to a game, whether a stored backup is still current, and where a backup may be written.
/// </summary>
public static class ManifestBackupPolicy
{
    /// <summary>Parse a depotcache file name (<c>&lt;depot&gt;_&lt;manifest&gt;.manifest</c>). Uses the installer's
    /// own shape rule, so a name the installer would refuse is never backed up either.</summary>
    public static bool TryParseManifestName(string fileName, out long depotId, out string manifestId)
    {
        depotId = 0;
        manifestId = "";
        if (!LuaInstaller.ManifestNameRegex().IsMatch(fileName)) return false;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        int sep = stem.IndexOf('_', StringComparison.Ordinal);
        if (!long.TryParse(stem.AsSpan(0, sep), NumberStyles.None, CultureInfo.InvariantCulture, out depotId))
            return false;
        manifestId = stem[(sep + 1)..];
        return true;
    }

    /// <summary>
    /// The manifests to back up for one game: the NEWEST file of every depot the lua declares (so a game
    /// Steam has updated is backed up at its current version), plus the file a <c>setManifestid</c> pin
    /// names when it is a different one (so a pinned build still restores).
    /// </summary>
    /// <remarks>"Newest" is by write time, not by manifest id: Steam manifest ids are not monotonic, so the
    /// numerically larger id is not the later build. The id only breaks a timestamp tie, for determinism.</remarks>
    public static IReadOnlyList<DepotManifest> SelectManifests(
        IEnumerable<long> depotIds,
        IReadOnlyDictionary<long, string> pins,
        IEnumerable<DepotManifest> available)
    {
        var wanted = depotIds.Concat(pins.Keys).ToHashSet();
        var picked = new List<DepotManifest>();

        foreach (var group in available.Where(m => wanted.Contains(m.DepotId)).GroupBy(static m => m.DepotId))
        {
            var newest = group
                .OrderByDescending(static m => m.LastWriteUtc)
                .ThenByDescending(static m => ulong.TryParse(m.ManifestId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : 0)
                .First();
            picked.Add(newest);

            if (pins.TryGetValue(group.Key, out string? pin) && pin != newest.ManifestId &&
                group.FirstOrDefault(m => m.ManifestId == pin) is { Path: not null } pinned)
                picked.Add(pinned);
        }

        return [.. picked.OrderBy(static m => m.DepotId).ThenBy(static m => m.ManifestId, StringComparer.Ordinal)];
    }

    private static readonly Dictionary<long, string> EmptyPins = [];

    /// <summary>DOS ZIP entry headers have 2-second timestamp granularity, so differences up to 2 s
    /// between a restored file and its backup copy are rounding, not a newer build.</summary>
    public static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    private static DateTime ToUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>True when <paramref name="storedUtc"/> leads <paramref name="localUtc"/> by more than
    /// <see cref="TimestampTolerance"/> (DOS ZIP 2-second timestamp granularity).</summary>
    public static bool IsStoredNewer(DateTime? storedUtc, DateTime localUtc)
    {
        if (storedUtc is not { } s) return false;
        return ToUtc(s) - ToUtc(localUtc) > TimestampTolerance;
    }

    /// <summary>
    /// Stored manifests the new snapshot must keep because depotcache can no longer supply them: the lua still
    /// declares the depot, and depotcache has nothing for it, or lacks exactly the pinned file. Without this a
    /// pass on a PC that shares the folder, or after Steam cleared depotcache, would replace the only copy with
    /// a poorer one.
    /// </summary>
    public static IReadOnlyList<BackedUpManifest> SelectCarried(
        IReadOnlyCollection<long> depotIds,
        IReadOnlyDictionary<long, string> pins,
        IReadOnlyList<DepotManifest> selected,
        IEnumerable<BackedUpManifest> stored) =>
        Reconcile(depotIds, pins, EmptyPins, selected, stored, allowAntiDowngrade: false).Carried;

    /// <summary>
    /// Reconcile local <c>depotcache</c> candidates (<paramref name="selected"/>) against <paramref name="stored"/>
    /// manifests: preserves missing or pinned stored manifests and, when <paramref name="allowAntiDowngrade"/>
    /// is enabled, prevents an unpinned depot from regressing to an older local manifest when a verified stored
    /// manifest is strictly newer (beyond <see cref="TimestampTolerance"/>). Explicit active <c>setManifestid</c>
    /// pins in <paramref name="activePins"/> always win over anti-downgrade so an intentional build freeze holds.
    /// </summary>
    public static ManifestReconcileResult Reconcile(
        IReadOnlyCollection<long> depotIds,
        IReadOnlyDictionary<long, string> pins,
        IReadOnlyDictionary<long, string> activePins,
        IReadOnlyList<DepotManifest> selected,
        IEnumerable<BackedUpManifest> stored,
        bool allowAntiDowngrade = true)
    {
        var declared = depotIds as HashSet<long> ?? [.. depotIds];
        var storedList = stored as IReadOnlyList<BackedUpManifest> ?? [.. stored];
        var carried = new List<BackedUpManifest>();
        var carriedKeys = new HashSet<(long DepotId, string ManifestId)>();

        foreach (var m in storedList)
        {
            if (!declared.Contains(m.DepotId)) continue;
            if (selected.Any(s => s.DepotId == m.DepotId && s.ManifestId == m.ManifestId)) continue;
            bool depotMissing = !selected.Any(s => s.DepotId == m.DepotId);
            bool matchesPin = pins.TryGetValue(m.DepotId, out string? pin) && pin == m.ManifestId;
            if ((depotMissing || matchesPin) && carriedKeys.Add((m.DepotId, m.ManifestId)))
                carried.Add(m);
        }

        bool preventedDowngrade = false;
        HashSet<(long DepotId, string ManifestId)>? suppressedLocal = null;

        if (allowAntiDowngrade)
        {
            foreach (var group in storedList.Where(m => declared.Contains(m.DepotId)).GroupBy(static m => m.DepotId))
            {
                long depot = group.Key;
                if (activePins.ContainsKey(depot)) continue;

                var localForDepot = selected.Where(s => s.DepotId == depot).ToList();
                if (localForDepot.Count == 0) continue;

                var newestLocal = localForDepot
                    .OrderByDescending(static m => ToUtc(m.LastWriteUtc))
                    .ThenByDescending(static m => ulong.TryParse(m.ManifestId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : 0)
                    .First();

                var newestStored = group
                    .Where(static m => m.LastWriteUtc.HasValue)
                    .OrderByDescending(static m => ToUtc(m.LastWriteUtc.GetValueOrDefault()))
                    .ThenByDescending(static m => ulong.TryParse(m.ManifestId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ? id : 0)
                    .FirstOrDefault();

                if (newestStored is null
                    || localForDepot.Any(s => s.ManifestId == newestStored.ManifestId)
                    || !IsStoredNewer(newestStored.LastWriteUtc, newestLocal.LastWriteUtc))
                {
                    continue;
                }

                preventedDowngrade = true;
                if (carriedKeys.Add((newestStored.DepotId, newestStored.ManifestId)))
                    carried.Add(newestStored);

                string? pinnedId = pins.GetValueOrDefault(depot);
                foreach (var local in localForDepot)
                {
                    if (pinnedId is not null && string.Equals(local.ManifestId, pinnedId, StringComparison.Ordinal))
                        continue;

                    suppressedLocal ??= [];
                    suppressedLocal.Add((local.DepotId, local.ManifestId));
                }
            }
        }

        IReadOnlyList<DepotManifest> keptSelected = suppressedLocal is null
            ? selected
            : [.. selected.Where(s => !suppressedLocal.Contains((s.DepotId, s.ManifestId)))];

        IReadOnlyList<BackedUpManifest> orderedCarried = [.. carried
            .OrderBy(static m => m.DepotId)
            .ThenBy(static m => m.ManifestId, StringComparer.Ordinal)];

        return new ManifestReconcileResult(keptSelected, orderedCarried, preventedDowngrade);
    }

    /// <summary>Compare the current snapshot with what is stored. Content-addressed manifest names mean
    /// (depot, manifest id) pairs are enough to detect a manifest change; the lua is compared by hash.
    /// <paramref name="zipIntact"/> = the stored zip is present at its expected size from metadata (avoiding
    /// hydrating cloud-streaming placeholders during skip-checks; full SHA-256 verification remains mandatory
    /// and fail-closed on restore). False forces a rewrite.</summary>
    public static ManifestBackupChange Decide(
        ManifestBackupMetadata? stored, bool zipIntact, string luaSha256, IEnumerable<DepotManifest> selected)
    {
        if (stored is null) return ManifestBackupChange.New;
        if (!zipIntact || !string.Equals(stored.LuaSha256, luaSha256, StringComparison.OrdinalIgnoreCase))
            return ManifestBackupChange.Updated;

        var now = selected.Select(static m => (m.DepotId, m.ManifestId)).ToHashSet();
        var before = stored.Manifests.Select(static m => (m.DepotId, m.ManifestId)).ToHashSet();
        return now.SetEquals(before) ? ManifestBackupChange.Unchanged : ManifestBackupChange.Updated;
    }

    /// <summary>
    /// A folder backups may be written to: fully qualified, not a drive root, not Windows or Program Files
    /// (the save-backup rule) and not inside the Steam install, which this feature only ever reads.
    /// settings.json is writable by any process of the user, so this runs again on every use.
    /// </summary>
    public static bool IsAcceptableDestination(string? path, string? steamRoot)
    {
        if (!SaveLocationCatalog.IsAcceptableCustomFolder(path)) return false;
        if (string.IsNullOrWhiteSpace(steamRoot)) return true;

        try
        {
            string full = Path.GetFullPath(path!).TrimEnd('\\');
            string steam = Path.GetFullPath(steamRoot).TrimEnd('\\');
            return !full.Equals(steam, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(steam + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Largest lua the vault accepts. Real ones are kilobytes; the cap only bounds memory.</summary>
    public const int MaxLuaBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Why a lua must not enter the vault (nor leave it on restore), or null. It is the installer's own screen,
    /// so the vault never holds a lua the restore would then refuse, and a poisoned lua never replaces a good one.
    /// </summary>
    /// <remarks>Deliberately NOT "valid UTF-8" nor "declares its own appid": SteamTools luas carry GBK and
    /// Shift-JIS comments, and a DLC lua need not name its own id, so either rule would refuse real files.
    /// An empty or declaration-less lua is handled by the store (it never replaces a stored backup).</remarks>
    public static string? LuaProblem(string luaText) =>
        LuaManifestValidator.Screen(luaText) is { Rejected: true, Reason: var reason } ? $"the lua {reason}" : null;

    /// <summary>Whether a zip entry name may appear in a backup of <paramref name="appId"/>: exactly
    /// <c>&lt;appid&gt;.lua</c> or a well-formed manifest name, at the archive root. Anything else (a folder,
    /// "..", a drive letter, another app's lua) makes the whole archive untrusted.</summary>
    public static bool IsAllowedEntry(string fullName, long appId) =>
        fullName == $"{appId.ToString(CultureInfo.InvariantCulture)}.lua"
        || LuaInstaller.ManifestNameRegex().IsMatch(fullName);
}
