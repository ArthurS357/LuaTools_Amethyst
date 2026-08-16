using System.Text.RegularExpressions;

namespace LuaToolsGui.Services;

/// <summary>
/// Strips credentials out of text that is about to be written to a log file.
///
/// <para>
/// The crash log records <c>Exception.ToString()</c>, and some exception messages in this app are built
/// from raw HTTP response bodies — <c>AuthService</c> throws
/// <c>$"Token exchange failed ({code}): {body}"</c>, where <c>body</c> is Supabase's token response and
/// therefore contains a live access token and refresh token. Written verbatim, a single failed sign-in
/// would leave working credentials sitting in a plain-text file in the user's roaming profile.
/// </para>
///
/// <para>
/// This is a redaction pass, not an encoder: it is deliberately eager, and losing some diagnostic detail
/// is the correct trade. Patterns are matched case-insensitively with a bounded timeout so a pathological
/// input cannot turn the crash logger itself into a hang.
/// </para>
/// </summary>
internal static partial class LogSanitizer
{
    public const string Redacted = "[REDACTED]";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>JSON/query fields that carry a secret, e.g. <c>"access_token":"ey…"</c> or
    /// <c>?api_key=smm_…</c>. The value is replaced, the field name is kept so the log still shows what
    /// happened.</summary>
    [GeneratedRegex(
        """(?<field>"?(?:access_token|refresh_token|id_token|provider_token|provider_refresh_token|token_hash|code_verifier|auth_code|api_key|apikey|password|client_secret|authorization)"?\s*[:=]\s*"?)(?<value>[^"'\s,&}]+)""",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex SecretFieldRegex();

    /// <summary>Bare JWTs (Supabase access tokens, the Supabase anon key). Three base64url segments.</summary>
    [GeneratedRegex(
        @"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\b",
        RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex JwtRegex();

    /// <summary>Hubcap API keys: "smm_" followed by a long hex run.</summary>
    [GeneratedRegex(@"\bsmm_[0-9a-fA-F]{16,}\b", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex HubcapKeyRegex();

    /// <summary>An <c>Authorization: Bearer …</c> header value.</summary>
    [GeneratedRegex(@"\bBearer\s+[^\s""',;}]+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex BearerRegex();

    /// <summary>Redact anything that looks like a credential. Never throws — a sanitizer that can fail is
    /// worse than useless, because it fails exactly when something is already going wrong.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        try
        {
            string result = text;
            result = JwtRegex().Replace(result, Redacted);
            result = HubcapKeyRegex().Replace(result, Redacted);
            result = BearerRegex().Replace(result, "Bearer " + Redacted);
            result = SecretFieldRegex().Replace(result, m => m.Groups["field"].Value + Redacted);
            return result;
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological input. Drop the text entirely rather than risk emitting an unredacted secret.
            return "[REDACTED: sanitiser timed out]";
        }
    }

    /// <summary>Sanitize an exception for logging, including its full chain of inner exceptions.</summary>
    public static string Sanitize(Exception? ex) =>
        ex is null ? "(no exception)" : Sanitize(ex.ToString());
}
