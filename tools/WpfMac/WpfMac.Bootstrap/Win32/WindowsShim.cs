// Windows: an app's own P/Invokes into user32 reach the stack's windows when the handle is the stack's.
//
// The problem. On Windows the app runs on the real Windows API: wpfmac has nothing to emulate, so its
// [DllImport("user32.dll")] calls bind to the real user32. But this stack's WinForms controls are not
// Windows windows. Control.Handle is a handle the stack's WinForms driver made up, so every Win32 call
// an app makes on one -- GetWindowDC(control.Handle), SendMessage(control.Handle, ...),
// GetWindowRect, SetWindowPos, BeginPaint -- reaches a Windows that has never heard of it and fails
// (DotNetBar's combo boxes paint themselves with BeginPaint and died on the null DC that came back).
//
// The choices considered:
//   - make the controls real child HWNDs. They would then be real Windows windows the stack does not
//     draw (GDI on a real HWND under a swap chain), and the WinForms the stack runs everywhere else
//     would have a Windows-only shape. No.
//   - detour user32 in the process. That catches the stack's own calls, WPF's, the runtime's, and the
//     native code's too; on win-arm64 there is no detour library at all (MonoMod does not support it).
//   - give the APP's assemblies a different user32. This is what the runtime offers for exactly this:
//     NativeLibrary.SetDllImportResolver, per assembly, consulted before the OS search. It is what the
//     macOS head already does with ResolvingUnmanagedDll (there the real user32 does not exist).
//
// So: for each covered system DLL, a DLL is generated (ShimImage) that exports the same functions at
// the same ordinals. A function nobody implements jumps straight to the real one, arguments untouched.
// A function that has a managed implementation (WpfMac.Win32, Windows.cs) goes through a classifier
// first: if one of its handle arguments is the stack's own, the managed implementation runs (the one
// macOS uses, over the stack's windows); otherwise the real function does. A real HWND -- a WPF
// window, the host window a form is shown in, a window the app created itself, the desktop -- always
// reaches the real user32, so nothing about it changes.
//
// The app's assemblies get the generated DLL through their resolver; the stack's own assemblies (the
// overlay) and the runtime's do not, because they call user32 on real handles only (but for
// System.Diagnostics.Process, whose MainWindowHandle asks about the app's own windows). An assembly that
// installs a resolver of its own is left alone (a second SetDllImportResolver would throw in it).
//
// No native toolchain: the image is written here, from the real DLL's export table, so it follows the
// Windows build it runs on, and it is cached by content.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace WpfMac.Bootstrap.Win32
{
    internal static unsafe class WindowsShim
    {
        private sealed class Module
        {
            public required string Name;              // user32
            public required IntPtr Real;
            public IntPtr Shim;
            public readonly Dictionary<string, (IntPtr Fn, int Mask)> Implemented = new(StringComparer.Ordinal);
        }

        private static readonly Dictionary<string, Module> s_modules = new(StringComparer.OrdinalIgnoreCase);
        private static string? s_stackDir, s_runtimeDir;
        private static readonly HashSet<Assembly> s_seen = new();

        public static void Initialize(string overlayDir, string? cacheDir)
        {
            if (!OperatingSystem.IsWindows()) return;
            s_stackDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(overlayDir)) + Path.DirectorySeparatorChar;
            s_runtimeDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory())) + Path.DirectorySeparatorChar;
            IntPtr classifier;
            try
            {
                Assembly asm = Assembly.Load(new AssemblyName("WpfMac.Win32"));
                Type? exports = asm.GetType("WpfMac.Win32.Exports");
                var register = exports?.GetMethod("RegisterWindows");
                if (register == null) return;
                Action<string, string, IntPtr, int> add = (module, name, fn, mask) =>
                {
                    if (!s_modules.TryGetValue(module, out var m))
                    {
                        IntPtr real = NativeLibrary.Load(module + ".dll", typeof(WindowsShim).Assembly, DllImportSearchPath.System32);
                        s_modules[module] = m = new Module { Name = module, Real = real };
                    }
                    m.Implemented[name] = (fn, mask);
                };
                Action<string, string> logOnce = CompatLog.Once;
                classifier = (IntPtr)register.Invoke(null, new object[] { add, logOnce, RuntimeInformation.ProcessArchitecture })!;
            }
            catch (Exception e)
            {
                CompatLog.Info($"win32 dispatch: the managed implementations did not load: {e.GetBaseException().Message}");
                return;
            }

            string dir = Path.Combine(cacheDir ?? Path.Combine(Path.GetTempPath(), "WpfMac"), "win32");
            foreach (Module m in s_modules.Values)
            {
                try
                {
                    Build(m, dir, classifier);
                }
                catch (Exception e)
                {
                    CompatLog.Info($"win32 dispatch: no {m.Name} shim: {e.Message}");
                }
            }
            if (!s_modules.Values.Any(m => m.Shim != IntPtr.Zero)) return;

            AppDomain.CurrentDomain.AssemblyLoad += (_, e) => Hook(e.LoadedAssembly);
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) Hook(a);
        }

        private static void Build(Module m, string dir, IntPtr classifier)
        {
            var exports = ReadExports(m.Real, out int ordinalBase);
            var list = new List<ShimImage.Export>(exports.Count);
            int dispatched = 0;
            foreach (var (ordinal, name, rva) in exports)
            {
                bool d = name != null && m.Implemented.ContainsKey(name);
                if (d) dispatched++;
                list.Add(new ShimImage.Export(ordinal, name, rva != 0, d));
            }
            var arch = RuntimeInformation.ProcessArchitecture;
            var layout = ShimImage.Build($"wpfmac-{m.Name}.dll", ordinalBase, list, arch);
            string hash = Convert.ToHexString(SHA256.HashData(layout.Image))[..16].ToLowerInvariant();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"wpfmac-{m.Name}-{hash}.dll");
            if (!File.Exists(path))
            {
                string temp = path + "." + Environment.ProcessId + ".tmp";
                File.WriteAllBytes(temp, layout.Image);
                try { File.Move(temp, path); } catch (IOException) { File.Delete(temp); }   // another process won
            }
            IntPtr shim = NativeLibrary.Load(path);
            byte* data = (byte*)shim + layout.DataRva;
            *(IntPtr*)(data + ShimImage.ClassifierOffset) = classifier;
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].Present) continue;
                byte* entry = data + ShimImage.TableOffset + i * ShimImage.EntrySize;
                IntPtr target = GetProcAddressByOrdinal(m.Real, list[i].Ordinal);
                *(IntPtr*)entry = target;
                if (list[i].Dispatched && m.Implemented.TryGetValue(list[i].Name!, out var impl))
                {
                    *(IntPtr*)(entry + 8) = impl.Fn;
                    *(int*)(entry + 16) = impl.Mask;
                }
            }
            m.Shim = shim;
            CompatLog.Info($"win32 dispatch: {m.Name}: {list.Count(e => e.Present)} exports, {dispatched} reach the stack's windows ({Path.GetFileName(path)})");
        }

        /// <summary>The export table of a loaded module: (ordinal, name or null, function RVA or 0) for
        /// every ordinal from Base to Base + NumberOfFunctions - 1.</summary>
        private static List<(int Ordinal, string? Name, int Rva)> ReadExports(IntPtr module, out int ordinalBase)
        {
            byte* b = (byte*)module;
            int pe = *(int*)(b + 0x3C);
            int opt = pe + 24;
            ushort magic = *(ushort*)(b + opt);
            int dirs = opt + (magic == 0x20B ? 112 : 96);
            int edirRva = *(int*)(b + dirs);
            byte* edir = b + edirRva;
            ordinalBase = *(int*)(edir + 16);
            int functions = *(int*)(edir + 20);
            int names = *(int*)(edir + 24);
            int* eat = (int*)(b + *(int*)(edir + 28));
            int* namePtrs = (int*)(b + *(int*)(edir + 32));
            ushort* nameOrds = (ushort*)(b + *(int*)(edir + 36));
            var byIndex = new string?[functions];
            for (int i = 0; i < names; i++)
            {
                int index = nameOrds[i];
                if (index < functions) byIndex[index] = Marshal.PtrToStringAnsi((IntPtr)(b + namePtrs[i]));
            }
            var list = new List<(int, string?, int)>(functions);
            for (int i = 0; i < functions; i++) list.Add((ordinalBase + i, byIndex[i], eat[i]));
            return list;
        }

        [DllImport("kernel32", EntryPoint = "GetProcAddress", ExactSpelling = true)]
        private static extern IntPtr GetProcAddressOrdinal(IntPtr module, IntPtr ordinal);

        private static IntPtr GetProcAddressByOrdinal(IntPtr module, int ordinal) => GetProcAddressOrdinal(module, (IntPtr)ordinal);

        private static void Hook(Assembly a)
        {
            lock (s_seen)
            {
                if (!s_seen.Add(a)) return;
            }
            if (a.IsDynamic) return;
            string location;
            try { location = a.Location; } catch { return; }
            if (string.IsNullOrEmpty(location)) return;
            if (s_stackDir != null && location.StartsWith(s_stackDir, StringComparison.OrdinalIgnoreCase)) return;
            // The runtime calls user32 on real handles only -- except where it answers a question
            // about the app's own windows: Process.MainWindowHandle and MainWindowTitle enumerate the
            // process's windows, which are the stack's forms (see WpfMac.Win32, WindowsAnswers.cs).
            if (s_runtimeDir != null && location.StartsWith(s_runtimeDir, StringComparison.OrdinalIgnoreCase)
                && a.GetName().Name != "System.Diagnostics.Process") return;
            if (!ImportsCoveredModule(location, out bool ownResolver)) return;
            if (ownResolver)
            {
                CompatLog.Info($"win32 dispatch: {a.GetName().Name} resolves its own native libraries; its user32 calls stay on the real user32");
                return;
            }
            try
            {
                NativeLibrary.SetDllImportResolver(a, Resolve);
            }
            catch (InvalidOperationException)
            {
                // It set one first (from a module initializer): its own resolution stands.
            }
        }

        private static IntPtr Resolve(string name, Assembly requester, DllImportSearchPath? path)
        {
            string module = ShimLibrary.Normalize(name);
            if (s_modules.TryGetValue(module, out var m) && m.Shim != IntPtr.Zero)
            {
                CompatLog.Once("dispatch:" + module + ":" + requester.GetName().Name, $"win32 dispatch: {requester.GetName().Name} binds {name} through the stack");
                return m.Shim;
            }
            return IntPtr.Zero;                  // the default search, as without a resolver
        }

        /// <summary>Whether the assembly P/Invokes one of the covered DLLs, and whether it calls
        /// NativeLibrary.SetDllImportResolver itself (read from its metadata, without loading it again).</summary>
        private static bool ImportsCoveredModule(string path, out bool ownResolver)
        {
            ownResolver = false;
            try
            {
                using var fs = File.OpenRead(path);
                using var pe = new PEReader(fs);
                if (!pe.HasMetadata) return false;
                MetadataReader md = pe.GetMetadataReader();
                bool imports = false;
                for (int row = 1, rows = md.GetTableRowCount(System.Reflection.Metadata.Ecma335.TableIndex.ModuleRef); row <= rows; row++)
                {
                    var h = System.Reflection.Metadata.Ecma335.MetadataTokens.ModuleReferenceHandle(row);
                    string name = ShimLibrary.Normalize(md.GetString(md.GetModuleReference(h).Name));
                    if (s_modules.ContainsKey(name)) { imports = true; break; }
                }
                if (!imports) return false;
                foreach (var h in md.MemberReferences)
                {
                    if (md.StringComparer.Equals(md.GetMemberReference(h).Name, "SetDllImportResolver")) { ownResolver = true; break; }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
