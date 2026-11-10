// A Mac installation as Gatekeeper takes it from the internet: its code signed with the publisher's Developer ID
// (signing.developerIdCertificate, a keychain identity "Developer ID Application: Name (TEAM)") -- every Mach-O of
// the payload, then the app stubs' bundles, each with the hardened runtime and a secure time stamp, as notarization
// requires; the programs with the entitlements .NET needs under the hardened runtime (its JIT's executable memory, the
// runtime's native libraries and an app's own, which are not the publisher's) -- the package signed as Developer ID
// Installer (signing.developerIdInstaller), and the whole notarized by Apple (signing.notaryKey: an App Store
// Connect API key, AuthKey_<id>.p8, with notaryKeyId and notaryIssuer) and the ticket stapled to it, so that it
// installs on a Mac that is offline too. codesign, notarytool and stapler come with macOS's command line tools.

namespace WpfWebGpu.Packager.Desktop;

public static partial class MacPackager
{
    private const string HardenedRuntimeEntitlements = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>com.apple.security.cs.allow-jit</key><true/>
          <key>com.apple.security.cs.allow-unsigned-executable-memory</key><true/>
          <key>com.apple.security.cs.disable-library-validation</key><true/>
          <key>com.apple.security.cs.allow-dyld-environment-variables</key><true/>
        </dict>
        </plist>
        """;

    /// <summary>Signs the payload's code with the Developer ID: the Mach-O files outside bundles (deepest first), then
    /// the bundles.</summary>
    private static void SignPayload(string payload, string identity, string work, Action<string> log)
    {
        string entitlements = Path.Combine(work, "hardened-runtime.entitlements");
        File.WriteAllText(entitlements, HardenedRuntimeEntitlements);
        // an app bundle: <name>.app with its Contents (not .NET's shared framework folders, Microsoft.AspNetCore.App,
        // which the file system's case-blind match also finds)
        var bundles = Directory.EnumerateDirectories(payload, "*.app", SearchOption.AllDirectories)
            .Where(d => d.EndsWith(".app", StringComparison.Ordinal) && File.Exists(Path.Combine(d, "Contents", "Info.plist"))).ToList();
        int signed = 0;
        foreach (string file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories)
                     .Where(f => !bundles.Any(b => f.StartsWith(b + "/", StringComparison.Ordinal)))
                     .OrderByDescending(f => f.Count(c => c == '/')))
        {
            if (MachOKind(file) is not { } kind) continue;
            var args = new List<string> { "--force", "--options", "runtime", "--timestamp", "--sign", identity };
            if (kind == MachOExecute) { args.Add("--entitlements"); args.Add(entitlements); }
            args.Add(file);
            Run("/usr/bin/codesign", args.ToArray());
            signed++;
        }
        foreach (string bundle in bundles.OrderByDescending(b => b.Length))
            Run("/usr/bin/codesign", "--force", "--options", "runtime", "--timestamp", "--sign", identity, "--entitlements", entitlements, bundle);
        log($"  signed: {signed} programs and libraries, {bundles.Count} apps ({identity})");
    }

    /// <summary>Has Apple notarize the package and staples the ticket to it.</summary>
    private static void Notarize(string pkg, SigningSection signing, Action<string> log)
    {
        if (signing.NotaryKeyId.Length == 0 || signing.NotaryIssuer.Length == 0)
            throw new PackagerError("signing.notaryKey needs signing.notaryKeyId and signing.notaryIssuer (App Store Connect's key id and issuer id)");
        if (!File.Exists(signing.NotaryKey)) throw new PackagerError($"no such key: {signing.NotaryKey}");
        log($"  notarizing {Path.GetFileName(pkg)} (Apple's notary service; this takes minutes)");
        string output = Capture("/usr/bin/xcrun", "notarytool", "submit", pkg, "--key", signing.NotaryKey, "--key-id", signing.NotaryKeyId,
            "--issuer", signing.NotaryIssuer, "--wait", "--output-format", "json");
        var result = System.Text.Json.JsonDocument.Parse(output).RootElement;
        string status = result.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
        if (status != "Accepted")
        {
            string id = result.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            string why = id.Length > 0 ? Capture("/usr/bin/xcrun", "notarytool", "log", id, "--key", signing.NotaryKey, "--key-id", signing.NotaryKeyId, "--issuer", signing.NotaryIssuer) : output;
            throw new PackagerError($"notarization: {status}\n{why}");
        }
        Run("/usr/bin/xcrun", "stapler", "staple", pkg);
        log("  notarized, the ticket stapled");
    }

    private const uint MachOExecute = 2;   // MH_EXECUTE

    // a Mach-O file's type (MH_EXECUTE, MH_DYLIB, ...), a universal one's first slice's; null: not Mach-O
    private static uint? MachOKind(string file)
    {
        try
        {
            using var s = File.OpenRead(file);
            var head = new byte[32];
            if (s.Read(head) < 16) return null;
            uint magic = BitConverter.ToUInt32(head, 0);
            if (magic is 0xFEEDFACF or 0xFEEDFACE) return BitConverter.ToUInt32(head, 12);
            if (magic == 0xBEBAFECA)   // FAT_MAGIC, big endian: the first slice's offset
            {
                uint offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(16));
                s.Position = offset;
                if (s.Read(head) < 16) return null;
                return BitConverter.ToUInt32(head, 0) is 0xFEEDFACF or 0xFEEDFACE ? BitConverter.ToUInt32(head, 12) : null;
            }
            return null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string Capture(string tool, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var error = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && output.Trim().Length == 0) throw new PackagerError($"{Path.GetFileName(tool)} {args[0]} failed: {error.Result.Trim()}");
        return output;
    }
}
