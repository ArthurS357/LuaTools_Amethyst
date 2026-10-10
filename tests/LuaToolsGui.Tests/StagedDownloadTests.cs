using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// <see cref="LuaToolsApiClient.SaveResponseAsync"/> — the one place every lua.tools download is streamed
/// to disk (queue, Fixes page, depot manifests). A body that fails, is cancelled or goes silent must not
/// leave its partial in %TEMP%, and silence must end the download instead of hanging it.
/// </summary>
public sealed class StagedDownloadTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"stageddl_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    private enum Tail { End, Throw, Hang }

    /// <summary>A body that hands out <paramref name="chunks"/> one-byte chunks, each after
    /// <paramref name="delay"/>, then ends, throws, or goes silent.</summary>
    private sealed class ScriptedBody(int chunks, TimeSpan delay, Tail tail) : Stream
    {
        private int _sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_sent < chunks)
            {
                await Task.Delay(delay, ct);
                _sent++;
                buffer.Span[0] = (byte)'x';
                return 1;
            }
            switch (tail)
            {
                case Tail.Throw: throw new IOException("connection reset");
                case Tail.Hang: await Task.Delay(Timeout.Infinite, ct); return 0;
                default: return 0;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Response(Stream body, string? serverName = null)
    {
        var content = new StreamContent(body);
        if (serverName is not null)
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = serverName };
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
    }

    private string[] Staged() => Directory.Exists(_folder) ? Directory.GetFiles(_folder) : [];

    // Every await below is bounded, so a regression that reintroduces the hang fails the test instead of
    // stalling the suite.
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CompleteBody_IsStagedUnderTheServersName()
    {
        using var res = Response(new ScriptedBody(3, TimeSpan.Zero, Tail.End), "386940_19234567.lua");

        var file = await LuaToolsApiClient.SaveResponseAsync(res, "386940.lua", null, CancellationToken.None, _folder)
            .WaitAsync(Guard);

        file.FileName.Should().Be("386940_19234567.lua");
        File.ReadAllText(file.FilePath).Should().Be("xxx");
        // The staged name is where build identity comes from — it must survive staging intact.
        LuaInstaller.BuildIdFromFileName(file.FilePath).Should().Be("19234567");
    }

    [Fact]
    public async Task BodyThatFailsMidway_LeavesNoPartialBehind()
    {
        using var res = Response(new ScriptedBody(2, TimeSpan.Zero, Tail.Throw), "1.zip");

        var act = () => LuaToolsApiClient.SaveResponseAsync(res, "1.zip", null, CancellationToken.None, _folder)
            .WaitAsync(Guard);

        await act.Should().ThrowAsync<IOException>();
        Staged().Should().BeEmpty();
    }

    [Fact]
    public async Task CancelledBody_LeavesNoPartialBehind()
    {
        using var res = Response(new ScriptedBody(2, TimeSpan.Zero, Tail.Hang), "1.zip");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var act = () => LuaToolsApiClient.SaveResponseAsync(res, "1.zip", null, cts.Token, _folder, Guard * 2)
            .WaitAsync(Guard);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Staged().Should().BeEmpty();
    }

    [Fact]
    public async Task SilentBody_IsAbandonedAsAStall_NotAUserCancel()
    {
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("en");   // asserts the English text
        using var res = Response(new ScriptedBody(2, TimeSpan.Zero, Tail.Hang), "1.zip");

        var act = () => LuaToolsApiClient.SaveResponseAsync(
                res, "1.zip", null, CancellationToken.None, _folder, TimeSpan.FromMilliseconds(200))
            .WaitAsync(Guard);

        // ApiException, not OperationCanceledException: the queue reads the latter as the user cancelling.
        (await act.Should().ThrowAsync<ApiException>()).WithMessage("*stopped sending data*");
        Staged().Should().BeEmpty();
    }

    /// <summary>The stall deadline is per read, not per download: a body slower in total than the
    /// deadline, but never silent for that long, must complete.</summary>
    [Fact]
    public async Task SlowButSteadyBody_IsNotMistakenForAStall()
    {
        using var res = Response(new ScriptedBody(8, TimeSpan.FromMilliseconds(100), Tail.End), "1.zip");

        var file = await LuaToolsApiClient.SaveResponseAsync(
                res, "1.zip", null, CancellationToken.None, _folder, TimeSpan.FromMilliseconds(500))
            .WaitAsync(Guard);

        File.ReadAllText(file.FilePath).Should().Be("xxxxxxxx");
    }

    [Theory]
    [InlineData("386940.zip", "386940.zip")]
    [InlineData("\"386940_123.lua\"", "386940_123.lua")]
    [InlineData("..\\..\\evil.dll", ".._.._evil.dll")]   // separators replaced: stays inside the folder
    [InlineData("a/b.zip", "a_b.zip")]
    [InlineData("CONSOLE.zip", "CONSOLE.zip")]             // looks like a device name, is not one
    [InlineData("COM10.zip", "COM10.zip")]
    [InlineData("nullable.lua", "nullable.lua")]
    public void StagedFileName_KeepsUsableServerNames(string serverName, string expected) =>
        LuaToolsApiClient.StagedFileName(serverName, "fallback.zip").Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"\"")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("  ")]
    [InlineData("CON")]
    [InlineData("nul.zip")]
    [InlineData("Com1.lua")]
    [InlineData("LPT9.tar.gz")]
    [InlineData("aux .zip")]
    public void StagedFileName_FallsBackWhenWindowsWouldNotTreatItAsAPlainFile(string? serverName) =>
        LuaToolsApiClient.StagedFileName(serverName, "fallback.zip").Should().Be("fallback.zip");
}
