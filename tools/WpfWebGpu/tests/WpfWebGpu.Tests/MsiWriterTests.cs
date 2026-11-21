using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WpfWebGpu.Packager;
using WpfWebGpu.Packager.Desktop;
using WpfWebGpu.Packager.Desktop.Msi;
using WpfWebGpu.Setup;
using Xunit;

namespace WpfWebGpu.Tests;

public class MsiWriterTests
{
    private static MsiReader Write(MsiDatabase db, Action<CompoundFile>? more = null)
    {
        var cf = new CompoundFile();
        db.WriteTo(cf);
        more?.Invoke(cf);
        string path = Path.Combine(TestEnvironment.NewDirectory("msi"), "t.msi");
        cf.Write(path);
        return new MsiReader(File.ReadAllBytes(path));
    }

    [Fact]
    public void Tables_read_back_as_written_their_rows_in_key_order()
    {
        var db = new MsiDatabase();
        db["Property"].Add("ProductName", "Product");
        db["Property"].Add("ALLUSERS", "1");
        db["Property"].Add("Manufacturer", "Publisher");
        db["File"].Add("f2", "c1", "B.DLL|b.dll", 70000, "1.2.3.4", null, null, 40000);
        db["File"].Add("f1", "c1", "a.txt", 5, null, null, null, 1);
        db["Media"].Add(1, 40000, null, "#data1.cab", null, null);
        db["Binary"].Add("Helper", new byte[] { 1, 2, 3 });
        var msi = Write(db);

        Assert.Equal(1252, msi.Codepage);
        Assert.Equal(new[] { "Binary", "File", "Media", "Property" }, msi.Tables.Order());
        Assert.Contains(msi.Columns, c => c.Table == "File" && c.Name == "Sequence" && c.Type == 0x0104);
        Assert.Contains(msi.Columns, c => c.Table == "Property" && c.Name == "Property" && c.Type == 0x2D48);
        var props = msi.Rows("Property");
        Assert.Equal(3, props.Count);
        // rows in the order of their keys' string ids: as they were first seen
        Assert.Equal(new[] { "ProductName", "ALLUSERS", "Manufacturer" }, props.Select(r => (string)r[0]!));
        var files = msi.Rows("File");
        Assert.Equal(new object?[] { "f2", "c1", "B.DLL|b.dll", 70000, "1.2.3.4", null, null, 40000 }, files[0]);
        Assert.Equal(new object?[] { 1, 40000, null, "#data1.cab", null, null }, msi.Rows("Media")[0]);
        Assert.Equal(1, msi.Rows("Binary")[0][1]);
        Assert.Equal(new byte[] { 1, 2, 3 }, msi.Stream("Binary.Helper"));
        // reference counts: "c1" twice
        Assert.Equal(2, msi.RefCounts[msi.Strings.IndexOf("c1")]);
    }

    [Fact]
    public void More_than_65535_strings_take_3_byte_references_and_a_long_string_two_pool_entries()
    {
        var db = new MsiDatabase();
        for (int i = 0; i < 70000; i++) db["Property"].Add("P" + i, "V" + i);
        string longValue = new('x', 70000);
        db["Property"].Add("Long", longValue);
        var msi = Write(db);
        Assert.Equal(3, msi.StringRefSize);
        var rows = msi.Rows("Property").ToDictionary(r => (string)r[0]!, r => (string?)r[1]);
        Assert.Equal(70001, rows.Count);
        Assert.Equal("V69999", rows["P69999"]);
        Assert.Equal(longValue, rows["Long"]);
    }

    [Fact]
    public void A_large_compound_file_has_its_FAT_beyond_the_header_and_a_valid_tree()
    {
        var cf = new CompoundFile { RootClsid = Guid.NewGuid() };
        var big = RandomNumberGenerator.GetBytes(10 << 20);   // 20480 sectors: 160 FAT sectors, 109 in the header
        cf.Add("big", big);
        var names = Enumerable.Range(0, 37).Select(i => "s" + i).ToList();
        foreach (string n in names) cf.Add(n, Encoding.UTF8.GetBytes(n + new string('.', n.Length * 100)));
        cf.Add("empty", Array.Empty<byte>());
        string path = Path.Combine(TestEnvironment.NewDirectory("cfb"), "big.cfb");
        cf.Write(path);
        var r = new CompoundFileReader(File.ReadAllBytes(path));
        Assert.Equal(big, r.Read("big"));
        foreach (string n in names) Assert.Equal(Encoding.UTF8.GetBytes(n + new string('.', n.Length * 100)), r.Read(n));
        Assert.Empty(r.Read("empty")!);
        Assert.Equal(cf.RootClsid, r.Entries[0].Clsid);

        // the tree: ordered by (length, upper-cased name), no red node with a red child, the same black count on every path
        int Compare(string a, string b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a.ToUpperInvariant(), b.ToUpperInvariant());
        var seen = new List<string>();
        int Walk(uint node, bool parentRed)
        {
            if (node == 0xFFFFFFFF) return 1;
            var e = r.Entries[(int)node];
            bool red = e.Color == 0;
            Assert.False(red && parentRed);
            int left = Walk(e.Left, red);
            seen.Add(e.Name);
            int right = Walk(e.Right, red);
            Assert.Equal(left, right);
            return left + (red ? 0 : 1);
        }
        Walk(r.Entries[0].Child, parentRed: false);
        Assert.Equal(names.Count + 2, seen.Count);
        for (int i = 1; i < seen.Count; i++) Assert.True(Compare(seen[i - 1], seen[i]) < 0, $"{seen[i - 1]} before {seen[i]}");
    }

    [Fact]
    public void A_cabinet_holds_its_files_in_MSZIP_blocks()
    {
        string dir = TestEnvironment.NewDirectory("cab");
        var contents = new[]
        {
            Array.Empty<byte>(),
            "x"u8.ToArray(),
            Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("compressible text ", 5000))),
            RandomNumberGenerator.GetBytes(100_000),
            RandomNumberGenerator.GetBytes(32768),
        };
        var items = new List<Cabinet.Item>();
        for (int i = 0; i < contents.Length; i++)
        {
            string f = Path.Combine(dir, "in" + i);
            File.WriteAllBytes(f, contents[i]);
            items.Add(new Cabinet.Item("f" + (i + 1), f, contents[i].Length, new DateTime(2026, 11, 21, 10, 20, 30)));
        }
        foreach (bool compress in new[] { true, false })
        {
            string cab = Path.Combine(dir, compress ? "z.cab" : "s.cab");
            Cabinet.Write(cab, items, compress);
            var read = ReadCabinet(File.ReadAllBytes(cab));
            Assert.Equal(items.Select(i => i.Name), read.Select(r => r.Name));
            for (int i = 0; i < contents.Length; i++) Assert.Equal(contents[i], read[i].Data);
        }
    }

    // a cabinet decoded as Windows' FDI does: the header, the folders, the files, each block "CK" + deflate (or stored)
    private static List<(string Name, byte[] Data)> ReadCabinet(byte[] cab)
    {
        Assert.Equal(0x4643534Du, BinaryPrimitives.ReadUInt32LittleEndian(cab));
        Assert.Equal((uint)cab.Length, BinaryPrimitives.ReadUInt32LittleEndian(cab.AsSpan(8)));
        int filesAt = (int)BinaryPrimitives.ReadUInt32LittleEndian(cab.AsSpan(16));
        int folders = BinaryPrimitives.ReadUInt16LittleEndian(cab.AsSpan(26)), files = BinaryPrimitives.ReadUInt16LittleEndian(cab.AsSpan(28));
        var streams = new List<byte[]>();
        for (int f = 0; f < folders; f++)
        {
            int at = (int)BinaryPrimitives.ReadUInt32LittleEndian(cab.AsSpan(36 + f * 8));
            int blocks = BinaryPrimitives.ReadUInt16LittleEndian(cab.AsSpan(40 + f * 8));
            int type = BinaryPrimitives.ReadUInt16LittleEndian(cab.AsSpan(42 + f * 8));
            var ms = new MemoryStream();
            for (int b = 0; b < blocks; b++)
            {
                int compressed = BinaryPrimitives.ReadUInt16LittleEndian(cab.AsSpan(at + 4)), size = BinaryPrimitives.ReadUInt16LittleEndian(cab.AsSpan(at + 6));
                Assert.True(size <= 32768);
                var data = cab.AsSpan(at + 8, compressed).ToArray();
                if (type == 1)
                {
                    Assert.Equal((byte)'C', data[0]);
                    Assert.Equal((byte)'K', data[1]);
                    using var inflate = new DeflateStream(new MemoryStream(data, 2, data.Length - 2), CompressionMode.Decompress);
                    var block = new byte[size];
                    inflate.ReadExactly(block);
                    ms.Write(block);
                }
                else ms.Write(data);
                at += 8 + compressed;
            }
            streams.Add(ms.ToArray());
        }
        var result = new List<(string, byte[])>();
        for (int i = 0, at = filesAt; i < files; i++)
        {
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(cab.AsSpan(at));
            int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(cab.AsSpan(at + 4));
            int folder = BinaryPrimitives.ReadUInt16LittleEndian(cab.AsSpan(at + 8));
            int end = Array.IndexOf(cab, (byte)0, at + 16);
            result.Add((Encoding.ASCII.GetString(cab, at + 16, end - at - 16), streams[folder].AsSpan(offset, size).ToArray()));
            at = end + 1;
        }
        return result;
    }

    [Fact]
    public void The_signature_digest_is_of_the_streams_in_their_names_byte_order_then_the_class()
    {
        var db = new MsiDatabase();
        db["Property"].Add("ProductName", "P");
        var cf = new CompoundFile();
        db.WriteTo(cf);
        cf.Add(SummaryInformation.StreamName, new SummaryInformation { Revision = "{00000000-0000-0000-0000-000000000001}" }.ToBytes());
        byte[] digest = cf.ContentDigest(HashAlgorithmName.SHA256);
        string path = Path.Combine(TestEnvironment.NewDirectory("msisig"), "s.msi");
        cf.Write(path);

        // as osslsigncode computes it from the file: the entries' raw names compared bytewise, the longer first on a tie
        var r = new CompoundFileReader(File.ReadAllBytes(path));
        var streams = r.Entries.Skip(1).Where(e => e.Type == 2).ToList();
        streams.Sort((a, b) =>
        {
            int n = Math.Min(a.RawName.Length, b.RawName.Length);
            for (int i = 0; i < n; i++) if (a.RawName[i] != b.RawName[i]) return a.RawName[i].CompareTo(b.RawName[i]);
            return a.RawName.Length == b.RawName.Length ? 0 : a.RawName.Length > b.RawName.Length ? -1 : 1;
        });
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var e in streams) hash.AppendData(r.Read(e));
        hash.AppendData(r.Entries[0].Clsid.ToByteArray());
        Assert.Equal(hash.GetHashAndReset(), digest);
        Assert.Equal(new Guid("000C1084-0000-0000-C000-000000000046"), r.Entries[0].Clsid);
    }

    [Fact]
    public void Registry_values_are_written_as_the_Registry_table_formats_them()
    {
        HiveText.Line V(string kind, string value) => new("K", "n", kind, value);
        Assert.Equal("[WPFAPPDIR]\\Bin\\[\\[]x[\\]]", MsiPackager.RegistryValue(V("str", "${WPFRUN_INSTALLDIR}\\Bin\\[x]")));
        Assert.Equal("##1", MsiPackager.RegistryValue(V("str", "#1")));
        Assert.Equal("#%[WPFPROGRAMDATA]\\P", MsiPackager.RegistryValue(V("expand", "${WPFRUN_PROGRAMDATA}\\P")));
        Assert.Equal("#-1", MsiPackager.RegistryValue(V("dword", "ffffffff")));
        Assert.Equal("[~]a[~]b c[~]", MsiPackager.RegistryValue(V("multi", "a%00b c%00")));
        Assert.Equal("#x0102FF", MsiPackager.RegistryValue(V("bin", Convert.ToBase64String(new byte[] { 1, 2, 255 }))));
        Assert.Null(MsiPackager.RegistryValue(V("qword", "1")));
        Assert.Equal("10.97.3", MsiPackager.ProductVersion("10.97.3"));
        Assert.Equal("1.0.0", MsiPackager.ProductVersion("1"));
        Assert.Throws<PackagerError>(() => MsiPackager.ProductVersion("2026.1"));
        Assert.Equal(MsiPackager.NameGuid("x"), MsiPackager.NameGuid("x"));
        Assert.NotEqual(MsiPackager.NameGuid("x"), MsiPackager.NameGuid("y"));
        // RFC 4122's own example of a version 5 name: the DNS namespace's "www.example.com"... here the URL namespace's
        Assert.Matches("^\\{[0-9A-F]{8}-[0-9A-F]{4}-5[0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}\\}$", MsiPackager.NameGuid("x"));
    }

    [Fact]
    public void An_installation_becomes_a_package_with_its_files_registry_shortcuts_and_custom_actions()
    {
        string root = TestEnvironment.NewDirectory("msipkg");
        string install = Directory.CreateDirectory(Path.Combine(root, "install")).FullName;
        Directory.CreateDirectory(Path.Combine(install, "Bin"));
        File.WriteAllText(Path.Combine(install, "Bin", "App.exe"), "not really a program");
        File.WriteAllText(Path.Combine(install, "Bin", "App.dll"), "library");
        File.WriteAllText(Path.Combine(install, "readme with a long name.txt"), "hello");
        string runtimePack = Path.Combine(root, "desktop-win-arm64");
        Directory.CreateDirectory(Path.Combine(runtimePack, "runtime", "launcher"));
        File.WriteAllText(Path.Combine(runtimePack, "runtime", "wpfrunw.exe"), "launcher");
        File.WriteAllText(Path.Combine(runtimePack, "runtime", "launcher", "wpfrun.dll"), "wpfrun");
        string setupPack = Directory.CreateDirectory(Path.Combine(root, "setup-win-arm64")).FullName;
        File.WriteAllBytes(Path.Combine(setupPack, "Setup.exe"), Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray());
        string registry = Directory.CreateDirectory(Path.Combine(root, "registry")).FullName;
        File.WriteAllText(Path.Combine(registry, "HKEY_LOCAL_MACHINE.reg"),
            "# HKEY_LOCAL_MACHINE\n[SOFTWARE\\Vendor\\Product]\n\"Path\"=str:${WPFRUN_INSTALLDIR}\\Bin\n\"Count\"=dword:00000002\n\"Big\"=qword:0000000100000000\n[SOFTWARE\\Vendor\\Empty]\n");
        string data = Path.Combine(root, "pd.txt");
        File.WriteAllText(data, "settings");

        var manifest = new PackageManifest
        {
            Inputs = new InputsSection { InstallDir = install },
            Product = new ProductSection { Name = "Product", Version = "2.1.0", Publisher = "Publisher", IdPrefix = "com.example" },
            Apps = { new AppEntry { Exe = "Bin/App.exe", Name = "The App", Kind = AppKind.Gui, Shortcut = true } },
        };
        var staged = new StagedState { RegistryDir = registry, ProgramDataFiles = new[] { (data, "Product/settings.txt") } };
        PackFile Pack(string kind, string dir) => new() { Kind = kind, Directory = dir, Info = new Dictionary<string, string> { ["rid"] = "win-arm64" } };
        var log = new List<string>();
        string out_ = Path.Combine(root, "out");
        string msiPath = MsiPackager.Build(manifest, staged, Pack("desktop-win-arm64", runtimePack), Pack("setup-win-arm64", setupPack), out_, log.Add);

        var msi = new MsiReader(File.ReadAllBytes(msiPath));
        var files = msi.Rows("File");
        var names = files.Select(f => (string)f[2]!).ToList();
        Assert.Contains("App.exe", names);
        Assert.Contains("README~1.TXT|readme with a long name.txt", names);
        Assert.Contains("wpfrun.env", names);
        Assert.Contains("SETUP~1.JSO|setup.json", names);
        Assert.Equal(Enumerable.Range(1, files.Count), files.Select(f => (int)f[7]!).Order());
        var props = msi.Rows("Property").ToDictionary(r => (string)r[0]!, r => (string?)r[1]);
        Assert.Equal("2.1.0", props["ProductVersion"]);
        Assert.Equal(MsiPackager.NameGuid("upgrade:com.example." + DesktopCommon.Segment("Product")), props["UpgradeCode"]);
        var reg = msi.Rows("Registry");
        Assert.Contains(reg, r => (string?)r[3] == "Path" && (string?)r[4] == "[WPFAPPDIR]\\Bin" && (int)r[1]! == 2);
        Assert.Contains(reg, r => (string?)r[3] == "Count" && (string?)r[4] == "#2");
        Assert.Contains(reg, r => (string)r[2]! == "SOFTWARE\\Vendor\\Empty" && (string?)r[3] == "+");
        Assert.DoesNotContain(reg, r => (string?)r[3] == "Big");
        // the ProgramData file and the registry values: never over the machine's, permanent
        var components = msi.Rows("Component").ToDictionary(c => (string)c[0]!);
        var pdFile = files.Single(f => (string)f[2]! == "settings.txt");
        Assert.Equal(0x190, components[(string)pdFile[1]!][3]);
        var shortcut = Assert.Single(msi.Rows("Shortcut"));
        Assert.Equal("The App", shortcut[6]);
        Assert.StartsWith("run \"[#", (string)shortcut[5]!);
        var actions = msi.Rows("CustomAction").ToDictionary(a => (string)a[0]!);
        Assert.Equal("/msi cleanup \"[INSTALLDIR].\" \"[KEEPDATA]\"", actions["WpfCleanup"][3]);
        Assert.Equal(2 | 0x400 | 0x800 | 0x40, actions["WpfCleanup"][1]);
        var sequence = msi.Rows("InstallExecuteSequence").ToDictionary(a => (string)a[0]!, a => (int)a[2]!);
        Assert.True(sequence["WpfRecord"] < sequence["InstallFiles"] && sequence["WpfInstall"] > sequence["WriteRegistryValues"]);
        // the cabinet inside, the custom actions' program with its payload
        var media = Assert.Single(msi.Rows("Media"));
        Assert.Equal("#data1.cab", media[3]);
        var cab = ReadCabinet(msi.Stream("data1.cab")!);
        Assert.Equal(files.Count, cab.Count);
        Assert.Equal("settings", Encoding.UTF8.GetString(cab.Single(c => c.Name == (string)pdFile[0]!).Data));
        string helper = Path.Combine(root, "helper.exe");
        File.WriteAllBytes(helper, msi.Stream("Binary.WpfSetup")!);
        using (var payload = Payload.Open(helper)!)
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
        {
            Assert.NotNull(zip.GetEntry("registry/HKEY_LOCAL_MACHINE.reg"));
            using var list = new StreamReader(zip.GetEntry("programdata.txt")!.Open());
            Assert.Equal("Product/settings.txt", list.ReadToEnd().Trim());
        }
        // the summary: Arm64, schema 500, compressed
        var summary = msi.File.Read(SummaryInformation.StreamName)!;
        Assert.Contains("Arm64;1033", Encoding.Latin1.GetString(summary));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_wizard_s_dialogs_hold_together(bool withLicense)
    {
        string root = TestEnvironment.NewDirectory("msiui");
        string install = Directory.CreateDirectory(Path.Combine(root, "install")).FullName;
        File.WriteAllText(Path.Combine(install, "readme.txt"), "hello");
        string runtimePack = Directory.CreateDirectory(Path.Combine(root, "desktop-win-arm64", "runtime")).Parent!.FullName;
        string setupPack = Directory.CreateDirectory(Path.Combine(root, "setup-win-arm64")).FullName;
        File.WriteAllBytes(Path.Combine(setupPack, "Setup.exe"), new byte[100]);
        string license = Path.Combine(root, "license.txt");
        File.WriteAllText(license, "Terms {of use}\nno\\warranty, café");
        var manifest = new PackageManifest
        {
            Inputs = new InputsSection { InstallDir = install },
            Product = new ProductSection { Name = "Product", Version = "1.0.0", License = withLicense ? license : "" },
        };
        PackFile Pack(string kind, string dir) => new() { Kind = kind, Directory = dir, Info = new Dictionary<string, string> { ["rid"] = "win-arm64" } };
        string msiPath = MsiPackager.Build(manifest, new StagedState { RegistryDir = Path.Combine(root, "none"), ProgramDataFiles = Array.Empty<(string, string)>() },
            Pack("desktop-win-arm64", runtimePack), Pack("setup-win-arm64", setupPack), Path.Combine(root, "out"), _ => { });

        var msi = new MsiReader(File.ReadAllBytes(msiPath));
        var dialogs = msi.Rows("Dialog").ToDictionary(d => (string)d[0]!);
        var controls = msi.Rows("Control").ToLookup(c => (string)c[0]!);
        Assert.Equal(withLicense, dialogs.ContainsKey("LicenseAgreementDlg"));
        foreach (var (name, d) in dialogs)
        {
            var mine = controls[name].ToDictionary(c => (string)c[1]!);
            Assert.Contains((string)d[7]!, mine.Keys);                           // its first control
            if (d[8] is string def) Assert.Contains(def, mine.Keys);
            if (d[9] is string cancel) Assert.Contains(cancel, mine.Keys);
            // the tab order: a cycle through every control in it
            var chained = mine.Values.Where(c => c[10] != null).ToList();
            string at = (string)chained[0][1]!;
            var seen = new HashSet<string>();
            while (seen.Add(at)) at = (string)mine[at][10]!;
            Assert.Equal(chained.Count, seen.Count);
        }
        foreach (var e in msi.Rows("ControlEvent"))
        {
            Assert.Contains((string)e[1]!, controls[(string)e[0]!].Select(c => (string)c[1]!));
            if ((string)e[2]! is "NewDialog" or "SpawnDialog") Assert.Contains((string)e[3]!, dialogs.Keys);
        }
        foreach (var c in msi.Rows("ControlCondition").Concat(msi.Rows("EventMapping")))
            Assert.Contains((string)c[1]!, controls[(string)c[0]!].Select(x => (string)x[1]!));
        var binaries = msi.Rows("Binary").Select(b => (string)b[0]!).ToHashSet();
        foreach (var c in controls.SelectMany(g => g).Where(c => (string)c[2]! is "Bitmap" or "Icon"))
            Assert.Contains((string)c[9]!, binaries);
        var ui = msi.Rows("InstallUISequence").ToDictionary(a => (string)a[0]!, a => (int)a[2]!);
        foreach (var (action, sequence) in ui.Where(a => a.Key.EndsWith("Dlg") || a.Key is "ExitDialog" or "UserExit" or "FatalError"))
            Assert.Contains(action, dialogs.Keys);
        Assert.True(ui["CostFinalize"] < ui["WelcomeDlg"] && ui["ProgressDlg"] < ui["ExecuteAction"]);
        var props = msi.Rows("Property").ToDictionary(r => (string)r[0]!, r => (string?)r[1]);
        Assert.Equal("ErrorDlg", props["ErrorDialog"]);
        Assert.Contains("INSTALLDIR", props["SecureCustomProperties"]!.Split(';'));
        if (withLicense)
        {
            string rtf = (string)controls["LicenseAgreementDlg"].Single(c => (string)c[1]! == "LicenseText")[9]!;
            Assert.StartsWith(@"{\rtf1", rtf);
            Assert.Contains(@"Terms \{of use\}\par", rtf);
            Assert.Contains(@"no\\warranty, caf\u233?", rtf);
        }
    }
}
