// The installation's registry into Windows' own: each hive as the Packager staged it (the registry stand-in's
// format: [key], "name"=kind:value, '%XX' escapes), its paths' tokens made this installation's. A value the machine
// already has is left as it is: an upgrade keeps what the machine has made of its settings. What it sets, and the keys
// it makes, go into the installation's record (InstallRecord.cs) for uninstall /all; Remove takes them away again.

using Microsoft.Win32;

namespace WpfWebGpu.Setup;

internal static class RegistryImport
{
    private static RegistryKey? Root(string hive, string? userSid = null)
    {
        // the installing user's, where this runs as another (Windows Installer's custom actions run as SYSTEM)
        if (hive == "HKEY_CURRENT_USER" && userSid is { Length: > 0 } && userSid != System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value)
        {
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
            return users.OpenSubKey(userSid, writable: true);
        }
        return hive switch
        {
            "HKEY_LOCAL_MACHINE" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
            "HKEY_CURRENT_USER" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
            "HKEY_CLASSES_ROOT" => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64),
            "HKEY_USERS" => RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64),
            _ => null,
        };
    }

    /// <summary>The hive's keys and values into the registry, what the machine has not yet into the record. With
    /// <paramref name="write"/> false only the record (what an MSI's Registry table is about to write); with
    /// <paramref name="kinds"/> only the values of those kinds written (what the table cannot carry).</summary>
    public static int Import(string hive, string text, IReadOnlyDictionary<string, string> tokens, InstallRecord record,
        bool write = true, string? userSid = null, Func<string, bool>? kinds = null)
    {
        RegistryKey? root = Root(hive, userSid);
        if (root == null) return 0;
        string currentPath = "";
        int n = 0;
        RegistryKey? current = null;
        string Resolve(string s) { foreach (var (t, v) in tokens) s = s.Replace(t, v, StringComparison.Ordinal); return s; }
        foreach (var line in HiveText.Read(text))
        {
            if (line.Name == null)
            {
                current?.Dispose();
                string path = line.Key;
                // the keys it makes, from the outermost not there yet
                string[] parts = path.Split('\\');
                for (int i = 1; i <= parts.Length; i++)
                {
                    string sub = string.Join('\\', parts, 0, i);
                    using var existing = root.OpenSubKey(sub);
                    if (existing == null) { record.RegistryKeys.Add(new RegistryItem(hive, sub, null)); break; }
                }
                current = write ? root.CreateSubKey(path, writable: true) : root.OpenSubKey(path);
                currentPath = path;
                continue;
            }
            if (kinds != null && !kinds(line.Kind!)) continue;
            if (current?.GetValue(line.Name) != null) continue;
            if (write && current != null)
            {
                switch (line.Kind)
                {
                    case "str": current.SetValue(line.Name, Resolve(HiveText.Text(line)), RegistryValueKind.String); break;
                    case "expand": current.SetValue(line.Name, Resolve(HiveText.Text(line)), RegistryValueKind.ExpandString); break;
                    case "dword": current.SetValue(line.Name, unchecked((int)HiveText.Dword(line)), RegistryValueKind.DWord); break;
                    case "qword": current.SetValue(line.Name, unchecked((long)HiveText.Qword(line)), RegistryValueKind.QWord); break;
                    case "multi": current.SetValue(line.Name, HiveText.Text(line).Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(Resolve).ToArray(), RegistryValueKind.MultiString); break;
                    case "bin": current.SetValue(line.Name, HiveText.Binary(line), RegistryValueKind.Binary); break;
                    default: continue;
                }
            }
            else if (line.Kind is not ("str" or "expand" or "dword" or "qword" or "multi" or "bin")) continue;
            record.RegistryValues.Add(new RegistryItem(hive, currentPath, line.Name));
            n++;
        }
        current?.Dispose();
        root.Dispose();
        return n;
    }

    /// <summary>The values and keys the record lists, taken away (values first, the keys it made with all under them);
    /// HKEY_CURRENT_USER is the installing user's (by their SID under HKEY_USERS, where their hive is loaded).</summary>
    public static int Remove(InstallRecord record)
    {
        int n = 0;
        RegistryKey? Open(string hive)
        {
            if (hive == "HKEY_CURRENT_USER" && record.UserSid is { } sid && sid != System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value)
            {
                var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
                var hku = users.OpenSubKey(sid, writable: true);
                users.Dispose();
                return hku;
            }
            return Root(hive);
        }
        foreach (var v in record.RegistryValues)
        {
            using var root = Open(v.Hive);
            using var key = root?.OpenSubKey(v.Key, writable: true);
            if (key?.GetValue(v.Name) == null) continue;
            key.DeleteValue(v.Name!, throwOnMissingValue: false);
            n++;
        }
        foreach (var k in record.RegistryKeys.OrderByDescending(k => k.Key.Length))
        {
            using var root = Open(k.Hive);
            try { root?.DeleteSubKeyTree(k.Key, throwOnMissingSubKey: false); }
            catch (UnauthorizedAccessException) { Console.WriteLine($"  could not remove {k.Hive}\\{k.Key}"); }
        }
        return n;
    }
}
