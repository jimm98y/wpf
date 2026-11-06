// A machine's own identity among its ProgramData: a self-signed certificate with its private key, made for the
// machine's host name -- an OPC UA application instance certificate, a service's TLS certificate made at install.
// It names the machine the installation was taken from, so where it is installed it is invalid (another host), and
// the program makes its own there, as it does on a fresh install; it is left out of the seed. A certificate a
// certificate authority issued, and a peer's certificate trusted without its key, are not the machine's and stay.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WpfWebGpu.Packager;

public static class MachineCertificates
{
    private static readonly HashSet<string> s_keyFiles = new(StringComparer.OrdinalIgnoreCase) { ".pfx", ".p12" };
    private static readonly HashSet<string> s_certFiles = new(StringComparer.OrdinalIgnoreCase) { ".der", ".cer", ".crt", ".pem" };
    private const long MaxSize = 1024 * 1024;

    /// <summary>The files (by target) that are a machine's own certificate or its key, each with why.</summary>
    public static Dictionary<string, string> Find(IReadOnlyList<(string Source, string Target)> files)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var own = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);       // thumbprint -> why
        foreach (var (source, target) in files)
        {
            if (!s_keyFiles.Contains(Path.GetExtension(source)) || new FileInfo(source).Length > MaxSize) continue;
            if (MachineOwn(source) is not { } why) continue;
            found[target] = why.Reason;
            own[why.Thumbprint] = why.Reason;
        }
        if (own.Count == 0) return found;
        // the certificate's public half beside it: named for its thumbprint, or the same certificate
        foreach (var (source, target) in files)
        {
            if (found.ContainsKey(target) || !s_certFiles.Contains(Path.GetExtension(source))) continue;
            string name = Path.GetFileName(source);
            string? thumbprint = own.Keys.FirstOrDefault(t => name.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (thumbprint == null && new FileInfo(source).Length <= MaxSize)
            {
                try
                {
                    using var c = X509CertificateLoader.LoadCertificateFromFile(source);
                    if (own.ContainsKey(c.Thumbprint)) thumbprint = c.Thumbprint;
                }
                catch (CryptographicException) { }
            }
            if (thumbprint != null) found[target] = own[thumbprint];
        }
        return found;
    }

    private static (string Thumbprint, string Reason)? MachineOwn(string pfx)
    {
        foreach (string? password in new[] { null, "" })
        {
            try
            {
                using var c = X509CertificateLoader.LoadPkcs12FromFile(pfx, password, X509KeyStorageFlags.DefaultKeySet);
                if (!c.HasPrivateKey || !c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData)) return null;
                var hosts = c.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames())
                    .Where(h => !h.Equals("localhost", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (hosts.Count == 0) return null;
                return (c.Thumbprint, $"the certificate {c.Subject} is the machine {string.Join(", ", hosts)}'s own (self-signed, with its key): the program makes its own where it is installed");
            }
            catch (CryptographicException) { }          // another password, or not a certificate this reads
        }
        return null;
    }
}
