using System.IO;
using System.Threading;
using System.Windows.Threading;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// A private dispatcher thread for the queue to live on.
/// </summary>
/// <remarks>
/// <see cref="DownloadQueue"/> puts every scheduling decision on the WPF dispatcher, which is what removes
/// the need for locks around its collections. Its production constructor takes
/// <c>Application.Current.Dispatcher</c>; the internal one takes any dispatcher, which is what lets these
/// tests exercise the REAL pump — the scheduler, the throttled progress marshalling, the install gate —
/// rather than a re-implementation of it. No <c>Application</c> is needed: nothing here loads XAML.
/// </remarks>
public sealed class QueueHost : IDisposable
{
    private readonly Thread _thread;

    public QueueHost()
    {
        var ready = new ManualResetEventSlim();
        Dispatcher? captured = null;

        _thread = new Thread(() =>
        {
            captured = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(30));
        Dispatcher = captured ?? throw new InvalidOperationException("queue host never started");
    }

    public Dispatcher Dispatcher { get; }

    /// <summary>Read queue state from the test thread without racing the pump.</summary>
    public T On<T>(Func<T> work) => Dispatcher.Invoke(work);

    public void Dispose() => Dispatcher.InvokeShutdown();
}

public sealed class DownloadQueueTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly QueueHost _host = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luatools_queue_" + Guid.NewGuid().ToString("N"));

    private CacheService NewCache() => new(_dir);

    private async Task<DownloadQueue> StartedAsync(CacheService? cache = null)
    {
        var queue = new DownloadQueue(cache ?? NewCache(), _host.Dispatcher);
        await queue.StartAsync(CancellationToken.None);
        return queue;
    }

    /// <summary>A staged file on disk, so the queue's cleanup paths act on something real.</summary>
    private DownloadedFile Staged(string name)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "staged");
        return new DownloadedFile(path, name);
    }

    private DownloadJob Job(
        string key,
        Func<DownloadItem, IProgress<DownloadProgress>, CancellationToken, Task<DownloadedFile>>? download = null,
        Func<DownloadedFile, DownloadItem, CancellationToken, Task<JobResult>>? install = null,
        Func<DownloadedFile, DownloadItem, CancellationToken, Task<bool>>? confirm = null,
        DownloadKind kind = DownloadKind.Manifest,
        long appId = 730,
        string? outputPath = null) =>
        new(kind, key, appId, "Test Game", "Test Source", null,
            download ?? ((_, _, _) => Task.FromResult(Staged(key.Replace(':', '_') + ".zip"))),
            install ?? ((_, _, _) => Task.FromResult(new JobResult(true, "installed", null))),
            confirm,
            OutputPath: outputPath);

    public void Dispose()
    {
        _host.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ── Scheduling ───────────────────────────────────────────────────

    [Fact]
    public async Task A_queued_job_runs_and_settles_as_completed()
    {
        var queue = await StartedAsync();

        var item = queue.Enqueue(Job("manifest:730"));
        var result = await item.Completion.WaitAsync(Timeout);

        result!.Ok.Should().BeTrue();
        item.Status.Should().Be(DownloadStatus.Completed);
        item.Message.Should().Be("installed");
    }

    [Fact]
    public async Task Enqueuing_a_key_that_is_already_active_joins_it_instead_of_downloading_twice()
    {
        var queue = await StartedAsync();
        var release = new TaskCompletionSource();
        int downloads = 0;

        var job = Job("manifest:730", download: async (_, _, _) =>
        {
            Interlocked.Increment(ref downloads);
            await release.Task;
            return Staged("dedupe.zip");
        });

        var first = queue.Enqueue(job);
        var second = queue.Enqueue(job);

        // This is what replaces the per-page "if (IsDownloading) return;" gates, and unlike them it holds
        // across pages: the second caller gets the SAME item and can await the same outcome.
        second.Should().BeSameAs(first);

        release.SetResult();
        await first.Completion.WaitAsync(Timeout);
        downloads.Should().Be(1);
    }

    [Fact]
    public async Task Dedupe_is_case_insensitive_but_still_distinguishes_different_games()
    {
        var queue = await StartedAsync();
        var release = new TaskCompletionSource();

        var a = queue.Enqueue(Job("Manifest:730", download: async (_, _, _) =>
            { await release.Task; return Staged("a.zip"); }));
        var sameKeyOtherCase = queue.Enqueue(Job("manifest:730"));
        var other = queue.Enqueue(Job("manifest:440", appId: 440));

        sameKeyOtherCase.Should().BeSameAs(a);
        other.Should().NotBeSameAs(a);

        release.SetResult();
        await Task.WhenAll(a.Completion, other.Completion).WaitAsync(Timeout);
    }

    [Fact]
    public async Task A_failing_job_settles_as_failed_without_blocking_the_rest_of_the_queue()
    {
        var queue = await StartedAsync();

        var bad = queue.Enqueue(Job("manifest:1",
            download: (_, _, _) => throw new DownloadAbortedException("source refused the key")));
        var good = queue.Enqueue(Job("manifest:2", appId: 2));

        await bad.Completion.WaitAsync(Timeout);
        var ok = await good.Completion.WaitAsync(Timeout);

        bad.Status.Should().Be(DownloadStatus.Failed);
        bad.Message.Should().Be("source refused the key"); // the user-facing reason survives, not a generic
        ok!.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task An_unexpected_exception_is_reported_generically_rather_than_leaking_its_text()
    {
        var queue = await StartedAsync();

        // Only ApiException and DownloadAbortedException carry text meant for a user. Anything else could
        // be a raw HTTP body or a path, so it goes to the log and the row says something safe.
        var item = queue.Enqueue(Job("manifest:730",
            download: (_, _, _) => throw new InvalidOperationException("Bearer eyJhbGciOi.SECRET")));

        await item.Completion.WaitAsync(Timeout);

        item.Status.Should().Be(DownloadStatus.Failed);
        item.Message.Should().NotContain("SECRET");
        item.Message.Should().Be(LuaToolsGui.Resources.Strings.Add_Err_Download);
    }

    [Fact]
    public async Task The_jobs_continuation_has_already_run_by_the_time_completion_resolves()
    {
        var queue = await StartedAsync();
        bool continuationRan = false;

        var job = Job("manifest:730") with { OnFinished = (_, _) => continuationRan = true };
        var item = queue.Enqueue(job);

        await item.Completion.WaitAsync(Timeout);

        // The silent-install path awaits Completion and then reads the banner OnFinished writes. Settling
        // first would let it read an empty banner and report a successful install as a generic failure.
        continuationRan.Should().BeTrue();
    }

    [Fact]
    public async Task A_continuation_that_throws_cannot_kill_the_pump()
    {
        var queue = await StartedAsync();

        var bad = queue.Enqueue(Job("manifest:1", appId: 1)
            with
        { OnFinished = (_, _) => throw new InvalidOperationException("bad subscriber") });
        await bad.Completion.WaitAsync(Timeout);
        bad.Status.Should().Be(DownloadStatus.Completed);

        // The next job still runs: a faulty continuation is one page's problem, not the queue's.
        (await queue.Enqueue(Job("manifest:2", appId: 2)).Completion.WaitAsync(Timeout))!.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task An_install_that_reports_failure_settles_as_failed_and_keeps_its_message()
    {
        var queue = await StartedAsync();

        var item = queue.Enqueue(Job("manifest:730",
            install: (_, _, _) => Task.FromResult(new JobResult(false, "Steam is running"))));

        await item.Completion.WaitAsync(Timeout);

        item.Status.Should().Be(DownloadStatus.Failed);
        item.Message.Should().Be("Steam is running");
    }

    [Fact]
    public async Task Installs_are_serialized_even_though_downloads_are_not()
    {
        var queue = await StartedAsync();
        int concurrent = 0, peak = 0;

        Func<DownloadedFile, DownloadItem, CancellationToken, Task<JobResult>> install = async (_, _, _) =>
        {
            int now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, now);
            await Task.Delay(40);
            Interlocked.Decrement(ref concurrent);
            return new JobResult(true, "installed");
        };

        var items = Enumerable.Range(1, 4)
            .Select(i => queue.Enqueue(Job($"manifest:{i}", install: install, appId: i)))
            .ToList();

        await Task.WhenAll(items.Select(i => i.Completion)).WaitAsync(Timeout);

        // LuaInstaller writes into Steam's shared stplug-in/depotcache, which two concurrent installs race.
        peak.Should().Be(1);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen = Volatile.Read(ref target);
        while (value > seen)
        {
            int prior = Interlocked.CompareExchange(ref target, value, seen);
            if (prior == seen) return;
            seen = prior;
        }
    }

    // ── Confirmation gate ────────────────────────────────────────────

    [Fact]
    public async Task A_declined_confirmation_cancels_the_item_and_deletes_the_staged_file()
    {
        var queue = await StartedAsync();
        string? stagedPath = null;
        bool installed = false;

        var item = queue.Enqueue(Job("manifest:730",
            download: (_, _, _) =>
            {
                var f = Staged("declined.zip");
                stagedPath = f.FilePath;
                return Task.FromResult(f);
            },
            install: (_, _, _) => { installed = true; return Task.FromResult(new JobResult(true, "installed")); },
            confirm: (_, _, _) => Task.FromResult(false)));

        await item.Completion.WaitAsync(Timeout);

        item.Status.Should().Be(DownloadStatus.Cancelled);
        installed.Should().BeFalse();
        File.Exists(stagedPath!).Should().BeFalse(); // nothing left behind in staging
    }

    [Fact]
    public async Task A_gate_that_throws_is_treated_as_declined_rather_than_installing_anyway()
    {
        var queue = await StartedAsync();
        bool installed = false;

        var item = queue.Enqueue(Job("manifest:730",
            install: (_, _, _) => { installed = true; return Task.FromResult(new JobResult(true, "installed")); },
            confirm: (_, _, _) => throw new InvalidOperationException("overlay blew up")));

        await item.Completion.WaitAsync(Timeout);

        // Fail closed: a confirmation that could not be obtained is not a confirmation.
        item.Status.Should().Be(DownloadStatus.Cancelled);
        installed.Should().BeFalse();
    }

    [Fact]
    public async Task An_item_waiting_on_a_confirmation_reports_that_it_needs_the_user()
    {
        var queue = await StartedAsync();
        var asked = new TaskCompletionSource();
        var answer = new TaskCompletionSource<bool>();

        var item = queue.Enqueue(Job("manifest:730", confirm: (_, _, _) =>
        {
            asked.TrySetResult();
            return answer.Task;
        }));

        await asked.Task.WaitAsync(Timeout);
        await WaitForAsync(() => item.Status is DownloadStatus.AwaitingConfirmation);

        item.NeedsAction.Should().BeTrue();
        item.IsActive.Should().BeTrue();

        answer.SetResult(true);
        (await item.Completion.WaitAsync(Timeout))!.Ok.Should().BeTrue();
    }

    // ── Cancel, pause, resume, retry, reorder ────────────────────────

    [Fact]
    public async Task Cancelling_a_running_item_settles_it_as_cancelled()
    {
        var queue = await StartedAsync();
        var started = new TaskCompletionSource();

        var item = queue.Enqueue(Job("manifest:730", download: async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout, ct);
            return Staged("never.zip");
        }));

        await started.Task.WaitAsync(Timeout);
        queue.Cancel(item);
        await item.Completion.WaitAsync(Timeout);

        item.Status.Should().Be(DownloadStatus.Cancelled);
        item.CanRetry.Should().BeTrue();
    }

    [Fact]
    public async Task A_paused_depot_download_resumes_without_re_fetching_what_it_finished()
    {
        var queue = await StartedAsync();
        var runStarted = new TaskCompletionSource();
        int runs = 0;
        List<long> secondRunSawAsDone = [];

        var item = queue.Enqueue(Job("depot:730", kind: DownloadKind.Depot, outputPath: _dir,
            download: async (live, _, ct) =>
            {
                int run = Interlocked.Increment(ref runs);
                if (run == 1)
                {
                    lock (live.CompletedDepots) live.CompletedDepots.Add(1001); // one depot landed
                    runStarted.TrySetResult();
                    await Task.Delay(Timeout, ct); // ...then the pause kills it
                    return Staged("never.zip");
                }
                lock (live.CompletedDepots) secondRunSawAsDone = [.. live.CompletedDepots];
                return Staged("resumed.zip");
            }));

        await runStarted.Task.WaitAsync(Timeout);
        await WaitForAsync(() => item.CanPause);

        queue.Pause(item);
        await WaitForAsync(() => item.Status is DownloadStatus.Paused);
        item.CanResume.Should().BeTrue();
        item.IsActive.Should().BeTrue();   // a pause is not an outcome; the row keeps its Cancel button
        item.Completion.IsCompleted.Should().BeFalse();

        queue.Resume(item);
        (await item.Completion.WaitAsync(Timeout))!.Ok.Should().BeTrue();

        runs.Should().Be(2);
        secondRunSawAsDone.Should().Contain(1001L); // the resumed run skips what the first one finished
    }

    [Fact]
    public async Task Only_depot_downloads_can_be_paused()
    {
        var queue = await StartedAsync();
        var started = new TaskCompletionSource();

        var manifest = queue.Enqueue(Job("manifest:730", download: async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout, ct);
            return Staged("never.zip");
        }));

        await started.Task.WaitAsync(Timeout);
        await WaitForAsync(() => manifest.Status is DownloadStatus.Downloading);

        // A manifest download has nothing on disk to come back to, so pausing it would silently be a cancel.
        manifest.CanPause.Should().BeFalse();
        queue.Pause(manifest);
        manifest.Status.Should().Be(DownloadStatus.Downloading);

        queue.Cancel(manifest);
        await manifest.Completion.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Cancelling_a_paused_item_settles_it_rather_than_leaving_the_row_stuck()
    {
        var queue = await StartedAsync();
        var started = new TaskCompletionSource();

        var item = queue.Enqueue(Job("depot:730", kind: DownloadKind.Depot, outputPath: _dir,
            download: async (_, _, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout, ct);
                return Staged("never.zip");
            }));

        await started.Task.WaitAsync(Timeout);
        await WaitForAsync(() => item.CanPause);
        queue.Pause(item);
        await WaitForAsync(() => item.Status is DownloadStatus.Paused);

        // Nothing is running for a paused item, so no one else is left to observe the token — Cancel has to
        // settle it itself. Otherwise the row offers neither Remove (it is "active") nor a way out.
        queue.Cancel(item);
        await item.Completion.WaitAsync(Timeout);

        item.Status.Should().Be(DownloadStatus.Cancelled);
        item.CanRemove.Should().BeTrue();
    }

    [Fact]
    public async Task Retrying_a_depot_download_inherits_the_depots_the_failed_attempt_finished()
    {
        var queue = await StartedAsync();

        var failing = queue.Enqueue(Job("depot:730", kind: DownloadKind.Depot, outputPath: _dir,
            download: (live, _, _) =>
            {
                lock (live.CompletedDepots) live.CompletedDepots.Add(2002);
                live.CreatedFiles.Add(Path.Combine(_dir, "part.bin"));
                throw new DownloadAbortedException("depot 2003 failed");
            }));

        await failing.Completion.WaitAsync(Timeout);
        failing.Status.Should().Be(DownloadStatus.Failed);

        var retried = queue.Retry(failing);
        await retried.Completion.WaitAsync(Timeout);

        // Without this the retry restarts at depot 1 and re-hashes tens of GB that were already done.
        retried.Should().NotBeSameAs(failing);
        retried.CompletedDepots.Should().Contain(2002L);
        retried.CreatedFilesSnapshot().Should().ContainSingle();
    }

    [Fact]
    public void Queued_items_can_be_reordered_and_the_move_is_clamped()
    {
        // Deliberately NOT started: with no pump running, every item stays Queued, which is the only window
        // in which order means anything. Blocking the dispatcher to achieve the same thing would deadlock —
        // Enqueue marshals onto it.
        var queue = new DownloadQueue(NewCache(), _host.Dispatcher);

        var first = queue.Enqueue(Job("manifest:1", appId: 1));
        var second = queue.Enqueue(Job("manifest:2", appId: 2));

        first.CanReorder.Should().BeTrue();

        queue.Move(second, -1);
        _host.On(() => queue.Items.IndexOf(second)).Should().Be(0);

        // Clamped, not wrapped: moving the head up again is a no-op rather than sending it to the tail.
        queue.Move(second, -1);
        _host.On(() => queue.Items.IndexOf(second)).Should().Be(0);

        queue.Move(second, +1);
        _host.On(() => queue.Items.IndexOf(second)).Should().Be(1);
    }

    [Fact]
    public async Task A_finished_item_can_no_longer_be_reordered()
    {
        var queue = await StartedAsync();
        var item = queue.Enqueue(Job("manifest:730"));
        await item.Completion.WaitAsync(Timeout);

        item.CanReorder.Should().BeFalse();
    }

    // ── Progress ─────────────────────────────────────────────────────

    [Fact]
    public async Task Byte_progress_from_a_job_reaches_the_item()
    {
        var queue = await StartedAsync();

        var item = queue.Enqueue(Job("manifest:730", download: (_, progress, _) =>
        {
            // The last report always lands, however tightly it follows the previous one — the queue's
            // 100ms throttle exempts the completing tick so the bar cannot stall short of 100%.
            progress.Report(new DownloadProgress(0, 2048));
            progress.Report(new DownloadProgress(2048, 2048));
            return Task.FromResult(Staged("progress.zip"));
        }));

        await item.Completion.WaitAsync(Timeout);
        await WaitForAsync(() => item.BytesRead == 2048);

        item.TotalBytes.Should().Be(2048);
        item.Percent.Should().Be(100);
        item.IsIndeterminate.Should().BeFalse();
    }

    // ── History ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_finished_download_is_recorded_in_history_and_persisted()
    {
        var cache = NewCache();
        var queue = await StartedAsync(cache);

        var item = queue.Enqueue(Job("manifest:730"));
        await item.Completion.WaitAsync(Timeout);

        var history = _host.On(() => queue.History.ToList());
        history.Should().ContainSingle();
        history[0].Title.Should().Be("Test Game");
        history[0].Status.Should().Be(DownloadStatus.Completed);

        // A second CacheService over the same directory is what a restart looks like.
        new CacheService(_dir).GetDownloadHistory().Should().ContainSingle()
            .Which.Title.Should().Be("Test Game");
    }

    [Fact]
    public async Task History_survives_a_restart_and_reloads_newest_first()
    {
        var cache = NewCache();
        var first = await StartedAsync(cache);
        await first.Enqueue(Job("manifest:1", appId: 1)).Completion.WaitAsync(Timeout);
        await first.Enqueue(Job("manifest:2", appId: 2)).Completion.WaitAsync(Timeout);

        var reopened = await StartedAsync(new CacheService(_dir));

        var history = _host.On(() => reopened.History.ToList());
        history.Should().HaveCount(2);
        history[0].Record.CompletedAtMs.Should().BeGreaterThanOrEqualTo(history[1].Record.CompletedAtMs);
    }

    [Fact]
    public async Task Clearing_history_one_row_at_a_time_and_wholesale_both_reach_disk()
    {
        var cache = NewCache();
        var queue = await StartedAsync(cache);
        await queue.Enqueue(Job("manifest:1", appId: 1)).Completion.WaitAsync(Timeout);
        await queue.Enqueue(Job("manifest:2", appId: 2)).Completion.WaitAsync(Timeout);

        var one = _host.On(() => queue.History[0]);
        queue.RemoveHistory(one);
        new CacheService(_dir).GetDownloadHistory().Should().ContainSingle();

        queue.ClearHistory();
        _host.On(() => queue.History.Count).Should().Be(0);
        new CacheService(_dir).GetDownloadHistory().Should().BeEmpty();
    }

    [Fact]
    public async Task Anything_still_running_when_the_app_closes_is_recorded_rather_than_vanishing()
    {
        var cache = NewCache();
        var queue = await StartedAsync(cache);
        var started = new TaskCompletionSource();

        var item = queue.Enqueue(Job("manifest:730", download: async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout, ct);
            return Staged("never.zip");
        }));

        await started.Task.WaitAsync(Timeout);
        await queue.StopAsync(CancellationToken.None);

        item.Status.Should().Be(DownloadStatus.Cancelled);
        item.Message.Should().Be(LuaToolsGui.Resources.Strings.Downloads_Err_Interrupted);
        new CacheService(_dir).GetDownloadHistory().Should().ContainSingle()
            .Which.Status.Should().Be(nameof(DownloadStatus.Cancelled));
    }

    /// <summary>Poll a dispatcher-owned condition. The pump settles state asynchronously by design.</summary>
    private async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_host.On(condition)) return;
            await Task.Delay(15);
        }
        _host.On(condition).Should().BeTrue("the queue should have reached the expected state");
    }
}
