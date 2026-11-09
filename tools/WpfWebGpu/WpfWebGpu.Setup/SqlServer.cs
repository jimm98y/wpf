// The SQL Server the installation brings (sqlserver/ in the payload: Microsoft's SQL Server Express setup), installed
// unless the person installing says no -- asked, chosen by default; /sql or /nosql answer it, /quiet takes the default
// -- and not where the instance is there already (an upgrade, a machine that has one). Unattended: the database
// engine only, the instance named as the installation's configuration expects it (the one its registry registered),
// TCP on, Windows and SQL logins -- its sysadmins the machine's Administrators and the accounts the installation's
// services run as, whose configuration connects with Windows authentication -- and an sa password made here (kept
// where only administrators read it).
// On Windows on Arm the same x64 setup runs, in Windows' emulation.

using System.Diagnostics;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;

namespace WpfWebGpu.Setup;

internal static class SqlServer
{
    /// <summary>Installs it from <paramref name="zip"/> if the installation carries it and it is wanted.</summary>
    public static void Install(ZipArchive zip, SetupInfo info, string[] args, bool quiet, IEnumerable<string> serviceAccounts)
    {
        if (info.SqlServer is not { } sql) return;
        ZipArchiveEntry? media = zip.GetEntry("sqlserver/" + sql.Media);
        if (media == null) return;
        if (InstanceExists(sql.Instance))
        {
            Console.WriteLine($"  {sql.Engine}: instance {sql.Instance} is installed already");
            return;
        }
        if (!Wanted(sql, args, quiet))
        {
            Console.WriteLine($"  {sql.Engine}: left out");
            return;
        }

        string temp = Path.Combine(Path.GetTempPath(), "sqlserver-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            Console.WriteLine($"  {sql.Engine}: unpacking its setup");
            string exe = Path.Combine(temp, sql.Media);
            media.ExtractToFile(exe);
            string extracted = Path.Combine(temp, "media");
            Run(exe, $"/q /x:\"{extracted}\"", "unpacking");

            string password = Password();
            string sysadmins = string.Join(" ", serviceAccounts
                .Select(a => a.Replace(" ", "") is var bare && (bare.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
                    || bare.Equals(@".\LocalSystem", StringComparison.OrdinalIgnoreCase)) ? @"NT AUTHORITY\SYSTEM" : a)
                .Prepend(@"BUILTIN\Administrators").Distinct(StringComparer.OrdinalIgnoreCase).Select(a => $"\"{a}\""));
            Console.WriteLine($"  {sql.Engine}: installing instance {sql.Instance} (this takes several minutes)");
            Run(Path.Combine(extracted, "SETUP.EXE"),
                "/Q /ACTION=Install /FEATURES=SQLEngine /IACCEPTSQLSERVERLICENSETERMS /SUPPRESSPRIVACYSTATEMENTNOTICE /UPDATEENABLED=0 "
                + $"/INSTANCENAME={sql.Instance} /SQLSYSADMINACCOUNTS={sysadmins}/SECURITYMODE=SQL /SAPWD=\"{password}\" "
                + "/TCPENABLED=1 /BROWSERSVCSTARTUPTYPE=Automatic /SQLSVCSTARTUPTYPE=Automatic",
                "installing");
            string kept = Keep(info, sql, password);
            Console.WriteLine($"  {sql.Engine}: .\\{sql.Instance}, login sa, password {password}");
            Console.WriteLine($"    (kept in {kept}, readable by administrators)");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static bool Wanted(SetupSqlServer sql, string[] args, bool quiet)
    {
        if (args.Any(a => a.Equals("/nosql", StringComparison.OrdinalIgnoreCase))) return false;
        if (args.Any(a => a.Equals("/sql", StringComparison.OrdinalIgnoreCase)) || quiet || Console.IsInputRedirected) return true;
        Console.Write($"Install {sql.Engine} for the installation's databases? [Y/n] ");
        string? answer = Console.ReadLine();
        return answer == null || !answer.TrimStart().StartsWith('n') && !answer.TrimStart().StartsWith('N');
    }

    private static bool InstanceExists(string instance)
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
        return key?.GetValue(instance) != null;
    }

    private static void Run(string exe, string arguments, string what)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException($"{exe} did not start");
        p.WaitForExit();
        // 3010: done, a restart wanted to finish
        if (p.ExitCode is not (0 or 3010))
            throw new InvalidOperationException($"SQL Server {what} failed (exit code 0x{p.ExitCode:x8}); its log is in "
                + @"%ProgramFiles%\Microsoft SQL Server\170\Setup Bootstrap\Log");
    }

    // SQL Server's policy: at least eight characters, of three of upper case, lower case, digits and symbols
    private static string Password()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        Span<byte> random = stackalloc byte[20];
        RandomNumberGenerator.Fill(random);
        var chars = new char[random.Length];
        for (int i = 0; i < random.Length; i++) chars[i] = alphabet[random[i] % alphabet.Length];
        return "Sq1-" + new string(chars);
    }

    /// <summary>The password into ProgramData\&lt;product&gt;\sqlserver.txt, readable by administrators and the
    /// system only.</summary>
    private static string Keep(SetupInfo info, SetupSqlServer sql, string password)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), info.Product);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "sqlserver.txt");
        File.WriteAllText(file, $"{sql.Engine}\r\nserver: .\\{sql.Instance}\r\nlogin: sa\r\npassword: {password}\r\n");
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(acl);
        return file;
    }
}
