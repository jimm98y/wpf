using System.Diagnostics;
using Xunit;

namespace WpfWebGpu.Tests;

// The runtime detours (WpfWebGpu.Detours), each installed alone: the others change the whole test process.
public class DetourTests
{
    [Fact]
    public void A_process_s_affinity_is_every_processor_until_set_and_a_set_one_is_kept()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "the detour is macOS's: Windows and Linux have affinity of their own");
        WpfWebGpu.Detours.Detours.InstallProcessorAffinity();

        using var self = Process.GetCurrentProcess();
        long all = Environment.ProcessorCount >= 64 ? -1 : (long)((1UL << Environment.ProcessorCount) - 1);
        Assert.Equal(all, self.ProcessorAffinity.ToInt64());

        self.ProcessorAffinity = new IntPtr(1);
        using (var again = Process.GetProcessById(self.Id)) Assert.Equal(1, again.ProcessorAffinity.ToInt64());

        // as SetProcessAffinityMask: no processor, or one the machine lacks, is an invalid parameter
        Assert.Equal(87, Assert.Throws<System.ComponentModel.Win32Exception>(() => self.ProcessorAffinity = IntPtr.Zero).NativeErrorCode);
        if (Environment.ProcessorCount < 63)
            Assert.Throws<System.ComponentModel.Win32Exception>(() => self.ProcessorAffinity = new IntPtr(1L << 62));
        self.ProcessorAffinity = new IntPtr(all);
    }
}
