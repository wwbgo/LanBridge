using LanBridge.Control;
using LanBridge.Core;
using LanBridge.Host;
using Xunit;

namespace LanBridge.Host.Tests;

public class PipeServerTests
{
    [Fact]
    public async Task Oversized_state_returns_a_bounded_error_instead_of_disconnecting()
    {
        string directory = CoordinatorTests.TestDirectory();
        Directory.CreateDirectory(directory);
        string name = "LanBridge.Test.Host." + Guid.NewGuid().ToString("N");
        var rule = new ForwardRule { Name = new string('x', PipeProtocol.MaximumPayloadBytes), Enabled = false };
        await File.WriteAllTextAsync(Path.Combine(directory, "rules.json"), System.Text.Json.JsonSerializer.Serialize(new[] { rule }, PipeProtocol.Json));
        try
        {
            await using var coordinator = new BridgeCoordinator(directory, "Console");
            await coordinator.InitializeAsync();
            using var stop = new CancellationTokenSource();
            var serving = new PipeControlServer(PipeProtocol.CurrentOwnerSid, new(coordinator), new(Path.Combine(directory, "host.log")), name).RunAsync(stop.Token);
            try
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new BridgeClient(name).GetStateAsync());
                Assert.Contains("安全上限", error.Message);
            }
            finally { stop.Cancel(); await serving.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Real_acl_pipe_supports_crud_toggle_and_restart_persistence()
    {
        string directory = CoordinatorTests.TestDirectory();
        string name = "LanBridge.Test.Host." + Guid.NewGuid().ToString("N");
        var rule = new ForwardRule { Name = "real pipe", ListenPort = CoordinatorTests.FreePort(), TargetHost = "127.0.0.1", TargetPort = CoordinatorTests.FreePort(), Enabled = false };
        try
        {
            await using (var coordinator = new BridgeCoordinator(directory, "Console"))
            {
                await coordinator.InitializeAsync();
                var server = new PipeControlServer(PipeProtocol.CurrentOwnerSid, new(coordinator), new(Path.Combine(directory, "host.log")), pipeName: name);
                using var stop = new CancellationTokenSource();
                var serving = server.RunAsync(stop.Token);
                try
                {
                    var client = new BridgeClient(name);
                    await client.SaveRuleAsync(rule);
                    Assert.Equal(rule, Assert.Single((await client.GetStateAsync()).Rules));
                    await client.SetEnabledAsync(rule.Id, true);
                    var enabled = await client.GetStateAsync();
                    Assert.True(Assert.Single(enabled.Rules).Enabled);
                    Assert.True(Assert.Single(enabled.Statuses).Running);
                    await client.SetEnabledAsync(rule.Id, false);
                    Assert.False(Assert.Single((await client.GetStateAsync()).Statuses).Running);
                    Assert.Contains("目标", await client.ProbeAsync(rule));
                }
                finally { stop.Cancel(); await serving.WaitAsync(TimeSpan.FromSeconds(5)); }
            }
            await using (var restored = new BridgeCoordinator(directory, "Console"))
            {
                await restored.InitializeAsync();
                Assert.Equal(rule, Assert.Single((await restored.GetStateAsync()).Rules));
                await restored.DeleteRuleAsync(rule.Id);
            }
            await using var empty = new BridgeCoordinator(directory, "Console");
            await empty.InitializeAsync();
            Assert.Empty((await empty.GetStateAsync()).Rules);
        }
        finally { Directory.Delete(directory, true); }
    }
}
