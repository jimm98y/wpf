// OLE DB's parameters: positional (the name is the caller's, not the provider's), typed by OleDbType, which maps to
// and from DbType as .NET's OLE DB provider maps it, and is inferred from the value where it is not set.

using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.Common;

namespace System.Data.OleDb
{
    public sealed partial class OleDbParameter : DbParameter, IDataParameter, IDbDataParameter, ICloneable
    {
        private string _name = "";
        private OleDbType? _type;
        private ParameterDirection _direction = ParameterDirection.Input;
        private object _value;
        private int _size;
        private byte _precision, _scale;
        private string _sourceColumn = "";
        private DataRowVersion _sourceVersion = DataRowVersion.Current;
        internal OleDbParameterCollection Parent;

        public OleDbParameter() { }

        public OleDbParameter(string name, OleDbType dataType) : this() { ParameterName = name; OleDbType = dataType; }

        public OleDbParameter(string name, OleDbType dataType, int size) : this(name, dataType) => Size = size;

        public OleDbParameter(string name, OleDbType dataType, int size, string srcColumn) : this(name, dataType, size) => SourceColumn = srcColumn;

        public OleDbParameter(string parameterName, OleDbType dbType, int size, ParameterDirection direction, bool isNullable, byte precision, byte scale,
            string srcColumn, DataRowVersion srcVersion, object value) : this(parameterName, dbType, size, srcColumn)
        {
            Direction = direction;
            IsNullable = isNullable;
            Precision = precision;
            Scale = scale;
            SourceVersion = srcVersion;
            Value = value;
        }

        public OleDbParameter(string parameterName, OleDbType dbType, int size, ParameterDirection direction, byte precision, byte scale,
            string sourceColumn, DataRowVersion sourceVersion, bool sourceColumnNullMapping, object value) : this(parameterName, dbType, size, sourceColumn)
        {
            Direction = direction;
            Precision = precision;
            Scale = scale;
            SourceVersion = sourceVersion;
            SourceColumnNullMapping = sourceColumnNullMapping;
            Value = value;
        }

        public OleDbParameter(string name, object value) : this() { ParameterName = name; Value = value; }

        // ---- types ------------------------------------------------------------------------------------------------

        internal static DbType DbTypeOf(OleDbType type) => type switch
        {
            OleDbType.BigInt => DbType.Int64,
            OleDbType.Binary or OleDbType.VarBinary or OleDbType.LongVarBinary => DbType.Binary,
            OleDbType.Boolean => DbType.Boolean,
            OleDbType.BSTR or OleDbType.VarWChar or OleDbType.LongVarWChar => DbType.String,
            OleDbType.Char => DbType.AnsiStringFixedLength,
            OleDbType.VarChar or OleDbType.LongVarChar => DbType.AnsiString,
            OleDbType.WChar => DbType.StringFixedLength,
            OleDbType.Currency => DbType.Currency,
            OleDbType.Date or OleDbType.DBTimeStamp or OleDbType.Filetime => DbType.DateTime,
            OleDbType.DBDate => DbType.Date,
            OleDbType.DBTime => DbType.Time,
            OleDbType.Decimal or OleDbType.Numeric => DbType.Decimal,
            OleDbType.Double => DbType.Double,
            OleDbType.Single => DbType.Single,
            OleDbType.Error or OleDbType.Integer => DbType.Int32,
            OleDbType.Guid => DbType.Guid,
            OleDbType.SmallInt => DbType.Int16,
            OleDbType.TinyInt => DbType.SByte,
            OleDbType.UnsignedTinyInt => DbType.Byte,
            OleDbType.UnsignedSmallInt => DbType.UInt16,
            OleDbType.UnsignedInt => DbType.UInt32,
            OleDbType.UnsignedBigInt => DbType.UInt64,
            OleDbType.VarNumeric => DbType.VarNumeric,
            _ => DbType.Object,
        };

        private static OleDbType OleDbTypeOf(DbType type) => type switch
        {
            DbType.AnsiString => OleDbType.VarChar,
            DbType.AnsiStringFixedLength => OleDbType.Char,
            DbType.Binary => OleDbType.VarBinary,
            DbType.Boolean => OleDbType.Boolean,
            DbType.Byte => OleDbType.UnsignedTinyInt,
            DbType.Currency => OleDbType.Currency,
            DbType.Date => OleDbType.DBDate,
            DbType.DateTime => OleDbType.DBTimeStamp,
            DbType.Decimal => OleDbType.Decimal,
            DbType.Double => OleDbType.Double,
            DbType.Guid => OleDbType.Guid,
            DbType.Int16 => OleDbType.SmallInt,
            DbType.Int32 => OleDbType.Integer,
            DbType.Int64 => OleDbType.BigInt,
            DbType.Object => OleDbType.Variant,
            DbType.SByte => OleDbType.TinyInt,
            DbType.Single => OleDbType.Single,
            DbType.String => OleDbType.VarWChar,
            DbType.StringFixedLength => OleDbType.WChar,
            DbType.Time => OleDbType.DBTime,
            DbType.UInt16 => OleDbType.UnsignedSmallInt,
            DbType.UInt32 => OleDbType.UnsignedInt,
            DbType.UInt64 => OleDbType.UnsignedBigInt,
            DbType.VarNumeric => OleDbType.VarNumeric,
            _ => throw new ArgumentException($"No mapping exists from DbType {type} to a known OleDbType."),
        };

        // the type a value implies, where none was set
        private static OleDbType Infer(object value) => value switch
        {
            null or DBNull => OleDbType.Empty,
            string or char[] => OleDbType.VarWChar,
            char => OleDbType.WChar,
            bool => OleDbType.Boolean,
            byte => OleDbType.UnsignedTinyInt,
            sbyte => OleDbType.TinyInt,
            short => OleDbType.SmallInt,
            ushort => OleDbType.UnsignedSmallInt,
            int => OleDbType.Integer,
            uint => OleDbType.UnsignedInt,
            long => OleDbType.BigInt,
            ulong => OleDbType.UnsignedBigInt,
            float => OleDbType.Single,
            double => OleDbType.Double,
            decimal => OleDbType.Decimal,
            DateTime => OleDbType.DBTimeStamp,
            TimeSpan => OleDbType.DBTime,
            Guid => OleDbType.Guid,
            byte[] => OleDbType.VarBinary,
            Enum e => Infer(Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()), Globalization.CultureInfo.InvariantCulture)),
            _ => OleDbType.Variant,
        };

        public override DbType DbType { get => DbTypeOf(OleDbType); set => _type = OleDbTypeOf(value); }

        [DbProviderSpecificTypeProperty(true)]
        [RefreshProperties(RefreshProperties.All)]
        public OleDbType OleDbType
        {
            get => _type ?? (Infer(_value) is var t && t != OleDbType.Empty ? t : OleDbType.VarWChar);
            set
            {
                if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value), $"The OleDbType enumeration value, {(int)value}, is invalid.");
                _type = value;
            }
        }

        public override void ResetDbType() => _type = null;

        public void ResetOleDbType() => _type = null;

        [RefreshProperties(RefreshProperties.All)]
        public override ParameterDirection Direction
        {
            get => _direction;
            set
            {
                if (value is not (ParameterDirection.Input or ParameterDirection.Output or ParameterDirection.InputOutput or ParameterDirection.ReturnValue))
                    throw new ArgumentOutOfRangeException(nameof(value), $"The ParameterDirection enumeration value, {(int)value}, is invalid.");
                _direction = value;
            }
        }

        public override bool IsNullable { get; set; }

        public override string ParameterName { get => _name; set => _name = value ?? ""; }

        [DefaultValue((byte)0)]
        public new byte Precision { get => _precision; set => _precision = value; }

        [DefaultValue((byte)0)]
        public new byte Scale { get => _scale; set => _scale = value; }

        public override int Size
        {
            get => _size;
            set
            {
                if (value < -1) throw new ArgumentException($"Invalid parameter Size value '{value}'. The value must be greater than or equal to 0.", nameof(value));
                _size = value;
            }
        }

        public override string SourceColumn { get => _sourceColumn; set => _sourceColumn = value ?? ""; }

        public override bool SourceColumnNullMapping { get; set; }

        public override DataRowVersion SourceVersion { get => _sourceVersion; set => _sourceVersion = value; }

        [RefreshProperties(RefreshProperties.All)]
        [TypeConverter(typeof(StringConverter))]
        public override object Value { get => _value; set => _value = value; }

        object ICloneable.Clone() => new OleDbParameter(_name, OleDbType, _size, _direction, _precision, _scale, _sourceColumn, _sourceVersion, SourceColumnNullMapping, _value is ICloneable c ? c.Clone() : _value)
        {
            IsNullable = IsNullable, _type = _type,
        };

        public override string ToString() => ParameterName;
    }

    [Editor("Microsoft.VSDesigner.Data.Design.DBParametersEditor, Microsoft.VSDesigner, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", "System.Drawing.Design.UITypeEditor, System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
    [ListBindable(false)]
    public sealed partial class OleDbParameterCollection : DbParameterCollection
    {
        private readonly List<OleDbParameter> _items = new List<OleDbParameter>();

        internal OleDbParameterCollection() { }

        public override int Count => _items.Count;
        public override bool IsFixedSize => false;
        public override bool IsReadOnly => false;
        public override bool IsSynchronized => false;
        public override object SyncRoot => ((ICollection)_items).SyncRoot;

        public new OleDbParameter this[int index] { get => (OleDbParameter)GetParameter(index); set => SetParameter(index, value); }

        public new OleDbParameter this[string parameterName] { get => (OleDbParameter)GetParameter(parameterName); set => SetParameter(parameterName, value); }

        private static OleDbParameter Check(object value)
        {
            if (value is OleDbParameter p) return p;
            if (value == null) throw new ArgumentNullException(nameof(value), "The OleDbParameterCollection only accepts non-null OleDbParameter type objects.");
            throw new InvalidCastException($"The OleDbParameterCollection only accepts non-null OleDbParameter type objects, not {value.GetType().Name} objects.");
        }

        private void Adopt(OleDbParameter p)
        {
            if (p.Parent != null && p.Parent != this) throw new ArgumentException("The OleDbParameter is already contained by another OleDbParameterCollection.");
            if (p.Parent == this) throw new ArgumentException("The OleDbParameter is already contained by this OleDbParameterCollection.");
            p.Parent = this;
        }

        public OleDbParameter Add(OleDbParameter value)
        {
            Add((object)value);
            return value;
        }

        public override int Add(object value)
        {
            OleDbParameter p = Check(value);
            Adopt(p);
            _items.Add(p);
            return _items.Count - 1;
        }

        public OleDbParameter Add(string parameterName, OleDbType oleDbType) => Add(new OleDbParameter(parameterName, oleDbType));
        public OleDbParameter Add(string parameterName, OleDbType oleDbType, int size) => Add(new OleDbParameter(parameterName, oleDbType, size));
        public OleDbParameter Add(string parameterName, OleDbType oleDbType, int size, string sourceColumn) => Add(new OleDbParameter(parameterName, oleDbType, size, sourceColumn));

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Add(String parameterName, Object value) has been deprecated. Use AddWithValue(String parameterName, Object value) instead.")]
        public OleDbParameter Add(string parameterName, object value) => Add(new OleDbParameter(parameterName, value));

        public OleDbParameter AddWithValue(string parameterName, object value) => Add(new OleDbParameter(parameterName, value));

        public override void AddRange(Array values)
        {
            ArgumentNullException.ThrowIfNull(values);
            foreach (object v in values) Check(v);
            foreach (object v in values) Add(v);
        }

        public void AddRange(OleDbParameter[] values) => AddRange((Array)values);

        public override void Clear()
        {
            foreach (OleDbParameter p in _items) p.Parent = null;
            _items.Clear();
        }

        public bool Contains(OleDbParameter value) => _items.Contains(value);
        public override bool Contains(object value) => value is OleDbParameter p && _items.Contains(p);
        public override bool Contains(string value) => IndexOf(value) >= 0;

        public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
        public void CopyTo(OleDbParameter[] array, int index) => _items.CopyTo(array, index);

        public override IEnumerator GetEnumerator() => _items.GetEnumerator();

        protected override DbParameter GetParameter(int index)
        {
            if (index < 0 || index >= _items.Count) throw new IndexOutOfRangeException($"Invalid index {index} for this OleDbParameterCollection with Count={_items.Count}.");
            return _items[index];
        }

        protected override DbParameter GetParameter(string parameterName)
        {
            int i = IndexOf(parameterName);
            if (i < 0) throw new IndexOutOfRangeException($"An OleDbParameter with ParameterName '{parameterName}' is not contained by this OleDbParameterCollection.");
            return _items[i];
        }

        public int IndexOf(OleDbParameter value) => _items.IndexOf(value);
        public override int IndexOf(object value) => value is OleDbParameter p ? _items.IndexOf(p) : -1;

        public override int IndexOf(string parameterName)
        {
            int i = _items.FindIndex(p => p.ParameterName == parameterName);
            return i >= 0 ? i : _items.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));
        }

        public void Insert(int index, OleDbParameter value) => Insert(index, (object)value);

        public override void Insert(int index, object value)
        {
            OleDbParameter p = Check(value);
            Adopt(p);
            _items.Insert(index, p);
        }

        public void Remove(OleDbParameter value) => Remove((object)value);

        public override void Remove(object value)
        {
            OleDbParameter p = Check(value);
            int i = _items.IndexOf(p);
            if (i < 0) throw new ArgumentException("Attempted to remove an OleDbParameter that is not contained by this OleDbParameterCollection.");
            RemoveAt(i);
        }

        public override void RemoveAt(int index)
        {
            OleDbParameter p = (OleDbParameter)GetParameter(index);
            p.Parent = null;
            _items.RemoveAt(index);
        }

        public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(GetParameter(parameterName)));

        protected override void SetParameter(int index, DbParameter value)
        {
            OleDbParameter p = Check(value);
            OleDbParameter old = (OleDbParameter)GetParameter(index);
            if (old == p) return;
            Adopt(p);
            old.Parent = null;
            _items[index] = p;
        }

        protected override void SetParameter(string parameterName, DbParameter value) => SetParameter(IndexOf(GetParameter(parameterName)), value);
    }
}
