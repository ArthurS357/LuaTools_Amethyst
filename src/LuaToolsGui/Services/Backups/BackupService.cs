using System.IO;

namespace LuaToolsGui.Services.Backups;

/// <summary>Outcome of a cleanup. <see cref="Refused"/> means the whole batch was rejected before anything
/// was touched; otherwise <see cref="Errors"/> names the folders that could not be removed.</summary>
public sealed record BackupDeleteResult(int DeletedCount, long BytesFreed, IReadOnlyList<string> Errors, bool Refused);

/// <summary>
/// Finds and deletes the backup folders and loose .bak files <see cref="BackupInventory"/> recognises,
/// directly under the Steam root and nowhere else.
///
/// <para>
/// <b>Never follows a reparse point.</b> A junction or symlink named like a backup is skipped on scan and
/// refuses the batch on delete; inside a folder, sizes are summed without entering links, and
/// <see cref="DeleteTree"/> detaches a nested link without deleting what it points at (pinned by
/// <c>BackupServiceTests</c>).
/// </para>
/// </summary>
public sealed class BackupService
{
    /// <summary>
    /// A backup younger than this is refused on delete. An AmethystTool install creates its backup folder and
    /// then moves the displaced files into it, and AmethystTool's updater writes its .bak while it runs, so a
    /// very recent entry may belong to an install still in progress; deleting it would lose the only copy of
    /// what was replaced. Five minutes is far longer than any install and costs a user who really wants it
    /// gone one short wait.
    /// </summary>
    internal static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(5);

    /// <summary>Test seam: the "now" for <see cref="MinimumAge"/>.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    private static readonly EnumerationOptions NoLinks = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint, // a skipped directory is also not recursed into
        IgnoreInaccessible = true,
    };

    /// <summary>
    /// Every recognised backup folder and loose <c>.bak</c> file directly under <paramref name="steamRoot"/>,
    /// newest first. Files are looked for in the root ONLY: a .bak inside a game folder is a Denuvo fix's
    /// revert copy and must never be offered here.
    /// </summary>
    public Task<IReadOnlyList<BackupEntry>> ScanAsync(string steamRoot, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<BackupEntry>>(() =>
        {
            if (!Directory.Exists(steamRoot)) return [];

            var root = new DirectoryInfo(steamRoot);
            var found = new List<BackupEntry>();
            foreach (var dir in root.EnumerateDirectories())
            {
                ct.ThrowIfCancellationRequested();
                if (IsLink(dir)
                    || !BackupInventory.TryParse(dir.Name, out var kind, out var createdUtc)
                    || !BackupInventory.IsInsideSteamRoot(dir.FullName, steamRoot)) continue;

                var (size, files) = Measure(dir, ct);
                found.Add(new BackupEntry(dir.Name, dir.FullName, createdUtc, size, files, kind));
            }
            foreach (var file in root.EnumerateFiles("*.bak", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if (IsLink(file)
                    || BackupInventory.ClassifyFile(file.Name) is not { } kind
                    || !BackupInventory.IsInsideSteamRoot(file.FullName, steamRoot)) continue;

                found.Add(new BackupEntry(file.Name, file.FullName, file.LastWriteTimeUtc, file.Length, null, kind));
            }
            return [.. found.OrderByDescending(e => e.CreatedUtc)];
        }, ct);

    /// <summary>
    /// Delete <paramref name="entries"/>. Every one is re-validated first (name classifies as the kind it
    /// claims, direct child of <paramref name="steamRoot"/>, not a link, at least <see cref="MinimumAge"/>
    /// old); if any fails, NOTHING is deleted. A folder or file already gone is skipped, not an error.
    /// </summary>
    public Task<BackupDeleteResult> DeleteAsync(
        IReadOnlyList<BackupEntry> entries, string steamRoot, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            DateTime nowUtc = Clock.GetUtcNow().UtcDateTime;
            foreach (var e in entries)
            {
                if (!BackupInventory.MayDelete(e, steamRoot) || IsUnsafeOnDisk(e))
                {
                    AppLog.Log($"backup cleanup: refused, {e.FullPath} is not a backup in {steamRoot}");
                    return new BackupDeleteResult(0, 0, [e.Name], Refused: true);
                }
                if (IsTooRecent(e, nowUtc))
                {
                    AppLog.Log($"backup cleanup: refused, {e.Name} is less than {MinimumAge.TotalMinutes:0} minutes old "
                        + "(an install may still be writing it)");
                    return new BackupDeleteResult(0, 0, [e.Name], Refused: true);
                }
            }

            int deleted = 0;
            long freed = 0;
            var errors = new List<string>();
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (e.IsFile)
                    {
                        if (!DeleteBakFile(e.FullPath)) continue; // removed by hand since the scan
                    }
                    else
                    {
                        if (!Directory.Exists(e.FullPath)) continue;
                        DeleteTree(new DirectoryInfo(e.FullPath));
                    }
                    deleted++;
                    freed += e.SizeBytes;
                    AppLog.Log($"backup cleanup: deleted {e.Name} ({ByteSize.Format(e.SizeBytes)})");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add(e.Name);
                    AppLog.Log($"backup cleanup: could not delete {e.Name}: {ex.Message}");
                }
            }
            return new BackupDeleteResult(deleted, freed, errors, Refused: false);
        }, ct);

    /// <summary>
    /// Depth-first delete that never walks into a reparse point. <see cref="Directory.Delete(string, bool)"/>
    /// with <c>recursive: true</c> is not used: <see cref="DirectoryJunction.Remove"/> records that it can
    /// enumerate THROUGH a junction and delete the target's files. A link here is detached with
    /// <c>recursive: false</c> (RemoveDirectory on the link itself), so what it points at is untouched.
    /// </summary>
    internal static void DeleteTree(DirectoryInfo dir)
    {
        // Re-read the disk right before entering: the batch was validated earlier, and a folder swapped for
        // a junction since then must not be walked.
        //
        // ponytail: residual TOCTOU, accepted. Between this Refresh/IsLink and EnumerateFileSystemInfos (and,
        // per entry, between IsLink(entry) and its Delete) a same-user process could still swap a folder for a
        // junction. The window is microseconds, the swap has to win that race on purpose, and the backups live
        // in the Steam root, which that process can already write. Closing it 100% means holding the directory
        // open for the whole walk: CreateFile with FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT
        // and no FILE_SHARE_DELETE, so it cannot be renamed or replaced, then enumerating and deleting relative
        // to that handle. Same idea as AssetIntegrity.OpenPinned, which pins a FILE between hash and use; a
        // directory needs P/Invoke (no managed API opens one). Not done: documented, not implemented.
        dir.Refresh();
        if (IsLink(dir)) throw new IOException($"{dir.Name} became a link after validation; not entered");

        foreach (var entry in dir.EnumerateFileSystemInfos())
        {
            if (IsLink(entry))
            {
                if (entry is DirectoryInfo link) link.Delete(recursive: false);
                else entry.Delete(); // a file symlink: DeleteFile removes the link, not its target
                continue;
            }
            if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
            if (entry is DirectoryInfo sub) DeleteTree(sub);
            else entry.Delete();
        }
        dir.Delete(recursive: false);
    }

    /// <summary>
    /// Delete one loose .bak, re-reading its attributes immediately before: gone = false (nothing to do), a
    /// reparse point or a directory in its place = refused (it changed since validation).
    /// </summary>
    internal static bool DeleteBakFile(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }

        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory))
            throw new IOException($"{Path.GetFileName(path)} is no longer a plain file; not deleted");
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        File.Delete(path);
        return true;
    }

    /// <summary>
    /// The entry is on disk as something other than what it claims: a link, or a directory where a file was
    /// listed (a junction named like a .bak) and vice versa. Missing is fine: that is "already deleted".
    /// </summary>
    private static bool IsUnsafeOnDisk(BackupEntry e) => e.IsFile
        ? Directory.Exists(e.FullPath) || (File.Exists(e.FullPath) && IsLink(new FileInfo(e.FullPath)))
        : File.Exists(e.FullPath) || (Directory.Exists(e.FullPath) && IsLink(new DirectoryInfo(e.FullPath)));

    /// <summary>
    /// Younger than <see cref="MinimumAge"/>, or dated in the future (a clock that moved back since). Read from
    /// the source again rather than from <see cref="BackupEntry.CreatedUtc"/>, which came from a scan that may
    /// be minutes old: a folder's timestamp is in its own name, a .bak's is its last write. A .bak written to
    /// after the scan is exactly the case this exists for.
    /// </summary>
    private static bool IsTooRecent(BackupEntry e, DateTime nowUtc)
    {
        DateTime createdUtc;
        if (e.IsFile)
        {
            if (!File.Exists(e.FullPath)) return false; // already gone: skipped by the delete loop
            createdUtc = File.GetLastWriteTimeUtc(e.FullPath);
        }
        else if (!BackupInventory.TryParse(Path.GetFileName(e.FullPath), out _, out createdUtc))
        {
            return true; // MayDelete already parsed this name; unreachable, but never a reason to delete
        }
        return createdUtc > nowUtc - MinimumAge;
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;

    private static (long Size, int Files) Measure(DirectoryInfo dir, CancellationToken ct)
    {
        long size = 0;
        int files = 0;
        foreach (var f in dir.EnumerateFiles("*", NoLinks))
        {
            ct.ThrowIfCancellationRequested();
            size += f.Length;
            files++;
        }
        return (size, files);
    }
}
