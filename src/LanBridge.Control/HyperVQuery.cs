using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LanBridge.Control;

namespace LanBridge.Control;

public static class HyperVQuery
{
    // Constant script, encoded to avoid shell quoting and injection. No request parameters reach PowerShell.
    private const string Script = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $ErrorActionPreference='Stop'; " +
        "try { Import-Module Hyper-V -ErrorAction Stop } catch { [Console]::Error.WriteLine('LB_HYPERV_MODULE_MISSING'); exit 2 }; " +
        "try { $v=@(Get-VM -ErrorAction Stop | ForEach-Object { " +
        "[pscustomobject]@{Id=$_.Id.ToString();Name=$_.Name;State=$_.State.ToString();" +
        "Addresses=@($_ | Get-VMNetworkAdapter -ErrorAction Stop | ForEach-Object { $_.IPAddresses } | Where-Object { $_ })} }); " +
        "ConvertTo-Json -InputObject $v -Depth 4 -Compress } catch { " +
        "$p=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()); " +
        "if ($_.Exception.Message -match 'permission|access.+denied|权限|拒绝访问' -or (!$p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -and !$p.IsInRole('S-1-5-32-578'))) { [Console]::Error.WriteLine('LB_HYPERV_PERMISSION') } else { [Console]::Error.WriteLine('LB_HYPERV_QUERY_FAILED') }; exit 3 }";

    public static async Task<List<VirtualMachineInfo>> GetAsync(CancellationToken ct = default)
    {
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 Hyper-V 查询。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            // Bounded reads run concurrently to avoid redirected stdout/stderr pipe deadlocks.
            var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var error = ReadBoundedAsync(process.StandardError, deadline.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
            if (process.ExitCode != 0)
                throw new InvalidOperationException(ExplainFailure(await error));
            var result = JsonSerializer.Deserialize<List<VirtualMachineInfo>>(await output, PipeProtocol.Json)
                ?? throw new InvalidDataException("Hyper-V 返回了空结果。");
            if (result.Count > 128) throw new InvalidDataException("Hyper-V 虚拟机列表超过 128 条限制；请手动填写 IP。");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Hyper-V 查询超时；仍可手动填写目标 IP。"); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    public static string ExplainFailure(string diagnostic) => diagnostic switch
    {
        var text when text.Contains("LB_HYPERV_MODULE_MISSING", StringComparison.Ordinal) =>
            "未安装或无法加载 Hyper-V PowerShell 模块。请先确认 Windows 已启用 Hyper-V 管理工具，或手动填写目标 IP。",
        var text when text.Contains("LB_HYPERV_PERMISSION", StringComparison.Ordinal) =>
            "当前桌面程序没有 Hyper-V 查询权限。请先从托盘退出 LanBridge，再以管理员身份打开；也可加入 Hyper-V 管理员组后重新登录。不会自动提权或更改用户组。",
        _ => "Hyper-V 查询失败。请检查 Hyper-V 虚拟机管理服务及当前账户权限，或手动填写目标 IP。"
    };

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[2048];
        var result = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            if (result.Length + count > 128 * 1024) throw new InvalidDataException("Hyper-V 输出超过安全上限。");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
