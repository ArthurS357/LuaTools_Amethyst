using System.IO;
using System.IO.Compression;
using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>How an apply ended. The caller turns this into exactly one toast.</summary>
public enum FixApplyOutcome
{
    /// <summary>Every entry landed.</summary>
    Applied,

    /// <summary>Some entries landed, some did not. The fix IS applied and IS revertable.</summary>
    PartiallyApplied,

    /// <summary>The revert record could not be written, so nothing was touched.</summary>
    RecordFailed,

    /// <summary>The archive could not be opened or read at all.</summary>
    Failed,
}

/// <summary>How a revert ended. The caller turns this into exactly one toast.</summary>
public enum FixRevertOutcome
{
    /// <summary>Fully reverted; backups and record removed.</summary>
    Done,

    /// <summary>No record for this fix — most likely already reverted.</summary>
    NoRecord,

    /// <summary>A file was replaced after this fix wrote it. Nothing was restored over it.</summary>
    Conflict,

    /// <summary>Some entries could not be restored (usually a locked file). Retryable.</summary>
    Partial,

    /// <summary>The whole operation threw.</summary>
    Failed,
}

/// <param name="Failed">Entries that could not be written, including any refused for escaping the folder.</param>
public readonly record struct FixApplyResult(FixApplyOutcome Outcome, int Failed, string? Error)
{
    public bool Ok => Outcome == FixApplyOutcome.Applied;
}

/// <param name="ConflictFile">The first conflicting relative path, for the message. Null when none.</param>
public readonly record struct FixRevertResult(
    FixRevertOutcome Outcome, int Restored, int Deleted, int Errors, int Conflicts,
    string? ConflictFile, string? Error)
{
    public bool Ok => Outcome == FixRevertOutcome.Done;
}

/// <summary>
/// Applies and reverts a Denuvo fix archive inside a game's install folder, keeping a record of exactly
/// what it changed so the change can be undone.
/// </summary>
/// <remarks>
/// <para>
/// <b>No UI.</b> Every method returns a result and touches nothing but the filesystem, so the whole
/// apply/revert contract is testable against a temp directory. The view model owns the toasts, and is
/// the single emitter for each action — a revert that half-failed must not fire two.
/// </para>
/// <para>
/// <b>Screening happens before this.</b> The caller runs <see cref="FixAnalyzer"/> over the staged
/// archive and refuses a blocked one. This class re-checks containment per entry anyway (see
/// <see cref="FixAnalyzer.IsContained"/>): the write path has to be safe on its own, so that calling it
/// without screening cannot re-open zip-slip.
/// </para>
/// </remarks>
public class DenuvoFixService
{
    /// <summary>Folder inside the game install that holds records and backups.</summary>
    internal const string FixRecordDir = ".luatools-fix";

    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    // ── Apply ────────────────────────────────────────────────────────

    /// <summary>
    /// Extract a fix archive into the game folder, backing up whatever it overwrites.
    /// </summary>
    /// <remarks>
    /// Runs in four phases, because the revert record is the ONLY thing that makes a fix undoable and the
    /// Revert button keys off that file existing:
    ///
    /// <list type="number">
    /// <item><b>Plan</b> — decide modified-vs-added from <c>File.Exists</c> alone. Nothing is written.</item>
    /// <item><b>Commit</b> — write a provisional record listing every planned entry. If this fails the
    /// apply stops here, with the game untouched. Writing the record LAST (and swallowing its failure)
    /// would let an unwritable path silently "succeed", leaving .bak files with nothing indexing them and
    /// no Revert button that could ever appear.</item>
    /// <item><b>Apply</b> — back up, extract, and build the real entry list with hashes.</item>
    /// <item><b>Settle</b> — rewrite the record with what actually happened.</item>
    /// </list>
    ///
    /// Phase 4 is not tidiness. The provisional record describes INTENT, and an entry whose backup threw
    /// would claim a <c>.bak</c> that was never written — which the revert treats as a hard error, making
    /// a partial apply permanently un-revertable. It is also the only place <c>HashAfter</c> can come
    /// from, since that is unknowable until the file has been extracted. So the record starts as a promise
    /// and ends as a fact, and a crash in between still leaves something the user can revert.
    /// </remarks>
    public FixApplyResult Apply(string zipPath, long appId, string fixId, string installDir)
    {
        try
        {
            string fixKey = SafeFixKey(fixId);
            string fixDir = Path.Combine(installDir, FixRecordDir);
            string recordPath = Path.Combine(fixDir, $"{fixKey}.json");

            string appliedAt = DateTimeOffset.UtcNow.ToString("o");

            // ── Phase 1: plan. File.Exists only; nothing on disk changes yet. ──────────────────────
            using var archive = ZipFile.OpenRead(zipPath);
            var plan = new List<(string RelPath, string Dest, string? BakRel, ZipArchiveEntry Entry)>();
            int failed = 0;

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
                string relPath = entry.FullName.Replace('\\', '/');

                // A zip entry naming its way out of the game folder is never legitimate; skip it and
                // count it, rather than writing wherever it points.
                if (!FixAnalyzer.IsContained(installDir, entry.FullName, out string dest))
                {
                    AppLog.Log($"fix apply: refused entry outside the game folder — {entry.FullName}");
                    failed++;
                    continue;
                }

                // Backups are keyed by the FULL relative path, not just the file name: fix archives
                // routinely ship the same name in several folders (steam_api64.dll, config.ini), and
                // keying by name alone collapses them onto one .bak. A revert would then restore one file
                // with the other's contents, or silently restore nothing once the .bak was consumed.
                plan.Add((relPath, dest, File.Exists(dest) ? $"{fixKey}/{relPath}.bak" : null, entry));
            }

            // ── Phase 2: commit. Nothing has been touched yet, so failing here costs nothing. ──────
            var provisional = new DenuvoFixRecord
            {
                AppId = appId,
                FixId = fixId,
                AppliedAt = appliedAt,
                Files = [.. plan.Select(p => new DenuvoFixRecordEntry
                {
                    RelativePath = p.RelPath,
                    Action = p.BakRel is null ? "added" : "modified",
                    BackupPath = p.BakRel,
                })],
            };

            try
            {
                Directory.CreateDirectory(fixDir);
                File.WriteAllText(recordPath, JsonSerializer.Serialize(provisional, WriteOpts));
            }
            catch (Exception ex)
            {
                AppLog.Log($"fix apply: could not write the revert record — {ex.Message}");
                return new FixApplyResult(FixApplyOutcome.RecordFailed, 0, ex.Message);
            }

            // ── Phase 3: apply, recording what really happened rather than what was planned. ───────
            var applied = new List<DenuvoFixRecordEntry>(plan.Count);

            foreach (var (relPath, dest, bakRel, entry) in plan)
            {
                try
                {
                    string? hashBefore = null;

                    if (bakRel is not null)
                    {
                        // The backup path comes from data we built, but it still gets the containment
                        // check: relPath originates in the archive.
                        if (!FixAnalyzer.IsContained(fixDir, bakRel, out string bakAbs)) { failed++; continue; }

                        if (!File.Exists(bakAbs)) // never clobber a known-good .bak
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(bakAbs)!);
                            File.Copy(dest, bakAbs, overwrite: false);
                        }

                        // Hash the ORIGINAL before it is overwritten; there is no second chance.
                        hashBefore = FileHash.Sha256(dest);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);

                    // Built once, here, because this is the first moment both hashes exist: HashBefore had
                    // to be taken before the overwrite, and HashAfter is unknowable until the file lands.
                    // HashAfter is what a later revert checks the file against to prove nothing has
                    // replaced it since — another fix layered on top, a game update, a hand-edit.
                    applied.Add(new DenuvoFixRecordEntry
                    {
                        RelativePath = relPath,
                        Action = bakRel is null ? "added" : "modified",
                        BackupPath = bakRel,
                        HashBefore = hashBefore,
                        HashAfter = FileHash.Sha256(dest),
                    });
                }
                catch { failed++; } // entry stays OUT of `applied`, so the settled record won't claim it
            }

            // ── Phase 4: settle. Replace the promise with the facts. ───────────────────────────────
            var settled = new DenuvoFixRecord
            {
                AppId = appId,
                FixId = fixId,
                AppliedAt = appliedAt,
                Files = applied,
            };
            try { File.WriteAllText(recordPath, JsonSerializer.Serialize(settled, WriteOpts)); }
            catch { /* the provisional record from phase 2 stands: over-broad, but still revertable */ }

            return failed > 0
                ? new FixApplyResult(FixApplyOutcome.PartiallyApplied, failed, null)
                : new FixApplyResult(FixApplyOutcome.Applied, 0, null);
        }
        catch (Exception ex)
        {
            AppLog.Log($"fix apply: failed — {ex.Message}");
            return new FixApplyResult(FixApplyOutcome.Failed, 0, ex.Message);
        }
    }

    // ── Revert ───────────────────────────────────────────────────────

    /// <summary>
    /// Revert a previously applied fix: restore <c>.bak</c> files for modified entries, delete added
    /// files, then clean up the backup directory and the record.
    /// </summary>
    /// <remarks>
    /// Takes the install folder rather than an appid: the record is identified by its path inside that
    /// folder, and resolving the folder is the caller's job anyway (it has to report "game not found"
    /// before offering a revert at all). Keeping the lookup out of here is what lets the whole
    /// apply/revert contract be tested against a temp directory.
    /// </remarks>
    public FixRevertResult Revert(string fixId, string installDir)
    {
        try
        {
            string recordPath = GetFixRecordPath(installDir, fixId);
            var record = ReadFixRecord(recordPath);
            if (record is null)
                return new FixRevertResult(FixRevertOutcome.NoRecord, 0, 0, 0, 0, null, null);

            string fixDir = Path.Combine(installDir, FixRecordDir);
            int restored = 0, deleted = 0, errors = 0, conflicts = 0;
            string? conflictFile = null;

            foreach (var entry in record.Files)
            {
                // The record sits in a user-writable folder and a fix archive can plant one, so its paths
                // get the same containment check the extract does. This branch DELETES.
                if (!FixAnalyzer.IsContained(installDir, entry.RelativePath, out string dest))
                {
                    AppLog.Log($"fix revert: refused record entry outside the game folder — {entry.RelativePath}");
                    errors++;
                    continue;
                }

                try
                {
                    if (entry.Action == "modified" && entry.BackupPath is { } bakRel)
                    {
                        if (!FixAnalyzer.IsContained(fixDir, bakRel, out string bakAbs)) { errors++; continue; }

                        if (!File.Exists(bakAbs))
                        {
                            // The record says this file was modified, so a backup MUST exist. Missing
                            // means it was never written or something removed it, and the original is gone
                            // for good — the one case where the fixed file silently stays in place.
                            // Counting it as an error is the only honest outcome: reporting success would
                            // tell the user their game is clean when it is still patched.
                            errors++;
                            continue;
                        }

                        // Only put the original back if the file on disk is still the one THIS fix wrote.
                        // Two hashes count as ours: HashAfter (untouched since we applied) and HashBefore
                        // (an earlier attempt already restored it, so a retry must not treat it as
                        // foreign). Anything else means something replaced the file after us — most likely
                        // a second fix layered on top, whose own backup holds our version — and restoring
                        // would throw that away.
                        if (File.Exists(dest)
                            && !FileHash.Matches(dest, entry.HashAfter)
                            && !FileHash.Matches(dest, entry.HashBefore))
                        {
                            conflicts++;
                            conflictFile ??= entry.RelativePath;
                            continue;
                        }

                        // The .bak is NOT deleted here, deliberately. Backups are removed as a group once
                        // the whole revert succeeds (see below). Deleting per-file would make a retry after
                        // a partial failure impossible to tell apart from a lost backup: every
                        // already-restored entry would come back as a missing .bak and fail forever.
                        // Leaving them means a retry just re-copies, which is idempotent.
                        File.Copy(bakAbs, dest, overwrite: true);
                        restored++;
                    }
                    else if (entry.Action == "added")
                    {
                        if (!File.Exists(dest)) continue;

                        // AMETHYST HARDENING, stricter than upstream. This branch DELETES, and
                        // FileHash.Matches answers "true" to a null expected hash — so upstream deletes
                        // unverified whenever the record carries no hash. Every record this app writes has
                        // one (phase 3), so requiring it costs nothing real and closes the case where a fix
                        // archive plants a record naming game files it never created. An old or foreign
                        // record without a hash is reported as an error, which leaves the file alone and
                        // is recoverable; deleting it would not be.
                        if (string.IsNullOrEmpty(entry.HashAfter))
                        {
                            AppLog.Log($"fix revert: refused to delete {entry.RelativePath} — record carries no hash");
                            errors++;
                            continue;
                        }

                        // This fix created the file, so deleting it is normally right — but not if someone
                        // has since replaced it. Then the file is theirs, not ours.
                        if (!FileHash.Matches(dest, entry.HashAfter))
                        {
                            conflicts++;
                            conflictFile ??= entry.RelativePath;
                            continue;
                        }

                        File.Delete(dest);
                        deleted++;
                    }
                }
                catch { errors++; }
            }

            // Reported before plain errors because it is the only outcome the user can act on, and the
            // action is specific: revert the fix that was applied later, then come back to this one.
            if (conflicts > 0)
                return new FixRevertResult(
                    FixRevertOutcome.Conflict, restored, deleted, errors, conflicts, conflictFile, null);

            if (errors > 0)
                // Backups and record stay put ON PURPOSE. The usual reason a revert fails is a locked file
                // because the game is running, and deleting the .bak files here would leave the user
                // half-reverted with no way to ever finish. Keeping them makes the revert simply
                // retryable once the game is closed.
                return new FixRevertResult(FixRevertOutcome.Partial, restored, deleted, errors, 0, null, null);

            // Fully reverted, so the backups have served their purpose and the record is what makes the
            // Revert button appear — both go, and the fix reads as un-applied again.
            string backupDir = Path.Combine(installDir, FixRecordDir, SafeFixKey(fixId));
            try { if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true); } catch { }
            try { if (File.Exists(recordPath)) File.Delete(recordPath); } catch { }

            return new FixRevertResult(FixRevertOutcome.Done, restored, deleted, 0, 0, null, null);
        }
        catch (Exception ex)
        {
            AppLog.Log($"fix revert: failed — {ex.Message}");
            return new FixRevertResult(FixRevertOutcome.Failed, 0, 0, 0, 0, null, ex.Message);
        }
    }

    // ── Record helpers ───────────────────────────────────────────────

    /// <summary>
    /// A fix id can carry <c>/</c>, <c>:</c> and other characters that are illegal in a Windows path
    /// segment (e.g. "online-fix:5e01e852-..."). Sanitise it for use as a folder/file name while keeping
    /// the raw id in the record itself.
    /// </summary>
    internal static string SafeFixKey(string fixId) =>
        string.Concat(fixId.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    /// <summary>Resolve the revert-record path for a given fix inside a game's install folder.</summary>
    internal static string GetFixRecordPath(string installDir, string fixId) =>
        Path.Combine(installDir, FixRecordDir, $"{SafeFixKey(fixId)}.json");

    /// <summary>Read a fix's revert record from disk, or null if it doesn't exist or won't parse.</summary>
    internal static DenuvoFixRecord? ReadFixRecord(string recordPath)
    {
        try
        {
            if (!File.Exists(recordPath)) return null;
            return JsonSerializer.Deserialize<DenuvoFixRecord>(File.ReadAllText(recordPath), ReadOpts);
        }
        catch { return null; }
    }

    /// <summary>True when this fix has a revert record in the game folder — i.e. it is applied.</summary>
    public static bool IsApplied(string? installDir, string fixId)
    {
        if (string.IsNullOrEmpty(installDir)) return false;
        try { return File.Exists(GetFixRecordPath(installDir, fixId)); }
        catch { return false; }
    }
}
