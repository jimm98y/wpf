using System.Reflection;
using Xunit;

namespace WpfWebGpu.Tests;

// kernel32's disk space queries in the stack's user-mode Windows (WpfWebGpu.Win32/Kernel32.Disk.cs).
public unsafe class DiskSpaceExportTests
{
    private static MethodInfo Export(string name) => Win32Exports.Method(name);

    [Fact]
    public void A_directory_s_disk_reports_its_size_and_free_space_and_a_missing_one_is_refused()
    {
        string dir = TestEnvironment.NewDirectory("disk");
        ulong free = 0, total = 0, totalFree = 0;
        fixed (char* p = dir)
            Assert.Equal(1, (int)Export("GetDiskFreeSpaceExW").Invoke(null, new object[]
            {
                Pointer.Box(p, typeof(char*)), Pointer.Box(&free, typeof(ulong*)), Pointer.Box(&total, typeof(ulong*)), Pointer.Box(&totalFree, typeof(ulong*)),
            })!);
        var drive = new DriveInfo(dir);
        Assert.Equal((ulong)drive.TotalSize, total);
        Assert.True(free > 0 && free <= totalFree && totalFree <= total);

        uint sectors = 0, bytes = 0, freeClusters = 0, clusters = 0;
        fixed (char* p = dir)
            Assert.Equal(1, (int)Export("GetDiskFreeSpaceW").Invoke(null, new object[]
            {
                Pointer.Box(p, typeof(char*)), Pointer.Box(&sectors, typeof(uint*)), Pointer.Box(&bytes, typeof(uint*)),
                Pointer.Box(&freeClusters, typeof(uint*)), Pointer.Box(&clusters, typeof(uint*)),
            })!);
        Assert.Equal((8u, 512u), (sectors, bytes));
        Assert.Equal((uint)Math.Min(total / 4096, uint.MaxValue), clusters);

        fixed (char* p = Path.Combine(dir, "no-such-folder"))
            Assert.Equal(0, (int)Export("GetDiskFreeSpaceExW").Invoke(null, new object[]
            {
                Pointer.Box(p, typeof(char*)), Pointer.Box(null, typeof(ulong*)), Pointer.Box(null, typeof(ulong*)), Pointer.Box(null, typeof(ulong*)),
            })!);
    }
}
