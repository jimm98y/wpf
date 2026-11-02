// wpfrun bundle --visionos: the --ios bundle, as a visionOS app (the simulator's) rather than an iPad app
// run in its compatibility window.
//
// .NET ships no visionOS runtime, but needs none: visionOS is iOS's arm64 Darwin, with the same UIKit and
// Metal the iOS head uses, and its loader takes an iOS binary as its own once the binary says it is one.
// So the iOS simulator build is taken as it is and each Mach-O in it -- the host, Mono, the runtime's
// native libraries -- is marked for the visionOS simulator (LC_BUILD_VERSION platform 12), the bundle's
// Info.plist for Vision (device family 7, the XRSimulator platform), and every binary signed again, since
// the mark changes what was signed. A visionOS app is what can later open an immersive space for stereo
// content, which an iPad app in its compatibility window cannot.

using System.Diagnostics;

namespace WpfWebGpu;

internal static partial class Bundle
{
    private const int VisionOsSimulatorPlatform = 12;   // PLATFORM_XROS_SIMULATOR
    private const int VisionDeviceFamily = 7;

    private static int RetagForVisionOs(string appDir)
    {
        string sdk = Tool("xcrun", "--sdk", "xrsimulator", "--show-sdk-version").Trim();
        if (string.IsNullOrEmpty(sdk)) throw new UserError("no visionOS simulator SDK (xcrun --sdk xrsimulator); install the visionOS platform in Xcode.");
        string minimum = sdk.Split('.')[0] + ".0";

        var binaries = new List<string>();
        foreach (string file in Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories))
            if (IsMachO(file)) binaries.Add(file);
        foreach (string bin in binaries)
        {
            string retagged = bin + ".visionos";
            int code = Run("xcrun", "vtool", "-set-build-version", VisionOsSimulatorPlatform.ToString(), minimum, sdk, "-replace", "-output", retagged, bin);
            if (code != 0) return code;
            File.Move(retagged, bin, overwrite: true);
        }

        string plist = Path.Combine(appDir, "Info.plist");
        foreach (string command in new[]
        {
            "Delete :UIDeviceFamily", "Add :UIDeviceFamily array", $"Add :UIDeviceFamily:0 integer {VisionDeviceFamily}",
            "Delete :CFBundleSupportedPlatforms", "Add :CFBundleSupportedPlatforms array", "Add :CFBundleSupportedPlatforms:0 string XRSimulator",
            "Delete :MinimumOSVersion", $"Add :MinimumOSVersion string {minimum}",
            "Delete :DTPlatformName", "Add :DTPlatformName string xrsimulator",
        })
            Run("/usr/libexec/PlistBuddy", "-c", command, plist);   // a Delete of what is not there fails, and is meant to

        // the libraries first, the bundle (its executable and seal) last
        foreach (string bin in binaries.OrderByDescending(b => b.Length))
        {
            if (Path.GetDirectoryName(bin) == appDir && Path.GetExtension(bin) == "") continue;
            int code = Run("codesign", "--force", "--sign", "-", bin);
            if (code != 0) return code;
        }
        return Run("codesign", "--force", "--sign", "-", appDir);
    }

    private static bool IsMachO(string file)
    {
        try
        {
            using var s = File.OpenRead(file);
            Span<byte> magic = stackalloc byte[4];
            if (s.Read(magic) != 4) return false;
            uint m = BitConverter.ToUInt32(magic);
            return m is 0xFEEDFACF or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA;   // 64-bit Mach-O, either order; fat
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static int Run(string tool, params string[] args)
    {
        using var p = Process.Start(new ProcessStartInfo(tool, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && tool != "/usr/libexec/PlistBuddy") Console.Error.WriteLine($"wpfrun bundle: {tool} {string.Join(' ', args)}: {err.Trim()}");
        return tool == "/usr/libexec/PlistBuddy" ? 0 : p.ExitCode;
    }

    private static string Tool(string tool, params string[] args)
    {
        using var p = Process.Start(new ProcessStartInfo(tool, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output : "";
    }
}
