// The option bags: plain state, as on Windows, where WMI reads them when a call is made.

using System.Collections.Specialized;
using System.ComponentModel;
using System.Security;

namespace System.Management
{
    public abstract partial class ManagementOptions
    {
        public static readonly TimeSpan InfiniteTimeout = TimeSpan.MaxValue;

        private ManagementNamedValueCollection _context;
        private TimeSpan _timeout = InfiniteTimeout;

        internal ManagementOptions(ManagementNamedValueCollection context, TimeSpan timeout)
        {
            _context = context;
            Timeout = timeout;
        }

        public ManagementNamedValueCollection Context
        {
            get => _context ??= new ManagementNamedValueCollection();
            set => _context = value;
        }

        public TimeSpan Timeout
        {
            get => _timeout;
            set
            {
                if (value.Ticks < 0) throw new ArgumentOutOfRangeException(nameof(value));
                _timeout = value;
            }
        }

        public abstract object Clone();

        internal ManagementNamedValueCollection CloneContext() => _context?.Clone();
    }

    public partial class EnumerationOptions : ManagementOptions
    {
        public EnumerationOptions() : this(null, InfiniteTimeout, 1, true, true, false, false, false, false, false) { }

        public EnumerationOptions(ManagementNamedValueCollection context, TimeSpan timeout, int blockSize, bool rewindable, bool returnImmediatley, bool useAmendedQualifiers, bool ensureLocatable, bool prototypeOnly, bool directRead, bool enumerateDeep)
            : base(context, timeout)
        {
            BlockSize = blockSize;
            Rewindable = rewindable;
            ReturnImmediately = returnImmediatley;
            UseAmendedQualifiers = useAmendedQualifiers;
            EnsureLocatable = ensureLocatable;
            PrototypeOnly = prototypeOnly;
            DirectRead = directRead;
            EnumerateDeep = enumerateDeep;
        }

        private int _blockSize = 1;

        public int BlockSize
        {
            get => _blockSize;
            set
            {
                if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
                _blockSize = value;
            }
        }

        public bool DirectRead { get; set; }
        public bool EnsureLocatable { get; set; }
        public bool EnumerateDeep { get; set; }
        public bool PrototypeOnly { get; set; }
        public bool ReturnImmediately { get; set; }
        public bool Rewindable { get; set; }
        public bool UseAmendedQualifiers { get; set; }

        public override object Clone() =>
            new EnumerationOptions(CloneContext(), Timeout, BlockSize, Rewindable, ReturnImmediately, UseAmendedQualifiers, EnsureLocatable, PrototypeOnly, DirectRead, EnumerateDeep);
    }

    public partial class ObjectGetOptions : ManagementOptions
    {
        public ObjectGetOptions() : this(null, InfiniteTimeout, false) { }

        public ObjectGetOptions(ManagementNamedValueCollection context) : this(context, InfiniteTimeout, false) { }

        public ObjectGetOptions(ManagementNamedValueCollection context, TimeSpan timeout, bool useAmendedQualifiers) : base(context, timeout)
        {
            UseAmendedQualifiers = useAmendedQualifiers;
        }

        public bool UseAmendedQualifiers { get; set; }

        public override object Clone() => new ObjectGetOptions(CloneContext(), Timeout, UseAmendedQualifiers);
    }

    public partial class ConnectionOptions : ManagementOptions
    {
        public ConnectionOptions()
            : this("", null, (string)null, null, ImpersonationLevel.Impersonate, AuthenticationLevel.Unchanged, false, null, InfiniteTimeout) { }

        public ConnectionOptions(string locale, string username, SecureString password, string authority, ImpersonationLevel impersonation, AuthenticationLevel authentication, bool enablePrivileges, ManagementNamedValueCollection context, TimeSpan timeout)
            : this(locale, username, (string)null, authority, impersonation, authentication, enablePrivileges, context, timeout)
        {
            _securePassword = password?.Copy();
        }

        public ConnectionOptions(string locale, string username, string password, string authority, ImpersonationLevel impersonation, AuthenticationLevel authentication, bool enablePrivileges, ManagementNamedValueCollection context, TimeSpan timeout)
            : base(context, timeout)
        {
            Locale = locale;
            Username = username;
            _password = password;
            Authority = authority;
            Impersonation = impersonation;
            Authentication = authentication;
            EnablePrivileges = enablePrivileges;
        }

        private string _locale = "";
        private string _password;
        private SecureString _securePassword;

        public AuthenticationLevel Authentication { get; set; }
        public string Authority { get; set; }
        public bool EnablePrivileges { get; set; }
        public ImpersonationLevel Impersonation { get; set; }

        public string Locale
        {
            get => _locale;
            set => _locale = value ?? "";
        }

        public string Password
        {
            set { _password = value; _securePassword = null; }
        }

        public SecureString SecurePassword
        {
            set { _securePassword = value?.Copy(); _password = null; }
        }

        public string Username { get; set; }

        public override object Clone()
        {
            var o = new ConnectionOptions(Locale, Username, _password, Authority, Impersonation, Authentication, EnablePrivileges, CloneContext(), Timeout);
            o._securePassword = _securePassword?.Copy();
            return o;
        }
    }

    public partial class ManagementNamedValueCollection : NameObjectCollectionBase
    {
        public ManagementNamedValueCollection() { }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        protected ManagementNamedValueCollection(Runtime.Serialization.SerializationInfo info, Runtime.Serialization.StreamingContext context) : base(info, context) { }

        public object this[string name] => BaseGet(name);

        public void Add(string name, object value)
        {
            BaseRemove(name);
            BaseAdd(name, value);
        }

        public ManagementNamedValueCollection Clone()
        {
            var c = new ManagementNamedValueCollection();
            foreach (string name in this)
            {
                object v = BaseGet(name);
                c.BaseAdd(name, v is ICloneable cl ? cl.Clone() : v);
            }
            return c;
        }

        public void Remove(string name) => BaseRemove(name);

        public void RemoveAll() => BaseClear();
    }
}
