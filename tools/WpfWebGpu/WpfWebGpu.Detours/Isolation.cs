// `wpfrun run --isolate` (for `wpfrun compare` on Windows): the WebGPU instance gets its own namespace
// of named kernel objects, so it can run beside the stock instance of the same app.
//
// Single-instance apps hold a named mutex (or event, or semaphore, or shared memory) and refuse to
// start when it already exists, so the second instance of a side-by-side comparison would exit. Every
// public constructor and Open/TryOpenExisting of those types that takes a name gets the name suffixed,
// at method entry, by one IL hook each -- whatever the overload, including ones added after this was
// written. Process-local objects (null names) are untouched.

using System.IO.MemoryMappedFiles;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;

namespace WpfWebGpu.Detours;

public static class Isolation
{
    private static readonly List<ILHook> s_hooks = new();
    private static string s_suffix = "";

    /// <summary>Suffixes every kernel object name with <paramref name="suffix"/>. Returns the count of methods hooked.</summary>
    public static int Install(string suffix, Action<string, string> logOnce)
    {
        s_suffix = suffix;
        MethodInfo rename = typeof(Isolation).GetMethod(nameof(Rename), BindingFlags.Public | BindingFlags.Static)!;
        int hooked = 0;
        foreach (Type type in new[] { typeof(Mutex), typeof(EventWaitHandle), typeof(Semaphore), typeof(MemoryMappedFile) })
        {
            IEnumerable<MethodBase> methods = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Concat<MethodBase>(type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name is "OpenExisting" or "TryOpenExisting" or "CreateNew" or "CreateOrOpen"));
            foreach (MethodBase method in methods)
            {
                ParameterInfo? name = method.GetParameters().FirstOrDefault(p => p.ParameterType == typeof(string) && p.Name is "name" or "mapName");
                if (name == null) continue;
                int index = name.Position + (method.IsStatic ? 0 : 1);
                try
                {
                    s_hooks.Add(new ILHook(method, il =>
                    {
                        var c = new ILCursor(il);
                        c.Emit(OpCodes.Ldarg, index);
                        c.Emit(OpCodes.Call, rename);
                        c.Emit(OpCodes.Starg, index);
                    }));
                    hooked++;
                }
                catch (Exception e)
                {
                    logOnce("isolate:" + method, $"isolate: {type.Name}.{method.Name}: {e.Message}");
                }
            }
        }
        return hooked;
    }

    public static string? Rename(string? name) => string.IsNullOrEmpty(name) ? name : name + s_suffix;
}
