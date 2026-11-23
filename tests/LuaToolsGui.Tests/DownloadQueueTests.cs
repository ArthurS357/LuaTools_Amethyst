using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using AwesomeAssertions;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Downloads;
using LuaToolsGui.ViewModels;
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

    [Fact]
    public async Task Stopping_from_a_blocked_dispatcher_thread_does_not_deadlock()
    {
        // App.OnExit runs on the dispatcher thread and blocks it on the host's StopAsync. StopAsync used to
        // post back to that same thread, which never ran: the tray "Exit" left the process alive forever.
        var queue = await StartedAsync();
        var started = new TaskCompletionSource();
        var item = queue.Enqueue(Job("manifest:730", download: async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout, ct);
            return Staged("never.zip");
        }));
        await started.Task.WaitAsync(Timeout);

        bool stopped = _host.On(() => queue.StopAsync(CancellationToken.None).Wait(Timeout));

        stopped.Should().BeTrue();
        item.Status.Should().Be(DownloadStatus.Cancelled);
    }

    // ── Fixes / Denuvo integration (#26 — Lote K3) ───────────────────

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            handler(request, ct);
    }

    private static AuthService CreateSignedInAuth(string authFile)
    {
        var auth = new AuthService(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))), authFile);
        typeof(AuthService).GetField("_accessToken", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(auth, "test-access-token");
        typeof(AuthService).GetField("_refreshToken", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(auth, "test-refresh-token");
        typeof(AuthService).GetField("_expiresAt", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(auth, DateTimeOffset.UtcNow.AddHours(1));
        return auth;
    }

    private static byte[] CreateZipBytes(string entryName, string content)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }
        return ms.ToArray();
    }

    private (FixesViewModel Vm, LuaToolsApiClient Api, SteamService Steam, string SteamRoot, string GameInstallDir)
        CreateFixesVm(
            DownloadQueue queue,
            AuthService auth,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> apiRespond,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> r2Respond,
            long installedAppId = 12345)
    {
        string steamRoot = Path.Combine(_dir, "steam_" + Guid.NewGuid().ToString("N")[..8]);
        string stPlugIn = Path.Combine(steamRoot, "config", "stplug-in");
        string steamApps = Path.Combine(steamRoot, "steamapps");
        string gameInstallDir = Path.Combine(steamApps, "common", "TestDenuvoGame");
        Directory.CreateDirectory(stPlugIn);
        Directory.CreateDirectory(gameInstallDir);
        File.WriteAllText(Path.Combine(steamApps, $"appmanifest_{installedAppId}.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\"" + installedAppId + "\"\n\t\"name\"\t\"Test Denuvo Game\"\n\t\"installdir\"\t\"TestDenuvoGame\"\n\t\"StateFlags\"\t\"4\"\n}");

        var settings = new SettingsService(_dir) { SteamPathOverride = steamRoot };
        var cache = new CacheService(Path.Combine(_dir, "cache_" + Guid.NewGuid().ToString("N")[..8]));
        var steam = new SteamService(settings);
        var vault = new LuaVault(() => steam.StPlugInDir, Path.Combine(_dir, "vault_" + Guid.NewGuid().ToString("N")[..8]));
        var installer = new LuaInstaller(steam, settings, cache, vault);

        var api = new LuaToolsApiClient(auth, null!, null!, null!, null!)
        {
            CacheDirectory = Path.Combine(_dir, "apicache_" + Guid.NewGuid().ToString("N")[..8]),
        };
        typeof(LuaToolsApiClient).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(api, new HttpClient(new DelegateHandler(apiRespond)) { BaseAddress = new Uri("https://lua.tools") });
        typeof(LuaToolsApiClient).GetField("FixDownloadHttp", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(api, new HttpClient(new DelegateHandler(r2Respond)));

        var vm = new FixesViewModel(
            api, auth, installer, steam,
            new SteamLibraryService(steam), new CoverCache(), new ToastService(), settings,
            new DenuvoFixService(), queue)
        {
            RestartSteamOverride = () => false,
        };
        return (vm, api, steam, steamRoot, gameInstallDir);
    }

    private static FixItemVm MakeFixVm(string id, bool hasManifest = true, bool hasFix = true, bool gameInstalled = true) =>
        new(new DenuvoFix
        {
            Id = id,
            Title = $"Build {id}",
            HasManifest = hasManifest,
            HasFix = hasFix,
            ManifestFilename = $"{id}_manifest.zip",
            FixFilename = $"{id}_fix.zip",
            CreatedAt = "2026-11-20T00:00:00Z",
        })
        {
            GameInstalled = gameInstalled,
            IsApplied = false,
        };

    [Fact]
    public async Task Enqueuing_manifest_and_fix_slots_or_multiple_fixes_for_same_game_never_collides()
    {
        // Deliberately not started so all enqueued items stay Queued and we can assert distinct queue rows
        // and dedupe keys without racing completion.
        var queue = new DownloadQueue(NewCache(), _host.Dispatcher);
        var auth = CreateSignedInAuth(Path.Combine(_dir, "auth_nocollide.dat"));
        var (vm, _, _, _, _) = CreateFixesVm(
            queue, auth,
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var fix1 = MakeFixVm("fix-alpha");
        var fix2 = MakeFixVm("fix-beta");
        var nullAppGame = new FixGameCardVm(new DenuvoGameListing { AppId = "", Name = "AppIdLess Game", FixCount = 2 });

        var item1Manifest = await vm.EnqueueFixDownloadAsync(fix1, "manifest", nullAppGame);
        var item1Fix = await vm.EnqueueFixDownloadAsync(fix1, "fix", nullAppGame);
        var item2Fix = await vm.EnqueueFixDownloadAsync(fix2, "fix", nullAppGame);
        var item1ManifestDuplicate = await vm.EnqueueFixDownloadAsync(fix1, "manifest", nullAppGame);

        item1Manifest.Should().NotBeNull();
        item1Fix.Should().NotBeNull();
        item2Fix.Should().NotBeNull();

        // Keyed by (fix.Id, slot): manifest vs fix of the same fix, and two fixes of the same game (even with
        // empty/null SteamAppId -> AppId 0), produce distinct items and DownloadKinds.
        item1Manifest!.Job.Kind.Should().Be(DownloadKind.FixManifest);
        item1Manifest.Job.DedupeKey.Should().Be("fix:fix-alpha:manifest");
        item1Manifest.Job.AppId.Should().Be(0);

        item1Fix!.Job.Kind.Should().Be(DownloadKind.DenuvoFix);
        item1Fix.Job.DedupeKey.Should().Be("fix:fix-alpha:fix");

        item2Fix!.Job.Kind.Should().Be(DownloadKind.DenuvoFix);
        item2Fix.Job.DedupeKey.Should().Be("fix:fix-beta:fix");

        item1Manifest.Should().NotBeSameAs(item1Fix);
        item1Fix.Should().NotBeSameAs(item2Fix);
        item1ManifestDuplicate.Should().BeSameAs(item1Manifest, "same (fix.Id, slot) deduplicates to the active item");
        _host.On(() => queue.Items.Count).Should().Be(3);

        fix1.IsManifestDownloading.Should().BeTrue();
        fix1.IsFixDownloading.Should().BeTrue();
        fix1.CanDownloadManifest.Should().BeFalse();
        fix1.CanDownloadFix.Should().BeFalse();
        fix2.IsFixDownloading.Should().BeTrue();
        fix2.IsManifestDownloading.Should().BeFalse();
        fix2.CanDownloadManifest.Should().BeTrue();
    }

    [Fact]
    public async Task Guest_click_prompts_sign_in_before_enqueue_and_signed_url_is_granted_inside_worker()
    {
        Directory.CreateDirectory(_dir);
        int grantCalls = 0;
        int r2Calls = 0;
        byte[] manifestZip = CreateZipBytes("12345.lua", "addappid(12345)\n");

        Task<HttpResponseMessage> ApiRespond(HttpRequestMessage req, CancellationToken _)
        {
            if (req.RequestUri!.PathAndQuery.StartsWith("/api/denuvo/download", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref grantCalls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"url\":\"https://r2.example.com/signed/12345.zip\",\"filename\":\"12345.zip\"}"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        Task<HttpResponseMessage> R2Respond(HttpRequestMessage req, CancellationToken _)
        {
            Interlocked.Increment(ref r2Calls);
            var content = new ByteArrayContent(manifestZip);
            content.Headers.ContentLength = manifestZip.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        // 1. Guest user: prompts sign-in on click and does NOT enqueue when still guest.
        var unstartedQueue = new DownloadQueue(NewCache(), _host.Dispatcher);
        var guestAuth = new AuthService(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))), Path.Combine(_dir, "guest_empty.dat"));
        int signInPromptCount = 0;
        var (guestVm, _, _, _, _) = CreateFixesVm(unstartedQueue, guestAuth, ApiRespond, R2Respond);
        guestVm.RequestSignIn = () =>
        {
            signInPromptCount++;
            return Task.CompletedTask; // user dismissed sign-in modal
        };

        var fix = MakeFixVm("fix-signin");
        var game = new FixGameCardVm(new DenuvoGameListing { AppId = "12345", Name = "Test Denuvo Game", FixCount = 1 });
        var guestItem = await guestVm.EnqueueFixDownloadAsync(fix, "manifest", game);

        signInPromptCount.Should().Be(1);
        guestItem.Should().BeNull();
        _host.On(() => unstartedQueue.Items.Count).Should().Be(0);
        grantCalls.Should().Be(0);

        // 2. Signed-in user: Enqueue happens immediately, but /api/denuvo/download is NOT called until the queue pump runs the worker.
        var signedInAuth = CreateSignedInAuth(Path.Combine(_dir, "signed_in.dat"));
        var (signedVm, _, _, steamRoot, _) = CreateFixesVm(unstartedQueue, signedInAuth, ApiRespond, R2Respond);

        var queuedItem = await signedVm.EnqueueFixDownloadAsync(fix, "manifest", game);
        queuedItem.Should().NotBeNull();
        grantCalls.Should().Be(0, "the signed URL grant must run inside the queue worker, never before Enqueue");
        r2Calls.Should().Be(0);

        // Now start the queue pump: the worker runs, requests the signed URL, downloads the bytes, and installs the manifest.
        await unstartedQueue.StartAsync(CancellationToken.None);
        var result = await queuedItem!.Completion.WaitAsync(Timeout);

        result!.Ok.Should().BeTrue();
        grantCalls.Should().Be(1);
        r2Calls.Should().Be(1);
        File.Exists(Path.Combine(steamRoot, "config", "stplug-in", "12345.lua")).Should().BeTrue();
    }

    [Fact]
    public async Task Byte_progress_and_cross_cancellation_stay_synchronized_between_queue_and_fix_card()
    {
        Directory.CreateDirectory(_dir);
        var queue = await StartedAsync();
        var auth = CreateSignedInAuth(Path.Combine(_dir, "auth_progress.dat"));

        var streamGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstChunkRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] payload = new byte[4096];
        Array.Fill(payload, (byte)'A');

        Task<HttpResponseMessage> ApiRespond(HttpRequestMessage req, CancellationToken _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"url\":\"https://r2.example.com/signed/fix.zip\",\"filename\":\"fix.zip\"}"),
            });

        Task<HttpResponseMessage> R2Respond(HttpRequestMessage req, CancellationToken ct)
        {
            var stream = new GatedChunkStream(payload, firstChunkSize: 2048, firstChunkRead, streamGate.Task);
            var content = new StreamContent(stream);
            content.Headers.ContentLength = payload.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        var (vm, _, _, _, _) = CreateFixesVm(queue, auth, ApiRespond, R2Respond);
        var fix = MakeFixVm("fix-sync");
        var game = new FixGameCardVm(new DenuvoGameListing { AppId = "12345", Name = "Test Denuvo Game", FixCount = 1 });

        // Enqueue the "fix" slot and wait until the first 2048 of 4096 bytes have been read.
        var item = await vm.EnqueueFixDownloadAsync(fix, "fix", game);
        item.Should().NotBeNull();
        await firstChunkRead.Task.WaitAsync(Timeout);
        await WaitForAsync(() => item!.BytesRead >= 2048 && fix.DownloadProgress >= 50);

        item!.TotalBytes.Should().Be(4096);
        item.BytesRead.Should().Be(2048);
        fix.IsDownloading.Should().BeTrue();
        fix.CanCancelDownload.Should().BeTrue();
        fix.DownloadProgress.Should().BeApproximately(50, 1.0);

        // Simulate navigating away / recreating the FixItemVm list and calling SyncFixesWithQueue:
        var reloadedFix = MakeFixVm("fix-sync");
        typeof(FixesViewModel).GetField("_allFixes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, new List<FixItemVm> { reloadedFix });
        vm.SyncFixesWithQueue();

        reloadedFix.IsDownloading.Should().BeTrue("SyncFixesWithQueue re-attaches active queue jobs after navigation");
        reloadedFix.DownloadProgress.Should().BeApproximately(50, 1.0);
        reloadedFix.CanCancelDownload.Should().BeTrue();

        // Cancel from the card's CancelDownloadCommand -> cancels the DownloadItem in the queue.
        vm.CancelDownloadCommand.Execute(reloadedFix);
        await item.Completion.WaitAsync(Timeout);
        await WaitForAsync(() => !reloadedFix.IsDownloading);

        item.Status.Should().Be(DownloadStatus.Cancelled);
        reloadedFix.IsDownloading.Should().BeFalse();
        reloadedFix.CanCancelDownload.Should().BeFalse();

        // Now enqueue the "manifest" slot and cancel from the DownloadQueue side (the Downloads tab).
        var item2 = await vm.EnqueueFixDownloadAsync(reloadedFix, "manifest", game);
        item2.Should().NotBeNull();
        await WaitForAsync(() => reloadedFix.IsManifestDownloading);

        queue.Cancel(item2!);
        await item2!.Completion.WaitAsync(Timeout);
        await WaitForAsync(() => !reloadedFix.IsDownloading);

        item2.Status.Should().Be(DownloadStatus.Cancelled);
        reloadedFix.IsManifestDownloading.Should().BeFalse();
        reloadedFix.CanCancelDownload.Should().BeFalse();
    }

    [Fact]
    public async Task Retrying_a_failed_DenuvoFix_or_FixManifest_in_DownloadQueue_requests_fresh_url_and_applies()
    {
        Directory.CreateDirectory(_dir);
        var queue = await StartedAsync();
        var auth = CreateSignedInAuth(Path.Combine(_dir, "auth_retry.dat"));
        byte[] validFixZip = CreateZipBytes("denuvo_patch.dll", "patched-bytes");

        int grantAttempt = 0;
        Task<HttpResponseMessage> ApiRespond(HttpRequestMessage req, CancellationToken _)
        {
            int attempt = Interlocked.Increment(ref grantAttempt);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"url\":\"https://r2.example.com/signed/attempt-{attempt}.zip\",\"filename\":\"fix.zip\"}}"),
            });
        }

        Task<HttpResponseMessage> R2Respond(HttpRequestMessage req, CancellationToken _)
        {
            // First signed URL fails; second signed URL (on Retry) succeeds with a valid zip.
            if (req.RequestUri!.AbsolutePath.Contains("attempt-1", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            var content = new ByteArrayContent(validFixZip);
            content.Headers.ContentLength = validFixZip.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        var (vm, _, _, _, gameInstallDir) = CreateFixesVm(queue, auth, ApiRespond, R2Respond);
        var fix = MakeFixVm("fix-retryable");
        typeof(FixesViewModel).GetField("_allFixes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, new List<FixItemVm> { fix });
        var game = new FixGameCardVm(new DenuvoGameListing { AppId = "12345", Name = "Test Denuvo Game", FixCount = 1 });

        var firstItem = await vm.EnqueueFixDownloadAsync(fix, "fix", game);
        firstItem.Should().NotBeNull();
        await firstItem!.Completion.WaitAsync(Timeout);
        await WaitForAsync(() => firstItem.Status == DownloadStatus.Failed);

        firstItem.CanRetry.Should().BeTrue();
        fix.IsApplied.Should().BeFalse();
        grantAttempt.Should().Be(1);

        // Retry from DownloadQueue (what the Downloads tab Retry button calls):
        var retriedItem = queue.Retry(firstItem);
        var retryResult = await retriedItem.Completion.WaitAsync(Timeout);
        await WaitForAsync(() => fix.IsApplied);

        retryResult!.Ok.Should().BeTrue();
        grantAttempt.Should().Be(2, "Retry re-runs the worker callback and requests a fresh signed URL");
        fix.IsApplied.Should().BeTrue();
        File.Exists(Path.Combine(gameInstallDir, "denuvo_patch.dll")).Should().BeTrue();
    }

    [Fact]
    public async Task Reloading_or_clearing_fix_list_unbinds_discarded_FixSlotDownloadVms_from_PropertyChanged()
    {
        Directory.CreateDirectory(_dir);
        var queue = new DownloadQueue(NewCache(), _host.Dispatcher);
        var auth = CreateSignedInAuth(Path.Combine(_dir, "auth_unbind.dat"));

        var (vm, _, _, _, _) = CreateFixesVm(
            queue, auth,
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var discardedFix = MakeFixVm("fix-unbind");
        vm.ReplaceFixes([discardedFix]);
        var game = new FixGameCardVm(new DenuvoGameListing { AppId = "12345", Name = "Test Denuvo Game", FixCount = 1 });

        var item = await vm.EnqueueFixDownloadAsync(discardedFix, "fix", game);
        item.Should().NotBeNull();
        discardedFix.FixSlot.AttachedItem.Should().BeSameAs(item);

        int staleNotifications = 0;
        discardedFix.FixSlot.PropertyChanged += (_, _) => Interlocked.Increment(ref staleNotifications);

        // Reloading / replacing the fix list unbinds the old FixSlotDownloadVm instances before replacing them.
        var freshFix = MakeFixVm("fix-unbind");
        vm.ReplaceFixes([freshFix]);
        vm.SyncFixesWithQueue();

        discardedFix.FixSlot.AttachedItem.Should().BeNull("discarded FixSlotDownloadVm must detach from DownloadItem");
        freshFix.FixSlot.AttachedItem.Should().BeSameAs(item);

        int countAfterUnbind = Volatile.Read(ref staleNotifications);

        // Trigger PropertyChanged on the active DownloadItem (by cancelling it) — the discarded VM must receive 0 events.
        queue.Cancel(item!);
        await item!.Completion.WaitAsync(Timeout);
        await WaitForAsync(() => !freshFix.IsDownloading);

        Volatile.Read(ref staleNotifications).Should().Be(countAfterUnbind,
            "a discarded FixSlotDownloadVm must have zero remaining subscriptions on DownloadItem.PropertyChanged");

        // Closing the detail flyout also unbinds the remaining items.
        vm.CloseDetailCommand.Execute(null);
        freshFix.FixSlot.AttachedItem.Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_manifest_and_fix_installs_for_same_game_serialize_and_coalesce_steam_restarts()
    {
        Directory.CreateDirectory(_dir);
        var queue = await StartedAsync();
        var auth = CreateSignedInAuth(Path.Combine(_dir, "auth_serial.dat"));

        byte[] manifest1Zip = CreateZipBytes("12345.lua", "addappid(12345)\n");
        byte[] manifest2Zip = CreateZipBytes("12345.lua", "addappid(12345)\n-- v2\n");

        Task<HttpResponseMessage> ApiRespond(HttpRequestMessage req, CancellationToken _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"url\":\"https://r2.example.com/signed/m.zip\",\"filename\":\"12345.zip\"}"),
            });

        int r2Call = 0;
        Task<HttpResponseMessage> R2Respond(HttpRequestMessage req, CancellationToken _)
        {
            byte[] bytes = Interlocked.Increment(ref r2Call) == 1 ? manifest1Zip : manifest2Zip;
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentLength = bytes.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        int activeInstalls = 0;
        int maxConcurrentInstalls = 0;
        int restartCalls = 0;
        var firstEnteredRestart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRestart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (vm, _, _, _, _) = CreateFixesVm(queue, auth, ApiRespond, R2Respond);
        vm.RestartSteamOverride = () =>
        {
            int cur = Interlocked.Increment(ref activeInstalls);
            int prevMax;
            while (cur > (prevMax = Volatile.Read(ref maxConcurrentInstalls)))
                Interlocked.CompareExchange(ref maxConcurrentInstalls, cur, prevMax);

            int count = Interlocked.Increment(ref restartCalls);
            if (count == 1)
            {
                firstEnteredRestart.TrySetResult();
                releaseFirstRestart.Task.Wait(Timeout);
            }

            Interlocked.Decrement(ref activeInstalls);
            return true;
        };

        var fixA = MakeFixVm("fix-serial-a");
        var fixB = MakeFixVm("fix-serial-b");
        vm.ReplaceFixes([fixA, fixB]);
        var game = new FixGameCardVm(new DenuvoGameListing { AppId = "12345", Name = "Test Denuvo Game", FixCount = 2 });

        var itemA = await vm.EnqueueFixDownloadAsync(fixA, "manifest", game);
        var itemB = await vm.EnqueueFixDownloadAsync(fixB, "manifest", game);
        releaseFirstRestart.TrySetResult();

        var resA = await itemA!.Completion.WaitAsync(Timeout);
        var resB = await itemB!.Completion.WaitAsync(Timeout);

        resA!.Ok.Should().BeTrue();
        resB!.Ok.Should().BeTrue();
        maxConcurrentInstalls.Should().Be(1, "installations are serialized by _installGate");
        restartCalls.Should().BeInRange(1, 2);
    }

    private sealed class GatedChunkStream(
        byte[] data,
        int firstChunkSize,
        TaskCompletionSource firstChunkRead,
        Task gate) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset == 0)
            {
                int toCopy = Math.Min(firstChunkSize, Math.Min(buffer.Length, data.Length));
                data.AsMemory(0, toCopy).CopyTo(buffer);
                _offset += toCopy;
                firstChunkRead.TrySetResult();
                return toCopy;
            }

            await gate.WaitAsync(cancellationToken);
            if (_offset >= data.Length) return 0;
            int rest = Math.Min(buffer.Length, data.Length - _offset);
            data.AsMemory(_offset, rest).CopyTo(buffer);
            _offset += rest;
            return rest;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
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
