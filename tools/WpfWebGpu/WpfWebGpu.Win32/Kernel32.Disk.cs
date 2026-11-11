// kernel32's disk space queries: the file system that holds the directory, as the OS reports it (statvfs, through
// DriveInfo) -- a Windows path read as the stack reads every path, a missing directory refused as Windows refuses
// it (ERROR_PATH_NOT_FOUND), no directory at all meaning the current one's disk.

using System.IO;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    private const int ErrorPathNotFound = 3;

    [Win32Export("GetDiskFreeSpaceExW")]
    public static int GetDiskFreeSpaceExW(char* directory, ulong* freeToCaller, ulong* total, ulong* totalFree)
        => DiskFreeSpaceEx(ReadUtf16(directory), freeToCaller, total, totalFree);

    [Win32Export("GetDiskFreeSpaceExA")]
    public static int GetDiskFreeSpaceExA(byte* directory, ulong* freeToCaller, ulong* total, ulong* totalFree)
        => DiskFreeSpaceEx(directory == null ? null : new string((sbyte*)directory), freeToCaller, total, totalFree);

    [Win32Export("GetDiskFreeSpaceW")]
    public static int GetDiskFreeSpaceW(char* root, uint* sectorsPerCluster, uint* bytesPerSector, uint* freeClusters, uint* totalClusters)
        => DiskFreeSpace(ReadUtf16(root), sectorsPerCluster, bytesPerSector, freeClusters, totalClusters);

    [Win32Export("GetDiskFreeSpaceA")]
    public static int GetDiskFreeSpaceA(byte* root, uint* sectorsPerCluster, uint* bytesPerSector, uint* freeClusters, uint* totalClusters)
        => DiskFreeSpace(root == null ? null : new string((sbyte*)root), sectorsPerCluster, bytesPerSector, freeClusters, totalClusters);

    private static int DiskFreeSpaceEx(string? directory, ulong* freeToCaller, ulong* total, ulong* totalFree)
    {
        if (Drive(directory) is not DriveInfo drive) return FALSE;
        if (freeToCaller != null) *freeToCaller = (ulong)drive.AvailableFreeSpace;     // what this user may still use
        if (total != null) *total = (ulong)drive.TotalSize;
        if (totalFree != null) *totalFree = (ulong)drive.TotalFreeSpace;
        return TRUE;
    }

    // the old form counts clusters in 32 bits: NTFS's 4 KB clusters of 512-byte sectors, capped as Windows caps
    // them for a disk past 16 TB (the Ex form is the one that tells the truth there)
    private static int DiskFreeSpace(string? root, uint* sectorsPerCluster, uint* bytesPerSector, uint* freeClusters, uint* totalClusters)
    {
        if (Drive(root) is not DriveInfo drive) return FALSE;
        const uint sector = 512, perCluster = 8, cluster = sector * perCluster;
        if (sectorsPerCluster != null) *sectorsPerCluster = perCluster;
        if (bytesPerSector != null) *bytesPerSector = sector;
        if (freeClusters != null) *freeClusters = (uint)Math.Min((ulong)drive.AvailableFreeSpace / cluster, uint.MaxValue);
        if (totalClusters != null) *totalClusters = (uint)Math.Min((ulong)drive.TotalSize / cluster, uint.MaxValue);
        return TRUE;
    }

    private static DriveInfo? Drive(string? directory)
    {
        string? path = string.IsNullOrEmpty(directory) ? Environment.CurrentDirectory : WpfWebGpu.ComInterop.WindowsPaths.Disk(directory);
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) { SetLastError(ErrorPathNotFound); return null; }
        try
        {
            var drive = new DriveInfo(path);
            _ = drive.TotalSize;            // a path the OS cannot measure fails here, not halfway through the answer
            return drive;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetLastError(ErrorPathNotFound);
            return null;
        }
    }
}
