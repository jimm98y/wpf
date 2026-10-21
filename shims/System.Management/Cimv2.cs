// Runs queries against the classes in Classes.cs, which describe this machine.
//
// What is not modelled reports the truth about this machine rather than failing: a class the shim
// does not know has no instances (and is logged, so the gap is visible), as do the other built-in
// namespaces; a namespace Windows itself would not have without some product installed is
// InvalidNamespace, exactly what such an app sees on a Windows machine without that product.

using System.Collections.Generic;
using System.Globalization;

namespace System.Management
{
    internal static class Cimv2
    {
        // Namespaces every Windows machine has. Queries in those other than root\cimv2 find nothing here.
        private static readonly HashSet<string> s_namespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "root", @"root\cimv2", @"root\default", @"root\wmi", @"root\StandardCimv2", @"root\SecurityCenter2",
            @"root\subscription", @"root\Microsoft\Windows\Storage", @"root\cimv2\power", @"root\cimv2\security\MicrosoftTpm",
        };

        public static bool IsLocalServer(string server) =>
            string.IsNullOrEmpty(server) || server == "." || server.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || server.Equals("127.0.0.1", StringComparison.Ordinal) || server.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

        public static bool IsKnownNamespace(string ns) => s_namespaces.Contains(Normalize(ns));

        private static string Normalize(string ns) => (ns ?? "").Replace('/', '\\').Trim('\\');

        private static bool IsCimv2(ManagementScope scope) => Normalize(scope.EffectiveNamespace).Equals(@"root\cimv2", StringComparison.OrdinalIgnoreCase);

        public static ManagementObjectCollection Query(ManagementScope scope, ObjectQuery query)
        {
            string language = query.QueryLanguage;
            if (!string.Equals(language, "WQL", StringComparison.OrdinalIgnoreCase))
            {
                return Failed(new ManagementException(ManagementStatus.InvalidQueryType));
            }
            string text = query.QueryString?.Trim() ?? "";
            if (!text.StartsWith("select", StringComparison.OrdinalIgnoreCase))
            {
                // ASSOCIATORS OF / REFERENCES OF: no associations are modelled.
                if (text.StartsWith("associators", StringComparison.OrdinalIgnoreCase) || text.StartsWith("references", StringComparison.OrdinalIgnoreCase))
                {
                    ShimLog.Once("wmi:assoc", "System.Management: association queries return nothing here: " + text);
                    return Empty();
                }
                return Failed(new ManagementException(ManagementStatus.InvalidQuery));
            }

            Wql.Select select;
            try
            {
                select = Wql.ParseSelect(text);
            }
            catch (ManagementException e)
            {
                return Failed(e);
            }

            if (select.ClassName.Equals("meta_class", StringComparison.OrdinalIgnoreCase))
            {
                return SchemaQuery(scope, select);
            }
            return Instances(scope, select.ClassName, select.Properties, select.Condition);
        }

        public static ManagementObjectCollection Instances(ManagementScope scope, string className, string[] properties, Wql.Expr condition)
        {
            if (!IsCimv2(scope))
            {
                ShimLog.Once("wmi:ns:" + scope.EffectiveNamespace, $"System.Management: no data for namespace {scope.EffectiveNamespace} ({className})");
                return Empty();
            }
            WmiClass cls = Classes.Find(className);
            if (cls == null)
            {
                ShimLog.Once("wmi:class:" + className, "System.Management: no data for class " + className);
                return Empty();
            }
            if (properties != null)
            {
                foreach (string p in properties)
                {
                    if (!p.StartsWith("__", StringComparison.Ordinal) && cls.Property(p) == null) return Failed(new ManagementException(ManagementStatus.InvalidQuery));
                }
            }

            var items = new List<ManagementBaseObject>();
            try
            {
                foreach (WmiData d in cls.Instances(scope.EffectiveServer))
                {
                    if (condition != null && !condition.Eval(name => Lookup(d, name))) continue;
                    items.Add(new ManagementObject(properties == null ? d : Project(d, properties), scope.Clone()));
                }
            }
            catch (Exception e) when (e is not ManagementException)
            {
                ShimLog.Once("wmi:fail:" + className, $"System.Management: {className} failed: {e.GetType().Name}: {e.Message}");
                return Failed(new ManagementException(ManagementStatus.ProviderFailure, "Provider failure ", e));
            }
            return new ManagementObjectCollection(items, null);
        }

        public static WmiData GetObject(ManagementScope scope, ManagementPath path)
        {
            WmiClass cls = IsCimv2(scope) ? Classes.Find(path.ClassName) : null;
            if (cls == null)
            {
                ShimLog.Once("wmi:get:" + path.ClassName, "System.Management: no data for " + path.Path);
                throw new ManagementException(ManagementStatus.NotFound);
            }
            if (path.IsClass) return cls.Schema(scope.EffectiveServer);

            foreach (WmiData d in cls.Instances(scope.EffectiveServer))
            {
                if (path.IsSingleton || KeysMatch(d, cls, path.Keys)) return d;
            }
            throw new ManagementException(ManagementStatus.NotFound);
        }

        public static ManagementObjectCollection NoAssociations(ManagementObject o)
        {
            ShimLog.Once("wmi:related", "System.Management: related objects are not modelled; none for " + o);
            return Empty();
        }

        private static ManagementObjectCollection SchemaQuery(ManagementScope scope, Wql.Select select)
        {
            var items = new List<ManagementBaseObject>();
            if (IsCimv2(scope))
            {
                foreach (WmiClass cls in Classes.All)
                {
                    WmiData d = cls.Schema(scope.EffectiveServer);
                    if (select.Condition != null && !select.Condition.Eval(name => name.Equals("__this", StringComparison.OrdinalIgnoreCase) ? d.ClassName : Lookup(d, name))) continue;
                    items.Add(new ManagementClass(d, scope.Clone()));
                }
            }
            return new ManagementObjectCollection(items, null);
        }

        private static bool KeysMatch(WmiData d, WmiClass cls, IReadOnlyList<KeyValuePair<string, string>> keys)
        {
            if (keys.Count == 0) return false;
            foreach (KeyValuePair<string, string> k in keys)
            {
                string name = k.Key.Length > 0 ? k.Key : (cls.Keys.Length == 1 ? cls.Keys[0] : null);
                if (name == null) return false;
                object v = d.Find(name)?.Value;
                if (v == null || !string.Equals(Convert.ToString(v, CultureInfo.InvariantCulture), k.Value, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        private static object Lookup(WmiData d, string name)
        {
            if (name.StartsWith("__", StringComparison.Ordinal))
            {
                foreach (PropertyData sp in d.SystemProperties())
                {
                    if (string.Equals(sp.Name, name, StringComparison.OrdinalIgnoreCase)) return sp.Value;
                }
            }
            return d.Find(name)?.Value;
        }

        // SELECT a, b: only those properties come back (accessing another is NotFound, as on Windows).
        private static WmiData Project(WmiData d, string[] properties)
        {
            WmiData p = d.Clone();
            p.Properties.RemoveAll(x => Array.FindIndex(properties, n => n.Equals(x.Name, StringComparison.OrdinalIgnoreCase)) < 0);
            return p;
        }

        private static ManagementObjectCollection Empty() => new ManagementObjectCollection(new List<ManagementBaseObject>(), null);

        private static ManagementObjectCollection Failed(Exception e) => new ManagementObjectCollection(null, e);

        public static CimType CimTypeOf(Type t)
        {
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Boolean: return CimType.Boolean;
                case TypeCode.Byte: return CimType.UInt8;
                case TypeCode.SByte: return CimType.SInt8;
                case TypeCode.Int16: return CimType.SInt16;
                case TypeCode.UInt16: return CimType.UInt16;
                case TypeCode.Int32: return CimType.SInt32;
                case TypeCode.UInt32: return CimType.UInt32;
                case TypeCode.Int64: return CimType.SInt64;
                case TypeCode.UInt64: return CimType.UInt64;
                case TypeCode.Single: return CimType.Real32;
                case TypeCode.Double: return CimType.Real64;
                case TypeCode.Char: return CimType.Char16;
                case TypeCode.String: return CimType.String;
                case TypeCode.DateTime: return CimType.DateTime;
                default: return CimType.Object;
            }
        }

        public static Type ClrTypeOf(CimType t) => t switch
        {
            CimType.Boolean => typeof(bool),
            CimType.UInt8 => typeof(byte),
            CimType.SInt8 => typeof(sbyte),
            CimType.SInt16 => typeof(short),
            CimType.UInt16 => typeof(ushort),
            CimType.SInt32 => typeof(int),
            CimType.UInt32 => typeof(uint),
            CimType.SInt64 => typeof(long),
            CimType.UInt64 => typeof(ulong),
            CimType.Real32 => typeof(float),
            CimType.Real64 => typeof(double),
            CimType.Char16 => typeof(char),
            CimType.Object => typeof(object),
            _ => typeof(string),                  // String, DateTime (DMTF text), Reference
        };
    }

    /// <summary>Reports to wpfrun's compatibility log, when the process has one.</summary>
    internal static class ShimLog
    {
        public static void Once(string key, string message)
        {
            try
            {
                (AppContext.GetData("WpfWebGpu.LogOnce") as Action<string, string>)?.Invoke(key, message);
            }
            catch
            {
            }
        }
    }
}
