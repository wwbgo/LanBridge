using LanBridge.Control;
using LanBridge.Core;
using Xunit;

namespace LanBridge.Host.Tests;

public sealed class ManagedRuleOperationsTests
{
    private static ForwardRule Rule => new() { AutoFirewall = true, Enabled = true, ListenAddress = "0.0.0.0", ListenPort = 18080, TargetHost = "127.0.0.1", TargetPort = 8080 };
    [Fact]
    public async Task Timed_out_save_that_host_committed_is_confirmed_without_firewall_rollback()
    {
        var next = Rule;
        var changes = new List<bool>();
        var ops = new ManagedRuleOperations((_, _) => throw new TimeoutException("pipe timeout"), (_, _) => Task.CompletedTask,
            (_, enabled, _) => { changes.Add(enabled); return Task.CompletedTask; },
            _ => Task.FromResult(new BridgeState([next], [], "Console", "")));
        await ops.SaveAsync(next, null);
        Assert.Equal(new[] { true }, changes);
    }
    [Fact]
    public async Task Unknown_save_outcome_does_not_rollback_and_reports_reconciliation_needed()
    {
        var next = Rule;
        var changes = new List<bool>();
        var ops = new ManagedRuleOperations((_, _) => throw new TimeoutException("pipe timeout"), (_, _) => Task.CompletedTask,
            (_, enabled, _) => { changes.Add(enabled); return Task.CompletedTask; },
            _ => throw new TimeoutException("readback unavailable"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ops.SaveAsync(next, null));
        Assert.Contains("结果未知", error.Message);
        Assert.Equal(new[] { true }, changes);
    }
    [Fact]
    public async Task Timed_out_delete_that_host_committed_is_confirmed_without_firewall_restore()
    {
        var changes = new List<bool>();
        var ops = new ManagedRuleOperations((_, _) => Task.CompletedTask, (_, _) => throw new TimeoutException("pipe timeout"),
            (_, enabled, _) => { changes.Add(enabled); return Task.CompletedTask; },
            _ => Task.FromResult(new BridgeState([], [], "Console", "")));
        await ops.DeleteAsync(Rule);
        Assert.Equal(new[] { false }, changes);
    }
    [Fact]
    public async Task Default_rule_never_requests_firewall_elevation()
    {
        var events = new List<string>();
        await Create(events).SaveAsync(Rule with { AutoFirewall = false }, null);
        Assert.Equal(new[] { "save" }, events);
    }
    [Fact]
    public async Task Cancelled_elevation_does_not_save_rule()
    {
        bool saved = false;
        var ops = new ManagedRuleOperations((_, _) => { saved = true; return Task.CompletedTask; }, (_, _) => Task.CompletedTask,
            (_, _, _) => throw new OperationCanceledException("UAC cancelled"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ops.SaveAsync(Rule, null));
        Assert.False(saved);
    }
    [Fact]
    public async Task Failed_save_restores_previous_firewall_scope()
    {
        var previous = Rule;
        var changes = new List<(ForwardRule,bool)>();
        var ops = new ManagedRuleOperations((_, _) => throw new IOException("store failed"), (_, _) => Task.CompletedTask,
            (rule, enabled, _) => { changes.Add((rule,enabled)); return Task.CompletedTask; },
            _ => Task.FromResult(new BridgeState([previous], [], "Console", "")));
        await Assert.ThrowsAsync<IOException>(() => ops.SaveAsync(previous with { ListenPort = 18081 }, previous));
        Assert.Equal(new[] { (previous with { ListenPort = 18081 },true), (previous,true) }, changes);
    }
    [Fact]
    public async Task Disabling_or_unmanaging_removes_owned_firewall()
    {
        var previous = Rule;
        var events = new List<string>();
        await Create(events).SaveAsync(previous with { Enabled = false }, previous);
        Assert.Equal(new[] { "firewall:False", "save" }, events);
        events.Clear();
        await Create(events).SaveAsync(previous with { AutoFirewall = false }, previous);
        Assert.Equal(new[] { "firewall:False", "save" }, events);
    }
    [Fact]
    public async Task Delete_removes_firewall_before_deleting_saved_rule()
    {
        var events = new List<string>();
        var ops = Create(events);
        var method = typeof(ManagedRuleOperations).GetMethod("DeleteAsync");
        Assert.NotNull(method);
        await (Task)method.Invoke(ops, new object[] { Rule, CancellationToken.None })!;
        Assert.Equal(new[] { "firewall:False", "delete" }, events);
    }
    [Fact]
    public async Task Failed_rollback_is_reported_with_recovery_guidance()
    {
        int calls = 0;
        var ops = new ManagedRuleOperations((_, _) => throw new IOException("store failed"), (_, _) => Task.CompletedTask,
            (_, _, _) => ++calls == 1 ? Task.CompletedTask : throw new IOException("firewall offline"),
            _ => Task.FromResult(new BridgeState([], [], "Console", "")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ops.SaveAsync(Rule, null));
        Assert.Contains("回滚", error.Message);
    }
    private static ManagedRuleOperations Create(List<string> events) => new(
        (_, _) => { events.Add("save"); return Task.CompletedTask; },
        (_, _) => { events.Add("delete"); return Task.CompletedTask; },
        (_, enabled, _) => { events.Add("firewall:" + enabled); return Task.CompletedTask; });
    [Fact]
    public async Task Opted_in_save_sets_firewall_before_starting_listener()
    {
        var events = new List<string>();
        var type = typeof(BridgeClient).Assembly.GetType("LanBridge.Control.ManagedRuleOperations");
        Assert.NotNull(type);
        object operations = Activator.CreateInstance(type,
            (Func<ForwardRule,CancellationToken,Task>)((r, ct) => { events.Add("save"); return Task.CompletedTask; }),
            (Func<Guid,CancellationToken,Task>)((id, ct) => Task.CompletedTask),
            (Func<ForwardRule,bool,CancellationToken,Task>)((r, enabled, ct) => { events.Add("firewall:" + enabled); return Task.CompletedTask; }),
            (Func<CancellationToken,Task<BridgeState>>)(_ => Task.FromResult(new BridgeState([], [], "Console", ""))))!;
        var rule = new ForwardRule { AutoFirewall = true, Enabled = true, ListenAddress = "0.0.0.0", ListenPort = 18080, TargetHost = "127.0.0.1", TargetPort = 8080 };
        await (Task)type.GetMethod("SaveAsync")!.Invoke(operations, new object?[] { rule, null, CancellationToken.None })!;
        Assert.Equal(new[] { "firewall:True", "save" }, events);
    }
}
