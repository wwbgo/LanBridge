using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace LanBridge.Core;

/// <summary>Bounded, in-process forwarding. Disposing waits for every owned socket/task.</summary>
public sealed class ForwardingEngine : IAsyncDisposable
{
    public const int MaximumRules = 256;
    public const int MaximumConnectionsPerRule = 256;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Runtime> runtimes = new();
    private bool disposed;
    private readonly TimeSpan udpIdleTimeout;

    public ForwardingEngine(TimeSpan? udpIdleTimeout = null)
    {
        this.udpIdleTimeout = udpIdleTimeout ?? TimeSpan.FromMinutes(1);
        if (this.udpIdleTimeout <= TimeSpan.Zero || this.udpIdleTimeout > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(udpIdleTimeout));
    }

    public async Task StartAsync(ForwardRule rule, CancellationToken cancellationToken = default)
    {
        var errors = RuleValidator.Validate(rule);
        if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors), nameof(rule));
        if (rule.Enabled && rule.ListenPort == rule.TargetPort && !IPAddress.TryParse(rule.TargetHost, out _))
            await ResolveTargetAsync(rule, cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            runtimes.TryGetValue(rule.Id, out var previous);
            if (previous is null && runtimes.Count >= MaximumRules)
            {
                // Stopped/deleted rule history must not permanently consume live capacity.
                var retired = runtimes.FirstOrDefault(pair => !pair.Value.Status.Running);
                if (retired.Value is null) throw new InvalidOperationException($"规则数量不能超过 {MaximumRules}。");
                await retired.Value.StopAsync().ConfigureAwait(false);
                runtimes.TryRemove(retired.Key, out _);
            }
            // Stage a different bind before disturbing the old listener or its live connections.
            bool sameBind = previous is not null &&
                (previous.Rule.Protocol == rule.Protocol || previous.Rule.Protocol == TransportProtocol.TcpUdp || rule.Protocol == TransportProtocol.TcpUdp) &&
                previous.Rule.ListenPort == rule.ListenPort &&
                IPAddress.Parse(previous.Rule.ListenAddress).Equals(IPAddress.Parse(rule.ListenAddress));
            bool restorePrevious = sameBind && previous!.Status.Running;
            if (sameBind) await previous!.StopAsync().ConfigureAwait(false);
            var runtime = new Runtime(rule, cancellationToken, udpIdleTimeout);
            try { runtime.Start(); }
            catch
            {
                await runtime.StopAsync().ConfigureAwait(false);
                if (restorePrevious)
                {
                    var restored = new Runtime(previous!.Rule, previous.StartToken, udpIdleTimeout);
                    runtimes[rule.Id] = restored;
                    restored.Start();
                }
                else if (previous is null) runtimes[rule.Id] = runtime;
                throw;
            }
            if (previous is not null && !sameBind) await previous.StopAsync().ConfigureAwait(false);
            runtimes[rule.Id] = runtime;
        }
        finally { gate.Release(); }
    }

    /// <summary>Checks local binding only. Never connects to the target or reserves a listener.</summary>
    public async Task<ListenCheckResult> CheckListenAsync(ForwardRule rule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ct.ThrowIfCancellationRequested();
            if (!IPAddress.TryParse(rule.ListenAddress, out var address) || rule.ListenPort is < 1 or > 65535 || !Enum.IsDefined(rule.Protocol))
                return new(ListenCheckStatus.Unknown, "请先填写有效的本机监听 IP、1–65535 端口和传输协议。");
            var endpoint = new IPEndPoint(address, rule.ListenPort);
            runtimes.TryGetValue(rule.Id, out var ownRuntime);
            foreach (bool udp in rule.Protocol == TransportProtocol.TcpUdp ? new[] { false, true } : new[] { rule.Protocol == TransportProtocol.Udp })
            {
                // Skip only this exact protocol's actual live socket, not a saved rule/status alone.
                if (ownRuntime?.OwnsListener(endpoint, udp) == true) continue;
                try
                {
                    using var socket = new Socket(address.AddressFamily, udp ? SocketType.Dgram : SocketType.Stream,
                        udp ? ProtocolType.Udp : ProtocolType.Tcp);
                    socket.ExclusiveAddressUse = true;
                    if (address.AddressFamily == AddressFamily.InterNetworkV6) socket.DualMode = false;
                    // Bind only: no Listen, Connect, packets, or retained reservation.
                    socket.Bind(new IPEndPoint(address, rule.ListenPort));
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
                {
                    return new(ListenCheckStatus.Conflict, $"{(udp ? "UDP" : "TCP")} 本机监听 {rule.ListenAddress}:{rule.ListenPort} 已被占用或被系统禁止绑定（独占/保留端口）：{ex.SocketErrorCode}。");
                }
                catch (SocketException ex)
                {
                    return new(ListenCheckStatus.Unknown, $"无法确认 {(udp ? "UDP" : "TCP")} 本机监听 {rule.ListenAddress}:{rule.ListenPort}：{ex.SocketErrorCode}。请检查本机地址/网络状态，保存时仍会实际绑定。");
                }
                catch (PlatformNotSupportedException)
                {
                    return new(ListenCheckStatus.Unknown, "当前系统不支持此监听地址或独占绑定检查。");
                }
            }
            return new(ListenCheckStatus.Available, "本机监听端口当前可用；此检查不预留端口，保存时仍需以后台实际绑定结果为准。");
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync(Guid id)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (runtimes.TryGetValue(id, out var runtime)) await runtime.StopAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public IReadOnlyList<RuleStatus> GetStatuses() => runtimes.Values.Select(r => r.Status).ToArray();

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            await Task.WhenAll(runtimes.Values.Select(r => r.StopAsync())).ConfigureAwait(false);
            runtimes.Clear();
        }
        finally { gate.Release(); }
    }

    private static async Task<IPAddress[]> ResolveTargetAsync(ForwardRule rule, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var addresses = await Dns.GetHostAddressesAsync(rule.TargetHost, timeout.Token).ConfigureAwait(false);
        addresses = addresses.Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToArray();
        // Recheck each actual resolution, not just the entered hostname: this also
        // prevents DNS aliases/rebinding from recursively connecting to this listener.
        if (addresses.Any(a => RuleValidator.IsSelfTarget(rule, a)))
            throw new ArgumentException("目标解析到本机的同一监听端口，已拒绝转发循环。", nameof(rule));
        return addresses;
    }

    private sealed class Runtime
    {
        private readonly ForwardRule rule;
        private readonly CancellationTokenSource lifetime;
        private readonly ConcurrentDictionary<long, Task> workers = new();
        private readonly SemaphoreSlim slots = new(MaximumConnectionsPerRule);
        private readonly ConcurrentDictionary<IPEndPoint, UdpSession> udpSessions = new();
        private readonly TimeSpan udpIdleTimeout;
        private TcpListener? listener;
        private UdpClient? udp;
        private Task loop = Task.CompletedTask;
        private int running, active;
        private long nextId, sent, received;
        private string? error;
        private bool stopped;
        public ForwardRule Rule => rule;
        public CancellationToken StartToken { get; }
        public Runtime(ForwardRule rule, CancellationToken token, TimeSpan udpIdleTimeout)
        {
            this.rule = rule;
            StartToken = token;
            this.udpIdleTimeout = udpIdleTimeout;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        }
        public RuleStatus Status => new(rule.Id, Volatile.Read(ref running) != 0, Volatile.Read(ref error),
            Volatile.Read(ref active), Interlocked.Read(ref sent), Interlocked.Read(ref received));

        public bool OwnsListener(IPEndPoint endpoint, bool useUdp)
        {
            if (!Status.Running || lifetime.IsCancellationRequested) return false;
            try
            {
                Socket? socket = useUdp ? udp?.Client : listener?.Server;
                return socket is not null && !socket.SafeHandle.IsClosed && socket.IsBound &&
                    endpoint.Equals(socket.LocalEndPoint);
            }
            catch (ObjectDisposedException) { return false; }
            catch (SocketException) { return false; }
        }

        public void Start()
        {
            if (!rule.Enabled) return;
            try
            {
                lifetime.Token.ThrowIfCancellationRequested();
                var endpoint = new IPEndPoint(IPAddress.Parse(rule.ListenAddress), rule.ListenPort);
                bool useTcp = rule.Protocol is TransportProtocol.Tcp or TransportProtocol.TcpUdp;
                bool useUdp = rule.Protocol is TransportProtocol.Udp or TransportProtocol.TcpUdp;
                if (useTcp)
                {
                    listener = new TcpListener(endpoint);
                    listener.Server.ExclusiveAddressUse = true;
                    listener.Start(128);
                }
                if (useUdp)
                {
                    udp = new UdpClient(endpoint.AddressFamily);
                    udp.ExclusiveAddressUse = true;
                    udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
                    udp.Client.Bind(endpoint);
                }
                Volatile.Write(ref running, 1);
                var loops = new List<Task>(2);
                if (useTcp) loops.Add(AcceptAsync());
                if (useUdp) loops.Add(ReceiveUdpAsync());
                loop = Task.WhenAll(loops);
            }
            catch (Exception ex)
            {
                listener?.Stop();
                udp?.Dispose();
                error = $"无法监听 {rule.ListenAddress}:{rule.ListenPort}: {ex.Message}";
                throw new InvalidOperationException(error, ex);
            }
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    await slots.WaitAsync(lifetime.Token).ConfigureAwait(false);
                    TcpClient client;
                    try { client = await listener!.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false); }
                    catch { slots.Release(); throw; }
                    Track(ForwardTcpAsync(client));
                }
            }
            catch (Exception ex) when (lifetime.IsCancellationRequested || ex is OperationCanceledException) { }
            catch (Exception ex) { error = $"监听失败: {ex.Message}"; lifetime.Cancel(); }
            finally { listener?.Stop(); Volatile.Write(ref running, 0); }
        }

        private async Task ForwardTcpAsync(TcpClient client)
        {
            Interlocked.Increment(ref active);
            using (client)
            using (var target = new TcpClient())
            using (var session = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                try
                {
                    using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    var addresses = await ResolveTargetAsync(rule, connectTimeout.Token).ConfigureAwait(false);
                    await target.ConnectAsync(addresses, rule.TargetPort, connectTimeout.Token).ConfigureAwait(false);
                    var clientStream = client.GetStream();
                    var targetStream = target.GetStream();
                    await Task.WhenAll(PumpAsync(clientStream, targetStream, target.Client, true, session),
                        PumpAsync(targetStream, clientStream, client.Client, false, session)).ConfigureAwait(false);
                }
                catch (Exception ex) when (lifetime.IsCancellationRequested) { _ = ex; }
                catch (Exception ex) { error = $"TCP 目标 {rule.TargetHost}:{rule.TargetPort}: {ex.Message}"; }
                finally { Interlocked.Decrement(ref active); slots.Release(); }
            }
        }

        private void Track(Task worker)
        {
            long id = Interlocked.Increment(ref nextId);
            workers[id] = worker;
            _ = worker.ContinueWith(_ => workers.TryRemove(id, out var ignored), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private async Task ReceiveUdpAsync()
        {
            IPAddress? targetAddress = null;
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    UdpReceiveResult packet;
                    try { packet = await udp!.ReceiveAsync(lifetime.Token).ConfigureAwait(false); }
                    catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
                    { continue; } // A vanished UDP client must not take down the shared listener.
                    try
                    {
                        if (!udpSessions.TryGetValue(packet.RemoteEndPoint, out var target))
                        {
                            if (udpSessions.Count >= MaximumConnectionsPerRule) continue;
                            using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                            dnsTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                            if (targetAddress is null)
                            {
                                var addresses = await ResolveTargetAsync(rule, dnsTimeout.Token).ConfigureAwait(false);
                                targetAddress = addresses.FirstOrDefault(a => a.AddressFamily == udp!.Client.AddressFamily) ?? addresses.First();
                            }
                            target = new UdpSession(targetAddress, rule.TargetPort, lifetime.Token, udpIdleTimeout);
                            udpSessions[packet.RemoteEndPoint] = target;
                            Interlocked.Increment(ref active);
                            Track(ReceiveUdpRepliesAsync(packet.RemoteEndPoint, target));
                        }
                        target.Touch();
                        await target.Client.SendAsync(packet.Buffer, target.Token).ConfigureAwait(false);
                        Interlocked.Add(ref sent, packet.Buffer.Length);
                    }
                    catch (Exception ex) when (!lifetime.IsCancellationRequested)
                    { error = $"UDP 目标 {rule.TargetHost}:{rule.TargetPort}: {ex.Message}"; }
                }
            }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex) { error = $"UDP 监听失败: {ex.Message}"; lifetime.Cancel(); }
            finally { udp?.Dispose(); Volatile.Write(ref running, 0); }
        }

        private async Task ReceiveUdpRepliesAsync(IPEndPoint client, UdpSession target)
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    // A connected per-client socket accepts replies only from this rule's target.
                    var packet = await target.Client.ReceiveAsync(target.Token).ConfigureAwait(false);
                    target.Touch();
                    await udp!.SendAsync(packet.Buffer, client, target.Token).ConfigureAwait(false);
                    Interlocked.Add(ref received, packet.Buffer.Length);
                }
            }
            catch (Exception) when (target.Token.IsCancellationRequested) { }
            catch (Exception ex) { error = $"UDP 目标 {rule.TargetHost}:{rule.TargetPort}: {ex.Message}"; }
            finally
            {
                udpSessions.TryRemove(client, out _);
                target.Dispose();
                Interlocked.Decrement(ref active);
            }
        }

        private sealed class UdpSession : IDisposable
        {
            public UdpClient Client { get; }
            private readonly CancellationTokenSource idle;
            private readonly TimeSpan timeout;
            public CancellationToken Token { get; }

            public UdpSession(IPAddress address, int port, CancellationToken lifetime, TimeSpan timeout)
            {
                this.timeout = timeout;
                idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                Token = idle.Token;
                Client = new UdpClient(address.AddressFamily);
                try { Client.Connect(address, port); Touch(); }
                catch { Dispose(); throw; }
            }

            public void Touch() => idle.CancelAfter(timeout);
            public void Dispose() { Client.Dispose(); idle.Dispose(); }
        }

        private async Task PumpAsync(NetworkStream source, NetworkStream destination, Socket destinationSocket,
            bool outbound, CancellationTokenSource session)
        {
            try
            {
                byte[] buffer = new byte[32 * 1024];
                while (true)
                {
                    int count = await source.ReadAsync(buffer, session.Token).ConfigureAwait(false);
                    if (count == 0)
                    {
                        // FIN closes only this direction. Forward EOF so the target can respond,
                        // but keep the reverse pump alive until its own EOF (or a real failure/stop).
                        destinationSocket.Shutdown(SocketShutdown.Send);
                        return;
                    }
                    await destination.WriteAsync(buffer.AsMemory(0, count), session.Token).ConfigureAwait(false);
                    if (outbound) Interlocked.Add(ref sent, count); else Interlocked.Add(ref received, count);
                }
            }
            catch
            {
                // A failed pump cannot make progress; unblock its sibling before WhenAll awaits it.
                session.Cancel();
                throw;
            }
        }

        public async Task StopAsync()
        {
            if (stopped) return;
            stopped = true;
            lifetime.Cancel();
            listener?.Stop();
            udp?.Dispose();
            await loop.ConfigureAwait(false);
            await Task.WhenAll(workers.Values).ConfigureAwait(false);
            Volatile.Write(ref running, 0);
            lifetime.Dispose();
            slots.Dispose();
        }
    }
}
