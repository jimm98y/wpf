// Named events and semaphores off Windows, where .NET throws PlatformNotSupportedException for them (only a named
// Mutex is supported there): the object a name stands for, shared by every process that creates or opens it, as
// Windows' kernel object is.
//
// Its state lives in the machine state (kernel-objects/<name hashed>): a small memory-mapped file every process maps,
// changed with interlocked operations -- an event's signal (and, for a manual-reset one, a generation each Set
// advances, so a waiter sees a Set even if a Reset follows at once), a semaphore's count and maximum -- and the
// processes holding it, for the object exists while a live process holds a handle to it, as on Windows: one whose
// holders have all gone is made anew by the next CreateEvent. A wait polls the state, backing off from a millisecond
// to ten.
//
// The handle the app gets is a real EventWaitHandle or Semaphore, unnamed underneath; .NET's create and open cores,
// Set and Reset, Release and the single-object wait core are hooked for the named ones. A WaitAny or WaitAll across
// a named handle and others waits on the local handle only.

using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;

namespace WpfWebGpu.Detours;

public static class NamedWaitHandles
{
    private static string s_folder = "";
    private static readonly List<IDisposable> s_hooks = new();
    private static readonly ConditionalWeakTable<WaitHandle, Shared> s_shared = new();

    // ---- the shared object ------------------------------------------------------------------------------------

    // layout: kind (1 event, 2 semaphore), manual reset, signal / count, maximum, generation, lock, then holders (pids)
    private const int Kind = 0, Manual = 4, Value = 8, Max = 12, Generation = 16, LockAt = 20, Holders = 32, HolderSlots = 120, Size = Holders + HolderSlots * 4;

    internal sealed unsafe class Shared : IDisposable
    {
        private readonly MemoryMappedFile _file;
        private readonly MemoryMappedViewAccessor _view;
        private readonly byte* _p;
        private int _disposed;

        private Shared(MemoryMappedFile file, MemoryMappedViewAccessor view)
        {
            _file = file;
            _view = view;
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _p = p + view.PointerOffset;
        }

        private ref int At(int offset) => ref *(int*)(_p + offset);

        internal static string PathOf(string name)
        {
            // Global\ and Local\ are Windows' namespaces; the machine state is one
            string bare = name.StartsWith(@"Global\", StringComparison.OrdinalIgnoreCase) || name.StartsWith(@"Local\", StringComparison.OrdinalIgnoreCase)
                ? name[(name.IndexOf('\\') + 1)..] : name;
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bare)), 0, 16).ToLowerInvariant();
            return Path.Combine(s_folder, hash + ".kobj");
        }

        private static Shared Map(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                if (stream.Length < Size) stream.SetLength(Size);
                if (!OperatingSystem.IsWindows())
                    try { File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("666", 8)); } catch (UnauthorizedAccessException) { }
                var file = MemoryMappedFile.CreateFromFile(stream, null, Size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
                return new Shared(file, file.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite));
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private void Lock()
        {
            int self = Environment.ProcessId;
            var spin = new SpinWait();
            long since = Environment.TickCount64;
            while (true)
            {
                int holder = Interlocked.CompareExchange(ref At(LockAt), self, 0);
                if (holder == 0) return;
                if (Environment.TickCount64 - since > 1000 && !Alive(holder)) Interlocked.CompareExchange(ref At(LockAt), 0, holder);
                spin.SpinOnce();
            }
        }

        private void Unlock() => Volatile.Write(ref At(LockAt), 0);

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int signal);

        private static bool Alive(int pid) => pid == Environment.ProcessId || kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() == 1;

        // whether a live process holds it; the dead are let go
        private bool Held()
        {
            bool any = false;
            for (int i = 0; i < HolderSlots; i++)
            {
                int pid = At(Holders + i * 4);
                if (pid == 0) continue;
                if (Alive(pid)) any = true;
                else At(Holders + i * 4) = 0;
            }
            return any;
        }

        private void Hold()
        {
            for (int i = 0; i < HolderSlots; i++)
                if (At(Holders + i * 4) == 0) { At(Holders + i * 4) = Environment.ProcessId; return; }
        }

        /// <summary>CreateEvent / CreateSemaphore: the object by that name, made where no live process holds one
        /// (<paramref name="createdNew"/>), else the existing one. Null where the name is another kind's.</summary>
        internal static Shared Create(string name, int kind, bool manual, int value, int max, out bool createdNew)
        {
            Shared s = Map(PathOf(name));
            s.Lock();
            try
            {
                createdNew = !s.Held();
                if (createdNew)
                {
                    s.At(Kind) = kind;
                    s.At(Manual) = manual ? 1 : 0;
                    s.At(Value) = value;
                    s.At(Max) = max;
                    s.At(Generation) = 0;
                }
                else if (s.At(Kind) != kind)
                {
                    s.Unlock();
                    s.Dispose();
                    return null;
                }
                s.Hold();
            }
            finally { if (s._disposed == 0) s.Unlock(); }
            return s;
        }

        /// <summary>OpenEvent / OpenSemaphore: the object by that name where a live process holds it.</summary>
        internal static Shared Open(string name, int kind, out bool wrongKind)
        {
            wrongKind = false;
            string path = PathOf(name);
            if (!File.Exists(path)) return null;
            Shared s = Map(path);
            s.Lock();
            bool ok = s.Held() && s.At(Kind) == kind;
            wrongKind = s.Held() && s.At(Kind) != kind;
            if (ok) s.Hold();
            s.Unlock();
            if (ok) return s;
            s.Dispose();
            return null;
        }

        internal bool IsManual => At(Manual) != 0;

        internal void Set()
        {
            if (IsManual) Interlocked.Increment(ref At(Generation));
            Volatile.Write(ref At(Value), 1);
        }

        internal void Reset() => Volatile.Write(ref At(Value), 0);

        /// <summary>ReleaseSemaphore: the count before, or -1 where it would pass the maximum.</summary>
        internal int Release(int count)
        {
            while (true)
            {
                int before = Volatile.Read(ref At(Value));
                if ((long)before + count > At(Max)) return -1;
                if (Interlocked.CompareExchange(ref At(Value), before + count, before) == before) return before;
            }
        }

        // one attempt: an event signalled (an auto-reset one taken), a manual one's Set seen, a semaphore's count taken
        private bool TryTake(int generationAtStart)
        {
            if (At(Kind) == 2)
            {
                while (true)
                {
                    int n = Volatile.Read(ref At(Value));
                    if (n <= 0) return false;
                    if (Interlocked.CompareExchange(ref At(Value), n - 1, n) == n) return true;
                }
            }
            if (IsManual) return Volatile.Read(ref At(Value)) != 0 || Volatile.Read(ref At(Generation)) != generationAtStart;
            return Interlocked.CompareExchange(ref At(Value), 0, 1) == 1;
        }

        internal bool Wait(int timeoutMs)
        {
            int generation = Volatile.Read(ref At(Generation));
            long deadline = timeoutMs < 0 ? long.MaxValue : Environment.TickCount64 + timeoutMs;
            int sleep = 1;
            while (true)
            {
                if (TryTake(generation)) return true;
                long now = Environment.TickCount64;
                if (now >= deadline) return false;
                Thread.Sleep((int)Math.Min(sleep, deadline - now));
                sleep = Math.Min(sleep * 2, 10);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                Lock();
                for (int i = 0; i < HolderSlots; i++)
                    if (At(Holders + i * 4) == Environment.ProcessId) { At(Holders + i * 4) = 0; break; }
                Unlock();
            }
            catch (ObjectDisposedException) { }
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _file.Dispose();
        }
    }

    // ---- what the hooks call ------------------------------------------------------------------------------------

    /// <summary>EventWaitHandle.CreateEventCore(initialState, mode, name, options, out createdNew): true when made here.</summary>
    public static bool CreateEvent(EventWaitHandle self, bool initialState, EventResetMode mode, string? name, ref bool createdNew)
    {
        if (string.IsNullOrEmpty(name)) return false;
        LocalCore(self, "CreateEventCore", initialState, mode);
        Shared s = Shared.Create(name, 1, mode == EventResetMode.ManualReset, initialState ? 1 : 0, 1, out createdNew)
            ?? throw new WaitHandleCannotBeOpenedException($"A WaitHandle with system-wide name '{name}' cannot be created. A WaitHandle of a different type might have the same name.");
        s_shared.AddOrUpdate(self, s);
        return true;
    }

    /// <summary>Semaphore.CreateSemaphoreCore(initialCount, maximumCount, name, options, out createdNew).</summary>
    public static bool CreateSemaphore(Semaphore self, int initialCount, int maximumCount, string? name, ref bool createdNew)
    {
        if (string.IsNullOrEmpty(name)) return false;
        LocalCore(self, "CreateSemaphoreCore", initialCount, maximumCount);
        Shared s = Shared.Create(name, 2, false, initialCount, maximumCount, out createdNew)
            ?? throw new WaitHandleCannotBeOpenedException($"A WaitHandle with system-wide name '{name}' cannot be created. A WaitHandle of a different type might have the same name.");
        s_shared.AddOrUpdate(self, s);
        return true;
    }

    // the unnamed core, for the handle underneath
    private static void LocalCore(object self, string core, object a, object b) =>
        self.GetType().GetMethod(core, BindingFlags.NonPublic | BindingFlags.Instance, new[] { a.GetType(), b.GetType() })!.Invoke(self, new[] { a, b });

    // OpenExistingResult: Success 0, NameNotFound 1, PathNotFound 2, NameInvalid 3; -1 for .NET's own answer
    internal static int OpenEvent(string? name, ref EventWaitHandle? result)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        Shared? s = Shared.Open(name, 1, out bool wrongKind);
        if (s == null) return wrongKind ? 3 : 1;
        var handle = new EventWaitHandle(false, s.IsManual ? EventResetMode.ManualReset : EventResetMode.AutoReset);
        s_shared.AddOrUpdate(handle, s);
        result = handle;
        return 0;
    }

    internal static int OpenSemaphore(string? name, ref Semaphore? result)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        Shared? s = Shared.Open(name, 2, out bool wrongKind);
        if (s == null) return wrongKind ? 3 : 1;
        var handle = new Semaphore(0, int.MaxValue);
        s_shared.AddOrUpdate(handle, s);
        result = handle;
        return 0;
    }

    private delegate int Opener<T>(string? name, ref T? result) where T : WaitHandle;
    public delegate bool TryOpenOrig<T>(string name, out T? result) where T : WaitHandle;
    public delegate bool TryOpen<T>(TryOpenOrig<T> orig, string name, out T? result) where T : WaitHandle;

    private static T OpenOrThrow<T>(string name, Opener<T> open, Func<string, T> orig) where T : WaitHandle
    {
        if (string.IsNullOrEmpty(name)) return orig(name);
        T? result = null;
        return open(name, ref result) switch
        {
            0 => result!,
            3 => throw new WaitHandleCannotBeOpenedException($"A WaitHandle with system-wide name '{name}' cannot be created. A WaitHandle of a different type might have the same name."),
            _ => throw new WaitHandleCannotBeOpenedException($"No handle of the given name exists."),
        };
    }

    private static bool TryOpenWith<T>(string name, out T? result, Opener<T> open, TryOpenOrig<T> orig) where T : WaitHandle
    {
        result = null;
        if (string.IsNullOrEmpty(name)) return orig(name, out result);
        return open(name, ref result) == 0;
    }

    /// <summary>WaitHandle.WaitOneNoCheck: -1 for .NET's own wait, else 1 signalled, 0 timed out.</summary>
    public static int Wait(WaitHandle self, int timeoutMs) =>
        s_shared.TryGetValue(self, out Shared? s) ? (s.Wait(timeoutMs) ? 1 : 0) : -1;

    internal static Shared? Of(WaitHandle h) => s_shared.TryGetValue(h, out Shared? s) ? s : null;

    // ---- installing -------------------------------------------------------------------------------------------

    internal static void Install(string stateDirectory)
    {
        s_folder = Path.Combine(stateDirectory, "kernel-objects");
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        // the named create cores: (initial, mode, name, options, out createdNew) and (initial, max, name, options, out createdNew)
        MethodInfo eventCore = typeof(EventWaitHandle).GetMethods(Any).Single(m => m.Name == "CreateEventCore" && m.GetParameters().Length == 5);
        HookPrefixVoid(eventCore, typeof(NamedWaitHandles).GetMethod(nameof(CreateEvent))!, 0, 1, 2, 3, 5);
        MethodInfo semaphoreCore = typeof(Semaphore).GetMethods(Any).Single(m => m.Name == "CreateSemaphoreCore" && m.GetParameters().Length == 5);
        HookPrefixVoid(semaphoreCore, typeof(NamedWaitHandles).GetMethod(nameof(CreateSemaphore))!, 0, 1, 2, 3, 5);

        // OpenExisting and TryOpenExisting, as Windows answers them: the object, or none by that name
        s_hooks.Add(new Hook(typeof(EventWaitHandle).GetMethod("OpenExisting", new[] { typeof(string) })!,
            (Func<string, EventWaitHandle> orig, string name) => OpenOrThrow(name, OpenEvent, orig)));
        s_hooks.Add(new Hook(typeof(EventWaitHandle).GetMethod("TryOpenExisting", new[] { typeof(string), typeof(EventWaitHandle).MakeByRefType() })!,
            new TryOpen<EventWaitHandle>((TryOpenOrig<EventWaitHandle> orig, string name, out EventWaitHandle? result) => TryOpenWith(name, out result, OpenEvent, orig))));
        s_hooks.Add(new Hook(typeof(Semaphore).GetMethod("OpenExisting", new[] { typeof(string) })!,
            (Func<string, Semaphore> orig, string name) => OpenOrThrow(name, OpenSemaphore, orig)));
        s_hooks.Add(new Hook(typeof(Semaphore).GetMethod("TryOpenExisting", new[] { typeof(string), typeof(Semaphore).MakeByRefType() })!,
            new TryOpen<Semaphore>((TryOpenOrig<Semaphore> orig, string name, out Semaphore? result) => TryOpenWith(name, out result, OpenSemaphore, orig))));

        // the single wait: WaitOneNoCheck(timeout, ...) -> bool
        HookPrefixResult(typeof(WaitHandle).GetMethods(Any).Single(m => m.Name == "WaitOneNoCheck"), typeof(NamedWaitHandles).GetMethod(nameof(Wait))!, 0, 1);

        s_hooks.Add(new Hook(typeof(EventWaitHandle).GetMethod("Set", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!,
            (Func<EventWaitHandle, bool> orig, EventWaitHandle self) =>
            {
                if (Of(self) is { } s) { s.Set(); return true; }
                return orig(self);
            }));
        s_hooks.Add(new Hook(typeof(EventWaitHandle).GetMethod("Reset", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!,
            (Func<EventWaitHandle, bool> orig, EventWaitHandle self) =>
            {
                if (Of(self) is { } s) { s.Reset(); return true; }
                return orig(self);
            }));
        s_hooks.Add(new Hook(typeof(Semaphore).GetMethod("ReleaseCore", BindingFlags.NonPublic | BindingFlags.Instance)!,
            (Func<Semaphore, int, int> orig, Semaphore self, int count) =>
            {
                if (Of(self) is not { } s) return orig(self, count);
                int before = s.Release(count);
                if (before < 0) throw new SemaphoreFullException();
                return before;
            }));
        // a handle closed: the object let go of, so it goes once no process holds it
        s_hooks.Add(new Hook(typeof(WaitHandle).GetMethod("Dispose", BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(bool) })!,
            (Action<WaitHandle, bool> orig, WaitHandle self, bool disposing) =>
            {
                if (Of(self) is { } s) { s_shared.Remove(self); s.Dispose(); }
                orig(self, disposing);
            }));
    }

    // at entry: call <hook>(args...); a true answer returns at once (a void method)
    private static void HookPrefixVoid(MethodBase target, MethodInfo hook, params int[] args)
    {
        s_hooks.Add(new ILHook(target, il =>
        {
            var c = new ILCursor(il);
            foreach (int a in args) c.Emit(OpCodes.Ldarg, a);
            c.Emit(OpCodes.Call, hook);
            ILLabel go = c.DefineLabel();
            c.Emit(OpCodes.Brfalse, go);
            c.Emit(OpCodes.Ret);
            c.MarkLabel(go);
        }));
    }

    // at entry: call <hook>(args...); an answer other than -1 is the method's result
    private static void HookPrefixResult(MethodBase target, MethodInfo hook, params int[] args)
    {
        s_hooks.Add(new ILHook(target, il =>
        {
            var c = new ILCursor(il);
            foreach (int a in args) c.Emit(OpCodes.Ldarg, a);
            c.Emit(OpCodes.Call, hook);
            c.Emit(OpCodes.Dup);
            c.Emit(OpCodes.Ldc_I4_M1);
            ILLabel go = c.DefineLabel();
            c.Emit(OpCodes.Beq, go);
            c.Emit(OpCodes.Ret);
            c.MarkLabel(go);
            c.Emit(OpCodes.Pop);
        }));
    }
}
