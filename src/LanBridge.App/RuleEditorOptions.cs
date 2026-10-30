using System.Globalization;
using System.Net;
using LanBridge.Core;

namespace LanBridge.App;

/// <summary>Pure option and input policies shared by the editor and its standalone tests.</summary>
internal static class RuleEditorOptions
{
    internal sealed record PortChoice(int Port, string Label)
    {
        public override string ToString() => $"{Port.ToString(CultureInfo.InvariantCulture)} · {Label}";
    }

    internal static IReadOnlyList<PortChoice> CommonPorts { get; } = Array.AsReadOnly(new[]
    {
        new PortChoice(22, "SSH"), new PortChoice(53, "DNS"),
        new PortChoice(80, "HTTP"), new PortChoice(443, "HTTPS"),
        new PortChoice(1433, "SQL Server"), new PortChoice(3306, "MySQL"),
        new PortChoice(3389, "RDP"), new PortChoice(5432, "PostgreSQL"),
        new PortChoice(5900, "VNC"), new PortChoice(6379, "Redis"),
        new PortChoice(8000, "HTTP 开发"), new PortChoice(8080, "HTTP 备用"),
        new PortChoice(8443, "HTTPS 备用")
    });

    internal static bool TryParsePort(string? text, out int port)
    {
        var value = text?.Trim();
        var choice = CommonPorts.FirstOrDefault(choice => string.Equals(choice.ToString(), value, StringComparison.Ordinal));
        if (choice is not null)
        {
            port = choice.Port;
            return true;
        }
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
    }

    internal static IReadOnlyList<string> ListenAddresses(IEnumerable<string> upInterfaceAddresses, string? original)
    {
        var addresses = new List<string> { "127.0.0.1", "0.0.0.0", "::1", "::" };
        foreach (var address in upInterfaceAddresses)
            if (IPAddress.TryParse(address, out _) && !addresses.Contains(address, StringComparer.OrdinalIgnoreCase))
                addresses.Add(address);
        if (!string.IsNullOrWhiteSpace(original) && !addresses.Contains(original, StringComparer.OrdinalIgnoreCase))
            addresses.Add(original);
        return addresses.AsReadOnly();
    }

    internal static bool ShouldShowDiscoveryFeedback(int endpointCount, IReadOnlyList<string>? warnings) =>
        endpointCount == 0 || warnings is { Count: > 0 };

    internal static int ProtocolIndex(TransportProtocol protocol) => protocol switch
    {
        TransportProtocol.Tcp => 0,
        TransportProtocol.Udp => 1,
        TransportProtocol.TcpUdp => 2,
        _ => 0
    };

    internal static TransportProtocol ProtocolAt(int index) => index switch
    {
        0 => TransportProtocol.Tcp,
        1 => TransportProtocol.Udp,
        2 => TransportProtocol.TcpUdp,
        _ => TransportProtocol.Tcp
    };
}
