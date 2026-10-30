using System.Net;
using System.Net.Sockets;
using LanBridge.Core;
using Xunit;

namespace LanBridge.Core.Tests;

public class UdpTests
{
    internal static int FreePort()
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)peer.Client.LocalEndPoint!).Port;
    }

    [Fact]
    public async Task Udp_idle_session_expires_and_next_datagram_creates_a_fresh_session()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var target = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        target.Client.ReceiveBufferSize = 4 * 1024 * 1024;
        var rule = ValidationTests.Valid with
        {
            ListenPort = FreePort(), TargetPort = ((IPEndPoint)target.Client.LocalEndPoint!).Port,
            Protocol = TransportProtocol.Udp
        };
        await using var engine = new ForwardingEngine(TimeSpan.FromMilliseconds(150));
        await engine.StartAsync(rule);
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Connect(IPAddress.Loopback, rule.ListenPort);
        await client.SendAsync(new byte[] { 1 }, timeout.Token);
        var first = await target.ReceiveAsync(timeout.Token);
        Assert.Equal(1, Assert.Single(engine.GetStatuses()).ActiveConnections);
        while (Assert.Single(engine.GetStatuses()).ActiveConnections != 0)
            await Task.Delay(10, timeout.Token);
        await client.SendAsync(new byte[] { 2 }, timeout.Token);
        var second = await target.ReceiveAsync(timeout.Token);
        Assert.NotEqual(first.RemoteEndPoint, second.RemoteEndPoint);
        await target.SendAsync(second.Buffer, second.RemoteEndPoint, timeout.Token);
        Assert.Equal(new byte[] { 2 }, (await client.ReceiveAsync(timeout.Token)).Buffer);
        Assert.Null(Assert.Single(engine.GetStatuses()).Error);
    }

    [Fact]
    public async Task Udp_dns_target_prefers_the_listener_address_family()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var target = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var rule = ValidationTests.Valid with
        {
            ListenPort = FreePort(), TargetHost = "localhost", TargetPort = ((IPEndPoint)target.Client.LocalEndPoint!).Port,
            Protocol = TransportProtocol.Udp
        };
        await using var engine = new ForwardingEngine();
        await engine.StartAsync(rule);
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await client.SendAsync(new byte[] { 42 }, new IPEndPoint(IPAddress.Loopback, rule.ListenPort), timeout.Token);
        var packet = await target.ReceiveAsync(timeout.Token);
        await target.SendAsync(packet.Buffer, packet.RemoteEndPoint, timeout.Token);
        Assert.Equal(new byte[] { 42 }, (await client.ReceiveAsync(timeout.Token)).Buffer);
    }

    [Fact]
    public async Task Udp_session_limit_is_bounded_and_stop_drains_every_session()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var target = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var rule = ValidationTests.Valid with
        {
            ListenPort = FreePort(), TargetPort = ((IPEndPoint)target.Client.LocalEndPoint!).Port,
            Protocol = TransportProtocol.Udp
        };
        await using var engine = new ForwardingEngine();
        await engine.StartAsync(rule);
        var clients = new List<UdpClient>();
        try
        {
            for (int i = 0; i < ForwardingEngine.MaximumConnectionsPerRule; i++)
            {
                var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                clients.Add(client);
                await client.SendAsync(new byte[] { 1 }, new IPEndPoint(IPAddress.Loopback, rule.ListenPort), timeout.Token);
                await target.ReceiveAsync(timeout.Token);
            }
            Assert.Equal(ForwardingEngine.MaximumConnectionsPerRule, Assert.Single(engine.GetStatuses()).ActiveConnections);
            using var overflow = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await overflow.SendAsync(new byte[] { 2 }, new IPEndPoint(IPAddress.Loopback, rule.ListenPort), timeout.Token);
            using var noPacket = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await target.ReceiveAsync(noPacket.Token));
            Assert.Equal(ForwardingEngine.MaximumConnectionsPerRule, Assert.Single(engine.GetStatuses()).ActiveConnections);
            await engine.StopAsync(rule.Id).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, Assert.Single(engine.GetStatuses()).ActiveConnections);
            Assert.Null(Assert.Single(engine.GetStatuses()).Error);
        }
        finally { foreach (var client in clients) client.Dispose(); }
    }

    [Fact]
    public async Task Udp_concurrent_clients_keep_independent_sessions_and_exact_datagrams()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var target = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        target.Client.ReceiveBufferSize = 4 * 1024 * 1024;
        const int clients = 16;
        const int packets = 3;
        var echo = Task.Run(async () =>
        {
            var sources = new HashSet<IPEndPoint>();
            for (int i = 0; i < clients * packets; i++)
            {
                var packet = await target.ReceiveAsync(timeout.Token);
                sources.Add(packet.RemoteEndPoint);
                await target.SendAsync(packet.Buffer, packet.RemoteEndPoint, timeout.Token);
            }
            Assert.Equal(clients, sources.Count);
        });
        var rule = ValidationTests.Valid with
        {
            ListenPort = FreePort(), TargetPort = ((IPEndPoint)target.Client.LocalEndPoint!).Port,
            Protocol = TransportProtocol.Udp
        };
        await using var engine = new ForwardingEngine();
        await engine.StartAsync(rule);
        await Task.WhenAll(Enumerable.Range(0, clients).Select(async i =>
        {
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            client.Connect(IPAddress.Loopback, rule.ListenPort);
            foreach (int size in new[] { 0, 100 + i, 60_000 })
            {
                byte[] payload = Enumerable.Repeat((byte)i, size).ToArray();
                await client.SendAsync(payload, timeout.Token);
                var reply = await client.ReceiveAsync(timeout.Token);
                Assert.Equal(payload, reply.Buffer);
            }
        }));
        await echo;
        var status = Assert.Single(engine.GetStatuses());
        Assert.True(status.Running);
        Assert.Equal(clients, status.ActiveConnections);
        long total = Enumerable.Range(0, clients).Sum(i => 100L + i + 60_000);
        Assert.Equal(total, status.BytesSent);
        Assert.Equal(total, status.BytesReceived);
        await engine.StopAsync(rule.Id).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, Assert.Single(engine.GetStatuses()).ActiveConnections);
        using var replacement = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        replacement.ExclusiveAddressUse = true;
        replacement.Bind(new IPEndPoint(IPAddress.Loopback, rule.ListenPort));
    }
}
