using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LanBridge.Core;
using LanBridge.Control;

// Black-box tests: actual host process, real pipe IPC and real loopback sockets.
if (args.Length != 1 || !File.Exists(args[0]))
    throw new ArgumentException("Pass the full path of a built LanBridge.Host.exe.");
var hostPath = Path.GetFullPath(args[0]);
var scratch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch");
var data = Path.Combine(scratch, "lanbridge-acceptance-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(data);
var client = new BridgeClient();
Process? host = null;
int passed = 0;
void Pass(string name) { passed++; Console.WriteLine("PASS " + name); }
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
var ct = deadline.Token;
var tcpTarget = new TcpListener(IPAddress.Loopback, FreeCombinedPort());
tcpTarget.Start();
using var udpTarget = new UdpClient(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)tcpTarget.LocalEndpoint).Port));
using var servers = CancellationTokenSource.CreateLinkedTokenSource(ct);
var tcpServerTask = Task.Run(async () => {
    try {
        while (!servers.IsCancellationRequested) {
            var connection = await tcpTarget.AcceptTcpClientAsync(servers.Token);
            _ = Task.Run(async () => {
                using (connection) {
                    try {
                        var stream = connection.GetStream();
                        byte[] buffer = new byte[8192];
                        int read;
                        while ((read = await stream.ReadAsync(buffer, servers.Token)) != 0)
                            await stream.WriteAsync(buffer.AsMemory(0, read), servers.Token);
                        connection.Client.Shutdown(SocketShutdown.Send);
                    } catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
                }
            });
        }
    } catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
});
var udpServerTask = Task.Run(async () => {
    try {
        while (!servers.IsCancellationRequested) {
            var packet = await udpTarget.ReceiveAsync(servers.Token);
            await udpTarget.SendAsync(packet.Buffer, packet.RemoteEndPoint, servers.Token);
        }
    } catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
});
int FreeCombinedPort() {
    for (int attempt = 0; attempt < 128; attempt++) {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        using var tcp = new TcpListener(IPAddress.Loopback, port);
        try { tcp.Start(); return port; }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied) { }
    }
    throw new InvalidOperationException("No available loopback port for both protocols.");
}
int FreePort(SocketType type) {
    using var socket = new Socket(AddressFamily.InterNetwork, type, type == SocketType.Stream ? ProtocolType.Tcp : ProtocolType.Udp);
    socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    return ((IPEndPoint)socket.LocalEndPoint!).Port;
}
async Task StartHost() {
    host = Process.Start(new ProcessStartInfo(hostPath) {
        UseShellExecute = false, CreateNoWindow = true,
        ArgumentList = { "--console", "--data-dir", data }
    }) ?? throw new InvalidOperationException("Host did not launch.");
    Exception? last = null;
    for (int i = 0; i < 40; i++) {
        if (host.HasExited) throw new InvalidOperationException($"Host exited with {host.ExitCode}.");
        try { await client.GetStateAsync(ct); return; }
        catch (Exception ex) { last = ex; await Task.Delay(150, ct); }
    }
    throw new InvalidOperationException("Host pipe did not become ready.", last);
}
async Task StopHost() {
    if (host is not null) {
        if (!host.HasExited) { host.Kill(entireProcessTree: true); await host.WaitForExitAsync(ct); }
        host.Dispose(); host = null;
    }
}
async Task TcpRoundTrip(int port) {
    using var peer = new TcpClient();
    await peer.ConnectAsync(IPAddress.Loopback, port, ct);
    var payload = new byte[128 * 1024];
    Random.Shared.NextBytes(payload);
    var stream = peer.GetStream();
    var reader = Task.Run(async () => {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, ct);
        return output.ToArray();
    });
    await stream.WriteAsync(payload, ct);
    peer.Client.Shutdown(SocketShutdown.Send);
    var response = await reader.WaitAsync(TimeSpan.FromSeconds(10), ct);
    Check(payload.SequenceEqual(response), "TCP half-close lost or corrupted bytes.");
}
async Task UdpRoundTrip(int port, string text) {
    using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    peer.Connect(IPAddress.Loopback, port);
    byte[] payload = Encoding.UTF8.GetBytes(text);
    await peer.SendAsync(payload, ct);
    var response = await peer.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
    Check(response.Buffer.SequenceEqual(payload), "UDP response did not match client.");
}
try {
    // Refuse to touch an already running backend (might be the user's real session).
    bool occupied;
    try { await client.GetStateAsync(ct); occupied = true; }
    catch { occupied = false; }
    if (occupied) throw new InvalidOperationException("A LanBridge backend already runs for this user. Close it before isolated acceptance testing.");
    await StartHost();
    Check((await client.GetStateAsync(ct)).Rules.Count == 0, "Isolated store must be empty.");
    Pass("new host starts with isolated empty config");
    var tcp = new ForwardRule {
        Name = "acceptance-tcp", ListenAddress = "127.0.0.1", ListenPort = FreePort(SocketType.Stream),
        TargetHost = "127.0.0.1", TargetPort = ((IPEndPoint)tcpTarget.LocalEndpoint).Port, Enabled = false
    };
    await client.SaveRuleAsync(tcp, ct);
    var state = await client.GetStateAsync(ct);
    Check(state.Rules.Single().Id == tcp.Id && !state.Rules.Single().Enabled, "Disabled rule not persisted.");
    Check(!state.Statuses.Any(s => s.Id == tcp.Id && s.Running), "Disabled rule must not listen.");
    Pass("create disabled rule and read it back over IPC");
    await client.SetEnabledAsync(tcp.Id, true, ct);
    await TcpRoundTrip(tcp.ListenPort);
    Check((await client.GetStateAsync(ct)).Statuses.Any(s => s.Id == tcp.Id && s.Running), "Enabled rule not running.");
    Pass("enable rule: 128 KiB TCP echo including half-close");
    var probe = await client.ProbeAsync(tcp, ct);
    Console.WriteLine("PROBE " + probe);
    bool rejected = false;
    try { await client.SaveRuleAsync(tcp with { ListenPort = 0 }, ct); }
    catch { rejected = true; }
    Check(rejected, "Invalid listen port should be rejected.");
    Check((await client.GetStateAsync(ct)).Rules.Single(r => r.Id == tcp.Id).ListenPort == tcp.ListenPort, "Invalid edit destroyed previous rule.");
    await TcpRoundTrip(tcp.ListenPort);
    Pass("invalid edit rejected without damaging active rule");
    var udp = new ForwardRule {
        Name = "acceptance-udp", ListenAddress = "127.0.0.1", ListenPort = FreePort(SocketType.Dgram),
        TargetHost = "127.0.0.1", TargetPort = ((IPEndPoint)udpTarget.Client.LocalEndPoint!).Port,
        Protocol = TransportProtocol.Udp, Enabled = true
    };
    await client.SaveRuleAsync(udp, ct);
    await Task.WhenAll(UdpRoundTrip(udp.ListenPort, "first-client"), UdpRoundTrip(udp.ListenPort, "second-client"));
    Pass("UDP real sockets: independent simultaneous client replies");
    var combined = tcp with { Id = Guid.NewGuid(), Name = "acceptance-combined", ListenPort = FreeCombinedPort(), Protocol = TransportProtocol.TcpUdp, Enabled = true };
    await client.SaveRuleAsync(combined, ct);
    Check((await client.GetStateAsync(ct)).Rules.Single(r => r.Id == combined.Id) == combined, "Combined protocol lost over IPC.");
    await Task.WhenAll(TcpRoundTrip(combined.ListenPort), UdpRoundTrip(combined.ListenPort, "combined-one"), UdpRoundTrip(combined.ListenPort, "combined-two"));
    Check((await client.ProbeAsync(combined, ct)).Contains("UDP 未验证"), "Combined TCP probe must not claim UDP verification.");
    Pass("one combined rule forwards TCP and isolated UDP concurrently; probe is TCP-only");
    await client.SaveRuleAsync(combined with { Protocol = TransportProtocol.Tcp }, ct);
    await TcpRoundTrip(combined.ListenPort);
    await client.SaveRuleAsync(combined, ct);
    await Task.WhenAll(TcpRoundTrip(combined.ListenPort), UdpRoundTrip(combined.ListenPort, "combined-rebound"));
    Pass("editing combined protocol to TCP and back preserves usable listeners");
    await StopHost();
    await StartHost();
    state = await client.GetStateAsync(ct);
    Check(state.Rules.Count == 3 && state.Rules.All(r => r.Enabled), "Restart did not restore persisted enabled rules.");
    await TcpRoundTrip(tcp.ListenPort);
    await UdpRoundTrip(udp.ListenPort, "after-restart");
    Pass("host restart restores persisted TCP and UDP rules");
    Check(state.Rules.Single(r => r.Id == combined.Id).Protocol == TransportProtocol.TcpUdp, "Restart changed combined protocol.");
    await Task.WhenAll(TcpRoundTrip(combined.ListenPort), UdpRoundTrip(combined.ListenPort, "combined-after-restart"));
    Pass("restart restores combined protocol and both forwarding transports");
    Check((await client.GetStateAsync(ct)).ProcessId == host!.Id, "State must identify the actual child Host.");
    await client.ShutdownAsync(ct);
    Check(host.HasExited && host.ExitCode == 0, "Graceful shutdown did not exit the exact Host successfully.");
    Pass("authorized IPC shutdown stops exact Host with exit code zero");
    await StopHost();
    await StartHost();
    Check((await client.GetStateAsync(ct)).Rules.All(r => r.Enabled), "Graceful exit changed enabled configuration.");
    await Task.WhenAll(TcpRoundTrip(combined.ListenPort), UdpRoundTrip(combined.ListenPort, "after-graceful-exit"));
    Pass("graceful shutdown preserves enabled config and restart restores combined forwarding");
    await client.SetEnabledAsync(combined.Id, false, ct);
    foreach (var type in new[] { SocketType.Stream, SocketType.Dgram }) {
        using var replacement = new Socket(AddressFamily.InterNetwork, type, type == SocketType.Stream ? ProtocolType.Tcp : ProtocolType.Udp);
        replacement.ExclusiveAddressUse = true;
        replacement.Bind(new IPEndPoint(IPAddress.Loopback, combined.ListenPort));
    }
    Pass("disable combined rule releases both TCP and UDP ports");
    await client.SetEnabledAsync(tcp.Id, false, ct);
    using (var replacement = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)) {
        replacement.ExclusiveAddressUse = true;
        replacement.Bind(new IPEndPoint(IPAddress.Loopback, tcp.ListenPort));
    }
    Pass("disable releases the TCP listening port");
    await client.DeleteRuleAsync(tcp.Id, ct);
    await client.DeleteRuleAsync(udp.Id, ct);
    await client.DeleteRuleAsync(combined.Id, ct);
    Check((await client.GetStateAsync(ct)).Rules.Count == 0, "Deleted rules remain in host.");
    await StopHost();
    await StartHost();
    Check((await client.GetStateAsync(ct)).Rules.Count == 0, "Deleted rules returned after restart.");
    Pass("delete rules and persist empty config across restart");
    Console.WriteLine($"RESULT {passed} acceptance checks passed. Isolated data: {data}");
} finally {
    await StopHost();
    servers.Cancel(); tcpTarget.Stop(); udpTarget.Dispose();
    await Task.WhenAll(tcpServerTask, udpServerTask);
}
