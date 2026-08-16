using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace LuaToolsGui.Services;

/// <summary>What kind of problem an analysis turned up.</summary>
public enum FixFindingKind
{
    /// <summary>The archive could not be opened or read.</summary>
    Unreadable,

    /// <summary>An entry path escapes the destination directory (zip-slip).</summary>
    PathEscape,

    /// <summary>An entry path is rooted, drive-qualified or a UNC path.</summary>
    AbsolutePath,

    /// <summary>Two entries resolve to the same destination path.</summary>
    DuplicateEntry,

    /// <summary>The archive has an implausible number of entries.</summary>
    TooManyEntries,

    /// <summary>A single entry expands beyond the allowed size.</summary>
    EntryTooLarge,

    /// <summary>The archive expands beyond the allowed total size.</summary>
    ArchiveTooLarge,

    /// <summary>The archive expands far out of proportion to its compressed size (zip bomb).</summary>
    CompressionRatio,

    /// <summary>A <c>.lua</c> entry uses a call a manifest never needs.</summary>
    DangerousLuaCall,

    /// <summary>A <c>.lua</c> entry only reveals a dangerous call after de-obfuscation.</summary>
    ObfuscatedLuaCall,

    /// <summary>Informational: a <c>.lua</c> entry has lines that are not known manifest directives.</summary>
    UnrecognizedLuaLines,

    /// <summary>Informational: the archive contains another archive.</summary>
    NestedArchive,

    /// <summary>Informational: the archive contains executable code (normal for a game fix).</summary>
    ExecutableContent,
}

/// <summary>
/// Which rules a <c>.lua</c> entry is judged by. The two are not interchangeable, and treating them as
/// one shipped a bug: the plugin flow screened <c>backend/main.lua</c> with the manifest rules and refused
/// the install over <c>require</c>.
/// </summary>
public enum LuaScreeningProfile
{
    /// <summary>
    /// A Steam manifest: a tiny DSL of <c>addappid()</c> / <c>setManifestid()</c> calls that the unlocker
    /// interprets inside steam.exe. Anything outside that vocabulary is suspect, so the denylist is broad
    /// — see <see cref="LuaManifestValidator"/>.
    /// </summary>
    SteamManifest,

    /// <summary>
    /// Ordinary Lua source that is meant to be a program (a plugin backend). Only constructs that execute
    /// code or spawn a process are refused — see <see cref="LuaCodeValidator"/>.
    /// </summary>
    ApplicationCode,
}

/// <summary>One observation about a file being installed.</summary>
/// <param name="Kind">What was observed.</param>
/// <param name="Blocking">True when this alone must stop the install.</param>
/// <param name="Detail">Human-readable explanation, safe to show in a toast.</param>
/// <param name="Entry">Archive entry it relates to, when applicable.</param>
public sealed record FixFinding(FixFindingKind Kind, bool Blocking, string Detail, string? Entry = null)
{
    public override string ToString() => Entry is null ? Detail : $"{Entry}: {Detail}";
}

/// <summary>The result of analysing a fix or manifest payload.</summary>
public sealed record FixAnalysis(IReadOnlyList<FixFinding> Findings)
{
    public static FixAnalysis Empty { get; } = new([]);

    /// <summary>True when at least one finding is blocking — the payload must not be installed.</summary>
    public bool Blocked => Findings.Any(f => f.Blocking);

    /// <summary>The first blocking finding's explanation, for the error shown to the user.</summary>
    public string? BlockReason => Findings.FirstOrDefault(f => f.Blocking)?.ToString();

    /// <summary>One-line summary for the log: what was found, blocking or not.</summary>
    public string Summary => Findings.Count == 0
        ? "no findings"
        : string.Join("; ", Findings.Select(f => $"{(f.Blocking ? "BLOCK" : "note")} {f.Kind}{(f.Entry is null ? "" : $" [{f.Entry}]")}"));
}

/// <summary>
/// Size and shape limits. Deliberately generous — these exist to catch the absurd (a decompression bomb,
/// a hundred thousand entries), not to second-guess how big a legitimate game fix may be.
/// </summary>
/// <param name="MaxEntries">Reject archives with more entries than this.</param>
/// <param name="MaxEntryBytes">Reject any single entry that expands beyond this.</param>
/// <param name="MaxTotalBytes">Reject archives whose entries expand beyond this in total.</param>
/// <param name="MaxCompressionRatio">
/// Reject when uncompressed/compressed exceeds this AND the total is over
/// <paramref name="RatioFloorBytes"/>. The floor matters: a 2 KB file of zeros has an enormous ratio and
/// is completely harmless, so applying the ratio rule to small archives is pure false positives.
/// </param>
/// <param name="RatioFloorBytes">Total uncompressed size below which the ratio rule is not applied.</param>
public sealed record FixAnalyzerLimits(
    int MaxEntries = 20_000,
    long MaxEntryBytes = 2L * 1024 * 1024 * 1024,
    long MaxTotalBytes = 8L * 1024 * 1024 * 1024,
    int MaxCompressionRatio = 200,
    long RatioFloorBytes = 50L * 1024 * 1024)
{
    public static FixAnalyzerLimits Default { get; } = new();
}

/// <summary>
/// Screens a downloaded fix or manifest payload BEFORE it is written anywhere.
///
/// <para>
/// <see cref="LuaManifestValidator"/> already screens a single lua's source, and every install path funnels
/// through it. That covers what a manifest lua may CONTAIN, but not what an ARCHIVE may do while being
/// extracted, and it is blind to a dangerous call that is assembled at runtime rather than written
/// literally. This class covers both, and is the gate the Fixes page calls before it touches the disk.
/// </para>
///
/// <para>
/// The concrete hole this closes: the Fixes "fix" slot extracted a zip into the game folder with
/// <c>Path.Combine(installDir, entry.FullName)</c> and no containment check. <c>Path.Combine</c> returns
/// the second argument outright when it is rooted, so an entry named <c>C:\Windows\System32\x.dll</c> — or
/// one traversing with <c>..\..\</c> — wrote wherever it liked, with the app's privileges. That is
/// zip-slip, and it was reachable from a fix served by the API.
/// </para>
///
/// <para>
/// DESIGN BIAS. A false positive here breaks a legitimate fix a user wants; a false negative fails to
/// catch an unusual attack. So the blocking set is narrow and structural — path escapes, absolute paths,
/// duplicate destinations, absurd sizes, and lua calls that have no place in a manifest. Everything else
/// (executables in a fix zip, nested archives, unrecognised lua directives) is RECORDED, not blocked:
/// game fixes are executables by nature, and blocking on that would break the feature outright.
/// </para>
/// </summary>
public static partial class FixAnalyzer
{
    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar", ".tar", ".gz", ".cab"];
    private static readonly string[] ExecutableExtensions = [".exe", ".dll", ".sys", ".bat", ".cmd", ".ps1", ".scr", ".msi", ".com"];

    /// <summary>
    /// Analyse an archive that is about to be extracted into <paramref name="destinationRoot"/>.
    /// </summary>
    /// <param name="archivePath">The staged .zip on disk.</param>
    /// <param name="destinationRoot">
    /// Where entries would be written, used for the containment check. Pass null when entries are not
    /// extracted by path (e.g. <see cref="LuaInstaller.InstallZip"/> flattens to fixed names), and the
    /// path-escape check is skipped as inapplicable while every other check still runs.
    /// </param>
    /// <param name="limits">Size/shape limits; <see cref="FixAnalyzerLimits.Default"/> when null.</param>
    /// <param name="luaProfile">
    /// Which rules <c>.lua</c> entries are judged by. Defaults to <see cref="LuaScreeningProfile.SteamManifest"/>
    /// because that is what the Fixes flow ships; an archive of application code must pass
    /// <see cref="LuaScreeningProfile.ApplicationCode"/> or every module it loads reads as a violation.
    /// </param>
    public static FixAnalysis AnalyzeArchive(string archivePath, string? destinationRoot,
        FixAnalyzerLimits? limits = null, LuaScreeningProfile luaProfile = LuaScreeningProfile.SteamManifest)
    {
        limits ??= FixAnalyzerLimits.Default;
        var findings = new List<FixFinding>();

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(archivePath);
        }
        catch (Exception ex)
        {
            // Unreadable is blocking: something that cannot be inspected must not be extracted.
            return new FixAnalysis([new(FixFindingKind.Unreadable, true, $"the archive could not be read ({ex.Message})")]);
        }

        using (archive)
        {
            if (archive.Entries.Count > limits.MaxEntries)
            {
                findings.Add(new(FixFindingKind.TooManyEntries, true,
                    $"the archive has {archive.Entries.Count} entries (limit {limits.MaxEntries})"));
                return new FixAnalysis(findings);
            }

            long totalUncompressed = 0;
            long totalCompressed = 0;
            var seenDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in archive.Entries)
            {
                string name = entry.FullName;
                if (name.Length == 0) continue;

                bool isDirectory = name.EndsWith('/') || name.EndsWith('\\') || string.IsNullOrEmpty(entry.Name);

                // ── Structure ────────────────────────────────────────────────
                if (IsAbsoluteOrRooted(name))
                {
                    findings.Add(new(FixFindingKind.AbsolutePath, true,
                        "entry uses an absolute path, which would write outside the target folder", name));
                    continue; // its destination is meaningless; don't also report an escape
                }

                // Containment and duplicate-destination only mean anything when entries are extracted BY
                // PATH. With no destination root the caller flattens them to fixed names, so both checks
                // are skipped as inapplicable rather than guessed at.
                if (destinationRoot is not null)
                {
                    if (!IsContained(destinationRoot, name, out string resolved))
                    {
                        findings.Add(new(FixFindingKind.PathEscape, true,
                            "entry path escapes the target folder (zip-slip)", name));
                        continue;
                    }

                    // Two entries writing the same file is a known analyser-evasion trick: the check sees
                    // one, the extractor's last write wins with the other.
                    if (!isDirectory && !seenDestinations.Add(resolved))
                    {
                        findings.Add(new(FixFindingKind.DuplicateEntry, true,
                            "two entries resolve to the same destination file", name));
                        continue;
                    }
                }

                if (isDirectory) continue;

                // ── Size ─────────────────────────────────────────────────────
                if (entry.Length > limits.MaxEntryBytes)
                {
                    findings.Add(new(FixFindingKind.EntryTooLarge, true,
                        $"entry expands to {entry.Length:N0} bytes (limit {limits.MaxEntryBytes:N0})", name));
                    return new FixAnalysis(findings);
                }

                totalUncompressed += entry.Length;
                totalCompressed += entry.CompressedLength;

                if (totalUncompressed > limits.MaxTotalBytes)
                {
                    findings.Add(new(FixFindingKind.ArchiveTooLarge, true,
                        $"the archive expands beyond {limits.MaxTotalBytes:N0} bytes"));
                    return new FixAnalysis(findings);
                }

                // ── Content ──────────────────────────────────────────────────
                string extension = Path.GetExtension(entry.Name);

                if (extension.Equals(".lua", StringComparison.OrdinalIgnoreCase))
                    findings.AddRange(ScreenLuaEntry(entry, luaProfile));
                else if (ArchiveExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    findings.Add(new(FixFindingKind.NestedArchive, false, "contains a nested archive", name));
                else if (ExecutableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    findings.Add(new(FixFindingKind.ExecutableContent, false, "contains executable code", name));
            }

            // Ratio last: it needs the totals, and only means anything above the floor.
            if (totalUncompressed > limits.RatioFloorBytes && totalCompressed > 0)
            {
                long ratio = totalUncompressed / Math.Max(totalCompressed, 1);
                if (ratio > limits.MaxCompressionRatio)
                    findings.Add(new(FixFindingKind.CompressionRatio, true,
                        $"the archive expands {ratio}x, far beyond the {limits.MaxCompressionRatio}x limit " +
                        "(decompression bomb)"));
            }
        }

        return new FixAnalysis(findings);
    }

    /// <summary>Analyse a bare <c>.lua</c> file on disk (the non-zip fix/manifest download).</summary>
    public static FixAnalysis AnalyzeLuaFile(string path)
    {
        try
        {
            return AnalyzeLuaSource(File.ReadAllText(path), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            return new FixAnalysis([new(FixFindingKind.Unreadable, true, $"the file could not be read ({ex.Message})")]);
        }
    }

    /// <summary>
    /// Screen lua source both as written and after de-obfuscation.
    ///
    /// <para>
    /// <see cref="LuaManifestValidator"/> is the single source of truth for WHAT is forbidden — this does
    /// not keep a second denylist that could drift out of step with it. What this adds is running that
    /// same screen a second time over a normalised copy, so a call spelled
    /// <c>_G["\x6f\x73"]["execute"]</c> or <c>"os" .. "." .. "execute"</c> is caught too.
    /// </para>
    /// </summary>
    public static FixAnalysis AnalyzeLuaSource(string lua, string? entryName = null,
        LuaScreeningProfile profile = LuaScreeningProfile.SteamManifest)
    {
        var findings = new List<FixFinding>();

        LuaScreenResult Screen(string source) => profile == LuaScreeningProfile.ApplicationCode
            ? LuaCodeValidator.Screen(source)
            : LuaManifestValidator.Screen(source);

        var direct = Screen(lua);
        if (direct.Rejected)
        {
            findings.Add(new(FixFindingKind.DangerousLuaCall, true, $"the lua {direct.Reason}", entryName));
            return new FixAnalysis(findings);
        }

        string normalised = NormalizeLua(lua);
        if (!string.Equals(normalised, lua, StringComparison.Ordinal))
        {
            var deobfuscated = Screen(normalised);
            if (deobfuscated.Rejected)
            {
                findings.Add(new(FixFindingKind.ObfuscatedLuaCall, true,
                    $"the lua hides a forbidden call behind escapes or string joining — after decoding, it {deobfuscated.Reason}",
                    entryName));
                return new FixAnalysis(findings);
            }
        }

        if (direct.UnrecognizedLines > 0)
            findings.Add(new(FixFindingKind.UnrecognizedLuaLines, false,
                $"{direct.UnrecognizedLines} line(s) are not known manifest directives", entryName));

        return new FixAnalysis(findings);
    }

    private static IEnumerable<FixFinding> ScreenLuaEntry(ZipArchiveEntry entry, LuaScreeningProfile profile)
    {
        // Cap what is read: a lua that is not a lua-sized file is itself the answer. Reading it whole to
        // screen it would otherwise be a way to make the analyser the bomb.
        const int MaxLuaBytes = 8 * 1024 * 1024;
        if (entry.Length > MaxLuaBytes)
            return [new(FixFindingKind.EntryTooLarge, true,
                $"a .lua of {entry.Length:N0} bytes is implausible", entry.FullName)];

        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return AnalyzeLuaSource(reader.ReadToEnd(), entry.FullName, profile).Findings;
        }
        catch (Exception ex)
        {
            return [new(FixFindingKind.Unreadable, true, $"a .lua entry could not be read ({ex.Message})", entry.FullName)];
        }
    }

    // ── De-obfuscation ──────────────────────────────────────────────────────

    /// <summary>Lua <c>\xNN</c> hex escape.</summary>
    [GeneratedRegex(@"\\x([0-9A-Fa-f]{2})", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex HexEscapeRegex();

    /// <summary>Lua <c>\NNN</c> decimal escape (1–3 digits).</summary>
    [GeneratedRegex(@"\\(\d{1,3})", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex DecimalEscapeRegex();

    /// <summary>Two string literals joined by <c>..</c>, e.g. <c>"os" .. "execute"</c>.</summary>
    [GeneratedRegex(@"([""'])\s*\.\.\s*([""'])", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex ConcatJoinRegex();

    /// <summary>Indexing by string literal, e.g. <c>_G["os"]</c> or <c>os["execute"]</c>.</summary>
    [GeneratedRegex(@"\[\s*([""'])([A-Za-z_][A-Za-z0-9_]*)\1\s*\]", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex StringIndexRegex();

    /// <summary>
    /// Rewrite lua into a form where simple obfuscation of an identifier collapses back to the identifier,
    /// so the existing denylist can see it.
    ///
    /// <para>
    /// Three transforms, all of which only ever JOIN or DECODE — none can invent an identifier that the
    /// source could not evaluate to:
    /// </para>
    /// <list type="number">
    ///   <item><c>\x6f\x73</c> and <c>\111\115</c> escapes are decoded to their characters.</item>
    ///   <item>String concatenation between two literals is collapsed: <c>"os" .. ".execute"</c> becomes
    ///   <c>"os.execute"</c>.</item>
    ///   <item>Indexing by a string literal becomes dot access: <c>_G["os"]["execute"]</c> becomes
    ///   <c>_G.os.execute</c>.</item>
    /// </list>
    ///
    /// <para>
    /// This is intentionally NOT a Lua interpreter. It defeats the obfuscation people actually paste in,
    /// not a determined attacker computing a name at runtime — and it does not need to, because the result
    /// is only ever used to ADD a rejection. A manifest that survives both passes is no worse off than
    /// before this existed.
    /// </para>
    /// </summary>
    internal static string NormalizeLua(string lua)
    {
        if (string.IsNullOrEmpty(lua)) return lua ?? string.Empty;

        try
        {
            string result = HexEscapeRegex().Replace(lua, m =>
                ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

            result = DecimalEscapeRegex().Replace(result, m =>
                int.TryParse(m.Groups[1].Value, out int code) && code is >= 0 and <= 255
                    ? ((char)code).ToString()
                    : m.Value);

            // Collapse repeatedly: "o".."s".."." .. "execute" needs more than one pass.
            for (int i = 0; i < 4; i++)
            {
                string joined = ConcatJoinRegex().Replace(result, string.Empty);
                if (string.Equals(joined, result, StringComparison.Ordinal)) break;
                result = joined;
            }

            result = StringIndexRegex().Replace(result, ".$2");

            return result;
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological input: hand back something the caller will screen normally rather than throw.
            return lua;
        }
    }

    // ── Path safety ─────────────────────────────────────────────────────────

    /// <summary>True when an entry name is rooted, drive-qualified or a UNC path.</summary>
    internal static bool IsAbsoluteOrRooted(string entryName)
    {
        // Normalise separators first: a zip may use either, and Path.IsPathRooted on Windows only
        // recognises the ones it knows.
        string candidate = entryName.Replace('/', '\\');

        return candidate.StartsWith(@"\\", StringComparison.Ordinal)   // UNC
            || Path.IsPathRooted(candidate)                            // "\x" or "C:\x"
            || (candidate.Length >= 2 && candidate[1] == ':');         // "C:x" (drive-relative)
    }

    /// <summary>
    /// True when <paramref name="entryName"/> resolves inside <paramref name="root"/>.
    ///
    /// <para>
    /// Resolution goes through <see cref="Path.GetFullPath(string)"/> so <c>..</c> segments are collapsed
    /// by the same rules the filesystem uses, rather than by a string check that a crafted name could slip
    /// past. The trailing separator on the root is what stops <c>C:\games\Game</c> from being considered
    /// to contain <c>C:\games\GameEvil</c>.
    /// </para>
    /// </summary>
    internal static bool IsContained(string root, string entryName, out string resolved)
    {
        resolved = string.Empty;
        try
        {
            string fullRoot = Path.GetFullPath(root);
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
                fullRoot += Path.DirectorySeparatorChar;

            string candidate = Path.GetFullPath(Path.Combine(fullRoot, entryName));
            resolved = candidate;

            return candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Invalid characters, too long, etc. — treat as not contained rather than guessing.
            return false;
        }
    }
}
