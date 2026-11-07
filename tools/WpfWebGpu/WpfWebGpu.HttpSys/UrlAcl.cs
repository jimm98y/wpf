// Who may register a URL prefix, as http.sys decides it: an administrator (elevated: here root) any; anyone else only
// under a URL reservation -- `netsh http add urlacl`, kept in HKLM\SYSTEM\CurrentControlSet\Services\HTTP\
// Parameters\UrlAclInfo, a value per reserved URL holding its security descriptor (binary, as netsh writes it, or
// SDDL) -- whose access list grants the account GENERIC_EXECUTE ("listen"). The reservation that decides is the
// most specific one covering the prefix: the same scheme and port, a host it covers (+ every host, * the weak
// wildcard, a name itself) and a path the prefix's path lies under. Windows' own default reservation
// (http://+:80/Temporary_Listen_Addresses/, everyone) is there as on every Windows machine.
//
// Accounts are the Principal stand-in's (shims/System.Security.Principal.Windows): root is SYSTEM, the machine's
// service accounts the installers make NETWORK SERVICE and LOCAL SERVICE, uid N the machine account
// S-1-5-21-<machine>-(N+500); a user's token carries Everyone, Users, Authenticated Users and Interactive -- and not
// Administrators, which an unelevated Windows token carries only to deny.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WpfWebGpu.HttpSys;

public static partial class UrlAcl
{
    private const string Key = @"SYSTEM\CurrentControlSet\Services\HTTP\Parameters\UrlAclInfo";
    private const uint GENERIC_EXECUTE = 0x20000000, GENERIC_ALL = 0x10000000;
    private static readonly (string Url, string Sddl)[] s_windowsDefaults = { ("http://+:80/Temporary_Listen_Addresses/", "D:(A;;GX;;;WD)") };

    /// <summary>Whether the account <paramref name="uid"/> may register <paramref name="prefix"/>; why not, where not.</summary>
    public static bool MayRegister(uint uid, SharedPrefix prefix, out string why) => MayRegister(uid, UserName(uid), prefix, out why);

    /// <summary>As <see cref="MayRegister(uint, SharedPrefix, out string)"/>, the account's name given.</summary>
    public static bool MayRegister(uint uid, string? user, SharedPrefix prefix, out string why)
    {
        why = "";
        if (uid == 0) return true;                                  // an elevated administrator
        (SharedPrefix Url, byte[]? Descriptor, string? Sddl)? best = null;
        foreach (var r in Reservations())
        {
            if (!Covers(r.Url, prefix)) continue;
            if (best == null || Specificity(r.Url) > Specificity(best.Value.Url)) best = r;
        }
        if (best == null)
        {
            why = $"no URL reservation covers {prefix.FullPrefix} (netsh http add urlacl: {Key})";
            return false;
        }
        var sids = TokenOf(uid, user);
        bool allowed = best.Value.Descriptor != null ? Allows(best.Value.Descriptor, sids) : AllowsSddl(best.Value.Sddl!, sids);
        if (!allowed) why = $"the URL reservation {best.Value.Url.FullPrefix} does not let {Account(uid)} listen";
        return allowed;
    }

    /// <summary>This process's account (root: 0).</summary>
    public static uint CurrentUid() => WpfWebGpu.Services.WindowsAccounts.CurrentUid();

    /// <summary>The owner of a file: who registered it.</summary>
    public static uint? OwnerOf(string path)
    {
        if (OperatingSystem.IsWindows()) return 0;
        // .NET's own stat (System.Native's FileStatus: Flags, Mode, Uid, ...), the same on every system it runs on
        Span<byte> status = stackalloc byte[256];
        unsafe
        {
            fixed (byte* p = status)
                if (SystemNative_LStat(path, p) != 0) return null;
        }
        return BitConverter.ToUInt32(status[8..12]);
    }

    private static string? UserName(uint uid) => WpfWebGpu.Services.WindowsAccounts.UserName(uid);

    [LibraryImport("libSystem.Native", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int SystemNative_LStat(string path, byte* status);

    // ---- the reservations -------------------------------------------------------------------------------------

    private static IEnumerable<(SharedPrefix Url, byte[]? Descriptor, string? Sddl)> Reservations()
    {
        foreach (var (url, sddl) in s_windowsDefaults)
            if (SharedPrefix.Parse(url) is { } d) yield return (d, null, sddl);
        Microsoft.Win32.RegistryKey? key = null;
        try { key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Key); }
        catch (Exception) { }
        if (key == null) yield break;
        using (key)
        {
            foreach (string name in key.GetValueNames())
            {
                if (Normalized(name) is not { } url) continue;
                object? v = null;
                try { v = key.GetValue(name); } catch (Exception) { }
                if (v is byte[] binary) yield return (url, binary, null);
                else if (v is string text && text.Length > 0) yield return (url, null, text);
            }
        }
    }

    // a reservation's URL as netsh takes it: the port explicit, the path ending in '/'
    private static SharedPrefix? Normalized(string url)
    {
        if (SharedPrefix.Parse(url) is { } p) return p;
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return null;
        int path = url.IndexOf('/', scheme + 3);
        string host = path < 0 ? url[(scheme + 3)..] : url[(scheme + 3)..path];
        string port = url[..scheme].Equals("https", StringComparison.OrdinalIgnoreCase) ? "443" : "80";
        return SharedPrefix.Parse($"{url[..scheme]}://{host}:{port}{(path < 0 ? "/" : url[path..])}");
    }

    private static bool Covers(SharedPrefix reservation, SharedPrefix prefix)
    {
        if (reservation.Https != prefix.Https || reservation.Port != prefix.Port) return false;
        bool host = reservation.Host == "+" || string.Equals(reservation.Host, prefix.Host, StringComparison.OrdinalIgnoreCase)
            || (reservation.Host == "*" && prefix.Host != "+");
        return host && prefix.Path.StartsWith(reservation.Path, StringComparison.OrdinalIgnoreCase);
    }

    // a named host's reservation before a wildcard's; then the longer path
    private static int Specificity(SharedPrefix r) => (r.Host is "+" or "*" ? 0 : 1 << 20) + r.Path.Length;

    // ---- the account's token ----------------------------------------------------------------------------------

    private static string Account(uint uid) => WpfWebGpu.Services.WindowsAccounts.Describe(uid);

    /// <summary>The Windows service account a system account stands for: NETWORK SERVICE, LOCAL SERVICE.</summary>
    public static (string Sid, string Name)? ServiceAccount(string? user) => WpfWebGpu.Services.WindowsAccounts.ServiceAccount(user);

    internal static HashSet<string> TokenOf(uint uid) => WpfWebGpu.Services.WindowsAccounts.TokenOf(uid);

    internal static HashSet<string> TokenOf(uint uid, string? user) => WpfWebGpu.Services.WindowsAccounts.TokenOf(uid, user);

    // ---- the descriptor's access list: its access-allowed and -denied entries, in order ------------------------

    private static bool Allows(byte[] sd, HashSet<string> token)
    {
        if (sd.Length < 20 || (BitConverter.ToUInt16(sd, 2) & 0x4) == 0) return false;      // SE_DACL_PRESENT
        int dacl = BitConverter.ToInt32(sd, 16);
        if (dacl == 0) return true;                                                          // a null DACL: everyone
        if (dacl + 8 > sd.Length) return false;
        int count = BitConverter.ToUInt16(sd, dacl + 4), at = dacl + 8;
        for (int i = 0; i < count && at + 8 <= sd.Length; i++)
        {
            byte type = sd[at];
            int size = BitConverter.ToUInt16(sd, at + 2);
            uint mask = BitConverter.ToUInt32(sd, at + 4);
            if ((type == 0 || type == 1) && BinarySid(sd, at + 8) is { } sid && token.Contains(sid) && (mask & (GENERIC_EXECUTE | GENERIC_ALL)) != 0)
                return type == 0;                                                            // ACCESS_ALLOWED / _DENIED
            at += Math.Max(size, 8);
        }
        return false;
    }

    private static string? BinarySid(byte[] b, int at) => WpfWebGpu.Services.WindowsAccounts.BinarySid(b, at);

    private static readonly Dictionary<string, string> s_aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WD"] = "S-1-1-0", ["BU"] = "S-1-5-32-545", ["BA"] = "S-1-5-32-544", ["AU"] = "S-1-5-11", ["SY"] = "S-1-5-18",
        ["IU"] = "S-1-5-4", ["NS"] = "S-1-5-20", ["LS"] = "S-1-5-19",
    };

    // D:(A;;GX;;;WD)(D;;GA;;;S-1-5-21-...): the access list's entries, in order
    private static bool AllowsSddl(string sddl, HashSet<string> token)
    {
        int d = sddl.IndexOf("D:", StringComparison.OrdinalIgnoreCase);
        if (d < 0) return false;
        foreach (string ace in sddl[(d + 2)..].Split('(', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] f = ace.TrimEnd(')').Split(';');
            if (f.Length < 6 || (f[0] != "A" && f[0] != "D")) continue;
            string sid = s_aliases.TryGetValue(f[5], out var known) ? known : f[5];
            if (!token.Contains(sid)) continue;
            string rights = f[2].ToUpperInvariant();
            bool listen = rights.Contains("GX") || rights.Contains("GA")
                || (rights.StartsWith("0X") && uint.TryParse(rights[2..], System.Globalization.NumberStyles.HexNumber, null, out uint m) && (m & (GENERIC_EXECUTE | GENERIC_ALL)) != 0);
            if (listen) return f[0] == "A";
        }
        return false;
    }
}
