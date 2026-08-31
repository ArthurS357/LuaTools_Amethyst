using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Hosting;

namespace LuaToolsGui.Services.Downloads;

/// <summary>
/// The app's single download scheduler: every manifest and depot download, from every entry point (the Add
/// page, the Depots page, the Steam store plugin and the HTTP bridge) runs through this one queue.
/// </summary>
/// <remarks>
/// <para><b>Threading.</b> All queue state — <see cref="Items"/>, <see cref="History"/>, every
/// <see cref="DownloadItem"/> property and every scheduling decision — lives on the WPF dispatcher. Only
/// the HTTP stream and the install call run on background threads. That removes the need for any locking
/// around the collections and makes bindings safe by construction. The dispatcher is touched once per state
/// transition plus a throttled progress tick, never once per network chunk.</para>
///
/// <para><b>No download cap.</b> Every queued item starts as soon as the pump sees it, so an item's index
/// in <see cref="Items"/> stops mattering once it is in flight — reordering is only meaningful between
/// Enqueue and the pump. The depot downloader has its own app-wide serialization (see
/// <see cref="DepotDownloaderService"/>), so parallel depot jobs still queue behind each other there.</para>
///
/// <para><b>Installs are always serialized</b> behind <c>_installGate</c>: <see cref="LuaInstaller"/>
/// writes into Steam's shared <c>depotcache</c> and <c>stplug-in</c>, which two concurrent installs would
/// race.</para>
///
/// <para><b>The queue enforces no policy of its own.</b> Integrity pinning, archive screening, staging and
/// quarantine all stay inside the services the job delegates call — the queue never fetches a byte or
/// touches Steam itself, so routing a download through it cannot bypass a check that used to run inline.
/// </para>
/// </remarks>
public sealed class DownloadQueue : IHostedService
{
    private readonly CacheService _cache;
    private readonly Dispatcher _dispatcher;

    /// <summary>Signals the pump that the schedule may have changed.</summary>
    private readonly SemaphoreSlim _kick = new(0);

    /// <summary>Serializes the install phase. See the remarks above.</summary>
    private readonly SemaphoreSlim _installGate = new(1, 1);

    /// <summary>How long a successful item lingers in the queue before clearing itself.</summary>
    private static readonly TimeSpan AutoDismissDelay = TimeSpan.FromSeconds(3);

    /// <summary>Cap on retained history rows, oldest dropped first.</summary>
    internal const int MaxHistory = 100;

    private readonly CancellationTokenSource _shutdown = new();
    private Task? _pump;

    public DownloadQueue(CacheService cache)
        : this(cache, Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher) { }

    /// <summary>
    /// Test seam: run the queue against a dispatcher the caller owns and can pump.
    /// </summary>
    /// <remarks>
    /// The dispatcher is captured ONCE, here, rather than read per call. Reading
    /// <c>Dispatcher.CurrentDispatcher</c> at use time would hand the background pump a brand-new
    /// dispatcher of its own that nobody ever runs, and the queue would silently stop scheduling.
    /// </remarks>
    internal DownloadQueue(CacheService cache, Dispatcher dispatcher)
    {
        _cache = cache;
        _dispatcher = dispatcher;
    }

    /// <summary>Active and recently finished items, in scheduling order. Index = priority.</summary>
    public ObservableCollection<DownloadItem> Items { get; } = [];

    /// <summary>Finished downloads from this and previous sessions, newest first.</summary>
    public ObservableCollection<DownloadHistoryEntry> History { get; } = [];

    /// <summary>Raised when <see cref="ActiveCount"/> changes.</summary>
    public event Action? StateChanged;

    public int ActiveCount => Items.Count(i => i.IsActive);

    // ── Lifecycle ────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct)
    {
        foreach (var r in _cache.GetDownloadHistory().OrderByDescending(r => r.CompletedAtMs))
            History.Add(new DownloadHistoryEntry(r));

        _pump = Task.Run(() => PumpAsync(_shutdown.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancel everything in flight and record it. Anything still active when the app closes is written to
    /// history as cancelled rather than silently vanishing.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        _shutdown.Cancel();

        List<DownloadItem> active = [];
        await _dispatcher.InvokeAsync(() => active = Items.Where(i => i.IsActive).ToList());

        foreach (var item in active)
        {
            try { item.Cts.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
        }

        await _dispatcher.InvokeAsync(() =>
        {
            foreach (var item in active)
            {
                if (!item.IsActive) continue;
                item.Message ??= Resources.Strings.Downloads_Err_Interrupted;
                item.Status = DownloadStatus.Cancelled;
                History.Insert(0, new DownloadHistoryEntry(
                    DownloadHistoryEntry.From(item, DownloadStatus.Cancelled)));
                item.SettleCompletion(null);
            }
            TrimAndPersistHistory();
        });

        if (_pump is not null)
        {
            try { await _pump.WaitAsync(TimeSpan.FromSeconds(3), ct); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // The pump is parked on _kick; shutdown proceeds regardless.
            }
        }
    }

    // ── Public API ───────────────────────────────────────────────────

    /// <summary>
    /// Add a job, or return the existing active item with the same <see cref="DownloadJob.DedupeKey"/>.
    /// This is the app-wide replacement for the per-page busy gates and for the HTTP bridge's
    /// duplicate-appid check.
    /// </summary>
    public DownloadItem Enqueue(DownloadJob job) => _dispatcher.Invoke(() =>
    {
        if (FindActiveCore(job.DedupeKey) is { } existing)
        {
            AppLog.Log($"DownloadQueue: already queued, reusing item {job.DedupeKey}");
            return existing;
        }

        var item = new DownloadItem(job);
        Items.Add(item);
        item.PropertyChanged += OnItemPropertyChanged;
        StateChanged?.Invoke();
        Kick();
        return item;
    });

    /// <summary>The in-flight item for a dedupe key, or null.</summary>
    public DownloadItem? FindActive(string dedupeKey) => _dispatcher.Invoke(() => FindActiveCore(dedupeKey));

    private DownloadItem? FindActiveCore(string dedupeKey) =>
        Items.FirstOrDefault(i => i.IsActive
            && string.Equals(i.Job.DedupeKey, dedupeKey, StringComparison.OrdinalIgnoreCase));

    public void Cancel(DownloadItem item) => _dispatcher.Invoke(() =>
    {
        if (!item.IsActive) return;

        // Nothing is running for a Queued item (it never entered RunItemAsync) OR for a Paused one (Pause
        // cancelled the token and RunItemAsync already returned at its PauseRequested check), so in both
        // cases no one is left to observe the token and settle the item — this has to do it. Missing the
        // Paused case leaves the row stuck: Cancel appears to do nothing, and because Paused counts as
        // active it offers no Remove either, so Resume would be the only way out.
        bool nothingRunning = item.Status is DownloadStatus.Queued or DownloadStatus.Paused;

        try { item.Cts.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
        if (nothingRunning)
        {
            item.PauseRequested = false; // settled, not parked: don't let a later path read it as a pause
            Finish(item, DownloadStatus.Cancelled, Resources.Strings.Downloads_Err_Cancelled, null);
        }
        Kick();
    });

    /// <summary>
    /// Pause a running depot download. Kills the child process but leaves its bytes on disk; Resume picks
    /// up from the first unfinished depot. Only depot jobs can pause (see
    /// <see cref="DownloadItem.CanPause"/>) — they are the only kind whose partial work survives the
    /// process dying.
    /// </summary>
    public void Pause(DownloadItem item) => _dispatcher.Invoke(() =>
    {
        if (!item.CanPause) return;
        item.PauseRequested = true;
        item.Status = DownloadStatus.Paused;
        item.BytesPerSecond = 0;
        item.Eta = null;
        try { item.Cts.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
        StateChanged?.Invoke();
    });

    /// <summary>Resume a paused depot download from the first depot it had not finished.</summary>
    public void Resume(DownloadItem item) => _dispatcher.Invoke(() =>
    {
        if (!item.CanResume) return;
        item.PauseRequested = false;
        item.ResetCts();
        item.Status = DownloadStatus.Queued;
        StateChanged?.Invoke();
        Kick();
    });

    /// <summary>Re-enqueue a failed or cancelled item's job as a fresh item at the tail.</summary>
    public DownloadItem Retry(DownloadItem item)
    {
        _dispatcher.Invoke(() => Remove(item));
        var fresh = Enqueue(item.Job);

        // A depot job's progress lives on disk, not in the item, so a retry must inherit what the failed
        // attempt finished. Without this the new item restarts at depot 1 and re-hashes tens of GB that
        // were already complete.
        if (item.Job.Kind is DownloadKind.Depot && !ReferenceEquals(fresh, item))
        {
            // Both sets are written from the child process's stdout thread, so they are read under the
            // same lock even here, where the failed run has certainly stopped. Relying on "it has stopped
            // by now" is the kind of timing assumption that only breaks once, in the field.
            foreach (long id in item.CompletedDepotsSnapshot()) fresh.CompletedDepots.Add(id);

            // The files are on disk under the SAME output folder, so without this a cancel of the retry
            // would offer to delete only what the retry re-created and orphan the rest — and with nothing
            // recorded yet it would suppress the prompt entirely.
            foreach (string f in item.CreatedFilesSnapshot()) fresh.CreatedFiles.Add(f);
        }
        return fresh;
    }

    /// <summary>Move a pending item up (-1) or down (+1). No-op once it has started.</summary>
    public void Move(DownloadItem item, int delta) => _dispatcher.Invoke(() =>
    {
        if (item.Status != DownloadStatus.Queued) return;
        int from = Items.IndexOf(item);
        if (from < 0) return;
        int to = Math.Clamp(from + delta, 0, Items.Count - 1);
        if (to == from) return;
        Items.Move(from, to);
        Kick();
    });

    /// <summary>Drop a finished item from the active list. History keeps the record.</summary>
    public void Remove(DownloadItem item) => _dispatcher.Invoke(() =>
    {
        if (item.IsActive) return;
        item.PropertyChanged -= OnItemPropertyChanged;
        Items.Remove(item);
        StateChanged?.Invoke();
    });

    public void ClearHistory() => _dispatcher.Invoke(() =>
    {
        History.Clear();
        TrimAndPersistHistory();
    });

    /// <summary>Drop one finished download from the history. Nothing on disk is touched.</summary>
    public void RemoveHistory(DownloadHistoryEntry entry) => _dispatcher.Invoke(() =>
    {
        if (History.Remove(entry)) TrimAndPersistHistory();
    });

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadItem.Status)) StateChanged?.Invoke();
    }

    // ── Scheduler ────────────────────────────────────────────────────

    private void Kick()
    {
        try { _kick.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _kick.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }

            try { await _dispatcher.InvokeAsync(StartEligible); }
            catch (Exception ex) { AppLog.Log($"DownloadQueue: pump cycle failed: {LogSanitizer.Sanitize(ex)}"); }
        }
    }

    /// <summary>Start every queued item. Dispatcher thread only.</summary>
    private void StartEligible()
    {
        // Snapshot first: RunItemAsync can settle an item synchronously and mutate Items re-entrantly.
        var ready = Items
            .Where(i => i.Status == DownloadStatus.Queued && !i.Cts.IsCancellationRequested)
            .ToList();

        foreach (var item in ready)
        {
            item.Status = DownloadStatus.Downloading;
            StateChanged?.Invoke();
            _ = RunItemAsync(item);
        }
    }

    private async Task RunItemAsync(DownloadItem item)
    {
        DownloadedFile? file = null;

        // The token source this run owns. Resume swaps in a fresh one, so an older run whose cancellation
        // has not propagated yet can still be mid-flight when the new one starts — and if it were allowed
        // to settle the item it would cancel the run that just replaced it. Comparing identity is what
        // makes a superseded run silent instead of destructive.
        var cts = item.Cts;
        var ct = cts.Token;
        bool Superseded() => !ReferenceEquals(item.Cts, cts);

        try
        {
            // ── 1. Download ──────────────────────────────────────────
            // Progress is time-throttled here rather than in the services: a report arrives per 80 KB chunk
            // (~25,000 for a 2 GB zip) and posting each one would flood the UI thread.
            long lastPostTicks = 0;
            var sink = new ProgressRelay<DownloadProgress>(p =>
            {
                long now = DateTime.UtcNow.Ticks;
                bool done = p.TotalBytes is > 0 && p.BytesRead >= p.TotalBytes.Value;
                if (!done && now - lastPostTicks < TimeSpan.TicksPerMillisecond * 100) return;
                lastPostTicks = now;
                _ = _dispatcher.InvokeAsync(() => item.ApplySample(p.BytesRead, p.TotalBytes),
                    DispatcherPriority.Background);
            });

            file = await Task.Run(() => item.Job.DownloadAsync(item, sink, ct), ct);

            // ── 2. Optional confirmation gate ────────────────────────
            if (item.Job.ConfirmAsync is { } confirm)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    item.Status = DownloadStatus.AwaitingConfirmation;
                    StateChanged?.Invoke();
                });

                bool proceed;
                try { proceed = await confirm(file, item, ct); }
                catch (OperationCanceledException) { proceed = false; }
                catch (Exception ex)
                {
                    AppLog.Log($"DownloadQueue: confirm gate threw, treating as declined: {LogSanitizer.Sanitize(ex)}");
                    proceed = false;
                }

                if (!proceed || ct.IsCancellationRequested)
                {
                    DeleteStaged(file.FilePath);
                    await _dispatcher.InvokeAsync(() => Finish(
                        item, DownloadStatus.Cancelled, Resources.Strings.Add_Status_Cancelled, null));
                    return;
                }
            }

            // ── 3. Install (always serialized) ───────────────────────
            await _dispatcher.InvokeAsync(() => item.Status = DownloadStatus.Installing);

            await _installGate.WaitAsync(CancellationToken.None);
            JobResult result;
            try
            {
                result = await Task.Run(() => item.Job.InstallAsync(file, item, ct), CancellationToken.None);
            }
            finally { _installGate.Release(); }

            await _dispatcher.InvokeAsync(() => Finish(
                item,
                result.Ok ? DownloadStatus.Completed : DownloadStatus.Failed,
                result.Message,
                result));
        }
        catch (OperationCanceledException)
        {
            // A pause cancels the same token a real cancel does. Leave the item parked in Paused and keep
            // its bytes: Resume re-enters this method and skips the depots already finished.
            if (item.PauseRequested || Superseded()) return;
            if (file is not null) DeleteStaged(file.FilePath);
            await _dispatcher.InvokeAsync(() => Finish(
                item, DownloadStatus.Cancelled, Resources.Strings.Downloads_Err_Cancelled, null));
        }
        catch (Exception ex)
        {
            if (file is not null) DeleteStaged(file.FilePath);
            if (Superseded()) return;
            AppLog.Log($"DownloadQueue: job failed ({item.Job.DedupeKey}): {LogSanitizer.Sanitize(ex)}");

            // Both of these carry a message meant for the user; anything else is unexpected, so it gets the
            // generic text and the detail goes to the log above.
            string message = ex is ApiException or DownloadAbortedException
                ? LogSanitizer.Sanitize(ex.Message)
                : Resources.Strings.Add_Err_Download;

            // Some aborts are not failures — they settle as Cancelled so they do not read as something
            // having broken.
            var status = ex is DownloadAbortedException { IsCancellation: true }
                ? DownloadStatus.Cancelled
                : DownloadStatus.Failed;

            await _dispatcher.InvokeAsync(() => Finish(item, status, message, null));
        }
        finally
        {
            Kick();
        }
    }

    /// <summary>Settle an item into a terminal state and record it. Dispatcher thread only.</summary>
    private void Finish(DownloadItem item, DownloadStatus status, string? message, JobResult? result)
    {
        if (!item.IsActive) return; // already settled (e.g. cancelled while queued)

        item.Message = message;
        item.Status = status;

        // Before the history row is built: From() copies the reveal path off the item, and settling the
        // result (which carries it) happens further down.
        item.RecordInstalledPath(result?.InstalledPath);

        History.Insert(0, new DownloadHistoryEntry(DownloadHistoryEntry.From(item, status)));
        TrimAndPersistHistory();

        // Per-job continuation only. There is deliberately no queue-wide "finished" event: each entry point
        // already reports its own outcome, and a global subscriber would double-notify all of them.
        //
        // Runs BEFORE Completion is settled, and the order matters. The silent-install path awaits
        // Completion and then reads the banner this continuation writes; settling first would let it read
        // an empty banner and report a successful install as a generic failure.
        try { item.Job.OnFinished?.Invoke(item, result); }
        catch (Exception ex) { AppLog.Log($"DownloadQueue: OnFinished threw: {LogSanitizer.Sanitize(ex)}"); }

        item.SettleCompletion(result);

        StateChanged?.Invoke();

        // A successful item has nothing left to act on — History keeps the record, so clear it from the
        // queue. Failed/Cancelled stay put: their message and Retry button are the only copy the user gets.
        if (status == DownloadStatus.Completed) _ = AutoDismissAsync(item);
    }

    /// <summary>Drop a completed item from the queue after a beat, so the user can read the outcome first.</summary>
    private async Task AutoDismissAsync(DownloadItem item)
    {
        try { await Task.Delay(AutoDismissDelay, _shutdown.Token); }
        catch (OperationCanceledException) { return; } // shutting down; leave the list alone

        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                // Re-check: the user may have dismissed it, or Retry may have swapped it out.
                if (item.Status == DownloadStatus.Completed && Items.Contains(item)) Remove(item);
            });
        }
        catch (Exception ex) { AppLog.Log($"DownloadQueue: auto-dismiss failed: {LogSanitizer.Sanitize(ex)}"); }
    }

    private void TrimAndPersistHistory()
    {
        while (History.Count > MaxHistory) History.RemoveAt(History.Count - 1);
        try { _cache.SaveDownloadHistory(History.Select(h => h.Record)); }
        catch (Exception ex) { AppLog.Log($"DownloadQueue: persisting history failed: {LogSanitizer.Sanitize(ex)}"); }
    }

    /// <summary>Best-effort delete of a staged file the install never consumed.</summary>
    private static void DeleteStaged(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}
