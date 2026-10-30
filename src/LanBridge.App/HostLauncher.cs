using System.Diagnostics;

namespace LanBridge.App;

internal static class HostLauncher
{
    internal static ProcessStartInfo CreateStartInfo(string baseDirectory) => new(Path.Combine(baseDirectory, "LanBridge.Host.exe"), "--console")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = baseDirectory
    };
}
