using System.ComponentModel;
using System.Runtime.Serialization;

namespace System.Net
{
    [Serializable]
    public class WebException : InvalidOperationException, ISerializable
    {
        private readonly WebExceptionStatus _status = WebExceptionStatus.UnknownError;
        [NonSerialized] private readonly WebResponse _response;

        public WebException() { }

        public WebException(string message) : base(message) { }

        public WebException(string message, Exception innerException) : base(message, innerException) { }

        public WebException(string message, WebExceptionStatus status) : this(message, null, status, null) { }

        public WebException(string message, Exception innerException, WebExceptionStatus status, WebResponse response) : base(message, innerException)
        {
            _status = status;
            _response = response;
            if (innerException != null) HResult = innerException.HResult;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        protected WebException(SerializationInfo serializationInfo, StreamingContext streamingContext) : base(serializationInfo, streamingContext) { }

        public WebExceptionStatus Status => _status;

        public WebResponse Response => _response;

        void ISerializable.GetObjectData(SerializationInfo serializationInfo, StreamingContext streamingContext) => base.GetObjectData(serializationInfo, streamingContext);

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        public override void GetObjectData(SerializationInfo serializationInfo, StreamingContext streamingContext) => base.GetObjectData(serializationInfo, streamingContext);
    }

    [Serializable]
    public class ProtocolViolationException : InvalidOperationException, ISerializable
    {
        public ProtocolViolationException() { }

        public ProtocolViolationException(string message) : base(message) { }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        protected ProtocolViolationException(SerializationInfo serializationInfo, StreamingContext streamingContext) : base(serializationInfo, streamingContext) { }

        void ISerializable.GetObjectData(SerializationInfo serializationInfo, StreamingContext streamingContext) => base.GetObjectData(serializationInfo, streamingContext);

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        public override void GetObjectData(SerializationInfo serializationInfo, StreamingContext streamingContext) => base.GetObjectData(serializationInfo, streamingContext);
    }
}
