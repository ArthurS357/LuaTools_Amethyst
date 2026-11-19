using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;
using HopVerdict = LuaToolsGui.Services.LuaToolsApiClient.HopVerdict;

namespace LuaToolsGui.Tests;

/// <summary>
/// F-09, part B: the signed fix download follows redirects by hand, screening every hop with
/// <see cref="LuaToolsApiClient.IsFollowableSignedUrl"/>. HttpClient's own auto-redirect would land on any
/// host the first response names, so the dedicated client must have it off and every other client must not.
/// </summary>
public sealed class SignedDownloadRedirectTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("https://0123456789abcdef.r2.cloudflarestorage.com/fixes/1/fix.zip?X-Amz-Signature=abc", 1)]
    [InlineData("https://cdn.example.com/fix.zip", LuaToolsApiClient.MaxRedirectHops)]
    // Named hosts that merely start like loopback/metadata addresses are not refused.
    [InlineData("https://localhost-cdn.example.com/fix.zip", 2)]
    [InlineData("https://169-254-169-254.example.com/fix.zip", 2)]
    public void LegitimateHop_IsFollowed(string next, int hop) =>
        LuaToolsApiClient.ValidateRedirectHop(new Uri(next), hop).Should().Be(HopVerdict.Ok);

    [Theory]
    // The verdict travels as its name: HopVerdict is internal, and a public test method can't take it.
    [InlineData("http://cdn.example.com/fix.zip", nameof(HopVerdict.NotHttps))]
    [InlineData("ftp://cdn.example.com/fix.zip", nameof(HopVerdict.InvalidScheme))]
    [InlineData("file:///C:/Windows/fix.zip", nameof(HopVerdict.InvalidScheme))]
    [InlineData("https://192.168.0.10/fix.zip", nameof(HopVerdict.LoopbackOrIpLiteral))]
    [InlineData("https://169.254.169.254/latest/meta-data/", nameof(HopVerdict.LoopbackOrIpLiteral))]
    [InlineData("https://127.0.0.1/fix.zip", nameof(HopVerdict.LoopbackOrIpLiteral))]
    [InlineData("https://[::1]/fix.zip", nameof(HopVerdict.LoopbackOrIpLiteral))]
    [InlineData("https://localhost/fix.zip", nameof(HopVerdict.LoopbackOrIpLiteral))]
    // Resolve to loopback, yet System.Uri calls them plain DNS names with IsLoopback false.
    [InlineData("https://localhost./fix.zip", nameof(HopVerdict.LoopbackOrIpLiteral))]
    [InlineData("https://LOCALHOST.:8443/fix.zip", nameof(HopVerdict.LoopbackOrIpLiteral))]
    [InlineData("https://steam.localhost/fix.zip", nameof(HopVerdict.LoopbackOrIpLiteral))]
    public void BadHop_IsRefused_WithItsReason(string next, string expected) =>
        LuaToolsApiClient.ValidateRedirectHop(new Uri(next), 1).ToString().Should().Be(expected);

    [Fact]
    public void HopPastTheLimit_IsRefused_EvenToAGoodHost() =>
        LuaToolsApiClient.ValidateRedirectHop(new Uri("https://cdn.example.com/fix.zip"), LuaToolsApiClient.MaxRedirectHops + 1)
            .Should().Be(HopVerdict.TooManyHops);

    [Fact]
    public async Task ValidChain_IsFollowed_IncludingARelativeLocation_AndEachHopIsLogged()
    {
        string cdn = $"cdn-{Guid.NewGuid():N}.example.com";
        string sig = $"sig{Guid.NewGuid():N}";
        var handler = new Chain(uri => uri.Host switch
        {
            "api.example.com" => Redirect($"https://{cdn}/a"),
            _ when uri.AbsolutePath == "/a" => Redirect($"/b?{sig}"),
            _ => new HttpResponseMessage(HttpStatusCode.OK),
        });

        using var res = await Send(handler, "https://api.example.com/signed", Generous);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Requested.Select(u => u.AbsoluteUri).Should().Equal(
            "https://api.example.com/signed", $"https://{cdn}/a", $"https://{cdn}/b?{sig}");
        string log = TestLogRedirect.ReadLog();
        log.Should().Contain($"Signed download redirect 1: https://{cdn}");
        log.Should().Contain($"Signed download redirect 2: https://{cdn}");
        log.Should().NotContain(sig);
    }

    [Fact]
    public async Task BadHopMidChain_RefusesTheWholeDownload_WithoutRequestingIt()
    {
        string secret = $"sig{Guid.NewGuid():N}";
        var handler = new Chain(uri => uri.Host switch
        {
            "api.example.com" => Redirect("https://cdn.example.com/a"),
            "cdn.example.com" => Redirect($"https://127.0.0.1/steal?X-Amz-Signature={secret}"),
            _ => new HttpResponseMessage(HttpStatusCode.OK),
        });

        var act = () => Send(handler, "https://api.example.com/signed", Generous);

        (await act.Should().ThrowAsync<ApiException>())
            .Which.Message.Should().Be(LuaToolsGui.Resources.Strings.Api_Err_LinkRefused);
        handler.Requested.Should().HaveCount(2).And.NotContain(u => u.Host == "127.0.0.1");
        string log = TestLogRedirect.ReadLog();
        log.Should().Contain("Signed download refused: https://127.0.0.1 (redirect 2: LoopbackOrIpLiteral)");
        log.Should().NotContain(secret);
    }

    [Fact]
    public async Task ChainLongerThanTheLimit_IsRefused()
    {
        int n = 0;
        // Endless redirects to good hosts. The cap at 50 turns a missing hop limit into a failure, not a hang.
        var handler = new Chain(_ => ++n > 50
            ? throw new InvalidOperationException("hop limit not enforced")
            : Redirect($"https://hop{n}.example.com/fix.zip"));

        var act = () => Send(handler, "https://api.example.com/signed", Generous);

        await act.Should().ThrowAsync<ApiException>();
        handler.Requested.Should().HaveCount(LuaToolsApiClient.MaxRedirectHops + 1);
        TestLogRedirect.ReadLog().Should().Contain($"(redirect {LuaToolsApiClient.MaxRedirectHops + 1}: TooManyHops)");
    }

    /// <summary>One budget for every hop: three hops of 150 ms each overrun a 250 ms budget that any single
    /// hop would fit in, so a per-hop timeout (the budget multiplied by the chain) would let this pass.</summary>
    [Fact]
    public async Task HeadersBudget_CoversTheWholeChain_NotEachHop()
    {
        var handler = new Chain(uri => uri.AbsolutePath == "/3"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : Redirect($"/{int.Parse(uri.AbsolutePath[1..]) + 1}"),
            delay: TimeSpan.FromMilliseconds(150));

        var act = () => Send(handler, "https://api.example.com/0", TimeSpan.FromMilliseconds(250));

        await act.Should().ThrowAsync<OperationCanceledException>();
        // Proves the time ran out across hops, not inside the first one.
        handler.Requested.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public void FixDownloadClient_DoesNotAutoRedirect()
    {
        var client = new LuaToolsApiClient(null!, null!, null!, null!, null!);
        HandlerOf(client.FixDownloadHttp).AllowAutoRedirect.Should().BeFalse();
    }

    /// <summary>The dedicated client must not leak into the others: the API client and the CDP client
    /// (the CEF bridge, §2.4) keep the default auto-redirect.</summary>
    [Fact]
    public void ApiAndCdpClients_KeepAutoRedirect()
    {
        var dir = Path.Combine(Path.GetTempPath(), "luatools-redirect-" + Guid.NewGuid().ToString("N"));
        try
        {
            var api = new LuaToolsApiClient(null!, null!, null!, null!, null!);
            var cdp = new CefInjectorService(new SteamService(new SettingsService(dir)),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<CefInjectorService>.Instance);

            HandlerOf(PrivateHttp(api)).AllowAutoRedirect.Should().BeTrue();
            HandlerOf(PrivateHttp(cdp)).AllowAutoRedirect.Should().BeTrue();
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ } }
    }

    private static async Task<HttpResponseMessage> Send(Chain handler, string url, TimeSpan budget)
    {
        using var http = new HttpClient(handler, disposeHandler: false);
        return await LuaToolsApiClient.SendCheckingRedirectsAsync(http, new Uri(url), budget, CancellationToken.None);
    }

    private static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    private static HttpClient PrivateHttp(object owner) =>
        (HttpClient)owner.GetType().GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;

    private static HttpClientHandler HandlerOf(HttpClient http) =>
        (HttpClientHandler)typeof(HttpMessageInvoker)
            .GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(http)!;

    /// <summary>Answers each request from <c>answer</c>, after <c>delay</c>; never follows a redirect itself.</summary>
    private sealed class Chain(Func<Uri, HttpResponseMessage> answer, TimeSpan delay = default) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            return answer(request.RequestUri!);
        }
    }
}
