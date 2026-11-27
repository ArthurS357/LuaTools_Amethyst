using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LuaToolsGui.Services.Backups;

/// <summary>What one game's backup pass did. <see cref="Change"/> is null when it failed.</summary>
public sealed record ManifestBackupOutcome(long AppId, ManifestBackupChange? Change, int ManifestCount, string? Error)
{
    public bool Failed => Error is not null;
}

/// <summary>Every game a batch pass touched, with the totals the Settings page reports.</summary>
public sealed record ManifestBackupSummary(IReadOnlyList<ManifestBackupOutcome> Games)
{
    public int Count(ManifestBackupChange change) => Games.Count(g => g.Change == change);
    public int Failed => Games.Count(static g => g.Failed);
    public int ManifestCount => Games.Sum(static g => g.ManifestCount);

    /// <summary>Games whose lua names no manifest present in depotcache (the zip then holds the lua only).</summary>
    public IReadOnlyList<long> WithoutManifests =>
        [.. Games.Where(static g => !g.Failed && g.ManifestCount == 0).Select(static g => g.AppId)];
}

/// <summary>
/// The file side of the manifest backup: reads <c>stplug-in</c> and <c>depotcache</c>, writes
/// <c>&lt;dest&gt;\&lt;appid&gt;\&lt;appid&gt;.zip</c> + <c>metadata.json</c>, and stages a verified copy back for a restore.
/// </summary>
/// <remarks>
/// <para><b>Steam is only ever read.</b> Every Steam file is opened <see cref="FileAccess.Read"/> with full
/// sharing, so Steam holding a lua or manifest open never blocks it and nothing in the Steam folder changes.</para>
///
/// <para>The zip is flat (<c>&lt;appid&gt;.lua</c> + <c>&lt;depot&gt;_&lt;manifest&gt;.manifest</c>) — exactly what
/// <see cref="LuaInstaller.InstallZip"/> and the drop zone accept, so a backup can be dropped on the app or
/// handed to someone else as is.</para>
///
/// <para>Static and path-driven (no DI types) so the same code serves the app and a one-off export.</para>
/// </remarks>
public static class ManifestBackupStore
{
    public const string MetadataFileName = "metadata.json";
    public const string IndexFileName = "index.json";

    private static readonly DateTime DosEpochCutoffUtc = new(1980, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Back up every installed game. One game failing never stops the others.</summary>
    /// <exception cref="ArgumentException"><paramref name="destRoot"/> is not an acceptable destination.</exception>
    public static ManifestBackupSummary BackupAll(
        string stPlugInDir, string depotCacheDir, string destRoot, Func<long, string?> nameOf,
        IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        EnsureDestination(destRoot, depotCacheDir);
        var games = LuaInstaller.EnumerateInstalled(stPlugInDir).ToList();
        var index = IndexDepotCache(depotCacheDir);
        var outcomes = new List<ManifestBackupOutcome>(games.Count);

        progress?.Report((0, games.Count));
        try
        {
            for (int i = 0; i < games.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (appId, luaPath) = games[i];
                outcomes.Add(BackupGame(appId, luaPath, index, destRoot, nameOf(appId)));
                progress?.Report((i + 1, games.Count));
            }
        }
        finally
        {
            // Also on cancel: the games written so far must show up in the catalog (metadata reads only).
            RefreshIndex(destRoot, () =>
            {
                SweepStaleTemps(destRoot);
                SyncIndex(destRoot);
            });
        }
        return new ManifestBackupSummary(outcomes);
    }

    /// <summary>Back up one game (the post-install hook).</summary>
    /// <exception cref="ArgumentException"><paramref name="destRoot"/> is not an acceptable destination.</exception>
    public static ManifestBackupOutcome BackupOne(
        long appId, string stPlugInDir, string depotCacheDir, string destRoot, string? name)
    {
        EnsureDestination(destRoot, depotCacheDir);
        string luaPath = Path.Combine(stPlugInDir, $"{appId.ToString(CultureInfo.InvariantCulture)}.lua");
        var outcome = BackupGame(appId, luaPath, IndexDepotCache(depotCacheDir), destRoot, name);
        if (!outcome.Failed) RefreshIndex(destRoot, () => UpsertIndex(destRoot, appId));
        return outcome;
    }

    /// <summary>Every well-named manifest in depotcache, grouped by depot. Read once per pass.</summary>
    public static ILookup<long, DepotManifest> IndexDepotCache(string depotCacheDir)
    {
        var all = new List<DepotManifest>();
        if (Directory.Exists(depotCacheDir))
        {
            foreach (var file in new DirectoryInfo(depotCacheDir).EnumerateFiles("*.manifest"))
                if (ManifestBackupPolicy.TryParseManifestName(file.Name, out long depot, out string id))
                    all.Add(new DepotManifest(depot, id, file.FullName, file.LastWriteTimeUtc));
        }
        return all.ToLookup(static m => m.DepotId);
    }

    private static ManifestBackupOutcome BackupGame(
        long appId, string luaPath, ILookup<long, DepotManifest> index, string destRoot, string? name)
    {
        try
        {
            if (!File.Exists(luaPath)) return new(appId, null, 0, "lua not found in stplug-in");

            // The lua is read ONCE: these bytes are parsed, hashed and stored, so a write by Steam or another
            // tool mid-pass cannot leave a zip whose lua disagrees with the recorded hash.
            byte[] luaBytes = ReadShared(luaPath, ManifestBackupPolicy.MaxLuaBytes);
            string luaText = DecodeText(luaBytes);
            if (ManifestBackupPolicy.LuaProblem(luaText) is { } luaProblem)
                return new(appId, null, 0, $"{luaProblem}; not backed up");
            var lua = LuaFileParser.ParseText(luaText, appId);

            // Commented-out pins ("Auto Update Apps") still name the build the lua came with: keep them too.
            var pins = new Dictionary<long, string>(lua.CommentedPins);
            foreach (var (depot, id) in lua.ActivePins) pins[depot] = id;
            var depots = lua.Entries.Select(static e => e.Id).Concat(pins.Keys).Distinct().ToList();
            // An empty, truncated or misnamed depotcache file is treated as absent, so a stored copy of the same
            // manifest is carried instead of being replaced by the damaged one.
            var usable = depots.SelectMany(d => index[d]).Where(m => IsUsableLocal(appId, m)).ToList();
            var selected = ManifestBackupPolicy.SelectManifests(depots, pins, usable);

            string appDir = Path.Combine(destRoot, appId.ToString(CultureInfo.InvariantCulture));
            if (IsLink(appDir)) return new(appId, null, 0, "the backup folder is a link; refused");

            SweepStaleTemps(appDir);
            string zipPath = ZipPath(appDir, appId);
            string luaSha = Convert.ToHexStringLower(SHA256.HashData(luaBytes));
            var stored = ReadMetadata(appDir, appId);
            // An empty, truncated or unrecognised lua says nothing about the game: never overwrite another with it.
            if (depots.Count == 0 && File.Exists(zipPath) &&
                !string.Equals(stored?.LuaSha256, luaSha, StringComparison.OrdinalIgnoreCase))
                return new(appId, null, 0, "the lua declares nothing; stored backup kept");
            // Metadata missing, unreadable or from a newer build: the zip is still the only record of what it holds.
            if (stored is null && File.Exists(zipPath) &&
                ManifestBackupPolicy.SelectCarried(depots, pins, selected, ListManifests(zipPath)).Count > 0)
                return new(appId, null, 0, "unusable metadata.json and the stored zip holds the only copy of a manifest; " +
                    "kept (delete the game's backup folder to rebuild it)");
            DateTime nowUtc = DateTime.UtcNow;
            IReadOnlyList<BackedUpManifest> storedManifests = stored?.Manifests ?? [];
            bool? zipVerified = null;
            var damaged = new HashSet<string>(StringComparer.Ordinal); // stored entries that are not whole manifests
            if (stored is not null && NeedsLegacyTimestampLookup(depots, lua.ActivePins, selected, storedManifests))
                (storedManifests, zipVerified) = InspectLegacyZip(zipPath, appId, stored, nowUtc, damaged);

            var reconciled = ManifestBackupPolicy.Reconcile(
                depots, pins, lua.ActivePins, selected, storedManifests, allowAntiDowngrade: true);

            // Skip-check inspects metadata + file length only (zero zip reads) so cloud-streaming folders
            // like Google Drive / OneDrive are not forced to hydrate every archive on every scan.
            // Full SHA-256 verification remains mandatory and fail-closed on restore and before anti-downgrade
            // suppresses any local depotcache manifest.
            bool zipIntact;
            if (reconciled.PreventedDowngrade)
            {
                zipVerified ??= stored is not null && IsVerifiedStoredZip(zipPath, appId, stored, damaged);
                if (zipVerified == true)
                {
                    // Judge only by stored copies that are whole: a damaged legacy entry in one depot must neither
                    // win nor cost another depot its newer manifest. A damaged entry also forces the cleaning rewrite.
                    if (damaged.Count > 0)
                    {
                        storedManifests = [.. storedManifests.Where(m => !damaged.Contains(ManifestEntryName(m)))];
                        reconciled = ManifestBackupPolicy.Reconcile(
                            depots, pins, lua.ActivePins, selected, storedManifests, allowAntiDowngrade: true);
                    }
                    zipIntact = stored is not null && damaged.Count == 0 && HasExpectedZip(zipPath, stored);
                }
                else
                {
                    reconciled = ManifestBackupPolicy.Reconcile(
                        depots, pins, lua.ActivePins, selected, storedManifests, allowAntiDowngrade: false);
                    zipIntact = false;
                }
            }
            else
            {
                zipIntact = stored is not null && zipVerified != false && HasExpectedZip(zipPath, stored);
            }

            var effectiveSelected = reconciled.Selected;
            var carried = reconciled.Carried;
            var snapshot = effectiveSelected.Concat(carried.Select(static m => new DepotManifest(m.DepotId, m.ManifestId, "", default)));
            var change = ManifestBackupPolicy.Decide(stored, zipIntact, luaSha, snapshot);
            if (change == ManifestBackupChange.Unchanged)
                return new(appId, change, effectiveSelected.Count + carried.Count, null);

            Directory.CreateDirectory(appDir);
            // Read into memory BEFORE the zip they live in is rewritten.
            var kept = ReadCarried(zipPath, carried, nowUtc);
            var recorded = new List<BackedUpManifest>(effectiveSelected.Count + kept.Count);
            // No in-place fallback: a held zip (cloud client, antivirus) fails this pass and stays whole. Hash and size
            // come from the temp file BEFORE the rename, so nothing can fail between the zip landing and its commit
            // marker except the marker itself (no read-back of a file a cloud client may already be streaming).
            string zipSha = "";
            long zipSize = 0;
            AtomicFile.Write(zipPath, p =>
            {
                WriteZip(p, appId, luaBytes, effectiveSelected, kept, recorded, nowUtc);
                zipSha = FileHash.Sha256(p) ?? throw new IOException("the written zip could not be read back");
                zipSize = new FileInfo(p).Length;
            }, inPlaceFallback: false);

            // metadata.json goes LAST: it is the commit marker. A crash before it leaves the old metadata, whose
            // zip hash no longer matches, so a restore refuses the half-written state instead of trusting it.
            var meta = new ManifestBackupMetadata(ManifestBackupMetadata.CurrentSchema, appId, name,
                DateTime.UtcNow, luaSha, zipSha, recorded,
                ZipSizeBytes: zipSize, ManifestCount: recorded.Count, HasManifests: recorded.Count > 0);
            WriteCommitMarker(Path.Combine(appDir, MetadataFileName), JsonSerializer.Serialize(meta, Json));
            return new(appId, change, recorded.Count, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(appId, null, 0, ex.Message);
        }
    }

    /// <summary>The zip has already landed, so a sync client holding metadata.json for a moment would leave the game
    /// unrestorable until the next pass: retry the strict rename briefly before giving up.</summary>
    /// <remarks>ponytail: fixed 3 × 200 ms; make it configurable only if real sync clients are seen holding longer.</remarks>
    private static void WriteCommitMarker(string path, string json)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                AtomicFile.WriteAllText(path, json, inPlaceFallback: false);
                return;
            }
            catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static bool IsUsableLocal(long appId, DepotManifest m)
    {
        if (ManifestFile.IsIntact(m.Path, m.DepotId, m.ManifestId)) return true;
        AppLog.Log($"manifest backup: {appId}: depotcache {Path.GetFileName(m.Path)} is empty, truncated or not " +
                   "the manifest its name claims; skipped");
        return false;
    }

    private sealed record CarriedManifest(BackedUpManifest Manifest, byte[] Bytes, DateTimeOffset LastWrite);

    private static string ManifestEntryName(BackedUpManifest m) =>
        $"{m.DepotId.ToString(CultureInfo.InvariantCulture)}_{m.ManifestId}.manifest";

    /// <summary>The manifests a stored zip holds, by entry name (no hash: only used to decide whether to keep it).</summary>
    private static List<BackedUpManifest> ListManifests(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var found = new List<BackedUpManifest>();
        foreach (var e in zip.Entries)
            if (ManifestBackupPolicy.TryParseManifestName(e.FullName, out long depot, out string id))
                found.Add(new BackedUpManifest(depot, id, ""));
        return found;
    }

    /// <summary>The carried manifests, taken from the stored zip and checked against their recorded SHA-256.
    /// All or nothing: a zip that is absent, unreadable or disagrees with metadata.json (a half-synced cloud
    /// folder) throws, which fails this game's pass and leaves what is stored alone. The one exception, a
    /// hash-matching legacy copy that is not a whole manifest, is dropped (see below).</summary>
    private static List<CarriedManifest> ReadCarried(string zipPath, IReadOnlyList<BackedUpManifest> carried, DateTime nowUtc)
    {
        var kept = new List<CarriedManifest>(carried.Count);
        if (carried.Count == 0) return kept;

        const string Refused = "the stored zip cannot supply a manifest metadata.json records; kept as is";
        if (!File.Exists(zipPath) || IsLink(zipPath)) throw new InvalidDataException(Refused);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var m in carried)
        {
            string name = ManifestEntryName(m);
            if (!LuaInstaller.ManifestNameRegex().IsMatch(name) || zip.GetEntry(name) is not { } entry)
                throw new InvalidDataException(Refused);

            byte[] bytes = ReadEntry(entry, ManifestFile.MaxManifestBytes) ?? throw new InvalidDataException(Refused);
            // A hash mismatch is a torn or tampered zip: fail, keep it as it is. A matching hash on bytes that are
            // not a whole manifest is a legacy copy of a damaged file (stored before the shape check existed): it
            // could never be restored, and keeping it would block every later pass and the restore of the rest, so
            // it is dropped and the rewrite makes the game restorable again.
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), m.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Refused);
            if (!ManifestFile.IsIntact(bytes, m.DepotId, m.ManifestId))
            {
                AppLog.Log($"manifest backup: stored {name} is not a whole manifest (legacy damaged copy); dropped");
                continue;
            }
            var carriedMeta = m.LastWriteUtc is null && entry.LastWriteTime.UtcDateTime > DosEpochCutoffUtc
                ? m with { LastWriteUtc = ClampUtc(entry.LastWriteTime.UtcDateTime, nowUtc) }
                : m;
            kept.Add(new CarriedManifest(carriedMeta, bytes, entry.LastWriteTime));
        }
        return kept;
    }

    private static DateTime ClampUtc(DateTime value, DateTime nowUtc)
    {
        DateTime utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return utc > nowUtc ? nowUtc : utc;
    }

    private static bool NeedsLegacyTimestampLookup(
        IReadOnlyCollection<long> depots,
        IReadOnlyDictionary<long, string> activePins,
        IReadOnlyList<DepotManifest> selected,
        IReadOnlyList<BackedUpManifest> stored)
    {
        if (stored.Count == 0 || selected.Count == 0) return false;

        foreach (long depot in depots)
        {
            if (activePins.ContainsKey(depot)) continue;
            var localIds = selected.Where(s => s.DepotId == depot).Select(static s => s.ManifestId).ToHashSet(StringComparer.Ordinal);
            if (localIds.Count == 0) continue;

            if (stored.Any(m => m.DepotId == depot && m.LastWriteUtc is null && !localIds.Contains(m.ManifestId)))
                return true;
        }
        return false;
    }

    private static (IReadOnlyList<BackedUpManifest> Manifests, bool Verified) InspectLegacyZip(
        string zipPath, long appId, ManifestBackupMetadata stored, DateTime nowUtc, HashSet<string> damaged)
    {
        if (string.IsNullOrWhiteSpace(stored.ZipSha256) || IsLink(zipPath) || !File.Exists(zipPath))
            return (stored.Manifests, false);

        // Let IOException / UnauthorizedAccessException propagate when an existing zip is locked by
        // cloud sync or AV so BackupGame fails safely instead of overwriting the stored backup.
        using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!IsTrustedForAntiDowngrade(fs, appId, stored, damaged))
            return (stored.Manifests, false);

        try
        {
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
            var enriched = new List<BackedUpManifest>(stored.Manifests.Count);
            foreach (var m in stored.Manifests)
            {
                if (m.LastWriteUtc is null &&
                    zip.GetEntry(ManifestEntryName(m)) is { } entry &&
                    entry.LastWriteTime.UtcDateTime is { } utc &&
                    utc > DosEpochCutoffUtc)
                {
                    enriched.Add(m with { LastWriteUtc = ClampUtc(utc, nowUtc) });
                }
                else
                {
                    enriched.Add(m);
                }
            }
            return (enriched, true);
        }
        catch (InvalidDataException)
        {
            return (stored.Manifests, false);
        }
    }

    private static bool IsVerifiedStoredZip(string zipPath, long appId, ManifestBackupMetadata stored, HashSet<string> damaged)
    {
        if (string.IsNullOrWhiteSpace(stored.ZipSha256) || IsLink(zipPath) || !File.Exists(zipPath))
            return false;

        // Propagate IOException / UnauthorizedAccessException on an existing locked zip (fail-closed).
        using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return IsTrustedForAntiDowngrade(fs, appId, stored, damaged);
    }

    /// <summary>
    /// Whether the stored zip may stand in for a local manifest (anti-downgrade): its bytes and every stored
    /// manifest are what metadata.json recorded. NOT the full restore gate: a stored lua that today's screen refuses
    /// is about to be replaced by this pass anyway, and a stored manifest that is not whole is collected in
    /// <paramref name="damaged"/> (the caller judges without it) instead of distrusting every other depot.
    /// A read that fails part-way throws, failing the pass with the stored zip kept, instead of reading as "untrusted"
    /// and letting the older local manifest replace the newer stored one.
    /// </summary>
    private static bool IsTrustedForAntiDowngrade(Stream zip, long appId, ManifestBackupMetadata stored, HashSet<string> damaged) =>
        ArchiveProblem(zip, appId, stored, screenLua: false, damaged) switch
        {
            null => true,
            ZipUnreadable => throw new IOException("the stored zip could not be read to the end; kept as is"),
            _ => false,
        };

    private static void WriteZip(
        string path, long appId, byte[] luaBytes, IReadOnlyList<DepotManifest> manifests,
        IReadOnlyList<CarriedManifest> carried, List<BackedUpManifest> recorded, DateTime nowUtc)
    {
        recorded.Clear(); // AtomicFile may call this twice (temp file, then in place)
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        AddEntry(zip, $"{appId.ToString(CultureInfo.InvariantCulture)}.lua", luaBytes);
        foreach (var m in manifests)
        {
            byte[] bytes = ReadShared(m.Path, ManifestFile.MaxManifestBytes);
            // Checked again on the bytes actually stored: the file may have changed since it was selected.
            if (!ManifestFile.IsIntact(bytes, m.DepotId, m.ManifestId))
                throw new InvalidDataException("a depotcache manifest changed while it was being backed up; stored backup kept");
            DateTime clampedUtc = ClampUtc(m.LastWriteUtc, nowUtc);
            var entry = new BackedUpManifest(
                m.DepotId, m.ManifestId, Convert.ToHexStringLower(SHA256.HashData(bytes)), clampedUtc);
            // The canonical name, the one metadata.json and the restore gate look up ("0123_4.manifest" → "123_4").
            AddEntry(zip, ManifestEntryName(entry), bytes, ToDosTimestamp(clampedUtc));
            recorded.Add(entry);
        }
        foreach (var c in carried)
        {
            AddEntry(zip, ManifestEntryName(c.Manifest), c.Bytes, c.LastWrite);
            recorded.Add(c.Manifest);
        }
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] bytes, DateTimeOffset? lastWrite = null)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        if (lastWrite is { } time) entry.LastWriteTime = time;
        using var s = entry.Open();
        s.Write(bytes);
    }

    /// <summary>Convert a UTC timestamp to a local <see cref="DateTimeOffset"/> clamped to the DOS ZIP range
    /// (1980-01-01 through 2107-12-31). Classic ZIP headers store local wall-clock time without an offset.</summary>
    internal static DateTimeOffset ToDosTimestamp(DateTime utc)
    {
        DateTime local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        var min = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var max = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Local);
        if (local < min) local = min;
        else if (local > max) local = max;
        return new DateTimeOffset(local);
    }

    /// <summary>Metadata-only skip check: avoids opening/hydrating zips on cloud-streaming drives.</summary>
    internal static bool HasExpectedZip(string zipPath, ManifestBackupMetadata stored)
    {
        if (string.IsNullOrWhiteSpace(stored.ZipSha256) || stored.ZipSizeBytes is not > 0 || IsLink(zipPath))
            return false;
        try
        {
            var info = new FileInfo(zipPath);
            return info.Exists && info.Length == stored.ZipSizeBytes.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // ── Discovery catalog (index.json) ──────────────────────────────

    /// <summary>Read the root <c>index.json</c> if present and valid; otherwise null.</summary>
    public static ManifestBackupIndex? ReadIndex(string destRoot)
    {
        try
        {
            if (IsLink(destRoot)) return null;
            string path = Path.Combine(destRoot, IndexFileName);
            if (!File.Exists(path) || IsLink(path)) return null;
            var index = JsonSerializer.Deserialize<ManifestBackupIndex>(File.ReadAllText(path), Json);
            if (index is not { Schema: ManifestBackupIndex.CurrentSchema, Games: not null }) return null;

            foreach (var g in index.Games)
            {
                // "games":[null] (a hand edit, a sync-conflict merge) reads as unusable and is rebuilt, never an NRE.
                if (g is null || g.AppId is <= 0 or > uint.MaxValue
                    || string.IsNullOrWhiteSpace(g.ZipSha256)
                    || string.IsNullOrWhiteSpace(g.ZipFile))
                    return null;
            }
            return index;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Scan every <c>&lt;appid&gt;\metadata.json</c> under <paramref name="destRoot"/> without reading zip bytes.</summary>
    public static ManifestBackupIndex ScanIndex(string destRoot)
    {
        var entries = new List<ManifestBackupIndexEntry>();
        if (Directory.Exists(destRoot) && !IsLink(destRoot))
        {
            foreach (var dir in new DirectoryInfo(destRoot).EnumerateDirectories())
            {
                if (IsLink(dir.FullName)) continue;
                if (TryParseAppDir(dir.Name) is not { } appId) continue;
                if (ReadMetadata(dir.FullName, appId) is not { } meta || string.IsNullOrWhiteSpace(meta.ZipSha256)) continue;
                string zipPath = ZipPath(dir.FullName, appId);
                if (!File.Exists(zipPath) || IsLink(zipPath)) continue;

                entries.Add(ToIndexEntry(meta));
            }
        }
        entries.Sort(static (a, b) => a.AppId.CompareTo(b.AppId));
        return new ManifestBackupIndex(ManifestBackupIndex.CurrentSchema, DateTime.UtcNow, entries);
    }

    /// <summary>Read-only lookup: prefers <c>index.json</c> and falls back to scanning per-app metadata.</summary>
    public static ManifestBackupIndex LoadOrScanIndex(string destRoot) => ReadIndex(destRoot) ?? ScanIndex(destRoot);

    /// <summary>Metadata-only availability check for one <paramref name="appId"/> (zero zip bytes read).</summary>
    public static ManifestBackupAppStatus GetAppStatus(long appId, string destRoot)
    {
        if (appId is <= 0 or > uint.MaxValue || IsLink(destRoot))
            return new(Enabled: true, Available: false, HasManifests: false, ManifestCount: 0, UpdatedAtUtc: null);

        string appDir = Path.Combine(destRoot, appId.ToString(CultureInfo.InvariantCulture));
        string zip = ZipPath(appDir, appId);
        if (IsLink(appDir) || IsLink(zip) || !File.Exists(zip))
            return new(Enabled: true, Available: false, HasManifests: false, ManifestCount: 0, UpdatedAtUtc: null);

        if (ReadMetadata(appDir, appId) is not { } meta || string.IsNullOrWhiteSpace(meta.ZipSha256))
            return new(Enabled: true, Available: false, HasManifests: false, ManifestCount: 0, UpdatedAtUtc: null);

        return new(Enabled: true, Available: true,
            HasManifests: meta.EffectiveHasManifests,
            ManifestCount: meta.EffectiveManifestCount,
            UpdatedAtUtc: meta.UpdatedAtUtc);
    }

    private static ManifestBackupIndexEntry ToIndexEntry(ManifestBackupMetadata meta)
    {
        string id = meta.AppId.ToString(CultureInfo.InvariantCulture);
        int count = meta.EffectiveManifestCount;
        return new(meta.AppId, meta.Name, meta.UpdatedAtUtc, count, meta.EffectiveHasManifests,
            $"{id}/{id}.zip", meta.ZipSha256);
    }

    /// <summary>index.json is a derived catalog: failing to write it never fails a backup that already landed.
    /// A stale one is removed when possible, so readers fall back to scanning the metadata files.</summary>
    private static void RefreshIndex(string destRoot, Action write)
    {
        try { write(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Log($"manifest backup: index.json not updated: {ex.GetType().Name}: {LogSanitizer.Sanitize(ex.Message)}");
            string path = Path.Combine(destRoot, IndexFileName);
            try { if (!IsLink(path)) File.Delete(path); }
            catch (Exception del) when (del is IOException or UnauthorizedAccessException) { /* next pass rewrites it */ }
        }
    }

    private static void SyncIndex(string destRoot)
    {
        Directory.CreateDirectory(destRoot);
        string indexPath = Path.Combine(destRoot, IndexFileName);
        if (IsLink(indexPath)) throw new IOException("index.json is a link; refused");

        var scanned = ScanIndex(destRoot);
        if (ReadIndex(destRoot) is { } existing && existing.Games.SequenceEqual(scanned.Games)) return;

        AtomicFile.WriteAllText(indexPath, JsonSerializer.Serialize(scanned, Json));
    }

    private static void UpsertIndex(string destRoot, long appId)
    {
        string appDir = Path.Combine(destRoot, appId.ToString(CultureInfo.InvariantCulture));
        if (ReadMetadata(appDir, appId) is not { } meta || string.IsNullOrWhiteSpace(meta.ZipSha256)) return;

        if (ReadIndex(destRoot) is not { } existing)
        {
            SyncIndex(destRoot);
            return;
        }

        var updatedEntry = ToIndexEntry(meta);
        var current = existing.Games.FirstOrDefault(g => g.AppId == appId);
        if (current == updatedEntry) return;

        string indexPath = Path.Combine(destRoot, IndexFileName);
        if (IsLink(indexPath)) throw new IOException("index.json is a link; refused");

        var next = existing.Games.Where(g => g.AppId != appId).Append(updatedEntry)
            .OrderBy(static g => g.AppId).ToList();
        var nextIndex = new ManifestBackupIndex(ManifestBackupIndex.CurrentSchema, DateTime.UtcNow, next);
        AtomicFile.WriteAllText(indexPath, JsonSerializer.Serialize(nextIndex, Json));
    }

    // ── Restore ─────────────────────────────────────────────────────

    /// <summary>
    /// Copy a game's backup zip to <paramref name="stagingDir"/> and verify THE COPY through a pin
    /// (<see cref="AssetIntegrity.OpenPinned"/>, read-share only) that the caller keeps until the install has
    /// read it: nothing can swap the bytes between the check and the extraction. Null when there is no backup
    /// or it fails any check (fail-closed).
    /// </summary>
    public static (string Path, FileStream Pin)? StageVerifiedRestore(long appId, string destRoot, string stagingDir)
    {
        if (appId is <= 0 or > uint.MaxValue || IsLink(destRoot)) return null;
        string appDir = Path.Combine(destRoot, appId.ToString(CultureInfo.InvariantCulture));
        string zip = ZipPath(appDir, appId);
        if (IsLink(appDir) || !File.Exists(zip) || IsLink(zip)) return null;
        if (ReadMetadata(appDir, appId) is not { } meta) return null;

        Directory.CreateDirectory(stagingDir);
        string staged = Path.Combine(stagingDir, $"luatools_backup_{Guid.NewGuid():N}_{appId}.zip");

        FileStream? pin = null;
        try
        {
            File.Copy(zip, staged);
            pin = AssetIntegrity.OpenPinned(staged);
            if (VerifyArchive(pin, appId, meta)) return (staged, pin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unverifiable = refused */ }

        pin?.Dispose();
        try { File.Delete(staged); } catch (IOException) { /* temp file, swept later */ }
        return null;
    }

    /// <summary>
    /// The restore gate: the zip must hash to the recorded SHA-256, hold exactly one <c>&lt;appid&gt;.lua</c>,
    /// and nothing but root-level lua/manifest entries — no folders, no "..", no drive letters, no symlink
    /// entries. Every entry must also agree with metadata.json (same manifest set, same per-file SHA-256) and be
    /// sound: the lua passes the installer's screen, each manifest is whole and the one its name claims (a
    /// damaged manifest restored into depotcache would stick there). Any doubt refuses the whole archive.
    /// (The installer re-checks entry names on extraction too.) Leaves <paramref name="zip"/> open and rewound.
    /// </summary>
    public static bool VerifyArchive(Stream zip, long appId, ManifestBackupMetadata meta) =>
        ArchiveProblem(zip, appId, meta) is null;

    /// <summary>The <see cref="ArchiveProblem"/> for a read that failed (I/O), as opposed to bytes that are wrong.</summary>
    internal const string ZipUnreadable = "the zip could not be read to the end";

    /// <summary><see cref="VerifyArchive"/> with the reason it refused, or null when the archive passes.</summary>
    /// <param name="damaged">When given, a manifest whose hash matches but that is not whole is added here instead of
    /// refusing the archive (anti-downgrade only; the restore gate always refuses it).</param>
    internal static string? ArchiveProblem(
        Stream zip, long appId, ManifestBackupMetadata meta, bool screenLua = true, HashSet<string>? damaged = null)
    {
        if (string.IsNullOrWhiteSpace(meta.ZipSha256) || meta.AppId != appId) return "metadata.json does not describe this game";

        try
        {
            // M5 (accepted operator decision): metadata.json carries a plain zipSha256, no HMAC, so backups stay
            // portable across PCs via Google Drive/OneDrive and shareable between users.
            // The hash proves integrity (torn sync, corruption, a changed zip), NOT authorship: whoever can write
            // the folder, including anyone a cloud folder is shared with, can replace zip and metadata together.
            // What still bounds such a zip: the entry whitelist, symlink refusal, and the installer's own lua
            // screening. The pinned copy keeps the checked bytes and the installed bytes the same.
            zip.Position = 0;
            string sha = Convert.ToHexStringLower(SHA256.HashData(zip));
            if (!string.Equals(sha, meta.ZipSha256, StringComparison.OrdinalIgnoreCase))
                return "the zip does not match the SHA-256 in metadata.json";

            var expected = new Dictionary<string, BackedUpManifest>(StringComparer.Ordinal);
            foreach (var m in meta.Manifests)
                if (!expected.TryAdd(ManifestEntryName(m), m)) return "metadata.json lists a manifest twice";

            zip.Position = 0;
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            int luas = 0;
            foreach (var e in archive.Entries)
            {
                const int UnixTypeMask = 0xF000, UnixSymlink = 0xA000;
                if (((e.ExternalAttributes >> 16) & UnixTypeMask) == UnixSymlink) return "the zip holds a symlink entry";
                if (!ManifestBackupPolicy.IsAllowedEntry(e.FullName, appId)) return "the zip holds an entry a backup never has";
                if (EntryProblem(e, meta, expected, screenLua) is { } problem)
                {
                    if (damaged is null || problem != NotWhole(e.FullName)) return problem;
                    damaged.Add(e.FullName);
                }
                if (e.FullName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) luas++;
            }
            if (luas != 1) return "the zip does not hold exactly one lua";
            return expected.Count == 0 ? null : "metadata.json records a manifest the zip does not hold";
        }
        catch (InvalidDataException) { return "the zip is damaged"; }
        catch (IOException) { return ZipUnreadable; }
        finally { if (zip.CanSeek) zip.Position = 0; }
    }

    /// <summary>One allowed entry checked against metadata.json and for soundness. Removes a matched manifest
    /// from <paramref name="expected"/>, so a duplicate or unrecorded one is refused.</summary>
    private static string? EntryProblem(
        ZipArchiveEntry e, ManifestBackupMetadata meta, Dictionary<string, BackedUpManifest> expected, bool screenLua)
    {
        if (e.FullName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
        {
            if (ReadEntry(e, ManifestBackupPolicy.MaxLuaBytes) is not { } lua) return "the lua is larger than a backup accepts";
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(lua)), meta.LuaSha256, StringComparison.OrdinalIgnoreCase))
                return "the lua does not match the SHA-256 in metadata.json";
            return screenLua ? ManifestBackupPolicy.LuaProblem(DecodeText(lua)) : null;
        }

        if (!expected.Remove(e.FullName, out var recorded)) return $"{e.FullName} is not recorded in metadata.json";
        if (ReadEntry(e, ManifestFile.MaxManifestBytes) is not { } bytes) return $"{e.FullName} is larger than a backup accepts";
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), recorded.Sha256, StringComparison.OrdinalIgnoreCase))
            return $"{e.FullName} does not match its SHA-256 in metadata.json";
        return ManifestFile.IsIntact(bytes, recorded.DepotId, recorded.ManifestId) ? null : NotWhole(e.FullName);
    }

    private static string NotWhole(string entry) => $"{entry} is empty, truncated or not the manifest its name claims";

    /// <summary>Drop <c>AtomicFile</c> temps (<c>*.&lt;guid&gt;.tmp</c>) a killed pass left in the synced folder.
    /// An hour old at least, so a pass still writing is never touched.</summary>
    private static void SweepStaleTemps(string appDir)
    {
        if (!Directory.Exists(appDir)) return;
        foreach (var tmp in new DirectoryInfo(appDir).EnumerateFiles("*.tmp"))
        {
            if (tmp.LastWriteTimeUtc > DateTime.UtcNow.AddHours(-1)) continue;
            try { tmp.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next pass */ }
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>The appid a vault sub-folder is for, or null. Canonical digits only: "0570" is not 570's folder
    /// (every path here is built from the canonical form), so it must not be listed as if it were.</summary>
    internal static long? TryParseAppDir(string name) =>
        long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id is > 0 and <= uint.MaxValue
        && name == id.ToString(CultureInfo.InvariantCulture) ? id : null;

    internal static string ZipPath(string appDir, long appId) =>
        Path.Combine(appDir, $"{appId.ToString(CultureInfo.InvariantCulture)}.zip");

    /// <summary>The stored metadata, or null when missing, unreadable, from another schema or another app.</summary>
    public static ManifestBackupMetadata? ReadMetadata(string appDir, long appId)
    {
        try
        {
            string path = Path.Combine(appDir, MetadataFileName);
            if (!File.Exists(path) || IsLink(path)) return null;
            var meta = JsonSerializer.Deserialize<ManifestBackupMetadata>(File.ReadAllText(path), Json);
            // Every manifest entry must be whole ("manifests":[null] or a nulled field would NRE later, in the middle
            // of a batch): anything else reads as unusable metadata, which the callers already treat fail-closed.
            return meta is { Schema: ManifestBackupMetadata.CurrentSchema, Manifests: not null, LuaSha256: not null }
                   && meta.AppId == appId
                   && meta.Manifests.All(static m => m is { Sha256.Length: > 0 }
                                                     && ManifestBackupPolicy.TryParseManifestName(ManifestEntryName(m), out _, out _))
                ? meta : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static void EnsureDestination(string destRoot, string depotCacheDir)
    {
        if (!ManifestBackupPolicy.IsAcceptableDestination(destRoot, Path.GetDirectoryName(depotCacheDir)) ||
            IsLink(destRoot))
            throw new ArgumentException("The manifest backup folder is not an acceptable destination.", nameof(destRoot));
        // An unmounted drive (Google Drive not running, USB disk unplugged) fails the pass once, up front, instead
        // of every game failing with a path error.
        if (Path.GetPathRoot(destRoot) is { Length: > 0 } root && !Directory.Exists(root))
            throw new IOException("The manifest backup folder's drive is not available (offline or not mounted).");
    }

    /// <summary>True for a symlink or junction. Deliberately NOT every reparse point: OneDrive / Google Drive
    /// placeholders are reparse points too (cloud tags), and a synced folder is the main use of this feature.</summary>
    internal static bool IsLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.Exists && info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>Read a Steam file without ever taking a write handle or blocking Steam's own. Over
    /// <paramref name="max"/> bytes it is refused (memory bound), which fails the game's pass, not the batch.</summary>
    private static byte[] ReadShared(string path, int max)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ManifestFile.ReadBounded(fs, max)
               ?? throw new InvalidDataException($"{Path.GetFileName(path)} is larger than the backup accepts");
    }

    /// <summary>A zip entry's bytes, or null past <paramref name="max"/> (decompressed, whatever its header says).</summary>
    private static byte[]? ReadEntry(ZipArchiveEntry entry, int max)
    {
        if (entry.Length > max) return null;
        using var s = entry.Open();
        return ManifestFile.ReadBounded(s, max);
    }

    private static string DecodeText(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
