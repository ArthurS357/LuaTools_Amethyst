using System.IO;

namespace LuaToolsGui.Services;

/// <summary>One file a removal is going to take out of the Steam root, and where it is kept first.</summary>
public sealed record RemovalStep(string FileName, string SourcePath, string BackupPath);

/// <summary>Why a file the manifest names is being left where it is.</summary>
public enum RemovalSkipReason
{
    /// <summary>It is not on disk any more — already removed, or moved by hand. Nothing to do.</summary>
    Absent,

    /// <summary>Another install still claims this exact name, so removing it would break that one.</summary>
    ClaimedByAnotherInstall,
}

/// <summary>A file the plan deliberately leaves alone.</summary>
public sealed record RemovalSkip(string FileName, RemovalSkipReason Reason);

/// <summary>
/// The decision of what an uninstall would do, made entirely from values. Either the steps are known and
/// <see cref="Rejection"/> is null, or nothing is removed and <see cref="Rejection"/> says why.
/// </summary>
public sealed record PluginRemovalPlan(
    IReadOnlyList<RemovalStep> Steps,
    IReadOnlyList<RemovalSkip> Skipped,
    string? BackupDirectory,
    string? Rejection)
{
    public bool Rejected => Rejection is not null;

    /// <summary>True when the plan would not touch a single file — everything is gone or shared.</summary>
    public bool IsNoOp => !Rejected && Steps.Count == 0;

    /// <summary>Names left in place because another install still needs them.</summary>
    public IReadOnlyList<string> SharedKept =>
        [.. Skipped.Where(s => s.Reason == RemovalSkipReason.ClaimedByAnotherInstall).Select(s => s.FileName)];

    internal static PluginRemovalPlan Reject(string reason) => new([], [], null, reason);
}

/// <summary>
/// The pure half of uninstalling: given what a plugin recorded, what is actually on disk, and what other
/// installs still claim, decide which files may be removed.
///
/// <para>
/// Three rules do the real work, and each one exists because of a specific way this can break a Steam
/// install.
/// </para>
///
/// <para>
/// <b>Removal is driven by the manifest, never by a list of names.</b> Nothing is removed for a plugin
/// with no recorded files. The alternative — "delete the four names AmethystTool installs" — deletes files
/// this app never placed, which for a proxy DLL means breaking whatever tool did place it.
/// </para>
///
/// <para>
/// <b>A name another install still claims is kept.</b> <c>dwmapi.dll</c> and <c>xinput1_4.dll</c> are
/// placed by AmethystTool AND by three of the Mode page's unlockers. Uninstalling one while the other is
/// live would leave Steam loading a proxy whose partner is gone. The file stays; only the manifest entry
/// goes, and the caller is told which names were kept.
/// </para>
///
/// <para>
/// <b>Nothing is deleted outright.</b> Every removal is a move into a timestamped backup folder, the same
/// shape <see cref="AmethystToolPlan"/> uses when it displaces a file. An uninstall that turns out to have
/// been a mistake is then recoverable, which "delete" never is.
/// </para>
/// </summary>
public static class PluginRemoval
{
    /// <summary>Prefix of the per-uninstall backup folder created inside the Steam root.</summary>
    public const string BackupDirectoryPrefix = "Removal-backup-";

    /// <summary>
    /// Decide the removal.
    /// </summary>
    /// <param name="steamRoot">Steam's install root — where the recorded files live.</param>
    /// <param name="pluginId">Which plugin is being removed; also the backup sub-folder name.</param>
    /// <param name="recordedFiles">The file names this plugin's manifest entry lists.</param>
    /// <param name="existingSteamRootFiles">Names currently present in the Steam root.</param>
    /// <param name="claimedByOtherInstalls">
    /// Names some other live install still needs — other manifest entries, plus the active Mode's files.
    /// Anything in here is kept no matter what the manifest says.
    /// </param>
    /// <param name="now">Timestamp for the backup folder; supplied so tests are deterministic.</param>
    public static PluginRemovalPlan Create(
        string steamRoot,
        string pluginId,
        IEnumerable<string> recordedFiles,
        IReadOnlySet<string> existingSteamRootFiles,
        IReadOnlySet<string> claimedByOtherInstalls,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(recordedFiles);
        ArgumentNullException.ThrowIfNull(existingSteamRootFiles);
        ArgumentNullException.ThrowIfNull(claimedByOtherInstalls);

        if (!IsPlainFileName(pluginId))
            return PluginRemovalPlan.Reject($"the plugin id is not usable as a folder name ({Describe(pluginId)})");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();
        foreach (string raw in recordedFiles)
        {
            // The manifest is app-written, but it is a file on disk a user can edit, and every name in it
            // becomes a path next to steam.exe. It gets the same shape check the installer applies.
            if (!IsPlainFileName(raw))
                return PluginRemovalPlan.Reject(
                    $"the install record names an entry that is not a plain file name ({Describe(raw)})");

            if (seen.Add(raw)) candidates.Add(raw); // a repeated name is one file, not two removals
        }

        var steps = new List<RemovalStep>(candidates.Count);
        var skipped = new List<RemovalSkip>();

        // Resolved before the backup folder is named, so a plan that removes nothing names no folder and
        // the caller never creates an empty directory in the user's Steam root.
        var removable = new List<string>(candidates.Count);
        foreach (string name in candidates)
        {
            if (claimedByOtherInstalls.Contains(name))
            {
                skipped.Add(new RemovalSkip(name, RemovalSkipReason.ClaimedByAnotherInstall));
                continue;
            }
            if (!existingSteamRootFiles.Contains(name))
            {
                skipped.Add(new RemovalSkip(name, RemovalSkipReason.Absent));
                continue;
            }
            removable.Add(name);
        }

        string? backupDir = removable.Count > 0
            ? Path.Combine(steamRoot, BackupDirectoryPrefix + now.ToString("yyyyMMdd-HHmmss"), pluginId)
            : null;

        foreach (string name in removable)
            steps.Add(new RemovalStep(
                name,
                Path.Combine(steamRoot, name),
                Path.Combine(backupDir!, name)));

        return new PluginRemovalPlan(steps, skipped, backupDir, null);
    }

    /// <summary>
    /// Everything some OTHER live install still needs, as values.
    ///
    /// <para>
    /// Two sources. The manifest is the obvious one. The <b>active Mode</b> is the half that is easy to
    /// miss: a Mode installed before Modes were recorded keeps no manifest entry of its own, only
    /// <c>settings.SelectedMode</c>, and three of the Mode page's unlockers place <c>dwmapi.dll</c> and
    /// <c>xinput1_4.dll</c> — the same two names AmethystTool installs.
    /// </para>
    ///
    /// <para>
    /// <b>The active Mode does not claim against itself.</b> Without that exclusion, uninstalling the
    /// active Mode would find every one of its own files "still needed by another install" and remove
    /// nothing at all, reporting success — the exact failure the shared-file rule exists to prevent,
    /// pointed at the wrong target.
    /// </para>
    /// </summary>
    /// <param name="manifestClaims">Names other manifest entries list — <see cref="InstallManifest.FilesClaimedByOthers"/>.</param>
    /// <param name="pluginId">The plugin being removed.</param>
    /// <param name="activeModePluginId">Id of the active Mode, or null when no Mode is selected.</param>
    /// <param name="activeModeFiles">Files that Mode places in the Steam root.</param>
    public static IReadOnlySet<string> CombineClaims(
        IEnumerable<string> manifestClaims,
        string pluginId,
        string? activeModePluginId,
        IEnumerable<string> activeModeFiles)
    {
        ArgumentNullException.ThrowIfNull(manifestClaims);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(activeModeFiles);

        var claimed = new HashSet<string>(manifestClaims, StringComparer.OrdinalIgnoreCase);

        if (activeModePluginId is null
            || activeModePluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase))
            return claimed;

        foreach (string file in activeModeFiles) claimed.Add(file);
        return claimed;
    }

    /// <summary>
    /// A bare file name with no directory part, no traversal, no root and no alternate data stream — the
    /// only shape that can safely be combined with the Steam root. Same rule as
    /// <see cref="AmethystToolPlan"/> applies on the way in; removal is the other direction and deserves
    /// the same gate.
    ///
    /// <para>
    /// Public because the gate belongs at both ends. <see cref="UnlockerService"/> reads names back out of
    /// the manifest when a Mode install folds the previous Mode's leftovers into its own record, and the
    /// manifest is a file on disk a user can edit — a name that is really a path would otherwise be probed,
    /// hashed and written straight back in before removal ever got the chance to refuse it.
    /// </para>
    /// </summary>
    public static bool IsPlainFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name is "." or "..") return false;
        if (name.Contains(':')) return false; // drive-relative ("C:x") and NTFS streams ("a.dll:evil")
        if (name.IndexOfAny(['/', '\\']) >= 0) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

        return Path.GetFileName(name) == name;
    }

    /// <summary>Bounded, quoted form for a message that may be logged or shown.</summary>
    private static string Describe(string? name) =>
        name is null ? "null" : $"'{(name.Length > 60 ? name[..60] + "…" : name)}'";
}
