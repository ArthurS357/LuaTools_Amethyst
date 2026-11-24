using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LuaToolsGui.Services.AppInfo;

/// <summary>One game's launch-option edit, plus the snapshot needed to undo it.</summary>
public sealed class LaunchMod
{
    public int AppId { get; set; }

    /// <summary>appinfo's changeNumber when the snapshot was taken. If Steam later ships a different
    /// one, the app has been updated and the snapshot is re-based rather than trusted.</summary>
    public uint ChangeNumber { get; set; }

    /// <summary>The app's launch entries BEFORE we touched them. A whole snapshot, not a diff — a diff
    /// against a file Steam rewrites at will is worthless.</summary>
    public List<LaunchOption> Original { get; set; } = [];

    /// <summary>What the entries should look like.</summary>
    public List<LaunchOption> Desired { get; set; } = [];

    /// <summary>Raw <c>config.launch</c> VDF table captured before the first edit (base64 of inline-key
    /// <see cref="BinaryVdf"/>). Preserves unmodelled Steam keys (<c>vacmodulefilename</c>,
    /// <c>description_loc</c>, custom sub-tables, duplicate keys, non-UTF-8 bytes) across reorders and
    /// deletions. Null for legacy v1 records migrated from older builds.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginalLaunchVdf { get; set; }

    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Persists launch-option edits so they survive Steam rewriting appinfo.vdf.
///
/// <para>
/// Steam regenerates appinfo from PICS on login, app updates and store browsing — it rewrote the file
/// twice during development. A one-shot edit therefore silently disappears. This mirrors what SteamEdit
/// does with its mods.dat: keep the desired state plus a pre-edit snapshot in our own file, and
/// re-apply when the live appinfo no longer matches.
/// </para>
/// </summary>
public class LaunchModStore
{
    public const int CurrentFormatVersion = 2;

    /// <summary>Upper bound for a single app's raw <c>config.launch</c> VDF snapshot (1 MiB). Real
    /// launch tables are a few KB; capping refuses hostile/bloated files before decoding.</summary>
    internal const int MaxRawLaunchBytes = 1024 * 1024;
    private const int MaxRawLaunchBase64Length = ((MaxRawLaunchBytes + 2) / 3) * 4;

    private sealed class StoreEnvelope
    {
        public int Version { get; set; }
        public List<LaunchMod>? Mods { get; set; }
    }

    private static readonly string Dir = AppDataMigrator.CurrentDirectory;
    private static readonly string FilePath = Path.Combine(Dir, "launchmods.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _gate = new();
    private Dictionary<int, LaunchMod> _mods = [];

    /// <summary>The file exists but the last read failed on I/O (see <see cref="Load"/>).</summary>
    private bool _loadFailed;

    private readonly string _path;

    public LaunchModStore() : this(FilePath) { }

    /// <summary>Test seam: point the store at a throwaway file.</summary>
    internal LaunchModStore(string path)
    {
        _path = path;
        Load();
    }

    // Readers retry a load that failed on I/O too: a file locked for a moment at startup must not hide every
    // record (drift notice, overrides list, "modded" badge) for the whole session.
    public bool IsEmpty { get { lock (_gate) { TryRecoverLoad(); return _mods.Count == 0; } } }

    public IReadOnlyList<LaunchMod> All { get { lock (_gate) { TryRecoverLoad(); return _mods.Values.ToList(); } } }

    public LaunchMod? Get(int appId)
    {
        lock (_gate)
        {
            TryRecoverLoad();
            return _mods.GetValueOrDefault(appId);
        }
    }

    /// <summary>Record an edit. The snapshot is only captured the FIRST time a game is modified —
    /// re-saving must not overwrite the original with our own previous edit.</summary>
    public void Save(int appId, uint changeNumber, IReadOnlyList<LaunchOption> current,
                     IReadOnlyList<LaunchOption> desired, VdfTable? originalLaunchTable = null)
    {
        lock (_gate)
        {
            // Fail closed: saving from an empty store that only LOOKS empty would replace every record on disk.
            if (!TryRecoverLoad())
                throw new IOException($"{Path.GetFileName(_path)} exists but could not be read; it was left untouched and nothing was saved.");

            if (!_mods.TryGetValue(appId, out var mod))
            {
                mod = new LaunchMod
                {
                    AppId = appId,
                    ChangeNumber = changeNumber,
                    Original = current.Select(o => o.Clone()).ToList(),
                    OriginalLaunchVdf = originalLaunchTable is not null ? EncodeRawLaunch(originalLaunchTable) : null,
                };
                _mods[appId] = mod;
            }

            mod.Desired = desired.Select(o => o.Clone()).ToList();
            mod.SavedAt = DateTime.UtcNow;
            Persist();
        }
    }

    /// <summary>Forget a game's edit (used after restoring it).</summary>
    public void Remove(int appId)
    {
        lock (_gate)
        {
            if (TryRecoverLoad() && _mods.Remove(appId)) Persist();
        }
    }

    /// <summary>Forget several edits in one write, so a batch can't leave the file half-updated.</summary>
    public void RemoveMany(IEnumerable<int> appIds)
    {
        lock (_gate)
        {
            if (!TryRecoverLoad()) return;
            bool any = false;
            foreach (int appId in appIds) any |= _mods.Remove(appId);
            if (any) Persist();
        }
    }

    /// <summary>
    /// Copy the current records to <c>launchmods.json.pre-restore</c> before a restore starts deleting
    /// them. <see cref="Persist"/>'s <c>.bak</c> can't do this job: it preserves only the initial
    /// pre-edit snapshot (not a rolling backup), and emptying the store deletes the live file outright.
    /// </summary>
    /// <returns>False when the copy couldn't be written; the caller must not delete anything then.</returns>
    public bool TrySnapshot()
    {
        lock (_gate)
        {
            if (!TryRecoverLoad())
            {
                AppLog.Log("LaunchModStore: pre-restore snapshot refused, the record file could not be read");
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string snapshotPath = _path + ".pre-restore";
                string content = SerializeStore();
                // Keep the previous restore's snapshot as a generation instead of overwriting it.
                if (File.Exists(snapshotPath) && File.ReadAllText(snapshotPath) != content)
                {
                    string generation = UniqueStampedPath(snapshotPath);
                    File.Copy(snapshotPath, generation, overwrite: false);
                    PruneTimestamped(snapshotPath, MaxTimestampedBackups, generation);
                }
                AtomicFile.WriteAllText(snapshotPath, content);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Log($"LaunchModStore: pre-restore snapshot failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Re-base a stored snapshot when Steam has updated the app since it was taken — SteamEdit's trick.
    /// Without this, "restore original" would write back launch entries from a version of the game that
    /// no longer exists. Entries that match our own edit are not what Steam shipped, so they never replace
    /// the original: that would leave "restore" with nothing to go back to.
    /// </summary>
    public bool Rebase(int appId, uint currentChangeNumber, IReadOnlyList<LaunchOption> currentOriginal,
                       VdfTable? currentLaunchTable = null)
    {
        lock (_gate)
        {
            if (!TryRecoverLoad() || !_mods.TryGetValue(appId, out var mod) || mod.ChangeNumber == currentChangeNumber)
                return false;

            mod.ChangeNumber = currentChangeNumber;
            if (Differs(currentOriginal, mod.Desired))
            {
                mod.Original = currentOriginal.Select(o => o.Clone()).ToList();
                mod.OriginalLaunchVdf = currentLaunchTable is not null ? EncodeRawLaunch(currentLaunchTable) : null;
            }
            Persist();
            return true;
        }
    }

    /// <summary>Backfill the raw VDF snapshot onto a legacy v1 record while the live appinfo still holds
    /// Steam's unmodified entries, migrating the file to v2 on disk.</summary>
    internal void EnsureOriginalRawLaunch(int appId, VdfTable? currentLaunchTable)
    {
        if (currentLaunchTable is null) return;
        lock (_gate)
        {
            if (!TryRecoverLoad() || !_mods.TryGetValue(appId, out var mod) || mod.OriginalLaunchVdf is not null)
                return;

            mod.OriginalLaunchVdf = EncodeRawLaunch(currentLaunchTable);
            Persist();
        }
    }

    // ── raw VDF snapshot codec ──────────────────────────────────────

    internal static string EncodeRawLaunch(VdfTable launchTable)
    {
        using var ms = new MemoryStream();
        BinaryVdf.Write(ms, launchTable, static _ => 0u, indexedKeys: false);
        return Convert.ToBase64String(ms.ToArray());
    }

    internal static bool TryDecodeRawLaunch(string? base64, [NotNullWhen(true)] out VdfTable? table)
    {
        table = null;
        if (string.IsNullOrWhiteSpace(base64) || base64.Length > MaxRawLaunchBase64Length) return false;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { return false; }

        if (bytes.Length is 0 or > MaxRawLaunchBytes) return false;

        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var parsed = BinaryVdf.ReadStrict(ms);
            if (ms.Position != ms.Length) return false;
            table = parsed;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return false;
        }
    }

    // ── persistence ─────────────────────────────────────────────────

    private void Load()
    {
        _loadFailed = false;
        if (!File.Exists(_path)) return;

        string json;
        try
        {
            json = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Present but not readable right now (locked by an antivirus or a sync client, permissions). That
            // is NOT corruption: quarantining it would move a valid record aside and let the next save start
            // from empty. The file stays where it is and writes are refused until it reads (TryRecoverLoad).
            AppLog.Log($"LaunchModStore: existing file could not be read, left in place: {ex.GetType().Name}: {ex.Message}");
            _mods = [];
            _loadFailed = true;
            return;
        }

        try
        {
            _mods = ParseAndValidate(json, out int skippedCount);
            if (skippedCount > 0)
                PreservePreEditBackup(_path, allowTimestampedFallback: true);
        }
        catch (Exception ex)
        {
            AppLog.Log($"LaunchModStore: existing file unreadable (kept for manual recovery): {ex.GetType().Name}: {ex.Message}");
            // Read but not parseable → keep the file for manual recovery rather than starting empty and
            // overwriting it on the next save. Timestamped so previous .corrupt backups are not overwritten.
            _mods = [];
            // If the move failed the unparseable file is still in place: treat it like a read failure, so no
            // write replaces it and the next access retries the parse and the move.
            _loadFailed = !QuarantineCorruptFile(_path);
        }
    }

    /// <summary>
    /// Retry a load that failed on I/O. False while the file still cannot be read: the in-memory store is
    /// then empty, not the file's contents, so nothing may be written or snapshotted from it.
    /// </summary>
    private bool TryRecoverLoad()
    {
        if (_loadFailed) Load();
        return !_loadFailed;
    }

    /// <summary>
    /// Preserve a backup copy of <paramref name="path"/> without overwriting an existing <c>.bak</c>.
    /// When <paramref name="allowTimestampedFallback"/> is true (used when <see cref="Load"/> skips an
    /// invalid entry) and <c>.bak</c> is already taken, writes a timestamped <c>.bak.&lt;yyyyMMddHHmmss&gt;</c>
    /// copy so the pre-skip file is never lost before a later <see cref="Persist"/> writes the reduced set.
    /// </summary>
    private static void PreservePreEditBackup(string path, bool allowTimestampedFallback)
    {
        try
        {
            if (!File.Exists(path)) return;
            string bakPath = path + ".bak";
            if (!File.Exists(bakPath))
            {
                File.Copy(path, bakPath, overwrite: false);
                return;
            }

            if (!allowTimestampedFallback) return;

            if (MatchesLatestBackup(path, bakPath)) return;

            string generation = UniqueStampedPath(bakPath);
            File.Copy(path, generation, overwrite: false);
            PruneTimestamped(bakPath, MaxTimestampedBackups, generation);
        }
        catch { /* best effort */ }
    }

    private static bool MatchesLatestBackup(string path, string bakPath)
    {
        string latestBackup = TimestampedSiblings(bakPath).FirstOrDefault() ?? bakPath;

        if (!File.Exists(latestBackup)) return false;
        if (new FileInfo(path).Length != new FileInfo(latestBackup).Length) return false;
        return File.ReadAllBytes(path).AsSpan().SequenceEqual(File.ReadAllBytes(latestBackup));
    }

    /// <summary>Move an unparseable file aside; false when it could not be moved and is still in place.</summary>
    private static bool QuarantineCorruptFile(string path)
    {
        try
        {
            File.Move(path, UniqueStampedPath(path + ".corrupt"), overwrite: false);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Log($"LaunchModStore: could not quarantine the unreadable file, left in place: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ── timestamped generations (.bak.<ts>, .pre-restore.<ts>) ───────

    /// <summary>How many <c>&lt;file&gt;.&lt;yyyyMMddHHmmss&gt;</c> generations are kept per base file.</summary>
    internal const int MaxTimestampedBackups = 5;

    private const string StampFormat = "yyyyMMddHHmmss";

    /// <summary><c>&lt;basePath&gt;.&lt;UTC stamp&gt;</c>, with a <c>.&lt;n&gt;</c> suffix if that name is taken.</summary>
    private static string UniqueStampedPath(string basePath)
    {
        string baseDest = $"{basePath}.{DateTime.UtcNow.ToString(StampFormat, CultureInfo.InvariantCulture)}";
        string dest = baseDest;
        int suffix = 1;
        while (File.Exists(dest))
            dest = $"{baseDest}.{suffix++}";
        return dest;
    }

    /// <summary>
    /// The <c>&lt;basePath&gt;.&lt;stamp&gt;[.&lt;n&gt;]</c> siblings, newest first by the UTC stamp IN THE NAME.
    /// Not by <c>LastWriteTime</c>: <see cref="File.Copy(string, string, bool)"/> keeps the source's, so it
    /// says when the content was written, not when the generation was taken. Other names are ignored.
    /// </summary>
    internal static List<string> TimestampedSiblings(string basePath)
    {
        string? dir = Path.GetDirectoryName(basePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return [];

        string prefix = Path.GetFileName(basePath) + ".";
        var found = new List<(string Path, DateTime Stamp, int Suffix)>();
        foreach (string file in Directory.EnumerateFiles(dir, prefix + "*"))
        {
            // Win32 wildcards let "name.*" match "name" itself (an empty extension), so re-check the prefix.
            string fileName = Path.GetFileName(file);
            if (fileName.Length <= prefix.Length || !fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string[] parts = fileName[prefix.Length..].Split('.');
            if (parts.Length > 2
                || !DateTime.TryParseExact(parts[0], StampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime stamp))
                continue;
            int suffix = 0;
            if (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out suffix))
                continue;
            found.Add((file, stamp, suffix));
        }
        return [.. found.OrderByDescending(static f => f.Stamp).ThenByDescending(static f => f.Suffix).Select(static f => f.Path)];
    }

    /// <summary>Delete all but the <paramref name="keep"/> newest generations of <paramref name="basePath"/>.
    /// <paramref name="justCreated"/> is always kept and counts toward <paramref name="keep"/>: after a clock
    /// rollback older generations can carry later stamps, and the copy just taken must not be the one pruned.
    /// Best effort: a file that cannot be deleted is logged and left, never failing the save that pruned.</summary>
    internal static void PruneTimestamped(string basePath, int keep, string? justCreated = null)
    {
        var others = TimestampedSiblings(basePath)
            .Where(p => !string.Equals(p, justCreated, StringComparison.OrdinalIgnoreCase));
        foreach (string old in others.Skip(justCreated is null ? keep : keep - 1))
        {
            try { File.Delete(old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Log($"LaunchModStore: could not prune {Path.GetFileName(old)}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Parse either the v2 envelope (<c>{"Version":2,"Mods":[...]}</c>) or a legacy v1 array
    /// (<c>[...]</c>). Whole-file/envelope corruption throws so the file is quarantined; an individual
    /// invalid entry inside the array is logged and skipped so remaining valid entries survive.</summary>
    private static Dictionary<int, LaunchMod> ParseAndValidate(string json, out int skippedCount)
    {
        skippedCount = 0;
        using var doc = JsonDocument.Parse(json);
        JsonElement entriesArray;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            entriesArray = doc.RootElement;
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (!doc.RootElement.TryGetProperty("Version", out var verEl)
                || verEl.ValueKind != JsonValueKind.Number
                || !verEl.TryGetInt32(out int version)
                || version != CurrentFormatVersion)
                throw new InvalidDataException("Unsupported launchmods.json format version.");

            if (!doc.RootElement.TryGetProperty("Mods", out var modsEl)
                || modsEl.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Missing or invalid Mods array in launchmods.json.");

            entriesArray = modsEl;
        }
        else
        {
            throw new InvalidDataException("Unexpected JSON root in launchmods.json.");
        }

        var map = new Dictionary<int, LaunchMod>();
        int index = 0;
        foreach (var entryEl in entriesArray.EnumerateArray())
        {
            int currentIndex = index++;
            LaunchMod? mod;
            try
            {
                mod = entryEl.Deserialize<LaunchMod>(JsonOpts);
            }
            catch (JsonException ex)
            {
                skippedCount++;
                AppLog.Log($"LaunchModStore: skipping malformed entry at index {currentIndex}: {ex.Message}");
                continue;
            }

            if (mod is null || mod.AppId <= 0 || mod.Original is null || mod.Desired is null)
            {
                skippedCount++;
                AppLog.Log($"LaunchModStore: skipping invalid LaunchMod entry at index {currentIndex} (appId={mod?.AppId ?? 0}).");
                continue;
            }
            if (mod.Original.Any(static o => o is null) || mod.Desired.Any(static o => o is null))
            {
                skippedCount++;
                AppLog.Log($"LaunchModStore: skipping entry with null LaunchOption for app {mod.AppId}.");
                continue;
            }
            if (mod.OriginalLaunchVdf is not null && !TryDecodeRawLaunch(mod.OriginalLaunchVdf, out _))
            {
                skippedCount++;
                AppLog.Log($"LaunchModStore: skipping entry with corrupt OriginalLaunchVdf for app {mod.AppId}.");
                continue;
            }
            if (!map.TryAdd(mod.AppId, mod))
            {
                skippedCount++;
                AppLog.Log($"LaunchModStore: skipping duplicate LaunchMod entry for app {mod.AppId}.");
                continue;
            }
        }

        return map;
    }

    private string SerializeStore() => JsonSerializer.Serialize(
        new StoreEnvelope { Version = CurrentFormatVersion, Mods = _mods.Values.ToList() }, JsonOpts);

    /// <summary>Atomic write via <see cref="AtomicFile"/> with a non-overwriting <c>.bak</c> of the initial
    /// on-disk state, so consecutive saves do not clobber the first migration/recovery snapshot.</summary>
    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (_mods.Count == 0)
            {
                if (File.Exists(_path)) File.Delete(_path);
                return;
            }

            PreservePreEditBackup(_path, allowTimestampedFallback: false);
            AtomicFile.WriteAllText(_path, SerializeStore());
        }
        catch (Exception ex)
        {
            AppLog.Log($"LaunchModStore: save failed: {ex.GetType().Name}: {ex.Message}");
            // Losing the record is bad but must never take the app down mid-edit.
        }
    }

    /// <summary>True when two entry lists differ in any field we manage — used to detect that Steam has
    /// overwritten our edits.</summary>
    public static bool Differs(IReadOnlyList<LaunchOption> a, IReadOnlyList<LaunchOption> b)
    {
        if (a.Count != b.Count) return true;
        for (int i = 0; i < a.Count; i++)
        {
            var (x, y) = (a[i], b[i]);
            if (x.Index != y.Index || x.Executable != y.Executable || x.Arguments != y.Arguments
                || x.WorkingDir != y.WorkingDir || x.Description != y.Description || x.Type != y.Type
                || x.OsList != y.OsList || x.OsArch != y.OsArch
                || x.BetaKey != y.BetaKey || x.OwnsDlc != y.OwnsDlc)
                return true;
        }
        return false;
    }
}
