// The installation's registry into Windows' own: each hive as the Packager staged it (the registry stand-in's
// format: [key], "name"=kind:value, '%XX' escapes), its paths' tokens made this installation's. A value the machine
// already has is left as it is: an upgrade keeps what the machine has made of its settings.

using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace WpfWebGpu.Setup;

internal static class RegistryImport
{
    public static int Import(string hive, string text, IReadOnlyDictionary<string, string> tokens)
    {
        RegistryKey? root = hive switch
        {
            "HKEY_LOCAL_MACHINE" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
            "HKEY_CURRENT_USER" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
            "HKEY_CLASSES_ROOT" => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64),
            "HKEY_USERS" => RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64),
            _ => null,
        };
        if (root == null) return 0;
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
                current = path.Length == 0 ? null : root.CreateSubKey(path, writable: true);
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
            n++;
        }
        current?.Dispose();
        root.Dispose();
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
