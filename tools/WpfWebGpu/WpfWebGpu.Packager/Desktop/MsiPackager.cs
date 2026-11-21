// The whole installation as a Windows Installer package (.msi), written in managed code on any machine: what Setup.exe
// installs, in Windows Installer's own tables, for the tools that deploy MSIs (Group Policy, Intune, Configuration
// Manager) and for msiexec's /qn, repair, rollback and Apps & features.
//
//   File, Component, Directory   the installation folder (app\, the runtimes, setup.json, wpfrun.env), one component
//                                a folder; ProgramData, one component a file, never over a file the machine has, and
//                                permanent (Windows Installer leaves it; the uninstall's clean-up takes what the
//                                installation added, unless KEEPDATA=1)
//   Media + cabinets             MSZIP cabinets, in the package where they fit (under 1.5 GB), else beside it
//   Registry                     the installation's registry, one component a value -- also never over a value the
//                                machine has, also permanent; its paths [WPFAPPDIR] and [WPFPROGRAMDATA]
//   Shortcut, Icon               the window apps on the Start menu, each through wpfrunw.exe, with the app's icon
//   Upgrade                      a newer package replaces an older one (and one of the same version); an older one
//                                does not install over a newer
//   CustomAction, Binary         Setup.exe (its payload setup.json, the hives, the ProgramData list): the services, the
//                                record of what is new to the machine, ending running programs, the clean-up (Msi.cs)
//
// SQL Server is not in it: Windows Installer runs one installation at a time, and SQL Server's setup is one of its
// own. Where the installation brings one, Setup.exe installs it; a deployment of the MSI installs it first.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WpfWebGpu.Packager.Desktop.Msi;
using WpfWebGpu.Setup;

namespace WpfWebGpu.Packager.Desktop;

public static class MsiPackager
{
    private const long CabinetLimit = 1600L << 20;       // uncompressed bytes a cabinet takes
    private const long EmbeddedLimit = 1500L << 20;      // compressed bytes the package carries itself

    public static string Build(PackageManifest manifest, StagedState staged, PackFile runtimePack, PackFile setupPack, string outDir, Action<string> log,
        Func<Processor, PackFile?>? otherRuntime = null)
    {
        var plan = WindowsPackager.PlanOf(manifest, runtimePack, log, sqlPack: null, otherRuntime);
        var info = plan.Info;
        string product = manifest.Product.Name;
        string version = ProductVersion(manifest.Product.Version);
        Directory.CreateDirectory(outDir);
        string baseName = $"{DesktopCommon.FileName(product)}-{manifest.Product.Version}-windows-{plan.Arch}";
        string msiPath = Path.Combine(outDir, baseName + ".msi");
        log($"Windows Installer ({plan.Arch}): {msiPath}");
        if (manifest.SqlServer.Include)
            log($"  SQL Server is not in the MSI (Windows Installer runs one installation at a time): Setup.exe installs it, or the deployment does, first ({manifest.SqlServer.Instance})");

        string work = Path.Combine(outDir, ".work", "msi-" + plan.Arch);
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        using var certificate = Authenticode.Certificate(manifest);
        string upgradeCode = NameGuid("upgrade:" + info.Id);

        var db = new MsiDatabase();
        var tables = new Tables(db);
        bool sixtyFour = true;   // x64 and Arm64 packages: Program Files, the 64-bit registry
        int componentBits = sixtyFour ? 0x100 : 0;

        // ---- the folders ----
        db["Directory"].Add("TARGETDIR", null, "SourceDir");
        db["Directory"].Add("ProgramFiles64Folder", "TARGETDIR", ".");
        db["Directory"].Add("CommonAppDataFolder", "TARGETDIR", ".");
        db["Directory"].Add("ProgramMenuFolder", "TARGETDIR", ".");
        db["Directory"].Add("INSTALLDIR", "ProgramFiles64Folder", tables.DirName("ProgramFiles64Folder", WindowsName(product)));
        db["Directory"].Add("WPFMENU", "ProgramMenuFolder", tables.DirName("ProgramMenuFolder", WindowsName(product)));

        // ---- the files ----
        var files = new List<(string Source, string Target)>(WindowsPackager.InstallationFiles(manifest, plan, certificate, outDir, log));
        string setupJson = Path.Combine(work, "setup.json");
        using (var s = File.Create(setupJson)) info.Write(s);
        files.Add((setupJson, "setup.json"));
        foreach (string runtime in plan.Runtimes.Values.Select(r => r.Folder).Distinct())
        {
            if (!files.Any(f => f.Target.StartsWith(runtime + "/launcher/", StringComparison.Ordinal))) continue;
            string env = Path.Combine(work, runtime + "-wpfrun.env");
            File.WriteAllText(env, $"WPFRUN_CACHE=%LOCALAPPDATA%\\{WindowsName(product)}\\cache\r\nWPFRUN_LOGS=%LOCALAPPDATA%\\{WindowsName(product)}\\logs\r\n");
            files.Add((env, runtime + "/launcher/wpfrun.env"));
        }
        var fileKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cabItems = new List<Cabinet.Item>();
        // the installation folder: a component a folder, its first file the key path
        foreach (var folder in files.GroupBy(f => Parent(f.Target), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            string dir = tables.Dir("INSTALLDIR", folder.Key);
            string component = tables.Next("c");
            string? keyPath = null;
            foreach (var (source, target) in folder.OrderBy(f => f.Target, StringComparer.Ordinal))
            {
                string key = tables.AddFile(component, dir, source, Path.GetFileName(target), cabItems);
                fileKeys[target] = key;
                keyPath ??= key;
            }
            db["Component"].Add(component, NameGuid("folder:" + upgradeCode + ":" + folder.Key.ToLowerInvariant()), dir, componentBits, null, keyPath);
            tables.Feature(component);
        }
        // ProgramData: a component a file, never over the machine's, permanent
        var programData = staged.ProgramDataFiles.OrderBy(f => f.Target, StringComparer.Ordinal).ToList();
        foreach (var (source, target) in programData)
        {
            string dir = tables.Dir("CommonAppDataFolder", Parent(target));
            string component = tables.Next("c");
            string key = tables.AddFile(component, dir, source, Path.GetFileName(target), cabItems);
            db["Component"].Add(component, NameGuid("programdata:" + upgradeCode + ":" + target.ToLowerInvariant()), dir, componentBits | 0x10 | 0x80, null, key);
            tables.Feature(component);
        }

        // ---- the registry ----
        int registryValues = 0, skipped = 0;
        string? emptyKeys = null;
        foreach (string hiveFile in Directory.Exists(staged.RegistryDir) ? Directory.EnumerateFiles(staged.RegistryDir, "*.reg").Order() : Enumerable.Empty<string>())
        {
            string hive = Path.GetFileNameWithoutExtension(hiveFile);
            int root = hive switch { "HKEY_CLASSES_ROOT" => 0, "HKEY_CURRENT_USER" => 1, "HKEY_LOCAL_MACHINE" => 2, "HKEY_USERS" => 3, _ => -1 };
            if (root < 0) continue;
            var lines = HiveText.Read(File.ReadAllText(hiveFile)).ToList();
            var withValues = new HashSet<string>(lines.Where(l => l.Name != null).Select(l => l.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines)
            {
                string key = Formatted(line.Key);
                if (line.Name == null)
                {
                    if (withValues.Contains(line.Key)) continue;
                    // a key with no values: made, with the rest of the empty ones in one component
                    if (emptyKeys == null)
                    {
                        emptyKeys = tables.Next("c");
                        db["Component"].Add(emptyKeys, NameGuid("keys:" + upgradeCode), "INSTALLDIR", componentBits | 0x10, null, null);
                        tables.Feature(emptyKeys);
                    }
                    db["Registry"].Add(tables.Next("r"), root, key, "+", null, emptyKeys);
                    continue;
                }
                string? value = RegistryValue(line);
                if (value == null || line.Name.Length == 0 && value.Length == 0) { skipped++; continue; }   // a qword: Setup writes it
                string component = tables.Next("c");
                string row = tables.Next("r");
                db["Registry"].Add(row, root, key, line.Name.Length == 0 ? null : Formatted(line.Name), value.Length == 0 ? null : value, component);
                db["Component"].Add(component, NameGuid("registry:" + upgradeCode + ":" + hive + "\\" + line.Key.ToLowerInvariant() + "\\" + line.Name.ToLowerInvariant()),
                    "INSTALLDIR", componentBits | 0x4 | 0x10 | 0x80, null, row);
                tables.Feature(component);
                registryValues++;
            }
        }

        // ---- the Start menu ----
        string menu = tables.Next("c");
        db["Registry"].Add("rMenu", 2, Formatted($@"Software\{info.Id}"), "StartMenu", "#1", menu);
        // the folder installed to, which an upgrade's AppSearch finds again (its data points there)
        db["Registry"].Add("rDir", 2, Formatted($@"Software\{info.Id}"), "InstallDir", "[INSTALLDIR]", menu);
        db["RegLocator"].Add("WpfFindDir", 2, $@"Software\{info.Id}", "InstallDir", 2 | 0x10);   // the raw value, 64-bit
        db["AppSearch"].Add("INSTALLDIR", "WpfFindDir");
        _ = db["Signature"];   // AppSearch reads it, though a registry search has no row there
        db["Component"].Add(menu, NameGuid("menu:" + upgradeCode), "WPFMENU", componentBits | 0x4, null, "rMenu");
        db["RemoveFile"].Add("rmMenu", menu, null, "WPFMENU", 2);
        tables.Feature(menu);
        string? productIcon = null;
        fileKeys.TryGetValue("runtime/wpfrunw.exe", out string? launcher);
        if (launcher == null && info.Apps.Count > 0) throw new PackagerError("the runtime has no wpfrunw.exe to start the window apps with");
        foreach (var app in info.Apps)
        {
            string target = "app/" + app.Exe;
            if (!fileKeys.TryGetValue(target, out string? exe)) { log($"  {app.Exe}: not in the installation, no Start menu entry"); continue; }
            string? icon = null;
            var source = files.First(f => f.Target.Equals(target, StringComparison.OrdinalIgnoreCase)).Source;
            if (PeFile.ReadIcon(source) is { } ico)
            {
                icon = tables.Next("i") + ".ico";
                db["Icon"].Add(icon, ico);
                productIcon ??= icon;
            }
            string workDir = tables.Dir("INSTALLDIR", Parent(target));
            foreach (string? processor in app.Processors.Prepend(null))
            {
                string name = processor == null ? app.Name : $"{app.Name} ({processor})";
                db["Shortcut"].Add(tables.Next("s"), "WPFMENU", tables.FileName("WPFMENU", WindowsName(name)), menu, $"[#{launcher}]",
                    processor == null ? $"run \"[#{exe}]\"" : $"run --processor {processor} \"[#{exe}]\"", name, null, icon, icon == null ? null : 0, null, workDir);
            }
        }

        // ---- the cabinets ----
        bool compress = !manifest.Output.Fast;
        var cabinets = new List<(string Path, int LastSequence)>();
        for (int at = 0; at < cabItems.Count;)
        {
            long size = 0;
            int end = at;
            while (end < cabItems.Count && end - at < 60000 && (end == at || size + cabItems[end].Length <= CabinetLimit)) size += cabItems[end++].Length;
            string cab = Path.Combine(work, $"data{cabinets.Count + 1}.cab");
            Cabinet.Write(cab, cabItems.GetRange(at, end - at), compress);
            cabinets.Add((cab, end));
            at = end;
        }
        long cabBytes = cabinets.Sum(c => new FileInfo(c.Path).Length);
        bool embedded = cabBytes <= EmbeddedLimit;
        for (int i = 0; i < cabinets.Count; i++)
        {
            string name = embedded ? Path.GetFileName(cabinets[i].Path) : $"{baseName}-{i + 1}.cab";
            db["Media"].Add(i + 1, cabinets[i].LastSequence, null, embedded ? "#" + name : name, null, null);
            if (embedded) db.AddStream(name, cabinets[i].Path);
            else File.Move(cabinets[i].Path, Path.Combine(outDir, name), overwrite: true);
        }

        // ---- Setup.exe, the custom actions' program, with its own small payload ----
        string helper = Path.Combine(work, "Setup.exe");
        string helperZip = Path.Combine(work, "helper.zip");
        using (var zs = File.Create(helperZip))
        using (var zip = new ZipArchive(zs, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(setupJson, "setup.json");
            foreach (string hive in Directory.Exists(staged.RegistryDir) ? Directory.EnumerateFiles(staged.RegistryDir, "*.reg") : Enumerable.Empty<string>())
                zip.CreateEntryFromFile(hive, "registry/" + Path.GetFileName(hive));
            using var list = new StreamWriter(zip.CreateEntry("programdata.txt").Open(), new UTF8Encoding(false));
            foreach (var (_, target) in programData) list.WriteLine(target);
        }
        using (var output = File.Create(helper))
        {
            using (var stub = File.OpenRead(Path.Combine(setupPack.Directory, "Setup.exe"))) stub.CopyTo(output);
            long zipOffset = output.Position;
            using (var z = File.OpenRead(helperZip)) z.CopyTo(output);
            output.Write(Payload.Trailer(zipOffset));
        }
        if (certificate != null) Authenticode.Sign(helper, certificate, manifest.Signing.TimestampUrl);
        db["Binary"].Add("WpfSetup", helper);

        // ---- what Windows Installer does, and when ----
        const string installDir = "\"[INSTALLDIR].\"";
        db["CustomAction"].Add("WpfSetAppDir", 51, "WPFAPPDIR", "[INSTALLDIR]app");
        db["CustomAction"].Add("WpfSetProgramData", 51, "WPFPROGRAMDATA", "[%ProgramData]");
        db["CustomAction"].Add("WpfSetLocation", 51, "ARPINSTALLLOCATION", "[INSTALLDIR]");
        // deferred (0x400), as SYSTEM (0x800); the uninstall's go on past a failure (0x40); a rollback one 0x100
        db["CustomAction"].Add("WpfRecord", 2 | 0x400 | 0x800, "WpfSetup", $"/msi record {installDir} \"[UserSID]\"");
        db["CustomAction"].Add("WpfRollback", 2 | 0x400 | 0x100 | 0x800, "WpfSetup", $"/msi rollback {installDir}");
        db["CustomAction"].Add("WpfInstall", 2 | 0x400 | 0x800, "WpfSetup", $"/msi install {installDir} \"[UserSID]\"");
        db["CustomAction"].Add("WpfStop", 2 | 0x400 | 0x800 | 0x40, "WpfSetup", $"/msi stop {installDir} \"[UPGRADINGPRODUCTCODE][REINSTALL]\"");
        db["CustomAction"].Add("WpfCleanup", 2 | 0x400 | 0x800 | 0x40, "WpfSetup", $"/msi cleanup {installDir} \"[KEEPDATA]\"");
        const string installing = "NOT (REMOVE=\"ALL\")", removing = "REMOVE=\"ALL\"";
        var execute = new (string Action, string? Condition, int Sequence)[]
        {
            ("FindRelatedProducts", null, 25), ("AppSearch", null, 50), ("LaunchConditions", null, 100), ("ValidateProductID", null, 700),
            ("CostInitialize", null, 800), ("FileCost", null, 900), ("CostFinalize", null, 1000),
            ("WpfSetAppDir", null, 1010), ("WpfSetProgramData", null, 1011), ("WpfSetLocation", null, 1012),
            ("InstallValidate", null, 1400), ("RemoveExistingProducts", null, 1450), ("InstallInitialize", null, 1500),
            ("WpfStop", removing + " OR REINSTALL", 1510), ("WpfRecord", installing, 1520),
            ("ProcessComponents", null, 1600), ("UnpublishFeatures", null, 1800), ("RemoveRegistryValues", null, 2600),
            ("RemoveShortcuts", null, 3200), ("RemoveFiles", null, 3500), ("RemoveFolders", null, 3600),
            ("WpfCleanup", removing + " AND NOT UPGRADINGPRODUCTCODE", 3650),
            ("CreateFolders", null, 3700), ("InstallFiles", null, 4000), ("CreateShortcuts", null, 4500), ("WriteRegistryValues", null, 5000),
            ("WpfRollback", installing, 5990), ("WpfInstall", installing, 5995),
            ("RegisterUser", null, 6000), ("RegisterProduct", null, 6100), ("PublishFeatures", null, 6300), ("PublishProduct", null, 6400),
            ("InstallFinalize", null, 6600),
        };
        foreach (var (action, condition, sequence) in execute) db["InstallExecuteSequence"].Add(action, condition, sequence);
        var ui = new (string Action, int Sequence)[]
        {
            ("FindRelatedProducts", 25), ("AppSearch", 50), ("LaunchConditions", 100), ("ValidateProductID", 700), ("CostInitialize", 800), ("FileCost", 900),
            ("CostFinalize", 1000), ("WpfSetAppDir", 1010), ("WpfSetProgramData", 1011), ("WpfSetLocation", 1012), ("ExecuteAction", 1300),
        };
        foreach (var (action, sequence) in ui) db["InstallUISequence"].Add(action, null, sequence);

        // a newer package replaces this one and one of the same version (each package has its own product code); this
        // one does not install over a newer
        db["Upgrade"].Add(upgradeCode, null, version, null, 0x200 | 0x1, null, "WPFOLDER");
        db["Upgrade"].Add(upgradeCode, version, null, null, 0x2, null, "WPFNEWER");
        db["LaunchCondition"].Add("Installed OR NOT WPFNEWER", $"A newer version of {product} is installed already.");
        db["Feature"].Add("Complete", null, product, null, 1, 1, "INSTALLDIR", 0);

        string productCode = Braced(Guid.NewGuid());
        var properties = new List<(string, string)>
        {
            ("ProductCode", productCode), ("UpgradeCode", upgradeCode), ("ProductName", product), ("ProductVersion", version),
            ("Manufacturer", manifest.Product.Publisher.Length > 0 ? manifest.Product.Publisher : product), ("ProductLanguage", "1033"),
            ("ALLUSERS", "1"), ("ARPNOMODIFY", "1"), ("MSIRESTARTMANAGERCONTROL", "Disable"),
            ("SecureCustomProperties", "INSTALLDIR;WPFOLDER;WPFNEWER;KEEPDATA"),
        };
        if (productIcon != null) properties.Add(("ARPPRODUCTICON", productIcon));
        foreach (var (name, value) in properties) db["Property"].Add(name, value);
        // opened by double-click: the wizard
        MsiUi.Add(db, manifest.Product.License.Length > 0 ? License.Rtf(manifest.Product.License, "Tahoma", 16) : null,
            DesktopCommon.Icon(manifest) ?? DesktopCommon.Icon(manifest, manifest.Apps.FirstOrDefault(a => a.Kind == AppKind.Gui)));

        // ---- the package ----
        db.Codepage = AllIn1252(db) ? 1252 : 65001;
        var cf = new CompoundFile();
        db.WriteTo(cf);
        var summary = new SummaryInformation
        {
            Codepage = db.Codepage,
            Subject = product,
            Author = manifest.Product.Publisher.Length > 0 ? manifest.Product.Publisher : product,
            Comments = $"{product} {manifest.Product.Version}",
            Template = (plan.Arch == "arm64" ? "Arm64" : "x64") + ";1033",
            Revision = Braced(Guid.NewGuid()),
            PageCount = 500,
            WordCount = 2,
        };
        cf.Add(SummaryInformation.StreamName, summary.ToBytes());
        if (certificate != null)
        {
            const string signatureStream = "\u0005DigitalSignature";
            cf.Add(signatureStream, Authenticode.MsiSignature(cf.ContentDigest(HashAlgorithmName.SHA256), certificate, manifest.Signing.TimestampUrl));
            log($"  signed: {certificate.Subject}{(manifest.Signing.TimestampUrl.Length > 0 ? ", time-stamped" : "")}");
        }
        cf.Write(msiPath);
        Directory.Delete(work, recursive: true);

        log($"  {files.Count(f => f.Target.StartsWith("app/", StringComparison.Ordinal))} files of the installation, {programData.Count} of ProgramData, {registryValues} registry values"
            + (skipped > 0 ? $" ({skipped} more by Setup's custom action)" : "") + $", {info.Services.Count} services, {info.Apps.Count} Start menu entries");
        if (!embedded) log($"  its cabinets ({cabBytes >> 20} MB) are beside it, {baseName}-1.cab ...: too large for one package; they go together");
        log($"{msiPath} ({new FileInfo(msiPath).Length / (1024 * 1024)} MB)");
        return msiPath;
    }

    // ---- the tables' bookkeeping ----

    private sealed class Tables(MsiDatabase db)
    {
        private readonly Dictionary<string, int> _counters = new();
        private readonly Dictionary<string, string> _dirs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _shortNames = new(StringComparer.Ordinal);
        private int _sequence;

        public string Next(string prefix)
        {
            _counters[prefix] = _counters.TryGetValue(prefix, out int n) ? n + 1 : 1;
            return prefix + _counters[prefix];
        }

        public void Feature(string component) => db["FeatureComponents"].Add("Complete", component);

        /// <summary>The Directory row of <paramref name="path"/> ('/'-separated) under <paramref name="root"/>, and of each
        /// folder above it.</summary>
        public string Dir(string root, string path)
        {
            if (path.Length == 0) return root;
            string key = root + "/" + path;
            if (_dirs.TryGetValue(key, out string? id)) return id;
            int slash = path.LastIndexOf('/');
            string parent = Dir(root, slash < 0 ? "" : path[..slash]);
            id = Next("d");
            db["Directory"].Add(id, parent, DirName(parent, path[(slash + 1)..]));
            return _dirs[key] = id;
        }

        public string DirName(string parent, string name) => FileName(parent, name);

        /// <summary>"short|long" (the long name alone where it is a valid short one), the short one unique in the folder.</summary>
        public string FileName(string dir, string name)
        {
            if (!_shortNames.TryGetValue(dir, out var used)) _shortNames[dir] = used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (IsShort(name) && used.Add(name)) return name;
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name).TrimStart('.');
            static string Clean(string s) => new(s.ToUpperInvariant().Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-').ToArray());
            stem = Clean(stem);
            ext = Clean(ext);
            if (ext.Length > 3) ext = ext[..3];
            for (int i = 1; ; i++)
            {
                string tail = "~" + i;
                string s = (stem.Length + tail.Length > 8 ? stem[..Math.Max(0, 8 - tail.Length)] : stem) + tail + (ext.Length > 0 ? "." + ext : "");
                if (used.Add(s)) return s + "|" + name;
            }
        }

        public string AddFile(string component, string dir, string source, string name, List<Cabinet.Item> cab)
        {
            string key = Next("f");
            var fi = new FileInfo(source);
            if (fi.Length > int.MaxValue) throw new PackagerError($"{source}: Windows Installer's files are under 2 GB");
            string? version = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? PeFile.ReadFileVersion(source) : null;
            db["File"].Add(key, component, FileName(dir, name), (int)fi.Length, version, null, null, ++_sequence);
            cab.Add(new Cabinet.Item(key, source, fi.Length, fi.LastWriteTime));
            return key;
        }
    }

    private static bool IsShort(string name)
    {
        int dot = name.IndexOf('.');
        if (dot != name.LastIndexOf('.')) return false;
        string stem = dot < 0 ? name : name[..dot], ext = dot < 0 ? "" : name[(dot + 1)..];
        if (stem.Length is 0 or > 8 || ext.Length > 3 || dot >= 0 && ext.Length == 0) return false;
        return name.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-' or '!' or '#' or '$' or '%' or '&' or '\'' or '(' or ')' or '@' or '^' or '`' or '{' or '}' or '~');
    }

    private static string Parent(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    /// <summary>A file or folder name as Windows takes it (what Setup.exe names them: its characters a name cannot have
    /// made '_').</summary>
    internal static string WindowsName(string s) => string.Concat(s.Select(c => c < 0x20 || "\"<>|:*?\\/".Contains(c) ? '_' : c));

    // ---- values ----

    /// <summary>A text as a formatted field holds it literally: its brackets escaped ([\[] and [\]]), the installation's
    /// path tokens its properties.</summary>
    internal static string Formatted(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c switch { '[' => "[\\[]", ']' => "[\\]]", _ => c.ToString() });
        return sb.ToString()
            .Replace("${WPFRUN_INSTALLDIR}", "[WPFAPPDIR]", StringComparison.Ordinal)
            .Replace("${WPFRUN_APPDIR}", "[WPFAPPDIR]", StringComparison.Ordinal)
            .Replace("${WPFRUN_PROGRAMDATA}", "[WPFPROGRAMDATA]", StringComparison.Ordinal);
    }

    /// <summary>The Registry table's Value of a staged value: "#" a DWORD, "#%" an expandable string, "#x" binary, a
    /// multi-string's strings between "[~]"s; a string starting with '#' doubled. Null for a qword, which the table
    /// cannot hold.</summary>
    internal static string? RegistryValue(HiveText.Line line)
    {
        switch (line.Kind)
        {
            case "str":
                string text = Formatted(HiveText.Text(line));
                return text.StartsWith('#') ? "#" + text : text;
            case "expand": return "#%" + Formatted(HiveText.Text(line));
            case "dword": return "#" + unchecked((int)HiveText.Dword(line)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case "multi":
                var parts = HiveText.Text(line).Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(Formatted);
                return "[~]" + string.Join("[~]", parts) + "[~]";
            case "bin": return "#x" + Convert.ToHexString(HiveText.Binary(line));
            default: return null;
        }
    }

    /// <summary>The product version as Windows Installer compares it: major.minor.build (at most 255.255.65535), a
    /// fourth field kept but not compared.</summary>
    internal static string ProductVersion(string version)
    {
        string[] parts = version.Split('.');
        if (parts.Length is < 1 or > 4 || !parts.All(p => int.TryParse(p, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)))
            throw new PackagerError($"the product version {version} is not one Windows Installer takes: numbers, major.minor.build");
        int[] n = parts.Select(int.Parse).ToArray();
        if (n[0] > 255 || n.Length > 1 && n[1] > 255 || n.Length > 2 && n[2] > 65535)
            throw new PackagerError($"the product version {version} is not one Windows Installer takes: at most 255.255.65535");
        while (n.Length < 3) n = n.Append(0).ToArray();
        return string.Join(".", n);
    }

    private static bool AllIn1252(MsiDatabase db)
    {
        var cp = MsiDatabase.Encode(1252);
        try
        {
            foreach (var t in db.Tables)
                foreach (var row in t.Rows)
                    for (int c = 0; c < row.Length; c++)
                        if (row[c] is string s && t.Columns[c].IsString) cp.GetBytes(s);
            return true;
        }
        catch (EncoderFallbackException) { return false; }
    }

    private static string Braced(Guid g) => "{" + g.ToString().ToUpperInvariant() + "}";

    /// <summary>A GUID of the name, the same for it every time (RFC 4122 version 5): a component keeps its id from
    /// one package to the next, as Windows Installer's rules want.</summary>
    internal static string NameGuid(string name)
    {
        byte[] space = new Guid("6BA7B811-9DAD-11D1-80B4-00C04FD430C8").ToByteArray(bigEndian: true);
        byte[] hash = SHA1.HashData(space.Concat(Encoding.UTF8.GetBytes(name)).ToArray());
        var b = hash[..16];
        b[6] = (byte)((b[6] & 0x0F) | 0x50);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return Braced(new Guid(b, bigEndian: true));
    }
}
