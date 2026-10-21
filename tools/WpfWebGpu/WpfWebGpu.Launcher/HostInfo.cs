using System.Runtime.InteropServices;

namespace WpfWebGpu;

/// <summary>What the machine wpfrun is running on looks like to the .NET host.</summary>
internal static class HostInfo
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMacOS => OperatingSystem.IsMacOS();

    /// <summary>The portable RID of this process, e.g. osx-arm64 or win-x64.</summary>
    public static string Rid
    {
        get
        {
            string os = IsWindows ? "win" : IsMacOS ? "osx" : "linux";
            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                Architecture.Arm => "arm",
                var a => a.ToString().ToLowerInvariant(),
            };
            return $"{os}-{arch}";
        }
    }

    /// <summary>
    /// The RIDs whose runtimeTargets the host applies on this machine, most specific first. Since
    /// .NET 8 the host uses this fixed portable chain rather than the full RID graph.
    /// </summary>
    public static IReadOnlyList<string> RidChain(string rid)
    {
        int dash = rid.IndexOf('-');
        string os = dash < 0 ? rid : rid[..dash];
        var chain = new List<string> { rid, os };
        if (os != "win") chain.Add("unix");
        chain.Add("any");
        return chain;
    }

    /// <summary>The dotnet muxer that runs the app: the one running wpfrun, wherever it is.</summary>
    public static string DotnetMuxer()
    {
        string exe = IsWindows ? "dotnet.exe" : "dotnet";
        string? self = Environment.ProcessPath;
        if (self != null && string.Equals(Path.GetFileName(self), exe, StringComparison.OrdinalIgnoreCase))
        {
            return self;
        }

        // An apphost: the runtime lives in <dotnet root>/shared/Microsoft.NETCore.App/<version>/.
        string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        string? root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(runtimeDir))));
        if (root != null && File.Exists(Path.Combine(root, exe)))
        {
            return Path.Combine(root, exe);
        }

        string? env = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, exe)))
        {
            return Path.Combine(env, exe);
        }
        return exe;                       // PATH
    }
}
