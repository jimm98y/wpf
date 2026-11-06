// `wpfrun reg import|export`: moves registry state between a Windows machine and the registry
// apps see under wpfrun on macOS (the Microsoft.Win32.Registry shim's store in wpfrun's state).
//
// The files are regedit's: "Windows Registry Editor Version 5.00" (UTF-16) or REGEDIT4 (ANSI/UTF-8),
//   [HKEY_CURRENT_USER\Software\Vendor]     a key            [-HKEY_...]   deletes one
//   "Name"="text"   @="default"   "Name"=dword:0000002a   "Name"=hex:01,02   "Name"=hex(7):...
//   "Name"=-        deletes a value; a line ending in \ continues on the next.
// So what `reg export` (or regedit) wrote on Windows imports here, and an export from here imports there.

extern alias RegistryShim;

using System.Globalization;
using System.Text;
using RegistryShim::Microsoft.Win32;
using WpfWebGpu.Packager;

namespace WpfWebGpu;

internal static class RegFile
{
    public static int Run(string[] args)
    {
        if (HostInfo.IsWindows)
            throw new UserError("on Windows an app run through wpfrun uses the real registry: use reg.exe or regedit.");
        if (args.Length < 1) throw new UserError("usage: wpfrun reg import <file.reg> | wpfrun reg export <key> <file.reg>");

        // Before the first use of Registry: the shim picks its store when its hives are created.
        Environment.SetEnvironmentVariable("WPFWEBGPU_REGISTRY_DIR", Path.Combine(Locations.StateRoot, "registry"));
        switch (args[0])
        {
            case "import" when args.Length == 2:
                var (keys, values, deleted) = Import(ReadText(args[1]));
                Console.WriteLine($"imported {values} values in {keys} keys{(deleted > 0 ? $", {deleted} deletions" : "")} into {Path.Combine(Locations.StateRoot, "registry")}");
                return 0;
            case "export" when args.Length == 3:
                int n = Export(args[1], args[2]);
                Console.WriteLine($"exported {n} keys to {args[2]}");
                return 0;
            default:
                throw new UserError("usage: wpfrun reg import <file.reg> | wpfrun reg export <key> <file.reg>");
        }
    }

    // ---- import -------------------------------------------------------------------------------------

    internal static (int Keys, int Values, int Deleted) Import(string text)
    {
        int keys = 0, values = 0, deleted = 0;
        RegistryKey? current = null;
        foreach (RegEntry e in RegText.Parse(text))
        {
            switch (e.Kind)
            {
                case RegEntryKind.DeleteKey:
                    current?.Dispose();
                    current = null;
                    Hive(e.Hive).DeleteSubKeyTree(e.Key, false);
                    deleted++;
                    break;
                case RegEntryKind.Key:
                    current?.Dispose();
                    current = e.Key.Length == 0 ? Hive(e.Hive) : Hive(e.Hive).CreateSubKey(e.Key);
                    keys++;
                    break;
                case RegEntryKind.DeleteValue:
                    current!.DeleteValue(e.Name, false);
                    deleted++;
                    break;
                default:
                    current!.SetValue(e.Name, e.Value!, (RegistryValueKind)e.ValueKind);
                    values++;
                    break;
            }
        }
        current?.Dispose();
        return (keys, values, deleted);
    }

    private static RegistryKey Hive(string hive) => hive switch
    {
        "HKEY_CURRENT_USER" => Registry.CurrentUser,
        "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
        "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
        "HKEY_USERS" => Registry.Users,
        _ => Registry.CurrentConfig,
    };

    private static (RegistryKey Hive, string Sub) Split(string path)
    {
        var (hive, sub) = RegText.SplitKey(path);
        return (Hive(hive), sub);
    }

    internal static (object Value, RegistryValueKind Kind) ParseData(string data)
    {
        var (value, kind) = RegText.ParseData(data);
        return (value, (RegistryValueKind)kind);
    }

    private static string ReadText(string path)
    {
        // regedit writes UTF-16LE with a BOM (version 5) or the ANSI code page (REGEDIT4).
        return RegText.Read(path);
    }

    // ---- export -------------------------------------------------------------------------------------

    internal static int Export(string keyPath, string file)
    {
        var (hive, sub) = Split(keyPath.TrimEnd('\\'));
        using RegistryKey? key = sub.Length == 0 ? hive : hive.OpenSubKey(sub);
        if (key == null) throw new UserError($"no such key: {keyPath}");
        var sb = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
        // regedit accepts the full hive names only (HKEY_LOCAL_MACHINE, not HKLM).
        int count = Write(sb, key, sub.Length == 0 ? hive.Name : hive.Name + "\\" + sub);
        // As regedit writes it: UTF-16LE with a byte-order mark.
        File.WriteAllText(file, sb.ToString(), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        return count;
    }

    private static int Write(StringBuilder sb, RegistryKey key, string path)
    {
        sb.Append("\r\n[").Append(path).Append("]\r\n");
        foreach (string name in key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(name.Length == 0 ? "@" : "\"" + Escape(name) + "\"").Append('=');
            sb.Append(FormatData(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), key.GetValueKind(name))).Append("\r\n");
        }
        int count = 1;
        foreach (string child in key.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            using RegistryKey? sub = key.OpenSubKey(child);
            if (sub != null) count += Write(sb, sub, path + "\\" + child);
        }
        return count;
    }

    internal static string FormatData(object? value, RegistryValueKind kind) => value switch
    {
        null => "hex(0):",
        int i => "dword:" + unchecked((uint)i).ToString("x8", CultureInfo.InvariantCulture),
        long l => "hex(b):" + HexList(BitConverter.GetBytes(l)),
        byte[] b => "hex:" + HexList(b),
        string[] m => "hex(7):" + HexList(Encoding.Unicode.GetBytes(string.Concat(m.Select(s => s + "\0")) + "\0")),
        string s when kind == RegistryValueKind.ExpandString => "hex(2):" + HexList(Encoding.Unicode.GetBytes(s + "\0")),
        _ => "\"" + Escape(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "") + "\"",
    };

    private static string HexList(byte[] b) => string.Join(",", b.Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
