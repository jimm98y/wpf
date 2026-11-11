using System.IO.Compression;
using System.Security.Cryptography;
using WpfWebGpu.Setup;
using Xunit;

namespace WpfWebGpu.Tests;

public class SetupPayloadTests
{
    private static readonly byte[] Program = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();

    private static byte[] Zip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var s = zip.CreateEntry("setup.json").Open()) s.Write("{\"product\":\"P\"}"u8);
        return ms.ToArray();
    }

    private static string ReadSetupJson(Stream payload)
    {
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        using var r = new StreamReader(zip.GetEntry("setup.json")!.Open());
        return r.ReadToEnd();
    }

    [Fact]
    public void An_appended_installation_is_read_from_the_program_itself()
    {
        string dir = TestEnvironment.NewDirectory("payload");
        string setup = Path.Combine(dir, "P-Setup.exe");
        using (var f = File.Create(setup))
        {
            f.Write(Program);
            f.Write(Zip());
            f.Write(Payload.Trailer(Program.Length));
        }
        using (var payload = Payload.Open(setup)!) Assert.Contains("\"P\"", ReadSetupJson(payload));
        using (var f = File.OpenRead(setup)) Assert.Equal(Program.Length, Payload.ProgramLength(f));
    }

    [Fact]
    public void A_large_installation_beside_the_program_is_read_once_its_hash_matches()
    {
        string dir = TestEnvironment.NewDirectory("payload");
        string setup = Path.Combine(dir, "P-Setup.exe"), data = Path.Combine(dir, "P-Setup.data");
        byte[] zip = Zip();
        File.WriteAllBytes(data, zip);
        using (var f = File.Create(setup))
        {
            f.Write(Program);
            f.Write(Payload.ExternalTrailer("P-Setup.data", zip.Length, SHA256.HashData(zip)));
        }
        using (var payload = Payload.Open(setup)!) Assert.Contains("\"P\"", ReadSetupJson(payload));
        using (var f = File.OpenRead(setup)) Assert.Equal(Program.Length, Payload.ProgramLength(f));

        // a data file changed after the program vouched for it is refused, as is one that is missing
        zip[^1] ^= 1;
        File.WriteAllBytes(data, zip);
        Assert.Throws<InvalidDataException>(() => Payload.Open(setup));
        File.Delete(data);
        Assert.Throws<FileNotFoundException>(() => Payload.Open(setup));
    }

    [Fact]
    public void A_signed_program_vouches_for_the_installation_beside_it()
    {
        // a PE file to sign as Setup.exe: this assembly
        byte[] program = File.ReadAllBytes(typeof(SetupPayloadTests).Assembly.Location);
        string dir = TestEnvironment.NewDirectory("payload");
        string setup = Path.Combine(dir, "P-Setup.exe"), data = Path.Combine(dir, "P-Setup.data");
        byte[] zip = Zip();
        File.WriteAllBytes(data, zip);
        using (var f = File.Create(setup))
        {
            f.Write(program);
            f.Write(Payload.ExternalTrailer("P-Setup.data", zip.Length, SHA256.HashData(zip)));
        }
        using var rsa = RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        WpfWebGpu.Packager.Desktop.Authenticode.Sign(setup, certificate);

        Assert.True(WpfWebGpu.Packager.Desktop.Authenticode.HasValidHash(setup));
        using (var payload = Payload.Open(setup)!) Assert.Contains("\"P\"", ReadSetupJson(payload));
        using (var f = File.OpenRead(setup)) Assert.Equal(program.Length, Payload.ProgramLength(f));
    }

    [Fact]
    public void A_program_with_no_installation_has_none()
    {
        string setup = Path.Combine(TestEnvironment.NewDirectory("payload"), "P-Setup.exe");
        File.WriteAllBytes(setup, Program);
        Assert.Null(Payload.Open(setup));
    }
}
