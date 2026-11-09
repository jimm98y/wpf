// An APK's AndroidManifest.xml as it is stored: Android's binary XML. A file of chunks -- a string pool every
// name and value refers to by index, a map of attribute names to resource ids, then the elements -- of which
// the Packager changes only strings (the template's package id and label become the app's) and integers (its
// version code), so the elements stay as the build wrote them and only the string pool is written anew.

using System.Buffers.Binary;
using System.Text;

namespace WpfWebGpu.Packager.Android;

public sealed class Axml
{
    private const ushort ResXml = 0x0003, ResStringPool = 0x0001, ResXmlStartElement = 0x0102;
    private const uint Utf8Flag = 1 << 8;
    private const byte TypeIntDec = 0x10, TypeIntHex = 0x11, TypeIntBoolean = 0x12;

    private readonly byte[] _rest;          // the chunks after the string pool, as they were
    private readonly byte[] _styles;        // the pool's style data, unparsed (a manifest has none in practice)
    private readonly int _styleCount;
    private readonly bool _utf8;

    public List<string> Strings { get; }

    public Axml(byte[] file)
    {
        if (BinaryPrimitives.ReadUInt16LittleEndian(file) != ResXml) throw new PackagerError("not a binary XML file");
        int poolAt = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(2));         // the XML chunk's header size
        if (BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(poolAt)) != ResStringPool) throw new PackagerError("binary XML without a string pool first");
        var pool = file.AsSpan(poolAt);
        int poolHeader = BinaryPrimitives.ReadUInt16LittleEndian(pool[2..]);
        int poolSize = BinaryPrimitives.ReadInt32LittleEndian(pool[4..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(pool[8..]);
        _styleCount = BinaryPrimitives.ReadInt32LittleEndian(pool[12..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(pool[16..]);
        int stringsStart = BinaryPrimitives.ReadInt32LittleEndian(pool[20..]);
        int stylesStart = BinaryPrimitives.ReadInt32LittleEndian(pool[24..]);
        _utf8 = (flags & Utf8Flag) != 0;

        Strings = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(pool[(poolHeader + i * 4)..]);
            Strings.Add(ReadString(pool[(stringsStart + offset)..], _utf8));
        }
        // style offsets and style data, kept as they are
        if (_styleCount > 0)
        {
            int styleOffsets = poolHeader + count * 4;
            var offsets = pool.Slice(styleOffsets, _styleCount * 4).ToArray();
            var data = pool[stylesStart..poolSize].ToArray();
            _styles = offsets.Concat(data).ToArray();
        }
        else _styles = Array.Empty<byte>();
        _rest = file[(poolAt + poolSize)..];
    }

    private static string ReadString(ReadOnlySpan<byte> at, bool utf8)
    {
        if (utf8)
        {
            int p = 0;
            ReadLength8(at, ref p);                         // characters
            int bytes = ReadLength8(at, ref p);
            return Encoding.UTF8.GetString(at.Slice(p, bytes));
        }
        int q = 0;
        int chars = BinaryPrimitives.ReadUInt16LittleEndian(at);
        q = 2;
        if ((chars & 0x8000) != 0) { chars = ((chars & 0x7FFF) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(at[2..]); q = 4; }
        return Encoding.Unicode.GetString(at.Slice(q, chars * 2));
    }

    private static int ReadLength8(ReadOnlySpan<byte> at, ref int p)
    {
        int len = at[p++];
        if ((len & 0x80) != 0) len = ((len & 0x7F) << 8) | at[p++];
        return len;
    }

    /// <summary>Every string equal to <paramref name="from"/>, or starting with it and a '.', replaced (a package
    /// id and the names under it: a provider's authority).</summary>
    public int ReplacePrefix(string from, string to)
    {
        int n = 0;
        for (int i = 0; i < Strings.Count; i++)
        {
            string s = Strings[i];
            if (s == from) { Strings[i] = to; n++; }
            else if (s.StartsWith(from + ".", StringComparison.Ordinal)) { Strings[i] = to + s[from.Length..]; n++; }
        }
        return n;
    }

    /// <summary>Every occurrence of <paramref name="from"/> within the strings replaced (a label, "X (VR)").</summary>
    public int ReplaceWithin(string from, string to)
    {
        int n = 0;
        for (int i = 0; i < Strings.Count; i++)
            if (Strings[i].Contains(from, StringComparison.Ordinal)) { Strings[i] = Strings[i].Replace(from, to, StringComparison.Ordinal); n++; }
        return n;
    }

    /// <summary>The integer value of every attribute named <paramref name="name"/> (versionCode) set.</summary>
    public int SetIntAttribute(string name, int value)
    {
        int nameIndex = Strings.IndexOf(name);
        if (nameIndex < 0) return 0;
        int n = 0, p = 0;
        while (p + 8 <= _rest.Length)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(_rest.AsSpan(p));
            int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(_rest.AsSpan(p + 2));
            int size = BinaryPrimitives.ReadInt32LittleEndian(_rest.AsSpan(p + 4));
            if (size <= 0) break;
            if (type == ResXmlStartElement)
            {
                int ext = p + headerSize;
                int attrStart = BinaryPrimitives.ReadUInt16LittleEndian(_rest.AsSpan(ext + 8));
                int attrSize = BinaryPrimitives.ReadUInt16LittleEndian(_rest.AsSpan(ext + 10));
                int attrCount = BinaryPrimitives.ReadUInt16LittleEndian(_rest.AsSpan(ext + 12));
                for (int a = 0; a < attrCount; a++)
                {
                    int at = ext + attrStart + a * attrSize;
                    if (BinaryPrimitives.ReadInt32LittleEndian(_rest.AsSpan(at + 4)) != nameIndex) continue;
                    byte dataType = _rest[at + 15];
                    if (dataType is not (TypeIntDec or TypeIntHex)) continue;
                    BinaryPrimitives.WriteInt32LittleEndian(_rest.AsSpan(at + 16), value);
                    n++;
                }
            }
            p += size;
        }
        return n;
    }

    /// <summary>The element whose name attribute is <paramref name="name"/> (an activity: android:name) made enabled:
    /// its boolean enabled attribute set true. False when there is no such element or it has no enabled attribute.</summary>
    public bool EnableElement(string name)
    {
        int nameAttr = Strings.IndexOf("name"), enabledAttr = Strings.IndexOf("enabled"), value = Strings.IndexOf(name);
        if (nameAttr < 0 || enabledAttr < 0 || value < 0) return false;
        int p = 0;
        while (p + 8 <= _rest.Length)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(_rest.AsSpan(p));
            int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(_rest.AsSpan(p + 2));
            int size = BinaryPrimitives.ReadInt32LittleEndian(_rest.AsSpan(p + 4));
            if (size <= 0) break;
            if (type == ResXmlStartElement)
            {
                int ext = p + headerSize;
                int attrStart = BinaryPrimitives.ReadUInt16LittleEndian(_rest.AsSpan(ext + 8));
                int attrSize = BinaryPrimitives.ReadUInt16LittleEndian(_rest.AsSpan(ext + 10));
                int attrCount = BinaryPrimitives.ReadUInt16LittleEndian(_rest.AsSpan(ext + 12));
                bool named = false;
                int enabledAt = -1;
                for (int a = 0; a < attrCount; a++)
                {
                    int at = ext + attrStart + a * attrSize;
                    int attrName = BinaryPrimitives.ReadInt32LittleEndian(_rest.AsSpan(at + 4));
                    if (attrName == nameAttr && BinaryPrimitives.ReadInt32LittleEndian(_rest.AsSpan(at + 8)) == value) named = true;
                    else if (attrName == enabledAttr && _rest[at + 15] == TypeIntBoolean) enabledAt = at;
                }
                if (named && enabledAt >= 0)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(_rest.AsSpan(enabledAt + 16), -1);
                    return true;
                }
            }
            p += size;
        }
        return false;
    }

    public byte[] ToArray()
    {
        // the string pool: header, string offsets, style offsets and data as they were, the strings
        var data = new MemoryStream();
        var offsets = new int[Strings.Count];
        for (int i = 0; i < Strings.Count; i++)
        {
            offsets[i] = (int)data.Length;
            WriteString(data, Strings[i], _utf8);
        }
        while (data.Length % 4 != 0) data.WriteByte(0);
        const int poolHeader = 28;
        int styleOffsetBytes = _styleCount * 4;
        int stringsStart = poolHeader + Strings.Count * 4 + styleOffsetBytes;
        int stylesStart = _styleCount > 0 ? stringsStart + (int)data.Length : 0;
        int poolSize = stringsStart + (int)data.Length + (_styles.Length - styleOffsetBytes);

        var pool = new byte[poolSize];
        BinaryPrimitives.WriteUInt16LittleEndian(pool, ResStringPool);
        BinaryPrimitives.WriteUInt16LittleEndian(pool.AsSpan(2), poolHeader);
        BinaryPrimitives.WriteInt32LittleEndian(pool.AsSpan(4), poolSize);
        BinaryPrimitives.WriteInt32LittleEndian(pool.AsSpan(8), Strings.Count);
        BinaryPrimitives.WriteInt32LittleEndian(pool.AsSpan(12), _styleCount);
        BinaryPrimitives.WriteUInt32LittleEndian(pool.AsSpan(16), _utf8 ? Utf8Flag : 0);
        BinaryPrimitives.WriteInt32LittleEndian(pool.AsSpan(20), stringsStart);
        BinaryPrimitives.WriteInt32LittleEndian(pool.AsSpan(24), stylesStart);
        for (int i = 0; i < offsets.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(pool.AsSpan(poolHeader + i * 4), offsets[i]);
        if (_styleCount > 0)
        {
            _styles.AsSpan(0, styleOffsetBytes).CopyTo(pool.AsSpan(poolHeader + Strings.Count * 4));
            _styles.AsSpan(styleOffsetBytes).CopyTo(pool.AsSpan(stylesStart));
        }
        data.ToArray().CopyTo(pool, stringsStart);

        var file = new byte[8 + pool.Length + _rest.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(file, ResXml);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), 8);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), file.Length);
        pool.CopyTo(file, 8);
        _rest.CopyTo(file, 8 + pool.Length);
        return file;
    }

    private static void WriteString(Stream s, string value, bool utf8)
    {
        if (utf8)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            WriteLength8(s, value.Length);
            WriteLength8(s, bytes.Length);
            s.Write(bytes);
            s.WriteByte(0);
            return;
        }
        if (value.Length > 0x7FFF)
        {
            Span<byte> l = stackalloc byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(l, (ushort)(0x8000 | (value.Length >> 16)));
            BinaryPrimitives.WriteUInt16LittleEndian(l[2..], (ushort)value.Length);
            s.Write(l);
        }
        else
        {
            Span<byte> l = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(l, (ushort)value.Length);
            s.Write(l);
        }
        s.Write(Encoding.Unicode.GetBytes(value));
        s.WriteByte(0); s.WriteByte(0);
    }

    private static void WriteLength8(Stream s, int len)
    {
        if (len > 0x7F) { s.WriteByte((byte)(0x80 | (len >> 8))); s.WriteByte((byte)len); }
        else s.WriteByte((byte)len);
    }
}
