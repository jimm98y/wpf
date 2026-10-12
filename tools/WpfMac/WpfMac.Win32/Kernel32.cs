// kernel32: processes, memory, modules, time, synchronisation, the system's description of itself.
//
// Where .NET or macOS has the real answer it is returned (process and thread ids, memory, the time
// zone, environment variables); where a Windows concept has no counterpart the answer is the one a
// Windows that lacks the feature gives (no console, no package identity, no DLL other than the ones
// libwin32shim stands in for).

using System.Collections.Concurrent;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace WpfMac.Win32;

public static unsafe partial class Exports
{
    // ---- the handle table shared by events, mutexes and processes ------------------------------

    private static readonly ConcurrentDictionary<nint, object> s_handles = new();
    private static long s_nextHandle = 0x4000;

    internal static nint NewHandle(object o)
    {
        nint h = (nint)Interlocked.Add(ref s_nextHandle, 4);
        s_handles[h] = o;
        return h;
    }

    internal static object? Handle(nint h) => s_handles.TryGetValue(h, out var o) ? o : null;

    // A module "handle" for the app's own executable (GetModuleHandle(NULL)), which is not a real image.
    internal static readonly nint ExeModule = (nint)0x140000000;

    private static void RegisterKernel32()
    {
        R((delegate* unmanaged<uint>)&GetCurrentThreadId, "GetCurrentThreadId");
        R((delegate* unmanaged<uint>)&GetCurrentProcessId, "GetCurrentProcessId");
        R((delegate* unmanaged<nint>)&GetCurrentProcess, "GetCurrentProcess");
        R((delegate* unmanaged<nint>)&GetCurrentThread, "GetCurrentThread");
        R((delegate* unmanaged<uint>)&GetLastError, "GetLastError");
        R((delegate* unmanaged<uint, void>)&SetLastErrorExport, "SetLastError");
        R((delegate* unmanaged<nint, int>)&CloseHandle, "CloseHandle");

        R((delegate* unmanaged<uint, nuint, void*>)&LocalAlloc, "LocalAlloc", "GlobalAlloc");
        R((delegate* unmanaged<void*, void*>)&LocalFree, "LocalFree", "GlobalFree");
        R((delegate* unmanaged<void*, void*>)&LocalLock, "LocalLock", "GlobalLock");
        R((delegate* unmanaged<void*, int>)&LocalUnlock, "LocalUnlock", "GlobalUnlock");
        R((delegate* unmanaged<void*, nuint>)&LocalSize, "LocalSize", "GlobalSize");
        R((delegate* unmanaged<void*, nuint, uint, uint, void*>)&VirtualAlloc, "VirtualAlloc");
        R((delegate* unmanaged<void*, nuint, uint, int>)&VirtualFree, "VirtualFree");
        R((delegate* unmanaged<void*, void*, nuint, void>)&RtlMoveMemory, "RtlMoveMemory", "RtlCopyMemory", "CopyMemory", "MoveMemory");
        R((delegate* unmanaged<void*, nuint, void>)&RtlZeroMemory, "RtlZeroMemory", "ZeroMemory", "RtlSecureZeroMemory");
        R((delegate* unmanaged<void*, nuint, byte, void>)&RtlFillMemory, "RtlFillMemory", "FillMemory");

        R((delegate* unmanaged<long*, int>)&QueryPerformanceCounter, "QueryPerformanceCounter");
        R((delegate* unmanaged<long*, int>)&QueryPerformanceFrequency, "QueryPerformanceFrequency");
        R((delegate* unmanaged<uint>)&GetTickCount, "GetTickCount");
        R((delegate* unmanaged<ulong>)&GetTickCount64, "GetTickCount64");
        R((delegate* unmanaged<uint, void>)&Sleep, "Sleep");
        R((delegate* unmanaged<uint, int, uint>)&SleepEx, "SleepEx");
        R((delegate* unmanaged<long*, void>)&GetSystemTimeAsFileTime, "GetSystemTimeAsFileTime", "GetSystemTimePreciseAsFileTime");
        R((delegate* unmanaged<ushort*, void>)&GetSystemTime, "GetSystemTime");
        R((delegate* unmanaged<ushort*, void>)&GetLocalTime, "GetLocalTime");
        R((delegate* unmanaged<byte*, uint>)&GetTimeZoneInformation, "GetTimeZoneInformation");
        R((delegate* unmanaged<byte*, uint>)&GetDynamicTimeZoneInformation, "GetDynamicTimeZoneInformation");

        R((delegate* unmanaged<char*, nint>)&GetModuleHandleW, "GetModuleHandleW");
        R((delegate* unmanaged<byte*, nint>)&GetModuleHandleBare, "GetModuleHandle", "GetModuleHandleA");
        R((delegate* unmanaged<char*, nint>)&LoadLibraryW, "LoadLibraryW");
        R((delegate* unmanaged<byte*, nint>)&LoadLibraryBare, "LoadLibrary", "LoadLibraryA");
        R((delegate* unmanaged<char*, nint, uint, nint>)&LoadLibraryExW, "LoadLibraryExW");
        R((delegate* unmanaged<byte*, nint, uint, nint>)&LoadLibraryExBare, "LoadLibraryEx", "LoadLibraryExA");
        R((delegate* unmanaged<nint, int>)&FreeLibrary, "FreeLibrary");
        R((delegate* unmanaged<nint, byte*, nint>)&GetProcAddress, "GetProcAddress");
        R((delegate* unmanaged<nint, char*, uint, uint>)&GetModuleFileNameW, "GetModuleFileNameW");
        R((delegate* unmanaged<nint, byte*, uint, uint>)&GetModuleFileNameBare, "GetModuleFileName", "GetModuleFileNameA");
        R((delegate* unmanaged<void*, int>)&ReturnTrue1, "SetDllDirectoryW", "SetDllDirectory", "SetDllDirectoryA", "SetDefaultDllDirectories", "RemoveDllDirectory",
            "SetProcessMitigationPolicy");
        R((delegate* unmanaged<void*, nint>)&AddDllDirectory, "AddDllDirectory");
        R((delegate* unmanaged<uint, uint>)&SetErrorMode, "SetErrorMode");

        R((delegate* unmanaged<nint, int*, int>)&IsWow64Process, "IsWow64Process");
        R((delegate* unmanaged<nint, ushort*, ushort*, int>)&IsWow64Process2, "IsWow64Process2");
        R((delegate* unmanaged<byte*, void>)&GetSystemInfo, "GetSystemInfo", "GetNativeSystemInfo");
        R((delegate* unmanaged<uint*, int>)&GetVersionEx, "GetVersionExW", "GetVersionEx", "GetVersionExA");
        R((delegate* unmanaged<uint*, int>)&RtlGetVersion, "RtlGetVersion");
        R((delegate* unmanaged<uint>)&GetVersion, "GetVersion");
        R((delegate* unmanaged<uint, uint, uint, uint, uint*, int>)&GetProductInfo, "GetProductInfo");
        R((delegate* unmanaged<byte*, int>)&GlobalMemoryStatusEx, "GlobalMemoryStatusEx");
        R((delegate* unmanaged<uint, uint>)&SetThreadExecutionState, "SetThreadExecutionState");
        R((delegate* unmanaged<nint, nuint, nuint, int>)&SetProcessWorkingSetSize, "SetProcessWorkingSetSize");
        R((delegate* unmanaged<void*, void*>)&IdentityPointer, "EncodePointer", "DecodePointer", "EncodeSystemPointer", "DecodeSystemPointer");

        R((delegate* unmanaged<void*, void>)&InitializeCriticalSection, "InitializeCriticalSection");
        R((delegate* unmanaged<void*, uint, uint, int>)&InitializeCriticalSectionEx, "InitializeCriticalSectionEx");
        R((delegate* unmanaged<void*, uint, int>)&InitializeCriticalSectionAndSpinCount, "InitializeCriticalSectionAndSpinCount");
        R((delegate* unmanaged<void*, void>)&EnterCriticalSection, "EnterCriticalSection");
        R((delegate* unmanaged<void*, int>)&TryEnterCriticalSection, "TryEnterCriticalSection");
        R((delegate* unmanaged<void*, void>)&LeaveCriticalSection, "LeaveCriticalSection");
        R((delegate* unmanaged<void*, void>)&DeleteCriticalSection, "DeleteCriticalSection");

        R((delegate* unmanaged<void*, int, int, char*, nint>)&CreateEventW, "CreateEventW");
        R((delegate* unmanaged<void*, int, int, byte*, nint>)&CreateEventBare, "CreateEvent", "CreateEventA");
        R((delegate* unmanaged<nint, int>)&SetEvent, "SetEvent");
        R((delegate* unmanaged<nint, int>)&ResetEvent, "ResetEvent");
        R((delegate* unmanaged<void*, int, char*, nint>)&CreateMutexW, "CreateMutexW");
        R((delegate* unmanaged<void*, int, byte*, nint>)&CreateMutexBare, "CreateMutex", "CreateMutexA");
        R((delegate* unmanaged<nint, int>)&ReleaseMutex, "ReleaseMutex");
        R((delegate* unmanaged<nint, uint, uint>)&WaitForSingleObject, "WaitForSingleObject");
        R((delegate* unmanaged<uint, nint*, int, uint, uint>)&WaitForMultipleObjects, "WaitForMultipleObjects");
        R((delegate* unmanaged<uint, int, uint, nint>)&OpenProcess, "OpenProcess");

        R((delegate* unmanaged<uint, void*, uint, uint, char*, uint, void*, uint>)&FormatMessageW, "FormatMessageW");
        R((delegate* unmanaged<uint, void*, uint, uint, byte*, uint, void*, uint>)&FormatMessageBare, "FormatMessage", "FormatMessageA");
        R((delegate* unmanaged<char*, int>)&lstrlenW, "lstrlenW");
        R((delegate* unmanaged<int, int, int, int>)&MulDiv, "MulDiv");
        R((delegate* unmanaged<uint>)&GetUserDefaultLCID, "GetUserDefaultLCID", "GetSystemDefaultLCID", "GetThreadLocale");
        R((delegate* unmanaged<ushort>)&GetUserDefaultUILanguage, "GetUserDefaultUILanguage", "GetSystemDefaultUILanguage", "GetUserDefaultLangID", "GetSystemDefaultLangID");
        R((delegate* unmanaged<uint>)&GetACP, "GetACP");
        R((delegate* unmanaged<uint>)&GetOEMCP, "GetOEMCP");
        R((delegate* unmanaged<char*, uint*, int>)&GetComputerNameW, "GetComputerNameW", "GetComputerName");
        R((delegate* unmanaged<char*, char*, uint, uint>)&ExpandEnvironmentStringsW, "ExpandEnvironmentStringsW", "ExpandEnvironmentStrings");
        R((delegate* unmanaged<char*, char*, uint, uint>)&GetEnvironmentVariableW, "GetEnvironmentVariableW", "GetEnvironmentVariable");
        R((delegate* unmanaged<char*, char*, int>)&SetEnvironmentVariableW, "SetEnvironmentVariableW", "SetEnvironmentVariable");
        R((delegate* unmanaged<uint, char*, uint>)&GetTempPathW, "GetTempPathW", "GetTempPath", "GetTempPath2W");
        R((delegate* unmanaged<char*, uint, uint>)&GetWindowsDirectoryW, "GetWindowsDirectoryW", "GetWindowsDirectory", "GetSystemWindowsDirectoryW");
        R((delegate* unmanaged<char*, uint, uint>)&GetSystemDirectoryW, "GetSystemDirectoryW", "GetSystemDirectory");
        R((delegate* unmanaged<char*, char*, uint, uint>)&GetShortPathNameW, "GetShortPathNameW", "GetShortPathName", "GetLongPathNameW", "GetLongPathName");
        R((delegate* unmanaged<char*, void>)&OutputDebugStringW, "OutputDebugStringW", "OutputDebugString");

        R((delegate* unmanaged<uint, int>)&ReturnFalseU, "AttachConsole", "FreeConsole");
        R((delegate* unmanaged<int>)&ReturnFalse0, "AllocConsole");
        R((delegate* unmanaged<nint>)&ReturnNull0, "GetConsoleWindow");
        R((delegate* unmanaged<uint*, char*, int>)&GetCurrentPackageFullName, "GetCurrentPackageFullName", "GetCurrentPackageId", "GetCurrentPackageFamilyName");
        R((delegate* unmanaged<void*, void*, uint, uint, int>)&RegisterApplicationRecoveryCallback, "RegisterApplicationRecoveryCallback");
        R((delegate* unmanaged<char*, uint, int>)&RegisterApplicationRestart, "RegisterApplicationRestart");
        R((delegate* unmanaged<int>)&ReturnSOk0, "UnregisterApplicationRecoveryCallback", "UnregisterApplicationRestart");
        R((delegate* unmanaged<int*, int>)&ApplicationRecoveryInProgress, "ApplicationRecoveryInProgress");
        R((delegate* unmanaged<int, void>)&ApplicationRecoveryFinished, "ApplicationRecoveryFinished");
        R((delegate* unmanaged<void*, nint>)&CreateActCtx, "CreateActCtxW", "CreateActCtx", "CreateActCtxA");
        R((delegate* unmanaged<nint, nuint*, int>)&ActivateActCtx, "ActivateActCtx");
        R((delegate* unmanaged<uint, nuint, int>)&DeactivateActCtx, "DeactivateActCtx");
        R((delegate* unmanaged<nint, void>)&ReleaseActCtx, "ReleaseActCtx");
    }

    // ---- identity ------------------------------------------------------------------------------

    [ThreadStatic] private static int t_lastError;

    [UnmanagedCallersOnly] private static uint GetCurrentThreadId() => (uint)Environment.CurrentManagedThreadId;
    [UnmanagedCallersOnly] private static uint GetCurrentProcessId() => (uint)Environment.ProcessId;
    [UnmanagedCallersOnly] private static nint GetCurrentProcess() => -1;
    [UnmanagedCallersOnly] private static nint GetCurrentThread() => -2;
    [UnmanagedCallersOnly] private static uint GetLastError() => (uint)t_lastError;
    [UnmanagedCallersOnly] private static void SetLastErrorExport(uint e) { t_lastError = (int)e; SetLastError((int)e); }

    [UnmanagedCallersOnly]
    private static int CloseHandle(nint h)
    {
        if (s_handles.TryRemove(h, out var o))
        {
            (o as IDisposable)?.Dispose();
            return TRUE;
        }
        // Pseudo-handles, and handles some other API of this library gave out: accepted, as Windows
        // accepts closing a handle twice in a release build.
        return TRUE;
    }

    // ---- memory --------------------------------------------------------------------------------

    private static readonly ConcurrentDictionary<nint, nuint> s_sizes = new();

    [UnmanagedCallersOnly]
    private static void* LocalAlloc(uint flags, nuint bytes)
    {
        // LMEM_ZEROINIT/GMEM_ZEROINIT is 0x40. Plain malloc, like Marshal.AllocHGlobal off Windows,
        // so memory either side frees is the other's to free too.
        void* p = (flags & 0x40) != 0 ? NativeMemory.AllocZeroed(Math.Max(bytes, 1)) : NativeMemory.Alloc(Math.Max(bytes, 1));
        s_sizes[(nint)p] = bytes;
        return p;
    }

    [UnmanagedCallersOnly]
    private static void* LocalFree(void* p)
    {
        if (p != null)
        {
            s_sizes.TryRemove((nint)p, out _);
            NativeMemory.Free(p);
        }
        return null;
    }

    [UnmanagedCallersOnly] private static void* LocalLock(void* p) => p;
    [UnmanagedCallersOnly] private static int LocalUnlock(void* p) { SetLastError(0); return FALSE; }
    [UnmanagedCallersOnly] private static nuint LocalSize(void* p) => s_sizes.TryGetValue((nint)p, out var n) ? n : 0;

    private static readonly ConcurrentDictionary<nint, nuint> s_virtual = new();

    [UnmanagedCallersOnly]
    private static void* VirtualAlloc(void* address, nuint size, uint type, uint protect)
    {
        if (address != null && s_virtual.ContainsKey((nint)address)) return address;   // MEM_COMMIT of a reservation
        void* p = NativeMemory.AlignedAlloc(Math.Max(size, 1), 4096);
        NativeMemory.Clear(p, size);
        s_virtual[(nint)p] = size;
        return p;
    }

    [UnmanagedCallersOnly]
    private static int VirtualFree(void* address, nuint size, uint type)
    {
        if ((type & 0x8000) != 0 && s_virtual.TryRemove((nint)address, out _))   // MEM_RELEASE
        {
            NativeMemory.AlignedFree(address);
        }
        return TRUE;
    }

    [UnmanagedCallersOnly] private static void RtlMoveMemory(void* dst, void* src, nuint n) => Buffer.MemoryCopy(src, dst, n, n);
    [UnmanagedCallersOnly] private static void RtlZeroMemory(void* dst, nuint n) => NativeMemory.Clear(dst, n);
    [UnmanagedCallersOnly] private static void RtlFillMemory(void* dst, nuint n, byte fill) => NativeMemory.Fill(dst, n, fill);

    // ---- time ----------------------------------------------------------------------------------

    [UnmanagedCallersOnly] private static int QueryPerformanceCounter(long* v) { *v = Stopwatch.GetTimestamp(); return TRUE; }
    [UnmanagedCallersOnly] private static int QueryPerformanceFrequency(long* v) { *v = Stopwatch.Frequency; return TRUE; }
    [UnmanagedCallersOnly] private static uint GetTickCount() => (uint)Environment.TickCount64;
    [UnmanagedCallersOnly] private static ulong GetTickCount64() => (ulong)Environment.TickCount64;
    [UnmanagedCallersOnly] private static void Sleep(uint ms) => Thread.Sleep(ms == uint.MaxValue ? Timeout.Infinite : (int)ms);
    [UnmanagedCallersOnly] private static uint SleepEx(uint ms, int alertable) { Thread.Sleep(ms == uint.MaxValue ? Timeout.Infinite : (int)ms); return 0; }
    [UnmanagedCallersOnly] private static void GetSystemTimeAsFileTime(long* ft) => *ft = DateTime.UtcNow.ToFileTimeUtc();
    [UnmanagedCallersOnly] private static void GetSystemTime(ushort* st) => WriteSystemTime(st, DateTime.UtcNow);
    [UnmanagedCallersOnly] private static void GetLocalTime(ushort* st) => WriteSystemTime(st, DateTime.Now);

    private static void WriteSystemTime(ushort* st, DateTime t)
    {
        st[0] = (ushort)t.Year; st[1] = (ushort)t.Month; st[2] = (ushort)t.DayOfWeek; st[3] = (ushort)t.Day;
        st[4] = (ushort)t.Hour; st[5] = (ushort)t.Minute; st[6] = (ushort)t.Second; st[7] = (ushort)t.Millisecond;
    }

    // TIME_ZONE_INFORMATION: LONG Bias; WCHAR StandardName[32]; SYSTEMTIME StandardDate; LONG StandardBias;
    //                        WCHAR DaylightName[32]; SYSTEMTIME DaylightDate; LONG DaylightBias;   (172 bytes)
    private static uint WriteTimeZone(byte* tzi, bool dynamic)
    {
        TimeZoneInfo tz = TimeZoneInfo.Local;
        int size = dynamic ? 432 : 172;
        NativeMemory.Clear(tzi, (nuint)size);
        *(int*)tzi = -(int)tz.BaseUtcOffset.TotalMinutes;
        WriteUtf16((char*)(tzi + 4), 32, tz.StandardName);
        WriteUtf16((char*)(tzi + 88), 32, tz.DaylightName);

        uint result = 0;    // TIME_ZONE_ID_UNKNOWN: no daylight saving
        var rule = tz.GetAdjustmentRules().FirstOrDefault(r => r.DateStart <= DateTime.Today && r.DateEnd >= DateTime.Today);
        if (rule != null && tz.SupportsDaylightSavingTime)
        {
            WriteTransition((ushort*)(tzi + 68), rule.DaylightTransitionEnd);
            WriteTransition((ushort*)(tzi + 152), rule.DaylightTransitionStart);
            *(int*)(tzi + 168) = -(int)rule.DaylightDelta.TotalMinutes;
            result = tz.IsDaylightSavingTime(DateTime.Now) ? 2u : 1u;
        }
        if (dynamic)
        {
            WriteUtf16((char*)(tzi + 172), 128, tz.Id);                 // TimeZoneKeyName
            *(int*)(tzi + 428) = rule == null ? TRUE : FALSE;           // DynamicDaylightTimeDisabled
        }
        return result;
    }

    // A SYSTEMTIME in "day-in-month" form, as Windows stores a recurring transition.
    private static void WriteTransition(ushort* st, TimeZoneInfo.TransitionTime t)
    {
        st[0] = 0;
        st[1] = (ushort)t.Month;
        st[2] = (ushort)(t.IsFixedDateRule ? 0 : (int)t.DayOfWeek);
        st[3] = (ushort)(t.IsFixedDateRule ? t.Day : t.Week);
        st[4] = (ushort)t.TimeOfDay.Hour; st[5] = (ushort)t.TimeOfDay.Minute; st[6] = (ushort)t.TimeOfDay.Second;
    }

    [UnmanagedCallersOnly] private static uint GetTimeZoneInformation(byte* tzi) => WriteTimeZone(tzi, false);
    [UnmanagedCallersOnly] private static uint GetDynamicTimeZoneInformation(byte* tzi) => WriteTimeZone(tzi, true);

    // ---- modules -------------------------------------------------------------------------------

    /// <summary>The library that stands in for every Windows DLL; set by the bootstrap.</summary>
    public static nint ShimHandle { get; set; }

    /// <summary>The Windows DLL names libwin32shim covers (lower case, no .dll); set by the bootstrap.</summary>
    public static Func<string, bool>? CoversModule { get; set; }

    private static nint ModuleHandleFor(string? name, bool load)
    {
        if (name == null) return ExeModule;
        string file = Path.GetFileName(name);
        string bare = file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
        string exe = Path.GetFileNameWithoutExtension(AppExePath());
        if (string.Equals(bare, exe, StringComparison.OrdinalIgnoreCase) || string.Equals(file, exe + ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return ExeModule;
        }
        // A Windows system DLL: hand out the library that stands in for it, so that GetProcAddress
        // on the result finds the same functions a DllImport does.
        if (ShimHandle != 0 && CoversModule?.Invoke(bare.ToLowerInvariant()) == true) return ShimHandle;
        if (load && NativeLibrary.TryLoad(name, out nint h)) return h;
        SetLastError(126);   // ERROR_MOD_NOT_FOUND
        return 0;
    }

    internal static string AppExePath()
    {
        string dir = AppContext.GetData("WpfMac.AppDirectory") as string ?? AppContext.BaseDirectory;
        string name = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "app";
        return Path.Combine(dir, name + ".exe");
    }

    [UnmanagedCallersOnly] private static nint GetModuleHandleW(char* name) => ModuleHandleFor(ReadUtf16(name), load: false);
    [UnmanagedCallersOnly] private static nint GetModuleHandleBare(byte* name) => ModuleHandleFor(ReadEither(name), load: false);
    [UnmanagedCallersOnly] private static nint LoadLibraryW(char* name) => ModuleHandleFor(ReadUtf16(name), load: true);
    [UnmanagedCallersOnly] private static nint LoadLibraryBare(byte* name) => ModuleHandleFor(ReadEither(name), load: true);
    [UnmanagedCallersOnly] private static nint LoadLibraryExW(char* name, nint file, uint flags) => ModuleHandleFor(ReadUtf16(name), load: true);
    [UnmanagedCallersOnly] private static nint LoadLibraryExBare(byte* name, nint file, uint flags) => ModuleHandleFor(ReadEither(name), load: true);
    [UnmanagedCallersOnly] private static int FreeLibrary(nint h) => TRUE;

    [UnmanagedCallersOnly]
    private static nint GetProcAddress(nint module, byte* name)
    {
        if ((nuint)name < 0x10000 || module == ExeModule || module == 0)   // by ordinal, or the exe
        {
            SetLastError(127);  // ERROR_PROC_NOT_FOUND
            return 0;
        }
        string? n = Marshal.PtrToStringUTF8((IntPtr)name);
        if (n != null && NativeLibrary.TryGetExport(module, n, out nint p)) return p;
        SetLastError(127);
        return 0;
    }

    private static uint ModuleFileName(nint module, string? path, out string? value)
    {
        value = module == 0 || module == ExeModule ? AppExePath() : path;
        return value == null ? 0u : (uint)value.Length;
    }

    [UnmanagedCallersOnly]
    private static uint GetModuleFileNameW(nint module, char* buffer, uint size)
    {
        ModuleFileName(module, null, out string? path);
        if (path == null) { SetLastError(126); return 0; }
        int n = WriteUtf16(buffer, (int)size, path);
        if (n < path.Length) SetLastError(ERROR_INSUFFICIENT_BUFFER);
        return (uint)n;
    }

    [UnmanagedCallersOnly]
    private static uint GetModuleFileNameBare(nint module, byte* buffer, uint size)
    {
        ModuleFileName(module, null, out string? path);
        if (path == null) { SetLastError(126); return 0; }
        return (uint)WriteUtf8(buffer, (int)size, path);
    }

    [UnmanagedCallersOnly] private static int ReturnTrue1(void* _) => TRUE;
    [UnmanagedCallersOnly] private static nint AddDllDirectory(void* _) => 1;
    private static uint s_errorMode;
    [UnmanagedCallersOnly] private static uint SetErrorMode(uint mode) { uint old = s_errorMode; s_errorMode = mode; return old; }

    // ---- the system --------------------------------------------------------------------------

    // What this reports itself as: Windows 11 23H2 (10.0.22631), a workstation, on arm64.
    private const uint OsMajor = 10, OsMinor = 0, OsBuild = 22631;

    [UnmanagedCallersOnly] private static int IsWow64Process(nint process, int* wow) { *wow = FALSE; return TRUE; }

    [UnmanagedCallersOnly]
    private static int IsWow64Process2(nint process, ushort* processMachine, ushort* nativeMachine)
    {
        *processMachine = 0;   // IMAGE_FILE_MACHINE_UNKNOWN: not running under WOW64
        if (nativeMachine != null) *nativeMachine = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? (ushort)0xAA64 : (ushort)0x8664;
        return TRUE;
    }

    [UnmanagedCallersOnly]
    private static void GetSystemInfo(byte* si)
    {
        // SYSTEM_INFO, 48 bytes on 64-bit.
        NativeMemory.Clear(si, 48);
        *(ushort*)si = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? (ushort)12 : (ushort)9;  // ARM64 / AMD64
        *(uint*)(si + 4) = (uint)Environment.SystemPageSize;
        *(nint*)(si + 8) = 0x10000;
        *(nint*)(si + 16) = (nint)0x7FFFFFFEFFFF;
        int cpus = Environment.ProcessorCount;
        *(nuint*)(si + 24) = cpus >= 64 ? nuint.MaxValue : (nuint)((1UL << cpus) - 1);
        *(uint*)(si + 32) = (uint)cpus;
        *(uint*)(si + 36) = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? 0u : 8664u;
        *(uint*)(si + 40) = 0x10000;
    }

    // OSVERSIONINFOEXW: dwOSVersionInfoSize, dwMajorVersion, dwMinorVersion, dwBuildNumber, dwPlatformId,
    // szCSDVersion[128], wServicePackMajor, wServicePackMinor, wSuiteMask, wProductType, wReserved.
    private static void WriteVersion(uint* v)
    {
        uint size = v[0];
        v[1] = OsMajor; v[2] = OsMinor; v[3] = OsBuild; v[4] = 2;   // VER_PLATFORM_WIN32_NT
        ((char*)(v + 5))[0] = '\0';
        if (size >= 284)
        {
            byte* tail = (byte*)v + 276;
            *(ushort*)tail = 0; *(ushort*)(tail + 2) = 0;
            *(ushort*)(tail + 4) = 0x100;   // VER_SUITE_SINGLEUSERTS
            tail[6] = 1;                    // VER_NT_WORKSTATION
        }
    }

    [UnmanagedCallersOnly] private static int GetVersionEx(uint* v) { WriteVersion(v); return TRUE; }
    [UnmanagedCallersOnly] private static int RtlGetVersion(uint* v) { WriteVersion(v); return 0; }
    [UnmanagedCallersOnly] private static uint GetVersion() => OsMajor | (OsMinor << 8) | (OsBuild << 16);
    [UnmanagedCallersOnly] private static int GetProductInfo(uint a, uint b, uint c, uint d, uint* type) { *type = 0x30; return TRUE; }   // PRODUCT_PROFESSIONAL

    [UnmanagedCallersOnly]
    private static int GlobalMemoryStatusEx(byte* m)
    {
        // MEMORYSTATUSEX: dwLength, dwMemoryLoad, then seven ULONGLONGs.
        ulong total = MacNative.SysctlU64("hw.memsize");
        var gc = GC.GetGCMemoryInfo();
        ulong avail = gc.TotalAvailableMemoryBytes > 0 ? Math.Min(total, (ulong)Math.Max(0, gc.TotalAvailableMemoryBytes - gc.MemoryLoadBytes)) : total / 2;
        *(uint*)(m + 4) = total == 0 ? 0u : (uint)(100 - avail * 100 / total);
        *(ulong*)(m + 8) = total;
        *(ulong*)(m + 16) = avail;
        *(ulong*)(m + 24) = total * 2;
        *(ulong*)(m + 32) = avail + total;
        *(ulong*)(m + 40) = 0x7FFFFFFE0000;
        *(ulong*)(m + 48) = 0x7FFF00000000;
        *(ulong*)(m + 56) = 0;
        return TRUE;
    }

    private static uint s_executionState = 0x80000000;  // ES_CONTINUOUS
    [UnmanagedCallersOnly] private static uint SetThreadExecutionState(uint state) { uint old = s_executionState; s_executionState = state; return old; }
    [UnmanagedCallersOnly] private static int SetProcessWorkingSetSize(nint p, nuint min, nuint max) => TRUE;
    [UnmanagedCallersOnly] private static void* IdentityPointer(void* p) => p;

    // ---- synchronisation ------------------------------------------------------------------------

    private static readonly ConcurrentDictionary<nint, object> s_criticalSections = new();
    private static object Section(void* cs) => s_criticalSections.GetOrAdd((nint)cs, _ => new object());

    [UnmanagedCallersOnly] private static void InitializeCriticalSection(void* cs) => s_criticalSections[(nint)cs] = new object();
    [UnmanagedCallersOnly] private static int InitializeCriticalSectionEx(void* cs, uint spin, uint flags) { s_criticalSections[(nint)cs] = new object(); return TRUE; }
    [UnmanagedCallersOnly] private static int InitializeCriticalSectionAndSpinCount(void* cs, uint spin) { s_criticalSections[(nint)cs] = new object(); return TRUE; }
    [UnmanagedCallersOnly] private static void EnterCriticalSection(void* cs) => Monitor.Enter(Section(cs));
    [UnmanagedCallersOnly] private static int TryEnterCriticalSection(void* cs) => Monitor.TryEnter(Section(cs)) ? TRUE : FALSE;
    [UnmanagedCallersOnly] private static void LeaveCriticalSection(void* cs) { var o = Section(cs); if (Monitor.IsEntered(o)) Monitor.Exit(o); }
    [UnmanagedCallersOnly] private static void DeleteCriticalSection(void* cs) => s_criticalSections.TryRemove((nint)cs, out _);

    private static nint CreateEventCore(bool manualReset, bool initial, string? name)
    {
        // A NAMED event is shared between processes on Windows. One process at a time is what can be
        // offered here; the name is kept so that a second CreateEvent of it in the same process
        // returns the same event (with ERROR_ALREADY_EXISTS), which is what single-instance checks
        // inside one process rely on.
        if (name != null)
        {
            lock (s_namedEvents)
            {
                if (s_namedEvents.TryGetValue(name, out var existing))
                {
                    SetLastError(183);  // ERROR_ALREADY_EXISTS
                    return NewHandle(existing);
                }
                var created = new EventWaitHandle(initial, manualReset ? EventResetMode.ManualReset : EventResetMode.AutoReset);
                s_namedEvents[name] = created;
                SetLastError(0);
                return NewHandle(created);
            }
        }
        SetLastError(0);
        return NewHandle(new EventWaitHandle(initial, manualReset ? EventResetMode.ManualReset : EventResetMode.AutoReset));
    }

    private static readonly Dictionary<string, EventWaitHandle> s_namedEvents = new(StringComparer.Ordinal);

    [UnmanagedCallersOnly] private static nint CreateEventW(void* sa, int manual, int initial, char* name) => CreateEventCore(manual != 0, initial != 0, ReadUtf16(name));
    [UnmanagedCallersOnly] private static nint CreateEventBare(void* sa, int manual, int initial, byte* name) => CreateEventCore(manual != 0, initial != 0, ReadEither(name));
    [UnmanagedCallersOnly] private static int SetEvent(nint h) => Handle(h) is EventWaitHandle e && e.Set() ? TRUE : FALSE;
    [UnmanagedCallersOnly] private static int ResetEvent(nint h) => Handle(h) is EventWaitHandle e && e.Reset() ? TRUE : FALSE;

    private static nint CreateMutexCore(bool initialOwner, string? name)
    {
        // .NET supports named mutexes across processes on macOS (Global\ and Local\ prefixes included).
        try
        {
            var m = name == null ? new Mutex(initialOwner) : new Mutex(initialOwner, name, out bool createdNew);
            SetLastError(name != null && !initialOwner ? 0 : 0);
            return NewHandle(m);
        }
        catch (Exception)
        {
            SetLastError(5);    // ERROR_ACCESS_DENIED
            return 0;
        }
    }

    [UnmanagedCallersOnly] private static nint CreateMutexW(void* sa, int owner, char* name) => CreateMutexCore(owner != 0, ReadUtf16(name));
    [UnmanagedCallersOnly] private static nint CreateMutexBare(void* sa, int owner, byte* name) => CreateMutexCore(owner != 0, ReadEither(name));
    [UnmanagedCallersOnly] private static int ReleaseMutex(nint h) { if (Handle(h) is Mutex m) { try { m.ReleaseMutex(); return TRUE; } catch { } } return FALSE; }

    private const uint WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 0x102, WAIT_FAILED = 0xFFFFFFFF;

    private static uint Wait(object? o, uint ms)
    {
        int timeout = ms == uint.MaxValue ? Timeout.Infinite : (int)Math.Min(ms, int.MaxValue);
        switch (o)
        {
            case WaitHandle w:
                try { return w.WaitOne(timeout) ? WAIT_OBJECT_0 : WAIT_TIMEOUT; }
                catch (AbandonedMutexException) { return 0x80; }   // WAIT_ABANDONED
            case Process p:
                return p.WaitForExit(timeout) ? WAIT_OBJECT_0 : WAIT_TIMEOUT;
            default:
                SetLastError(ERROR_INVALID_HANDLE);
                return WAIT_FAILED;
        }
    }

    [UnmanagedCallersOnly] private static uint WaitForSingleObject(nint h, uint ms) => Wait(Handle(h), ms);

    [UnmanagedCallersOnly]
    private static uint WaitForMultipleObjects(uint count, nint* handles, int waitAll, uint ms)
    {
        var waits = new WaitHandle[count];
        for (int i = 0; i < count; i++)
        {
            if (Handle(handles[i]) is not WaitHandle w) { SetLastError(ERROR_INVALID_HANDLE); return WAIT_FAILED; }
            waits[i] = w;
        }
        int timeout = ms == uint.MaxValue ? Timeout.Infinite : (int)Math.Min(ms, int.MaxValue);
        if (waitAll != 0) return WaitHandle.WaitAll(waits, timeout) ? WAIT_OBJECT_0 : WAIT_TIMEOUT;
        int index = WaitHandle.WaitAny(waits, timeout);
        return index == WaitHandle.WaitTimeout ? WAIT_TIMEOUT : (uint)index;
    }

    [UnmanagedCallersOnly]
    private static nint OpenProcess(uint access, int inherit, uint pid)
    {
        try { return NewHandle(Process.GetProcessById((int)pid)); }
        catch { SetLastError(ERROR_INVALID_PARAMETER); return 0; }
    }

    // ---- text and the environment ---------------------------------------------------------------

    private static readonly Dictionary<uint, string> s_messages = new()
    {
        [0] = "The operation completed successfully.",
        [2] = "The system cannot find the file specified.",
        [3] = "The system cannot find the path specified.",
        [5] = "Access is denied.",
        [6] = "The handle is invalid.",
        [8] = "Not enough memory resources are available to process this command.",
        [32] = "The process cannot access the file because it is being used by another process.",
        [87] = "The parameter is incorrect.",
        [120] = "This function is not supported on this system.",
        [122] = "The data area passed to a system call is too small.",
        [126] = "The specified module could not be found.",
        [127] = "The specified procedure could not be found.",
        [183] = "Cannot create a file when that file already exists.",
        [1400] = "Invalid window handle.",
        [1460] = "This operation returned because the timeout period expired.",
    };

    private static string MessageText(uint id) =>
        (s_messages.TryGetValue(id, out string? m) ? m : $"Unknown error (0x{id:x})") + "\r\n";

    private static uint FormatMessageCore(uint flags, uint id, void* buffer, uint size, bool wide)
    {
        if ((flags & 0x1000) == 0)   // only FORMAT_MESSAGE_FROM_SYSTEM has a source here
        {
            SetLastError(ERROR_CALL_NOT_IMPLEMENTED);
            return 0;
        }
        string text = MessageText(id);
        if ((flags & 0x100) != 0)    // FORMAT_MESSAGE_ALLOCATE_BUFFER: buffer is a pointer to the pointer
        {
            int bytes = wide ? (text.Length + 1) * 2 : System.Text.Encoding.UTF8.GetByteCount(text) + 1;
            void* p = NativeMemory.AllocZeroed((nuint)bytes);
            s_sizes[(nint)p] = (nuint)bytes;
            if (wide) WriteUtf16((char*)p, text.Length + 1, text); else WriteUtf8((byte*)p, bytes, text);
            *(void**)buffer = p;
            return (uint)text.Length;
        }
        int written = wide ? WriteUtf16((char*)buffer, (int)size, text) : WriteUtf8((byte*)buffer, (int)size, text);
        return (uint)written;
    }

    [UnmanagedCallersOnly] private static uint FormatMessageW(uint flags, void* source, uint id, uint lang, char* buffer, uint size, void* args) => FormatMessageCore(flags, id, buffer, size, true);
    [UnmanagedCallersOnly] private static uint FormatMessageBare(uint flags, void* source, uint id, uint lang, byte* buffer, uint size, void* args) => FormatMessageCore(flags, id, buffer, size, false);
    [UnmanagedCallersOnly] private static int lstrlenW(char* s) => s == null ? 0 : new ReadOnlySpan<char>(s, int.MaxValue).IndexOf('\0');

    [UnmanagedCallersOnly]
    private static int MulDiv(int number, int numerator, int denominator)
    {
        if (denominator == 0) return -1;
        long r = (long)number * numerator;
        // Windows rounds half away from zero.
        long q = (Math.Abs(r) + Math.Abs(denominator) / 2) / Math.Abs(denominator);
        q *= Math.Sign(r) * Math.Sign(denominator);
        return q > int.MaxValue || q < int.MinValue ? -1 : (int)q;
    }

    [UnmanagedCallersOnly] private static uint GetUserDefaultLCID() => (uint)CultureInfo.CurrentCulture.LCID;
    [UnmanagedCallersOnly] private static ushort GetUserDefaultUILanguage() => (ushort)CultureInfo.CurrentUICulture.LCID;
    // The code page "ANSI" means: UTF-8 is what .NET uses for it off Windows, so that is the truth.
    [UnmanagedCallersOnly] private static uint GetACP() => 65001;
    [UnmanagedCallersOnly] private static uint GetOEMCP() => 437;

    [UnmanagedCallersOnly]
    private static int GetComputerNameW(char* buffer, uint* size)
    {
        // The NetBIOS name: upper case, at most 15 characters.
        string name = Environment.MachineName.ToUpperInvariant();
        if (name.Length > 15) name = name[..15];
        if (*size <= name.Length) { *size = (uint)name.Length + 1; SetLastError(111); return FALSE; }  // ERROR_BUFFER_OVERFLOW
        *size = (uint)WriteUtf16(buffer, (int)*size, name);
        return TRUE;
    }

    private static uint CopyOut(string value, char* buffer, uint size)
    {
        if (buffer == null || size <= value.Length) return (uint)value.Length + 1;   // the size needed, with the NUL
        return (uint)WriteUtf16(buffer, (int)size, value);
    }

    [UnmanagedCallersOnly] private static uint ExpandEnvironmentStringsW(char* src, char* dst, uint size) => CopyOut(Environment.ExpandEnvironmentVariables(ReadUtf16(src) ?? ""), dst, size) + (dst != null && size > 0 ? 1u : 0u);

    [UnmanagedCallersOnly]
    private static uint GetEnvironmentVariableW(char* name, char* buffer, uint size)
    {
        string? value = Environment.GetEnvironmentVariable(ReadUtf16(name) ?? "");
        if (value == null) { SetLastError(203); return 0; }   // ERROR_ENVVAR_NOT_FOUND
        return CopyOut(value, buffer, size);
    }

    [UnmanagedCallersOnly] private static int SetEnvironmentVariableW(char* name, char* value) { Environment.SetEnvironmentVariable(ReadUtf16(name) ?? "", ReadUtf16(value)); return TRUE; }
    [UnmanagedCallersOnly] private static uint GetTempPathW(uint size, char* buffer) => CopyOut(Path.GetTempPath(), buffer, size);
    [UnmanagedCallersOnly] private static uint GetWindowsDirectoryW(char* buffer, uint size) => CopyOut(@"C:\Windows", buffer, size);
    [UnmanagedCallersOnly] private static uint GetSystemDirectoryW(char* buffer, uint size) => CopyOut(@"C:\Windows\System32", buffer, size);
    [UnmanagedCallersOnly] private static uint GetShortPathNameW(char* path, char* buffer, uint size) => CopyOut(ReadUtf16(path) ?? "", buffer, size);
    [UnmanagedCallersOnly] private static void OutputDebugStringW(char* s) => Debug.WriteLine(ReadUtf16(s));

    // ---- what there is no counterpart of -------------------------------------------------------

    [UnmanagedCallersOnly] private static int ReturnFalseU(uint _) { SetLastError(ERROR_CALL_NOT_IMPLEMENTED); return FALSE; }
    [UnmanagedCallersOnly] private static int ReturnFalse0() { SetLastError(ERROR_CALL_NOT_IMPLEMENTED); return FALSE; }
    [UnmanagedCallersOnly] private static nint ReturnNull0() => 0;
    [UnmanagedCallersOnly] private static int ReturnSOk0() => S_OK;
    [UnmanagedCallersOnly] private static int GetCurrentPackageFullName(uint* length, char* name) { if (length != null) *length = 0; return 15700; }   // APPMODEL_ERROR_NO_PACKAGE
    [UnmanagedCallersOnly] private static int RegisterApplicationRecoveryCallback(void* cb, void* p, uint ping, uint flags) => S_OK;
    [UnmanagedCallersOnly] private static int RegisterApplicationRestart(char* cmd, uint flags) => S_OK;
    [UnmanagedCallersOnly] private static int ApplicationRecoveryInProgress(int* cancelled) { *cancelled = FALSE; return S_OK; }
    [UnmanagedCallersOnly] private static void ApplicationRecoveryFinished(int success) { }

    // Activation contexts select comctl32 v6 (visual styles) for the code between Activate and
    // Deactivate. There is one comctl32 here, so they are accepted and do nothing.
    [UnmanagedCallersOnly] private static nint CreateActCtx(void* actctx) => NewHandle(new object());
    [UnmanagedCallersOnly] private static int ActivateActCtx(nint ctx, nuint* cookie) { if (cookie != null) *cookie = 1; return TRUE; }
    [UnmanagedCallersOnly] private static int DeactivateActCtx(uint flags, nuint cookie) => TRUE;
    [UnmanagedCallersOnly] private static void ReleaseActCtx(nint ctx) => s_handles.TryRemove(ctx, out _);
}
