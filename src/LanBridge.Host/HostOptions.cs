using LanBridge.Control;

namespace LanBridge.Host;

public sealed record HostOptions(bool Service, string OwnerSid, string DataDirectory)
{
    public static HostOptions Parse(string[] args)
    {
        bool? service = null;
        string? sid = null, directory = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--console" when service is null: service = false; break;
                case "--service" when service is null: service = true; break;
                case "--owner-sid" when sid is null && i + 1 < args.Length: sid = args[++i]; break;
                case "--data-dir" when directory is null && i + 1 < args.Length: directory = args[++i]; break;
                default: throw new ArgumentException("参数无效。用法：LanBridge.Host.exe --console [--data-dir <绝对路径>] 或 --service --owner-sid <用户 SID>");
            }
        }
        if (service is null) throw new ArgumentException("必须显式选择 --console 或 --service。");
        if (service.Value && sid is null) throw new ArgumentException("服务模式必须指定 --owner-sid。");
        sid ??= PipeProtocol.CurrentOwnerSid;
        _ = PipeProtocol.PipeName(sid);
        if (!service.Value && sid != PipeProtocol.CurrentOwnerSid)
            throw new UnauthorizedAccessException("控制台模式只能使用当前用户 SID。");
        if (service.Value && directory is not null) throw new ArgumentException("服务模式不接受自定义数据目录；使用受保护的 ProgramData/LanBridge。");
        if (directory is not null && !Path.IsPathFullyQualified(directory)) throw new ArgumentException("--data-dir 必须是绝对路径。");
        directory ??= Path.Combine(Environment.GetFolderPath(service.Value ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData), "LanBridge");
        directory = Path.GetFullPath(directory);
        if (directory.TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetPathRoot(directory)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("不能把磁盘根目录作为数据目录。");
        return new(service.Value, sid, directory);
    }
}
