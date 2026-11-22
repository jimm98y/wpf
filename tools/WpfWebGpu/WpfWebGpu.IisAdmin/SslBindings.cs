// A binding's certificate, which IIS keeps in http.sys's configuration rather than in applicationHost.config: the SSL
// binding of the binding's address and port (SslBindingInfo\0.0.0.0:443), or of its host name and port where it uses SNI
// (SslSniBindingInfo\host:443), with the values netsh http add sslcert gives it. The stack's http.sys serves HTTPS with
// the certificate it names (WpfWebGpu.HttpSys's Certificates), as httpapi's HttpSetServiceConfiguration sets it too.

using System.Globalization;
using Microsoft.Win32;

namespace WpfWebGpu.IisAdmin;

internal static class SslBindings
{
    private const string Parameters = @"SYSTEM\CurrentControlSet\Services\HTTP\Parameters\";
    // IIS's application id in http.sys's SSL bindings
    private static readonly Guid IisAppId = new("4dc3e181-e14b-4a21-b022-59fc669b0914");

    /// <summary>The SSL binding a site's binding is served with: its registry key, null where it has no endpoint.</summary>
    private static string? Key(AppHostElement binding)
    {
        string? info = binding.GetPropertyByName("bindingInformation").get_Value() as string;
        if (string.IsNullOrEmpty(info)) return null;
        // "address:port:host" -- the address "*", an IPv4 address, or an IPv6 one in brackets
        int hostColon = info.LastIndexOf(':');
        if (hostColon <= 0) return null;
        string host = info[(hostColon + 1)..], addressPort = info[..hostColon];
        int portColon = addressPort.LastIndexOf(':');
        if (portColon < 0 || !int.TryParse(addressPort[(portColon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)) return null;
        string address = addressPort[..portColon];
        bool sni = ((uint)binding.GetPropertyByName("sslFlags").get_Value() & 1) != 0;
        if (sni && host.Length > 0) return $@"{Parameters}SslSniBindingInfo\{host.ToLowerInvariant()}:{port}";
        address = address is "*" or "" ? "0.0.0.0" : address;
        return $@"{Parameters}SslBindingInfo\{address}:{port}";
    }

    public static (string Hash, string Store)? Read(AppHostElement binding)
    {
        if (Key(binding) is not { } key) return null;
        using var k = Registry.LocalMachine.OpenSubKey(key);
        if (k?.GetValue("SslCertHash") is not byte[] hash) return null;
        return (Convert.ToHexString(hash).ToLowerInvariant(), k.GetValue("SslCertStoreName") as string ?? "MY");
    }

    public static void Add(AppHostElement binding, string hash, string? store)
    {
        if (Key(binding) is not { } key) throw HResults.Error(HResults.E_INVALIDARG, "the binding has no address and port");
        Registry.LocalMachine.DeleteSubKeyTree(key, false);
        using var k = Registry.LocalMachine.CreateSubKey(key);
        k.SetValue("SslCertHash", Convert.FromHexString(hash.Replace(" ", "")), RegistryValueKind.Binary);
        k.SetValue("SslCertStoreName", string.IsNullOrEmpty(store) ? "MY" : store);
        k.SetValue("AppId", IisAppId.ToByteArray(), RegistryValueKind.Binary);
        k.SetValue("DefaultSslCertCheckMode", 0, RegistryValueKind.DWord);
        k.SetValue("DefaultSslRevocationFreshnessTime", 0, RegistryValueKind.DWord);
        k.SetValue("DefaultSslRevocationUrlRetrievalTimeout", 0, RegistryValueKind.DWord);
        k.SetValue("DefaultFlags", 0, RegistryValueKind.DWord);
    }

    public static void Remove(AppHostElement binding)
    {
        if (Key(binding) is { } key) Registry.LocalMachine.DeleteSubKeyTree(key, false);
    }
}
