using WpfWebGpu.Packager;
using Xunit;

namespace WpfWebGpu.Tests;

public class PackagerTests
{
    private const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\SOFTWARE\Vendor\Product\Launch]
        "Tool"="C:\\Program Files\\Vendor\\Product\\Clients\\Tool\\Tool.exe"
        "Cmd"="\"C:\\PROGRAM FILES\\Vendor\\Product\\Clients\\Tool\\Tool.exe\" /x"
        "Data"="C:\\ProgramData\\Vendor\\Data"
        "Other"="C:\\Program Files\\Vendor\\ProductX\\a.exe"
        "List"=hex(7):43,00,3a,00,5c,00,50,00,72,00,6f,00,67,00,72,00,61,00,6d,00,44,00,61,00,74,00,61,00,00,00,00,00

        [HKEY_LOCAL_MACHINE\SOFTWARE\Vendor\Gone]
        "x"=dword:00000001

        [-HKEY_LOCAL_MACHINE\SOFTWARE\Vendor\Gone]
        """;

    private static string Install()
    {
        string dir = TestEnvironment.NewDirectory("install");
        Directory.CreateDirectory(Path.Combine(dir, "Clients", "Tool"));
        File.WriteAllText(Path.Combine(dir, "Clients", "Tool", "Tool.exe"), "");
        return dir;
    }

    [Fact]
    public void Finds_the_installation_s_folder_in_the_registry_s_paths()
    {
        var mappings = Scanner.DetectMappings(RegText.Parse(Reg), Install());
        Assert.Equal(@"C:\Program Files\Vendor\Product", mappings[0].From, ignoreCase: true);
        Assert.Equal(PathTokens.InstallDir, mappings[0].To);
        Assert.Contains(mappings, m => m.From == @"C:\ProgramData" && m.To == PathTokens.ProgramData);
        Assert.DoesNotContain(mappings, m => m.From.Contains("ProductX"));
    }

    [Fact]
    public void Maps_whole_path_segments_ignoring_case()
    {
        var mapper = new PathMapper(new[]
        {
            new PathMapping { From = @"C:\Program Files\Vendor\Product", To = PathTokens.InstallDir },
            new PathMapping { From = @"C:\ProgramData", To = PathTokens.ProgramData },
        });
        Assert.Equal(PathTokens.InstallDir + @"\a.exe", mapper.Map(@"c:\program files\vendor\product\a.exe"));
        Assert.Equal("\"" + PathTokens.InstallDir + "\\a.exe\" /x", mapper.Map("\"C:\\Program Files\\Vendor\\Product\\a.exe\" /x"));
        Assert.Equal(@"C:\Program Files\Vendor\ProductX\a.exe", mapper.Map(@"C:\Program Files\Vendor\ProductX\a.exe"));
        Assert.Equal(PathTokens.ProgramData, mapper.Map(@"C:\ProgramData"));
    }

    [Fact]
    public void Stages_the_registry_as_the_stand_in_s_hive_files()
    {
        string install = Install(), work = TestEnvironment.NewDirectory("work");
        string reg = Path.Combine(work, "in.reg");
        File.WriteAllText(reg, Reg);
        var manifest = new PackageManifest();
        manifest.Inputs.InstallDir = install;
        manifest.Inputs.Registry.Add(reg);
        manifest.PathMappings.AddRange(Scanner.DetectMappings(RegText.Parse(Reg), install));

        var staged = Staging.Stage(manifest, work);
        string hive = File.ReadAllText(Path.Combine(staged.RegistryDir, "HKEY_LOCAL_MACHINE.reg"));
        Assert.Contains("[SOFTWARE\\Vendor\\Product\\Launch]\n", hive);
        Assert.Contains("\"Tool\"=str:" + PathTokens.InstallDir + "\\Clients\\Tool\\Tool.exe\n", hive);
        Assert.Contains("\"Cmd\"=str:%22" + PathTokens.InstallDir + "\\Clients\\Tool\\Tool.exe%22 /x\n", hive);
        Assert.Contains("\"Data\"=str:" + PathTokens.ProgramData + "\\Vendor\\Data\n", hive);
        Assert.Contains("\"List\"=multi:" + PathTokens.ProgramData + "\n", hive);
        Assert.DoesNotContain("Gone", hive);

        string resolved = Staging.Resolve(hive, new Dictionary<string, string> { [PathTokens.InstallDir] = "/opt/p", [PathTokens.ProgramData] = "/var/p 100%" });
        Assert.Contains("\"Tool\"=str:/opt/p\\Clients\\Tool\\Tool.exe\n", resolved);
        Assert.Contains("\"Data\"=str:/var/p 100%25\\Vendor\\Data\n", resolved);
    }

    [Fact]
    public void A_machine_s_own_certificate_is_left_out_of_the_seed_and_a_peer_s_kept()
    {
        string data = TestEnvironment.NewDirectory("pki"), work = TestEnvironment.NewDirectory("work");
        string own = Path.Combine(data, "pki", "own"), trusted = Path.Combine(data, "pki", "trusted", "certs");
        Directory.CreateDirectory(Path.Combine(own, "certs"));
        Directory.CreateDirectory(Path.Combine(own, "private"));
        Directory.CreateDirectory(trusted);
        using var mine = SelfSigned("CN=Server, DC=winbox", "winbox");
        File.WriteAllBytes(Path.Combine(own, "private", $"Server [{mine.Thumbprint}].pfx"), mine.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12));
        File.WriteAllBytes(Path.Combine(own, "certs", "Server.der"), mine.RawData);
        using var peer = SelfSigned("CN=Peer, DC=otherbox", "otherbox");
        File.WriteAllBytes(Path.Combine(trusted, $"Peer [{peer.Thumbprint}].der"), peer.RawData);
        using var local = SelfSigned("CN=Local", "localhost");
        File.WriteAllBytes(Path.Combine(own, "private", "Local.pfx"), local.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12));
        File.WriteAllText(Path.Combine(own, "certs", "Readme.txt"), "certificates");
        var manifest = new PackageManifest();
        manifest.Inputs.InstallDir = Install();
        manifest.Inputs.ProgramData.Add(data);

        var staged = Staging.Stage(manifest, work);
        string root = Path.GetFileName(data);
        var kept = staged.ProgramDataFiles.Select(f => f.Target).ToHashSet();
        Assert.Equal(new[] { $"{root}/pki/own/certs/Server.der", $"{root}/pki/own/private/Server [{mine.Thumbprint}].pfx" }, staged.LeftOut.Keys.Order());
        Assert.Contains("winbox", staged.LeftOut.Values.First());
        Assert.Contains($"{root}/pki/trusted/certs/Peer [{peer.Thumbprint}].der", kept);     // a peer's, without its key
        Assert.Contains($"{root}/pki/own/private/Local.pfx", kept);                           // made for no host
        Assert.Contains($"{root}/pki/own/certs/Readme.txt", kept);
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 SelfSigned(string subject, string host)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(subject, rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var names = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    [Fact]
    public void A_service_s_account_is_taken_from_its_registry_key()
    {
        const string reg = """
            Windows Registry Editor Version 5.00

            [HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\WebHost]
            "ObjectName"="NT AUTHORITY\\NetworkService"

            [HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Renamed]
            "ImagePath"="\"C:\\Program Files\\P\\Collector.exe\" -service"
            "ObjectName"="NT AUTHORITY\\LOCAL SERVICE"

            [HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Machine]
            "ObjectName"="LocalSystem"
            """;
        var manifest = new PackageManifest();
        manifest.Services.Add(new ServiceEntry { Name = "WebHost", Exe = "Services/WebHost.exe" });
        manifest.Services.Add(new ServiceEntry { Name = "Collector", Exe = "Services/Collector.exe" });
        manifest.Services.Add(new ServiceEntry { Name = "Machine", Exe = "Services/Machine.exe" });
        manifest.Services.Add(new ServiceEntry { Name = "Unknown", Exe = "Services/Unknown.exe" });
        Scanner.AssignAccounts(manifest, RegText.Parse(reg), null);
        Assert.Equal(new[] { ServiceAccount.NetworkService, ServiceAccount.LocalService, ServiceAccount.LocalSystem, ServiceAccount.LocalSystem },
            manifest.Services.Select(x => x.Account));
        Assert.Equal("wpf-networkservice", WpfWebGpu.Packager.Desktop.DesktopCommon.SystemAccount(ServiceAccount.NetworkService, mac: false));
        Assert.Equal("_wpflocalservice", WpfWebGpu.Packager.Desktop.DesktopCommon.SystemAccount(ServiceAccount.LocalService, mac: true));
        Assert.Null(WpfWebGpu.Packager.Desktop.DesktopCommon.SystemAccount(ServiceAccount.LocalSystem, mac: true));
    }

    [Theory]
    [InlineData("**/Logs/**", "11/Logs/Traces/a.log", true)]
    [InlineData("**/Logs/**", "Logs/a.log", true)]
    [InlineData("**/Logs/**", "11/Catalogs/a.log", false)]
    [InlineData("**/*.dmp", "a/b/c.DMP", true)]
    [InlineData("Data/*.bin", "Data/x/y.bin", false)]
    public void Excludes_by_glob(string glob, string path, bool excluded) =>
        Assert.Equal(excluded, new GlobSet(new[] { glob }).Matches(path));

    [Fact]
    public void A_manifest_saves_and_loads_with_paths_relative_to_it()
    {
        string dir = TestEnvironment.NewDirectory("manifest");
        var m = new PackageManifest();
        m.Inputs.InstallDir = "install";
        m.Apps.Add(new AppEntry { Exe = "a/App.exe", Kind = AppKind.Service, Mobile = true });
        m.Output.Targets.Add(Target.Quest);
        string path = Path.Combine(dir, "x" + PackageManifest.Extension);
        m.Save(path);
        Assert.Contains("\"kind\": \"Service\"", File.ReadAllText(path));
        var back = PackageManifest.Load(path);
        Assert.Equal(Path.Combine(dir, "install"), back.Inputs.InstallDir);
        Assert.Equal(AppKind.Service, back.Apps[0].Kind);
        Assert.Equal(Target.Quest, back.Output.Targets[0]);
    }
}

public class PackagerArchiveTests
{
    [Fact]
    public void A_zip_written_reads_back_with_every_entry_whole_and_aligned()
    {
        string dir = TestEnvironment.NewDirectory("zip");
        string path = Path.Combine(dir, "a.zip");
        long cd;
        using (var f = File.Create(path))
        {
            var zip = new WpfWebGpu.Packager.Archives.ZipWriter(f);
            zip.Add("empty.txt", Array.Empty<byte>(), compress: true);
            zip.Add("text.txt", System.Text.Encoding.UTF8.GetBytes(new string('x', 10000)), compress: true);
            zip.Add("lib/arm64-v8a/libx.so", new byte[] { 1, 2, 3 }, compress: false);
            zip.Add("stored.png", new byte[] { 4, 5 }, compress: false);
            cd = zip.Finish();
        }
        using (var z = System.IO.Compression.ZipFile.OpenRead(path))
        {
            Assert.Equal(4, z.Entries.Count);
            foreach (var e in z.Entries) { using var s = e.Open(); s.CopyTo(Stream.Null); }
            using var r = new StreamReader(z.GetEntry("text.txt")!.Open());
            Assert.Equal(10000, r.ReadToEnd().Length);
        }
        using var reader = new WpfWebGpu.Packager.Archives.ZipReader(path);
        var so = reader.Entries.Single(e => e.Name.EndsWith(".so"));
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.Read(so));
        // the data of a native library on a 16 KB page, of any other stored entry on 4 bytes
        byte[] all = File.ReadAllBytes(path);
        int soData = Array.IndexOf(all, (byte)1, (int)so.LocalHeaderOffset + 30);
        Assert.Equal(0, soData % 16384);
        Assert.True(cd > 0);
    }
}
