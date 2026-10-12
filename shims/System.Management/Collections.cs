// Query results and property bags.

using System.Collections;
using System.Collections.Generic;

namespace System.Management
{
    public partial class ManagementObjectCollection : ICollection, IEnumerable, IDisposable
    {
        private readonly List<ManagementBaseObject> _items;
        // What Windows reports while the results are read (an invalid query, say), not when the
        // query is made: thrown from Count and from the enumerator.
        private readonly Exception _error;

        internal ManagementObjectCollection(List<ManagementBaseObject> items, Exception error)
        {
            _items = items ?? new List<ManagementBaseObject>();
            _error = error;
        }

        public int Count
        {
            get
            {
                ThrowIfFailed();
                return _items.Count;
            }
        }

        public bool IsSynchronized => false;

        public object SyncRoot => this;

        public void CopyTo(Array array, int index)
        {
            ArgumentNullException.ThrowIfNull(array);
            ThrowIfFailed();
            if (index < array.GetLowerBound(0) || index > array.GetUpperBound(0) + 1) throw new ArgumentOutOfRangeException(nameof(index));
            if (array.Length - index < _items.Count) throw new ArgumentException(null, nameof(array));
            foreach (ManagementBaseObject o in _items) array.SetValue(o, index++);
        }

        public void CopyTo(ManagementBaseObject[] objectCollection, int index) => CopyTo((Array)objectCollection, index);

        public void Dispose() { }

        public ManagementObjectEnumerator GetEnumerator() => new ManagementObjectEnumerator(this);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private void ThrowIfFailed()
        {
            if (_error != null) throw _error;
        }

        public partial class ManagementObjectEnumerator : IEnumerator, IDisposable
        {
            private readonly ManagementObjectCollection _owner;
            private int _index = -1;

            internal ManagementObjectEnumerator(ManagementObjectCollection owner)
            {
                _owner = owner;
            }

            public ManagementBaseObject Current
            {
                get
                {
                    if (_index < 0 || _index >= _owner._items.Count) throw new InvalidOperationException();
                    return _owner._items[_index];
                }
            }

            object IEnumerator.Current => Current;

            public void Dispose() { }

            public bool MoveNext()
            {
                _owner.ThrowIfFailed();
                if (_index < _owner._items.Count) _index++;
                return _index < _owner._items.Count;
            }

            public void Reset() => _index = -1;
        }
    }

    public partial class PropertyData
    {
        private readonly string _name;
        private readonly CimType _type;
        private readonly bool _isArray;
        private readonly string _origin;
        private object _value;

        internal PropertyData(string name, CimType type, bool isArray, object value, string origin)
        {
            _name = name;
            _type = type;
            _isArray = isArray;
            _value = value;
            _origin = origin;
        }

        internal PropertyData Clone() => new PropertyData(_name, _type, _isArray, _value is ICloneable c ? c.Clone() : _value, _origin);

        public bool IsArray => _isArray;

        public bool IsLocal => true;

        public string Name => _name;

        public string Origin => _origin;

        public QualifierDataCollection Qualifiers => new QualifierDataCollection();

        public CimType Type => _type;

        public object Value
        {
            get => _value;
            set => _value = value;
        }
    }

    public partial class PropertyDataCollection : ICollection, IEnumerable
    {
        private readonly List<PropertyData> _list;
        private readonly string _origin;
        private readonly bool _system;

        internal PropertyDataCollection(List<PropertyData> list, string origin, bool system)
        {
            _list = list;
            _origin = origin;
            _system = system;
        }

        public int Count => _list.Count;

        public bool IsSynchronized => false;

        public virtual PropertyData this[string propertyName]
        {
            get
            {
                ArgumentNullException.ThrowIfNull(propertyName);
                foreach (PropertyData p in _list)
                {
                    if (string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase)) return p;
                }
                throw new ManagementException(ManagementStatus.NotFound);
            }
        }

        public object SyncRoot => this;

        public void Add(string propertyName, CimType propertyType, bool isArray) => AddCore(propertyName, propertyType, isArray, null);

        public virtual void Add(string propertyName, object propertyValue)
        {
            ArgumentNullException.ThrowIfNull(propertyValue);
            Type t = propertyValue.GetType();
            bool isArray = t.IsArray;
            AddCore(propertyName, Cimv2.CimTypeOf(isArray ? t.GetElementType() : t), isArray, propertyValue);
        }

        public void Add(string propertyName, object propertyValue, CimType propertyType) =>
            AddCore(propertyName, propertyType, propertyValue is Array, propertyValue);

        private void AddCore(string name, CimType type, bool isArray, object value)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (_system) throw new ManagementException(ManagementStatus.AccessDenied);
            _list.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            _list.Add(new PropertyData(name, type, isArray, value, _origin));
        }

        public void CopyTo(Array array, int index)
        {
            ArgumentNullException.ThrowIfNull(array);
            if (index < array.GetLowerBound(0) || index > array.GetUpperBound(0) + 1) throw new ArgumentOutOfRangeException(nameof(index));
            if (array.Length - index < _list.Count) throw new ArgumentException(null, nameof(array));
            foreach (PropertyData p in _list) array.SetValue(p, index++);
        }

        public void CopyTo(PropertyData[] propertyArray, int index) => CopyTo((Array)propertyArray, index);

        public PropertyDataEnumerator GetEnumerator() => new PropertyDataEnumerator(_list);

        public virtual void Remove(string propertyName)
        {
            ArgumentNullException.ThrowIfNull(propertyName);
            if (_system) throw new ManagementException(ManagementStatus.AccessDenied);
            if (_list.RemoveAll(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase)) == 0)
                throw new ManagementException(ManagementStatus.NotFound);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public partial class PropertyDataEnumerator : IEnumerator
        {
            private readonly PropertyData[] _items;
            private int _index = -1;

            internal PropertyDataEnumerator(List<PropertyData> list)
            {
                _items = list.ToArray();
            }

            public PropertyData Current
            {
                get
                {
                    if (_index < 0 || _index >= _items.Length) throw new InvalidOperationException();
                    return _items[_index];
                }
            }

            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_index < _items.Length) _index++;
                return _index < _items.Length;
            }

            public void Reset() => _index = -1;
        }
    }

    [ComponentModel.ToolboxItem(false)]
    public partial class ManagementObjectSearcher : ComponentModel.Component
    {
        private ManagementScope _scope;
        private ObjectQuery _query;
        private EnumerationOptions _options;

        public ManagementObjectSearcher() : this((ManagementScope)null, null, null) { }

        public ManagementObjectSearcher(ManagementScope scope, ObjectQuery query) : this(scope, query, null) { }

        public ManagementObjectSearcher(ManagementScope scope, ObjectQuery query, EnumerationOptions options)
        {
            _scope = scope?.Clone();
            _query = (ObjectQuery)query?.Clone();
            _options = (EnumerationOptions)options?.Clone();
        }

        public ManagementObjectSearcher(ObjectQuery query) : this(null, query, null) { }

        public ManagementObjectSearcher(string queryString) : this(null, new ObjectQuery(queryString), null) { }

        public ManagementObjectSearcher(string scope, string queryString) : this(new ManagementScope(scope), new ObjectQuery(queryString), null) { }

        public ManagementObjectSearcher(string scope, string queryString, EnumerationOptions options)
            : this(new ManagementScope(scope), new ObjectQuery(queryString), options) { }

        public EnumerationOptions Options
        {
            get => _options ??= new EnumerationOptions();
            set => _options = value ?? throw new ArgumentNullException(nameof(value));
        }

        public ObjectQuery Query
        {
            get => _query ??= new ObjectQuery();
            set => _query = value ?? throw new ArgumentNullException(nameof(value));
        }

        public ManagementScope Scope
        {
            get => _scope ??= new ManagementScope();
            set => _scope = value?.Clone() ?? throw new ArgumentNullException(nameof(value));
        }

        public ManagementObjectCollection Get()
        {
            Scope.Connect();
            return Cimv2.Query(Scope, Query);
        }

        // Asynchronous queries are not implemented.
        public void Get(ManagementOperationObserver watcher) => throw new PlatformNotSupportedException("System.Management is not supported on this platform.");
    }
}
