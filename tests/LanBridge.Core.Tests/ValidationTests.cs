using LanBridge.Core;
using Xunit;

namespace LanBridge.Core.Tests;

public class ValidationTests
{
    internal static ForwardRule Valid => new() { ListenPort = 23456, TargetHost = "127.0.0.1", TargetPort = 23457 };

    [Fact]
    public void Wildcard_bind_allows_remote_same_port_but_rejects_local_self_forwarding()
    {
        Assert.Empty(RuleValidator.Validate(Valid with { ListenAddress = "0.0.0.0", TargetHost = "192.0.2.10", TargetPort = 23456 }));
        Assert.Empty(RuleValidator.Validate(Valid with { ListenAddress = "::", TargetHost = "2001:db8::10", TargetPort = 23456 }));
        Assert.NotEmpty(RuleValidator.Validate(Valid with { ListenAddress = "0.0.0.0", TargetHost = "127.0.0.1", TargetPort = 23456 }));
        Assert.NotEmpty(RuleValidator.Validate(Valid with { ListenAddress = "::", TargetHost = "::1", TargetPort = 23456 }));
        Assert.NotEmpty(RuleValidator.Validate(Valid with { TargetHost = "localhost", TargetPort = 23456 }));
    }

    [Theory]
    [InlineData("localhost.")]
    [InlineData("127.0.0.2")]
    [InlineData("LOCALHOST")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:0.0.0.0")]
    public void Wildcard_bind_rejects_local_aliases_and_ipv4_mapped_targets(string host)
    {
        Assert.NotEmpty(RuleValidator.Validate(Valid with { ListenAddress = "0.0.0.0", TargetHost = host, TargetPort = 23456 }));
    }

    [Fact]
    public void Wildcard_bind_rejects_actual_local_interface_and_machine_hostname_targets()
    {
        var rule = Valid with { ListenAddress = "0.0.0.0", TargetPort = 23456 };
        Assert.NotEmpty(RuleValidator.Validate(rule with { TargetHost = Environment.MachineName }));
        foreach (var address in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
            Assert.NotEmpty(RuleValidator.Validate(rule with { TargetHost = address.ToString() }));
    }

    [Theory]
    [InlineData(TransportProtocol.Tcp)]
    [InlineData(TransportProtocol.Udp)]
    public async Task Dns_alias_resolving_to_localhost_is_rejected_before_binding(TransportProtocol protocol)
    {
        // localhost is guaranteed by the local resolver and must map to loopback;
        // assert the DNS prerequisite before testing the engine's resolution guard.
        var addresses = await System.Net.Dns.GetHostAddressesAsync("localhost");
        Assert.Contains(addresses, System.Net.IPAddress.IsLoopback);
        int port = protocol == TransportProtocol.Tcp ? TcpTests.FreePort() : UdpTests.FreePort();
        var rule = Valid with { ListenAddress = "0.0.0.0", ListenPort = port, TargetPort = port, TargetHost = "localhost", Protocol = protocol };
        await using var engine = new ForwardingEngine();
        await Assert.ThrowsAsync<ArgumentException>(() => engine.StartAsync(rule));
        Assert.Empty(engine.GetStatuses());
    }

    [Fact]
    public async Task Invalid_update_is_rejected_without_stopping_existing_rule()
    {
        await using var engine = new ForwardingEngine();
        var rule = Valid with { ListenPort = TcpTests.FreePort() };
        await engine.StartAsync(rule);
        await Assert.ThrowsAsync<ArgumentException>(() => engine.StartAsync(rule with { TargetPort = 0 }));
        Assert.True(Assert.Single(engine.GetStatuses()).Running);
        await Assert.ThrowsAsync<ArgumentNullException>(() => engine.StartAsync(null!));
    }

    [Fact]
    public void Validation_rejects_invalid_rules_and_accepts_literal_bind_and_dns_target()
    {
        Assert.Empty(RuleValidator.Validate(Valid));
        Assert.Empty(RuleValidator.Validate(Valid with { ListenAddress = "::1", TargetHost = "vm.internal" }));
        ForwardRule[] invalid = [
            Valid with { Id = Guid.Empty }, Valid with { ListenPort = 0 }, Valid with { ListenPort = 65536 },
            Valid with { TargetPort = -1 }, Valid with { TargetPort = 65536 },
            Valid with { ListenAddress = "localhost" }, Valid with { ListenAddress = "" },
            Valid with { TargetHost = "" }, Valid with { TargetHost = "https://host" },
            Valid with { Protocol = (TransportProtocol)42 },
            Valid with { ListenAddress = "0.0.0.0", TargetPort = 23456 },
            Valid with { Name = new string('x', 257) } ];
        foreach (var rule in invalid) Assert.NotEmpty(RuleValidator.Validate(rule));
    }
}
