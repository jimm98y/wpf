// file: requests over the page's file system, as .NET's: GET reads the file, PUT (or any other method with
// a body) writes it through the request stream. The path is read as Windows reads it: '\' a separator, and
// the name found whatever its case (WindowsPathCore), as the app's own File calls find it.

using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WpfWebGpu.Paths;

using System.ComponentModel;

namespace System.Net
{
    public class FileWebRequest : WebRequest, ISerializable
    {
        private readonly Uri _uri;
        private WebHeaderCollection _headers = new WebHeaderCollection();
        private string _method = "GET";
        private string _connectionGroupName;
        private long _contentLength;
        private ICredentials _credentials;
        private IWebProxy _proxy;
        private bool _preAuthenticate;
        private int _timeout = 100_000;
        private FileStream _writing;
        private bool _aborted, _submitted;
        private FileWebResponse _response;

        internal FileWebRequest(Uri uri)
        {
            if (uri.Scheme != Uri.UriSchemeFile) throw new ArgumentOutOfRangeException(nameof(uri));
            _uri = uri;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Serialization has been deprecated for FileWebRequest.")]
        protected FileWebRequest(SerializationInfo serializationInfo, StreamingContext streamingContext) => throw new PlatformNotSupportedException();

        [Obsolete("Serialization has been deprecated for FileWebRequest.")]
        void ISerializable.GetObjectData(SerializationInfo serializationInfo, StreamingContext streamingContext) => throw new PlatformNotSupportedException();

        [Obsolete("Serialization has been deprecated for FileWebRequest.")]
        protected override void GetObjectData(SerializationInfo serializationInfo, StreamingContext streamingContext) => throw new PlatformNotSupportedException();

        internal string Path => WindowsPathCore.Disk(_uri.LocalPath);

        public override string ConnectionGroupName { get => _connectionGroupName; set => _connectionGroupName = value; }

        public override long ContentLength
        {
            get => _contentLength;
            set
            {
                if (value < 0) throw new ArgumentException("The Content-Length value must be greater than or equal to zero.", nameof(value));
                _contentLength = value;
            }
        }

        public override string ContentType { get => _headers["Content-Type"]; set => _headers["Content-Type"] = value; }
        public override ICredentials Credentials { get => _credentials; set => _credentials = value; }
        public override WebHeaderCollection Headers => _headers;

        public override string Method
        {
            get => _method;
            set
            {
                if (string.IsNullOrEmpty(value)) throw new ArgumentException("Cannot set null or blank methods on request.", nameof(value));
                _method = value;
            }
        }

        public override bool PreAuthenticate { get => _preAuthenticate; set => _preAuthenticate = value; }
        public override IWebProxy Proxy { get => _proxy; set => _proxy = value; }
        public override Uri RequestUri => _uri;

        public override int Timeout
        {
            get => _timeout;
            set
            {
                if (value < 0 && value != System.Threading.Timeout.Infinite) throw new ArgumentOutOfRangeException(nameof(value), "Timeout can be only be set to 'System.Threading.Timeout.Infinite' or a value >= 0.");
                _timeout = value;
            }
        }

        public override bool UseDefaultCredentials
        {
            get => throw new NotSupportedException("This property is not supported by this class.");
            set => throw new NotSupportedException("This property is not supported by this class.");
        }

        public override Stream GetRequestStream()
        {
            if (_aborted) throw new WebException("The request was aborted: The request was canceled.", WebExceptionStatus.RequestCanceled);
            if (string.Equals(_method, "GET", StringComparison.OrdinalIgnoreCase) || string.Equals(_method, "HEAD", StringComparison.OrdinalIgnoreCase))
                throw new ProtocolViolationException("Cannot send a content-body with this verb-type.");
            if (_submitted) throw new InvalidOperationException("This operation cannot be performed after the request has been submitted.");
            _submitted = true;
            try
            {
                return _writing = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new WebException(e.Message, e);
            }
        }

        public override WebResponse GetResponse()
        {
            if (_aborted) throw new WebException("The request was aborted: The request was canceled.", WebExceptionStatus.RequestCanceled);
            _submitted = true;
            if (_response != null) return _response;
            _writing?.Dispose();
            try
            {
                return _response = new FileWebResponse(this, _uri, Path, _writing != null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // as .NET's: the failure to open the file, inside a WebException
                throw new WebException(e.Message, new WebException(e.Message, e, WebExceptionStatus.UnknownError, null), WebExceptionStatus.UnknownError, null);
            }
        }

        public override Task<Stream> GetRequestStreamAsync() => Task.Run(GetRequestStream);

        public override Task<WebResponse> GetResponseAsync() => Task.Run(GetResponse);

        public override IAsyncResult BeginGetRequestStream(AsyncCallback callback, object state) => TaskToAsyncResult.Begin(GetRequestStreamAsync(), callback, state);

        public override Stream EndGetRequestStream(IAsyncResult asyncResult) => TaskToAsyncResult.End<Stream>(asyncResult ?? throw new ArgumentNullException(nameof(asyncResult)));

        public override IAsyncResult BeginGetResponse(AsyncCallback callback, object state) => TaskToAsyncResult.Begin(GetResponseAsync(), callback, state);

        public override WebResponse EndGetResponse(IAsyncResult asyncResult) => TaskToAsyncResult.End<WebResponse>(asyncResult ?? throw new ArgumentNullException(nameof(asyncResult)));

        public override void Abort()
        {
            _aborted = true;
            _writing?.Dispose();
            _response?.Close();
        }
    }

    public class FileWebResponse : WebResponse, ISerializable
    {
        private readonly Uri _uri;
        private readonly WebHeaderCollection _headers = new WebHeaderCollection();
        private readonly long _length;
        private Stream _stream;
        private bool _closed;

        internal FileWebResponse(FileWebRequest request, Uri uri, string path, bool wrote)
        {
            _uri = uri;
            if (wrote)
            {
                _stream = Stream.Null;
                _length = 0;
            }
            else
            {
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                _stream = file;
                _length = file.Length;
            }
            _headers["Content-Length"] = _length.ToString(Globalization.CultureInfo.InvariantCulture);
            _headers["Content-Type"] = "application/octet-stream";
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Serialization has been deprecated for FileWebResponse.")]
        protected FileWebResponse(SerializationInfo serializationInfo, StreamingContext streamingContext) => throw new PlatformNotSupportedException();

        [Obsolete("Serialization has been deprecated for FileWebResponse.")]
        void ISerializable.GetObjectData(SerializationInfo serializationInfo, StreamingContext streamingContext) => throw new PlatformNotSupportedException();

        [Obsolete("Serialization has been deprecated for FileWebResponse.")]
        protected override void GetObjectData(SerializationInfo serializationInfo, StreamingContext streamingContext) => throw new PlatformNotSupportedException();

        private void CheckOpen()
        {
            if (_closed) throw new ObjectDisposedException(GetType().FullName);
        }

        public override long ContentLength { get { CheckOpen(); return _length; } }
        public override string ContentType { get { CheckOpen(); return "application/octet-stream"; } }
        public override WebHeaderCollection Headers { get { CheckOpen(); return _headers; } }
        public override Uri ResponseUri { get { CheckOpen(); return _uri; } }
        public override bool SupportsHeaders => true;

        public override Stream GetResponseStream()
        {
            CheckOpen();
            return _stream;
        }

        public override void Close()
        {
            if (_closed) return;
            _closed = true;
            _stream?.Dispose();
            _stream = null;
        }
    }
}
