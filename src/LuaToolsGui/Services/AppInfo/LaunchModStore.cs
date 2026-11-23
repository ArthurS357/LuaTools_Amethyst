using System.Diagnostics.CodeAnalysis;
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

    private readonly string _path;

    public LaunchModStore() : this(FilePath) { }

    /// <summary>Test seam: point the store at a throwaway file.</summary>
    internal LaunchModStore(string path)
    {
        _path = path;
        Load();
    }

    public bool IsEmpty { get { lock (_gate) return _mods.Count == 0; } }

    public IReadOnlyList<LaunchMod> All { get { lock (_gate) return _mods.Values.ToList(); } }

    public LaunchMod? Get(int appId)
    {
        lock (_gate) return _mods.GetValueOrDefault(appId);
    }

    /// <summary>Record an edit. The snapshot is only captured the FIRST time a game is modified —
    /// re-saving must not overwrite the original with our own previous edit.</summary>
    public void Save(int appId, uint changeNumber, IReadOnlyList<LaunchOption> current,
                     IReadOnlyList<LaunchOption> desired, VdfTable? originalLaunchTable = null)
    {
        lock (_gate)
        {
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
            if (_mods.Remove(appId)) Persist();
        }
    }

    /// <summary>Forget several edits in one write, so a batch can't leave the file half-updated.</summary>
    public void RemoveMany(IEnumerable<int> appIds)
    {
        lock (_gate)
        {
            bool any = false;
            foreach (int appId in appIds) any |= _mods.Remove(appId);
            if (any) Persist();
        }
    }

    /// <summary>
    /// Copy the current records to <c>launchmods.json.pre-restore</c> before a restore starts deleting
    /// them. <see cref="Persist"/>'s .bak can't do this job: emptying the store deletes the file outright,
    /// so after a full restore the .bak holds the state from one removal earlier, or nothing.
    /// </summary>
    /// <returns>False when the copy couldn't be written; the caller must not delete anything then.</returns>
    public bool TrySnapshot()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                AtomicFile.WriteAllText(_path + ".pre-restore", SerializeStore());
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
            if (!_mods.TryGetValue(appId, out var mod) || mod.ChangeNumber == currentChangeNumber)
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
            if (!_mods.TryGetValue(appId, out var mod) || mod.OriginalLaunchVdf is not null)
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
        try
        {
            if (!File.Exists(_path)) return;
            _mods = ParseAndValidate(File.ReadAllText(_path));
        }
        catch (Exception ex)
        {
            AppLog.Log($"LaunchModStore: existing file unreadable (kept for manual recovery): {ex.GetType().Name}: {ex.Message}");
            // Present but unreadable → keep the file for manual recovery rather than starting empty and
            // overwriting it on the next save. Timestamped so previous .corrupt backups are not overwritten.
            QuarantineCorruptFile(_path);
            _mods = [];
        }
    }

    private static void QuarantineCorruptFile(string path)
    {
        try
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string baseDest = $"{path}.corrupt.{stamp}";
            string dest = baseDest;
            int suffix = 1;
            while (File.Exists(dest))
                dest = $"{baseDest}.{suffix++}";

            File.Move(path, dest, overwrite: false);
        }
        catch { /* best effort */ }
    }

    /// <summary>Parse either the v2 envelope (<c>{"Version":2,"Mods":[...]}</c>) or a legacy v1 array
    /// (<c>[...]</c>). Whole-file/envelope corruption throws so the file is quarantined; an individual
    /// invalid entry inside the array is logged and skipped so remaining valid entries survive.</summary>
    private static Dictionary<int, LaunchMod> ParseAndValidate(string json)
    {
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
                AppLog.Log($"LaunchModStore: skipping malformed entry at index {currentIndex}: {ex.Message}");
                continue;
            }

            if (mod is null || mod.AppId <= 0 || mod.Original is null || mod.Desired is null)
            {
                AppLog.Log($"LaunchModStore: skipping invalid LaunchMod entry at index {currentIndex} (appId={mod?.AppId ?? 0}).");
                continue;
            }
            if (mod.Original.Any(static o => o is null) || mod.Desired.Any(static o => o is null))
            {
                AppLog.Log($"LaunchModStore: skipping entry with null LaunchOption for app {mod.AppId}.");
                continue;
            }
            if (mod.OriginalLaunchVdf is not null && !TryDecodeRawLaunch(mod.OriginalLaunchVdf, out _))
            {
                AppLog.Log($"LaunchModStore: skipping entry with corrupt OriginalLaunchVdf for app {mod.AppId}.");
                continue;
            }
            if (!map.TryAdd(mod.AppId, mod))
            {
                AppLog.Log($"LaunchModStore: skipping duplicate LaunchMod entry for app {mod.AppId}.");
                continue;
            }
        }

        return map;
    }

    private string SerializeStore() => JsonSerializer.Serialize(
        new StoreEnvelope { Version = CurrentFormatVersion, Mods = _mods.Values.ToList() }, JsonOpts);

    /// <summary>Atomic write via <see cref="AtomicFile"/> with a .bak, same as SettingsService — a torn file
    /// here loses every recorded edit AND the snapshots needed to undo them.</summary>
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

            try { if (File.Exists(_path)) File.Copy(_path, _path + ".bak", overwrite: true); }
            catch { /* best effort */ }
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
