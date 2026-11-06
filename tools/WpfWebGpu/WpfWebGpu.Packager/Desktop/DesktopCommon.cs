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

    public static string AppName(AppEntry app) => app.Name.Length > 0 ? app.Name : Path.GetFileNameWithoutExtension(app.Exe);
}
