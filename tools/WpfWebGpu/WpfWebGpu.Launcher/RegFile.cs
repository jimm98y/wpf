// `wpfrun reg import|export`: moves registry state between a Windows machine and the registry
// apps see under wpfrun on macOS (the Microsoft.Win32.Registry shim's store in wpfrun's state).
//
// The files are regedit's: "Windows Registry Editor Version 5.00" (UTF-16) or REGEDIT4 (ANSI/UTF-8),
//   [HKEY_CURRENT_USER\Software\Vendor]     a key            [-HKEY_...]   deletes one
//   "Name"="text"   @="default"   "Name"=dword:0000002a   "Name"=hex:01,02   "Name"=hex(7):...
//   "Name"=-        deletes a value; a line ending in \ continues on the next.
// So what `reg export` (or regedit) wrote on Windows imports here, and an export from here imports there.

extern alias RegistryShim;

using System.Globalization;
using System.Text;
using RegistryShim::Microsoft.Win32;

namespace WpfWebGpu;

internal static class RegFile
{
    public static int Run(string[] args)
    {
        if (HostInfo.IsWindows)
            throw new UserError("on Windows an app run through wpfrun uses the real registry: use reg.exe or regedit.");
        if (args.Length < 1) throw new UserError("usage: wpfrun reg import <file.reg> | wpfrun reg export <key> <file.reg>");

        // Before the first use of Registry: the shim picks its store when its hives are created.
        Environment.SetEnvironmentVariable("WPFWEBGPU_REGISTRY_DIR", Path.Combine(Locations.StateRoot, "registry"));
        switch (args[0])
        {
            case "import" when args.Length == 2:
                var (keys, values, deleted) = Import(ReadText(args[1]));
                Console.WriteLine($"imported {values} values in {keys} keys{(deleted > 0 ? $", {deleted} deletions" : "")} into {Path.Combine(Locations.StateRoot, "registry")}");
                return 0;
            case "export" when args.Length == 3:
                int n = Export(args[1], args[2]);
                Console.WriteLine($"exported {n} keys to {args[2]}");
                return 0;
            default:
                throw new UserError("usage: wpfrun reg import <file.reg> | wpfrun reg export <key> <file.reg>");
        }
    }

    // ---- import -------------------------------------------------------------------------------------

    internal static (int Keys, int Values, int Deleted) Import(string text)
    {
        int keys = 0, values = 0, deleted = 0;
        RegistryKey? current = null;
        string? currentPath = null;
        bool header = false;
        foreach (string line in LogicalLines(text))
        {
            string l = line.Trim();
            if (l.Length == 0 || l[0] == ';') continue;
            if (!header)
            {
                if (l != "Windows Registry Editor Version 5.00" && l != "REGEDIT4") throw new UserError("not a .reg file (no 'Windows Registry Editor Version 5.00' or 'REGEDIT4' header).");
                header = true;
                continue;
            }
            if (l[0] == '[')
            {
                if (l[^1] != ']') throw new UserError($"bad key line: {l}");
                current?.Dispose();
                current = null;
                string path = l[1..^1];
                if (path.StartsWith('-'))
                {
                    var (hive, sub) = Split(path[1..]);
                    hive.DeleteSubKeyTree(sub, false);
                    deleted++;
                    currentPath = null;
                    continue;
                }
                var (root, subPath) = Split(path);
                current = subPath.Length == 0 ? root : root.CreateSubKey(subPath);
                currentPath = path;
                keys++;
                continue;
            }
            if (current == null) throw new UserError($"value outside a key{(currentPath == null ? "" : " (its key was deleted)")}: {l}");

            var (name, data) = SplitValue(l);
            if (data == "-")
            {
                current.DeleteValue(name, false);
                deleted++;
                continue;
            }
            var (value, kind) = ParseData(data);
            current.SetValue(name, value, kind);
            values++;
        }
        current?.Dispose();
        return (keys, values, deleted);
    }

    // Joins "\"-continued lines (hex data is wrapped that way) into one.
    private static IEnumerable<string> LogicalLines(string text)
    {
        var sb = new StringBuilder();
        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            // regedit wraps only hex data; a string value may well end in a backslash.
            if (line.EndsWith('\\') && IsHexValue(sb.ToString() + line))
            {
                sb.Append(line, 0, line.Length - 1);
                continue;
            }
            sb.Append(sb.Length > 0 ? line.TrimStart() : line);
            yield return sb.ToString();
            sb.Clear();
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static bool IsHexValue(string line)
    {
        string t = line.TrimStart();
        if (t.Length == 0 || t[0] == '[' || t[0] == ';') return false;
        int at = t.StartsWith("@=", StringComparison.Ordinal) ? 2 : -1;
        if (at < 0)
        {
            // The value name is quoted and may contain anything escaped; the data follows its '"='.
            for (int i = 1; i < t.Length; i++)
            {
                if (t[i] == '\\') { i++; continue; }
                if (t[i] == '"') { at = i + 2; break; }
            }
        }
        return at > 0 && at < t.Length && t[at..].StartsWith("hex", StringComparison.OrdinalIgnoreCase);
    }

    private static (RegistryKey Hive, string Sub) Split(string path)
    {
        int slash = path.IndexOf('\\');
        string hive = slash < 0 ? path : path[..slash];
        string sub = slash < 0 ? "" : path[(slash + 1)..];
        RegistryKey root = hive.ToUpperInvariant() switch
        {
            "HKEY_CURRENT_USER" or "HKCU" => Registry.CurrentUser,
            "HKEY_LOCAL_MACHINE" or "HKLM" => Registry.LocalMachine,
            "HKEY_CLASSES_ROOT" or "HKCR" => Registry.ClassesRoot,
            "HKEY_USERS" or "HKU" => Registry.Users,
            "HKEY_CURRENT_CONFIG" or "HKCC" => Registry.CurrentConfig,
            _ => throw new UserError($"unknown hive '{hive}'."),
        };
        return (root, sub);
    }

    private static (string Name, string Data) SplitValue(string line)
    {
        if (line.StartsWith("@=", StringComparison.Ordinal)) return ("", line[2..]);
        if (line[0] != '"') throw new UserError($"bad value line: {line}");
        var name = new StringBuilder();
        int i = 1;
        for (; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < line.Length) { name.Append(line[++i]); continue; }
            if (c == '"') break;
            name.Append(c);
        }
        if (i + 1 >= line.Length || line[i + 1] != '=') throw new UserError($"bad value line: {line}");
        return (name.ToString(), line[(i + 2)..].Trim());
    }

    internal static (object Value, RegistryValueKind Kind) ParseData(string data)
    {
        if (data.StartsWith('"'))
        {
            if (data.Length < 2 || data[^1] != '"') throw new UserError($"bad string value: {data}");
            var sb = new StringBuilder();
            for (int i = 1; i < data.Length - 1; i++)
            {
                if (data[i] == '\\' && i + 1 < data.Length - 1) i++;
                sb.Append(data[i]);
            }
            return (sb.ToString(), RegistryValueKind.String);
        }
        if (data.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
            return (unchecked((int)uint.Parse(data[6..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)), RegistryValueKind.DWord);

        int colon = data.IndexOf(':');
        if (colon < 0 || !data.StartsWith("hex", StringComparison.OrdinalIgnoreCase)) throw new UserError($"bad value: {data}");
        string type = data[..colon];
        byte[] bytes = Hex(data[(colon + 1)..]);
        int kind = type.Equals("hex", StringComparison.OrdinalIgnoreCase) ? 3
            : type.Length > 5 && type[3] == '(' && type[^1] == ')' ? int.Parse(type[4..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : throw new UserError($"bad value type: {type}");
        return kind switch
        {
            1 => (Utf16(bytes), RegistryValueKind.String),
            2 => (Utf16(bytes), RegistryValueKind.ExpandString),
            4 => (bytes.Length >= 4 ? BitConverter.ToInt32(bytes, 0) : 0, RegistryValueKind.DWord),
            7 => (Utf16(bytes).Split('\0', StringSplitOptions.None) is var parts && parts.Length > 0 ? TrimMulti(parts) : Array.Empty<string>(), RegistryValueKind.MultiString),
            11 => (bytes.Length >= 8 ? BitConverter.ToInt64(bytes, 0) : 0L, RegistryValueKind.QWord),
            _ => (bytes, RegistryValueKind.Binary),
        };
    }

    // A REG_MULTI_SZ ends in two NULs: the strings, then an empty one, then the terminator.
    private static string[] TrimMulti(string[] parts)
    {
        int n = parts.Length;
        while (n > 0 && parts[n - 1].Length == 0) n--;
        return parts[..n];
    }

    private static string Utf16(byte[] bytes)
    {
        string s = Encoding.Unicode.GetString(bytes);
        return s.EndsWith('\0') ? s[..^1] : s;
    }

    private static byte[] Hex(string s)
    {
        var list = new List<byte>();
        foreach (string part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            list.Add(byte.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return list.ToArray();
    }

    private static string ReadText(string path)
    {
        if (!File.Exists(path)) throw new UserError($"no such file: {path}");
        // regedit writes UTF-16LE with a BOM (version 5) or the ANSI code page (REGEDIT4).
        return File.ReadAllText(path, Encoding.UTF8);
    }

    // ---- export -------------------------------------------------------------------------------------

    internal static int Export(string keyPath, string file)
    {
        var (hive, sub) = Split(keyPath.TrimEnd('\\'));
        using RegistryKey? key = sub.Length == 0 ? hive : hive.OpenSubKey(sub);
        if (key == null) throw new UserError($"no such key: {keyPath}");
        var sb = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
        // regedit accepts the full hive names only (HKEY_LOCAL_MACHINE, not HKLM).
        int count = Write(sb, key, sub.Length == 0 ? hive.Name : hive.Name + "\\" + sub);
        // As regedit writes it: UTF-16LE with a byte-order mark.
        File.WriteAllText(file, sb.ToString(), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        return count;
    }

    private static int Write(StringBuilder sb, RegistryKey key, string path)
    {
        sb.Append("\r\n[").Append(path).Append("]\r\n");
        foreach (string name in key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(name.Length == 0 ? "@" : "\"" + Escape(name) + "\"").Append('=');
            sb.Append(FormatData(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), key.GetValueKind(name))).Append("\r\n");
        }
        int count = 1;
        foreach (string child in key.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            using RegistryKey? sub = key.OpenSubKey(child);
            if (sub != null) count += Write(sb, sub, path + "\\" + child);
        }
        return count;
    }

    internal static string FormatData(object? value, RegistryValueKind kind) => value switch
    {
        null => "hex(0):",
        int i => "dword:" + unchecked((uint)i).ToString("x8", CultureInfo.InvariantCulture),
        long l => "hex(b):" + HexList(BitConverter.GetBytes(l)),
        byte[] b => "hex:" + HexList(b),
        string[] m => "hex(7):" + HexList(Encoding.Unicode.GetBytes(string.Concat(m.Select(s => s + "\0")) + "\0")),
        string s when kind == RegistryValueKind.ExpandString => "hex(2):" + HexList(Encoding.Unicode.GetBytes(s + "\0")),
        _ => "\"" + Escape(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "") + "\"",
    };

    private static string HexList(byte[] b) => string.Join(",", b.Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
