// The rest of System.Net.Requests, as .NET's own has them: authentication modules are no more (HttpClient
// authenticates; registering one is accepted, asking one to authenticate is not supported), Authorization
// is the plain value it always was, GlobalProxySelection is WebRequest.DefaultWebProxy by its older name,
// and HttpRequestCachePolicy describes a cache that HttpClient does not keep.

using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Net.Cache;

namespace System.Net
{
    [Obsolete("AuthenticationManager is not supported. Methods will no-op or throw PlatformNotSupportedException.", DiagnosticId = "SYSLIB0009", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
    public class AuthenticationManager
    {
        private AuthenticationManager() { }

        public static ICredentialPolicy CredentialPolicy { get; set; }

        public static StringDictionary CustomTargetNameDictionary { get; } = new StringDictionary();

        public static IEnumerator RegisteredModules => Array.Empty<IAuthenticationModule>().GetEnumerator();

        public static Authorization Authenticate(string challenge, WebRequest request, ICredentials credentials) => throw new PlatformNotSupportedException();

        public static Authorization PreAuthenticate(WebRequest request, ICredentials credentials) => throw new PlatformNotSupportedException();

        public static void Register(IAuthenticationModule authenticationModule) => ArgumentNullException.ThrowIfNull(authenticationModule);

        public static void Unregister(IAuthenticationModule authenticationModule) => ArgumentNullException.ThrowIfNull(authenticationModule);

        public static void Unregister(string authenticationScheme) => ArgumentNullException.ThrowIfNull(authenticationScheme);
    }

    public class Authorization
    {
        private string[] _protectionRealm;

        public Authorization(string token) : this(token, true) { }

        public Authorization(string token, bool finished) : this(token, finished, null) { }

        public Authorization(string token, bool finished, string connectionGroupId)
        {
            Message = string.IsNullOrEmpty(token) ? null : token;
            Complete = finished;
            ConnectionGroupId = string.IsNullOrEmpty(connectionGroupId) ? null : connectionGroupId;
        }

        public string Message { get; }
        public string ConnectionGroupId { get; }
        public bool Complete { get; }
        public bool MutuallyAuthenticated { get; set; }

        public string[] ProtectionRealm
        {
            get => _protectionRealm;
            set => _protectionRealm = value != null && value.Length != 0 ? value : null;
        }
    }

    [Obsolete("GlobalProxySelection has been deprecated. Use WebRequest.DefaultWebProxy instead to access and set the global default proxy. Use 'null' instead of GetEmptyWebProxy.")]
    public class GlobalProxySelection
    {
        private sealed class EmptyWebProxy : IWebProxy
        {
            public ICredentials Credentials { get; set; }
            public Uri GetProxy(Uri uri) => uri;
            public bool IsBypassed(Uri uri) => true;
        }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public static IWebProxy Select
        {
            get => WebRequest.DefaultWebProxy ?? GetEmptyWebProxy();
            set => WebRequest.DefaultWebProxy = value;
        }

        public static IWebProxy GetEmptyWebProxy() => new EmptyWebProxy();
    }
}

namespace System.Net.Cache
{
    public class HttpRequestCachePolicy : RequestCachePolicy
    {
        private readonly HttpRequestCacheLevel _level;
        private readonly TimeSpan _maxAge = TimeSpan.MaxValue, _minFresh = TimeSpan.MinValue, _maxStale = TimeSpan.MinValue;

        public HttpRequestCachePolicy() : this(HttpRequestCacheLevel.Default) { }

        public HttpRequestCachePolicy(HttpRequestCacheLevel level) : base(MapLevel(level)) => _level = level;

        public HttpRequestCachePolicy(HttpCacheAgeControl cacheAgeControl, TimeSpan ageOrFreshOrStale) : this(HttpRequestCacheLevel.Default)
        {
            switch (cacheAgeControl)
            {
                case HttpCacheAgeControl.MinFresh: _minFresh = ageOrFreshOrStale; break;
                case HttpCacheAgeControl.MaxAge: _maxAge = ageOrFreshOrStale; break;
                case HttpCacheAgeControl.MaxStale: _maxStale = ageOrFreshOrStale; break;
                default: throw new ArgumentException($"The specified value is not valid in the 'HttpCacheAgeControl' enumeration.", nameof(cacheAgeControl));
            }
        }

        public HttpRequestCachePolicy(HttpCacheAgeControl cacheAgeControl, TimeSpan maxAge, TimeSpan freshOrStale) : this(HttpRequestCacheLevel.Default)
        {
            switch (cacheAgeControl)
            {
                case HttpCacheAgeControl.MinFresh: _minFresh = freshOrStale; break;
                case HttpCacheAgeControl.MaxAge: _maxAge = maxAge; break;
                case HttpCacheAgeControl.MaxStale: _maxStale = freshOrStale; break;
                case HttpCacheAgeControl.MaxAgeAndMinFresh: _maxAge = maxAge; _minFresh = freshOrStale; break;
                case HttpCacheAgeControl.MaxAgeAndMaxStale: _maxAge = maxAge; _maxStale = freshOrStale; break;
                default: throw new ArgumentException($"The specified value is not valid in the 'HttpCacheAgeControl' enumeration.", nameof(cacheAgeControl));
            }
        }

        public HttpRequestCachePolicy(HttpCacheAgeControl cacheAgeControl, TimeSpan maxAge, TimeSpan freshOrStale, DateTime cacheSyncDate)
            : this(cacheAgeControl, maxAge, freshOrStale) => CacheSyncDate = cacheSyncDate;

        public HttpRequestCachePolicy(DateTime cacheSyncDate) : this(HttpRequestCacheLevel.Default) => CacheSyncDate = cacheSyncDate;

        public new HttpRequestCacheLevel Level => _level;
        public DateTime CacheSyncDate { get; }
        public TimeSpan MaxAge => _maxAge;
        public TimeSpan MinFresh => _minFresh;
        public TimeSpan MaxStale => _maxStale;

        private static RequestCacheLevel MapLevel(HttpRequestCacheLevel level)
        {
            if (level <= HttpRequestCacheLevel.NoCacheNoStore) return (RequestCacheLevel)level;
            if (level == HttpRequestCacheLevel.CacheOrNextCacheOnly) return RequestCacheLevel.CacheOnly;
            if (level == HttpRequestCacheLevel.Refresh) return RequestCacheLevel.Reload;
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        public override string ToString()
            => "Level:" + _level
               + (_maxAge == TimeSpan.MaxValue ? string.Empty : " MaxAge:" + _maxAge)
               + (_minFresh == TimeSpan.MinValue ? string.Empty : " MinFresh:" + _minFresh)
               + (_maxStale == TimeSpan.MinValue ? string.Empty : " MaxStale:" + _maxStale)
               + (CacheSyncDate == DateTime.MinValue ? string.Empty : " CacheSyncDate:" + CacheSyncDate.ToString(CultureInfo.CurrentCulture));
    }
}
