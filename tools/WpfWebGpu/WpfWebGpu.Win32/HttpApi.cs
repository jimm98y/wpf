// httpapi's configuration of http.sys (HttpSetServiceConfiguration and its kin): the certificates HTTPS is served with, as
// netsh http add sslcert and IIS (Microsoft.Web.Administration's bindings) set them. Kept where Windows keeps them -- the
// registry's HTTP\Parameters\SslBindingInfo (by address and port) and SslSniBindingInfo (by host name and port), with
// netsh's values -- which is where the stack's http.sys reads the certificate a port is served with (WpfWebGpu.HttpSys's
// Certificates). The address is a sockaddr as the caller made it: Windows' layout, or this system's (a .NET program's
// IPEndPoint.Serialize() gives BSD's on macOS: a length byte before the family).

using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    private const int HttpServiceConfigSSLCertInfo = 1, HttpServiceConfigSslSniCertInfo = 5;
    private const int HttpServiceConfigQueryExact = 0, HttpServiceConfigQueryNext = 1;
    private const int ERROR_NO_MORE_ITEMS = 259, ERROR_INSUFFICIENT_BUFFER_HTTP = 122;
    private const string HttpParameters = @"SYSTEM\CurrentControlSet\Services\HTTP\Parameters\";

    [StructLayout(LayoutKind.Sequential)]
    public struct HTTPAPI_VERSION { public ushort Major, Minor; }

    // HTTP_SERVICE_CONFIG_SSL_PARAM
    [StructLayout(LayoutKind.Sequential)]
    private struct SslParam
    {
        public uint SslHashLength;
        public byte* pSslHash;
        public Guid AppId;
        public char* pSslCertStoreName;
        public uint DefaultCertCheckMode, DefaultRevocationFreshnessTime, DefaultRevocationUrlRetrievalTimeout;
        public char* pDefaultSslCtlIdentifier, pDefaultSslCtlStoreName;
        public uint DefaultFlags;
    }

    [StructLayout(LayoutKind.Sequential)] private struct SslSet { public byte* pIpPort; public SslParam Param; }
    [StructLayout(LayoutKind.Sequential)] private struct SslQuery { public int QueryDesc; public byte* pIpPort; public uint dwToken; }
    [StructLayout(LayoutKind.Sequential)] private struct SniKey { public fixed byte IpPort[128]; public char* Host; }
    [StructLayout(LayoutKind.Sequential)] private struct SniSet { public SniKey Key; public SslParam Param; }
    [StructLayout(LayoutKind.Sequential)] private struct SniQuery { public int QueryDesc; public SniKey Key; public uint dwToken; }

    [Win32Export("HttpInitialize")]
    public static uint HttpInitialize(HTTPAPI_VERSION version, uint flags, void* reserved) => 0;

    [Win32Export("HttpTerminate")]
    public static uint HttpTerminate(uint flags, void* reserved) => 0;

    [Win32Export("HttpSetServiceConfiguration")]
    public static uint HttpSetServiceConfiguration(nint serviceHandle, int configId, void* input, uint inputLength, void* overlapped)
    {
        if (input == null) return ERROR_INVALID_PARAMETER;
        string? key;
        SslParam* param;
        switch (configId)
        {
            case HttpServiceConfigSSLCertInfo when inputLength >= sizeof(SslSet):
                key = SslKeyName(((SslSet*)input)->pIpPort); param = &((SslSet*)input)->Param; break;
            case HttpServiceConfigSslSniCertInfo when inputLength >= sizeof(SniSet):
                key = SniKeyName(&((SniSet*)input)->Key); param = &((SniSet*)input)->Param; break;
            default: return ERROR_INVALID_PARAMETER;
        }
        if (key == null) return ERROR_INVALID_PARAMETER;
        string path = HttpParameters + key;
        using (var existing = Registry.LocalMachine.OpenSubKey(path))
            if (existing != null) return ERROR_ALREADY_EXISTS_HTTP;
        using var k = Registry.LocalMachine.CreateSubKey(path);
        if (param->pSslHash != null && param->SslHashLength > 0)
            k.SetValue("SslCertHash", new ReadOnlySpan<byte>(param->pSslHash, (int)param->SslHashLength).ToArray(), RegistryValueKind.Binary);
        k.SetValue("AppId", param->AppId.ToByteArray(), RegistryValueKind.Binary);
        if (param->pSslCertStoreName != null) k.SetValue("SslCertStoreName", new string(param->pSslCertStoreName));
        k.SetValue("DefaultSslCertCheckMode", (int)param->DefaultCertCheckMode, RegistryValueKind.DWord);
        k.SetValue("DefaultSslRevocationFreshnessTime", (int)param->DefaultRevocationFreshnessTime, RegistryValueKind.DWord);
        k.SetValue("DefaultSslRevocationUrlRetrievalTimeout", (int)param->DefaultRevocationUrlRetrievalTimeout, RegistryValueKind.DWord);
        if (param->pDefaultSslCtlIdentifier != null) k.SetValue("DefaultSslCtlIdentifier", new string(param->pDefaultSslCtlIdentifier));
        if (param->pDefaultSslCtlStoreName != null) k.SetValue("DefaultSslCtlStoreName", new string(param->pDefaultSslCtlStoreName));
        k.SetValue("DefaultFlags", (int)param->DefaultFlags, RegistryValueKind.DWord);
        return 0;
    }

    private const int ERROR_ALREADY_EXISTS_HTTP = 183;

    [Win32Export("HttpDeleteServiceConfiguration")]
    public static uint HttpDeleteServiceConfiguration(nint serviceHandle, int configId, void* input, uint inputLength, void* overlapped)
    {
        if (input == null) return ERROR_INVALID_PARAMETER;
        string? key = configId switch
        {
            HttpServiceConfigSSLCertInfo when inputLength >= sizeof(byte*) => SslKeyName(((SslSet*)input)->pIpPort),
            HttpServiceConfigSslSniCertInfo when inputLength >= sizeof(SniKey) => SniKeyName(&((SniSet*)input)->Key),
            _ => null,
        };
        if (key == null) return ERROR_INVALID_PARAMETER;
        using (var existing = Registry.LocalMachine.OpenSubKey(HttpParameters + key))
            if (existing == null) return ERROR_FILE_NOT_FOUND;
        Registry.LocalMachine.DeleteSubKeyTree(HttpParameters + key, false);
        return 0;
    }

    [Win32Export("HttpQueryServiceConfiguration")]
    public static uint HttpQueryServiceConfiguration(nint serviceHandle, int configId, void* input, uint inputLength, void* output, uint outputLength, uint* returnLength, void* overlapped)
    {
        if (input == null) return ERROR_INVALID_PARAMETER;
        string? key;
        string table = configId == HttpServiceConfigSslSniCertInfo ? "SslSniBindingInfo" : "SslBindingInfo";
        switch (configId)
        {
            case HttpServiceConfigSSLCertInfo when inputLength >= sizeof(SslQuery):
            {
                var q = (SslQuery*)input;
                key = q->QueryDesc == HttpServiceConfigQueryExact ? SslKeyName(q->pIpPort)
                    : q->QueryDesc == HttpServiceConfigQueryNext ? NthKey(table, q->dwToken) : null;
                break;
            }
            case HttpServiceConfigSslSniCertInfo when inputLength >= sizeof(SniQuery):
            {
                var q = (SniQuery*)input;
                key = q->QueryDesc == HttpServiceConfigQueryExact ? SniKeyName(&q->Key)
                    : q->QueryDesc == HttpServiceConfigQueryNext ? NthKey(table, q->dwToken) : null;
                if (key == null && q->QueryDesc == HttpServiceConfigQueryNext) return ERROR_NO_MORE_ITEMS;
                break;
            }
            default: return ERROR_INVALID_PARAMETER;
        }
        if (key == null) return configId == HttpServiceConfigSSLCertInfo && ((SslQuery*)input)->QueryDesc == HttpServiceConfigQueryNext ? (uint)ERROR_NO_MORE_ITEMS : (uint)ERROR_INVALID_PARAMETER;
        using var k = Registry.LocalMachine.OpenSubKey(HttpParameters + key);
        if (k == null) return ERROR_FILE_NOT_FOUND;

        // the set structure, then what its pointers point to, in the caller's buffer
        string name = key[(table.Length + 1)..];
        byte[] hash = k.GetValue("SslCertHash") as byte[] ?? Array.Empty<byte>();
        string? store = k.GetValue("SslCertStoreName") as string;
        string? ctlId = k.GetValue("DefaultSslCtlIdentifier") as string, ctlStore = k.GetValue("DefaultSslCtlStoreName") as string;
        bool sni = configId == HttpServiceConfigSslSniCertInfo;
        byte[] address = sni ? Array.Empty<byte>() : SockAddr(name, sni: false);
        string? host = sni ? name[..name.LastIndexOf(':')] : null;
        int head = sni ? sizeof(SniSet) : sizeof(SslSet);
        int Str(string? s) => s == null ? 0 : (s.Length + 1) * 2;
        int need = head + (sni ? 0 : address.Length) + hash.Length + Str(store) + Str(ctlId) + Str(ctlStore) + Str(host) + 16;
        if (returnLength != null) *returnLength = (uint)need;
        if (output == null || outputLength < need) return ERROR_INSUFFICIENT_BUFFER_HTTP;

        new Span<byte>(output, need).Clear();
        byte* next = (byte*)output + head;
        byte* Put(ReadOnlySpan<byte> data) { byte* at = next; data.CopyTo(new Span<byte>(next, data.Length)); next += (data.Length + 7) & ~7; return at; }
        char* PutStr(string? s) => s == null ? null : (char*)Put(MemoryMarshal.AsBytes((s + "\0").AsSpan()));
        SslParam* param;
        if (sni)
        {
            var set = (SniSet*)output;
            int port = int.Parse(name[(name.LastIndexOf(':') + 1)..]);
            byte[] any = SockAddr("0.0.0.0", false, port);
            any.AsSpan().CopyTo(new Span<byte>(set->Key.IpPort, 128));
            set->Key.Host = PutStr(host);
            param = &set->Param;
        }
        else
        {
            var set = (SslSet*)output;
            set->pIpPort = Put(address);
            param = &set->Param;
        }
        param->SslHashLength = (uint)hash.Length;
        param->pSslHash = hash.Length > 0 ? Put(hash) : null;
        param->AppId = k.GetValue("AppId") is byte[] { Length: 16 } app ? new Guid(app) : Guid.Empty;
        param->pSslCertStoreName = PutStr(store);
        param->DefaultCertCheckMode = (uint)(k.GetValue("DefaultSslCertCheckMode") as int? ?? 0);
        param->DefaultRevocationFreshnessTime = (uint)(k.GetValue("DefaultSslRevocationFreshnessTime") as int? ?? 0);
        param->DefaultRevocationUrlRetrievalTimeout = (uint)(k.GetValue("DefaultSslRevocationUrlRetrievalTimeout") as int? ?? 0);
        param->pDefaultSslCtlIdentifier = PutStr(ctlId);
        param->pDefaultSslCtlStoreName = PutStr(ctlStore);
        param->DefaultFlags = (uint)(k.GetValue("DefaultFlags") as int? ?? 0);
        return 0;
    }

    // ---- the keys: "0.0.0.0:443", "[::]:443", "host:443", as netsh names them

    private static string? SslKeyName(byte* sockaddr) => Endpoint(sockaddr) is { } ep ? "SslBindingInfo\\" + Name(ep) : null;

    private static string? SniKeyName(SniKey* key)
    {
        if (key->Host == null || Endpoint(key->IpPort) is not { } ep) return null;
        return $"SslSniBindingInfo\\{new string(key->Host).ToLowerInvariant()}:{ep.Port}";
    }

    private static string Name(IPEndPoint ep) => ep.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ep.Address}]:{ep.Port}" : $"{ep.Address}:{ep.Port}";

    // the n-th binding of a table, for an enumeration (HttpServiceConfigQueryNext's token)
    private static string? NthKey(string table, uint token)
    {
        using var t = Registry.LocalMachine.OpenSubKey(HttpParameters + table);
        string[] names = t?.GetSubKeyNames() ?? Array.Empty<string>();
        return token < names.Length ? table + "\\" + names[token] : null;
    }

    /// <summary>A sockaddr's address and port, in whichever layout it was made: Windows' (a 16-bit family: 2, or 23 for
    /// IPv6), Linux's (10 for IPv6) or BSD's (a length byte, then the family: 2, or 30 for IPv6).</summary>
    private static IPEndPoint? Endpoint(byte* sa)
    {
        if (sa == null) return null;
        bool bsd = sa[0] is 16 or 28 && sa[1] is 2 or 30;
        int family = bsd ? sa[1] : sa[0] | sa[1] << 8;
        int port = sa[2] << 8 | sa[3];
        if (family == 2) return new IPEndPoint(new IPAddress(new ReadOnlySpan<byte>(sa + 4, 4)), port);
        if (family is 23 or 10 or 30) return new IPEndPoint(new IPAddress(new ReadOnlySpan<byte>(sa + 8, 16)), port);
        return null;
    }

    // a sockaddr in Windows' layout, as http.sys hands one back
    private static byte[] SockAddr(string address, bool sni, int port = -1)
    {
        string host = address;
        if (port < 0)
        {
            int colon = address.LastIndexOf(':');
            port = int.Parse(address[(colon + 1)..]);
            host = address[..colon];
        }
        var ip = IPAddress.Parse(host.Trim('[', ']'));
        byte[] b = new byte[ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 28 : 16];
        b[0] = (byte)(ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 23 : 2);
        b[2] = (byte)(port >> 8);
        b[3] = (byte)port;
        ip.GetAddressBytes().CopyTo(b, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 8 : 4);
        return b;
    }
}
