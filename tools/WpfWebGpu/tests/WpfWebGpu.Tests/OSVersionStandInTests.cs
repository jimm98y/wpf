// The stand-in for Environment.OSVersion in a bundle (WpfWebGpu.ComInterop/BrowserApis.cs): what .NET
// on Windows answers, there and off it alike.

using WpfWebGpu.ComInterop;
using Xunit;

namespace WpfWebGpu.Tests;

public sealed class OSVersionStandInTests
{
    [Fact]
    public void OSVersion_IsWindows11()
    {
        OperatingSystem os = BrowserApis.Environment_get_OSVersion();
        Assert.Equal(PlatformID.Win32NT, os.Platform);
        if (OperatingSystem.IsWindows()) Assert.Equal(Environment.OSVersion.Version, os.Version);
        else Assert.Equal(new Version(10, 0, 22631, 0), os.Version);
    }
}
