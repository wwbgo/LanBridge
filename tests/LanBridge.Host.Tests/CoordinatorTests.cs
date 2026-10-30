using LanBridge.Control;
using LanBridge.Core;
using LanBridge.Host;
using Xunit;

namespace LanBridge.Host.Tests;

public sealed class CoordinatorTests
{
    [Fact]
    public async Task Invalid_or_conflicting_update_preserves_previous_rule_disk_and_listener()
    {
        string directory = TestDirectory();
        var target = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        target.Start(); blocker.Start();
        int listenPort = FreePort();
        var original = new ForwardRule { Name = "working", ListenPort = listenPort, TargetHost = "127.0.0.1", TargetPort = ((System.Net.IPEndPoint)target.LocalEndpoint).Port };
        try
        {
            await using var coordinator = new BridgeCoordinator(directory, "Console");
            await coordinator.InitializeAsync();
            await coordinator.SaveRuleAsync(original);
            string disk = await File.ReadAllTextAsync(Path.Combine(directory, "rules.json"));
            await Assert.ThrowsAsync<ArgumentException>(() => coordinator.SaveRuleAsync(original with { TargetPort = 0 }));
            await Assert.ThrowsAnyAsync<Exception>(() => coordinator.SaveRuleAsync(original with { ListenPort = ((System.Net.IPEndPoint)blocker.LocalEndpoint).Port }));
            Assert.Equal(original, Assert.Single((await coordinator.GetStateAsync()).Rules));
            Assert.Equal(disk, await File.ReadAllTextAsync(Path.Combine(directory, "rules.json")));
            Assert.True(Assert.Single((await coordinator.GetStateAsync()).Statuses).Running);
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, listenPort).WaitAsync(TimeSpan.FromSeconds(3));
            using var accepted = await target.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { target.Stop(); blocker.Stop(); Directory.Delete(directory, true); }
    }

    internal static string TestDirectory() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", "LanBridgeTests", Guid.NewGuid().ToString("N"));
    internal static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Corrupt_configuration_is_reported_without_reset_or_overwrite()
    {
        string directory = TestDirectory();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "rules.json");
        const string corrupt = "{ invalid configuration";
        await File.WriteAllTextAsync(path, corrupt);
        try
        {
            await using var coordinator = new BridgeCoordinator(directory, "Console");
            await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.InitializeAsync());
            Assert.Equal(corrupt, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task One_bad_restored_rule_does_not_prevent_other_enabled_rules()
    {
        string directory = TestDirectory();
        Directory.CreateDirectory(directory);
        var good = new ForwardRule { ListenPort = FreePort(), TargetHost = "127.0.0.1", TargetPort = FreePort() };
        var bad = good with { Id = Guid.NewGuid(), ListenAddress = "invalid" };
        string path = Path.Combine(directory, "rules.json");
        string json = System.Text.Json.JsonSerializer.Serialize(new[] { bad, good }, PipeProtocol.Json);
        await File.WriteAllTextAsync(path, json);
        try
        {
            await using var coordinator = new BridgeCoordinator(directory, "Console");
            await coordinator.InitializeAsync();
            var state = await coordinator.GetStateAsync();
            Assert.True(state.Statuses.Single(s => s.Id == good.Id).Running);
            Assert.False(state.Statuses.Single(s => s.Id == bad.Id).Running);
            Assert.NotNull(state.Statuses.Single(s => s.Id == bad.Id).Error);
            Assert.Equal(json, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Failed_persistence_rolls_back_a_disable_and_keeps_previous_runtime()
    {
        string directory = TestDirectory();
        var rule = new ForwardRule { ListenPort = FreePort(), TargetHost = "127.0.0.1", TargetPort = FreePort() };
        try
        {
            await using var coordinator = new BridgeCoordinator(directory, "Console");
            await coordinator.InitializeAsync();
            await coordinator.SaveRuleAsync(rule);
            string path = Path.Combine(directory, "rules.json");
            File.Move(path, path + ".backup");
            Directory.CreateDirectory(path); // Force atomic replacement to fail, without mocks or permission assumptions.
            var error = await Record.ExceptionAsync(() => coordinator.SetEnabledAsync(rule.Id, false));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Expected a persistence failure");
            var state = await coordinator.GetStateAsync();
            Assert.Equal(rule, Assert.Single(state.Rules));
            Assert.True(Assert.Single(state.Statuses).Running);
            Directory.Delete(path);
            File.Move(path + ".backup", path);
            await coordinator.SetEnabledAsync(rule.Id, false);
            Assert.False(Assert.Single((await coordinator.GetStateAsync()).Rules).Enabled);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Crud_persists_disabled_rule_and_restores_after_restart()
    {
        string directory = TestDirectory();
        Directory.CreateDirectory(directory);
        try
        {
            var rule = new ForwardRule { Name = "持久化", ListenPort = 24119, TargetHost = "127.0.0.1", TargetPort = 24120, Enabled = false };
            await using (var coordinator = new BridgeCoordinator(directory, "Console"))
            {
                await coordinator.InitializeAsync();
                await coordinator.SaveRuleAsync(rule);
                Assert.Equal(rule, Assert.Single((await coordinator.GetStateAsync()).Rules));
            }
            await using (var restored = new BridgeCoordinator(directory, "Console"))
            {
                await restored.InitializeAsync();
                Assert.Equal(rule, Assert.Single((await restored.GetStateAsync()).Rules));
                await restored.DeleteRuleAsync(rule.Id);
                Assert.Empty((await restored.GetStateAsync()).Rules);
            }
            await using var empty = new BridgeCoordinator(directory, "Console");
            await empty.InitializeAsync();
            Assert.Empty((await empty.GetStateAsync()).Rules);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Direct_set_enabled_rejects_auto_firewall_rules()
    {
        string directory = TestDirectory();
        try
        {
            await using var coordinator = new BridgeCoordinator(directory, "Console");
            await coordinator.InitializeAsync();
            var rule = new ForwardRule { Id = Guid.NewGuid(), ListenPort = FreePort(), TargetHost = "127.0.0.1", TargetPort = FreePort(), Enabled = false, AutoFirewall = true };
            await coordinator.SaveRuleAsync(rule);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SetEnabledAsync(rule.Id, true));
            Assert.Contains("防火墙", error.Message);
            Assert.Equal(rule, Assert.Single((await coordinator.GetStateAsync()).Rules));
        }
        finally { Directory.Delete(directory, true); }
    }
}
