using System.IO;

namespace LuaToolsGui.Services.Backups;

/// <summary>Outcome of a cleanup. <see cref="Refused"/> means the whole batch was rejected before anything
/// was touched; otherwise <see cref="Errors"/> names the folders that could not be removed.</summary>
public sealed record BackupDeleteResult(int DeletedCount, long BytesFreed, IReadOnlyList<string> Errors, bool Refused);

/// <summary>
/// Finds and deletes the backup folders <see cref="BackupInventory"/> recognises, directly under the Steam
/// root and nowhere else.
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
    private static readonly EnumerationOptions NoLinks = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint, // a skipped directory is also not recursed into
        IgnoreInaccessible = true,
    };

    /// <summary>Every recognised backup folder directly under <paramref name="steamRoot"/>, newest first.</summary>
    public Task<IReadOnlyList<BackupEntry>> ScanAsync(string steamRoot, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<BackupEntry>>(() =>
        {
            if (!Directory.Exists(steamRoot)) return [];

            var found = new List<BackupEntry>();
            foreach (var dir in new DirectoryInfo(steamRoot).EnumerateDirectories())
            {
                ct.ThrowIfCancellationRequested();
                if (IsLink(dir)
                    || !BackupInventory.TryParse(dir.Name, out var kind, out var createdUtc)
                    || !BackupInventory.IsInsideSteamRoot(dir.FullName, steamRoot)) continue;

                var (size, files) = Measure(dir, ct);
                found.Add(new BackupEntry(dir.Name, dir.FullName, createdUtc, size, files, kind));
            }
            return [.. found.OrderByDescending(e => e.CreatedUtc)];
        }, ct);

    /// <summary>
    /// Delete <paramref name="entries"/>. Every one is re-validated first (recognised name, direct child of
    /// <paramref name="steamRoot"/>, not a link); if any fails, NOTHING is deleted. A folder already gone is
    /// skipped, not an error.
    /// </summary>
    public Task<BackupDeleteResult> DeleteAsync(
        IReadOnlyList<BackupEntry> entries, string steamRoot, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            foreach (var e in entries)
            {
                if (!BackupInventory.MayDelete(e, steamRoot)
                    || (Directory.Exists(e.FullPath) && IsLink(new DirectoryInfo(e.FullPath))))
                {
                    AppLog.Log($"backup cleanup: refused, {e.FullPath} is not a backup folder in {steamRoot}");
                    return new BackupDeleteResult(0, 0, [e.Name], Refused: true);
                }
            }

            int deleted = 0;
            long freed = 0;
            var errors = new List<string>();
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(e.FullPath)) continue; // removed by hand since the scan
                try
                {
                    DeleteTree(new DirectoryInfo(e.FullPath));
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
        // a junction since then must not be walked. ponytail: narrows the window to microseconds; pinning
        // the directory with an open FILE_FLAG_OPEN_REPARSE_POINT handle would close it, if ever needed.
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
