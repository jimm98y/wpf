// HttpWebRequest over HttpClient, as .NET's own is: the headers and the body an app sets are collected,
// and the request goes out when the response is asked for -- once; asking again hands back the same
// response. A status past 299 (399 without automatic redirects) is a WebException that carries the
// response. The synchronous calls wait for the asynchronous ones on the thread pool: in the browser the
// fetch completes on the page's own thread, so waiting on the app's does not stop it (HttpClient.Send,
// the synchronous send, is not there at all).
//
// What a browser does for itself is left to it -- cookies it keeps, the proxy, decompression, NTLM and
// Negotiate -- so those settings are kept and reported but do not change the request. Basic credentials
// are sent as Windows sends them: up front with PreAuthenticate, else when the server asks.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Cache;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Runtime.Serialization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using System.ComponentModel;

namespace System.Net
{
    public class HttpWebRequest : WebRequest, ISerializable
    {
        private static int s_defaultMaximumResponseHeadersLength = 64;
        private static int s_defaultMaximumErrorResponseLength = -1;
        private static RequestCachePolicy s_defaultCachePolicy = new RequestCachePolicy(RequestCacheLevel.BypassCache);

        private readonly Uri _requestUri;
        private Uri _address;
        private WebHeaderCollection _headers = new WebHeaderCollection();
        private string _method = "GET";
        private string _host;
        private string _mediaType;
        private int _timeout = 100_000, _readWriteTimeout = 300_000, _continueTimeout = 350;
        private int _maximumAutomaticRedirections = 50;
        private int _maximumResponseHeadersLength = s_defaultMaximumResponseHeadersLength;
        private bool _allowAutoRedirect = true, _allowWriteStreamBuffering = true, _keepAlive = true, _pipelined = true;
        private bool _allowReadStreamBuffering, _preAuthenticate, _sendChunked, _unsafeSharing;
        private DecompressionMethods _automaticDecompression;
        private Version _protocolVersion = HttpVersion.Version11;
        private ICredentials _credentials;
        private IWebProxy _proxy;
        private bool _proxySet;
        private CookieContainer _cookieContainer;
        private X509CertificateCollection _clientCertificates;
        private HttpContinueDelegate _continueDelegate;
        private RemoteCertificateValidationCallback _serverCertificateValidationCallback;
        private string _connectionGroupName;

        private readonly object _gate = new object();
        private readonly CancellationTokenSource _abort = new CancellationTokenSource();
        private RequestBuffer _body;
        private Task<WebResponse> _sending;
        private bool _submitted;

        internal HttpWebRequest(Uri uri)
        {
            _requestUri = uri;
            _address = uri;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("WebRequest, HttpWebRequest, ServicePoint, and WebClient are obsolete. Use HttpClient instead.", DiagnosticId = "SYSLIB0014", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        protected HttpWebRequest(SerializationInfo serializationInfo, StreamingContext streamingContext) => throw new PlatformNotSupportedException();

        [Obsolete("Serialization has been deprecated for HttpWebRequest.")]
        void ISerializable.GetObjectData(SerializationInfo serializationInfo, StreamingContext streamingContext) => throw new PlatformNotSupportedException();

        [Obsolete("Serialization has been deprecated for HttpWebRequest.")]
        protected override void GetObjectData(SerializationInfo serializationInfo, StreamingContext streamingContext) => throw new PlatformNotSupportedException();

        // ---- statics ---------------------------------------------------------------------------------

        public static int DefaultMaximumResponseHeadersLength
        {
            get => s_defaultMaximumResponseHeadersLength;
            set => s_defaultMaximumResponseHeadersLength = value;
        }

        public static int DefaultMaximumErrorResponseLength
        {
            get => s_defaultMaximumErrorResponseLength;
            set => s_defaultMaximumErrorResponseLength = value;
        }

        public static new RequestCachePolicy DefaultCachePolicy
        {
            get => s_defaultCachePolicy;
            set => s_defaultCachePolicy = value ?? new RequestCachePolicy(RequestCacheLevel.BypassCache);
        }

        // ---- the request -----------------------------------------------------------------------------

        // Once the response has been asked for, .NET's HttpWebRequest refuses some settings as submitted, some
        // as "writing has started", and takes the rest (the header properties among them) without effect.
        private void CheckNotSubmitted()
        {
            if (_submitted) throw new InvalidOperationException("This operation cannot be performed after the request has been submitted.");
        }

        private void CheckNotWriting()
        {
            if (_submitted) throw new InvalidOperationException("This property cannot be set after writing has started.");
        }

        public override Uri RequestUri => _requestUri;

        /// <summary>Where the request ended up: the request's URI until a redirect was followed.</summary>
        public Uri Address => _address;

        public virtual bool HaveResponse => _sending != null && _sending.IsCompletedSuccessfully;

        public override string Method
        {
            get => _method;
            set
            {
                if (string.IsNullOrEmpty(value)) throw new ArgumentException("The value cannot be an empty string.", nameof(value));
                foreach (char c in value)
                    if (c <= ' ' || c >= 127 || "()<>@,;:\\\"/[]?={}".IndexOf(c) >= 0)
                        throw new ArgumentException("The specified value is not a valid method.", nameof(value));
                _method = value;
            }
        }

        public override WebHeaderCollection Headers
        {
            get => _headers;
            set
            {
                CheckNotSubmitted();
                var copy = new WebHeaderCollection();
                if (value != null) foreach (string key in value.AllKeys) copy.Add(key, value[key]);
                _headers = copy;
            }
        }

        private string Header(string name) => _headers[name];

        private void SetHeader(string name, string value)
        {
            if (string.IsNullOrEmpty(value)) _headers.Remove(name);
            else _headers.Set(name, value);
        }

        public string Accept { get => Header("Accept"); set => SetHeader("Accept", value); }

        public string Referer { get => Header("Referer"); set => SetHeader("Referer", value); }

        public string UserAgent { get => Header("User-Agent"); set => SetHeader("User-Agent", value); }

        public override string ContentType { get => Header("Content-Type"); set => SetHeader("Content-Type", value); }

        public string Connection
        {
            get => Header("Connection");
            set
            {
                if (value != null && (value.Contains("keep-alive", StringComparison.OrdinalIgnoreCase) || value.Contains("close", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Keep-Alive and Close may not be set using this property.", nameof(value));
                SetHeader("Connection", value);
            }
        }

        public string Expect
        {
            get => Header("Expect");
            set
            {
                if (value != null && value.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("100-Continue may not be set using this property.", nameof(value));
                SetHeader("Expect", value);
            }
        }

        public string TransferEncoding
        {
            get => Header("Transfer-Encoding");
            set
            {
                if (!string.IsNullOrEmpty(value))
                {
                    if (value.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Chunked encoding must be set via the SendChunked property.", nameof(value));
                    if (!_sendChunked) throw new InvalidOperationException("TransferEncoding requires the SendChunked property to be set to true.");
                }
                SetHeader("Transfer-Encoding", value);
            }
        }

        public override long ContentLength
        {
            get => long.TryParse(Header("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out long n) ? n : 0;
            set
            {
                CheckNotWriting();
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _headers.Set("Content-Length", value.ToString(CultureInfo.InvariantCulture));
            }
        }

        public DateTime Date
        {
            get => DateHeader("Date");
            set => SetDateHeader("Date", value);
        }

        public DateTime IfModifiedSince
        {
            get => DateHeader("If-Modified-Since");
            set => SetDateHeader("If-Modified-Since", value);
        }

        private DateTime DateHeader(string name)
            => DateTime.TryParse(Header(name), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime d) ? d : DateTime.MinValue;

        private void SetDateHeader(string name, DateTime value)
            => SetHeader(name, value == DateTime.MinValue ? null : value.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture));

        public string Host
        {
            get => _host ?? (_requestUri.IsDefaultPort ? _requestUri.Host : _requestUri.Host + ":" + _requestUri.Port.ToString(CultureInfo.InvariantCulture));
            set
            {
                CheckNotWriting();
                ArgumentNullException.ThrowIfNull(value);
                _host = value;
            }
        }

        public string MediaType { get => _mediaType; set => _mediaType = value; }

        public void AddRange(int range) => AddRange("bytes", (long)range);
        public void AddRange(long range) => AddRange("bytes", range);
        public void AddRange(int from, int to) => AddRange("bytes", (long)from, to);
        public void AddRange(long from, long to) => AddRange("bytes", from, to);
        public void AddRange(string rangeSpecifier, int range) => AddRange(rangeSpecifier, (long)range);
        public void AddRange(string rangeSpecifier, int from, int to) => AddRange(rangeSpecifier, (long)from, to);

        public void AddRange(string rangeSpecifier, long range)
        {
            ArgumentNullException.ThrowIfNull(rangeSpecifier);
            AddRange(rangeSpecifier, range.ToString(CultureInfo.InvariantCulture) + (range >= 0 ? "-" : ""));
        }

        public void AddRange(string rangeSpecifier, long from, long to)
        {
            ArgumentNullException.ThrowIfNull(rangeSpecifier);
            if (from < 0 || to < 0) throw new ArgumentOutOfRangeException(from < 0 ? nameof(from) : nameof(to), "The specified value must be greater than 0.");
            if (from > to) throw new ArgumentOutOfRangeException(nameof(from), "The From parameter cannot be less than To.");
            AddRange(rangeSpecifier, from.ToString(CultureInfo.InvariantCulture) + "-" + to.ToString(CultureInfo.InvariantCulture));
        }

        private void AddRange(string unit, string range)
        {
            string current = Header("Range");
            if (string.IsNullOrEmpty(current)) SetHeader("Range", unit + "=" + range);
            else
            {
                if (!current.StartsWith(unit + "=", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("A different range specifier has already been added to this request.");
                SetHeader("Range", current + "," + range);
            }
        }

        public override int Timeout
        {
            get => _timeout;
            set
            {
                if (value < 0 && value != System.Threading.Timeout.Infinite) throw new ArgumentOutOfRangeException(nameof(value), "Timeout can be only be set to 'System.Threading.Timeout.Infinite' or a value >= 0.");
                _timeout = value;
            }
        }

        public int ReadWriteTimeout
        {
            get => _readWriteTimeout;
            set
            {
                if (value <= 0 && value != System.Threading.Timeout.Infinite) throw new ArgumentOutOfRangeException(nameof(value), "Timeout can be only be set to 'System.Threading.Timeout.Infinite' or a value > 0.");
                CheckNotSubmitted();
                _readWriteTimeout = value;
            }
        }

        public int ContinueTimeout
        {
            get => _continueTimeout;
            set
            {
                if (value < 0 && value != System.Threading.Timeout.Infinite) throw new ArgumentOutOfRangeException(nameof(value), "Timeout can be only be set to 'System.Threading.Timeout.Infinite' or a value >= 0.");
                CheckNotSubmitted();
                _continueTimeout = value;
            }
        }

        public virtual bool AllowAutoRedirect { get => _allowAutoRedirect; set => _allowAutoRedirect = value; }

        public int MaximumAutomaticRedirections
        {
            get => _maximumAutomaticRedirections;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
                _maximumAutomaticRedirections = value;
            }
        }

        public int MaximumResponseHeadersLength
        {
            get => _maximumResponseHeadersLength;
            set
            {
                if (value < 0 && value != System.Threading.Timeout.Infinite) throw new ArgumentOutOfRangeException(nameof(value));
                _maximumResponseHeadersLength = value;
            }
        }

        public virtual bool AllowReadStreamBuffering { get => _allowReadStreamBuffering; set => _allowReadStreamBuffering = value; }
        public virtual bool AllowWriteStreamBuffering { get => _allowWriteStreamBuffering; set => _allowWriteStreamBuffering = value; }
        public bool KeepAlive { get => _keepAlive; set => _keepAlive = value; }
        public bool Pipelined { get => _pipelined; set => _pipelined = value; }
        public bool SendChunked { get => _sendChunked; set { CheckNotWriting(); _sendChunked = value; } }
        public bool UnsafeAuthenticatedConnectionSharing { get => _unsafeSharing; set => _unsafeSharing = value; }
        public DecompressionMethods AutomaticDecompression { get => _automaticDecompression; set { CheckNotWriting(); _automaticDecompression = value; } }
        public override bool PreAuthenticate { get => _preAuthenticate; set => _preAuthenticate = value; }
        public override string ConnectionGroupName { get => _connectionGroupName; set => _connectionGroupName = value; }
        public HttpContinueDelegate ContinueDelegate { get => _continueDelegate; set => _continueDelegate = value; }
        public RemoteCertificateValidationCallback ServerCertificateValidationCallback { get => _serverCertificateValidationCallback; set => _serverCertificateValidationCallback = value; }

        public Version ProtocolVersion
        {
            get => _protocolVersion;
            set
            {
                if (value != HttpVersion.Version10 && value != HttpVersion.Version11) throw new ArgumentException("Only HTTP/1.0 and HTTP/1.1 version requests are currently supported.", nameof(value));
                _protocolVersion = value;
            }
        }

        public override ICredentials Credentials { get => _credentials; set => _credentials = value; }

        public override bool UseDefaultCredentials
        {
            get => _credentials == CredentialCache.DefaultCredentials;
            set
            {
                CheckNotWriting();
                _credentials = value ? CredentialCache.DefaultCredentials : null;
            }
        }

        public override IWebProxy Proxy
        {
            get => _proxySet ? _proxy : DefaultWebProxy;
            set
            {
                CheckNotSubmitted();
                _proxy = value;
                _proxySet = true;
            }
        }

        public virtual CookieContainer CookieContainer { get => _cookieContainer; set => _cookieContainer = value; }

        public virtual bool SupportsCookieContainer => true;

        public X509CertificateCollection ClientCertificates
        {
            get => _clientCertificates ??= new X509CertificateCollection();
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                _clientCertificates = value;
            }
        }

        public ServicePoint ServicePoint => ServicePointManager.FindServicePoint(_requestUri);

        // ---- the body ----------------------------------------------------------------------------------

        /// <summary>What the app writes, kept until the request goes out.</summary>
        private sealed class RequestBuffer : MemoryStream
        {
            public bool Closed;
            protected override void Dispose(bool disposing) => Closed = true;   // the bytes stay for the send
            public byte[] Bytes => ToArray();
        }

        public override Stream GetRequestStream() => GetRequestStream(out _);

        public Stream GetRequestStream(out TransportContext context)
        {
            context = null;
            if (_abort.IsCancellationRequested) throw Canceled();
            if (string.Equals(_method, "GET", StringComparison.OrdinalIgnoreCase) || string.Equals(_method, "HEAD", StringComparison.OrdinalIgnoreCase)
                || string.Equals(_method, "CONNECT", StringComparison.OrdinalIgnoreCase))
                throw new ProtocolViolationException("Cannot send a content-body with this verb-type.");
            lock (_gate)
            {
                if (_sending != null) throw new InvalidOperationException("This operation cannot be performed after the request has been submitted.");
                return _body ??= new RequestBuffer();
            }
        }

        public override IAsyncResult BeginGetRequestStream(AsyncCallback callback, object state)
            => TaskToAsyncResult.Begin(Task.FromResult(GetRequestStream()), callback, state);

        public override Stream EndGetRequestStream(IAsyncResult asyncResult) => EndGetRequestStream(asyncResult, out _);

        public Stream EndGetRequestStream(IAsyncResult asyncResult, out TransportContext context)
        {
            ArgumentNullException.ThrowIfNull(asyncResult);
            context = null;
            return TaskToAsyncResult.End<Stream>(asyncResult);
        }

        // ---- the response --------------------------------------------------------------------------------

        public override WebResponse GetResponse()
        {
            try
            {
                return Task.Run(Sending).GetAwaiter().GetResult();
            }
            catch (AggregateException e) when (e.InnerExceptions.Count == 1)
            {
                throw e.InnerException;
            }
        }

        public override IAsyncResult BeginGetResponse(AsyncCallback callback, object state) => TaskToAsyncResult.Begin(Sending(), callback, state);

        public override WebResponse EndGetResponse(IAsyncResult asyncResult)
        {
            ArgumentNullException.ThrowIfNull(asyncResult);
            return TaskToAsyncResult.End<WebResponse>(asyncResult);
        }

        public override void Abort()
        {
            _abort.Cancel();
            Task<WebResponse> sending;
            lock (_gate) sending = _sending;
            if (sending != null && sending.IsCompletedSuccessfully) sending.Result.Close();
        }

        private static WebException Canceled() => new WebException("The request was aborted: The request was canceled.", WebExceptionStatus.RequestCanceled);

        private Task<WebResponse> Sending()
        {
            lock (_gate)
            {
                _submitted = true;
                return _sending ??= SendAsync();
            }
        }

        private async Task<WebResponse> SendAsync()
        {
            await Task.Yield();     // the caller's thread is not the one the send runs on
            if (_abort.IsCancellationRequested) throw Canceled();
            using var timeout = new CancellationTokenSource();
            if (_timeout != System.Threading.Timeout.Infinite) timeout.CancelAfter(_timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _abort.Token);
            HttpResponseMessage message;
            try
            {
                message = await SendWithCredentialsAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_abort.IsCancellationRequested)
            {
                throw Canceled();
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new WebException("The operation has timed out.", WebExceptionStatus.Timeout);
            }
            catch (HttpRequestException e)
            {
                throw new WebException(e.Message, e, StatusOf(e), null);
            }

            HttpWebResponse response;
            try
            {
                Stream stream = await message.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                response = new HttpWebResponse(message, _requestUri, _cookieContainer, stream);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !_abort.IsCancellationRequested)
            {
                message.Dispose();
                throw new WebException("The operation has timed out.", WebExceptionStatus.Timeout);
            }
            catch (OperationCanceledException)
            {
                message.Dispose();
                throw Canceled();
            }
            _address = response.ResponseUri;

            int status = (int)response.StatusCode;
            if (status < 200 || status > (_allowAutoRedirect ? 299 : 399))
                throw new WebException($"The remote server returned an error: ({status}) {response.StatusDescription}.", null, WebExceptionStatus.ProtocolError, response);
            return response;
        }

        private static WebExceptionStatus StatusOf(HttpRequestException e) => e.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => WebExceptionStatus.NameResolutionFailure,
            HttpRequestError.SecureConnectionError => WebExceptionStatus.SecureChannelFailure,
            HttpRequestError.ProxyTunnelError => WebExceptionStatus.ProxyNameResolutionFailure,
            HttpRequestError.ResponseEnded => WebExceptionStatus.ReceiveFailure,
            HttpRequestError.InvalidResponse => WebExceptionStatus.ServerProtocolViolation,
            _ => WebExceptionStatus.UnknownError,
        };

        // Basic credentials: sent up front with PreAuthenticate, else once the server has asked for them
        private async Task<HttpResponseMessage> SendWithCredentialsAsync(CancellationToken cancel)
        {
            NetworkCredential basic = _credentials?.GetCredential(_requestUri, "Basic");
            if (basic != null && string.IsNullOrEmpty(basic.UserName)) basic = null;
            HttpClient client = ClientFor(_allowAutoRedirect, _maximumAutomaticRedirections, _automaticDecompression);
            string authority = _requestUri.GetLeftPart(UriPartial.Authority);
            bool upFront = basic != null && _preAuthenticate && s_basicAccepted.ContainsKey(authority);
            HttpResponseMessage response = await client.SendAsync(Build(upFront ? basic : null), HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && basic != null && !upFront
                && response.Headers.WwwAuthenticate.Any(h => string.Equals(h.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)))
            {
                response.Dispose();
                response = await client.SendAsync(Build(basic), HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
                if (_preAuthenticate && response.StatusCode != HttpStatusCode.Unauthorized) s_basicAccepted[authority] = true;
            }
            if (_cookieContainer != null)
            {
                Uri at = response.RequestMessage?.RequestUri ?? _requestUri;
                if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string> cookies))
                    foreach (string cookie in cookies)
                    {
                        try { _cookieContainer.SetCookies(at, cookie); }
                        catch (CookieException) { }
                    }
            }
            return response;
        }

        // where Basic credentials have been accepted: PreAuthenticate sends them there up front from then on
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_basicAccepted = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> s_contentHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Allow", "Content-Disposition", "Content-Encoding", "Content-Language", "Content-Length", "Content-Location",
            "Content-MD5", "Content-Range", "Content-Type", "Expires", "Last-Modified",
        };

        private HttpRequestMessage Build(NetworkCredential basic)
        {
            var request = new HttpRequestMessage(new HttpMethod(_method), _requestUri) { Version = _protocolVersion };
            byte[] body = _body?.Bytes;
            if (body != null || s_contentHeaders.Overlaps(_headers.AllKeys.Where(k => !string.Equals(k, "Content-Length", StringComparison.OrdinalIgnoreCase))))
                request.Content = new ByteArrayContent(body ?? Array.Empty<byte>());
            foreach (string name in _headers.AllKeys)
            {
                string value = _headers[name];
                if (s_contentHeaders.Contains(name))
                {
                    if (request.Content == null || string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    request.Content.Headers.Remove(name);
                    request.Content.Headers.TryAddWithoutValidation(name, value);
                }
                else request.Headers.TryAddWithoutValidation(name, value);
            }
            if (_host != null) request.Headers.Host = _host;
            if (!_keepAlive) request.Headers.ConnectionClose = true;
            if (_sendChunked) request.Headers.TransferEncodingChunked = true;
            if (_mediaType != null && request.Content?.Headers.ContentType is { } contentType) contentType.MediaType ??= _mediaType;
            if (basic != null && request.Headers.Authorization == null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes((string.IsNullOrEmpty(basic.Domain) ? "" : basic.Domain + "\\") + basic.UserName + ":" + basic.Password)));
            if (_cookieContainer != null && request.Headers.TryGetValues("Cookie", out _) == false)
            {
                string cookies = _cookieContainer.GetCookieHeader(_requestUri);
                if (!string.IsNullOrEmpty(cookies)) request.Headers.TryAddWithoutValidation("Cookie", cookies);
            }
            // the page streams the response as it arrives unless the app asked for it whole
            if (OperatingSystem.IsBrowser() && !_allowReadStreamBuffering)
                request.Options.Set(new HttpRequestOptionsKey<bool>("WebAssemblyEnableStreamingResponse"), true);
            return request;
        }

        // One client per redirect and decompression setting, shared by every request that has them: a
        // handler holds connections, and a request of its own each would hold them for nothing.
        private static readonly Dictionary<(bool, int, DecompressionMethods), HttpClient> s_clients = new Dictionary<(bool, int, DecompressionMethods), HttpClient>();

        private static HttpClient ClientFor(bool redirect, int redirects, DecompressionMethods decompression)
        {
            lock (s_clients)
            {
                if (s_clients.TryGetValue((redirect, redirects, decompression), out HttpClient client)) return client;
                var handler = new HttpClientHandler();
                // a browser's fetch keeps the cookies and decompresses on its own, and refuses to be told
                Try(() => handler.UseCookies = false);
                Try(() => handler.AllowAutoRedirect = redirect);
                Try(() => handler.MaxAutomaticRedirections = redirects);
                Try(() => handler.AutomaticDecompression = decompression);
                client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
                s_clients[(redirect, redirects, decompression)] = client;
                return client;
            }
        }

        private static void Try(Action set)
        {
            try { set(); }
            catch (PlatformNotSupportedException) { }
        }
    }
}
