// A Mac installation as Gatekeeper takes it from the internet, signed in managed code on any machine (Apple's
// MachOSigner, the xar's signatures): every Mach-O of the payload with the publisher's Developer ID Application
// certificate (signing.developerIdCertificate, a .p12; its password WPFPKG_DEVELOPERID_PASSWORD) -- the hardened
// runtime and a secure time stamp from Apple's server, as notarization requires; the programs with the entitlements
// .NET needs under the hardened runtime (its JIT's executable memory, the runtime's native libraries and an app's
// own, which are not the publisher's) -- and the package with its Developer ID Installer certificate
// (signing.developerIdInstaller, a .p12; WPFPKG_DEVELOPERID_INSTALLER_PASSWORD). Notarized by Apple
// (signing.notaryKey: an App Store Connect API key, AuthKey_<id>.p8, with notaryKeyId and notaryIssuer) through
// notarytool, and the ticket stapled to it so that it installs on a Mac that is offline too: that is a Mac's.
//
// The app stubs (/Applications/<Product>/<App>.app) are shell scripts that start the signed runtime: a script's
// signature lives in its extended attributes, which a package's payload does not carry, so they are not signed --
// as Installer puts them there, they are not quarantined, and Gatekeeper does not assess them.

using WpfWebGpu.Packager.Apple;

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

    /// <summary>A Developer ID certificate's identity from its .p12 (its password the environment variable named), time
    /// stamped by Apple's server; null where none is named.</summary>
    private static MachOSigner.Identity? DeveloperId(string p12, string passwordVariable)
    {
        if (p12.Length == 0) return null;
        if (!p12.EndsWith(".p12", StringComparison.OrdinalIgnoreCase) && !p12.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase))
            throw new PackagerError($"{p12}: a Developer ID certificate is named by its .p12 (exported from the keychain with its private key)");
        var loaded = MachOSigner.Identity.Load(p12, Environment.GetEnvironmentVariable(passwordVariable));
        return new MachOSigner.Identity { Certificate = loaded.Certificate, Chain = loaded.Chain, TeamId = loaded.TeamId, TimestampUrl = Timestamps.Apple };
    }

    /// <summary>Signs every Mach-O of the payload with the Developer ID: its programs with the hardened runtime's
    /// entitlements, its libraries without.</summary>
    internal static void SignPayload(string payload, MachOSigner.Identity identity, Action<string> log)
    {
        var entitlements = (PlistDict)Plist.Read(System.Text.Encoding.UTF8.GetBytes(HardenedRuntimeEntitlements));
        int programs = 0, libraries = 0;
        foreach (string file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (MachOKind(file) is not { } kind) continue;
            // an app bundle's own code is sealed with its bundle, which the payload's are not (their executables scripts)
            string? bundle = BundleOf(payload, file);
            if (bundle != null && kind == MachOExecute)
                throw new PackagerError($"{Path.GetRelativePath(payload, bundle)}: an app bundle with a program of its own is not signed here");
            bool program = kind == MachOExecute;
            MachOSigner.Sign(file, new MachOSigner.Options
            {
                // codesign's identifier: the file's name without its extension
                Identifier = Path.GetFileNameWithoutExtension(file),
                HardenedRuntime = true,
                MainExecutable = program,
                Entitlements = program ? entitlements : null,
            }, identity);
            if (program) programs++; else libraries++;
        }
        log($"  signed: {programs} programs, {libraries} libraries ({identity.Certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false)}), time-stamped by Apple");
    }

    private static string? BundleOf(string payload, string file)
    {
        for (string? d = Path.GetDirectoryName(file); d != null && d.Length > payload.Length; d = Path.GetDirectoryName(d))
            if (d.EndsWith(".app", StringComparison.Ordinal) && File.Exists(Path.Combine(d, "Contents", "Info.plist"))) return d;
        return null;
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
