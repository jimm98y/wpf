using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WpfWebGpu.Packager.Apple;
using WpfWebGpu.Packager.Desktop;
using Xunit;

namespace WpfWebGpu.Tests;

// A Mac installation's signatures, written in managed code (MachOSigner, the xar writer) with a certificate made as a
// Developer ID's are, then read back by macOS's own tools: codesign verifies the code, pkgutil the package. (The
// certificate is not Apple's, so neither trusts it: what they check is that the signatures are whole and right.)
public class MacSigningTests
{
    private static MachOSigner.Identity Certificate(string kind, string usage, string appleExtension)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=Developer ID {kind}: Test Publisher (TESTTEAM01), OU=TESTTEAM01, O=Test Publisher, C=US",
            key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(usage) }, true));
        request.CertificateExtensions.Add(new X509Extension(appleExtension, new byte[] { 0x05, 0x00 }, true));
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // through a .p12, as the Packager loads one
        string p12 = Path.Combine(TestEnvironment.NewDirectory("p12"), kind + ".p12");
        File.WriteAllBytes(p12, made.Export(X509ContentType.Pkcs12, "pw"));
        return MachOSigner.Identity.Load(p12, "pw");
    }

    private static MachOSigner.Identity Application() => Certificate("Application", "1.3.6.1.5.5.7.3.3", "1.2.840.113635.100.6.1.13");

    private static (int Code, string Output) Tool(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var error = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output + error.Result);
    }

    // a library and a program of the runtime the tests run on: Mach-O files as .NET ships them (signed by Microsoft)
    private static string Copy(string name)
    {
        string from = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, name);
        string to = Path.Combine(TestEnvironment.NewDirectory("macho"), name);
        File.Copy(from, to);
        return to;
    }

    [Fact]
    public void Code_signed_with_a_Developer_ID_verifies_as_codesign_s()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "verified by codesign");
        var identity = Application();
        var entitlements = new PlistDict { new("com.apple.security.cs.allow-jit", true) };

        string library = Copy("libclrjit.dylib");
        MachOSigner.Sign(library, new MachOSigner.Options { Identifier = "libclrjit", HardenedRuntime = true }, identity);
        string program = Copy("createdump");
        MachOSigner.Sign(program, new MachOSigner.Options { Identifier = "createdump", HardenedRuntime = true, MainExecutable = true, Entitlements = entitlements }, identity);

        foreach (string file in new[] { library, program })
        {
            var verify = Tool("/usr/bin/codesign", "--verify", "--strict", "-vv", file);
            Assert.True(verify.Code == 0, verify.Output);
            string details = Tool("/usr/bin/codesign", "-dvvv", file).Output;
            Assert.Contains("Authority=Developer ID Application: Test Publisher (TESTTEAM01)", details);
            Assert.Contains("v=20500 ", details);                 // codesign's version for the hardened runtime
            Assert.Contains("flags=0x10000(runtime)", details);
            Assert.Matches(@"Runtime Version=\d+\.\d+", details);   // the SDK it was built with
        }
        Assert.Contains("Identifier=libclrjit", Tool("/usr/bin/codesign", "-dvvv", library).Output);
        Assert.Contains("com.apple.security.cs.allow-jit", Tool("/usr/bin/codesign", "-d", "--entitlements", "-", "--xml", program).Output);
    }

    [Fact]
    public void A_universal_file_is_signed_slice_by_slice()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "verified by codesign");
        // a universal file of the library's slice, at a 16 KB alignment, as lipo lays one out
        byte[] slice = File.ReadAllBytes(Copy("libclrjit.dylib"));
        const int align = 14, offset = 1 << align;
        var fat = new byte[offset + slice.Length];
        void Be(int at, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(at), v);
        Be(0, 0xcafebabe);
        Be(4, 1);
        Be(8, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(slice.AsSpan(4)));    // cputype
        Be(12, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(slice.AsSpan(8)));   // cpusubtype
        Be(16, offset);
        Be(20, (uint)slice.Length);
        Be(24, align);
        slice.CopyTo(fat, offset);
        string file = Path.Combine(TestEnvironment.NewDirectory("fat"), "libfat.dylib");
        File.WriteAllBytes(file, fat);

        MachOSigner.Sign(file, new MachOSigner.Options { Identifier = "libfat", HardenedRuntime = true }, Application());
        var verify = Tool("/usr/bin/codesign", "--verify", "--strict", "-vv", file);
        Assert.True(verify.Code == 0, verify.Output);
        Assert.Contains("Format=Mach-O universal", Tool("/usr/bin/codesign", "-dvvv", file).Output);
        Assert.Contains("Architectures in the fat file", Tool("/usr/bin/lipo", "-info", file).Output);
    }

    [Fact]
    public void A_package_signed_as_productsign_signs_it()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "verified by pkgutil");
        var identity = Certificate("Installer", "1.2.840.113635.100.4.13", "1.2.840.113635.100.6.1.14");
        string dir = TestEnvironment.NewDirectory("signedpkg");
        string data = Path.Combine(dir, "big.bin");
        var random = new Random(3);
        var bytes = new byte[3 * 1024 * 1024];
        random.NextBytes(bytes);
        File.WriteAllBytes(data, bytes);
        var archive = new XarWriter();
        archive.AddBytes("Distribution", "<?xml version=\"1.0\"?><installer-gui-script minSpecVersion=\"2\"/>"u8.ToArray(), compress: true);
        archive.AddFile("Resources/big.bin", data, compress: false);
        string pkg = Path.Combine(dir, "signed.pkg");
        archive.Write(pkg, identity);

        // pkgutil finds the signature whole, by the certificate given (not one Apple issued, so not trusted)
        string check = Tool("/usr/sbin/pkgutil", "--check-signature", pkg).Output;
        Assert.Contains("Developer ID Installer: Test Publisher (TESTTEAM01)", check);
        Assert.DoesNotContain("no signature", check);
        // the files where the TOC says, after the signatures
        string flat = TestEnvironment.NewDirectory("signedflat");
        var x = Tool("/usr/bin/xar", "-xf", pkg, "-C", flat);
        Assert.True(x.Code == 0, x.Output);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(flat, "Resources", "big.bin")));
    }
}
