using System.Security.AccessControl;
using System.Security.Principal;
using LanBridge.Control;
using LanBridge.Host;
using Xunit;

namespace LanBridge.Host.Tests;

public class HostSecurityTests
{
    [Fact]
    public void Pipe_acl_has_only_owner_system_and_administrators_allow_entries()
    {
        string owner = PipeProtocol.CurrentOwnerSid;
        var security = HostSecurity.CreatePipeSecurity(owner);
        var allowed = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<System.IO.Pipes.PipeAccessRule>().Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => r.IdentityReference.Value).Order().ToArray();
        Assert.Equal(new[] { owner, "S-1-5-18", "S-1-5-32-544" }.Distinct().Order(), allowed);
        Assert.True(security.AreAccessRulesProtected);
    }

    [Fact]
    public void Data_protection_removes_explicit_untrusted_access_from_existing_configuration()
    {
        string path = CoordinatorTests.TestDirectory();
        Directory.CreateDirectory(path);
        string file = Path.Combine(path, "rules.json");
        File.WriteAllText(file, "[]");
        try
        {
            var permissions = new FileInfo(file).GetAccessControl();
            permissions.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(permissions);
            HostSecurity.ProtectDirectory(path, PipeProtocol.CurrentOwnerSid, service: false);
            var rules = new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
            Assert.DoesNotContain(rules, r => r.IdentityReference.Value == "S-1-1-0");
            Assert.True(new FileInfo(file).GetAccessControl().AreAccessRulesProtected);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void Lease_excludes_second_host_and_can_reacquire_after_disposal()
    {
        string name = "LanBridge.Test.Lease." + Guid.NewGuid().ToString("N");
        using (var first = HostSecurity.AcquireLease(PipeProtocol.CurrentOwnerSid, name))
            Assert.Throws<InvalidOperationException>(() => HostSecurity.AcquireLease(PipeProtocol.CurrentOwnerSid, name));
        using var restarted = HostSecurity.AcquireLease(PipeProtocol.CurrentOwnerSid, name);
    }
}
