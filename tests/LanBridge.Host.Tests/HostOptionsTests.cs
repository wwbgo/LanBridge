using LanBridge.Control;
using LanBridge.Host;
using Xunit;

namespace LanBridge.Host.Tests;

public class HostOptionsTests
{
    [Fact]
    public void Console_supports_absolute_data_directory_and_current_sid()
    {
        string path = CoordinatorTests.TestDirectory();
        var options = HostOptions.Parse(["--console", "--data-dir", path]);
        Assert.False(options.Service);
        Assert.Equal(Path.GetFullPath(path), options.DataDirectory);
        Assert.Equal(PipeProtocol.CurrentOwnerSid, options.OwnerSid);
    }

    public static IEnumerable<object[]> InvalidArguments => new string[][]
    {
        [], ["--service"], ["--console", "--service"], ["--console", "--data-dir", "relative/path"],
        ["--console", "--data-dir", "C:/"], ["--console", "--shutdown"]
    }.Select(args => new object[] { args });

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void Invalid_arguments_are_rejected(string[] args) => Assert.Throws<ArgumentException>(() => HostOptions.Parse(args));

    [Fact]
    public void Log_rotation_is_bounded_and_strips_newline_injection()
    {
        string path = CoordinatorTests.TestDirectory();
        Directory.CreateDirectory(path);
        try
        {
            var log = new BoundedLog(Path.Combine(path, "host.log"));
            for (int i = 0; i < 2200; i++) log.Write(new string('x', 950));
            log.Write("safe\r\ninjected");
            Assert.Equal(2, Directory.GetFiles(path).Length);
            Assert.All(Directory.GetFiles(path), file => Assert.InRange(new FileInfo(file).Length, 1, BoundedLog.MaximumFileBytes));
            Assert.Contains("safe  injected", File.ReadAllText(Path.Combine(path, "host.log")));
        }
        finally { Directory.Delete(path, true); }
    }
}
