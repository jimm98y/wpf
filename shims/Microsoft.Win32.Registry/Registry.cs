// Microsoft.Win32.Registry stand-in, with TWO implementations behind one public surface:
//
//   Windows      the real registry, through advapi32 (see Win32Registry.cs)
//   elsewhere    a tree backed by a per-user file, one per hive (see FileRegistryStore.cs), so
//                that apps which keep their settings in the registry both RUN and REMEMBER off
//                Windows instead of NRE-ing on a null hive or starting fresh every launch
//
// It used to be the in-memory half only, because the choice was made at BUILD time: the Windows
// head referenced the real runtime package and never loaded this assembly at all. A portable
// publish (-p:WpfWebGpuPortable=true) has no build-time choice to make -- one output runs on all
// three desktop systems and the two flavours share an assembly identity, so whichever ships has to
// be correct everywhere.
//
// The Windows path matters more than "writes now persist": WPF reads the registry to decide light
// versus dark (AppsUseLightTheme) and to read a number of system parameters, so an in-memory
// registry on Windows does not fail, it just answers "nothing is there" and the app renders wrong.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Microsoft.Win32
{
    public enum RegistryValueKind { Unknown = 0, String = 1, ExpandString = 2, Binary = 3, DWord = 4, MultiString = 7, QWord = 11, None = -1 }
    public enum RegistryHive { ClassesRoot = unchecked((int)0x80000000), CurrentUser = unchecked((int)0x80000001), LocalMachine = unchecked((int)0x80000002), Users = unchecked((int)0x80000003), PerformanceData = unchecked((int)0x80000004), CurrentConfig = unchecked((int)0x80000005) }
    public enum RegistryView { Default = 0, Registry64 = 0x100, Registry32 = 0x200 }
    public enum RegistryKeyPermissionCheck { Default = 0, ReadSubTree = 1, ReadWriteSubTree = 2 }
    [Flags] public enum RegistryOptions { None = 0, Volatile = 1 }

    public sealed class RegistryKey : IDisposable
    {
        // WPFWEBGPU_REGISTRY_FILE=1 forces the file-backed path even on Windows. It exists because
        // the machines this is developed and built on are Windows ones, and without it the entire
        // off-Windows implementation is code that can only be exercised somewhere else -- which is
        // how the in-memory version kept its "OpenSubKey conjures the key" behaviour for so long.
        internal static readonly bool OnWindows =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            Environment.GetEnvironmentVariable("WPFWEBGPU_REGISTRY_FILE") != "1";

        // Off-Windows path: an in-memory tree that is loaded from, and written back to, a file.
        private readonly Dictionary<string, RegistryKey> _subKeys;
        private readonly Dictionary<string, object> _values;

        // The hive this key belongs to (itself, for a hive) and the file behind that hive. Every
        // mutation rewrites the hive through the root, which is why each key needs to know it.
        private readonly RegistryKey _hive;
        private FileRegistryStore _store;
        private bool _loaded;

        // Windows path: a real HKEY. Predefined hives are pseudo-handles and must never be closed.
        private IntPtr _hKey;
        private readonly bool _predefined;
        private bool _closed;

        public string Name { get; }

        public Microsoft.Win32.SafeHandles.SafeRegistryHandle Handle =>
            new Microsoft.Win32.SafeHandles.SafeRegistryHandle(_hKey, ownsHandle: false);

        internal RegistryKey(string name) : this(name, null) { }

        private RegistryKey(string name, RegistryKey hive)
        {
            Name = name;
            _subKeys = new Dictionary<string, RegistryKey>(StringComparer.OrdinalIgnoreCase);
            _values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            _hive = hive ?? this;
        }

        /// <summary>
        /// Attaches the file behind a hive. Loading is DEFERRED to the first read or write: the six
        /// hives are created by a static initializer, and doing file I/O there would put it on the
        /// first touch of anything registry-shaped, including in processes that never read a value.
        /// </summary>
        internal void AttachStore(string hiveFileName)
        {
            _store = new FileRegistryStore(hiveFileName);
        }

        private void EnsureLoaded()
        {
            if (_hive._loaded || _hive._store == null) return;
            _hive._loaded = true;                 // set first: Load() calls back in through CreateSubKey
            _hive._store.Load(_hive);
        }

        private void Persist()
        {
            if (_hive._store != null && _hive._loaded) _hive._store.Save(_hive);
        }

        // Used by FileRegistryStore while loading and saving, so that reading the file does not
        // recurse into EnsureLoaded and writing it does not go back through the public API.
        internal void SetValueLoaded(string name, object value) => _values[name ?? string.Empty] = value;
        internal object GetLoadedValue(string name) => _values.TryGetValue(name ?? string.Empty, out var v) ? v : null;
        internal string[] LoadedValueNames() { var a = new string[_values.Count]; _values.Keys.CopyTo(a, 0); return a; }
        internal string[] LoadedSubKeyNames() { var a = new string[_subKeys.Count]; _subKeys.Keys.CopyTo(a, 0); return a; }
        internal RegistryKey GetLoadedSubKey(string name) => _subKeys.TryGetValue(name ?? string.Empty, out var k) ? k : null;

        internal RegistryKey(string name, IntPtr hKey, bool predefined)
        {
            Name = name;
            _hKey = hKey;
            _predefined = predefined;
        }

        private bool Native => OnWindows && _hKey != IntPtr.Zero && !_closed;

        // ---- opening and creating ----------------------------------------------------------
        //
        // On Windows OpenSubKey returns NULL for a key that does not exist, exactly as the real API
        // does. That is not a detail: the whole point of reading e.g. Personalization\AppsUseLightTheme
        // is to distinguish "absent" from "present", and the in-memory version's habit of conjuring
        // the key would answer every probe with "yes, empty".

        public RegistryKey OpenSubKey(string name) => OpenSubKey(name, false);

        public RegistryKey OpenSubKey(string name, bool writable)
        {
            if (Native)
            {
                IntPtr h = Win32Registry.Open(_hKey, name ?? string.Empty,
                                              writable ? Win32Registry.KEY_ALL : Win32Registry.KEY_READ);
                return h == IntPtr.Zero ? null : new RegistryKey(Name + "\\" + name, h, predefined: false);
            }

            // Off Windows this used to CREATE the key it was asked to open, so every probe for an
            // absent setting answered "here it is, empty". Now it reports absence, like the real API.
            EnsureLoaded();
            RegistryKey k = this;
            foreach (var part in (name ?? string.Empty).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!k._subKeys.TryGetValue(part, out RegistryKey sub)) return null;
                k = sub;
            }
            return k;
        }

        public RegistryKey OpenSubKey(string name, RegistryKeyPermissionCheck c) =>
            OpenSubKey(name, c == RegistryKeyPermissionCheck.ReadWriteSubTree);

        public RegistryKey CreateSubKey(string name) => CreateSubKey(name, RegistryOptions.None);

        public RegistryKey CreateSubKey(string name, RegistryOptions options)
        {
            if (Native)
            {
                IntPtr h = Win32Registry.Create(_hKey, name ?? string.Empty, Win32Registry.KEY_ALL,
                                                (options & RegistryOptions.Volatile) != 0);
                return h == IntPtr.Zero ? null : new RegistryKey(Name + "\\" + name, h, predefined: false);
            }

            EnsureLoaded();
            RegistryKey k = this;
            foreach (var part in (name ?? string.Empty).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!k._subKeys.TryGetValue(part, out var sub))
                {
                    sub = new RegistryKey(k.Name + "\\" + part, _hive);
                    k._subKeys[part] = sub;
                }
                k = sub;
            }
            return k;
        }

        public RegistryKey CreateSubKey(string name, bool w) => CreateSubKey(name);
        public RegistryKey CreateSubKey(string name, RegistryKeyPermissionCheck c) => CreateSubKey(name);

        // ---- values -------------------------------------------------------------------------

        public object GetValue(string name) => GetValue(name, null);

        public object GetValue(string name, object defaultValue)
        {
            if (Native)
            {
                return Win32Registry.TryGetValue(_hKey, name ?? string.Empty, expand: true, out object v, out _)
                    ? v : defaultValue;
            }
            EnsureLoaded();
            return _values.TryGetValue(name ?? string.Empty, out var value) ? value : defaultValue;
        }

        public object GetValue(string name, object defaultValue, object options)
        {
            if (Native)
            {
                // RegistryValueOptions.DoNotExpandEnvironmentNames == 1, passed as the real enum by
                // callers compiled against the real assembly; taken as `object` here because this
                // assembly does not declare that type.
                bool expand = Convert.ToInt32(options ?? 0) == 0;
                return Win32Registry.TryGetValue(_hKey, name ?? string.Empty, expand, out object v, out _)
                    ? v : defaultValue;
            }
            return GetValue(name, defaultValue);
        }

        public void SetValue(string name, object value) => SetValue(name, value, RegistryValueKind.Unknown);

        public void SetValue(string name, object value, RegistryValueKind kind)
        {
            if (Native) { Win32Registry.SetValue(_hKey, name ?? string.Empty, value, kind); return; }
            EnsureLoaded();
            _values[name ?? string.Empty] = value;
            Persist();
        }

        public void DeleteValue(string name)
        {
            if (Native) { Win32Registry.DeleteValue(_hKey, name ?? string.Empty); return; }
            EnsureLoaded();
            if (_values.Remove(name ?? string.Empty)) Persist();
        }

        public void DeleteValue(string name, bool throwOnMissing) => DeleteValue(name);

        public RegistryValueKind GetValueKind(string name)
        {
            if (Native)
            {
                return Win32Registry.TryGetValue(_hKey, name ?? string.Empty, expand: false, out _, out RegistryValueKind k)
                    ? k : RegistryValueKind.Unknown;
            }

            // Off Windows this answered String for everything, including a DWord, which is wrong in
            // the way that matters: apps branch on the kind to decide how to read a value. The kind
            // is recoverable from what was stored, and the file format keeps it across restarts.
            EnsureLoaded();
            if (!_values.TryGetValue(name ?? string.Empty, out object value)) return RegistryValueKind.Unknown;
            return value switch
            {
                int => RegistryValueKind.DWord,
                long => RegistryValueKind.QWord,
                byte[] => RegistryValueKind.Binary,
                string[] => RegistryValueKind.MultiString,
                null => RegistryValueKind.None,
                _ => RegistryValueKind.String,
            };
        }

        // ---- subkeys ------------------------------------------------------------------------

        public void DeleteSubKey(string name)
        {
            if (Native) { Win32Registry.DeleteKey(_hKey, name ?? string.Empty, tree: false); return; }
            EnsureLoaded();
            if (RemoveSubKeyPath(name)) Persist();
        }

        public void DeleteSubKey(string name, bool t) => DeleteSubKey(name);

        public void DeleteSubKeyTree(string name)
        {
            if (Native) { Win32Registry.DeleteKey(_hKey, name ?? string.Empty, tree: true); return; }
            EnsureLoaded();
            if (RemoveSubKeyPath(name)) Persist();
        }

        /// <summary>
        /// Removes a subkey named by a PATH, not just a single segment. The in-memory version only
        /// ever removed a direct child, so DeleteSubKeyTree(@"Software\Vendor\App") silently did
        /// nothing -- which reads as "delete failed to take" only much later.
        /// </summary>
        private bool RemoveSubKeyPath(string name)
        {
            string[] parts = (name ?? string.Empty).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;

            RegistryKey k = this;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!k._subKeys.TryGetValue(parts[i], out RegistryKey next)) return false;
                k = next;
            }
            return k._subKeys.Remove(parts[parts.Length - 1]);
        }

        public void DeleteSubKeyTree(string name, bool t) => DeleteSubKeyTree(name);

        public string[] GetValueNames()
        {
            if (Native) return Win32Registry.ValueNames(_hKey);
            EnsureLoaded();
            var a = new string[_values.Count]; _values.Keys.CopyTo(a, 0); return a;
        }

        public string[] GetSubKeyNames()
        {
            if (Native) return Win32Registry.SubKeyNames(_hKey);
            EnsureLoaded();
            var a = new string[_subKeys.Count]; _subKeys.Keys.CopyTo(a, 0); return a;
        }

        public int SubKeyCount
        {
            get { if (Native) { Win32Registry.Counts(_hKey, out int s, out _); return s; } EnsureLoaded(); return _subKeys.Count; }
        }

        public int ValueCount
        {
            get { if (Native) { Win32Registry.Counts(_hKey, out _, out int v); return v; } EnsureLoaded(); return _values.Count; }
        }

        // ---- lifetime -----------------------------------------------------------------------

        public void Flush()
        {
            if (Native) { Win32Registry.Flush(_hKey); return; }
            Persist();
        }

        public void Close() => Dispose();

        public void Dispose()
        {
            // A predefined hive is a pseudo-handle (HKEY_CURRENT_USER and friends are constants, not
            // allocations); closing one is meaningless and the real API ignores it too.
            if (Native && !_predefined)
            {
                Win32Registry.Close(_hKey);
                _hKey = IntPtr.Zero;
            }
            _closed = true;
        }

        public override string ToString() => Name;
    }

    public static class Registry
    {
        // These stay FIELDS, not properties: apps are compiled against the real assembly's
        // static readonly fields and emit ldsfld.
        //
        // On Windows they wrap the predefined HKEY pseudo-handles; elsewhere they are the roots of
        // the in-memory tree. (Simplified from a lookup table while chasing a mono wasm "NIY
        // encountered in method .cctor" assertion; that turned out to be the INTERPRETER-only build,
        // not this IL, so treat it as a tidy-up rather than a fix.)
        public static readonly RegistryKey ClassesRoot = Root("HKEY_CLASSES_ROOT", 0x80000000);
        public static readonly RegistryKey CurrentUser = Root("HKEY_CURRENT_USER", 0x80000001);
        public static readonly RegistryKey LocalMachine = Root("HKEY_LOCAL_MACHINE", 0x80000002);
        public static readonly RegistryKey Users = Root("HKEY_USERS", 0x80000003);
        public static readonly RegistryKey PerformanceData = Root("HKEY_PERFORMANCE_DATA", 0x80000004);
        public static readonly RegistryKey CurrentConfig = Root("HKEY_CURRENT_CONFIG", 0x80000005);

        private static RegistryKey Root(string name, uint hive)
        {
            if (RegistryKey.OnWindows) return new RegistryKey(name, unchecked((IntPtr)(int)hive), predefined: true);

            var key = new RegistryKey(name);
            key.AttachStore(name);          // one file per hive; loaded on first use, not here
            return key;
        }

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
            var h = FromPath(keyName, out var rest);
            if (h == null) return defaultValue;
            var k = string.IsNullOrEmpty(rest) ? h : h.OpenSubKey(rest);
            if (k == null) return defaultValue;
            try { return k.GetValue(valueName, defaultValue); }
            finally { if (!ReferenceEquals(k, h)) k.Dispose(); }
        }

        public static void SetValue(string keyName, string valueName, object value) =>
            SetValue(keyName, valueName, value, RegistryValueKind.Unknown);

        public static void SetValue(string keyName, string valueName, object value, RegistryValueKind valueKind)
        {
            var h = FromPath(keyName, out var rest);
            if (h == null) return;
            var k = string.IsNullOrEmpty(rest) ? h : h.CreateSubKey(rest);
            if (k == null) return;
            try { k.SetValue(valueName, value, valueKind); }
            finally { if (!ReferenceEquals(k, h)) k.Dispose(); }
        }
    }
}

namespace Microsoft.Win32.SafeHandles
{
    /// <summary>
    ///  Present so that references to the registry HANDLE type still resolve.
    /// </summary>
    /// <remarks>
    ///  The type has to EXIST because libraries P/Invoke registry APIs through it
    ///  (Microsoft.VisualStudio.Threading's RegNotifyChangeKeyValue, for one), and a missing type in a
    ///  method SIGNATURE is not a lazy failure: mono-aot-cross refuses the whole assembly with "Could
    ///  not load signature ... Could not resolve type with token", which took down an entire AOT build.
    ///
    ///  Deliberately NOT derived from SafeHandle, which was the first attempt: SafeHandle is a critical
    ///  finalizer type, and merely having one in this assembly made the interpreter refuse the class
    ///  ("NIY encountered in method Microsoft.Win32.Registry:.cctor", then a fatal assertion) — so the
    ///  fix for the AOT build broke the interpreter build instead.
    ///
    ///  It now CARRIES the handle, because on Windows there is a real one to carry and a caller that
    ///  P/Invokes through DangerousGetHandle would otherwise be handed zero. Off Windows it is still
    ///  an empty shell, as it was.
    /// </remarks>
    public sealed class SafeRegistryHandle : System.IDisposable
    {
        private readonly System.IntPtr _handle;
        private readonly bool _ownsHandle;

        public SafeRegistryHandle() { }
        public SafeRegistryHandle(System.IntPtr preexistingHandle, bool ownsHandle)
        {
            _handle = preexistingHandle;
            _ownsHandle = ownsHandle;
        }

        public bool IsInvalid => _handle == System.IntPtr.Zero;
        public bool IsClosed => false;
        public System.IntPtr DangerousGetHandle() => _handle;
        public void Dispose() { }
    }
}
