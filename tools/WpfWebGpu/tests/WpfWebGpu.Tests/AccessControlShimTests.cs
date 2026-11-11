extern alias AclShim;
extern alias ThreadingAclShim;
extern alias PrincipalShim;

using AclShim::System.Security.AccessControl;
using PrincipalShim::System.Security.Principal;
using Xunit;
using MutexAcl = ThreadingAclShim::System.Threading.MutexAcl;
using MutexSecurity = ThreadingAclShim::System.Security.AccessControl.MutexSecurity;
using MutexAccessRule = ThreadingAclShim::System.Security.AccessControl.MutexAccessRule;
using MutexRights = ThreadingAclShim::System.Security.AccessControl.MutexRights;
using ThreadingAclExtensions = ThreadingAclShim::System.Threading.ThreadingAclExtensions;

namespace WpfWebGpu.Tests;

// The access control stand-ins off Windows (shims/System.Security.AccessControl, System.Threading.AccessControl).
public class AccessControlShimTests
{
    [Fact]
    public void A_global_mutex_made_with_an_access_list_keeps_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "on Windows the framework's own");
        // System.Threading.AccessControl is built against the framework's access control types (as on Windows)
        var everyone = new global::System.Security.Principal.SecurityIdentifier(global::System.Security.Principal.WellKnownSidType.WorldSid, null);
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(everyone, MutexRights.FullControl, global::System.Security.AccessControl.AccessControlType.Allow));
        string name = "Global\\wpfwebgpu-test-" + Guid.NewGuid().ToString("N");
        using var mutex = MutexAcl.Create(false, name, out bool createdNew, security);
        Assert.True(createdNew);
        var rules = ThreadingAclExtensions.GetAccessControl(mutex).GetAccessRules(true, false, typeof(global::System.Security.Principal.SecurityIdentifier));
        Assert.Contains(rules.Cast<MutexAccessRule>(), r => r.IdentityReference.Equals(everyone) && r.MutexRights == MutexRights.FullControl);
        using var again = MutexAcl.OpenExisting(name, MutexRights.Synchronize);
        Assert.True(again.WaitOne(0));
        again.ReleaseMutex();
    }

    [Fact]
    public void A_descriptor_reads_from_and_writes_back_to_SDDL()
    {
        var sd = new RawSecurityDescriptor("O:BAG:SYD:P(A;OICI;FA;;;SY)(A;;0x1200a9;;;BU)(D;;GW;;;WD)");
        Assert.Equal("S-1-5-32-544", sd.Owner!.Value);
        Assert.Equal(3, sd.DiscretionaryAcl!.Count);
        Assert.True((sd.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0);
        string written = sd.GetSddlForm(AccessControlSections.All);
        Assert.Equal("O:BAG:SYD:P(A;CIOI;0x1f01ff;;;SY)(A;;0x1200a9;;;BU)(D;;0x40000000;;;WD)", written);
        Assert.Equal(written, new RawSecurityDescriptor(written).GetSddlForm(AccessControlSections.All));
    }
}
