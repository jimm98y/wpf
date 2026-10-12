// System.Security.AccessControl's registry types, which the real Microsoft.Win32.Registry assembly
// carries (RegistryKey's access-control overloads name them). Same shapes as .NET's: RegistryRights is
// the Windows access mask, and RegistrySecurity is an ordinary NativeObjectSecurity that holds rules.
// Nothing enforces them here; see RegistryKey.GetAccessControl.

using System.Security.Principal;

namespace System.Security.AccessControl
{
    [Flags]
    public enum RegistryRights
    {
        QueryValues = 0x0001,
        SetValue = 0x0002,
        CreateSubKey = 0x0004,
        EnumerateSubKeys = 0x0008,
        Notify = 0x0010,
        CreateLink = 0x0020,
        ExecuteKey = ReadKey,
        ReadKey = 0x20019,
        WriteKey = 0x20006,
        Delete = 0x10000,
        ReadPermissions = 0x20000,
        ChangePermissions = 0x40000,
        TakeOwnership = 0x80000,
        FullControl = 0xF003F,
    }

    public sealed class RegistryAccessRule : AccessRule
    {
        public RegistryAccessRule(IdentityReference identity, RegistryRights registryRights, AccessControlType type)
            : this(identity, (int)registryRights, false, InheritanceFlags.None, PropagationFlags.None, type) { }

        public RegistryAccessRule(string identity, RegistryRights registryRights, AccessControlType type)
            : this(new NTAccount(identity), (int)registryRights, false, InheritanceFlags.None, PropagationFlags.None, type) { }

        public RegistryAccessRule(IdentityReference identity, RegistryRights registryRights, InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AccessControlType type)
            : this(identity, (int)registryRights, false, inheritanceFlags, propagationFlags, type) { }

        public RegistryAccessRule(string identity, RegistryRights registryRights, InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AccessControlType type)
            : this(new NTAccount(identity), (int)registryRights, false, inheritanceFlags, propagationFlags, type) { }

        internal RegistryAccessRule(IdentityReference identity, int accessMask, bool isInherited, InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AccessControlType type)
            : base(identity, accessMask, isInherited, inheritanceFlags, propagationFlags, type) { }

        public RegistryRights RegistryRights => (RegistryRights)AccessMask;
    }

    public sealed class RegistryAuditRule : AuditRule
    {
        public RegistryAuditRule(IdentityReference identity, RegistryRights registryRights, InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AuditFlags flags)
            : this(identity, (int)registryRights, false, inheritanceFlags, propagationFlags, flags) { }

        public RegistryAuditRule(string identity, RegistryRights registryRights, InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AuditFlags flags)
            : this(new NTAccount(identity), (int)registryRights, false, inheritanceFlags, propagationFlags, flags) { }

        internal RegistryAuditRule(IdentityReference identity, int accessMask, bool isInherited, InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AuditFlags flags)
            : base(identity, accessMask, isInherited, inheritanceFlags, propagationFlags, flags) { }

        public RegistryRights RegistryRights => (RegistryRights)AccessMask;
    }

    public sealed class RegistrySecurity : NativeObjectSecurity
    {
        public RegistrySecurity() : base(true, ResourceType.RegistryKey) { }

        public override AccessRule AccessRuleFactory(IdentityReference identityReference, int accessMask, bool isInherited, InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AccessControlType type) =>
            new RegistryAccessRule(identityReference, accessMask, isInherited, inheritanceFlags, propagationFlags, type);

        public override AuditRule AuditRuleFactory(IdentityReference identityReference, int accessMask, bool isInherited, InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AuditFlags flags) =>
            new RegistryAuditRule(identityReference, accessMask, isInherited, inheritanceFlags, propagationFlags, flags);

        public void AddAccessRule(RegistryAccessRule rule) => base.AddAccessRule(rule);
        public void SetAccessRule(RegistryAccessRule rule) => base.SetAccessRule(rule);
        public void ResetAccessRule(RegistryAccessRule rule) => base.ResetAccessRule(rule);
        public bool RemoveAccessRule(RegistryAccessRule rule) => base.RemoveAccessRule(rule);
        public void RemoveAccessRuleAll(RegistryAccessRule rule) => base.RemoveAccessRuleAll(rule);
        public void RemoveAccessRuleSpecific(RegistryAccessRule rule) => base.RemoveAccessRuleSpecific(rule);
        public void AddAuditRule(RegistryAuditRule rule) => base.AddAuditRule(rule);
        public void SetAuditRule(RegistryAuditRule rule) => base.SetAuditRule(rule);
        public bool RemoveAuditRule(RegistryAuditRule rule) => base.RemoveAuditRule(rule);
        public void RemoveAuditRuleAll(RegistryAuditRule rule) => base.RemoveAuditRuleAll(rule);
        public void RemoveAuditRuleSpecific(RegistryAuditRule rule) => base.RemoveAuditRuleSpecific(rule);

        public override Type AccessRightType => typeof(RegistryRights);
        public override Type AccessRuleType => typeof(RegistryAccessRule);
        public override Type AuditRuleType => typeof(RegistryAuditRule);
    }
}
