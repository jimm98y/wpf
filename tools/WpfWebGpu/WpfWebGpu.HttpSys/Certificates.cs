// The certificate a port's HTTPS is served with: the one http.sys's SSL binding for the port (or the name asked
// for) names -- netsh http add sslcert's, in the registry, from the machine's store -- and with none, one made for
// this machine.

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace WpfWebGpu.HttpSys;

internal sealed class Certificates(ILogger logger)
{
    private X509Certificate2? _own;

    public X509Certificate2 For(string? name, int port)
    {
        if (Bound(name, port) is { } bound) return bound;
        lock (this)
        {
            if (_own != null) return _own;
            logger.LogWarning(
                "no certificate is bound to port {Port} (HKLM\\SYSTEM\\CurrentControlSet\\Services\\HTTP\\Parameters\\SslBindingInfo\\0.0.0.0:{Port}, as netsh http add sslcert binds one): a certificate made for this machine is used",
                port, port);
            return _own = MachineCertificate();
        }
    }

    private static X509Certificate2? Bound(string? name, int port)
    {
        const string Parameters = @"SYSTEM\CurrentControlSet\Services\HTTP\Parameters\";
        var keys = new List<string>();
        if (!string.IsNullOrEmpty(name)) keys.Add($@"{Parameters}SslSniBindingInfo\{name}:{port}");
        keys.Add($@"{Parameters}SslCcsBindingInfo\0.0.0.0:{port}");
        keys.Add($@"{Parameters}SslBindingInfo\0.0.0.0:{port}");
        keys.Add($@"{Parameters}SslBindingInfo\[::]:{port}");
        foreach (string path in keys)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                if (key?.GetValue("SslCertHash") is not byte[] hash) continue;
                string storeName = key.GetValue("SslCertStoreName") as string ?? "MY";
                using var store = new X509Store(storeName, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, Convert.ToHexString(hash), validOnly: false);
                if (found.Count > 0 && found[0].HasPrivateKey) return found[0];
            }
            catch (Exception) { }
        }
        return null;
    }

    private static X509Certificate2 MachineCertificate()
    {
        using var rsa = RSA.Create(2048);
        string machine = Environment.MachineName;
        var request = new CertificateRequest($"CN={machine}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(machine);
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // the key a TLS server can use on every platform: the certificate exported and loaded again
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null);
    }
}
