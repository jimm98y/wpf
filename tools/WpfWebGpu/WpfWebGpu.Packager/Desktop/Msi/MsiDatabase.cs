// A Windows Installer database, written here: its tables as Windows Installer stores them in the compound file.
//
//   _StringPool, _StringData   every string once: the pool a (length, reference count) per string id from 1, the
//                              codepage first (its high bit set when there are more than 65535 strings, and every
//                              string reference is then 3 bytes, not 2); a string of 64 KB or more takes two entries
//   _Tables, _Columns          the tables, and each one's columns (number, name, type)
//   <table>                    its rows column by column (all of the first column's values, then the second's), the
//                              rows in the order of their primary keys' stored values; a string its id, a 2-byte
//                              integer its value + 0x8000, a 4-byte one its value ^ 0x80000000, null 0; a binary
//                              column 1, the data a stream of its own, "<table>.<key>"
//
// The stream names are compressed as Windows Installer does (two of [0-9A-Za-z._] into one character from 0x3800,
// one alone from 0x4800, a table's name after 0x4840), which keeps them within the compound file's 31 characters.
// A table without rows has no stream.

using System.Text;

namespace WpfWebGpu.Packager.Desktop.Msi;

public sealed class MsiColumn
{
    public required string Name { get; init; }
    public required int Type { get; init; }
    public bool IsString => (Type & 0x0800) != 0 && !IsBinary;
    public bool IsBinary => (Type & ~0x1000 & 0x0F00) == 0x0900 && (Type & 0xFF) == 0;
    public bool IsKey => (Type & 0x2000) != 0;
    public bool IsNullable => (Type & 0x1000) != 0;
    public int IntWidth => (Type & 0xFF) <= 2 ? 2 : 4;
}

public sealed class MsiTable
{
    public required string Name { get; init; }
    public required IReadOnlyList<MsiColumn> Columns { get; init; }
    public List<object?[]> Rows { get; } = new();

    /// <summary>A row: a string, an int, null, or for a binary column the file (a path) or the bytes it holds.</summary>
    public void Add(params object?[] values)
    {
        if (values.Length != Columns.Count) throw new ArgumentException($"{Name}: {Columns.Count} columns, {values.Length} values");
        for (int i = 0; i < values.Length; i++)
        {
            var c = Columns[i];
            object? v = values[i];
            if (v == null) { if (!c.IsNullable && !c.IsBinary) throw new ArgumentException($"{Name}.{c.Name} is not nullable"); continue; }
            if (c.IsBinary) { if (v is not (string or byte[])) throw new ArgumentException($"{Name}.{c.Name}: a path or bytes"); }
            else if (c.IsString) { if (v is not string) throw new ArgumentException($"{Name}.{c.Name}: a string, not {v.GetType().Name}"); }
            else if (v is not int n) throw new ArgumentException($"{Name}.{c.Name}: an integer, not {v.GetType().Name}");
            else if (c.IntWidth == 2 && (n < -32767 || n > 32767)) throw new ArgumentException($"{Name}.{c.Name}: {n} is out of a 2-byte column's range");
        }
        Rows.Add(values);
    }
}

public sealed class MsiDatabase
{
    private readonly Dictionary<string, MsiTable> _tables = new(StringComparer.Ordinal);
    private readonly List<(string Name, string? Path, byte[]? Bytes)> _streams = new();

    /// <summary>The codepage its strings are in (1252 by default; 65001 where they need more).</summary>
    public int Codepage { get; set; } = 1252;

    public IEnumerable<MsiTable> Tables => _tables.Values;

    /// <summary>The table, made with its standard columns the first time.</summary>
    public MsiTable this[string name]
    {
        get
        {
            if (_tables.TryGetValue(name, out var t)) return t;
            if (!MsiSchema.Tables.TryGetValue(name, out var columns)) throw new ArgumentException($"no schema for table {name}");
            return _tables[name] = new MsiTable { Name = name, Columns = columns };
        }
    }

    /// <summary>A stream of its own (an embedded cabinet: the Media table's "#name").</summary>
    public void AddStream(string name, string path) => _streams.Add((name, path, null));

    public void AddStream(string name, byte[] bytes) => _streams.Add((name, null, bytes));

    /// <summary>The codepage's encoding, strict (a character it does not have throws); 1252 from .NET's own code
    /// pages.</summary>
    public static Encoding Encode(int codepage)
    {
        if (codepage != 65001) Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codepage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static string EncodeName(string name, bool table)
    {
        static int Mime(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'A' and <= 'Z' => c - 'A' + 10,
            >= 'a' and <= 'z' => c - 'a' + 36,
            '.' => 62,
            '_' => 63,
            _ => -1,
        };
        var sb = new StringBuilder();
        if (table) sb.Append('䡀');
        for (int i = 0; i < name.Length; i++)
        {
            int a = Mime(name[i]);
            if (a < 0) { sb.Append(name[i]); continue; }
            int b = i + 1 < name.Length ? Mime(name[i + 1]) : -1;
            if (b >= 0) { sb.Append((char)(0x3800 + a + (b << 6))); i++; }
            else sb.Append((char)(0x4800 + a));
        }
        if (sb.Length > 31) throw new PackagerError($"the stream name {name} is too long for a compound file");
        return sb.ToString();
    }

    /// <summary>The database's streams into <paramref name="file"/>.</summary>
    public void WriteTo(CompoundFile file)
    {
        file.RootClsid = new Guid("000C1084-0000-0000-C000-000000000046");
        var tables = _tables.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

        // the strings: their ids, and how often each is referred to
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var strings = new List<string> { "" };
        var refs = new List<int> { 0 };
        int Id(string s)
        {
            if (!ids.TryGetValue(s, out int id)) { id = strings.Count; ids[s] = id; strings.Add(s); refs.Add(0); }
            refs[id]++;
            return id;
        }
        // _Tables and _Columns hold every table, even one without rows
        var tableRows = tables.Select(t => Id(t.Name)).ToList();
        var columnRows = new List<(int Table, int Number, int Name, int Type)>();
        foreach (var t in tables)
            for (int i = 0; i < t.Columns.Count; i++)
                columnRows.Add((Id(t.Name), i + 1, Id(t.Columns[i].Name), t.Columns[i].Type));
        var cells = new Dictionary<MsiTable, uint[][]>();
        foreach (var t in tables)
        {
            var data = new uint[t.Rows.Count][];
            for (int r = 0; r < t.Rows.Count; r++)
            {
                var row = t.Rows[r];
                var stored = new uint[t.Columns.Count];
                for (int c = 0; c < t.Columns.Count; c++)
                {
                    var col = t.Columns[c];
                    object? v = row[c];
                    stored[c] = v == null ? 0u
                        : col.IsBinary ? 1u
                        : col.IsString ? (uint)Id((string)v)
                        : col.IntWidth == 2 ? (uint)((int)v + 0x8000)
                        : (uint)(int)v ^ 0x80000000;
                }
                data[r] = stored;
            }
            cells[t] = data;
        }

        Encoding encoding = Encode(Codepage);
        int refSize = strings.Count > 0xFFFF ? 3 : 2;

        // _StringPool, _StringData
        var pool = new MemoryStream();
        var text = new MemoryStream();
        void U16(int v) { pool.WriteByte((byte)v); pool.WriteByte((byte)(v >> 8)); }
        U16(Codepage & 0xFFFF);
        U16((Codepage >> 16) | (refSize == 3 ? 0x8000 : 0));
        for (int i = 1; i < strings.Count; i++)
        {
            byte[] bytes;
            try { bytes = encoding.GetBytes(strings[i]); }
            catch (EncoderFallbackException) { throw new PackagerError($"\"{strings[i]}\" is not in codepage {Codepage}"); }
            int count = Math.Min(refs[i], 0xFFFF);
            if (bytes.Length == 0) throw new InvalidOperationException("an empty string is null in a Windows Installer table");
            if (bytes.Length < 0x10000) { U16(bytes.Length); U16(count); }
            else { U16(0); U16(count); U16(bytes.Length & 0xFFFF); U16(bytes.Length >> 16); }
            text.Write(bytes);
        }
        file.Add(EncodeName("_StringPool", true), pool.ToArray());
        file.Add(EncodeName("_StringData", true), text.ToArray());

        file.Add(EncodeName("_Tables", true), Columnar(tableRows.Order().Select(id => new[] { (uint)id }).ToList(), new[] { refSize }));
        file.Add(EncodeName("_Columns", true), Columnar(
            columnRows.OrderBy(c => c.Table).ThenBy(c => c.Number)
                .Select(c => new[] { (uint)c.Table, (uint)(c.Number + 0x8000), (uint)c.Name, (uint)(c.Type + 0x8000) }).ToList(),
            new[] { refSize, 2, refSize, 2 }));

        foreach (var t in tables)
        {
            if (t.Rows.Count == 0) continue;
            int[] widths = t.Columns.Select(c => c.IsBinary ? 2 : c.IsString ? refSize : c.IntWidth).ToArray();
            int[] keys = Enumerable.Range(0, t.Columns.Count).Where(i => t.Columns[i].IsKey).ToArray();
            var order = Enumerable.Range(0, t.Rows.Count).ToList();
            order.Sort((a, b) =>
            {
                foreach (int k in keys)
                {
                    int d = cells[t][a][k].CompareTo(cells[t][b][k]);
                    if (d != 0) return d;
                }
                return 0;
            });
            for (int i = 1; i < order.Count; i++)
                if (keys.All(k => cells[t][order[i]][k] == cells[t][order[i - 1]][k]))
                    throw new PackagerError($"{t.Name}: two rows with the key {string.Join(", ", keys.Select(k => t.Rows[order[i]][k]))}");
            file.Add(EncodeName(t.Name, true), Columnar(order.Select(r => cells[t][r]).ToList(), widths));

            // the binary columns' data: "<table>.<key>[.<key>...]"
            for (int c = 0; c < t.Columns.Count; c++)
            {
                if (!t.Columns[c].IsBinary) continue;
                foreach (var row in t.Rows)
                {
                    if (row[c] == null) continue;
                    string name = EncodeName(t.Name + "." + string.Join(".", keys.Select(k => Convert.ToString(row[k], System.Globalization.CultureInfo.InvariantCulture))), false);
                    if (row[c] is string path) file.Add(name, path); else file.Add(name, (byte[])row[c]!);
                }
            }
        }
        foreach (var (name, path, bytes) in _streams)
        {
            string encoded = EncodeName(name, false);
            if (path != null) file.Add(encoded, path); else file.Add(encoded, bytes!);
        }
    }

    private static byte[] Columnar(List<uint[]> rows, int[] widths)
    {
        int rowSize = widths.Sum();
        var data = new byte[rows.Count * rowSize];
        int offset = 0;
        for (int c = 0; c < widths.Length; c++)
        {
            for (int r = 0; r < rows.Count; r++)
            {
                uint v = rows[r][c];
                if (widths[c] < 4 && v >> (8 * widths[c]) != 0) throw new InvalidOperationException($"{v} does not fit {widths[c]} bytes");
                for (int k = 0; k < widths[c]; k++) data[offset + r * widths[c] + k] = (byte)(v >> (8 * k));
            }
            offset += rows.Count * widths[c];
        }
        return data;
    }
}

/// <summary>The standard tables' columns, as Windows Installer's SDK defines them.</summary>
public static class MsiSchema
{
    // a column's type: 0x0D00 a string (0x0F00 a localizable one), 0x0500 a 2-byte integer, 0x0100 a 4-byte one,
    // 0x0900 a binary stream; 0x1000 nullable, 0x2000 part of the primary key; a string's low byte its width
    private static MsiColumn C(string name, int type) => new() { Name = name, Type = type };
    private static MsiColumn S(string name, int width, bool key = false, bool nullable = false, bool localizable = false) =>
        C(name, 0x0D00 | width | (key ? 0x2000 : 0) | (nullable ? 0x1000 : 0) | (localizable ? 0x0200 : 0));
    private static MsiColumn I2(string name, bool key = false, bool nullable = false) => C(name, 0x0502 | (key ? 0x2000 : 0) | (nullable ? 0x1000 : 0));
    private static MsiColumn I4(string name, bool key = false, bool nullable = false) => C(name, 0x0104 | (key ? 0x2000 : 0) | (nullable ? 0x1000 : 0));
    private static MsiColumn V(string name, bool nullable = false) => C(name, 0x0900 | (nullable ? 0x1000 : 0));

    private static MsiColumn[] Sequence() => new[] { S("Action", 72, key: true), S("Condition", 255, nullable: true), I2("Sequence", nullable: true) };

    public static readonly IReadOnlyDictionary<string, MsiColumn[]> Tables = new Dictionary<string, MsiColumn[]>(StringComparer.Ordinal)
    {
        ["Property"] = new[] { S("Property", 72, key: true), S("Value", 0, localizable: true) },
        ["Directory"] = new[] { S("Directory", 72, key: true), S("Directory_Parent", 72, nullable: true), S("DefaultDir", 255, localizable: true) },
        ["Component"] = new[] { S("Component", 72, key: true), S("ComponentId", 38, nullable: true), S("Directory_", 72), I2("Attributes"), S("Condition", 255, nullable: true), S("KeyPath", 72, nullable: true) },
        ["File"] = new[] { S("File", 72, key: true), S("Component_", 72), S("FileName", 255, localizable: true), I4("FileSize"), S("Version", 72, nullable: true), S("Language", 20, nullable: true), I2("Attributes", nullable: true), I4("Sequence") },
        ["Media"] = new[] { I2("DiskId", key: true), I4("LastSequence"), S("DiskPrompt", 64, nullable: true, localizable: true), S("Cabinet", 255, nullable: true), S("VolumeLabel", 32, nullable: true), S("Source", 72, nullable: true) },
        ["Feature"] = new[] { S("Feature", 38, key: true), S("Feature_Parent", 38, nullable: true), S("Title", 64, nullable: true, localizable: true), S("Description", 255, nullable: true, localizable: true), I2("Display", nullable: true), I2("Level"), S("Directory_", 72, nullable: true), I2("Attributes") },
        ["FeatureComponents"] = new[] { S("Feature_", 38, key: true), S("Component_", 72, key: true) },
        ["Registry"] = new[] { S("Registry", 72, key: true), I2("Root"), S("Key", 255, localizable: true), S("Name", 255, nullable: true, localizable: true), C("Value", 0x1F00), S("Component_", 72) },
        ["Shortcut"] = new[] { S("Shortcut", 72, key: true), S("Directory_", 72), S("Name", 128, localizable: true), S("Component_", 72), S("Target", 72), S("Arguments", 255, nullable: true), S("Description", 255, nullable: true, localizable: true), I2("Hotkey", nullable: true), S("Icon_", 72, nullable: true), I2("IconIndex", nullable: true), I2("ShowCmd", nullable: true), S("WkDir", 72, nullable: true) },
        ["Icon"] = new[] { S("Name", 72, key: true), V("Data") },
        ["Binary"] = new[] { S("Name", 72, key: true), V("Data") },
        ["CustomAction"] = new[] { S("Action", 72, key: true), I2("Type"), S("Source", 72, nullable: true), S("Target", 255, nullable: true) },
        ["InstallExecuteSequence"] = Sequence(),
        ["InstallUISequence"] = Sequence(),
        ["AdminExecuteSequence"] = Sequence(),
        ["AdminUISequence"] = Sequence(),
        ["AdvtExecuteSequence"] = Sequence(),
        ["Upgrade"] = new[] { S("UpgradeCode", 38, key: true), S("VersionMin", 20, key: true, nullable: true), S("VersionMax", 20, key: true, nullable: true), S("Language", 255, key: true, nullable: true), I4("Attributes", key: true), S("Remove", 255, nullable: true), S("ActionProperty", 72) },
        ["LaunchCondition"] = new[] { S("Condition", 255, key: true), S("Description", 255, localizable: true) },
        ["RemoveFile"] = new[] { S("FileKey", 72, key: true), S("Component_", 72), S("FileName", 255, nullable: true, localizable: true), S("DirProperty", 72), I2("InstallMode") },
        ["CreateFolder"] = new[] { S("Directory_", 72, key: true), S("Component_", 72, key: true) },
        ["ServiceControl"] = new[] { S("ServiceControl", 72, key: true), S("Name", 255, localizable: true), I2("Event"), S("Arguments", 255, nullable: true, localizable: true), I2("Wait", nullable: true), S("Component_", 72) },
        ["AppSearch"] = new[] { S("Property", 72, key: true), S("Signature_", 72, key: true) },
        ["Signature"] = new[] { S("Signature", 72, key: true), S("FileName", 255, localizable: true), S("MinVersion", 20, nullable: true), S("MaxVersion", 20, nullable: true), I4("MinSize", nullable: true), I4("MaxSize", nullable: true), I4("MinDate", nullable: true), I4("MaxDate", nullable: true), S("Languages", 255, nullable: true) },
        ["RegLocator"] = new[] { S("Signature_", 72, key: true), I2("Root"), S("Key", 255), S("Name", 255, nullable: true), I2("Type", nullable: true) },

        // the dialogs
        ["Dialog"] = new[] { S("Dialog", 72, key: true), I2("HCentering"), I2("VCentering"), I2("Width"), I2("Height"), I4("Attributes", nullable: true), S("Title", 128, nullable: true, localizable: true), S("Control_First", 50), S("Control_Default", 50, nullable: true), S("Control_Cancel", 50, nullable: true) },
        ["Control"] = new[] { S("Dialog_", 72, key: true), S("Control", 50, key: true), S("Type", 20), I2("X"), I2("Y"), I2("Width"), I2("Height"), I4("Attributes", nullable: true), S("Property", 72, nullable: true), S("Text", 0, nullable: true, localizable: true), S("Control_Next", 50, nullable: true), S("Help", 50, nullable: true, localizable: true) },
        ["ControlEvent"] = new[] { S("Dialog_", 72, key: true), S("Control_", 50, key: true), S("Event", 50, key: true), S("Argument", 255, key: true), S("Condition", 255, key: true, nullable: true), I2("Ordering", nullable: true) },
        ["ControlCondition"] = new[] { S("Dialog_", 72, key: true), S("Control_", 50, key: true), S("Action", 50, key: true), S("Condition", 255, key: true) },
        ["EventMapping"] = new[] { S("Dialog_", 72, key: true), S("Control_", 50, key: true), S("Event", 50, key: true), S("Attribute", 50) },
        ["CheckBox"] = new[] { S("Property", 72, key: true), S("Value", 64, nullable: true) },
        ["TextStyle"] = new[] { S("TextStyle", 72, key: true), S("FaceName", 32), I2("Size"), I4("Color", nullable: true), I2("StyleBits", nullable: true) },
        ["UIText"] = new[] { S("Key", 72, key: true), S("Text", 255, nullable: true, localizable: true) },
        ["ActionText"] = new[] { S("Action", 72, key: true), S("Description", 0, nullable: true, localizable: true), S("Template", 0, nullable: true, localizable: true) },
        ["Error"] = new[] { I2("Error", key: true), S("Message", 0, nullable: true, localizable: true) },
    };
}
