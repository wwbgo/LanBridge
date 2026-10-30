namespace LanBridge.Discovery.Tests;
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--discover")
        {
            foreach (var platform in Enum.GetValues<TargetPlatform>())
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var result = await new EndpointDiscovery().DiscoverAsync(platform, timeout.Token);
                Console.WriteLine($"{platform}: endpoints={result.Endpoints.Count}");
                foreach (var endpoint in result.Endpoints) Console.WriteLine($"  {endpoint.Name} {endpoint.Host}:{endpoint.Port} {endpoint.Protocol}");
                foreach (var warning in result.Warnings) Console.WriteLine($"  WARNING: {warning}");
            }
            return 0;
        }
        if (args.Length > 0 && args[0] == "--echo") { Console.Out.Write(args[1]); Console.Error.Write("diagnostic"); }
        if (args.Length > 0 && args[0] == "--flood")
        {
            // Write a known byte count, independent of the runner's OEM console code page.
            var bytes = System.Text.Encoding.UTF8.GetBytes(new string('é', 600_000));
            using var output = args[1] == "stderr" ? Console.OpenStandardError() : Console.OpenStandardOutput();
            output.Write(bytes);
        }
        if (args.Length > 0 && args[0] == "--utf16") { var bytes = System.Text.Encoding.Unicode.GetBytes("Ubuntu 开发\r\n"); using var output = Console.OpenStandardOutput(); output.Write(bytes); }
        if (args.Length > 0 && args[0] == "--wait") { File.WriteAllText(args[1], Environment.ProcessId.ToString()); Thread.Sleep(30000); }
        return 0;
    }
}
