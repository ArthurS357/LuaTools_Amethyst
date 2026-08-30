using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>Queried state of the installed AmethystTool vs. the latest published release.</summary>
public sealed record AmethystToolStatus(
    bool Installed,
    string? InstalledTag,   // from the on-disk manifest (null if never installed through this app)
    string? LatestTag,
    bool UpdateAvailable,
    bool Offline);          // couldn't reach GitHub (unreachable, or rate-limited)

/// <summary>
/// Installs / updates AmethystTool — a NATIVE injection plugin whose files live in the Steam install root,
/// where steam.exe loads them.
///
/// <para>
/// The I/O half of the feature. Everything that decides <i>what</i> happens is in
/// <see cref="AmethystToolPlan"/>; this type only carries it out: fetch the release JSON through
/// <see cref="GithubProxy"/> (so blocked regions still work), download the archive pinned to
/// <see cref="AppConfig.AmethystToolOwner"/>/<see cref="AppConfig.AmethystToolRepo"/>, verify it against
/// the digest GitHub published, screen it with <see cref="FixAnalyzer"/>, extract to a staging folder, then
/// back up and copy.
/// </para>
///
/// <para>
/// <b>Verification is fail-closed and that is the point.</b> Two of the four installed files are proxy
/// DLLs loaded by steam.exe, and the bytes can arrive from a third-party mirror. A release with no
/// published digest is therefore refused outright rather than installed unverified — there is nothing to
/// compare against, and "couldn't check" must never read as "checked and fine". Unlike
/// <see cref="SteamlessService"/> no pinned-hash fallback exists here, because this repository's releases
/// do carry digests; adding one would only create a path around the check.
/// </para>
///
/// <para>
/// Modeled on <see cref="PluginInstallerService"/>, including the Steam restart: the DLLs are locked while
/// Steam runs, so an install stops Steam first and relaunches it if it was up.
/// </para>
/// </summary>
public class AmethystToolService(SteamService steam, GithubProxy gh, DownloadNotice notice,
    InstallManifestService manifests, PluginRemovalService removal, UnlockerService unlocker)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Records the tag that was installed, so an update is detectable without re-hashing the
    /// Steam root. Kept beside the app's other state rather than in the Steam root, which stays limited to
    /// exactly the four payload files plus any backup folder.</summary>
    private static string StateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "amethysttool");
    private static string ManifestPath => Path.Combine(StateDir, "installed.json");

    private string? SteamDir => steam.EffectivePath;

    private GithubRelease? _cachedLatest;

    private sealed class Manifest
    {
        /// <summary>Release tag the payload in the Steam root came from — what makes an update detectable.</summary>
        public string? Tag { get; set; }
        /// <summary>SHA-256 of the archive that produced it, so "which build is on disk" is answerable
        /// from this file alone when a user reports a problem.</summary>
        public string? ZipSha { get; set; }
        /// <summary>Backup folder the last install created, if any — surfaced so the user can find the
        /// files that were displaced.</summary>
        public string? BackupDirectory { get; set; }
    }

    private static Manifest? ReadManifest()
    {
        try
        {
            return File.Exists(ManifestPath)
                ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath), JsonOpts)
                : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // absent or unreadable state is "unknown", never a failure to install
        }
    }

    private static void WriteManifest(Manifest m)
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(m));
    }

    /// <summary>Names present directly in the Steam root, for the pure policy to consult.</summary>
    private IReadOnlySet<string> SteamRootFiles()
    {
        if (SteamDir is not { } dir || !Directory.Exists(dir))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Network-free check: every payload file is present in the Steam root AND a Mode has not taken the
    /// proxy-DLL slot since.
    ///
    /// <para>
    /// <b>Why the slot is part of "installed".</b> Two of the four names are <c>dwmapi.dll</c> and
    /// <c>xinput1_4.dll</c>, which every Mode also writes. Installing BetterSteamTools over AmethystTool
    /// replaces exactly those two and leaves <c>AmethystTool.dll</c> and <c>amethysttool.toml</c> behind,
    /// so all four names are still present while none of the loaded bytes are AmethystTool's — and the
    /// card reported "up to date, v1.1.0" next to a BetterSteamTools card holding the ACTIVE badge. Name
    /// presence cannot tell those apart; the slot can, because only one backend ever holds it.
    /// </para>
    ///
    /// <para>
    /// <see cref="ActiveBackend.None"/> still counts as installed: nothing has claimed the slot, so the
    /// files in the root are the best evidence there is. Only a Mode actively owning it demotes this.
    /// </para>
    /// </summary>
    public bool IsInstalledLocally() =>
        AmethystToolPlan.IsInstalled(SteamRootFiles())
        && ActiveBackendPolicy.StillOwnsItsFiles(unlocker.ActiveBackend, ActiveBackend.AmethystTool);

    /// <summary>
    /// Whether AmethystTool currently owns the proxy-DLL slot — what the card's ACTIVE badge means.
    ///
    /// <para>
    /// Two conditions, and both are needed. The SELECTION is what makes it exclusive: it lives in the one
    /// string a Mode install also writes, so handing the slot to a Mode demotes this without anything
    /// having to notice. The FILES are what make it evidence rather than a claim: a payload deleted outside
    /// this app leaves a selection pointing at nothing, and a badge saying otherwise would be a guess.
    /// </para>
    /// </summary>
    public bool IsActive => unlocker.ActiveBackend == ActiveBackend.AmethystTool && IsInstalledLocally();

    /// <summary>Where the last install put the files it displaced, or null if it displaced none.</summary>
    public string? LastBackupDirectory => ReadManifest()?.BackupDirectory;

    /// <summary>
    /// Whether Uninstall has something to work from — an install record, or the local state file that
    /// <see cref="BackfillRecordIfMissing"/> can turn into one. Gating the button on the record ALONE
    /// would disable it for everyone who installed before records existed, even though their removal is
    /// perfectly provable from this service's own manifest.
    ///
    /// <para>
    /// File presence directly, NOT <see cref="IsInstalledLocally"/>: that one now answers "does the card
    /// say installed?", which a Mode holding the slot deliberately makes false. This asks the different
    /// question "is there anything left to remove?", and installing a Mode over AmethystTool leaves
    /// <c>AmethystTool.dll</c> and <c>amethysttool.toml</c> in the root — exactly when the button must
    /// still work. Sharing the slot-aware check would have stranded those files with no way to remove
    /// them in-app. Claiming nothing unsafe: <see cref="BackfillRecordIfMissing"/> records only payload
    /// files actually found, and removal still skips any name a live install claims.
    /// </para>
    /// </summary>
    public bool CanUninstall =>
        removal.HasRecordFor(PluginIds.AmethystTool)
        || (ReadManifest() is not null && AmethystToolPlan.IsInstalled(SteamRootFiles()));

    public async Task<GithubRelease?> FetchLatestAsync(bool force, CancellationToken ct = default)
    {
        if (!force && _cachedLatest is not null) return _cachedLatest;

        string url =
            $"https://api.github.com/repos/{AppConfig.AmethystToolOwner}/{AppConfig.AmethystToolRepo}/releases/latest";
        try
        {
            using var res = await gh.SendAsync(url, ct);
            // Covers rate limiting (403/429) as well as an unreachable host: both are "we don't know what
            // the latest release is", which the UI reports as offline rather than as a failed install.
            if (res is null || !res.IsSuccessStatusCode) return null;

            var rel = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (rel is not null) _cachedLatest = rel;
            return rel;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException)
        {
            return null;
        }
    }

    public async Task<AmethystToolStatus> GetStatusAsync(bool force = false, CancellationToken ct = default)
    {
        bool installed = IsInstalledLocally();
        var manifest = ReadManifest();
        var latest = await FetchLatestAsync(force, ct);

        if (latest is null)
            return new AmethystToolStatus(installed, manifest?.Tag, null, UpdateAvailable: false, Offline: true);

        // A present install whose recorded tag is unknown (installed by hand, or state lost) counts as
        // updatable: re-running the install is the cheap way back to a known-good state, and it is safe
        // because it backs up whatever it replaces.
        bool updateAvailable = installed && manifest?.Tag != latest.TagName;

        return new AmethystToolStatus(installed, manifest?.Tag, latest.TagName, updateAvailable, Offline: false);
    }

    /// <summary>
    /// Install or re-install AmethystTool into the Steam root. Idempotent: running it again on an
    /// up-to-date install replaces the same four files, preserving what was there first.
    /// </summary>
    public async Task<(bool ok, string? error)> InstallAsync(
        IProgress<double?>? progress, CancellationToken ct = default)
    {
        if (SteamDir is not { } steamDir) return (false, Resources.Strings.Plugin_Err_SteamNotFound);

        var latest = await FetchLatestAsync(force: true, ct);
        if (latest is null) return (false, Resources.Strings.Plugin_Err_GithubUnreachable);

        if (FindArchiveAsset(latest) is not { } asset)
            return (false, string.Format(
                Resources.Strings.Amethyst_Err_MissingAsset, AppConfig.AmethystToolAssetPrefix + "*.zip"));

        string tmp = Path.Combine(Path.GetTempPath(), "luatools-amethyst-" + Guid.NewGuid().ToString("N"));
        string staged = Path.Combine(tmp, "extracted");
        Directory.CreateDirectory(staged);
        try
        {
            // Pinned to the repository, not merely to a GitHub host: the URL below comes from release JSON
            // that an API mirror may have served, and what it points at is copied next to steam.exe.
            // See GithubProxy.IsAssetUrlForRepo.
            string zipPath = Path.Combine(tmp, asset.Name);
            await gh.DownloadAssetAsync(asset.DownloadUrl, AppConfig.AmethystToolOwner,
                AppConfig.AmethystToolRepo, zipPath, progress, ct);

            // Held open for the whole verify → screen → extract sequence, so the bytes that were checked
            // are the bytes that get extracted. See AssetIntegrity.OpenPinned.
            using var pinned = AssetIntegrity.OpenPinned(zipPath);

            // Fail-closed: an absent or malformed digest returns false here, so an unverifiable release
            // stops the install instead of proceeding unchecked. See AssetIntegrity.
            if (!AssetIntegrity.Matches(zipPath, asset.Digest))
                return (false, string.Format(Resources.Strings.Plugin_Err_VerifyFailed, asset.Name));
            string zipSha = AssetIntegrity.Sha256OfFile(zipPath);

            // Zip-slip, absolute paths, duplicate destinations and zip bombs — refused before extraction.
            // ApplicationCode profile: this archive is a compiled plugin, not a Steam manifest.
            if (FixAnalyzer.AnalyzeArchive(zipPath, staged, luaProfile: LuaScreeningProfile.ApplicationCode)
                is { Blocked: true } analysis)
                return (false, string.Format(Resources.Strings.Amethyst_Err_Archive, analysis.BlockReason));

            ZipFile.ExtractToDirectory(zipPath, staged);
            FlattenStagedLayout(staged);

            var plan = AmethystToolPlan.Create(
                steamDir,
                staged,
                Directory.EnumerateFiles(staged, "*", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName).OfType<string>(),
                SteamRootFiles(),
                DateTimeOffset.Now);

            if (plan.Rejection is { } rejection)
                return (false, string.Format(Resources.Strings.Amethyst_Err_Archive, rejection));

            // Disclose before anything leaves the temp folder. Cancelling here has cost nothing.
            if (!await notice.ReviewAsync(new DownloadReview(
                    AppConfig.AmethystToolOwner, AppConfig.AmethystToolRepo, latest.TagName,
                    asset.Name, zipSha,
                    FileCount: plan.Steps.Count,
                    ArchiveScreened: true), ct))
                return (false, Resources.Strings.Download_Notice_Cancelled);

            // The proxy DLLs are loaded by a running steam.exe and cannot be replaced under it.
            bool wasRunning = SteamService.IsSteamRunning();
            if (wasRunning)
            {
                steam.StopSteam();
                await Task.Delay(1200, ct); // let handles on the DLLs release after the shutdown
            }

            ApplyPlan(plan);

            // Deliberately AFTER ApplyPlan rather than in a finally: if a copy threw, the Steam root holds
            // a partial payload, and relaunching Steam onto a proxy DLL whose target is missing is worse
            // than leaving Steam closed with a failure reported. The backup folder still has the originals.
            if (wasRunning) steam.StartSteam();

            WriteManifest(new Manifest
            {
                Tag = latest.TagName,
                ZipSha = zipSha,
                BackupDirectory = plan.BackupDirectory,
            });

            // Record WHICH FILES were placed, not just which version. The private manifest above answers
            // "is there an update?"; only this one answers "what may be removed?", and uninstall works
            // from it rather than from the compiled-in name list — see PluginRemoval.
            //
            // RecordExclusive rather than Record: a Mode's entry can still list dwmapi.dll/xinput1_4.dll
            // from before this install overwrote them, and the bytes on disk are AmethystTool's now, so
            // that claim is stale. Recording and trimming in ONE write is what stops a failure between the
            // two halves from persisting a name both entries claim — which neither could then remove. A
            // name the Mode entry lists that the PAYLOAD never writes, e.g. OpenSteamTool.dll, is left
            // claimed exactly as it was; see InstallManifest.AbsorbFiles for why this trims instead of
            // folding. ApplyPlan has by now QUARANTINED that particular file into the backup folder, so
            // the Mode's claim on it names something no longer in the root — removal reports it Absent and
            // skips it, which is the honest outcome: it was moved aside, not deleted, and dropping the
            // claim would erase the only record that the Mode put it there.
            manifests.RecordExclusive(new InstalledPlugin(
                PluginIds.AmethystTool, latest.TagName, DateTimeOffset.Now,
                [.. plan.Steps.Select(step => new InstalledFile(
                    step.FileName, TryHash(step.DestinationPath)))]));

            // Take the proxy-DLL slot. The payload just overwrote dwmapi.dll and xinput1_4.dll, so whichever
            // Mode was active no longer owns what it placed — one assignment demotes it, which is what stops
            // the page showing two backends as ACTIVE at once. Deliberately AFTER the copy succeeded.
            unlocker.SelectAmethystTool();

            return (true, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, ex.Message); }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch (IOException) { /* temp cleanup */ }
        }
    }

    /// <summary>
    /// Carry out a plan: every displaced file is moved into the backup folder BEFORE its replacement is
    /// written, so an interrupted install never leaves a destination with neither the old nor a complete
    /// new file.
    /// </summary>
    /// <remarks>
    /// <c>amethysttool.toml</c> is part of the payload and is replaced like the rest. That does reset a
    /// hand-edited config, which is precisely why the backup is unconditional: the previous file is intact
    /// in the backup folder and the UI says where. Skipping it instead would leave a config from an old
    /// release driving a new DLL, which is the harder failure to diagnose.
    /// </remarks>
    internal static void ApplyPlan(AmethystInstallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Rejected) throw new InvalidOperationException("Refusing to apply a rejected plan.");

        if (plan.BackupDirectory is { } backupDir) Directory.CreateDirectory(backupDir);

        // Before the payload, not after: OpenSteamTool.dll is the displaced backend's engine, and the
        // point is that steam.exe never comes back up with it and AmethystTool.dll both in the root. A
        // move, so switching back finds it in the folder the card names.
        foreach (var quarantined in plan.Quarantine)
            if (File.Exists(quarantined.SourcePath))
                File.Move(quarantined.SourcePath, quarantined.BackupPath, overwrite: true);

        foreach (var step in plan.Steps)
        {
            if (step.BackupPath is { } backup && File.Exists(step.DestinationPath))
                File.Move(step.DestinationPath, backup, overwrite: true);

            File.Copy(step.SourcePath, step.DestinationPath, overwrite: true);
        }
    }

    /// <summary>
    /// The release archive puts its files at the root, but a zip built with a wrapping folder is a normal
    /// packaging accident. When extraction produced exactly one directory and no files, treat that
    /// directory's contents as the payload. Only one level is unwrapped, and only from a folder the
    /// already-screened archive produced.
    /// </summary>
    private static void FlattenStagedLayout(string staged)
    {
        var files = Directory.GetFiles(staged, "*", SearchOption.TopDirectoryOnly);
        var dirs = Directory.GetDirectories(staged);
        if (files.Length > 0 || dirs.Length != 1) return;

        foreach (string file in Directory.GetFiles(dirs[0], "*", SearchOption.TopDirectoryOnly))
            File.Move(file, Path.Combine(staged, Path.GetFileName(file)), overwrite: true);
    }

    /// <summary>
    /// The one asset an install may take: "<c>AmethystTool-*.zip</c>". Matching by prefix keeps a new
    /// release installable without an app rebuild while still refusing every other asset the release
    /// carries (the loose AmethystTool.dll, for instance, which is published for manual installs).
    /// </summary>
    private static GithubAsset? FindArchiveAsset(GithubRelease release) =>
        release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith(AppConfig.AmethystToolAssetPrefix, StringComparison.OrdinalIgnoreCase)
            && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Remove AmethystTool's files from the Steam root, working from the install record.
    ///
    /// <para>
    /// Delegates the whole thing to <see cref="PluginRemovalService"/> rather than deleting the four names
    /// this service knows about. Two of them — <c>dwmapi.dll</c> and <c>xinput1_4.dll</c> — are also placed
    /// by the Mode page's unlockers, so "remove what AmethystTool installs" and "remove what AmethystTool
    /// installed" are different sets, and only the second one is safe.
    /// </para>
    /// </summary>
    public async Task<PluginRemovalOutcome> UninstallAsync(CancellationToken ct = default)
    {
        BackfillRecordIfMissing();
        var outcome = await removal.RemoveAsync(PluginIds.AmethystTool, stopSteam: true, ct);

        // Drop the version record too, so the page stops reporting a tag for something no longer there.
        // Only on a real removal: a failure leaves the files in place, and the record must keep matching.
        if (!outcome.Failed && !outcome.NothingRecorded)
        {
            ClearManifest();

            // Release the slot, mirroring ModeRemovalService: leaving it held would show an ACTIVE badge
            // for an install that is gone. Guarded because a Mode may have taken the slot since — clearing
            // it then would deselect a Mode this removal never touched.
            if (unlocker.ActiveBackend == ActiveBackend.AmethystTool) unlocker.ClearSelectedMode();
        }

        return outcome;
    }

    /// <summary>
    /// Write an install record for a copy installed before records existed, so Uninstall works for it.
    ///
    /// <para>
    /// Gated on this service's OWN manifest being present, which is the evidence that matters: that file
    /// only exists because this app ran an install here. Without it the payload names alone prove nothing —
    /// <c>dwmapi.dll</c> and <c>xinput1_4.dll</c> belong to whoever put them there, and claiming them on a
    /// hunch is exactly how an uninstall breaks another tool. The shared-file check still applies on top,
    /// so a name an active Mode needs is kept even when it is back-filled here.
    /// </para>
    /// </summary>
    private void BackfillRecordIfMissing()
    {
        if (SteamDir is not { } steamDir) return;
        if (manifests.Load().Get(PluginIds.AmethystTool) is { Files.Count: > 0 }) return;
        if (ReadManifest() is not { } local) return; // no evidence this app installed it

        var present = SteamRootFiles();
        var files = AmethystToolPlan.PayloadFiles
            .Where(present.Contains)
            .Select(name => new InstalledFile(name, TryHash(Path.Combine(steamDir, name))))
            .ToList();

        // Record, NOT RecordExclusive — the one write in this app that must stay non-exclusive, so nobody
        // "uniformises" it later. Every other site records files it just wrote itself, which is what makes
        // its claim on them exclusive by construction. This one records files it merely FOUND, on evidence
        // (this service's own manifest) that says an install happened here, not that these exact bytes are
        // still AmethystTool's. A Mode installed over it afterwards owns dwmapi.dll/xinput1_4.dll, and
        // absorbing would strip the entry that can actually verify them.
        if (files.Count > 0)
            manifests.Record(new InstalledPlugin(
                PluginIds.AmethystTool, local.Tag, DateTimeOffset.Now, files));
    }

    private static void ClearManifest()
    {
        try { if (File.Exists(ManifestPath)) File.Delete(ManifestPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>SHA-256 of a just-placed file, or null if it cannot be read. Recorded for diagnosis only,
    /// so an unreadable file costs a field rather than the whole install record.</summary>
    private static string? TryHash(string path)
    {
        try { return AssetIntegrity.Sha256OfFile(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
