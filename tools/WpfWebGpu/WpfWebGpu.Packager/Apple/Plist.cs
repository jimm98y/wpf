// Apple's property lists, read and written in managed code: XML, and the binary form (bplist00) the build tools
// write an app's Info.plist in. Dictionaries keep their keys' order.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace WpfWebGpu.Packager.Apple;

public sealed class PlistDict : List<KeyValuePair<string, object>>
{
    public object? this[string key]
    {
        get => this.FirstOrDefault(p => p.Key == key).Value;
        set
        {
            int i = FindIndex(p => p.Key == key);
            if (value == null) { if (i >= 0) RemoveAt(i); return; }
            if (i >= 0) this[i] = new(key, value);
            else Add(new(key, value));
        }
    }
}

public static class Plist
{
    // ---- reading -------------------------------------------------------------------------------------------

    public static object Read(byte[] data) =>
        data.Length >= 8 && Encoding.ASCII.GetString(data, 0, 8) == "bplist00" ? new BinaryReader(data).Root() : ReadXml(data);

    private static object ReadXml(byte[] data)
    {
        var doc = XDocument.Parse(Encoding.UTF8.GetString(data));
        return FromXml(doc.Root!.Elements().First());
    }

    private static object FromXml(XElement e) => e.Name.LocalName switch
    {
        "dict" => DictFromXml(e),
        "array" => e.Elements().Select(FromXml).ToList(),
        "string" => e.Value,
        "integer" => long.Parse(e.Value, CultureInfo.InvariantCulture),
        "real" => double.Parse(e.Value, CultureInfo.InvariantCulture),
        "true" => true,
        "false" => false,
        "date" => DateTime.Parse(e.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
        "data" => Convert.FromBase64String(string.Concat(e.Value.Where(c => !char.IsWhiteSpace(c)))),
        _ => throw new PackagerError($"plist: unknown element <{e.Name.LocalName}>"),
    };

    private static PlistDict DictFromXml(XElement e)
    {
        var d = new PlistDict();
        var items = e.Elements().ToList();
        for (int i = 0; i + 1 < items.Count; i += 2) d.Add(new(items[i].Value, FromXml(items[i + 1])));
        return d;
    }

    private sealed class BinaryReader(byte[] b)
    {
        private int _offsetSize, _refSize;
        private long[] _offsets = Array.Empty<long>();

        public object Root()
        {
            var t = b.AsSpan(b.Length - 32);
            _offsetSize = t[6];
            _refSize = t[7];
            long count = (long)BinaryPrimitives.ReadUInt64BigEndian(t[8..]);
            long top = (long)BinaryPrimitives.ReadUInt64BigEndian(t[16..]);
            long table = (long)BinaryPrimitives.ReadUInt64BigEndian(t[24..]);
            _offsets = new long[count];
            for (long i = 0; i < count; i++) _offsets[i] = Int(b.AsSpan((int)(table + i * _offsetSize), _offsetSize));
            return Object(top);
        }

        private static long Int(ReadOnlySpan<byte> s)
        {
            long v = 0;
            foreach (byte x in s) v = (v << 8) | x;
            return v;
        }

        private (int Count, int Start) Length(int at, int low)
        {
            if (low != 0xF) return (low, at + 1);
            int marker = b[at + 1];
            int n = 1 << (marker & 0xF);
            return ((int)Int(b.AsSpan(at + 2, n)), at + 2 + n);
        }

        private object Object(long index)
        {
            int at = (int)_offsets[index];
            int marker = b[at], high = marker >> 4, low = marker & 0xF;
            switch (high)
            {
                case 0x0: return low == 0x9;                  // 0x08 false, 0x09 true
                case 0x1: return BinaryPrimitives.ReadInt64BigEndian(Pad(b.AsSpan(at + 1, 1 << low), 8));
                case 0x2: return low == 2 ? BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(at + 1)) : BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(at + 1));
                case 0x3: return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(at + 1)));
                case 0x4: { var (n, s) = Length(at, low); return b.AsSpan(s, n).ToArray(); }
                case 0x5: { var (n, s) = Length(at, low); return Encoding.ASCII.GetString(b, s, n); }
                case 0x6: { var (n, s) = Length(at, low); return Encoding.BigEndianUnicode.GetString(b, s, n * 2); }
                case 0xA:
                {
                    var (n, s) = Length(at, low);
                    var list = new List<object>(n);
                    for (int i = 0; i < n; i++) list.Add(Object(Int(b.AsSpan(s + i * _refSize, _refSize))));
                    return list;
                }
                case 0xD:
                {
                    var (n, s) = Length(at, low);
                    var d = new PlistDict();
                    for (int i = 0; i < n; i++)
                    {
                        var key = (string)Object(Int(b.AsSpan(s + i * _refSize, _refSize)));
                        var value = Object(Int(b.AsSpan(s + (n + i) * _refSize, _refSize)));
                        d.Add(new(key, value));
                    }
                    return d;
                }
                default: throw new PackagerError($"plist: unsupported binary object 0x{marker:x2}");
            }
        }

        private static byte[] Pad(ReadOnlySpan<byte> s, int size)
        {
            var r = new byte[size];
            s.CopyTo(r.AsSpan(size - s.Length));
            return r;
        }
    }

    // ---- writing --------------------------------------------------------------------------------------------

    public static byte[] WriteXml(object root)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\">\n");
        Xml(sb, root, 0);
        sb.Append("</plist>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void Xml(StringBuilder sb, object v, int depth)
    {
        string pad = new string('\t', depth);
        switch (v)
        {
            case PlistDict d:
                sb.Append(pad).Append("<dict>\n");
                foreach (var (k, x) in d) { sb.Append(pad).Append("\t<key>").Append(Esc(k)).Append("</key>\n"); Xml(sb, x, depth + 1); }
                sb.Append(pad).Append("</dict>\n");
                break;
            case IList<object> a:
                sb.Append(pad).Append("<array>\n");
                foreach (var x in a) Xml(sb, x, depth + 1);
                sb.Append(pad).Append("</array>\n");
                break;
            case string s: sb.Append(pad).Append("<string>").Append(Esc(s)).Append("</string>\n"); break;
            case bool b: sb.Append(pad).Append(b ? "<true/>\n" : "<false/>\n"); break;
            case long or int: sb.Append(pad).Append("<integer>").Append(Convert.ToInt64(v, CultureInfo.InvariantCulture)).Append("</integer>\n"); break;
            case double or float: sb.Append(pad).Append("<real>").Append(Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)).Append("</real>\n"); break;
            case DateTime t: sb.Append(pad).Append("<date>").Append(t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append("</date>\n"); break;
            case byte[] data: sb.Append(pad).Append("<data>").Append(Convert.ToBase64String(data)).Append("</data>\n"); break;
            default: throw new PackagerError($"plist: cannot write {v.GetType().Name}");
        }
    }

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
