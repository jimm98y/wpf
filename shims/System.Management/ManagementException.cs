using System.ComponentModel;
using System.Runtime.Serialization;

namespace System.Management
{
    public partial class ManagementException : SystemException
    {
        private readonly ManagementStatus _errorCode;

        public ManagementException() : this(ManagementStatus.Failed, "", null) { }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        protected ManagementException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
            _errorCode = (ManagementStatus)info.GetInt32("errorCode");
        }

        public ManagementException(string message) : this(ManagementStatus.Failed, message, null) { }

        public ManagementException(string message, Exception innerException) : base(message, innerException)
        {
            _errorCode = ManagementStatus.Failed;
            HResult = (int)_errorCode;
        }

        internal ManagementException(ManagementStatus status) : this(status, Describe(status), null) { }

        internal ManagementException(ManagementStatus status, string message, Exception inner) : base(message, inner)
        {
            _errorCode = status;
            HResult = (int)status;
        }

        public ManagementStatus ErrorCode => _errorCode;

        public ManagementBaseObject ErrorInformation => null;

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            info.AddValue("errorCode", (int)_errorCode);
        }

        // WMI's own descriptions (FormatMessage of the status in wmiutils), trailing space included:
        // apps that match on the message see the text they see on Windows.
        private static string Describe(ManagementStatus status) => status switch
        {
            ManagementStatus.NotFound => "Not found ",
            ManagementStatus.InvalidNamespace => "Invalid namespace ",
            ManagementStatus.InvalidClass => "Invalid class ",
            ManagementStatus.InvalidQuery => "Invalid query ",
            ManagementStatus.InvalidParameter => "Invalid parameter ",
            ManagementStatus.NotSupported => "Not supported ",
            ManagementStatus.AccessDenied => "Access denied ",
            ManagementStatus.InvalidMethod => "Invalid method ",
            _ => "Generic failure ",
        };
    }
}
