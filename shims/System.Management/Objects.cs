// ManagementBaseObject, ManagementObject, ManagementClass: the members the shim implements. The rest
// of their surface (qualifiers, methods, Put/Delete, async observers) is in Surface.g.cs and throws.

using System.Collections.Generic;

namespace System.Management
{
    /// <summary>A WMI object's state: its class, where it lives, and its properties.</summary>
    internal sealed class WmiData
    {
        public string Server = Environment.MachineName.ToUpperInvariant();
        public string Namespace = @"root\cimv2";
        public string ClassName = "";
        public bool IsClass;
        public string[] Keys = Array.Empty<string>();
        public List<PropertyData> Properties = new List<PropertyData>();

        public WmiData Clone()
        {
            var d = (WmiData)MemberwiseClone();
            d.Properties = new List<PropertyData>(Properties.Count);
            foreach (PropertyData p in Properties) d.Properties.Add(p.Clone());
            return d;
        }

        public PropertyData Find(string name)
        {
            foreach (PropertyData p in Properties)
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        /// <summary>Class.Key="v",... -- or the class alone; null when a key is missing (a projection without it).</summary>
        public string RelativePath
        {
            get
            {
                if (IsClass) return ClassName;
                if (Keys.Length == 0) return ClassName + "=@";
                var path = new ManagementPath { ClassName = ClassName };
                var parts = new List<string>();
                foreach (string k in Keys)
                {
                    PropertyData p = Find(k);
                    if (p == null || p.Value == null) return null;
                    parts.Add(k + "=" + FormatKey(p.Value));
                }
                return ClassName + "." + string.Join(",", parts);
            }
        }

        public string FullPath => RelativePath is string rel ? @"\\" + Server + @"\" + Namespace + ":" + rel : null;

        private static string FormatKey(object v) => v switch
        {
            string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
            bool b => b ? "TRUE" : "FALSE",
            IFormattable f => f.ToString(null, Globalization.CultureInfo.InvariantCulture),
            _ => "\"" + v + "\"",
        };

        public List<PropertyData> SystemProperties()
        {
            string rel = RelativePath;
            var list = new List<PropertyData>
            {
                new PropertyData("__GENUS", CimType.SInt32, false, IsClass ? 1 : 2, "___SYSTEM"),
                new PropertyData("__CLASS", CimType.String, false, ClassName, "___SYSTEM"),
                new PropertyData("__SUPERCLASS", CimType.String, false, null, "___SYSTEM"),
                new PropertyData("__DYNASTY", CimType.String, false, ClassName, "___SYSTEM"),
                new PropertyData("__RELPATH", CimType.String, false, rel, "___SYSTEM"),
                new PropertyData("__PROPERTY_COUNT", CimType.SInt32, false, Properties.Count, "___SYSTEM"),
                new PropertyData("__DERIVATION", CimType.String, true, Array.Empty<string>(), "___SYSTEM"),
                new PropertyData("__SERVER", CimType.String, false, Server, "___SYSTEM"),
                new PropertyData("__NAMESPACE", CimType.String, false, Namespace, "___SYSTEM"),
                new PropertyData("__PATH", CimType.String, false, FullPath, "___SYSTEM"),
            };
            return list;
        }
    }

    public partial class ManagementBaseObject
    {
        internal WmiData _data;

        internal ManagementBaseObject() { }

        internal ManagementBaseObject(WmiData data)
        {
            _data = data;
        }

        /// <summary>The object's state, fetched first if it is a ManagementObject that has not been yet.</summary>
        internal virtual WmiData Data => _data ?? throw new ManagementException(ManagementStatus.NotFound);

        public virtual ManagementPath ClassPath
        {
            get
            {
                WmiData d = Data;
                return new ManagementPath(@"\\" + d.Server + @"\" + d.Namespace + ":" + d.ClassName);
            }
        }

        public object this[string propertyName]
        {
            get => GetPropertyValue(propertyName);
            set => SetPropertyValue(propertyName, value);
        }

        public virtual PropertyDataCollection Properties => new PropertyDataCollection(Data.Properties, Data.ClassName, false);

        public virtual PropertyDataCollection SystemProperties => new PropertyDataCollection(Data.SystemProperties(), "___SYSTEM", true);

        public virtual object Clone() => new ManagementBaseObject(Data.Clone());

        public bool CompareTo(ManagementBaseObject otherObject, ComparisonSettings settings)
        {
            ArgumentNullException.ThrowIfNull(otherObject);
            WmiData a = Data, b = otherObject.Data;
            bool ignoreCase = (settings & ComparisonSettings.IgnoreCase) != 0;
            if ((settings & ComparisonSettings.IgnoreClass) == 0 && !string.Equals(a.ClassName, b.ClassName, StringComparison.OrdinalIgnoreCase)) return false;
            if (a.Properties.Count != b.Properties.Count) return false;
            foreach (PropertyData p in a.Properties)
            {
                PropertyData q = b.Find(p.Name);
                if (q == null || !ValuesEqual(p.Value, q.Value, ignoreCase)) return false;
            }
            return true;
        }

        private static bool ValuesEqual(object x, object y, bool ignoreCase)
        {
            if (x == null || y == null) return x == null && y == null;
            if (x is string s && y is string t) return string.Equals(s, t, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (x is Array ax && y is Array ay)
            {
                if (ax.Length != ay.Length) return false;
                for (int i = 0; i < ax.Length; i++) if (!ValuesEqual(ax.GetValue(i), ay.GetValue(i), ignoreCase)) return false;
                return true;
            }
            return x.Equals(y);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(this, obj)) return true;
            if (obj is not ManagementBaseObject other) return false;
            try { return CompareTo(other, ComparisonSettings.IncludeAll); }
            catch (ManagementException) { return false; }
        }

        public override int GetHashCode()
        {
            try { return StringComparer.OrdinalIgnoreCase.GetHashCode(_data?.FullPath ?? _data?.ClassName ?? ""); }
            catch (ManagementException) { return 0; }
        }

        public object GetPropertyValue(string propertyName)
        {
            ArgumentNullException.ThrowIfNull(propertyName);
            WmiData d = Data;
            if (propertyName.StartsWith("__", StringComparison.Ordinal))
            {
                foreach (PropertyData sp in d.SystemProperties())
                {
                    if (string.Equals(sp.Name, propertyName, StringComparison.OrdinalIgnoreCase)) return sp.Value;
                }
            }
            PropertyData p = d.Find(propertyName);
            if (p == null) throw new ManagementException(ManagementStatus.NotFound);
            return p.Value;
        }

        public void SetPropertyValue(string propertyName, object propertyValue)
        {
            ArgumentNullException.ThrowIfNull(propertyName);
            PropertyData p = Data.Find(propertyName);
            if (p == null) throw new ManagementException(ManagementStatus.NotFound);
            p.Value = propertyValue;
        }
    }

    public partial class ManagementObject
    {
        private ManagementScope _scope;
        private ManagementPath _path;
        private ObjectGetOptions _options;

        public ManagementObject() : this((ManagementScope)null, null, null) { }

        public ManagementObject(ManagementPath path) : this(null, path, null) { }

        public ManagementObject(ManagementPath path, ObjectGetOptions options) : this(null, path, options) { }

        public ManagementObject(ManagementScope scope, ManagementPath path, ObjectGetOptions options)
        {
            _path = path?.Clone();
            _options = options;
            _scope = scope?.Clone();
            // A path that names its namespace (or server) is where the object lives, whatever the scope.
            if (_path != null && (_path.NamespacePath.Length > 0 || _path.Server.Length > 0))
            {
                var ns = new ManagementPath { Server = _path.Server, NamespacePath = _path.NamespacePath.Length > 0 ? _path.NamespacePath : ManagementPath.DefaultPath.NamespacePath };
                _scope = new ManagementScope(ns, _scope?.Options);
            }
        }

        public ManagementObject(string path) : this(null, new ManagementPath(path), null) { }

        public ManagementObject(string path, ObjectGetOptions options) : this(null, new ManagementPath(path), options) { }

        public ManagementObject(string scopeString, string pathString, ObjectGetOptions options)
            : this(new ManagementScope(scopeString), new ManagementPath(pathString), options) { }

        /// <summary>An object a query returned: already fetched.</summary>
        internal ManagementObject(WmiData data, ManagementScope scope) : base(data)
        {
            _scope = scope;
        }

        internal override WmiData Data
        {
            get
            {
                // As on Windows, reading an object that has a path but was never fetched fetches it.
                if (_data == null && _path != null && _path.ClassName.Length > 0) Get();
                return base.Data;
            }
        }

        public override ManagementPath ClassPath
        {
            get
            {
                if (_data == null && (_path == null || _path.ClassName.Length == 0)) return new ManagementPath();
                return base.ClassPath;
            }
        }

        public ObjectGetOptions Options
        {
            get => _options ??= new ObjectGetOptions();
            set => _options = value ?? throw new ArgumentNullException(nameof(value));
        }

        public virtual ManagementPath Path
        {
            get
            {
                if (_data != null && _data.FullPath is string full) return new ManagementPath(full);
                return _path?.Clone() ?? new ManagementPath();
            }
            set
            {
                _path = value?.Clone() ?? new ManagementPath();
                _data = null;
            }
        }

        public ManagementScope Scope
        {
            get => _scope ??= new ManagementScope();
            set { _scope = value?.Clone() ?? throw new ArgumentNullException(nameof(value)); _data = null; }
        }

        public override object Clone()
        {
            var o = new ManagementObject(_data?.Clone(), _scope?.Clone()) { _path = _path?.Clone(), _options = (ObjectGetOptions)_options?.Clone() };
            return o;
        }

        public void Get()
        {
            if (_path == null || _path.ClassName.Length == 0) throw new InvalidOperationException();
            Scope.Connect();
            _data = Cimv2.GetObject(Scope, _path);
        }

        // Associations (ASSOCIATORS OF / REFERENCES OF) are not modelled: no related objects.
        public ManagementObjectCollection GetRelated() => Cimv2.NoAssociations(this);

        public ManagementObjectCollection GetRelated(string relatedClass) => Cimv2.NoAssociations(this);

        public ManagementObjectCollection GetRelationships() => Cimv2.NoAssociations(this);

        public ManagementObjectCollection GetRelationships(string relationshipClass) => Cimv2.NoAssociations(this);

        public override string ToString()
        {
            if (_data != null) return _data.FullPath ?? "";
            return _path?.Path ?? "";
        }
    }

    public partial class ManagementClass
    {
        public ManagementClass() : this((ManagementScope)null, null, null) { }

        public ManagementClass(ManagementPath path) : this(null, path, null) { }

        public ManagementClass(ManagementPath path, ObjectGetOptions options) : this(null, path, options) { }

        public ManagementClass(ManagementScope scope, ManagementPath path, ObjectGetOptions options) : base(scope, AsClassPath(path), options) { }

        public ManagementClass(string path) : this(null, new ManagementPath(path), null) { }

        public ManagementClass(string path, ObjectGetOptions options) : this(null, new ManagementPath(path), options) { }

        public ManagementClass(string scope, string path, ObjectGetOptions options) : this(new ManagementScope(scope), new ManagementPath(path), options) { }

        internal ManagementClass(WmiData data, ManagementScope scope) : base(data, scope) { }

        private static ManagementPath AsClassPath(ManagementPath path)
        {
            if (path != null && path.IsInstance) throw new ArgumentOutOfRangeException(nameof(path));
            return path;
        }

        public override ManagementPath Path
        {
            get => base.Path;
            set => base.Path = AsClassPath(value);
        }

        public override object Clone()
        {
            var c = new ManagementClass(_data?.Clone(), Scope.Clone());
            if (_data == null) c.Path = base.Path;
            return c;
        }

        public ManagementObjectCollection GetInstances() => GetInstances((EnumerationOptions)null);

        public ManagementObjectCollection GetInstances(EnumerationOptions options)
        {
            string className = _data?.ClassName ?? base.Path.ClassName;
            if (string.IsNullOrEmpty(className)) throw new InvalidOperationException();
            Scope.Connect();
            return Cimv2.Instances(Scope, className, null, null);
        }

        // Each class here stands alone (no hierarchy is modelled).
        public ManagementObjectCollection GetSubclasses() => new ManagementObjectCollection(new List<ManagementBaseObject>(), null);

        public ManagementObjectCollection GetSubclasses(EnumerationOptions options) => GetSubclasses();
    }
}
