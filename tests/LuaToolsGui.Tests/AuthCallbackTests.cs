using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using AwesomeAssertions;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// F-14. The OAuth callback listener used to take the first <c>?code=</c> from any request, so a page that
/// hit <c>localhost:53789/callback?code=x</c> during a sign-in killed it. No <c>state</c> of ours can close
/// that (see <see cref="AuthStateTests"/>), so the listener now only serves the redirect's shape and lets a
/// code that does not exchange go by without ending the sign-in.
/// </summary>
public sealed class AuthCallbackTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly SupabaseSession Ours = new() { AccessToken = "ours" };

    private readonly HttpListener _listener = new();
    private readonly HttpClient _http = new();
    private readonly ConcurrentQueue<string> _exchanged = new();
    private readonly string _base;

    public AuthCallbackTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _base = $"http://localhost:{port}";
        _listener.Prefixes.Add(_base + "/");
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Close();
        _http.Dispose();
    }

    /// <summary>Only "good" exchanges — anything else was issued against another code_challenge.</summary>
    private Task<SupabaseSession> Wait(CancellationToken ct = default, TimeSpan? errorGrace = null) =>
        AuthService.WaitForCallbackAsync(_listener, code =>
        {
            _exchanged.Enqueue(code);
            return code == "good"
                ? Task.FromResult(Ours)
                : Task.FromException<SupabaseSession>(new AuthException("Token exchange failed (400)."));
        }, ct, errorGrace ?? TimeSpan.Zero);

    private Task<HttpResponseMessage> Send(string pathAndQuery, string? dest = "document", string method = "GET")
    {
        var req = new HttpRequestMessage(new HttpMethod(method), _base + pathAndQuery);
        if (dest is not null) req.Headers.Add("Sec-Fetch-Dest", dest);
        return _http.SendAsync(req);
    }

    [Theory]
    [InlineData("document")]
    [InlineData(null)]          // no Sec-Fetch-* at all: older browser
    public async Task The_real_redirect_completes_the_sign_in(string? dest)
    {
        var wait = Wait();

        var res = await Send("/callback?code=good", dest);

        (await wait.WaitAsync(Timeout)).Should().BeSameAs(Ours);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain("Signed in!");
    }

    [Fact]
    public async Task A_forged_code_does_not_end_the_sign_in()
    {
        var wait = Wait();

        var forged = await Send("/callback?code=forged");
        await Send("/callback?code=good");

        (await wait.WaitAsync(Timeout)).Should().BeSameAs(Ours);
        (await forged.Content.ReadAsStringAsync()).Should().Contain("Sign-in failed");
        _exchanged.Should().Equal("forged", "good");
    }

    [Theory]
    [InlineData("/other?code=forged", "document", "GET")]
    [InlineData("/callback/x?code=forged", "document", "GET")]
    [InlineData("/callback?code=forged", "image", "GET")]   // <img src=...> from a web page
    [InlineData("/callback?code=forged", "empty", "GET")]   // fetch()
    [InlineData("/callback?code=forged", "iframe", "GET")]
    [InlineData("/callback?code=forged", "document", "POST")]
    [InlineData("/callback", "document", "GET")]            // stray request, nothing to read
    [InlineData("/favicon.ico", "image", "GET")]
    public async Task Anything_but_the_redirect_is_404_and_never_exchanged(string path, string dest, string method)
    {
        var wait = Wait();

        var res = await Send(path, dest, method);
        await Send("/callback?code=good");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await wait.WaitAsync(Timeout)).Should().BeSameAs(Ours);
        _exchanged.Should().Equal("good");
    }

    [Fact]
    public async Task A_provider_error_ends_the_attempt()
    {
        var wait = Wait();

        await Send("/callback?error_description=access_denied");

        await wait.Awaiting(w => w.WaitAsync(Timeout)).Should().ThrowAsync<AuthException>();
        _exchanged.Should().BeEmpty();
    }

    [Fact]
    public async Task A_provider_error_inside_the_grace_is_ignored_and_the_real_code_still_wins()
    {
        var wait = Wait(errorGrace: TimeSpan.FromMinutes(1));

        var forged = await Send("/callback?error_description=forged");
        wait.IsCompleted.Should().BeFalse();
        await Send("/callback?code=good");

        forged.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await wait.WaitAsync(Timeout)).Should().BeSameAs(Ours);
    }

    [Fact]
    public async Task A_provider_error_after_the_grace_ends_the_attempt()
    {
        var wait = Wait(errorGrace: TimeSpan.FromMilliseconds(200));
        await Task.Delay(400);   // real clock: the grace is a Stopwatch window

        await Send("/callback?error_description=access_denied");

        await wait.Awaiting(w => w.WaitAsync(Timeout)).Should().ThrowAsync<AuthException>();
    }

    [Fact]
    public async Task Cancelling_stops_waiting_with_the_timed_out_error()
    {
        using var cts = new CancellationTokenSource();
        var wait = Wait(cts.Token);

        await cts.CancelAsync();

        await wait.Awaiting(w => w.WaitAsync(Timeout)).Should().ThrowAsync<AuthException>()
            .WithMessage(Resources.Strings.Auth_Err_SignInTimedOut);
    }

    [Fact]
    public void Persisting_the_session_never_blocks_the_calling_thread()
    {
        // SaveStored ran on the UI thread after sign-in/refresh and slept up to 3x150 ms on a locked file.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        string src = File.ReadAllText(Path.Combine(dir!.FullName, "src", "LuaToolsGui", "Services", "AuthService.cs"));

        src.Should().NotContain("Thread.Sleep");
        src.Should().Contain("await SaveStoredAsync(");
    }
}
