using LanBridge.Discovery;
using Xunit;

namespace LanBridge.Discovery.Tests;

public sealed class RobustnessTests
{
    private const string Machines = "[{\"Name\":\"local\",\"Running\":true,\"Port\":51048}]";
    private const string Connections = "[{\"Name\":\"local\",\"URI\":\"ssh://user@127.0.0.1:51048/run/podman.sock\",\"IsMachine\":true,\"Default\":true}]";

    [Theory]
    [InlineData("null", Connections)]
    [InlineData("{}", Connections)]
    [InlineData("[null]", Connections)]
    [InlineData("[{\"Name\":\"local\",\"Running\":true,\"Port\":\"51048\"}]", Connections)]
    [InlineData(Machines, "null")]
    [InlineData(Machines, "[null]")]
    [InlineData(Machines, "not json")]
    public async Task PodmanUnexpectedJsonShapesProduceWarnings(string machines, string connections)
    {
        var commands = PodmanProof(machines, connections);
        var result = await new EndpointDiscovery(commands).DiscoverAsync(TargetPlatform.Podman);
        Assert.Empty(result.Endpoints);
        Assert.NotEmpty(result.Warnings);
        commands.AssertComplete();
    }

    [Theory]
    [InlineData("[{\"Endpoints\":{\"docker\":{\"Host\":\"npipe:////evil-server/pipe/docker_engine\"}}}]")]
    [InlineData("[{\"Endpoints\":{\"kubernetes\":{\"Host\":\"npipe:////./pipe/docker_engine\"}}}]")]
    [InlineData("[{\"Host\":\"npipe:////./pipe/docker_engine\"}]")]
    [InlineData("[{\"Endpoints\":{\"docker\":{\"Host\":\"ssh://user:secret@remote\"}}}]")]
    [InlineData("null")]
    public async Task DockerUnprovenContextNeverListsContainers(string context)
    {
        var commands = new ScriptedCommands();
        commands.Add("docker.exe", ["context", "inspect"], new(0, context, ""));
        var result = await new EndpointDiscovery(commands).DiscoverAsync(TargetPlatform.Docker);
        Assert.Empty(result.Endpoints);
        Assert.NotEmpty(result.Warnings);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("secret"));
        commands.AssertComplete();
    }

    [Fact]
    public async Task ContainersCombinesBothEnginesDeduplicatesAndAggregatesUnpublishedPortsByReadableName()
    {
        var commands = new ScriptedCommands();
        commands.Add("docker.exe", ["context", "inspect"], new(0, "[{\"Endpoints\":{\"docker\":{\"Host\":\"npipe:////./pipe/docker_engine\"}}}]", ""));
        commands.Add("docker.exe", ["--host", "npipe:////./pipe/docker_engine", "version", "--format", "json"], new(0, "{\"Server\":{\"Version\":\"27\"}}", ""));
        commands.Add("docker.exe", ["--host", "npipe:////./pipe/docker_engine", "ps", "--format", "{{json .}}"], new(0, "{\"ID\":\"docker-id\",\"Names\":\"web\"}\n", ""));
        commands.Add("docker.exe", ["--host", "npipe:////./pipe/docker_engine", "inspect", "--format", "{{json .NetworkSettings.Ports}}", "docker-id"], new(0, "{\"8080/tcp\":[{\"HostIp\":\"127.0.0.1\",\"HostPort\":\"18080\"}]}", ""));
        commands.Add("podman.exe", ["machine", "list", "--format", "json"], new(0, "[{\"Name\":\"podman-machine-default\",\"Running\":true,\"Port\":51048}]", ""));
        commands.Add("podman.exe", ["system", "connection", "list", "--format", "json"], new(0, "[{\"Name\":\"podman-machine-default\",\"URI\":\"ssh://user@127.0.0.1:51048/run/podman.sock\",\"IsMachine\":true,\"Default\":true}]", ""));
        commands.Add("podman.exe", ["--connection", "podman-machine-default", "ps", "--format", "{{json .}}"], new(0, "{\"ID\":\"podman-long-id-123456789\",\"Names\":[\"/api\"]}\n", ""));
        commands.Add("podman.exe", ["--connection", "podman-machine-default", "inspect", "--format", "{{json .NetworkSettings.Ports}}", "podman-long-id-123456789"], new(0, "{\"8080/tcp\":[{\"HostIp\":\"127.0.0.1\",\"HostPort\":\"18080\"}],\"9000/tcp\":null,\"9001/udp\":null}", ""));
        var result = await new EndpointDiscovery(commands).DiscoverAsync(TargetPlatform.Containers);
        Assert.Single(result.Endpoints);
        Assert.Equal(("127.0.0.1", 18080), (result.Endpoints[0].Host, result.Endpoints[0].Port));
        var warning = Assert.Single(result.Warnings, warning => warning.Contains("未发布端口", StringComparison.Ordinal));
        Assert.Contains("api", warning);
        Assert.DoesNotContain("podman-long-id-123456789", warning);
        commands.AssertComplete();
    }

    [Fact]
    public async Task ContainersSuppressesUnavailableEngineWhenTheOtherEngineSucceeds()
    {
        var commands = new ScriptedCommands();
        commands.Add("docker.exe", ["context", "inspect"], new(-1, "", "", "docker shim unavailable"));
        commands.Add("podman.exe", ["machine", "list", "--format", "json"], new(0, Machines, ""));
        commands.Add("podman.exe", ["system", "connection", "list", "--format", "json"], new(0, Connections, ""));
        commands.Add("podman.exe", ["--connection", "local", "ps", "--format", "{{json .}}"], new(0, "", ""));
        var result = await new EndpointDiscovery(commands).DiscoverAsync(TargetPlatform.Containers);
        Assert.Empty(result.Endpoints);
        Assert.Empty(result.Warnings);
        commands.AssertComplete();
    }

    private static ScriptedCommands PodmanProof(string machines = Machines, string connections = Connections)
    {
        var commands = new ScriptedCommands();
        commands.Add("podman.exe", ["machine", "list", "--format", "json"], new(0, machines, ""));
        commands.Add("podman.exe", ["system", "connection", "list", "--format", "json"], new(0, connections, ""));
        return commands;
    }
}
