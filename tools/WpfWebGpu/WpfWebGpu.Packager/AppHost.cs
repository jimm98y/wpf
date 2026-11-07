// The app a .NET executable starts. The apphost the SDK writes beside every app holds its app's path (App.dll,
// relative to it) where its placeholder was, and starts that app whatever the executable is called: a product
// carries one app under App.exe, App_ARM64.exe and App_x86.exe, each the apphost for its processor, as Windows
// runs whichever its installation has.

using System.Text;

namespace WpfWebGpu.Packager;

#if APPHOST_INTERNAL
internal
#else
public
#endif
static class AppHost
{
    private const int MaxSize = 16 << 20;   // an apphost is a few hundred KB; a single-file bundle carries its app inside

    /// <summary>The full path of the app <paramref name="exe"/> starts: the .dll its apphost names, where it is a
    /// framework-dependent app beside it; else <c>App.dll</c> for <c>App.exe</c>, as before; null for neither.</summary>
    public static string? AppDll(string exe)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        string same = Path.ChangeExtension(exe, ".dll");
        if (IsApp(same)) return same;
        try
        {
            var info = new FileInfo(exe);
            if (!info.Exists || info.Length > MaxSize) return null;
            byte[] image = File.ReadAllBytes(exe);
            byte[] dll = Encoding.ASCII.GetBytes(".dll\0");
            for (int at = image.AsSpan().IndexOf(dll); at >= 0;)
            {
                int start = at;
                while (start > 0 && image[start - 1] >= 0x20 && image[start - 1] < 0x7F && at - start < 1024) start--;
                if (start > 0 && image[start - 1] == 0 && at > start)
                {
                    string relative = Encoding.UTF8.GetString(image, start, at - start + 4).Replace('\\', '/');
                    if (!Path.IsPathRooted(relative))
                    {
                        string candidate = Path.GetFullPath(Path.Combine(dir, relative));
                        if (IsApp(candidate)) return candidate;
                    }
                }
                int next = image.AsSpan(at + dll.Length).IndexOf(dll);
                at = next < 0 ? -1 : at + dll.Length + next;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static bool IsApp(string dll) => File.Exists(dll) && File.Exists(Path.ChangeExtension(dll, ".runtimeconfig.json"));
}
