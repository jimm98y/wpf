// Paths as Windows reads them, for an app written for Windows that runs elsewhere. Windows takes '\' and
// '/' alike as a separator in every path; .NET off Windows takes '/' alone, and a '\' is a character of a
// file's name. So a path an app builds with backslashes -- ICONICS' keypad asks for
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

using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WpfWebGpu.ComInterop;

public static class WindowsPaths
{
    private static readonly bool s_windows = OperatingSystem.IsWindows();

    /// <summary>The path with Windows' separators made this system's.</summary>
    public static string? Native(string? path)
        => path == null || s_windows || path.IndexOf('\\') < 0 ? path : path.Replace('\\', '/');

    private static string[] Native(string[] paths)
    {
        if (paths == null || s_windows) return paths!;
        var copy = new string[paths.Length];
        for (int i = 0; i < paths.Length; i++) copy[i] = Native(paths[i])!;
        return copy;
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

    public static bool File_Exists(string? path) => File.Exists(Native(path));
    public static void File_Delete(string path) => File.Delete(Native(path)!);
    public static void File_Copy(string sourceFileName, string destFileName) => File.Copy(Native(sourceFileName)!, Native(destFileName)!);
    public static void File_Copy(string sourceFileName, string destFileName, bool overwrite) => File.Copy(Native(sourceFileName)!, Native(destFileName)!, overwrite);
    public static void File_Move(string sourceFileName, string destFileName) => File.Move(Native(sourceFileName)!, Native(destFileName)!);
    public static void File_Move(string sourceFileName, string destFileName, bool overwrite) => File.Move(Native(sourceFileName)!, Native(destFileName)!, overwrite);
    public static string File_ReadAllText(string path) => File.ReadAllText(Native(path)!);
    public static string File_ReadAllText(string path, Encoding encoding) => File.ReadAllText(Native(path)!, encoding);
    public static byte[] File_ReadAllBytes(string path) => File.ReadAllBytes(Native(path)!);
    public static string[] File_ReadAllLines(string path) => File.ReadAllLines(Native(path)!);
    public static string[] File_ReadAllLines(string path, Encoding encoding) => File.ReadAllLines(Native(path)!, encoding);
    public static IEnumerable<string> File_ReadLines(string path) => File.ReadLines(Native(path)!);
    public static IEnumerable<string> File_ReadLines(string path, Encoding encoding) => File.ReadLines(Native(path)!, encoding);
    public static void File_WriteAllText(string path, string? contents) => File.WriteAllText(Native(path)!, contents);
    public static void File_WriteAllText(string path, string? contents, Encoding encoding) => File.WriteAllText(Native(path)!, contents, encoding);
    public static void File_WriteAllBytes(string path, byte[] bytes) => File.WriteAllBytes(Native(path)!, bytes);
    public static void File_WriteAllLines(string path, string[] contents) => File.WriteAllLines(Native(path)!, contents);
    public static void File_WriteAllLines(string path, IEnumerable<string> contents) => File.WriteAllLines(Native(path)!, contents);
    public static void File_WriteAllLines(string path, string[] contents, Encoding encoding) => File.WriteAllLines(Native(path)!, contents, encoding);
    public static void File_WriteAllLines(string path, IEnumerable<string> contents, Encoding encoding) => File.WriteAllLines(Native(path)!, contents, encoding);
    public static void File_AppendAllText(string path, string? contents) => File.AppendAllText(Native(path)!, contents);
    public static void File_AppendAllText(string path, string? contents, Encoding encoding) => File.AppendAllText(Native(path)!, contents, encoding);
    public static void File_AppendAllLines(string path, IEnumerable<string> contents) => File.AppendAllLines(Native(path)!, contents);
    public static FileStream File_Open(string path, FileMode mode) => File.Open(Native(path)!, mode);
    public static FileStream File_Open(string path, FileMode mode, FileAccess access) => File.Open(Native(path)!, mode, access);
    public static FileStream File_Open(string path, FileMode mode, FileAccess access, FileShare share) => File.Open(Native(path)!, mode, access, share);
    public static FileStream File_OpenRead(string path) => File.OpenRead(Native(path)!);
    public static FileStream File_OpenWrite(string path) => File.OpenWrite(Native(path)!);
    public static StreamReader File_OpenText(string path) => File.OpenText(Native(path)!);
    public static FileStream File_Create(string path) => File.Create(Native(path)!);
    public static FileStream File_Create(string path, int bufferSize) => File.Create(Native(path)!, bufferSize);
    public static StreamWriter File_CreateText(string path) => File.CreateText(Native(path)!);
    public static StreamWriter File_AppendText(string path) => File.AppendText(Native(path)!);
    public static FileAttributes File_GetAttributes(string path) => File.GetAttributes(Native(path)!);
    public static void File_SetAttributes(string path, FileAttributes fileAttributes) => File.SetAttributes(Native(path)!, fileAttributes);
    public static DateTime File_GetLastWriteTime(string path) => File.GetLastWriteTime(Native(path)!);
    public static DateTime File_GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(Native(path)!);
    public static DateTime File_GetCreationTime(string path) => File.GetCreationTime(Native(path)!);
    public static DateTime File_GetCreationTimeUtc(string path) => File.GetCreationTimeUtc(Native(path)!);
    public static void File_SetLastWriteTime(string path, DateTime lastWriteTime) => File.SetLastWriteTime(Native(path)!, lastWriteTime);
    public static void File_SetLastWriteTimeUtc(string path, DateTime lastWriteTimeUtc) => File.SetLastWriteTimeUtc(Native(path)!, lastWriteTimeUtc);

    // ---- System.IO.Directory ----

    public static bool Directory_Exists(string? path) => Directory.Exists(Native(path));
    public static DirectoryInfo Directory_CreateDirectory(string path) => Directory.CreateDirectory(Native(path)!);
    public static void Directory_Delete(string path) => Directory.Delete(Native(path)!);
    public static void Directory_Delete(string path, bool recursive) => Directory.Delete(Native(path)!, recursive);
    public static void Directory_Move(string sourceDirName, string destDirName) => Directory.Move(Native(sourceDirName)!, Native(destDirName)!);
    public static DirectoryInfo? Directory_GetParent(string path) => Directory.GetParent(Native(path)!);
    public static string[] Directory_GetFiles(string path) => Directory.GetFiles(Native(path)!);
    public static string[] Directory_GetFiles(string path, string searchPattern) => Directory.GetFiles(Native(path)!, searchPattern);
    public static string[] Directory_GetFiles(string path, string searchPattern, SearchOption searchOption) => Directory.GetFiles(Native(path)!, searchPattern, searchOption);
    public static string[] Directory_GetDirectories(string path) => Directory.GetDirectories(Native(path)!);
    public static string[] Directory_GetDirectories(string path, string searchPattern) => Directory.GetDirectories(Native(path)!, searchPattern);
    public static string[] Directory_GetDirectories(string path, string searchPattern, SearchOption searchOption) => Directory.GetDirectories(Native(path)!, searchPattern, searchOption);
    public static string[] Directory_GetFileSystemEntries(string path) => Directory.GetFileSystemEntries(Native(path)!);
    public static string[] Directory_GetFileSystemEntries(string path, string searchPattern) => Directory.GetFileSystemEntries(Native(path)!, searchPattern);
    public static IEnumerable<string> Directory_EnumerateFiles(string path) => Directory.EnumerateFiles(Native(path)!);
    public static IEnumerable<string> Directory_EnumerateFiles(string path, string searchPattern) => Directory.EnumerateFiles(Native(path)!, searchPattern);
    public static IEnumerable<string> Directory_EnumerateFiles(string path, string searchPattern, SearchOption searchOption) => Directory.EnumerateFiles(Native(path)!, searchPattern, searchOption);
    public static IEnumerable<string> Directory_EnumerateDirectories(string path) => Directory.EnumerateDirectories(Native(path)!);
    public static IEnumerable<string> Directory_EnumerateDirectories(string path, string searchPattern) => Directory.EnumerateDirectories(Native(path)!, searchPattern);
    public static IEnumerable<string> Directory_EnumerateDirectories(string path, string searchPattern, SearchOption searchOption) => Directory.EnumerateDirectories(Native(path)!, searchPattern, searchOption);
    public static DateTime Directory_GetLastWriteTime(string path) => Directory.GetLastWriteTime(Native(path)!);
    public static void Directory_SetCurrentDirectory(string path) => Directory.SetCurrentDirectory(Native(path)!);

    // ---- what is made for a path ----

    public static FileInfo FileInfo_new(string fileName) => new FileInfo(Native(fileName)!);
    public static DirectoryInfo DirectoryInfo_new(string path) => new DirectoryInfo(Native(path)!);
    public static FileStream FileStream_new(string path, FileMode mode) => new FileStream(Native(path)!, mode);
    public static FileStream FileStream_new(string path, FileMode mode, FileAccess access) => new FileStream(Native(path)!, mode, access);
    public static FileStream FileStream_new(string path, FileMode mode, FileAccess access, FileShare share) => new FileStream(Native(path)!, mode, access, share);
    public static FileStream FileStream_new(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize) => new FileStream(Native(path)!, mode, access, share, bufferSize);
    public static FileStream FileStream_new(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, bool useAsync) => new FileStream(Native(path)!, mode, access, share, bufferSize, useAsync);
    public static FileStream FileStream_new(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options) => new FileStream(Native(path)!, mode, access, share, bufferSize, options);
    public static StreamReader StreamReader_new(string path) => new StreamReader(Native(path)!);
    public static StreamReader StreamReader_new(string path, bool detectEncodingFromByteOrderMarks) => new StreamReader(Native(path)!, detectEncodingFromByteOrderMarks);
    public static StreamReader StreamReader_new(string path, Encoding encoding) => new StreamReader(Native(path)!, encoding);
    public static StreamReader StreamReader_new(string path, Encoding encoding, bool detectEncodingFromByteOrderMarks) => new StreamReader(Native(path)!, encoding, detectEncodingFromByteOrderMarks);
    public static StreamWriter StreamWriter_new(string path) => new StreamWriter(Native(path)!);
    public static StreamWriter StreamWriter_new(string path, bool append) => new StreamWriter(Native(path)!, append);
    public static StreamWriter StreamWriter_new(string path, bool append, Encoding encoding) => new StreamWriter(Native(path)!, append, encoding);
    public static XmlTextReader XmlTextReader_new(string url) => new XmlTextReader(Native(url)!);
    public static XmlTextWriter XmlTextWriter_new(string filename, Encoding? encoding) => new XmlTextWriter(Native(filename)!, encoding);

    // ---- XML from a file ----

    public static void XmlDocument_Load(XmlDocument document, string filename) => document.Load(Native(filename)!);
    public static void XmlDocument_Save(XmlDocument document, string filename) => document.Save(Native(filename)!);
    public static XmlReader XmlReader_Create(string inputUri) => XmlReader.Create(Native(inputUri)!);
    public static XmlReader XmlReader_Create(string inputUri, XmlReaderSettings? settings) => XmlReader.Create(Native(inputUri)!, settings);
    public static XmlWriter XmlWriter_Create(string outputFileName) => XmlWriter.Create(Native(outputFileName)!);
    public static XmlWriter XmlWriter_Create(string outputFileName, XmlWriterSettings? settings) => XmlWriter.Create(Native(outputFileName)!, settings);
    public static XDocument XDocument_Load(string uri) => XDocument.Load(Native(uri)!);
    public static XDocument XDocument_Load(string uri, LoadOptions options) => XDocument.Load(Native(uri)!, options);
    public static void XDocument_Save(XDocument document, string fileName) => document.Save(Native(fileName)!);
    public static XElement XElement_Load(string uri) => XElement.Load(Native(uri)!);
    public static XElement XElement_Load(string uri, LoadOptions options) => XElement.Load(Native(uri)!, options);
    public static void XElement_Save(XElement element, string fileName) => element.Save(Native(fileName)!);

    // ---- a path as a URI ----
    //
    // Windows makes a file URI of an absolute path ("C:\..."), asked for one that may be relative or
    // absolute; .NET off Windows takes a path that starts with '/' for a relative reference there, which
    // has no LocalPath. ICONICS' display host made a URI of each display template's path that way, and the
    // configurator died at start (InvalidOperationException) as soon as the templates were found at all.

    public static Uri Uri_new(string uriString) => new Uri(AsUri(uriString)!);
    public static Uri Uri_new(string uriString, UriKind uriKind)
    {
        string? s = AsUri(uriString);
        return new Uri(s!, uriKind == UriKind.RelativeOrAbsolute && !s_windows && s != null && s.StartsWith('/') && !s.StartsWith("//") ? UriKind.Absolute : uriKind);
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

    public static Assembly Assembly_LoadFrom(string assemblyFile) => Assembly.LoadFrom(Native(assemblyFile)!);
    public static Assembly Assembly_LoadFile(string path) => Assembly.LoadFile(Native(path)!);
}
