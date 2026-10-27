// WebRequest: the factory by URI scheme (http and https, file, ftp, and whatever an app registers) and the
// base whose members a subclass that does not override them reports as not implemented, as .NET's does.

using System.Collections.Generic;
using System.IO;
using System.Net.Cache;
using System.Net.Security;
using System.Runtime.Serialization;
using System.Security.Principal;
using System.Threading.Tasks;

using System.ComponentModel;

namespace System.Net
{
    public abstract class WebRequest : MarshalByRefObject, ISerializable
    {
        private static readonly object s_gate = new object();
        private static List<KeyValuePair<string, IWebRequestCreate>> s_prefixes;
        private static IWebProxy s_defaultProxy;
        private static bool s_defaultProxySet;
        private static RequestCachePolicy s_defaultCachePolicy = new RequestCachePolicy(RequestCacheLevel.BypassCache);

        private RequestCachePolicy _cachePolicy;

        [Obsolete("WebRequest, HttpWebRequest, ServicePoint, and WebClient are obsolete. Use HttpClient instead.", DiagnosticId = "SYSLIB0014", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        protected WebRequest()
        {
            AuthenticationLevel = AuthenticationLevel.MutualAuthRequested;
            ImpersonationLevel = TokenImpersonationLevel.None;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("WebRequest, HttpWebRequest, ServicePoint, and WebClient are obsolete. Use HttpClient instead.", DiagnosticId = "SYSLIB0014", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        protected WebRequest(SerializationInfo serializationInfo, StreamingContext streamingContext) => throw new PlatformNotSupportedException();

        [Obsolete("Serialization has been deprecated for WebRequest.")]
        void ISerializable.GetObjectData(SerializationInfo serializationInfo, StreamingContext streamingContext) => throw new PlatformNotSupportedException();

        [Obsolete("Serialization has been deprecated for WebRequest.")]
        protected virtual void GetObjectData(SerializationInfo serializationInfo, StreamingContext streamingContext) => throw new PlatformNotSupportedException();

        internal static NotImplementedException PropertyNotImplemented() => new NotImplementedException("This property is not implemented by this class.");

        internal static NotImplementedException MethodNotImplemented() => new NotImplementedException("This method is not implemented by this class.");

        // ---- the factory ----------------------------------------------------------------------------

        private sealed class Creator : IWebRequestCreate
        {
            private readonly Func<Uri, WebRequest> _create;
            public Creator(Func<Uri, WebRequest> create) => _create = create;
            public WebRequest Create(Uri uri) => _create(uri);
        }

        private static List<KeyValuePair<string, IWebRequestCreate>> Prefixes
        {
            get
            {
                if (s_prefixes == null)
                {
                    var http = new Creator(u => new HttpWebRequest(u));
                    s_prefixes = new List<KeyValuePair<string, IWebRequestCreate>>
                    {
                        new KeyValuePair<string, IWebRequestCreate>("http:", http),
                        new KeyValuePair<string, IWebRequestCreate>("https:", http),
                        new KeyValuePair<string, IWebRequestCreate>("file:", new Creator(u => new FileWebRequest(u))),
                        new KeyValuePair<string, IWebRequestCreate>("ftp:", new Creator(u => new FtpWebRequest(u))),
                    };
                }
                return s_prefixes;
            }
        }

        [Obsolete("WebRequest, HttpWebRequest, ServicePoint, and WebClient are obsolete. Use HttpClient instead.", DiagnosticId = "SYSLIB0014", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        public static WebRequest Create(string requestUriString)
        {
            ArgumentNullException.ThrowIfNull(requestUriString);
            return Create(new Uri(requestUriString), useUriBase: false);
        }

        [Obsolete("WebRequest, HttpWebRequest, ServicePoint, and WebClient are obsolete. Use HttpClient instead.", DiagnosticId = "SYSLIB0014", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        public static WebRequest Create(Uri requestUri)
        {
            ArgumentNullException.ThrowIfNull(requestUri);
            return Create(requestUri, useUriBase: false);
        }

        [Obsolete("WebRequest, HttpWebRequest, ServicePoint, and WebClient are obsolete. Use HttpClient instead.", DiagnosticId = "SYSLIB0014", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        public static WebRequest CreateDefault(Uri requestUri)
        {
            ArgumentNullException.ThrowIfNull(requestUri);
            return Create(requestUri, useUriBase: true);
        }

        [Obsolete("WebRequest, HttpWebRequest, ServicePoint, and WebClient are obsolete. Use HttpClient instead.", DiagnosticId = "SYSLIB0014", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        public static HttpWebRequest CreateHttp(string requestUriString)
        {
            ArgumentNullException.ThrowIfNull(requestUriString);
            return CreateHttp(new Uri(requestUriString));
        }

        [Obsolete("WebRequest, HttpWebRequest, ServicePoint, and WebClient are obsolete. Use HttpClient instead.", DiagnosticId = "SYSLIB0014", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        public static HttpWebRequest CreateHttp(Uri requestUri)
        {
            ArgumentNullException.ThrowIfNull(requestUri);
            if (requestUri.Scheme != Uri.UriSchemeHttp && requestUri.Scheme != Uri.UriSchemeHttps) throw UnknownPrefix();
            return new HttpWebRequest(requestUri);
        }

        // the longest registered prefix of the URI (of its scheme alone for CreateDefault)
        private static WebRequest Create(Uri requestUri, bool useUriBase)
        {
            string lookup = useUriBase ? requestUri.Scheme + ":" : requestUri.AbsoluteUri;
            IWebRequestCreate creator = null;
            int best = -1;
            lock (s_gate)
            {
                foreach (KeyValuePair<string, IWebRequestCreate> p in Prefixes)
                {
                    if (p.Key.Length > best && lookup.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        creator = p.Value;
                        best = p.Key.Length;
                    }
                }
            }
            if (creator == null) throw UnknownPrefix();
            return creator.Create(requestUri);
        }

        private static NotSupportedException UnknownPrefix() => new NotSupportedException("The URI prefix is not recognized.");

        public static bool RegisterPrefix(string prefix, IWebRequestCreate creator)
        {
            ArgumentNullException.ThrowIfNull(prefix);
            ArgumentNullException.ThrowIfNull(creator);
            lock (s_gate)
            {
                if (Uri.TryCreate(prefix, UriKind.Absolute, out Uri uri))
                {
                    string normalized = uri.AbsoluteUri;
                    // a prefix given as a scheme and host ("http://contoso.com") is matched as its absolute URI
                    if (!prefix.EndsWith('/') && normalized.EndsWith('/')) normalized = normalized.Substring(0, normalized.Length - 1);
                    prefix = normalized;
                }
                List<KeyValuePair<string, IWebRequestCreate>> list = Prefixes;
                foreach (KeyValuePair<string, IWebRequestCreate> p in list)
                    if (string.Equals(p.Key, prefix, StringComparison.OrdinalIgnoreCase)) return false;
                var copy = new List<KeyValuePair<string, IWebRequestCreate>>(list) { new KeyValuePair<string, IWebRequestCreate>(prefix, creator) };
                s_prefixes = copy;
                return true;
            }
        }

        // ---- process-wide defaults --------------------------------------------------------------------

        public static IWebProxy DefaultWebProxy
        {
            get
            {
                lock (s_gate)
                {
                    if (!s_defaultProxySet)
                    {
                        s_defaultProxy = GetSystemWebProxy();
                        s_defaultProxySet = true;
                    }
                    return s_defaultProxy;
                }
            }
            set
            {
                lock (s_gate)
                {
                    s_defaultProxy = value;
                    s_defaultProxySet = true;
                }
            }
        }

        /// <summary>The system's proxy, as HttpClient finds it; a page has none of its own to name (the
        /// browser applies its own), and is answered with one that sends everything directly.</summary>
        public static IWebProxy GetSystemWebProxy()
        {
            try
            {
                return System.Net.Http.HttpClient.DefaultProxy;
            }
            catch (PlatformNotSupportedException)
            {
                return GlobalProxySelection.GetEmptyWebProxy();
            }
        }

        public static RequestCachePolicy DefaultCachePolicy
        {
            get => s_defaultCachePolicy;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                s_defaultCachePolicy = value;
            }
        }

        // ---- members a subclass provides ----------------------------------------------------------------

        public AuthenticationLevel AuthenticationLevel { get; set; }

        public TokenImpersonationLevel ImpersonationLevel { get; set; }

        public virtual RequestCachePolicy CachePolicy
        {
            get => _cachePolicy;
            set => _cachePolicy = value;
        }

        public virtual string ConnectionGroupName { get => throw PropertyNotImplemented(); set => throw PropertyNotImplemented(); }
        public virtual long ContentLength { get => throw PropertyNotImplemented(); set => throw PropertyNotImplemented(); }
        public virtual string ContentType { get => throw PropertyNotImplemented(); set => throw PropertyNotImplemented(); }
        [System.Diagnostics.CodeAnalysis.DisallowNull]
        public virtual ICredentials Credentials { get => throw PropertyNotImplemented(); set => throw PropertyNotImplemented(); }
        public virtual WebHeaderCollection Headers { get => throw PropertyNotImplemented(); set => throw PropertyNotImplemented(); }
        public virtual string Method { get => throw PropertyNotImplemented(); set => throw PropertyNotImplemented(); }
        public virtual bool PreAuthenticate { get => throw PropertyNotImplemented(); set => throw PropertyNotImplemented(); }
        public virtual IWebProxy Proxy { get => throw PropertyNotImplemented(); set => throw PropertyNotImplemented(); }
        public virtual Uri RequestUri => throw PropertyNotImplemented();
        public virtual int Timeout { get => throw PropertyNotImplemented(); set => throw PropertyNotImplemented(); }
        public virtual bool UseDefaultCredentials { get => throw PropertyNotImplemented(); set => throw PropertyNotImplemented(); }

        public virtual void Abort() => throw MethodNotImplemented();
        public virtual IAsyncResult BeginGetRequestStream(AsyncCallback callback, object state) => throw MethodNotImplemented();
        public virtual IAsyncResult BeginGetResponse(AsyncCallback callback, object state) => throw MethodNotImplemented();
        public virtual Stream EndGetRequestStream(IAsyncResult asyncResult) => throw MethodNotImplemented();
        public virtual WebResponse EndGetResponse(IAsyncResult asyncResult) => throw MethodNotImplemented();
        public virtual Stream GetRequestStream() => throw MethodNotImplemented();
        public virtual WebResponse GetResponse() => throw MethodNotImplemented();

        public virtual Task<Stream> GetRequestStreamAsync()
            => Task.Factory.FromAsync((callback, state) => ((WebRequest)state).BeginGetRequestStream(callback, state),
                iar => ((WebRequest)iar.AsyncState).EndGetRequestStream(iar), this);

        public virtual Task<WebResponse> GetResponseAsync()
            => Task.Factory.FromAsync((callback, state) => ((WebRequest)state).BeginGetResponse(callback, state),
                iar => ((WebRequest)iar.AsyncState).EndGetResponse(iar), this);
    }
}
