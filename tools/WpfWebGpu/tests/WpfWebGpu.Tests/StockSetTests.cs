using WpfWebGpu.Audit;
using Xunit;

namespace WpfWebGpu.Tests;

public class StockSetTests
{
    private static AppFiles MakeApp(string dir, string frameworksJson)
    {
        Directory.CreateDirectory(Path.Combine(dir, "runtimes", "win", "lib", "net10.0"));
        File.WriteAllBytes(Path.Combine(dir, "App.dll"), new byte[] { 0 });
        File.WriteAllBytes(Path.Combine(dir, "Local.dll"), new byte[] { 0 });
        File.WriteAllBytes(Path.Combine(dir, "runtimes", "win", "lib", "net10.0", "Nested.dll"), new byte[] { 0 });
        File.WriteAllText(Path.Combine(dir, "App.runtimeconfig.json"), $$"""{ "runtimeOptions": { "tfm": "net10.0", "frameworks": {{frameworksJson}} } }""");
        return AppFiles.Resolve(Path.Combine(dir, "App.dll"));
    }

    private static LoadSet OverlayOf(params string[] names)
    {
        var load = new LoadSet();
        foreach (string n in names) load.Add(new LoadSet.Entry(n, n + ".dll", new Version(10, 0), "overlay"));
        return load;
    }

    private static string Temp() => Directory.CreateTempSubdirectory("wpfrun-stockset-").FullName;

    [Fact]
    public void Stock_finds_the_apps_own_files_and_its_frameworks_but_not_what_neither_ships()
    {
        string dir = Temp();
        try
        {
            var set = StockSet.Build(MakeApp(dir, $$"""[ { "name": "Microsoft.NETCore.App", "version": "{{Environment.Version.Major}}.0.0" } ]"""), OverlayOf());
            Assert.True(set.Known);
            Assert.True(set.Contains("Local"));
            Assert.True(set.Contains("Nested"));
            Assert.True(set.Contains("System.Runtime"));
            Assert.False(set.Contains("System.ServiceModel.Syndication.NotShippedAnywhere"));
            Assert.Contains(set.Frameworks, f => f.StartsWith("Microsoft.NETCore.App ", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WindowsDesktop_is_never_read_the_overlay_stands_in_for_it()
    {
        string dir = Temp();
        try
        {
            var set = StockSet.Build(MakeApp(dir, $$"""[ { "name": "Microsoft.NETCore.App", "version": "{{Environment.Version.Major}}.0.0" }, { "name": "Microsoft.WindowsDesktop.App", "version": "{{Environment.Version.Major}}.0.0" } ]"""),
                                     OverlayOf("PresentationFramework", "System.Windows.Forms"));
            Assert.True(set.Known);                              // on every OS, Windows included
            Assert.True(set.Contains("PresentationFramework"));  // from the overlay
            Assert.True(set.Contains("System.Windows.Forms"));
            // A member of the real framework the overlay was not given is not taken from the machine.
            Assert.False(set.Contains("PresentationCore"));
            Assert.Contains($"Microsoft.WindowsDesktop.App {Environment.Version.Major}.0.0 (as the overlay)", set.Frameworks);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_framework_not_installed_here_makes_stock_unknown()
    {
        string dir = Temp();
        try
        {
            var set = StockSet.Build(MakeApp(dir, """[ { "name": "Microsoft.NETCore.App", "version": "10.0.0" }, { "name": "Contoso.NotInstalled.App", "version": "99.0.0" } ]"""), OverlayOf());
            Assert.False(set.Known);
            Assert.Contains("Contoso.NotInstalled.App 99.0.0 (not installed here)", set.Frameworks);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
