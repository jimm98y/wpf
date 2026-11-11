// advapi32's accounts and SIDs: names to SIDs and back (LookupAccountName/Sid), the SID helpers, and the local
// security policy's account rights (Lsa*) -- what a product's service installer calls to find a service account
// and grant it "log on as a service".
//
// The accounts are the ones the stack's Principal stand-in draws (NTAccount, SecurityIdentifier: the machine's
// users, root as SYSTEM, the service accounts as NETWORK SERVICE and LOCAL SERVICE), the same ones every access
// check here decides by. Rights granted are kept in the stack's registry (HKLM\SECURITY\Policy\Accounts\<SID>), so
// one program sees what another granted; off Windows nothing withholds a right from an account, so a grant changes
// nothing else -- the account can run services already.

using System.Security.Principal;
using Microsoft.Win32;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    private const int ERROR_NONE_MAPPED = 1332;
    private const int SidTypeUser = 1, SidTypeAlias = 4, SidTypeWellKnownGroup = 5;
    private const uint STATUS_SUCCESS = 0, STATUS_OBJECT_NAME_NOT_FOUND = 0xC0000034, STATUS_INVALID_PARAMETER = 0xC000000D;

    // ---- names and SIDs ----------------------------------------------------------------------------------------

    [Win32Export("LookupAccountNameW", "LookupAccountName")]
    public static int LookupAccountNameW(char* system, char* name, byte* sid, uint* sidSize, char* domain, uint* domainLength, int* use) =>
        LookupAccountNameCore(ReadUtf16(name), sid, sidSize, domain, domainLength, use);

    [Win32Export("LookupAccountNameA")]
    public static int LookupAccountNameA(byte* system, byte* name, byte* sid, uint* sidSize, byte* domain, uint* domainLength, int* use)
    {
        // the domain comes back in the ANSI code page, which off Windows is UTF-8: written through a wide buffer
        uint capacity = domainLength == null ? 0 : *domainLength;
        char* wide = stackalloc char[(int)Math.Min(capacity, 512u) + 1];
        int r = LookupAccountNameCore(ReadEither(name), sid, sidSize, capacity == 0 ? null : wide, domainLength, use);
        if (r != 0 && domain != null && capacity > 0) WriteUtf8(domain, (int)capacity, new string(wide));
        return r;
    }

    private static int LookupAccountNameCore(string? name, byte* sid, uint* sidSize, char* domain, uint* domainLength, int* use)
    {
        if (string.IsNullOrEmpty(name) || sidSize == null || domainLength == null) return Fail(ERROR_INVALID_PARAMETER);
        SecurityIdentifier? found;
        try { found = (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier)); }
        catch (Exception e) when (e is IdentityNotMappedException or ArgumentException or SystemException) { found = null; }
        if (found == null) return Fail(ERROR_NONE_MAPPED);

        string account = ((NTAccount)found.Translate(typeof(NTAccount))).Value;
        string domainName = account.Contains('\\') ? account[..account.IndexOf('\\')] : "";
        var bytes = new byte[found.BinaryLength];
        found.GetBinaryForm(bytes, 0);

        bool fits = *sidSize >= bytes.Length && *domainLength > domainName.Length && sid != null && domain != null;
        uint wantSid = (uint)bytes.Length, wantDomain = (uint)domainName.Length + 1;
        if (!fits)
        {
            *sidSize = wantSid;
            *domainLength = wantDomain;
            return Fail(ERROR_INSUFFICIENT_BUFFER);
        }
        bytes.CopyTo(new Span<byte>(sid, bytes.Length));
        *sidSize = wantSid;
        *domainLength = (uint)WriteUtf16(domain, (int)*domainLength, domainName);
        if (use != null) *use = SidUse(found);
        return TRUE;
    }

    [Win32Export("LookupAccountSidW", "LookupAccountSid")]
    public static int LookupAccountSidW(char* system, byte* sid, char* name, uint* nameLength, char* domain, uint* domainLength, int* use)
    {
        if (sid == null || nameLength == null || domainLength == null || !ValidSid(sid)) return Fail(ERROR_INVALID_PARAMETER);
        var id = new SecurityIdentifier((nint)sid);
        string account;
        try { account = ((NTAccount)id.Translate(typeof(NTAccount))).Value; }
        catch (Exception e) when (e is IdentityNotMappedException or SystemException) { return Fail(ERROR_NONE_MAPPED); }
        int slash = account.IndexOf('\\');
        string user = slash < 0 ? account : account[(slash + 1)..], domainName = slash < 0 ? "" : account[..slash];
        if (name == null || domain == null || *nameLength <= user.Length || *domainLength <= domainName.Length)
        {
            *nameLength = (uint)user.Length + 1;
            *domainLength = (uint)domainName.Length + 1;
            return Fail(ERROR_INSUFFICIENT_BUFFER);
        }
        *nameLength = (uint)WriteUtf16(name, (int)*nameLength, user);
        *domainLength = (uint)WriteUtf16(domain, (int)*domainLength, domainName);
        if (use != null) *use = SidUse(id);
        return TRUE;
    }

    // a well-known group (Everyone, NT AUTHORITY's), a built-in alias (BUILTIN's), or an account
    private static int SidUse(SecurityIdentifier sid)
    {
        string s = sid.Value;
        if (s.StartsWith("S-1-5-32-", StringComparison.Ordinal)) return SidTypeAlias;
        return s.StartsWith("S-1-5-21-", StringComparison.Ordinal) ? SidTypeUser : SidTypeWellKnownGroup;
    }

    // revision 1, at most 15 subauthorities (SID_MAX_SUB_AUTHORITIES)
    private static bool ValidSid(byte* sid) => sid != null && sid[0] == 1 && sid[1] <= 15;

    [Win32Export("IsValidSid")] public static int IsValidSid(byte* sid) => ValidSid(sid) ? TRUE : FALSE;

    [Win32Export("GetLengthSid")] public static uint GetLengthSid(byte* sid) => ValidSid(sid) ? (uint)(8 + 4 * sid[1]) : 0;

    [Win32Export("EqualSid")]
    public static int EqualSid(byte* a, byte* b) =>
        ValidSid(a) && ValidSid(b) && new ReadOnlySpan<byte>(a, 8 + 4 * a[1]).SequenceEqual(new ReadOnlySpan<byte>(b, 8 + 4 * b[1])) ? TRUE : FALSE;

    [Win32Export("FreeSid")] public static void* FreeSid(void* sid) { if (sid != null) LocalFree(sid); return null; }

    [Win32Export("ConvertSidToStringSidW", "ConvertSidToStringSid")]
    public static int ConvertSidToStringSidW(byte* sid, char** text)
    {
        if (!ValidSid(sid) || text == null) return Fail(ERROR_INVALID_PARAMETER);
        string s = new SecurityIdentifier((nint)sid).Value;
        char* buffer = (char*)LocalAlloc(0, (nuint)(s.Length + 1) * 2);
        WriteUtf16(buffer, s.Length + 1, s);
        *text = buffer;
        return TRUE;
    }

    [Win32Export("ConvertStringSidToSidW", "ConvertStringSidToSid")]
    public static int ConvertStringSidToSidW(char* text, byte** sid)
    {
        if (text == null || sid == null) return Fail(ERROR_INVALID_PARAMETER);
        SecurityIdentifier id;
        try { id = new SecurityIdentifier(new string(text)); }
        catch (ArgumentException) { return Fail(1337); }      // ERROR_INVALID_SID
        byte* buffer = (byte*)LocalAlloc(0, (nuint)id.BinaryLength);
        var bytes = new byte[id.BinaryLength];
        id.GetBinaryForm(bytes, 0);
        bytes.CopyTo(new Span<byte>(buffer, bytes.Length));
        *sid = buffer;
        return TRUE;
    }

    // ---- the local security policy's account rights -----------------------------------------------------------

    private sealed class LsaPolicy;

    private const string PolicyAccounts = @"SECURITY\Policy\Accounts";

    [Win32Export("LsaOpenPolicy")]
    public static uint LsaOpenPolicy(void* system, void* attributes, uint access, nint* policy)
    {
        if (policy == null) return STATUS_INVALID_PARAMETER;
        *policy = NewHandle(new LsaPolicy());
        return STATUS_SUCCESS;
    }

    [Win32Export("LsaClose")]
    public static uint LsaClose(nint policy) => Handle(policy) is LsaPolicy && CloseHandle(policy) == TRUE ? STATUS_SUCCESS : 0xC0000008;   // STATUS_INVALID_HANDLE

    [Win32Export("LsaAddAccountRights")]
    public static uint LsaAddAccountRights(nint policy, byte* sid, void* rights, uint count) => ChangeRights(policy, sid, rights, count, add: true, all: false);

    [Win32Export("LsaRemoveAccountRights")]
    public static uint LsaRemoveAccountRights(nint policy, byte* sid, byte allRights, void* rights, uint count) => ChangeRights(policy, sid, rights, count, add: false, all: allRights != 0);

    private static uint ChangeRights(nint policy, byte* sid, void* rights, uint count, bool add, bool all)
    {
        if (Handle(policy) is not LsaPolicy) return 0xC0000008;
        if (!ValidSid(sid)) return STATUS_INVALID_PARAMETER;
        string key = $@"{PolicyAccounts}\{new SecurityIdentifier((nint)sid).Value}";
        using RegistryKey account = Registry.LocalMachine.CreateSubKey(key);
        var held = new SortedSet<string>((account.GetValue("Rights") as string[]) ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (all) held.Clear();
        else foreach (string right in LsaStrings(rights, count)) { if (add) held.Add(right); else held.Remove(right); }
        account.SetValue("Rights", held.ToArray(), RegistryValueKind.MultiString);
        return STATUS_SUCCESS;
    }

    [Win32Export("LsaEnumerateAccountRights")]
    public static uint LsaEnumerateAccountRights(nint policy, byte* sid, void** rights, uint* count)
    {
        if (Handle(policy) is not LsaPolicy) return 0xC0000008;
        if (!ValidSid(sid) || rights == null || count == null) return STATUS_INVALID_PARAMETER;
        using RegistryKey? account = Registry.LocalMachine.OpenSubKey($@"{PolicyAccounts}\{new SecurityIdentifier((nint)sid).Value}");
        string[] held = (account?.GetValue("Rights") as string[]) ?? Array.Empty<string>();
        if (held.Length == 0) { *rights = null; *count = 0; return STATUS_OBJECT_NAME_NOT_FOUND; }
        // LSA_UNICODE_STRINGs, then their characters, in one block the caller frees with LsaFreeMemory
        int entry = IntPtr.Size == 8 ? 16 : 8;
        nuint size = (nuint)(held.Length * entry + held.Sum(r => (r.Length + 1) * 2));
        byte* block = (byte*)LocalAlloc(0x40, size);
        char* text = (char*)(block + held.Length * entry);
        for (int i = 0; i < held.Length; i++)
        {
            byte* u = block + i * entry;
            *(ushort*)u = (ushort)(held[i].Length * 2);
            *(ushort*)(u + 2) = (ushort)((held[i].Length + 1) * 2);
            *(char**)(u + (IntPtr.Size == 8 ? 8 : 4)) = text;
            text += WriteUtf16(text, held[i].Length + 1, held[i]) + 1;
        }
        *rights = block;
        *count = (uint)held.Length;
        return STATUS_SUCCESS;
    }

    [Win32Export("LsaFreeMemory")] public static uint LsaFreeMemory(void* buffer) { LocalFree(buffer); return STATUS_SUCCESS; }

    // LSA_UNICODE_STRING: Length (bytes), MaximumLength, then the buffer pointer, aligned
    private static IEnumerable<string> LsaStrings(void* array, uint count)
    {
        var result = new List<string>();
        int entry = IntPtr.Size == 8 ? 16 : 8;
        for (int i = 0; i < count && array != null; i++)
        {
            byte* u = (byte*)array + i * entry;
            ushort bytes = *(ushort*)u;
            char* chars = *(char**)(u + (IntPtr.Size == 8 ? 8 : 4));
            if (chars != null) result.Add(new string(chars, 0, bytes / 2));
        }
        return result;
    }

    /// <summary>An NTSTATUS as the Win32 error it stands for.</summary>
    [Win32Export("LsaNtStatusToWinError")]
    public static uint LsaNtStatusToWinError(uint status) => status switch
    {
        STATUS_SUCCESS => 0,
        0xC0000022 => 5,            // STATUS_ACCESS_DENIED
        0xC0000008 => 6,            // STATUS_INVALID_HANDLE
        STATUS_INVALID_PARAMETER => 87,
        0xC0000017 => 8,            // STATUS_NO_MEMORY
        STATUS_OBJECT_NAME_NOT_FOUND => 2,
        0xC0000073 => ERROR_NONE_MAPPED,   // STATUS_NONE_MAPPED
        0xC0000078 => 1337,         // STATUS_INVALID_SID
        _ => 317,                   // ERROR_MR_MID_NOT_FOUND: what Windows answers for an NTSTATUS it has no mapping for
    };
}
