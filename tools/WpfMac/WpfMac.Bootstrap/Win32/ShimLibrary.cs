// macOS: hands an app libwin32shim when it P/Invokes a Windows DLL, and fills the library's slots.
//
// libwin32shim exports every Windows API function by name (see WpfMac.Win32Native). This class
//   - answers ResolvingUnmanagedDll for the Windows DLL names the library covers, so an app's
//     [DllImport("user32.dll")] binds to it instead of failing with DllNotFoundException;
//   - installs the handler every function without an implementation falls into, which logs the call
//     once and returns that function's failure value (FALSE/NULL with ERROR_CALL_NOT_IMPLEMENTED,
//     E_NOTIMPL, ...), so a Windows-only call degrades the way it would on a Windows that lacked it;
//   - points the slots of the functions that DO have an implementation (Win32/*.cs) at them.
//
// The STACK's own assemblies are deliberately not given the library: their Windows P/Invokes are
// guarded or already handle DllNotFoundException, and an import that suddenly succeeds with a stub
// value could take a path they never take off Windows. Only the app's code (and the libraries it
// ships) gets it.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace WpfMac.Bootstrap.Win32
{
    internal static unsafe class ShimLibrary
    {
        private const int ERROR_CALL_NOT_IMPLEMENTED = 120;

        private static IntPtr s_handle;
        private static string? s_stackDir;
        private static readonly HashSet<string> s_modules = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> s_exports = new(StringComparer.Ordinal);
        private static string[] s_names = Array.Empty<string>();
        private static string[] s_exportModules = Array.Empty<string>();
        private static byte[] s_failures = Array.Empty<byte>();
        private static readonly HashSet<AssemblyLoadContext> s_hooked = new();

        private static delegate* unmanaged<int, void*, int> s_setSlot;

        public static bool Loaded => s_handle != IntPtr.Zero;

        /// <summary>
        /// From now on an abort() ends the process quietly (status 134, a line in the log) instead of
        /// as a crash macOS reports with a dialog. See wpfmac_quiet_abort in win32shim_runtime.c.
        /// </summary>
        public static void QuietAborts(string? logPath)
        {
            if (s_handle == IntPtr.Zero || !NativeLibrary.TryGetExport(s_handle, "wpfmac_quiet_abort", out IntPtr export)) return;
            byte[]? path = logPath == null ? null : System.Text.Encoding.UTF8.GetBytes(logPath + "\0");
            fixed (byte* p = path)
            {
                if (((delegate* unmanaged<byte*, int>)export)(p) == 0) CompatLog.Info("quiet aborts: sigaction failed");
            }

            // The runtime restores the default SIGABRT action before it aborts over an unhandled
            // exception, so that case ends here instead, once the log has the exception (its handler
            // was added first and runs first).
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (!e.IsTerminating) return;
                Console.Error.WriteLine("Unhandled exception. " + e.ExceptionObject);
                Console.Error.WriteLine("wpfmac: exiting with status 134 instead of crashing. See the compatibility log.");
                CompatLog.Info("[exit] unhandled exception: exited with status 134 instead of crashing");
                var exit = (delegate* unmanaged<int, void>)NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_exit");
                exit(134);
            };
        }

        public static void Initialize(string overlayDir)
        {
            string path = Path.Combine(overlayDir, "libwin32shim.dylib");
            if (!OperatingSystem.IsMacOS() || !File.Exists(path)) return;
            try
            {
                s_handle = NativeLibrary.Load(path);
            }
            catch (Exception e)
            {
                CompatLog.Info($"win32 shim: could not load {path}: {e.Message}");
                return;
            }
            s_stackDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(overlayDir)) + Path.DirectorySeparatorChar;

            var count = (delegate* unmanaged<int>)NativeLibrary.GetExport(s_handle, "wpfmac_shim_export_count");
            var moduleCount = (delegate* unmanaged<int>)NativeLibrary.GetExport(s_handle, "wpfmac_shim_module_count");
            var moduleName = (delegate* unmanaged<int, byte*>)NativeLibrary.GetExport(s_handle, "wpfmac_shim_module_name");
            var exportName = (delegate* unmanaged<int, byte*>)NativeLibrary.GetExport(s_handle, "wpfmac_shim_export_name");
            var exportModule = (delegate* unmanaged<int, int>)NativeLibrary.GetExport(s_handle, "wpfmac_shim_export_module");
            var exportFailure = (delegate* unmanaged<int, int>)NativeLibrary.GetExport(s_handle, "wpfmac_shim_export_failure");
            s_setSlot = (delegate* unmanaged<int, void*, int>)NativeLibrary.GetExport(s_handle, "wpfmac_shim_set_slot");
            var setHandler = (delegate* unmanaged<delegate* unmanaged<int, long>, void>)NativeLibrary.GetExport(s_handle, "wpfmac_shim_set_unimplemented_handler");

            int modules = moduleCount();
            var moduleNames = new string[modules];
            for (int i = 0; i < modules; i++)
            {
                moduleNames[i] = Marshal.PtrToStringUTF8((IntPtr)moduleName(i))!;
                s_modules.Add(moduleNames[i]);
            }
            int n = count();
            s_names = new string[n];
            s_exportModules = new string[n];
            s_failures = new byte[n];
            for (int i = 0; i < n; i++)
            {
                s_names[i] = Marshal.PtrToStringUTF8((IntPtr)exportName(i))!;
                s_exportModules[i] = moduleNames[exportModule(i)];
                s_failures[i] = (byte)exportFailure(i);
                s_exports[s_names[i]] = i;
            }

            setHandler(&OnUnimplemented);
            int implemented = Implementations.RegisterAll(s_handle, m => s_modules.Contains(m));
            // Direct3D (d3d11, dxgi, d3d9) over WebGPU: there is no other Direct3D here.
            int d3d = 0;
            Direct3DShim.Register((module, name, fn) => { if (Register(name, (void*)fn)) d3d++; });
            implemented += d3d;

            Hook(AssemblyLoadContext.Default);
            AppDomain.CurrentDomain.AssemblyLoad += (_, e) =>
            {
                var alc = AssemblyLoadContext.GetLoadContext(e.LoadedAssembly);
                if (alc != null) Hook(alc);
            };
            CompatLog.Info($"win32 shim: {n} exports from {modules} Windows DLLs, {implemented} implemented");
        }

        /// <summary>Points <paramref name="name"/>'s slot at <paramref name="fn"/>. False if the library has no such export.</summary>
        public static bool Register(string name, void* fn)
        {
            if (!s_exports.TryGetValue(name, out int index)) return false;
            return s_setSlot(index, fn) != 0;
        }

        private static void Hook(AssemblyLoadContext alc)
        {
            lock (s_hooked)
            {
                if (!s_hooked.Add(alc)) return;
            }
            alc.ResolvingUnmanagedDll += Resolve;
        }

        private static IntPtr Resolve(Assembly requester, string name)
        {
            if (s_handle == IntPtr.Zero) return IntPtr.Zero;
            string module = Normalize(name);
            if (!s_modules.Contains(module)) return IntPtr.Zero;
            string? location = requester.Location;
            if (s_stackDir != null && !string.IsNullOrEmpty(location)
                && location.StartsWith(s_stackDir, StringComparison.OrdinalIgnoreCase))
            {
                return IntPtr.Zero;          // the stack's own imports: see the header
            }
            CompatLog.Once("dll:" + module, $"win32 shim: {requester.GetName().Name} loads {name}");
            return s_handle;
        }

        /// <summary>user32.dll, USER32, user32 -> user32. A path is reduced to its file name.</summary>
        internal static string Normalize(string name)
        {
            string file = Path.GetFileName(name);
            if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) file = file[..^4];
            return file.ToLowerInvariant();
        }

        [UnmanagedCallersOnly]
        private static long OnUnimplemented(int index)
        {
            byte failure = (uint)index < (uint)s_failures.Length ? s_failures[index] : (byte)1;
            if ((uint)index < (uint)s_names.Length)
            {
                CompatLog.Once("unimpl:" + index, $"win32 shim: {s_exportModules[index]}!{s_names[index]} is not implemented; returned {Describe(failure)}");
            }
            switch (failure)
            {
                case 0: return 0;                                         // void
                case 2: return unchecked((int)0x80004001);                // E_NOTIMPL
                case 3: return unchecked((int)0xC0000002);                // STATUS_NOT_IMPLEMENTED
                case 4: return ERROR_CALL_NOT_IMPLEMENTED;                // WIN32_ERROR, returned
                case 5: Marshal.SetLastSystemError(ERROR_CALL_NOT_IMPLEMENTED); return -1;  // INVALID_HANDLE_VALUE
                default: Marshal.SetLastSystemError(ERROR_CALL_NOT_IMPLEMENTED); return 0;  // FALSE / NULL / 0
            }
        }

        private static string Describe(byte failure) => failure switch
        {
            0 => "nothing",
            2 => "E_NOTIMPL",
            3 => "STATUS_NOT_IMPLEMENTED",
            4 => "ERROR_CALL_NOT_IMPLEMENTED",
            5 => "INVALID_HANDLE_VALUE",
            _ => "0 (last error ERROR_CALL_NOT_IMPLEMENTED)",
        };
    }
}
