// In-memory Microsoft.Win32.Registry shim (off-Windows). Provides non-null predefined keys and
// no-op/in-memory read-write so unmodified apps that persist settings to the registry run on macOS.
using System;
using System.Collections.Generic;

namespace Microsoft.Win32
{
    public enum RegistryValueKind { Unknown = 0, String = 1, ExpandString = 2, Binary = 3, DWord = 4, MultiString = 7, QWord = 11, None = -1 }
    public enum RegistryHive { ClassesRoot = unchecked((int)0x80000000), CurrentUser = unchecked((int)0x80000001), LocalMachine = unchecked((int)0x80000002), Users = unchecked((int)0x80000003), PerformanceData = unchecked((int)0x80000004), CurrentConfig = unchecked((int)0x80000005) }
    public enum RegistryView { Default = 0, Registry64 = 0x100, Registry32 = 0x200 }
    public enum RegistryKeyPermissionCheck { Default = 0, ReadSubTree = 1, ReadWriteSubTree = 2 }
    [Flags] public enum RegistryOptions { None = 0, Volatile = 1 }

    public sealed class RegistryKey : IDisposable
    {
        private readonly Dictionary<string, RegistryKey> _subKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);
        public string Name { get; }
        internal RegistryKey(string name) { Name = name; }

        public RegistryKey OpenSubKey(string name) => OpenSubKey(name, false);
        public RegistryKey OpenSubKey(string name, bool writable) => CreateSubKey(name);
        public RegistryKey OpenSubKey(string name, RegistryKeyPermissionCheck c) => CreateSubKey(name);
        public RegistryKey CreateSubKey(string name)
        {
            RegistryKey k = this;
            foreach (var part in (name ?? string.Empty).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!k._subKeys.TryGetValue(part, out var sub)) { sub = new RegistryKey(k.Name + "\\" + part); k._subKeys[part] = sub; }
                k = sub;
            }
            return k;
        }
        public RegistryKey CreateSubKey(string name, bool w) => CreateSubKey(name);
        public RegistryKey CreateSubKey(string name, RegistryKeyPermissionCheck c) => CreateSubKey(name);
        public object GetValue(string name) => GetValue(name, null);
        public object GetValue(string name, object defaultValue) => _values.TryGetValue(name ?? string.Empty, out var v) ? v : defaultValue;
        public object GetValue(string name, object defaultValue, object options) => GetValue(name, defaultValue);
        public void SetValue(string name, object value) { _values[name ?? string.Empty] = value; }
        public void SetValue(string name, object value, RegistryValueKind kind) { _values[name ?? string.Empty] = value; }
        public void DeleteValue(string name) { _values.Remove(name ?? string.Empty); }
        public void DeleteValue(string name, bool throwOnMissing) { _values.Remove(name ?? string.Empty); }
        public void DeleteSubKey(string name) { _subKeys.Remove(name ?? string.Empty); }
        public void DeleteSubKey(string name, bool t) { _subKeys.Remove(name ?? string.Empty); }
        public void DeleteSubKeyTree(string name) { _subKeys.Remove(name ?? string.Empty); }
        public void DeleteSubKeyTree(string name, bool t) { _subKeys.Remove(name ?? string.Empty); }
        public string[] GetValueNames() { var a = new string[_values.Count]; _values.Keys.CopyTo(a, 0); return a; }
        public string[] GetSubKeyNames() { var a = new string[_subKeys.Count]; _subKeys.Keys.CopyTo(a, 0); return a; }
        public RegistryValueKind GetValueKind(string name) => RegistryValueKind.String;
        public int SubKeyCount => _subKeys.Count;
        public int ValueCount => _values.Count;
        public void Flush() { }
        public void Close() { }
        public void Dispose() { }
        public override string ToString() => Name;
    }

    public static class Registry
    {
        private static readonly Dictionary<string, RegistryKey> s_hives = new(StringComparer.OrdinalIgnoreCase);
        private static RegistryKey Hive(string n) { if (!s_hives.TryGetValue(n, out var k)) { k = new RegistryKey(n); s_hives[n] = k; } return k; }

        public static readonly RegistryKey ClassesRoot = Hive("HKEY_CLASSES_ROOT");
        public static readonly RegistryKey CurrentUser = Hive("HKEY_CURRENT_USER");
        public static readonly RegistryKey LocalMachine = Hive("HKEY_LOCAL_MACHINE");
        public static readonly RegistryKey Users = Hive("HKEY_USERS");
        public static readonly RegistryKey PerformanceData = Hive("HKEY_PERFORMANCE_DATA");
        public static readonly RegistryKey CurrentConfig = Hive("HKEY_CURRENT_CONFIG");

        private static RegistryKey FromPath(string keyName, out string rest)
        {
            rest = string.Empty;
            if (string.IsNullOrEmpty(keyName)) return null;
            int slash = keyName.IndexOf('\\');
            string hive = slash < 0 ? keyName : keyName.Substring(0, slash);
            rest = slash < 0 ? string.Empty : keyName.Substring(slash + 1);
            switch (hive.ToUpperInvariant())
            {
                case "HKEY_CLASSES_ROOT": case "HKCR": return ClassesRoot;
                case "HKEY_CURRENT_USER": case "HKCU": return CurrentUser;
                case "HKEY_LOCAL_MACHINE": case "HKLM": return LocalMachine;
                case "HKEY_USERS": return Users;
                case "HKEY_PERFORMANCE_DATA": return PerformanceData;
                case "HKEY_CURRENT_CONFIG": return CurrentConfig;
                default: return CurrentUser;
            }
        }
        public static object GetValue(string keyName, string valueName, object defaultValue)
        {
            var h = FromPath(keyName, out var rest); if (h == null) return defaultValue;
            var k = string.IsNullOrEmpty(rest) ? h : h.OpenSubKey(rest); return k == null ? defaultValue : k.GetValue(valueName, defaultValue);
        }
        public static void SetValue(string keyName, string valueName, object value)
        {
            var h = FromPath(keyName, out var rest); if (h == null) return;
            (string.IsNullOrEmpty(rest) ? h : h.CreateSubKey(rest)).SetValue(valueName, value);
        }
        public static void SetValue(string keyName, string valueName, object value, RegistryValueKind valueKind) => SetValue(keyName, valueName, value);
    }
}
