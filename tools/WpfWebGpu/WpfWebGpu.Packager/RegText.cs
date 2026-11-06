// regedit's files, read: "Windows Registry Editor Version 5.00" (UTF-16) or REGEDIT4 (ANSI/UTF-8),
//   [HKEY_CURRENT_USER\Software\Vendor]     a key            [-HKEY_...]   deletes one
//   "Name"="text"   @="default"   "Name"=dword:0000002a   "Name"=hex:01,02   "Name"=hex(7):...
//   "Name"=-        deletes a value; a line ending in \ continues on the next.
//
// The parser alone, with no registry behind it: `wpfrun reg import` applies what it reads to the registry
// apps see under wpfrun, and the Packager stages it into the hive files an installation is seeded with.

using System.Globalization;
using System.Text;

namespace WpfWebGpu.Packager;

/// <summary>A value's type, numbered as Windows numbers it (REG_SZ = 1, ...).</summary>
public enum RegKind { String = 1, ExpandString = 2, Binary = 3, DWord = 4, MultiString = 7, QWord = 11 }

/// <summary>One statement of a .reg file: a key opened or deleted, a value set or deleted.</summary>
public readonly record struct RegEntry(RegEntryKind Kind, string Hive, string Key, string Name, object? Value, RegKind ValueKind);

public enum RegEntryKind { Key, DeleteKey, Value, DeleteValue }

public static class RegText
{
    /// <summary>A .reg file's text, from its bytes as regedit wrote them (UTF-16LE with a BOM, or 8-bit).</summary>
    public static string Read(string path)
    {
        if (!File.Exists(path)) throw new PackagerError($"no such file: {path}");
        return File.ReadAllText(path, Encoding.UTF8);   // a BOM picks UTF-16
    }

    /// <summary>The file's statements in order. Hive names come back in full (HKEY_LOCAL_MACHINE).</summary>
    public static IEnumerable<RegEntry> Parse(string text)
    {
        string? hive = null, key = null;
        bool header = false;
        foreach (string line in LogicalLines(text))
        {
            string l = line.Trim();
            if (l.Length == 0 || l[0] == ';') continue;
            if (!header)
            {
                if (l != "Windows Registry Editor Version 5.00" && l != "REGEDIT4") throw new PackagerError("not a .reg file (no 'Windows Registry Editor Version 5.00' or 'REGEDIT4' header).");
                header = true;
                continue;
            }
            if (l[0] == '[')
            {
                if (l[^1] != ']') throw new PackagerError($"bad key line: {l}");
                string path = l[1..^1];
                bool delete = path.StartsWith('-');
                (hive, key) = SplitKey(delete ? path[1..] : path);
                yield return new RegEntry(delete ? RegEntryKind.DeleteKey : RegEntryKind.Key, hive, key, "", null, 0);
                if (delete) hive = key = null;
                continue;
            }
            if (hive == null || key == null) throw new PackagerError($"value outside a key: {l}");

            var (name, data) = SplitValue(l);
            if (data == "-")
            {
                yield return new RegEntry(RegEntryKind.DeleteValue, hive, key, name, null, 0);
                continue;
            }
            var (value, kind) = ParseData(data);
            yield return new RegEntry(RegEntryKind.Value, hive, key, name, value, kind);
        }
    }

    /// <summary>A hive's full name from either spelling (HKLM, HKEY_LOCAL_MACHINE).</summary>
    public static string HiveName(string hive) => hive.ToUpperInvariant() switch
    {
        "HKEY_CURRENT_USER" or "HKCU" => "HKEY_CURRENT_USER",
        "HKEY_LOCAL_MACHINE" or "HKLM" => "HKEY_LOCAL_MACHINE",
        "HKEY_CLASSES_ROOT" or "HKCR" => "HKEY_CLASSES_ROOT",
        "HKEY_USERS" or "HKU" => "HKEY_USERS",
        "HKEY_CURRENT_CONFIG" or "HKCC" => "HKEY_CURRENT_CONFIG",
        _ => throw new PackagerError($"unknown hive '{hive}'."),
    };

    public static (string Hive, string Key) SplitKey(string path)
    {
        int slash = path.IndexOf('\\');
        string hive = slash < 0 ? path : path[..slash];
        return (HiveName(hive), slash < 0 ? "" : path[(slash + 1)..]);
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

    private static (string Name, string Data) SplitValue(string line)
    {
        if (line.StartsWith("@=", StringComparison.Ordinal)) return ("", line[2..]);
        if (line[0] != '"') throw new PackagerError($"bad value line: {line}");
        var name = new StringBuilder();
        int i = 1;
        for (; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < line.Length) { name.Append(line[++i]); continue; }
            if (c == '"') break;
            name.Append(c);
        }
        if (i + 1 >= line.Length || line[i + 1] != '=') throw new PackagerError($"bad value line: {line}");
        return (name.ToString(), line[(i + 2)..].Trim());
    }

    /// <summary>A value's data as a .reg writes it after the '='.</summary>
    public static (object Value, RegKind Kind) ParseData(string data)
    {
        if (data.StartsWith('"'))
        {
            if (data.Length < 2 || data[^1] != '"') throw new PackagerError($"bad string value: {data}");
            var sb = new StringBuilder();
            for (int i = 1; i < data.Length - 1; i++)
            {
                if (data[i] == '\\' && i + 1 < data.Length - 1) i++;
                sb.Append(data[i]);
            }
            return (sb.ToString(), RegKind.String);
        }
        if (data.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
            return (unchecked((int)uint.Parse(data[6..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)), RegKind.DWord);

        int colon = data.IndexOf(':');
        if (colon < 0 || !data.StartsWith("hex", StringComparison.OrdinalIgnoreCase)) throw new PackagerError($"bad value: {data}");
        string type = data[..colon];
        byte[] bytes = Hex(data[(colon + 1)..]);
        int kind = type.Equals("hex", StringComparison.OrdinalIgnoreCase) ? 3
            : type.Length > 5 && type[3] == '(' && type[^1] == ')' ? int.Parse(type[4..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : throw new PackagerError($"bad value type: {type}");
        return kind switch
        {
            1 => (Utf16(bytes), RegKind.String),
            2 => (Utf16(bytes), RegKind.ExpandString),
            4 => (bytes.Length >= 4 ? BitConverter.ToInt32(bytes, 0) : 0, RegKind.DWord),
            7 => (TrimMulti(Utf16(bytes).Split('\0')), RegKind.MultiString),
            11 => (bytes.Length >= 8 ? BitConverter.ToInt64(bytes, 0) : 0L, RegKind.QWord),
            _ => (bytes, RegKind.Binary),
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
}
