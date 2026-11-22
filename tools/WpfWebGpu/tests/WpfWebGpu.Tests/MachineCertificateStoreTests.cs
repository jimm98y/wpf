// The machine's certificate stores off Windows (MachineCertificateStore): the installation's, a folder of each in the
// machine's state, which a setup run as the user adds to and its other programs -- http.sys -- read, on Linux and on
// macOS alike (where .NET's own machine stores are the System keychain, which only root may add to).

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WpfWebGpu.ComInterop;
using Xunit;

namespace WpfWebGpu.Tests;

[Collection("IisAdmin")]
public sealed class MachineCertificateStoreTests
{
    private static X509Certificate2 WithKey(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // as a setup makes one: exportable, its key copied onto it
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    [Fact]
    public void A_certificate_added_to_the_machine_s_My_is_found_there_with_its_key()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the machine's own stores");
        string state = TestEnvironment.NewDirectory("certstate");
        string? before = Environment.GetEnvironmentVariable("WPFRUN_STATE");
        Environment.SetEnvironmentVariable("WPFRUN_STATE", state);
        try
        {
            using var cert = WithKey("machine-test");
            using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
            {
                MachineCertificateStore.Open(store, OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
                store.Add(cert);
            }
            Assert.True(File.Exists(Path.Combine(state, "certificates", "LocalMachine", "my", cert.Thumbprint + ".pfx")));

            // read as http.sys reads it: by its thumbprint, with its key
            using (var store = new X509Store("MY", StoreLocation.LocalMachine))
            {
                MachineCertificateStore.Open(store, OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false);
                Assert.Single(found);
                Assert.True(found[0].HasPrivateKey);
                Assert.Throws<CryptographicException>(() => store.Add(cert));   // opened to read
            }

            // a trusted root added is the installation's too, and given the system's trust (as Windows' machine store is its
            // trust); removed, taken out of it again
            var trust = new List<(string, bool)>();
            SystemTrust.Override = (c, on) => trust.Add((c.Thumbprint, on));
            try
            {
                using (var root = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
                {
                    MachineCertificateStore.Open(root, OpenFlags.ReadWrite);
                    root.Add(X509CertificateLoader.LoadCertificate(cert.RawData));
                }
                Assert.True(File.Exists(Path.Combine(state, "certificates", "LocalMachine", "root", cert.Thumbprint + ".pfx")));
                using (var root = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
                {
                    MachineCertificateStore.Open(root, OpenFlags.ReadWrite);
                    root.Remove(X509CertificateLoader.LoadCertificate(cert.RawData));
                }
                Assert.Equal(new[] { (cert.Thumbprint, true), (cert.Thumbprint, false) }, trust);
            }
            finally { SystemTrust.Override = null; }
        }
        finally { Environment.SetEnvironmentVariable("WPFRUN_STATE", before); }
    }
}
