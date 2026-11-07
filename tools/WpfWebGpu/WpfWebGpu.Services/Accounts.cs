// The accounts of this machine as Windows' access checks see them, for what decides by a security descriptor here --
// http.sys's URL reservations, the service manager's services. The Principal stand-in (shims/System.Security.Principal.
// Windows) draws them the same way: root is SYSTEM, the machine's service accounts the installers make NETWORK SERVICE
// and LOCAL SERVICE, uid N the machine account S-1-5-21-<machine>-(N+500); a user's token carries Everyone, Users,
// Authenticated Users and Interactive -- and not Administrators, which an unelevated Windows token carries only to deny.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WpfWebGpu.Services;

public static partial class WindowsAccounts
{
    /// <summary>This process's account (root: 0).</summary>
    public static uint CurrentUid() => OperatingSystem.IsWindows() ? 0 : getuid();

    [LibraryImport("libc")]
    private static partial uint getuid();

    [LibraryImport("libc")]
    private static partial IntPtr getpwuid(uint uid);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr getpwnam(string name);

    // struct passwd starts with char *pw_name, char *pw_passwd, then uid_t pw_uid, on macOS and Linux alike
    public static string? UserName(uint uid)
    {
        if (OperatingSystem.IsWindows()) return null;
        IntPtr pw = getpwuid(uid);
        return pw == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(pw));
    }

    /// <summary>The uid of the account named <paramref name="name"/>; null where there is none.</summary>
    public static uint? Uid(string name)
    {
        if (OperatingSystem.IsWindows()) return null;
        IntPtr pw = getpwnam(name);
        return pw == IntPtr.Zero ? null : (uint)Marshal.ReadInt32(pw, 2 * IntPtr.Size);
    }

    /// <summary>The Windows service account a system account stands for: NETWORK SERVICE, LOCAL SERVICE.</summary>
    public static (string Sid, string Name)? ServiceAccount(string? user) => user switch
    {
        "_wpfnetworkservice" or "wpf-networkservice" => ("S-1-5-20", "NETWORK SERVICE"),
        "_wpflocalservice" or "wpf-localservice" => ("S-1-5-19", "LOCAL SERVICE"),
        _ => null,
    };

    /// <summary>The account as a Windows name, for messages.</summary>
    public static string Describe(uint uid) => uid == 0 ? "SYSTEM"
        : ServiceAccount(UserName(uid)) is { } service ? $"NT AUTHORITY\\{service.Name} (uid {uid})"
        : $"uid {uid} ({Sid(Domain(uid + 500))})";

    public static HashSet<string> TokenOf(uint uid) => TokenOf(uid, UserName(uid));

    /// <summary>The SIDs the account's token carries.</summary>
    public static HashSet<string> TokenOf(uint uid, string? user)
    {
        if (uid != 0 && ServiceAccount(user) is { } service)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                service.Sid, "S-1-1-0", "S-1-5-32-545", "S-1-5-6", "S-1-2-1", "S-1-5-11", "S-1-5-15", "S-1-2-0",
            };
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "S-1-1-0",                 // Everyone
            "S-1-5-32-545",            // BUILTIN\Users
            "S-1-5-11",                // Authenticated Users
            "S-1-5-4",                 // INTERACTIVE
            "S-1-2-0",                 // LOCAL
            Sid(Domain(513)),          // MACHINE\None
        };
        sids.Add(uid == 0 ? "S-1-5-18" : Sid(Domain(uid + 500)));
        if (uid == 0) sids.Add("S-1-5-32-544");
        return sids;
    }

    // the machine's account domain as the Principal stand-in draws it: a hash of the machine's name
    private static uint[] Domain(uint rid)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes("WpfWebGpu machine SID " + Environment.MachineName.ToUpperInvariant()));
        return new[] { 21u, BitConverter.ToUInt32(h, 0), BitConverter.ToUInt32(h, 4), BitConverter.ToUInt32(h, 8), rid };
    }

    private static string Sid(uint[] subs) => "S-1-5-" + string.Join('-', subs);

    /// <summary>A SID as binary, in a buffer, as text (S-1-5-...); null where it does not fit.</summary>
    public static string? BinarySid(byte[] b, int at)
    {
        if (at + 8 > b.Length) return null;
        int subs = b[at + 1];
        if (at + 8 + subs * 4 > b.Length) return null;
        long authority = 0;
        for (int i = 2; i < 8; i++) authority = (authority << 8) | b[at + i];
        var sb = new StringBuilder($"S-{b[at]}-{authority}");
        for (int i = 0; i < subs; i++) sb.Append('-').Append(BitConverter.ToUInt32(b, at + 8 + i * 4));
        return sb.ToString();
    }

    /// <summary>
    /// Windows' access check of a self-relative security descriptor's access list: the entries in order, an
    /// access-denied one naming a right still wanted refusing it, access-allowed ones granting rights until every
    /// right wanted is granted. A descriptor with no access list grants everything; an empty list nothing.
    /// </summary>
    public static bool AccessAllowed(byte[] sd, HashSet<string> token, uint desired)
    {
        if (sd.Length < 20) return false;
        if ((BitConverter.ToUInt16(sd, 2) & 0x4) == 0) return true;                         // no DACL: everyone
        int dacl = BitConverter.ToInt32(sd, 16);
        if (dacl == 0) return true;                                                          // a null DACL: everyone
        if (dacl + 8 > sd.Length) return false;
        int count = BitConverter.ToUInt16(sd, dacl + 4), at = dacl + 8;
        uint granted = 0;
        for (int i = 0; i < count && at + 8 <= sd.Length; i++)
        {
            byte type = sd[at];
            int size = BitConverter.ToUInt16(sd, at + 2);
            uint mask = BitConverter.ToUInt32(sd, at + 4);
            if ((type == 0 || type == 1) && BinarySid(sd, at + 8) is { } sid && token.Contains(sid))
            {
                if (type == 1 && (mask & desired & ~granted) != 0) return false;            // ACCESS_DENIED
                if (type == 0) granted |= mask;                                              // ACCESS_ALLOWED
                if ((granted & desired) == desired) return true;
            }
            at += Math.Max(size, 8);
        }
        return false;
    }
}
