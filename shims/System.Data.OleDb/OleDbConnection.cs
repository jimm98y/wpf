// An OLE DB connection, opened on the managed provider for its OLE DB provider (Provider.cs).

using System.ComponentModel;
using System.Data.Common;
using System.Reflection;
using System.Transactions;

namespace System.Data.OleDb
{
    [DefaultEvent("InfoMessage")]
    public sealed partial class OleDbConnection : DbConnection, IDbConnection, ICloneable, IDisposable
    {
        private string _connectionString = "";
        private OleDbConnectionStringBuilder _options = new OleDbConnectionStringBuilder();
        private Provider _provider;
        private DbConnection _inner;
        private OleDbInfoMessageEventHandler _infoMessage;
        internal OleDbTransaction LocalTransaction;

        public OleDbConnection() { }

        public OleDbConnection(string connectionString) : this() => ConnectionString = connectionString;

        internal DbConnection Inner => _inner;
        internal Provider ProviderOf => _provider;

        [DefaultValue("")]
        [SettingsBindable(true)]
        [RefreshProperties(RefreshProperties.All)]
        [Editor("Microsoft.VSDesigner.Data.ADO.Design.OleDbConnectionStringEditor, Microsoft.VSDesigner, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", "System.Drawing.Design.UITypeEditor, System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
        public override string ConnectionString
        {
            get => _connectionString;
            set
            {
                if (State != ConnectionState.Closed)
                    throw new InvalidOperationException($"Not allowed to change the 'ConnectionString' property. The connection's current state is {StateName}.");
                var options = new OleDbConnectionStringBuilder(value ?? "");
                if (!string.IsNullOrEmpty(value)) OleDb.Provider.Validate(options);
                _options = options;
                _connectionString = value ?? "";
                _provider = null;
            }
        }

        public override int ConnectionTimeout =>
            _options.TryGetValue("Connect Timeout", out object v) && int.TryParse(Convert.ToString(v, Globalization.CultureInfo.InvariantCulture), out int t) ? t : 15;

        public override string Database => _inner?.State == ConnectionState.Open ? _inner.Database
            : _options.TryGetValue("Initial Catalog", out object v) ? Convert.ToString(v, Globalization.CultureInfo.InvariantCulture) : "";

        public override string DataSource => _options.DataSource;

        [Browsable(true)]
        public string Provider => _options.Provider;

        public override string ServerVersion
        {
            get
            {
                if (State != ConnectionState.Open) throw new InvalidOperationException("Invalid operation. The connection is closed.");
                return _inner.ServerVersion;
            }
        }

        [Browsable(false)]
        public override ConnectionState State => _inner?.State ?? ConnectionState.Closed;

        private string StateName => State.ToString();

        public event OleDbInfoMessageEventHandler InfoMessage
        {
            add => _infoMessage += value;
            remove => _infoMessage -= value;
        }

        public override void Open()
        {
            if (State != ConnectionState.Closed)
                throw new InvalidOperationException($"The connection was not closed. The connection's current state is {StateName}.");
            if (string.IsNullOrEmpty(_connectionString)) throw new InvalidOperationException("The ConnectionString property has not been initialized.");
            _provider ??= OleDb.Provider.Resolve(_options.Provider);
            DbConnection inner = _provider.Factory.CreateConnection()!;
            inner.ConnectionString = _provider.Translate(_options);
            inner.StateChange += (_, e) => OnStateChange(e);
            HookInfoMessages(inner);
            try { inner.Open(); }
            catch (Exception e)
            {
                inner.Dispose();
                throw _provider.Wrap(e);
            }
            _inner = inner;
        }

        // the server's messages (PRINT, low-severity RAISERROR) as OLE DB reports them
        private void HookInfoMessages(DbConnection inner)
        {
            EventInfo info = inner.GetType().GetEvent("InfoMessage");
            if (info?.EventHandlerType == null) return;
            MethodInfo method = typeof(OleDbConnection).GetMethod(nameof(OnInnerInfoMessage), BindingFlags.NonPublic | BindingFlags.Instance)!;
            try { info.AddEventHandler(inner, Delegate.CreateDelegate(info.EventHandlerType, this, method)); }
            catch (ArgumentException) { }
        }

        private void OnInnerInfoMessage(object sender, EventArgs e)
        {
            if (_infoMessage == null) return;
            Type t = e.GetType();
            string message = t.GetProperty("Message")?.GetValue(e) as string ?? "";
            string source = t.GetProperty("Source")?.GetValue(e) as string;
            var errors = new System.Collections.Generic.List<OleDbError>();
            if (t.GetProperty("Errors")?.GetValue(e) is Collections.IEnumerable records)
                foreach (object r in records)
                {
                    Type rt = r.GetType();
                    errors.Add(new OleDbError(rt.GetProperty("Message")?.GetValue(r) as string ?? "", rt.GetProperty("Number")?.GetValue(r) is int n ? n : 0, source ?? "", "01000"));
                }
            _infoMessage(this, new OleDbInfoMessageEventArgs(message, source, new OleDbErrorCollection(errors)));
        }

        public override void Close()
        {
            DbConnection inner = _inner;
            _inner = null;
            LocalTransaction = null;
            if (inner != null)
            {
                inner.Close();
                inner.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Close();
            base.Dispose(disposing);
        }

        internal void CheckOpen(string method)
        {
            if (State != ConnectionState.Open)
                throw new InvalidOperationException($"{method} requires an open and available Connection. The connection's current state is {StateName.ToLowerInvariant()}.");
        }

        public new OleDbTransaction BeginTransaction() => BeginTransaction(IsolationLevel.Unspecified);

        public new OleDbTransaction BeginTransaction(IsolationLevel isolationLevel) => (OleDbTransaction)BeginDbTransaction(isolationLevel);

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            CheckOpen("BeginTransaction");
            if (LocalTransaction is { IsActive: true })
                throw new InvalidOperationException("OleDbConnection does not support parallel transactions.");
            DbTransaction inner;
            try { inner = _inner.BeginTransaction(isolationLevel == IsolationLevel.Unspecified ? IsolationLevel.ReadCommitted : isolationLevel); }
            catch (Exception e) { throw _provider.Wrap(e); }
            return LocalTransaction = new OleDbTransaction(this, inner, isolationLevel == IsolationLevel.Unspecified ? IsolationLevel.ReadCommitted : isolationLevel);
        }

        public override void ChangeDatabase(string value)
        {
            CheckOpen("ChangeDatabase");
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Database cannot be null, the empty string, or string of only whitespace.", nameof(value));
            try { _inner.ChangeDatabase(value); }
            catch (Exception e) { throw _provider.Wrap(e); }
        }

        public new OleDbCommand CreateCommand() => new OleDbCommand("", this);

        protected override DbCommand CreateDbCommand() => CreateCommand();

        public override void EnlistTransaction(Transaction transaction)
        {
            CheckOpen("EnlistTransaction");
            _inner.EnlistTransaction(transaction);
        }

        public DataTable GetOleDbSchemaTable(Guid schema, object[] restrictions)
        {
            CheckOpen("GetOleDbSchemaTable");
            try { return _provider.Schema(_inner, LocalTransaction is { IsActive: true } t ? t.Inner : null, schema, restrictions); }
            catch (Exception e) { throw _provider.Wrap(e); }
        }

        public override DataTable GetSchema() => GetSchema(DbMetaDataCollectionNames.MetaDataCollections, null);

        public override DataTable GetSchema(string collectionName) => GetSchema(collectionName, null);

        public override DataTable GetSchema(string collectionName, string[] restrictionValues)
        {
            CheckOpen("GetSchema");
            try { return _inner.GetSchema(collectionName, restrictionValues); }
            catch (Exception e) { throw _provider.Wrap(e); }
        }

        // the pooled connections the managed provider keeps
        public static void ReleaseObjectPool()
        {
            foreach (string name in new[] { "Microsoft.Data.SqlClient.SqlConnection, Microsoft.Data.SqlClient", "System.Data.SqlClient.SqlConnection, System.Data.SqlClient" })
            {
                try { Type.GetType(name, throwOnError: false)?.GetMethod("ClearAllPools", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null); }
                catch (Exception e) when (e is IO.FileNotFoundException or IO.FileLoadException or TargetInvocationException) { }
            }
        }

        // the state as the provider has it now (a connection the server broke reads closed)
        public void ResetState() { }

        object ICloneable.Clone() => new OleDbConnection(ConnectionString);
    }

    public sealed partial class OleDbTransaction : DbTransaction
    {
        private readonly OleDbConnection _connection;
        private readonly IsolationLevel _isolationLevel;
        private bool _done;

        internal OleDbTransaction(OleDbConnection connection, DbTransaction inner, IsolationLevel isolationLevel)
        {
            _connection = connection;
            Inner = inner;
            _isolationLevel = isolationLevel;
        }

        internal DbTransaction Inner { get; }
        internal bool IsActive => !_done && Inner.Connection != null;

        public new OleDbConnection Connection => _done ? null : _connection;

        protected override DbConnection DbConnection => Connection;

        public override IsolationLevel IsolationLevel
        {
            get
            {
                if (_done) throw Completed();
                return _isolationLevel;
            }
        }

        private static InvalidOperationException Completed() => new InvalidOperationException("This OleDbTransaction has completed; it is no longer usable.");

        // a nested transaction: SQL Server's providers do not have them
        public OleDbTransaction Begin() => Begin(IsolationLevel.Unspecified);

        public OleDbTransaction Begin(IsolationLevel isolevel)
        {
            if (_done) throw Completed();
            throw new OleDbException("Cannot start more transactions on this session.", unchecked((int)0x8004D013),
                new OleDbErrorCollection(new[] { new OleDbError("Cannot start more transactions on this session.", 0, _connection.Provider, "25000") }), null);
        }

        public override void Commit()
        {
            if (_done) throw Completed();
            try { Inner.Commit(); }
            catch (Exception e) { throw _connection.ProviderOf.Wrap(e); }
            Finish();
        }

        public override void Rollback()
        {
            if (_done) throw Completed();
            try { Inner.Rollback(); }
            catch (Exception e) { throw _connection.ProviderOf.Wrap(e); }
            Finish();
        }

        private void Finish()
        {
            _done = true;
            if (_connection.LocalTransaction == this) _connection.LocalTransaction = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_done)
            {
                try { Inner.Dispose(); } catch (DbException) { }
                Finish();
            }
            base.Dispose(disposing);
        }
    }

    [DefaultProperty("Provider")]
    [RefreshProperties(RefreshProperties.All)]
    public sealed partial class OleDbConnectionStringBuilder : DbConnectionStringBuilder
    {
        // the keywords the builder always has, with their defaults
        private static readonly (string Key, object Default)[] s_known =
        {
            ("Data Source", ""), ("File Name", ""), ("OLE DB Services", -13), ("Persist Security Info", false), ("Provider", ""),
        };

        public OleDbConnectionStringBuilder() { }

        public OleDbConnectionStringBuilder(string connectionString) : this()
        {
            if (!string.IsNullOrEmpty(connectionString)) ConnectionString = connectionString;
        }

        [DisplayName("Data Source")] [RefreshProperties(RefreshProperties.All)]
        public string DataSource { get => (string)this["Data Source"]; set => this["Data Source"] = value; }

        [DisplayName("File Name")] [RefreshProperties(RefreshProperties.All)]
        [Editor("System.Windows.Forms.Design.FileNameEditor, System.Design, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", "System.Drawing.Design.UITypeEditor, System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
        public string FileName { get => (string)this["File Name"]; set => this["File Name"] = value; }

        [DisplayName("OLE DB Services")] [RefreshProperties(RefreshProperties.All)]
        public int OleDbServices { get => Convert.ToInt32(this["OLE DB Services"], Globalization.CultureInfo.InvariantCulture); set => this["OLE DB Services"] = value; }

        [DisplayName("Persist Security Info")] [RefreshProperties(RefreshProperties.All)]
        public bool PersistSecurityInfo
        {
            get => this["Persist Security Info"] is bool b ? b : bool.TryParse(Convert.ToString(this["Persist Security Info"], Globalization.CultureInfo.InvariantCulture), out bool v) && v;
            set => this["Persist Security Info"] = value;
        }

        [DisplayName("Provider")] [RefreshProperties(RefreshProperties.All)]
        public string Provider { get => (string)this["Provider"]; set => this["Provider"] = value; }

        public override object this[string keyword]
        {
            get
            {
                ArgumentNullException.ThrowIfNull(keyword);
                if (base.TryGetValue(keyword, out object value))
                {
                    int known = Array.FindIndex(s_known, k => k.Key.Equals(keyword, StringComparison.OrdinalIgnoreCase));
                    return known >= 0 && s_known[known].Default is not string ? Coerce(value, s_known[known].Default) : value;
                }
                foreach (var (key, def) in s_known) if (key.Equals(keyword, StringComparison.OrdinalIgnoreCase)) return def;
                throw new ArgumentException($"Keyword not supported: '{keyword}'.");
            }
            set
            {
                ArgumentNullException.ThrowIfNull(keyword);
                if (value == null) { Remove(keyword); return; }
                base[keyword] = value;
            }
        }

        private static object Coerce(object value, object def) => def switch
        {
            int => value is int i ? i : int.TryParse(Convert.ToString(value, Globalization.CultureInfo.InvariantCulture), out int p) ? p : def,
            bool => value is bool b ? b : Convert.ToString(value, Globalization.CultureInfo.InvariantCulture)?.Trim().ToUpperInvariant() is "TRUE" or "YES",
            _ => value,
        };

        public override Collections.ICollection Keys
        {
            get
            {
                var keys = new System.Collections.Generic.List<string>();
                foreach (var (key, _) in s_known) keys.Add(key);
                foreach (string key in base.Keys)
                    if (!keys.Exists(k => k.Equals(key, StringComparison.OrdinalIgnoreCase))) keys.Add(key);
                return keys;
            }
        }

        public override void Clear() => base.Clear();

        public override bool ContainsKey(string keyword)
        {
            ArgumentNullException.ThrowIfNull(keyword);
            return base.ContainsKey(keyword) || Array.Exists(s_known, k => k.Key.Equals(keyword, StringComparison.OrdinalIgnoreCase));
        }

        public override bool Remove(string keyword)
        {
            ArgumentNullException.ThrowIfNull(keyword);
            return base.Remove(keyword);
        }

        public override bool TryGetValue(string keyword, [Diagnostics.CodeAnalysis.NotNullWhen(true)] out object value)
        {
            ArgumentNullException.ThrowIfNull(keyword);
            if (base.TryGetValue(keyword, out value)) return true;
            foreach (var (key, def) in s_known)
                if (key.Equals(keyword, StringComparison.OrdinalIgnoreCase)) { value = def; return true; }
            return false;
        }
    }
}
