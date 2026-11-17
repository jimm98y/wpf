// The rest of OLE DB's surface: errors, the data adapter and its command builder, the factory, the schema rowsets'
// GUIDs and the providers' enumerator.

using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.Common;
using System.Linq;
using System.Runtime.Serialization;

namespace System.Data.OleDb
{
    // ---- errors ----------------------------------------------------------------------------------------------------

    public sealed partial class OleDbError
    {
        internal OleDbError(string message, int nativeError, string source, string sqlState)
        {
            Message = message ?? "";
            NativeError = nativeError;
            Source = source ?? "";
            SQLState = sqlState ?? "";
        }

        public string Message { get; }
        public int NativeError { get; }
        public string Source { get; }
        public string SQLState { get; }
        public override string ToString() => Message;
    }

    [ListBindable(false)]
    public sealed partial class OleDbErrorCollection : ICollection, IEnumerable
    {
        private readonly List<OleDbError> _items;

        internal OleDbErrorCollection(IEnumerable<OleDbError> errors) => _items = errors.ToList();

        public int Count => _items.Count;
        public OleDbError this[int index] => _items[index];
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => this;
        public void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public void CopyTo(OleDbError[] array, int index) => _items.CopyTo(array, index);
        public IEnumerator GetEnumerator() => _items.GetEnumerator();
    }

    public sealed partial class OleDbException : DbException
    {
        private readonly int _hresult;

        internal OleDbException(string message, int hresult, OleDbErrorCollection errors, Exception inner) : base(message, inner)
        {
            _hresult = hresult;
            HResult = hresult;
            Errors = errors;
        }

        public override int ErrorCode => _hresult;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        public OleDbErrorCollection Errors { get; }

        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        [EditorBrowsable(EditorBrowsableState.Never)]
        public override void GetObjectData(SerializationInfo si, StreamingContext context)
        {
            ArgumentNullException.ThrowIfNull(si);
            si.AddValue("oledbErrors", null, typeof(OleDbErrorCollection));
            base.GetObjectData(si, context);
        }
    }

    public sealed partial class OleDbInfoMessageEventArgs : EventArgs
    {
        internal OleDbInfoMessageEventArgs(string message, string source, OleDbErrorCollection errors)
        {
            Message = message;
            Source = source;
            Errors = errors;
        }

        public int ErrorCode => 0;
        public OleDbErrorCollection Errors { get; }
        public string Message { get; }
        public string Source { get; }
        public override string ToString() => Message;
    }

    // ---- the data adapter ------------------------------------------------------------------------------------------

    [DefaultEvent("RowUpdated")]
    [Designer("Microsoft.VSDesigner.Data.VS.OleDbDataAdapterDesigner, Microsoft.VSDesigner, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
    [ToolboxItem("Microsoft.VSDesigner.Data.VS.OleDbDataAdapterToolboxItem, Microsoft.VSDesigner, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
    public sealed partial class OleDbDataAdapter : DbDataAdapter, IDataAdapter, IDbDataAdapter, ICloneable
    {
        private static readonly object s_updated = new object(), s_updating = new object();
        private OleDbCommand _select, _insert, _update, _delete;

        public OleDbDataAdapter() { GC.SuppressFinalize(this); }

        public OleDbDataAdapter(OleDbCommand selectCommand) : this() => SelectCommand = selectCommand;

        public OleDbDataAdapter(string selectCommandText, OleDbConnection selectConnection) : this() => SelectCommand = new OleDbCommand(selectCommandText, selectConnection);

        public OleDbDataAdapter(string selectCommandText, string selectConnectionString) : this() =>
            SelectCommand = new OleDbCommand(selectCommandText, new OleDbConnection(selectConnectionString));

        private OleDbDataAdapter(OleDbDataAdapter from) : base(from) { GC.SuppressFinalize(this); }

        [DefaultValue(null)]
        public new OleDbCommand DeleteCommand { get => _delete; set => _delete = value; }
        [DefaultValue(null)]
        public new OleDbCommand InsertCommand { get => _insert; set => _insert = value; }
        [DefaultValue(null)]
        public new OleDbCommand SelectCommand { get => _select; set => _select = value; }
        [DefaultValue(null)]
        public new OleDbCommand UpdateCommand { get => _update; set => _update = value; }

        IDbCommand IDbDataAdapter.DeleteCommand { get => _delete; set => _delete = (OleDbCommand)value; }
        IDbCommand IDbDataAdapter.InsertCommand { get => _insert; set => _insert = (OleDbCommand)value; }
        IDbCommand IDbDataAdapter.SelectCommand { get => _select; set => _select = (OleDbCommand)value; }
        IDbCommand IDbDataAdapter.UpdateCommand { get => _update; set => _update = (OleDbCommand)value; }

        public event OleDbRowUpdatedEventHandler RowUpdated { add => Events.AddHandler(s_updated, value); remove => Events.RemoveHandler(s_updated, value); }
        public event OleDbRowUpdatingEventHandler RowUpdating
        {
            add
            {
                // a command builder's handler stays the last, as .NET keeps it
                var existing = (OleDbRowUpdatingEventHandler)Events[s_updating];
                if (existing != null && value?.Target is DbCommandBuilder)
                {
                    foreach (Delegate d in existing.GetInvocationList())
                        if (d.Target is DbCommandBuilder) Events.RemoveHandler(s_updating, d);
                }
                Events.AddHandler(s_updating, value);
            }
            remove => Events.RemoveHandler(s_updating, value);
        }

        protected override RowUpdatedEventArgs CreateRowUpdatedEvent(DataRow dataRow, IDbCommand command, StatementType statementType, DataTableMapping tableMapping) =>
            new OleDbRowUpdatedEventArgs(dataRow, command, statementType, tableMapping);

        protected override RowUpdatingEventArgs CreateRowUpdatingEvent(DataRow dataRow, IDbCommand command, StatementType statementType, DataTableMapping tableMapping) =>
            new OleDbRowUpdatingEventArgs(dataRow, command, statementType, tableMapping);

        // an ADO Recordset is a COM object, of a library there is not here
        public int Fill(DataSet dataSet, object ADODBRecordSet, string srcTable) => throw new PlatformNotSupportedException("ADODB recordsets are COM objects, which are not available on this platform.");

        public int Fill(DataTable dataTable, object ADODBRecordSet) => throw new PlatformNotSupportedException("ADODB recordsets are COM objects, which are not available on this platform.");

        protected override void OnRowUpdated(RowUpdatedEventArgs value)
        {
            if (Events[s_updated] is OleDbRowUpdatedEventHandler handler && value is OleDbRowUpdatedEventArgs e) handler(this, e);
            base.OnRowUpdated(value);
        }

        protected override void OnRowUpdating(RowUpdatingEventArgs value)
        {
            if (Events[s_updating] is OleDbRowUpdatingEventHandler handler && value is OleDbRowUpdatingEventArgs e) handler(this, e);
            base.OnRowUpdating(value);
        }

        object ICloneable.Clone() => new OleDbDataAdapter(this);
    }

    public sealed partial class OleDbRowUpdatedEventArgs : RowUpdatedEventArgs
    {
        public OleDbRowUpdatedEventArgs(DataRow dataRow, IDbCommand command, StatementType statementType, DataTableMapping tableMapping)
            : base(dataRow, command, statementType, tableMapping) { }

        public new OleDbCommand Command => (OleDbCommand)base.Command;
    }

    public sealed partial class OleDbRowUpdatingEventArgs : RowUpdatingEventArgs
    {
        public OleDbRowUpdatingEventArgs(DataRow dataRow, IDbCommand command, StatementType statementType, DataTableMapping tableMapping)
            : base(dataRow, command, statementType, tableMapping) { }

        protected override IDbCommand BaseCommand { get => base.BaseCommand; set => base.BaseCommand = value as OleDbCommand; }

        public new OleDbCommand Command { get => (OleDbCommand)BaseCommand; set => BaseCommand = value; }
    }

    // ---- the command builder ---------------------------------------------------------------------------------------

    public sealed partial class OleDbCommandBuilder : DbCommandBuilder
    {
        public OleDbCommandBuilder() { GC.SuppressFinalize(this); }

        public OleDbCommandBuilder(OleDbDataAdapter adapter) : this() => DataAdapter = adapter;

        [DefaultValue(null)]
        public new OleDbDataAdapter DataAdapter { get => (OleDbDataAdapter)base.DataAdapter; set => base.DataAdapter = value; }

        private void RowUpdatingHandler(object sender, OleDbRowUpdatingEventArgs e) => RowUpdatingHandler(e);

        protected override void SetRowUpdatingHandler(DbDataAdapter adapter)
        {
            if (adapter == base.DataAdapter) ((OleDbDataAdapter)adapter).RowUpdating -= RowUpdatingHandler;
            else ((OleDbDataAdapter)adapter).RowUpdating += RowUpdatingHandler;
        }

        protected override void ApplyParameterInfo(DbParameter parameter, DataRow datarow, StatementType statementType, bool whereClause)
        {
            var p = (OleDbParameter)parameter;
            if (datarow[SchemaTableColumn.ProviderType] is int type) p.OleDbType = (OleDbType)type;
            if (datarow[SchemaTableColumn.NumericPrecision] is short precision && precision != 0xff) p.Precision = (byte)precision;
            else if (datarow[SchemaTableColumn.NumericPrecision] is int precision32 && precision32 is > 0 and < 0xff) p.Precision = (byte)precision32;
            if (datarow[SchemaTableColumn.NumericScale] is short scale && scale != 0xff) p.Scale = (byte)scale;
            else if (datarow[SchemaTableColumn.NumericScale] is int scale32 && scale32 is > 0 and < 0xff) p.Scale = (byte)scale32;
        }

        public static void DeriveParameters(OleDbCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (command.CommandType != CommandType.StoredProcedure)
                throw new InvalidOperationException($"DeriveParameters: CommandType.{command.CommandType} is not supported; it requires CommandType.StoredProcedure.");
            if (string.IsNullOrEmpty(command.CommandText)) throw new InvalidOperationException("DeriveParameters: CommandText property has not been initialized");
            OleDbConnection connection = command.Connection ?? throw new InvalidOperationException("DeriveParameters: Connection property has not been initialized.");
            connection.CheckOpen("DeriveParameters");
            string name = command.CommandText;
            string schema = null;
            int dot = name.LastIndexOf('.');
            if (dot > 0) { schema = name[..dot].Trim('[', ']'); name = name[(dot + 1)..]; }
            DataTable rows = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Procedure_Parameters, new object[] { null, schema, name.Trim('[', ']') });
            if (rows.Rows.Count == 0) throw new InvalidOperationException($"The stored procedure '{command.CommandText}' doesn't exist.");
            command.Parameters.Clear();
            foreach (DataRow r in rows.Rows)
            {
                var p = new OleDbParameter((string)r["PARAMETER_NAME"], (OleDbType)(int)r["DATA_TYPE"])
                {
                    Direction = Convert.ToInt16(r["PARAMETER_TYPE"], Globalization.CultureInfo.InvariantCulture) switch
                    {
                        4 => ParameterDirection.ReturnValue, 2 => ParameterDirection.InputOutput, 3 => ParameterDirection.Output, _ => ParameterDirection.Input,
                    },
                };
                if (r["CHARACTER_MAXIMUM_LENGTH"] is long size) p.Size = (int)Math.Min(size, int.MaxValue);
                if (r["NUMERIC_PRECISION"] is short precision) p.Precision = (byte)precision;
                if (r["NUMERIC_SCALE"] is short scale) p.Scale = (byte)scale;
                command.Parameters.Add(p);
            }
        }

        public new OleDbCommand GetDeleteCommand() => (OleDbCommand)base.GetDeleteCommand();
        public new OleDbCommand GetDeleteCommand(bool useColumnsForParameterNames) => (OleDbCommand)base.GetDeleteCommand(useColumnsForParameterNames);
        public new OleDbCommand GetInsertCommand() => (OleDbCommand)base.GetInsertCommand();
        public new OleDbCommand GetInsertCommand(bool useColumnsForParameterNames) => (OleDbCommand)base.GetInsertCommand(useColumnsForParameterNames);
        public new OleDbCommand GetUpdateCommand() => (OleDbCommand)base.GetUpdateCommand();
        public new OleDbCommand GetUpdateCommand(bool useColumnsForParameterNames) => (OleDbCommand)base.GetUpdateCommand(useColumnsForParameterNames);

        protected override string GetParameterName(int parameterOrdinal) => "p" + parameterOrdinal.ToString(Globalization.CultureInfo.InvariantCulture);
        protected override string GetParameterName(string parameterName) => parameterName;
        protected override string GetParameterPlaceholder(int parameterOrdinal) => "?";

        public override string QuoteIdentifier(string unquotedIdentifier) => QuoteIdentifier(unquotedIdentifier, null);

        // SQL Server's quotes ([ ]), where the builder has none of its own
        public string QuoteIdentifier(string unquotedIdentifier, OleDbConnection connection)
        {
            ArgumentNullException.ThrowIfNull(unquotedIdentifier, nameof(unquotedIdentifier));
            var (prefix, suffix) = Quotes(connection);
            return prefix + unquotedIdentifier.Replace(suffix, suffix + suffix, StringComparison.Ordinal) + suffix;
        }

        public override string UnquoteIdentifier(string quotedIdentifier) => UnquoteIdentifier(quotedIdentifier, null);

        public string UnquoteIdentifier(string quotedIdentifier, OleDbConnection connection)
        {
            ArgumentNullException.ThrowIfNull(quotedIdentifier, nameof(quotedIdentifier));
            var (prefix, suffix) = Quotes(connection);
            if (quotedIdentifier.Length >= prefix.Length + suffix.Length && quotedIdentifier.StartsWith(prefix, StringComparison.Ordinal) && quotedIdentifier.EndsWith(suffix, StringComparison.Ordinal))
                return quotedIdentifier[prefix.Length..^suffix.Length].Replace(suffix + suffix, suffix, StringComparison.Ordinal);
            return quotedIdentifier;
        }

        private (string Prefix, string Suffix) Quotes(OleDbConnection connection)
        {
            if (!string.IsNullOrEmpty(QuotePrefix)) return (QuotePrefix, string.IsNullOrEmpty(QuoteSuffix) ? QuotePrefix : QuoteSuffix);
            connection ??= DataAdapter?.SelectCommand?.Connection;
            if (connection == null) throw new ArgumentNullException(nameof(connection), "QuoteIdentifier requires a connection when QuotePrefix is not set.");
            return ("[", "]");
        }
    }

    // ---- the factory -----------------------------------------------------------------------------------------------

    public sealed partial class OleDbFactory : DbProviderFactory
    {
        public static readonly OleDbFactory Instance = new OleDbFactory();

        private OleDbFactory() { }

        public override DbCommand CreateCommand() => new OleDbCommand();
        public override DbCommandBuilder CreateCommandBuilder() => new OleDbCommandBuilder();
        public override DbConnection CreateConnection() => new OleDbConnection();
        public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new OleDbConnectionStringBuilder();
        public override DbDataAdapter CreateDataAdapter() => new OleDbDataAdapter();
        public override DbParameter CreateParameter() => new OleDbParameter();
    }

    // ---- the schema rowsets ----------------------------------------------------------------------------------------

    public sealed partial class OleDbSchemaGuid
    {
        private static Guid G(uint a) => new Guid(a, 0x5cf3, 0x11ce, 0xad, 0xe5, 0x00, 0xaa, 0x00, 0x44, 0x77, 0x3d);

        public static readonly Guid Tables_Info = G(0xc8b522e0), Trustee = G(0xc8b522ef), Assertions = G(0xc8b52210), Catalogs = G(0xc8b52211),
            Character_Sets = G(0xc8b52212), Collations = G(0xc8b52213), Columns = G(0xc8b52214), Check_Constraints = G(0xc8b52215),
            Constraint_Column_Usage = G(0xc8b52216), Constraint_Table_Usage = G(0xc8b52217), Key_Column_Usage = G(0xc8b52218),
            Referential_Constraints = G(0xc8b52219), Table_Constraints = G(0xc8b5221a), Column_Domain_Usage = G(0xc8b5221b), Indexes = G(0xc8b5221e),
            Column_Privileges = G(0xc8b52221), Table_Privileges = G(0xc8b52222), Usage_Privileges = G(0xc8b52223), Procedures = G(0xc8b52224),
            Schemata = G(0xc8b52225), Sql_Languages = G(0xc8b52226), Statistics = G(0xc8b52227), Tables = G(0xc8b52229), Translations = G(0xc8b5222a),
            Provider_Types = G(0xc8b5222c), Views = G(0xc8b5222d), View_Column_Usage = G(0xc8b5222e), View_Table_Usage = G(0xc8b5222f),
            Procedure_Parameters = G(0xc8b522b8), Foreign_Keys = G(0xc8b522c4), Primary_Keys = G(0xc8b522c5), Procedure_Columns = G(0xc8b522c9),
            Table_Statistics = G(0xc8b522ff), Check_Constraints_By_Table = G(0xc8b52301),
            SchemaGuids = new Guid(0xf3264c9b, 0x1860, 0x4dfe, 0xb7, 0x1b, 0x29, 0x61, 0xb2, 0xea, 0x91, 0xbd),
            DbInfoKeywords = new Guid(0xf3264c9c, 0x1860, 0x4dfe, 0xb7, 0x1b, 0x29, 0x61, 0xb2, 0xea, 0x91, 0xbd),
            DbInfoLiterals = new Guid(0xf3264c9d, 0x1860, 0x4dfe, 0xb7, 0x1b, 0x29, 0x61, 0xb2, 0xea, 0x91, 0xbd);

        public OleDbSchemaGuid() { }
    }

    public static partial class OleDbMetaDataCollectionNames
    {
        public static readonly string Catalogs = "Catalogs", Collations = "Collations", Columns = "Columns", Indexes = "Indexes",
            ProcedureColumns = "ProcedureColumns", ProcedureParameters = "ProcedureParameters", Procedures = "Procedures", Tables = "Tables", Views = "Views";
    }

    public static partial class OleDbMetaDataColumnNames
    {
        public static readonly string BooleanFalseLiteral = "BooleanFalseLiteral", BooleanTrueLiteral = "BooleanTrueLiteral",
            DateTimeDigits = "DateTimeDigits", NativeDataType = "NativeDataType";
    }

    // ---- the providers ---------------------------------------------------------------------------------------------

    public sealed partial class OleDbEnumerator
    {
        public OleDbEnumerator() { }

        // the registered providers, as OLE DB's root enumerator lists them: those with a managed equivalent here
        public DataTable GetElements()
        {
            var table = new DataTable("MSDAENUM") { Locale = Globalization.CultureInfo.InvariantCulture };
            table.Columns.Add("SOURCES_NAME", typeof(string));
            table.Columns.Add("SOURCES_PARSENAME", typeof(string));
            table.Columns.Add("SOURCES_DESCRIPTION", typeof(string));
            table.Columns.Add("SOURCES_TYPE", typeof(int));
            table.Columns.Add("SOURCES_ISPARENT", typeof(bool));
            table.Columns.Add("SOURCES_CLSID", typeof(string));
            foreach (var (name, description) in Provider.Known)
                table.Rows.Add(name, "{" + new Guid(Security.Cryptography.MD5.HashData(Text.Encoding.UTF8.GetBytes(name))).ToString().ToUpperInvariant() + "}", description, 1, false, "");
            return table;
        }

        public static OleDbDataReader GetEnumerator(Type type) => throw new ArgumentException($"The type '{type}' is not an OLE DB enumerator registered on this machine.", nameof(type));

        public static OleDbDataReader GetRootEnumerator() =>
            throw new PlatformNotSupportedException("OLE DB's root enumerator is a COM object; use OleDbEnumerator.GetElements.");
    }
}
