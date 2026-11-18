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

// Named events and semaphores (WpfWebGpu.Detours/NamedWaitHandles.cs): one object per name, whoever opens it. Installed
// with nothing else running (the wait core is patched; the bootstrap patches it before the app's threads start).
[Collection(ServiceDatabaseEnvironment.Name)]
public class NamedWaitHandleTests
{
    private static readonly object s_installed = Install();

    private static object Install()
    {
        if (!OperatingSystem.IsWindows())
            typeof(WpfWebGpu.Detours.NamedWaitHandles).GetMethod("Install", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, new object[] { TestEnvironment.NewDirectory("kobj") });
        return new object();
    }

    [Fact]
    public void A_named_event_is_one_object_for_every_handle_to_its_name()
    {
        _ = s_installed;
        string name = "Global\\test-event-" + Guid.NewGuid().ToString("N");
        using var a = new EventWaitHandle(false, EventResetMode.AutoReset, name, out bool createdA);
        using var b = new EventWaitHandle(true, EventResetMode.ManualReset, name, out bool createdB);
        Assert.True(createdA);
        Assert.False(createdB);                       // the existing one, its own initial state and mode kept
        Assert.False(b.WaitOne(50));
        var waiter = Task.Run(() => b.WaitOne(5000));
        Thread.Sleep(100);
        a.Set();
        Assert.True(waiter.Result);
        Assert.False(a.WaitOne(30));                  // auto-reset: the one waiter took it
        Assert.True(EventWaitHandle.TryOpenExisting(name, out EventWaitHandle? opened));
        using (opened) { opened!.Set(); Assert.True(a.WaitOne(1000)); }
        Assert.False(EventWaitHandle.TryOpenExisting(name + "-none", out _));
        Assert.Throws<WaitHandleCannotBeOpenedException>(() => new Semaphore(1, 1, name));
    }

    [Fact]
    public void A_named_semaphore_s_count_is_shared_and_its_maximum_kept()
    {
        _ = s_installed;
        string name = "test-semaphore-" + Guid.NewGuid().ToString("N");
        using var a = new Semaphore(1, 2, name, out bool created);
        using var b = Semaphore.OpenExisting(name);
        Assert.True(created);
        Assert.True(b.WaitOne(100));
        Assert.False(a.WaitOne(30));
        Assert.Equal(0, a.Release());
        Assert.Equal(1, b.Release());
        Assert.Throws<SemaphoreFullException>(() => a.Release());
    }
}
