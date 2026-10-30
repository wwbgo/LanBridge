using LanBridge.Discovery;
using Xunit;
namespace LanBridge.Discovery.Tests;
public sealed class WslLocaleTests
{
    [Theory]
    [InlineData("名称 状态 版本", "正在运行", "已停止")]
    [InlineData("NAME STATE VERSION", "Running", "Stopped")]
    [InlineData("NOM ÉTAT VERSION", "En cours d’exécution", "Arrêté")]
    public async Task OnlyRunningWsl2NamesAreQueriedRegardlessOfTranslatedState(string header, string running, string stopped)
    {
        var runner = new LocaleCommands(header, running, stopped);
        var result = await new EndpointDiscovery(runner).DiscoverAsync(TargetPlatform.Wsl);
        Assert.Equal(new[] { "172.20.1.2" }, result.Endpoints.Select(e => e.Host));
        Assert.Empty(result.Warnings);
        Assert.Equal(new[] { "Ubuntu Test" }, runner.Queried);
    }
    private sealed class LocaleCommands(string header, string running, string stopped) : ICommandRunner
    {
        public List<string> Queried { get; } = [];
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken ct)
        {
            if (command.Arguments.SequenceEqual(new[] { "--list", "--running", "--quiet" }))
                return Task.FromResult(new CommandResult(0, "Ubuntu Test\nFedora\npodman-machine-default\ndocker-desktop\n", ""));
            if (command.Arguments.SequenceEqual(new[] { "--list", "--verbose" }))
                return Task.FromResult(new CommandResult(0, $"{header}\n* Ubuntu Test {running} 2\n  Fedora {running} 1\n  Debian {stopped} 2\n  podman-machine-default {running} 2\n  docker-desktop {running} 2\n", ""));
            Assert.Equal("--distribution", command.Arguments[0]);
            Queried.Add(command.Arguments[1]);
            Assert.Equal("Ubuntu Test", command.Arguments[1]);
            return Task.FromResult(new CommandResult(0, """[{"addr_info":[{"local":"172.20.1.2"}]}]""", ""));
        }
    }
}
