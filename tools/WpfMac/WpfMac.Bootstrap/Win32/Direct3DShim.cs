// The Direct3D shim's exports (d3d11, dxgi, d3d9, and d3dcompiler_47's), routed to their managed
// implementation in the stack's WebGPU interop (Microsoft.Wpf.Interop.WebGpu, Direct3D/Exports.cs; design in
// Documentation/d3d11-webgpu-shim.md). The bootstrap does not reference it, so it is reached by name.
//
// One implementation on every OS (the user's decision, 2026-10-19): every app assembly that P/Invokes these
// DLLs gets the shim -- off Windows through libwin32shim (ShimLibrary), on Windows through the generated
// mirror images (WindowsShim), even though the real Direct3D is there. WPF_D3D11_SHIM narrows it, for
// diagnosis and as the test oracle (the real Direct3D to compare against), not as a product mode:
//
//   unset / all / 1 every app assembly
//   0 / off         nothing on Windows (the real Direct3D); off Windows the DLLs then stay unimplemented
//   A*;B;C*         those assemblies
//
// The stack's own assemblies never go through this: they reach the shim's objects through their
// vtables, never through the exports.

using System.Reflection;

namespace WpfMac.Bootstrap.Win32
{
    internal static class Direct3DShim
    {
        private const string DefaultSpec = "all";
        private static string[] s_patterns = Array.Empty<string>();
        private static bool s_all;

        /// <summary>Whether WPF_D3D11_SHIM turns the shim off.</summary>
        public static bool Disabled
        {
            get
            {
                string? spec = Environment.GetEnvironmentVariable("WPF_D3D11_SHIM");
                return spec == "0" || string.Equals(spec, "off", StringComparison.OrdinalIgnoreCase) || string.Equals(spec, "false", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>Whether the assembly named <paramref name="name"/> is redirected (Windows).</summary>
        public static bool Covers(string? name)
        {
            if (s_all) return true;
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string p in s_patterns)
            {
                bool match = p.EndsWith('*')
                    ? name.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
                    : string.Equals(name, p, StringComparison.OrdinalIgnoreCase);
                if (match) return true;
            }
            return false;
        }

        /// <summary>Registers every export the shim implements through <paramref name="add"/>
        /// (module, export name, function pointer). The number registered; 0 when off or missing.</summary>
        public static int Register(Action<string, string, IntPtr> add)
        {
            if (Disabled)
            {
                CompatLog.Info("d3d11 shim: off (WPF_D3D11_SHIM)");
                return 0;
            }
            string spec = Environment.GetEnvironmentVariable("WPF_D3D11_SHIM") is { Length: > 0 } s ? s.Trim() : DefaultSpec;
            s_all = string.Equals(spec, "all", StringComparison.OrdinalIgnoreCase) || spec == "1";
            s_patterns = spec.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            try
            {
                Assembly asm = Assembly.Load(new AssemblyName("Microsoft.Wpf.Interop.WebGpu"));
                MethodInfo? register = asm.GetType("Microsoft.Wpf.Interop.WebGpu.Direct3D.Exports")
                    ?.GetMethod("Register", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (register == null)
                {
                    CompatLog.Info("d3d11 shim: this WebGPU interop has no Direct3D exports");
                    return 0;
                }
                int n = 0;
                Action<string, string, IntPtr> counting = (m, e, fn) => { n++; add(m, e, fn); };
                register.Invoke(null, new object[] { counting, (Action<string, string>)CompatLog.Once });
                string who = !OperatingSystem.IsWindows() ? "" : s_all ? ", for every app assembly" : ", for " + string.Join(";", s_patterns);
                CompatLog.Info($"d3d11 shim: {n} exports{who}");
                return n;
            }
            catch (Exception e)
            {
                CompatLog.Info($"d3d11 shim: did not load: {e.GetBaseException().Message}");
                return 0;
            }
        }
    }
}
