using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
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
    /// <summary>
    /// Whether missing manifests can be fetched from the API. Guests can still download depots whose
    /// manifest Steam already has — they just can't pull new ones. Checked locally so the picker never
    /// needs a request to decide what to grey out.
    /// </summary>
    public bool CanFetchManifests => !auth.IsGuest;

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
    /// <remarks>
    /// Prefers the real depotcache, then falls back to a file left behind in the old, wrong
    /// <c>config\depotcache</c> (see <see cref="SteamService.DepotCacheDir"/>). The fallback is READ ONLY:
    /// it exists so an install whose migration has not run yet — or could not move a locked file — still
    /// counts those manifests as cached instead of re-fetching a whole depot list. Nothing is ever written
    /// to the legacy folder.
    /// </remarks>
    private string? CachedManifestPath(long depotId, string manifestId)
    {
        string name = ManifestFileName(depotId, manifestId);

        if (steam.DepotCacheDir is not { } dir) return null;

        string real = Path.Combine(dir, name);
        if (File.Exists(real)) return real;

        if (steam.LegacyDepotCacheDir is { } legacyDir)
        {
            string legacy = Path.Combine(legacyDir, name);
            if (File.Exists(legacy)) return legacy;
        }

        // Neither has it. Return the real path so callers that treat this as "where it WOULD be" keep
        // pointing at the folder Steam actually reads.
        return real;
    }

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
    /// reaches <c>depotcache</c>.
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
            // server choose what this app writes into Steam's depotcache. The name is rebuilt here
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
}
