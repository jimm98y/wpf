using System.Diagnostics;
using WpfWebGpu.Packager.Desktop;
using Xunit;

namespace WpfWebGpu.Tests;

// The component package written in place of pkgbuild's, read back by macOS's own tools: pkgutil expands its payload
// (pbzx, compressed or stored) and lsbom reads its Bom.
public class MacComponentTests
{
    private static string Tree()
    {
        string root = TestEnvironment.NewDirectory("pkgroot");
        string app = Path.Combine(root, "Library", "Application Support", "Product", "app");
        Directory.CreateDirectory(Path.Combine(app, "Empty folder"));
        File.WriteAllText(Path.Combine(app, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(app, "run.sh"), "#!/bin/sh\necho hi\n");
        File.SetUnixFileMode(Path.Combine(app, "run.sh"), (UnixFileMode)Convert.ToInt32("755", 8));
        // more than a chunk, half of it compressible, half not: chunks of both kinds
        var random = new Random(7);
        byte[] big = new byte[20 * 1024 * 1024];
        random.NextBytes(big.AsSpan(0, 12 * 1024 * 1024));
        File.WriteAllBytes(Path.Combine(app, "big.bin"), big);
        File.CreateSymbolicLink(Path.Combine(app, "link.txt"), "a.txt");
        string bundle = Path.Combine(root, "Applications", "Product", "Tool.app", "Contents");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "Info.plist"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
            <key>CFBundleIdentifier</key><string>com.example.product.tool</string>
            <key>CFBundleShortVersionString</key><string>1.2.3</string>
            <key>CFBundleVersion</key><string>1.2.3</string>
            </dict></plist>
            """);
        return root;
    }

    private static string Scripts()
    {
        string dir = TestEnvironment.NewDirectory("pkgscripts");
        File.WriteAllText(Path.Combine(dir, "postinstall"), "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(Path.Combine(dir, "postinstall"), (UnixFileMode)Convert.ToInt32("755", 8));
        return dir;
    }

    private static string Tool(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{tool} {string.Join(' ', args)}: {output}{error}");
        return output;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Its_package_expands_to_the_tree_it_was_made_of(bool compress)
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "a macOS package is made on a Mac");
        string root = Tree(), scripts = Scripts();
        string pkg = Path.Combine(TestEnvironment.NewDirectory("pkgout"), "component.pkg");
        MacComponent.Write(root, scripts, "com.example.product", "1.2.3", compress, pkg, _ => { });

        // the payload one gzip stream, its CRC and length right (gzip -t checks both)
        string flat = TestEnvironment.NewDirectory("pkgflat");
        Tool("/usr/bin/xar", "-xf", pkg, "-C", flat, "Payload");
        Tool("/usr/bin/gzip", "-t", "-S", "", Path.Combine(flat, "Payload"));

        string expanded = Path.Combine(TestEnvironment.NewDirectory("pkgexpand"), "x");
        Tool("/usr/sbin/pkgutil", "--expand-full", pkg, expanded);
        string payload = Path.Combine(expanded, "Payload");
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file);
            var info = new FileInfo(file);
            var got = new FileInfo(Path.Combine(payload, rel));
            if (info.LinkTarget != null)
            {
                Assert.Equal(info.LinkTarget, got.LinkTarget);
                continue;
            }
            Assert.True(File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(got.FullName)), rel);
            Assert.Equal(info.UnixFileMode, got.UnixFileMode);
        }
        Assert.True(Directory.Exists(Path.Combine(payload, "Library", "Application Support", "Product", "app", "Empty folder")));
        Assert.True(File.Exists(Path.Combine(expanded, "Scripts", "postinstall")));

        string info2 = File.ReadAllText(Path.Combine(expanded, "PackageInfo"));
        Assert.Contains("<bundle path=\"./Applications/Product/Tool.app\" id=\"com.example.product.tool\"", info2);
        Assert.Contains("<postinstall file=\"./postinstall\"", info2);
        Assert.DoesNotContain("<preinstall", info2);
        Assert.Contains("numberOfFiles=\"15\"", info2);

        // the Bom: every entry root:wheel, each file's cksum what cksum(1) says
        string bom = Tool("/usr/bin/lsbom", Path.Combine(expanded, "Bom"));
        string bigLine = bom.Split('\n').Single(l => l.StartsWith("./Library/Application Support/Product/app/big.bin\t", StringComparison.Ordinal));
        string[] fields = bigLine.Split('\t');
        Assert.Equal("100644", fields[1]);
        Assert.Equal("0/0", fields[2]);
        string cksum = Tool("/usr/bin/cksum", Path.Combine(root, "Library", "Application Support", "Product", "app", "big.bin")).Split(' ')[0];
        Assert.Equal(cksum, fields[4]);
        Assert.Contains("./Applications/Product/Tool.app/Contents/Info.plist\t", bom);
    }
}
