using Xunit;

namespace WpfMac.Tests;

/// <summary>
/// The libwin32shim manifests that build.sh writes beside the overlay: what P/Invokes resolve to on
/// macOS. A P/Invoke resolves when its DLL is one the shim stands in for and the shim exports the
/// name (exports are by name; the DLL each is filed under is only a label). Checked when a macOS
/// overlay has been built.
/// </summary>
public class GeneratedExportsTests
{
    /// <summary>(exported names, DLL names that resolve to the shim); null when no overlay is built.</summary>
    private static (HashSet<string> Names, HashSet<string> Modules)? Exports()
    {
        string? repo = AppContext.BaseDirectory;
        while (repo != null && !Directory.Exists(Path.Combine(repo, "shims"))) repo = Path.GetDirectoryName(repo);
        if (repo == null) return null;
        string? manifest = Directory.Exists(Path.Combine(repo, "artifacts", "wpfmac"))
            ? Directory.EnumerateFiles(Path.Combine(repo, "artifacts", "wpfmac"), "win32shim_exports.txt", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (manifest == null) return null;
        var names = File.ReadLines(manifest).Select(l => l[(l.IndexOf('\t') + 1)..]).ToHashSet(StringComparer.Ordinal);
        var modules = File.ReadLines(Path.Combine(Path.GetDirectoryName(manifest)!, "win32shim_modules.txt")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (names, modules);
    }

    [Theory]
    [InlineData("user32\tGetWindowLongW")]
    [InlineData("user32\tGetWindowLong")]          // undecorated: macOS does not try the W/A suffix
    [InlineData("user32\tkeybd_event")]            // lowercase, yet not a CRT function
    [InlineData("kernel32\tGetTickCount")]
    [InlineData("ntdll\tRtlGetVersion")]           // documented in the WDK, not the SDK metadata
    [InlineData("shcore\tGetDpiForMonitor")]       // filed under an API set in the metadata
    [InlineData("uxtheme\t#135")]                  // by ordinal
    [InlineData("winmm\tsndPlaySoundW")]
    public void Exports_what_apps_call(string entry)
    {
        var exports = Exports();
        Assert.SkipWhen(exports == null, "no macOS overlay built (tools/WpfMac/build.sh)");
        string[] parts = entry.Split('\t');
        Assert.Contains(parts[0], exports!.Value.Modules);      // the DLL resolves to libwin32shim
        Assert.Contains(parts[1], exports.Value.Names);         // which exports the function by name
    }
}
