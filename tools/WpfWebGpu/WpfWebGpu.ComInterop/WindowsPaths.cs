// Paths as Windows reads them, for an app written for Windows that runs elsewhere. Windows takes '\' and
// '/' alike as a separator in every path; .NET off Windows takes '/' alone, and a '\' is a character of a
// file's name. So a path an app builds with backslashes -- app' keypad asks for
// "Clients\Layouts\KeyPad\keyPad-en.xml" under its data folder, and the template it is copied from is
// found by Path.GetFileName of that -- names a file that is not there, and the keypad found no layout.
//
// The app's calls of the System.IO members (and the XML loaders, Assembly.LoadFrom) that take a path are
// rewritten off Windows onto these (PathSubstitutions, at load by the launcher's step, at build for a
// bundled head): each takes '\' for the separator, as Windows does, and calls the member itself. A
// method stands in for <Type>.<member> by its name, <Type>_<member> (a constructor: <Type>_new), and by
// its parameters: the member's own, the instance first for an instance member.
//
// On Windows nothing is rewritten, and these would be the members themselves.

using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WpfWebGpu.ComInterop;

public static class WindowsPaths
{
    private static readonly bool s_windows = OperatingSystem.IsWindows();

    /// <summary>The path with Windows' separators made this system's.</summary>
    public static string? Native(string? path) => Paths.WindowsPathCore.Native(path!);

    /// <summary>The path an app hands a member that opens, lists or checks something: Windows' separators
    /// made this system's, and found as Windows' case-insensitive names find it (WindowsPathCore).</summary>
    public static string? Disk(string? path) => Paths.WindowsPathCore.Disk(path!);

    private static string[] Native(string[] paths)
    {
        if (paths == null || s_windows) return paths!;
        var copy = new string[paths.Length];
        for (int i = 0; i < paths.Length; i++) copy[i] = Native(paths[i])!;
        return copy;
    }

    // ---- System.Environment ----
    //
    // What the app was started as. wpfrun starts every app through its host (WpfWebGpu.AppHost), and a page
    // through the bundle's; the runtime's first argument is the host then. The app's own file
    // (WPFWEBGPU_APP_PATH, the .exe it is started as on Windows) is what Windows puts there.

    public static string[] Environment_GetCommandLineArgs()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (AppContext.GetData("WPFWEBGPU_APP_PATH") is not string app || app.Length == 0) return args;
        if (args.Length == 0) return new[] { app };
        var copy = (string[])args.Clone();
        copy[0] = app;
        return copy;
    }

    public static string Environment_get_CommandLine()
    {
        if (AppContext.GetData("WPFWEBGPU_APP_PATH") is not string app || app.Length == 0) return Environment.CommandLine;
        // as Windows' command line reads: each argument quoted where it needs to be
        return string.Join(" ", Environment_GetCommandLineArgs().Select(a =>
            a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\""));
    }

    // ---- System.Diagnostics.Process ----
    //
    // A Windows .NET program an app starts (Workbench's tools menu: the server location utility, the trace
    // configurator) is a PE file this system cannot execute: Process.Start failed, the app took that as
    // the tool not being there, and nothing opened. It is run as the app itself was, through the launcher
    // (WPFWEBGPU_LAUNCHER, set by it): `wpfrun run <exe> <arguments>`. Anything else -- a native program, a
    // document, a URL -- is started as it was.

    public static System.Diagnostics.Process? Process_Start(string fileName)
        => System.Diagnostics.Process.Start(Launchable(new System.Diagnostics.ProcessStartInfo(fileName)));

    public static System.Diagnostics.Process? Process_Start(string fileName, string arguments)
        => System.Diagnostics.Process.Start(Launchable(new System.Diagnostics.ProcessStartInfo(fileName, arguments)));

    public static System.Diagnostics.Process? Process_Start(string fileName, IEnumerable<string> arguments)
        => System.Diagnostics.Process.Start(Launchable(new System.Diagnostics.ProcessStartInfo(fileName, arguments)));

    public static System.Diagnostics.Process? Process_Start(System.Diagnostics.ProcessStartInfo startInfo)
        => System.Diagnostics.Process.Start(Launchable(startInfo));

    public static bool Process_Start(System.Diagnostics.Process process)
    {
        Launchable(process.StartInfo);
        return process.Start();
    }

    /// <summary>The start info made to run a Windows .NET executable through the launcher; any other
    /// unchanged.</summary>
    // WF_TRACE_PROCESS=1: each start an app makes, and whether it goes through the launcher.
    private static readonly bool s_traceProcess = Environment.GetEnvironmentVariable("WF_TRACE_PROCESS") == "1";

    private static System.Diagnostics.ProcessStartInfo Launchable(System.Diagnostics.ProcessStartInfo psi)
    {
        if (s_windows || psi == null || string.IsNullOrEmpty(psi.FileName)) return psi!;
        string? launcher = Environment.GetEnvironmentVariable("WPFWEBGPU_LAUNCHER");
        string exe = Disk(psi.FileName)!;
        bool managed = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe) && IsManagedExecutable(exe);
        if (s_traceProcess)
            Console.Error.WriteLine($"[process] start '{psi.FileName}' args='{psi.Arguments}' shell={psi.UseShellExecute} verb='{psi.Verb}' -> " +
                (string.IsNullOrEmpty(launcher) ? "as is (no launcher)" : managed ? "through the launcher" : File.Exists(exe) ? "as is (not a .NET program)" : "as is (no such file)"));
        if (string.IsNullOrEmpty(launcher) || !managed) return psi;
        psi.FileName = launcher;
        if (psi.ArgumentList.Count > 0)
        {
            psi.ArgumentList.Insert(0, exe);
            psi.ArgumentList.Insert(0, "run");
        }
        else
        {
            psi.Arguments = "run \"" + exe + "\"" + (string.IsNullOrEmpty(psi.Arguments) ? "" : " " + psi.Arguments);
        }
        // Explorer starts an .exe in its own folder unless told otherwise; the launcher runs it there too.
        if (string.IsNullOrEmpty(psi.WorkingDirectory)) psi.WorkingDirectory = Path.GetDirectoryName(exe)!;
        else psi.WorkingDirectory = Disk(psi.WorkingDirectory)!;
        psi.UseShellExecute = false;
        return psi;
    }

    // A .NET program: a PE image with a CLR header (a .NET Framework .exe), or the native host .NET builds
    // as the .exe of an app whose code is the managed .dll beside it.
    private static bool IsManagedExecutable(string path)
        => HasClrHeader(path) || (HasClrHeader(Path.ChangeExtension(path, ".dll")) && File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json")));

    private static bool HasClrHeader(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
            return pe.PEHeaders.CorHeader != null;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    // ---- System.IO.Path ----

    public static string? Path_GetFileName(string? path) => Path.GetFileName(Native(path));
    public static string? Path_GetFileNameWithoutExtension(string? path) => Path.GetFileNameWithoutExtension(Native(path));
    public static string? Path_GetExtension(string? path) => Path.GetExtension(Native(path));
    public static string? Path_GetDirectoryName(string? path) => Path.GetDirectoryName(Native(path));
    public static string? Path_ChangeExtension(string? path, string? extension) => Path.ChangeExtension(Native(path), extension);
    public static bool Path_HasExtension(string? path) => Path.HasExtension(Native(path));
    public static bool Path_IsPathRooted(string? path) => Path.IsPathRooted(Native(path));
    public static string? Path_GetPathRoot(string? path) => Path.GetPathRoot(Native(path));
    public static string Path_GetFullPath(string path) => Path.GetFullPath(Native(path)!);
    public static string Path_GetFullPath(string path, string basePath) => Path.GetFullPath(Native(path)!, Native(basePath)!);
    public static string Path_Combine(string path1, string path2) => Path.Combine(Native(path1)!, Native(path2)!);
    public static string Path_Combine(string path1, string path2, string path3) => Path.Combine(Native(path1)!, Native(path2)!, Native(path3)!);
    public static string Path_Combine(string path1, string path2, string path3, string path4) => Path.Combine(Native(path1)!, Native(path2)!, Native(path3)!, Native(path4)!);
    public static string Path_Combine(string[] paths) => Path.Combine(Native(paths));

    // ---- System.IO.File ----

    public static bool File_Exists(string? path) => File.Exists(Disk(path));
    public static void File_Delete(string path) => File.Delete(Disk(path)!);
    public static void File_Copy(string sourceFileName, string destFileName) => File.Copy(Disk(sourceFileName)!, Disk(destFileName)!);
    public static void File_Copy(string sourceFileName, string destFileName, bool overwrite) => File.Copy(Disk(sourceFileName)!, Disk(destFileName)!, overwrite);
    public static void File_Move(string sourceFileName, string destFileName) => File.Move(Disk(sourceFileName)!, Disk(destFileName)!);
    public static void File_Move(string sourceFileName, string destFileName, bool overwrite) => File.Move(Disk(sourceFileName)!, Disk(destFileName)!, overwrite);
    public static string File_ReadAllText(string path) => File.ReadAllText(Disk(path)!);
    public static string File_ReadAllText(string path, Encoding encoding) => File.ReadAllText(Disk(path)!, encoding);
    public static byte[] File_ReadAllBytes(string path) => File.ReadAllBytes(Disk(path)!);
    public static string[] File_ReadAllLines(string path) => File.ReadAllLines(Disk(path)!);
    public static string[] File_ReadAllLines(string path, Encoding encoding) => File.ReadAllLines(Disk(path)!, encoding);
    public static IEnumerable<string> File_ReadLines(string path) => File.ReadLines(Disk(path)!);
    public static IEnumerable<string> File_ReadLines(string path, Encoding encoding) => File.ReadLines(Disk(path)!, encoding);
    public static void File_WriteAllText(string path, string? contents) => File.WriteAllText(Disk(path)!, contents);
    public static void File_WriteAllText(string path, string? contents, Encoding encoding) => File.WriteAllText(Disk(path)!, contents, encoding);
    public static void File_WriteAllBytes(string path, byte[] bytes) => File.WriteAllBytes(Disk(path)!, bytes);
    public static void File_WriteAllLines(string path, string[] contents) => File.WriteAllLines(Disk(path)!, contents);
    public static void File_WriteAllLines(string path, IEnumerable<string> contents) => File.WriteAllLines(Disk(path)!, contents);
    public static void File_WriteAllLines(string path, string[] contents, Encoding encoding) => File.WriteAllLines(Disk(path)!, contents, encoding);
    public static void File_WriteAllLines(string path, IEnumerable<string> contents, Encoding encoding) => File.WriteAllLines(Disk(path)!, contents, encoding);
    public static void File_AppendAllText(string path, string? contents) => File.AppendAllText(Disk(path)!, contents);
    public static void File_AppendAllText(string path, string? contents, Encoding encoding) => File.AppendAllText(Disk(path)!, contents, encoding);
    public static void File_AppendAllLines(string path, IEnumerable<string> contents) => File.AppendAllLines(Disk(path)!, contents);
    public static FileStream File_Open(string path, FileMode mode) => File.Open(Disk(path)!, mode);
    public static FileStream File_Open(string path, FileMode mode, FileAccess access) => File.Open(Disk(path)!, mode, access);
    public static FileStream File_Open(string path, FileMode mode, FileAccess access, FileShare share) => File.Open(Disk(path)!, mode, access, share);
    public static FileStream File_OpenRead(string path) => File.OpenRead(Disk(path)!);
    public static FileStream File_OpenWrite(string path) => File.OpenWrite(Disk(path)!);
    public static StreamReader File_OpenText(string path) => File.OpenText(Disk(path)!);
    public static FileStream File_Create(string path) => File.Create(Disk(path)!);
    public static FileStream File_Create(string path, int bufferSize) => File.Create(Disk(path)!, bufferSize);
    public static StreamWriter File_CreateText(string path) => File.CreateText(Disk(path)!);
    public static StreamWriter File_AppendText(string path) => File.AppendText(Disk(path)!);
    public static FileAttributes File_GetAttributes(string path) => File.GetAttributes(Disk(path)!);
    public static void File_SetAttributes(string path, FileAttributes fileAttributes) => File.SetAttributes(Disk(path)!, fileAttributes);
    public static DateTime File_GetLastWriteTime(string path) => File.GetLastWriteTime(Disk(path)!);
    public static DateTime File_GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(Disk(path)!);
    public static DateTime File_GetCreationTime(string path) => File.GetCreationTime(Disk(path)!);
    public static DateTime File_GetCreationTimeUtc(string path) => File.GetCreationTimeUtc(Disk(path)!);
    public static void File_SetLastWriteTime(string path, DateTime lastWriteTime) => File.SetLastWriteTime(Disk(path)!, lastWriteTime);
    public static void File_SetLastWriteTimeUtc(string path, DateTime lastWriteTimeUtc) => File.SetLastWriteTimeUtc(Disk(path)!, lastWriteTimeUtc);

    // ---- System.IO.Directory ----

    public static bool Directory_Exists(string? path) => Directory.Exists(Disk(path));
    public static DirectoryInfo Directory_CreateDirectory(string path) => Directory.CreateDirectory(Disk(path)!);
    public static void Directory_Delete(string path) => Directory.Delete(Disk(path)!);
    public static void Directory_Delete(string path, bool recursive) => Directory.Delete(Disk(path)!, recursive);
    public static void Directory_Move(string sourceDirName, string destDirName) => Directory.Move(Disk(sourceDirName)!, Disk(destDirName)!);
    public static DirectoryInfo? Directory_GetParent(string path) => Directory.GetParent(Disk(path)!);
    public static string[] Directory_GetFiles(string path) => Directory.GetFiles(Disk(path)!);
    public static string[] Directory_GetFiles(string path, string searchPattern) => Directory.GetFiles(Disk(path)!, searchPattern);
    public static string[] Directory_GetFiles(string path, string searchPattern, SearchOption searchOption) => Directory.GetFiles(Disk(path)!, searchPattern, searchOption);
    public static string[] Directory_GetDirectories(string path) => Directory.GetDirectories(Disk(path)!);
    public static string[] Directory_GetDirectories(string path, string searchPattern) => Directory.GetDirectories(Disk(path)!, searchPattern);
    public static string[] Directory_GetDirectories(string path, string searchPattern, SearchOption searchOption) => Directory.GetDirectories(Disk(path)!, searchPattern, searchOption);
    public static string[] Directory_GetFileSystemEntries(string path) => Directory.GetFileSystemEntries(Disk(path)!);
    public static string[] Directory_GetFileSystemEntries(string path, string searchPattern) => Directory.GetFileSystemEntries(Disk(path)!, searchPattern);
    public static IEnumerable<string> Directory_EnumerateFiles(string path) => Directory.EnumerateFiles(Disk(path)!);
    public static IEnumerable<string> Directory_EnumerateFiles(string path, string searchPattern) => Directory.EnumerateFiles(Disk(path)!, searchPattern);
    public static IEnumerable<string> Directory_EnumerateFiles(string path, string searchPattern, SearchOption searchOption) => Directory.EnumerateFiles(Disk(path)!, searchPattern, searchOption);
    public static IEnumerable<string> Directory_EnumerateDirectories(string path) => Directory.EnumerateDirectories(Disk(path)!);
    public static IEnumerable<string> Directory_EnumerateDirectories(string path, string searchPattern) => Directory.EnumerateDirectories(Disk(path)!, searchPattern);
    public static IEnumerable<string> Directory_EnumerateDirectories(string path, string searchPattern, SearchOption searchOption) => Directory.EnumerateDirectories(Disk(path)!, searchPattern, searchOption);
    public static DateTime Directory_GetLastWriteTime(string path) => Directory.GetLastWriteTime(Disk(path)!);
    public static void Directory_SetCurrentDirectory(string path) => Directory.SetCurrentDirectory(Disk(path)!);

    // ---- what is made for a path ----

    public static FileInfo FileInfo_new(string fileName) => new FileInfo(Disk(fileName)!);
    public static DirectoryInfo DirectoryInfo_new(string path) => new DirectoryInfo(Disk(path)!);
    public static FileStream FileStream_new(string path, FileMode mode) => new FileStream(Disk(path)!, mode);
    public static FileStream FileStream_new(string path, FileMode mode, FileAccess access) => new FileStream(Disk(path)!, mode, access);
    public static FileStream FileStream_new(string path, FileMode mode, FileAccess access, FileShare share) => new FileStream(Disk(path)!, mode, access, share);
    public static FileStream FileStream_new(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize) => new FileStream(Disk(path)!, mode, access, share, bufferSize);
    public static FileStream FileStream_new(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, bool useAsync) => new FileStream(Disk(path)!, mode, access, share, bufferSize, useAsync);
    public static FileStream FileStream_new(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options) => new FileStream(Disk(path)!, mode, access, share, bufferSize, options);
    public static StreamReader StreamReader_new(string path) => new StreamReader(Disk(path)!);
    public static StreamReader StreamReader_new(string path, bool detectEncodingFromByteOrderMarks) => new StreamReader(Disk(path)!, detectEncodingFromByteOrderMarks);
    public static StreamReader StreamReader_new(string path, Encoding encoding) => new StreamReader(Disk(path)!, encoding);
    public static StreamReader StreamReader_new(string path, Encoding encoding, bool detectEncodingFromByteOrderMarks) => new StreamReader(Disk(path)!, encoding, detectEncodingFromByteOrderMarks);
    public static StreamWriter StreamWriter_new(string path) => new StreamWriter(Disk(path)!);
    public static StreamWriter StreamWriter_new(string path, bool append) => new StreamWriter(Disk(path)!, append);
    public static StreamWriter StreamWriter_new(string path, bool append, Encoding encoding) => new StreamWriter(Disk(path)!, append, encoding);
    public static XmlTextReader XmlTextReader_new(string url) => new XmlTextReader(DiskOrUri(url)!);
    public static XmlTextWriter XmlTextWriter_new(string filename, Encoding? encoding) => new XmlTextWriter(Disk(filename)!, encoding);

    // ---- XML from a file ----
    //
    // The loaders take a URI as well as a path, and a URI is not a path: app reads
    // AGConfigurationMode.xml by its file URI next to its assemblies, and made a path of it -- "/file:/..."
    // -- found no file, and so never knew it could be in web (AnyGlass) mode: its title and status bars
    // stayed the desktop's green in every mode.

    /// <summary>A loader's argument: a URI as it is -- a file URI's path read as Windows reads it -- and a
    /// path as Disk reads it. "C:\..." is a path (a drive, not a scheme).</summary>
    public static string? DiskOrUri(string? s)
    {
        if (s == null || s_windows) return s;
        bool drive = s.Length >= 2 && char.IsAsciiLetter(s[0]) && s[1] == ':';
        if (!drive && s.Contains(':') && Uri.TryCreate(s, UriKind.Absolute, out Uri? uri))
            return uri.IsFile ? Disk(uri.LocalPath) : s;
        return Disk(s);
    }

    public static void XmlDocument_Load(XmlDocument document, string filename) => document.Load(DiskOrUri(filename)!);
    public static void XmlDocument_Save(XmlDocument document, string filename) => document.Save(Disk(filename)!);
    public static XmlReader XmlReader_Create(string inputUri) => XmlReader.Create(DiskOrUri(inputUri)!);
    public static XmlReader XmlReader_Create(string inputUri, XmlReaderSettings? settings) => XmlReader.Create(DiskOrUri(inputUri)!, settings);
    public static XmlWriter XmlWriter_Create(string outputFileName) => XmlWriter.Create(Disk(outputFileName)!);
    public static XmlWriter XmlWriter_Create(string outputFileName, XmlWriterSettings? settings) => XmlWriter.Create(Disk(outputFileName)!, settings);
    public static XDocument XDocument_Load(string uri) => XDocument.Load(DiskOrUri(uri)!);
    public static XDocument XDocument_Load(string uri, LoadOptions options) => XDocument.Load(DiskOrUri(uri)!, options);
    public static void XDocument_Save(XDocument document, string fileName) => document.Save(Disk(fileName)!);
    public static XElement XElement_Load(string uri) => XElement.Load(DiskOrUri(uri)!);
    public static XElement XElement_Load(string uri, LoadOptions options) => XElement.Load(DiskOrUri(uri)!, options);
    public static void XElement_Save(XElement element, string fileName) => element.Save(Disk(fileName)!);

    // ---- a path as a URI ----
    //
    // Windows makes a file URI of an absolute path ("C:\..."), asked for one that may be relative or
    // absolute; .NET off Windows takes a path that starts with '/' for a relative reference there, which
    // has no LocalPath. app' display host made a URI of each display template's path that way, and the
    // configurator died at start (InvalidOperationException) as soon as the templates were found at all.
    //
    // Only a path that is one, though: a leading '/' is also how a WPF application writes a reference to
    // a resource -- "/Telerik.Windows.Controls.RibbonView;component/themes/RibbonWindow.xaml", "/Images/
    // a.png" -- which Windows keeps relative too. Made absolute, Telerik's RadRibbonWindow could not load
    // its template (Application.LoadComponent: "Cannot use absolute URI") and Workbench did not start. So a
    // '/' string is a file URI where it names something on this disk: the file or folder itself, or a
    // folder it would be in below the root.

    public static Uri Uri_new(string uriString) => new Uri(AsUri(uriString)!);
    public static Uri Uri_new(string uriString, UriKind uriKind)
    {
        string? s = AsUri(uriString);
        return new Uri(s!, uriKind == UriKind.RelativeOrAbsolute && IsLocalPath(s) ? UriKind.Absolute : uriKind);
    }

    private static bool IsLocalPath(string? s)
    {
        if (s_windows || s == null || !s.StartsWith('/') || s.StartsWith("//") || s.Contains(";component/", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            if (File.Exists(s) || Directory.Exists(s)) return true;
            string? folder = Path.GetDirectoryName(s);
            return !string.IsNullOrEmpty(folder) && folder != "/" && Directory.Exists(folder);
        }
        catch (Exception) { return false; }
    }

    // a path with Windows' separators made this system's, when it is one -- rooted once they are; a
    // relative reference keeps them (Windows keeps them in a relative URI too)
    private static string? AsUri(string? s)
    {
        if (s == null || s_windows || s.IndexOf('\\') < 0) return s;
        string native = Native(s)!;
        return native.StartsWith('/') ? native : s;
    }

    // ---- assemblies from a file ----

    public static Assembly Assembly_LoadFrom(string assemblyFile) => Assembly.LoadFrom(Disk(assemblyFile)!);
    public static Assembly Assembly_LoadFile(string path) => Assembly.LoadFile(Disk(path)!);
}
