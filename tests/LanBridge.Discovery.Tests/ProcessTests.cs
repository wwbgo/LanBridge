using System.Diagnostics;
using LanBridge.Discovery;
using Xunit;
namespace LanBridge.Discovery.Tests;
public sealed class ProcessTests
{
    [Fact]
    public async Task ProcessPreservesExactOutputAndSeparateArguments()
    {
        var result = await new ProcessCommandRunner().RunAsync(Child("--echo", "space and \"quotes\" & ;"), default);
        Assert.Null(result.Error);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("space and \"quotes\" & ;", result.Stdout);
        Assert.Equal("diagnostic", result.Stderr);
    }
    [Fact]
    public async Task ProcessDoesNotPadShortOutputWithNulls()
    {
        var result = await new ProcessCommandRunner().RunAsync(Child("--echo", "x"), default);
        Assert.Equal("x", result.Stdout);
        Assert.Equal("diagnostic", result.Stderr);
    }
    [Fact]
    public async Task ProcessPreservesMultiBufferOutputExactly()
    {
        var expected = new string('x', 8192) + "tail";
        var result = await new ProcessCommandRunner().RunAsync(Child("--echo", expected), default);
        Assert.Equal(expected, result.Stdout);
    }
    [Theory]
    [InlineData("stdout")]
    [InlineData("stderr")]
    public async Task ProcessRejectsEitherStreamOverOneMiBOfBytes(string stream)
    {
        var result = await new ProcessCommandRunner().RunAsync(Child("--flood", stream), default);
        Assert.NotNull(result.Error);
        Assert.Contains("limit", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Stdout);
        Assert.Empty(result.Stderr);
    }
    [Fact]
    public async Task PreCancelledCommandNeverStarts()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessCommandRunner().RunAsync(new("nonexistent-lanbridge-command.exe", [], TimeSpan.FromSeconds(3)), cts.Token));
    }
    [Fact]
    public async Task ProcessDecodesRedirectedUtf16WithoutCorruptingDistroNames()
    {
        var result = await new ProcessCommandRunner().RunAsync(Child("--utf16"), default);
        Assert.Equal("Ubuntu 开发\r\n", result.Stdout);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutOrCancellationKillsOwnedProcess(bool cancel)
    {
        var marker = Path.Combine(AppContext.BaseDirectory, $"process-{Guid.NewGuid():N}.pid");
        using var cts = new CancellationTokenSource();
        try
        {
            var command = Child("--wait", marker) with { Timeout = TimeSpan.FromSeconds(2) };
            var task = new ProcessCommandRunner().RunAsync(command, cts.Token);
            for (var i = 0; i < 100 && !File.Exists(marker); i++) await Task.Delay(10);
            Assert.True(File.Exists(marker), "Child did not start in time");
            var pid = int.Parse(File.ReadAllText(marker));
            if (cancel)
            {
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            }
            else Assert.Contains("timed out", (await task).Error!);
            try
            {
                using var child = Process.GetProcessById(pid);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                Assert.True(child.HasExited);
            }
            catch (ArgumentException) { /* Already reaped. */ }
        }
        finally
        {
            if (File.Exists(marker))
            {
                try { using var child = Process.GetProcessById(int.Parse(File.ReadAllText(marker))); if (!child.HasExited) child.Kill(true); }
                catch (ArgumentException) { }
                File.Delete(marker);
            }
        }
    }
    internal static CommandSpec Child(params string[] args) => new("dotnet.exe", new[] { typeof(ProcessTests).Assembly.Location }.Concat(args).ToArray(), TimeSpan.FromSeconds(3));
}
