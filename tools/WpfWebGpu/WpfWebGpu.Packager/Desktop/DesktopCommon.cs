// What the desktop installers share: names made safe for each system, the product's icon.

using System.Text;

namespace WpfWebGpu.Packager.Desktop;

internal static class DesktopCommon
{
    /// <summary>A name as an identifier's segment: lower case letters, digits and '-'.</summary>
    public static string Segment(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.ToLowerInvariant()) if (char.IsAsciiLetterOrDigit(c) || c == '-') sb.Append(c);
        return sb.Length == 0 ? "app" : sb.ToString();
    }

    public static string FileName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));

    /// <summary>The app's own icon (its .exe's), else the product's.</summary>
    public static (byte[] Rgba, int Width, int Height)? Icon(PackageManifest manifest, AppEntry? app = null)
    {
        if (app != null && PeFile.ReadIcon(Path.Combine(manifest.Inputs.InstallDir, app.Exe)) is { } own && Icons.Largest(own) is { } image) return image;
        if (manifest.Product.Icon.Length == 0) return null;
        string p = Path.Combine(manifest.Inputs.InstallDir, manifest.Product.Icon);
        byte[]? ico = p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? PeFile.ReadIcon(p) : File.Exists(p) ? File.ReadAllBytes(p) : null;
        return ico == null ? null : Icons.Largest(ico);
    }

    /// <summary>Whether a program of the installation serves through http.sys (references its server assembly):
    /// then the package installs the machine's http.sys service (wpfrun httpsys), which owns the ports.</summary>
    public static bool UsesHttpSys(PackageManifest manifest)
    {
        byte[] name = System.Text.Encoding.ASCII.GetBytes("Microsoft.AspNetCore.Server.HttpSys");
        var excludes = new GlobSet(manifest.Exclude);
        foreach (string dll in Directory.EnumerateFiles(manifest.Inputs.InstallDir, "*.dll", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(manifest.Inputs.InstallDir, dll).Replace('\\', '/');
            if (excludes.Matches(rel) || Path.GetFileNameWithoutExtension(dll).Equals("Microsoft.AspNetCore.Server.HttpSys", StringComparison.OrdinalIgnoreCase)) continue;
            var info = new FileInfo(dll);
            if (info.Length > 64 * 1024 * 1024) continue;
            if (File.ReadAllBytes(dll).AsSpan().IndexOf(name) >= 0) return true;
        }
        return false;
    }

    /// <summary>The system account a service account is on macOS or Linux (the installers make it where it is
    /// missing; the stack takes it for Windows' own: NETWORK SERVICE, LOCAL SERVICE); null for LocalSystem (root).</summary>
    public static string? SystemAccount(ServiceAccount account, bool mac) => account switch
    {
        ServiceAccount.NetworkService => mac ? "_wpfnetworkservice" : "wpf-networkservice",
        ServiceAccount.LocalService => mac ? "_wpflocalservice" : "wpf-localservice",
        _ => null,
    };

    public static string AccountTitle(ServiceAccount account) => account == ServiceAccount.LocalService ? "LOCAL SERVICE" : "NETWORK SERVICE";

    public static string AppName(AppEntry app) => app.Name.Length > 0 ? app.Name : Path.GetFileNameWithoutExtension(app.Exe);
}
