using WpfWebGpu.Packager;
using Xunit;

namespace WpfWebGpu.Tests;

public class PackagerTests
{
    private const string Reg = """
        Windows Registry Editor Version 5.00

        [HKEY_LOCAL_MACHINE\SOFTWARE\Vendor\Product\Launch]
        "Tool"="C:\\Program Files\\Vendor\\Product\\Clients\\Tool\\Tool.exe"
        "Cmd"="\"C:\\PROGRAM FILES\\Vendor\\Product\\Clients\\Tool\\Tool.exe\" /x"
        "Data"="C:\\ProgramData\\Vendor\\Data"
        "Other"="C:\\Program Files\\Vendor\\ProductX\\a.exe"
        "List"=hex(7):43,00,3a,00,5c,00,50,00,72,00,6f,00,67,00,72,00,61,00,6d,00,44,00,61,00,74,00,61,00,00,00,00,00

        [HKEY_LOCAL_MACHINE\SOFTWARE\Vendor\Gone]
        "x"=dword:00000001

        [-HKEY_LOCAL_MACHINE\SOFTWARE\Vendor\Gone]
        """;

    private static string Install()
    {
        string dir = TestEnvironment.NewDirectory("install");
        Directory.CreateDirectory(Path.Combine(dir, "Clients", "Tool"));
        File.WriteAllText(Path.Combine(dir, "Clients", "Tool", "Tool.exe"), "");
        return dir;
    }

    [Fact]
    public void Finds_the_installation_s_folder_in_the_registry_s_paths()
    {
        var mappings = Scanner.DetectMappings(RegText.Parse(Reg), Install());
        Assert.Equal(@"C:\Program Files\Vendor\Product", mappings[0].From, ignoreCase: true);
        Assert.Equal(PathTokens.InstallDir, mappings[0].To);
        Assert.Contains(mappings, m => m.From == @"C:\ProgramData" && m.To == PathTokens.ProgramData);
        Assert.DoesNotContain(mappings, m => m.From.Contains("ProductX"));
    }

    [Fact]
    public void Maps_whole_path_segments_ignoring_case()
    {
        var mapper = new PathMapper(new[]
        {
            new PathMapping { From = @"C:\Program Files\Vendor\Product", To = PathTokens.InstallDir },
            new PathMapping { From = @"C:\ProgramData", To = PathTokens.ProgramData },
        });
        Assert.Equal(PathTokens.InstallDir + @"\a.exe", mapper.Map(@"c:\program files\vendor\product\a.exe"));
        Assert.Equal("\"" + PathTokens.InstallDir + "\\a.exe\" /x", mapper.Map("\"C:\\Program Files\\Vendor\\Product\\a.exe\" /x"));
        Assert.Equal(@"C:\Program Files\Vendor\ProductX\a.exe", mapper.Map(@"C:\Program Files\Vendor\ProductX\a.exe"));
        Assert.Equal(PathTokens.ProgramData, mapper.Map(@"C:\ProgramData"));
    }

    [Fact]
    public void Stages_the_registry_as_the_stand_in_s_hive_files()
    {
        string install = Install(), work = TestEnvironment.NewDirectory("work");
        string reg = Path.Combine(work, "in.reg");
        File.WriteAllText(reg, Reg);
        var manifest = new PackageManifest();
        manifest.Inputs.InstallDir = install;
        manifest.Inputs.Registry.Add(reg);
        manifest.PathMappings.AddRange(Scanner.DetectMappings(RegText.Parse(Reg), install));

        var staged = Staging.Stage(manifest, work);
        string hive = File.ReadAllText(Path.Combine(staged.RegistryDir, "HKEY_LOCAL_MACHINE.reg"));
        Assert.Contains("[SOFTWARE\\Vendor\\Product\\Launch]\n", hive);
        Assert.Contains("\"Tool\"=str:" + PathTokens.InstallDir + "\\Clients\\Tool\\Tool.exe\n", hive);
        Assert.Contains("\"Cmd\"=str:%22" + PathTokens.InstallDir + "\\Clients\\Tool\\Tool.exe%22 /x\n", hive);
        Assert.Contains("\"Data\"=str:" + PathTokens.ProgramData + "\\Vendor\\Data\n", hive);
        Assert.Contains("\"List\"=multi:" + PathTokens.ProgramData + "\n", hive);
        Assert.DoesNotContain("Gone", hive);

        string resolved = Staging.Resolve(hive, new Dictionary<string, string> { [PathTokens.InstallDir] = "/opt/p", [PathTokens.ProgramData] = "/var/p 100%" });
        Assert.Contains("\"Tool\"=str:/opt/p\\Clients\\Tool\\Tool.exe\n", resolved);
        Assert.Contains("\"Data\"=str:/var/p 100%25\\Vendor\\Data\n", resolved);
    }

    [Theory]
    [InlineData("**/Logs/**", "11/Logs/Traces/a.log", true)]
    [InlineData("**/Logs/**", "Logs/a.log", true)]
    [InlineData("**/Logs/**", "11/Catalogs/a.log", false)]
    [InlineData("**/*.dmp", "a/b/c.DMP", true)]
    [InlineData("Data/*.bin", "Data/x/y.bin", false)]
    public void Excludes_by_glob(string glob, string path, bool excluded) =>
        Assert.Equal(excluded, new GlobSet(new[] { glob }).Matches(path));

    [Fact]
    public void A_manifest_saves_and_loads_with_paths_relative_to_it()
    {
        string dir = TestEnvironment.NewDirectory("manifest");
        var m = new PackageManifest();
        m.Inputs.InstallDir = "install";
        m.Apps.Add(new AppEntry { Exe = "a/App.exe", Kind = AppKind.Service, Mobile = true });
        m.Output.Targets.Add(Target.Quest);
        string path = Path.Combine(dir, "x" + PackageManifest.Extension);
        m.Save(path);
        Assert.Contains("\"kind\": \"Service\"", File.ReadAllText(path));
        var back = PackageManifest.Load(path);
        Assert.Equal(Path.Combine(dir, "install"), back.Inputs.InstallDir);
        Assert.Equal(AppKind.Service, back.Apps[0].Kind);
        Assert.Equal(Target.Quest, back.Output.Targets[0]);
    }
}
