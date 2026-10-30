using LanBridge.Core;

namespace LanBridge.App;

internal enum DiscoverySource { Manual, HyperV, Wsl, Containers }

internal static class UiPresentation
{
    internal static string DiscoverySourceLabel(DiscoverySource source) => source switch
    {
        DiscoverySource.Manual => "手动输入",
        DiscoverySource.HyperV => "Hyper-V",
        DiscoverySource.Wsl => "WSL2",
        DiscoverySource.Containers => "Docker / Podman",
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    internal static string DiscoverySourceHelp(DiscoverySource source) => source switch
    {
        DiscoverySource.Manual => "手动输入目标 IP 或主机名；无需发现即可继续。",
        DiscoverySource.HyperV => "仅在点击发现时按需请求一次性 UAC，读取 Hyper-V 虚拟机地址；不会提权桌面程序或后台转发。取消或拒绝 UAC 后仍可手动填写目标。",
        DiscoverySource.Wsl => "仅查询已运行的 WSL2 发行版；容器专用发行版请选 Docker / Podman。镜像网络可手动填写 127.0.0.1 和应用端口；候选地址尚未测试连通性。",
        DiscoverySource.Containers => "自动检查 Docker 和 Podman，合并已发布到 Windows 的端口；不会启动容器、使用容器内部 IP 或自动发布端口。",
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    internal static string Status(bool connected, bool enabled, bool running, string? error) =>
        !connected ? "已断开" : running ? "监听中" : !string.IsNullOrWhiteSpace(error) ? "启动失败" : enabled ? "等待启动" : "已停止";
    internal static string ProbeOutcome(string? result) =>
        result?.StartsWith("TCP 目标连接成功", StringComparison.Ordinal) == true
            ? result
            : result?.StartsWith("UDP 无连接", StringComparison.Ordinal) == true
                ? result
                : "目标连接测试未成功，请确认目标服务已启动，且目标地址和端口正确。";

    internal static string ProtocolLabel(TransportProtocol protocol) => protocol switch
    {
        TransportProtocol.Tcp => "TCP", TransportProtocol.Udp => "UDP", TransportProtocol.TcpUdp => "TCP/UDP",
        _ => "未知协议"
    };
    internal static string Endpoint(string host, int port) => host.Contains(':') ? $"[{host.Trim('[', ']')}]:{port}" : $"{host}:{port}";
}
