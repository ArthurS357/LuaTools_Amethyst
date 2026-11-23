using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace LuaToolsGui.Services;

public sealed partial class DepotDownloaderService
{
    /// <summary>
    /// Where the short-lived depot-keys file lives.
    ///
    /// <para>
    /// Deliberately NOT the shared download staging folder (<c>%TEMP%\LuaToolsGui\downloads</c>). That
    /// folder holds downloaded artifacts, is written by three services and is swept on a one-day delay;
    /// live AES-256 depot keys do not belong in it. This directory holds nothing else and is emptied on
    /// every launch.
    /// </para>
    /// </summary>
    private static readonly string KeysDir = Path.Combine(Path.GetTempPath(), "LuaToolsGui", "depotkeys");

    /// <summary>Kill the child if it produces no output for this long — it's prompting or wedged.</summary>
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Chunks fetched concurrently per depot (the tool's <c>-max-downloads</c>), raised from its default
    /// of 8, which does not saturate a fast connection.
    /// </summary>
    /// <remarks>
    /// Requests are round-robined across Steam's real CDN server list, so this is spread over several hosts
    /// rather than hammering one, and each in-flight chunk rents roughly its uncompressed size (~1 MB) from
    /// an ArrayPool. It stays a compiled-in constant rather than a setting: exposing it would add a key to
    /// <c>settings.json</c>, and the value only ever needs changing if the CDN starts refusing connections.
    /// </remarks>
    private const int MaxChunkDownloads = 32;

    private const string PreAllocatingPrefix = "Pre-allocating ";

    /// <summary>The folder DepotDownloader creates inside its output directory. Its presence is proof the
    /// downloader actually ran there.</summary>
    private const string DownloaderMarkerDir = ".DepotDownloader";

    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>Matches the tool's per-file progress line, e.g. " 42.17% game/data.pak".</summary>
    [GeneratedRegex(@"^\s*([0-9]+(?:\.[0-9]+)?)%\s+(.+)$")]
    private static partial Regex ProgressRegex();

    // ── Disk budget ──────────────────────────────────────────────────

    /// <summary>Free bytes on the volume that will hold <paramref name="path"/>, or null if unknown.</summary>
    /// <remarks>
    /// Shared by the depot picker (which warns before you commit) and the job's own pre-check (which
    /// refuses before a byte is allocated), so the two can never disagree.
    /// </remarks>
    public static long? FreeSpaceFor(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            return null; // unmapped/UNC path: let the download try and fail on its own terms
        }
    }

    /// <summary>The volume a path lives on ("C:\"), for display. Empty when it can't be resolved.</summary>
    public static string DriveOf(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)) ?? ""; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    // ── Depot keys on disk ───────────────────────────────────────────

    /// <summary>
    /// Write the <c>depotID;hexKey</c> file the <c>-depotkeys</c> flag expects, readable only by the
    /// current user, and delete it when the handle is disposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This file is plaintext by necessity — a separate process has to read it — so the protections are
    /// SCOPE and LIFETIME rather than encryption. DPAPI is not an option for the same reason.
    /// </para>
    /// <para>
    /// The DACL is set at creation and protected from inheritance, granting the current user alone. That is
    /// tighter than the containing per-user temp directory, which also grants SYSTEM and the local
    /// Administrators group. If the ACL cannot be applied (a filesystem without ACL support), the write is
    /// abandoned rather than silently downgraded.
    /// </para>
    /// </remarks>
    public static KeysFile WriteKeysFile(IReadOnlyDictionary<long, string> keys)
    {
        Directory.CreateDirectory(KeysDir);
        string path = Path.Combine(KeysDir, $"depotkeys_{Guid.NewGuid():N}.txt");

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("No SID for the current user; refusing to write depot keys."),
            FileSystemRights.FullControl, AccessControlType.Allow));

        var sb = new StringBuilder();
        foreach (var (id, key) in keys)
            sb.Append(id.ToString(CultureInfo.InvariantCulture)).Append(';').Append(key).Append('\n');

        var info = new FileInfo(path);
        using (var stream = info.Create(FileMode.CreateNew, FileSystemRights.WriteData, FileShare.None,
                                        4096, FileOptions.None, security))
        {
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(sb.ToString());
            stream.Write(bytes, 0, bytes.Length);
        }

        return new KeysFile(path);
    }

    /// <summary>A depot-keys file that deletes itself on dispose.</summary>
    public sealed class KeysFile(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Log("DepotDownloader: could not delete the depot-keys file; the startup sweep will.");
            }
        }
    }

    /// <summary>
    /// Delete every depot-keys file left behind by a previous session. Called once at startup.
    /// </summary>
    /// <remarks>
    /// The dispose above covers the normal path and every failure path that unwinds. It does NOT cover a
    /// kill: a crashed or force-closed process leaves live decryption keys on disk. Unlike the download
    /// staging sweep this has no age cutoff — nothing in this directory outlives the run that created it,
    /// so anything here on launch is by definition abandoned.
    /// </remarks>
    public static void SweepKeyFiles()
    {
        try
        {
            if (!Directory.Exists(KeysDir)) return;
            foreach (string path in Directory.EnumerateFiles(KeysDir))
            {
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next launch */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    // ── Process invocation ───────────────────────────────────────────

    /// <summary>
    /// Download one depot. Serialized app-wide (see class remarks).
    /// </summary>
    private async Task<DepotRunResult> RunAsync(
        long appId, DepotSelection sel, string keysFile, string outDir, bool validate,
        IProgress<(double Fraction, DepotPhase Phase)>? progress, IProgress<string>? createdFile,
        CancellationToken ct)
    {
        string? exe = await EnsureToolAsync(null, ct);
        if (exe is null) return new DepotRunResult(false, DepotFailure.ToolUnavailable, null);

        ArgumentNullException.ThrowIfNull(sel.ManifestId);
        ArgumentNullException.ThrowIfNull(sel.ManifestPath);

        await _runGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(outDir);

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = ToolDir,
            };
            // ArgumentList quotes each value itself, so nothing here is shell-parsed and a path with spaces
            // (or quotes) needs no manual escaping. Deliberately NO -username/-qr (they would prompt, and
            // this tool is only ever used anonymously) and NO -loginid (ignored on the anonymous path).
            foreach (string a in new[]
            {
                "-app", appId.ToString(CultureInfo.InvariantCulture),
                "-depot", sel.DepotId.ToString(CultureInfo.InvariantCulture),
                "-manifest", sel.ManifestId,
                "-depotkeys", keysFile,
                "-manifestfile", sel.ManifestPath,
                "-dir", outDir,
                "-max-downloads", MaxChunkDownloads.ToString(CultureInfo.InvariantCulture),
            }) psi.ArgumentList.Add(a);

            // Mandatory on a resume: without it the tool short-circuits and reports success over a
            // partially-written file. See class remarks.
            if (validate) psi.ArgumentList.Add("-validate");

            using var proc = Process.Start(psi);
            if (proc is null) return new DepotRunResult(false, DepotFailure.DownloaderFailed, null);

            long lastOutput = DateTime.UtcNow.Ticks;
            string? lastError = null;
            string? lastLine = null;
            DepotPhase? lastPhase = null;

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                Interlocked.Exchange(ref lastOutput, DateTime.UtcNow.Ticks);

                var m = ProgressRegex().Match(e.Data);
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double pct))
                {
                    // A progress line IS the download phase; chunks are moving by definition.
                    lastPhase = DepotPhase.Downloading;
                    progress?.Report((Math.Clamp(pct / 100d, 0d, 1d), DepotPhase.Downloading));
                    return;
                }

                string trimmed = e.Data.Trim();

                // "Pre-allocating X" is printed ONLY when X did not already exist, so it is exactly the set
                // of files this run created — which is what a cancel is allowed to delete. Anything the user
                // already had is reported as "Validating" instead and is never recorded.
                if (createdFile is not null && trimmed.StartsWith(PreAllocatingPrefix, StringComparison.Ordinal))
                {
                    string path = trimmed[PreAllocatingPrefix.Length..].Trim();
                    if (path.Length > 0) createdFile.Report(path);
                }

                // Only on CHANGE: these are per-file, so a big depot would otherwise emit thousands.
                if (PhaseOf(trimmed) is { } p && p != lastPhase)
                {
                    lastPhase = p;
                    progress?.Report((0d, p));
                }

                // The tool writes fatal errors to STDOUT and leaves stderr empty ("There is not enough space
                // on the disk", "No valid depot key for N"). Keeping the last non-progress line is what turns
                // a useless "exit 1" into the actual reason. Stack frames are skipped: an unhandled exception
                // prints its message and THEN a dozen "at …" lines.
                if (trimmed.Length > 0 && !trimmed.StartsWith("at ", StringComparison.Ordinal)) lastLine = trimmed;
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                Interlocked.Exchange(ref lastOutput, DateTime.UtcNow.Ticks);
                lastError = e.Data;
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Cancellation and the watchdog both resolve to "kill the child".
            using var reg = ct.Register(() => TryKill(proc));
            bool timedOut = false;

            while (!proc.WaitForExit(2000))
            {
                if (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastOutput) <= SilenceTimeout.Ticks) continue;
                AppLog.Log($"DepotDownloader: silent for {SilenceTimeout.TotalMinutes} minutes; killing it.");
                timedOut = true;
                TryKill(proc);
                break;
            }

            // Blocking overload with no timeout also waits for the async readers to drain.
            proc.WaitForExit();
            ct.ThrowIfCancellationRequested();

            if (timedOut) return new DepotRunResult(false, DepotFailure.DownloaderFailed, null);
            if (proc.ExitCode != 0)
            {
                // The child's stdout is attacker-influenceable in principle and this string is both logged
                // and shown, so it goes through the same redaction every other sink uses, and is capped.
                string? why = LogSanitizer.Sanitize(lastError ?? lastLine ?? "");
                if (why is { Length: > 300 }) why = why[..300];
                AppLog.Log($"DepotDownloader: exited {proc.ExitCode} for depot {sel.DepotId} — {why}");
                return new DepotRunResult(false, DepotFailure.DownloaderFailed,
                    string.IsNullOrWhiteSpace(why) ? null : why);
            }

            progress?.Report((1d, DepotPhase.Downloading));
            return new DepotRunResult(true, DepotFailure.None, null);
        }
        finally { _runGate.Release(); }
    }

    /// <summary>
    /// Map one stdout line to a phase, or null when it says nothing about phase.
    /// </summary>
    /// <remarks>
    /// Order matters: "Downloading depot N manifest" has to be tested before the bare "Downloading depot N",
    /// or every manifest fetch would read as the download starting.
    /// </remarks>
    private static DepotPhase? PhaseOf(string line) => line switch
    {
        _ when line.StartsWith(PreAllocatingPrefix, StringComparison.Ordinal) => DepotPhase.PreAllocating,
        _ when line.StartsWith("Validating ", StringComparison.Ordinal) => DepotPhase.Validating,
        _ when line.StartsWith("Downloading depot ", StringComparison.Ordinal)
               && line.EndsWith(" manifest", StringComparison.Ordinal) => DepotPhase.Manifest,
        _ when line.StartsWith("Downloading depot ", StringComparison.Ordinal) => DepotPhase.Downloading,
        _ => null,
    };

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            // already gone
        }
    }

    // ── Cleanup after a cancel ───────────────────────────────────────

    /// <summary>True when the downloader has actually written to this folder.</summary>
    public static bool HasDownloadedContent(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && Directory.Exists(Path.Combine(dir, DownloaderMarkerDir));

    /// <summary>
    /// Delete exactly the files this download created, then tidy up after them. False only if something was
    /// left behind.
    /// </summary>
    /// <remarks>
    /// <para><b>Deliberately not a recursive delete of the output folder.</b> That folder is chosen by the
    /// user, so it can legitimately be an existing game directory being repaired — wiping it would destroy
    /// an install we never created. Only paths the downloader reported <c>Pre-allocating</c> are removed,
    /// and it prints that line only for files that did not already exist.</para>
    ///
    /// <para>Every path is resolved against the output folder and checked to be inside it before deletion,
    /// so a malformed or hostile line in the child's stdout cannot reach outside the download folder.</para>
    ///
    /// <para>Call only once the child has exited: the kill is asynchronous and the downloader holds handles
    /// on what it pre-allocated, so deleting too early just fails.</para>
    /// </remarks>
    public static bool TryDeleteCreatedFiles(string? dir, IReadOnlyCollection<string> createdFiles)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;

        string root;
        try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        bool allGone = true;
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string f in createdFiles)
        {
            try
            {
                // Resolved against the OUTPUT folder, not the working directory (which is ToolDir): absolute
                // paths are unaffected, and a relative one lands where the downloader meant it.
                string full = Path.GetFullPath(f, root);
                // Containment check: never delete outside the folder we were given.
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (File.Exists(full)) File.Delete(full);
                if (Path.GetDirectoryName(full) is { Length: > 0 } parent) touched.Add(parent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or ArgumentException or NotSupportedException)
            {
                allGone = false; // locked or unresolvable; the folder just keeps it
            }
        }

        // The downloader's own bookkeeping is ours by definition, so it goes too.
        try
        {
            string marker = Path.Combine(root, DownloaderMarkerDir);
            if (Directory.Exists(marker)) Directory.Delete(marker, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { allGone = false; }

        PruneEmptyDirectories(root, touched);
        return allGone;
    }

    /// <summary>
    /// Remove directories left empty by the deletion, deepest first, stopping at (and keeping) the root.
    /// </summary>
    /// <remarks>
    /// Walks up from the folders actually emptied rather than scanning the tree. The output directory is
    /// user-chosen and can be an existing game folder, so sweeping every empty directory under it would
    /// delete ones that were there before this download and are none of our business.
    /// </remarks>
    private static void PruneEmptyDirectories(string root, HashSet<string> touched)
    {
        foreach (string start in touched.OrderByDescending(d => d.Length))
        {
            string? dir = start;
            // Up the chain: emptying a leaf can leave its parent empty too, but never remove the root.
            while (dir is not null
                   && dir.Length > root.Length
                   && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(dir)) { dir = Path.GetDirectoryName(dir); continue; }
                    if (Directory.EnumerateFileSystemEntries(dir).Any()) break; // still holds something
                    Directory.Delete(dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    break; // in use or denied: stop climbing this branch
                }
                dir = Path.GetDirectoryName(dir);
            }
        }
    }
}
