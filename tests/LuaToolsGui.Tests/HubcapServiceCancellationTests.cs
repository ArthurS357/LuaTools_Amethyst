using System.Net;
using System.Net.Http;
using AwesomeAssertions;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers the two deadlines on <see cref="HubcapService"/>'s metadata calls: the service's own bound on
/// how long a stats/status call may hang, and the caller's ability to abandon one.
///
/// <para>
/// Both endpoints sit on the UI's critical path — <c>CheckStatusAsync</c> is awaited inside the source-list
/// fetch, and the same pair runs in the headless plugin pipeline — while the shared <see cref="HttpClient"/>
/// carries a five-minute timeout sized for manifest zips. A hung host therefore stalled the source list for
/// minutes, and every call site passed <c>default</c>, so nothing was released when the user moved on.
/// </para>
///
/// <para>
/// The distinction these tests pin is the one that is easy to collapse: a <b>timeout</b> is a Hubcap
/// failure and yields <c>null</c> so the UI degrades, whereas <b>caller cancellation</b> propagates as
/// <see cref="OperationCanceledException"/>. Returning null for both is what let a superseded fetch carry
/// on and repopulate the source list behind whatever the user had navigated to.
/// </para>
/// </summary>
public class HubcapServiceCancellationTests
{
    private const string Key = "smm_" + "0123456789abcdef";

    /// <summary>Hangs until the request's own token trips, then surfaces that as the cancellation
    /// <see cref="HttpClient"/> would raise. Stands in for a Hubcap host that accepts the connection and
    /// never answers — the case a wall-clock timeout exists for.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        public int Calls;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
    }

    private sealed class UnreachableException() : Exception("Delay(Infinite) returned.");

    /// <summary>Answers immediately, so a call that completes proves the plumbing works at all.</summary>
    private sealed class CannedHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static HubcapService WithHandler(HttpMessageHandler handler, int timeoutMs = 150) =>
        new(handler, TimeSpan.FromMilliseconds(timeoutMs));

    // ── The metadata deadline ────────────────────────────────────────

    [Fact]
    public async Task GetStats_gives_up_on_a_hung_host_instead_of_waiting_for_the_client_timeout()
    {
        var handler = new HangingHandler();
        var svc = WithHandler(handler);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var result = await svc.GetStatsAsync(Key);
        elapsed.Stop();

        // Offline, not an exception and not a bad key: a Hubcap that won't answer is a degraded source.
        result.Should().BeOfType<HubcapResult<HubcapStats>.Offline>();
        // The point of the change. Without the deadline this waits on HttpClient.Timeout — five minutes.
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task CheckStatus_gives_up_on_a_hung_host()
    {
        var svc = WithHandler(new HangingHandler());

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var result = await svc.CheckStatusAsync(Key, "730");
        elapsed.Stop();

        result.Should().BeOfType<HubcapResult<HubcapManifestStatus>.Offline>();
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void The_shipped_metadata_deadline_is_far_below_the_client_timeout()
    {
        // Guards the relationship rather than the literal: a deadline at or above the client's own timeout
        // would silently do nothing, which is exactly the bug being fixed.
        HubcapService.DefaultMetadataTimeout.Should().BeLessThan(TimeSpan.FromMinutes(5));
        HubcapService.DefaultMetadataTimeout.Should().BeGreaterThan(TimeSpan.FromSeconds(5));
    }

    // ── Caller cancellation ──────────────────────────────────────────

    [Fact]
    public async Task GetStats_propagates_cancellation_requested_by_the_caller()
    {
        // Deadline set well beyond the test's patience so that what trips is unambiguously the caller.
        var svc = WithHandler(new HangingHandler(), timeoutMs: 60_000);
        using var cts = new CancellationTokenSource();

        var call = svc.GetStatsAsync(Key, cts.Token);
        await cts.CancelAsync();

        // Must NOT come back as null — a null here is indistinguishable from "Hubcap said no", which is
        // what let a stale fetch continue and rebuild the UI for the app the user had left.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public async Task CheckStatus_propagates_cancellation_requested_by_the_caller()
    {
        var svc = WithHandler(new HangingHandler(), timeoutMs: 60_000);
        using var cts = new CancellationTokenSource();

        var call = svc.CheckStatusAsync(Key, "730", cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public async Task An_already_cancelled_token_fails_immediately_rather_than_waiting_out_the_deadline()
    {
        // HttpClient still enters the handler with a pre-cancelled token — the abort happens on the first
        // await inside it, not before dispatch — so this asserts the outcome and the timing, not that the
        // handler went untouched.
        var svc = WithHandler(new HangingHandler(), timeoutMs: 60_000);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.GetStatsAsync(Key, cts.Token));
        elapsed.Stop();

        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    // ── The deadline must not fire on a healthy call ─────────────────

    [Fact]
    public async Task A_prompt_response_still_deserializes_normally()
    {
        // Field names as the live API returns them, so the deadline work can't quietly break parsing.
        var svc = WithHandler(new CannedHandler(
            """{"daily_usage":3,"daily_limit":25,"can_make_requests":true}"""));

        var result = await svc.GetStatsAsync(Key);

        var ok = result.Should().BeOfType<HubcapResult<HubcapStats>.Ok>().Subject;
        ok.Value.DailyUsage.Should().Be(3);
        ok.Value.DailyLimit.Should().Be(25);
        ok.Value.CanMakeRequests.Should().BeTrue();
    }

    [Fact]
    public async Task A_prompt_status_response_still_deserializes_normally()
    {
        var svc = WithHandler(new CannedHandler(
            """{"status":"available","manifest_file_exists":true}"""));

        var result = await svc.CheckStatusAsync(Key, "730");

        var ok = result.Should().BeOfType<HubcapResult<HubcapManifestStatus>.Ok>().Subject;
        ok.Value.ManifestFileExists.Should().BeTrue();
        ok.Value.Status.Should().Be("available");
    }
}
