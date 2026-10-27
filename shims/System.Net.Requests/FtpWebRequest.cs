// ftp: requests have no transport in a browser (a page cannot open a socket). They are made and set up as
// .NET's are; asking for the response or the request stream is refused.

using System.IO;
using System.Net.Cache;
using System.Security.Cryptography.X509Certificates;

namespace System.Net
{
    public sealed class FtpWebRequest : WebRequest
    {
        private readonly Uri _uri;
        private WebHeaderCollection _headers = new WebHeaderCollection();
        private string _method = WebRequestMethods.Ftp.DownloadFile;
        private int _timeout = 100_000, _readWriteTimeout = 300_000;
        private long _contentLength, _contentOffset;
        private X509CertificateCollection _clientCertificates;

        internal FtpWebRequest(Uri uri) => _uri = uri;

        private static PlatformNotSupportedException NoFtp() => new PlatformNotSupportedException("FTP has no transport in a browser.");

        public static new RequestCachePolicy DefaultCachePolicy { get; set; } = new RequestCachePolicy(RequestCacheLevel.BypassCache);

        public X509CertificateCollection ClientCertificates { get => _clientCertificates ??= new X509CertificateCollection(); set => _clientCertificates = value ?? throw new ArgumentNullException(nameof(value)); }
        public override string ConnectionGroupName { get; set; }
        public override long ContentLength { get => _contentLength; set => _contentLength = value; }
        public long ContentOffset { get => _contentOffset; set => _contentOffset = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)); }
        public override string ContentType { get => throw new NotSupportedException("This property is not supported by this class."); set => throw new NotSupportedException("This property is not supported by this class."); }
        [System.Diagnostics.CodeAnalysis.DisallowNull]
        public override ICredentials Credentials { get; set; }
        public bool EnableSsl { get; set; }
        public override WebHeaderCollection Headers { get => _headers; set => _headers = value ?? new WebHeaderCollection(); }
        public bool KeepAlive { get; set; } = true;
        public override string Method { get => _method; set => _method = string.IsNullOrEmpty(value) ? throw new ArgumentException("The value cannot be an empty string.", nameof(value)) : value; }
        public override bool PreAuthenticate { get => throw new NotSupportedException("This property is not supported by this class."); set => throw new NotSupportedException("This property is not supported by this class."); }
        public override IWebProxy Proxy { get; set; }
        public int ReadWriteTimeout { get => _readWriteTimeout; set => _readWriteTimeout = value; }
        [System.Diagnostics.CodeAnalysis.DisallowNull]
        public string RenameTo { get; set; }
        public override Uri RequestUri => _uri;
        public ServicePoint ServicePoint => ServicePointManager.FindServicePoint(_uri);
        public override int Timeout { get => _timeout; set => _timeout = value; }
        public bool UseBinary { get; set; } = true;
        public override bool UseDefaultCredentials { get => throw new NotSupportedException("This property is not supported by this class."); set => throw new NotSupportedException("This property is not supported by this class."); }
        public bool UsePassive { get; set; } = true;

        public override void Abort() { }
        public override IAsyncResult BeginGetRequestStream(AsyncCallback callback, object state) => throw NoFtp();
        public override IAsyncResult BeginGetResponse(AsyncCallback callback, object state) => throw NoFtp();
        public override Stream EndGetRequestStream(IAsyncResult asyncResult) => throw NoFtp();
        public override WebResponse EndGetResponse(IAsyncResult asyncResult) => throw NoFtp();
        public override Stream GetRequestStream() => throw NoFtp();
        public override WebResponse GetResponse() => throw NoFtp();
    }

    public class FtpWebResponse : WebResponse, IDisposable
    {
        internal FtpWebResponse() { }

        public string BannerMessage => null;
        public override long ContentLength => -1;
        public string ExitMessage => null;
        public override WebHeaderCollection Headers => new WebHeaderCollection();
        public DateTime LastModified => DateTime.MinValue;
        public override Uri ResponseUri => null;
        public FtpStatusCode StatusCode => FtpStatusCode.Undefined;
        public string StatusDescription => null;
        public override bool SupportsHeaders => true;
        public string WelcomeMessage => null;
        public override Stream GetResponseStream() => Stream.Null;
        public override void Close() { }
    }
}
