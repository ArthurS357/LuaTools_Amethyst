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
    /// <summary>The backups drive cannot hold the zip plus a safety margin. Nothing was written; <see cref="SaveBackupResult.Bytes"/> is the space asked for.</summary>
    NoSpace,
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

    /// <summary>Free space a backup of <paramref name="rawBytes"/> must find: the raw size (an upper bound for the
    /// zip, which only shrinks it) plus 10% and a flat 50 MB, so the drive is not left at zero.</summary>
    internal static long RequiredFreeBytes(long rawBytes) => rawBytes + rawBytes / 10 + 50L * 1024 * 1024;

    [GeneratedRegex(@"^(?<app>[0-9]+)_[0-9]{8}-[0-9]{6}(_[0-9]+)?\.zip\z")]
    private static partial Regex BackupNameRegex();

    [GeneratedRegex(@"^[0-9]+\z")]
    private static partial Regex DigitsRegex();

    private readonly Func<string?> _steamRoot;
    private readonly Func<string> _backupsDirSource;
    private readonly Func<string, long> _freeBytes;

    /// <summary>Read from settings on every use, so changing the folder takes effect without a restart.</summary>
    public SaveBackupService(SteamService steam, SettingsService settings)
        : this(() => steam.EffectivePath, () => settings.EffectiveSaveBackupFolder) { }

    internal SaveBackupService(Func<string?> steamRoot, string backupsDir, Func<string, long>? freeBytes = null)
        : this(steamRoot, () => backupsDir, freeBytes) { }

    internal SaveBackupService(Func<string?> steamRoot, Func<string> backupsDirSource, Func<string, long>? freeBytes = null)
    {
        _steamRoot = steamRoot;
        _backupsDirSource = backupsDirSource;
        _freeBytes = freeBytes ?? FreeBytesOnDrive;
    }

    /// <summary>The folder backups are written to and listed from, without a trailing separator.</summary>
    private string BackupsDir => Path.GetFullPath(_backupsDirSource()).TrimEnd('\\');

    /// <summary>Free space on the drive holding <paramref name="dir"/>. A root DriveInfo cannot read (UNC share)
    /// reports unlimited: the check is a guard against the common case, not a guarantee.</summary>
    private static long FreeBytesOnDrive(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return long.MaxValue; }
    }

    /// <summary>True when the dialog has something to act on: a <c>userdata</c> folder to back up, or a backup
    /// to restore (the folder may be gone — that is when a restore matters most).</summary>
    public bool HasSavesOrBackups(long appId) => FindSaveDirs(appId).Count > 0 || ListBackups(appId).Count > 0;

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

    private static readonly char[] ExtraInvalidNameChars = [':', '?', '*', '"', '<', '>', '|', '/', '\\'];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const int MaxFolderNameLength = 80;

    /// <summary>
    /// A game name made safe to be ONE folder name: invalid characters (<c>: ? * " &lt; &gt; | / \</c> and
    /// control characters) become <c>_</c>, trailing dots/spaces go (Windows drops them silently), a reserved
    /// device name (<c>CON</c>, <c>NUL</c>…) gets a <c>_</c> prefix, and the length is capped. Returns an empty
    /// string when nothing usable is left; the caller falls back to the appid. Never contains a separator,
    /// so the result cannot climb out of the backups folder.
    /// </summary>
    internal static string SanitizeFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name.Trim())
            sb.Append(char.IsControl(c) || Array.IndexOf(invalid, c) >= 0 || Array.IndexOf(ExtraInvalidNameChars, c) >= 0 ? '_' : c);

        string s = sb.ToString();
        if (s.Length > MaxFolderNameLength) s = s[..MaxFolderNameLength];
        s = s.TrimEnd('.', ' ');
        if (s.Length == 0) return "";

        // "CON" and "CON.txt" are both the console device; the part before the first dot decides.
        string stem = s.Contains('.') ? s[..s.IndexOf('.')] : s;
        return ReservedDeviceNames.Contains(stem.TrimEnd(' ')) ? "_" + s : s;
    }

    /// <summary><c>&lt;backups&gt;\&lt;sanitized game name&gt;</c>, falling back to <c>App_&lt;appid&gt;</c> when
    /// the name leaves nothing usable. Null if the result would not be a direct child of the backups folder.</summary>
    private string? GameFolderPath(long appId, string? gameName)
    {
        string safe = SanitizeFolderName(gameName);
        if (safe.Length == 0) safe = "App_" + appId.ToString(CultureInfo.InvariantCulture);

        string backupsDir = BackupsDir;
        string folder = Path.GetFullPath(Path.Combine(backupsDir, safe));
        return string.Equals(Path.GetDirectoryName(folder), backupsDir, StringComparison.OrdinalIgnoreCase) ? folder : null;
    }

    /// <summary>True when the game's own backup folder already exists. False means the next backup creates it,
    /// which is what the UI asks the user about once.</summary>
    public bool GameFolderExists(long appId, string? gameName) =>
        GameFolderPath(appId, gameName) is { } folder && Directory.Exists(folder);

    /// <summary>Zip the game's <c>userdata</c> saves into <c>&lt;backups&gt;\&lt;game name&gt;\</c>.</summary>
    public Task<SaveBackupResult> BackupSavesAsync(long appId, string gameName, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (appId <= 0) return new SaveBackupResult(SaveBackupOutcome.Refused);
            if (_steamRoot() is null) return new SaveBackupResult(SaveBackupOutcome.SteamNotFound);

            var dirs = FindSaveDirs(appId);
            var files = dirs.SelectMany(d => EnumerateFiles(d.Dir).Select(f => (d.Account, d.Dir, File: f))).ToList();
            if (files.Count == 0) return new SaveBackupResult(SaveBackupOutcome.NoSaves);

            long required = RequiredFreeBytes(files.Sum(static f => f.File.Length));
            string backupsDir = BackupsDir;
            long free = _freeBytes(backupsDir);
            if (free < required)
            {
                AppLog.Log($"save backup: {appId} refused: needs {ByteSize.Format(required)}, {ByteSize.Format(free)} free");
                return new SaveBackupResult(SaveBackupOutcome.NoSpace, Bytes: required);
            }

            try
            {
                if (GameFolderPath(appId, gameName) is not { } gameDir) return new SaveBackupResult(SaveBackupOutcome.Refused);
                // A link standing where the game folder should be would redirect the zip somewhere else.
                if (Directory.Exists(gameDir) && IsLink(new DirectoryInfo(gameDir))) return new SaveBackupResult(SaveBackupOutcome.Refused);

                Directory.CreateDirectory(gameDir);
                string zipPath = NextBackupPath(gameDir, appId);
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

    /// <summary>
    /// Every backup zip of <paramref name="appId"/>: those inside the per-game folders (<c>&lt;backups&gt;\&lt;game&gt;\</c>)
    /// and the loose ones older versions left in the backups root. Every direct subfolder is searched, not only the
    /// one for today's game name, so history survives a game being renamed (or listed under a placeholder name
    /// before its real one was known). Links are never followed, and the appid is matched by file name.
    /// </summary>
    public IReadOnlyList<SaveBackupEntry> ListBackups(long appId)
    {
        string backupsDir = BackupsDir;
        if (!Directory.Exists(backupsDir)) return [];

        string pattern = $"{appId.ToString(CultureInfo.InvariantCulture)}_*.zip";
        var list = new List<SaveBackupEntry>();
        var root = new DirectoryInfo(backupsDir);
        try
        {
            foreach (var dir in new[] { root }.Concat(root.EnumerateDirectories().Where(static d => !IsLink(d))))
            {
                try
                {
                    foreach (var f in dir.EnumerateFiles(pattern))
                    {
                        if (IsLink(f) || !OwnsName(f.Name, appId)) continue;
                        list.Add(new SaveBackupEntry(f.Name, f.FullName, f.LastWriteTimeUtc, f.Length));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* one unreadable folder is skipped, not fatal */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* root unreadable: list what was found */ }
        return [.. list.OrderByDescending(e => e.CreatedUtc)];
    }

    /// <summary>Delete one backup of <paramref name="appId"/>. Refuses anything that is not a plain file
    /// named like our backups, directly inside the backups folder or one of its game folders.</summary>
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
    public Task<SaveBackupResult> RestoreBackupAsync(string zipPath, long appId, string gameName, CancellationToken ct = default) =>
        appId <= 0 || !IsOurBackup(zipPath, appId)
            ? Task.FromResult(new SaveBackupResult(SaveBackupOutcome.Refused))
            : ExtractAsync(zipPath, appId, gameName, ct);

    /// <summary>
    /// Restore a save zip from ANYWHERE (another drive, a download, a copy kept after the game was removed)
    /// into <c>userdata</c>. The archive is outside our backups folder, so it is untrusted: it must be a
    /// plain <c>.zip</c> file (no link) and goes through the same all-or-nothing <see cref="Validate"/> as
    /// a restore — account/appid layout, zip-slip, links, size — and the current saves are zipped first.
    /// </summary>
    public Task<SaveBackupResult> ImportSaveAsync(string zipPath, long appId, string gameName, CancellationToken ct = default) =>
        appId <= 0 || !IsPlainZipFile(zipPath)
            ? Task.FromResult(new SaveBackupResult(SaveBackupOutcome.Refused))
            : ExtractAsync(zipPath, appId, gameName, ct);

    private static bool IsPlainZipFile(string zipPath)
    {
        try
        {
            if (!Path.IsPathFullyQualified(zipPath)
                || !string.Equals(Path.GetExtension(zipPath), ".zip", StringComparison.OrdinalIgnoreCase)) return false;
            var info = new FileInfo(zipPath);
            return info.Exists && !IsLink(info);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private Task<SaveBackupResult> ExtractAsync(string zipPath, long appId, string gameName, CancellationToken ct) =>
        Task.Run(async () =>
        {
            if (UserdataDir is not { } userdata) return new SaveBackupResult(SaveBackupOutcome.SteamNotFound);

            string? safetyCopy = null;
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var plan = Validate(zip, userdata, appId);
                if (plan is null) return new SaveBackupResult(SaveBackupOutcome.Refused);

                // Each file is written atomically, but a disk that fills up halfway would still leave half the
                // saves restored and half not. Refuse up front instead; nothing has been touched yet.
                // ponytail: summed size over-counts files that get replaced; per-file accounting if it ever bites.
                long need = plan.Sum(static p => p.Entry.Length);
                long free = _freeBytes(userdata);
                if (free < need)
                {
                    AppLog.Log($"save backup: restore of {Path.GetFileName(zipPath)} refused: needs {ByteSize.Format(need)}, {ByteSize.Format(free)} free");
                    return new SaveBackupResult(SaveBackupOutcome.Failed);
                }

                // Only worth a safety copy when there is something to lose.
                if (FindSaveDirs(appId).Count > 0)
                {
                    var safety = await BackupSavesAsync(appId, gameName, ct);
                    // NoSaves = the folder exists but holds no files (a fresh install): nothing to lose.
                    if (safety.Outcome is not (SaveBackupOutcome.Ok or SaveBackupOutcome.NoSaves))
                        return new SaveBackupResult(SaveBackupOutcome.Failed);
                    safetyCopy = safety.Path;
                }

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
                AppLog.Log($"save backup: restore of {Path.GetFileName(zipPath)} failed: {LogSanitizer.Sanitize(ex)}; " +
                           $"saves before the restore: {(safetyCopy is null ? "none existed" : Path.GetFileName(safetyCopy))}");
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
            string root = BackupsDir;
            string? parent = Path.GetDirectoryName(full);
            // Directly in the backups folder (older versions), or in ONE game folder under it, never deeper
            // and never through a link: the game folder is the only extra place a zip of ours may sit.
            bool placed = string.Equals(parent, root, StringComparison.OrdinalIgnoreCase)
                || (parent is not null
                    && string.Equals(Path.GetDirectoryName(parent), root, StringComparison.OrdinalIgnoreCase)
                    && !IsLink(new DirectoryInfo(parent)));
            return placed
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

    private static string NextBackupPath(string backupsDir, long appId)
    {
        string stem = $"{appId.ToString(CultureInfo.InvariantCulture)}_{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        string path = Path.Combine(backupsDir, stem + ".zip");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(backupsDir, $"{stem}_{n}.zip");
        return path;
    }

    private static IEnumerable<FileInfo> EnumerateFiles(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles("*", NoLinks).Where(f => !IsLink(f));

    private static bool IsLink(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;
}
