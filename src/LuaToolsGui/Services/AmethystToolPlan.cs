using System.IO;

namespace LuaToolsGui.Services;

/// <summary>
/// One file the AmethystTool install is going to place, and what has to happen to whatever is already
/// sitting at that path.
/// </summary>
/// <param name="FileName">Bare file name, always one of <see cref="AmethystToolPlan.PayloadFiles"/>.</param>
/// <param name="SourcePath">Where the extracted copy lives (inside the staging folder).</param>
/// <param name="DestinationPath">Where it goes — always directly in the Steam root.</param>
/// <param name="BackupPath">
/// Where the file currently at <see cref="DestinationPath"/> must be moved first, or
/// <see langword="null"/> when nothing is there to preserve.
/// </param>
public sealed record AmethystInstallStep(
    string FileName,
    string SourcePath,
    string DestinationPath,
    string? BackupPath);

/// <summary>
/// One file left in the Steam root by the backend this install displaces, which the payload does NOT
/// overwrite — so without this it would still be sitting next to <c>steam.exe</c> afterwards.
/// </summary>
/// <param name="FileName">Bare name, always one of <see cref="AmethystToolPlan.ConflictingFiles"/>.</param>
/// <param name="SourcePath">Where it is now — directly in the Steam root.</param>
/// <param name="BackupPath">Where it is moved to. Moved, never deleted: it is another tool's file.</param>
public sealed record AmethystQuarantineStep(
    string FileName,
    string SourcePath,
    string BackupPath);

/// <summary>
/// The decision of what an AmethystTool install would do, made entirely from values — no disk is touched
/// to produce one. Either every step is known and <see cref="Rejection"/> is null, or nothing is installed
/// and <see cref="Rejection"/> says why.
/// </summary>
public sealed record AmethystInstallPlan(
    IReadOnlyList<AmethystInstallStep> Steps,
    string? BackupDirectory,
    string? Rejection)
{
    /// <summary>
    /// Files the displaced backend placed that the payload does not overwrite, and which have to leave the
    /// Steam root for AmethystTool to be the only engine there. Empty for a clean Steam root.
    /// </summary>
    /// <remarks>
    /// Not part of the positional signature so the three-argument construction every existing caller and
    /// test uses keeps compiling and keeps meaning "nothing to quarantine".
    /// </remarks>
    public IReadOnlyList<AmethystQuarantineStep> Quarantine { get; init; } = [];

    public bool Rejected => Rejection is not null;

    /// <summary>True when at least one destination file already existed and will be preserved first.</summary>
    public bool HasBackups => BackupDirectory is not null;

    internal static AmethystInstallPlan Reject(string reason) => new([], null, reason);
}

/// <summary>
/// The pure half of the AmethystTool installer: given a Steam root, a staging folder and the names the
/// archive actually produced, decide which files get copied where and what has to be backed up first.
///
/// <para>
/// Split out from <see cref="AmethystToolService"/> deliberately. The decisions worth being sure about —
/// "only these four names are ever written", "nothing escapes the Steam root", "an existing DLL is never
/// clobbered without a copy first" — are the ones that are hardest to exercise against a real Steam
/// install, and easiest to get wrong. Here they are ordinary functions over strings, so the tests cover
/// them with a temp folder and no Steam at all.
/// </para>
///
/// <para>
/// This is defence in depth, not the only defence. <see cref="FixAnalyzer.AnalyzeArchive"/> already
/// refuses the archive outright for zip-slip, absolute paths and duplicate destinations before anything is
/// extracted. The allow-list below re-checks the shape of every name it is handed anyway, because this is
/// the last place a name can be rejected before it becomes a path next to steam.exe.
/// </para>
/// </summary>
public static class AmethystToolPlan
{
    /// <summary>
    /// The only files that are ever copied into the Steam root.
    ///
    /// <para>
    /// The release archive also carries INSTALL.txt, README.md, RELEASE_NOTES.md and TESTING.md. Those are
    /// documentation and have no business in the Steam root, so this is an allow-list rather than an
    /// exclusion list: a file the archive gains in a future release is ignored by default instead of being
    /// installed by default.
    /// </para>
    ///
    /// <para>
    /// <c>dwmapi.dll</c> and <c>xinput1_4.dll</c> are PROXY DLLs — steam.exe loads them by name and they
    /// forward to the real system copies. If one of them is already present it belongs to some other tool
    /// (or to a previous AmethystTool install), and overwriting it without a copy first is how a Steam
    /// install gets broken in a way the user cannot undo. Hence <see cref="Create"/> never plans an
    /// overwrite without a matching backup step.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> PayloadFiles =
    [
        "AmethystTool.dll",
        "amethysttool.toml",
        "dwmapi.dll",
        "xinput1_4.dll",
    ];

    /// <summary>
    /// Files another backend leaves behind that this install has to move out of the way.
    ///
    /// <para>
    /// The proxy DLLs take care of themselves: <c>dwmapi.dll</c> and <c>xinput1_4.dll</c> are in
    /// <see cref="PayloadFiles"/>, so installing overwrites whatever a Mode put there and steam.exe loads
    /// AmethystTool instead. <c>OpenSteamTool.dll</c> is the part that does not — BetterSteamTools places
    /// it, the payload has no file by that name, and AmethystTool is a FORK of BetterSteamTools whose
    /// loader can still find and load it. That leaves two engines hooking one Steam process, which is not
    /// a state anything downstream is built for. Its config travels with it, so a later reinstall does not
    /// find a stale <c>opensteamtool.toml</c> next to a DLL that came back.
    /// </para>
    ///
    /// <para>
    /// <c>cloud_redirect.dll</c> is deliberately NOT here. Nothing loads it by name — OpenSteamTool does —
    /// so once that DLL is out of the root it is inert, and moving a separate add-on's file would be doing
    /// more than getting the conflict out of the way.
    /// </para>
    ///
    /// <para>These are MOVED into the backup folder, never deleted: they belong to another tool, and a
    /// user who switches back must find them where the card says they went.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> ConflictingFiles =
    [
        "OpenSteamTool.dll",
        "opensteamtool.toml",
    ];

    /// <summary>
    /// The payload files no <see cref="UnlockerMode"/> ever places — the ones whose presence points at
    /// AmethystTool and at nothing else.
    ///
    /// <para>
    /// The other two, <c>dwmapi.dll</c> and <c>xinput1_4.dll</c>, are shared with every Mode and are
    /// therefore evidence of nothing. See <see cref="IsAmethystRoot"/> for the one decision this exists
    /// for.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> ExclusiveFiles =
    [
        "AmethystTool.dll",
        "amethysttool.toml",
    ];

    /// <summary>Prefix of the per-install backup folder created inside the Steam root.</summary>
    public const string BackupDirectoryPrefix = "AmethystTool-backup-";

    /// <summary>
    /// Decide the install.
    /// </summary>
    /// <param name="steamRoot">Steam's install root — where the four payload files end up.</param>
    /// <param name="stagedRoot">Folder the archive was extracted into.</param>
    /// <param name="stagedFileNames">
    /// Names the extraction produced, relative to <paramref name="stagedRoot"/>. Anything that is not a
    /// plain file name rejects the whole plan.
    /// </param>
    /// <param name="existingSteamRootFiles">
    /// Names already present in the Steam root. Only membership is consulted, which is what keeps this
    /// function free of I/O — the caller does the one directory listing.
    /// </param>
    /// <param name="now">Timestamp for the backup folder name; supplied so tests are deterministic.</param>
    public static AmethystInstallPlan Create(
        string steamRoot,
        string stagedRoot,
        IEnumerable<string> stagedFileNames,
        IReadOnlySet<string> existingSteamRootFiles,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedRoot);
        ArgumentNullException.ThrowIfNull(stagedFileNames);
        ArgumentNullException.ThrowIfNull(existingSteamRootFiles);

        var staged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in stagedFileNames)
        {
            if (!IsPlainFileName(raw))
                return AmethystInstallPlan.Reject(
                    $"the archive produced an entry that is not a plain file name ({Describe(raw)})");

            // A duplicate can only reach here if the caller enumerated the same file twice; treating it as
            // a rejection rather than silently collapsing it keeps "what was seen" and "what is installed"
            // the same set.
            if (!staged.Add(raw))
                return AmethystInstallPlan.Reject($"the archive produced '{raw}' more than once");
        }

        var steps = new List<AmethystInstallStep>(PayloadFiles.Count);
        var missing = new List<string>();
        bool anyBackup = false;

        foreach (string name in PayloadFiles)
        {
            if (!staged.Contains(name)) { missing.Add(name); continue; }
            if (existingSteamRootFiles.Contains(name)) anyBackup = true;
        }

        // Partial payloads are refused rather than half-installed: a proxy DLL without AmethystTool.dll
        // next to it makes steam.exe load a forwarder whose target is absent.
        if (missing.Count > 0)
            return AmethystInstallPlan.Reject(
                $"the archive is missing required file(s): {string.Join(", ", missing)}");

        // The displaced backend's own files. They need the same backup folder as an overwrite does, so
        // finding one is enough on its own to create it.
        var conflicting = ConflictingFiles.Where(existingSteamRootFiles.Contains).ToList();

        string? backupDir = anyBackup || conflicting.Count > 0
            ? Path.Combine(steamRoot, BackupDirectoryPrefix + now.ToString("yyyyMMdd-HHmmss"))
            : null;

        foreach (string name in PayloadFiles)
        {
            steps.Add(new AmethystInstallStep(
                name,
                Path.Combine(stagedRoot, name),
                Path.Combine(steamRoot, name),
                backupDir is not null && existingSteamRootFiles.Contains(name)
                    ? Path.Combine(backupDir, name)
                    : null));
        }

        return new AmethystInstallPlan(steps, backupDir, null)
        {
            Quarantine = [.. conflicting.Select(name => new AmethystQuarantineStep(
                name,
                Path.Combine(steamRoot, name),
                Path.Combine(backupDir!, name)))],
        };
    }

    /// <summary>
    /// Whether every payload file is present in the Steam root — the network-free "is it installed?" test.
    /// </summary>
    public static bool IsInstalled(IReadOnlySet<string> steamRootFiles)
    {
        ArgumentNullException.ThrowIfNull(steamRootFiles);
        return PayloadFiles.All(steamRootFiles.Contains);
    }

    /// <summary>
    /// Whether this Steam root carries AmethystTool's own files — the question first-run detection has to
    /// ask before it hashes anything.
    ///
    /// <para>
    /// <b>What it is for.</b> <see cref="UnlockerService.DetectActiveModeAsync"/> adopts a Mode by hashing
    /// <c>dwmapi.dll</c> and <c>xinput1_4.dll</c> against published releases. AmethystTool is a FORK, so
    /// those two files can be byte-identical to the Mode it forked from — meaning a root that is
    /// AmethystTool's matches BetterSteamTools perfectly, and with an empty slot (fresh or lost
    /// <c>settings.json</c>) detection would hand the ACTIVE badge to the wrong card. The hash says what a
    /// file IS; it cannot say which tool put it there. These two names can, because no Mode places them.
    /// </para>
    ///
    /// <para>
    /// <b>It abstains rather than claims.</b> A true answer makes detection select nothing, leaving the
    /// slot at <see cref="ActiveBackend.None"/> for the user to resolve. It deliberately does NOT select
    /// AmethystTool: file presence is the same weak evidence
    /// <see cref="AmethystToolService.BackfillRecordIfMissing"/> already refuses to claim ownership from,
    /// and "we don't know" is a state this app can show honestly.
    /// </para>
    /// </summary>
    public static bool IsAmethystRoot(IReadOnlySet<string> steamRootFiles)
    {
        ArgumentNullException.ThrowIfNull(steamRootFiles);
        return ExclusiveFiles.All(steamRootFiles.Contains);
    }

    /// <summary>
    /// A bare file name with no directory part, no traversal, no root and no alternate data stream — the
    /// only shape that can safely be combined with the Steam root.
    /// </summary>
    private static bool IsPlainFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name is "." or "..") return false;
        if (name.Contains(':')) return false; // drive-relative ("C:x") and NTFS streams ("a.dll:evil")
        if (name.IndexOfAny(['/', '\\']) >= 0) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

        // Path.GetFileName is the final arbiter: if it disagrees with the input, the input carried
        // something path-ish that the checks above did not name.
        return Path.GetFileName(name) == name;
    }

    /// <summary>Rejected names are attacker-influenced, so only a bounded, quoted form goes into a message
    /// that may be logged or shown.</summary>
    private static string Describe(string? name) =>
        name is null ? "null" : $"'{(name.Length > 60 ? name[..60] + "…" : name)}'";
}
