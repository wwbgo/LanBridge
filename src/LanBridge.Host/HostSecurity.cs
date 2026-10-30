using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using LanBridge.Control;

namespace LanBridge.Host;

public static class HostSecurity
{
    public static PipeSecurity CreatePipeSecurity(string ownerSid)
    {
        _ = PipeProtocol.PipeName(ownerSid);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(WindowsIdentity.GetCurrent().User!);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        foreach (var sid in AllowedSids(ownerSid))
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    public static IDisposable AcquireLease(string ownerSid, string? name = null)
    {
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in AllowedSids(ownerSid))
            security.AddAccessRule(new MutexAccessRule(sid, MutexRights.FullControl, AccessControlType.Allow));
        // Handle lifetime (not thread-affine mutex ownership) is the lease. An existing object is always rejected.
        // Global namespace excludes a service and a console process even across logon sessions.
        var mutex = MutexAcl.Create(false, name ?? @"Global\" + PipeProtocol.PipeName(ownerSid), out bool created, security);
        if (!created)
        {
            mutex.Dispose();
            throw new InvalidOperationException("该用户的 LanBridge 后端已在运行。");
        }
        return mutex;
    }

    public static void ProtectDirectory(string path, string ownerSid, bool service)
    {
        var directory = new DirectoryInfo(path);
        // Check the nearest existing ancestry before CreateDirectory can traverse a link.
        for (var current = directory; current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("数据目录不能通过符号链接或目录联接访问。");
        Directory.CreateDirectory(path);
        var entries = directory.EnumerateFileSystemInfos().ToList();
        if (entries.Any(e => (e.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new UnauthorizedAccessException("数据目录不能包含符号链接或目录联接。");
        if (service && entries.Any(e => e is DirectoryInfo))
            throw new UnauthorizedAccessException("服务数据目录不能包含子目录；请使用专用 ProgramData/LanBridge 目录。");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(WindowsIdentity.GetCurrent().User!);
        foreach (var sid in AllowedSids(ownerSid))
        {
            // Service data belongs to SYSTEM, not the unprivileged controller (including WRITE_DAC via ownership).
            var rights = service && sid.Value == ownerSid ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl;
            security.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        directory.SetAccessControl(security);
        foreach (var file in entries.OfType<FileInfo>().Where(f => service || f.Name is "rules.json" or "host.log" or "host.log.1" || f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
        {
            var fileSecurity = new FileSecurity();
            fileSecurity.SetAccessRuleProtection(true, false);
            fileSecurity.SetOwner(WindowsIdentity.GetCurrent().User!);
            foreach (var sid in AllowedSids(ownerSid))
                fileSecurity.AddAccessRule(new FileSystemAccessRule(sid,
                    service && sid.Value == ownerSid ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl, AccessControlType.Allow));
            file.SetAccessControl(fileSecurity);
        }
    }

    private static IEnumerable<SecurityIdentifier> AllowedSids(string ownerSid) =>
        new[] { new SecurityIdentifier(ownerSid), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.DistinctBy(s => s.Value);
}
