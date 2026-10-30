using System.Diagnostics;
using System.IO.Pipes;
using LanBridge.Control;
using LanBridge.Host;
using Xunit;

namespace LanBridge.Host.Tests;

public sealed class ClientShutdownTests
{
    [Fact]
    public async Task Service_rejection_precedes_any_process_handle_access()
    {
        var error = await RunAgainstState("Service", int.MaxValue, 1);
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("当前后台为 Windows 服务", error.Message);
    }

    [Fact]
    public async Task Reused_pid_with_wrong_start_time_is_rejected_before_shutdown()
    {
        var error = await RunAgainstState("Console", Environment.ProcessId, 1);
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("身份", error.Message);
    }

    private static async Task<Exception> RunAgainstState(string mode, int pid, long ticks)
    {
        string name = "LanBridge.Shutdown.Test." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int shutdowns = 0;
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 8192, 8192,
                        HostSecurity.CreatePipeSecurity(PipeProtocol.CurrentOwnerSid));
                    await pipe.WaitForConnectionAsync(stop.Token);
                    var request = await PipeProtocol.ReadAsync<BridgeRequest>(pipe, stop.Token);
                    if (request.Command == "shutdown") shutdowns++;
                    var state = request.Command == "getState"
                        ? new BridgeState([], [], mode, "", pid, Process.GetCurrentProcess().MainModule?.FileName ?? "", ticks)
                        : null;
                    await PipeProtocol.WriteAsync(pipe, new BridgeResponse(true, State: state), stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            var error = await Record.ExceptionAsync(() => new BridgeClient(name).ShutdownAsync());
            Assert.NotNull(error);
            Assert.Equal(0, shutdowns);
            return error;
        }
        finally { stop.Cancel(); await server; }
    }
}
