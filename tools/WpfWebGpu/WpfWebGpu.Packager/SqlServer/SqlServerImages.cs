// The SQL Server a Linux or macOS installation runs, as Microsoft publishes it for containers: pulled from Microsoft's
// registry when the installation is installed, never carried in it -- an image is a Linux system (its userland GPL),
// and the installers carry only what they may redistribute. On x64 (and on a Mac, in Rosetta) SQL Server 2025
// Express; on Arm Linux Azure SQL Edge, as SQL Server is published for x64 only and does not run in qemu's emulation.

namespace WpfWebGpu.Packager.SqlServer;

/// <param name="Engine">what it is, as the installer says it</param>
/// <param name="Image">its image, registry included</param>
/// <param name="Platform">the image's platform (linux/amd64, linux/arm64)</param>
/// <param name="Pid">its edition (MSSQL_PID)</param>
/// <param name="Eula">the value its ACCEPT_EULA wants</param>
public sealed record SqlServerImage(string Engine, string Image, string Platform, string Pid, string Eula)
{
    public static readonly SqlServerImage X64 = new("SQL Server 2025 Express", "mcr.microsoft.com/mssql/server:2025-latest", "linux/amd64", "Express", "Y");
    public static readonly SqlServerImage Arm64 = new("Azure SQL Edge", "mcr.microsoft.com/azure-sql-edge:latest", "linux/arm64", "Developer", "1");

    /// <summary>The image for a Linux machine of <paramref name="arch"/> (amd64, arm64).</summary>
    public static SqlServerImage ForLinux(string arch) => arch == "arm64" ? Arm64 : X64;
}
