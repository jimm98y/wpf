// A macOS component package -- what pkgbuild makes -- written here, so that its payload is compressed on every core.
//
// pkgbuild compresses the payload on one thread: 8.5 GB of installation took it most of ten minutes, the rest of the
// machine idle. The payload is a gzipped cpio archive, as pkgbuild writes it -- one gzip stream, made here as pigz
// makes one: the archive cut into 16 MB chunks, each deflated on its own (by .NET's own deflate, on every machine) and
// ended on a byte boundary (a sync flush), all at once, then joined in order behind one header, with the whole
// archive's CRC (kept as the bytes go by) after them. xz (pkgbuild's "latest", pbzx) was tried: a third smaller, but seven times slower a
// core than deflate, which a busy machine's cores did not make up for. A fast build (for testing) stores the blocks.
//
// The rest is pkgbuild's: the cpio (odc) of the root, every entry root:wheel ("--ownership recommended") with its
// mode -- 0755 for a folder and for code (an executable bit set, or, on Windows, which keeps none, a Mach-O or a
// script by its first bytes), 0644 for the rest, as chmod -R u+rwX,go+rX,go-w leaves them; the Bom (Bom.cs, from the
// listing made while the payload is written); the scripts as a gzipped cpio of their own; PackageInfo naming the
// scripts and the bundles, none of them relocatable. The product archive holds them as they are (MacPackager), so
// nothing here needs a Mac.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace WpfWebGpu.Packager.Desktop;

internal static partial class MacComponent
{
    private const int ChunkSize = 16 * 1024 * 1024;

    /// <summary>Writes the component package of <paramref name="root"/>, installed at "/", with <paramref name="scripts"/>'
    /// preinstall and postinstall, as the folder a product archive holds it as (<paramref name="expanded"/>: Bom, Payload,
    /// Scripts, PackageInfo). Returns what it installs, in kilobytes.</summary>
    public static long Write(string root, string scripts, string identifier, string version, bool compress, string expanded, Action<string> log)
    {
        if (Directory.Exists(expanded)) Directory.Delete(expanded, recursive: true);
        Directory.CreateDirectory(expanded);

        var watch = Stopwatch.StartNew();
        var listing = new StringBuilder();
        // as pkgbuild counts them: every entry, and each file's and link's size in whole kilobytes
        long installKBytes = 0, payloadBytes = 0;
        int entries = 0;
        using (var payload = new ParallelGzipStream(File.Create(Path.Combine(expanded, "Payload")), compress))
        {
            var cpio = new CpioWriter(payload);
            foreach (var entry in Entries(root))
            {
                string path = entry.Relative.Length == 0 ? "." : "./" + entry.Relative;
                if (path.IndexOfAny(new[] { '\t', '\n' }) >= 0) throw new PackagerError($"a macOS package cannot hold \"{path}\"");
                uint mode = Mode(entry.Info);
                long mtime = new DateTimeOffset(entry.Info.LastWriteTimeUtc).ToUnixTimeSeconds();
                string octal = Convert.ToString(mode, 8);
                entries++;
                if (entry.Info.LinkTarget is string target)
                {
                    byte[] link = Encoding.UTF8.GetBytes(target);
                    cpio.Add(path, mode, mtime, link.Length, s => s.Write(link));
                    listing.Append($"{path}\t{octal}\t0/0\t{link.Length}\t{Cksum.Of(link)}\t{target}\n");
                    installKBytes += (link.Length + 1023) / 1024;
                }
                else if (entry.Info is DirectoryInfo)
                {
                    cpio.Add(path, mode, mtime, 0, null);
                    listing.Append($"{path}\t{octal}\t0/0\n");
                }
                else
                {
                    long size = ((FileInfo)entry.Info).Length;
                    uint sum = 0;
                    cpio.Add(path, mode, mtime, size, s => sum = CopyWithCksum(entry.Info.FullName, s, size));
                    listing.Append($"{path}\t{octal}\t0/0\t{size}\t{sum}\n");
                    installKBytes += (size + 1023) / 1024;
                    payloadBytes += size;
                }
            }
            cpio.Finish();
        }
        long payloadSize = new FileInfo(Path.Combine(expanded, "Payload")).Length;
        log($"  payload: {entries} entries, {payloadBytes / (1024 * 1024)} MB in {payloadSize / (1024 * 1024)} MB "
            + $"({(compress ? "compressed on " + Environment.ProcessorCount + " cores" : "stored")}) in {watch.Elapsed.TotalSeconds:0} s");

        Bom.Write(Bom.ReadListing(listing.ToString()), Path.Combine(expanded, "Bom"));

        // the scripts as a flat package keeps them: a gzipped cpio of their folder
        using (var archive = new ParallelGzipStream(File.Create(Path.Combine(expanded, "Scripts")), compress: true))
        {
            var cpio = new CpioWriter(archive);
            foreach (var entry in Entries(scripts))
            {
                string path = entry.Relative.Length == 0 ? "." : "./" + entry.Relative;
                long mtime = new DateTimeOffset(entry.Info.LastWriteTimeUtc).ToUnixTimeSeconds();
                if (entry.Info is DirectoryInfo) cpio.Add(path, 0x4000 | 0b111_101_101, mtime, 0, null);
                else
                {
                    byte[] bytes = File.ReadAllBytes(entry.Info.FullName);
                    cpio.Add(path, 0x8000 | 0b111_101_101, mtime, bytes.Length, s => s.Write(bytes));    // scripts run
                }
            }
            cpio.Finish();
        }

        File.WriteAllText(Path.Combine(expanded, "PackageInfo"), PackageInfo(root, scripts, identifier, version, entries, installKBytes));
        return installKBytes;
    }

    // ---- the entries ----------------------------------------------------------------------------------------

    private readonly record struct Entry(string Relative, FileSystemInfo Info);

    // the root first, then each folder before what it holds, in a stable order; links are entries, never followed
    private static IEnumerable<Entry> Entries(string root)
    {
        yield return new Entry("", new DirectoryInfo(root));
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            var children = new DirectoryInfo(dir).EnumerateFileSystemInfos()
                .Where(c => c.Name != ".DS_Store" && !c.Name.StartsWith("._", StringComparison.Ordinal))
                .OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
            var subdirs = new List<string>();
            foreach (var child in children)
            {
                yield return new Entry(Path.GetRelativePath(root, child.FullName).Replace('\\', '/'), child);
                if (child is DirectoryInfo && child.LinkTarget == null) subdirs.Add(child.FullName);
            }
            // depth first, in order: pushed last to first
            for (int i = subdirs.Count - 1; i >= 0; i--) pending.Push(subdirs[i]);
        }
    }

    private static uint Mode(FileSystemInfo info)
    {
        if (info.LinkTarget != null) return 0xA000u | 0b111_101_101;
        if (info is DirectoryInfo) return 0x4000u | 0b111_101_101;
        bool executable = OperatingSystem.IsWindows() ? IsCode(info.FullName) : ((uint)info.UnixFileMode & 0b001_001_001) != 0;
        return 0x8000u | (executable ? 0b111_101_101u : 0b110_100_100u);
    }

    // what runs, by its first bytes: a Mach-O (thin or universal, either byte order) or a script ("#!")
    private static bool IsCode(string path)
    {
        using var s = File.OpenRead(path);
        Span<byte> head = stackalloc byte[4];
        if (s.Read(head) < 2) return false;
        if (head[0] == '#' && head[1] == '!') return true;
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(head);
        return magic is 0xfeedfacf or 0xfeedface or 0xcffaedfe or 0xcefaedfe or 0xbebafeca or 0xcafebabe;
    }

    private static uint CopyWithCksum(string path, Stream to, long size)
    {
        var sum = new Cksum();
        byte[] buffer = new byte[1024 * 1024];
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        long left = size;
        while (left > 0)
        {
            int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (n == 0) throw new PackagerError($"{path} changed while it was packaged");
            sum.Add(buffer.AsSpan(0, n));
            to.Write(buffer, 0, n);
            left -= n;
        }
        return sum.Result(size);
    }

    // ---- PackageInfo ----------------------------------------------------------------------------------------

    private static string PackageInfo(string root, string scripts, string identifier, string version, int entries, long installKBytes)
    {
        // the app bundles, as pkgbuild --analyze finds them: kept where they are installed (not relocatable),
        // upgraded in place, and only by a bundle of the same identifier
        var bundles = new List<(string Path, string Id, string Short, string Version)>();
        foreach (string app in Directory.EnumerateDirectories(root, "*.app", SearchOption.AllDirectories).OrderBy(a => a, StringComparer.Ordinal))
        {
            string plist = Path.Combine(app, "Contents", "Info.plist");
            if (!File.Exists(plist) || bundles.Any(b => app.StartsWith(Path.Combine(root, b.Path) + "/", StringComparison.Ordinal))) continue;
            string? id = PlistValue(plist, "CFBundleIdentifier");
            if (id == null) continue;
            bundles.Add((Path.GetRelativePath(root, app), id, PlistValue(plist, "CFBundleShortVersionString") ?? "", PlistValue(plist, "CFBundleVersion") ?? ""));
        }
        var x = new StringBuilder();
        x.Append($"""
            <?xml version="1.0" encoding="utf-8"?>
            <pkg-info overwrite-permissions="true" relocatable="false" identifier="{X(identifier)}" postinstall-action="none" version="{X(version)}" format-version="2" install-location="/" auth="root">
                <payload numberOfFiles="{entries}" installKBytes="{installKBytes}"/>

            """);
        foreach (var b in bundles)
            x.Append($"    <bundle path=\"./{X(b.Path)}\" id=\"{X(b.Id)}\" CFBundleShortVersionString=\"{X(b.Short)}\" CFBundleVersion=\"{X(b.Version)}\"/>\n");
        foreach (string list in new[] { "bundle-version", "upgrade-bundle" })
            x.Append(Bundles(list, bundles.Select(b => b.Id)));
        x.Append("    <update-bundle/>\n    <atomic-update-bundle/>\n");
        x.Append(Bundles("strict-identifier", bundles.Select(b => b.Id)));
        x.Append("    <relocate/>\n    <scripts>\n");
        foreach (string script in new[] { "preinstall", "postinstall" })
            if (File.Exists(Path.Combine(scripts, script))) x.Append($"        <{script} file=\"./{script}\" timeout=\"600\"/>\n");
        x.Append("    </scripts>\n</pkg-info>\n");
        return x.ToString();

        static string Bundles(string name, IEnumerable<string> ids)
        {
            var list = ids.ToList();
            if (list.Count == 0) return $"    <{name}/>\n";
            return $"    <{name}>\n" + string.Concat(list.Select(id => $"        <bundle id=\"{X(id)}\"/>\n")) + $"    </{name}>\n";
        }
    }

    // a value of an Info.plist (XML or binary), as a string; null where it has none
    private static string? PlistValue(string plist, string key)
    {
        try
        {
            object? value = Apple.Plist.Read(File.ReadAllBytes(plist)) is Apple.PlistDict d ? d[key] : null;
            return value?.ToString() is { Length: > 0 } text ? text : null;
        }
        catch (Exception e) when (e is PackagerError or FormatException or System.Xml.XmlException) { return null; }
    }

    private static string X(string s) => SecurityElement.Escape(s)!;

    private static void Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new PackagerError($"{Path.GetFileName(tool)} failed ({p.ExitCode}): {output.Trim()}");
    }

    // ---- cpio (odc) -----------------------------------------------------------------------------------------

    /// <summary>The portable (odc, "070707") cpio format pkgbuild writes: octal fields, every entry root:wheel.</summary>
    internal sealed class CpioWriter
    {
        private readonly Stream _out;
        private int _index;

        public CpioWriter(Stream output) => _out = output;

        public void Add(string name, uint mode, long mtime, long size, Action<Stream>? data)
        {
            if (size > 077777777777L) throw new PackagerError($"{name}: a file of 8 GB or more cannot be in a macOS package");
            int i = ++_index;
            // dev and ino together tell the entries apart (ino has six octal digits); one link each, so none is
            // taken for another's hard link
            Header(i / 0777777, i % 0777777 + 1, mode, mtime <= 0 ? 0 : mtime, name, size);
            data?.Invoke(_out);
        }

        public void Finish()
        {
            Header(0, 0, 0, 0, "TRAILER!!!", 0);
            // to a whole block, as cpio pads its archives
            long length = _out.CanSeek ? _out.Position : -1;
            if (_out is ParallelGzipStream gzip) length = gzip.Length;
            if (length >= 0 && length % 512 != 0) _out.Write(new byte[512 - length % 512]);
        }

        private void Header(int dev, int ino, uint mode, long mtime, string name, long size)
        {
            byte[] n = Encoding.UTF8.GetBytes(name);
            string header = "070707" + O(dev, 6) + O(ino, 6) + O(mode, 6) + O(0, 6) + O(0, 6) + O(1, 6) + O(0, 6)
                + O(mtime, 11) + O(n.Length + 1, 6) + O(size, 11);
            _out.Write(Encoding.ASCII.GetBytes(header));
            _out.Write(n);
            _out.WriteByte(0);
        }

        private static string O(long value, int width) => Convert.ToString(value, 8).PadLeft(width, '0');
    }

    // ---- gzip, deflated on every core -----------------------------------------------------------------------

    /// <summary>
    /// One gzip stream (RFC 1952) whose deflate blocks are made in parallel: each 16 MB chunk deflated by a stream of
    /// its own and sync-flushed (so that it ends on a byte boundary, the next chunk's blocks following on), a last
    /// empty block ending the data, then the CRC-32 of it all and its length. Every gzip reader reads it as one.
    /// </summary>
    internal sealed class ParallelGzipStream : Stream
    {
        private readonly Stream _out;
        private readonly int _level;
        private readonly Queue<Task<(byte[] Data, int Length)>> _pending = new();
        private readonly System.IO.Hashing.Crc32 _crc32 = new();
        private readonly int _inFlight = Math.Max(2, Environment.ProcessorCount * 2);
        private byte[] _chunk = new byte[ChunkSize];
        private int _used;
        private long _length;
        private bool _finished;

        public ParallelGzipStream(Stream output, bool compress)
        {
            _out = output;
            _level = compress ? 6 : 0;   // gzip's default; 0: stored blocks
            // magic, deflate, no flags, no time, no extra flags, Unix
            _out.Write(new byte[] { 0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 3 });
        }

        public override long Length => _length;

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                int n = Math.Min(data.Length, ChunkSize - _used);
                data[..n].CopyTo(_chunk.AsSpan(_used));
                _crc32.Append(data[..n]);
                _used += n;
                _length += n;
                data = data[n..];
                if (_used == ChunkSize) Submit(last: false);
            }
        }

        public override void WriteByte(byte value) => Write(new[] { value });

        private void Submit(bool last)
        {
            byte[] chunk = _chunk;
            int size = _used, level = _level;
            _pending.Enqueue(Task.Run(() => Zlib.Deflate(chunk, size, level, last)));
            _chunk = new byte[ChunkSize];
            _used = 0;
            while (_pending.Count >= _inFlight) Drain();
        }

        private void Drain()
        {
            var (data, length) = _pending.Dequeue().GetAwaiter().GetResult();
            _out.Write(data, 0, length);
        }

        private void Finish()
        {
            if (_finished) return;
            _finished = true;
            Submit(last: true);   // what is left (perhaps nothing), and the final block
            while (_pending.Count > 0) Drain();
            Span<byte> trailer = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(trailer, _crc32.GetCurrentHashAsUInt32());
            BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], unchecked((uint)_length));
            _out.Write(trailer);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Finish();
                _out.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>Raw deflate by .NET's own (zlib-ng, in the runtime on every machine).</summary>
    internal static class Zlib
    {
        /// <summary>The chunk deflated as raw blocks, ended on a byte boundary (a sync flush) -- or, the last, with the
        /// final block.</summary>
        public static (byte[] Data, int Length) Deflate(byte[] chunk, int size, int level, bool last)
        {
            using var output = new MemoryStream(size / 2 + 1024);
            var deflate = new System.IO.Compression.DeflateStream(output,
                level == 0 ? System.IO.Compression.CompressionLevel.NoCompression : System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true);
            deflate.Write(chunk, 0, size);
            if (last)
            {
                deflate.Dispose();                                     // the final block
                return (output.GetBuffer(), (int)output.Length);
            }
            deflate.Flush();                                           // Z_SYNC_FLUSH: ended on a byte boundary
            int length = (int)output.Length;
            byte[] data = output.GetBuffer();
            deflate.Dispose();                                         // its final block written after length: not taken
            return (data, length);
        }
    }

    // ---- cksum ----------------------------------------------------------------------------------------------

    /// <summary>POSIX cksum: the CRC a Bom keeps of each file (CRC-32, MSB first, over the bytes and then the length).</summary>
    internal sealed class Cksum
    {
        private static readonly uint[] s_table = Table();
        private uint _crc;

        public void Add(ReadOnlySpan<byte> data)
        {
            uint crc = _crc;
            foreach (byte b in data) crc = (crc << 8) ^ s_table[(crc >> 24) ^ b];
            _crc = crc;
        }

        public uint Result(long length)
        {
            uint crc = _crc;
            for (ulong n = (ulong)length; n != 0; n >>= 8) crc = (crc << 8) ^ s_table[(crc >> 24) ^ (byte)n];
            return ~crc;
        }

        public static uint Of(ReadOnlySpan<byte> data)
        {
            var c = new Cksum();
            c.Add(data);
            return c.Result(data.Length);
        }

        private static uint[] Table()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i << 24;
                for (int k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
                t[i] = c;
            }
            return t;
        }
    }
}
