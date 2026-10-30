using System.IO.Pipes;
using LanBridge.Control;

namespace LanBridge.Host;

public sealed class PipeControlServer(string ownerSid, BridgeDispatcher dispatcher, BoundedLog log, string? pipeName = null)
{
    public Task RunAsync(CancellationToken ct) => Task.WhenAll(Enumerable.Range(0, 4).Select(_ => WorkerAsync(ct)));

    private async Task WorkerAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            using var pipe = NamedPipeServerStreamAcl.Create(pipeName ?? PipeProtocol.PipeName(ownerSid), PipeDirection.InOut, 4,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 8192, 8192, HostSecurity.CreatePipeSecurity(ownerSid));
            try
            {
                await pipe.WaitForConnectionAsync(stop);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                deadline.CancelAfter(PipeProtocol.RequestTimeout);
                BridgeResponse response;
                bool shutdown = false;
                try
                {
                    var request = await PipeProtocol.ReadAsync<BridgeRequest>(pipe, deadline.Token);
                    response = await dispatcher.DispatchAsync(request, deadline.Token);
                    shutdown = request.Command == "shutdown" && response.Success;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log.Write("Request rejected: " + ex.GetType().Name);
                    response = new(false, SafeError(ex));
                }
                try { await PipeProtocol.WriteAsync(pipe, response, deadline.Token); }
                catch (InvalidDataException)
                {
                    log.Write("Pipe response size limit exceeded");
                    await PipeProtocol.WriteAsync(pipe, new BridgeResponse(false, "后端状态超过管道安全上限；请缩小规则配置或修复异常配置。"), deadline.Token);
                }
                finally { if (shutdown) dispatcher.CompleteShutdown(); }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { log.Write("Pipe request deadline exceeded"); }
            catch (IOException) { log.Write("Pipe client disconnected or invalid frame"); }
            catch (System.Text.Json.JsonException) { log.Write("Pipe invalid JSON"); }
        }
    }

    private static string SafeError(Exception ex) => ex switch
    {
        ArgumentException or InvalidOperationException or KeyNotFoundException or TimeoutException =>
            ex.Message.Length <= 1024 ? ex.Message : ex.Message[..1024],
        UnauthorizedAccessException => "后端权限不足；请检查配置文件和运行账户权限。",
        System.Net.Sockets.SocketException socket => "无法启动监听：" + socket.SocketErrorCode,
        InvalidDataException or System.Text.Json.JsonException => "配置或管道消息格式无效；原配置未覆盖。",
        IOException => "读写配置或管道失败；操作未提交，请检查文件访问权限和磁盘空间。",
        _ => "后端操作失败（" + ex.GetType().Name + "）；请查看后端日志。"
    };
}
