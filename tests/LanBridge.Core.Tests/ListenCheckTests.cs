using System.Net;
using System.Net.Sockets;
using LanBridge.Core;
using Xunit;

namespace LanBridge.Core.Tests;

public sealed class ListenCheckTests
{
    [Fact]
    public async Task Available_check_releases_probe_and_ignores_target_host()
    {
        await using var engine = new ForwardingEngine();
        int port = FreePort();
        // An incomplete/unreachable target is unrelated to the local bind preflight.
        var rule = new ForwardRule { ListenPort = port, TargetHost = "", TargetPort = 0 };
        var result = await engine.CheckListenAsync(rule);
        Assert.Equal(ListenCheckStatus.Available, result.Status);
        Assert.Contains("保存", result.Message);
        Assert.Empty(engine.GetStatuses());
        using var probe = Bind(IPAddress.Loopback, port, TransportProtocol.Tcp);
    }

    [Theory]
    [InlineData(TransportProtocol.Tcp, false, false)]
    [InlineData(TransportProtocol.Udp, false, false)]
    [InlineData(TransportProtocol.Tcp, true, false)]
    [InlineData(TransportProtocol.Udp, true, false)]
    [InlineData(TransportProtocol.Tcp, false, true)]
    [InlineData(TransportProtocol.Udp, false, true)]
    public async Task Occupied_bind_is_a_conflict(TransportProtocol protocol, bool occupiedWildcard, bool requestedWildcard)
    {
        using var blocker = Bind(occupiedWildcard ? IPAddress.Any : IPAddress.Loopback, 0, protocol);
        int port = ((IPEndPoint)blocker.LocalEndPoint!).Port;
        await using var engine = new ForwardingEngine();
        var result = await engine.CheckListenAsync(new ForwardRule
        {
            ListenAddress = requestedWildcard ? "0.0.0.0" : "127.0.0.1", ListenPort = port, Protocol = protocol
        });
        Assert.Equal(ListenCheckStatus.Conflict, result.Status);
        Assert.Contains(protocol == TransportProtocol.Tcp ? "TCP" : "UDP", result.Message);
        Assert.True(blocker.IsBound);
    }

    [Theory]
    [InlineData(TransportProtocol.Tcp)]
    [InlineData(TransportProtocol.Udp)]
    public async Task Combined_check_requires_both_protocols(TransportProtocol occupied)
    {
        using var blocker = Bind(IPAddress.Loopback, 0, occupied);
        await using var engine = new ForwardingEngine();
        var result = await engine.CheckListenAsync(new ForwardRule
        {
            ListenPort = ((IPEndPoint)blocker.LocalEndPoint!).Port, Protocol = TransportProtocol.TcpUdp
        });
        Assert.Equal(ListenCheckStatus.Conflict, result.Status);
        Assert.Contains(occupied == TransportProtocol.Tcp ? "TCP" : "UDP", result.Message);
    }

    [Theory]
    [InlineData(TransportProtocol.Tcp, TransportProtocol.Tcp)]
    [InlineData(TransportProtocol.Udp, TransportProtocol.Udp)]
    [InlineData(TransportProtocol.TcpUdp, TransportProtocol.TcpUdp)]
    [InlineData(TransportProtocol.TcpUdp, TransportProtocol.Tcp)]
    [InlineData(TransportProtocol.TcpUdp, TransportProtocol.Udp)]
    public async Task Same_id_unchanged_actual_listener_is_not_an_external_conflict(TransportProtocol running, TransportProtocol edited)
    {
        await using var engine = new ForwardingEngine();
        var rule = new ForwardRule { ListenPort = FreePort(), TargetHost = "192.0.2.1", TargetPort = 1, Protocol = running };
        await engine.StartAsync(rule);
        var result = await engine.CheckListenAsync(rule with { Name = "edited", TargetHost = "", Protocol = edited });
        Assert.Equal(ListenCheckStatus.Available, result.Status);
        Assert.True(Assert.Single(engine.GetStatuses()).Running);
    }

    [Theory]
    [InlineData("not-an-ip", 12345, TransportProtocol.Tcp)]
    [InlineData("127.0.0.1", 0, TransportProtocol.Tcp)]
    [InlineData("127.0.0.1", 65536, TransportProtocol.Tcp)]
    [InlineData("127.0.0.1", 12345, (TransportProtocol)99)]
    [InlineData("192.0.2.197", 12345, TransportProtocol.Tcp)]
    public async Task Invalid_or_nonlocal_bind_is_unknown(string address, int port, TransportProtocol protocol)
    {
        await using var engine = new ForwardingEngine();
        var result = await engine.CheckListenAsync(new ForwardRule { ListenAddress = address, ListenPort = port, Protocol = protocol });
        Assert.Equal(ListenCheckStatus.Unknown, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Theory]
    [InlineData(TransportProtocol.Tcp)]
    [InlineData(TransportProtocol.Udp)]
    public async Task Self_ignore_never_masks_the_other_required_protocol(TransportProtocol running)
    {
        await using var engine = new ForwardingEngine();
        var rule = new ForwardRule { ListenPort = FreePort(), TargetHost = "192.0.2.1", TargetPort = 1, Protocol = running };
        await engine.StartAsync(rule);
        var other = running == TransportProtocol.Tcp ? TransportProtocol.Udp : TransportProtocol.Tcp;
        using var blocker = Bind(IPAddress.Loopback, rule.ListenPort, other);
        var result = await engine.CheckListenAsync(rule with { Protocol = TransportProtocol.TcpUdp });
        Assert.Equal(ListenCheckStatus.Conflict, result.Status);
        Assert.Contains(other == TransportProtocol.Tcp ? "TCP" : "UDP", result.Message);
        Assert.True(Assert.Single(engine.GetStatuses()).Running);
    }

    [Fact]
    public async Task Stopped_same_id_does_not_hide_a_replacement_external_socket()
    {
        await using var engine = new ForwardingEngine();
        var rule = new ForwardRule { ListenPort = FreePort(), TargetHost = "192.0.2.1", TargetPort = 1 };
        await engine.StartAsync(rule);
        await engine.StopAsync(rule.Id);
        using var blocker = Bind(IPAddress.Loopback, rule.ListenPort, TransportProtocol.Tcp);
        Assert.Equal(ListenCheckStatus.Conflict, (await engine.CheckListenAsync(rule)).Status);
    }

    [Fact]
    public async Task Self_ignore_is_limited_to_same_id_and_exact_endpoint()
    {
        await using var engine = new ForwardingEngine();
        var rule = new ForwardRule { ListenPort = FreePort(), TargetHost = "192.0.2.1", TargetPort = 1 };
        await engine.StartAsync(rule);
        Assert.Equal(ListenCheckStatus.Conflict, (await engine.CheckListenAsync(rule with { Id = Guid.NewGuid() })).Status);
        Assert.Equal(ListenCheckStatus.Conflict, (await engine.CheckListenAsync(rule with { ListenAddress = "0.0.0.0" })).Status);
        using var blocker = Bind(IPAddress.Loopback, 0, TransportProtocol.Tcp);
        Assert.Equal(ListenCheckStatus.Conflict, (await engine.CheckListenAsync(rule with { ListenPort = ((IPEndPoint)blocker.LocalEndPoint!).Port })).Status);
    }

    [Theory]
    [InlineData(TransportProtocol.Tcp)]
    [InlineData(TransportProtocol.Udp)]
    public async Task IPv6_wildcard_occupancy_is_checked_in_both_directions(TransportProtocol protocol)
    {
        Assert.True(Socket.OSSupportsIPv6);
        await using var engine = new ForwardingEngine();
        using (var blocker = Bind(IPAddress.IPv6Any, 0, protocol))
        {
            var result = await engine.CheckListenAsync(new ForwardRule { ListenAddress = "::1", ListenPort = ((IPEndPoint)blocker.LocalEndPoint!).Port, Protocol = protocol });
            Assert.Equal(ListenCheckStatus.Conflict, result.Status);
        }
        using (var blocker = Bind(IPAddress.IPv6Loopback, 0, protocol))
        {
            var result = await engine.CheckListenAsync(new ForwardRule { ListenAddress = "::", ListenPort = ((IPEndPoint)blocker.LocalEndPoint!).Port, Protocol = protocol });
            Assert.Equal(ListenCheckStatus.Conflict, result.Status);
        }
    }

    [Theory]
    [InlineData(TransportProtocol.Tcp)]
    [InlineData(TransportProtocol.Udp)]
    public async Task External_dual_stack_wildcard_blocks_IPv4(TransportProtocol protocol)
    {
        using var blocker = new Socket(AddressFamily.InterNetworkV6,
            protocol == TransportProtocol.Udp ? SocketType.Dgram : SocketType.Stream,
            protocol == TransportProtocol.Udp ? ProtocolType.Udp : ProtocolType.Tcp);
        blocker.DualMode = true;
        blocker.ExclusiveAddressUse = true;
        blocker.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        await using var engine = new ForwardingEngine();
        var result = await engine.CheckListenAsync(new ForwardRule { ListenPort = ((IPEndPoint)blocker.LocalEndPoint!).Port, Protocol = protocol });
        Assert.Equal(ListenCheckStatus.Conflict, result.Status);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public async Task Combined_available_releases_both_protocols(string addressText)
    {
        var address = IPAddress.Parse(addressText);
        int port;
        using (var reservation = Bind(address, 0, TransportProtocol.Tcp)) port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        await using var engine = new ForwardingEngine();
        var rule = new ForwardRule { ListenAddress = addressText, ListenPort = port, Protocol = TransportProtocol.TcpUdp };
        Assert.Equal(ListenCheckStatus.Available, (await engine.CheckListenAsync(rule)).Status);
        using var tcp = Bind(address, port, TransportProtocol.Tcp);
        using var udp = Bind(address, port, TransportProtocol.Udp);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_availability()
    {
        await using var engine = new ForwardingEngine();
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.CheckListenAsync(new ForwardRule { ListenPort = FreePort() }, ct.Token));
        Assert.Empty(engine.GetStatuses());
    }

    [Theory]
    [InlineData(TransportProtocol.Tcp)]
    [InlineData(TransportProtocol.Udp)]
    public async Task IPv6_only_engine_listener_does_not_reserve_IPv4(TransportProtocol protocol)
    {
        int port;
        using (var reservation = Bind(IPAddress.IPv6Any, 0, protocol)) port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        await using var engine = new ForwardingEngine();
        var rule = new ForwardRule { ListenAddress = "::", ListenPort = port, TargetHost = "192.0.2.1", TargetPort = 1, Protocol = protocol };
        await engine.StartAsync(rule);
        Assert.Equal(ListenCheckStatus.Available, (await engine.CheckListenAsync(rule)).Status);
        Assert.Equal(ListenCheckStatus.Available, (await engine.CheckListenAsync(rule with { Id = Guid.NewGuid(), ListenAddress = "0.0.0.0" })).Status);
    }

    [Theory]
    [InlineData(TransportProtocol.Tcp)]
    [InlineData(TransportProtocol.Udp)]
    public async Task Separate_protocol_does_not_conflict(TransportProtocol occupied)
    {
        using var blocker = Bind(IPAddress.Loopback, 0, occupied);
        await using var engine = new ForwardingEngine();
        var result = await engine.CheckListenAsync(new ForwardRule
        {
            ListenPort = ((IPEndPoint)blocker.LocalEndPoint!).Port,
            Protocol = occupied == TransportProtocol.Tcp ? TransportProtocol.Udp : TransportProtocol.Tcp
        });
        Assert.Equal(ListenCheckStatus.Available, result.Status);
    }

    internal static int FreePort()
    {
        using var socket = Bind(IPAddress.Loopback, 0, TransportProtocol.Tcp);
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    internal static Socket Bind(IPAddress address, int port, TransportProtocol protocol)
    {
        var socket = new Socket(address.AddressFamily,
            protocol == TransportProtocol.Udp ? SocketType.Dgram : SocketType.Stream,
            protocol == TransportProtocol.Udp ? ProtocolType.Udp : ProtocolType.Tcp);
        try
        {
            socket.ExclusiveAddressUse = true;
            if (address.AddressFamily == AddressFamily.InterNetworkV6) socket.DualMode = false;
            socket.Bind(new IPEndPoint(address, port));
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }
}
