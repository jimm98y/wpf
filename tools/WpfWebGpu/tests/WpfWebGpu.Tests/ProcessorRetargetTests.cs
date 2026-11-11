using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Mono.Cecil;
using WpfWebGpu.ComRewrite;
using Xunit;

namespace WpfWebGpu.Tests;

// An app published for one 64-bit processor (dotnet publish -r win-x64 marks its IL AMD64), run on the other: the
// runtime refuses to load it as it is; the loader writes it AnyCPU, as Windows on ARM would run it emulated.
public class ProcessorRetargetTests
{
    [Fact]
    public void IL_marked_for_the_other_processor_is_loaded_as_AnyCPU()
    {
        var other = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? TargetArchitecture.ARM64 : TargetArchitecture.AMD64;
        string dir = TestEnvironment.NewDirectory("retarget");
        string file = Path.Combine(dir, "Marked.dll");
        using (var asm = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Marked", new Version(1, 0)), "Marked", ModuleKind.Dll))
        {
            asm.MainModule.Architecture = other;
            asm.Write(file);
        }
        var scan = AssemblyScan.Read(file)!;
        Assert.True(AssemblyScan.ForOtherProcessor(scan.Machine));
        Assert.False(AssemblyScan.ForOtherProcessor(0x14C));                 // AnyCPU (I386, IL only): runs anywhere

        var (image, _, result) = ComRewriter.RewriteToMemory(file, new[] { dir }, com: false);
        Assert.True(result.Retargeted);
        using var pe = new PEReader(image);
        Assert.Equal(Machine.I386, pe.PEHeaders.CoffHeader.Machine);
        Assert.True((pe.PEHeaders.CorHeader!.Flags & CorFlags.ILOnly) != 0);
        Assert.True((pe.PEHeaders.CorHeader.Flags & CorFlags.Requires32Bit) == 0);
    }
}
