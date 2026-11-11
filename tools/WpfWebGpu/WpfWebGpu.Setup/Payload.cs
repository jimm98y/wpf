// Setup.exe's payload: the installer program, then the installation as a zip, then a trailer saying where the zip
// starts --
//
//   [Setup.exe][zip ... ][offset of the zip: 8 bytes, little endian]["WPFPKG01"][Authenticode signature]
//
// The signature, where the Packager signs it, covers what is before it and is the PE file's certificate table, which
// the header's directory finds.
//
// A large installation is not appended: a signed file is under 4 GB (its certificate table is found by a 32-bit
// offset), and Windows and the tools between a download and a user balk at executables of gigabytes long before
// that. Past MaxEmbedded the zip is a file beside Setup.exe, as installers on several discs have their .cab files
// beside them, and Setup.exe carries what it must be instead --
//
//   [Setup.exe][file name, UTF-8][its length: 4 bytes][the zip's length: 8 bytes][its SHA-256: 32 bytes]["WPFPKG02"][signature]
//
// -- so the signature vouches for the data file too: Setup checks it before reading a byte of it.
//
// The zip is written and read as a stream of its own (OffsetStream), so its offsets are from its own start and it
// is read with ZipArchive as it was written; zip64 where it passes 4 GB. Shared by the Packager, which writes it
// (its project links this file), and Setup, which reads it.
//
//   setup.json       what to install (SetupInfo)
//   app/...          the Program Files folder
//   runtime/...      what runs it (desktop-win-<arch>.pack)
//   programdata/...  ProgramData, put where it is not yet
//   registry/*.reg   the registry, one file per hive, its paths as tokens
//   sqlserver/...    the SQL Server it brings, optional (SqlServer.cs): Microsoft's SQL Server Express setup

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WpfWebGpu.Setup;

public static class Payload
{
    public static ReadOnlySpan<byte> Magic => "WPFPKG01"u8;
    public static ReadOnlySpan<byte> ExternalMagic => "WPFPKG02"u8;
    public const int TrailerSize = 16;

    /// <summary>The largest zip carried inside Setup.exe; a larger one is a file beside it.</summary>
    public const long MaxEmbedded = 2L << 30;

    /// <summary>The trailer of a Setup.exe whose zip is the file <paramref name="fileName"/> beside it.</summary>
    public static byte[] ExternalTrailer(string fileName, long length, byte[] sha256)
    {
        byte[] name = System.Text.Encoding.UTF8.GetBytes(fileName);
        var t = new byte[name.Length + 4 + 8 + 32 + 8];
        name.CopyTo(t, 0);
        BinaryPrimitives.WriteInt32LittleEndian(t.AsSpan(name.Length), name.Length);
        BinaryPrimitives.WriteInt64LittleEndian(t.AsSpan(name.Length + 4), length);
        sha256.CopyTo(t, name.Length + 12);
        ExternalMagic.CopyTo(t.AsSpan(name.Length + 44));
        return t;
    }

    /// <summary>
    /// The installation <paramref name="setupExe"/> carries, as a stream of the zip: appended to it, or the data file
    /// beside it -- opened so that nothing can write it, and its SHA-256 checked against the one Setup.exe holds.
    /// Null when it carries none.
    /// </summary>
    public static Stream? Open(string setupExe, Action<string>? log = null)
    {
        var self = new FileStream(setupExe, FileMode.Open, FileAccess.Read, FileShare.Read);
        long end = SignedEnd(self);
        if (FindMagic(self, end, ExternalMagic) is not long magicAt)
        {
            if (Find(self) is not var (offset, length)) { self.Dispose(); return null; }
            return new OffsetStream(self, offset, length, ownsInner: true);
        }
        try
        {
            var fixedPart = new byte[44];
            self.Position = magicAt - 44;
            self.ReadExactly(fixedPart);
            int nameLength = BinaryPrimitives.ReadInt32LittleEndian(fixedPart);
            long dataLength = BinaryPrimitives.ReadInt64LittleEndian(fixedPart.AsSpan(4));
            byte[] expected = fixedPart.AsSpan(12, 32).ToArray();
            if (nameLength <= 0 || nameLength > 1024 || magicAt - 44 - nameLength < 0) throw new InvalidDataException("this Setup.exe's trailer is damaged.");
            var name = new byte[nameLength];
            self.Position = magicAt - 44 - nameLength;
            self.ReadExactly(name);
            string fileName = System.Text.Encoding.UTF8.GetString(name);
            if (fileName != Path.GetFileName(fileName)) throw new InvalidDataException("this Setup.exe's trailer is damaged.");
            string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(setupExe))!, fileName);
            if (!File.Exists(path)) throw new FileNotFoundException($"{fileName} is not beside {Path.GetFileName(setupExe)}: the installation is in it, and the two go together.", path);

            var data = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            try
            {
                if (data.Length != dataLength) throw new InvalidDataException($"{fileName} is not the one this Setup.exe was made with (its length differs).");
                log?.Invoke($"Checking {fileName}");
                byte[] actual = System.Security.Cryptography.SHA256.HashData(data);
                if (!actual.AsSpan().SequenceEqual(expected)) throw new InvalidDataException($"{fileName} is damaged or not the one this Setup.exe was made with.");
                data.Position = 0;
                return data;
            }
            catch { data.Dispose(); throw; }
        }
        finally { self.Dispose(); }
    }

    /// <summary>The length of the program itself: Setup.exe before its zip or the trailer naming its data file.</summary>
    public static long ProgramLength(FileStream file)
    {
        long end = SignedEnd(file);
        if (FindMagic(file, end, ExternalMagic) is long magicAt)
        {
            var nameLength = new byte[4];
            file.Position = magicAt - 44;
            file.ReadExactly(nameLength);
            return magicAt - 44 - BinaryPrimitives.ReadInt32LittleEndian(nameLength);
        }
        return Find(file)?.Offset ?? end;
    }

    // where the 8-byte magic ends the file's own bytes (before up to 7 bytes of the signature's padding): its offset
    private static long? FindMagic(FileStream file, long end, ReadOnlySpan<byte> magic)
    {
        Span<byte> m = stackalloc byte[8];
        for (int pad = 0; pad < 8 && end - pad >= 8; pad++)
        {
            file.Position = end - pad - 8;
            file.ReadExactly(m);
            if (m.SequenceEqual(magic)) return end - pad - 8;
        }
        return null;
    }

    public static byte[] Trailer(long zipOffset)
    {
        var t = new byte[TrailerSize];
        BinaryPrimitives.WriteInt64LittleEndian(t, zipOffset);
        Magic.CopyTo(t.AsSpan(8));
        return t;
    }

    /// <summary>Where the zip starts in <paramref name="file"/>, and how long it is; null when it has none.</summary>
    public static (long Offset, long Length)? Find(FileStream file)
    {
        long end = SignedEnd(file);
        if (end < TrailerSize) return null;
        var t = new byte[TrailerSize];
        file.Position = end - TrailerSize;
        file.ReadExactly(t);
        if (!t.AsSpan(8).SequenceEqual(Magic))
        {
            // the signature's table starts 8-byte aligned: the trailer may be followed by up to 7 bytes of padding
            for (int pad = 1; pad < 8 && end - pad >= TrailerSize; pad++)
            {
                file.Position = end - pad - TrailerSize;
                file.ReadExactly(t);
                if (t.AsSpan(8).SequenceEqual(Magic)) { end -= pad; break; }
            }
            if (!t.AsSpan(8).SequenceEqual(Magic)) return null;
        }
        long offset = BinaryPrimitives.ReadInt64LittleEndian(t);
        if (offset <= 0 || offset > end - TrailerSize) return null;
        return (offset, end - TrailerSize - offset);
    }

    // where the file's own bytes end: its Authenticode signature's table, when it has one at its end
    private static long SignedEnd(FileStream file)
    {
        var head = new byte[1024];
        file.Position = 0;
        int n = file.Read(head);
        if (n < 0x40 || head[0] != 'M' || head[1] != 'Z') return file.Length;
        int pe = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(0x3C));
        if (pe <= 0 || pe + 24 + 136 > n || BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(pe)) != 0x00004550) return file.Length;
        int optional = pe + 24;
        int security = optional + (BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(optional)) == 0x20B ? 112 : 96) + 4 * 8;
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(security));
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(security + 4));
        return size > 0 && offset > 0 && offset + (long)size == file.Length ? offset : file.Length;
    }
}

/// <summary>A window of a stream, seen as a stream of its own (position 0 at <paramref name="offset"/>).</summary>
public sealed class OffsetStream(Stream inner, long offset, long length = -1, bool ownsInner = false) : Stream
{
    protected override void Dispose(bool disposing)
    {
        if (disposing && ownsInner) inner.Dispose();
        base.Dispose(disposing);
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => length >= 0 ? length : inner.Length - offset;
    public override long Position { get => inner.Position - offset; set => inner.Position = value + offset; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int index, int count)
    {
        if (length >= 0) count = (int)Math.Max(0, Math.Min(count, length - Position));
        return inner.Read(buffer, index, count);
    }
    public override long Seek(long o, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => o,
        SeekOrigin.Current => Position + o,
        _ => Length + o,
    };
    public override void SetLength(long value) => inner.SetLength(value + offset);
    public override void Write(byte[] buffer, int index, int count) => inner.Write(buffer, index, count);
}

/// <summary>What Setup installs (setup.json in the payload).</summary>
public sealed class SetupInfo
{
    public string Product { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    /// <summary>The installation's id (its Apps-and-features entry, its services' and shortcuts' prefix).</summary>
    public string Id { get; set; } = "";
    public List<SetupApp> Apps { get; set; } = new();
    public List<SetupService> Services { get; set; } = new();
    /// <summary>The SQL Server it brings (sqlserver/ in the payload), or none.</summary>
    public SetupSqlServer? SqlServer { get; set; }

    public static SetupInfo Read(Stream s) => JsonSerializer.Deserialize(s, SetupJson.Default.SetupInfo) ?? new SetupInfo();
    public void Write(Stream s) => JsonSerializer.Serialize(s, this, SetupJson.Default.SetupInfo);
}

public sealed class SetupApp
{
    public string Name { get; set; } = "";
    /// <summary>Relative to app/, '/'-separated.</summary>
    public string Exe { get; set; } = "";
}

public sealed class SetupService
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Exe { get; set; } = "";
    public List<string> Args { get; set; } = new();
    /// <summary>"Auto", "Manual" or "Disabled".</summary>
    public string Start { get; set; } = "Auto";
    public List<string> DependsOn { get; set; } = new();
    public Dictionary<string, string> Environment { get; set; } = new();
    /// <summary>"LocalSystem", "NetworkService" or "LocalService": the account the service runs as.</summary>
    public string Account { get; set; } = "LocalSystem";
    /// <summary>The account as it was registered (NT SERVICE\Name, a virtual account); empty: Account's.</summary>
    public string StartName { get; set; } = "";
    public string Description { get; set; } = "";
    public bool DelayedAutoStart { get; set; }
    /// <summary>Its security descriptor as registered (self-relative, base64); empty: Windows' default.</summary>
    public string Security { get; set; } = "";
}

public sealed class SetupSqlServer
{
    /// <summary>What it is ("SQL Server 2025 Express").</summary>
    public string Engine { get; set; } = "";
    /// <summary>Its setup's file name in sqlserver/.</summary>
    public string Media { get; set; } = "";
    /// <summary>The instance it installs (SQLEXPRESS).</summary>
    public string Instance { get; set; } = "SQLEXPRESS";
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SetupInfo))]
internal sealed partial class SetupJson : JsonSerializerContext;
