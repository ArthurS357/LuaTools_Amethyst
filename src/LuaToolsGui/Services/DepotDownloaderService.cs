using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

/// <summary>One depot to fetch: which depot, which version, where its manifest is, and how big it is.</summary>
/// <param name="ManifestPath">
/// Absolute path to the <c>.manifest</c> in Steam's depotcache, or null when it isn't there yet. Null is
/// normal at pick time: the run loop resolves it, fetching into depotcache if needed, and rewrites the
/// record before handing it to the downloader.
/// </param>
/// <param name="ManifestId">
/// Null for a shared redistributable: its gid lives under <see cref="FromAppId"/>, not the game's own
/// app-info, and is resolved at download time along with the real size.
/// </param>
public sealed record DepotSelection(long DepotId, string? ManifestId, string? ManifestPath, long Size)
{
    /// <summary>Owning app for a shared depot (see <see cref="ContentDepot.IsShared"/>), else null.</summary>
    public long? FromAppId { get; init; }
}

/// <summary>What the downloader is doing right now, parsed from its stdout.</summary>
/// <remarks>
/// A big depot spends a long stretch before a single byte arrives: the tool pre-allocates every new file at
/// full size first, and a resumed one re-hashes what is already on disk. Without this the row just reads
/// "0 B of 4.49 GB" and looks hung.
/// </remarks>
public enum DepotPhase
{
    /// <summary>Fetching the depot manifest.</summary>
    Manifest,
    /// <summary>Creating zero-filled files at their final size. No bytes are being fetched yet.</summary>
    PreAllocating,
    /// <summary>Re-hashing files that already exist (a resume with -validate).</summary>
    Validating,
    /// <summary>Actually pulling chunks.</summary>
    Downloading,
}

/// <summary>Why a depot download stopped, in a form the UI can localize. <see cref="None"/> means it didn't.</summary>
public enum DepotFailure
{
    None,
    /// <summary>The pinned tool digest is blank, so nothing may be downloaded or run. See <see cref="AppConfig"/>.</summary>
    ToolNotPinned,
    /// <summary>The tool could not be obtained, or what was obtained failed verification.</summary>
    ToolUnavailable,
    /// <summary>Steam isn't located, so there is no depotcache to read or write.</summary>
    SteamNotFound,
    /// <summary>No decryption key is known for this app at all.</summary>
    NoKeys,
    /// <summary>A specific depot has no usable key.</summary>
    NoKeyForDepot,
    /// <summary>A key exists for this depot but the manifest proves it is the wrong one.</summary>
    BadKey,
    /// <summary>The manifest isn't cached and couldn't be fetched (or what came back wasn't a manifest).</summary>
    NoManifest,
    /// <summary>Fetching a missing manifest needs an account; this one is a guest.</summary>
    SignInRequired,
    /// <summary>Not enough room on the destination volume.</summary>
    NotEnoughSpace,
    /// <summary>The downloader itself exited non-zero, timed out, or could not be started.</summary>
    DownloaderFailed,
}

/// <summary>Outcome of one depot's process run.</summary>
public sealed record DepotRunResult(bool Ok, DepotFailure Failure, string? Detail);

/// <summary>Outcome of a whole selection.</summary>
/// <param name="CompletedDepots">Depots finished so far — a resume skips these instead of re-hashing them.</param>
public sealed record DepotJobResult(
    bool Ok, DepotFailure Failure, long? DepotId, string? Detail, IReadOnlyList<long> CompletedDepots)
{
    public static DepotJobResult Success(IReadOnlyList<long> done) =>
        new(true, DepotFailure.None, null, null, done);
}

/// <summary>How far along a selection is, for the progress bar and the status line.</summary>
public readonly record struct DepotProgress(
    long Bytes, long Total, DepotPhase Phase, int Index, int Count);

/// <summary>
/// Runs DepotDownloaderMod to pull raw depot content from Steam's CDN.
/// </summary>
/// <remarks>
/// <para><b>No account is ever used.</b> <c>-username</c> and <c>-qr</c> are never passed, so the tool takes
/// its anonymous branch. Those flags are the only paths that reach a <c>Console.ReadLine()</c>, and with
/// stdout redirected a prompt would block forever — hence the silence watchdog below. An anonymous account
/// owns nothing, which is exactly why both inputs must be supplied: the depot key (<c>-depotkeys</c>) and
/// the manifest (<c>-manifestfile</c>).</para>
///
/// <para><b>One process per depot.</b> The tool accepts multiple <c>-depot</c>/<c>-manifest</c> pairs, but
/// <c>-manifestfile</c> is a single value applied to every depot in its loop, so batching would feed them
/// all the same manifest.</para>
///
/// <para><b>Resume needs <c>-validate</c>.</b> Files are pre-allocated at full size, and because
/// <c>-manifestfile</c> makes the tool's "previous" and "new" manifests identical, its hash check always
/// matches — so a re-run WITHOUT <c>-validate</c> downloads nothing and reports success over a half-written
/// file.</para>
///
/// <para><b>Serialized app-wide</b> behind <c>_runGate</c>. Concurrent anonymous sessions share a
/// SteamKit-derived LoginID and disconnect each other, and parallel multi-GB transfers only split the same
/// bandwidth.</para>
///
/// <para><b>Divergences from upstream, all deliberate.</b> The tool is resolved by PINNED TAG and verified
/// against a compiled-in SHA-256 rather than whatever <c>releases/latest</c> advertises; verification is
/// <see cref="AssetIntegrity"/> (fail-closed) rather than upstream's fail-open helper; the keys file is
/// written with a restricted DACL and swept at startup; and logging goes to <see cref="AppLog"/>.</para>
/// </remarks>
public sealed partial class DepotDownloaderService(
    GithubProxy gh,
    SteamService steam,
    AuthService auth,
    CacheService cache,
    LuaToolsApiClient api,
    LuaInstaller installer)
{
    private static readonly string ToolDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "depotdownloader");

    private static string ExePath => Path.Combine(ToolDir, "DepotDownloaderMod.exe");

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

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Kill the child if it produces no output for this long — it's prompting or wedged.</summary>
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromMinutes(10);

    /// <summary>How long an up-to-date check is trusted before GitHub is asked again.</summary>
    private static readonly TimeSpan ToolCheckInterval = TimeSpan.FromHours(6);

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

    private readonly SemaphoreSlim _toolGate = new(1, 1);
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>Matches the tool's per-file progress line, e.g. " 42.17% game/data.pak".</summary>
    [GeneratedRegex(@"^\s*([0-9]+(?:\.[0-9]+)?)%\s+(.+)$")]
    private static partial Regex ProgressRegex();

    /// <summary>
    /// Whether the compiled-in pin is present and well-formed. False disables the whole feature — the tool
    /// is never downloaded and never run.
    /// </summary>
    /// <remarks>
    /// This is the switch that makes an un-pinned build safe rather than merely un-updated. Upstream has no
    /// equivalent: it will happily fetch and execute whatever <c>releases/latest</c> hands back.
    /// </remarks>
    public static bool PinIsUsable =>
        AssetIntegrity.ParseDigest(AppConfig.DepotDownloaderPinnedSha256) is not null
        && AppConfig.DepotDownloaderPinnedAssetName.Length > 0
        && AppConfig.DepotDownloaderPinnedTag.Length > 0;

    /// <summary>
    /// Whether missing manifests can be fetched from the API. Guests can still download depots whose
    /// manifest Steam already has — they just can't pull new ones. Checked locally so the picker never
    /// needs a request to decide what to grey out.
    /// </summary>
    public bool CanFetchManifests => !auth.IsGuest;

    // ── Tool acquisition ─────────────────────────────────────────────

    private static bool CheckedRecently(long lastMs) =>
        lastMs > 0
        && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastMs < (long)ToolCheckInterval.TotalMilliseconds;

    /// <summary>
    /// Ensure the pinned tool is on disk and verified. Null when it could not be obtained at all.
    /// </summary>
    /// <remarks>
    /// Every path here is fail-closed. The release must carry the pinned tag, the asset must have the
    /// pinned name, the digest GitHub publishes must equal the pinned digest, and the delivered bytes must
    /// hash to it as well. A failure never falls back to "run whatever is on disk from an older pin"
    /// either: the recorded version has to match the pin before an existing exe is reused.
    /// </remarks>
    public async Task<string?> EnsureToolAsync(IProgress<double?>? progress, CancellationToken ct = default)
    {
        if (!PinIsUsable)
        {
            AppLog.Log("DepotDownloader: no usable pin in AppConfig — the depot downloader is disabled.");
            return null;
        }

        if (HaveVerifiedTool() && CheckedRecently(cache.DepotDownloaderCheckedAtMs)) return ExePath;

        await _toolGate.WaitAsync(ct);
        try
        {
            if (HaveVerifiedTool() && CheckedRecently(cache.DepotDownloaderCheckedAtMs)) return ExePath; // won the race

            string url = $"https://api.github.com/repos/{AppConfig.DepotDownloaderRepo}" +
                         $"/releases/tags/{AppConfig.DepotDownloaderPinnedTag}";
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode)
            {
                AppLog.Log($"DepotDownloader: release lookup failed ({res?.StatusCode.ToString() ?? "no response"}).");
                return Fallback();
            }

            var release = JsonSerializer.Deserialize<GithubRelease>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            var asset = release?.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, AppConfig.DepotDownloaderPinnedAssetName, StringComparison.Ordinal));
            if (asset is null)
            {
                AppLog.Log($"DepotDownloader: release {AppConfig.DepotDownloaderPinnedTag} has no asset named " +
                           $"{AppConfig.DepotDownloaderPinnedAssetName}.");
                return Fallback();
            }

            // The digest the API advertises has to agree with the pin BEFORE anything is fetched. This is
            // what turns "verified against the same response that chose the URL" into "verified against
            // this build".
            if (AssetIntegrity.ParseDigest(asset.Digest) is not { } published
                || !published.Equals(AssetIntegrity.ParseDigest(AppConfig.DepotDownloaderPinnedSha256),
                                     StringComparison.Ordinal))
            {
                AppLog.Log("DepotDownloader: published digest does not match the pinned digest — refusing. " +
                           "Bump AppConfig.DepotDownloaderPinned* together after verifying the new release.");
                return Fallback();
            }

            if (HaveVerifiedTool())
            {
                cache.DepotDownloaderCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return ExePath; // already on the pinned build; skip the ~37 MB download
            }

            Directory.CreateDirectory(ToolDir);
            string zipPath = Path.Combine(ToolDir, "depotdownloader.zip");
            try
            {
                // Repo-scoped: refuses before a byte is fetched if the release metadata points anywhere
                // other than this repo's own release assets.
                await gh.DownloadAssetAsync(asset.DownloadUrl, AppConfig.DepotDownloaderOwner,
                    AppConfig.DepotDownloaderRepoName, zipPath, progress, ct);

                // Verify and extract through ONE held handle, so what was hashed is what gets extracted.
                //
                // Hashing a path and then re-opening it to use it leaves a window in which another process
                // running as this user can substitute the file — and the bytes that end up as a running
                // .exe are then not the bytes whose digest was checked. AssetIntegrity.OpenPinned takes
                // FileShare.Read WITHOUT FileShare.Delete, so the file cannot be replaced, truncated or
                // renamed while this handle is open.
                using (var pinned = AssetIntegrity.OpenPinned(zipPath))
                {
                    // Fail-closed, against the PIN rather than the response. AssetIntegrity treats an
                    // absent or malformed digest as a failure, so there is no digest-less path through here.
                    if (!AssetIntegrity.MatchesStream(pinned, AppConfig.DepotDownloaderPinnedSha256))
                    {
                        AppLog.Log("DepotDownloader: delivered bytes do not match the pinned digest — discarding.");
                        return Fallback();
                    }

                    // Extract the WHOLE zip: the exe needs SteamKit2.dll and friends beside it.
                    // ExtractToDirectory validates that no entry escapes the destination, so a crafted
                    // archive cannot write outside ToolDir.
                    pinned.Position = 0;
                    using var archive = new ZipArchive(pinned, ZipArchiveMode.Read, leaveOpen: true);
                    archive.ExtractToDirectory(ToolDir, overwriteFiles: true);
                }
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch (IOException) { /* harmless */ }
            }

            if (!File.Exists(ExePath))
            {
                AppLog.Log("DepotDownloader: the pinned archive did not contain DepotDownloaderMod.exe at its root.");
                return null;
            }

            cache.DepotDownloaderVersion = AppConfig.DepotDownloaderPinnedTag;
            cache.DepotDownloaderCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return ExePath;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException
                                      or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Log($"DepotDownloader: obtaining the tool failed — {ex.Message}");
            return Fallback();
        }
        finally { _toolGate.Release(); }

        // A failed lookup still counts as "we looked": EnsureToolAsync runs once per depot, so an offline
        // ten-depot game would otherwise walk every GithubProxy mirror ten times. Backing off costs at most
        // one interval of update latency, and it never resurrects a tool that isn't on the pinned build.
        string? Fallback()
        {
            bool have = HaveVerifiedTool();
            if (have) cache.DepotDownloaderCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return have ? ExePath : null;
        }
    }

    /// <summary>
    /// True when the exe on disk is the one this build pins. The recorded version is what makes an existing
    /// install reusable — an exe left over from a previous pin is treated as absent, not as good enough.
    /// </summary>
    private bool HaveVerifiedTool() =>
        File.Exists(ExePath)
        && string.Equals(cache.DepotDownloaderVersion, AppConfig.DepotDownloaderPinnedTag, StringComparison.Ordinal);

    // ── Input sourcing ───────────────────────────────────────────────

    /// <summary>
    /// Depot-id to decryption key for an app: from its installed lua first (that's where LuaTools puts
    /// them), falling back to Steam's own config.vdf for depots the lua doesn't carry.
    /// </summary>
    /// <remarks>
    /// These keys never leave the machine. Their only consumers are <see cref="WriteKeysFile"/>, whose
    /// output the local child process reads, and <see cref="ManifestFile.KeyLooksValid"/>. See the
    /// "Removed: outbound data collection" note in <see cref="AppConfig"/> for what must not come back.
    /// </remarks>
    public IReadOnlyDictionary<long, string> ResolveKeys(long appId)
    {
        var keys = new Dictionary<long, string>();

        try
        {
            if (steam.StPlugInDir is { } dir)
            {
                string lua = Path.Combine(dir, $"{appId}.lua");
                if (File.Exists(lua) && LuaFileParser.Parse(lua, appId) is { } parsed)
                {
                    // DisabledEntries too: a depot switched off on the Depots page still has a valid key,
                    // and the user explicitly picked it for download.
                    foreach (var e in parsed.Entries.Concat(parsed.DisabledEntries))
                        if (e.Key is { Length: > 0 }) keys[e.Id] = e.Key;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Log($"DepotDownloader: reading depot keys from the lua for {appId} failed — {ex.Message}");
        }

        try
        {
            if (steam.EffectivePath is { } root)
            {
                string vdf = Path.Combine(root, "config", "config.vdf");
                if (File.Exists(vdf))
                {
                    foreach (var pair in SteamConfigVdf.ExtractKeys(File.ReadAllText(vdf)))
                        if (!keys.ContainsKey(pair.DepotId)) keys[pair.DepotId] = pair.Key;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Log($"DepotDownloader: reading depot keys from config.vdf failed — {ex.Message}");
        }

        return keys;
    }

    /// <summary>
    /// The depotcache path for a depot at a given manifest version, or null when Steam doesn't have it.
    /// </summary>
    /// <remarks>
    /// Existence is not enough. A half-written or truncated file (a killed download, a full disk) stays on
    /// disk under the right name and would be handed to the downloader, which fails on it with a raw parse
    /// error. Requiring the file to parse AND to declare the depot and gid its name claims turns that into
    /// a clean cache miss, which the fetch path then repairs.
    /// </remarks>
    public string? ResolveManifestPath(long depotId, string manifestId)
    {
        if (CachedManifestPath(depotId, manifestId) is not { } path) return null;
        return ManifestFile.Matches(path, depotId, manifestId) ? path : null;
    }

    /// <summary>The name a depot's manifest has in depotcache, whether or not it is there.</summary>
    private string? CachedManifestPath(long depotId, string manifestId) =>
        steam.DepotCacheDir is { } dir ? Path.Combine(dir, ManifestFileName(depotId, manifestId)) : null;

    /// <summary>
    /// The content-addressed depotcache filename. Both halves are constrained by their types — a
    /// <see langword="long"/> and a digits-only gid — so this cannot produce a path separator or a
    /// traversal segment no matter what the API returned.
    /// </summary>
    internal static string ManifestFileName(long depotId, string manifestId) =>
        $"{depotId}_{manifestId}.manifest";

    /// <summary>Whether a manifest id is safe to interpolate into a filename and parse as a gid.</summary>
    internal static bool IsValidManifestId(string? manifestId) =>
        manifestId is { Length: > 0 and <= 20 } && manifestId.All(char.IsAsciiDigit);

    /// <summary>
    /// Delete a cached manifest that failed validation, so a re-fetch can actually replace it.
    /// </summary>
    /// <remarks>
    /// Mandatory before re-fetching, not a tidy-up: <see cref="LuaInstaller.InstallManifestFile"/> SKIPS a
    /// destination that already exists (deliberately — the name is content-addressed and Steam may hold the
    /// file open). A corrupt entry would otherwise survive the fetch, be handed back, and fail again on
    /// every attempt, so the download could never recover on its own.
    /// </remarks>
    public bool DiscardCachedManifest(long depotId, string manifestId)
    {
        if (CachedManifestPath(depotId, manifestId) is not { } path) return false;
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Log($"DepotDownloader: could not discard the cached manifest for depot {depotId} — {ex.Message}");
            return false; // Steam holding it open; the fetch will fail loudly rather than silently
        }
    }

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

    // ── Orchestration ────────────────────────────────────────────────

    /// <summary>
    /// Download a whole selection into <paramref name="outDir"/>, resolving manifests and budgeting disk
    /// before a single byte is written.
    /// </summary>
    /// <param name="completed">Depots already finished (a resume). These are skipped rather than re-hashed.</param>
    /// <param name="createdFile">Receives every path the run creates, so a cancel can delete exactly those.</param>
    /// <param name="depotCompleted">
    /// Reports each depot id the moment it finishes, so a caller that is cancelled mid-run still knows what
    /// not to fetch again. <see cref="DepotJobResult.CompletedDepots"/> only reaches a caller that gets a
    /// RESULT, and a pause or a cancel throws instead — which is exactly when the list matters most, since
    /// resuming without it re-hashes every depot that was already done.
    /// </param>
    public async Task<DepotJobResult> DownloadDepotsAsync(
        long appId, IReadOnlyList<DepotSelection> selections, string outDir,
        IReadOnlyCollection<long> completed, IProgress<DepotProgress>? progress,
        IProgress<string>? createdFile, IProgress<long>? depotCompleted, CancellationToken ct)
    {
        var done = new List<long>(completed);

        if (steam.DepotCacheDir is null)
            return new DepotJobResult(false, DepotFailure.SteamNotFound, null, null, done);
        if (!PinIsUsable)
            return new DepotJobResult(false, DepotFailure.ToolNotPinned, null, null, done);

        var keys = ResolveKeys(appId);
        if (keys.Count == 0)
            return new DepotJobResult(false, DepotFailure.NoKeys, null, null, done);

        // Sampled ONCE, before anything runs. Checking it inside the loop would be self-fulfilling: the
        // first depot creates outDir, so every later depot would think a previous session had written there.
        bool outDirExisted = Directory.Exists(outDir);

        // The ~37 MB first fetch happens once here, with visible progress, rather than inside the per-depot
        // loop where it would stall the first depot's bar.
        var toolProgress = progress is null ? null
            : new Progress<double?>(f => progress.Report(new DepotProgress(
                (long)((f ?? 0) * 1000), 1000, DepotPhase.Manifest, 0, selections.Count)));
        if (await EnsureToolAsync(toolProgress, ct) is null)
            return new DepotJobResult(false, DepotFailure.ToolUnavailable, null, null, done);

        // ── Resolve EVERYTHING before a single byte is written ────────
        // A manifest that cannot be fetched must not abort the job after earlier depots have already pulled
        // tens of GB, and the budget below must not sum 0 for an unresolved shared depot.
        var resolved = new List<DepotSelection>(selections.Count);
        for (int i = 0; i < selections.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new DepotProgress(0, 0, DepotPhase.Manifest, i + 1, selections.Count));

            var sel = selections[i];
            if (!IsValidManifestId(sel.ManifestId))
                return new DepotJobResult(false, DepotFailure.NoManifest, sel.DepotId, null, done);

            if (!done.Contains(sel.DepotId))
            {
                var (path, failure) = await EnsureManifestAsync(sel, ct);
                if (failure != DepotFailure.None)
                    return new DepotJobResult(false, failure, sel.DepotId, null, done);
                sel = sel with { ManifestPath = path };

                // Without a key the tool cannot decrypt a single chunk, and a failed depot aborts the whole
                // job — so refuse here, before anything is written, naming the depot instead of surfacing
                // the downloader's own "No valid depot key" much later.
                if (!keys.TryGetValue(sel.DepotId, out string? hex) || !TryParseKey(hex, out byte[] key))
                    return new DepotJobResult(false, DepotFailure.NoKeyForDepot, sel.DepotId, null, done);

                // A key that exists but is WRONG can only be caught when the manifest still has its
                // filenames encrypted, which is the small minority — see ManifestFile.KeyLooksValid.
                if (!ManifestFile.KeyLooksValid(sel.ManifestPath, key))
                    return new DepotJobResult(false, DepotFailure.BadKey, sel.DepotId, null, done);
            }

            // The manifest's own cb_disk_original beats app-info's size: it is exact, and app info may not
            // have carried a size at all (a token-gated app returns no depot list, so those depots arrive
            // here as 0 and would otherwise be budgeted as free).
            if (ManifestFile.TryRead(sel.ManifestPath) is { SizeOnDisk: > 0 } info)
                sel = sel with { Size = info.SizeOnDisk };

            resolved.Add(sel);
        }

        // ── Budget, now that the sizes are real ──────────────────────
        // The downloader pre-allocates every file at full size BEFORE fetching a byte, so a short disk fails
        // almost immediately — but only after creating multi-GB of zero-filled files.
        long totalSize = resolved.Sum(s => s.Size);
        long needed = resolved.Where(s => !done.Contains(s.DepotId)).Sum(s => s.Size);
        if (needed > 0 && FreeSpaceFor(outDir) is { } free && free < needed)
            return new DepotJobResult(false, DepotFailure.NotEnoughSpace, null,
                FormatSpace(needed, free), done);

        // ── Download ─────────────────────────────────────────────────
        using var keysFile = WriteKeysFile(keys);
        bool needsValidate = outDirExisted;
        long bytesDone = 0;

        for (int i = 0; i < resolved.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var ready = resolved[i];

            if (done.Contains(ready.DepotId)) { bytesDone += ready.Size; continue; }

            // Re-checked per depot: the volume is shared with everything else on the machine, so a budget
            // that cleared at the start can be gone by depot 12. Running out mid-download is not reported as
            // a disk error — the tool simply stops printing, and the watchdog kills it as a "timeout".
            if (ready.Size > 0 && FreeSpaceFor(outDir) is { } left && left < ready.Size)
                return new DepotJobResult(false, DepotFailure.NotEnoughSpace, ready.DepotId,
                    FormatSpace(ready.Size, left), done);

            // Only the FIRST depot after a resume is the partially-written one, so only it needs the
            // expensive re-hash. An existing output folder forces the same treatment even on a fresh run:
            // it means a previous session already wrote here, and skipping validation there would hand back
            // a half-written file reported as complete — this tool's worst failure mode.
            bool validate = needsValidate;
            needsValidate = false;

            long baseBytes = bytesDone;
            int index = i + 1;
            var relay = progress is null ? null
                : new Progress<(double Fraction, DepotPhase Phase)>(p => progress.Report(new DepotProgress(
                    baseBytes + (long)(p.Fraction * ready.Size), totalSize, p.Phase, index, resolved.Count)));

            var res = await RunAsync(appId, ready, keysFile.Path, outDir, validate, relay, createdFile, ct);
            if (!res.Ok) return new DepotJobResult(false, res.Failure, ready.DepotId, res.Detail, done);

            done.Add(ready.DepotId);
            depotCompleted?.Report(ready.DepotId);
            bytesDone += ready.Size;
            progress?.Report(new DepotProgress(bytesDone, totalSize, DepotPhase.Downloading, index, resolved.Count));
        }

        return DepotJobResult.Success(done);
    }

    private static string FormatSpace(long needed, long free) =>
        $"{needed.ToString(CultureInfo.InvariantCulture)}/{free.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// A depot key as bytes. Keys come from a lua file and from Steam's config.vdf, so a malformed one is a
    /// real possibility and reads the same as having no key at all: the download cannot proceed.
    /// </summary>
    private static bool TryParseKey(string? hex, out byte[] key)
    {
        key = [];
        if (hex is not { Length: 64 }) return false; // AES-256, hex-encoded
        try { key = Convert.FromHexString(hex); return true; }
        catch (FormatException) { return false; }
    }

    /// <summary>
    /// The depotcache path for a depot's manifest, fetching it from the API and installing it there when
    /// it's missing.
    /// </summary>
    /// <remarks>
    /// The screening order is the point. The API is expected to serve raw manifest bytes but has been
    /// observed returning them inside a zip; the wrapper is unwrapped to a name this method COMPUTES (never
    /// <c>entry.FullName</c>, so a crafted archive cannot choose a path), the bytes are proved to be a Steam
    /// manifest BEFORE anything is written, and only then is the file copied into depotcache. A bad file
    /// that reaches depotcache is sticky — every later run resolves it locally and fails identically.
    /// </remarks>
    private async Task<(string? Path, DepotFailure Failure)> EnsureManifestAsync(
        DepotSelection sel, CancellationToken ct)
    {
        string manifestId = sel.ManifestId!;

        // Already on disk (a previous run, a pinned install, or Steam's own copy): no request at all.
        if (ResolveManifestPath(sel.DepotId, manifestId) is { } have) return (have, DepotFailure.None);

        // Nothing usable — but something may still be sitting there under the right name. It has to go
        // before the fetch, or InstallManifestFile will skip the copy and hand the bad file straight back.
        DiscardCachedManifest(sel.DepotId, manifestId);

        if (!CanFetchManifests) return (null, DepotFailure.SignInRequired);

        DownloadedFile staged;
        try
        {
            staged = await api.DownloadDepotManifestAsync(sel.DepotId, manifestId, null, ct);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or IOException)
        {
            AppLog.Log($"DepotDownloader: fetching the manifest for depot {sel.DepotId} failed — {ex.Message}");
            return (null, DepotFailure.NoManifest);
        }

        return InstallFetchedManifest(sel.DepotId, manifestId, staged.FilePath);
    }

    /// <summary>
    /// Screen a freshly staged manifest and, if it really is one, put it in depotcache. Deletes the staged
    /// file either way.
    /// </summary>
    /// <remarks>
    /// The network-free half of <see cref="EnsureManifestAsync"/>, split out so the screening order can be
    /// tested without a signed-in session. That order is the security property: unwrap to a name this method
    /// COMPUTES, prove the bytes are a Steam manifest, and only then write. Nothing that fails a check ever
    /// reaches <c>config\depotcache</c>.
    /// </remarks>
    internal (string? Path, DepotFailure Failure) InstallFetchedManifest(
        long depotId, string manifestId, string stagedPath)
    {
        string unzipDir = Path.Combine(Path.GetDirectoryName(stagedPath)!, "mf_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(unzipDir);

            // ALWAYS rematerialize under the computed name, zip or not.
            //
            // InstallManifestFile names the destination after the file it is handed
            // (Path.GetFileName), and the staged name comes from the response's Content-Disposition
            // header when it sends one. Passing the staged file straight through would therefore let the
            // server choose what this app writes into Steam's config\depotcache. The name is rebuilt here
            // from a long and a digits-only gid, so it cannot carry a separator or a traversal segment.
            string manifestFile = Path.Combine(unzipDir, ManifestFileName(depotId, manifestId));

            if (IsZip(stagedPath))
            {
                using var archive = ZipFile.OpenRead(stagedPath);
                if (archive.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name)) is not { } entry)
                    return (null, DepotFailure.NoManifest);

                // The entry's own name is ignored for the same reason: no zip-slip.
                entry.ExtractToFile(manifestFile, overwrite: true);
            }
            else
            {
                File.Copy(stagedPath, manifestFile, overwrite: true);
            }

            // Screen with the FULL identity check, not just the leading magic.
            //
            // A truncated response — a killed transfer, a proxy that cut the body — keeps the first four
            // bytes of a real manifest and so passes a magic-only test, but has no usable metadata section.
            // Writing it would poison depotcache permanently: the name is content-addressed and
            // InstallManifestFile skips an existing destination, so every later run would resolve the
            // broken copy locally and fail identically. Matches() parses the file and requires it to
            // declare the depot and gid its name claims, which is exactly the condition
            // ResolveManifestPath applies before a download is allowed to use it.
            if (!ManifestFile.Matches(manifestFile, depotId, manifestId))
            {
                AppLog.Log(ManifestFile.IsSteamManifest(manifestFile)
                    ? $"DepotDownloader: the manifest returned for depot {depotId} does not declare that " +
                      "depot and gid — refusing to cache it."
                    : $"DepotDownloader: what came back for depot {depotId} is not a Steam manifest.");
                return (null, DepotFailure.NoManifest);
            }

            // Reuse the same depotcache write manifest installs already use: keeps the content-addressed
            // name, skips an identical existing file, and stamps the mtime.
            var result = installer.InstallManifestFile(manifestFile);
            if (result.AnyFailed)
            {
                AppLog.Log($"DepotDownloader: installing the manifest for depot {depotId} failed — {result.Error}");
                return (null, DepotFailure.NoManifest);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            AppLog.Log($"DepotDownloader: staging the manifest for depot {depotId} failed — {ex.Message}");
            return (null, DepotFailure.NoManifest);
        }
        finally
        {
            try { if (File.Exists(stagedPath)) File.Delete(stagedPath); }
            catch (IOException) { /* swept later */ }
            try { if (Directory.Exists(unzipDir)) Directory.Delete(unzipDir, recursive: true); }
            catch (IOException) { /* swept later */ }
        }

        // Re-resolved rather than assumed: the installed file has to parse AND declare the identity its
        // name claims before the download is allowed to use it. If it somehow does not — a copy that was
        // interrupted, a destination Steam swapped underneath — discard it rather than leave a file behind
        // that every later run would resolve and fail on.
        if (ResolveManifestPath(depotId, manifestId) is { } installed) return (installed, DepotFailure.None);

        DiscardCachedManifest(depotId, manifestId);
        return (null, DepotFailure.NoManifest);
    }

    /// <summary>Sniff the PK header rather than trusting the extension.</summary>
    private static bool IsZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[2];
            return fs.ReadAtLeast(head, 2, throwOnEndOfStream: false) == 2 && head[0] == 'P' && head[1] == 'K';
        }
        catch (IOException) { return false; }
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
