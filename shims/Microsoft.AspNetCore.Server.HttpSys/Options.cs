// HttpSysOptions and what hangs off it, as ASP.NET Core has them, with http.sys's defaults. What Kestrel has a
// counterpart for (body size, connections, timeouts, synchronous IO, Latin-1 headers) is applied to it; the rest
// (the request queue, kernel caching, delegation) is http.sys's own and is kept but has nothing to act on.

using System;
using System.Security.AccessControl;

namespace Microsoft.AspNetCore.Server.HttpSys
{
    public class HttpSysOptions
    {
        private const long DefaultRequestQueueLength = 1000;
        private const long DefaultMaxRequestBodySize = 30_000_000;
        private static readonly int DefaultMaxAccepts = 5 * Environment.ProcessorCount;

        private long _requestQueueLength = DefaultRequestQueueLength;
        private long? _maxConnections;
        private long? _maxRequestBodySize = DefaultMaxRequestBodySize;
        private Http503VerbosityLevel _rejectionVerbosityLevel = Http503VerbosityLevel.Basic;

        public HttpSysOptions() { }

        public string? RequestQueueName { get; set; }
        public RequestQueueMode RequestQueueMode { get; set; }
        public GenericSecurityDescriptor? RequestQueueSecurityDescriptor { get; set; }
        public ClientCertificateMethod ClientCertificateMethod { get; set; } = ClientCertificateMethod.NoCertificate;
        public int MaxAccepts { get; set; } = DefaultMaxAccepts;
        public bool EnableResponseCaching { get; set; } = true;
        public UrlPrefixCollection UrlPrefixes { get; } = new UrlPrefixCollection();
        public AuthenticationManager Authentication { get; } = new AuthenticationManager();
        public TimeoutManager Timeouts { get; } = new TimeoutManager();
        public bool ThrowWriteExceptions { get; set; }
        public bool EnableKernelResponseBuffering { get; set; }
        public bool AllowSynchronousIO { get; set; }
        public bool UseLatin1RequestHeaders { get; set; }
        public bool UnsafePreferInlineScheduling { get; set; }

        public long? MaxConnections
        {
            get => _maxConnections;
            set
            {
                if (value.HasValue && value < -1) throw new ArgumentOutOfRangeException(nameof(value), value, "The value must be positive, or -1 for infinite.");
                _maxConnections = value;
            }
        }

        public long RequestQueueLimit
        {
            get => _requestQueueLength;
            set
            {
                if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), value, "The value must be greater than zero.");
                _requestQueueLength = value;
            }
        }

        public long? MaxRequestBodySize
        {
            get => _maxRequestBodySize;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), value, "The value must be greater or equal to zero.");
                _maxRequestBodySize = value;
            }
        }

        public Http503VerbosityLevel Http503Verbosity
        {
            get => _rejectionVerbosityLevel;
            set
            {
                if (value < Http503VerbosityLevel.Basic || value > Http503VerbosityLevel.Full)
                    throw new ArgumentOutOfRangeException(nameof(value), value, "The value is not a defined verbosity level.");
                _rejectionVerbosityLevel = value;
            }
        }
    }

    public sealed class AuthenticationManager
    {
        private AuthenticationSchemes _authSchemes;

        internal AuthenticationManager() { }

        public AuthenticationSchemes Schemes
        {
            get => _authSchemes;
            set
            {
                if (value.HasFlag(AuthenticationSchemes.Kerberos))
                    throw new ArgumentException("Value Kerberos is obsolete, use Negotiate instead.", nameof(value));
                _authSchemes = value;
            }
        }

        public bool AllowAnonymous { get; set; } = true;
        public bool AutomaticAuthentication { get; set; } = true;
        public string? AuthenticationDisplayName { get; set; }
        public bool EnableKerberosCredentialCaching { get; set; }
        public bool CaptureCredentials { get; set; }
    }

    public sealed class TimeoutManager
    {
        private TimeSpan _entityBody, _drainEntityBody, _requestQueue, _idleConnection, _headerWait;
        private long _minSendBytesPerSecond = 150;          // http.sys's own default

        internal TimeoutManager() { }

        public TimeSpan EntityBody { get => _entityBody; set => _entityBody = Checked(value); }
        public TimeSpan DrainEntityBody { get => _drainEntityBody; set => _drainEntityBody = Checked(value); }
        public TimeSpan RequestQueue { get => _requestQueue; set => _requestQueue = Checked(value); }
        public TimeSpan IdleConnection { get => _idleConnection; set => _idleConnection = Checked(value); }
        public TimeSpan HeaderWait { get => _headerWait; set => _headerWait = Checked(value); }

        public long MinSendBytesPerSecond
        {
            get => _minSendBytesPerSecond;
            set
            {
                if (value < 0 || value > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
                _minSendBytesPerSecond = value;
            }
        }

        // in seconds, as http.sys takes them: up to 65535, zero its default
        private static TimeSpan Checked(TimeSpan value)
        {
            long seconds = (long)value.TotalSeconds;
            if (seconds < 0 || seconds > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
            return value;
        }
    }

    [Flags]
    public enum AuthenticationSchemes
    {
        None = 0,
        Basic = 1,
        NTLM = 4,
        Negotiate = 8,
        Kerberos = 16,
    }

    public enum ClientCertificateMethod
    {
        NoCertificate = 0,
        AllowCertificate = 1,
        AllowRenegotation = 2,
    }

    public enum Http503VerbosityLevel : long
    {
        Basic = 0,
        Limited = 1,
        Full = 2,
    }

    public enum RequestQueueMode
    {
        Create = 0,
        Attach = 1,
        CreateOrAttach = 2,
    }

    public enum HttpSysRequestTimingType
    {
        ConnectionStart = 0,
        DataStart = 1,
        TlsCertificateLoadStart = 2,
        TlsCertificateLoadEnd = 3,
        TlsHandshakeLeg1Start = 4,
        TlsHandshakeLeg1End = 5,
        TlsHandshakeLeg2Start = 6,
        TlsHandshakeLeg2End = 7,
        TlsAttributesQueryStart = 8,
        TlsAttributesQueryEnd = 9,
        TlsClientCertQueryStart = 10,
        TlsClientCertQueryEnd = 11,
        Http2StreamStart = 12,
        Http2HeaderDecodeStart = 13,
        Http2HeaderDecodeEnd = 14,
        RequestHeaderParseStart = 15,
        RequestHeaderParseEnd = 16,
        RequestRoutingStart = 17,
        RequestRoutingEnd = 18,
        RequestQueuedForInspection = 19,
        RequestDeliveredForInspection = 20,
        RequestReturnedAfterInspection = 21,
        RequestQueuedForDelegation = 22,
        RequestDeliveredForDelegation = 23,
        RequestReturnedAfterDelegation = 24,
        RequestQueuedForIO = 25,
        RequestDeliveredForIO = 26,
        Http3StreamStart = 27,
        Http3HeaderDecodeStart = 28,
        Http3HeaderDecodeEnd = 29,
    }

    public static class HttpSysDefaults
    {
        public const string AuthenticationScheme = "Windows";
    }

    public class HttpSysException : System.ComponentModel.Win32Exception
    {
        internal HttpSysException() : base() { }
        internal HttpSysException(int errorCode) : base(errorCode) { }
        internal HttpSysException(int errorCode, string message) : base(errorCode, message) { }

        public override int ErrorCode => NativeErrorCode;
    }

    /// <summary>A rule passing requests on to another process's request queue: there is no other queue here.</summary>
    public class DelegationRule : IDisposable
    {
        internal DelegationRule(string queueName, string urlPrefix)
        {
            QueueName = queueName;
            UrlPrefix = urlPrefix;
        }

        public string QueueName { get; }
        public string UrlPrefix { get; }
        public void Dispose() { }
    }

    public interface IHttpSysRequestDelegationFeature
    {
        bool CanDelegate { get; }
        void DelegateRequest(DelegationRule destination);
    }

    public interface IHttpSysRequestInfoFeature
    {
        System.Collections.Generic.IReadOnlyDictionary<int, ReadOnlyMemory<byte>> RequestInfo { get; }
    }

    public interface IHttpSysRequestPropertyFeature
    {
        bool TryGetTlsClientHello(Span<byte> tlsClientHelloBytesDestination, out int bytesReturned);
    }

    public interface IHttpSysRequestTimingFeature
    {
        ReadOnlySpan<long> Timestamps { get; }
        bool TryGetTimestamp(HttpSysRequestTimingType timestampType, out long timestamp);
        bool TryGetElapsedTime(HttpSysRequestTimingType startingTimestampType, HttpSysRequestTimingType endingTimestampType, out TimeSpan elapsed);
    }

    public interface IServerDelegationFeature
    {
        DelegationRule CreateDelegationRule(string queueName, string urlPrefix);
    }
}
