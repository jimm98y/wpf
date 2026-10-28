extern alias PrincipalShim;

using PrincipalShim::System.Security.Principal;
using Xunit;

namespace WpfWebGpu.Tests;

/// <summary>The macOS System.Security.Principal.Windows: the Mac user as a local Windows account, and
/// Windows' well-known accounts as Windows names them.</summary>
public class PrincipalShimTests
{
    [Fact]
    public void The_current_user_is_a_local_account_of_this_machine()
    {
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        Assert.Equal(Environment.UserDomainName + "\\" + Environment.UserName, me.Name);
        Assert.True(me.IsAuthenticated);
        Assert.False(me.IsAnonymous);
        Assert.False(me.IsGuest);
        Assert.True(me.User!.IsAccountSid());
        Assert.StartsWith("S-1-5-21-", me.User.Value);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.WorldSid, null), me.Groups!);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), me.Groups!);
        // and translates to its name and back, as LookupAccountSid/LookupAccountName do
        Assert.Equal(me.Name, me.User.Translate(typeof(NTAccount)).Value);
        Assert.Equal(me.User, new NTAccount(me.Name).Translate(typeof(SecurityIdentifier)));
        Assert.Equal(me.User, new NTAccount(Environment.UserName).Translate(typeof(SecurityIdentifier)));
        Assert.Null(WindowsIdentity.GetCurrent(ifImpersonating: true));
    }

    [Fact]
    public void Well_known_accounts_have_their_Windows_names()
    {
        var ns = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
        Assert.Equal("S-1-5-20", ns.Value);
        Assert.Equal(@"NT AUTHORITY\NETWORK SERVICE", ns.Translate(typeof(NTAccount)).Value);
        Assert.Equal(@"BUILTIN\Administrators", new SecurityIdentifier("S-1-5-32-544").Translate(typeof(NTAccount)).Value);
        Assert.Equal("Everyone", new SecurityIdentifier("WD").Translate(typeof(NTAccount)).Value);
        Assert.Equal(new SecurityIdentifier("S-1-5-18"), new NTAccount(@"NT AUTHORITY\SYSTEM").Translate(typeof(SecurityIdentifier)));
        Assert.Equal(new SecurityIdentifier("S-1-5-32-545"), new NTAccount("Users").Translate(typeof(SecurityIdentifier)));
        Assert.True(ns.IsWellKnown(WellKnownSidType.NetworkServiceSid));
    }

    [Fact]
    public void A_name_nobody_has_is_not_mapped()
    {
        var nobody = new NTAccount(@"CONTOSO\nobody-at-all");
        Assert.Throws<IdentityNotMappedException>(() => nobody.Translate(typeof(SecurityIdentifier)));
        var refs = new IdentityReferenceCollection { nobody, new NTAccount("Everyone") };
        IdentityReferenceCollection soft = refs.Translate(typeof(SecurityIdentifier));
        Assert.Same(nobody, soft[0]);                                    // kept as it was
        Assert.Equal(new SecurityIdentifier("S-1-1-0"), soft[1]);
        var e = Assert.Throws<IdentityNotMappedException>(() => refs.Translate(typeof(SecurityIdentifier), forceSuccess: true));
        Assert.Equal(nobody, Assert.Single(e.UnmappedIdentities));
    }

    [Fact]
    public void A_sid_round_trips_through_its_binary_form()
    {
        var sid = new SecurityIdentifier("S-1-5-21-1004336348-1177238915-682003330-1001");
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        Assert.Equal(28, bytes.Length);
        Assert.Equal(sid, new SecurityIdentifier(bytes, 0));
        Assert.Equal(new SecurityIdentifier("S-1-5-21-1004336348-1177238915-682003330"), sid.AccountDomainSid);
        Assert.Equal(new SecurityIdentifier("S-1-5-21-1004336348-1177238915-682003330-500"),
                     new SecurityIdentifier(WellKnownSidType.AccountAdministratorSid, sid));
        Assert.Throws<ArgumentException>(() => new SecurityIdentifier("S-2-5"));
        Assert.Throws<ArgumentNullException>(() => new SecurityIdentifier(WellKnownSidType.AccountGuestSid, null));
    }

    [Fact]
    public void The_principal_answers_for_its_groups()
    {
        var p = new WindowsPrincipal(WindowsIdentity.GetCurrent());
        Assert.True(p.IsInRole(WindowsBuiltInRole.User));
        Assert.True(p.IsInRole(@"BUILTIN\Users"));
        Assert.False(p.IsInRole(WindowsBuiltInRole.Guest));
        Assert.Equal(System.Diagnostics.Process.GetCurrentProcess() is not null && IsMacAdmin(), p.IsInRole(WindowsBuiltInRole.Administrator));
        int ran = 0;
        WindowsIdentity.RunImpersonated(WindowsIdentity.GetCurrent().AccessToken, () => ran++);
        Assert.Equal(1, ran);
    }

    private static bool IsMacAdmin()
    {
        var id = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("id", "-G") { RedirectStandardOutput = true })!;
        string groups = id.StandardOutput.ReadToEnd();
        id.WaitForExit();
        return groups.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("80");
    }
}
