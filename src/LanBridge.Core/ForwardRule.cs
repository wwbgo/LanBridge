using System.Net;
using System.Net.NetworkInformation;

namespace LanBridge.Core;

public enum TransportProtocol { Tcp, Udp, TcpUdp }

public sealed record ForwardRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string ListenAddress { get; init; } = "127.0.0.1";
    public int ListenPort { get; init; }
    public string TargetHost { get; init; } = "";
    public int TargetPort { get; init; }
    public TransportProtocol Protocol { get; init; } = TransportProtocol.Tcp;
    public bool Enabled { get; init; } = true;
    public bool AutoFirewall { get; init; }
}

public sealed record RuleStatus(Guid Id, bool Running, string? Error, int ActiveConnections, long BytesSent, long BytesReceived);

public static class RuleValidator
{
    public static IReadOnlyList<string> Validate(ForwardRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var errors = new List<string>();
        if (rule.Id == Guid.Empty) errors.Add("规则 ID 不能为空。");
        if (rule.Name is null || rule.Name.Length > 256) errors.Add("名称不能超过 256 个字符。");
        bool validAddress = IPAddress.TryParse(rule.ListenAddress, out var address);
        if (!validAddress) errors.Add("监听地址必须是有效 IPv4 或 IPv6 地址。");
        if (rule.ListenPort is < 1 or > 65535) errors.Add("监听端口必须为 1–65535。");
        if (rule.TargetPort is < 1 or > 65535) errors.Add("目标端口必须为 1–65535。");
        if (string.IsNullOrWhiteSpace(rule.TargetHost) || rule.TargetHost.Length > 253 ||
            Uri.CheckHostName(rule.TargetHost) == UriHostNameType.Unknown)
            errors.Add("目标必须是 IP 地址或主机名，不能包含协议或路径。");
        if (!Enum.IsDefined(rule.Protocol)) errors.Add("不支持的传输协议。");
        if (validAddress && rule.ListenPort == rule.TargetPort)
        {
            bool pointsToListener;
            if (IPAddress.TryParse(rule.TargetHost, out var target)) pointsToListener = IsSelfTarget(rule, target);
            else
            {
                string host = rule.TargetHost?.TrimEnd('.') ?? "";
                if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                    pointsToListener = IsSelfTarget(rule, IPAddress.Loopback) || IsSelfTarget(rule, IPAddress.IPv6Loopback);
                else
                {
                    var properties = IPGlobalProperties.GetIPGlobalProperties();
                    string fqdn = string.IsNullOrEmpty(properties.DomainName) ? properties.HostName : properties.HostName + "." + properties.DomainName;
                    bool localAlias = string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(host, properties.HostName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(host, fqdn, StringComparison.OrdinalIgnoreCase);
                    pointsToListener = localAlias && LocalAddresses().Any(a => IsSelfTarget(rule, a));
                }
            }
            if (pointsToListener) errors.Add("目标不能指回本机的同一监听端口。");
        }
        return errors;
    }

    internal static bool IsSelfTarget(ForwardRule rule, IPAddress target)
    {
        if (rule.ListenPort != rule.TargetPort) return false;
        var address = Normalize(IPAddress.Parse(rule.ListenAddress));
        target = Normalize(target);
        bool wildcard = address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
        return address.Equals(target) || (wildcard && address.AddressFamily == target.AddressFamily &&
            (IPAddress.IsLoopback(target) || LocalAddresses().Any(a => Normalize(a).Equals(target))));
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static IEnumerable<IPAddress> LocalAddresses() => new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }
        .Concat(NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address));
}
