using LanBridge.Control;
using LanBridge.Core;
using System.Diagnostics;

namespace LanBridge.Host;

/// <summary>Serializes mutations; a failed validation or bind never reaches persistence.</summary>
public sealed class BridgeCoordinator : IAsyncDisposable
{
    private readonly HostRuleRepository store;
    private readonly ForwardingEngine engine = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string mode;
    private readonly string logPath;
    private List<ForwardRule> rules = [];
    private readonly Dictionary<Guid, string> startupErrors = [];
    private bool initialized;
    private bool shuttingDown;

    public BridgeCoordinator(string directory, string mode)
    {
        Directory.CreateDirectory(directory);
        store = new HostRuleRepository(Path.Combine(directory, "rules.json"));
        logPath = Path.Combine(directory, "host.log");
        this.mode = mode;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (initialized) return;
            rules = (await store.LoadAsync(ct)).ToList(); // A corrupt store must fail startup visibly.
            if (rules.Count > 128 || rules.Select(r => r.Id).Distinct().Count() != rules.Count)
                throw new InvalidDataException("配置超出 128 条规则或包含重复 ID；未修改配置。");
            foreach (var rule in rules.Where(r => r.Enabled))
            {
                try { await engine.StartAsync(rule, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { startupErrors[rule.Id] = ex.Message; }
            }
            initialized = true;
        }
        finally { gate.Release(); }
    }

    public async Task<BridgeState> GetStateAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            EnsureInitialized();
            var statuses = engine.GetStatuses().ToDictionary(s => s.Id);
            foreach (var rule in rules)
                if (startupErrors.TryGetValue(rule.Id, out var error))
                    statuses[rule.Id] = new(rule.Id, false, error, 0, 0, 0);
                else if (!statuses.ContainsKey(rule.Id))
                    statuses[rule.Id] = new(rule.Id, false, null, 0, 0, 0);
            using var process = Process.GetCurrentProcess();
            var path = process.MainModule?.FileName ?? Environment.ProcessPath ?? "";
            var startTime = process.StartTime.ToUniversalTime().Ticks;
            return new(rules.ToList(), statuses.Values.Where(s => rules.Any(r => r.Id == s.Id)).ToList(), mode, logPath,
                Environment.ProcessId, path, startTime);
        }
        finally { gate.Release(); }
    }

    public async Task<ListenCheckResult> CheckListenAsync(ForwardRule rule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        await gate.WaitAsync(ct);
        try
        {
            EnsureInitialized();
            if (shuttingDown) return new(ListenCheckStatus.Unknown, "后台正在退出，无法确认监听端口。");
            if (System.Net.IPAddress.TryParse(rule.ListenAddress, out var address) &&
                rule.ListenPort is >= 1 and <= 65535 && Enum.IsDefined(rule.Protocol))
            {
                var conflict = rules.FirstOrDefault(saved => saved.Id != rule.Id && saved.ListenPort == rule.ListenPort &&
                    Enum.IsDefined(saved.Protocol) &&
                    (saved.Protocol == rule.Protocol || saved.Protocol == TransportProtocol.TcpUdp || rule.Protocol == TransportProtocol.TcpUdp) &&
                    System.Net.IPAddress.TryParse(saved.ListenAddress, out var savedAddress) &&
                    savedAddress.AddressFamily == address.AddressFamily &&
                    (savedAddress.Equals(address) || savedAddress.Equals(System.Net.IPAddress.Any) ||
                     savedAddress.Equals(System.Net.IPAddress.IPv6Any) || address.Equals(System.Net.IPAddress.Any) ||
                     address.Equals(System.Net.IPAddress.IPv6Any)));
                if (conflict is not null)
                    return new(ListenCheckStatus.Conflict, $"本机监听与已保存规则“{conflict.Name}”（{conflict.Id}，{(conflict.Enabled ? "已启用" : "已禁用")}，{conflict.Protocol} {conflict.ListenAddress}:{conflict.ListenPort}）重叠；请调整监听地址、端口或协议。保存仍由后台实际校验。");
            }
            return await engine.CheckListenAsync(rule, ct);
        }
        finally { gate.Release(); }
    }

    public async Task SaveRuleAsync(ForwardRule rule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        await gate.WaitAsync(ct);
        try
        {
            EnsureInitialized();
            if (shuttingDown) throw new InvalidOperationException("后台正在退出，不能更改规则。");
            var errors = RuleValidator.Validate(rule);
            if (errors.Count != 0) throw new ArgumentException(string.Join("；", errors));
            var old = rules.FirstOrDefault(r => r.Id == rule.Id);
            if (old is null && rules.Count >= 128) throw new InvalidOperationException("最多支持 128 条规则。");
            var next = rules.Where(r => r.Id != rule.Id).Append(rule).ToList();
            await ApplyAndPersistAsync(rule.Id, old, rule, next);
        }
        finally { gate.Release(); }
    }

    public async Task DeleteRuleAsync(Guid id, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            EnsureInitialized();
            if (shuttingDown) throw new InvalidOperationException("后台正在退出，不能更改规则。");
            var old = rules.FirstOrDefault(r => r.Id == id);
            if (old is null) return;
            await ApplyAndPersistAsync(id, old, null, rules.Where(r => r.Id != id).ToList());
        }
        finally { gate.Release(); }
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            EnsureInitialized();
            if (shuttingDown) throw new InvalidOperationException("后台正在退出，不能更改规则。");
            var old = rules.FirstOrDefault(r => r.Id == id) ?? throw new KeyNotFoundException("规则不存在。");
            if (old.AutoFirewall)
                throw new InvalidOperationException("此规则启用了自动防火墙管理；请通过 LanBridge 桌面界面启停，以同步其防火墙许可。");
            var next = old with { Enabled = enabled };
            var errors = RuleValidator.Validate(next);
            if (errors.Count != 0) throw new ArgumentException(string.Join("；", errors));
            await ApplyAndPersistAsync(id, old, next, rules.Select(r => r.Id == id ? next : r).ToList());
        }
        finally { gate.Release(); }
    }

    private async Task ApplyAndPersistAsync(Guid id, ForwardRule? old, ForwardRule? next, List<ForwardRule> nextRules)
    {
        // Once mutation starts it runs to commit/rollback, even if the requesting pipe disconnects.
        // Defend at the orchestration boundary as well: a rejected rebind must preserve the old listener,
        // even if the engine has already torn down its previous runtime before reporting the failure.
        try
        {
            if (next?.Enabled == true) await engine.StartAsync(next);
            else await engine.StopAsync(id);
        }
        catch
        {
            if (old?.Enabled == true && !engine.GetStatuses().Any(s => s.Id == id && s.Running))
            {
                try { await engine.StartAsync(old); }
                catch (Exception rollbackError) { startupErrors[id] = "更新失败，恢复监听失败：" + rollbackError.Message; }
            }
            throw;
        }
        try { await store.SaveAsync(nextRules); }
        catch
        {
            try
            {
                if (old?.Enabled == true) await engine.StartAsync(old);
                else await engine.StopAsync(id);
            }
            catch (Exception rollbackError) { startupErrors[id] = "持久化失败，恢复监听失败：" + rollbackError.Message; }
            throw;
        }
        rules = nextRules;
        startupErrors.Remove(id);
    }

    private void EnsureInitialized()
    {
        if (!initialized) throw new InvalidOperationException("后端尚未初始化。");
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!mode.Equals("Console", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("当前后台为 Windows 服务；请使用服务管理器停止服务，托盘不会擅自操作系统服务。");
            EnsureInitialized();
            if (shuttingDown) return;
            shuttingDown = true;
            foreach (var rule in rules) await engine.StopAsync(rule.Id);
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { await engine.DisposeAsync(); }
        finally { gate.Release(); gate.Dispose(); }
    }
}
