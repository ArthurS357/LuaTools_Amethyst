using System.IO;

namespace LuaToolsGui.Services;

/// <summary>What a migration pass did. All counts, no exceptions: the pass is best-effort by design.</summary>
/// <param name="Moved">Manifests relocated into the real depotcache, where Steam can finally see them.</param>
/// <param name="AlreadyPresent">Skipped because the real folder already had that exact file.</param>
/// <param name="Rejected">Skipped because the file is not a valid manifest matching its own name.</param>
/// <param name="Failed">Tried and failed (locked, permissions, disk).</param>
public readonly record struct DepotCacheMigrationResult(int Moved, int AlreadyPresent, int Rejected, int Failed)
{
    /// <summary>True when nothing at all was found to consider, so there is nothing worth reporting.</summary>
    public bool IsEmpty => Moved == 0 && AlreadyPresent == 0 && Rejected == 0 && Failed == 0;
}

/// <summary>
/// Moves manifests out of the old, wrong <c>config\depotcache</c> into the real <c>depotcache</c>.
/// </summary>
/// <remarks>
/// <para>
/// Until 1.7.3 this app wrote every <c>.manifest</c> to <c>&lt;Steam&gt;\config\depotcache</c>. Steam reads
/// <c>&lt;Steam&gt;\depotcache</c> — a sibling of steamapps, not a child of config. See
/// <see cref="SteamService.DepotCacheDir"/> for the evidence. A manifest in the wrong folder is invisible
/// to Steam, so a pinned depot resolves to nothing and the download simply never starts, with no error
/// shown anywhere. Fixing the path only helps future writes; anything already installed stays stranded
/// until it is moved, which is what this does.
/// </para>
/// <para>
/// Deliberately silent. There is no toast and no setting: the user did nothing wrong, there is nothing for
/// them to decide, and a repair that needs explaining is worse than one that just happens. It logs, and
/// only when it actually moved something.
/// </para>
/// <para>
/// Safe to run on every launch. It is idempotent, never overwrites, and touches nothing outside the two
/// depotcache folders.
/// </para>
/// </remarks>
public class DepotCacheMigrationService(SteamService steam)
{
    /// <summary>Run the pass off the calling thread. Never throws.</summary>
    public Task<DepotCacheMigrationResult> RunAsync(CancellationToken ct = default) =>
        Task.Run(() => Run(ct), ct);

    /// <summary>Run the pass against the located Steam install. Never throws.</summary>
    public DepotCacheMigrationResult Run(CancellationToken ct = default) =>
        steam.LegacyDepotCacheDir is { } legacyDir && steam.DepotCacheDir is { } realDir
            ? Migrate(legacyDir, realDir, ct)
            : default; // Steam not located

    /// <summary>
    /// The whole pass, against two explicit folders. Never throws — every failure is counted and the
    /// sweep continues, because one locked file must not strand the rest.
    /// </summary>
    public static DepotCacheMigrationResult Migrate(
        string legacyDir, string realDir, CancellationToken ct = default)
    {
        // Paranoia, not a real case: if these ever resolved to the same folder, moving a file onto itself
        // would delete it.
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyDir)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(realDir)),
                StringComparison.OrdinalIgnoreCase))
            return default;

        string[] files;
        try
        {
            if (!Directory.Exists(legacyDir)) return default; // the common case after the first run
            files = Directory.GetFiles(legacyDir, "*.manifest");
        }
        catch
        {
            return default; // unreadable legacy folder: nothing to do, and nothing worth logging
        }

        if (files.Length == 0)
        {
            TryRemoveIfEmpty(legacyDir);
            return default;
        }

        int moved = 0, present = 0, rejected = 0, failed = 0;

        foreach (string src in files)
        {
            if (ct.IsCancellationRequested) break;

            string name = Path.GetFileName(src);
            string dest = Path.Combine(realDir, name);

            // The name is content-addressed (<depot>_<gid>), so a file already there IS this file. Leave
            // the stale copy alone rather than deleting it: reclaiming disk is not worth a destructive
            // step in a silent startup task.
            try { if (File.Exists(dest)) { present++; continue; } }
            catch { failed++; continue; }

            // Only move something that really is the manifest its name claims. A truncated or half-written
            // file that reaches the real depotcache is sticky — LuaInstaller skips a destination that
            // exists, so a bad entry would survive every later fetch and break that depot permanently.
            // Better to strand it here, where it already was and where it harms nothing.
            if (!ParseName(name, out long depotId, out string gid) || !ManifestFile.Matches(src, depotId, gid))
            {
                rejected++;
                continue;
            }

            try
            {
                Directory.CreateDirectory(realDir); // only once there is something to put in it
                File.Move(src, dest);
                moved++;
            }
            catch
            {
                failed++;
            }
        }

        if (moved > 0)
            AppLog.Log(
                $"depotcache migration: moved {moved} stranded manifest(s) into the real depotcache " +
                $"({present} already there, {rejected} not valid, {failed} failed)");

        TryRemoveIfEmpty(legacyDir);

        return new DepotCacheMigrationResult(moved, present, rejected, failed);
    }

    /// <summary>
    /// Split <c>&lt;depot&gt;_&lt;gid&gt;.manifest</c>. Returns false for anything else, which is the
    /// point: an unrecognised name cannot be validated, so it is not moved.
    /// </summary>
    private static bool ParseName(string fileName, out long depotId, out string gid)
    {
        depotId = 0;
        gid = "";

        string stem = Path.GetFileNameWithoutExtension(fileName);
        int split = stem.IndexOf('_');
        if (split <= 0 || split == stem.Length - 1) return false;

        if (!long.TryParse(stem[..split], out depotId)) return false;

        gid = stem[(split + 1)..];
        return ulong.TryParse(gid, out _);
    }

    /// <summary>
    /// Drop the legacy folder once it holds nothing, so a fully migrated install stops scanning it.
    /// Only ever deletes an empty directory.
    /// </summary>
    private static void TryRemoveIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { /* best effort: an un-removable empty folder costs one cheap scan next launch */ }
    }
}
