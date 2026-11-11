// An iOS or visionOS app bundle signed as codesign signs it, in managed code: the code it carries first (its loose
// libraries; its frameworks, each a bundle sealed in turn), then the seal of its files (_CodeSignature/CodeResources),
// then its executable, whose signature holds the seal's hash, the Info.plist's and the entitlements.
//
// The seal is codesign's for an iOS bundle (the flat layout: everything at the bundle's top): every file but the
// executable and the seal itself, by SHA-1 ("files", for old systems) and SHA-256 ("files2", without Info.plist and
// PkgInfo, which the executable's signature covers), under codesign's rules; a framework inside is sealed by its own
// signature -- its cdhash and its designated requirement -- not by its bytes.

using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace WpfWebGpu.Packager.Apple;

public static class BundleSigner
{
    /// <summary>Signs the bundle at <paramref name="app"/> ad hoc, with <paramref name="entitlements"/> (or none).</summary>
    public static void SignAdHoc(string app, PlistDict? entitlements)
    {
        byte[] infoPlist = File.ReadAllBytes(Path.Combine(app, "Info.plist"));
        var info = (PlistDict)Plist.Read(infoPlist);
        string executable = info["CFBundleExecutable"] as string ?? throw new PackagerError($"{app}: Info.plist names no executable");
        string identifier = info["CFBundleIdentifier"] as string ?? Path.GetFileNameWithoutExtension(app);

        // nested code first: frameworks (bundles of their own), then the loose libraries
        foreach (string framework in Directory.EnumerateDirectories(app, "*.framework", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
            SignAdHoc(framework, null);
        foreach (string library in Directory.EnumerateFiles(app, "*.dylib", SearchOption.AllDirectories))
            if (!IsInsideFramework(app, library))
                MachOSigner.SignAdHoc(library, new MachOSigner.Options { Identifier = Path.GetFileNameWithoutExtension(library) });

        byte[] seal = Seal(app, executable);
        Directory.CreateDirectory(Path.Combine(app, "_CodeSignature"));
        File.WriteAllBytes(Path.Combine(app, "_CodeSignature", "CodeResources"), seal);
        MachOSigner.SignAdHoc(Path.Combine(app, executable), new MachOSigner.Options
        {
            Identifier = identifier, InfoPlist = infoPlist, CodeResources = seal, Entitlements = entitlements,
            MainExecutable = !app.EndsWith(".framework", StringComparison.Ordinal),
        });
    }

    private static bool IsInsideFramework(string app, string path)
    {
        string rel = Path.GetRelativePath(app, path).Replace('\\', '/');
        return rel.Split('/').SkipLast(1).Any(part => part.EndsWith(".framework", StringComparison.Ordinal));
    }

    // ---- the seal ---------------------------------------------------------------------------------------------

    private sealed record Rule(string Pattern, double Weight, bool Omit, bool Optional, bool Nested)
    {
        public readonly Regex Regex = new(Pattern, RegexOptions.CultureInvariant);
    }

    // codesign's rules for an iOS bundle, as it writes them
    private static readonly Rule[] s_rules =
    {
        new("^.*", 1, false, false, false),
        new(@"^.*\.lproj/", 1000, false, true, false),
        new(@"^.*\.lproj/locversion.plist$", 1100, true, false, false),
        new(@"^Base\.lproj/", 1010, false, false, false),
        new(@"^version.plist$", 1, false, false, false),
    };

    private static readonly Rule[] s_rules2 =
    {
        new(@".*\.dSYM($|/)", 11, false, false, false),
        new(@"^(.*/)?\.DS_Store$", 2000, true, false, false),
        new("^.*", 1, false, false, false),
        new(@"^.*\.lproj/", 1000, false, true, false),
        new(@"^.*\.lproj/locversion.plist$", 1100, true, false, false),
        new(@"^Base\.lproj/", 1010, false, false, false),
        new(@"^Info\.plist$", 20, true, false, false),
        new(@"^PkgInfo$", 20, true, false, false),
        new(@"^embedded\.provisionprofile$", 20, false, false, false),
        new(@"^version\.plist$", 20, false, false, false),
    };

    private static Rule? Match(Rule[] rules, string path) =>
        rules.Where(r => r.Regex.IsMatch(path)).OrderByDescending(r => r.Weight).FirstOrDefault();

    /// <summary>The bundle's _CodeSignature/CodeResources: what it seals, under codesign's rules.</summary>
    internal static byte[] Seal(string app, string executable)
    {
        var files = new PlistDict();
        var files2 = new PlistDict();
        var entries = new List<string>();
        Collect(app, app, entries);
        foreach (string rel in entries.OrderBy(e => e, StringComparer.Ordinal))
        {
            if (rel == executable || rel.StartsWith("_CodeSignature/", StringComparison.Ordinal)) continue;
            string full = Path.Combine(app, rel);

            // a framework: sealed by its signature in files2 (and not at all, file by file, in either)
            if (rel.EndsWith(".framework", StringComparison.Ordinal) && Directory.Exists(full))
            {
                var fw = (PlistDict)Plist.Read(File.ReadAllBytes(Path.Combine(full, "Info.plist")));
                string binary = Path.Combine(full, (string)fw["CFBundleExecutable"]!);
                files2.Add(new(rel, new PlistDict
                {
                    new("cdhash", MachOSigner.CdHash(binary)[..20]),
                    new("requirement", $"cdhash H\"{Convert.ToHexString(MachOSigner.CdHash(binary)[..20]).ToLowerInvariant()}\""),
                }));
                continue;
            }

            FileInfo fi = new(full);
            if (fi.LinkTarget is string target)
            {
                if (Match(s_rules, rel) is { Omit: false }) files.Add(new(rel, new PlistDict { new("symlink", target) }));
                if (Match(s_rules2, rel) is { Omit: false }) files2.Add(new(rel, new PlistDict { new("symlink", target) }));
                continue;
            }

            byte[] data = File.ReadAllBytes(full);
            if (Match(s_rules, rel) is { Omit: false } r1)
                files.Add(new(rel, r1.Optional ? new PlistDict { new("hash", SHA1.HashData(data)), new("optional", true) } : SHA1.HashData(data)));
            if (Match(s_rules2, rel) is { Omit: false } r2)
            {
                var entry = new PlistDict { new("hash2", SHA256.HashData(data)) };
                if (r2.Optional) entry.Add(new("optional", true));
                files2.Add(new(rel, entry));
            }
        }

        return Plist.WriteXml(new PlistDict
        {
            new("files", files),
            new("files2", files2),
            new("rules", Rules(s_rules)),
            new("rules2", Rules(s_rules2)),
        });
    }

    // the files, links and frameworks of a bundle, paths relative to it; a framework is one entry, not its files
    private static void Collect(string root, string dir, List<string> into)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
        {
            string rel = Path.GetRelativePath(root, entry).Replace('\\', '/');
            var info = new FileInfo(entry);
            if (info.LinkTarget != null || File.Exists(entry)) { into.Add(rel); continue; }
            if (entry.EndsWith(".framework", StringComparison.Ordinal)) { into.Add(rel); continue; }
            Collect(root, entry, into);
        }
    }

    private static PlistDict Rules(Rule[] rules)
    {
        var dict = new PlistDict();
        foreach (Rule r in rules)
        {
            if (!r.Omit && !r.Optional && r.Weight == 1) { dict.Add(new(r.Pattern, true)); continue; }
            var d = new PlistDict();
            if (r.Omit) d.Add(new("omit", true));
            if (r.Optional) d.Add(new("optional", true));
            d.Add(new("weight", r.Weight));
            dict.Add(new(r.Pattern, d));
        }
        return dict;
    }
}
