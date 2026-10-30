using LanBridge.Core;

namespace LanBridge.Control;

/// <summary>Coordinates UI-requested rule changes with narrowly owned firewall changes.</summary>
public sealed class ManagedRuleOperations(
    Func<ForwardRule, CancellationToken, Task> save,
    Func<Guid, CancellationToken, Task> delete,
    Func<ForwardRule, bool, CancellationToken, Task> firewall,
    Func<CancellationToken, Task<BridgeState>>? readState = null)
{
    public async Task SaveAsync(ForwardRule next, ForwardRule? previous, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (previous is not null && next.Id != previous.Id) throw new ArgumentException("编辑不能更换规则 ID。");
        bool manage = next.AutoFirewall || previous?.AutoFirewall == true;
        if (manage) await firewall(next, next.AutoFirewall && next.Enabled, ct);
        try { await save(next, ct); }
        catch (Exception failure)
        {
            if (IsAmbiguous(failure) && await ResolveSaveAsync(next, previous, failure)) return;
            if (manage) await RestoreAsync(previous ?? next, previous?.AutoFirewall == true && previous.Enabled, failure);
            throw;
        }
    }

    public async Task DeleteAsync(ForwardRule rule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.AutoFirewall) await firewall(rule, false, ct);
        try { await delete(rule.Id, ct); }
        catch (Exception failure)
        {
            if (IsAmbiguous(failure) && await ResolveDeleteAsync(rule, failure)) return;
            if (rule.AutoFirewall) await RestoreAsync(rule, rule.Enabled, failure);
            throw;
        }
    }

    private static bool IsAmbiguous(Exception ex) => ex is TimeoutException or IOException or OperationCanceledException;

    private async Task<bool> ResolveSaveAsync(ForwardRule next, ForwardRule? previous, Exception failure)
    {
        if (readState is null) throw Unknown(failure, "规则修改结果未知，无法确认后台是否提交；请刷新状态后再操作。", null);
        BridgeState state;
        try { state = await readState(CancellationToken.None); }
        catch (Exception readFailure) { throw Unknown(failure, "规则修改结果未知，状态读取也失败；请刷新状态后再操作。", readFailure); }
        var current = state.Rules.FirstOrDefault(r => r.Id == next.Id);
        if (current == next) return true;
        if (previous is null ? current is null : current == previous) return false;
        throw Unknown(failure, "规则修改结果未知：后台状态与新旧规则均不匹配；请刷新后核对，不要重复操作。", null);
    }

    private async Task<bool> ResolveDeleteAsync(ForwardRule rule, Exception failure)
    {
        if (readState is null) throw Unknown(failure, "删除结果未知，无法确认后台是否提交；请刷新状态后再操作。", null);
        BridgeState state;
        try { state = await readState(CancellationToken.None); }
        catch (Exception readFailure) { throw Unknown(failure, "删除结果未知，状态读取也失败；请刷新状态后再操作。", readFailure); }
        var current = state.Rules.FirstOrDefault(r => r.Id == rule.Id);
        if (current is null) return true;
        if (current == rule) return false;
        throw Unknown(failure, "删除结果未知：后台规则已变化；请刷新后核对，不要重复操作。", null);
    }

    private static InvalidOperationException Unknown(Exception failure, string message, Exception? readFailure) =>
        new(message + " 原请求错误：" + failure.Message + (readFailure is null ? "" : "；状态读取错误：" + readFailure.Message),
            readFailure is null ? failure : new AggregateException(failure, readFailure));

    private async Task RestoreAsync(ForwardRule rule, bool enabled, Exception failure)
    {
        // Never reuse the cancelled UI token: rollback is an independent recovery action.
        try { await firewall(rule, enabled, CancellationToken.None); }
        catch (Exception rollback)
        {
            throw new InvalidOperationException($"规则操作失败，防火墙回滚也失败（规则 {rule.Id:D}）。请重试编辑或删除以同步状态。原错误：{failure.Message}；回滚：{rollback.Message}",
                new AggregateException(failure, rollback));
        }
    }
}
