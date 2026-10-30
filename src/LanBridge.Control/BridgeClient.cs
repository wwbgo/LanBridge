using System.IO.Pipes;
using System.Security.Principal;
using LanBridge.Core;

namespace LanBridge.Control;

public sealed class BridgeClient
{
    private readonly string pipeName;
    private readonly TimeSpan timeout;
    public BridgeClient() : this(PipeProtocol.PipeName(PipeProtocol.CurrentOwnerSid)) { }
    public BridgeClient(string pipeName, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        this.pipeName = pipeName;
        this.timeout = timeout ?? PipeProtocol.RequestTimeout;
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<BridgeState> GetStateAsync(CancellationToken ct = default) =>
        (await SendAsync(new("getState"), ct)).State ?? throw new InvalidDataException("后端未返回状态。");
    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        var state = await GetStateAsync(ct);
        if (!state.Mode.Equals("Console", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前后台为 Windows 服务；请使用服务管理器停止服务，托盘不会擅自操作系统服务。");
        if (state.ProcessId <= 0) throw new InvalidOperationException("后台版本不支持安全退出，请先更新并重新启动后台。");
        if (string.IsNullOrWhiteSpace(state.ProcessPath) || state.ProcessStartTimeUtcTicks <= 0)
            throw new InvalidOperationException("后台版本不支持安全身份校验，请先更新并重新启动后台。");
        using var process = System.Diagnostics.Process.GetProcessById(state.ProcessId);
        string actualPath = process.MainModule?.FileName ?? "";
        long actualStart = process.StartTime.ToUniversalTime().Ticks;
        if (!string.Equals(actualPath, state.ProcessPath, StringComparison.OrdinalIgnoreCase) || actualStart != state.ProcessStartTimeUtcTicks)
            throw new InvalidOperationException("后台进程身份校验失败；为避免关闭错误进程，未发送退出请求。");
        _ = await SendAsync(new("shutdown"), ct);
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(8), ct); }
        catch (TimeoutException) { throw new TimeoutException("后台已停止监听，但进程尚未退出；请重试退出或检查后台日志。"); }
    }
    public async Task SaveRuleAsync(ForwardRule rule, CancellationToken ct = default) =>
        _ = await SendAsync(new("saveRule", Rule: rule ?? throw new ArgumentNullException(nameof(rule))), ct);
    public async Task DeleteRuleAsync(Guid id, CancellationToken ct = default) =>
        _ = await SendAsync(new("deleteRule", Id: id), ct);
    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default) =>
        _ = await SendAsync(new("setEnabled", Id: id, Enabled: enabled), ct);
    /// <summary>Advisory local bind check; saving remains authoritative. Does not probe the target.</summary>
    public async Task<ListenCheckResult> CheckListenAsync(ForwardRule rule, CancellationToken ct = default) =>
        (await SendAsync(new("checkListen", Rule: rule ?? throw new ArgumentNullException(nameof(rule))), ct)).ListenCheck
            ?? new(ListenCheckStatus.Unknown, "后端未返回监听检查结果；请更新后台，保存时仍会实际绑定。");
    public async Task<string> ProbeAsync(ForwardRule rule, CancellationToken ct = default) =>
        (await SendAsync(new("probe", Rule: rule ?? throw new ArgumentNullException(nameof(rule))), ct)).Probe
            ?? throw new InvalidDataException("后端未返回探测结果。");
    public async Task<IReadOnlyList<VirtualMachineInfo>> GetVirtualMachinesAsync(CancellationToken ct = default) =>
        (await SendAsync(new("getVirtualMachines"), ct)).VirtualMachines
            ?? throw new InvalidDataException("后端未返回虚拟机列表。");

    private async Task<BridgeResponse> SendAsync(BridgeRequest request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            // Identification only: the server must not impersonate our token to access other resources.
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeAccessRights.ReadWrite | PipeAccessRights.ReadPermissions,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None);
            await pipe.ConnectAsync(1500, deadline.Token);
            VerifyServer(pipe);
            await PipeProtocol.WriteAsync(pipe, request, deadline.Token);
            var response = await PipeProtocol.ReadAsync<BridgeResponse>(pipe, deadline.Token);
            if (!response.Success) throw new InvalidOperationException(response.Error ?? "后端操作失败。");
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("LanBridge 后端请求超时。"); }
        catch (TimeoutException ex) { throw new TimeoutException("无法连接 LanBridge 后端或请求超时。", ex); }
    }

    private static void VerifyServer(NamedPipeClientStream pipe)
    {
        // Reading the kernel object's owner avoids requiring token-query access to a SYSTEM service.
        // Only the current account or SYSTEM may own our endpoint; ACL spoofing by another account is rejected.
        string? sid = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value;
        if (sid != PipeProtocol.CurrentOwnerSid && sid != "S-1-5-18")
            throw new UnauthorizedAccessException("拒绝连接：管道服务端不是当前用户或 SYSTEM。");
    }
}
