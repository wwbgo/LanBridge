using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LanBridge.Host;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = HostOptions.Parse(args);
            if (options.Service) ValidateServiceExecutable();
            using var lease = HostSecurity.AcquireLease(options.OwnerSid);
            HostSecurity.ProtectDirectory(options.DataDirectory, options.OwnerSid, options.Service);
            var log = new BoundedLog(Path.Combine(options.DataDirectory, "host.log"));
            log.Write("Host starting: " + (options.Service ? "Service" : "Console"));
            await using var coordinator = new BridgeCoordinator(options.DataDirectory, options.Service ? "Service" : "Console");
            await coordinator.InitializeAsync();
            using var shutdown = new CancellationTokenSource();
            var server = new PipeControlServer(options.OwnerSid, new(coordinator, shutdown.Cancel), log);
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = [], DisableDefaults = true, ContentRootPath = AppContext.BaseDirectory
            });
            if (options.Service) builder.Services.AddWindowsService(o => o.ServiceName = "LanBridge");
            builder.Services.AddSingleton(server);
            builder.Services.AddHostedService<PipeWorker>();
            using var host = builder.Build();
            await host.RunAsync(shutdown.Token);
            log.Write("Host stopped");
            return 0;
        }
        catch (Exception ex)
        {
            // Startup failures are visible in console/SCM. Never continue with an empty replacement config.
            Console.Error.WriteLine("LanBridge 后端启动失败：" + ex.Message);
            return 1;
        }
    }

    private static void ValidateServiceExecutable()
    {
        string programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(AppContext.BaseDirectory).StartsWith(programFiles, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("服务必须从受保护的 Program Files 目录运行；请使用 Install-Service.ps1。");
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("服务可执行目录不能包含符号链接或联接。");
    }

    private sealed class PipeWorker(PipeControlServer server) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => server.RunAsync(stoppingToken);
    }
}
