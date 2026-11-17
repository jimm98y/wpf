// A result read through the managed provider's reader, described as OLE DB describes it: the schema table's
// ProviderType is the column's OleDbType. Its command's output parameters are read back once it closes, as OLE DB
// returns them after the last result.

using System.Collections;
using System.Collections.Generic;
using System.Data.Common;

namespace System.Data.OleDb
{
    public sealed partial class OleDbDataReader : DbDataReader
    {
        private readonly DbDataReader _inner;
        private readonly DbCommand _command;
        private readonly List<(OleDbParameter Ours, DbParameter Theirs)> _bound;
        private readonly Provider _provider;
        private bool _closed;

        internal OleDbDataReader(DbDataReader inner, DbCommand command, List<(OleDbParameter, DbParameter)> bound, Provider provider)
        {
            _inner = inner;
            _command = command;
            _bound = bound;
            _provider = provider;
        }

        private T Call<T>(Func<T> f)
        {
            try { return f(); }
            catch (Exception e) { throw _provider.Wrap(e); }
        }

        public override int Depth => _inner.Depth;
        public override int FieldCount => _inner.FieldCount;
        public override bool HasRows => _inner.HasRows;
        public override bool IsClosed => _closed || _inner.IsClosed;
        public override object this[int index] => GetValue(index);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override int RecordsAffected => _inner.RecordsAffected;
        public override int VisibleFieldCount => _inner.VisibleFieldCount;

        public override void Close()
        {
            if (_closed) return;
            _closed = true;
            Call(() => { _inner.Close(); return 0; });
            OleDbCommand.ReadBack(_bound);
            _command.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Close();
            base.Dispose(disposing);
        }

        public override bool GetBoolean(int ordinal) => Call(() => _inner.GetBoolean(ordinal));
        public override byte GetByte(int ordinal) => Call(() => _inner.GetByte(ordinal));
        public override long GetBytes(int ordinal, long dataIndex, byte[] buffer, int bufferIndex, int length) => Call(() => _inner.GetBytes(ordinal, dataIndex, buffer, bufferIndex, length));
        public override char GetChar(int ordinal) => Call(() => _inner.GetChar(ordinal));
        public override long GetChars(int ordinal, long dataIndex, char[] buffer, int bufferIndex, int length) => Call(() => _inner.GetChars(ordinal, dataIndex, buffer, bufferIndex, length));

        // a chapter (a hierarchical rowset's child rows): SQL Server's providers have none
        public new OleDbDataReader GetData(int ordinal) => throw new InvalidOperationException($"The column {ordinal} is not a chapter column.");

        protected override DbDataReader GetDbDataReader(int ordinal) => GetData(ordinal);

        public override string GetDataTypeName(int index) => Call(() => TypeName(_inner.GetFieldType(index)));

        // OLE DB names a column's type by its DBTYPE (DBTYPE_I4, DBTYPE_WVARCHAR...)
        private static string TypeName(Type t) => Type.GetTypeCode(t) switch
        {
            TypeCode.Boolean => "DBTYPE_BOOL",
            TypeCode.Byte => "DBTYPE_UI1",
            TypeCode.Int16 => "DBTYPE_I2",
            TypeCode.Int32 => "DBTYPE_I4",
            TypeCode.Int64 => "DBTYPE_I8",
            TypeCode.Single => "DBTYPE_R4",
            TypeCode.Double => "DBTYPE_R8",
            TypeCode.Decimal => "DBTYPE_NUMERIC",
            TypeCode.DateTime => "DBTYPE_DBTIMESTAMP",
            TypeCode.String => "DBTYPE_WVARCHAR",
            _ => t == typeof(Guid) ? "DBTYPE_GUID" : t == typeof(byte[]) ? "DBTYPE_BINARY" : t == typeof(TimeSpan) ? "DBTYPE_DBTIME2" : "DBTYPE_VARIANT",
        };

        public override DateTime GetDateTime(int ordinal) => Call(() => _inner.GetDateTime(ordinal));
        public override decimal GetDecimal(int ordinal) => Call(() => _inner.GetDecimal(ordinal));
        public override double GetDouble(int ordinal) => Call(() => _inner.GetDouble(ordinal));
        public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);
        public override Type GetFieldType(int index) => Call(() => _inner.GetFieldType(index));
        public override float GetFloat(int ordinal) => Call(() => _inner.GetFloat(ordinal));
        public override Guid GetGuid(int ordinal) => Call(() => _inner.GetGuid(ordinal));
        public override short GetInt16(int ordinal) => Call(() => _inner.GetInt16(ordinal));
        public override int GetInt32(int ordinal) => Call(() => _inner.GetInt32(ordinal));
        public override long GetInt64(int ordinal) => Call(() => _inner.GetInt64(ordinal));
        public override string GetName(int index) => Call(() => _inner.GetName(index));
        public override int GetOrdinal(string name) => Call(() => _inner.GetOrdinal(name));
        public override string GetString(int ordinal) => Call(() => _inner.GetString(ordinal));
        public TimeSpan GetTimeSpan(int ordinal) => Call(() => _inner.GetFieldValue<TimeSpan>(ordinal));
        public override object GetValue(int ordinal) => Call(() => _inner.GetValue(ordinal));
        public override int GetValues(object[] values) => Call(() => _inner.GetValues(values));
        public override bool IsDBNull(int ordinal) => Call(() => _inner.IsDBNull(ordinal));
        public override bool NextResult() => Call(() => _inner.NextResult());
        public override bool Read() => Call(() => _inner.Read());

        public override DataTable GetSchemaTable()
        {
            DataTable table = Call(() => _inner.GetSchemaTable());
            if (table == null || !table.Columns.Contains(SchemaTableColumn.ProviderType)) return table;
            // ProviderType as OLE DB gives it: the column's OleDbType (the managed provider's is its own enumeration)
            DataColumn provider = table.Columns[SchemaTableColumn.ProviderType]!;
            bool wasReadOnly = provider.ReadOnly;
            provider.ReadOnly = false;
            foreach (DataRow row in table.Rows)
            {
                if (row[SchemaTableColumn.DataType] is not Type t) continue;
                string typeName = table.Columns.Contains("DataTypeName") ? row["DataTypeName"] as string ?? "" : "";
                row[provider] = (int)OleDbTypeOf(t, typeName);
            }
            provider.ReadOnly = wasReadOnly;
            return table;
        }

        private static OleDbType OleDbTypeOf(Type t, string sqlType)
        {
            switch (sqlType.ToLowerInvariant())
            {
                case "char": case "varchar": case "text": return sqlType.Equals("char", StringComparison.OrdinalIgnoreCase) ? OleDbType.Char : OleDbType.VarChar;
                case "nchar": return OleDbType.WChar;
                case "money": case "smallmoney": return OleDbType.Currency;
                case "date": return OleDbType.DBDate;
                case "time": return OleDbType.DBTime;
                case "binary": return OleDbType.Binary;
                case "tinyint": return OleDbType.UnsignedTinyInt;
            }
            return Type.GetTypeCode(t) switch
            {
                TypeCode.Boolean => OleDbType.Boolean,
                TypeCode.Byte => OleDbType.UnsignedTinyInt,
                TypeCode.SByte => OleDbType.TinyInt,
                TypeCode.Int16 => OleDbType.SmallInt,
                TypeCode.Int32 => OleDbType.Integer,
                TypeCode.Int64 => OleDbType.BigInt,
                TypeCode.Single => OleDbType.Single,
                TypeCode.Double => OleDbType.Double,
                TypeCode.Decimal => OleDbType.Numeric,
                TypeCode.DateTime => OleDbType.DBTimeStamp,
                TypeCode.String => OleDbType.VarWChar,
                _ => t == typeof(Guid) ? OleDbType.Guid : t == typeof(byte[]) ? OleDbType.VarBinary : t == typeof(TimeSpan) ? OleDbType.DBTime : OleDbType.Variant,
            };
        }
    }
}
