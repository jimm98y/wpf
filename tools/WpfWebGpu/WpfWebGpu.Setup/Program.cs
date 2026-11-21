// Setup.exe: installs the installation it carries (Payload.cs), as the original Windows installer did -- its files
// in Program Files, its data in ProgramData, its registry, its services with the service control manager, its
// window apps on the Start menu, an entry in Apps & features -- with every program run on WPF and WinForms on
// WebGPU (runtime/: a private .NET and wpfrun), the way the same installation runs on macOS and Linux.
//
//   Setup.exe                 install (or upgrade)
//   Setup.exe /uninstall      remove everything it put on the machine (InstallRecord.cs): its ProgramData, its
//                             registry values and keys, the access rules it added, the SQL Server instance it installed,
//                             every user's caches and logs of it -- asked first, yes the default
//   /uninstall /keepdata      remove it, ProgramData and the registry's values kept for a later install
//                             (/all: everything, without asking)
//   /quiet                    no prompt at the end, nor the questions (their defaults taken: everything removed)
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
        Directory.CreateDirectory(root);
        var record = InstallRecord.Load(info.Id);
        record.UserSid ??= System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        foreach (string top in zip.Entries.Where(e => e.FullName.StartsWith("programdata/", StringComparison.Ordinal))
                     .Select(e => e.FullName["programdata/".Length..].Split('/')[0]).Append(info.Product).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!Directory.Exists(Path.Combine(programData, top))) record.ProgramDataFolders.Add(Path.Combine(programData, top));

        int files = 0, data = 0;
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue;
            string target;
            if (e.FullName.StartsWith("app/", StringComparison.Ordinal) || e.FullName.StartsWith("runtime/", StringComparison.Ordinal)
                || e.FullName.StartsWith("runtime-", StringComparison.Ordinal))
            {
                target = Path.Combine(root, e.FullName.Replace('/', '\\'));
                files++;
            }
            else if (e.FullName.StartsWith("programdata/", StringComparison.Ordinal))
            {
                // the machine's data: put where it is not yet, never over what the machine has made of it
                target = Path.Combine(programData, e.FullName["programdata/".Length..].Replace('/', '\\'));
                if (File.Exists(target)) continue;
                record.ProgramDataFiles.Add(target);
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
            values += RegistryImport.Import(Path.GetFileNameWithoutExtension(e.Name), reader.ReadToEnd(), tokens, record);
        }
        Console.WriteLine($"  registry: {values} values");

        // the services: each program run as the service control manager's own process (wpfrun prepare: the dotnet
        // exec command wpfrun would run it with), so its ServiceBase talks to the manager itself
        string wpfrunCache = Path.Combine(programData, info.Product, "wpfrun");
        foreach (var service in info.Services)
        {
            string name = ServiceName(info, service);
            var (runtime, processor) = RuntimeOf(info, service.Exe);
            string command = Prepare(root, runtime, processor, wpfrunCache, service.Exe, service.Args);
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
                {
                    var sid = account == "NetworkService" ? System.Security.Principal.WellKnownSidType.NetworkServiceSid : System.Security.Principal.WellKnownSidType.LocalServiceSid;
                    Grant(folder, sid);
                    record.Acls.Add(new AclItem(folder, new System.Security.Principal.SecurityIdentifier(sid, null).Value));
                }
        }
        // the database server the services keep their configuration in, before they start; the accounts they run as
        // (which exist now, a service's virtual account with it) log in to it as Windows users, as on the machine the
        // installation was made on
        record.SqlInstance ??= SqlServer.Install(zip, info, s_args, s_quiet, info.Services.Select(s => s.StartName.Length > 0 ? s.StartName : s.Account switch
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
            // its default processor's, as the launcher finds it; then one for each processor to fall back on
            Shortcut.Create(Path.Combine(menu, FileName(app.Name) + ".lnk"),
                Path.Combine(root, "runtime", "wpfrunw.exe"),
                $"run \"{exe}\"",
                Path.GetDirectoryName(exe)!, exe, app.Name);
            foreach (string processor in app.Processors)
                Shortcut.Create(Path.Combine(menu, FileName($"{app.Name} ({processor})") + ".lnk"),
                    Path.Combine(root, "runtime", "wpfrunw.exe"),
                    $"run --processor {processor} \"{exe}\"",
                    Path.GetDirectoryName(exe)!, exe, $"{app.Name} ({processor})");
        }
        Console.WriteLine($"  {info.Apps.Count + info.Apps.Sum(a => a.Processors.Count)} Start menu entries in {menu}");

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
        record.Save(info.Id);
        // the apps' caches and logs, each user's, in a folder of the installation's own (wpfrun's, its launchers read
        // it), for the uninstall to find
        foreach (string runtime in info.Runtimes.Values.Append("runtime").Distinct())
            if (Directory.Exists(Path.Combine(root, runtime, "launcher")))
                File.WriteAllText(Path.Combine(root, runtime, "launcher", "wpfrun.env"),
                    $"WPFRUN_CACHE=%LOCALAPPDATA%\\{FileName(info.Product)}\\cache\r\nWPFRUN_LOGS=%LOCALAPPDATA%\\{FileName(info.Product)}\\logs\r\n");
        using (var key = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{info.Id}"))
        {
            key.SetValue("DisplayName", info.Product);
            key.SetValue("DisplayVersion", info.Version);
            key.SetValue("Publisher", info.Publisher);
            key.SetValue("InstallLocation", root);
            key.SetValue("UninstallString", $"\"{uninstaller}\" /uninstall");
            key.SetValue("QuietUninstallString", $"\"{uninstaller}\" /uninstall /quiet");
            if (info.Apps.FirstOrDefault() is { } first) key.SetValue("DisplayIcon", Path.Combine(root, "app", first.Exe.Replace('/', '\\')));
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, Size(root) / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        Console.WriteLine($"{info.Product} {info.Version} installed.");
        return 0;
    }

    // the runtime folder of the processor a program runs as by default (runtime\: Setup's own)
    private static (string Folder, string? Processor) RuntimeOf(SetupInfo info, string exe) =>
        info.Programs.TryGetValue(exe, out string? processor) && info.Runtimes.TryGetValue(processor, out string? folder) ? (folder, processor) : ("runtime", null);

    private static string Prepare(string root, string runtime, string? processor, string cache, string exe, List<string> args)
    {
        var psi = new ProcessStartInfo(Path.Combine(root, runtime, "dotnet", "dotnet.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        psi.ArgumentList.Add(Path.Combine(root, runtime, "launcher", "wpfrun.dll"));
        psi.ArgumentList.Add("prepare");
        // the processor named: run as it here, not handed on by a setup.json an upgrade has not written yet
        if (processor != null) { psi.ArgumentList.Add("--processor"); psi.ArgumentList.Add(processor); }
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
        var record = InstallRecord.Load(info.Id);
        bool Flag(string f) => s_args.Any(a => a.Equals(f, StringComparison.OrdinalIgnoreCase));
        bool all = Flag("/all") || !Flag("/keepdata") && (s_quiet || Console.IsInputRedirected || Ask(
            $"Remove {info.Product}'s data too -- its ProgramData, its registry settings? [Y/n] "));
        Console.WriteLine($"Removing {info.Product} from {root}" + (all ? ", with its data" : ""));

        // what runs: the services (those the installation registered, and those its programs created since, of its
        // folder), then every program of the installation still running
        var services = info.Services.Select(sv => ServiceName(info, sv)).Concat(Services.Of(root)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string name in services) Services.Stop(name);
        foreach (string name in services) { Services.Delete(name); Console.WriteLine($"  service {name} removed"); }
        // (the apps run as their own <App>.exe in each user's cache: ProgramHost)
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var caches = Profiles().Select(profile => Path.Combine(profile, "AppData", "Local", FileName(info.Product)))
            .Append(Path.Combine(programData, info.Product, "wpfrun")).ToList();
        StopPrograms(caches.Prepend(root));

        string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), info.Product);
        if (Directory.Exists(menu)) Directory.Delete(menu, recursive: true);
        // every user's caches and logs of the installation's apps (wpfrun.env), and wpfrun's own of the installation
        foreach (string cache in caches) Remove(cache);

        if (all)
        {
            int values = RegistryImport.Remove(record);
            Console.WriteLine($"  registry: {values} values removed");
            foreach (var acl in record.Acls) Ungrant(acl.Folder, acl.Sid);
            foreach (string file in record.ProgramDataFiles) { try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            foreach (string folder in record.ProgramDataFolders) Remove(folder);
            // the folders the files were in, where nothing else is left in them
            foreach (string? dir in record.ProgramDataFiles.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(d => d?.Length ?? 0))
                for (string? d = dir; d != null && d.Length > programData.Length && d.StartsWith(programData, StringComparison.OrdinalIgnoreCase); d = Path.GetDirectoryName(d))
                    if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); else break;
            Console.WriteLine($"  ProgramData: {record.ProgramDataFiles.Count} files, {record.ProgramDataFolders.Count} folders removed");
            InstallRecord.Delete(info.Id);
        }

        foreach (string dir in Directory.EnumerateDirectories(root))
            Remove(dir);
        foreach (string file in Directory.EnumerateFiles(root).Where(f => !f.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)))
            Remove(file);
        Registry.LocalMachine.DeleteSubKeyTree($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{info.Id}", throwOnMissingSubKey: false);
        // this program and its folder go once it has ended
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{root}\"") { CreateNoWindow = true, UseShellExecute = false });
        Console.WriteLine($"{info.Product} removed" + (all ? "." : " (ProgramData and the registry's values are kept; /uninstall removes them too)."));
        if (s_delayed > 0) Console.WriteLine($"  {s_delayed} files in use go when Windows restarts.");
        // SQL Server stays, as a product's uninstall leaves the SQL Server Express it brought (other programs may use
        // the instance): its own entry in Apps & features removes it
        if (record.SqlInstance is { } instance)
            Console.WriteLine($"  SQL Server's instance {instance} stays, with its databases: Apps & features removes it (Microsoft SQL Server).");
        return 0;
    }

    private static bool Ask(string question)
    {
        Console.Write(question);
        string? answer = Console.ReadLine()?.TrimStart();
        return answer == null || !(answer.StartsWith('n') || answer.StartsWith('N'));
    }

    private static int s_delayed;

    /// <summary>A file or folder deleted; what is in use (a program that would not stop) marked to go when Windows
    /// restarts, rather than the uninstall stopping half done.</summary>
    private static void Remove(string path)
    {
        if (Directory.Exists(path))
        {
            try { Directory.Delete(path, recursive: true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            foreach (string f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) Remove(f);
            foreach (string d in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(path))
                Retried(() => Directory.Delete(d), d);
        }
        else if (File.Exists(path))
            Retried(() => { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }, path);
    }

    // A file still in use, retried for a while before it is left for Windows' restart: a program just ended lets go
    // of its files a moment later (on Windows on Arm an x64 program's stay held for some seconds after it has
    // exited). One grace period for the whole uninstall, from the first file found in use.
    private static DateTime s_busyUntil;

    private static void Retried(Action delete, string path)
    {
        while (true)
        {
            try { delete(); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (s_busyUntil == default) s_busyUntil = DateTime.UtcNow.AddSeconds(15);
                if (DateTime.UtcNow < s_busyUntil) { Thread.Sleep(250); continue; }
                if (MoveFileExW(path, null, MOVEFILE_DELAY_UNTIL_REBOOT)) s_delayed++;
                return;
            }
        }
    }

    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 4;
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool MoveFileExW(string from, string? to, uint flags);

    /// <summary>Ends every program running from <paramref name="folders"/>: the installation's, and the apps' own
    /// executables in each user's cache (ProgramHost: an app runs as its own &lt;App&gt;.exe there).</summary>
    private static void StopPrograms(IEnumerable<string> folders)
    {
        var prefixes = folders.Select(f => f.TrimEnd('\\') + "\\").ToList();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                string? path = p.MainModule?.FileName;
                if (path == null || !prefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;
                Console.WriteLine($"  ending {Path.GetFileName(path)} ({p.Id})");
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { }
            finally { p.Dispose(); }
        }
    }

    /// <summary>The machine's user profiles (each one's caches of the installation's apps).</summary>
    private static IEnumerable<string> Profiles()
    {
        using var list = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        if (list == null) yield break;
        foreach (string sid in list.GetSubKeyNames())
        {
            using var k = list.OpenSubKey(sid);
            if (k?.GetValue("ProfileImagePath") is string path && Directory.Exists(Environment.ExpandEnvironmentVariables(path)))
                yield return Environment.ExpandEnvironmentVariables(path);
        }
    }

    private static void Ungrant(string folder, string sid)
    {
        if (!Directory.Exists(folder)) return;
        var info = new DirectoryInfo(folder);
        var acl = System.IO.FileSystemAclExtensions.GetAccessControl(info);
        acl.RemoveAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(sid),
            System.Security.AccessControl.FileSystemRights.Modify,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        System.IO.FileSystemAclExtensions.SetAccessControl(info, acl);
    }

    private static long Size(string root)
    {
        long n = 0;
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)) n += f.Length;
        return n;
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
