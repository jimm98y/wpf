// ServicePoint and ServicePointManager: the connection settings of .NET Framework's HTTP stack. HttpClient
// (and a page's fetch) manages its own connections, so, as in .NET's own, they are kept and answered as set,
// with the same defaults, and found per scheme and authority.

using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace System.Net
{
    public class ServicePoint
    {
        private int _connectionLimit, _maxIdleTime, _receiveBufferSize = -1, _connectionLeaseTimeout = System.Threading.Timeout.Infinite;

        internal ServicePoint(Uri address)
        {
            Address = address;
            ConnectionName = address.Scheme;
            _connectionLimit = ServicePointManager.DefaultConnectionLimit;
            _maxIdleTime = ServicePointManager.MaxServicePointIdleTime;
            Expect100Continue = ServicePointManager.Expect100Continue;
            UseNagleAlgorithm = ServicePointManager.UseNagleAlgorithm;
            IdleSince = DateTime.Now;
        }

        public Uri Address { get; }
        public BindIPEndPoint BindIPEndPointDelegate { get; set; }
        public X509Certificate Certificate { get; internal set; }
        public X509Certificate ClientCertificate { get; internal set; }
        public string ConnectionName { get; }
        public int CurrentConnections => 0;
        public bool Expect100Continue { get; set; }
        public DateTime IdleSince { get; internal set; }
        public virtual Version ProtocolVersion => HttpVersion.Version11;
        public bool SupportsPipelining => true;
        public bool UseNagleAlgorithm { get; set; }

        public int ConnectionLeaseTimeout
        {
            get => _connectionLeaseTimeout;
            set
            {
                if (value < System.Threading.Timeout.Infinite) throw new ArgumentOutOfRangeException(nameof(value));
                _connectionLeaseTimeout = value;
            }
        }

        public int ConnectionLimit
        {
            get => _connectionLimit;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
                _connectionLimit = value;
            }
        }

        public int MaxIdleTime
        {
            get => _maxIdleTime;
            set
            {
                if (value < System.Threading.Timeout.Infinite) throw new ArgumentOutOfRangeException(nameof(value));
                _maxIdleTime = value;
            }
        }

        public int ReceiveBufferSize
        {
            get => _receiveBufferSize;
            set
            {
                if (value < -1) throw new ArgumentOutOfRangeException(nameof(value));
                _receiveBufferSize = value;
            }
        }

        public bool CloseConnectionGroup(string connectionGroupName) => false;

        public void SetTcpKeepAlive(bool enabled, int keepAliveTime, int keepAliveInterval) => ServicePointManager.CheckKeepAlive(enabled, keepAliveTime, keepAliveInterval);
    }

    [Obsolete("WebRequest, HttpWebRequest, ServicePoint, and WebClient are obsolete. Use HttpClient instead. Settings on ServicePointManager no longer affect SslStream or HttpClient.", DiagnosticId = "SYSLIB0014", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
    public class ServicePointManager
    {
        public const int DefaultNonPersistentConnectionLimit = 4;
        public const int DefaultPersistentConnectionLimit = 2;

        private static readonly ConcurrentDictionary<string, ServicePoint> s_points = new ConcurrentDictionary<string, ServicePoint>(StringComparer.OrdinalIgnoreCase);
        private static int s_defaultConnectionLimit = DefaultPersistentConnectionLimit;
        private static int s_maxServicePointIdleTime = 100_000;
        private static int s_maxServicePoints;
        private static int s_dnsRefreshTimeout = 120_000;
        private static SecurityProtocolType s_securityProtocol = SecurityProtocolType.SystemDefault;

        private ServicePointManager() { }

        public static SecurityProtocolType SecurityProtocol
        {
            get => s_securityProtocol;
            set
            {
                const SecurityProtocolType known = SecurityProtocolType.SystemDefault | SecurityProtocolType.Ssl3 | SecurityProtocolType.Tls
                    | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
                if ((value & ~known) != 0) throw new NotSupportedException("The requested security protocol is not supported.");
                s_securityProtocol = value;
            }
        }

        public static int MaxServicePoints
        {
            get => s_maxServicePoints;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                s_maxServicePoints = value;
            }
        }

        public static int DefaultConnectionLimit
        {
            get => s_defaultConnectionLimit;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
                s_defaultConnectionLimit = value;
            }
        }

        public static int MaxServicePointIdleTime
        {
            get => s_maxServicePointIdleTime;
            set
            {
                if (value < System.Threading.Timeout.Infinite) throw new ArgumentOutOfRangeException(nameof(value));
                s_maxServicePointIdleTime = value;
            }
        }

        public static int DnsRefreshTimeout
        {
            get => s_dnsRefreshTimeout;
            set => s_dnsRefreshTimeout = Math.Max(value, System.Threading.Timeout.Infinite);
        }

        public static bool UseNagleAlgorithm { get; set; }
        public static bool Expect100Continue { get; set; } = true;
        public static bool EnableDnsRoundRobin { get; set; }
        public static bool CheckCertificateRevocationList { get; set; }
        public static bool ReusePort { get; set; }
        [System.Runtime.Versioning.UnsupportedOSPlatform("browser")]
        public static EncryptionPolicy EncryptionPolicy => EncryptionPolicy.RequireEncryption;
        public static RemoteCertificateValidationCallback ServerCertificateValidationCallback { get; set; }

        public static ServicePoint FindServicePoint(Uri address) => FindServicePoint(address, null);

        public static ServicePoint FindServicePoint(string uriString, IWebProxy proxy) => FindServicePoint(new Uri(uriString), proxy);

        public static ServicePoint FindServicePoint(Uri address, IWebProxy proxy)
        {
            ArgumentNullException.ThrowIfNull(address);
            // through the proxy, the point is the proxy's
            Uri at = address;
            if (proxy != null && !proxy.IsBypassed(address) && proxy.GetProxy(address) is Uri viaProxy) at = viaProxy;
            return s_points.GetOrAdd(at.Scheme + "://" + at.Authority, _ => new ServicePoint(address));
        }

        public static void SetTcpKeepAlive(bool enabled, int keepAliveTime, int keepAliveInterval) => CheckKeepAlive(enabled, keepAliveTime, keepAliveInterval);

        internal static void CheckKeepAlive(bool enabled, int keepAliveTime, int keepAliveInterval)
        {
            if (!enabled) return;
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keepAliveTime);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keepAliveInterval);
        }
    }
}
