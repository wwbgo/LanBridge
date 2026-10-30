using System.Net;
using System.Net.Sockets;
using LanBridge.Control;
using LanBridge.Core;
using LanBridge.Host;
using Xunit;

namespace LanBridge.Host.Tests;

public sealed class ListenCheckTests
{
    [Fact]
    public Task Real_client_checks_local_bind_without_saving_or_reserving() => WithHostAsync(async (coordinator, client, directory) =>
    {
        var rule = Rule();
        var result = await client.CheckListenAsync(rule);
        Assert.Equal(ListenCheckStatus.Available, result.Status);
        Assert.Contains("保存", result.Message);
        Assert.Empty((await client.GetStateAsync()).Rules);
        Assert.False(File.Exists(Path.Combine(directory, "rules.json")));
        using var blocker = Bind(IPAddress.Loopback, rule.ListenPort, TransportProtocol.Tcp);
        // A prior Available never bypasses the actual save/bind authority.
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SaveRuleAsync(rule));
        Assert.Empty((await coordinator.GetStateAsync()).Rules);
        Assert.False(File.Exists(Path.Combine(directory, "rules.json")));
    });

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1", TransportProtocol.Tcp, TransportProtocol.Tcp)]
    [InlineData("0.0.0.0", "127.0.0.1", TransportProtocol.Udp, TransportProtocol.TcpUdp)]
    [InlineData("127.0.0.1", "0.0.0.0", TransportProtocol.TcpUdp, TransportProtocol.Udp)]
    public Task Saved_rule_overlap_is_reported_even_when_disabled(string savedAddress, string requestedAddress,
        TransportProtocol savedProtocol, TransportProtocol requestedProtocol) => WithHostAsync(async (_, client, directory) =>
    {
        var saved = Rule() with { Name = "saved listener", Enabled = false, ListenAddress = savedAddress, Protocol = savedProtocol };
        await client.SaveRuleAsync(saved);
        string disk = await File.ReadAllTextAsync(Path.Combine(directory, "rules.json"));
        var result = await client.CheckListenAsync(saved with { Id = Guid.NewGuid(), ListenAddress = requestedAddress, Protocol = requestedProtocol });
        Assert.Equal(ListenCheckStatus.Conflict, result.Status);
        Assert.Contains(saved.Name, result.Message);
        Assert.Contains("已禁用", result.Message);
        Assert.Equal(ListenCheckStatus.Available, (await client.CheckListenAsync(saved)).Status);
        Assert.Equal(disk, await File.ReadAllTextAsync(Path.Combine(directory, "rules.json")));
        Assert.Equal(saved, Assert.Single((await client.GetStateAsync()).Rules));
    });

    [Fact]
    public Task Same_id_preflight_preserves_an_existing_live_TCP_connection() => WithHostAsync(async (_, client, directory) =>
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        try
        {
            var rule = Rule() with { TargetHost = "127.0.0.1", TargetPort = ((IPEndPoint)target.LocalEndpoint).Port };
            await client.SaveRuleAsync(rule, deadline.Token);
            string disk = await File.ReadAllTextAsync(Path.Combine(directory, "rules.json"), deadline.Token);
            using var connection = new TcpClient();
            await connection.ConnectAsync(IPAddress.Loopback, rule.ListenPort, deadline.Token);
            using var accepted = await target.AcceptTcpClientAsync(deadline.Token);
            Assert.Equal(ListenCheckStatus.Available, (await client.CheckListenAsync(rule with { Name = "edited" }, deadline.Token)).Status);
            Assert.Equal(1, Assert.Single((await client.GetStateAsync(deadline.Token)).Statuses).ActiveConnections);
            await connection.GetStream().WriteAsync(new byte[] { 42 }, deadline.Token);
            byte[] received = new byte[1];
            await accepted.GetStream().ReadExactlyAsync(received, deadline.Token);
            Assert.Equal(42, received[0]);
            Assert.False(target.Pending()); // Preflight did not open a target connection.
            Assert.Equal(disk, await File.ReadAllTextAsync(Path.Combine(directory, "rules.json"), deadline.Token));
            Assert.Equal(rule, Assert.Single((await client.GetStateAsync(deadline.Token)).Rules));
        }
        finally { target.Stop(); }
    });

    [Theory]
    [InlineData(TransportProtocol.Tcp)]
    [InlineData(TransportProtocol.Udp)]
    public Task Client_reports_an_external_combined_protocol_conflict(TransportProtocol occupied) => WithHostAsync(async (_, client, _) =>
    {
        using var blocker = Bind(IPAddress.Loopback, 0, occupied);
        var result = await client.CheckListenAsync(Rule() with { ListenPort = ((IPEndPoint)blocker.LocalEndPoint!).Port, Protocol = TransportProtocol.TcpUdp });
        Assert.Equal(ListenCheckStatus.Conflict, result.Status);
        Assert.Contains(occupied == TransportProtocol.Tcp ? "TCP" : "UDP", result.Message);
    });

    [Theory]
    [InlineData("127.0.0.1", TransportProtocol.Udp)]
    [InlineData("::1", TransportProtocol.Tcp)]
    [InlineData("127.0.0.2", TransportProtocol.Tcp)]
    public Task Saved_nonoverlapping_listener_is_not_a_conflict(string address, TransportProtocol protocol) => WithHostAsync(async (_, client, _) =>
    {
        var saved = Rule() with { Enabled = false };
        await client.SaveRuleAsync(saved);
        var result = await client.CheckListenAsync(saved with { Id = Guid.NewGuid(), ListenAddress = address, Protocol = protocol });
        Assert.Equal(ListenCheckStatus.Available, result.Status);
    });

    [Fact]
    public Task Same_id_exclusion_does_not_hide_another_saved_rule() => WithHostAsync(async (_, client, _) =>
    {
        var saved = Rule() with { Enabled = false, Name = "first" };
        await client.SaveRuleAsync(saved);
        await client.SaveRuleAsync(saved with { Id = Guid.NewGuid(), Name = "second" });
        var result = await client.CheckListenAsync(saved);
        Assert.Equal(ListenCheckStatus.Conflict, result.Status);
        Assert.Contains("second", result.Message);
    });

    [Fact]
    public Task Invalid_bind_returns_unknown_over_the_real_pipe() => WithHostAsync(async (_, client, _) =>
    {
        Assert.Equal(ListenCheckStatus.Unknown, (await client.CheckListenAsync(Rule() with { ListenAddress = "invalid" })).Status);
        Assert.Equal(ListenCheckStatus.Unknown, (await client.CheckListenAsync(Rule() with { ListenPort = 0 })).Status);
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CheckListenAsync(Rule(), ct.Token));
    });

    [Fact]
    public Task Ambiguous_check_command_is_rejected() => WithHostAsync(async (coordinator, _, _) =>
    {
        var dispatcher = new BridgeDispatcher(coordinator);
        await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.DispatchAsync(new("checkListen")));
        await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.DispatchAsync(new("checkListen", Rule: Rule(), Id: Guid.NewGuid())));
        Assert.Empty((await coordinator.GetStateAsync()).Rules);
    });

    [Fact]
    public async Task Client_missing_result_is_unknown_not_available()
    {
        string name = "LanBridge.Test.Listen.Legacy." + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        var serving = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            var request = await PipeProtocol.ReadAsync<BridgeRequest>(pipe, deadline.Token);
            Assert.Equal("checkListen", request.Command);
            Assert.NotNull(request.Rule);
            await PipeProtocol.WriteAsync(pipe, new BridgeResponse(true), deadline.Token);
        });
        var result = await new BridgeClient(name).CheckListenAsync(Rule(), deadline.Token);
        Assert.Equal(ListenCheckStatus.Unknown, result.Status);
        await serving;
    }

    [Fact]
    public Task Shutting_down_backend_cannot_claim_availability() => WithHostAsync(async (coordinator, client, _) =>
    {
        await coordinator.ShutdownAsync();
        Assert.Equal(ListenCheckStatus.Unknown, (await client.CheckListenAsync(Rule())).Status);
    });

    [Fact]
    public void Existing_responses_omit_the_optional_preflight_field_for_strict_older_clients()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new BridgeResponse(true), PipeProtocol.Json);
        Assert.DoesNotContain("\"listenCheck\"", json);
    }

    private static ForwardRule Rule() => new() { ListenPort = CoordinatorTests.FreePort(), TargetHost = "192.0.2.1", TargetPort = 1 };

    private static Socket Bind(IPAddress address, int port, TransportProtocol protocol)
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

    private static async Task WithHostAsync(Func<BridgeCoordinator, BridgeClient, string, Task> test)
    {
        string directory = CoordinatorTests.TestDirectory();
        string name = "LanBridge.Test.Listen." + Guid.NewGuid().ToString("N");
        try
        {
            await using var coordinator = new BridgeCoordinator(directory, "Console");
            await coordinator.InitializeAsync();
            using var stop = new CancellationTokenSource();
            var serving = new PipeControlServer(PipeProtocol.CurrentOwnerSid, new(coordinator), new(Path.Combine(directory, "host.log")), name).RunAsync(stop.Token);
            try { await test(coordinator, new BridgeClient(name), directory); }
            finally { stop.Cancel(); await serving.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        finally { Directory.Delete(directory, true); }
    }
}
