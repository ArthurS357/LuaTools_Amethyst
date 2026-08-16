using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers credential redaction in log output.
///
/// <para>
/// The concrete case this exists for: <c>AuthService</c> throws
/// <c>$"Token exchange failed ({code}): {body}"</c>, where <c>body</c> is Supabase's raw token response.
/// The global crash handler writes <c>Exception.ToString()</c> to a plain-text file in the user's roaming
/// profile, so without this a single failed sign-in leaves working credentials on disk.
/// </para>
/// </summary>
public class LogSanitizerTests
{
    private const string Jwt =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    [Fact]
    public void Redacts_a_supabase_token_response()
    {
        string body = $$"""{"access_token":"{{Jwt}}","refresh_token":"v1.MRq8v_v0Kk","expires_in":3600}""";

        string clean = LogSanitizer.Sanitize($"Token exchange failed (400): {body}");

        clean.Should().NotContain(Jwt);
        clean.Should().NotContain("v1.MRq8v_v0Kk");
        clean.Should().Contain("Token exchange failed (400)", "the diagnostic value must survive");
    }

    [Theory]
    [InlineData("access_token")]
    [InlineData("refresh_token")]
    [InlineData("token_hash")]
    [InlineData("code_verifier")]
    [InlineData("api_key")]
    [InlineData("password")]
    [InlineData("client_secret")]
    public void Redacts_known_secret_fields(string field)
    {
        string clean = LogSanitizer.Sanitize($$"""{"{{field}}":"s3cr3t-value-here"}""");

        clean.Should().NotContain("s3cr3t-value-here");
        clean.Should().Contain(field, "keeping the field name preserves the diagnostic");
        clean.Should().Contain(LogSanitizer.Redacted);
    }

    [Fact]
    public void Redacts_a_bare_jwt_anywhere_in_the_text() =>
        LogSanitizer.Sanitize($"apikey header was {Jwt} on that request")
            .Should().NotContain(Jwt).And.Contain(LogSanitizer.Redacted);

    [Fact]
    public void Redacts_a_hubcap_key() =>
        LogSanitizer.Sanitize("GET /api/v1/manifest/480?api_key=smm_0123456789abcdef0123456789abcdef")
            .Should().NotContain("smm_0123456789abcdef0123456789abcdef");

    [Fact]
    public void Redacts_an_authorization_header_entirely()
    {
        // "Authorization" is itself a known secret-bearing field, so the whole value after it is redacted —
        // the scheme token included. That is stricter than redacting only the credential, and the stricter
        // outcome is the correct one to lock in: the header name still survives for diagnostics.
        string clean = LogSanitizer.Sanitize($"Authorization: Bearer {Jwt}");

        clean.Should().NotContain(Jwt);
        clean.Should().StartWith("Authorization:");
        clean.Should().Contain(LogSanitizer.Redacted);
    }

    [Fact]
    public void Redacts_secrets_inside_a_query_string() =>
        LogSanitizer.Sanitize("https://db.lua.tools/auth/v1/verify?token_hash=abc123def&type=magiclink")
            .Should().NotContain("abc123def");

    [Fact]
    public void Walks_the_inner_exception_chain()
    {
        var inner = new InvalidOperationException($$"""{"refresh_token":"{{Jwt}}"}""");
        var outer = new InvalidOperationException("outer failure", inner);

        LogSanitizer.Sanitize(outer).Should().NotContain(Jwt);
    }

    // ── Must not destroy ordinary diagnostics ─────────────────────────────────

    [Theory]
    [InlineData("System.IO.IOException: The process cannot access the file 'C:\\Steam\\dwmapi.dll'")]
    [InlineData("HTTP POST /add-source/480")]
    [InlineData("Couldn't reach GitHub — check your connection and try again.")]
    public void Leaves_ordinary_messages_untouched(string message) =>
        LogSanitizer.Sanitize(message).Should().Be(message);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Handles_empty_input(string? text) =>
        LogSanitizer.Sanitize(text).Should().BeEmpty();

    [Fact]
    public void A_null_exception_does_not_throw() =>
        LogSanitizer.Sanitize((Exception?)null).Should().NotBeNullOrEmpty();
}
