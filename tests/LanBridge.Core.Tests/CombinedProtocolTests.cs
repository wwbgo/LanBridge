using System.Net;
using System.Net.Sockets;
using System.Text;
using LanBridge.Core;
using Xunit;

namespace LanBridge.Core.Tests;

public sealed class CombinedProtocolTests
{
    private const TransportProtocol CombinedProtocol = (TransportProtocol)2;

    internal static int FreeCombinedPort()
    {
        for (int attempt = 0; attempt < 128; attempt++)
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            using var tcp = new TcpListener(IPAddress.Loopback, port);
            try { tcp.Start(); return port; }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied) { }
        }
        throw new InvalidOperationException("No available loopback port for both TCP and UDP.");
    }

    [Fact]
    public async Task Combined_rule_forwards_tcp_and_udp_simultaneously_and_stop_releases_both_ports()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int targetPort = FreeCombinedPort();
        using var tcpTarget = new TcpListener(IPAddress.Loopback, targetPort);
        using var udpTarget = new UdpClient(new IPEndPoint(IPAddress.Loopback, targetPort));
        tcpTarget.Start();
        int listenPort = FreeCombinedPort();
        var rule = ValidationTests.Valid with
        {
            ListenPort = listenPort,
            TargetPort = targetPort,
            Protocol = CombinedProtocol
        };

        var tcpEcho = Task.Run(async () =>
        {
            using var peer = await tcpTarget.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream();
            byte[] request = new byte[Encoding.UTF8.GetByteCount("combined tcp")];
            await stream.ReadExactlyAsync(request, timeout.Token);
            await stream.WriteAsync(request, timeout.Token);
        });
        var udpEcho = Task.Run(async () =>
        {
            var packet = await udpTarget.ReceiveAsync(timeout.Token);
            await udpTarget.SendAsync(packet.Buffer, packet.RemoteEndPoint, timeout.Token);
        });

        await using var engine = new ForwardingEngine();
        await engine.StartAsync(rule, timeout.Token);

        byte[] tcpPayload = Encoding.UTF8.GetBytes("combined tcp");
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, listenPort, timeout.Token);
        await tcpClient.GetStream().WriteAsync(tcpPayload, timeout.Token);
        byte[] tcpReply = new byte[tcpPayload.Length];
        await tcpClient.GetStream().ReadExactlyAsync(tcpReply, timeout.Token);

        byte[] udpPayload = Encoding.UTF8.GetBytes("combined udp");
        using var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await udpClient.SendAsync(udpPayload, new IPEndPoint(IPAddress.Loopback, listenPort), timeout.Token);
        byte[] udpReply = (await udpClient.ReceiveAsync(timeout.Token)).Buffer;

        Assert.Equal(tcpPayload, tcpReply);
        Assert.Equal(udpPayload, udpReply);
        await Task.WhenAll(tcpEcho, udpEcho);
        Assert.True(Assert.Single(engine.GetStatuses()).Running);

        await engine.StopAsync(rule.Id).WaitAsync(TimeSpan.FromSeconds(3));
        var stopped = Assert.Single(engine.GetStatuses());
        Assert.False(stopped.Running);
        Assert.Equal(0, stopped.ActiveConnections);
        Assert.Null(stopped.Error);

        using var tcpReplacement = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ExclusiveAddressUse = true
        };
        tcpReplacement.Bind(new IPEndPoint(IPAddress.Loopback, listenPort));
        using var udpReplacement = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = true
        };
        udpReplacement.Bind(new IPEndPoint(IPAddress.Loopback, listenPort));
    }

    [Theory]
    [InlineData(TransportProtocol.Tcp, TransportProtocol.TcpUdp)]
    [InlineData(TransportProtocol.Udp, TransportProtocol.TcpUdp)]
    [InlineData(TransportProtocol.TcpUdp, TransportProtocol.Tcp)]
    [InlineData(TransportProtocol.TcpUdp, TransportProtocol.Udp)]
    public async Task Changing_protocol_on_same_rule_and_port_rebinds_successfully(TransportProtocol before, TransportProtocol after)
    {
        var rule = ValidationTests.Valid with { ListenPort = FreeCombinedPort(), TargetPort = FreeCombinedPort(), Protocol = before };
        await using var engine = new ForwardingEngine();
        await engine.StartAsync(rule);
        await engine.StartAsync(rule with { Protocol = after });
        Assert.True(Assert.Single(engine.GetStatuses()).Running);
        await engine.StopAsync(rule.Id);
    }

    [Fact]
    public void Combined_protocol_is_valid_but_unknown_protocol_is_rejected()
    {
        Assert.Empty(RuleValidator.Validate(ValidationTests.Valid with { Protocol = CombinedProtocol }));
        Assert.NotEmpty(RuleValidator.Validate(ValidationTests.Valid with { Protocol = (TransportProtocol)42 }));
    }

    [Fact]
    public async Task Combined_protocol_roundtrips_as_tcpUdp_without_changing_existing_enum_json()
    {
        string path = Path.Combine(Path.GetTempPath(), "LanBridgeCoreTests", Guid.NewGuid().ToString("N"), "rules.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var store = new RuleStore(path);
            var combined = ValidationTests.Valid with { Protocol = CombinedProtocol };
            await store.SaveAsync(new[] { combined });
            string combinedJson = await File.ReadAllTextAsync(path);
            Assert.Contains("\"protocol\":\"tcpUdp\"", combinedJson);
            Assert.Equal(combined, Assert.Single(await store.LoadAsync()));

            var tcp = combined with { Protocol = TransportProtocol.Tcp };
            await store.SaveAsync(new[] { tcp });
            string tcpJson = await File.ReadAllTextAsync(path);
            Assert.Contains("\"protocol\":\"Tcp\"", tcpJson);
            Assert.Equal(tcp, Assert.Single(await store.LoadAsync()));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
