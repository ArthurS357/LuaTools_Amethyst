using System.IO;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Pins the fail-closed contract of <see cref="AssetIntegrity"/>.
///
/// <para>
/// This is what stands between a third-party download mirror (GithubProxy falls back to public proxies so
/// the app works in blocked regions) and a DLL being copied into the Steam root or an .exe being executed.
/// The bug it replaced was the shape <c>if (ParseDigest(a.Digest) is { } want &amp;&amp; sha != want) fail;</c>,
/// which skipped verification entirely whenever a release carried no digest — so the "no digest" cases
/// below are the important ones, not the mismatch cases.
/// </para>
/// </summary>
public class AssetIntegrityTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    public AssetIntegrityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string WriteFile(string content)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha256Of(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    // ── The fail-open bug this replaced ───────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sha256:")]
    public void A_missing_digest_does_not_verify(string? digest)
    {
        // The whole point: "nothing to compare against" is a FAILURE, not a pass. A digest-less API
        // response must never be enough to get an unverified binary placed next to Steam.
        string path = WriteFile("payload");

        AssetIntegrity.Matches(path, digest).Should().BeFalse();
    }

    [Theory]
    [InlineData("sha256:not-hex-at-all")]
    [InlineData("deadbeef")]                                              // too short
    [InlineData("sha256:zzzz567890123456789012345678901234567890123456789012345678901234")] // non-hex
    public void A_malformed_digest_does_not_verify(string digest) =>
        AssetIntegrity.Matches(WriteFile("payload"), digest).Should().BeFalse();

    // ── Happy path and mismatch ───────────────────────────────────────────────

    [Fact]
    public void Verifies_a_file_matching_its_published_digest()
    {
        string path = WriteFile("payload");

        AssetIntegrity.Matches(path, "sha256:" + Sha256Of("payload")).Should().BeTrue();
    }

    [Fact]
    public void Accepts_a_bare_digest_without_the_sha256_prefix() =>
        AssetIntegrity.Matches(WriteFile("payload"), Sha256Of("payload")).Should().BeTrue();

    [Fact]
    public void Digest_comparison_is_case_insensitive() =>
        AssetIntegrity.Matches(WriteFile("payload"), "SHA256:" + Sha256Of("payload").ToUpperInvariant())
            .Should().BeTrue();

    [Fact]
    public void Rejects_a_file_whose_bytes_were_swapped()
    {
        // The mirror-substitution scenario: correct digest published, different bytes delivered.
        string path = WriteFile("malicious payload");

        AssetIntegrity.Matches(path, "sha256:" + Sha256Of("expected payload")).Should().BeFalse();
    }

    [Fact]
    public void A_single_flipped_byte_is_detected() =>
        AssetIntegrity.Matches(WriteFile("payloaD"), Sha256Of("payload")).Should().BeFalse();

    [Fact]
    public void A_missing_file_does_not_verify() =>
        AssetIntegrity.Matches(Path.Combine(_dir, "does-not-exist.bin"), Sha256Of("payload"))
            .Should().BeFalse();

    // ── ParseDigest ───────────────────────────────────────────────────────────

    [Fact]
    public void ParseDigest_strips_the_algorithm_prefix_and_lowercases()
    {
        string hex = Sha256Of("payload");

        AssetIntegrity.ParseDigest("sha256:" + hex.ToUpperInvariant()).Should().Be(hex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256:")]
    [InlineData("abc")]
    public void ParseDigest_returns_null_for_anything_unusable(string? digest) =>
        AssetIntegrity.ParseDigest(digest).Should().BeNull();

    [Fact]
    public void Sha256OfFile_matches_a_known_vector()
    {
        // Empty input has a well-known SHA-256; a wrong hash function or encoding would show up here.
        string path = WriteFile("");

        AssetIntegrity.Sha256OfFile(path)
            .Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }
}
