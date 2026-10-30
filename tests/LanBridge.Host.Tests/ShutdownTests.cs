using LanBridge.Control;
using LanBridge.Core;
using LanBridge.Host;
using Xunit;

namespace LanBridge.Host.Tests;

public class ShutdownTests
{
    [Fact]
    public async Task Console_shutdown_releases_listeners_preserves_enabled_config_and_rejects_new_mutations()
    {
        string path = CoordinatorTests.TestDirectory();
        try
        {
            await using var coordinator = new BridgeCoordinator(path, "Console");
            await coordinator.InitializeAsync();
            var rule = new ForwardRule { Name = "shutdown", ListenPort = CoordinatorTests.FreePort(), TargetHost = "127.0.0.1", TargetPort = CoordinatorTests.FreePort() };
            await coordinator.SaveRuleAsync(rule);
            var dispatcher = new BridgeDispatcher(coordinator);
            var response = await dispatcher.DispatchAsync(new("shutdown"));
            Assert.True(response.Success);
            var state = await coordinator.GetStateAsync();
            Assert.True(Assert.Single(state.Rules).Enabled);
            Assert.All(state.Statuses, status => Assert.False(status.Running));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SaveRuleAsync(rule));
            await using var restored = new BridgeCoordinator(path, "Console");
            await restored.InitializeAsync();
            Assert.True(Assert.Single((await restored.GetStateAsync()).Statuses).Running);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public async Task Service_shutdown_is_explicitly_rejected()
    {
        string path = CoordinatorTests.TestDirectory();
        try
        {
            await using var coordinator = new BridgeCoordinator(path, "Service");
            await coordinator.InitializeAsync();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new BridgeDispatcher(coordinator).DispatchAsync(new("shutdown")));
            Assert.Contains("服务", ex.Message);
        }
        finally { Directory.Delete(path, true); }
    }
}
