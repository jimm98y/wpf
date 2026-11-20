// The installation's registry into Windows' own: each hive as the Packager staged it (the registry stand-in's
// format: [key], "name"=kind:value, '%XX' escapes), its paths' tokens made this installation's. A value the machine
// already has is left as it is: an upgrade keeps what the machine has made of its settings. What it sets, and the keys
// it makes, go into the installation's record (InstallRecord.cs) for uninstall /all; Remove takes them away again.

using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace WpfWebGpu.Setup;

internal static class RegistryImport
{
    private static RegistryKey? Root(string hive) => hive switch
    {
        "HKEY_LOCAL_MACHINE" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        "HKEY_CURRENT_USER" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        "HKEY_CLASSES_ROOT" => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64),
        "HKEY_USERS" => RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64),
        _ => null,
    };

    public static int Import(string hive, string text, IReadOnlyDictionary<string, string> tokens, InstallRecord record)
    {
        RegistryKey? root = Root(hive);
        if (root == null) return 0;
        string currentPath = "";
        int n = 0;
        RegistryKey? current = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                current?.Dispose();
                string path = line[1..^1];
                if (path.Length == 0) { current = null; continue; }
                // the keys it makes, from the outermost not there yet
                string[] parts = path.Split('\\');
                for (int i = 1; i <= parts.Length; i++)
                {
                    string sub = string.Join('\\', parts, 0, i);
                    using var existing = root.OpenSubKey(sub);
                    if (existing == null) { record.RegistryKeys.Add(new RegistryItem(hive, sub, null)); break; }
                }
                current = root.CreateSubKey(path, writable: true);
                currentPath = path;
                continue;
            }
            if (current == null || line[0] != '"') continue;
            int eq = line.IndexOf("\"=", StringComparison.Ordinal);
            if (eq < 0) continue;
            string name = Unescape(line[1..eq]);
            string rest = line[(eq + 2)..];
            int colon = rest.IndexOf(':');
            if (colon < 0) continue;
            string kind = rest[..colon], payload = rest[(colon + 1)..];
            if (current.GetValue(name) != null) continue;
            string Resolve(string s) { foreach (var (t, v) in tokens) s = s.Replace(t, v, StringComparison.Ordinal); return s; }
            switch (kind)
            {
                case "str": current.SetValue(name, Resolve(Unescape(payload)), RegistryValueKind.String); break;
                case "expand": current.SetValue(name, Resolve(Unescape(payload)), RegistryValueKind.ExpandString); break;
                case "dword": current.SetValue(name, unchecked((int)uint.Parse(payload, NumberStyles.HexNumber, CultureInfo.InvariantCulture)), RegistryValueKind.DWord); break;
                case "qword": current.SetValue(name, unchecked((long)ulong.Parse(payload, NumberStyles.HexNumber, CultureInfo.InvariantCulture)), RegistryValueKind.QWord); break;
                case "multi": current.SetValue(name, Unescape(payload).Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(Resolve).ToArray(), RegistryValueKind.MultiString); break;
                case "bin": current.SetValue(name, Convert.FromBase64String(payload), RegistryValueKind.Binary); break;
                default: continue;
            }
            record.RegistryValues.Add(new RegistryItem(hive, currentPath, name));
            n++;
        }
        current?.Dispose();
        root.Dispose();
        return n;
    }

    /// <summary>The values and keys the record lists, taken away (values first, the keys it made with all under them);
    /// HKEY_CURRENT_USER is the installing user's (by their SID under HKEY_USERS, where their hive is loaded).</summary>
    public static int Remove(InstallRecord record)
    {
        int n = 0;
        RegistryKey? Open(string hive)
        {
            if (hive == "HKEY_CURRENT_USER" && record.UserSid is { } sid && sid != System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value)
            {
                var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
                var hku = users.OpenSubKey(sid, writable: true);
                users.Dispose();
                return hku;
            }
            return Root(hive);
        }
        foreach (var v in record.RegistryValues)
        {
            using var root = Open(v.Hive);
            using var key = root?.OpenSubKey(v.Key, writable: true);
            if (key?.GetValue(v.Name) == null) continue;
            key.DeleteValue(v.Name!, throwOnMissingValue: false);
            n++;
        }
        foreach (var k in record.RegistryKeys.OrderByDescending(k => k.Key.Length))
        {
            using var root = Open(k.Hive);
            try { root?.DeleteSubKeyTree(k.Key, throwOnMissingSubKey: false); }
            catch (UnauthorizedAccessException) { Console.WriteLine($"  could not remove {k.Hive}\\{k.Key}"); }
        }
        return n;
    }

    private static string Unescape(string s)
    {
        if (s.IndexOf('%') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length && int.TryParse(s.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int c))
            {
                sb.Append((char)c);
                i += 2;
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
