// Windows identities on a machine that has none: the Mac user is a local Windows account of this
// computer, as it would be on a workgroup PC. Its name is MACHINE\user (Environment.UserDomainName is
// the machine name off Windows too), its SID the machine's account domain plus a RID made from the uid
// -- 501, the first user macOS creates, is RID 1001, the first local user Windows creates -- and its
// groups the ones a local administrator's token carries, Administrators only for a member of macOS's
// admin group. uid 0 is NT AUTHORITY\SYSTEM. The well-known SIDs are Windows' own, with their names,
// and translate both ways; another local user translates through the user database (getpwnam). What
// Windows cannot map either -- a domain account, a name nobody has -- throws IdentityNotMappedException
// as it does there. There is one user and so nothing to impersonate: RunImpersonated runs the action.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Microsoft.Win32.SafeHandles
{
    public sealed partial class SafeAccessTokenHandle : SafeHandle
    {
        public SafeAccessTokenHandle() : base(IntPtr.Zero, true) { }
        public SafeAccessTokenHandle(IntPtr handle) : base(IntPtr.Zero, true) { SetHandle(handle); }
        public static SafeAccessTokenHandle InvalidHandle => new SafeAccessTokenHandle(IntPtr.Zero);
        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);
        // The tokens are this assembly's pseudo-handles (System.Security.Principal.Accounts): nothing to close.
        protected override bool ReleaseHandle() => true;
    }
}

namespace System.Security.Principal
{
    public sealed partial class IdentityNotMappedException : SystemException
    {
        private IdentityReferenceCollection _unmapped;

        public IdentityNotMappedException() : this("Some or all identity references could not be translated.") { }
        public IdentityNotMappedException(string message) : base(message) { }
        public IdentityNotMappedException(string message, Exception inner) : base(message, inner) { }
        internal IdentityNotMappedException(string message, IdentityReferenceCollection unmapped) : base(message) { _unmapped = unmapped; }

        public IdentityReferenceCollection UnmappedIdentities => _unmapped ??= new IdentityReferenceCollection();

        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        public override void GetObjectData(SerializationInfo serializationInfo, StreamingContext streamingContext)
            => base.GetObjectData(serializationInfo, streamingContext);
    }

    public abstract partial class IdentityReference
    {
        internal IdentityReference() { }
        public abstract string Value { get; }
        public abstract override bool Equals(object o);
        public abstract override int GetHashCode();
        public abstract bool IsValidTargetType(Type targetType);
        public abstract override string ToString();
        public abstract IdentityReference Translate(Type targetType);

        public static bool operator ==(IdentityReference left, IdentityReference right)
            => left is null ? right is null : right is not null && left.Equals(right);
        public static bool operator !=(IdentityReference left, IdentityReference right) => !(left == right);
    }

    public partial class IdentityReferenceCollection : ICollection<IdentityReference>
    {
        private readonly List<IdentityReference> _items;

        public IdentityReferenceCollection() : this(0) { }
        public IdentityReferenceCollection(int capacity) { _items = new List<IdentityReference>(capacity); }

        public int Count => _items.Count;
        public IdentityReference this[int index] { get => _items[index]; set => _items[index] = value ?? throw new ArgumentNullException(nameof(value)); }
        bool ICollection<IdentityReference>.IsReadOnly => false;

        public void Add(IdentityReference identity) => _items.Add(identity ?? throw new ArgumentNullException(nameof(identity)));
        public void Clear() => _items.Clear();
        public bool Contains(IdentityReference identity) => _items.Contains(identity ?? throw new ArgumentNullException(nameof(identity)));
        public void CopyTo(IdentityReference[] array, int offset) => _items.CopyTo(array, offset);
        public bool Remove(IdentityReference identity) => _items.Remove(identity ?? throw new ArgumentNullException(nameof(identity)));
        public IEnumerator<IdentityReference> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public IdentityReferenceCollection Translate(Type targetType) => Translate(targetType, false);

        /// <summary>Each reference in <paramref name="targetType"/>; one that has no translation is kept
        /// as it was, or with <paramref name="forceSuccess"/> they are all reported together.</summary>
        public IdentityReferenceCollection Translate(Type targetType, bool forceSuccess)
        {
            if (targetType is null) throw new ArgumentNullException(nameof(targetType));
            if (targetType != typeof(NTAccount) && targetType != typeof(SecurityIdentifier))
                throw new ArgumentException("Only NTAccount and SecurityIdentifier are valid target types.", nameof(targetType));
            var result = new IdentityReferenceCollection(Count);
            var unmapped = new IdentityReferenceCollection();
            foreach (IdentityReference r in _items)
            {
                IdentityReference t = r.GetType() == targetType ? r : Accounts.TryTranslate(r, targetType);
                if (t is null) { unmapped.Add(r); t = r; }
                result.Add(t);
            }
            if (forceSuccess && unmapped.Count > 0)
                throw new IdentityNotMappedException("Some or all identity references could not be translated.", unmapped);
            return result;
        }
    }

    public sealed partial class NTAccount : IdentityReference
    {
        private readonly string _name;

        public NTAccount(string name)
        {
            if (name is null) throw new ArgumentNullException(nameof(name));
            if (name.Length == 0) throw new ArgumentException("Argument cannot be the empty string.", nameof(name));
            if (name.Length > 512) throw new ArgumentException("Account name is too long.", nameof(name));
            _name = name;
        }

        public NTAccount(string domainName, string accountName)
        {
            if (accountName is null) throw new ArgumentNullException(nameof(accountName));
            if (accountName.Length == 0) throw new ArgumentException("Argument cannot be the empty string.", nameof(accountName));
            if (accountName.Length > 256) throw new ArgumentException("Account name is too long.", nameof(accountName));
            if (domainName is not null && domainName.Length > 255) throw new ArgumentException("Domain name is too long.", nameof(domainName));
            _name = string.IsNullOrEmpty(domainName) ? accountName : domainName + "\\" + accountName;
        }

        public override string Value => _name;
        public override string ToString() => _name;
        public override bool Equals([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] object o) => o is NTAccount a && string.Equals(a._name, _name, StringComparison.OrdinalIgnoreCase);
        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(_name);
        public override bool IsValidTargetType(Type targetType) => targetType == typeof(SecurityIdentifier) || targetType == typeof(NTAccount);

        public override IdentityReference Translate(Type targetType)
        {
            if (targetType is null) throw new ArgumentNullException(nameof(targetType));
            if (targetType == typeof(NTAccount)) return this;
            if (targetType != typeof(SecurityIdentifier))
                throw new ArgumentException("Only NTAccount and SecurityIdentifier are valid target types.", nameof(targetType));
            return Accounts.TryTranslate(this, targetType) ?? throw Unmapped(this);
        }

        internal static IdentityNotMappedException Unmapped(IdentityReference r)
        {
            var c = new IdentityReferenceCollection(1);
            c.Add(r);
            return new IdentityNotMappedException("Some or all identity references could not be translated.", c);
        }

        public static bool operator ==(NTAccount left, NTAccount right) => left is null ? right is null : left.Equals(right);
        public static bool operator !=(NTAccount left, NTAccount right) => !(left == right);
    }

    public sealed partial class SecurityIdentifier : IdentityReference, IComparable<SecurityIdentifier>
    {
        public static readonly int MaxBinaryLength = 1 + 1 + 6 + 15 * 4;
        public static readonly int MinBinaryLength = 1 + 1 + 6;

        private readonly ulong _authority;
        private readonly uint[] _sub;
        private string _sddl;

        internal SecurityIdentifier(ulong authority, params uint[] subAuthorities)
        {
            if (subAuthorities.Length > 15) throw new ArgumentOutOfRangeException(nameof(subAuthorities));
            _authority = authority;
            _sub = subAuthorities;
        }

        public SecurityIdentifier(byte[] binaryForm, int offset)
        {
            if (binaryForm is null) throw new ArgumentNullException(nameof(binaryForm));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (binaryForm.Length - offset < MinBinaryLength) throw new ArgumentOutOfRangeException(nameof(binaryForm), "Array is too small.");
            if (binaryForm[offset] != 1) throw new ArgumentException("SIDs with revision other than 1 are not supported.", nameof(binaryForm));
            int count = binaryForm[offset + 1];
            if (count > 15) throw new ArgumentException("The binary form of the SID is invalid.", nameof(binaryForm));
            if (binaryForm.Length - offset < MinBinaryLength + 4 * count) throw new ArgumentException("The binary form of the SID is invalid.", nameof(binaryForm));
            for (int i = 0; i < 6; i++) _authority = (_authority << 8) | binaryForm[offset + 2 + i];
            _sub = new uint[count];
            for (int i = 0; i < count; i++) _sub[i] = BitConverter.ToUInt32(binaryForm, offset + 8 + 4 * i);
        }

        public SecurityIdentifier(IntPtr binaryForm) : this(Read(binaryForm), 0) { }

        private static byte[] Read(IntPtr p)
        {
            if (p == IntPtr.Zero) throw new ArgumentNullException("binaryForm");
            int count = Marshal.ReadByte(p, 1);
            var b = new byte[MinBinaryLength + 4 * Math.Min(count, 15)];
            Marshal.Copy(p, b, 0, b.Length);
            return b;
        }

        public SecurityIdentifier(WellKnownSidType sidType, SecurityIdentifier domainSid)
        {
            SecurityIdentifier s = Accounts.WellKnown(sidType, domainSid);
            _authority = s._authority;
            _sub = s._sub;
        }

        public SecurityIdentifier(string sddlForm)
        {
            if (sddlForm is null) throw new ArgumentNullException(nameof(sddlForm));
            SecurityIdentifier s = Accounts.ParseSddl(sddlForm) ?? throw new ArgumentException("Value was invalid.", nameof(sddlForm));
            _authority = s._authority;
            _sub = s._sub;
        }

        internal ulong Authority => _authority;
        internal int SubCount => _sub.Length;
        internal uint Sub(int i) => _sub[i];
        internal uint LastSub => _sub.Length == 0 ? 0 : _sub[^1];

        public int BinaryLength => MinBinaryLength + 4 * _sub.Length;

        public override string Value
            => _sddl ??= "S-1-" + (_authority < (1UL << 32) ? _authority.ToString(CultureInfo.InvariantCulture) : "0x" + _authority.ToString("X12", CultureInfo.InvariantCulture))
                         + (_sub.Length == 0 ? "" : "-" + string.Join("-", Array.ConvertAll(_sub, v => v.ToString(CultureInfo.InvariantCulture))));

        public override string ToString() => Value;

        /// <summary>The account domain an account SID (S-1-5-21-a-b-c-...) belongs to: S-1-5-21-a-b-c.</summary>
        public SecurityIdentifier AccountDomainSid => IsAccountSid() ? new SecurityIdentifier(5, _sub[0], _sub[1], _sub[2], _sub[3]) : null;

        public bool IsAccountSid() => _authority == 5 && _sub.Length >= 4 && _sub[0] == 21;

        // EqualDomainSid: account SIDs of one domain, or both BUILTIN.
        public bool IsEqualDomainSid(SecurityIdentifier sid)
        {
            if (sid is null) return false;
            if (IsBuiltin || sid.IsBuiltin) return IsBuiltin && sid.IsBuiltin;
            return AccountDomainSid is SecurityIdentifier mine && mine.Equals(sid.AccountDomainSid);
        }

        private bool IsBuiltin => _authority == 5 && _sub.Length >= 1 && _sub[0] == 32;

        public bool IsWellKnown(WellKnownSidType type)
        {
            SecurityIdentifier w;
            try { w = Accounts.WellKnown(type, AccountDomainSid); }
            catch (ArgumentException) { return false; }
            return Equals(w);
        }

        public void GetBinaryForm(byte[] binaryForm, int offset)
        {
            if (binaryForm is null) throw new ArgumentNullException(nameof(binaryForm));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (binaryForm.Length - offset < BinaryLength) throw new ArgumentOutOfRangeException(nameof(binaryForm), "Destination array is not long enough.");
            binaryForm[offset] = 1;
            binaryForm[offset + 1] = (byte)_sub.Length;
            for (int i = 0; i < 6; i++) binaryForm[offset + 2 + i] = (byte)(_authority >> (8 * (5 - i)));
            for (int i = 0; i < _sub.Length; i++) BitConverter.TryWriteBytes(binaryForm.AsSpan(offset + 8 + 4 * i), _sub[i]);
        }

        // As Windows' own: identifier authority, then the number of subauthorities, then each in turn.
        public int CompareTo(SecurityIdentifier sid)
        {
            if (sid is null) throw new ArgumentNullException(nameof(sid));
            if (_authority != sid._authority) return _authority < sid._authority ? -1 : 1;
            if (_sub.Length != sid._sub.Length) return _sub.Length < sid._sub.Length ? -1 : 1;
            for (int i = 0; i < _sub.Length; i++)
                if (_sub[i] != sid._sub[i]) return _sub[i] < sid._sub[i] ? -1 : 1;
            return 0;
        }

        public override bool Equals([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] object o) => o is SecurityIdentifier s && Equals(s);
        public bool Equals(SecurityIdentifier sid) => sid is not null && CompareTo(sid) == 0;

        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(_authority);
            foreach (uint v in _sub) h.Add(v);
            return h.ToHashCode();
        }

        public override bool IsValidTargetType(Type targetType) => targetType == typeof(SecurityIdentifier) || targetType == typeof(NTAccount);

        public override IdentityReference Translate(Type targetType)
        {
            if (targetType is null) throw new ArgumentNullException(nameof(targetType));
            if (targetType == typeof(SecurityIdentifier)) return this;
            if (targetType != typeof(NTAccount))
                throw new ArgumentException("Only NTAccount and SecurityIdentifier are valid target types.", nameof(targetType));
            return Accounts.TryTranslate(this, targetType) ?? throw NTAccount.Unmapped(this);
        }

        public static bool operator ==(SecurityIdentifier left, SecurityIdentifier right) => left is null ? right is null : left.Equals(right);
        public static bool operator !=(SecurityIdentifier left, SecurityIdentifier right) => !(left == right);
    }

    public partial class WindowsIdentity : ClaimsIdentity, IDisposable, IDeserializationCallback, ISerializable
    {
        public new const string DefaultIssuer = "AD AUTHORITY";

        private readonly Accounts.Account _account;
        private readonly string _authType;
        private readonly WindowsAccountType _accountType;
        private readonly bool _authenticated;

        private WindowsIdentity(Accounts.Account account, string authType, WindowsAccountType accountType, bool authenticated)
            : base(null, null, null, ClaimTypes.Name, ClaimTypes.GroupSid)
        {
            _account = account;
            _authType = authType;
            _accountType = accountType;
            _authenticated = authenticated;
        }

        public WindowsIdentity(IntPtr userToken) : this(FromToken(userToken), "NTLM", WindowsAccountType.Normal, true) { }
        public WindowsIdentity(IntPtr userToken, string type) : this(FromToken(userToken), type, WindowsAccountType.Normal, true) { }
        public WindowsIdentity(IntPtr userToken, string type, WindowsAccountType acctType) : this(FromToken(userToken), type, acctType, true) { }
        public WindowsIdentity(IntPtr userToken, string type, WindowsAccountType acctType, bool isAuthenticated) : this(FromToken(userToken), type, acctType, isAuthenticated) { }

        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        public WindowsIdentity(SerializationInfo info, StreamingContext context) : base(null, null, null, ClaimTypes.Name, ClaimTypes.GroupSid)
            => throw new PlatformNotSupportedException("Serialization of WindowsIdentity is not supported.");

        protected WindowsIdentity(WindowsIdentity identity) : base(identity, null, identity?._authType, ClaimTypes.Name, ClaimTypes.GroupSid)
        {
            if (identity is null) throw new ArgumentNullException(nameof(identity));
            _account = identity._account;
            _authType = identity._authType;
            _accountType = identity._accountType;
            _authenticated = identity._authenticated;
        }

        /// <summary>The account a user principal name (user@domain) names: on this machine, a local user's.
        /// Anything else is an account Windows would have had to ask a domain controller for.</summary>
        public WindowsIdentity(string sUserPrincipalName)
            : this(Accounts.FromUpn(sUserPrincipalName) ?? throw new SecurityException("The user name or password is incorrect."), "Kerberos", WindowsAccountType.Normal, true) { }

        private static Accounts.Account FromToken(IntPtr token)
            => Accounts.FromToken(token) ?? throw new ArgumentException("Invalid token for impersonation - it cannot be duplicated.", "userToken");

        public SafeAccessTokenHandle AccessToken => new SafeAccessTokenHandle(_account.Token);
        public sealed override string AuthenticationType => _authType;
        public SecurityIdentifier User => _account.Sid;
        public SecurityIdentifier Owner => _account.Sid;
        public override string Name => _account.Name;
        public virtual IntPtr Token => _account.Token;
        public TokenImpersonationLevel ImpersonationLevel => TokenImpersonationLevel.None;
        public virtual bool IsAnonymous => _accountType == WindowsAccountType.Anonymous || _account.Sid.IsWellKnown(WellKnownSidType.AnonymousSid);
        public virtual bool IsGuest => _accountType == WindowsAccountType.Guest || _account.Sid.IsAccountSid() && _account.Sid.LastSub == 501;
        public virtual bool IsSystem => _accountType == WindowsAccountType.System || _account.Sid.IsWellKnown(WellKnownSidType.LocalSystemSid);
        public override bool IsAuthenticated => _authenticated && !IsAnonymous;

        public IdentityReferenceCollection Groups
        {
            get
            {
                if (IsAnonymous) return null;
                var c = new IdentityReferenceCollection(_account.Groups.Length);
                foreach (SecurityIdentifier g in _account.Groups) c.Add(g);
                return c;
            }
        }

        public virtual IEnumerable<Claim> UserClaims
        {
            get
            {
                if (IsAnonymous) yield break;
                yield return new Claim(ClaimTypes.PrimarySid, _account.Sid.Value, ClaimValueTypes.String, DefaultIssuer, DefaultIssuer, this);
                foreach (SecurityIdentifier g in _account.Groups)
                    yield return new Claim(ClaimTypes.GroupSid, g.Value, ClaimValueTypes.String, DefaultIssuer, DefaultIssuer, this);
            }
        }

        public virtual IEnumerable<Claim> DeviceClaims { get { yield break; } }

        public override IEnumerable<Claim> Claims
        {
            get
            {
                if (!IsAnonymous && _account.Name.Length > 0)
                    yield return new Claim(NameClaimType, _account.Name, ClaimValueTypes.String, DefaultIssuer, DefaultIssuer, this);
                foreach (Claim c in base.Claims) yield return c;
                foreach (Claim c in UserClaims) yield return c;
                foreach (Claim c in DeviceClaims) yield return c;
            }
        }

        public override ClaimsIdentity Clone() => new WindowsIdentity(this);
        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing) { }

        public static WindowsIdentity GetAnonymous() => new WindowsIdentity(Accounts.Anonymous, "", WindowsAccountType.Anonymous, false);
        public static WindowsIdentity GetCurrent() => new WindowsIdentity(Accounts.Current, "NTLM", Accounts.Current.Sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ? WindowsAccountType.System : WindowsAccountType.Normal, true);
        // Never impersonating: there is one user.
        public static WindowsIdentity GetCurrent(bool ifImpersonating) => ifImpersonating ? null : GetCurrent();
        public static WindowsIdentity GetCurrent(TokenAccessLevels desiredAccess) => GetCurrent();

        public static void RunImpersonated(SafeAccessTokenHandle safeAccessTokenHandle, Action action)
        {
            if (action is null) throw new ArgumentNullException(nameof(action));
            Check(safeAccessTokenHandle);
            action();
        }

        public static T RunImpersonated<T>(SafeAccessTokenHandle safeAccessTokenHandle, Func<T> func)
        {
            if (func is null) throw new ArgumentNullException(nameof(func));
            Check(safeAccessTokenHandle);
            return func();
        }

        public static Task RunImpersonatedAsync(SafeAccessTokenHandle safeAccessTokenHandle, Func<Task> func)
            => RunImpersonated(safeAccessTokenHandle, func);

        public static Task<T> RunImpersonatedAsync<T>(SafeAccessTokenHandle safeAccessTokenHandle, Func<Task<T>> func)
            => RunImpersonated(safeAccessTokenHandle, func);

        // An invalid handle means "as yourself" (Windows reverts to self); a token of ours is the one user too.
        private static void Check(SafeAccessTokenHandle h)
        {
            if (h is null) throw new ArgumentNullException("safeAccessTokenHandle");
            if (!h.IsInvalid && Accounts.FromToken(h.DangerousGetHandle()) is null)
                throw new ArgumentException("Invalid token for impersonation - it cannot be duplicated.", "safeAccessTokenHandle");
        }

        void IDeserializationCallback.OnDeserialization(object sender) => throw new PlatformNotSupportedException();
        void ISerializable.GetObjectData(SerializationInfo info, StreamingContext context) => throw new PlatformNotSupportedException();
    }

    public partial class WindowsPrincipal : ClaimsPrincipal
    {
        private readonly WindowsIdentity _identity;

        public WindowsPrincipal(WindowsIdentity ntIdentity) : base(ntIdentity ?? throw new ArgumentNullException(nameof(ntIdentity)))
        {
            _identity = ntIdentity;
        }

        public override IIdentity Identity => _identity;
        public virtual IEnumerable<Claim> UserClaims => _identity.UserClaims;
        public virtual IEnumerable<Claim> DeviceClaims => _identity.DeviceClaims;

        public virtual bool IsInRole(WindowsBuiltInRole role)
        {
            if (role < WindowsBuiltInRole.Administrator || role > WindowsBuiltInRole.Replicator)
                throw new ArgumentException($"Enum value '{role}' is not valid.", nameof(role));
            return IsInRole((int)role);
        }

        public virtual bool IsInRole(int rid) => IsInRole(new SecurityIdentifier(5, 32, (uint)rid));

        public virtual bool IsInRole(SecurityIdentifier sid)
        {
            if (sid is null) throw new ArgumentNullException(nameof(sid));
            if (_identity.IsAnonymous) return false;
            if (sid.Equals(_identity.User)) return true;
            foreach (IdentityReference g in _identity.Groups) if (sid.Equals(g)) return true;
            return false;
        }

        /// <summary>A group by its name, as Windows resolves it (LookupAccountName), or a role claim.</summary>
        public override bool IsInRole(string role)
        {
            if (role is null || role.Length == 0) return false;
            if (Accounts.TryTranslate(new NTAccount(role), typeof(SecurityIdentifier)) is SecurityIdentifier sid && IsInRole(sid)) return true;
            return base.IsInRole(role);
        }
    }

    /// <summary>The accounts of this machine and Windows' well-known ones.</summary>
    internal static class Accounts
    {
        internal sealed class Account
        {
            public SecurityIdentifier Sid;
            public string Name;
            public SecurityIdentifier[] Groups;
            public IntPtr Token;
        }

        // A token is a pseudo-handle: one for the user this process runs as, one for the anonymous logon.
        private static readonly IntPtr s_currentToken = new IntPtr(0x7E57_0001), s_anonymousToken = new IntPtr(0x7E57_0002);

        internal static readonly SecurityIdentifier Builtin = new SecurityIdentifier(5, 32);
        private const string NtAuthority = "NT AUTHORITY", BuiltinDomain = "BUILTIN";

        private static string Machine => Environment.MachineName.ToUpperInvariant();

        /// <summary>This machine's account domain, S-1-5-21-a-b-c: Windows draws a, b and c at random when it
        /// is installed; here they are a hash of the machine's name, so they stay what they were.</summary>
        internal static readonly SecurityIdentifier MachineDomain = MakeMachineDomain();

        private static SecurityIdentifier MakeMachineDomain()
        {
            byte[] h = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("WpfWebGpu machine SID " + Machine));
            return new SecurityIdentifier(5, 21, BitConverter.ToUInt32(h, 0), BitConverter.ToUInt32(h, 4), BitConverter.ToUInt32(h, 8));
        }

        private static SecurityIdentifier Domain(uint rid) => new SecurityIdentifier(5, 21, MachineDomain.Sub(1), MachineDomain.Sub(2), MachineDomain.Sub(3), rid);

        // uid <-> RID: uid 501 is RID 1001, as the first user of each is.
        private static uint RidOfUid(uint uid) => uid + 500;
        private static bool UidOfRid(uint rid, out uint uid) { uid = rid - 500; return rid >= 1000; }

        internal static readonly Account Current = MakeCurrent();
        internal static readonly Account Anonymous = new Account
        {
            Sid = new SecurityIdentifier(5, 7), Name = "", Groups = Array.Empty<SecurityIdentifier>(), Token = s_anonymousToken,
        };

        private static Account MakeCurrent()
        {
            uint uid = Native.Uid();
            if (uid == 0)
                return new Account
                {
                    Sid = new SecurityIdentifier(5, 18), Name = NtAuthority + "\\SYSTEM", Token = s_currentToken,
                    Groups = new[] { new SecurityIdentifier(5, 32, 544), new SecurityIdentifier(1, 0), new SecurityIdentifier(5, 11), new SecurityIdentifier(16, 16384) },
                };
            var groups = new List<SecurityIdentifier>
            {
                Domain(513),                          // MACHINE\None: a workgroup PC's local users
                new SecurityIdentifier(1, 0),         // Everyone
            };
            if (Native.InAdminGroup()) groups.Add(new SecurityIdentifier(5, 32, 544));   // BUILTIN\Administrators
            groups.Add(new SecurityIdentifier(5, 32, 545));                               // BUILTIN\Users
            groups.Add(new SecurityIdentifier(5, 4));                                     // INTERACTIVE
            groups.Add(new SecurityIdentifier(2, 1));                                     // CONSOLE LOGON
            groups.Add(new SecurityIdentifier(5, 11));                                    // Authenticated Users
            groups.Add(new SecurityIdentifier(5, 15));                                    // This Organization
            groups.Add(new SecurityIdentifier(2, 0));                                     // LOCAL
            groups.Add(new SecurityIdentifier(5, 64, 10));                                // NTLM Authentication
            return new Account
            {
                Sid = Domain(RidOfUid(uid)), Name = Environment.UserDomainName + "\\" + Environment.UserName,
                Groups = groups.ToArray(), Token = s_currentToken,
            };
        }

        internal static Account FromToken(IntPtr token)
            => token == s_currentToken ? Current : token == s_anonymousToken ? Anonymous : null;

        internal static Account FromUpn(string upn)
        {
            if (upn is null) throw new ArgumentNullException("sUserPrincipalName");
            string user = upn.Split('@')[0];
            if (string.Equals(user, Environment.UserName, StringComparison.OrdinalIgnoreCase)) return Current;
            return null;
        }

        // ---- Windows' well-known SIDs and their names ------------------------------------------------

        private readonly struct Known
        {
            public readonly WellKnownSidType Type; public readonly ulong Authority; public readonly uint[] Sub;
            public readonly bool DomainRelative; public readonly string Domain, Name;
            public Known(WellKnownSidType t, ulong a, uint[] s, string domain, string name, bool rel = false)
            { Type = t; Authority = a; Sub = s; Domain = domain; Name = name; DomainRelative = rel; }
        }

        private static Known K(WellKnownSidType t, ulong a, string domain, string name, params uint[] s) => new Known(t, a, s, domain, name);
        private static Known B(WellKnownSidType t, uint rid, string name) => new Known(t, 5, new[] { 32u, rid }, BuiltinDomain, name);
        private static Known D(WellKnownSidType t, uint rid, string name) => new Known(t, 5, new[] { rid }, null, name, rel: true);

        private static readonly Known[] s_known =
        {
            K(WellKnownSidType.NullSid, 0, "", "NULL SID", 0),
            K(WellKnownSidType.WorldSid, 1, "", "Everyone", 0),
            K(WellKnownSidType.LocalSid, 2, "", "LOCAL", 0),
            K(WellKnownSidType.WinLocalLogonSid, 2, "", "LOCAL", 0),
            K(WellKnownSidType.WinConsoleLogonSid, 2, "", "CONSOLE LOGON", 1),
            K(WellKnownSidType.CreatorOwnerSid, 3, "", "CREATOR OWNER", 0),
            K(WellKnownSidType.CreatorGroupSid, 3, "", "CREATOR GROUP", 1),
            K(WellKnownSidType.CreatorOwnerServerSid, 3, "", "CREATOR OWNER SERVER", 2),
            K(WellKnownSidType.CreatorGroupServerSid, 3, "", "CREATOR GROUP SERVER", 3),
            K(WellKnownSidType.WinCreatorOwnerRightsSid, 3, "", "OWNER RIGHTS", 4),
            K(WellKnownSidType.NTAuthoritySid, 5, NtAuthority, ""),
            K(WellKnownSidType.DialupSid, 5, NtAuthority, "DIALUP", 1),
            K(WellKnownSidType.NetworkSid, 5, NtAuthority, "NETWORK", 2),
            K(WellKnownSidType.BatchSid, 5, NtAuthority, "BATCH", 3),
            K(WellKnownSidType.InteractiveSid, 5, NtAuthority, "INTERACTIVE", 4),
            K(WellKnownSidType.ServiceSid, 5, NtAuthority, "SERVICE", 6),
            K(WellKnownSidType.AnonymousSid, 5, NtAuthority, "ANONYMOUS LOGON", 7),
            K(WellKnownSidType.ProxySid, 5, NtAuthority, "PROXY", 8),
            K(WellKnownSidType.EnterpriseControllersSid, 5, NtAuthority, "ENTERPRISE DOMAIN CONTROLLERS", 9),
            K(WellKnownSidType.SelfSid, 5, NtAuthority, "SELF", 10),
            K(WellKnownSidType.AuthenticatedUserSid, 5, NtAuthority, "Authenticated Users", 11),
            K(WellKnownSidType.RestrictedCodeSid, 5, NtAuthority, "RESTRICTED", 12),
            K(WellKnownSidType.TerminalServerSid, 5, NtAuthority, "TERMINAL SERVER USER", 13),
            K(WellKnownSidType.RemoteLogonIdSid, 5, NtAuthority, "REMOTE INTERACTIVE LOGON", 14),
            K(WellKnownSidType.ThisOrganizationSid, 5, NtAuthority, "This Organization", 15),
            K(WellKnownSidType.WinIUserSid, 5, NtAuthority, "IUSR", 17),
            K(WellKnownSidType.LocalSystemSid, 5, NtAuthority, "SYSTEM", 18),
            K(WellKnownSidType.LocalServiceSid, 5, NtAuthority, "LOCAL SERVICE", 19),
            K(WellKnownSidType.NetworkServiceSid, 5, NtAuthority, "NETWORK SERVICE", 20),
            K(WellKnownSidType.WinNewEnterpriseReadonlyControllersSid, 5, NtAuthority, "ENTERPRISE READ-ONLY DOMAIN CONTROLLERS BETA", 22),
            K(WellKnownSidType.WinWriteRestrictedCodeSid, 5, NtAuthority, "WRITE RESTRICTED", 33),
            K(WellKnownSidType.NtlmAuthenticationSid, 5, NtAuthority, "NTLM Authentication", 64, 10),
            K(WellKnownSidType.SChannelAuthenticationSid, 5, NtAuthority, "SChannel Authentication", 64, 14),
            K(WellKnownSidType.DigestAuthenticationSid, 5, NtAuthority, "Digest Authentication", 64, 21),
            K(WellKnownSidType.WinThisOrganizationCertificateSid, 5, NtAuthority, "This Organization Certificate", 65, 1),
            K(WellKnownSidType.OtherOrganizationSid, 5, NtAuthority, "Other Organization", 1000),
            K(WellKnownSidType.BuiltinDomainSid, 5, BuiltinDomain, BuiltinDomain, 32),
            B(WellKnownSidType.BuiltinAdministratorsSid, 544, "Administrators"),
            B(WellKnownSidType.BuiltinUsersSid, 545, "Users"),
            B(WellKnownSidType.BuiltinGuestsSid, 546, "Guests"),
            B(WellKnownSidType.BuiltinPowerUsersSid, 547, "Power Users"),
            B(WellKnownSidType.BuiltinAccountOperatorsSid, 548, "Account Operators"),
            B(WellKnownSidType.BuiltinSystemOperatorsSid, 549, "Server Operators"),
            B(WellKnownSidType.BuiltinPrintOperatorsSid, 550, "Print Operators"),
            B(WellKnownSidType.BuiltinBackupOperatorsSid, 551, "Backup Operators"),
            B(WellKnownSidType.BuiltinReplicatorSid, 552, "Replicator"),
            B(WellKnownSidType.BuiltinPreWindows2000CompatibleAccessSid, 554, "Pre-Windows 2000 Compatible Access"),
            B(WellKnownSidType.BuiltinRemoteDesktopUsersSid, 555, "Remote Desktop Users"),
            B(WellKnownSidType.BuiltinNetworkConfigurationOperatorsSid, 556, "Network Configuration Operators"),
            B(WellKnownSidType.BuiltinIncomingForestTrustBuildersSid, 557, "Incoming Forest Trust Builders"),
            B(WellKnownSidType.BuiltinPerformanceMonitoringUsersSid, 558, "Performance Monitor Users"),
            B(WellKnownSidType.BuiltinPerformanceLoggingUsersSid, 559, "Performance Log Users"),
            B(WellKnownSidType.BuiltinAuthorizationAccessSid, 560, "Windows Authorization Access Group"),
            B(WellKnownSidType.WinBuiltinTerminalServerLicenseServersSid, 561, "Terminal Server License Servers"),
            B(WellKnownSidType.WinBuiltinDCOMUsersSid, 562, "Distributed COM Users"),
            B(WellKnownSidType.WinBuiltinIUsersSid, 568, "IIS_IUSRS"),
            B(WellKnownSidType.WinBuiltinCryptoOperatorsSid, 569, "Cryptographic Operators"),
            B(WellKnownSidType.WinBuiltinEventLogReadersGroup, 573, "Event Log Readers"),
            B(WellKnownSidType.WinBuiltinCertSvcDComAccessGroup, 574, "Certificate Service DCOM Access"),
            K(WellKnownSidType.WinUntrustedLabelSid, 16, "Mandatory Label", "Untrusted Mandatory Level", 0),
            K(WellKnownSidType.WinLowLabelSid, 16, "Mandatory Label", "Low Mandatory Level", 4096),
            K(WellKnownSidType.WinMediumLabelSid, 16, "Mandatory Label", "Medium Mandatory Level", 8192),
            K(WellKnownSidType.WinMediumPlusLabelSid, 16, "Mandatory Label", "Medium Plus Mandatory Level", 8448),
            K(WellKnownSidType.WinHighLabelSid, 16, "Mandatory Label", "High Mandatory Level", 12288),
            K(WellKnownSidType.WinSystemLabelSid, 16, "Mandatory Label", "System Mandatory Level", 16384),
            K(WellKnownSidType.WinApplicationPackageAuthoritySid, 15, "", "APPLICATION PACKAGE AUTHORITY", 2),
            K(WellKnownSidType.WinBuiltinAnyPackageSid, 15, "APPLICATION PACKAGE AUTHORITY", "ALL APPLICATION PACKAGES", 2, 1),
            K(WellKnownSidType.WinCapabilityInternetClientSid, 15, "APPLICATION PACKAGE AUTHORITY", "Your Internet connection", 3, 1),
            K(WellKnownSidType.WinCapabilityInternetClientServerSid, 15, "APPLICATION PACKAGE AUTHORITY", "Your Internet connection, including incoming connections from the Internet", 3, 2),
            K(WellKnownSidType.WinCapabilityPrivateNetworkClientServerSid, 15, "APPLICATION PACKAGE AUTHORITY", "Your home or work networks", 3, 3),
            K(WellKnownSidType.WinCapabilityPicturesLibrarySid, 15, "APPLICATION PACKAGE AUTHORITY", "Your pictures library", 3, 4),
            K(WellKnownSidType.WinCapabilityVideosLibrarySid, 15, "APPLICATION PACKAGE AUTHORITY", "Your videos library", 3, 5),
            K(WellKnownSidType.WinCapabilityMusicLibrarySid, 15, "APPLICATION PACKAGE AUTHORITY", "Your music library", 3, 6),
            K(WellKnownSidType.WinCapabilityDocumentsLibrarySid, 15, "APPLICATION PACKAGE AUTHORITY", "Your documents library", 3, 7),
            K(WellKnownSidType.WinCapabilityEnterpriseAuthenticationSid, 15, "APPLICATION PACKAGE AUTHORITY", "Your Windows credentials", 3, 8),
            K(WellKnownSidType.WinCapabilitySharedUserCertificatesSid, 15, "APPLICATION PACKAGE AUTHORITY", "Software and hardware certificates or a smart card", 3, 9),
            K(WellKnownSidType.WinCapabilityRemovableStorageSid, 15, "APPLICATION PACKAGE AUTHORITY", "Removable storage", 3, 10),
            D(WellKnownSidType.AccountAdministratorSid, 500, "Administrator"),
            D(WellKnownSidType.AccountGuestSid, 501, "Guest"),
            D(WellKnownSidType.AccountKrbtgtSid, 502, "krbtgt"),
            D(WellKnownSidType.AccountDomainAdminsSid, 512, "Domain Admins"),
            D(WellKnownSidType.AccountDomainUsersSid, 513, "None"),
            D(WellKnownSidType.AccountDomainGuestsSid, 514, "Domain Guests"),
            D(WellKnownSidType.AccountComputersSid, 515, "Domain Computers"),
            D(WellKnownSidType.AccountControllersSid, 516, "Domain Controllers"),
            D(WellKnownSidType.AccountCertAdminsSid, 517, "Cert Publishers"),
            D(WellKnownSidType.AccountSchemaAdminsSid, 518, "Schema Admins"),
            D(WellKnownSidType.AccountEnterpriseAdminsSid, 519, "Enterprise Admins"),
            D(WellKnownSidType.AccountPolicyAdminsSid, 520, "Group Policy Creator Owners"),
            D(WellKnownSidType.WinAccountReadonlyControllersSid, 521, "Read-only Domain Controllers"),
            D(WellKnownSidType.AccountRasAndIasServersSid, 553, "RAS and IAS Servers"),
            D(WellKnownSidType.WinCacheablePrincipalsGroupSid, 571, "Allowed RODC Password Replication Group"),
            D(WellKnownSidType.WinNonCacheablePrincipalsGroupSid, 572, "Denied RODC Password Replication Group"),
            D(WellKnownSidType.WinEnterpriseReadonlyControllersSid, 498, "Enterprise Read-only Domain Controllers"),
        };

        /// <summary>CreateWellKnownSid: the SID of <paramref name="type"/>, in <paramref name="domainSid"/>
        /// for those that live in an account domain.</summary>
        internal static SecurityIdentifier WellKnown(WellKnownSidType type, SecurityIdentifier domainSid)
        {
            if (type == WellKnownSidType.LogonIdsSid)
                throw new ArgumentException("Well-known SIDs of type LogonIdsSid cannot be created.", "sidType");
            foreach (Known k in s_known)
            {
                if (k.Type != type) continue;
                if (!k.DomainRelative) return new SecurityIdentifier(k.Authority, k.Sub);
                if (domainSid is null) throw new ArgumentNullException(nameof(domainSid), "The domainSid parameter must be specified for creating well-known SID of type " + type + ".");
                SecurityIdentifier d = domainSid.AccountDomainSid ?? throw new ArgumentException("The domainSid parameter is not a valid Windows domain SID.", nameof(domainSid));
                return new SecurityIdentifier(5, 21, d.Sub(1), d.Sub(2), d.Sub(3), k.Sub[0]);
            }
            throw new ArgumentException("Value was invalid.", "sidType");
        }

        // ConvertStringSidToSid's two-letter aliases.
        private static readonly Dictionary<string, WellKnownSidType> s_sddlAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["WD"] = WellKnownSidType.WorldSid, ["CO"] = WellKnownSidType.CreatorOwnerSid, ["CG"] = WellKnownSidType.CreatorGroupSid,
            ["NU"] = WellKnownSidType.NetworkSid, ["IU"] = WellKnownSidType.InteractiveSid, ["SU"] = WellKnownSidType.ServiceSid,
            ["AN"] = WellKnownSidType.AnonymousSid, ["ED"] = WellKnownSidType.EnterpriseControllersSid, ["PS"] = WellKnownSidType.SelfSid,
            ["AU"] = WellKnownSidType.AuthenticatedUserSid, ["RC"] = WellKnownSidType.RestrictedCodeSid, ["SY"] = WellKnownSidType.LocalSystemSid,
            ["LS"] = WellKnownSidType.LocalServiceSid, ["NS"] = WellKnownSidType.NetworkServiceSid, ["BA"] = WellKnownSidType.BuiltinAdministratorsSid,
            ["BU"] = WellKnownSidType.BuiltinUsersSid, ["BG"] = WellKnownSidType.BuiltinGuestsSid, ["PU"] = WellKnownSidType.BuiltinPowerUsersSid,
            ["AO"] = WellKnownSidType.BuiltinAccountOperatorsSid, ["SO"] = WellKnownSidType.BuiltinSystemOperatorsSid, ["PO"] = WellKnownSidType.BuiltinPrintOperatorsSid,
            ["BO"] = WellKnownSidType.BuiltinBackupOperatorsSid, ["RE"] = WellKnownSidType.BuiltinReplicatorSid, ["RU"] = WellKnownSidType.BuiltinPreWindows2000CompatibleAccessSid,
            ["RD"] = WellKnownSidType.BuiltinRemoteDesktopUsersSid, ["NO"] = WellKnownSidType.BuiltinNetworkConfigurationOperatorsSid,
            ["MU"] = WellKnownSidType.BuiltinPerformanceMonitoringUsersSid, ["LU"] = WellKnownSidType.BuiltinPerformanceLoggingUsersSid,
            ["IS"] = WellKnownSidType.WinBuiltinIUsersSid, ["CY"] = WellKnownSidType.WinBuiltinCryptoOperatorsSid, ["ER"] = WellKnownSidType.WinBuiltinEventLogReadersGroup,
            ["CD"] = WellKnownSidType.WinBuiltinCertSvcDComAccessGroup, ["OW"] = WellKnownSidType.WinCreatorOwnerRightsSid, ["WR"] = WellKnownSidType.WinWriteRestrictedCodeSid,
            ["LW"] = WellKnownSidType.WinLowLabelSid, ["ME"] = WellKnownSidType.WinMediumLabelSid, ["MP"] = WellKnownSidType.WinMediumPlusLabelSid,
            ["HI"] = WellKnownSidType.WinHighLabelSid, ["SI"] = WellKnownSidType.WinSystemLabelSid, ["AC"] = WellKnownSidType.WinBuiltinAnyPackageSid,
            ["LA"] = WellKnownSidType.AccountAdministratorSid, ["LG"] = WellKnownSidType.AccountGuestSid,
        };

        internal static SecurityIdentifier ParseSddl(string s)
        {
            if (s_sddlAliases.TryGetValue(s, out WellKnownSidType alias)) return WellKnown(alias, MachineDomain);
            string[] parts = s.Split('-');
            if (parts.Length < 3 || !string.Equals(parts[0], "S", StringComparison.OrdinalIgnoreCase) || parts[1] != "1") return null;
            ulong authority;
            if (parts[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!ulong.TryParse(parts[2].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out authority)) return null;
            }
            else if (!ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out authority)) return null;
            if (authority >= 1UL << 48 || parts.Length - 3 > 15) return null;
            var sub = new uint[parts.Length - 3];
            for (int i = 0; i < sub.Length; i++)
                if (!uint.TryParse(parts[3 + i], NumberStyles.None, CultureInfo.InvariantCulture, out sub[i])) return null;
            return new SecurityIdentifier(authority, sub);
        }

        // ---- LookupAccountSid / LookupAccountName ---------------------------------------------------

        internal static IdentityReference TryTranslate(IdentityReference r, Type target)
        {
            if (r.GetType() == target) return r;
            if (r is SecurityIdentifier sid && target == typeof(NTAccount))
                return NameOf(sid) is string n ? new NTAccount(n) : null;
            if (r is NTAccount acct && target == typeof(SecurityIdentifier))
                return SidOf(acct.Value);
            return null;
        }

        private static string NameOf(SecurityIdentifier sid)
        {
            if (sid.Equals(Current.Sid)) return Current.Name;
            foreach (Known k in s_known)
            {
                if (k.DomainRelative || k.Name.Length == 0) continue;
                if (sid.Equals(new SecurityIdentifier(k.Authority, k.Sub)))
                    return k.Domain.Length == 0 ? k.Name : k.Domain + "\\" + k.Name;
            }
            if (sid.IsAccountSid() && sid.SubCount == 5 && sid.AccountDomainSid.Equals(MachineDomain))
            {
                uint rid = sid.LastSub;
                foreach (Known k in s_known)
                    if (k.DomainRelative && k.Sub[0] == rid) return Machine + "\\" + k.Name;
                if (UidOfRid(rid, out uint uid) && Native.UserName(uid) is string user) return Machine + "\\" + user;
            }
            return null;
        }

        private static SecurityIdentifier SidOf(string name)
        {
            string domain = null, account = name;
            int slash = name.IndexOf('\\');
            if (slash >= 0) { domain = name.Substring(0, slash); account = name.Substring(slash + 1); }
            else if (name.IndexOf('@') is int at && at > 0) { account = name.Substring(0, at); domain = Machine; }
            if (account.Length == 0) return null;
            bool isMachine = domain is null || string.Equals(domain, Machine, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(domain, Environment.UserDomainName, StringComparison.OrdinalIgnoreCase) || domain == ".";

            if (string.Equals(name, Current.Name, StringComparison.OrdinalIgnoreCase)
                || isMachine && string.Equals(account, Environment.UserName, StringComparison.OrdinalIgnoreCase))
                return Current.Sid;
            foreach (Known k in s_known)
            {
                if (k.Name.Length == 0 || !string.Equals(k.Name, account, StringComparison.OrdinalIgnoreCase)) continue;
                if (k.DomainRelative)
                {
                    if (isMachine) return Domain(k.Sub[0]);
                    continue;
                }
                if (domain is null || string.Equals(domain, k.Domain, StringComparison.OrdinalIgnoreCase))
                    return new SecurityIdentifier(k.Authority, k.Sub);
            }
            if (domain is not null && string.Equals(account, domain, StringComparison.OrdinalIgnoreCase))
            {
                if (isMachine) return MachineDomain;                                     // the machine's own domain
                if (string.Equals(domain, BuiltinDomain, StringComparison.OrdinalIgnoreCase)) return Builtin;
            }
            if (isMachine && Native.Uid(account) is uint uid) return Domain(RidOfUid(uid));
            return null;
        }
    }

    internal static class Native
    {
        [DllImport("libc", EntryPoint = "getuid")] private static extern uint getuid();
        [DllImport("libc", EntryPoint = "getgroups")] private static extern int getgroups(int size, uint[] list);
        [DllImport("libc", EntryPoint = "getpwnam")] private static extern IntPtr getpwnam(string name);
        [DllImport("libc", EntryPoint = "getpwuid")] private static extern IntPtr getpwuid(uint uid);

        internal static uint Uid() { try { return getuid(); } catch { return 501; } }

        // macOS's admin group (gid 80) is who may administer the machine: Windows' Administrators.
        internal static bool InAdminGroup()
        {
            try
            {
                int n = getgroups(0, null);
                if (n <= 0) return false;
                var g = new uint[n];
                n = getgroups(n, g);
                for (int i = 0; i < n; i++) if (g[i] == 80) return true;
            }
            catch { }
            return false;
        }

        // struct passwd starts { char *pw_name; char *pw_passwd; uid_t pw_uid; ... } on macOS and Linux.
        internal static uint? Uid(string user)
        {
            try
            {
                IntPtr p = getpwnam(user);
                return p == IntPtr.Zero ? null : (uint)Marshal.ReadInt32(p, 2 * IntPtr.Size);
            }
            catch { return null; }
        }

        internal static string UserName(uint uid)
        {
            try
            {
                IntPtr p = getpwuid(uid);
                return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(p));
            }
            catch { return null; }
        }
    }
}
