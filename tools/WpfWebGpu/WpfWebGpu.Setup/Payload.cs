// Setup.exe's payload: the installer program, then the installation as a zip, then a trailer saying where the zip
// starts --
//
//   [Setup.exe][zip ... ][offset of the zip: 8 bytes, little endian]["WPFPKG01"][Authenticode signature]
//
// The signature, where the Packager signs it, covers what is before it and is the PE file's certificate table, which
// the header's directory finds.
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
    public const int TrailerSize = 16;

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
public sealed class OffsetStream(Stream inner, long offset, long length = -1) : Stream
{
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
