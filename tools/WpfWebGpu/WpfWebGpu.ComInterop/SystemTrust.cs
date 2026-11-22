// A certificate added to the machine's trusted roots (LocalMachine\Root), trusted as Windows trusts it: by every program
// of the machine, a browser's and .NET's TLS among them. Windows' machine store is its trust; off Windows the machine's
// stores are the installation's (MachineCertificateStore), and a root added there is also given to the system's own
// trust, which only an administrator may change -- as a setup adds it on Windows, elevated:
//
//   macOS   the System keychain, as a trusted root: added there as an administrator (only one may write it), then
//           trusted as the user (security add-trusted-cert -d), whom macOS asks to confirm -- it changes the
//           administrators' trust settings only so; as root, with no one to confirm, it refuses
//   Linux   the system's CA bundle (Debian's /usr/local/share/ca-certificates and update-ca-certificates, Fedora's
//           /etc/pki/ca-trust/source/anchors and update-ca-trust), polkit asking for an administrator's password
//
// Root itself does it without asking. A certificate the system trusts already is left as it is; one removed from the
// machine's trusted roots is taken out of the system's trust as well.

using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

namespace WpfWebGpu.ComInterop;

internal static class SystemTrust
{
    private const string Keychain = "/Library/Keychains/System.keychain";

    /// <summary>For tests: what is given the system's trust (true) and taken out of it (false), in place of the system.</summary>
    internal static Action<X509Certificate2, bool>? Override;

    public static void Trust(X509Certificate2 cert)
    {
        if (Override != null) { Override(cert, true); return; }
        if (OperatingSystem.IsWindows() || IsTrusted(cert)) return;
        string file = Path.Combine(Path.GetTempPath(), "wpfwebgpu-trust-" + cert.Thumbprint + (OperatingSystem.IsMacOS() ? ".cer" : ".crt"));
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                File.WriteAllBytes(file, cert.Export(X509ContentType.Cert));
                // into the System keychain, the machine's, which only an administrator may write ("Write permissions
                // error" as the user)...
                if (!InSystemKeychain(cert))
                    AsAdministrator($"/usr/bin/security add-certificates -k {Q(Keychain)} {Q(file)}",
                        $"{Program()} wants to add the certificate “{Name(cert)}” to this Mac.");
                // ...then trusted as the user, whom macOS asks to confirm (its own prompt); it keeps a copy in the user's
                // keychain, taken out again where the user had none
                string login = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Keychains", "login.keychain-db");
                bool hadCopy = InKeychain(cert, login);
                var (code, output) = Run("/usr/bin/security", "add-trusted-cert", "-d", "-r", "trustRoot", file);
                if (!hadCopy && InKeychain(cert, login)) Run("/usr/bin/security", "delete-certificate", "-Z", cert.Thumbprint, login);
                if (code != 0 || !IsTrusted(cert))
                {
                    Com.LogOnce("trust:" + cert.Thumbprint, $"the certificate “{Name(cert)}” was not made trusted: {output.Trim()}");
                    throw new System.Security.Cryptography.CryptographicException($"Access is denied: the certificate was not made trusted ({output.Trim()}).");
                }
            }
            else if (LinuxBundle() is { } bundle)
            {
                File.WriteAllText(file, cert.ExportCertificatePem() + "\n");
                AsAdministrator($"cp {Q(file)} {Q(Path.Combine(bundle.Folder, AnchorName(cert)))} && chmod 644 {Q(Path.Combine(bundle.Folder, AnchorName(cert)))} && {bundle.Update}",
                    $"{Program()} wants to make the certificate “{Name(cert)}” trusted on this computer.");
            }
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    public static void Untrust(X509Certificate2 cert)
    {
        if (Override != null) { Override(cert, false); return; }
        if (OperatingSystem.IsWindows()) return;
        string file = Path.Combine(Path.GetTempPath(), "wpfwebgpu-untrust-" + cert.Thumbprint + ".cer");
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                if (!InSystemKeychain(cert)) return;
                File.WriteAllBytes(file, cert.Export(X509ContentType.Cert));
                // its trust taken away as it was given (the user confirming), then the certificate out of the keychain
                Run("/usr/bin/security", "remove-trusted-cert", "-d", file);
                AsAdministrator($"/usr/bin/security delete-certificate -Z {cert.Thumbprint} {Q(Keychain)}",
                    $"{Program()} wants to remove the certificate “{Name(cert)}” from this Mac.");
            }
            else if (LinuxBundle() is { } bundle && File.Exists(Path.Combine(bundle.Folder, AnchorName(cert))))
            {
                AsAdministrator($"rm -f {Q(Path.Combine(bundle.Folder, AnchorName(cert)))} && {bundle.Update} --fresh 2>/dev/null || {bundle.Update}",
                    $"{Program()} wants to remove the certificate “{Name(cert)}” from this computer's trusted certificates.");
            }
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    // trusted already: a chain of it alone (a self-signed root) that the system accepts
    private static bool IsTrusted(X509Certificate2 cert)
    {
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
            return chain.Build(cert);
        }
        catch (Exception) { return false; }
    }

    private static bool InSystemKeychain(X509Certificate2 cert) => InKeychain(cert, Keychain);

    private static bool InKeychain(X509Certificate2 cert, string keychain)
    {
        var (code, output) = Run("/usr/bin/security", "find-certificate", "-a", "-Z", keychain);
        return code == 0 && output.Contains(cert.Thumbprint, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Folder, string Update)? LinuxBundle()
    {
        if (File.Exists("/usr/sbin/update-ca-certificates") || File.Exists("/usr/bin/update-ca-certificates"))
            return ("/usr/local/share/ca-certificates", "update-ca-certificates");
        if (File.Exists("/usr/bin/update-ca-trust"))
            return ("/etc/pki/ca-trust/source/anchors", "update-ca-trust extract");
        return null;
    }

    private static string AnchorName(X509Certificate2 cert) => "wpfwebgpu-" + cert.Thumbprint.ToLowerInvariant() + ".crt";

    private static string Name(X509Certificate2 cert) => cert.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } n ? n : cert.Subject;

    private static string Program() =>
        Environment.GetEnvironmentVariable("WPFRUN_APP_NAME") is { Length: > 0 } app ? app : Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "A program");

    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    // the command as an administrator: as it is where this is root; else the system's elevation, which asks (macOS:
    // Authorization Services through AppleScript; Linux: polkit's pkexec)
    private static void AsAdministrator(string command, string why)
    {
        int code;
        string output;
        if (WindowsAccountsUid() == 0) (code, output) = Run("/bin/sh", "-c", command);
        else if (OperatingSystem.IsMacOS())
        {
            string script = $"do shell script \"{command.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" with prompt \"{why.Replace("\"", "\\\"")}\" with administrator privileges";
            (code, output) = Run("/usr/bin/osascript", "-e", script);
        }
        else if (new[] { "/usr/bin/pkexec", "/bin/pkexec" }.FirstOrDefault(File.Exists) is { } pkexec)
            (code, output) = Run(pkexec, "/bin/sh", "-c", command);
        else
            throw new System.Security.Cryptography.CryptographicException("Access is denied: no way to ask for an administrator (pkexec) to trust the certificate.");
        if (code != 0)
            throw new System.Security.Cryptography.CryptographicException($"Access is denied: the certificate's trust was not changed ({output.Trim()}).");
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint WindowsAccountsUid();

    private static (int Code, string Output) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var error = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output + error.Result);
    }
}
