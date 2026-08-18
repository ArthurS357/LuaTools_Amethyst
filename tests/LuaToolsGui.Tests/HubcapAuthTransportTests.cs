using System.Net;
using System.Net.Http;
using AwesomeAssertions;
using LuaToolsGui;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers HOW the key is presented to Hubcap.
///
/// <para>
/// It used to travel as <c>?api_key=…</c> on both the stats call and the manifest download. TLS hides a
/// URL in transit, but a URL is precisely what servers and their CDN write to access logs, and Hubcap sits
/// behind Cloudflare — so a live credential was being handed to third-party log retention on every call.
/// The header form is confirmed working on <c>/user/stats</c>.
/// </para>
///
/// <para>
/// The manifest endpoint is the one that spends the key's 25 daily requests, so it was never verified
/// directly. It therefore prefers the header and falls back to the query string if refused, and these
/// tests pin that negotiation offline — including the part that matters most: a genuinely bad key must
/// still be reported as a bad key rather than disguised by the retry.
/// </para>
/// </summary>
public class HubcapAuthTransportTests
{
    private const string Key = "smm_" + "0123456789abcdef";

    private sealed record Attempt(bool HadBearer, bool HadApiKeyInQuery, string? UserAgent);

    /// <summary>Records how each request authenticated, then answers per a scripted sequence.</summary>
    private sealed class RecordingHandler(params HttpStatusCode[] script) : HttpMessageHandler
    {
        public readonly List<Attempt> Attempts = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bool bearer = request.Headers.Authorization?.Scheme == "Bearer";
            bool inQuery = request.RequestUri?.Query.Contains("api_key=", StringComparison.Ordinal) == true;
            Attempts.Add(new Attempt(bearer, inQuery, request.Headers.UserAgent.ToString()));

            var status = script[Math.Min(Attempts.Count - 1, script.Length - 1)];
            bool isManifest = request.RequestUri?.AbsolutePath.Contains("/manifest/", StringComparison.Ordinal) == true;

            // The manifest endpoint streams a zip; the metadata ones return JSON. Answering both with the
            // same body made a "successful" stats call fail to parse, which the fallback then hid.
            HttpContent body = (status, isManifest) switch
            {
                (HttpStatusCode.OK, true) => new ByteArrayContent([0x50, 0x4B, 0x03, 0x04]),
                (HttpStatusCode.OK, false) => Json("""{"daily_usage":0,"daily_limit":25,"manifest_file_exists":true}"""),
                _ => Json(""),
            };
            return Task.FromResult(new HttpResponseMessage(status) { Content = body });
        }
    }

    private static StringContent Json(string body) =>
        new(body, System.Text.Encoding.UTF8, "application/json");

    private static HubcapService With(HttpMessageHandler h) => new(h, TimeSpan.FromSeconds(5));

    // ── Identification ───────────────────────────────────────────────

    [Theory]
    [InlineData("stats")]
    [InlineData("status")]
    [InlineData("manifest")]
    public async Task Every_request_identifies_the_app_and_its_version(string call)
    {
        // A request with no User-Agent is indistinguishable from a scraper at the far end, and Hubcap sits
        // behind Cloudflare. Naming the fork and version also ties a server-side problem to a build.
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var svc = With(handler);

        switch (call)
        {
            case "stats": await svc.GetStatsAsync(Key); break;
            case "status": await svc.CheckStatusAsync(Key, "730"); break;
            default: await svc.DownloadManifestAsync("730", Key, progress: null); break;
        }

        handler.Attempts.Should().NotBeEmpty();
        handler.Attempts[0].UserAgent.Should().StartWith("LuaToolsAmethyst/");
    }

    [Fact]
    public void The_user_agent_carries_the_assembly_version_rather_than_a_pasted_literal()
    {
        // The csproj <Version> is the single source of truth; it already drifted once, sitting at 1.1.3
        // through the whole 1.2.8 release. A hardcoded UA string would be a second place to forget.
        AppVersion.UserAgent.Should().Be($"LuaToolsAmethyst/{AppVersion.Current}");
        AppVersion.Current.Should().MatchRegex(@"^\d+\.\d+\.\d+");
        AppVersion.Current.Should().NotContain("+", "the commit suffix is trimmed");
    }

    // ── Stats ────────────────────────────────────────────────────────

    [Fact]
    public async Task Stats_authenticates_with_a_header_and_keeps_the_key_out_of_the_url()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        await With(handler).GetStatsAsync(Key);

        handler.Attempts.Should().ContainSingle();
        handler.Attempts[0].HadBearer.Should().BeTrue();
        handler.Attempts[0].HadApiKeyInQuery.Should().BeFalse();
    }

    [Fact]
    public async Task Stats_falls_back_to_the_query_string_if_the_header_is_refused()
    {
        // The header form is confirmed against the live endpoint, so this path should never run. It exists
        // so that a change on Hubcap's side degrades into one extra free request rather than into Settings
        // telling the user their key was rejected. Costs nothing: this endpoint doesn't touch the quota.
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);

        var result = await With(handler).GetStatsAsync(Key);

        result.Should().BeOfType<HubcapResult<HubcapStats>.Ok>();
        handler.Attempts.Should().HaveCount(2);
        handler.Attempts[1].HadApiKeyInQuery.Should().BeTrue();
    }

    [Fact]
    public async Task The_stats_and_manifest_transports_are_learned_independently()
    {
        // Separate endpoints; nothing guarantees they agree. Sharing one latch would let a manifest
        // refusal silently downgrade stats — or the reverse — with no evidence for it.
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        var svc = With(handler);

        await svc.GetStatsAsync(Key); // latches stats onto the query form
        handler.Attempts.Clear();

        await svc.DownloadManifestAsync("730", Key, progress: null);

        handler.Attempts[0].HadBearer.Should().BeTrue("manifest learned nothing from the stats refusal");
    }

    [Fact]
    public async Task Status_authenticates_with_a_header_too()
    {
        // Unchanged by this work — asserted so a later "consistency" edit can't move it to the query form.
        var handler = new RecordingHandler(HttpStatusCode.NotFound);
        await With(handler).CheckStatusAsync(Key, "730");

        handler.Attempts[0].HadBearer.Should().BeTrue();
        handler.Attempts[0].HadApiKeyInQuery.Should().BeFalse();
    }

    // ── Manifest: the happy path ─────────────────────────────────────

    [Fact]
    public async Task Manifest_tries_the_header_first_and_stops_there_when_it_works()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);

        var result = await With(handler).DownloadManifestAsync("730", Key, progress: null);

        result.Should().BeOfType<HubcapResult<DownloadedFile>.Ok>();
        handler.Attempts.Should().ContainSingle("the fallback must not cost an extra request when unneeded");
        handler.Attempts[0].HadBearer.Should().BeTrue();
        handler.Attempts[0].HadApiKeyInQuery.Should().BeFalse();
    }

    // ── Manifest: the fallback ───────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Manifest_retries_with_the_query_string_when_the_header_is_refused(HttpStatusCode refusal)
    {
        // 403 is covered as well as 401: an endpoint that doesn't accept header auth may refuse either
        // way, and narrowing the fallback to 401 would break downloading with no recovery.
        var handler = new RecordingHandler(refusal, HttpStatusCode.OK);

        var result = await With(handler).DownloadManifestAsync("730", Key, progress: null);

        result.Should().BeOfType<HubcapResult<DownloadedFile>.Ok>();
        handler.Attempts.Should().HaveCount(2);
        handler.Attempts[0].HadBearer.Should().BeTrue();
        handler.Attempts[1].HadBearer.Should().BeFalse();
        handler.Attempts[1].HadApiKeyInQuery.Should().BeTrue();
    }

    [Fact]
    public async Task Once_the_fallback_succeeds_later_downloads_skip_the_doomed_first_attempt()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        var svc = With(handler);

        await svc.DownloadManifestAsync("730", Key, progress: null); // 2 attempts, latches
        handler.Attempts.Clear();

        await svc.DownloadManifestAsync("440", Key, progress: null);

        handler.Attempts.Should().ContainSingle("the transport is already known");
        handler.Attempts[0].HadApiKeyInQuery.Should().BeTrue();
    }

    // ── Manifest: a bad key must still read as a bad key ─────────────

    [Fact]
    public async Task A_key_refused_both_ways_is_reported_as_Unauthorized()
    {
        // The failure mode worth guarding: the retry must not turn a genuinely invalid key into some
        // vaguer error the user can't act on.
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized);

        var result = await With(handler).DownloadManifestAsync("730", Key, progress: null);

        result.Should().BeOfType<HubcapResult<DownloadedFile>.Unauthorized>();
        handler.Attempts.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_key_refused_both_ways_does_not_latch_the_transport()
    {
        // Only a WORKING fallback proves the header was the problem. Latching on a plain bad key would
        // pin a user who then pastes a correct key to the query form for the rest of the session.
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized);
        var svc = With(handler);

        await svc.DownloadManifestAsync("730", Key, progress: null);
        handler.Attempts.Clear();

        await svc.DownloadManifestAsync("440", Key, progress: null);

        handler.Attempts.Should().HaveCount(2);
        handler.Attempts[0].HadBearer.Should().BeTrue("the header should still be preferred");
    }

    // ── Non-auth failures must not trigger the retry ─────────────────

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_definitive_non_auth_failure_is_returned_without_a_second_request(HttpStatusCode status)
    {
        // Retrying a 429 would spend a second request against an allowance already reported as exhausted.
        var handler = new RecordingHandler(status);

        var result = await With(handler).DownloadManifestAsync("730", Key, progress: null);

        result.Should().NotBeOfType<HubcapResult<DownloadedFile>.Ok>();
        handler.Attempts.Should().ContainSingle();
    }
}
