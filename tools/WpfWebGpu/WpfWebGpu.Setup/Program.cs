// Setup.exe: installs the installation it carries (Payload.cs), as the original Windows installer did -- its files
// in Program Files, its data in ProgramData, its registry, its services with the service control manager, its
// window apps on the Start menu, an entry in Apps & features -- with every program run on WPF and WinForms on
// WebGPU (runtime/: a private .NET and wpfrun), the way the same installation runs on macOS and Linux.
//
//   Setup.exe                 install (or upgrade)
//   Setup.exe /uninstall      remove; ProgramData and the registry's values are kept, as Windows keeps them
//   /quiet                    no prompt at the end, nor the question below (its default taken)
//   /sql, /nosql              the SQL Server it brings installed, or left out, without asking (SqlServer.cs)

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WpfWebGpu.Setup;

internal static partial class Program
{
    private static bool s_quiet;
    private static string[] s_args = Array.Empty<string>();

    private static int Main(string[] args)
    {
        s_quiet = args.Any(a => a.Equals("/quiet", StringComparison.OrdinalIgnoreCase));
        s_args = args;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Setup.exe installs on Windows.");
            int code = args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)) ? Uninstall() : Install();
            Done(code == 0 ? "Done." : "Failed.");
            return code;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Setup failed: " + e.Message);
            Console.Error.WriteLine(e);
            Done("Failed.");
            return 1;
        }
    }

    private static void Done(string message)
    {
        Console.WriteLine(message);
        if (s_quiet || Console.IsInputRedirected) return;
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }

    // ---- install ------------------------------------------------------------------------------------------------

    private static void Grant(string folder, System.Security.Principal.WellKnownSidType account)
    {
        var info = new DirectoryInfo(folder);
        var acl = System.IO.FileSystemAclExtensions.GetAccessControl(info);
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(account, null),
            System.Security.AccessControl.FileSystemRights.Modify,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        System.IO.FileSystemAclExtensions.SetAccessControl(info, acl);
    }

    private static int Install()
    {
        using var payload = Payload.Open(Environment.ProcessPath!, Console.WriteLine)
            ?? throw new InvalidOperationException("this Setup.exe carries no installation.");
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        SetupInfo info;
        using (var s = (zip.GetEntry("setup.json") ?? throw new InvalidOperationException("no setup.json")).Open()) info = SetupInfo.Read(s);

        string root = InstallRoot(info);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        Console.WriteLine($"Installing {info.Product} {info.Version} into {root}");

        // an upgrade: what runs is stopped before its files are replaced
        foreach (var service in info.Services) Services.Stop(ServiceName(info, service));

        int files = 0, data = 0;
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;
            string target;
            if (e.FullName.StartsWith("app/", StringComparison.Ordinal) || e.FullName.StartsWith("runtime/", StringComparison.Ordinal))
            {
                target = Path.Combine(root, e.FullName.Replace('/', '\\'));
                files++;
            }
            else if (e.FullName.StartsWith("programdata/", StringComparison.Ordinal))
            {
                // the machine's data: put where it is not yet, never over what the machine has made of it
                target = Path.Combine(programData, e.FullName["programdata/".Length..].Replace('/', '\\'));
                if (File.Exists(target)) continue;
                data++;
            }
            else continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
            if (files % 2000 == 0 && files > 0) Console.WriteLine($"  {files} files");
        }
        Console.WriteLine($"  {files} files, {data} of ProgramData");

        var tokens = new Dictionary<string, string>
        {
            ["${WPFRUN_INSTALLDIR}"] = Path.Combine(root, "app"),
            ["${WPFRUN_APPDIR}"] = Path.Combine(root, "app"),
            ["${WPFRUN_PROGRAMDATA}"] = programData,
        };
        int values = 0;
        foreach (ZipArchiveEntry e in zip.Entries.Where(e => e.FullName.StartsWith("registry/", StringComparison.Ordinal) && e.FullName.EndsWith(".reg", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            values += RegistryImport.Import(Path.GetFileNameWithoutExtension(e.Name), reader.ReadToEnd(), tokens);
        }
        Console.WriteLine($"  registry: {values} values");

        // the services: each program run as the service control manager's own process (wpfrun prepare: the dotnet
        // exec command wpfrun would run it with), so its ServiceBase talks to the manager itself
        string wpfrunCache = Path.Combine(programData, info.Product, "wpfrun");
        foreach (var service in info.Services)
        {
            string name = ServiceName(info, service);
            string command = Prepare(root, wpfrunCache, service.Exe, service.Args);
            Services.CreateOrUpdate(name, service.DisplayName.Length > 0 ? service.DisplayName : service.Name, command, service.Start,
                service.DependsOn.Select(d => ServiceName(info, new SetupService { Name = d })).ToList(), service.Environment, service.Account,
                service.StartName, service.Description, service.DelayedAutoStart, service.Security);
            Console.WriteLine($"  service {name}{(service.Account != "LocalSystem" ? $" ({service.Account})" : "")}");
        }
        // what a service running as NETWORK SERVICE or LOCAL SERVICE writes -- its data in ProgramData, wpfrun's
        // caches and logs -- writable by that account, as the product's own installer grants it on Windows
        var accounts = info.Services.Select(s => s.Account).Where(a => a != "LocalSystem").Distinct().ToList();
        if (accounts.Count > 0)
        {
            var folders = zip.Entries.Where(e => e.FullName.StartsWith("programdata/", StringComparison.Ordinal))
                .Select(e => e.FullName["programdata/".Length..].Split('/')[0]).Where(f => f.Length > 0).Distinct()
                .Select(f => Path.Combine(programData, f)).Append(Path.Combine(programData, info.Product)).Where(Directory.Exists);
            foreach (string folder in folders)
                foreach (string account in accounts)
                    Grant(folder, account == "NetworkService" ? System.Security.Principal.WellKnownSidType.NetworkServiceSid : System.Security.Principal.WellKnownSidType.LocalServiceSid);
        }
        // the database server the services keep their configuration in, before they start; the accounts they run as
        // (which exist now, a service's virtual account with it) log in to it as Windows users, as on the machine the
        // installation was made on
        SqlServer.Install(zip, info, s_args, s_quiet, info.Services.Select(s => s.StartName.Length > 0 ? s.StartName : s.Account switch
        {
            "NetworkService" => @"NT AUTHORITY\NETWORK SERVICE",
            "LocalService" => @"NT AUTHORITY\LOCAL SERVICE",
            _ => "LocalSystem",
        }));
        foreach (var service in info.Services.Where(s => s.Start == "Auto")) Services.Start(ServiceName(info, service));

        string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), info.Product);
        Directory.CreateDirectory(menu);
        foreach (var app in info.Apps)
        {
            string exe = Path.Combine(root, "app", app.Exe.Replace('/', '\\'));
            Shortcut.Create(Path.Combine(menu, FileName(app.Name) + ".lnk"),
                Path.Combine(root, "runtime", "dotnet", "dotnetw.exe"),
                $"\"{Path.Combine(root, "runtime", "launcher", "wpfrun.dll")}\" run \"{exe}\"",
                Path.GetDirectoryName(exe)!, exe, app.Name);
        }
        Console.WriteLine($"  {info.Apps.Count} Start menu entries in {menu}");

        // to remove it again: this program without its payload, and what it installed
        string uninstaller = Path.Combine(root, "uninstall.exe");
        using (var self = File.OpenRead(Environment.ProcessPath!))
        using (var to = File.Create(uninstaller))
        {
            long program = Payload.ProgramLength(self);
            self.Position = 0;
            CopyBytes(self, to, program);
        }
        using (var s = File.Create(Path.Combine(root, "setup.json"))) info.Write(s);
        using (var key = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{info.Id}"))
        {
            key.SetValue("DisplayName", info.Product);
            key.SetValue("DisplayVersion", info.Version);
            key.SetValue("Publisher", info.Publisher);
            key.SetValue("InstallLocation", root);
            key.SetValue("UninstallString", $"\"{uninstaller}\" /uninstall");
            key.SetValue("QuietUninstallString", $"\"{uninstaller}\" /uninstall /quiet");
            if (info.Apps.FirstOrDefault() is { } first) key.SetValue("DisplayIcon", Path.Combine(root, "app", first.Exe.Replace('/', '\\')));
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        Console.WriteLine($"{info.Product} {info.Version} installed.");
        return 0;
    }

    private static string Prepare(string root, string cache, string exe, List<string> args)
    {
        var psi = new ProcessStartInfo(Path.Combine(root, "runtime", "dotnet", "dotnet.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        psi.ArgumentList.Add(Path.Combine(root, "runtime", "launcher", "wpfrun.dll"));
        psi.ArgumentList.Add("prepare");
        psi.ArgumentList.Add(Path.Combine(root, "app", exe.Replace('/', '\\')));
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["WPFRUN_CACHE"] = Path.Combine(cache, "cache");
        psi.Environment["WPFRUN_LOGS"] = Path.Combine(cache, "logs");
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        string? line = output.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith("commandline: ", StringComparison.Ordinal));
        if (p.ExitCode != 0 || line == null) throw new InvalidOperationException($"wpfrun prepare {exe} failed: {output.Trim()}");
        return line["commandline: ".Length..];
    }

    // ---- uninstall ----------------------------------------------------------------------------------------------

    private static int Uninstall()
    {
        string root = Path.GetDirectoryName(Environment.ProcessPath!)!;
        string setupJson = Path.Combine(root, "setup.json");
        if (!File.Exists(setupJson)) throw new InvalidOperationException($"no installation in {root} (setup.json)");
        SetupInfo info;
        using (var s = File.OpenRead(setupJson)) info = SetupInfo.Read(s);
        Console.WriteLine($"Removing {info.Product} from {root}");
        foreach (var service in info.Services) { Services.Stop(ServiceName(info, service)); Services.Delete(ServiceName(info, service)); }
        string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), info.Product);
        if (Directory.Exists(menu)) Directory.Delete(menu, recursive: true);
        foreach (string dir in new[] { "app", "runtime" })
            if (Directory.Exists(Path.Combine(root, dir))) Directory.Delete(Path.Combine(root, dir), recursive: true);
        File.Delete(setupJson);
        Registry.LocalMachine.DeleteSubKeyTree($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{info.Id}", throwOnMissingSubKey: false);
        // this program and its folder go once it has ended
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{root}\"") { CreateNoWindow = true, UseShellExecute = false });
        Console.WriteLine($"{info.Product} removed (ProgramData and the registry's values are kept).");
        return 0;
    }

    // ---- names ----------------------------------------------------------------------------------------------------

    private static string InstallRoot(SetupInfo info) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), FileName(info.Product));

    /// <summary>A service keeps the name it has on the original installation's machine (an app asks the manager for it
    /// by that name).</summary>
    private static string ServiceName(SetupInfo info, SetupService service) => service.Name;

    private static string FileName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static void CopyBytes(Stream from, Stream to, long count)
    {
        var buffer = new byte[1 << 16];
        while (count > 0)
        {
            int n = from.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n <= 0) throw new EndOfStreamException();
            to.Write(buffer, 0, n);
            count -= n;
        }
    }
}
