using System.IO;
using System.Net.Http;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The store plugin's <c>/cancel/{appid}</c> for a headless add. It used to consult only the legacy
/// <c>/download</c> table, answer "Nothing to cancel", and let the add run on to a finished install.
/// </summary>
public sealed class PluginAddCancelTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Hubcap that never answers until the caller gives up — the add parks in its Check phase.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>The lua.tools client, queue and job factory are null: an add that got past the Check phase
    /// would fault on them and record an error, which is exactly what the assertions rule out.</summary>
    private PluginAddService Build(HttpMessageHandler hubcap) => new(
        new LuaToolsApiClient(null!, null!, null!, null!, null!),
        new HubcapService(hubcap, TimeSpan.FromSeconds(30)),
        new SettingsService(_dir) { HubcapApiKey = "smm_test" },
        new AuthService(),
        null!,
        null!);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Cancel_stops_an_add_before_it_reaches_the_download()
    {
        var handler = new HangingHandler();
        var service = Build(handler);
        service.Start(730, "Test Game");
        await handler.Entered.Task.WaitAsync(Timeout);
        var state = service.GetState(730)!;

        service.Cancel(730).Should().BeTrue();
        service.GetState(730).Should().BeNull("a cancelled add reads as idle, and a second /cancel finds nothing");

        var deadline = DateTime.UtcNow + Timeout;
        while (state.Checking && DateTime.UtcNow < deadline) await Task.Delay(20);
        state.Checking.Should().BeFalse("a poller must not spin on a cancelled add");
        state.Cts.IsCancellationRequested.Should().BeTrue();
        state.SourcesLoaded.Should().BeFalse();
        state.Error.Should().BeNull("a cancel is not a failure, and the null-backed download path never ran");
    }

    [Fact]
    public void Cancel_without_an_add_reports_nothing_to_cancel() =>
        Build(new HangingHandler()).Cancel(730).Should().BeFalse();
}
