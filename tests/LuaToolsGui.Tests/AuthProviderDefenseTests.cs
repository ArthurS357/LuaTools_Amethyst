using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The 2026-10-20 provider outage. Three things went wrong at once: the browser sign-in sat out its whole
/// five-minute timeout with no explanation, the /login code path that still worked was nowhere in sight, and
/// a startup refresh that failed for ANY reason deleted auth.dat — so signed-in users were signed out for good
/// by a server that never said their token was bad. GoTrue reports a dead refresh token as 400 + error_code;
/// 401 there only ever comes from the Kong gateway (missing apikey). Probed live the same day.
/// </summary>
public sealed class AuthProviderDefenseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lt-authdef-" + Guid.NewGuid().ToString("N"));
    private readonly string _authFile;

    public AuthProviderDefenseTests()
    {
        Directory.CreateDirectory(_dir);
        _authFile = Path.Combine(_dir, "auth.dat");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── Health check ────────────────────────────────────────────────

    [Fact]
    public async Task HealthCheck_returns_true_when_provider_up()
    {
        var auth = new AuthService(new FakeProvider(), _authFile);

        (await auth.IsProviderAvailableAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)] // Kong answering for a GoTrue it could not reach
    public async Task HealthCheck_returns_false_when_provider_down(HttpStatusCode status)
    {
        var auth = new AuthService(new FakeProvider { Health = _ => new HttpResponseMessage(status) }, _authFile);

        (await auth.IsProviderAvailableAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task HealthCheck_returns_false_when_transport_fails()
    {
        var auth = new AuthService(new FakeProvider { Health = _ => throw new HttpRequestException("offline") }, _authFile);

        (await auth.IsProviderAvailableAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task HealthCheck_returns_false_when_it_times_out()
    {
        var auth = new AuthService(
            new FakeProvider { Health = _ => throw new TaskCanceledException("timeout", new TimeoutException()) }, _authFile);

        (await auth.IsProviderAvailableAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task HealthCheck_is_a_GET_with_the_apikey()
    {
        // Live probe: HEAD is 405 and a keyless GET is Kong's own 401 — either would read "down" forever.
        var provider = new FakeProvider();
        var auth = new AuthService(provider, _authFile);

        await auth.IsProviderAvailableAsync();

        provider.LastHealth!.Method.Should().Be(HttpMethod.Get);
        provider.LastHealth.RequestUri!.ToString().Should().Be(AppConfig.SupabaseHealthUrl);
        provider.LastHealth.Headers.GetValues("apikey").Should().ContainSingle().Which.Should().Be(AppConfig.SupabaseAnonKey);
    }

    // ── Pre-login gate ──────────────────────────────────────────────

    [Fact]
    public async Task Login_is_blocked_with_dialog_when_health_check_fails()
    {
        var (auth, asked, browser, _) = Gate(HttpStatusCode.ServiceUnavailable, ProviderDownChoice.Cancel);

        await auth.SignInAsync();

        asked.Count.Should().Be(1, "a down provider must be explained before the browser opens");
        browser.Should().BeEmpty();
    }

    [Fact]
    public async Task Login_proceeds_when_user_clicks_try_anyway()
    {
        var (auth, asked, browser, _) = Gate(HttpStatusCode.ServiceUnavailable, ProviderDownChoice.TryAnyway);

        await auth.Invoking(a => a.SignInAsync()).Should().ThrowAsync<BrowserOpened>();

        asked.Count.Should().Be(1);
        browser.Should().ContainSingle("a false 'down' must never lock the user out of the browser flow");
    }

    [Fact]
    public async Task Manual_code_flow_opens_when_user_clicks_use_manual_code()
    {
        var (auth, _, browser, manual) = Gate(HttpStatusCode.ServiceUnavailable, ProviderDownChoice.UseManualCode);

        await auth.SignInAsync();

        manual.Count.Should().Be(1);
        browser.Should().BeEmpty();
    }

    [Fact]
    public async Task Login_succeeds_when_provider_up()
    {
        var (auth, asked, browser, _) = Gate(HttpStatusCode.OK, ProviderDownChoice.Cancel);

        await auth.Invoking(a => a.SignInAsync()).Should().ThrowAsync<BrowserOpened>();

        asked.Should().BeEmpty("a healthy provider must not interrupt sign-in");
        browser.Should().ContainSingle();
    }

    // ── Startup refresh: when ────────────────────────────────────────

    [Fact]
    public async Task Refresh_skipped_when_token_has_more_than_2_min()
    {
        Store(DateTimeOffset.UtcNow.AddMinutes(10));
        var provider = new FakeProvider();

        (await new AuthService(provider, _authFile).InitializeAsync()).Should().BeTrue();

        provider.RefreshCalls.Should().Be(0, "a still-valid token must not depend on the provider being up");
    }

    [Fact]
    public async Task Refresh_executes_when_token_near_expiry()
    {
        Store(DateTimeOffset.UtcNow.AddMinutes(1));
        var provider = new FakeProvider { Refresh = _ => Session("rotated") };

        (await new AuthService(provider, _authFile).InitializeAsync()).Should().BeTrue();

        provider.RefreshCalls.Should().Be(1);
    }

    [Fact]
    public async Task Refresh_executes_when_expiry_unknown()
    {
        WriteRaw(new { RefreshToken = "stored", AccessToken = "old" }); // no ExpiresAt at all
        var provider = new FakeProvider { Refresh = _ => Session("rotated") };

        (await new AuthService(provider, _authFile).InitializeAsync()).Should().BeTrue();

        provider.RefreshCalls.Should().Be(1, "an unknown expiry is not proof of validity");
    }

    // ── Startup refresh: what a failure may do to the session ───────

    [Theory]
    [InlineData("refresh_token_not_found")]
    [InlineData("refresh_token_already_used")]
    [InlineData("session_not_found")]
    [InlineData("session_expired")]
    [InlineData("validation_failed")]
    public async Task Refresh_400_token_invalid_clears_session(string errorCode)
    {
        Store(DateTimeOffset.UtcNow.AddMinutes(-5));
        var auth = new AuthService(new FakeProvider { Refresh = _ => GoTrueError(errorCode) }, _authFile);

        (await auth.InitializeAsync()).Should().BeFalse();

        auth.IsSignedIn.Should().BeFalse();
        File.Exists(_authFile).Should().BeFalse("the server proved this refresh token dead");
    }

    [Theory]
    [InlineData("malformed_body")]
    [InlineData("over_request_rate_limit")]
    [InlineData("Refresh_Token_Not_Found")] // not GoTrue's shape: refuse to guess
    public async Task Refresh_400_other_error_keeps_session(string errorCode) =>
        await ShouldKeepSession(_ => GoTrueError(errorCode));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("<html>Bad Request</html>")]
    public async Task Refresh_400_unparseable_body_keeps_session(string body) =>
        await ShouldKeepSession(_ => Respond(HttpStatusCode.BadRequest, body));

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"code":400,"msg":"bad"}""")]
    [InlineData("""{"error_code":400}""")]
    [InlineData("""{"error_code":""}""")]
    public async Task Refresh_400_missing_error_code_keeps_session(string body) =>
        await ShouldKeepSession(_ => Respond(HttpStatusCode.BadRequest, body));

    [Fact]
    public async Task Refresh_401_from_kong_keeps_session() =>
        await ShouldKeepSession(_ => Respond(HttpStatusCode.Unauthorized, """{"message":"No API key found in request"}"""));

    [Fact]
    public async Task Refresh_500_keeps_session() =>
        await ShouldKeepSession(_ => Respond(HttpStatusCode.InternalServerError, ""));

    [Fact]
    public async Task Refresh_timeout_keeps_session() =>
        await ShouldKeepSession(_ => throw new TaskCanceledException("timeout", new TimeoutException()));

    [Fact]
    public async Task Refresh_network_error_keeps_session() =>
        await ShouldKeepSession(_ => throw new HttpRequestException("No such host is known."));

    [Fact]
    public async Task Refresh_success_updates_session()
    {
        Store(DateTimeOffset.UtcNow.AddMinutes(-5));
        var auth = new AuthService(new FakeProvider { Refresh = _ => Session("rotated") }, _authFile);

        (await auth.InitializeAsync()).Should().BeTrue();

        auth.IsSignedIn.Should().BeTrue();
        ReadStored().RefreshToken.Should().Be("rotated", "the rotated token must persist or the next launch is signed out");
        (await auth.GetValidAccessTokenAsync()).Should().Be("access-rotated");
    }

    [Fact]
    public async Task Refresh_backoff_prevents_hammering_on_repeated_5xx()
    {
        Store(DateTimeOffset.UtcNow.AddMinutes(-5));
        var provider = new FakeProvider { Refresh = _ => Respond(HttpStatusCode.ServiceUnavailable, "") };
        var auth = new AuthService(provider, _authFile);

        await auth.Invoking(a => a.InitializeAsync()).Should().ThrowAsync<AuthException>();
        await auth.Invoking(a => a.GetValidAccessTokenAsync()).Should().ThrowAsync<AuthException>();
        await auth.Invoking(a => a.GetValidAccessTokenAsync()).Should().ThrowAsync<AuthException>();

        provider.RefreshCalls.Should().Be(1, "three failures inside the backoff window cost one request");
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private async Task ShouldKeepSession(Func<HttpRequestMessage, HttpResponseMessage> refresh)
    {
        Store(DateTimeOffset.UtcNow.AddMinutes(-5));
        byte[] before = File.ReadAllBytes(_authFile);
        var auth = new AuthService(new FakeProvider { Refresh = refresh }, _authFile);

        await auth.Invoking(a => a.InitializeAsync()).Should().ThrowAsync<Exception>("the caller decides; the session stays");

        auth.IsSignedIn.Should().BeTrue();
        File.ReadAllBytes(_authFile).Should().Equal(before, "nothing proved the refresh token dead");
    }

    private (AuthService Auth, List<int> Asked, List<string> Browser, List<int> Manual) Gate(
        HttpStatusCode health, ProviderDownChoice choice)
    {
        var asked = new List<int>();
        var browser = new List<string>();
        var manual = new List<int>();
        var auth = new AuthService(new FakeProvider { Health = _ => new HttpResponseMessage(health) }, _authFile)
        {
            AskWhenProviderDown = () => { asked.Add(1); return Task.FromResult(choice); },
            OpenManualCode = () => manual.Add(1),
            // Stops the flow right where the real one hands off to the browser, before any 5 min wait.
            OpenBrowser = url => { browser.Add(url); throw new BrowserOpened(); },
        };
        return (auth, asked, browser, manual);
    }

    private void Store(DateTimeOffset expiresAt) =>
        WriteRaw(new StoredAuth { RefreshToken = "stored", AccessToken = "old", ExpiresAt = expiresAt });

    private void WriteRaw(object stored) =>
        File.WriteAllBytes(_authFile, ProtectedData.Protect(
            JsonSerializer.SerializeToUtf8Bytes(stored), null, DataProtectionScope.CurrentUser));

    private StoredAuth ReadStored() =>
        JsonSerializer.Deserialize<StoredAuth>(
            ProtectedData.Unprotect(File.ReadAllBytes(_authFile), null, DataProtectionScope.CurrentUser))!;

    private static HttpResponseMessage Respond(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage GoTrueError(string errorCode) =>
        Respond(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { code = 400, error_code = errorCode, msg = "x" }));

    private static HttpResponseMessage Session(string refreshToken) =>
        Respond(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            access_token = "access-" + refreshToken, refresh_token = refreshToken, expires_in = 3600,
        }));

    private sealed class BrowserOpened : Exception;

    private sealed class FakeProvider : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Health { get; init; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
        public Func<HttpRequestMessage, HttpResponseMessage> Refresh { get; init; } =
            _ => throw new InvalidOperationException("unexpected refresh");
        public HttpRequestMessage? LastHealth { get; private set; }
        public int RefreshCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/health", StringComparison.Ordinal))
            {
                LastHealth = request;
                return Task.FromResult(Health(request));
            }
            Interlocked.Increment(ref RefreshCalls);
            return Task.FromResult(Refresh(request));
        }
    }
}
