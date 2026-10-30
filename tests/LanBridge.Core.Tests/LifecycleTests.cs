using System.Net;
using System.Net.Sockets;
using LanBridge.Core;
using Xunit;

namespace LanBridge.Core.Tests;

public class LifecycleTests
{
    internal static async Task EventuallyAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(10, token);
    }

    [Theory]
    [InlineData(TransportProtocol.Tcp)]
    [InlineData(TransportProtocol.Udp)]
    public async Task Stop_drains_active_workers_releases_port_and_allows_restart(TransportProtocol protocol)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var tcpTarget = new TcpListener(IPAddress.Loopback, 0);
        tcpTarget.Start();
        using var udpTarget = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var rule = ValidationTests.Valid with
        {
            Protocol = protocol,
            ListenPort = protocol == TransportProtocol.Tcp ? TcpTests.FreePort() : UdpTests.FreePort(),
            TargetPort = protocol == TransportProtocol.Tcp ? ((IPEndPoint)tcpTarget.LocalEndpoint).Port : ((IPEndPoint)udpTarget.Client.LocalEndPoint!).Port
        };
        await using var engine = new ForwardingEngine();
        for (int iteration = 0; iteration < 5; iteration++)
        {
            await engine.StartAsync(rule, timeout.Token);
            using var tcpClient = new TcpClient();
            using var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            if (protocol == TransportProtocol.Tcp)
            {
                await tcpClient.ConnectAsync(IPAddress.Loopback, rule.ListenPort, timeout.Token);
                using var accepted = await tcpTarget.AcceptTcpClientAsync(timeout.Token);
                await engine.StopAsync(rule.Id).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(0, await accepted.GetStream().ReadAsync(new byte[1], timeout.Token));
            }
            else
            {
                await udpClient.SendAsync(new byte[] { 1 }, new IPEndPoint(IPAddress.Loopback, rule.ListenPort), timeout.Token);
                await udpTarget.ReceiveAsync(timeout.Token);
                await engine.StopAsync(rule.Id).WaitAsync(TimeSpan.FromSeconds(2));
            }
            var status = Assert.Single(engine.GetStatuses());
            Assert.False(status.Running);
            Assert.Equal(0, status.ActiveConnections);
            Assert.Null(status.Error);
            using var replacement = new Socket(AddressFamily.InterNetwork,
                protocol == TransportProtocol.Tcp ? SocketType.Stream : SocketType.Dgram,
                protocol == TransportProtocol.Tcp ? ProtocolType.Tcp : ProtocolType.Udp);
            replacement.ExclusiveAddressUse = true;
            replacement.Bind(new IPEndPoint(IPAddress.Loopback, rule.ListenPort));
            await engine.StopAsync(rule.Id); // Idempotent.
        }
        await engine.DisposeAsync();
        await engine.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.StartAsync(rule));
    }

    [Theory]
    [InlineData(TransportProtocol.Tcp)]
    [InlineData(TransportProtocol.Udp)]
    public async Task Ipv6_loopback_forwards_real_bytes_and_releases_port(TransportProtocol protocol)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var tcpTarget = new TcpListener(IPAddress.IPv6Loopback, 0);
        tcpTarget.Start();
        using var udpTarget = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        int port;
        using (var reservation = new Socket(AddressFamily.InterNetworkV6,
            protocol == TransportProtocol.Tcp ? SocketType.Stream : SocketType.Dgram,
            protocol == TransportProtocol.Tcp ? ProtocolType.Tcp : ProtocolType.Udp))
        {
            reservation.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        }
        var rule = ValidationTests.Valid with
        {
            Protocol = protocol, ListenAddress = "::1", ListenPort = port, TargetHost = "::1",
            TargetPort = protocol == TransportProtocol.Tcp ? ((IPEndPoint)tcpTarget.LocalEndpoint).Port : ((IPEndPoint)udpTarget.Client.LocalEndPoint!).Port
        };
        await using var engine = new ForwardingEngine();
        await engine.StartAsync(rule);
        if (protocol == TransportProtocol.Tcp)
        {
            using var client = new TcpClient(AddressFamily.InterNetworkV6);
            await client.ConnectAsync(IPAddress.IPv6Loopback, port, timeout.Token);
            using var peer = await tcpTarget.AcceptTcpClientAsync(timeout.Token);
            var clientStream = client.GetStream();
            await clientStream.WriteAsync(new byte[] { 42 }, timeout.Token);
            byte[] payload = new byte[1];
            await peer.GetStream().ReadExactlyAsync(payload, timeout.Token);
            await peer.GetStream().WriteAsync(payload, timeout.Token);
            await clientStream.ReadExactlyAsync(payload, timeout.Token);
            Assert.Equal(42, payload[0]);
        }
        else
        {
            using var client = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            await client.SendAsync(new byte[] { 42 }, new IPEndPoint(IPAddress.IPv6Loopback, port), timeout.Token);
            var packet = await udpTarget.ReceiveAsync(timeout.Token);
            await udpTarget.SendAsync(packet.Buffer, packet.RemoteEndPoint, timeout.Token);
            Assert.Equal(new byte[] { 42 }, (await client.ReceiveAsync(timeout.Token)).Buffer);
        }
        await engine.StopAsync(rule.Id);
        using var replacement = new Socket(AddressFamily.InterNetworkV6,
            protocol == TransportProtocol.Tcp ? SocketType.Stream : SocketType.Dgram,
            protocol == TransportProtocol.Tcp ? ProtocolType.Tcp : ProtocolType.Udp);
        replacement.ExclusiveAddressUse = true;
        replacement.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
    }

    [Fact]
    public async Task Stopped_rule_history_does_not_exhaust_capacity_for_new_rules()
    {
        await using var engine = new ForwardingEngine();
        for (int i = 0; i < ForwardingEngine.MaximumRules + 8; i++)
        {
            var rule = ValidationTests.Valid with { Enabled = false };
            await engine.StartAsync(rule);
            await engine.StopAsync(rule.Id);
        }
        Assert.InRange(engine.GetStatuses().Count, 1, ForwardingEngine.MaximumRules);
        Assert.All(engine.GetStatuses(), status => Assert.False(status.Running));
    }

    [Fact]
    public async Task Failed_rebind_preserves_existing_listener_and_connection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var target = new TcpListener(IPAddress.Loopback, 0);
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        target.Start(); blocker.Start();
        var rule = ValidationTests.Valid with { ListenPort = TcpTests.FreePort(), TargetPort = ((IPEndPoint)target.LocalEndpoint).Port };
        await using var engine = new ForwardingEngine();
        await engine.StartAsync(rule);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, rule.ListenPort, timeout.Token);
        using var accepted = await target.AcceptTcpClientAsync(timeout.Token);
        var clientStream = client.GetStream();
        var targetStream = accepted.GetStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(rule with { ListenPort = ((IPEndPoint)blocker.LocalEndpoint).Port }));
        Assert.True(Assert.Single(engine.GetStatuses()).Running);
        await clientStream.WriteAsync(new byte[] { 42 }, timeout.Token);
        byte[] buffer = new byte[1];
        await targetStream.ReadExactlyAsync(buffer, timeout.Token);
        Assert.Equal(42, buffer[0]);
        using var second = new TcpClient();
        await second.ConnectAsync(IPAddress.Loopback, rule.ListenPort, timeout.Token);
        using var secondTarget = await target.AcceptTcpClientAsync(timeout.Token);
    }

    [Theory]
    [InlineData(TransportProtocol.Tcp)]
    [InlineData(TransportProtocol.Udp)]
    public async Task Disabled_rules_do_not_bind_and_cancellation_releases_listener(TransportProtocol protocol)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var lifetime = new CancellationTokenSource();
        int port = protocol == TransportProtocol.Tcp ? TcpTests.FreePort() : UdpTests.FreePort();
        var rule = ValidationTests.Valid with { ListenPort = port, Protocol = protocol, Enabled = false };
        await using var engine = new ForwardingEngine();
        await engine.StartAsync(rule);
        Assert.False(Assert.Single(engine.GetStatuses()).Running);
        using (var probe = new Socket(AddressFamily.InterNetwork, protocol == TransportProtocol.Tcp ? SocketType.Stream : SocketType.Dgram,
            protocol == TransportProtocol.Tcp ? ProtocolType.Tcp : ProtocolType.Udp))
        {
            probe.ExclusiveAddressUse = true;
            probe.Bind(new IPEndPoint(IPAddress.Loopback, port));
        }
        await engine.StartAsync(rule with { Enabled = true }, lifetime.Token);
        Assert.True(Assert.Single(engine.GetStatuses()).Running);
        lifetime.Cancel();
        await EventuallyAsync(() => !Assert.Single(engine.GetStatuses()).Running, timeout.Token);
        using var replacement = new Socket(AddressFamily.InterNetwork, protocol == TransportProtocol.Tcp ? SocketType.Stream : SocketType.Dgram,
            protocol == TransportProtocol.Tcp ? ProtocolType.Tcp : ProtocolType.Udp);
        replacement.ExclusiveAddressUse = true;
        replacement.Bind(new IPEndPoint(IPAddress.Loopback, port));
    }

    [Fact]
    public async Task Unavailable_tcp_target_is_visible_and_does_not_stop_listener()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var refused = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        refused.Bind(new IPEndPoint(IPAddress.Loopback, 0)); // Reserved but deliberately not listening.
        var rule = ValidationTests.Valid with { ListenPort = TcpTests.FreePort(), TargetPort = ((IPEndPoint)refused.LocalEndPoint!).Port };
        await using var engine = new ForwardingEngine();
        await engine.StartAsync(rule);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, rule.ListenPort, timeout.Token);
        await EventuallyAsync(() => Assert.Single(engine.GetStatuses()).Error is not null, timeout.Token);
        var status = Assert.Single(engine.GetStatuses());
        Assert.True(status.Running);
        await EventuallyAsync(() => Assert.Single(engine.GetStatuses()).ActiveConnections == 0, timeout.Token);
    }
}
