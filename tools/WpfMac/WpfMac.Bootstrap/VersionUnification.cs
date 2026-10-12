// Binds an assembly reference to the copy that IS in the process when the versions or keys disagree.
//
// A .NET 10 app is compiled against Microsoft.WindowsDesktop.App, so it names, for example,
// System.Windows.Forms 10.0.0.0 and WindowsFormsIntegration 10.0.0.0 (token 31bf3856ad364e35). The
// WebGPU stack carries the same assemblies under the identities .NET Framework libraries expect
// (System.Windows.Forms 4.0.0.0, WindowsFormsIntegration with the ECMA key), and changing those would
// break every app that is rebuilt against the stack. The default binder refuses a copy whose version is
// LOWER than the reference, and then raises Resolving; this answers it with the copy the deps.json
// already put on the trusted list, whatever its version.
//
// It only ever fires after the binder has failed, so it cannot change what an app that binds normally
// gets. It does not invent assemblies either: a name that is not on the trusted list stays a failure.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace WpfMac.Bootstrap
{
    internal static class VersionUnification
    {
        private static Dictionary<string, string>? s_tpa;
        private static readonly ConcurrentDictionary<string, Assembly?> s_resolved = new(StringComparer.OrdinalIgnoreCase);

        [ThreadStatic] private static HashSet<string>? t_inProgress;

        public static void Install(AssemblyLoadContext alc) => alc.Resolving += OnResolving;

        private static Assembly? OnResolving(AssemblyLoadContext alc, AssemblyName name)
        {
            if (string.IsNullOrEmpty(name.Name) || !string.IsNullOrEmpty(name.CultureName))
            {
                return null;                    // satellite assemblies have their own fallback
            }
            if (!Tpa().ContainsKey(name.Name))
            {
                return null;                    // not ours to answer: a genuine missing assembly
            }

            t_inProgress ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!t_inProgress.Add(name.Name))
            {
                return null;                    // the simple-name load below failed too; do not recurse
            }

            try
            {
                Assembly? asm = s_resolved.GetOrAdd(name.Name, n =>
                {
                    try { return alc.LoadFromAssemblyName(new AssemblyName(n)); }
                    catch (Exception e) { CompatLog.Info($"unify {n}: {e.GetType().Name}: {e.Message}"); return null; }
                });
                if (asm != null)
                {
                    CompatLog.Once($"unify:{name.FullName}", $"unified {name.FullName} -> {asm.GetName().FullName}");
                }
                return asm;
            }
            finally
            {
                t_inProgress.Remove(name.Name);
            }
        }

        private static Dictionary<string, string> Tpa()
        {
            if (s_tpa != null) return s_tpa;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string list)
            {
                foreach (string path in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    map.TryAdd(Path.GetFileNameWithoutExtension(path), path);
                }
            }
            return s_tpa = map;
        }
    }
}
