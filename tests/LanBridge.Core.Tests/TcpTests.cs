using System.Net;
using System.Net.Sockets;
using System.Text;
using LanBridge.Core;
using Xunit;

namespace LanBridge.Core.Tests;

public class TcpTests
{
    internal static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    [Fact]
    public async Task Tcp_half_close_preserves_delayed_large_response_after_request_eof()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        byte[] payload = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)i).ToArray();
        var server = Task.Run(async () =>
        {
            using var peer = await target.AcceptTcpClientAsync(timeout.Token);
            using var request = new MemoryStream();
            await peer.GetStream().CopyToAsync(request, timeout.Token);
            Assert.Equal("request", Encoding.UTF8.GetString(request.ToArray()));
            await Task.Delay(50, timeout.Token);
            await peer.GetStream().WriteAsync(payload, timeout.Token);
            peer.Client.Shutdown(SocketShutdown.Send);
        });
        var rule = ValidationTests.Valid with { ListenPort = FreePort(), TargetPort = ((IPEndPoint)target.LocalEndpoint).Port };
        await using var engine = new ForwardingEngine();
        await engine.StartAsync(rule);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, rule.ListenPort, timeout.Token);
        var clientStream = client.GetStream();
        await clientStream.WriteAsync(Encoding.UTF8.GetBytes("request"), timeout.Token);
        client.Client.Shutdown(SocketShutdown.Send);
        using var response = new MemoryStream();
        await clientStream.CopyToAsync(response, timeout.Token);
        Assert.Equal(payload, response.ToArray());
        await server;
    }

    [Fact]
    public async Task Target_half_close_does_not_discard_later_client_upload()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        byte[] payload = new byte[512 * 1024];
        Random.Shared.NextBytes(payload);
        var server = Task.Run(async () =>
        {
            using var peer = await target.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream();
            await stream.WriteAsync(new byte[] { 42 }, timeout.Token);
            peer.Client.Shutdown(SocketShutdown.Send);
            using var upload = new MemoryStream();
            await stream.CopyToAsync(upload, timeout.Token);
            Assert.Equal(payload, upload.ToArray());
        });
        var rule = ValidationTests.Valid with { ListenPort = FreePort(), TargetPort = ((IPEndPoint)target.LocalEndpoint).Port };
        await using var engine = new ForwardingEngine();
        await engine.StartAsync(rule);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, rule.ListenPort, timeout.Token);
        using var greeting = new MemoryStream();
        await client.GetStream().CopyToAsync(greeting, timeout.Token);
        Assert.Equal(new byte[] { 42 }, greeting.ToArray());
        await client.GetStream().WriteAsync(payload, timeout.Token);
        client.Client.Shutdown(SocketShutdown.Send);
        await server;
    }

    [Fact]
    public async Task Tcp_concurrent_clients_transfer_distinct_large_payloads_without_cross_talk()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        const int clients = 24;
        const int size = 128 * 1024;
        var server = Task.Run(async () =>
        {
            var workers = new List<Task>();
            for (int i = 0; i < clients; i++)
            {
                var peer = await target.AcceptTcpClientAsync(timeout.Token);
                workers.Add(Task.Run(async () =>
                {
                    using (peer)
                    {
                        var stream = peer.GetStream();
                        using var request = new MemoryStream();
                        await stream.CopyToAsync(request, timeout.Token);
                        await stream.WriteAsync(request.ToArray(), timeout.Token);
                        peer.Client.Shutdown(SocketShutdown.Send);
                    }
                }));
            }
            await Task.WhenAll(workers);
        });
        var rule = ValidationTests.Valid with { ListenPort = FreePort(), TargetPort = ((IPEndPoint)target.LocalEndpoint).Port };
        await using var engine = new ForwardingEngine();
        await engine.StartAsync(rule);
        await Task.WhenAll(Enumerable.Range(0, clients).Select(async i =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, rule.ListenPort, timeout.Token);
            var stream = client.GetStream();
            byte[] payload = Enumerable.Repeat((byte)i, size).ToArray();
            await stream.WriteAsync(payload, timeout.Token);
            client.Client.Shutdown(SocketShutdown.Send);
            using var reply = new MemoryStream();
            await stream.CopyToAsync(reply, timeout.Token);
            Assert.Equal(payload, reply.ToArray());
        }));
        await server;
        await LifecycleTests.EventuallyAsync(() => Assert.Single(engine.GetStatuses()).ActiveConnections == 0, timeout.Token);
        var status = Assert.Single(engine.GetStatuses());
        Assert.Equal((long)clients * size, status.BytesSent);
        Assert.Equal((long)clients * size, status.BytesReceived);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task Tcp_echo_crosses_real_listener_and_updates_counters()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        int targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var echo = Task.Run(async () =>
        {
            using var connection = await target.AcceptTcpClientAsync(timeout.Token);
            var stream = connection.GetStream();
            var bytes = new byte[32];
            int read = await stream.ReadAsync(bytes, timeout.Token);
            await stream.WriteAsync(bytes.AsMemory(0, read), timeout.Token);
        });
        var rule = ValidationTests.Valid with { ListenPort = FreePort(), TargetPort = targetPort };
        await using var engine = new ForwardingEngine();
        await engine.StartAsync(rule, timeout.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, rule.ListenPort, timeout.Token);
        byte[] payload = Encoding.UTF8.GetBytes("real tcp echo");
        await client.GetStream().WriteAsync(payload, timeout.Token);
        byte[] reply = new byte[payload.Length];
        await client.GetStream().ReadExactlyAsync(reply, timeout.Token);
        Assert.Equal(payload, reply);
        await echo;
        var status = Assert.Single(engine.GetStatuses());
        Assert.True(status.Running);
        Assert.Equal(payload.Length, status.BytesSent);
        Assert.Equal(payload.Length, status.BytesReceived);
    }
}
