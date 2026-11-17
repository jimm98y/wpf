// Where an application's Windows paths are off Windows: C:\Program Files\Vendor\... through the machine's
// mapping table, Windows' own folders as the stack's, anything else on a drive in the state's drive folder,
// \\server\share where the system has the share mounted.

using System.Reflection;
using WpfWebGpu.ComInterop;
using Xunit;

namespace WpfWebGpu.Tests;

[Collection("WindowsPathCore state")]
public sealed class WindowsDrivePathTests : IDisposable
{
    private static readonly Type Core = typeof(WindowsPaths).Assembly.GetType("WpfWebGpu.Paths.WindowsPathCore")!;
    private readonly string _state = TestEnvironment.NewDirectory("drivepaths");

    public WindowsDrivePathTests()
    {
        Core.GetField("StateOverride", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, _state);
    }

    public void Dispose()
    {
        Core.GetField("StateOverride", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
        Core.GetField("MountsOverride", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
    }

    private static string Native(string path) => WindowsPaths.Native(path)!;

    [Fact]
    public void TheMappingTableSaysWhereTheInstallationIs()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has its drives");
        string app = Path.Combine(_state, "installed", "app");
        File.WriteAllText(Path.Combine(_state, "paths.json"),
            $$"""[{"from": "C:\\Program Files\\Vendor", "to": "{{app}}"}, {"from": "C:\\Program Files\\Vendor\\Shared", "to": "/opt/shared"}]""");

        Assert.Equal(Path.Combine(app, "bin/x.dll"), Native(@"C:\Program Files\Vendor\bin\x.dll"));
        Assert.Equal(app, Native(@"c:\program files\VENDOR"));                               // as Windows, ignoring case
        Assert.Equal("/opt/shared/a.cfg", Native(@"C:\Program Files\Vendor\Shared\a.cfg"));    // the longest entry first
        // a whole segment: VendorX is not under Vendor
        Assert.Equal(Path.Combine(_state, "Drives", "C", "Program Files/VendorX/y"), Native(@"C:\Program Files\VendorX\y"));
    }

    /// <summary>The installer's chain: the manifest's mapping, the packager's table in tokens, the post-install
    /// script's substitution into the machine's state -- and the application's own C:\ path found there.</summary>
    [Fact]
    public void AnInstallersTableMakesTheApplicationsOwnPathsWork()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has its drives");
        var manifest = new WpfWebGpu.Packager.PackageManifest();
        manifest.PathMappings.Add(new WpfWebGpu.Packager.PathMapping { From = @"C:\Program Files\Vendor\Product", To = WpfWebGpu.Packager.PathTokens.InstallDir });
        manifest.PathMappings.Add(new WpfWebGpu.Packager.PathMapping { From = @"C:\Vendor Data", To = WpfWebGpu.Packager.PathTokens.ProgramData + @"\Vendor" });
        string table = WpfWebGpu.Packager.Desktop.DesktopCommon.PathTable(manifest)!;
        string root = Path.Combine(_state, "opt", "product");
        // what the post-install script's sed does
        File.WriteAllText(Path.Combine(_state, "paths.json"), table
            .Replace(WpfWebGpu.Packager.PathTokens.InstallDir, root + "/app")
            .Replace(WpfWebGpu.Packager.PathTokens.ProgramData, _state + "/ProgramData"));

        Assert.Equal(root + "/app/Config/Settings.xml", Native(@"C:\Program Files\Vendor\Product\Config\Settings.xml"));
        Assert.Equal(_state + "/ProgramData/Vendor/cache.db", Native(@"C:\Vendor Data\cache.db"));
    }

    [Fact]
    public void WindowsOwnFoldersAreTheStacks()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has its drives");
        Assert.Equal(Path.Combine(_state, "ProgramData", "Vendor/settings.xml"), Native(@"C:\ProgramData\Vendor\settings.xml"));
        Assert.Equal(Path.Combine(_state, "Public", "Documents"), Native(@"C:\Users\Public\Documents"));
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        Assert.Equal(Path.Combine(roaming, "Vendor/user.cfg"), Native($@"C:\Users\{Environment.UserName}\AppData\Roaming\Vendor\user.cfg"));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents/report.pdf"),
            Native($@"C:\Users\{Environment.UserName}\Documents\report.pdf"));
        Assert.Equal(Path.Combine(Path.GetTempPath().TrimEnd('/'), "x.tmp"), Native(@"C:\Windows\Temp\x.tmp"));
    }

    [Fact]
    public void AnythingElseOnADriveIsInTheDrivesFolder()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has its drives");
        Assert.Equal(Path.Combine(_state, "Drives", "D", "Logs/app.log"), Native(@"D:\Logs\app.log"));
        Assert.True(Directory.Exists(Path.Combine(_state, "Drives", "D")), "the drive's folder is there to write into");
        Assert.Equal(Path.Combine(_state, "Drives", "E"), Native(@"E:\"));
        Assert.Equal("C:relative", Native("C:relative"));                                      // relative to a drive: left alone
        Assert.Equal("sub/file.txt", Native(@"sub\file.txt"));
    }

    /// <summary>The other members that open a file by its path take the application's Windows path too.</summary>
    [Fact]
    public void ADataSetAndAnArchiveAreWhereTheirWindowsPathsSay()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has its drives");
        var set = new System.Data.DataSet("Readings");
        var table = set.Tables.Add("Tag");
        table.Columns.Add("Name");
        table.Rows.Add("Line1.Speed");
        // as on Windows, a folder that is not there is not made: the application makes it
        Assert.Throws<DirectoryNotFoundException>(() => WindowsPaths.DataSet_WriteXml(set, @"C:\Exports\readings.xml"));
        WindowsPaths.Directory_CreateDirectory(@"C:\Exports");
        WindowsPaths.DataSet_WriteXml(set, @"C:\Exports\readings.xml");
        Assert.True(File.Exists(Path.Combine(_state, "Drives", "C", "Exports/readings.xml")));
        var back = new System.Data.DataSet();
        WindowsPaths.DataSet_ReadXml(back, @"C:\Exports\readings.xml");
        Assert.Equal("Line1.Speed", back.Tables["Tag"]!.Rows[0]["Name"]);

        WindowsPaths.ZipFile_CreateFromDirectory(@"C:\Exports", @"C:\Backups.zip");
        Assert.True(File.Exists(Path.Combine(_state, "Drives", "C", "Backups.zip")));
        using var zip = WindowsPaths.ZipFile_OpenRead(@"C:\Backups.zip");
        Assert.Contains(zip.Entries, e => e.Name == "readings.xml");
    }

    [Fact]
    public void AServersShareIsWhereItIsMounted()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has its shares");
        Func<IEnumerable<(string, string, string)>> mounts = () => new[] { ("fileserver.local", "Shared Docs", "/Volumes/Shared Docs") };
        Core.GetField("MountsOverride", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, mounts);

        Assert.Equal("/Volumes/Shared Docs/plans/q3.xlsx", Native(@"\\FILESERVER\shared docs\plans\q3.xlsx"));
        Assert.Equal("/Volumes/Shared Docs", Native(@"\\fileserver\Shared Docs"));
        Assert.Equal("//elsewhere/share/x", Native(@"\\elsewhere\share\x"));                     // not mounted: as it was
    }

    [Fact]
    public void MountedSharesAreReadFromTheSystemsLists()
    {
        var parseMac = Core.GetMethod("ParseMacMounts", BindingFlags.Static | BindingFlags.NonPublic)!;
        var mac = ((IEnumerable<(string, string, string)>)parseMac.Invoke(null, new object[] {
            "/dev/disk3s1s1 on / (apfs, sealed, local, read-only, journaled)\n" +
            "//jane@nas.local/Team%20Share on /Volumes/Team Share (smbfs, nodev, nosuid, mounted by jane)\n" })!).ToArray();
        Assert.Equal(new[] { ("nas.local", "Team Share", "/Volumes/Team Share") }, mac);

        var parseLinux = Core.GetMethod("ParseLinuxMounts", BindingFlags.Static | BindingFlags.NonPublic)!;
        var linux = ((IEnumerable<(string, string, string)>)parseLinux.Invoke(null, new object[] {
            "/dev/sda1 / ext4 rw 0 0\n//nas/projects /mnt/proj\\040x cifs rw,vers=3.0 0 0\n",
            new[] { "/run/user/1000/gvfs/smb-share:server=nas,share=media,user=jane" } })!).ToArray();
        Assert.Equal(new[] { ("nas", "projects", "/mnt/proj x"), ("nas", "media", "/run/user/1000/gvfs/smb-share:server=nas,share=media,user=jane") }, linux);
    }
}

[CollectionDefinition("WindowsPathCore state", DisableParallelization = true)]
public sealed class WindowsPathCoreStateCollection { }
