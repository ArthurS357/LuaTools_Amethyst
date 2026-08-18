using System.Net;
using System.Net.Http;
using AwesomeAssertions;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Pins the mapping from what Hubcap actually answers onto <see cref="HubcapResult{T}"/>'s cases.
///
/// <para>
/// The distinction these cover is not cosmetic. Before the result type, a rejected key, a spent quota, a
/// 500 and an unplugged network all arrived as <c>null</c>, so Settings told the user their key was bad
/// whenever the network was down — and people deleted working credentials on that advice. The source list
/// had the mirror-image bug: it locked the premium rows on any failure, making a momentary blip look like
/// an exhausted quota with no way to tell or retry.
/// </para>
/// </summary>
public class HubcapResultTests
{
    private const string Key = "smm_" + "0123456789abcdef";

    /// <summary>Replies with a fixed status (and optional body/headers), standing in for Hubcap.</summary>
    private sealed class StubHandler(HttpStatusCode status, string body = "", Action<HttpResponseMessage>? decorate = null)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var res = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            decorate?.Invoke(res);
            return Task.FromResult(res);
        }
    }

    /// <summary>Fails at the transport layer — no response ever exists. A refused connection or dead DNS.</summary>
    private sealed class BrokenTransportHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused.");
    }

    private static HubcapService With(HttpMessageHandler h) => new(h, TimeSpan.FromSeconds(5));

    // ── Status → case ────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_rejected_key_is_Unauthorized_and_nothing_else(HttpStatusCode status)
    {
        var result = await With(new StubHandler(status)).GetStatsAsync(Key);

        // The ONLY case that entitles the UI to tell the user their key is bad.
        result.Should().BeOfType<HubcapResult<HubcapStats>.Unauthorized>();
    }

    [Fact]
    public async Task A_spent_quota_is_RateLimited()
    {
        var result = await With(new StubHandler(HttpStatusCode.TooManyRequests)).GetStatsAsync(Key);

        result.Should().BeOfType<HubcapResult<HubcapStats>.RateLimited>()
              .Which.RetryAfter.Should().BeNull(); // header absent — the type says "didn't say", not "zero"
    }

    [Fact]
    public async Task RateLimited_carries_Retry_After_when_Hubcap_sends_it()
    {
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, decorate: r =>
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90)));

        var result = await With(handler).GetStatsAsync(Key);

        result.Should().BeOfType<HubcapResult<HubcapStats>.RateLimited>()
              .Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task A_missing_manifest_is_NotFound_not_a_failure()
    {
        var result = await With(new StubHandler(HttpStatusCode.NotFound)).CheckStatusAsync(Key, "730");

        result.Should().BeOfType<HubcapResult<HubcapManifestStatus>.NotFound>();
    }

    [Fact]
    public async Task A_server_fault_is_Failed_and_keeps_the_status_code()
    {
        var result = await With(new StubHandler(HttpStatusCode.InternalServerError)).GetStatsAsync(Key);

        result.Should().BeOfType<HubcapResult<HubcapStats>.Failed>()
              .Which.Status.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_dead_network_is_Offline_and_keeps_the_cause()
    {
        var result = await With(new BrokenTransportHandler()).GetStatsAsync(Key);

        result.Should().BeOfType<HubcapResult<HubcapStats>.Offline>()
              .Which.Cause.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task A_success_whose_body_is_not_parseable_is_Failed_not_a_null_carrying_Ok()
    {
        // "null" deserializes to null without throwing, which previously slipped through as a success.
        var result = await With(new StubHandler(HttpStatusCode.OK, "null")).GetStatsAsync(Key);

        result.Should().BeOfType<HubcapResult<HubcapStats>.Failed>();
    }

    // ── The cases must stay distinguishable ──────────────────────────

    [Fact]
    public void Offline_and_Unauthorized_are_not_interchangeable()
    {
        // Guards the whole point of the type against a well-meant "simplification" back to one failure case.
        HubcapResult<HubcapStats> offline = new HubcapResult<HubcapStats>.Offline(new Exception());
        HubcapResult<HubcapStats> rejected = new HubcapResult<HubcapStats>.Unauthorized();

        offline.Should().NotBe(rejected);
        offline.IsOk.Should().BeFalse();
        rejected.IsOk.Should().BeFalse();
        offline.ValueOrDefault.Should().BeNull();
    }

    [Fact]
    public void Ok_exposes_its_value_through_both_accessors()
    {
        var stats = new HubcapStats { DailyUsage = 7, DailyLimit = 25 };
        HubcapResult<HubcapStats> result = new HubcapResult<HubcapStats>.Ok(stats);

        result.IsOk.Should().BeTrue();
        result.ValueOrDefault.Should().BeSameAs(stats);
    }

    // ── Wording shown to the user ────────────────────────────────────

    [Fact]
    public void Download_wording_separates_an_unreachable_host_from_a_bad_key()
    {
        string offline = HubcapErrorText.Describe(new HubcapResult<DownloadedFile>.Offline(new Exception()));
        string rejected = HubcapErrorText.Describe(new HubcapResult<DownloadedFile>.Unauthorized());
        string quota = HubcapErrorText.Describe(new HubcapResult<DownloadedFile>.RateLimited(null));

        offline.Should().NotBe(rejected).And.NotBe(quota);
        offline.Should().Contain("reach");
        rejected.Should().Contain("key");
        quota.Should().Contain("limit");
    }

    [Fact]
    public void Download_wording_mentions_the_wait_when_Hubcap_supplied_one()
    {
        string withWait = HubcapErrorText.Describe(
            new HubcapResult<DownloadedFile>.RateLimited(TimeSpan.FromMinutes(30)));

        withWait.Should().Contain("30 minutes");
    }

    [Fact]
    public void Download_wording_reports_the_status_code_for_a_server_fault()
    {
        string failed = HubcapErrorText.Describe(
            new HubcapResult<DownloadedFile>.Failed(HttpStatusCode.BadGateway));

        failed.Should().Contain("502");
    }
}
