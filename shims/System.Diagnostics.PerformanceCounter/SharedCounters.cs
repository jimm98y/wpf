// The categories an app creates, machine-wide, as Windows registers them: created by one process (a server), written
// by it and read by any other (a monitoring tool), until deleted. Each category is a definition (<name>.json: its help,
// type and counters) and its values (<name>.counters), memory-mapped by every process that uses it: a table of instances,
// each its name, its lifetime and owner, and a 64-bit value per counter, changed with interlocked operations as Windows'
// shared counter memory is. An instance whose lifetime is the process's goes when that process does.
//
// The folder is the machine's application data (CommonApplicationData\WpfWebGpu\PerformanceCounters: ProgramData on
// Windows, the machine state elsewhere), open to every account as Windows' counters are; where it cannot be written,
// categories stay the creating process's own.

using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace System.Diagnostics
{
    internal static class SharedCategories
    {
        private static string s_folder;
        private static bool s_resolved;

        /// <summary>The machine's folder of categories; null where there is none to write.</summary>
        internal static string Folder
        {
            get
            {
                if (s_resolved) return s_folder;
                s_resolved = true;
                try
                {
                    string root = Environment.GetEnvironmentVariable("WPFWEBGPU_PERFCOUNTERS_DIR") is { Length: > 0 } explicitDir ? explicitDir
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.Create), "WpfWebGpu", "PerformanceCounters");
                    Directory.CreateDirectory(root);
                    if (!OperatingSystem.IsWindows())
                        try { File.SetUnixFileMode(root, (UnixFileMode)Convert.ToInt32("1777", 8)); } catch (UnauthorizedAccessException) { }
                    string probe = Path.Combine(root, ".probe-" + Environment.ProcessId);
                    File.WriteAllText(probe, "");
                    File.Delete(probe);
                    s_folder = root;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException) { s_folder = null; }
                return s_folder;
            }
        }

        // a file's name for a category: case-insensitive, as category names are
        private static string FileOf(string name) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant())), 0, 12).ToLowerInvariant();

        private sealed class Definition
        {
            public string Name { get; set; }
            public string Help { get; set; }
            public int Type { get; set; }
            public List<CounterDefinition> Counters { get; set; } = new();
        }

        private sealed class CounterDefinition
        {
            public string Name { get; set; }
            public string Help { get; set; }
            public int Type { get; set; }
        }

        /// <summary>Registers <paramref name="category"/> for every process; false where there is nowhere to.</summary>
        internal static bool Register(CustomCategory category)
        {
            if (Folder is not { } folder) return false;
            string path = Path.Combine(folder, FileOf(category.Name) + ".json");
            var definition = new Definition
            {
                Name = category.Name, Help = category.Help, Type = (int)category.Type,
                Counters = category.Definitions.Select(d => new CounterDefinition { Name = d.Name, Help = d.Help, Type = (int)d.Type }).ToList(),
            };
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    JsonSerializer.Serialize(stream, definition);
                Open(path);
            }
            catch (IOException) when (File.Exists(path))
            {
                throw new InvalidOperationException($"Cannot create Performance Category '{category.Name}' because it already exists.");
            }
            category.Shared = SharedValues.Open(Path.ChangeExtension(path, ".counters"), category.Definitions.Count, create: true);
            return true;
        }

        private static void Open(string path)
        {
            if (!OperatingSystem.IsWindows())
                try { File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("666", 8)); } catch (UnauthorizedAccessException) { }
        }

        /// <summary>A category another process (or this one) registered, by its name; null where there is none.</summary>
        internal static CustomCategory Load(string name) => Folder is { } folder ? LoadFile(Path.Combine(folder, FileOf(name) + ".json")) : null;

        /// <summary>Every registered category.</summary>
        internal static IEnumerable<CustomCategory> All()
        {
            if (Folder is not { } folder) yield break;
            string[] files;
            try { files = Directory.GetFiles(folder, "*.json"); }
            catch (IOException) { yield break; }
            foreach (string f in files)
                if (LoadFile(f) is { } c) yield return c;
        }

        private static CustomCategory LoadFile(string path)
        {
            Definition d;
            try
            {
                if (!File.Exists(path)) return null;
                d = JsonSerializer.Deserialize<Definition>(File.ReadAllText(path));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
            if (d?.Name == null || d.Counters.Count == 0) return null;
            var category = new CustomCategory { Name = d.Name, Help = d.Help ?? "Help not available.", Type = (PerformanceCounterCategoryType)d.Type };
            foreach (var c in d.Counters) category.Definitions.Add((c.Name, c.Help ?? "Help not available.", (PerformanceCounterType)c.Type));
            try { category.Shared = SharedValues.Open(Path.ChangeExtension(path, ".counters"), category.Definitions.Count, create: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
            return category;
        }

        /// <summary>Unregisters a category: gone for every process.</summary>
        internal static void Delete(CustomCategory category)
        {
            category.Shared?.Dispose();
            category.Shared = null;
            if (Folder is not { } folder) return;
            string path = Path.Combine(folder, FileOf(category.Name) + ".json");
            try { File.Delete(path); File.Delete(Path.ChangeExtension(path, ".counters")); }
            catch (IOException) { }
        }
    }

    /// <summary>A category's values, shared by every process that maps them.</summary>
    internal sealed unsafe class SharedValues : IDisposable
    {
        private const int Magic = 0x43465057;            // "WPFC"
        private const int HeaderSize = 64, MaxInstances = 1024, NameChars = 128;
        // an instance: in use (int), owner process (int), lifetime (int), name length (int), name (UTF-16), values (long each)
        private const int EntryHead = 16;

        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _view;
        private readonly byte* _base;
        private readonly int _counters, _entrySize;

        private SharedValues(MemoryMappedFile file, MemoryMappedViewAccessor view, int counters)
        {
            _file = file;
            _view = view;
            _counters = counters;
            _entrySize = EntryHead + NameChars * 2 + counters * 8;
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _base = p + _view.PointerOffset;
            if (*(int*)_base == 0)
            {
                // the first to map it lays it out (a racing second writes the same)
                *(int*)(_base + 4) = 1;
                *(int*)(_base + 8) = counters;
                *(int*)(_base + 12) = MaxInstances;
                Interlocked.CompareExchange(ref *(int*)_base, Magic, 0);
            }
        }

        internal static SharedValues Open(string path, int counters, bool create)
        {
            long size = HeaderSize + (long)MaxInstances * (EntryHead + NameChars * 2 + counters * 8);
            var stream = new FileStream(path, create ? FileMode.OpenOrCreate : FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                if (stream.Length < size) stream.SetLength(size);
                if (!OperatingSystem.IsWindows())
                    try { File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("666", 8)); } catch (UnauthorizedAccessException) { }
                var file = MemoryMappedFile.CreateFromFile(stream, null, size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
                return new SharedValues(file, file.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite), counters);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private byte* Entry(int i) => _base + HeaderSize + (long)i * _entrySize;
        private long* Value(int entry, int counter) => (long*)(Entry(entry) + EntryHead + NameChars * 2 + counter * 8);

        // the table's lock, held by a process: one that has gone is taken from
        private void Lock()
        {
            int self = Environment.ProcessId;
            int* lockField = (int*)(_base + 16);
            var spin = new SpinWait();
            long since = Environment.TickCount64;
            while (true)
            {
                int holder = Interlocked.CompareExchange(ref *lockField, self, 0);
                if (holder == 0) return;
                if (Environment.TickCount64 - since > 1000 && !Alive(holder)) Interlocked.CompareExchange(ref *lockField, 0, holder);
                spin.SpinOnce();
            }
        }

        private void Unlock() => Interlocked.Exchange(ref *(int*)(_base + 16), 0);

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int signal);

        private static bool Alive(int pid)
        {
            if (pid == Environment.ProcessId) return true;
            if (!OperatingSystem.IsWindows()) return kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() == 1;    // EPERM: alive, another's
            try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
            catch (ArgumentException) { return false; }
        }

        // an instance in use: one whose lifetime is its process's is not, once that process has gone
        private bool InUse(int i)
        {
            byte* e = Entry(i);
            if (Volatile.Read(ref *(int*)e) == 0) return false;
            return *(int*)(e + 8) != (int)PerformanceCounterInstanceLifetime.Process || Alive(*(int*)(e + 4));
        }

        private string NameOf(int i)
        {
            byte* e = Entry(i);
            int length = Math.Clamp(*(int*)(e + 12), 0, NameChars);
            return new string((char*)(e + EntryHead), 0, length);
        }

        private int Find(string instance)
        {
            for (int i = 0; i < MaxInstances; i++)
                if (InUse(i) && string.Equals(NameOf(i), instance, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private int FindOrCreate(string instance, PerformanceCounterInstanceLifetime lifetime)
        {
            int found = Find(instance);
            if (found >= 0) return found;
            Lock();
            try
            {
                found = Find(instance);
                if (found >= 0) return found;
                for (int i = 0; i < MaxInstances; i++)
                {
                    if (InUse(i)) continue;
                    byte* e = Entry(i);
                    new Span<byte>(e, _entrySize).Clear();
                    *(int*)(e + 4) = Environment.ProcessId;
                    *(int*)(e + 8) = (int)lifetime;
                    int length = Math.Min(instance.Length, NameChars);
                    *(int*)(e + 12) = length;
                    instance.AsSpan(0, length).CopyTo(new Span<char>(e + EntryHead, NameChars));
                    Volatile.Write(ref *(int*)e, 1);
                    return i;
                }
                throw new InvalidOperationException("Custom counters file view is out of memory.");
            }
            finally { Unlock(); }
        }

        public long Get(int counter, string instance)
        {
            int i = Find(instance ?? "");
            return i < 0 ? 0 : Volatile.Read(ref *Value(i, counter));
        }

        public long Update(int counter, string instance, Func<long, long> change, PerformanceCounterInstanceLifetime lifetime)
        {
            int i = FindOrCreate(instance ?? "", lifetime);
            ref long slot = ref *Value(i, counter);
            while (true)
            {
                long old = Volatile.Read(ref slot);
                long next = change(old);
                if (Interlocked.CompareExchange(ref slot, next, old) == old) return next;
            }
        }

        public void RemoveInstance(string instance)
        {
            Lock();
            try
            {
                int i = Find(instance ?? "");
                if (i >= 0) Volatile.Write(ref *(int*)Entry(i), 0);
            }
            finally { Unlock(); }
        }

        public string[] InstanceNames()
        {
            var names = new List<string>();
            for (int i = 0; i < MaxInstances; i++)
                if (InUse(i)) names.Add(NameOf(i));
            return names.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public void Dispose()
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _file.Dispose();
        }
    }
}
