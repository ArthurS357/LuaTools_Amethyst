using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace LuaToolsGui.Services.Backups;

public enum SaveBackupOutcome
{
    Ok,
    /// <summary>No Steam install located, so there is no <c>userdata</c> to read or write.</summary>
    SteamNotFound,
    /// <summary>No <c>userdata\&lt;account&gt;\&lt;appid&gt;</c> folder (or it holds no files) — the game keeps its
    /// saves somewhere Steam does not mirror. Nothing was written.</summary>
    NoSaves,
    /// <summary>The archive or the target failed validation (zip-slip, link, wrong game, too large). Nothing was extracted.</summary>
    Refused,
    Failed,
}

public sealed record SaveBackupResult(SaveBackupOutcome Outcome, string? Path = null, int Files = 0, long Bytes = 0);

/// <summary>One backup zip on disk.</summary>
public sealed record SaveBackupEntry(string Name, string FullPath, DateTime CreatedUtc, long SizeBytes);

/// <summary>
/// Zips and restores <c>&lt;Steam&gt;\userdata\&lt;account&gt;\&lt;appid&gt;\</c> — the per-game folder Steam keeps
/// (Cloud cache in <c>remote\</c>, local config). Games that write their saves under Documents or AppData
/// are NOT covered; the caller is told with <see cref="SaveBackupOutcome.NoSaves"/> rather than guessing.
///
/// <para>
/// Zip layout is <c>&lt;account&gt;/&lt;appid&gt;/…relative path…</c>, so a restore lands each file back under the
/// account it came from. <b>Restore validates every entry before extracting any</b>: rooted names, escapes
/// out of the game folder (<see cref="FixAnalyzer.IsContained"/>), a different account/appid than the one
/// asked for, links on the way to the destination, and an unreasonable total size all refuse the whole
/// archive. Writes go through <see cref="AtomicFile"/>.
/// </para>
/// </summary>
public sealed partial class SaveBackupService
{
    /// <summary>Zip-bomb ceiling for a restore (uncompressed, summed from the central directory).</summary>
    internal const long MaxRestoreBytes = 4L * 1024 * 1024 * 1024;

    private static readonly EnumerationOptions NoLinks = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint, // a skipped directory is also not recursed into
        IgnoreInaccessible = true,
    };

    [GeneratedRegex(@"^(?<app>[0-9]+)_[0-9]{8}-[0-9]{6}(_[0-9]+)?\.zip\z")]
    private static partial Regex BackupNameRegex();

    [GeneratedRegex(@"^[0-9]+\z")]
    private static partial Regex DigitsRegex();

    private readonly Func<string?> _steamRoot;
    private readonly string _backupsDir;

    public SaveBackupService(SteamService steam)
        : this(() => steam.EffectivePath, Path.Combine(SettingsService.DefaultDirectory, "SaveBackups")) { }

    internal SaveBackupService(Func<string?> steamRoot, string backupsDir)
    {
        _steamRoot = steamRoot;
        _backupsDir = Path.GetFullPath(backupsDir);
    }

    private string? UserdataDir => _steamRoot() is { } root ? Path.Combine(root, "userdata") : null;

    /// <summary>Every existing <c>userdata\&lt;account&gt;\&lt;appid&gt;</c> folder. Account folders are digits only;
    /// <c>0</c> is Steam's anonymous placeholder, not a user. Links are never returned.</summary>
    internal List<(string Account, string Dir)> FindSaveDirs(long appId)
    {
        var found = new List<(string, string)>();
        if (UserdataDir is not { } userdata || !Directory.Exists(userdata)) return found;

        foreach (var acct in new DirectoryInfo(userdata).EnumerateDirectories())
        {
            if (IsLink(acct) || acct.Name == "0" || !DigitsRegex().IsMatch(acct.Name)) continue;
            var game = new DirectoryInfo(Path.Combine(acct.FullName, appId.ToString(CultureInfo.InvariantCulture)));
            if (game.Exists && !IsLink(game)) found.Add((acct.Name, game.FullName));
        }
        return found;
    }

    public Task<SaveBackupResult> BackupSavesAsync(long appId, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (appId <= 0) return new SaveBackupResult(SaveBackupOutcome.Refused);
            if (_steamRoot() is null) return new SaveBackupResult(SaveBackupOutcome.SteamNotFound);

            var dirs = FindSaveDirs(appId);
            var files = dirs.SelectMany(d => EnumerateFiles(d.Dir).Select(f => (d.Account, d.Dir, File: f))).ToList();
            if (files.Count == 0) return new SaveBackupResult(SaveBackupOutcome.NoSaves);

            try
            {
                Directory.CreateDirectory(_backupsDir);
                string zipPath = NextBackupPath(appId);
                long bytes = 0;

                AtomicFile.Write(zipPath, tmp =>
                {
                    using var zip = ZipFile.Open(tmp, ZipArchiveMode.Create);
                    foreach (var (account, dir, file) in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        string rel = Path.GetRelativePath(dir, file.FullName).Replace('\\', '/');
                        var entry = zip.CreateEntry($"{account}/{appId}/{rel}", CompressionLevel.Optimal);
                        entry.LastWriteTime = file.LastWriteTime;
                        // Steam may hold a save open for write; share like a backup tool would.
                        using var src = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        using var dst = entry.Open();
                        src.CopyTo(dst);
                        bytes += src.Length;
                    }
                });

                AppLog.Log($"save backup: {appId} -> {Path.GetFileName(zipPath)} ({files.Count} files, {ByteSize.Format(bytes)})");
                return new SaveBackupResult(SaveBackupOutcome.Ok, zipPath, files.Count, bytes);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                AppLog.Log($"save backup: {appId} failed: {LogSanitizer.Sanitize(ex)}");
                return new SaveBackupResult(SaveBackupOutcome.Failed);
            }
        }, ct);

    public IReadOnlyList<SaveBackupEntry> ListBackups(long appId)
    {
        if (!Directory.Exists(_backupsDir)) return [];

        var list = new List<SaveBackupEntry>();
        foreach (var f in new DirectoryInfo(_backupsDir).EnumerateFiles($"{appId.ToString(CultureInfo.InvariantCulture)}_*.zip"))
        {
            if (IsLink(f) || !OwnsName(f.Name, appId)) continue;
            list.Add(new SaveBackupEntry(f.Name, f.FullName, f.LastWriteTimeUtc, f.Length));
        }
        return [.. list.OrderByDescending(e => e.CreatedUtc)];
    }

    /// <summary>Delete one backup of <paramref name="appId"/>. Refuses anything that is not a plain file
    /// named like our backups, directly inside the backups folder.</summary>
    public bool DeleteBackup(string zipPath, long appId)
    {
        if (!IsOurBackup(zipPath, appId)) return false;
        try { File.Delete(zipPath); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Log($"save backup: could not delete {Path.GetFileName(zipPath)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Extract <paramref name="zipPath"/> back under <c>userdata</c>. The current saves are zipped first (a
    /// restore overwrites files, and that has to be undoable), and if that safety copy fails the restore
    /// does not run.
    /// </summary>
    public Task<SaveBackupResult> RestoreBackupAsync(string zipPath, long appId, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            if (appId <= 0 || !IsOurBackup(zipPath, appId)) return new SaveBackupResult(SaveBackupOutcome.Refused);
            if (UserdataDir is not { } userdata) return new SaveBackupResult(SaveBackupOutcome.SteamNotFound);

            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var plan = Validate(zip, userdata, appId);
                if (plan is null) return new SaveBackupResult(SaveBackupOutcome.Refused);

                // Only worth a safety copy when there is something to lose.
                if (FindSaveDirs(appId).Count > 0
                    && (await BackupSavesAsync(appId, ct)).Outcome is not SaveBackupOutcome.Ok)
                    return new SaveBackupResult(SaveBackupOutcome.Failed);

                long bytes = 0;
                foreach (var (entry, dest) in plan)
                {
                    ct.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    // The validator checked the chain, but a link can appear in between: look again.
                    if (HasLinkBetween(userdata, Path.GetDirectoryName(dest)!)) return new SaveBackupResult(SaveBackupOutcome.Refused);
                    AtomicFile.Write(dest, tmp => entry.ExtractToFile(tmp, overwrite: true));
                    bytes += entry.Length;
                }
                AppLog.Log($"save backup: restored {Path.GetFileName(zipPath)} ({plan.Count} files)");
                return new SaveBackupResult(SaveBackupOutcome.Ok, zipPath, plan.Count, bytes);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                AppLog.Log($"save backup: restore of {Path.GetFileName(zipPath)} failed: {LogSanitizer.Sanitize(ex)}");
                return new SaveBackupResult(SaveBackupOutcome.Failed);
            }
        }, ct);

    /// <summary>All-or-nothing check of the archive. Null = refuse the lot.</summary>
    private static List<(ZipArchiveEntry Entry, string Dest)>? Validate(ZipArchive zip, string userdata, long appId)
    {
        var plan = new List<(ZipArchiveEntry, string)>();
        long total = 0;
        string app = appId.ToString(CultureInfo.InvariantCulture);

        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName;
            if (name.EndsWith('/') || name.EndsWith('\\')) continue; // directory marker: created on demand

            // ':' also covers NTFS alternate data streams ("save.dat:evil"), which GetFullPath would not flag.
            if (FixAnalyzer.IsAbsoluteOrRooted(name) || name.Contains(':')) return null;

            // <account>/<appid>/<file…> — exactly the shape the backup writes, for exactly this game.
            var parts = name.Replace('\\', '/').Split('/');
            if (parts.Length < 3 || parts[0] == "0" || !DigitsRegex().IsMatch(parts[0]) || parts[1] != app) return null;

            string gameRoot = Path.Combine(userdata, parts[0], app);
            if (!FixAnalyzer.IsContained(gameRoot, string.Join('/', parts.Skip(2)), out string dest)
                || Path.GetFileName(dest).Length == 0) return null; // "…/." resolves to the folder itself
            if (HasLinkBetween(userdata, Path.GetDirectoryName(dest)!)) return null;

            total += entry.Length;
            if (total > MaxRestoreBytes) return null;
            plan.Add((entry, dest));
        }
        return plan.Count == 0 ? null : plan;
    }

    /// <summary>True when any EXISTING directory from <paramref name="root"/> (exclusive) down to
    /// <paramref name="dir"/> is a reparse point — writing through it would leave the game folder.</summary>
    private static bool HasLinkBetween(string root, string dir)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd('\\');
        for (string? cur = Path.GetFullPath(dir).TrimEnd('\\');
             cur is not null && cur.Length > rootFull.Length
                 && cur.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
             cur = Path.GetDirectoryName(cur))
        {
            if (Directory.Exists(cur) && IsLink(new DirectoryInfo(cur))) return true;
        }
        return false;
    }

    private bool IsOurBackup(string zipPath, long appId)
    {
        try
        {
            string full = Path.GetFullPath(zipPath);
            return string.Equals(Path.GetDirectoryName(full), _backupsDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                && OwnsName(Path.GetFileName(full), appId)
                && File.Exists(full)
                && !IsLink(new FileInfo(full));
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private static bool OwnsName(string fileName, long appId) =>
        BackupNameRegex().Match(fileName) is { Success: true } m
        && m.Groups["app"].Value == appId.ToString(CultureInfo.InvariantCulture);

    private string NextBackupPath(long appId)
    {
        string stem = $"{appId.ToString(CultureInfo.InvariantCulture)}_{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        string path = Path.Combine(_backupsDir, stem + ".zip");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(_backupsDir, $"{stem}_{n}.zip");
        return path;
    }

    private static IEnumerable<FileInfo> EnumerateFiles(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles("*", NoLinks).Where(f => !IsLink(f));

    private static bool IsLink(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;
}
