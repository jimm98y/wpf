// What the runtime's sources (Runtime/) ask of Windows, off Windows: an object's security descriptor -- a file's,
// a registry key's, a mutex's -- read and written. There is no such descriptor on a Mac or a Linux machine (a file
// has its mode bits; a mutex nothing), so the descriptor an app sets is kept here, for the process, and is what it
// reads back; an object nobody has set one on has Windows' usual default (its owner, SYSTEM and the Administrators
// in full control, the Users reading). Nothing enforces it, as nothing enforces an ACL on a volume without them.

using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace System.Security.AccessControl
{
    internal static class Win32
    {
        private static readonly ConcurrentDictionary<string, byte[]> s_descriptors = new(StringComparer.OrdinalIgnoreCase);

        private static string Key(ResourceType type, string? name, SafeHandle? handle) =>
            name != null ? $"{(int)type}:{Normalize(type, name)}" : $"{(int)type}:#{handle!.DangerousGetHandle()}";

        private static string Normalize(ResourceType type, string name) =>
            type == ResourceType.FileObject ? Path.GetFullPath(name.Replace('\\', Path.DirectorySeparatorChar)) : name;

        internal static int ConvertSdToSddl(byte[] binaryForm, int requestedRevision, SecurityInfos si, out string resultSddl)
        {
            resultSddl = Sddl.Write(new RawSecurityDescriptor(binaryForm, 0), si);
            return Interop.Errors.ERROR_SUCCESS;
        }

        internal static int GetSecurityInfo(ResourceType resourceType, string? name, SafeHandle? handle, AccessControlSections accessControlSections, out RawSecurityDescriptor? resultSd)
        {
            resultSd = null;
            if (name != null && resourceType == ResourceType.FileObject)
            {
                string path = Normalize(resourceType, name);
                if (!File.Exists(path) && !Directory.Exists(path)) return Interop.Errors.ERROR_FILE_NOT_FOUND;
            }
            byte[] binary = s_descriptors.TryGetValue(Key(resourceType, name, handle), out byte[]? kept) ? kept : Default(resourceType);
            resultSd = new RawSecurityDescriptor(binary, 0);
            return Interop.Errors.ERROR_SUCCESS;
        }

        internal static int SetSecurityInfo(ResourceType type, string? name, SafeHandle? handle, SecurityInfos securityInformation,
            SecurityIdentifier? owner, SecurityIdentifier? group, GenericAcl? sacl, GenericAcl? dacl)
        {
            string key = Key(type, name, handle);
            var current = new RawSecurityDescriptor(s_descriptors.TryGetValue(key, out byte[]? kept) ? kept : Default(type), 0);
            if ((securityInformation & SecurityInfos.Owner) != 0) current.Owner = owner;
            if ((securityInformation & SecurityInfos.Group) != 0) current.Group = group;
            if ((securityInformation & SecurityInfos.SystemAcl) != 0)
            {
                current.SystemAcl = sacl == null ? null : ToRaw(sacl);
                current.SetFlags(sacl == null ? current.ControlFlags & ~ControlFlags.SystemAclPresent : current.ControlFlags | ControlFlags.SystemAclPresent);
            }
            if ((securityInformation & SecurityInfos.DiscretionaryAcl) != 0)
            {
                current.DiscretionaryAcl = dacl == null ? null : ToRaw(dacl);
                current.SetFlags(dacl == null ? current.ControlFlags & ~ControlFlags.DiscretionaryAclPresent : current.ControlFlags | ControlFlags.DiscretionaryAclPresent);
            }
            var binary = new byte[current.BinaryLength];
            current.GetBinaryForm(binary, 0);
            s_descriptors[key] = binary;
            return Interop.Errors.ERROR_SUCCESS;
        }

        private static RawAcl ToRaw(GenericAcl acl)
        {
            var binary = new byte[acl.BinaryLength];
            acl.GetBinaryForm(binary, 0);
            return new RawAcl(binary, 0);
        }

        // Windows' default: the owner (the user this process runs as), SYSTEM and the Administrators in full control,
        // the Users reading
        private static byte[] Default(ResourceType type)
        {
            SecurityIdentifier owner;
            try { owner = WindowsIdentity.GetCurrent().User ?? new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null); }
            catch (Exception) { owner = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null); }
            int all = type switch
            {
                ResourceType.FileObject => 0x1F01FF,
                ResourceType.RegistryKey or ResourceType.RegistryWow6432Key => 0xF003F,
                ResourceType.KernelObject => 0x1F0003,
                _ => 0x10000000,
            };
            int read = type switch
            {
                ResourceType.FileObject => 0x1200A9,
                ResourceType.RegistryKey or ResourceType.RegistryWow6432Key => 0x20019,
                ResourceType.KernelObject => 0x100000,
                _ => unchecked((int)0x80000000),
            };
            var dacl = new RawAcl(GenericAcl.AclRevision, 4);
            dacl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, all, owner, false, null));
            dacl.InsertAce(1, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, all, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), false, null));
            dacl.InsertAce(2, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, all, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), false, null));
            dacl.InsertAce(3, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, read, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), false, null));
            var sd = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.SelfRelative, owner,
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), null, dacl);
            var binary = new byte[sd.BinaryLength];
            sd.GetBinaryForm(binary, 0);
            return binary;
        }
    }

    /// <summary>Privileges a Windows process enables to read an audit list or take ownership: held here, always.</summary>
    internal sealed class Privilege
    {
        public const string TakeOwnership = "SeTakeOwnershipPrivilege";
        public const string Security = "SeSecurityPrivilege";
        public const string Restore = "SeRestorePrivilege";
        public Privilege(string privilegeName) => Name = privilegeName;
        public string Name { get; }
        public bool NeedToRevert => false;
        public void Enable() { }
        public void Revert() { }
    }

    internal static class NotImplemented
    {
        internal static Exception ByDesign => new NotImplementedException();
    }
}

internal static partial class Interop
{
    internal static class Errors
    {
        internal const int ERROR_SUCCESS = 0;
        internal const int ERROR_FILE_NOT_FOUND = 2;
        internal const int ERROR_PATH_NOT_FOUND = 3;
        internal const int ERROR_ACCESS_DENIED = 5;
        internal const int ERROR_INVALID_HANDLE = 6;
        internal const int ERROR_NOT_ENOUGH_MEMORY = 8;
        internal const int ERROR_INVALID_PARAMETER = 87;
        internal const int ERROR_INVALID_NAME = 123;
        internal const int ERROR_PIPE_NOT_CONNECTED = 233;
        internal const int ERROR_INVALID_OWNER = 1307;
        internal const int ERROR_INVALID_PRIMARY_GROUP = 1308;
        internal const int ERROR_INVALID_ACL = 1336;
        internal const int ERROR_INVALID_SID = 1337;
        internal const int ERROR_INVALID_SECURITY_DESCR = 1338;
        internal const int ERROR_NO_SECURITY_ON_OBJECT = 1350;
        internal const int ERROR_UNKNOWN_REVISION = 1305;
    }
}
