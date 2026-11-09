// WScript.Shell, the Windows Script Host's shell object (wshom.ocx), off Windows: what an app makes from its ProgID
// to create and read shortcuts, find the special folders, expand environment strings, read and write the registry
// and run a command. Its shortcuts are COM objects as wshom's are: a cast to the app's own [ComImport]
// IWshShortcut reaches the shortcut through its vtable (ComCcw below), and late binding (InvokeMember, dynamic)
// reaches it by its members. The links themselves are Windows' .lnk files (ShellLinkFile) and .url files.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WpfWebGpu.ComInterop;

/// <summary>An object of a COM server this runs itself: a cast to a COM interface it does not implement reaches it
/// through its CCW, as the server's own object would be reached through its interfaces.</summary>
internal interface IComServerObject { }

/// <summary>WScript.Shell (IWshShell3).</summary>
[Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8"), ComVisible(true)]
public sealed class WshShell : IComServerObject
{
    public WshCollection SpecialFolders { get; } = new(SpecialFolderNames.Select(n => (object)n.Name).ToArray(), name =>
        SpecialFolderNames.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } f
            ? WindowsPaths.Environment_GetFolderPath(f.Folder) : "");

    private static readonly (string Name, Environment.SpecialFolder Folder)[] SpecialFolderNames =
    {
        ("AllUsersDesktop", Environment.SpecialFolder.CommonDesktopDirectory), ("AllUsersStartMenu", Environment.SpecialFolder.CommonStartMenu),
        ("AllUsersPrograms", Environment.SpecialFolder.CommonPrograms), ("AllUsersStartup", Environment.SpecialFolder.CommonStartup),
        ("Desktop", Environment.SpecialFolder.DesktopDirectory), ("Favorites", Environment.SpecialFolder.Favorites),
        ("Fonts", Environment.SpecialFolder.Fonts), ("MyDocuments", Environment.SpecialFolder.MyDocuments),
        ("NetHood", Environment.SpecialFolder.NetworkShortcuts), ("PrintHood", Environment.SpecialFolder.PrinterShortcuts),
        ("Programs", Environment.SpecialFolder.Programs), ("Recent", Environment.SpecialFolder.Recent),
        ("SendTo", Environment.SpecialFolder.SendTo), ("StartMenu", Environment.SpecialFolder.StartMenu),
        ("Startup", Environment.SpecialFolder.Startup), ("Templates", Environment.SpecialFolder.Templates),
    };

    /// <summary>A shortcut object for the link at <paramref name="PathLink"/> (.lnk, or .url for an Internet
    /// shortcut), the link's own settings when the file is there; nothing is written until Save.</summary>
    public object CreateShortcut(string PathLink)
    {
        string full = WindowsPaths.Path_GetFullPath(PathLink);
        if (full.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) return new WshUrlShortcut(full);
        if (!full.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            throw new COMException("The shortcut pathname must end with .lnk or .url.", unchecked((int)0x80020009));
        return new WshShortcut(full);
    }

    public string ExpandEnvironmentStrings(string Src) => Environment.ExpandEnvironmentVariables(Src);

    public string CurrentDirectory
    {
        get => Environment.CurrentDirectory;
        set => Environment.CurrentDirectory = WindowsPaths.Native(value)!;
    }

    // ---- the registry: HKCU\Software\Key\Value, a trailing '\' naming the key's default value ----

    public object? RegRead(string Name)
    {
        var (key, value) = Open(Name, writable: false);
        using (key)
        {
            object? v = key?.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (key == null || v == null)
                throw new COMException($"Unable to open registry key \"{Name}\" for reading.", unchecked((int)0x80070002));
            return v switch
            {
                byte[] bytes => bytes.Select(b => (object)b).ToArray(),           // REG_BINARY: an array of bytes
                string[] strings => strings.Cast<object>().ToArray(),            // REG_MULTI_SZ: an array of strings
                _ => v,
            };
        }
    }

    public void RegWrite(string Name, object Value) => RegWrite(Name, Value, "REG_SZ");

    public void RegWrite(string Name, object Value, object Type)
    {
        var kind = (Type as string ?? "REG_SZ").ToUpperInvariant() switch
        {
            "REG_SZ" => RegistryValueKind.String,
            "REG_EXPAND_SZ" => RegistryValueKind.ExpandString,
            "REG_DWORD" => RegistryValueKind.DWord,
            "REG_BINARY" => RegistryValueKind.Binary,
            var t => throw new COMException($"Invalid registry value type \"{t}\".", unchecked((int)0x80070057)),
        };
        var (key, value) = Open(Name, writable: true);
        using (key)
        {
            if (key == null) throw new COMException($"Unable to open registry key \"{Name}\" for writing.", unchecked((int)0x80070005));
            object data = kind switch
            {
                RegistryValueKind.DWord => Convert.ToInt32(Value, System.Globalization.CultureInfo.InvariantCulture),
                RegistryValueKind.Binary => BitConverter.GetBytes(Convert.ToInt32(Value, System.Globalization.CultureInfo.InvariantCulture)),
                _ => Convert.ToString(Value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            };
            key.SetValue(value, data, kind);
        }
    }

    public void RegDelete(string Name)
    {
        if (Name.EndsWith('\\'))
        {
            var (root, path) = Root(Name.TrimEnd('\\'));
            int at = path.LastIndexOf('\\');
            using var parent = at < 0 ? root : root.OpenSubKey(path[..at], writable: true);
            if (parent == null) throw new COMException($"Unable to remove registry key \"{Name}\".", unchecked((int)0x80070002));
            parent.DeleteSubKey(at < 0 ? path : path[(at + 1)..], throwOnMissingSubKey: true);
            return;
        }
        var (key, value) = Open(Name, writable: true);
        using (key)
        {
            if (key == null || key.GetValue(value) == null) throw new COMException($"Unable to remove registry key \"{Name}\".", unchecked((int)0x80070002));
            key.DeleteValue(value);
        }
    }

    private static (RegistryKey? Key, string Value) Open(string name, bool writable)
    {
        var (root, path) = Root(name);
        int at = path.LastIndexOf('\\');
        string keyPath = at < 0 ? "" : path[..at], value = at < 0 ? path : path[(at + 1)..];
        RegistryKey? key = keyPath.Length == 0 ? root : writable ? root.CreateSubKey(keyPath) : root.OpenSubKey(keyPath);
        return (key, value);
    }

    private static (RegistryKey Root, string Path) Root(string name)
    {
        int at = name.IndexOf('\\');
        string hive = at < 0 ? name : name[..at], rest = at < 0 ? "" : name[(at + 1)..];
        RegistryKey root = hive.ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            "HKCR" or "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
            "HKEY_USERS" => Registry.Users,
            "HKEY_CURRENT_CONFIG" => Registry.CurrentConfig,
            _ => throw new COMException($"Invalid root in registry key \"{name}\".", unchecked((int)0x80070057)),
        };
        return (root, rest);
    }

    // ---- Run: the command line as CreateProcess reads it, in a window of the style asked for ----

    public int Run(string Command) => Run(Command, 1, false);

    public int Run(string Command, object WindowStyle) => Run(Command, WindowStyle, false);

    public int Run(string Command, object WindowStyle, object WaitOnReturn)
    {
        string command = Environment.ExpandEnvironmentVariables(Command).Trim();
        string file, arguments;
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            file = end < 0 ? command[1..] : command[1..end];
            arguments = end < 0 ? "" : command[(end + 1)..].TrimStart();
        }
        else
        {
            int space = command.IndexOf(' ');
            file = space < 0 ? command : command[..space];
            arguments = space < 0 ? "" : command[(space + 1)..].TrimStart();
        }
        int style = Convert.ToInt32(WindowStyle ?? 1, System.Globalization.CultureInfo.InvariantCulture);
        var start = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = true,
            WindowStyle = style switch { 0 => ProcessWindowStyle.Hidden, 2 or 6 or 7 => ProcessWindowStyle.Minimized, 3 => ProcessWindowStyle.Maximized, _ => ProcessWindowStyle.Normal },
        };
        using var p = WindowsPaths.Process_Start(start);
        if (p == null || !Convert.ToBoolean(WaitOnReturn ?? false, System.Globalization.CultureInfo.InvariantCulture)) return 0;
        p.WaitForExit();
        return p.ExitCode;
    }
}

/// <summary>A WSH collection (IWshCollection): SpecialFolders.</summary>
public sealed class WshCollection : System.Collections.IEnumerable
{
    private readonly object[] _names;
    private readonly Func<string, string> _item;

    internal WshCollection(object[] names, Func<string, string> item) { _names = names; _item = item; }

    public object Item(object Index) => Index is string name ? _item(name)
        : _item((string)_names[Convert.ToInt32(Index, System.Globalization.CultureInfo.InvariantCulture)]);

    public int Count() => _names.Length;
    public int length => _names.Length;
    public System.Collections.IEnumerator GetEnumerator() => _names.Select(n => _item((string)n)).GetEnumerator();
}

/// <summary>A shell link (IWshShortcut): the .lnk at FullName.</summary>
public sealed class WshShortcut : IWshShortcut, IComServerObject
{
    private ShellLinkFile _link = new();

    internal WshShortcut(string fullName)
    {
        FullName = fullName;
        if (File.Exists(fullName)) try { _link = ShellLinkFile.Read(fullName); } catch (InvalidDataException) { }
    }

    public string FullName { get; private set; }
    public string Arguments { get => _link.Arguments; set => _link.Arguments = value ?? ""; }
    public string Description { get => _link.Description; set => _link.Description = value ?? ""; }
    public string Hotkey { get => HotkeyText(_link.HotKey); set => _link.HotKey = HotkeyValue(value ?? ""); }
    public string RelativePath { set => _link.RelativePath = value ?? ""; }
    public string TargetPath { get => _link.TargetPath; set => _link.TargetPath = value ?? ""; }
    public int WindowStyle { get => _link.ShowCommand; set => _link.ShowCommand = value; }
    public string WorkingDirectory { get => _link.WorkingDirectory; set => _link.WorkingDirectory = value ?? ""; }

    /// <summary>"path,index", as wshom reports it.</summary>
    public string IconLocation
    {
        get => $"{_link.IconPath},{_link.IconIndex}";
        set
        {
            string v = value ?? "";
            int comma = v.LastIndexOf(',');
            if (comma >= 0 && int.TryParse(v.AsSpan(comma + 1).Trim(), out int index)) { _link.IconPath = v[..comma].Trim(); _link.IconIndex = index; }
            else { _link.IconPath = v.Trim(); _link.IconIndex = 0; }
        }
    }

    public void Load(string PathLink)
    {
        string full = WindowsPaths.Path_GetFullPath(PathLink);
        _link = ShellLinkFile.Read(full);
        FullName = full;
    }

    public void Save()
    {
        string native = WindowsPaths.Native(FullName)!;
        if (Path.GetDirectoryName(native) is { Length: > 0 } dir && !Directory.Exists(dir))
            throw new COMException($"Unable to save shortcut \"{FullName}\".", unchecked((int)0x80070003));
        _link.Save(native);
    }

    // HOTKEYF_SHIFT 1, HOTKEYF_CONTROL 2, HOTKEYF_ALT 4 above a virtual key, as "Alt+Ctrl+Shift+F"
    private static string HotkeyText(ushort hotkey)
    {
        int vk = hotkey & 0xFF, mods = hotkey >> 8;
        if (vk == 0) return "";
        string key = vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A ? ((char)vk).ToString() : vk is >= 0x70 and <= 0x87 ? "F" + (vk - 0x6F) : "";
        if (key.Length == 0) return "";
        return ((mods & 4) != 0 ? "Alt+" : "") + ((mods & 2) != 0 ? "Ctrl+" : "") + ((mods & 1) != 0 ? "Shift+" : "") + key;
    }

    private static ushort HotkeyValue(string text)
    {
        int mods = 0, vk = 0;
        foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "ALT": mods |= 4; break;
                case "CTRL" or "CONTROL": mods |= 2; break;
                case "SHIFT": mods |= 1; break;
                case var k when k.Length == 1 && char.IsAsciiLetterOrDigit(k[0]): vk = k[0]; break;
                case var k when k.Length > 1 && k[0] == 'F' && int.TryParse(k.AsSpan(1), out int n) && n is >= 1 and <= 24: vk = 0x6F + n; break;
                default: throw new COMException($"Invalid hotkey \"{text}\".", unchecked((int)0x80070057));
            }
        }
        return vk == 0 ? (ushort)0 : (ushort)(mods << 8 | vk);
    }
}

/// <summary>An Internet shortcut (IWshURLShortcut): the .url at FullName.</summary>
public sealed class WshUrlShortcut : IWshURLShortcut, IComServerObject
{
    internal WshUrlShortcut(string fullName)
    {
        FullName = fullName;
        if (File.Exists(fullName)) Load(fullName);
    }

    public string FullName { get; private set; }
    public string TargetPath { get; set; } = "";

    public void Load(string PathLink)
    {
        string full = WindowsPaths.Path_GetFullPath(PathLink);
        bool section = false;
        foreach (string raw in File.ReadAllLines(full))
        {
            string line = raw.Trim();
            if (line.StartsWith('[')) { section = line.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase); continue; }
            if (section && line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) TargetPath = line[4..];
        }
        FullName = full;
    }

    public void Save() => File.WriteAllText(WindowsPaths.Native(FullName)!, $"[InternetShortcut]\r\nURL={TargetPath}\r\n");
}

/// <summary>IWshShortcut as wshom's type library lays it out (a dual interface), and its vtable for native callers.</summary>
[Guid("F935DC23-1CF0-11D0-ADB9-00C04FD58A0B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IWshShortcut
{
    [DispId(0)] string FullName { get; }
    [DispId(1000)] string Arguments { get; set; }
    [DispId(1001)] string Description { get; set; }
    [DispId(1002)] string Hotkey { get; set; }
    [DispId(1003)] string IconLocation { get; set; }
    [DispId(1004)] string RelativePath { set; }
    [DispId(1005)] string TargetPath { get; set; }
    [DispId(1006)] int WindowStyle { get; set; }
    [DispId(1007)] string WorkingDirectory { get; set; }
    [DispId(2000)] void Load(string PathLink);
    [DispId(2001)] void Save();

    public static unsafe class ComCcw_IWshShortcut
    {
        public static IntPtr Vtable()
        {
            var vt = (IntPtr*)Com.NewVtable(25);
            Com.FillDispatchSlots((IntPtr)vt, typeof(IWshShortcut).TypeHandle);
            vt[7] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&GetFullName;
            vt[8] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&GetArguments;
            vt[9] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&SetArguments;
            vt[10] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&GetDescription;
            vt[11] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&SetDescription;
            vt[12] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&GetHotkey;
            vt[13] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&SetHotkey;
            vt[14] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&GetIconLocation;
            vt[15] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&SetIconLocation;
            vt[16] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&SetRelativePath;
            vt[17] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&GetTargetPath;
            vt[18] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&SetTargetPath;
            vt[19] = (IntPtr)(delegate* unmanaged<IntPtr, int*, int>)&GetWindowStyle;
            vt[20] = (IntPtr)(delegate* unmanaged<IntPtr, int, int>)&SetWindowStyle;
            vt[21] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&GetWorkingDirectory;
            vt[22] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&SetWorkingDirectory;
            vt[23] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&Load;
            vt[24] = (IntPtr)(delegate* unmanaged<IntPtr, int>)&Save;
            return (IntPtr)vt;
        }

        private static IWshShortcut Of(IntPtr self) => (IWshShortcut)Com.CcwTarget(self);

        internal static int Get(IntPtr self, IntPtr* value, Func<IWshShortcut, string> get)
        {
            *value = IntPtr.Zero;
            try { *value = Marshal.StringToBSTR(get(Of(self))); return 0; }
            catch (Exception e) { return Com.HResultOf(e); }
        }

        internal static int Set(IntPtr self, IntPtr bstr, Action<IWshShortcut, string> set)
        {
            try { set(Of(self), bstr == IntPtr.Zero ? "" : Marshal.PtrToStringBSTR(bstr)); return 0; }
            catch (Exception e) { return Com.HResultOf(e); }
        }

        [UnmanagedCallersOnly] private static int GetFullName(IntPtr self, IntPtr* v) => Get(self, v, static s => s.FullName);
        [UnmanagedCallersOnly] private static int GetArguments(IntPtr self, IntPtr* v) => Get(self, v, static s => s.Arguments);
        [UnmanagedCallersOnly] private static int SetArguments(IntPtr self, IntPtr v) => Set(self, v, static (s, x) => s.Arguments = x);
        [UnmanagedCallersOnly] private static int GetDescription(IntPtr self, IntPtr* v) => Get(self, v, static s => s.Description);
        [UnmanagedCallersOnly] private static int SetDescription(IntPtr self, IntPtr v) => Set(self, v, static (s, x) => s.Description = x);
        [UnmanagedCallersOnly] private static int GetHotkey(IntPtr self, IntPtr* v) => Get(self, v, static s => s.Hotkey);
        [UnmanagedCallersOnly] private static int SetHotkey(IntPtr self, IntPtr v) => Set(self, v, static (s, x) => s.Hotkey = x);
        [UnmanagedCallersOnly] private static int GetIconLocation(IntPtr self, IntPtr* v) => Get(self, v, static s => s.IconLocation);
        [UnmanagedCallersOnly] private static int SetIconLocation(IntPtr self, IntPtr v) => Set(self, v, static (s, x) => s.IconLocation = x);
        [UnmanagedCallersOnly] private static int SetRelativePath(IntPtr self, IntPtr v) => Set(self, v, static (s, x) => s.RelativePath = x);
        [UnmanagedCallersOnly] private static int GetTargetPath(IntPtr self, IntPtr* v) => Get(self, v, static s => s.TargetPath);
        [UnmanagedCallersOnly] private static int SetTargetPath(IntPtr self, IntPtr v) => Set(self, v, static (s, x) => s.TargetPath = x);
        [UnmanagedCallersOnly] private static int GetWorkingDirectory(IntPtr self, IntPtr* v) => Get(self, v, static s => s.WorkingDirectory);
        [UnmanagedCallersOnly] private static int SetWorkingDirectory(IntPtr self, IntPtr v) => Set(self, v, static (s, x) => s.WorkingDirectory = x);
        [UnmanagedCallersOnly] private static int Load(IntPtr self, IntPtr v) => Set(self, v, static (s, x) => s.Load(x));

        [UnmanagedCallersOnly]
        private static int GetWindowStyle(IntPtr self, int* v)
        {
            *v = 0;
            try { *v = Of(self).WindowStyle; return 0; }
            catch (Exception e) { return Com.HResultOf(e); }
        }

        [UnmanagedCallersOnly]
        private static int SetWindowStyle(IntPtr self, int v)
        {
            try { Of(self).WindowStyle = v; return 0; }
            catch (Exception e) { return Com.HResultOf(e); }
        }

        [UnmanagedCallersOnly]
        private static int Save(IntPtr self)
        {
            try { Of(self).Save(); return 0; }
            catch (Exception e) { return Com.HResultOf(e); }
        }
    }
}

/// <summary>IWshURLShortcut as wshom's type library lays it out, and its vtable for native callers.</summary>
[Guid("F935DC2B-1CF0-11D0-ADB9-00C04FD58A0B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IWshURLShortcut
{
    [DispId(0)] string FullName { get; }
    [DispId(1005)] string TargetPath { get; set; }
    [DispId(2000)] void Load(string PathLink);
    [DispId(2001)] void Save();

    public static unsafe class ComCcw_IWshURLShortcut
    {
        public static IntPtr Vtable()
        {
            var vt = (IntPtr*)Com.NewVtable(12);
            Com.FillDispatchSlots((IntPtr)vt, typeof(IWshURLShortcut).TypeHandle);
            vt[7] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&GetFullName;
            vt[8] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&GetTargetPath;
            vt[9] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&SetTargetPath;
            vt[10] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&Load;
            vt[11] = (IntPtr)(delegate* unmanaged<IntPtr, int>)&Save;
            return (IntPtr)vt;
        }

        private static IWshURLShortcut Of(IntPtr self) => (IWshURLShortcut)Com.CcwTarget(self);

        [UnmanagedCallersOnly]
        private static int GetFullName(IntPtr self, IntPtr* v)
        {
            *v = IntPtr.Zero;
            try { *v = Marshal.StringToBSTR(Of(self).FullName); return 0; }
            catch (Exception e) { return Com.HResultOf(e); }
        }

        [UnmanagedCallersOnly]
        private static int GetTargetPath(IntPtr self, IntPtr* v)
        {
            *v = IntPtr.Zero;
            try { *v = Marshal.StringToBSTR(Of(self).TargetPath); return 0; }
            catch (Exception e) { return Com.HResultOf(e); }
        }

        [UnmanagedCallersOnly]
        private static int SetTargetPath(IntPtr self, IntPtr v)
        {
            try { Of(self).TargetPath = v == IntPtr.Zero ? "" : Marshal.PtrToStringBSTR(v); return 0; }
            catch (Exception e) { return Com.HResultOf(e); }
        }

        [UnmanagedCallersOnly]
        private static int Load(IntPtr self, IntPtr v)
        {
            try { Of(self).Load(v == IntPtr.Zero ? "" : Marshal.PtrToStringBSTR(v)); return 0; }
            catch (Exception e) { return Com.HResultOf(e); }
        }

        [UnmanagedCallersOnly]
        private static int Save(IntPtr self)
        {
            try { Of(self).Save(); return 0; }
            catch (Exception e) { return Com.HResultOf(e); }
        }
    }
}

/// <summary>
/// COM classes off Windows, by ProgID or CLSID: the registry's (HKCR\ProgID\CLSID, CurVer; a .NET class registered
/// for COM by its InprocServer32's Class and Assembly), and the servers this runs itself (WScript.Shell). On
/// Windows, the system's.
/// </summary>
public static class ComServers
{
    private static readonly Dictionary<Guid, Type> s_builtIn = new() { [typeof(WshShell).GUID] = typeof(WshShell) };
    private static readonly Dictionary<string, Guid> s_progIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WScript.Shell"] = typeof(WshShell).GUID, ["WScript.Shell.1"] = typeof(WshShell).GUID,
    };

    private const int CO_E_CLASSSTRING = unchecked((int)0x800401F3), REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);

    public static Type? TypeFromProgID(string progID, string? server, bool throwOnError)
    {
        if (OperatingSystem.IsWindows()) return Type.GetTypeFromProgID(progID, server, throwOnError);
        ArgumentNullException.ThrowIfNull(progID);
        Guid? clsid = RegisteredClsid(progID) ?? (s_progIds.TryGetValue(progID, out var known) ? known : null);
        if (clsid is { } c && ClassOf(c) is { } type) return type;
        if (throwOnError) throw new COMException("Invalid class string", CO_E_CLASSSTRING);
        return null;
    }

    public static Type? TypeFromCLSID(Guid clsid, string? server, bool throwOnError)
    {
        if (!OperatingSystem.IsWindows() && ClassOf(clsid) is { } type) return type;
        return Type.GetTypeFromCLSID(clsid, server, throwOnError);
    }

    /// <summary>An instance of a class this has off Windows, or null where it has none.</summary>
    internal static object? Create(Guid clsid) => ClassOf(clsid) is { } t ? Activator.CreateInstance(t) : null;

    private static Type? ClassOf(Guid clsid)
    {
        if (s_builtIn.TryGetValue(clsid, out var t)) return t;
        // a .NET class registered for COM (RegAsm's InprocServer32: Class, Assembly, CodeBase)
        try
        {
            using var server = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{clsid}}}\InprocServer32");
            if (server?.GetValue("Class") is not string cls || cls.Length == 0) return null;
            if (server.GetValue("CodeBase") is string codeBase && codeBase.Length > 0
                && WindowsPaths.Native(Uri.TryCreate(codeBase, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : codeBase) is { } file
                && File.Exists(WindowsPaths.Disk(file)))
                return System.Reflection.Assembly.LoadFrom(WindowsPaths.Disk(file)!).GetType(cls, throwOnError: false);
            return server.GetValue("Assembly") is string assembly ? Type.GetType(cls + ", " + assembly, throwOnError: false) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or System.Security.SecurityException) { return null; }
    }

    // HKCR\ProgID\CLSID, following a version-independent ProgID's CurVer
    private static Guid? RegisteredClsid(string progID)
    {
        try
        {
            for (int hops = 0; hops < 4; hops++)
            {
                using var key = Registry.ClassesRoot.OpenSubKey(progID);
                if (key == null) return null;
                using (var clsid = key.OpenSubKey("CLSID"))
                    if (clsid?.GetValue(null) is string s && Guid.TryParse(s, out var g)) return g;
                using var curVer = key.OpenSubKey("CurVer");
                if (curVer?.GetValue(null) is not string next || next.Length == 0) return null;
                progID = next;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return null;
    }

    internal static Exception NotRegistered(Guid clsid) => new COMException($"Class {{{clsid}}} is not registered", REGDB_E_CLASSNOTREG);
}
