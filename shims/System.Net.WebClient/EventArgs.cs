// The event arguments of WebClient's ...Async members: a result that reading rethrows the operation's
// error (or reports its cancellation), as AsyncCompletedEventArgs has every result do.

using System.ComponentModel;
using System.IO;

namespace System.Net
{
    public class DownloadDataCompletedEventArgs : AsyncCompletedEventArgs
    {
        private readonly byte[] _result;
        internal DownloadDataCompletedEventArgs(byte[] result, Exception exception, bool cancelled, object userToken) : base(exception, cancelled, userToken) => _result = result;
        public byte[] Result { get { RaiseExceptionIfNecessary(); return _result; } }
    }

    public class DownloadStringCompletedEventArgs : AsyncCompletedEventArgs
    {
        private readonly string _result;
        internal DownloadStringCompletedEventArgs(string result, Exception exception, bool cancelled, object userToken) : base(exception, cancelled, userToken) => _result = result;
        public string Result { get { RaiseExceptionIfNecessary(); return _result; } }
    }

    public class OpenReadCompletedEventArgs : AsyncCompletedEventArgs
    {
        private readonly Stream _result;
        internal OpenReadCompletedEventArgs(Stream result, Exception exception, bool cancelled, object userToken) : base(exception, cancelled, userToken) => _result = result;
        public Stream Result { get { RaiseExceptionIfNecessary(); return _result; } }
    }

    public class OpenWriteCompletedEventArgs : AsyncCompletedEventArgs
    {
        private readonly Stream _result;
        internal OpenWriteCompletedEventArgs(Stream result, Exception exception, bool cancelled, object userToken) : base(exception, cancelled, userToken) => _result = result;
        public Stream Result { get { RaiseExceptionIfNecessary(); return _result; } }
    }

    public class UploadDataCompletedEventArgs : AsyncCompletedEventArgs
    {
        private readonly byte[] _result;
        internal UploadDataCompletedEventArgs(byte[] result, Exception exception, bool cancelled, object userToken) : base(exception, cancelled, userToken) => _result = result;
        public byte[] Result { get { RaiseExceptionIfNecessary(); return _result; } }
    }

    public class UploadFileCompletedEventArgs : AsyncCompletedEventArgs
    {
        private readonly byte[] _result;
        internal UploadFileCompletedEventArgs(byte[] result, Exception exception, bool cancelled, object userToken) : base(exception, cancelled, userToken) => _result = result;
        public byte[] Result { get { RaiseExceptionIfNecessary(); return _result; } }
    }

    public class UploadStringCompletedEventArgs : AsyncCompletedEventArgs
    {
        private readonly string _result;
        internal UploadStringCompletedEventArgs(string result, Exception exception, bool cancelled, object userToken) : base(exception, cancelled, userToken) => _result = result;
        public string Result { get { RaiseExceptionIfNecessary(); return _result; } }
    }

    public class UploadValuesCompletedEventArgs : AsyncCompletedEventArgs
    {
        private readonly byte[] _result;
        internal UploadValuesCompletedEventArgs(byte[] result, Exception exception, bool cancelled, object userToken) : base(exception, cancelled, userToken) => _result = result;
        public byte[] Result { get { RaiseExceptionIfNecessary(); return _result; } }
    }

    public class DownloadProgressChangedEventArgs : ProgressChangedEventArgs
    {
        internal DownloadProgressChangedEventArgs(int progressPercentage, object userToken, long bytesReceived, long totalBytesToReceive) : base(progressPercentage, userToken)
        {
            BytesReceived = bytesReceived;
            TotalBytesToReceive = totalBytesToReceive;
        }

        public long BytesReceived { get; }
        public long TotalBytesToReceive { get; }
    }

    public class UploadProgressChangedEventArgs : ProgressChangedEventArgs
    {
        internal UploadProgressChangedEventArgs(int progressPercentage, object userToken, long bytesSent, long totalBytesToSend, long bytesReceived, long totalBytesToReceive)
            : base(progressPercentage, userToken)
        {
            BytesSent = bytesSent;
            TotalBytesToSend = totalBytesToSend;
            BytesReceived = bytesReceived;
            TotalBytesToReceive = totalBytesToReceive;
        }

        public long BytesReceived { get; }
        public long BytesSent { get; }
        public long TotalBytesToReceive { get; }
        public long TotalBytesToSend { get; }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public class WriteStreamClosedEventArgs : EventArgs
    {
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports the .NET Framework infrastructure and is not intended to be used directly from your code.", true)]
        public WriteStreamClosedEventArgs() { }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports the .NET Framework infrastructure and is not intended to be used directly from your code.", true)]
        public Exception Error => null;
    }
}
