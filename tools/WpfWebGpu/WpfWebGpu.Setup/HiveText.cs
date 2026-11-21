// A hive as the Packager stages it (the registry stand-in's format), read: "[key]" lines, then that key's values as
// "name"=kind:value -- str, expand and multi ('\0'-separated) with '%XX' escapes, dword and qword in hex, bin in
// base64 -- '#' lines comments. Setup imports it into Windows' registry; the Packager writes the MSI's Registry
// table from it.

using System.Globalization;
using System.Text;

namespace WpfWebGpu.Setup;

public static class HiveText
{
    /// <summary>A key (Name and Kind null) or one of its values; Value as it is written (Text gives it as text).</summary>
    public sealed record Line(string Key, string? Name, string? Kind, string? Value);

    public static IEnumerable<Line> Read(string text)
    {
        string? key = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                key = line[1..^1];
                if (key.Length == 0) { key = null; continue; }
                yield return new Line(key, null, null, null);
                continue;
            }
            if (key == null || line[0] != '"') continue;
            int eq = line.IndexOf("\"=", StringComparison.Ordinal);
            if (eq < 0) continue;
            string rest = line[(eq + 2)..];
            int colon = rest.IndexOf(':');
            if (colon < 0) continue;
            yield return new Line(key, Unescape(line[1..eq]), rest[..colon], rest[(colon + 1)..]);
        }
    }

    /// <summary>A str, expand or multi value's text ('\0' between a multi's strings).</summary>
    public static string Text(Line value) => Unescape(value.Value ?? "");

    public static uint Dword(Line value) => uint.Parse(value.Value!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    public static ulong Qword(Line value) => ulong.Parse(value.Value!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    public static byte[] Binary(Line value) => Convert.FromBase64String(value.Value!);

    public static string Unescape(string s)
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
