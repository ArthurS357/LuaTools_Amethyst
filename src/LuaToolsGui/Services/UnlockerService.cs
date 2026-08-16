using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>
/// Manages the mutually-exclusive Steam fixes (SteamTools / OpenSteamTools / CloudRedirect). Only one
/// is active at a time. Each fetches its own GitHub release, verifies files by sha256, and installs
/// into the Steam root (CloudRedirect runs a CLI that patches + deploys itself). Switching overwrites
/// shared files but doesn't delete the previous mode's leftovers. The active mode persists in settings.
/// </summary>
public class UnlockerService(SteamService steam, SettingsService settings, CacheService cache, GithubProxy gh)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Per-mode cache of the GitHub release so re-opening the page doesn't hammer the API
    // (unauthenticated GitHub allows only 60 req/hr per IP). The "Check for updates" button forces a
    // fresh fetch (30s cooldown) for anyone who wants certainty sooner.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly Dictionary<UnlockerMode, (GithubRelease release, DateTime fetchedAt)> _releaseCache = new();

    public IReadOnlyList<ModeDefinition> Modes { get; } =
    [
        // SteamTools now publishes each DLL on its OWN dynamically-tagged release (st-<timestamp>) —
        // one tag per release, DLLs not released together — so there's no fixed tag. Status/install
        // fetch ALL releases (one call, per_page=100) and resolve each DLL from its own latest st* release.
        new(UnlockerMode.SteamTools, "SteamTools",
            Description: Resources.Strings.Mode_Desc_SteamTools,
            Kind: ModeKind.Loose,
            Owner: "mendy-tools", Repo: "verynotsusdllsthataredefnotstrelated",
            FixedTag: null,
            PlaceFiles: ["dwmapi.dll", "xinput1_4.dll"],
            ZipAssetPattern: null,
            CliAssetName: null, CliArgs: null, VerifyFile: null),

        // DisplayName "BetterSteamTools" is the user-facing brand; the repo/dll/zip identifiers below stay
        // the upstream "OpenSteamTool" names (real download targets — renaming them breaks install).
        new(UnlockerMode.OpenSteamTools, "BetterSteamTools",
            Description: Resources.Strings.Mode_Desc_OpenSteamTools,
            Kind: ModeKind.Zip,
            Owner: "OpenSteam001", Repo: "OpenSteamTool",
            FixedTag: null,
            PlaceFiles: ["dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"],
            ZipAssetPattern: "OpenSteamTool-{version}-Release.zip",
            CliAssetName: null, CliArgs: null, VerifyFile: null),

        // Nightly build of OpenSteamTool (our own madoiscool/OST-Nightly, built from upstream main) —
        // kept as its own mode because it adds native CloudRedirect support (see the add-on below).
        // Same install/config shape as stable BST (same binary, same opensteamtool.toml).
        new(UnlockerMode.OpenSteamToolsNightly, "BetterSteamTools Nightly",
            Description: Resources.Strings.Mode_Desc_OpenSteamToolsNightly,
            Kind: ModeKind.Zip,
            Owner: "madoiscool", Repo: "OST-Nightly",
            FixedTag: null,
            PlaceFiles: ["dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"],
            ZipAssetPattern: "OpenSteamTool-{version}-Release.zip",
            CliAssetName: null, CliArgs: null, VerifyFile: null),

        new(UnlockerMode.CloudRedirect, "CloudRedirect (SteamTools Fix)",
            Description: Resources.Strings.Mode_Desc_CloudRedirect,
            Kind: ModeKind.Cli,
            Owner: "Selectively11", Repo: "CloudRedirect",
            FixedTag: null,
            PlaceFiles: ["cloud_redirect.dll"],
            ZipAssetPattern: null,
            CliAssetName: "CloudRedirectCLI.exe", CliArgs: "/stfixer", VerifyFile: "cloud_redirect.dll"),
    ];

    private ModeDefinition Def(UnlockerMode mode) => Modes.First(m => m.Mode == mode);

    /// <summary>The currently-active mode (the last one installed/selected), or null if none yet.</summary>
    public UnlockerMode? SelectedMode =>
        Enum.TryParse(settings.SelectedMode, out UnlockerMode m) ? m : null;

    /// <summary>Short display name of the active mode for status UI; null if none selected/detected yet.</summary>
    public string? SelectedModeDisplayName =>
        SelectedMode is { } m ? Def(m).DisplayName : null;

    // ── State query ─────────────────────────────────────────────────

    /// <summary>Query GitHub + local files → this mode's status. Returns Unknown on any failure/offline.
    /// Cached briefly unless <paramref name="forceRefresh"/>.</summary>
    public async Task<ModeState> GetStateAsync(UnlockerMode mode, bool forceRefresh = false, CancellationToken ct = default)
    {
        var def = Def(mode);
        bool active = SelectedMode == mode;

        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return new ModeState(mode, ModeStatus.Unknown, active, null);

        // OpenSteamTools status uses our mendy-tools "ost-" mirror (real per-DLL hashes), since the
        // upstream OST release only publishes a zip digest, not per-file hashes.
        if (mode == UnlockerMode.OpenSteamTools)
        {
            var (ostStatus, latestTag) = await OstMirrorStatusAsync(root, ct);
            return new ModeState(mode, ostStatus, active, latestTag);
        }

        // SteamTools: each DLL has its own per-timestamp "st-…" release, so resolve the latest of each
        // across ALL releases (not a single fixed tag).
        if (mode == UnlockerMode.SteamTools)
        {
            var releases = await FetchAllReleasesAsync(def.Owner, def.Repo, null, ct);
            if (releases is null) return new ModeState(mode, ModeStatus.Unknown, active, null);
            var (stStatus, latestTag) = SteamToolsStatus(def, releases, root);
            return new ModeState(mode, stStatus, active, latestTag);
        }

        // CloudRedirect (CLI) verifies its placed file by digest off a single release.
        GithubRelease? release = await FetchReleaseAsync(def, forceRefresh, ct);
        if (release is null)
            return new ModeState(mode, ModeStatus.Unknown, active, null);
        return new ModeState(mode, LooseModeStatus(def, release, root), active, release.TagName);
    }

    private static ModeStatus LooseModeStatus(ModeDefinition def, GithubRelease release, string root)
    {
        bool anyPresent = false, anyMissingOrStale = false;
        foreach (string file in def.PlaceFiles)
        {
            string local = Path.Combine(root, file);
            var asset = release.Assets.FirstOrDefault(a => a.Name.Equals(file, StringComparison.OrdinalIgnoreCase));
            string? wanted = AssetIntegrity.ParseDigest(asset?.Digest);

            if (!File.Exists(local)) { anyMissingOrStale = true; continue; }
            anyPresent = true;
            if (wanted is null || !AssetIntegrity.Sha256OfFile(local).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                anyMissingOrStale = true;
        }

        if (!anyPresent) return ModeStatus.NotInstalled;
        return anyMissingOrStale ? ModeStatus.UpdateAvailable : ModeStatus.UpToDate;
    }

    /// <summary>
    /// SteamTools status across per-timestamp releases: for each required DLL, compare the on-disk file
    /// to the digest of the NEWEST st* release that carries it. Up to date only if every present DLL
    /// matches its own latest. Returns the newest st* tag overall for display.
    /// </summary>
    private static (ModeStatus status, string? latestTag) SteamToolsStatus(
        ModeDefinition def, IReadOnlyList<GithubRelease> releases, string root)
    {
        bool anyPresent = false, anyMissingOrStale = false;
        foreach (string file in def.PlaceFiles)
        {
            string local = Path.Combine(root, file);
            string? wanted = AssetIntegrity.ParseDigest(LatestAssetFor(releases, file)?.Digest);

            if (!File.Exists(local)) { anyMissingOrStale = true; continue; }
            anyPresent = true;
            if (wanted is null || !AssetIntegrity.Sha256OfFile(local).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                anyMissingOrStale = true;
        }

        string? latestTag = releases
            .Where(r => r.TagName.StartsWith("st", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault()?.TagName;

        if (!anyPresent) return (ModeStatus.NotInstalled, latestTag);
        return (anyMissingOrStale ? ModeStatus.UpdateAvailable : ModeStatus.UpToDate, latestTag);
    }

    private const string MirrorRepoOwner = "mendy-tools";
    private const string MirrorRepo = "verynotsusdllsthataredefnotstrelated";

    /// <summary>
    /// OpenSteamTools status via the mendy-tools "ost-" mirror (real per-DLL hashes). Hash the on-disk
    /// dwmapi.dll against the mirror: matches the LATEST ost- release (by published_at) → UpToDate;
    /// matches an older ost- release (or files present but no match) → UpdateAvailable; absent → NotInstalled.
    /// Returns the latest ost- tag for display.
    /// </summary>
    private async Task<(ModeStatus status, string? latestTag)> OstMirrorStatusAsync(string root, CancellationToken ct)
    {
        string dwmapi = Path.Combine(root, "dwmapi.dll");
        if (!File.Exists(dwmapi)) return (ModeStatus.NotInstalled, null);

        var releases = await FetchAllReleasesAsync(MirrorRepoOwner, MirrorRepo, null, ct);
        if (releases is null) return (ModeStatus.Unknown, null);

        var ost = releases.Where(r => r.TagName.StartsWith("ost-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue).ToList();
        if (ost.Count == 0) return (ModeStatus.Unknown, null);

        var latest = ost[0];
        string dwmHash = AssetIntegrity.Sha256OfFile(dwmapi);

        if (AssetDigest(latest, "dwmapi.dll") == dwmHash) return (ModeStatus.UpToDate, latest.TagName);
        // Matches an older ost- release, or is present but unrecognized → an update exists.
        return (ModeStatus.UpdateAvailable, latest.TagName);
    }

    // ── Install / switch ─────────────────────────────────────────────

    /// <summary>Download + verify a mode's files, place them in the Steam root, remove the other mode's
    /// unique files, and persist the selection. Best-effort per file (locked files land in Failed).</summary>
    public async Task<ModeInstallResult> InstallAsync(
        UnlockerMode mode, IProgress<double?>? progress = null, CancellationToken ct = default)
    {
        var def = Def(mode);
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return ModeInstallResult.Fail("Steam location not found — set it in Settings.");

        // SteamTools: each DLL has its own per-timestamp "st-…" release → resolve each from its own
        // latest. Other modes use a single release.
        string? steamToolsTag = null;
        Func<string, GithubAsset?>? resolveSteamToolsAsset = null;
        if (mode == UnlockerMode.SteamTools)
        {
            var releases = await FetchAllReleasesAsync(def.Owner, def.Repo, null, ct);
            if (releases is null)
                return ModeInstallResult.Fail("Couldn't reach GitHub — check your connection and try again.");
            resolveSteamToolsAsset = file => LatestAssetFor(releases, file);
            steamToolsTag = releases
                .Where(r => r.TagName.StartsWith("st", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
                .FirstOrDefault()?.TagName;
        }

        GithubRelease? release = null;
        if (mode != UnlockerMode.SteamTools)
        {
            // Use the same (cached) release the card's status was based on, so install matches what was shown.
            release = await FetchReleaseAsync(def, forceRefresh: false, ct);
            if (release is null)
                return ModeInstallResult.Fail("Couldn't reach GitHub — check your connection and try again.");
        }

        // CLI modes (CloudRedirect) have a completely different flow: download a tool, run it, verify.
        if (def.Kind == ModeKind.Cli)
            return await InstallViaCliAsync(def, release!, root, progress, ct);

        string staging = Path.Combine(Path.GetTempPath(), "LuaToolsGui", "mode", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);

            // 1. Stage + verify into temp.
            Dictionary<string, string> staged; // filename → staged path
            string? zipDigest = null;
            if (def.Kind == ModeKind.Zip)
            {
                var asset = FindZipAsset(def, release!);
                if (asset is null) return ModeInstallResult.Fail("Release is missing the expected download.");

                string zipPath = Path.Combine(staging, asset.Name);
                await DownloadToFileAsync(asset.DownloadUrl, zipPath, progress, ct);

                zipDigest = AssetIntegrity.Sha256OfFile(zipPath);
                if (!AssetIntegrity.Matches(zipPath, asset.Digest))
                    return ModeInstallResult.Fail("Download failed verification (sha256 mismatch or no published digest).");

                staged = ExtractWanted(zipPath, def.PlaceFiles, staging);
                var missing = def.PlaceFiles.Where(f => !staged.ContainsKey(f)).ToList();
                if (missing.Count > 0)
                    return ModeInstallResult.Fail($"Download is missing: {string.Join(", ", missing)}.");
            }
            else
            {
                staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in def.PlaceFiles)
                {
                    // SteamTools resolves each DLL from its own latest st* release; others from the one release.
                    var asset = resolveSteamToolsAsset is not null
                        ? resolveSteamToolsAsset(file)
                        : release!.Assets.FirstOrDefault(a => a.Name.Equals(file, StringComparison.OrdinalIgnoreCase));
                    if (asset is null) return ModeInstallResult.Fail($"Couldn't find {file} in any release.");

                    string dest = Path.Combine(staging, file);
                    await DownloadToFileAsync(asset.DownloadUrl, dest, progress, ct);

                    if (!AssetIntegrity.Matches(dest, asset.Digest))
                        return ModeInstallResult.Fail($"{file} failed verification (sha256 mismatch or no published digest).");
                    staged[file] = dest;
                }
            }

            // 2. Copy verified files into the Steam root (overwrite). Two very different failures land here
            //    and used to be reported identically: the file is LOCKED (Steam running) or we are DENIED
            //    (Steam installed under Program Files and the app runs asInvoker). Telling a user to "close
            //    Steam" when the real problem is permissions sends them round a loop that cannot succeed,
            //    so track them separately.
            var failed = new List<string>();
            bool deniedByPermissions = false;
            foreach (string file in def.PlaceFiles)
            {
                try
                {
                    string dest = Path.Combine(root, file);
                    File.Copy(staged[file], dest, overwrite: true);
                    StampNow(dest);
                }
                catch (UnauthorizedAccessException)
                {
                    deniedByPermissions = true;
                    failed.Add(file);
                }
                catch (IOException)
                {
                    failed.Add(file); // in use — closing Steam genuinely does fix this one
                }
            }

            // 3. This mode is now the active one. (No cleanup of other modes' files — just overwrite.)
            settings.SelectedMode = mode.ToString();

            // Record the installed OST zip digest/version (kept for reference; the up-to-date check now
            // uses the mendy-tools "ost-" mirror's per-DLL hashes). Point its config at stplug-in too.
            if (def.Kind == ModeKind.Zip)
            {
                cache.OpenSteamToolsInstalledZipDigest = zipDigest;
                cache.OpenSteamToolsInstalledVersion = release!.TagName; // Zip ⇒ OST ⇒ release fetched above
            }
            if (mode is UnlockerMode.OpenSteamTools or UnlockerMode.OpenSteamToolsNightly)
                try { EnsureOpenSteamToolLuaPath(root); } catch { /* config tweak is best-effort */ }

            if (failed.Count == 0) return ModeInstallResult.Ok();

            // Permission denial wins the message when both happened: it's the one the user cannot resolve
            // by guessing. The app deliberately does not self-elevate (see app.manifest), so the action is
            // theirs to take.
            string reason = deniedByPermissions
                ? $"Couldn't write {failed.Count} file(s) to the Steam folder — access denied. Steam looks " +
                  "to be installed somewhere that needs administrator rights (e.g. Program Files). Close " +
                  "Steam, then run LuaTools as administrator once to apply this mode."
                : $"Couldn't write {failed.Count} file(s) — close Steam and try again.";

            return new ModeInstallResult(false, reason, failed);
        }
        catch (OperationCanceledException)
        {
            return ModeInstallResult.Fail("Cancelled.");
        }
        catch (Exception ex)
        {
            return ModeInstallResult.Fail(ex.Message);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// CLI mode (CloudRedirect): download the latest fixer tool, run it (it closes Steam, patches, and
    /// deploys cloud_redirect.dll itself), then confirm the deployed dll matches the latest release hash.
    /// </summary>
    private async Task<ModeInstallResult> InstallViaCliAsync(
        ModeDefinition def, GithubRelease release, string root, IProgress<double?>? progress, CancellationToken ct)
    {
        var cliAsset = release.Assets.FirstOrDefault(a => a.Name.Equals(def.CliAssetName, StringComparison.OrdinalIgnoreCase));
        if (cliAsset is null) return ModeInstallResult.Fail($"Release is missing {def.CliAssetName}.");

        var verifyAsset = release.Assets.FirstOrDefault(a => a.Name.Equals(def.VerifyFile, StringComparison.OrdinalIgnoreCase));
        // No published digest for the file the tool deploys ⇒ we could not confirm what it wrote. Refuse
        // up front rather than run the tool and then "verify" nothing.
        if (AssetIntegrity.ParseDigest(verifyAsset?.Digest) is null)
            return ModeInstallResult.Fail($"{def.VerifyFile} has no published digest to verify against.");

        string staging = Path.Combine(Path.GetTempPath(), "LuaToolsGui", "mode", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);

            // 1. Download + verify the CLI tool.
            string cliPath = Path.Combine(staging, def.CliAssetName!);
            await DownloadToFileAsync(cliAsset.DownloadUrl, cliPath, progress, ct);
            // This binary is EXECUTED below, and may have come from a third-party mirror — a missing digest
            // must stop the install, not wave it through.
            if (!AssetIntegrity.Matches(cliPath, cliAsset.Digest))
                return ModeInstallResult.Fail($"{def.CliAssetName} failed verification (sha256 mismatch or no published digest).");

            // 2. Run it. It closes Steam, patches SteamTools, and deploys the dll on its own.
            progress?.Report(null); // indeterminate — no progress signal from the external tool
            int exit = await RunProcessAsync(cliPath, def.CliArgs ?? "", ct);
            if (exit != 0)
                return ModeInstallResult.Fail($"{def.CliAssetName} exited with code {exit}.");

            // 3. Confirm the deployed file is the expected (latest) version.
            string deployed = Path.Combine(root, def.VerifyFile!);
            if (!File.Exists(deployed))
                return ModeInstallResult.Fail($"{def.VerifyFile} was not deployed — the fix didn't complete.");
            if (!AssetIntegrity.Matches(deployed, verifyAsset?.Digest))
                return ModeInstallResult.Fail($"{def.VerifyFile} is not the expected version — the update didn't apply.");

            settings.SelectedMode = def.Mode.ToString(); // CloudRedirect is now the active mode
            return ModeInstallResult.Ok();
        }
        catch (OperationCanceledException) { return ModeInstallResult.Fail("Cancelled."); }
        catch (Exception ex) { return ModeInstallResult.Fail(ex.Message); }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<int> RunProcessAsync(string exePath, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exePath, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the fixer.");
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    // ── First-run auto-detect ────────────────────────────────────────

    /// <summary>
    /// One-time detection of an already-installed mode when none is selected yet. Hashes the on-disk
    /// DLLs and matches them against published release asset digests:
    ///   1. OpenSteamTools — dwmapi.dll AND xinput1_4.dll vs mendy-tools tag "ost-" (loose-DLL mirror;
    ///      OST ships a zip whose API digest isn't per-DLL, so we mirror the DLLs for hash-matching).
    ///   2. SteamTools — same two DLLs vs mendy-tools tag "st" (ST/st-*).
    ///   3. CloudRedirect — cloud_redirect.dll vs Selectively11/CloudRedirect (only if 1+2 miss).
    /// Both DLLs must be present and each must hash-match SOME release with the right tag prefix — the
    /// two DLLs ship in SEPARATE releases (they aren't published together), so each is matched
    /// independently across all releases, not against one single release.
    /// Persists the match as the active mode. Returns the detected mode, or null if nothing matched.
    /// 1 guaranteed API call (one repo serves both ost-/st), plus 1 conditional (CloudRedirect).
    /// </summary>
    public async Task<UnlockerMode?> DetectActiveModeAsync(CancellationToken ct = default)
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid) return null;

        string dwmapi = Path.Combine(root, "dwmapi.dll");
        string xinput = Path.Combine(root, "xinput1_4.dll");

        // Both DLLs must exist and each must hash-match an asset in SOME release whose tag starts with the
        // given prefix. The two DLLs are released separately, so we match each one across all releases.
        bool BothDllsMatchPrefix(IReadOnlyList<GithubRelease> releases, string tagPrefix)
        {
            if (!File.Exists(dwmapi) || !File.Exists(xinput)) return false;

            var tagged = releases
                .Where(r => r.TagName.StartsWith(tagPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (tagged.Count == 0) return false;

            string dwmHash = AssetIntegrity.Sha256OfFile(dwmapi);
            string xinHash = AssetIntegrity.Sha256OfFile(xinput);
            bool dwmOk = tagged.Any(r => AssetDigest(r, "dwmapi.dll") == dwmHash);
            bool xinOk = tagged.Any(r => AssetDigest(r, "xinput1_4.dll") == xinHash);
            return dwmOk && xinOk;
        }

        UnlockerMode? detected = null;

        // Nightly BST first: the loader DLLs (dwmapi/xinput) can be byte-identical to stable BST, so
        // match on the payload OpenSteamTool.dll (which differs per nightly build) against our OST-Nightly
        // releases. Checked before the stable ost-/st match so a real nightly install wins.
        string ostDll = Path.Combine(root, "OpenSteamTool.dll");
        if (File.Exists(ostDll))
        {
            var nightly = await FetchAllReleasesAsync("madoiscool", "OST-Nightly", null, ct);
            if (nightly is not null)
            {
                string ostHash = AssetIntegrity.Sha256OfFile(ostDll);
                if (nightly.Any(r => AssetDigest(r, "OpenSteamTool.dll") == ostHash))
                    detected = UnlockerMode.OpenSteamToolsNightly;
            }
        }

        const string repo = "verynotsusdllsthataredefnotstrelated";

        // Single fetch — the same mendy-tools repo serves both the "ost-" mirror and the "st" releases.
        if (detected is null)
        {
            var stRepoReleases = await FetchAllReleasesAsync("mendy-tools", repo, null, ct);
            if (stRepoReleases is not null)
            {
                if (BothDllsMatchPrefix(stRepoReleases, "ost-"))
                    detected = UnlockerMode.OpenSteamTools;
                else if (BothDllsMatchPrefix(stRepoReleases, "st"))  // matches "ST" and "st-*"
                    detected = UnlockerMode.SteamTools;
            }
        }

        if (detected is null)
        {
            string crDll = Path.Combine(root, "cloud_redirect.dll");
            if (File.Exists(crDll))
            {
                var releases = await FetchAllReleasesAsync("Selectively11", "CloudRedirect", null, ct);
                string crHash = AssetIntegrity.Sha256OfFile(crDll);
                if (releases is not null && releases.Any(r => AssetDigest(r, "cloud_redirect.dll") == crHash))
                    detected = UnlockerMode.CloudRedirect;
            }
        }

        if (detected is { } m) settings.SelectedMode = m.ToString();
        return detected;
    }

    /// <summary>Digest (hex, no prefix) of a release's same-named asset, or null if absent.</summary>
    private static string? AssetDigest(GithubRelease r, string assetName) =>
        AssetIntegrity.ParseDigest(r.Assets.FirstOrDefault(a => a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase))?.Digest);

    /// <summary>The same-named asset, or null if this release doesn't have it.</summary>
    private static GithubAsset? FindAsset(GithubRelease r, string assetName) =>
        r.Assets.FirstOrDefault(a => a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// SteamTools ships each DLL on its own per-timestamp release (tag "st-…"), so each DLL has its
    /// own independent latest. Given the pre-fetched releases, return the asset for <paramref name="file"/>
    /// from the NEWEST st* release that contains it (by published_at). Null if none carry that file.
    /// </summary>
    private static GithubAsset? LatestAssetFor(IReadOnlyList<GithubRelease> releases, string file) =>
        releases
            .Where(r => r.TagName.StartsWith("st", StringComparison.OrdinalIgnoreCase)) // "ST" and "st-*"
            .OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .Select(r => FindAsset(r, file))
            .FirstOrDefault(a => a is not null);

    /// <summary>Fetch every release for a repo (per_page=100). If <paramref name="tag"/> is set, only
    /// that one release (wrapped in a list). Null on failure/offline.</summary>
    private async Task<List<GithubRelease>?> FetchAllReleasesAsync(string owner, string repo, string? tag, CancellationToken ct)
    {
        string url = tag is not null
            ? $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}"
            : $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=100";
        try
        {
            // Routed via GithubProxy: direct, then mirrors (for blocked/throttled regions).
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;
            string body = await res.Content.ReadAsStringAsync(ct);
            if (tag is not null)
            {
                var one = JsonSerializer.Deserialize<GithubRelease>(body, JsonOpts);
                return one is null ? null : [one];
            }
            return JsonSerializer.Deserialize<List<GithubRelease>>(body, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    // ── OpenSteamTool config ─────────────────────────────────────────

    private const string OstLuaPath = "config/stplug-in";

    /// <summary>
    /// Ensure &lt;Steam&gt;/opensteamtool.toml's [lua] paths array contains "config/stplug-in" so our luas
    /// are loaded. Creates the file/section/array if missing; appends without removing existing paths.
    /// Targeted text edit (preserves comments and other sections). Commented-out lines are ignored.
    /// </summary>
    private static void EnsureOpenSteamToolLuaPath(string steamRoot)
    {
        string tomlPath = Path.Combine(steamRoot, "opensteamtool.toml");

        // No file → create a minimal one.
        if (!File.Exists(tomlPath))
        {
            File.WriteAllText(tomlPath, $"[lua]\npaths = [\"{OstLuaPath}\"]\n");
            return;
        }

        var lines = File.ReadAllLines(tomlPath).ToList();

        // Find the active (uncommented) [lua] section header and the bounds of that section.
        int luaHeader = lines.FindIndex(l => IsActiveTableHeader(l, "lua"));
        if (luaHeader < 0)
        {
            // No active [lua] section → append one.
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("[lua]");
            lines.Add($"paths = [\"{OstLuaPath}\"]");
            File.WriteAllLines(tomlPath, lines);
            return;
        }

        // Section runs until the next active table header (or EOF).
        int sectionEnd = lines.FindIndex(luaHeader + 1, IsActiveAnyTableHeader);
        if (sectionEnd < 0) sectionEnd = lines.Count;

        // Look for an active `paths` key within the section. The array may span multiple lines.
        int pathsStart = -1;
        for (int i = luaHeader + 1; i < sectionEnd; i++)
        {
            string t = lines[i].TrimStart();
            if (t.StartsWith('#')) continue;                       // commented → ignore
            if (Regex.IsMatch(t, @"^paths\s*=")) { pathsStart = i; break; }
        }

        if (pathsStart < 0)
        {
            // [lua] exists but no active paths key → insert one right under the header.
            lines.Insert(luaHeader + 1, $"paths = [\"{OstLuaPath}\"]");
            File.WriteAllLines(tomlPath, lines);
            return;
        }

        // Find where the array closes (']'), scanning from pathsStart (handles multi-line arrays).
        int pathsEnd = pathsStart;
        while (pathsEnd < sectionEnd && !lines[pathsEnd].Contains(']')) pathsEnd++;
        if (pathsEnd >= sectionEnd) pathsEnd = sectionEnd - 1; // malformed/unclosed — best effort

        string block = string.Join("\n", lines.GetRange(pathsStart, pathsEnd - pathsStart + 1));

        // Already present (compare the path token, slashes normalized)? Nothing to do.
        if (Regex.IsMatch(block, @"[""']\s*" + Regex.Escape(OstLuaPath).Replace("/", @"[/\\]+") + @"\s*[""']",
                RegexOptions.IgnoreCase))
            return;

        // Insert our entry just before the closing ']' on the line that has it.
        int closeLine = pathsEnd;
        string line = lines[closeLine];
        int bracket = line.LastIndexOf(']');

        // Insert our entry just before the ']'. Add a comma after existing content unless the array
        // is empty (text before ']' ends right after the opening '[').
        string before = line[..bracket].TrimEnd();
        bool arrayEmpty = Regex.IsMatch(before, @"\[\s*$");
        string newBefore = arrayEmpty
            ? before + $" \"{OstLuaPath}\""
            : before + $", \"{OstLuaPath}\"";
        lines[closeLine] = newBefore + line[bracket..];

        File.WriteAllLines(tomlPath, lines);
    }

    /// <summary>True if the line is an active (uncommented) [name] table header.</summary>
    private static bool IsActiveTableHeader(string line, string name)
    {
        string t = line.TrimStart();
        return !t.StartsWith('#') && Regex.IsMatch(t, $@"^\[\s*{Regex.Escape(name)}\s*\]");
    }

    /// <summary>True if the line is any active (uncommented) [..] table header.</summary>
    private static bool IsActiveAnyTableHeader(string line)
    {
        string t = line.TrimStart();
        return !t.StartsWith('#') && Regex.IsMatch(t, @"^\[[^\[].*\]");
    }

    // ── CloudRedirect add-on (a feature of the OpenSteamTool Nightly build) ──────────
    // Not a mutually-exclusive mode: it drops cloud_redirect.dll into the Steam root and toggles
    // [cloud] enabled in opensteamtool.toml (parallel to how BST install writes [lua] paths). Only
    // meaningful when the Nightly BST mode is active.

    private const string CloudRedirectDll = "cloud_redirect.dll";
    private (GithubRelease release, DateTime fetchedAt)? _crReleaseCache;

    private async Task<GithubRelease?> FetchCloudRedirectReleaseAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && _crReleaseCache is { } c && DateTime.UtcNow - c.fetchedAt < CacheTtl)
            return c.release;

        string url = $"https://api.github.com/repos/{AppConfig.CloudRedirectRepo}/releases/latest";
        try
        {
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;
            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (release is not null) _crReleaseCache = (release, DateTime.UtcNow);
            return release;
        }
        catch { return null; }
    }

    /// <summary>Add-on state from disk (dll present + [cloud] enabled) plus, when <paramref name="checkUpdate"/>
    /// and installed, whether a newer cloud_redirect.dll is published.</summary>
    public async Task<CloudRedirectAddonState> GetCloudRedirectStateAsync(
        bool checkUpdate, bool forceRefresh = false, CancellationToken ct = default)
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return new CloudRedirectAddonState(false, false, false, null);

        string dll = Path.Combine(root, CloudRedirectDll);
        bool installed = File.Exists(dll);
        bool enabled = ReadOpenSteamToolCloudEnabled(root);

        bool updateAvailable = false;
        string? latest = null;
        if (checkUpdate && installed)
        {
            var release = await FetchCloudRedirectReleaseAsync(forceRefresh, ct);
            if (release is not null)
            {
                latest = release.TagName;
                string? wanted = AssetDigest(release, CloudRedirectDll);
                if (wanted is not null && !AssetIntegrity.Sha256OfFile(dll).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    updateAvailable = true;
            }
        }
        return new CloudRedirectAddonState(installed, enabled, updateAvailable, latest);
    }

    /// <summary>Enable: download cloud_redirect.dll if missing (verified), then set [cloud] enabled = true.
    /// Takes effect on the next Steam launch.</summary>
    public async Task<ModeInstallResult> EnableCloudRedirectAsync(IProgress<double?>? progress = null, CancellationToken ct = default)
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return ModeInstallResult.Fail("Steam location not found — set it in Settings.");

        if (!File.Exists(Path.Combine(root, CloudRedirectDll)))
        {
            var dl = await DownloadCloudRedirectDllAsync(root, progress, ct);
            if (!dl.Success) return dl;
        }
        try { SetOpenSteamToolCloudEnabled(root, true); }
        catch (Exception ex) { return ModeInstallResult.Fail(ex.Message); }
        return ModeInstallResult.Ok();
    }

    /// <summary>Disable: flip [cloud] enabled = false (keeps the dll on disk).</summary>
    public ModeInstallResult DisableCloudRedirect()
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return ModeInstallResult.Fail("Steam location not found — set it in Settings.");
        try { SetOpenSteamToolCloudEnabled(root, false); return ModeInstallResult.Ok(); }
        catch (Exception ex) { return ModeInstallResult.Fail(ex.Message); }
    }

    /// <summary>Update: replace cloud_redirect.dll with the latest (verified). Fails with a "close Steam"
    /// message if Steam holds the existing dll open.</summary>
    public async Task<ModeInstallResult> UpdateCloudRedirectAsync(IProgress<double?>? progress = null, CancellationToken ct = default)
    {
        string? root = steam.EffectivePath;
        if (root is null || !steam.IsValid)
            return ModeInstallResult.Fail("Steam location not found — set it in Settings.");
        return await DownloadCloudRedirectDllAsync(root, progress, ct);
    }

    private async Task<ModeInstallResult> DownloadCloudRedirectDllAsync(string root, IProgress<double?>? progress, CancellationToken ct)
    {
        var release = await FetchCloudRedirectReleaseAsync(forceRefresh: true, ct);
        if (release is null) return ModeInstallResult.Fail("Couldn't reach GitHub — check your connection and try again.");
        var asset = FindAsset(release, CloudRedirectDll);
        if (asset is null) return ModeInstallResult.Fail($"Release is missing {CloudRedirectDll}.");

        string staging = Path.Combine(Path.GetTempPath(), "LuaToolsGui", "cloud", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            string tmp = Path.Combine(staging, CloudRedirectDll);
            await DownloadToFileAsync(asset.DownloadUrl, tmp, progress, ct);
            if (!AssetIntegrity.Matches(tmp, asset.Digest))
                return ModeInstallResult.Fail($"{CloudRedirectDll} failed verification (sha256 mismatch or no published digest).");

            try
            {
                string dest = Path.Combine(root, CloudRedirectDll);
                File.Copy(tmp, dest, overwrite: true);
                StampNow(dest);
            }
            catch
            {
                // Steam has the loaded dll locked — surface a close-Steam message (same as mode install).
                return ModeInstallResult.Fail("Couldn't write cloud_redirect.dll — close Steam and try again.");
            }
            return ModeInstallResult.Ok();
        }
        catch (OperationCanceledException) { return ModeInstallResult.Fail("Cancelled."); }
        catch (Exception ex) { return ModeInstallResult.Fail(ex.Message); }
        finally { try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ } }
    }

    /// <summary>Ensure opensteamtool.toml has an active [cloud] section with enabled = true|false. Mirrors
    /// EnsureOpenSteamToolLuaPath's targeted, comment-preserving editing.</summary>
    private static void SetOpenSteamToolCloudEnabled(string steamRoot, bool enabled)
    {
        string tomlPath = Path.Combine(steamRoot, "opensteamtool.toml");
        string val = enabled ? "true" : "false";

        if (!File.Exists(tomlPath))
        {
            File.WriteAllText(tomlPath, $"[cloud]\nenabled = {val}\n");
            return;
        }

        var lines = File.ReadAllLines(tomlPath).ToList();

        int header = lines.FindIndex(l => IsActiveTableHeader(l, "cloud"));
        if (header < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add("[cloud]");
            lines.Add($"enabled = {val}");
            File.WriteAllLines(tomlPath, lines);
            return;
        }

        int sectionEnd = lines.FindIndex(header + 1, IsActiveAnyTableHeader);
        if (sectionEnd < 0) sectionEnd = lines.Count;

        for (int i = header + 1; i < sectionEnd; i++)
        {
            string t = lines[i].TrimStart();
            if (t.StartsWith('#')) continue;                       // commented → ignore
            if (Regex.IsMatch(t, @"^enabled\s*="))
            {
                string indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
                lines[i] = $"{indent}enabled = {val}";
                File.WriteAllLines(tomlPath, lines);
                return;
            }
        }

        // [cloud] exists but no active enabled key → insert one under the header.
        lines.Insert(header + 1, $"enabled = {val}");
        File.WriteAllLines(tomlPath, lines);
    }

    /// <summary>Read opensteamtool.toml's active [cloud] enabled value (false if the file/section/key is
    /// absent).</summary>
    private static bool ReadOpenSteamToolCloudEnabled(string steamRoot)
    {
        string tomlPath = Path.Combine(steamRoot, "opensteamtool.toml");
        if (!File.Exists(tomlPath)) return false;

        var lines = File.ReadAllLines(tomlPath);
        int header = Array.FindIndex(lines, l => IsActiveTableHeader(l, "cloud"));
        if (header < 0) return false;

        for (int i = header + 1; i < lines.Length; i++)
        {
            if (IsActiveAnyTableHeader(lines[i])) break;            // next section → done
            string t = lines[i].TrimStart();
            if (t.StartsWith('#')) continue;                       // commented → ignore
            var m = Regex.Match(t, @"^enabled\s*=\s*(\w+)");
            if (m.Success) return m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private async Task<GithubRelease?> FetchReleaseAsync(ModeDefinition def, bool forceRefresh, CancellationToken ct)
    {
        // Serve from cache within the TTL unless a forced refresh is requested.
        if (!forceRefresh
            && _releaseCache.TryGetValue(def.Mode, out var cached)
            && DateTime.UtcNow - cached.fetchedAt < CacheTtl)
            return cached.release;

        string url = def.FixedTag is not null
            ? $"https://api.github.com/repos/{def.Owner}/{def.Repo}/releases/tags/{def.FixedTag}"
            : $"https://api.github.com/repos/{def.Owner}/{def.Repo}/releases/latest";
        try
        {
            // Routed via GithubProxy: direct, then mirrors (for blocked/throttled regions).
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode) return null;
            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            if (release is not null) _releaseCache[def.Mode] = (release, DateTime.UtcNow);
            return release;
        }
        catch
        {
            return null; // offline / rate-limited / parse error → caller maps to Unknown
        }
    }

    /// <summary>Find the small Release zip (matches the pattern, excludes any Debug build).</summary>
    private static GithubAsset? FindZipAsset(ModeDefinition def, GithubRelease release)
    {
        string wanted = (def.ZipAssetPattern ?? "").Replace("{version}", release.TagName);
        return release.Assets.FirstOrDefault(a =>
                   a.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) &&
                   !a.Name.Contains("Debug", StringComparison.OrdinalIgnoreCase))
               ?? release.Assets.FirstOrDefault(a =>
                   a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                   a.Name.Contains("Release", StringComparison.OrdinalIgnoreCase) &&
                   !a.Name.Contains("Debug", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Extract just the wanted files from a zip into <paramref name="destDir"/> (flattened).</summary>
    private static Dictionary<string, string> ExtractWanted(string zipPath, string[] wanted, string destDir)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
            string? match = wanted.FirstOrDefault(w => w.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
            if (match is null || result.ContainsKey(match)) continue;
            string dest = Path.Combine(destDir, match);
            entry.ExtractToFile(dest, overwrite: true);
            result[match] = dest;
        }
        return result;
    }

    // Asset download routed via GithubProxy: direct, then mirrors (for blocked/throttled regions).
    private Task DownloadToFileAsync(string url, string destPath, IProgress<double?>? progress, CancellationToken ct) =>
        gh.DownloadAsync(url, destPath, progress, ct);

    // Hashing and digest parsing live in AssetIntegrity — see that type for why verification is
    // fail-closed and why four services having their own copies of it was itself the bug.

    private static void StampNow(string path)
    {
        try
        {
            var now = DateTime.Now;
            File.SetCreationTime(path, now);
            File.SetLastWriteTime(path, now);
        }
        catch { /* cosmetic */ }
    }
}
