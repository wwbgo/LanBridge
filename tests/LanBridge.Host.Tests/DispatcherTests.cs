using LanBridge.Control;
using LanBridge.Core;
using LanBridge.Host;
using Xunit;

namespace LanBridge.Host.Tests;

public class DispatcherTests
{
    [Fact]
    public async Task Unknown_and_ambiguous_commands_are_rejected_without_touching_store()
    {
        string path = CoordinatorTests.TestDirectory();
        await using (var coordinator = new BridgeCoordinator(path, "Console"))
        {
            await coordinator.InitializeAsync();
            var dispatcher = new BridgeDispatcher(coordinator);
            await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.DispatchAsync(new("runScript")));
            await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.DispatchAsync(new("getState", Id: Guid.NewGuid())));
            await Assert.ThrowsAsync<ArgumentException>(() => dispatcher.DispatchAsync(new("saveRule")));
            Assert.Empty((await coordinator.GetStateAsync()).Rules);
            Assert.False(File.Exists(Path.Combine(path, "rules.json")));
        }
        Directory.Delete(path, true);
    }

    [Fact]
    public async Task Combined_probe_reports_tcp_only_without_claiming_udp_health()
    {
        string path = CoordinatorTests.TestDirectory();
        try
        {
            await using var coordinator = new BridgeCoordinator(path, "Console");
            var response = await new BridgeDispatcher(coordinator).DispatchAsync(new("probe", Rule: new ForwardRule
            { ListenPort = 23457, TargetHost = "127.0.0.1", TargetPort = 23458, Protocol = (TransportProtocol)2 }));
            Assert.True(response.Success);
            Assert.Contains("UDP 未验证", response.Probe);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public async Task Udp_probe_is_explicitly_not_a_tcp_reachability_claim()
    {
        string path = CoordinatorTests.TestDirectory();
        await using (var coordinator = new BridgeCoordinator(path, "Console"))
        {
            var response = await new BridgeDispatcher(coordinator).DispatchAsync(new("probe", Rule: new ForwardRule
            { ListenPort = 23457, TargetHost = "127.0.0.1", TargetPort = 23458, Protocol = TransportProtocol.Udp }));
            Assert.True(response.Success);
            Assert.Contains("UDP 无连接", response.Probe);
            Assert.Contains("不能通过 TCP", response.Probe);
        }
        Directory.Delete(path, true);
    }
}
