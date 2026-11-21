// Setup as the MSI's custom actions: what Windows Installer's own tables do not do -- the services (each registered
// with the command line `wpfrun prepare` makes for it, which the ServiceInstall table cannot know in advance), the
// access rules for their accounts, ending the programs users still run from their caches, and the uninstall's
// clean-up of what the installation made since: caches, logs, and (unless KEEPDATA=1) its ProgramData and registry
// data. The MSI carries this program in its Binary table with a small payload of its own (setup.json, the registry
// hives, the ProgramData files' list); Windows Installer runs it deferred, as SYSTEM.
//
//   Setup.exe /msi record <install dir> <user SID>      before the files and registry: what is not there yet, into
//                                                       the installation's record (InstallRecord.cs), for cleanup
//   Setup.exe /msi install <install dir> <user SID>     after them: the services created and started, the access
//                                                       rules, the values the Registry table cannot carry (qword)
//   Setup.exe /msi rollback <install dir>               an install undone: its services gone again
//   Setup.exe /msi stop <install dir> <upgrading>       an uninstall, first: the services stopped and (not for an
//                                                       upgrade) removed, the programs from its folders ended
//   Setup.exe /msi cleanup <install dir> <keep data>    an uninstall, last: every user's caches and logs, what is
//                                                       left in the folder, and the data the record lists
//
// Everything it does goes into %TEMP%\<product>-msi.log as well (SYSTEM's temporary folder).

using System.IO.Compression;
using System.Text;

namespace WpfWebGpu.Setup;

internal static partial class Program
{
    private static int Msi(string[] args)
    {
        if (args.Length < 2) throw new InvalidOperationException("Setup.exe /msi record|install|rollback|stop|cleanup <install dir> ...");
        string verb = args[0];
        string root = Path.GetFullPath(args[1]).TrimEnd('\\');
        string Arg(int i) => args.Length > i ? args[i].Trim() : "";

        using var payload = Payload.Open(Environment.ProcessPath!) ?? throw new InvalidOperationException("this Setup.exe carries no installation");
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        SetupInfo info;
        using (var s = (zip.GetEntry("setup.json") ?? throw new InvalidOperationException("no setup.json")).Open()) info = SetupInfo.Read(s);

        using var log = new StreamWriter(Path.Combine(Path.GetTempPath(), FileName(info.Product) + "-msi.log"), append: true) { AutoFlush = true };
        var console = Console.Out;
        Console.SetOut(new Tee(console, log));
        try
        {
            Console.WriteLine($"{DateTime.Now:s} {info.Product} {info.Version}: /msi {string.Join(' ', args)}");
            int code = verb switch
            {
                "record" => MsiRecord(zip, info, root, Arg(2)),
                "install" => MsiInstall(zip, info, root, Arg(2)),
                "rollback" => MsiRollback(info),
                "stop" => MsiStop(info, root, upgrading: Arg(2).Length > 0),
                "cleanup" => MsiCleanup(info, root, keepData: Arg(2) is { Length: > 0 } k && k != "0"),
                _ => throw new InvalidOperationException($"unknown /msi {verb}"),
            };
            Console.WriteLine($"{DateTime.Now:s} done ({code})");
            return code;
        }
        catch (Exception e)
        {
            Console.WriteLine($"{DateTime.Now:s} failed: {e}");
            throw;
        }
        finally
        {
            Console.SetOut(console);
        }
    }

    private static IEnumerable<(string Hive, string Text)> Hives(ZipArchive zip)
    {
        foreach (ZipArchiveEntry e in zip.Entries.Where(e => e.FullName.StartsWith("registry/", StringComparison.Ordinal) && e.FullName.EndsWith(".reg", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            yield return (Path.GetFileNameWithoutExtension(e.Name), reader.ReadToEnd());
        }
    }

    // the ProgramData files the MSI installs, by their paths under ProgramData ('/'-separated)
    private static List<string> ProgramDataFiles(ZipArchive zip)
    {
        var files = new List<string>();
        if (zip.GetEntry("programdata.txt") is not { } list) return files;
        using var reader = new StreamReader(list.Open(), Encoding.UTF8);
        for (string? line; (line = reader.ReadLine()) != null;)
            if (line.Length > 0) files.Add(line);
        return files;
    }

    // before Windows Installer writes anything: the ProgramData folders, files and registry values and keys the
    // installation is about to add (none the machine has: an upgrade's record grows, never claims what was there)
    private static int MsiRecord(ZipArchive zip, SetupInfo info, string root, string userSid)
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var record = InstallRecord.Load(info.Id);
        if (userSid.Length > 0) record.UserSid ??= userSid;
        var files = ProgramDataFiles(zip);
        foreach (string top in files.Select(f => f.Split('/')[0]).Append(info.Product).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!Directory.Exists(Path.Combine(programData, top))) record.ProgramDataFolders.Add(Path.Combine(programData, top));
        foreach (string f in files)
        {
            string target = Path.Combine(programData, f.Replace('/', '\\'));
            if (!File.Exists(target)) record.ProgramDataFiles.Add(target);
        }
        int values = 0;
        foreach (var (hive, text) in Hives(zip))
            values += RegistryImport.Import(hive, text, new Dictionary<string, string>(), record, write: false, userSid: record.UserSid);
        record.Save(info.Id);
        Console.WriteLine($"  recorded: {record.ProgramDataFiles.Count} ProgramData files, {values} registry values new to the machine");
        return 0;
    }

    private static int MsiInstall(ZipArchive zip, SetupInfo info, string root, string userSid)
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var record = InstallRecord.Load(info.Id);
        // what the Registry table cannot carry
        var tokens = new Dictionary<string, string>
        {
            ["${WPFRUN_INSTALLDIR}"] = Path.Combine(root, "app"),
            ["${WPFRUN_APPDIR}"] = Path.Combine(root, "app"),
            ["${WPFRUN_PROGRAMDATA}"] = programData,
        };
        int qwords = 0;
        foreach (var (hive, text) in Hives(zip))
            qwords += RegistryImport.Import(hive, text, tokens, new InstallRecord(), userSid: userSid.Length > 0 ? userSid : record.UserSid, kinds: k => k == "qword");
        if (qwords > 0) Console.WriteLine($"  registry: {qwords} qword values");
        InstallServices(info, root, programData, record, ProgramDataFiles(zip).Select(f => f.Split('/')[0]));
        record.Save(info.Id);
        return 0;
    }

    private static int MsiRollback(SetupInfo info)
    {
        foreach (var service in info.Services) { Services.Stop(ServiceName(info, service)); Services.Delete(ServiceName(info, service)); }
        return 0;
    }

    private static int MsiStop(SetupInfo info, string root, bool upgrading)
    {
        var services = info.Services.Select(sv => ServiceName(info, sv)).Concat(Services.Of(root)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string name in services) Services.Stop(name);
        StopPrograms(Caches(info).Concat(OwnFolders(info, root)));
        // an upgrade's new version registers them again, as they are now
        if (upgrading) return 0;
        foreach (string name in services) { Services.Delete(name); Console.WriteLine($"  service {name} removed"); }
        return 0;
    }

    private static int MsiCleanup(SetupInfo info, string root, bool keepData)
    {
        foreach (string cache in Caches(info)) Remove(cache);
        if (!keepData)
        {
            var record = InstallRecord.Load(info.Id);
            RemoveData(record);
            InstallRecord.Delete(info.Id);
        }
        // the installation's own folders, and the folder itself where nothing else is left in it (INSTALLDIR can be any
        // folder: not one of others' programs swept away with it)
        foreach (string folder in OwnFolders(info, root)) Remove(folder);
        foreach (string file in new[] { "setup.json" }.Select(f => Path.Combine(root, f))) Remove(file);
        if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Retried(() => Directory.Delete(root), root);
        Console.WriteLine($"{info.Product} removed" + (keepData ? " (ProgramData and the registry's values kept: KEEPDATA)" : "") + ".");
        if (s_delayed > 0) Console.WriteLine($"  {s_delayed} files in use go when Windows restarts.");
        return 0;
    }

    // what the MSI installs in its folder: app\ and the runtimes
    private static IEnumerable<string> OwnFolders(SetupInfo info, string root) =>
        info.Runtimes.Values.Append("runtime").Append("app").Distinct(StringComparer.OrdinalIgnoreCase).Select(f => Path.Combine(root, f));

    private sealed class Tee(TextWriter a, TextWriter b) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { a.Write(value); b.Write(value); }
        public override void Write(string? value) { a.Write(value); b.Write(value); }
        public override void WriteLine(string? value) { a.WriteLine(value); b.WriteLine(value); }
    }
}
