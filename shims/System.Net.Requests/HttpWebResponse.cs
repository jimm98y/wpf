// HttpWebResponse over the HttpResponseMessage it came from: the status, the headers (the message's and its
// content's, as one collection), the cookies, and the body's stream -- the same stream each time it is
// asked for, as .NET's.

using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;

using System.ComponentModel;

namespace System.Net
{
    public class HttpWebResponse : WebResponse, ISerializable
    {
        private readonly HttpResponseMessage _message;
        private readonly WebHeaderCollection _headers = new WebHeaderCollection();
        private readonly Uri _responseUri;
        private Stream _stream;
        private CookieCollection _cookies;
        private bool _closed;

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports the .NET infrastructure and is not intended to be used directly from your code.", true)]
        public HttpWebResponse() { }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Serialization has been deprecated for HttpWebResponse.")]
        protected HttpWebResponse(SerializationInfo serializationInfo, StreamingContext streamingContext) => throw new PlatformNotSupportedException();

        internal HttpWebResponse(HttpResponseMessage message, Uri requestUri, CookieContainer cookieContainer, Stream stream)
        {
            _message = message;
            _stream = stream;
            _responseUri = message.RequestMessage?.RequestUri ?? requestUri;
            foreach (var header in message.Headers)
                foreach (string value in header.Value) _headers.Add(header.Key, value);
            foreach (var header in message.Content.Headers)
                foreach (string value in header.Value) _headers.Add(header.Key, value);
            // the cookies the request's container holds for the address; none without a container
            if (cookieContainer != null) _cookies = cookieContainer.GetCookies(_responseUri);
        }

        [Obsolete("Serialization has been deprecated for HttpWebResponse.")]
        void ISerializable.GetObjectData(SerializationInfo serializationInfo, StreamingContext streamingContext) => throw new PlatformNotSupportedException();

        [Obsolete("Serialization has been deprecated for HttpWebResponse.")]
        protected override void GetObjectData(SerializationInfo serializationInfo, StreamingContext streamingContext) => throw new PlatformNotSupportedException();

        private void CheckOpen()
        {
            if (_closed) throw new ObjectDisposedException(GetType().FullName);
        }

        public override bool IsMutuallyAuthenticated => false;

        public override long ContentLength
        {
            get
            {
                CheckOpen();
                return _message?.Content.Headers.ContentLength ?? -1;
            }
        }

        public override string ContentType
        {
            get
            {
                CheckOpen();
                return _headers["Content-Type"] ?? string.Empty;
            }
        }

        public string ContentEncoding
        {
            get
            {
                CheckOpen();
                return _headers["Content-Encoding"] ?? string.Empty;
            }
        }

        public virtual CookieCollection Cookies
        {
            get
            {
                CheckOpen();
                return _cookies ??= new CookieCollection();
            }
            set
            {
                CheckOpen();
                _cookies = value;
            }
        }

        public DateTime LastModified
        {
            get
            {
                CheckOpen();
                DateTimeOffset? last = _message?.Content.Headers.LastModified;
                return last.HasValue ? last.Value.LocalDateTime : DateTime.Now;
            }
        }

        public string Server
        {
            get
            {
                CheckOpen();
                return _headers["Server"] ?? string.Empty;
            }
        }

        public Version ProtocolVersion
        {
            get
            {
                CheckOpen();
                return _message?.Version ?? HttpVersion.Version11;
            }
        }

        public override WebHeaderCollection Headers
        {
            get
            {
                CheckOpen();
                return _headers;
            }
        }

        public virtual string Method
        {
            get
            {
                CheckOpen();
                return _message?.RequestMessage?.Method.Method ?? "GET";
            }
        }

        public override Uri ResponseUri
        {
            get
            {
                CheckOpen();
                return _responseUri;
            }
        }

        public virtual HttpStatusCode StatusCode
        {
            get
            {
                CheckOpen();
                return _message?.StatusCode ?? 0;
            }
        }

        public virtual string StatusDescription
        {
            get
            {
                CheckOpen();
                return _message?.ReasonPhrase ?? string.Empty;
            }
        }

        /// <summary>The Content-Type's charset; ISO-8859-1 for a text type that names none, as HTTP/1.1
        /// has it; null when there is no Content-Type.</summary>
        public string CharacterSet
        {
            get
            {
                CheckOpen();
                string type = _headers["Content-Type"];
                if (type == null) return null;
                foreach (string part in type.Split(';'))
                {
                    string p = part.Trim();
                    if (p.StartsWith("charset=", StringComparison.OrdinalIgnoreCase)) return p.Substring(8).Trim('"');
                }
                return type.TrimStart().StartsWith("text/", StringComparison.OrdinalIgnoreCase) ? "ISO-8859-1" : string.Empty;
            }
        }

        public override bool SupportsHeaders => true;

        public string GetResponseHeader(string headerName)
        {
            CheckOpen();
            return _headers[headerName] ?? string.Empty;
        }

        public override Stream GetResponseStream()
        {
            CheckOpen();
            return _stream ??= Stream.Null;
        }

        public override void Close() => Dispose(true);

        protected override void Dispose(bool disposing)
        {
            if (_closed) return;
            _closed = true;
            if (!disposing) return;
            _stream?.Dispose();
            _message?.Dispose();
        }
    }
}
