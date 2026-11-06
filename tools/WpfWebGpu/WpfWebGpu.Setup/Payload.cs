// Setup.exe's payload: the installer program, then the installation as a zip, then a trailer saying where the zip
// starts --
//
//   [Setup.exe][zip ... ][offset of the zip: 8 bytes, little endian]["WPFPKG01"]
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
        if (file.Length < TrailerSize) return null;
        var t = new byte[TrailerSize];
        file.Position = file.Length - TrailerSize;
        file.ReadExactly(t);
        if (!t.AsSpan(8).SequenceEqual(Magic)) return null;
        long offset = BinaryPrimitives.ReadInt64LittleEndian(t);
        if (offset <= 0 || offset > file.Length - TrailerSize) return null;
        return (offset, file.Length - TrailerSize - offset);
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
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SetupInfo))]
internal sealed partial class SetupJson : JsonSerializerContext;
