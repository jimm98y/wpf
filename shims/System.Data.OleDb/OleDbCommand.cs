// An OLE DB command, run as the managed provider's: positional ? markers named, a stored procedure called as OLE DB
// calls it, the parameters' values handed over before and the output ones read back after (a reader's once it closes).

using System.Collections.Generic;
using System.ComponentModel;
using System.Data.Common;
using System.Globalization;

namespace System.Data.OleDb
{
    [DefaultEvent("RecordsAffected")]
    [Designer("Microsoft.VSDesigner.Data.VS.OleDbCommandDesigner, Microsoft.VSDesigner, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
    [ToolboxItem(true)]
    public sealed partial class OleDbCommand : DbCommand, IDbCommand, ICloneable, IDisposable
    {
        private const int DefaultTimeout = 30;
        private string _commandText = "";
        private int _commandTimeout = DefaultTimeout;
        private CommandType _commandType = CommandType.Text;
        private OleDbConnection _connection;
        private OleDbTransaction _transaction;
        private readonly OleDbParameterCollection _parameters = new OleDbParameterCollection();
        private UpdateRowSource _updatedRowSource = UpdateRowSource.Both;
        private bool _designTimeInvisible;
        private DbCommand _running;

        public OleDbCommand() { }

        public OleDbCommand(string cmdText) : this() => CommandText = cmdText;

        public OleDbCommand(string cmdText, OleDbConnection connection) : this(cmdText) => Connection = connection;

        public OleDbCommand(string cmdText, OleDbConnection connection, OleDbTransaction transaction) : this(cmdText, connection) => Transaction = transaction;

        [DefaultValue("")]
        [RefreshProperties(RefreshProperties.All)]
        [Editor("Microsoft.VSDesigner.Data.ADO.Design.OleDbCommandTextEditor, Microsoft.VSDesigner, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", "System.Drawing.Design.UITypeEditor, System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
        public override string CommandText { get => _commandText; set => _commandText = value ?? ""; }

        public override int CommandTimeout
        {
            get => _commandTimeout;
            set
            {
                if (value < 0) throw new ArgumentException($"Invalid CommandTimeout value {value}; the value must be >= 0.", nameof(value));
                _commandTimeout = value;
            }
        }

        public void ResetCommandTimeout() => _commandTimeout = DefaultTimeout;

        [DefaultValue(CommandType.Text)]
        [RefreshProperties(RefreshProperties.All)]
        public override CommandType CommandType
        {
            get => _commandType;
            set
            {
                if (value is not (CommandType.Text or CommandType.StoredProcedure or CommandType.TableDirect))
                    throw new ArgumentOutOfRangeException(nameof(value), $"The CommandType enumeration value, {(int)value}, is invalid.");
                _commandType = value;
            }
        }

        [DefaultValue(null)]
        [Editor("Microsoft.VSDesigner.Data.Design.DbConnectionEditor, Microsoft.VSDesigner, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", "System.Drawing.Design.UITypeEditor, System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
        public new OleDbConnection Connection
        {
            get => _connection;
            set
            {
                if (value != _connection) _transaction = null;
                _connection = value;
            }
        }

        protected override DbConnection DbConnection { get => Connection; set => Connection = (OleDbConnection)value; }

        protected override DbParameterCollection DbParameterCollection => _parameters;

        protected override DbTransaction DbTransaction { get => Transaction; set => Transaction = (OleDbTransaction)value; }

        [Browsable(false)]
        [DefaultValue(true)]
        [DesignOnly(true)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public override bool DesignTimeVisible { get => !_designTimeInvisible; set { _designTimeInvisible = !value; TypeDescriptor.Refresh(this); } }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        public new OleDbParameterCollection Parameters => _parameters;

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public new OleDbTransaction Transaction
        {
            get => _transaction is { Connection: null } ? null : _transaction;
            set => _transaction = value;
        }

        [DefaultValue(UpdateRowSource.Both)]
        public override UpdateRowSource UpdatedRowSource
        {
            get => _updatedRowSource;
            set
            {
                if (value is < UpdateRowSource.None or > UpdateRowSource.Both)
                    throw new ArgumentOutOfRangeException(nameof(value), $"The UpdateRowSource enumeration value, {(int)value}, is invalid.");
                _updatedRowSource = value;
            }
        }

        public override void Cancel() => _running?.Cancel();

        public OleDbCommand Clone()
        {
            var clone = new OleDbCommand(_commandText, _connection, _transaction)
            {
                _commandTimeout = _commandTimeout, _commandType = _commandType, _updatedRowSource = _updatedRowSource, _designTimeInvisible = _designTimeInvisible,
            };
            foreach (OleDbParameter p in _parameters) clone.Parameters.Add((OleDbParameter)((ICloneable)p).Clone());
            return clone;
        }

        object ICloneable.Clone() => Clone();

        protected override DbParameter CreateDbParameter() => CreateParameter();

        public new OleDbParameter CreateParameter() => new OleDbParameter();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _running = null;
            base.Dispose(disposing);
        }

        public override void Prepare()
        {
            if (_commandType == CommandType.TableDirect) return;
            DbCommand inner = Build("Prepare", out _);
            try { inner.Prepare(); }
            catch (Exception e) { throw _connection.ProviderOf.Wrap(e); }
        }

        // ---- running -----------------------------------------------------------------------------------------------

        // the managed provider's command for this one, its parameters' values handed over
        private DbCommand Build(string method, out List<(OleDbParameter Ours, DbParameter Theirs)> bound)
        {
            if (_connection == null) throw new InvalidOperationException($"{method}: Connection property has not been initialized.");
            _connection.CheckOpen(method);
            if (_connection.LocalTransaction is { IsActive: true } pending && Transaction != pending)
                throw new InvalidOperationException(Transaction == null
                    ? $"{method} requires the command to have a transaction when the connection assigned to the command is in a pending local transaction.  The Transaction property of the command has not been initialized."
                    : "The transaction is either not associated with the current connection or has been completed.");
            if (string.IsNullOrEmpty(_commandText)) throw new InvalidOperationException($"{method}: CommandText property has not been initialized");

            DbCommand inner = _connection.Inner.CreateCommand();
            inner.CommandTimeout = _commandTimeout;
            inner.Transaction = Transaction?.Inner;
            var parameters = new List<OleDbParameter>();
            foreach (OleDbParameter p in _parameters) parameters.Add(p);
            switch (_commandType)
            {
                case CommandType.StoredProcedure:
                    inner.CommandText = Provider.ProcedureCall(_commandText, parameters);
                    break;
                case CommandType.TableDirect:
                    inner.CommandText = "SELECT * FROM " + _commandText;
                    break;
                default:
                    inner.CommandText = Provider.NameMarkers(_commandText, out int markers);
                    if (markers > parameters.Count)
                        throw new OleDbException("No value given for one or more required parameters.", unchecked((int)0x80040E10),
                            new OleDbErrorCollection(new[] { new OleDbError("No value given for one or more required parameters.", 0, _connection.Provider, "07002") }), null);
                    break;
            }
            bound = new List<(OleDbParameter, DbParameter)>();
            for (int i = 0; i < parameters.Count; i++)
            {
                OleDbParameter p = parameters[i];
                DbParameter q = inner.CreateParameter();
                q.ParameterName = Provider.Marker(i);
                // a return value is the EXEC batch's output (a batch's own return value is not the procedure's)
                q.Direction = p.Direction == ParameterDirection.ReturnValue ? ParameterDirection.Output : p.Direction;
                q.DbType = Provider.SqlDbTypeOf(p.OleDbType);
                if (p.Size != 0) q.Size = p.Size;
                if (p.Precision != 0) q.Precision = p.Precision;
                if (p.Scale != 0) q.Scale = p.Scale;
                q.IsNullable = p.IsNullable;
                if (p.Direction != ParameterDirection.Output && p.Direction != ParameterDirection.ReturnValue)
                {
                    if (p.Value == null)
                        throw new OleDbException($"Parameter {(string.IsNullOrEmpty(p.ParameterName) ? "?_" + (i + 1).ToString(CultureInfo.InvariantCulture) : p.ParameterName)} has no default value.",
                            unchecked((int)0x80040E10), new OleDbErrorCollection(new[] { new OleDbError($"Parameter {p.ParameterName} has no default value.", 0, _connection.Provider, "07002") }), null);
                    q.Value = p.Value is TimeSpan or DateTimeOffset or byte[] or string ? p.Value : ConvertForProvider(p.Value, p.OleDbType);
                }
                inner.Parameters.Add(q);
                bound.Add((p, q));
            }
            return inner;
        }

        // a value of a type SqlClient has no parameter for, as the provider converts it (the next wider type)
        private static object ConvertForProvider(object value, OleDbType type) => value switch
        {
            sbyte s => (short)s,
            ushort u => (int)u,
            uint u => (long)u,
            ulong u => (decimal)u,
            _ => value,
        };

        internal static void ReadBack(List<(OleDbParameter Ours, DbParameter Theirs)> bound)
        {
            foreach (var (ours, theirs) in bound)
                if (ours.Direction != ParameterDirection.Input) ours.Value = theirs.Value;
        }

        public override int ExecuteNonQuery()
        {
            DbCommand inner = Build("ExecuteNonQuery", out var bound);
            _running = inner;
            try
            {
                int n = inner.ExecuteNonQuery();
                ReadBack(bound);
                return n;
            }
            catch (Exception e) { throw _connection.ProviderOf.Wrap(e); }
            finally { _running = null; inner.Dispose(); }
        }

        public override object ExecuteScalar()
        {
            DbCommand inner = Build("ExecuteScalar", out var bound);
            _running = inner;
            try
            {
                object value = inner.ExecuteScalar();
                ReadBack(bound);
                return value;
            }
            catch (Exception e) { throw _connection.ProviderOf.Wrap(e); }
            finally { _running = null; inner.Dispose(); }
        }

        public new OleDbDataReader ExecuteReader() => ExecuteReader(CommandBehavior.Default);

        public new OleDbDataReader ExecuteReader(CommandBehavior behavior) => (OleDbDataReader)ExecuteDbDataReader(behavior);

        IDataReader IDbCommand.ExecuteReader() => ExecuteReader();

        IDataReader IDbCommand.ExecuteReader(CommandBehavior behavior) => ExecuteReader(behavior);

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            DbCommand inner = Build("ExecuteReader", out var bound);
            _running = inner;
            try { return new OleDbDataReader(inner.ExecuteReader(behavior), inner, bound, _connection.ProviderOf); }
            catch (Exception e)
            {
                inner.Dispose();
                throw _connection.ProviderOf.Wrap(e);
            }
            finally { _running = null; }
        }
    }
}
