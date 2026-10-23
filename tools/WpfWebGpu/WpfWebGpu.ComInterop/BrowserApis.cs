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
//                           puts there, and app names its client by it.
//   ServicePointManager     the connection limits and policies of System.Net.Requests, which the browser
//                           has no part of: the values an app sets and reads back.

using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;

namespace WpfWebGpu.ComInterop;

public static class BrowserApis
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

#pragma warning disable SYSLIB0014
    // ServicePointManager: what a desktop process starts with, as set and read back
    private static int s_connectionLimit = 2, s_maxIdle = 100_000, s_dnsTimeout = 120_000;
    private static bool s_expect100 = true, s_nagle = true, s_checkRevocation;
    private static SecurityProtocolType s_protocol = SecurityProtocolType.SystemDefault;
    private static RemoteCertificateValidationCallback? s_serverValidation;

    public static int ServicePointManager_get_DefaultConnectionLimit() => OperatingSystem.IsBrowser() ? s_connectionLimit : ServicePointManager.DefaultConnectionLimit;
    public static void ServicePointManager_set_DefaultConnectionLimit(int value) { if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); if (OperatingSystem.IsBrowser()) s_connectionLimit = value; else ServicePointManager.DefaultConnectionLimit = value; }
    public static int ServicePointManager_get_MaxServicePointIdleTime() => OperatingSystem.IsBrowser() ? s_maxIdle : ServicePointManager.MaxServicePointIdleTime;
    public static void ServicePointManager_set_MaxServicePointIdleTime(int value) { if (OperatingSystem.IsBrowser()) s_maxIdle = value; else ServicePointManager.MaxServicePointIdleTime = value; }
    public static int ServicePointManager_get_DnsRefreshTimeout() => OperatingSystem.IsBrowser() ? s_dnsTimeout : ServicePointManager.DnsRefreshTimeout;
    public static void ServicePointManager_set_DnsRefreshTimeout(int value) { if (OperatingSystem.IsBrowser()) s_dnsTimeout = value; else ServicePointManager.DnsRefreshTimeout = value; }
    public static bool ServicePointManager_get_Expect100Continue() => OperatingSystem.IsBrowser() ? s_expect100 : ServicePointManager.Expect100Continue;
    public static void ServicePointManager_set_Expect100Continue(bool value) { if (OperatingSystem.IsBrowser()) s_expect100 = value; else ServicePointManager.Expect100Continue = value; }
    public static bool ServicePointManager_get_UseNagleAlgorithm() => OperatingSystem.IsBrowser() ? s_nagle : ServicePointManager.UseNagleAlgorithm;
    public static void ServicePointManager_set_UseNagleAlgorithm(bool value) { if (OperatingSystem.IsBrowser()) s_nagle = value; else ServicePointManager.UseNagleAlgorithm = value; }
    public static bool ServicePointManager_get_CheckCertificateRevocationList() => OperatingSystem.IsBrowser() ? s_checkRevocation : ServicePointManager.CheckCertificateRevocationList;
    public static void ServicePointManager_set_CheckCertificateRevocationList(bool value) { if (OperatingSystem.IsBrowser()) s_checkRevocation = value; else ServicePointManager.CheckCertificateRevocationList = value; }
    public static SecurityProtocolType ServicePointManager_get_SecurityProtocol() => OperatingSystem.IsBrowser() ? s_protocol : ServicePointManager.SecurityProtocol;
    public static void ServicePointManager_set_SecurityProtocol(SecurityProtocolType value) { if (OperatingSystem.IsBrowser()) s_protocol = value; else ServicePointManager.SecurityProtocol = value; }
    public static RemoteCertificateValidationCallback? ServicePointManager_get_ServerCertificateValidationCallback() => OperatingSystem.IsBrowser() ? s_serverValidation : ServicePointManager.ServerCertificateValidationCallback;
    public static void ServicePointManager_set_ServerCertificateValidationCallback(RemoteCertificateValidationCallback? value) { if (OperatingSystem.IsBrowser()) s_serverValidation = value; else ServicePointManager.ServerCertificateValidationCallback = value; }
#pragma warning restore SYSLIB0014
}
