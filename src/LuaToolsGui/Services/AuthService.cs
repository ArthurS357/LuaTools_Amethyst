using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using LuaToolsGui.Models;

namespace LuaToolsGui.Services;

public class AuthService
{
    private static readonly string AuthFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui", "auth.dat");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt;

    public string? DisplayName { get; private set; }
    public string? Email { get; private set; }
    public string? AvatarUrl { get; private set; }

    /// <summary>True when a real (Discord) account is signed in. Guests have no session.</summary>
    public bool IsSignedIn => _refreshToken is not null;

    /// <summary>True when browsing as a guest (no account signed in).</summary>
    public bool IsGuest => !IsSignedIn;

    /// <summary>True when the signed-in session is a Discord bot placeholder account
    /// (email @bot.lua.tools) rather than a full linked lua.tools account.</summary>
    public bool IsBotProvisioned =>
        IsSignedIn && Email?.EndsWith(AppConfig.BotAccountEmailDomain, StringComparison.OrdinalIgnoreCase) == true;

    public event Action? AuthStateChanged;

    // ── Session restore ─────────────────────────────────────────────

    /// <summary>
    /// Restore a persisted session if one exists. Guests have no session and simply
    /// browse with the public endpoints. Returns true when an account is signed in.
    /// </summary>
    public async Task<bool> InitializeAsync()
    {
        StoredAuth? stored = LoadStored();
        if (stored is null || string.IsNullOrEmpty(stored.RefreshToken)) return false;

        _accessToken = stored.AccessToken;
        _refreshToken = stored.RefreshToken;
        _expiresAt = stored.ExpiresAt;
        DisplayName = stored.DisplayName;
        Email = stored.Email;
        AvatarUrl = stored.AvatarUrl;

        // Token still comfortably valid, or refresh succeeds → keep the session
        if (_expiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            AuthStateChanged?.Invoke();
            return true;
        }
        try
        {
            // Take the same lock GetValidAccessTokenAsync uses. Startup restore and a concurrent API call
            // can both land here, and two simultaneous refreshes race to ApplySession — the loser persists
            // a refresh token the server has already rotated away, so the NEXT launch starts signed out.
            await _refreshLock.WaitAsync();
            try
            {
                if (_expiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
                    await RefreshAsync();
            }
            finally { _refreshLock.Release(); }

            AuthStateChanged?.Invoke();
            return true;
        }
        catch
        {
            ClearSession(); // revoked/expired — fall back to guest
            AuthStateChanged?.Invoke();
            return false;
        }
    }

    // ── Interactive sign-in ─────────────────────────────────────────

    /// <summary>
    /// Full PKCE sign-in: opens Discord OAuth in the default browser and
    /// captures the redirect on a localhost listener.
    /// </summary>
    public async Task SignInAsync(CancellationToken ct = default)
    {
        string verifier = CreateCodeVerifier();
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        // NO `state` PARAMETER. This is the fix for "the Discord button does not actually sign me in".
        //
        // A nonce used to be passed here as an anti-forgery guard, and it silently broke the whole flow.
        // Supabase does not accept a caller-supplied state on /authorize — it MINTS one, and that value is
        // the key its flow-state row is stored under, carrying the redirect target across the trip to
        // Discord and back. Passing our own displaced it. Probed against the live endpoint:
        //
        //   with    &state=probe  ->  ...discord.com/...&state=probe                              (ours)
        //   without &state=       ->  ...discord.com/...&state=e4631a5c-2b82-4b95-8e5f-1729b53dbb65 (its own)
        //
        // Discord echoes that state back to Supabase's callback and nothing else, so overwriting it is
        // overwriting the only handle Supabase has on the pending flow. The redirect back to
        // http://localhost:53789/callback never happens, the listener below waits out its full five
        // minutes, and the user is told the sign-in "timed out" — which is why the button looked dead.
        //
        // Losing the nonce does not lose the protection it was reaching for. PKCE is what actually binds
        // the exchange: a code injected into the local listener by another process was issued against a
        // DIFFERENT code_challenge, so ExchangeCodeAsync below presents our verifier and Supabase refuses
        // it. The attack fails on the server, which is the right place for it to fail. The listener also
        // only exists while a sign-in the user started is in flight.
        string authorizeUrl = BuildAuthorizeUrl(challenge);

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{AppConfig.OAuthCallbackPort}/");
        try { listener.Start(); }
        catch (HttpListenerException)
        {
            // Port already held (a stale sign-in attempt, or another app). Fail with something actionable
            // instead of letting the raw listener exception escape to the UI.
            throw new AuthException(Resources.Strings.Auth_Err_CallbackPortBusy);
        }

        Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });

        string code;
        try
        {
            code = await WaitForCallbackAsync(listener, ct);
        }
        finally
        {
            listener.Stop();
        }

        var session = await ExchangeCodeAsync(code, verifier, ct);
        ApplySession(session);
        AuthStateChanged?.Invoke();
    }

    // ── Discord bot code sign-in ────────────────────────────────────

    /// <summary>
    /// Sign in with a 6-character Discord bot code (from <c>/login</c>): redeem it for a magic-link
    /// token, then verify that token into a real Supabase session. Single-use, 5-minute TTL.
    /// </summary>
    public async Task SignInWithCodeAsync(string code, CancellationToken ct = default)
    {
        // Step 1: redeem the code for a Supabase magic-link token hash. The code itself is the credential.
        var redeemReq = new HttpRequestMessage(HttpMethod.Post, $"{AppConfig.ApiBaseUrl}/api/auth/code/redeem")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { code = code.Trim().ToUpperInvariant() }),
                Encoding.UTF8, "application/json"),
        };

        var redeemRes = await _http.SendAsync(redeemReq, ct);
        string redeemBody = await redeemRes.Content.ReadAsStringAsync(ct);
        if (!redeemRes.IsSuccessStatusCode)
            throw new AuthException(redeemRes.StatusCode switch
            {
                HttpStatusCode.Gone => Resources.Strings.Settings_BotCode_Expired,
                HttpStatusCode.NotFound => Resources.Strings.Settings_BotCode_Invalid,
                HttpStatusCode.BadRequest => Resources.Strings.Settings_BotCode_Invalid,
                _ => Resources.Strings.Settings_BotCode_ServerError,
            });

        var redeem = JsonSerializer.Deserialize<CodeRedeemResponse>(redeemBody);
        if (redeem is null || string.IsNullOrEmpty(redeem.Token))
            throw new AuthException(Resources.Strings.Settings_BotCode_ServerError);

        // Step 2: verify the magic-link token hash into an access/refresh session.
        var session = await VerifyMagicTokenAsync(redeem.Token, ct);
        ApplySession(session);
        AuthStateChanged?.Invoke();
    }

    private async Task<SupabaseSession> VerifyMagicTokenAsync(string tokenHash, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{AppConfig.SupabaseUrl}/auth/v1/verify")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { type = "magiclink", token_hash = tokenHash }),
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("apikey", AppConfig.SupabaseAnonKey);

        var res = await _http.SendAsync(req, ct);
        string body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new AuthException(Resources.Strings.Settings_BotCode_ServerError);

        return JsonSerializer.Deserialize<SupabaseSession>(body)
               ?? throw new AuthException(Resources.Strings.Settings_BotCode_ServerError);
    }

    /// <summary>
    /// The Supabase authorize URL for a Discord sign-in.
    ///
    /// <para>
    /// Extracted for one reason: what must be true of it is an ABSENCE, and an absence is only guarded if
    /// something asserts it. This URL must carry no <c>state</c> parameter. Supabase mints its own and
    /// keys the pending flow on it; supplying ours overwrote that key, so the redirect back to the local
    /// listener never happened and every Discord sign-in died as a five-minute timeout. Re-adding a state
    /// here looks like hardening and is a total outage of the feature — see the note at the call site.
    /// </para>
    /// </summary>
    internal static string BuildAuthorizeUrl(string codeChallenge) =>
        $"{AppConfig.SupabaseUrl}/auth/v1/authorize?provider=discord" +
        $"&redirect_to={Uri.EscapeDataString(AppConfig.OAuthCallbackUrl)}" +
        $"&code_challenge={codeChallenge}&code_challenge_method=s256";

    private static async Task<string> WaitForCallbackAsync(
        HttpListener listener, CancellationToken ct)
    {
        // 5 minute window for the user to complete the browser flow
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        while (true)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Browser sign-in can fail for reasons this app cannot see or fix — a provider outage, a
                // redirect not allow-listed on the server. The Discord /login code path does not depend on
                // any of that, so the message names it rather than leaving the user with a dead end.
                throw new AuthException(Resources.Strings.Auth_Err_SignInTimedOut);
            }

            var query = HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "");
            string? code = query.Get("code");
            string? error = query.Get("error_description");

            if (code is null && error is null)
            {
                // Favicon or stray request — ignore and keep waiting
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                continue;
            }

            // The `state` coming back is Supabase's own and cannot be checked here — we never saw the value
            // it minted. It is not what secures this: an injected code was issued against another client's
            // code_challenge, so the exchange in ExchangeCodeAsync fails on the server. See the note on the
            // authorize URL for why validating a state of our own broke sign-in outright.

            bool ok = code is not null;
            byte[] page = Encoding.UTF8.GetBytes(ResultPage(ok, error));
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = page.Length;
            await ctx.Response.OutputStream.WriteAsync(page, timeout.Token);
            ctx.Response.Close();

            if (!ok) throw new AuthException(error ?? "Sign-in was denied.");
            return code!;
        }
    }

    private async Task<SupabaseSession> ExchangeCodeAsync(string code, string verifier, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{AppConfig.SupabaseUrl}/auth/v1/token?grant_type=pkce")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { auth_code = code, code_verifier = verifier }),
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("apikey", AppConfig.SupabaseAnonKey);

        var res = await _http.SendAsync(req, ct);
        string body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new AuthException($"Token exchange failed ({(int)res.StatusCode}): {body}");

        return JsonSerializer.Deserialize<SupabaseSession>(body)
               ?? throw new AuthException("Token exchange returned an empty session.");
    }

    // ── Token access / refresh ──────────────────────────────────────

    /// <summary>Returns a valid access token, refreshing first when close to expiry. Throws for guests.</summary>
    public async Task<string> GetValidAccessTokenAsync()
    {
        if (_refreshToken is null) throw new AuthException("Not signed in.");

        if (_expiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
        {
            await _refreshLock.WaitAsync();
            try
            {
                if (_expiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
                    await RefreshAsync();
            }
            finally
            {
                _refreshLock.Release();
            }
        }
        return _accessToken!;
    }

    private async Task RefreshAsync()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{AppConfig.SupabaseUrl}/auth/v1/token?grant_type=refresh_token")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { refresh_token = _refreshToken }),
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("apikey", AppConfig.SupabaseAnonKey);

        var res = await _http.SendAsync(req);
        string body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new AuthException($"Session refresh failed ({(int)res.StatusCode}).");

        var session = JsonSerializer.Deserialize<SupabaseSession>(body)
                      ?? throw new AuthException("Session refresh returned an empty session.");
        ApplySession(session);
    }

    /// <summary>Sign out of the account and return to guest browsing (app stays usable).</summary>
    public void SignOut()
    {
        ClearSession();
        AuthStateChanged?.Invoke();
    }

    // ── Internals ───────────────────────────────────────────────────

    private void ApplySession(SupabaseSession session)
    {
        _accessToken = session.AccessToken;
        _refreshToken = session.RefreshToken;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(session.ExpiresIn);

        if (session.User is not null)
        {
            var meta = session.User.Metadata;
            DisplayName = meta?.CustomClaims?.GlobalName ?? meta?.FullName ?? meta?.Name ?? session.User.Email;
            Email = session.User.Email;
            AvatarUrl = meta?.AvatarUrl;
        }

        SaveStored(new StoredAuth
        {
            RefreshToken = _refreshToken,
            AccessToken = _accessToken,
            ExpiresAt = _expiresAt,
            DisplayName = DisplayName,
            Email = Email,
            AvatarUrl = AvatarUrl,
        });
    }

    private void ClearSession()
    {
        _accessToken = null;
        _refreshToken = null;
        _expiresAt = default;
        DisplayName = Email = AvatarUrl = null;
        try { File.Delete(AuthFile); } catch { /* best effort */ }
    }

    private static StoredAuth? LoadStored()
    {
        try
        {
            if (!File.Exists(AuthFile)) return null;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(AuthFile), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredAuth>(plain);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveStored(StoredAuth auth)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AuthFile)!);
        byte[] enc = ProtectedData.Protect(
            JsonSerializer.SerializeToUtf8Bytes(auth), null, DataProtectionScope.CurrentUser);

        // The token file can be momentarily locked (another instance, AV, indexer). A failed
        // write must never break sign-in: the session is already live in memory — persisting is
        // a convenience for next launch. Retry briefly, then give up silently.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.WriteAllBytes(AuthFile, enc);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(150);
            }
            catch
            {
                return; // locked/denied — stay signed in for this session, just don't persist
            }
        }
    }

    private static string CreateCodeVerifier()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(48);
        return Base64Url(bytes);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string ResultPage(bool ok, string? error) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>LuaTools</title>
        <style>
          /* This page is served to the user's BROWSER, so it cannot use Themes/Colors.xaml — WPF
             resources don't reach it. The literals below are copies of the theme tokens, kept in
             sync by hand: SurfaceBase #0E0B14, SurfaceRaised #17122A, TextPrimary #F3EFFB,
             TextMuted #A395C4, Accent #A78BFA, Danger #F87171. Update both together. */
          body { background:#0E0B14; color:#F3EFFB; font-family:'Segoe UI',sans-serif;
                 display:flex; align-items:center; justify-content:center; height:100vh; margin:0; }
          .card { text-align:center; padding:2.5rem 3rem; background:#17122A;
                  border:1px solid rgba(237,233,254,.10); border-radius:14px; }
          h1 { font-size:1.3rem; margin:0 0 .5rem; color:{{(ok ? "#A78BFA" : "#F87171")}}; }
          p { color:#A395C4; font-size:.95rem; margin:0; }
        </style></head>
        <body><div class="card">
          <h1>{{(ok ? "Signed in!" : "Sign-in failed")}}</h1>
          <p>{{(ok ? "You can close this tab and return to LuaTools." : WebUtility.HtmlEncode(error ?? "Please try again from the app."))}}</p>
        </div></body></html>
        """;
}

public class AuthException(string message) : Exception(message);
