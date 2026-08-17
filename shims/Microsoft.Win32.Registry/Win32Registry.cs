// The WINDOWS half of the Microsoft.Win32.Registry stand-in: the real registry, through advapi32.
//
// WHY A SHIM NEEDS A WINDOWS PATH AT ALL. Off Windows this assembly is a substitute for one that
// would otherwise hand back null hives, and Windows never loaded it -- the head referenced the real
// runtime package instead, and picking between them was a build-time decision. A PORTABLE publish
// (-p:WpfWebGpuPortable=true) removes that choice: one output has to run on all three desktop
// systems, and the two flavours share an assembly identity, so exactly one of them can ship. The one
// that ships therefore has to be right everywhere, which means doing the real thing on Windows.
//
// Getting this wrong is quiet. WPF reads the registry to decide light versus dark
// (AppsUseLightTheme), among other things, so a Windows app running on the in-memory version does
// not fail -- it just answers "nothing is there" and renders in the wrong theme.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Microsoft.Win32
{
    /// <summary>
    /// P/Invokes and conversions for the real registry. Only ever reached when running on Windows;
    /// every entry point is called from <see cref="RegistryKey"/> behind that check.
    /// </summary>
    internal static class Win32Registry
    {
        internal const int KEY_READ = 0x20019;
        internal const int KEY_WRITE = 0x20006;
        internal const int KEY_ALL = KEY_READ | KEY_WRITE;

        // RegistryView, as the WOW64 access flags.
        internal const int VIEW_64 = 0x0100;
        internal const int VIEW_32 = 0x0200;

        private const int ERROR_SUCCESS = 0;
        private const int ERROR_FILE_NOT_FOUND = 2;
        private const int ERROR_MORE_DATA = 234;

        private const int REG_NONE = 0;
        private const int REG_SZ = 1;
        private const int REG_EXPAND_SZ = 2;
        private const int REG_BINARY = 3;
        private const int REG_DWORD = 4;
        private const int REG_MULTI_SZ = 7;
        private const int REG_QWORD = 11;

        private const string Advapi = "advapi32.dll";

        [DllImport(Advapi, CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
        private static extern int RegOpenKeyEx(IntPtr hKey, string subKey, int options, int samDesired, out IntPtr result);

        [DllImport(Advapi, CharSet = CharSet.Unicode, EntryPoint = "RegCreateKeyExW")]
        private static extern int RegCreateKeyEx(IntPtr hKey, string subKey, int reserved, string cls, int options,
                                                 int samDesired, IntPtr security, out IntPtr result, out int disposition);

        [DllImport(Advapi, EntryPoint = "RegCloseKey")]
        private static extern int RegCloseKey(IntPtr hKey);

        [DllImport(Advapi, CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
        private static extern int RegQueryValueEx(IntPtr hKey, string valueName, IntPtr reserved,
                                                  out int type, byte[] data, ref int cbData);

        [DllImport(Advapi, CharSet = CharSet.Unicode, EntryPoint = "RegSetValueExW")]
        private static extern int RegSetValueEx(IntPtr hKey, string valueName, int reserved, int type,
                                                byte[] data, int cbData);

        [DllImport(Advapi, CharSet = CharSet.Unicode, EntryPoint = "RegDeleteValueW")]
        private static extern int RegDeleteValue(IntPtr hKey, string valueName);

        [DllImport(Advapi, CharSet = CharSet.Unicode, EntryPoint = "RegDeleteKeyExW")]
        private static extern int RegDeleteKeyEx(IntPtr hKey, string subKey, int samDesired, int reserved);

        [DllImport(Advapi, CharSet = CharSet.Unicode, EntryPoint = "RegDeleteTreeW")]
        private static extern int RegDeleteTree(IntPtr hKey, string subKey);

        [DllImport(Advapi, CharSet = CharSet.Unicode, EntryPoint = "RegEnumKeyExW")]
        private static extern int RegEnumKeyEx(IntPtr hKey, int index, StringBuilder name, ref int cchName,
                                               IntPtr reserved, IntPtr cls, IntPtr cchClass, IntPtr lastWrite);

        [DllImport(Advapi, CharSet = CharSet.Unicode, EntryPoint = "RegEnumValueW")]
        private static extern int RegEnumValue(IntPtr hKey, int index, StringBuilder name, ref int cchName,
                                               IntPtr reserved, IntPtr type, IntPtr data, IntPtr cbData);

        [DllImport(Advapi, CharSet = CharSet.Unicode, EntryPoint = "RegQueryInfoKeyW")]
        private static extern int RegQueryInfoKey(IntPtr hKey, IntPtr cls, IntPtr cchClass, IntPtr reserved,
                                                  out int subKeys, out int maxSubKeyLen, IntPtr maxClassLen,
                                                  out int values, out int maxValueNameLen, IntPtr maxValueLen,
                                                  IntPtr securityDescriptor, IntPtr lastWrite);

        [DllImport(Advapi, EntryPoint = "RegFlushKey")]
        private static extern int RegFlushKey(IntPtr hKey);

        internal static IntPtr Open(IntPtr parent, string subKey, int access)
        {
            return RegOpenKeyEx(parent, subKey, 0, access, out IntPtr h) == ERROR_SUCCESS ? h : IntPtr.Zero;
        }

        internal static IntPtr Create(IntPtr parent, string subKey, int access, bool volatileKey)
        {
            int options = volatileKey ? 1 : 0;      // REG_OPTION_VOLATILE
            return RegCreateKeyEx(parent, subKey, 0, null, options, access, IntPtr.Zero, out IntPtr h, out _) == ERROR_SUCCESS
                ? h : IntPtr.Zero;
        }

        internal static void Close(IntPtr hKey) => RegCloseKey(hKey);

        internal static void Flush(IntPtr hKey) => RegFlushKey(hKey);

        internal static bool TryGetValue(IntPtr hKey, string name, bool expand, out object value, out RegistryValueKind kind)
        {
            value = null;
            kind = RegistryValueKind.Unknown;

            int cb = 0;
            int rc = RegQueryValueEx(hKey, name, IntPtr.Zero, out int type, null, ref cb);
            if (rc == ERROR_FILE_NOT_FOUND) return false;
            if (rc != ERROR_SUCCESS && rc != ERROR_MORE_DATA) return false;

            byte[] data = new byte[cb < 0 ? 0 : cb];
            if (cb > 0)
            {
                rc = RegQueryValueEx(hKey, name, IntPtr.Zero, out type, data, ref cb);
                if (rc != ERROR_SUCCESS) return false;
            }

            kind = (RegistryValueKind)type;
            switch (type)
            {
                case REG_SZ:
                case REG_EXPAND_SZ:
                    string s = Decode(data, cb);
                    // The real API expands REG_EXPAND_SZ unless the caller opts out.
                    value = (type == REG_EXPAND_SZ && expand) ? Environment.ExpandEnvironmentVariables(s) : s;
                    return true;

                case REG_DWORD:
                    value = cb >= 4 ? BitConverter.ToInt32(data, 0) : 0;
                    return true;

                case REG_QWORD:
                    value = cb >= 8 ? BitConverter.ToInt64(data, 0) : 0L;
                    return true;

                case REG_MULTI_SZ:
                    value = Decode(data, cb).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    return true;

                case REG_NONE:
                    value = data;
                    kind = RegistryValueKind.None;
                    return true;

                default:                            // REG_BINARY and anything unrecognised
                    value = data;
                    return true;
            }
        }

        private static string Decode(byte[] data, int cb)
        {
            if (cb <= 0) return string.Empty;
            int chars = cb / 2;
            string s = Encoding.Unicode.GetString(data, 0, chars * 2);
            // Registry strings are stored with their terminator; MULTI_SZ ends with two.
            return s.TrimEnd('\0');
        }

        internal static void SetValue(IntPtr hKey, string name, object value, RegistryValueKind kind)
        {
            if (kind == RegistryValueKind.Unknown) kind = KindOf(value);

            byte[] data;
            switch (kind)
            {
                case RegistryValueKind.DWord:
                    data = BitConverter.GetBytes(Convert.ToInt32(value));
                    break;
                case RegistryValueKind.QWord:
                    data = BitConverter.GetBytes(Convert.ToInt64(value));
                    break;
                case RegistryValueKind.Binary:
                    data = value as byte[] ?? Array.Empty<byte>();
                    break;
                case RegistryValueKind.MultiString:
                    string[] parts = value as string[] ?? Array.Empty<string>();
                    // Each string NUL-terminated, then one more NUL to end the block.
                    data = Encoding.Unicode.GetBytes(string.Join("\0", parts) + "\0\0");
                    break;
                default:
                    data = Encoding.Unicode.GetBytes(Convert.ToString(value) + "\0");
                    break;
            }

            RegSetValueEx(hKey, name, 0, (int)kind, data, data.Length);
        }

        private static RegistryValueKind KindOf(object value) =>
            value switch
            {
                int => RegistryValueKind.DWord,
                long => RegistryValueKind.QWord,
                byte[] => RegistryValueKind.Binary,
                string[] => RegistryValueKind.MultiString,
                _ => RegistryValueKind.String,
            };

        internal static void DeleteValue(IntPtr hKey, string name) => RegDeleteValue(hKey, name);

        internal static void DeleteKey(IntPtr hKey, string subKey, bool tree)
        {
            if (tree) RegDeleteTree(hKey, subKey);
            RegDeleteKeyEx(hKey, subKey, 0, 0);
        }

        internal static void Counts(IntPtr hKey, out int subKeys, out int values)
        {
            if (RegQueryInfoKey(hKey, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                out subKeys, out _, IntPtr.Zero,
                                out values, out _, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != ERROR_SUCCESS)
            {
                subKeys = 0;
                values = 0;
            }
        }

        internal static string[] SubKeyNames(IntPtr hKey)
        {
            var names = new System.Collections.Generic.List<string>();
            var buffer = new StringBuilder(256);
            for (int i = 0; ; i++)
            {
                int len = buffer.Capacity;
                int rc = RegEnumKeyEx(hKey, i, buffer, ref len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (rc == ERROR_MORE_DATA) { buffer.EnsureCapacity(buffer.Capacity * 2); i--; continue; }
                if (rc != ERROR_SUCCESS) break;
                names.Add(buffer.ToString(0, len));
            }
            return names.ToArray();
        }

        internal static string[] ValueNames(IntPtr hKey)
        {
            var names = new System.Collections.Generic.List<string>();
            var buffer = new StringBuilder(256);
            for (int i = 0; ; i++)
            {
                int len = buffer.Capacity;
                int rc = RegEnumValue(hKey, i, buffer, ref len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (rc == ERROR_MORE_DATA) { buffer.EnsureCapacity(buffer.Capacity * 2); i--; continue; }
                if (rc != ERROR_SUCCESS) break;
                names.Add(buffer.ToString(0, len));
            }
            return names.ToArray();
        }
    }
}
