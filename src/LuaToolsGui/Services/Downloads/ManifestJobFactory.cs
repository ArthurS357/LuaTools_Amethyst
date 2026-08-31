using System.Globalization;
using System.IO;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services.Downloads;

/// <summary>
/// Builds the <see cref="DownloadJob"/>s the queue runs. This is the one place that knows how a manifest
/// is fetched and installed, and how a depot selection is run.
/// </summary>
/// <remarks>
/// <para>Before this existed the same download-then-install sequence was written three times — the Add
/// page's <c>DownloadFromSourceAsync</c>, <see cref="PluginAddService"/> and the HTTP bridge — each with
/// its own zip sniffing, its own staged-file cleanup and its own result wording, which had already drifted
/// apart. All three now build a job here and hand it to <see cref="DownloadQueue"/>.</para>
///
/// <para><b>Nothing is loosened on the way through.</b> The delegates call exactly the services the inline
/// paths called: <see cref="LuaToolsApiClient"/> and <see cref="HubcapService"/> still stage into their own
/// <c>%TEMP%\LuaToolsGui\downloads</c> folder (no fourth staging copy is introduced), and
/// <see cref="DepotDownloaderService"/> still owns the pinned-digest check, the restricted-DACL keys file
/// and its own app-wide run gate. The queue adds scheduling, not permission.</para>
/// </remarks>
public sealed class ManifestJobFactory(
    LuaToolsApiClient api,
    HubcapService hubcap,
    SettingsService settings,
    LuaInstaller installer,
    CacheService cache,
    CoverCache covers,
    DepotDownloaderService depots)
{
    /// <summary>Dedupe key for a game's manifest download, shared by all three entry points.</summary>
    /// <remarks>
    /// Deliberately keyed on the APPID alone, not on the source: two sources for one game are two ways to
    /// get the same file into the same place, so letting both run would race the installer over one
    /// <c>&lt;appid&gt;.lua</c>. Whichever is asked for first wins, and the second caller joins it.
    /// </remarks>
    public static string ManifestKey(long appId) =>
        string.Create(CultureInfo.InvariantCulture, $"manifest:{appId}");

    public static string DepotKey(long appId) =>
        string.Create(CultureInfo.InvariantCulture, $"depot:{appId}");

    /// <summary>
    /// A game's manifest bundle: fetch from <paramref name="sourceName"/>, then install into Steam.
    /// </summary>
    /// <param name="needsKey">
    /// True for a key-gated (Hubcap) source, which downloads DIRECTLY with the user's own key and never
    /// touches lua.tools — the reason a guest with a key can still download.
    /// </param>
    /// <param name="confirm">
    /// Optional overwrite gate, supplied only by the Add page: it is the only caller with a window to show
    /// the before/after diff in. The headless paths pass null and overwrite, as they did inline.
    /// </param>
    public DownloadJob CreateManifestJob(
        long appId,
        string sourceName,
        bool needsKey,
        string? gameName,
        Func<DownloadedFile, DownloadItem, CancellationToken, Task<bool>>? confirm = null,
        Action<DownloadItem, JobResult?>? onFinished = null,
        Action? onReveal = null)
    {
        string display = SourceMeta.Get(sourceName).DisplayName ?? sourceName;
        string title = string.IsNullOrWhiteSpace(gameName)
            ? appId.ToString(CultureInfo.InvariantCulture)
            : gameName;

        return new DownloadJob(
            DownloadKind.Manifest,
            ManifestKey(appId),
            appId,
            title,
            display,
            covers.GetLocalPath(appId),
            (item, progress, ct) => FetchManifestAsync(appId, sourceName, needsKey, gameName, progress, ct),
            (file, item, ct) => Task.FromResult(InstallManifest(file, appId, title, display)),
            confirm,
            onFinished,
            onReveal);
    }

    private async Task<DownloadedFile> FetchManifestAsync(
        long appId, string sourceName, bool needsKey, string? gameName,
        IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        if (!needsKey)
            return await api.DownloadManifestAsync(
                appId.ToString(CultureInfo.InvariantCulture), sourceName, gameName, progress, ct);

        string appidStr = appId.ToString(CultureInfo.InvariantCulture);

        // Peek BEFORE downloading: a successful download invalidates this entry (HubcapService), and this
        // is the only chance to record what "current" meant at download time.
        string? fileModified = hubcap.PeekCachedFileModified(appidStr);

        var result = await hubcap.DownloadManifestAsync(appidStr, settings.HubcapApiKey ?? "", progress, ct);
        if (result is not HubcapResult<DownloadedFile>.Ok ok)
            throw new DownloadAbortedException(HubcapErrorText.Describe(result));

        // Only record a real marker — leaving no record (rather than an unknown/null one) keeps
        // ManifestFreshnessPolicy from guessing at staleness until the next status check confirms it.
        if (fileModified is not null) cache.SaveInstalledManifestFileModified(appidStr, fileModified);
        return ok.Value;
    }

    /// <summary>
    /// Install a staged manifest bundle and phrase the outcome.
    /// </summary>
    /// <remarks>
    /// The zip sniff is not cosmetic: the file is always saved as <c>&lt;appid&gt;.zip</c>, but some sources
    /// return a BARE .lua, and unzipping that throws "End of Central Directory record could not be found".
    /// </remarks>
    private JobResult InstallManifest(DownloadedFile file, long appId, string title, string source)
    {
        var result = IsZip(file.FilePath)
            ? installer.InstallZip(file.FilePath, appId)
            : installer.InstallLua(file.FilePath, appId);

        DeleteStaged(file.FilePath); // consumed — the temp staging copy is no longer needed

        if (result.Error is not null) return new JobResult(false, result.Error);
        if (result.AnyFailed)
            return new JobResult(false, string.Format(CultureInfo.CurrentCulture,
                Resources.Strings.Add_Status_InstallFailed, result.Failed.Count));

        string message = result.ManifestCount > 0
            ? string.Format(CultureInfo.CurrentCulture,
                Resources.Strings.Add_Status_AddedManifests, title, result.ManifestCount)
            : string.Format(CultureInfo.CurrentCulture, Resources.Strings.Add_Status_AddedFetch, title);
        message += " " + string.Format(CultureInfo.CurrentCulture, Resources.Strings.Add_FastFetch_Via, source);

        return new JobResult(true, message, installer.ReadInstalledLua(appId));
    }

    /// <summary>
    /// A depot selection: one child process per depot, writing into <paramref name="outDir"/>.
    /// </summary>
    /// <remarks>
    /// The "download" phase is the whole run and the "install" phase is a no-op — the bytes land in a
    /// user-chosen folder, not in Steam, so there is nothing to copy afterwards. Modelling it as a job
    /// anyway is what gives depot downloads the pause/resume, retry and history the inline Depots-page bar
    /// could not have.
    /// </remarks>
    public DownloadJob CreateDepotJob(long appId, string gameName, IReadOnlyList<DepotSelection> picked,
        string outDir, Action<DownloadItem, JobResult?>? onFinished = null)
    {
        long totalBytes = picked.Sum(p => p.Size);

        return new DownloadJob(
            DownloadKind.Depot,
            DepotKey(appId),
            appId,
            gameName,
            string.Format(CultureInfo.CurrentCulture, Resources.Strings.Downloads_Source_Depots, picked.Count),
            covers.GetLocalPath(appId),
            (item, progress, ct) => RunDepotsAsync(appId, picked, outDir, totalBytes, item, progress, ct),
            (file, item, ct) => Task.FromResult(new JobResult(true, string.Format(
                CultureInfo.CurrentCulture, Resources.Strings.Builds_Depot_Done, picked.Count, outDir), outDir)),
            OnFinished: onFinished,
            OutputPath: outDir);
    }

    private async Task<DownloadedFile> RunDepotsAsync(
        long appId, IReadOnlyList<DepotSelection> picked, string outDir, long totalBytes,
        DownloadItem item, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        // The depot downloader reports its own bytes/total and its own phase. Both are folded onto the
        // queue's contract here: the byte pair drives the bar, the phase becomes the row's detail line.
        var depotProgress = new ProgressRelay<DepotProgress>(p =>
        {
            progress.Report(new DownloadProgress(p.Bytes, p.Total > 0 ? p.Total : null));
            SetDetail(item, p);
        });

        // Runs on the child's stdout thread and reports one path per pre-allocated file — thousands for a
        // large depot, none of them displayed. Appending straight onto the item's set is why this is a
        // ProgressRelay and not a Progress<T>: marshalling each to the dispatcher would queue thousands of
        // UI work items to add to a HashSet.
        var created = new ProgressRelay<string>(path =>
        {
            lock (item.CreatedFiles) item.CreatedFiles.Add(path);
        });

        // Recorded as each depot lands, not from the returned result: a pause or a cancel throws out of the
        // call, so waiting for a DepotJobResult would lose exactly the list a resume needs.
        var depotDone = new ProgressRelay<long>(id =>
        {
            lock (item.CompletedDepots) item.CompletedDepots.Add(id);
        });

        List<long> alreadyDone;
        lock (item.CompletedDepots) alreadyDone = [.. item.CompletedDepots];

        var result = await depots.DownloadDepotsAsync(
            appId, picked, outDir, alreadyDone, depotProgress, created, depotDone, ct);

        if (!result.Ok)
            throw new DownloadAbortedException(
                DepotErrorText.Describe(result.Failure, result.DepotId, result.Detail));

        // Nothing was staged — the content is already in its final folder. The install phase only phrases
        // the result, and DownloadJob.OutputPath is what "Show in folder" actually opens.
        return new DownloadedFile(outDir, Path.GetFileName(outDir));
    }

    private static void SetDetail(DownloadItem item, DepotProgress p)
    {
        string phase = p.Phase switch
        {
            DepotPhase.PreAllocating => Resources.Strings.Builds_Depot_Phase_PreAllocating,
            DepotPhase.Validating => Resources.Strings.Builds_Depot_Phase_Validating,
            DepotPhase.Manifest => Resources.Strings.Builds_Depot_Phase_FetchingManifest,
            _ => Resources.Strings.Builds_Depot_Phase_Downloading,
        };

        item.Detail = p.Count > 0
            ? string.Format(CultureInfo.CurrentCulture,
                Resources.Strings.Builds_Depot_Downloading, p.Index, p.Count, phase)
            : phase;
    }

    /// <summary>True if the file begins with the ZIP local-file-header magic (PK\x03\x04).</summary>
    internal static bool IsZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> sig = stackalloc byte[4];
            return fs.Read(sig) == 4 && sig[0] == 0x50 && sig[1] == 0x4B && sig[2] == 0x03 && sig[3] == 0x04;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void DeleteStaged(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}
