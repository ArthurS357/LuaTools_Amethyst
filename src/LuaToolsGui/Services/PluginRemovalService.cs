using System.IO;

namespace LuaToolsGui.Services;

/// <summary>What an uninstall actually did.</summary>
/// <param name="Removed">Files moved out of the Steam root.</param>
/// <param name="SharedKept">Names left in place because another install still claims them.</param>
/// <param name="AlreadyGone">Recorded names that were not on disk any more.</param>
/// <param name="BackupDirectory">Where the removed files were kept, or null if none were removed.</param>
/// <param name="SteamStopped">Whether Steam had to be stopped to release file locks.</param>
/// <param name="Error">Non-null when the removal failed; every other field is then meaningless.</param>
public sealed record PluginRemovalOutcome(
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> SharedKept,
    IReadOnlyList<string> AlreadyGone,
    string? BackupDirectory,
    bool SteamStopped,
    string? Error)
{
    public bool Failed => Error is not null;

    /// <summary>Nothing was recorded for this plugin, so nothing could be removed. Distinct from a
    /// successful removal of zero files, and worth a different message.</summary>
    public bool NothingRecorded { get; init; }

    /// <summary>True when the call was a no-op: the files were already gone, or all shared.</summary>
    public bool NothingToDo => !Failed && Removed.Count == 0;

    internal static PluginRemovalOutcome Fail(string error) =>
        new([], [], [], null, SteamStopped: false, error);
}

/// <summary>
/// Removes what a plugin put in the Steam root, working from the install record rather than from a list
/// of names.
///
/// <para>
/// The I/O half; <see cref="PluginRemoval"/> makes every decision. This type reads the manifest, lists the
/// Steam root, asks for a plan, stops Steam, and carries the plan out.
/// </para>
///
/// <para>
/// <b>Steam is stopped but never restarted.</b> An uninstall is the user deciding they want this gone;
/// relaunching Steam for them puts a client back up that may now be missing a proxy DLL it was loading a
/// moment ago, and does it without asking. The outcome reports that Steam was stopped so the UI can say
/// so, and the user reopens it when they are ready.
/// </para>
/// </summary>
public sealed class PluginRemovalService(
    SteamService steam, InstallManifestService manifests, UnlockerService unlocker)
{
    /// <summary>Names present directly in the Steam root.</summary>
    private IReadOnlySet<string> SteamRootFiles()
    {
        if (steam.EffectivePath is not { } dir || !Directory.Exists(dir))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Every file name some OTHER live install still needs. The I/O half — gathering the manifest and the
    /// active Mode; <see cref="PluginRemoval.CombineClaims"/> decides what the two of them mean.
    /// </summary>
    public IReadOnlySet<string> ClaimedByOthers(string pluginId)
    {
        var activeMode = unlocker.SelectedMode;

        return PluginRemoval.CombineClaims(
            manifests.Load().FilesClaimedByOthers(pluginId),
            pluginId,
            activeMode is { } m ? PluginIds.ForMode(m) : null,
            activeMode is { } mode
                ? unlocker.Modes.Where(d => d.Mode == mode).SelectMany(d => d.PlaceFiles)
                : []);
    }

    /// <summary>Whether there is an install record to remove — what gates the Uninstall button.</summary>
    public bool HasRecordFor(string pluginId) =>
        manifests.Load().Get(pluginId) is { Files.Count: > 0 };

    /// <summary>
    /// Remove <paramref name="pluginId"/>'s recorded files from the Steam root, then forget it.
    /// Idempotent: with nothing left to remove it reports a no-op instead of failing.
    /// </summary>
    /// <param name="stopSteam">
    /// False skips stopping Steam — for a caller that has already stopped it, or for a plugin whose files
    /// Steam does not hold open.
    /// </param>
    public async Task<PluginRemovalOutcome> RemoveAsync(
        string pluginId, bool stopSteam = true, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        if (steam.EffectivePath is not { } steamRoot)
            return PluginRemovalOutcome.Fail(Resources.Strings.Plugin_Err_SteamNotFound);

        if (manifests.Load().Get(pluginId) is not { Files.Count: > 0 } entry)
            return new PluginRemovalOutcome([], [], [], null, SteamStopped: false, Error: null)
            {
                NothingRecorded = true,
            };

        var plan = PluginRemoval.Create(
            steamRoot, pluginId,
            entry.Files.Select(f => f.Name),
            SteamRootFiles(),
            ClaimedByOthers(pluginId),
            DateTimeOffset.Now);

        if (plan.Rejection is { } rejection)
            return PluginRemovalOutcome.Fail(string.Format(Resources.Strings.Removal_Err_Record, rejection));

        var alreadyGone = plan.Skipped
            .Where(s => s.Reason == RemovalSkipReason.Absent).Select(s => s.FileName).ToList();

        // Nothing to move: forget the plugin and return without touching Steam. Stopping a user's client
        // to perform zero file operations is the kind of thing that makes a feature feel unsafe.
        if (plan.IsNoOp)
        {
            manifests.Forget(pluginId);
            return new PluginRemovalOutcome([], plan.SharedKept, alreadyGone, null,
                SteamStopped: false, Error: null);
        }

        bool steamStopped = false;
        try
        {
            if (stopSteam && SteamService.IsSteamRunning())
            {
                // allowKill: true — this asks Steam to close first and only forces if it refuses, which is
                // what StopSteamGraceful already does. Locked proxy DLLs cannot be moved, so "Steam is
                // down" is not optional here; being polite about how it gets there is.
                steam.StopSteamGraceful(allowKill: true);
                steamStopped = true;
                await Task.Delay(1200, ct); // let handles on the DLLs release after the shutdown
            }

            ApplyPlan(plan);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return PluginRemovalOutcome.Fail(ex.Message) with { SteamStopped = steamStopped }; }

        manifests.Forget(pluginId);

        return new PluginRemovalOutcome(
            [.. plan.Steps.Select(s => s.FileName)],
            plan.SharedKept,
            alreadyGone,
            plan.BackupDirectory,
            steamStopped,
            Error: null);
    }

    /// <summary>
    /// Carry out a plan. Every file is <b>moved</b> into the backup folder, never deleted: an uninstall the
    /// user regrets — or one that removed a proxy some other tool actually needed — is then a matter of
    /// moving files back, rather than of reinstalling something to find out which version it was.
    /// </summary>
    internal static void ApplyPlan(PluginRemovalPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Rejected) throw new InvalidOperationException("Refusing to apply a rejected removal plan.");
        if (plan.BackupDirectory is null) return; // no steps; nothing to create a folder for

        Directory.CreateDirectory(plan.BackupDirectory);

        foreach (var step in plan.Steps)
        {
            // Re-checked at the moment of the move: the plan was built from a directory listing taken
            // before Steam was stopped, and a file can legitimately vanish in between.
            if (!File.Exists(step.SourcePath)) continue;
            File.Move(step.SourcePath, step.BackupPath, overwrite: true);
        }
    }
}
