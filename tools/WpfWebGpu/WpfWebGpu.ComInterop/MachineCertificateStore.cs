// The machine's certificate stores (StoreLocation.LocalMachine) as Windows has them: one of each name for the machine --
// My, the personal store an HTTPS binding takes its certificate from, Root, the trusted roots -- which every program of
// the machine opens, and an administrator's setup adds to. Off Windows the machine's stores are the installation's: a
// folder of each store in the machine's state (<state>/certificates/LocalMachine/<name>, each certificate a PFX file, as
// .NET keeps a user's stores on Linux), so its programs -- a setup run as the user, its services, its http.sys -- see
// the same certificates. Linux's .NET opens only Root and CertificateAuthority there ("Unix LocalMachine X509Store is
// limited to the Root and CertificateAuthority stores"), macOS's opens the System keychain, which only root may add to
// (a setup run as the user ended with "Access is denied"). The system's trusted roots stay the system's to read: Root
// and CertificateAuthority opened to read are the system's; to add to, the installation's.
//
// The folder is FolderStorePal below on every system: .NET's own folder store is Linux's only (macOS's stores are
// keychains), and a root added to the machine's trusted roots is given the system's trust too (SystemTrust).

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WpfWebGpu.ComInterop;

public static class MachineCertificateStore
{
    /// <summary>Opens the store as X509Store.Open does; off Windows, a machine's store as the installation's (above).</summary>
    public static void Open(X509Store store, OpenFlags flags)
    {
        if (!OperatingSystem.IsWindows() && store.Location == StoreLocation.LocalMachine && s_storePal != null && Installation(store.Name, flags))
        {
            s_storePal.SetValue(store, Provider(Folder(store.Name ?? "My"), flags));
            return;
        }
        try
        {
            store.Open(flags);
            return;
        }
        catch (CryptographicException e) when (store.Location == StoreLocation.LocalMachine && e.InnerException is PlatformNotSupportedException && s_storePal != null)
        {
        }
        s_storePal.SetValue(store, Provider(Folder(store.Name ?? "My"), flags));
    }

    // the installation's store rather than the system's: any but the trusted roots, and those too to add to
    private static bool Installation(string? name, OpenFlags flags)
    {
        bool roots = string.Equals(name, "Root", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "CA", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "AuthRoot", StringComparison.OrdinalIgnoreCase);
        return !roots || (flags & (OpenFlags.ReadWrite | OpenFlags.MaxAllowed)) != 0;
    }

    /// <summary>The folder of the machine's store <paramref name="name"/>.</summary>
    internal static string Folder(string name)
    {
        string? state = AppContext.GetData("WPFWEBGPU_STATE_DIR") as string;
        if (string.IsNullOrEmpty(state)) state = Environment.GetEnvironmentVariable("WPFRUN_STATE");
        if (string.IsNullOrEmpty(state)) state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WpfWebGpu");
        return Path.Combine(state, "certificates", "LocalMachine", name.ToLowerInvariant());
    }

    private static readonly FieldInfo? s_storePal = typeof(X509Store).GetField("_storePal", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Type? s_provider = typeof(X509Store).Assembly.GetType("System.Security.Cryptography.X509Certificates.OpenSslDirectoryBasedStoreProvider");

    // .NET's store of a user's certificates (a folder of PFX files), made without its constructor (which takes the
    // user's own folder) and given the machine's
    // (the machine's stores exist on Windows from its installation: one opened OpenExistingOnly is there, empty until
    // a certificate is added)
    private static object Provider(string folder, OpenFlags flags)
    {
        bool readOnly = (flags & (OpenFlags.ReadWrite | OpenFlags.MaxAllowed)) == 0;
        // the store of the stack's own on every system (the same folder of PFX files as .NET's on Linux), which gives a
        // root added to the machine's trusted roots the system's trust (SystemTrust); .NET's own where it has no other
        if (FolderStorePal.Available || s_provider == null) return FolderStorePal.Create(folder, readOnly);
        object provider = RuntimeHelpers.GetUninitializedObject(s_provider!);
        s_provider!.GetField("_storePath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(provider, folder);
        s_provider.GetField("_readOnly", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(provider, (flags & (OpenFlags.ReadWrite | OpenFlags.MaxAllowed)) == 0);
        return provider;
    }
}

/// <summary>A store of a folder of PFX files (each certificate's, named by its thumbprint), as .NET's IStorePal -- an
/// interface of its own, implemented here by a DispatchProxy -- for a system whose .NET has no such store (macOS).</summary>
public class FolderStorePal : DispatchProxy
{
    private static readonly Type? s_iface = typeof(X509Store).Assembly.GetType("System.Security.Cryptography.X509Certificates.IStorePal");
    private static readonly ConstructorInfo? s_fromPal = typeof(X509Certificate2).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
        .FirstOrDefault(c => c.GetParameters() is [{ ParameterType.Name: "ICertificatePal" }]);

    private string _folder = "";
    private bool _readOnly;

    internal static bool Available => s_iface != null && s_fromPal != null;

    // the machine's trusted roots: what is added to them is trusted by the system too (SystemTrust)
    private bool Roots => Path.GetFileName(_folder) is "root" or "authroot";

    internal static object Create(string folder, bool readOnly)
    {
        if (s_iface == null || s_fromPal == null) throw new PlatformNotSupportedException("the machine's certificate store: this .NET has no store interface to implement");
        var pal = (FolderStorePal)Create(s_iface, typeof(FolderStorePal));
        pal._folder = folder;
        pal._readOnly = readOnly;
        return pal;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method?.Name)
        {
            case "CloneTo":
                var collection = (X509Certificate2Collection)args![0]!;
                if (!Directory.Exists(_folder)) return null;
                foreach (string file in Directory.EnumerateFiles(_folder, "*.pfx").Order(StringComparer.Ordinal))
                {
                    try { collection.Add(X509CertificateLoader.LoadPkcs12FromFile(file, null, X509KeyStorageFlags.Exportable)); }
                    catch (CryptographicException) { }
                }
                return null;
            case "Add":
            {
                if (_readOnly) throw new CryptographicException("The X509 certificate store is read-only.");
                // the caller's certificate (its PAL, shared: the wrapper is not disposed), kept with its key where it has one
                var cert = (X509Certificate2)s_fromPal!.Invoke(new[] { args![0] });
                // trusted by the system before it is the machine's trusted root (an administrator's consent asked for)
                if (Roots) SystemTrust.Trust(cert);
                Directory.CreateDirectory(_folder);
                byte[] pfx = cert.Export(X509ContentType.Pkcs12);
                string path = Path.Combine(_folder, cert.Thumbprint + ".pfx");
                // a certificate already there with its key is not replaced by one without
                if (!cert.HasPrivateKey && File.Exists(path)) return null;
                File.WriteAllBytes(path, pfx);
                return null;
            }
            case "Remove":
            {
                if (_readOnly) throw new CryptographicException("The X509 certificate store is read-only.");
                var cert = (X509Certificate2)s_fromPal!.Invoke(new[] { args![0] });
                if (Roots && File.Exists(Path.Combine(_folder, cert.Thumbprint + ".pfx"))) SystemTrust.Untrust(cert);
                File.Delete(Path.Combine(_folder, cert.Thumbprint + ".pfx"));
                return null;
            }
            case "get_SafeHandle":
                return null;
            case "Dispose":
                return null;
            default:
                throw new NotSupportedException(method?.Name);
        }
    }
}
