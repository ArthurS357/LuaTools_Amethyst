using System.IO;
using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// F-06. Cancelling <c>GetContextAsync().WaitAsync(ct)</c> abandons the accept; stopping the listener then
/// faulted it with 995 (ERROR_OPERATION_ABORTED), unobserved, and every shutdown wrote it to crash.log.
/// </summary>
public sealed class ListenerShutdownTests
{
    private static HttpListener StartListener()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var l = new HttpListener();
        l.Prefixes.Add($"http://127.0.0.1:{port}/");
        l.Start();
        return l;
    }

    [Fact]
    public async Task Real_cancel_then_Stop_faults_the_accept_with_995_and_AcceptAsync_stays_silent()
    {
        var listener = StartListener();
        using var cts = new CancellationTokenSource();
        var accept = HttpServerService.AcceptAsync(listener, cts.Token);

        await cts.CancelAsync();
        await accept.Awaiting(a => a).Should().ThrowAsync<OperationCanceledException>();
        listener.Stop();
        listener.Close();
        await Task.Delay(200);   // the abandoned accept faults on the IO completion

        // Shutdown is not a cancelled-accept failure: nothing is logged for it (the raw 995 text would be).
        File.ReadAllText(AppLog.FilePath).Should().NotContain("HTTP accept faulted after cancel: System.Net.HttpListenerException (995)");
    }

    [Fact]
    public async Task A_995_on_the_abandoned_accept_is_observed_and_not_logged()
    {
        var pending = Task.FromException(new HttpListenerException(995, "marker-995-" + Guid.NewGuid().ToString("N")));
        string marker = ((HttpListenerException)pending.Exception!.InnerException!).Message;

        await HttpServerService.ObserveAbandoned(pending);

        File.ReadAllText(AppLog.FilePath).Should().NotContain(marker);
    }

    [Fact]
    public async Task A_real_fault_on_the_abandoned_accept_is_still_reported()
    {
        string marker = "marker-real-" + Guid.NewGuid().ToString("N");
        var pending = Task.FromException(new HttpListenerException(5, marker));

        await HttpServerService.ObserveAbandoned(pending);

        File.ReadAllText(AppLog.FilePath).Should().Contain(marker);
    }

    [Fact]
    public void Only_995_counts_as_the_shutdown_abort()
    {
        HttpServerService.IsShutdownAbort(new HttpListenerException(995)).Should().BeTrue();
        HttpServerService.IsShutdownAbort(new AggregateException(new HttpListenerException(995))).Should().BeTrue();
        HttpServerService.IsShutdownAbort(new HttpListenerException(5)).Should().BeFalse();
        HttpServerService.IsShutdownAbort(new IOException()).Should().BeFalse();
        HttpServerService.IsShutdownAbort(null).Should().BeFalse();
    }
}
