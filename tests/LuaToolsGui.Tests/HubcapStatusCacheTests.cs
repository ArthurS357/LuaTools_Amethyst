using System.Net;
using System.Net.Http;
using AwesomeAssertions;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers the availability cache on <c>CheckStatusAsync</c>.
///
/// <para>
/// The source list asks Hubcap "does this app have a manifest" once per app the user opens, so browsing
/// back and forth re-asked the same question over the network every time. The endpoint is free — the live
/// probe confirmed <c>daily_usage</c> does not move for it — so this is about the round-trip, not quota.
/// </para>
///
/// <para>
/// The rules worth pinning are the ones a later change could plausibly get wrong: failures must NOT be
/// cached (an <c>Offline</c> pinned for five minutes keeps a source dark long after the network returns,
/// and a pinned <c>Unauthorized</c> survives the user pasting a corrected key), and answers must not
/// outlive the key they were given for.
/// </para>
/// </summary>
public class HubcapStatusCacheTests
{
    private const string KeyA = "smm_" + "aaaaaaaaaaaaaaaa";
    private const string KeyB = "smm_" + "bbbbbbbbbbbbbbbb";

    /// <summary>Counts requests so a cache hit is observable as "the network was not touched".</summary>
    private sealed class CountingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class BrokenTransportHandler : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            throw new HttpRequestException("Connection refused.");
        }
    }

    private const string AvailableBody = """{"status":"available","manifest_file_exists":true}""";

    /// <summary>A clock the test moves by hand. Hand-rolled rather than pulling in
    /// Microsoft.Extensions.TimeProvider.Testing — TTL expiry is the only thing being simulated, and a
    /// whole package for one overridden method is not a trade worth making.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static (HubcapService Svc, ManualClock Clock) Build(HttpMessageHandler handler)
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-08-18T12:00:00Z"));
        return (new HubcapService(handler, TimeSpan.FromSeconds(5), clock), clock);
    }

    // ── Hit ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_repeat_check_for_the_same_app_does_not_hit_the_network()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, AvailableBody);
        var (svc, _) = Build(handler);

        var first = await svc.CheckStatusAsync(KeyA, "730");
        var second = await svc.CheckStatusAsync(KeyA, "730");

        handler.Calls.Should().Be(1);
        second.Should().BeEquivalentTo(first);
        second.Should().BeOfType<HubcapResult<HubcapManifestStatus>.Ok>();
    }

    [Fact]
    public async Task Different_apps_are_cached_separately()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, AvailableBody);
        var (svc, _) = Build(handler);

        await svc.CheckStatusAsync(KeyA, "730");
        await svc.CheckStatusAsync(KeyA, "440");
        await svc.CheckStatusAsync(KeyA, "730");

        handler.Calls.Should().Be(2); // one per distinct app, second 730 served from cache
    }

    [Fact]
    public async Task A_negative_answer_is_cached_too()
    {
        // "Not on Hubcap" is as definitive as "available" — re-asking every time buys nothing.
        var handler = new CountingHandler(HttpStatusCode.NotFound, "");
        var (svc, _) = Build(handler);

        await svc.CheckStatusAsync(KeyA, "730");
        var second = await svc.CheckStatusAsync(KeyA, "730");

        handler.Calls.Should().Be(1);
        second.Should().BeOfType<HubcapResult<HubcapManifestStatus>.NotFound>();
    }

    // ── Expiry ───────────────────────────────────────────────────────

    [Fact]
    public async Task An_entry_older_than_the_ttl_is_refetched()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, AvailableBody);
        var (svc, clock) = Build(handler);

        await svc.CheckStatusAsync(KeyA, "730");
        clock.Advance(HubcapService.StatusCacheTtl + TimeSpan.FromSeconds(1));
        await svc.CheckStatusAsync(KeyA, "730");

        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task An_entry_just_inside_the_ttl_is_still_served_from_cache()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, AvailableBody);
        var (svc, clock) = Build(handler);

        await svc.CheckStatusAsync(KeyA, "730");
        clock.Advance(HubcapService.StatusCacheTtl - TimeSpan.FromSeconds(1));
        await svc.CheckStatusAsync(KeyA, "730");

        handler.Calls.Should().Be(1);
    }

    // ── Failures must not stick ──────────────────────────────────────

    [Fact]
    public async Task An_unreachable_host_is_retried_rather_than_pinned()
    {
        var handler = new BrokenTransportHandler();
        var (svc, _) = Build(handler);

        var first = await svc.CheckStatusAsync(KeyA, "730");
        var second = await svc.CheckStatusAsync(KeyA, "730");

        first.Should().BeOfType<HubcapResult<HubcapManifestStatus>.Offline>();
        second.Should().BeOfType<HubcapResult<HubcapManifestStatus>.Offline>();
        // The point: caching this would keep the source dark for five minutes after the network returned.
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_rejected_key_is_retried_rather_than_pinned()
    {
        var handler = new CountingHandler(HttpStatusCode.Unauthorized, "");
        var (svc, _) = Build(handler);

        await svc.CheckStatusAsync(KeyA, "730");
        await svc.CheckStatusAsync(KeyA, "730");

        // Otherwise pasting a corrected key would still read as rejected until the entry aged out.
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_server_fault_is_retried_rather_than_pinned()
    {
        var handler = new CountingHandler(HttpStatusCode.InternalServerError, "");
        var (svc, _) = Build(handler);

        await svc.CheckStatusAsync(KeyA, "730");
        await svc.CheckStatusAsync(KeyA, "730");

        handler.Calls.Should().Be(2);
    }

    // ── Invalidation ─────────────────────────────────────────────────

    [Fact]
    public async Task Invalidating_one_app_leaves_the_others_cached()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, AvailableBody);
        var (svc, _) = Build(handler);

        await svc.CheckStatusAsync(KeyA, "730");
        await svc.CheckStatusAsync(KeyA, "440");
        handler.Calls.Should().Be(2);

        svc.InvalidateStatus("730");

        await svc.CheckStatusAsync(KeyA, "730"); // refetched
        await svc.CheckStatusAsync(KeyA, "440"); // still cached
        handler.Calls.Should().Be(3);
    }

    [Fact]
    public async Task Clearing_the_cache_drops_everything()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, AvailableBody);
        var (svc, _) = Build(handler);

        await svc.CheckStatusAsync(KeyA, "730");
        await svc.CheckStatusAsync(KeyA, "440");
        svc.ClearStatusCache();
        await svc.CheckStatusAsync(KeyA, "730");
        await svc.CheckStatusAsync(KeyA, "440");

        handler.Calls.Should().Be(4);
    }

    [Fact]
    public async Task Answers_given_for_one_key_are_not_reused_for_another()
    {
        // Availability is answered per key, so a new key must not inherit the previous one's view of what
        // it can download. Swapping keys in Settings is exactly this path.
        var handler = new CountingHandler(HttpStatusCode.OK, AvailableBody);
        var (svc, _) = Build(handler);

        await svc.CheckStatusAsync(KeyA, "730");
        await svc.CheckStatusAsync(KeyB, "730");

        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Returning_to_the_original_key_does_not_resurrect_its_old_entries()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, AvailableBody);
        var (svc, _) = Build(handler);

        await svc.CheckStatusAsync(KeyA, "730");
        await svc.CheckStatusAsync(KeyB, "730"); // wipes A's entries
        await svc.CheckStatusAsync(KeyA, "730"); // must not come back from the dead

        handler.Calls.Should().Be(3);
    }
}
