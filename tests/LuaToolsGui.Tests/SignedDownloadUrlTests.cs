using System.IO;
using System.Threading;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The Denuvo fix flow downloads from a URL the API hands back, and extracts the result into the game
/// folder. <see cref="LuaToolsApiClient.IsFollowableSignedUrl"/> refuses what no legitimate signed link
/// is; <see cref="LuaToolsApiClient.DownloadFromUrlAsync"/> must apply it before any request is made.
/// </summary>
public sealed class SignedDownloadUrlTests
{
    [Theory]
    // The shape R2 presigned links take (host is per-account, so this is representative, not pinned).
    [InlineData("https://0123456789abcdef.r2.cloudflarestorage.com/fixes/123/fix.zip?X-Amz-Signature=abc")]
    [InlineData("https://files.lua.tools/fix.zip?sig=abc")]
    // A host that merely STARTS like a loopback or metadata address is still a named host.
    [InlineData("https://localhost-cdn.example.com/fix.zip")]
    [InlineData("https://169-254-169-254.example.com/fix.zip")]
    public void LegitimateSignedLinks_AreFollowed(string url) =>
        LuaToolsApiClient.IsFollowableSignedUrl(url).Should().BeTrue();

    [Theory]
    [InlineData("http://0123456789abcdef.r2.cloudflarestorage.com/fixes/123/fix.zip")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://127.0.0.1/fix.zip")]
    [InlineData("https://[::1]/fix.zip")]
    [InlineData("https://[fe80::1]/fix.zip")]
    [InlineData("https://192.168.0.10/fix.zip")]
    [InlineData("https://localhost/fix.zip")]
    [InlineData("file:///C:/Windows/System32/fix.zip")]
    [InlineData("ftp://files.lua.tools/fix.zip")]
    [InlineData("/api/denuvo/fix.zip")]
    [InlineData("")]
    public void LinksNoSignedUrlTakes_AreRefused(string url) =>
        LuaToolsApiClient.IsFollowableSignedUrl(url).Should().BeFalse();

    /// <summary>
    /// Regression: the download used to follow whatever the API returned. A refused URL must fail with
    /// an <see cref="ApiException"/> before any connection is attempted (an attempt here would surface
    /// as HttpRequestException instead), and its host must reach the log without the signed path/query.
    /// </summary>
    [Fact]
    public async Task RefusedLink_FailsBeforeAnyRequest_AndLogsOnlyItsHost()
    {
        // Unused by DownloadFromUrlAsync; the client is built only to reach that method.
        var client = new LuaToolsApiClient(null!, null!, null!, null!, null!);
        string host = $"f09-{Guid.NewGuid():N}.example";
        string signature = $"sig{Guid.NewGuid():N}";
        string url = $"http://{host}/fix.zip?X-Amz-Signature={signature}";

        var act = () => client.DownloadFromUrlAsync(url, "fix.zip", null, CancellationToken.None);

        await act.Should().ThrowAsync<ApiException>();
        string log = TestLogRedirect.ReadLog();
        log.Should().Contain($"Signed download refused: http://{host}");
        log.Should().NotContain(signature);
    }
}
