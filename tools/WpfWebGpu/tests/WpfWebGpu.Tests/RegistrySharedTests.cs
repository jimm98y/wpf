// The registry off Windows as every process's (FileRegistryStore): what another process writes -- here, its save of the
// hive's file -- is read without a new start, and a write of this process's keeps what the other wrote meanwhile, as one
// registry is every process's on Windows (http.sys reading the SSL binding a setup just made).

extern alias RegistryShim;

using RegistryShim::Microsoft.Win32;
using Xunit;

namespace WpfWebGpu.Tests;

public sealed class RegistrySharedTests
{
    [Fact]
    public void Another_process_s_write_is_read_and_kept()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the registry's file off Windows");
        string key = @"SOFTWARE\WpfWebGpuTests\Shared" + Guid.NewGuid().ToString("N");
        using (var k = Registry.LocalMachine.CreateSubKey(key)) k.SetValue("Mine", "1");

        // another process writes the hive: its file with a value of its own added
        string file = Path.Combine(Environment.GetEnvironmentVariable("WPFWEBGPU_REGISTRY_DIR")!, "HKEY_LOCAL_MACHINE.reg");
        string text = File.ReadAllText(file);
        Assert.Contains($"[{key}]", text);
        Thread.Sleep(20);   // a later modification time
        File.WriteAllText(file, text.Replace($"[{key}]\n", $"[{key}]\n\"Theirs\"=str:2\n"));
        Thread.Sleep(60);   // the change told (FSEvents, inotify), or past the 50 ms it is looked at in without them

        using (var k = Registry.LocalMachine.OpenSubKey(key))
            Assert.Equal("2", k!.GetValue("Theirs"));

        // a write of this process's: theirs kept
        using (var k = Registry.LocalMachine.CreateSubKey(key)) k.SetValue("Mine", "3");
        string after = File.ReadAllText(file);
        Assert.Contains("\"Theirs\"=str:2", after);
        Assert.Contains("\"Mine\"=str:3", after);
        Registry.LocalMachine.DeleteSubKeyTree(key, false);
    }
}
