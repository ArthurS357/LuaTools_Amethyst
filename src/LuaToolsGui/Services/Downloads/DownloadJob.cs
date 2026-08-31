namespace LuaToolsGui.Services.Downloads;

/// <summary>What a queued download is for. Drives the row icon and the history label.</summary>
public enum DownloadKind
{
    /// <summary>A game's manifest bundle from a lua.tools source or from Hubcap.</summary>
    Manifest,

    /// <summary>Raw depot content pulled by <see cref="DepotDownloaderService"/>.</summary>
    Depot,
}

/// <summary>
/// A job refused to start, or stopped, for a reason the user can act on. Carries a ready-to-display,
/// already-localized message.
/// </summary>
/// <remarks>
/// Distinct from <see cref="ApiException"/> on purpose: this is thrown for outcomes that are not HTTP
/// failures — a Hubcap refusal already rendered by <see cref="HubcapErrorText"/>, or a depot run that
/// stopped on a <see cref="DepotFailure"/>. Reusing ApiException would surface the message correctly but
/// would name the failure after a call that may never have happened.
/// </remarks>
public sealed class DownloadAbortedException(string message, bool isCancellation = false) : Exception(message)
{
    /// <summary>
    /// Settle the item as Cancelled rather than Failed, keeping this exception's message.
    /// </summary>
    /// <remarks>
    /// For outcomes that stop the job without anything having gone wrong. Those are not errors and should
    /// not be dressed as red failures. Throwing <see cref="OperationCanceledException"/> would give the
    /// right status but discard the specific message.
    /// </remarks>
    public bool IsCancellation { get; } = isCancellation;
}

/// <summary>Outcome of a job's install phase.</summary>
/// <param name="Message">User-facing result text, already localized by the factory.</param>
public sealed record JobResult(bool Ok, string? Message, string? InstalledPath = null);

/// <summary>
/// One unit of work handed to <see cref="DownloadQueue"/>.
/// </summary>
/// <remarks>
/// <para>The queue is a pure scheduler. It knows nothing about lua.tools versus Hubcap versus the depot
/// downloader, and nothing about <see cref="LuaInstaller"/>; all of that lives in the delegates, which are
/// always built by <see cref="ManifestJobFactory"/>. That is what lets the three formerly duplicated
/// download+install implementations — the Add page, the plugin add service and the HTTP bridge — share one
/// code path.</para>
///
/// <para>Deliberate consequence of holding delegates: a job is NOT serializable, so nothing resumes across
/// an app restart. Only the completed-history <see cref="DownloadHistoryRecord"/> persists.</para>
/// </remarks>
public sealed record DownloadJob(
    DownloadKind Kind,

    /// <summary>
    /// Identity for duplicate suppression: "manifest:730", "depot:730". Enqueuing a key that is already
    /// active returns the existing item instead of starting a second download. This is what replaces the
    /// per-page <c>if (IsDownloading) return;</c> gates and the HTTP bridge's duplicate-appid check.
    /// </summary>
    string DedupeKey,

    long AppId,
    string Title,
    string SubTitle,
    string? CoverPath,

    /// <summary>
    /// Fetch the bytes to a staged file. Runs on a background thread.
    /// </summary>
    /// <remarks>
    /// Receives the live <see cref="DownloadItem"/> so a multi-step job (a depot selection, which runs one
    /// child process per depot) can report which step it is on and which depots it has finished. Any
    /// observable property it touches must be set on the dispatcher.
    /// </remarks>
    Func<DownloadItem, IProgress<DownloadProgress>, CancellationToken, Task<DownloadedFile>> DownloadAsync,

    /// <summary>Consume the staged file (install into Steam). Runs serialized against other installs.</summary>
    Func<DownloadedFile, DownloadItem, CancellationToken, Task<JobResult>> InstallAsync,

    /// <summary>
    /// Optional gate between download and install; true proceeds, false discards the staged file. While
    /// this is awaited the item is <see cref="DownloadStatus.AwaitingConfirmation"/>. Nothing else waits on
    /// it: the queue has no concurrency cap, so an unanswered dialog cannot wedge anything.
    /// </summary>
    Func<DownloadedFile, DownloadItem, CancellationToken, Task<bool>>? ConfirmAsync = null,

    /// <summary>
    /// Fired on the dispatcher once the item reaches a terminal state: usage-badge refresh, install banner,
    /// plugin AddState mutation. Exceptions are swallowed so a bad continuation cannot kill the pump.
    /// </summary>
    Action<DownloadItem, JobResult?>? OnFinished = null,

    /// <summary>Target of the Downloads tab's "Review" button — navigate to the page that owns the
    /// pending confirmation.</summary>
    Action? OnReveal = null,

    /// <summary>
    /// Where this job writes its output, for jobs that produce a folder rather than a staged file (depot
    /// downloads). Null for everything else.
    /// </summary>
    /// <remarks>
    /// Exists so a cancel can offer to delete what was written. The path is otherwise captured only inside
    /// the job's own closure, leaving nothing outside able to name it.
    /// </remarks>
    string? OutputPath = null);
