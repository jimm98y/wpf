// WebResponse's base: what a subclass does not override is not implemented, except Close, which does
// nothing, and the few answers every response has (not from a cache, not mutually authenticated).

using System.IO;
using System.Runtime.Serialization;

using System.ComponentModel;

namespace System.Net
{
    public abstract class WebResponse : MarshalByRefObject, ISerializable, IDisposable
    {
        protected WebResponse() { }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Serialization has been deprecated for WebResponse.")]
        protected WebResponse(SerializationInfo serializationInfo, StreamingContext streamingContext) => throw new PlatformNotSupportedException();

        [Obsolete("Serialization has been deprecated for WebResponse.")]
        void ISerializable.GetObjectData(SerializationInfo serializationInfo, StreamingContext streamingContext) => throw new PlatformNotSupportedException();

        [Obsolete("Serialization has been deprecated for WebResponse.")]
        protected virtual void GetObjectData(SerializationInfo serializationInfo, StreamingContext streamingContext) => throw new PlatformNotSupportedException();

        public virtual void Close() { }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;
            try { Close(); }
            catch { }
        }

        public virtual bool IsFromCache => false;
        public virtual bool IsMutuallyAuthenticated => false;
        public virtual long ContentLength { get => throw WebRequest.PropertyNotImplemented(); set => throw WebRequest.PropertyNotImplemented(); }
        public virtual string ContentType { get => throw WebRequest.PropertyNotImplemented(); set => throw WebRequest.PropertyNotImplemented(); }
        public virtual Stream GetResponseStream() => throw WebRequest.MethodNotImplemented();
        public virtual Uri ResponseUri => throw WebRequest.PropertyNotImplemented();
        public virtual WebHeaderCollection Headers => throw WebRequest.PropertyNotImplemented();
        public virtual bool SupportsHeaders => false;
    }
}
