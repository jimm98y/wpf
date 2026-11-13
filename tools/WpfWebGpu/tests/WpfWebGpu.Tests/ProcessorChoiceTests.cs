using Mono.Cecil;
using WpfWebGpu.Packager;
using Xunit;

namespace WpfWebGpu.Tests;

// The processors a program is installed for on Windows (WpfWebGpu.Packager/Processors.cs): what its assembly is built
// for, not its apphost; native code beside it narrowing the default; each Setup's machine running its own and what it
// emulates.
public class ProcessorChoiceTests
{
    private static string Assembly(string dir, string name, TargetArchitecture architecture, ModuleAttributes attributes = ModuleAttributes.ILOnly)
    {
        string file = Path.Combine(dir, name + ".dll");
        Directory.CreateDirectory(dir);
        using (var asm = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0)), name, ModuleKind.Dll))
        {
            asm.MainModule.Architecture = architecture;
            asm.MainModule.Attributes = attributes | ModuleAttributes.ILOnly;
            asm.Write(file);
        }
        // native code: its CLI header's IL-only flag cleared, as a mixed-mode assembly's (which Cecil does not write)
        if ((attributes & ModuleAttributes.ILOnly) == 0)
        {
            byte[] image = File.ReadAllBytes(file);
            int flags;
            using (var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(image)))
                flags = pe.PEHeaders.CorHeaderStartOffset + 16;
            image[flags] &= 0xFE;
            File.WriteAllBytes(file, image);
        }
        return file;
    }

    [Fact]
    public void A_program_is_offered_as_its_assembly_is_built()
    {
        string dir = TestEnvironment.NewDirectory("processors");
        // AnyCPU (an x64 apphost or not): the machine's own, and x64 to fall back on
        var (included, first) = Processors.Suggest(Assembly(Path.Combine(dir, "any"), "Any", TargetArchitecture.I386));
        Assert.Equal(new[] { Processor.Arm64, Processor.X64 }, included);
        Assert.Equal(Processor.Arm64, first);
        // IL marked x64 (published for win-x64): x64, and arm64 its IL runs as too
        (included, first) = Processors.Suggest(Assembly(Path.Combine(dir, "x64"), "X64", TargetArchitecture.AMD64));
        Assert.Equal(new[] { Processor.Arm64, Processor.X64 }, included);
        Assert.Equal(Processor.X64, first);
        // IL marked arm64
        (included, first) = Processors.Suggest(Assembly(Path.Combine(dir, "arm64"), "Arm", TargetArchitecture.ARM64));
        Assert.Equal(new[] { Processor.Arm64, Processor.X64 }, included);
        Assert.Equal(Processor.Arm64, first);
        // 32-bit required
        (included, first) = Processors.Suggest(Assembly(Path.Combine(dir, "x86"), "X86", TargetArchitecture.I386, ModuleAttributes.ILOnly | ModuleAttributes.Required32Bit));
        Assert.Equal(new[] { Processor.X86 }, included);
        Assert.Equal(Processor.X86, first);
    }

    [Fact]
    public void Native_code_beside_it_with_no_arm64_build_makes_x64_the_default()
    {
        string dir = TestEnvironment.NewDirectory("processors-native");
        string app = Assembly(dir, "App", TargetArchitecture.I386);
        // native code for x64 alone (a mixed-mode assembly: what PeFile reads as native code)
        Assembly(dir, "Native", TargetArchitecture.AMD64, attributes: 0);
        var (included, first) = Processors.Suggest(app);
        Assert.Equal(Processor.X64, first);
        Assert.Equal(new[] { Processor.Arm64, Processor.X64 }, included);
        // with an arm64 build of it under runtimes\win-arm64\native, the machine's own again
        string twin = Path.Combine(dir, "runtimes", "win-arm64", "native");
        Directory.CreateDirectory(twin);
        File.Copy(Path.Combine(dir, "Native.dll"), Path.Combine(twin, "Native.dll"));
        Assert.Equal(Processor.Arm64, Processors.Suggest(app).Default);
    }

    [Fact]
    public void Each_machine_runs_its_own_and_what_it_emulates()
    {
        var anyCpu = new AppEntry { Processors = { Processor.Arm64, Processor.X64 }, Processor = Processor.Arm64 };
        Assert.Equal(new[] { Processor.Arm64, Processor.X64 }, Processors.On(anyCpu, Processor.Arm64));
        // Windows x64 runs no arm64: the same AnyCPU program as x64
        Assert.Equal(new[] { Processor.X64 }, Processors.On(anyCpu, Processor.X64));
        // x64 by default, arm64 to fall back on
        var x64First = new AppEntry { Processors = { Processor.Arm64, Processor.X64 }, Processor = Processor.X64 };
        Assert.Equal(new[] { Processor.X64, Processor.Arm64 }, Processors.On(x64First, Processor.Arm64));
        // a 32-bit program: x86 on either
        var x86 = new AppEntry { Processors = { Processor.X86 }, Processor = Processor.X86 };
        Assert.Equal(new[] { Processor.X86 }, Processors.On(x86, Processor.Arm64));
        Assert.Equal(new[] { Processor.X86 }, Processors.On(x86, Processor.X64));
        // a manifest from before: the machine's own
        Assert.Equal(new[] { Processor.X64 }, Processors.On(new AppEntry(), Processor.X64));
        // the Packager app's check boxes: unticking the default moves it
        x64First.X64 = false;
        Assert.Equal(Processor.Arm64, x64First.Processor);
        Assert.False(x64First.Has(Processor.X64));
    }
}
