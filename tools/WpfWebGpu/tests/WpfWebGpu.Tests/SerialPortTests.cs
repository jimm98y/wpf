using System.IO.Ports;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WpfWebGpu.Detours;
using Xunit;

namespace WpfWebGpu.Tests;

/// <summary>Serial ports by their Windows names (WpfWebGpu.Detours/SerialPorts.cs): each device given a COM number
/// once and keeping it, the present ones listed, and COMn opened on its device by the app's own System.IO.Ports.</summary>
[Collection("serial ports")]     // the machine's COM names are the registry's
public class SerialPortTests
{
    private static void ClearNames()
    {
        Registry.LocalMachine.DeleteSubKeyTree(@"SYSTEM\CurrentControlSet\Control\COM Name Arbiter", throwOnMissingSubKey: false);
        Registry.LocalMachine.DeleteSubKeyTree(@"HARDWARE\DEVICEMAP\SERIALCOMM", throwOnMissingSubKey: false);
    }

    [Fact]
    public void A_device_keeps_the_COM_number_it_was_first_given()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows names its ports itself");
        ClearNames();
        Assert.Equal(new[] { "COM1", "COM2" }, SerialPorts.Assign(new[] { "/dev/fakeB", "/dev/fakeA" }));
        Assert.Equal(new[] { "COM2" }, SerialPorts.Assign(new[] { "/dev/fakeB" }));                 // A unplugged
        Assert.Equal(new[] { "COM1", "COM3" }, SerialPorts.Assign(new[] { "/dev/fakeC", "/dev/fakeA" }));
        using (var comm = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM")!)
            Assert.Equal(new[] { ("/dev/fakeA", "COM1"), ("/dev/fakeC", "COM3") },
                comm.GetValueNames().OrderBy(n => n, StringComparer.Ordinal).Select(n => (n, (string)comm.GetValue(n)!)));
        Assert.Equal((3, 0, 0), (SerialPorts.ComNumber("com3"), SerialPorts.ComNumber("COM0"), SerialPorts.ComNumber("/dev/ttyS0")));
        ClearNames();
    }

    [DllImport("libc", SetLastError = true)] private static extern int openpty(out int master, out int slave, IntPtr name, IntPtr termp, IntPtr winp);
    [DllImport("libc")] private static extern IntPtr ttyname(int fd);
    [DllImport("libc")] private static extern nint write(int fd, byte[] buffer, nint count);
    [DllImport("libc")] private static extern nint read(int fd, byte[] buffer, nint count);
    [DllImport("libc")] private static extern int close(int fd);

    [Fact]
    public void COMn_opens_its_device_and_the_port_keeps_its_name()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "a pseudo-terminal through the package's own native library: macOS here");
        ClearNames();
        WpfWebGpu.Detours.Detours.InstallSerialPorts();
        Assert.Equal(0, openpty(out int master, out int slave, 0, 0, 0));
        try
        {
            string device = Marshal.PtrToStringUTF8(ttyname(slave))!;
            close(slave);
            using (var arbiter = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\COM Name Arbiter\Devices"))
                arbiter.SetValue("COM9", device);

            using var port = new SerialPort("COM9", 9600) { ReadTimeout = 5000, NewLine = "\n" };
            port.Open();
            Assert.Equal("COM9", port.PortName);
            write(master, "hello\n"u8.ToArray(), 6);
            Assert.Equal("hello", port.ReadLine());
            port.Write("back");
            var buffer = new byte[16];
            Thread.Sleep(100);
            Assert.Equal("back", System.Text.Encoding.ASCII.GetString(buffer, 0, (int)read(master, buffer, 16)));
            port.Close();

            // a name no device has, as Windows fails it
            var missing = Assert.Throws<IOException>(() => new SerialPort("COM5").Open());
            Assert.Equal("The port 'COM5' does not exist.", missing.Message);
        }
        finally { close(master); ClearNames(); }
    }
}
