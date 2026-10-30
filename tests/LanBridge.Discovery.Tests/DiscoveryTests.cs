using LanBridge.Discovery;
using Xunit;

namespace LanBridge.Discovery.Tests;

public sealed class DiscoveryTests
{

    [Fact]
    public async Task WslQueriesOnlyRunningDistrosWithSeparateArgumentsAndFiltersAddresses()
    {
        var commands = new ScriptedCommands();
        commands.Add("wsl.exe", ["--list", "--verbose"], new(0, "\uFEFF  NAME            STATE           VERSION\n* Ubuntu Test     Running         2\n  Debian          Stopped         2\n  docker-desktop  Running         2\n  Ubuntu Test     Running         2\n", ""));
        commands.Add("wsl.exe", ["--list", "--running", "--quiet"], new(0, "Ubuntu Test\ndocker-desktop\n", ""));
        commands.Add("wsl.exe", ["--distribution", "Ubuntu Test", "--exec", "ip", "-j", "addr", "show"], new(0, "[{\"ifname\":\"eth0\",\"addr_info\":[{\"family\":\"inet\",\"local\":\"172.28.4.9\"},{\"family\":\"inet6\",\"local\":\"fd00::8\"},{\"family\":\"inet6\",\"local\":\"2001:db8::2\"},{\"family\":\"inet6\",\"local\":\"fe80::1\"}]},{\"ifname\":\"lo\",\"addr_info\":[{\"family\":\"inet\",\"local\":\"127.0.0.1\"}]}]", ""));
        var result = await new EndpointDiscovery(commands).DiscoverAsync(TargetPlatform.Wsl);
        Assert.Equal(new[] { "172.28.4.9", "fd00::8", "2001:db8::2" }, result.Endpoints.Select(e => e.Host));
        Assert.All(result.Endpoints, e => { Assert.Null(e.Port); Assert.Equal(LanBridge.Core.TransportProtocol.Tcp, e.Protocol); Assert.Contains("WSL", e.Name); Assert.Contains("端口", e.Detail); });
        Assert.DoesNotContain(result.Warnings, w => w.Contains("WSL1"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("hostname", StringComparison.OrdinalIgnoreCase));
        commands.AssertComplete();
    }

    [Fact]
    public async Task WslFallsBackToReadOnlyTextAddressListingWithoutStartingStoppedOrManagedDistros()
    {
        var commands = new ScriptedCommands();
        commands.Add("wsl.exe", ["--list", "--verbose"], new(0, "NAME STATE VERSION\n* Ubuntu Running 2\n  Debian Stopped 2\n  podman-machine-default Running 2\n  docker-desktop Running 2\n", ""));
        commands.Add("wsl.exe", ["--list", "--running", "--quiet"], new(0, "Ubuntu\npodman-machine-default\ndocker-desktop\n", ""));
        commands.Add("wsl.exe", ["--distribution", "Ubuntu", "--exec", "ip", "-j", "addr", "show"], new(1, "", "ip: unrecognized option"));
        commands.Add("wsl.exe", ["--distribution", "Ubuntu", "--exec", "ip", "-o", "addr", "show"], new(0, "2: eth0    inet 10.0.0.8/24 scope global eth0\n2: eth0    inet6 fe80::1/64 scope link\n", ""));
        var result = await new EndpointDiscovery(commands).DiscoverAsync(TargetPlatform.Wsl);
        Assert.Equal(new[] { "10.0.0.8" }, result.Endpoints.Select(e => e.Host));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("停止", StringComparison.Ordinal));
        commands.AssertComplete();
    }


    [Theory]
    [InlineData(1, "", "Access denied", null)]
    [InlineData(0, "", "", "timed out")]
    public async Task WslQueryFailureIsWarning(int code, string output, string stderr, string? error)
    {
        var commands = new ScriptedCommands();
        commands.Add("wsl.exe", ["--list", "--verbose"], new(0, "NAME STATE VERSION\n* Ubuntu Running 2\n", ""));
        commands.Add("wsl.exe", ["--list", "--running", "--quiet"], new(0, "Ubuntu\n", ""));
        commands.Add("wsl.exe", ["--distribution", "Ubuntu", "--exec", "ip", "-j", "addr", "show"], new(code, output, stderr, error));
        commands.Add("wsl.exe", ["--distribution", "Ubuntu", "--exec", "ip", "-o", "addr", "show"], new(1, "", "fallback unavailable"));
        var result = await new EndpointDiscovery(commands).DiscoverAsync(TargetPlatform.Wsl);
        Assert.Empty(result.Endpoints);
        Assert.Contains(result.Warnings, w => w.Contains(error ?? stderr));
        commands.AssertComplete();
    }

    [Fact]
    public async Task WslWorkIsBoundedAndMalformedNamesAreSkipped()
    {
        var commands = new ScriptedCommands();
        var names = Enumerable.Range(0, 18).Select(i => $"Distro-{i}").ToArray();
        commands.Add("wsl.exe", ["--list", "--verbose"], new(0, "bad\0name Running 2\n" + string.Join('\n', names.Select(name => $"  {name} Running 2")), ""));
        commands.Add("wsl.exe", ["--list", "--running", "--quiet"], new(0, "bad\0name\n" + string.Join('\n', names), ""));
        foreach (var name in names.Take(16)) commands.Add("wsl.exe", ["--distribution", name, "--exec", "ip", "-j", "addr", "show"], new(0, "[]", ""));
        var result = await new EndpointDiscovery(commands).DiscoverAsync(TargetPlatform.Wsl);
        Assert.Contains(result.Warnings, w => w.Contains("超过", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("无效", StringComparison.Ordinal));
        commands.AssertComplete();
    }

    [Fact]
    public async Task DockerRequiresProvenLocalNpipeContextAndExpandsPublishedPorts()
    {
        var commands = new ScriptedCommands();
        commands.Add("docker.exe", ["context", "inspect"], new(0, "[{\"Endpoints\":{\"docker\":{\"Host\":\"npipe:////./pipe/docker_engine\"}}}]", ""));
        commands.Add("docker.exe", ["--host", "npipe:////./pipe/docker_engine", "version", "--format", "json"], new(0, "{\"Server\":{\"Version\":\"27\"}}", ""));
        commands.Add("docker.exe", ["--host", "npipe:////./pipe/docker_engine", "ps", "--format", "{{json .}}"], new(0, "{\"ID\":\"abc123\",\"Names\":\"web\"}\n", ""));
        commands.Add("docker.exe", ["--host", "npipe:////./pipe/docker_engine", "inspect", "--format", "{{json .NetworkSettings.Ports}}", "abc123"], new(0, Fixture("docker-inspect.json"), ""));
        var result = await new EndpointDiscovery(commands).DiscoverAsync(TargetPlatform.Docker);
        Assert.Equal(new[] { "127.0.0.1", "127.0.0.1", "::1" }, result.Endpoints.Select(e => e.Host));
        Assert.Equal(new[] { 18080, 18081, 5353 }, result.Endpoints.Select(e => e.Port!.Value));
        Assert.Equal(new[] { LanBridge.Core.TransportProtocol.Tcp, LanBridge.Core.TransportProtocol.Tcp, LanBridge.Core.TransportProtocol.Udp }, result.Endpoints.Select(e => e.Protocol));
        Assert.Contains(result.Warnings, w => w.Contains("未发布端口", StringComparison.Ordinal));
        commands.AssertComplete();
    }

    [Fact]
    public async Task DockerEmptyContextDoesNotInventLocalMappings()
    {
        var commands = new ScriptedCommands();
        commands.Add("docker.exe", ["context", "inspect"], new(0, "[]", ""));
        var result = await new EndpointDiscovery(commands).DiscoverAsync(TargetPlatform.Docker);
        Assert.Empty(result.Endpoints);
        Assert.Contains(result.Warnings, w => w.Contains("上下文", StringComparison.Ordinal));
        commands.AssertComplete();
    }

    [Fact]
    public async Task PodmanRequiresRunningMachineMatchingDefaultLocalConnection()
    {
        var commands = new ScriptedCommands();
        commands.Add("podman.exe", ["machine", "list", "--format", "json"], new(0, Fixture("podman-machine.json"), ""));
        commands.Add("podman.exe", ["system", "connection", "list", "--format", "json"], new(0, Fixture("podman-connections.json"), ""));
        commands.Add("podman.exe", ["--connection", "podman-machine-default", "ps", "--format", "{{json .}}"], new(0, "{\"ID\":\"p123\",\"Names\":\"api\"}\n", ""));
        commands.Add("podman.exe", ["--connection", "podman-machine-default", "inspect", "--format", "{{json .NetworkSettings.Ports}}", "p123"], new(0, Fixture("podman-inspect.json"), ""));
        var result = await new EndpointDiscovery(commands).DiscoverAsync(TargetPlatform.Podman);
        Assert.Equal(new[] { "127.0.0.1", "::1" }, result.Endpoints.Select(e => e.Host));
        Assert.Equal(new[] { 8080, 8443 }, result.Endpoints.Select(e => e.Port!.Value));
        commands.AssertComplete();
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public async Task MissingWslReturnsWarningWithoutInventedEndpoints()
    {
        var commands = new ScriptedCommands();
        commands.Add("wsl.exe", ["--list", "--verbose"], new(0, "", "", "CLI 未找到"));

        var result = await new EndpointDiscovery(commands).DiscoverAsync(TargetPlatform.Wsl);
        Assert.Empty(result.Endpoints);
        Assert.Contains(result.Warnings, w => w.Contains("CLI 未找到"));
        commands.AssertComplete();
    }
}

internal sealed class ScriptedCommands : ICommandRunner
{
    private readonly Queue<(string File, string[] Args, CommandResult Result)> script = new();
    public void Add(string file, string[] args, CommandResult result) => script.Enqueue((file, args, result));
    public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var next = script.Dequeue();
        Assert.Equal(next.File, command.FileName);
        Assert.Equal(next.Args, command.Arguments);
        Assert.Equal(command.FileName == "wsl.exe" ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(3), command.Timeout);
        return Task.FromResult(next.Result);
    }
    public void AssertComplete() => Assert.Empty(script);
}
