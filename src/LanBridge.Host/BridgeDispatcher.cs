using LanBridge.Control;
using LanBridge.Core;
using System.Net.Sockets;

namespace LanBridge.Host;

public sealed class BridgeDispatcher(BridgeCoordinator coordinator, Action? shutdownCompleted = null)
{
    public void CompleteShutdown() => shutdownCompleted?.Invoke();
    public async Task<BridgeResponse> DispatchAsync(BridgeRequest request, CancellationToken ct = default)
    {
        // Reject ambiguous payloads; the pipe is a narrow command API, never a script runner.
        switch (request)
        {
            case { Command: "getState", Rule: null, Id: null, Enabled: null }:
                return new(true, State: await coordinator.GetStateAsync(ct));
            case { Command: "saveRule", Rule: not null, Id: null, Enabled: null }:
                await coordinator.SaveRuleAsync(request.Rule, ct); return new(true);
            case { Command: "deleteRule", Rule: null, Id: not null, Enabled: null } when request.Id != Guid.Empty:
                await coordinator.DeleteRuleAsync(request.Id.Value, ct); return new(true);
            case { Command: "setEnabled", Rule: null, Id: not null, Enabled: not null } when request.Id != Guid.Empty:
                await coordinator.SetEnabledAsync(request.Id.Value, request.Enabled.Value, ct); return new(true);
            case { Command: "checkListen", Rule: not null, Id: null, Enabled: null }:
                return new(true, ListenCheck: await coordinator.CheckListenAsync(request.Rule, ct));
            case { Command: "probe", Rule: not null, Id: null, Enabled: null }:
                return new(true, Probe: await ProbeAsync(request.Rule, ct));
            case { Command: "getVirtualMachines", Rule: null, Id: null, Enabled: null }:
                return new(true, VirtualMachines: await HyperVDiscovery.GetAsync(ct));
            case { Command: "shutdown", Rule: null, Id: null, Enabled: null }:
                await coordinator.ShutdownAsync(ct); return new(true);
            default:
                throw new ArgumentException("未知命令或命令参数无效。");
        }
    }

    private static async Task<string> ProbeAsync(ForwardRule rule, CancellationToken ct)
    {
        var errors = RuleValidator.Validate(rule);
        if (errors.Count != 0) throw new ArgumentException(string.Join("；", errors));
        if (rule.Protocol == TransportProtocol.Udp)
            return "UDP 无连接，不能通过 TCP 测试确认 UDP 可达；请使用目标应用实际发送并检查响应。";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var socket = new TcpClient();
        bool combined = rule.Protocol == TransportProtocol.TcpUdp;
        string UdpNote(string text) => combined ? text + "；UDP 未验证，请使用实际 UDP 客户端检查响应。" : text;
        try
        {
            await socket.ConnectAsync(rule.TargetHost, rule.TargetPort, deadline.Token);
            return UdpNote("TCP 目标连接成功；这不代表远程设备能够访问本机监听端口，也不验证应用协议。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return UdpNote("TCP 目标连接超时（3 秒）。"); }
        catch (SocketException ex) { return UdpNote("TCP 目标连接失败：" + ex.SocketErrorCode); }
    }
}
