// The processors a program of the installation runs as on Windows. What the program is built for is its assembly's
// (the .dll), not its executable's: an x64 apphost may start an AnyCPU assembly (PlatformTarget AnyCPU, published for
// win-x64), which runs as well on Arm or as x86. So an AnyCPU program is offered as each; IL the SDK marked for the
// RID it was published for (x64, arm64) as that, and as the other 64-bit processor (the loader takes it as AnyCPU);
// an x86 one (32-bit required) as x86. The native libraries beside it narrow that: a native DLL
// there with no build for a processor (a runtimes\win-<rid>\native twin) keeps it from being the default. Windows on
// Arm runs arm64, x64 and x86 programs; Windows x64 x64 and x86 ones. The installer carries a runtime per processor
// its programs run as.

using System.Reflection.PortableExecutable;

namespace WpfWebGpu.Packager;

public static class Processors
{
    /// <summary>Preference, where a program could run as several: the machine's own first, then emulated.</summary>
    private static readonly Processor[] s_order = { Processor.Arm64, Processor.X64, Processor.X86 };

    public static string Rid(Processor p) => p switch { Processor.Arm64 => "win-arm64", Processor.X64 => "win-x64", _ => "win-x86" };

    public static Processor? FromRid(string rid) => rid switch { "win-arm64" => Processor.Arm64, "win-x64" => Processor.X64, "win-x86" => Processor.X86, _ => null };

    public static string Name(Processor p) => p.ToString().ToLowerInvariant();

    /// <summary>What a machine of <paramref name="machine"/> runs: its own, and what it emulates.</summary>
    public static IReadOnlyList<Processor> RunnableOn(Processor machine) => machine switch
    {
        Processor.Arm64 => s_order,
        Processor.X64 => new[] { Processor.X64, Processor.X86 },
        _ => new[] { Processor.X86 },
    };

    /// <summary>The processors <paramref name="app"/> is installed for on a <paramref name="machine"/> machine, its
    /// default first.</summary>
    public static List<Processor> On(AppEntry app, Processor machine)
    {
        var runnable = RunnableOn(machine);
        var included = s_order.Where(p => app.Has(p) && runnable.Contains(p)).ToList();
        Processor first = Default(app, machine);
        included.Remove(first);
        included.Insert(0, first);
        return included;
    }

    /// <summary>The processor <paramref name="app"/> runs as by default on a <paramref name="machine"/> machine: its
    /// default where the machine runs it, else the first of its others it does, else the machine's own.</summary>
    public static Processor Default(AppEntry app, Processor machine)
    {
        var runnable = RunnableOn(machine);
        if (app.Processor is { } chosen && runnable.Contains(chosen)) return chosen;
        // an arm64 default on an x64 machine: the same program, AnyCPU, as x64
        foreach (Processor p in runnable) if (app.Has(p)) return p;
        return machine;
    }

    /// <summary>What the scanner suggests for the program whose assembly is <paramref name="dll"/>: the processors it
    /// is included for, and its default.</summary>
    public static (List<Processor> Included, Processor Default) Suggest(string dll)
    {
        var pe = PeFile.TryRead(dll);
        List<Processor> possible =
            pe == null ? new() { Processor.X64 }
            : pe.Requires32Bit ? new() { Processor.X86 }
            // IL the SDK marked for the RID it was published for: the other 64-bit processor runs it too (the loader
            // takes it as AnyCPU), its own first
            : pe.Machine == Machine.Arm64 && pe.ILOnly ? new() { Processor.Arm64, Processor.X64 }
            : pe.Machine == Machine.Amd64 && pe.ILOnly ? new() { Processor.X64, Processor.Arm64 }
            : pe.Machine == Machine.Arm64 ? new() { Processor.Arm64 }
            : pe.Machine == Machine.Amd64 ? new() { Processor.X64 }
            : new() { Processor.Arm64, Processor.X64, Processor.X86 };   // AnyCPU (IL only, i386, no 32-bit required)

        // the native code beside it: each processor it has a build for
        var natives = NativeMachines(Path.GetDirectoryName(Path.GetFullPath(dll))!);
        var fits = possible.Where(p => natives.All(n => n.Value.Contains(p))).ToList();
        Processor first = fits.Count > 0 ? fits[0] : possible[0];
        // AnyCPU: the machine's own and x64 to fall back on (x86 where it is what the native code is built for)
        var included = possible.Count > 1 ? new List<Processor> { Processor.Arm64, Processor.X64 } : new List<Processor>(possible);
        if (!included.Contains(first)) included.Add(first);
        included.Sort();
        return (included, first);
    }

    /// <summary>Each native DLL beside the program (and each mixed-mode assembly: native code too), with the
    /// processors it has a build for: its own, and its runtimes\win-&lt;rid&gt;\native twins.</summary>
    internal static Dictionary<string, HashSet<Processor>> NativeMachines(string dir)
    {
        var result = new Dictionary<string, HashSet<Processor>>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in Directory.EnumerateFiles(dir, "*.dll"))
        {
            if (PeFile.TryRead(f) is not { } pe || (pe.Managed && pe.ILOnly)) continue;
            if (FromMachine(pe.Machine) is not { } own) continue;
            string name = Path.GetFileName(f);
            if (s_helpers.Contains(Path.GetFileNameWithoutExtension(name))) continue;
            var set = new HashSet<Processor> { own };
            foreach (Processor p in s_order)
                if (File.Exists(Path.Combine(dir, "runtimes", Rid(p), "native", name))) set.Add(p);
            result[name] = set;
        }
        return result;
    }

    private static Processor? FromMachine(Machine m) => m switch
    {
        Machine.Arm64 => Processor.Arm64,
        Machine.Amd64 => Processor.X64,
        Machine.I386 => Processor.X86,
        _ => null,
    };

    // the .NET host's and WPF's own native pieces an app folder may carry: the runtime brings its own
    private static readonly HashSet<string> s_helpers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ijwhost", "hostfxr", "hostpolicy", "coreclr", "clrjit", "Microsoft.DiaSymReader.Native.amd64",
        "D3DCompiler_47_cor3", "PenImc_cor3", "PresentationNative_cor3", "vcruntime140_cor3", "wpfgfx_cor3",
    };
}
