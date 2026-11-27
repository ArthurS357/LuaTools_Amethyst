using System.Globalization;
using System.IO;

namespace LuaToolsGui.Services.Backups;

/// <summary>Health of one game's backup, as <see cref="ManifestVaultVerifier"/> found it.</summary>
public enum ManifestVaultHealth
{
    /// <summary>Restorable: lua and every manifest pass the restore gate.</summary>
    Healthy,

    /// <summary>Restorable, but holds the lua only (no depotcache manifest was ever backed up).</summary>
    LuaOnly,

    /// <summary>Would be refused on restore: hash mismatch, damaged entry, unusable metadata.json, missing zip.</summary>
    Corrupt,

    /// <summary>Could not be read right now (held by a cloud client or antivirus, or the drive went away).
    /// Not a verdict on the data: run the check again.</summary>
    Unreadable,
}

/// <summary>One game in a <see cref="ManifestVaultReport"/>. <see cref="Problem"/> says why it is not healthy.</summary>
public sealed record ManifestVaultGame(long AppId, ManifestVaultHealth Health, int ManifestCount, string? Problem);

/// <summary>What a whole-vault check found. index.json drift and leftover temps are reported, not errors: the
/// next backup pass rebuilds the catalog and sweeps the temps on its own.</summary>
public sealed record ManifestVaultReport(
    IReadOnlyList<ManifestVaultGame> Games,
    bool IndexReadable,
    IReadOnlyList<long> MissingFromIndex,
    IReadOnlyList<long> StaleInIndex,
    int OrphanTemps)
{
    public int Count(ManifestVaultHealth health) => Games.Count(g => g.Health == health);

    /// <summary>The games a restore would refuse or could not reach, in appid order.</summary>
    public IReadOnlyList<long> NeedsAttention =>
        [.. Games.Where(static g => g.Health is ManifestVaultHealth.Corrupt or ManifestVaultHealth.Unreadable)
                 .Select(static g => g.AppId)];
}

/// <summary>
/// Read-only health check of a manifest backup vault: every <c>&lt;appid&gt;.zip</c> through the same gate a
/// restore uses (<see cref="ManifestBackupStore.VerifyArchive"/>), plus index.json against the disk.
/// </summary>
/// <remarks>
/// <para><b>Never writes.</b> Files are opened <see cref="FileAccess.Read"/>; nothing is created, swept or rebuilt,
/// so it is safe to point at a vault a cloud client is syncing.</para>
/// <para><b>Hydrates every zip</b> on a streaming cloud folder (it has to read the bytes to hash them). That is
/// why it only ever runs when asked, never as part of a backup pass, whose skip-check stays metadata-only.</para>
/// </remarks>
public static class ManifestVaultVerifier
{
    /// <summary>Check one game's backup folder.</summary>
    public static ManifestVaultGame VerifyGame(string destRoot, long appId)
    {
        string appDir = Path.Combine(destRoot, appId.ToString(CultureInfo.InvariantCulture));
        string zipPath = ManifestBackupStore.ZipPath(appDir, appId);
        try
        {
            if (ManifestBackupStore.IsLink(appDir) || ManifestBackupStore.IsLink(zipPath))
                return Corrupt(appId, "the backup folder or zip is a link");
            if (ManifestBackupStore.ReadMetadata(appDir, appId) is not { } meta)
                return File.Exists(Path.Combine(appDir, ManifestBackupStore.MetadataFileName))
                    ? Corrupt(appId, "metadata.json is unreadable or malformed")
                    : Corrupt(appId, "metadata.json is missing");
            if (!File.Exists(zipPath)) return Corrupt(appId, "the zip is missing");

            using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (ManifestBackupStore.ArchiveProblem(fs, appId, meta) is { } problem)
                return problem == ManifestBackupStore.ZipUnreadable
                    ? new(appId, ManifestVaultHealth.Unreadable, 0, problem)
                    : Corrupt(appId, problem);
            return new(appId, meta.Manifests.Count > 0 ? ManifestVaultHealth.Healthy : ManifestVaultHealth.LuaOnly,
                meta.Manifests.Count, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(appId, ManifestVaultHealth.Unreadable, 0, ex.GetType().Name);
        }
    }

    /// <summary>Check every game folder under <paramref name="destRoot"/> and index.json against them.</summary>
    /// <param name="verifyGame">How one game is checked; the service wraps <see cref="VerifyGame"/> in its pass gate
    /// per game, so a long cloud verify never holds back a restore for minutes.</param>
    /// <exception cref="IOException">The vault's drive is offline, the vault is a link, or it cannot be listed:
    /// never an empty "all clean" report for a vault that was not looked at.</exception>
    public static ManifestVaultReport VerifyVault(
        string destRoot, IProgress<(int Done, int Total)>? progress, CancellationToken ct,
        Func<long, ManifestVaultGame>? verifyGame = null)
    {
        if (Path.GetPathRoot(destRoot) is { Length: > 0 } root && !Directory.Exists(root))
            throw new IOException("The manifest backup folder's drive is not available (offline or not mounted).");
        if (ManifestBackupStore.IsLink(destRoot)) throw new IOException("The manifest backup folder is a link; refused.");
        verifyGame ??= id => VerifyGame(destRoot, id);

        var appIds = new List<long>();
        int temps = CountOrphanTemps(destRoot);
        if (Directory.Exists(destRoot)) // a folder not created yet is a truthful empty vault
        {
            foreach (var dir in new DirectoryInfo(destRoot).EnumerateDirectories())
            {
                if (ManifestBackupStore.TryParseAppDir(dir.Name) is { } id)
                {
                    appIds.Add(id);
                    temps += CountOrphanTemps(dir.FullName);
                }
            }
        }
        appIds.Sort();

        var games = new List<ManifestVaultGame>(appIds.Count);
        progress?.Report((0, appIds.Count));
        foreach (long id in appIds)
        {
            ct.ThrowIfCancellationRequested();
            games.Add(verifyGame(id));
            progress?.Report((games.Count, appIds.Count));
        }

        // The catalog as discovery would serve it, against what the per-game metadata says now.
        var scanned = ManifestBackupStore.ScanIndex(destRoot).Games.ToDictionary(static g => g.AppId);
        var index = ManifestBackupStore.ReadIndex(destRoot);
        bool readable = index is not null || !File.Exists(Path.Combine(destRoot, ManifestBackupStore.IndexFileName));
        var listed = new HashSet<long>();
        var stale = new SortedSet<long>();
        foreach (var g in index?.Games ?? [])
            if (!listed.Add(g.AppId) || !scanned.TryGetValue(g.AppId, out var now) || now != g)
                stale.Add(g.AppId); // listed twice, gone from disk, or describing an older zip
        IReadOnlyList<long> missing = index is null ? [] : [.. scanned.Keys.Where(id => !listed.Contains(id)).Order()];

        return new ManifestVaultReport(games, readable, missing, [.. stale], temps);
    }

    private static ManifestVaultGame Corrupt(long appId, string problem) =>
        new(appId, ManifestVaultHealth.Corrupt, 0, problem);

    /// <summary>AtomicFile temps (<c>*.tmp</c>) older than an hour: a killed pass left them; younger may be in flight.</summary>
    private static int CountOrphanTemps(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            DateTime cutoff = DateTime.UtcNow.AddHours(-1);
            return new DirectoryInfo(dir).EnumerateFiles("*.tmp").Count(f => f.LastWriteTimeUtc <= cutoff);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
