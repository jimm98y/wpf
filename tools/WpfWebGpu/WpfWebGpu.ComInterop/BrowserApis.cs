// Members .NET's browser build refuses (PlatformNotSupportedException) for settings the browser takes
// care of itself, which an app's calls to are rewritten to at build (BuildRewrite.cs, BrowserSubstitutions):
// in the browser each is accepted -- the value remembered, read back as it was set -- and does what the
// browser does in its place; anywhere else it is the member itself.
//
//   ClientWebSocketOptions  KeepAliveInterval (the browser answers pings), Credentials (it sends its
//                           own), RemoteCertificateValidationCallback (it validates certificates),
//                           ClientCertificates (it picks them). app's app client sets all
//                           of these before every connect.
//   Marshal                 GetFunctionPointerForDelegate, GetDelegateForFunctionPointer: a delegate an
//                           app makes a function pointer of for the Windows it calls (a window procedure
//                           it subclasses with), which Mono on WebAssembly cannot make a thunk for: its
//                           token (Callbacks), which the stack's Win32 calls the delegate for.
//   Environment             GetCommandLineArgs, CommandLine: what the app was started as. In a page the
//                           runtime's first argument is the page's host assembly; the app's own file
//                           (WPFWEBGPU_APP_PATH, set by the host that calls its Main) is what Windows
//                           puts there, and app names its client by it; OSVersion: Windows'.
//
// System.Net.Requests (ServicePointManager with it) and System.Net.WebClient are not stood in for member by
// member: the browser bundle carries working replacements of the assemblies (shims/).

using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;

namespace WpfWebGpu.ComInterop;

public static partial class BrowserApis
{
    private sealed class Options
    {
        public TimeSpan KeepAlive = WebSocket.DefaultKeepAliveInterval;
        public ICredentials? Credentials;
        public RemoteCertificateValidationCallback? Validation;
        public X509CertificateCollection Certificates = new();
    }

    private static readonly ConditionalWeakTable<ClientWebSocketOptions, Options> s_options = new();
    private static Options Of(ClientWebSocketOptions o) => s_options.GetValue(o, static _ => new Options());

    public static void ClientWebSocketOptions_set_KeepAliveInterval(ClientWebSocketOptions o, TimeSpan value)
    {
        if (OperatingSystem.IsBrowser()) Of(o).KeepAlive = value;
        else o.KeepAliveInterval = value;
    }

    public static TimeSpan ClientWebSocketOptions_get_KeepAliveInterval(ClientWebSocketOptions o)
        => OperatingSystem.IsBrowser() ? Of(o).KeepAlive : o.KeepAliveInterval;

    public static void ClientWebSocketOptions_set_Credentials(ClientWebSocketOptions o, ICredentials? value)
    {
        if (OperatingSystem.IsBrowser()) Of(o).Credentials = value;
        else o.Credentials = value;
    }

    public static ICredentials? ClientWebSocketOptions_get_Credentials(ClientWebSocketOptions o)
        => OperatingSystem.IsBrowser() ? Of(o).Credentials : o.Credentials;

    public static void ClientWebSocketOptions_set_RemoteCertificateValidationCallback(ClientWebSocketOptions o, RemoteCertificateValidationCallback? value)
    {
        if (OperatingSystem.IsBrowser()) Of(o).Validation = value;
        else o.RemoteCertificateValidationCallback = value;
    }

    public static RemoteCertificateValidationCallback? ClientWebSocketOptions_get_RemoteCertificateValidationCallback(ClientWebSocketOptions o)
        => OperatingSystem.IsBrowser() ? Of(o).Validation : o.RemoteCertificateValidationCallback;

    public static X509CertificateCollection ClientWebSocketOptions_get_ClientCertificates(ClientWebSocketOptions o)
        => OperatingSystem.IsBrowser() ? Of(o).Certificates : o.ClientCertificates;

    public static void ClientWebSocketOptions_set_ClientCertificates(ClientWebSocketOptions o, X509CertificateCollection value)
    {
        if (OperatingSystem.IsBrowser()) Of(o).Certificates = value ?? throw new ArgumentNullException(nameof(value));
        else o.ClientCertificates = value;
    }

    public static IntPtr Marshal_GetFunctionPointerForDelegate(Delegate d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return Callbacks.ToNative(d);
    }

    public static IntPtr Marshal_GetFunctionPointerForDelegate<TDelegate>(TDelegate d) where TDelegate : notnull
        => Marshal_GetFunctionPointerForDelegate((Delegate)(object)d);

    public static Delegate Marshal_GetDelegateForFunctionPointer(IntPtr ptr, Type t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (ptr == IntPtr.Zero) throw new ArgumentNullException(nameof(ptr));
        return Callbacks.FromNative(ptr, t)!;
    }

    public static TDelegate Marshal_GetDelegateForFunctionPointer<TDelegate>(IntPtr ptr)
        => (TDelegate)(object)Marshal_GetDelegateForFunctionPointer(ptr, typeof(TDelegate));

    public static string[] Environment_GetCommandLineArgs()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (!OperatingSystem.IsBrowser() || AppContext.GetData("WPFWEBGPU_APP_PATH") is not string app) return args;
        if (args.Length == 0) return new[] { app };
        var copy = (string[])args.Clone();
        copy[0] = app;
        return copy;
    }

    public static string Environment_get_CommandLine()
    {
        if (!OperatingSystem.IsBrowser() || AppContext.GetData("WPFWEBGPU_APP_PATH") is not string) return Environment.CommandLine;
        // as Windows' command line reads: each argument quoted where it needs to be
        return string.Join(" ", Environment_GetCommandLineArgs().Select(a =>
            a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\""));
    }

    // What .NET on Windows answers: Win32NT and the version the stack reports itself as everywhere
    // (Kernel32's GetVersionEx, Windows 11 23H2). The browser's build says "Other 1.0", and DotNetBar,
    // which wants Windows 2000 or later (Major >= 5) for its alpha-blended menu shadows, fell back to
    // the in-menu shadow of Windows 98: the border drawn two pixels in, white past it.
    private static readonly OperatingSystem s_windows = new(PlatformID.Win32NT, new Version(10, 0, 22631, 0));

    public static OperatingSystem Environment_get_OSVersion() => OperatingSystem.IsWindows() ? Environment.OSVersion : s_windows;

}
