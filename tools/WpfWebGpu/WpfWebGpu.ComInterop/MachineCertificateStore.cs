// The machine's certificate stores (StoreLocation.LocalMachine) a system has none of: Windows keeps one of each
// name for the machine -- My, the personal store an HTTPS binding takes its certificate from, among them -- which
// every account opens; .NET on Linux opens only Root and CertificateAuthority there, and an app that lists the
// machine's certificates (to offer one for its web server) ended with "Unix LocalMachine X509Store is limited to
// the Root and CertificateAuthority stores". A store the system refuses is the machine's folder of that name
// instead (<state>/certificates/LocalMachine/<name>), kept as .NET keeps a user's stores -- the same provider,
// pointed at the machine's folder -- so every account of the machine opens the same store. Adding to it is the
// account's that made it (root: an administrator, as Windows lets only one write the machine's stores).
//
// Where the system has the store (Windows; macOS's keychains) the store is the system's, untouched.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WpfWebGpu.ComInterop;

internal static class MachineCertificateStore
{
    /// <summary>Opens the store as X509Store.Open does; a machine's store the system refuses, from the machine's folder.</summary>
    public static void Open(X509Store store, OpenFlags flags)
    {
        try
        {
            store.Open(flags);
            return;
        }
        catch (CryptographicException e) when (store.Location == StoreLocation.LocalMachine && e.InnerException is PlatformNotSupportedException
            && s_storePal != null && s_provider != null)
        {
        }
        s_storePal.SetValue(store, Provider(Folder(store.Name ?? "My"), flags));
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
        object provider = RuntimeHelpers.GetUninitializedObject(s_provider!);
        s_provider!.GetField("_storePath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(provider, folder);
        s_provider.GetField("_readOnly", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(provider, (flags & (OpenFlags.ReadWrite | OpenFlags.MaxAllowed)) == 0);
        return provider;
    }
}
