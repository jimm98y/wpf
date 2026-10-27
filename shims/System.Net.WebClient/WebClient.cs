// WebClient as .NET has it, over WebRequest. Every operation asks GetWebRequest for its request and
// GetWebResponse for its response -- where an app's subclass sees each one (EwxLayer's map layer turns on
// pre-authentication there) -- and an operation's failure that is not a WebException already is wrapped
// in one, "An exception occurred during a WebClient request." One operation at a time per client.
//
// The ...Async members run the operation on the thread pool and raise their Completed event (and the
// progress ones) through an AsyncOperation, so on the synchronization context they were called on. The
// ...TaskAsync members are those, completing a task from the event, so their events are raised too.
//
// An address that is not a URI is a path, read as Windows reads it (WindowsPathCore): app opens its
// templates' thumbnails with OpenRead(path).

using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Cache;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WpfWebGpu.Paths;

namespace System.Net
{
    public class WebClient : Component
    {
        private const string DefaultUploadFileContentType = "application/octet-stream";
        private const string UploadFileContentType = "multipart/form-data";
        private const string UploadValuesContentType = "application/x-www-form-urlencoded";
        private const int DefaultCopyBufferLength = 8192;

        private Uri _baseAddress;
        private ICredentials _credentials;
        private WebHeaderCollection _headers;
        private NameValueCollection _requestParameters;
        private WebResponse _webResponse;
        private WebRequest _webRequest;
        private Encoding _encoding = Encoding.Default;
        private string _method;
        private long _contentLength = -1;
        private string _contentType;
        private bool _initWebClientAsync;
        private bool _canceled;
        private IWebProxy _proxy;
        private bool _proxySet;
        private int _callNesting;
        private AsyncOperation _asyncOp;

        private SendOrPostCallback _downloadDataOperationCompleted, _downloadStringOperationCompleted, _downloadFileOperationCompleted;
        private SendOrPostCallback _openReadOperationCompleted, _openWriteOperationCompleted;
        private SendOrPostCallback _uploadDataOperationCompleted, _uploadStringOperationCompleted, _uploadFileOperationCompleted, _uploadValuesOperationCompleted;
        private SendOrPostCallback _reportDownloadProgressChanged, _reportUploadProgressChanged;

        [Obsolete("WebRequest, HttpWebRequest, ServicePoint, and WebClient are obsolete. Use HttpClient instead.", DiagnosticId = "SYSLIB0014", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        public WebClient()
        {
            // A subclass may have a finalizer of its own; WebClient has nothing to finalize.
            if (GetType() == typeof(WebClient)) GC.SuppressFinalize(this);
        }

        public event DownloadStringCompletedEventHandler DownloadStringCompleted;
        public event DownloadDataCompletedEventHandler DownloadDataCompleted;
        public event AsyncCompletedEventHandler DownloadFileCompleted;
        public event UploadStringCompletedEventHandler UploadStringCompleted;
        public event UploadDataCompletedEventHandler UploadDataCompleted;
        public event UploadFileCompletedEventHandler UploadFileCompleted;
        public event UploadValuesCompletedEventHandler UploadValuesCompleted;
        public event OpenReadCompletedEventHandler OpenReadCompleted;
        public event OpenWriteCompletedEventHandler OpenWriteCompleted;
        public event DownloadProgressChangedEventHandler DownloadProgressChanged;
        public event UploadProgressChangedEventHandler UploadProgressChanged;

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports the .NET Framework infrastructure and is not intended to be used directly from your code.", true)]
        public event WriteStreamClosedEventHandler WriteStreamClosed { add { } remove { } }

        protected virtual void OnDownloadStringCompleted(DownloadStringCompletedEventArgs e) => DownloadStringCompleted?.Invoke(this, e);
        protected virtual void OnDownloadDataCompleted(DownloadDataCompletedEventArgs e) => DownloadDataCompleted?.Invoke(this, e);
        protected virtual void OnDownloadFileCompleted(AsyncCompletedEventArgs e) => DownloadFileCompleted?.Invoke(this, e);
        protected virtual void OnDownloadProgressChanged(DownloadProgressChangedEventArgs e) => DownloadProgressChanged?.Invoke(this, e);
        protected virtual void OnUploadStringCompleted(UploadStringCompletedEventArgs e) => UploadStringCompleted?.Invoke(this, e);
        protected virtual void OnUploadDataCompleted(UploadDataCompletedEventArgs e) => UploadDataCompleted?.Invoke(this, e);
        protected virtual void OnUploadFileCompleted(UploadFileCompletedEventArgs e) => UploadFileCompleted?.Invoke(this, e);
        protected virtual void OnUploadValuesCompleted(UploadValuesCompletedEventArgs e) => UploadValuesCompleted?.Invoke(this, e);
        protected virtual void OnUploadProgressChanged(UploadProgressChangedEventArgs e) => UploadProgressChanged?.Invoke(this, e);
        protected virtual void OnOpenReadCompleted(OpenReadCompletedEventArgs e) => OpenReadCompleted?.Invoke(this, e);
        protected virtual void OnOpenWriteCompleted(OpenWriteCompletedEventArgs e) => OpenWriteCompleted?.Invoke(this, e);

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports the .NET Framework infrastructure and is not intended to be used directly from your code.", true)]
        protected virtual void OnWriteStreamClosed(WriteStreamClosedEventArgs e) { }

        // ---- settings -----------------------------------------------------------------------------------

        public Encoding Encoding
        {
            get => _encoding;
            set
            {
                ArgumentNullException.ThrowIfNull(value, "Encoding");
                _encoding = value;
            }
        }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public string BaseAddress
        {
            get => _baseAddress != null ? _baseAddress.ToString() : string.Empty;
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    _baseAddress = null;
                    return;
                }
                try
                {
                    _baseAddress = new Uri(value);
                }
                catch (UriFormatException e)
                {
                    throw new ArgumentException("The specified value is not a valid base address.", nameof(value), e);
                }
            }
        }

        public ICredentials Credentials { get => _credentials; set => _credentials = value; }

        public bool UseDefaultCredentials
        {
            get => _credentials == CredentialCache.DefaultCredentials;
            set => _credentials = value ? CredentialCache.DefaultCredentials : null;
        }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public WebHeaderCollection Headers
        {
            get => _headers ??= new WebHeaderCollection();
            set => _headers = value;
        }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public NameValueCollection QueryString
        {
            get => _requestParameters ??= new NameValueCollection();
            set => _requestParameters = value;
        }

        public WebHeaderCollection ResponseHeaders => _webResponse?.Headers;

        public IWebProxy Proxy
        {
            get => _proxySet ? _proxy : WebRequest.DefaultWebProxy;
            set
            {
                _proxy = value;
                _proxySet = true;
            }
        }

        public RequestCachePolicy CachePolicy { get; set; }

        public bool IsBusy => Volatile.Read(ref _callNesting) > 0;

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports the .NET Framework infrastructure and is not intended to be used directly from your code.", true)]
        public bool AllowReadStreamBuffering { get; set; }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports the .NET Framework infrastructure and is not intended to be used directly from your code.", true)]
        public bool AllowWriteStreamBuffering { get; set; }

        // ---- the request and response a subclass may adjust ------------------------------------------------

        protected virtual WebRequest GetWebRequest(Uri address)
        {
            WebRequest request = WebRequest.Create(address);
            CopyHeadersTo(request);
            if (_credentials != null) request.Credentials = _credentials;
            if (_method != null) request.Method = _method;
            if (_contentLength != -1) request.ContentLength = _contentLength;
            if (_proxySet) request.Proxy = _proxy;
            if (CachePolicy != null) request.CachePolicy = CachePolicy;
            return request;
        }

        protected virtual WebResponse GetWebResponse(WebRequest request)
        {
            WebResponse response = request.GetResponse();
            _webResponse = response;
            return response;
        }

        protected virtual WebResponse GetWebResponse(WebRequest request, IAsyncResult result)
        {
            WebResponse response = request.EndGetResponse(result);
            _webResponse = response;
            return response;
        }

        // The headers HttpWebRequest keeps as properties go to the properties; the rest as they are.
        private void CopyHeadersTo(WebRequest request)
        {
            if (_contentType != null)
            {
                var headers = new WebHeaderCollection();
                if (_headers != null) foreach (string name in _headers.AllKeys) headers[name] = _headers[name];
                headers["Content-Type"] = _contentType;
                CopyHeadersTo(request, headers);
                return;
            }
            CopyHeadersTo(request, _headers);
        }

        private static void CopyHeadersTo(WebRequest request, WebHeaderCollection source)
        {
            if (source == null || source.Count == 0) return;
            if (request is HttpWebRequest http)
            {
                var rest = new WebHeaderCollection();
                foreach (string name in source.AllKeys)
                {
                    string value = source[name];
                    switch (name.ToLowerInvariant())
                    {
                        case "accept": http.Accept = value; break;
                        case "connection": http.Connection = value; break;
                        case "content-type": http.ContentType = value; break;
                        case "expect": http.Expect = value; break;
                        case "referer": http.Referer = value; break;
                        case "user-agent": http.UserAgent = value; break;
                        case "host": http.Host = value; break;
                        case "transfer-encoding": http.SendChunked = true; http.TransferEncoding = value; break;
                        case "if-modified-since":
                            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime since)) http.IfModifiedSince = since;
                            break;
                        default: rest.Add(name, value); break;
                    }
                }
                foreach (string name in rest.AllKeys) http.Headers[name] = rest[name];
            }
            else
            {
                foreach (string name in source.AllKeys) request.Headers[name] = source[name];
            }
        }

        // ---- addresses ----------------------------------------------------------------------------------------

        private Uri GetUri(string address)
        {
            ArgumentNullException.ThrowIfNull(address);
            Uri uri;
            if (_baseAddress != null)
            {
                if (!Uri.TryCreate(_baseAddress, address, out uri)) return new Uri(Path.GetFullPath(WindowsPathCore.Native(address)));
            }
            else if (!Uri.TryCreate(address, UriKind.Absolute, out uri))
            {
                return new Uri(Path.GetFullPath(WindowsPathCore.Native(address)));
            }
            return GetUri(uri);
        }

        private Uri GetUri(Uri address)
        {
            ArgumentNullException.ThrowIfNull(address);
            Uri uri = address;
            if (!address.IsAbsoluteUri && _baseAddress != null && !Uri.TryCreate(_baseAddress, address, out uri)) return address;
            // the query string's parameters, unless the address has a query of its own
            if (string.IsNullOrEmpty(uri.Query) && _requestParameters != null)
            {
                var query = new StringBuilder();
                string delimiter = string.Empty;
                for (int i = 0; i < _requestParameters.Count; i++)
                {
                    query.Append(delimiter).Append(_requestParameters.AllKeys[i]).Append('=').Append(_requestParameters[i]);
                    delimiter = "&";
                }
                uri = new UriBuilder(uri) { Query = query.ToString() }.Uri;
            }
            return uri;
        }

        private static string MapToDefaultMethod(Uri address)
        {
            Uri uri = !address.IsAbsoluteUri ? new Uri(Path.GetFullPath(address.OriginalString)) : address;
            return string.Equals(uri.Scheme, Uri.UriSchemeFtp, StringComparison.Ordinal) ? WebRequestMethods.Ftp.UploadFile : "POST";
        }

        private static string FilePath(string fileName) => WindowsPathCore.Disk(Path.GetFullPath(WindowsPathCore.Native(fileName)));

        // ---- one operation at a time ----------------------------------------------------------------------

        private void StartOperation()
        {
            if (Interlocked.Increment(ref _callNesting) > 1)
            {
                EndOperation();
                throw new NotSupportedException("WebClient does not support concurrent I/O operations.");
            }
            _contentLength = -1;
            _webResponse = null;
            _webRequest = null;
            _method = null;
            _contentType = null;
            _canceled = false;
        }

        private AsyncOperation StartAsyncOperation(object userToken)
        {
            if (!_initWebClientAsync)
            {
                _downloadDataOperationCompleted = a => OnDownloadDataCompleted((DownloadDataCompletedEventArgs)a);
                _downloadStringOperationCompleted = a => OnDownloadStringCompleted((DownloadStringCompletedEventArgs)a);
                _downloadFileOperationCompleted = a => OnDownloadFileCompleted((AsyncCompletedEventArgs)a);
                _openReadOperationCompleted = a => OnOpenReadCompleted((OpenReadCompletedEventArgs)a);
                _openWriteOperationCompleted = a => OnOpenWriteCompleted((OpenWriteCompletedEventArgs)a);
                _uploadDataOperationCompleted = a => OnUploadDataCompleted((UploadDataCompletedEventArgs)a);
                _uploadStringOperationCompleted = a => OnUploadStringCompleted((UploadStringCompletedEventArgs)a);
                _uploadFileOperationCompleted = a => OnUploadFileCompleted((UploadFileCompletedEventArgs)a);
                _uploadValuesOperationCompleted = a => OnUploadValuesCompleted((UploadValuesCompletedEventArgs)a);
                _reportDownloadProgressChanged = a => OnDownloadProgressChanged((DownloadProgressChangedEventArgs)a);
                _reportUploadProgressChanged = a => OnUploadProgressChanged((UploadProgressChangedEventArgs)a);
                _initWebClientAsync = true;
            }
            AsyncOperation op = AsyncOperationManager.CreateOperation(userToken);
            StartOperation();
            _asyncOp = op;
            return op;
        }

        private void EndOperation() => Interlocked.Decrement(ref _callNesting);

        public void CancelAsync()
        {
            WebRequest request = _webRequest;
            _canceled = true;
            AbortRequest(request);
        }

        private static void AbortRequest(WebRequest request)
        {
            try { request?.Abort(); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
        }

        private static Exception Wrap(Exception e) => e is WebException or SecurityException ? e : new WebException("An exception occurred during a WebClient request.", e);

        private T Run<T>(Func<T> operation)
        {
            StartOperation();
            try
            {
                return operation();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                AbortRequest(_webRequest);
                throw Wrap(e);
            }
            finally
            {
                EndOperation();
            }
        }

        // The operation on the thread pool; its result, error or cancellation posted to the caller's context.
        private void RunAsync<T>(object userToken, Func<SendOrPostCallback> completed, Func<AsyncOperation, T> operation, Func<T, Exception, bool, object, object> args)
        {
            AsyncOperation op = StartAsyncOperation(userToken);
            Task.Run(() =>
            {
                T result = default;
                Exception error = null;
                try
                {
                    result = operation(op);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    AbortRequest(_webRequest);
                    error = Wrap(e);
                }
                if (_canceled)
                {
                    error = new WebException("The request was aborted: The request was canceled.", WebExceptionStatus.RequestCanceled);
                    result = default;
                }
                object e2 = args(result, error, _canceled, op.UserSuppliedState);
                _asyncOp = null;
                EndOperation();
                op.PostOperationCompleted(completed(), e2);
            });
        }

        // ---- the transfers ---------------------------------------------------------------------------------

        private byte[] DownloadBits(WebRequest request, Stream writeTo, AsyncOperation op)
        {
            WebResponse response = _webResponse = GetWebResponse(request);
            Stream source = response.GetResponseStream();
            long total = response.ContentLength;
            var buffer = new byte[total > 0 && total < DefaultCopyBufferLength ? (int)total : DefaultCopyBufferLength];
            long received = 0;
            int n;
            while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                writeTo.Write(buffer, 0, n);
                received += n;
                if (op != null)
                    op.Post(_reportDownloadProgressChanged, new DownloadProgressChangedEventArgs(total > 0 ? (int)(received * 100 / total) : 0, op.UserSuppliedState, received, total));
            }
            source.Dispose();
            return writeTo is MemoryStream memory ? memory.ToArray() : null;
        }

        private byte[] UploadBits(WebRequest request, byte[] header, byte[] body, byte[] footer, AsyncOperation op)
        {
            if (request.RequestUri.Scheme == Uri.UriSchemeFile) header = footer = null;
            long total = (header?.Length ?? 0) + body.Length + (footer?.Length ?? 0);
            using (Stream target = request.GetRequestStream())
            {
                if (header != null) target.Write(header, 0, header.Length);
                target.Write(body, 0, body.Length);
                if (footer != null) target.Write(footer, 0, footer.Length);
            }
            if (op != null) op.Post(_reportUploadProgressChanged, new UploadProgressChangedEventArgs(50, op.UserSuppliedState, total, total, 0, -1));
            return DownloadBits(request, new MemoryStream(), null);
        }

        private string GetStringUsingEncoding(WebRequest request, byte[] data)
        {
            Encoding enc = null;
            int bomLength = -1;
            // the charset of the request's Content-Type, as .NET's WebClient reads it
            string contentType;
            try { contentType = request.ContentType; }
            catch (Exception e) when (e is NotImplementedException or NotSupportedException) { contentType = null; }
            if (contentType != null)
            {
                foreach (string part in contentType.ToLowerInvariant().Split(';', ' ', ','))
                {
                    if (!part.StartsWith("charset=", StringComparison.Ordinal)) continue;
                    try { enc = Encoding.GetEncoding(part.Substring(8).Trim('"', '\'')); }
                    catch (ArgumentException) { }
                    break;
                }
            }
            if (enc == null)
            {
                // a byte order mark, if there is one
                Encoding[] encodings = { Encoding.UTF8, Encoding.UTF32, Encoding.Unicode, Encoding.BigEndianUnicode };
                foreach (Encoding candidate in encodings)
                {
                    ReadOnlySpan<byte> preamble = candidate.Preamble;
                    if (preamble.Length > 0 && data.AsSpan().StartsWith(preamble)) { enc = candidate; bomLength = preamble.Length; break; }
                }
            }
            enc ??= Encoding;
            if (bomLength == -1)
            {
                ReadOnlySpan<byte> preamble = enc.Preamble;
                bomLength = data.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
            }
            return enc.GetString(data, bomLength, data.Length - bomLength);
        }

        private byte[] UploadValuesBytes(NameValueCollection data)
        {
            string contentType = _headers?["Content-Type"];
            if (contentType != null && !string.Equals(contentType, UploadValuesContentType, StringComparison.OrdinalIgnoreCase))
                throw new WebException("The Content-Type header cannot be changed from its default value for this request.");
            _contentType = UploadValuesContentType;
            var body = new StringBuilder();
            string delimiter = string.Empty;
            foreach (string name in data.AllKeys)
            {
                body.Append(delimiter).Append(UrlEncode(name)).Append('=').Append(UrlEncode(data[name]));
                delimiter = "&";
            }
            return Encoding.ASCII.GetBytes(body.ToString());
        }

        private static string UrlEncode(string s) => s == null ? null : WebUtility.UrlEncode(s);

        private (byte[] Header, byte[] Body, byte[] Footer) UploadFileParts(Uri address, string fileName)
        {
            string contentType = _headers?["Content-Type"];
            if (contentType == null) contentType = DefaultUploadFileContentType;
            else if (contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
                throw new WebException("The Content-Type header cannot be set to a multipart type for this request.");
            string path = FilePath(fileName);
            byte[] body = File.ReadAllBytes(path);
            if (address.Scheme == Uri.UriSchemeFile) return (null, body, null);
            string boundary = "---------------------" + DateTime.Now.Ticks.ToString("x", NumberFormatInfo.InvariantInfo);
            _contentType = UploadFileContentType + "; boundary=" + boundary;
            byte[] header = Encoding.UTF8.GetBytes("--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\""
                + Path.GetFileName(path) + "\"\r\nContent-Type: " + contentType + "\r\n\r\n");
            byte[] footer = Encoding.ASCII.GetBytes("\r\n--" + boundary + "--\r\n");
            return (header, body, footer);
        }

        private WebRequest Request(Uri address, string method = null, long contentLength = -1)
        {
            _method = method;
            _contentLength = contentLength;
            return _webRequest = GetWebRequest(GetUri(address));
        }

        // ---- downloads ------------------------------------------------------------------------------------------

        public byte[] DownloadData(string address) => DownloadData(GetUri(address));

        public byte[] DownloadData(Uri address)
        {
            ArgumentNullException.ThrowIfNull(address);
            return Run(() => DownloadBits(Request(address), new MemoryStream(), null));
        }

        public void DownloadDataAsync(Uri address) => DownloadDataAsync(address, null);

        public void DownloadDataAsync(Uri address, object userToken)
        {
            ArgumentNullException.ThrowIfNull(address);
            RunAsync(userToken, () => _downloadDataOperationCompleted,
                op => DownloadBits(Request(address), new MemoryStream(), op),
                (r, e, c, t) => new DownloadDataCompletedEventArgs(r, e, c, t));
        }

        public string DownloadString(string address) => DownloadString(GetUri(address));

        public string DownloadString(Uri address)
        {
            ArgumentNullException.ThrowIfNull(address);
            return Run(() =>
            {
                WebRequest request = Request(address);
                return GetStringUsingEncoding(request, DownloadBits(request, new MemoryStream(), null));
            });
        }

        public void DownloadStringAsync(Uri address) => DownloadStringAsync(address, null);

        public void DownloadStringAsync(Uri address, object userToken)
        {
            ArgumentNullException.ThrowIfNull(address);
            RunAsync(userToken, () => _downloadStringOperationCompleted,
                op =>
                {
                    WebRequest request = Request(address);
                    return GetStringUsingEncoding(request, DownloadBits(request, new MemoryStream(), op));
                },
                (r, e, c, t) => new DownloadStringCompletedEventArgs(r, e, c, t));
        }

        public void DownloadFile(string address, string fileName) => DownloadFile(GetUri(address), fileName);

        public void DownloadFile(Uri address, string fileName)
        {
            ArgumentNullException.ThrowIfNull(address);
            ArgumentNullException.ThrowIfNull(fileName);
            Run(() => DownloadToFile(address, fileName, null));
        }

        private object DownloadToFile(Uri address, string fileName, AsyncOperation op)
        {
            string path = FilePath(fileName);
            bool succeeded = false;
            var file = new FileStream(path, FileMode.Create, FileAccess.Write);
            try
            {
                DownloadBits(Request(address), file, op);
                succeeded = true;
            }
            finally
            {
                file.Dispose();
                if (!succeeded) File.Delete(path);
            }
            return null;
        }

        public void DownloadFileAsync(Uri address, string fileName) => DownloadFileAsync(address, fileName, null);

        public void DownloadFileAsync(Uri address, string fileName, object userToken)
        {
            ArgumentNullException.ThrowIfNull(address);
            ArgumentNullException.ThrowIfNull(fileName);
            RunAsync(userToken, () => _downloadFileOperationCompleted,
                op => DownloadToFile(address, fileName, op),
                (r, e, c, t) => new AsyncCompletedEventArgs(e, c, t));
        }

        public Stream OpenRead(string address) => OpenRead(GetUri(address));

        public Stream OpenRead(Uri address)
        {
            ArgumentNullException.ThrowIfNull(address);
            return Run(() => OpenReadCore(address));
        }

        private Stream OpenReadCore(Uri address)
        {
            WebRequest request = Request(address);
            WebResponse response = _webResponse = GetWebResponse(request);
            return response.GetResponseStream();
        }

        public void OpenReadAsync(Uri address) => OpenReadAsync(address, null);

        public void OpenReadAsync(Uri address, object userToken)
        {
            ArgumentNullException.ThrowIfNull(address);
            RunAsync(userToken, () => _openReadOperationCompleted,
                op => OpenReadCore(address),
                (r, e, c, t) => new OpenReadCompletedEventArgs(r, e, c, t));
        }

        // ---- the write stream: the request goes out when it is closed ----------------------------------------

        private sealed class WriteStream : Stream
        {
            private readonly Stream _inner;
            private readonly WebRequest _request;
            private readonly WebClient _client;
            private bool _closed;

            public WriteStream(Stream inner, WebRequest request, WebClient client)
            {
                _inner = inner;
                _request = request;
                _client = client;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => !_closed;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

            protected override void Dispose(bool disposing)
            {
                if (_closed || !disposing)
                {
                    base.Dispose(disposing);
                    return;
                }
                _closed = true;
                try
                {
                    _inner.Dispose();
                    _client.GetWebResponse(_request).Close();
                }
                finally
                {
                    base.Dispose(disposing);
                }
            }
        }

        public Stream OpenWrite(string address) => OpenWrite(GetUri(address), null);

        public Stream OpenWrite(Uri address) => OpenWrite(address, null);

        public Stream OpenWrite(string address, string method) => OpenWrite(GetUri(address), method);

        public Stream OpenWrite(Uri address, string method)
        {
            ArgumentNullException.ThrowIfNull(address);
            method ??= MapToDefaultMethod(address);
            return Run(() => OpenWriteCore(address, method));
        }

        private Stream OpenWriteCore(Uri address, string method)
        {
            WebRequest request = Request(address, method);
            return new WriteStream(request.GetRequestStream(), request, this);
        }

        public void OpenWriteAsync(Uri address) => OpenWriteAsync(address, null, null);

        public void OpenWriteAsync(Uri address, string method) => OpenWriteAsync(address, method, null);

        public void OpenWriteAsync(Uri address, string method, object userToken)
        {
            ArgumentNullException.ThrowIfNull(address);
            method ??= MapToDefaultMethod(address);
            RunAsync(userToken, () => _openWriteOperationCompleted,
                op => OpenWriteCore(address, method),
                (r, e, c, t) => new OpenWriteCompletedEventArgs(r, e, c, t));
        }

        // ---- uploads ----------------------------------------------------------------------------------------------

        public byte[] UploadData(string address, byte[] data) => UploadData(GetUri(address), null, data);

        public byte[] UploadData(Uri address, byte[] data) => UploadData(address, null, data);

        public byte[] UploadData(string address, string method, byte[] data) => UploadData(GetUri(address), method, data);

        public byte[] UploadData(Uri address, string method, byte[] data)
        {
            ArgumentNullException.ThrowIfNull(address);
            ArgumentNullException.ThrowIfNull(data);
            method ??= MapToDefaultMethod(address);
            return Run(() => UploadBits(Request(address, method, data.Length), null, data, null, null));
        }

        public void UploadDataAsync(Uri address, byte[] data) => UploadDataAsync(address, null, data, null);

        public void UploadDataAsync(Uri address, string method, byte[] data) => UploadDataAsync(address, method, data, null);

        public void UploadDataAsync(Uri address, string method, byte[] data, object userToken)
        {
            ArgumentNullException.ThrowIfNull(address);
            ArgumentNullException.ThrowIfNull(data);
            method ??= MapToDefaultMethod(address);
            RunAsync(userToken, () => _uploadDataOperationCompleted,
                op => UploadBits(Request(address, method, data.Length), null, data, null, op),
                (r, e, c, t) => new UploadDataCompletedEventArgs(r, e, c, t));
        }

        public byte[] UploadFile(string address, string fileName) => UploadFile(GetUri(address), null, fileName);

        public byte[] UploadFile(Uri address, string fileName) => UploadFile(address, null, fileName);

        public byte[] UploadFile(string address, string method, string fileName) => UploadFile(GetUri(address), method, fileName);

        public byte[] UploadFile(Uri address, string method, string fileName)
        {
            ArgumentNullException.ThrowIfNull(address);
            ArgumentNullException.ThrowIfNull(fileName);
            method ??= MapToDefaultMethod(address);
            return Run(() => UploadFileCore(address, method, fileName, null));
        }

        private byte[] UploadFileCore(Uri address, string method, string fileName, AsyncOperation op)
        {
            var (header, body, footer) = UploadFileParts(GetUri(address), fileName);
            long length = (header?.Length ?? 0) + body.Length + (footer?.Length ?? 0);
            return UploadBits(Request(address, method, length), header, body, footer, op);
        }

        public void UploadFileAsync(Uri address, string fileName) => UploadFileAsync(address, null, fileName, null);

        public void UploadFileAsync(Uri address, string method, string fileName) => UploadFileAsync(address, method, fileName, null);

        public void UploadFileAsync(Uri address, string method, string fileName, object userToken)
        {
            ArgumentNullException.ThrowIfNull(address);
            ArgumentNullException.ThrowIfNull(fileName);
            method ??= MapToDefaultMethod(address);
            RunAsync(userToken, () => _uploadFileOperationCompleted,
                op => UploadFileCore(address, method, fileName, op),
                (r, e, c, t) => new UploadFileCompletedEventArgs(r, e, c, t));
        }

        public byte[] UploadValues(string address, NameValueCollection data) => UploadValues(GetUri(address), null, data);

        public byte[] UploadValues(Uri address, NameValueCollection data) => UploadValues(address, null, data);

        public byte[] UploadValues(string address, string method, NameValueCollection data) => UploadValues(GetUri(address), method, data);

        public byte[] UploadValues(Uri address, string method, NameValueCollection data)
        {
            ArgumentNullException.ThrowIfNull(address);
            ArgumentNullException.ThrowIfNull(data);
            method ??= MapToDefaultMethod(address);
            return Run(() =>
            {
                byte[] body = UploadValuesBytes(data);
                return UploadBits(Request(address, method, body.Length), null, body, null, null);
            });
        }

        public void UploadValuesAsync(Uri address, NameValueCollection data) => UploadValuesAsync(address, null, data, null);

        public void UploadValuesAsync(Uri address, string method, NameValueCollection data) => UploadValuesAsync(address, method, data, null);

        public void UploadValuesAsync(Uri address, string method, NameValueCollection data, object userToken)
        {
            ArgumentNullException.ThrowIfNull(address);
            ArgumentNullException.ThrowIfNull(data);
            method ??= MapToDefaultMethod(address);
            RunAsync(userToken, () => _uploadValuesOperationCompleted,
                op =>
                {
                    byte[] body = UploadValuesBytes(data);
                    return UploadBits(Request(address, method, body.Length), null, body, null, op);
                },
                (r, e, c, t) => new UploadValuesCompletedEventArgs(r, e, c, t));
        }

        public string UploadString(string address, string data) => UploadString(GetUri(address), null, data);

        public string UploadString(Uri address, string data) => UploadString(address, null, data);

        public string UploadString(string address, string method, string data) => UploadString(GetUri(address), method, data);

        public string UploadString(Uri address, string method, string data)
        {
            ArgumentNullException.ThrowIfNull(address);
            ArgumentNullException.ThrowIfNull(data);
            method ??= MapToDefaultMethod(address);
            return Run(() => UploadStringCore(address, method, data, null));
        }

        private string UploadStringCore(Uri address, string method, string data, AsyncOperation op)
        {
            byte[] body = Encoding.GetBytes(data);
            WebRequest request = Request(address, method, body.Length);
            return GetStringUsingEncoding(request, UploadBits(request, null, body, null, op));
        }

        public void UploadStringAsync(Uri address, string data) => UploadStringAsync(address, null, data, null);

        public void UploadStringAsync(Uri address, string method, string data) => UploadStringAsync(address, method, data, null);

        public void UploadStringAsync(Uri address, string method, string data, object userToken)
        {
            ArgumentNullException.ThrowIfNull(address);
            ArgumentNullException.ThrowIfNull(data);
            method ??= MapToDefaultMethod(address);
            RunAsync(userToken, () => _uploadStringOperationCompleted,
                op => UploadStringCore(address, method, data, op),
                (r, e, c, t) => new UploadStringCompletedEventArgs(r, e, c, t));
        }

        // ---- the TaskAsync members: the ...Async ones, completing a task from their event ---------------------

        public Task<byte[]> DownloadDataTaskAsync(string address) => DownloadDataTaskAsync(GetUri(address));

        public Task<byte[]> DownloadDataTaskAsync(Uri address)
        {
            var tcs = new TaskCompletionSource<byte[]>(address);
            DownloadDataCompletedEventHandler handler = null;
            handler = (s, e) => Complete(tcs, e, () => e.Result, () => DownloadDataCompleted -= handler);
            DownloadDataCompleted += handler;
            try { DownloadDataAsync(address, tcs); }
            catch { DownloadDataCompleted -= handler; throw; }
            return tcs.Task;
        }

        public Task<string> DownloadStringTaskAsync(string address) => DownloadStringTaskAsync(GetUri(address));

        public Task<string> DownloadStringTaskAsync(Uri address)
        {
            var tcs = new TaskCompletionSource<string>(address);
            DownloadStringCompletedEventHandler handler = null;
            handler = (s, e) => Complete(tcs, e, () => e.Result, () => DownloadStringCompleted -= handler);
            DownloadStringCompleted += handler;
            try { DownloadStringAsync(address, tcs); }
            catch { DownloadStringCompleted -= handler; throw; }
            return tcs.Task;
        }

        public Task DownloadFileTaskAsync(string address, string fileName) => DownloadFileTaskAsync(GetUri(address), fileName);

        public Task DownloadFileTaskAsync(Uri address, string fileName)
        {
            var tcs = new TaskCompletionSource<object>(address);
            AsyncCompletedEventHandler handler = null;
            handler = (s, e) => Complete(tcs, e, () => null, () => DownloadFileCompleted -= handler);
            DownloadFileCompleted += handler;
            try { DownloadFileAsync(address, fileName, tcs); }
            catch { DownloadFileCompleted -= handler; throw; }
            return tcs.Task;
        }

        public Task<Stream> OpenReadTaskAsync(string address) => OpenReadTaskAsync(GetUri(address));

        public Task<Stream> OpenReadTaskAsync(Uri address)
        {
            var tcs = new TaskCompletionSource<Stream>(address);
            OpenReadCompletedEventHandler handler = null;
            handler = (s, e) => Complete(tcs, e, () => e.Result, () => OpenReadCompleted -= handler);
            OpenReadCompleted += handler;
            try { OpenReadAsync(address, tcs); }
            catch { OpenReadCompleted -= handler; throw; }
            return tcs.Task;
        }

        public Task<Stream> OpenWriteTaskAsync(string address) => OpenWriteTaskAsync(GetUri(address), null);

        public Task<Stream> OpenWriteTaskAsync(Uri address) => OpenWriteTaskAsync(address, null);

        public Task<Stream> OpenWriteTaskAsync(string address, string method) => OpenWriteTaskAsync(GetUri(address), method);

        public Task<Stream> OpenWriteTaskAsync(Uri address, string method)
        {
            var tcs = new TaskCompletionSource<Stream>(address);
            OpenWriteCompletedEventHandler handler = null;
            handler = (s, e) => Complete(tcs, e, () => e.Result, () => OpenWriteCompleted -= handler);
            OpenWriteCompleted += handler;
            try { OpenWriteAsync(address, method, tcs); }
            catch { OpenWriteCompleted -= handler; throw; }
            return tcs.Task;
        }

        public Task<string> UploadStringTaskAsync(string address, string data) => UploadStringTaskAsync(address, null, data);

        public Task<string> UploadStringTaskAsync(Uri address, string data) => UploadStringTaskAsync(address, null, data);

        public Task<string> UploadStringTaskAsync(string address, string method, string data) => UploadStringTaskAsync(GetUri(address), method, data);

        public Task<string> UploadStringTaskAsync(Uri address, string method, string data)
        {
            var tcs = new TaskCompletionSource<string>(address);
            UploadStringCompletedEventHandler handler = null;
            handler = (s, e) => Complete(tcs, e, () => e.Result, () => UploadStringCompleted -= handler);
            UploadStringCompleted += handler;
            try { UploadStringAsync(address, method, data, tcs); }
            catch { UploadStringCompleted -= handler; throw; }
            return tcs.Task;
        }

        public Task<byte[]> UploadDataTaskAsync(string address, byte[] data) => UploadDataTaskAsync(GetUri(address), null, data);

        public Task<byte[]> UploadDataTaskAsync(Uri address, byte[] data) => UploadDataTaskAsync(address, null, data);

        public Task<byte[]> UploadDataTaskAsync(string address, string method, byte[] data) => UploadDataTaskAsync(GetUri(address), method, data);

        public Task<byte[]> UploadDataTaskAsync(Uri address, string method, byte[] data)
        {
            var tcs = new TaskCompletionSource<byte[]>(address);
            UploadDataCompletedEventHandler handler = null;
            handler = (s, e) => Complete(tcs, e, () => e.Result, () => UploadDataCompleted -= handler);
            UploadDataCompleted += handler;
            try { UploadDataAsync(address, method, data, tcs); }
            catch { UploadDataCompleted -= handler; throw; }
            return tcs.Task;
        }

        public Task<byte[]> UploadFileTaskAsync(string address, string fileName) => UploadFileTaskAsync(GetUri(address), null, fileName);

        public Task<byte[]> UploadFileTaskAsync(Uri address, string fileName) => UploadFileTaskAsync(address, null, fileName);

        public Task<byte[]> UploadFileTaskAsync(string address, string method, string fileName) => UploadFileTaskAsync(GetUri(address), method, fileName);

        public Task<byte[]> UploadFileTaskAsync(Uri address, string method, string fileName)
        {
            var tcs = new TaskCompletionSource<byte[]>(address);
            UploadFileCompletedEventHandler handler = null;
            handler = (s, e) => Complete(tcs, e, () => e.Result, () => UploadFileCompleted -= handler);
            UploadFileCompleted += handler;
            try { UploadFileAsync(address, method, fileName, tcs); }
            catch { UploadFileCompleted -= handler; throw; }
            return tcs.Task;
        }

        public Task<byte[]> UploadValuesTaskAsync(string address, NameValueCollection data) => UploadValuesTaskAsync(GetUri(address), null, data);

        public Task<byte[]> UploadValuesTaskAsync(string address, string method, NameValueCollection data) => UploadValuesTaskAsync(GetUri(address), method, data);

        public Task<byte[]> UploadValuesTaskAsync(Uri address, NameValueCollection data) => UploadValuesTaskAsync(address, null, data);

        public Task<byte[]> UploadValuesTaskAsync(Uri address, string method, NameValueCollection data)
        {
            var tcs = new TaskCompletionSource<byte[]>(address);
            UploadValuesCompletedEventHandler handler = null;
            handler = (s, e) => Complete(tcs, e, () => e.Result, () => UploadValuesCompleted -= handler);
            UploadValuesCompleted += handler;
            try { UploadValuesAsync(address, method, data, tcs); }
            catch { UploadValuesCompleted -= handler; throw; }
            return tcs.Task;
        }

        // only the event of this task's own operation (its user token is the task's source) completes it
        private static void Complete<T>(TaskCompletionSource<T> tcs, AsyncCompletedEventArgs e, Func<T> result, Action unsubscribe)
        {
            if (e.UserState != tcs) return;
            try { unsubscribe(); }
            finally
            {
                if (e.Error != null) tcs.TrySetException(e.Error);
                else if (e.Cancelled) tcs.TrySetCanceled();
                else tcs.TrySetResult(result());
            }
        }
    }
}
