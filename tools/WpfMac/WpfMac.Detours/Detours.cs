// Detours for Windows behaviours the .NET runtime refuses off Windows, in CoreLib and the other
// framework assemblies an app cannot be given a replacement for. Each is generic, and each is the
// behaviour Windows has, emulated with what macOS has:
//
//   Thread.SetApartmentState(STA)   throws PlatformNotSupportedException off Windows. Every thread an
//                                   app starts for a splash screen or a dialog sets STA first. Recorded
//                                   instead, and reported back by GetApartmentState.
//   MemoryMappedFile, by name       "Named maps are not supported". A named map is how Windows
//                                   processes share memory; a map of a file in wpfmac's state folder
//                                   is the same thing on macOS, shared by every process that opens it.
//   GetFolderPath(Common*)          the machine-wide folders (ProgramData and friends) are /usr/share
//                                   or empty on macOS, and not writable. They map into wpfmac's state.
//   SystemColors                    off Windows System.Drawing.Primitives answers a palette compiled
//                                   into it, Windows XP's (a beige #ECE9D8 Control). On Windows it asks
//                                   GetSysColor, and so does this, of the stack's user32.
//
// Only installed where needed (macOS); the bootstrap logs which ones are active.

using System.Drawing;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.CompilerServices;
using MonoMod.RuntimeDetour;

namespace WpfMac.Detours;

public static class Detours
{
    private static readonly List<Hook> s_hooks = new();
    private static string s_state = "";
    private static Action<string, string>? s_logOnce;

    /// <summary>Installs every detour. Returns the names of the ones installed.</summary>
    public static string[] InstallAll(string stateDirectory, Action<string, string> logOnce)
    {
        s_state = stateDirectory;
        s_logOnce = logOnce;
        var installed = new List<string>();
        Try(installed, "apartment state", InstallApartmentState);
        Try(installed, "named memory-mapped files", InstallMemoryMappedFiles);
        Try(installed, "machine-wide folders", InstallSpecialFolders);
        Try(installed, "system colours", InstallSystemColors);
        return installed.ToArray();
    }

    private static void Try(List<string> installed, string name, Action install)
    {
        try
        {
            install();
            installed.Add(name);
        }
        catch (Exception e)
        {
            s_logOnce?.Invoke("detour:" + name, $"detour '{name}' not installed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Add(MethodBase target, Delegate hook) => s_hooks.Add(new Hook(target, hook));

    // ---- apartment state --------------------------------------------------------------------------

    private static readonly ConditionalWeakTable<Thread, StrongBox<ApartmentState>> s_apartments = new();

    private static void InstallApartmentState()
    {
        Add(typeof(Thread).GetMethod(nameof(Thread.SetApartmentState), new[] { typeof(ApartmentState) })!,
            (Action<Thread, ApartmentState> orig, Thread self, ApartmentState state) =>
            {
                // Only STA is refused; the runtime still validates the rest (and a started thread).
                if (state != ApartmentState.STA) orig(self, state);
                s_apartments.AddOrUpdate(self, new StrongBox<ApartmentState>(state));
            });
        Add(typeof(Thread).GetMethod(nameof(Thread.TrySetApartmentState), new[] { typeof(ApartmentState) })!,
            (Func<Thread, ApartmentState, bool> orig, Thread self, ApartmentState state) =>
            {
                if (state != ApartmentState.STA && !orig(self, state)) return false;
                s_apartments.AddOrUpdate(self, new StrongBox<ApartmentState>(state));
                return true;
            });
        Add(typeof(Thread).GetMethod(nameof(Thread.GetApartmentState), Type.EmptyTypes)!,
            (Func<Thread, ApartmentState> orig, Thread self) =>
                s_apartments.TryGetValue(self, out var box) ? box.Value : orig(self));
    }

    // ---- system colours ------------------------------------------------------------------------------
    //
    // GraphWorX's light theme sets its property grid's HelpBackColor to SystemColors.Control: beige.

    // The stack's user32 (WpfMac.Win32), by name: the stack's own imports do not reach it.
    private static Func<int, uint>? s_getSysColor;

    // COLOR_* of each system KnownColor, as .NET's Windows build maps them: ActiveBorder..WindowText,
    // then ButtonFace..MenuHighlight (the later additions, past the web colours).
    private static readonly int[] s_colorIndex = { 10, 2, 9, 12, 15, 16, 21, 22, 20, 18, 1, 17, 13, 14, 26, 11, 3, 19, 24, 23, 4, 7, 0, 5, 6, 8 };
    private static readonly int[] s_laterColorIndex = { 15, 20, 16, 27, 28, 30, 29 };

    private static int SysColorIndex(KnownColor color) =>
        color >= KnownColor.ActiveBorder && color <= KnownColor.WindowText ? s_colorIndex[color - KnownColor.ActiveBorder]
        : color >= KnownColor.ButtonFace && color <= KnownColor.MenuHighlight ? s_laterColorIndex[color - KnownColor.ButtonFace]
        : -1;

    private static uint SysColorArgb(int index)
    {
        uint bgr = s_getSysColor!(index);   // COLORREF 0x00BBGGRR
        return 0xFF000000 | ((bgr & 0xFF) << 16) | (bgr & 0xFF00) | ((bgr >> 16) & 0xFF);
    }

    private static void InstallSystemColors()
    {
        s_getSysColor = Assembly.Load(new AssemblyName("WpfMac.Win32")).GetType("WpfMac.Win32.Exports", throwOnError: true)!
            .GetMethod("SysColor", BindingFlags.Public | BindingFlags.Static)!.CreateDelegate<Func<int, uint>>();
        Type table = typeof(Color).Assembly.GetType("System.Drawing.KnownColorTable", throwOnError: true)!;
        // The dark set (Application.SetColorMode) is the runtime's own on Windows too: left to it.
        FieldInfo? alternative = typeof(SystemColors).GetField("s_useAlternativeColorSet", BindingFlags.NonPublic | BindingFlags.Static);
        // GetSystemColorArgb is what every system colour comes through; KnownColorToArgb as well,
        // where the runtime inlined the first into it.
        Add(table.GetMethod("GetSystemColorArgb", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!,
            (Func<KnownColor, uint> orig, KnownColor color) =>
                SysColorIndex(color) is int i && i >= 0 && alternative?.GetValue(null) is not true ? SysColorArgb(i) : orig(color));
        Add(table.GetMethod("KnownColorToArgb", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!,
            (Func<KnownColor, uint> orig, KnownColor color) =>
                SysColorIndex(color) is int i && i >= 0 && alternative?.GetValue(null) is not true ? SysColorArgb(i) : orig(color));
    }

    // ---- named memory-mapped files -----------------------------------------------------------------

    private static string MapPath(string name)
    {
        // Global\ and Local\ select the session namespace on Windows; there is one namespace here.
        string bare = name.StartsWith(@"Global\", StringComparison.OrdinalIgnoreCase) || name.StartsWith(@"Local\", StringComparison.OrdinalIgnoreCase)
            ? name[(name.IndexOf('\\') + 1)..] : name;
        foreach (char c in Path.GetInvalidFileNameChars()) bare = bare.Replace(c, '_');
        string dir = Path.Combine(s_state, "shared-memory");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, bare);
    }

    private static MemoryMappedFile Map(string name, long capacity, FileMode mode, MemoryMappedFileAccess access)
    {
        string path = MapPath(name);
        if (mode == FileMode.CreateNew && File.Exists(path) && new FileInfo(path).Length > 0)
        {
            throw new IOException($"Cannot create '{name}' because a file or directory with the same name already exists.");
        }
        if (mode == FileMode.Open && !File.Exists(path))
        {
            throw new FileNotFoundException("Unable to find the specified file.", name);
        }
        var stream = new FileStream(path, mode == FileMode.Open ? FileMode.Open : FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        if (capacity > 0 && stream.Length < capacity) stream.SetLength(capacity);
        return MemoryMappedFile.CreateFromFile(stream, null, capacity > 0 ? capacity : 0,
            access == MemoryMappedFileAccess.Read ? MemoryMappedFileAccess.Read : MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None, leaveOpen: false);
    }

    private static void InstallMemoryMappedFiles()
    {
        var t = typeof(MemoryMappedFile);
        Type[] fiveArgs = { typeof(string), typeof(long), typeof(MemoryMappedFileAccess), typeof(MemoryMappedFileOptions), typeof(HandleInheritability) };

        Add(t.GetMethod(nameof(MemoryMappedFile.CreateOrOpen), fiveArgs)!,
            (Func<string, long, MemoryMappedFileAccess, MemoryMappedFileOptions, HandleInheritability, MemoryMappedFile> orig,
             string name, long capacity, MemoryMappedFileAccess access, MemoryMappedFileOptions options, HandleInheritability inherit) =>
                name == null ? orig(name!, capacity, access, options, inherit) : Map(name, capacity, FileMode.OpenOrCreate, access));
        Add(t.GetMethod(nameof(MemoryMappedFile.CreateNew), fiveArgs)!,
            (Func<string, long, MemoryMappedFileAccess, MemoryMappedFileOptions, HandleInheritability, MemoryMappedFile> orig,
             string name, long capacity, MemoryMappedFileAccess access, MemoryMappedFileOptions options, HandleInheritability inherit) =>
                name == null ? orig(name!, capacity, access, options, inherit) : Map(name, capacity, FileMode.CreateNew, access));
        Add(t.GetMethod(nameof(MemoryMappedFile.OpenExisting), new[] { typeof(string), typeof(MemoryMappedFileRights), typeof(HandleInheritability) })!,
            (Func<string, MemoryMappedFileRights, HandleInheritability, MemoryMappedFile> orig, string name, MemoryMappedFileRights rights, HandleInheritability inherit) =>
                Map(name, 0, FileMode.Open, (rights & MemoryMappedFileRights.Write) != 0 ? MemoryMappedFileAccess.ReadWrite : MemoryMappedFileAccess.Read));
        // The shorter overloads call these, except where the runtime inlined them; hook those too.
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            var p = m.GetParameters();
            if (m.Name == nameof(MemoryMappedFile.CreateOrOpen) && p.Length == 2)
                Add(m, (Func<string, long, MemoryMappedFile> orig, string name, long capacity) => name == null ? orig(name!, capacity) : Map(name, capacity, FileMode.OpenOrCreate, MemoryMappedFileAccess.ReadWrite));
            else if (m.Name == nameof(MemoryMappedFile.CreateNew) && p.Length == 2)
                Add(m, (Func<string, long, MemoryMappedFile> orig, string name, long capacity) => name == null ? orig(name!, capacity) : Map(name, capacity, FileMode.CreateNew, MemoryMappedFileAccess.ReadWrite));
            else if (m.Name == nameof(MemoryMappedFile.OpenExisting) && p.Length == 1)
                Add(m, (Func<string, MemoryMappedFile> orig, string name) => Map(name, 0, FileMode.Open, MemoryMappedFileAccess.ReadWrite));
        }
    }

    // ---- machine-wide folders ---------------------------------------------------------------------

    private static string? CommonFolder(Environment.SpecialFolder folder) => folder switch
    {
        Environment.SpecialFolder.CommonApplicationData => "ProgramData",
        Environment.SpecialFolder.CommonDocuments => "Public/Documents",
        Environment.SpecialFolder.CommonDesktopDirectory => "Public/Desktop",
        Environment.SpecialFolder.CommonMusic => "Public/Music",
        Environment.SpecialFolder.CommonPictures => "Public/Pictures",
        Environment.SpecialFolder.CommonVideos => "Public/Videos",
        Environment.SpecialFolder.CommonTemplates => "ProgramData/Templates",
        Environment.SpecialFolder.CommonStartMenu => "ProgramData/Start Menu",
        Environment.SpecialFolder.CommonPrograms => "ProgramData/Start Menu/Programs",
        Environment.SpecialFolder.CommonStartup => "ProgramData/Start Menu/Programs/Startup",
        Environment.SpecialFolder.CommonAdminTools => "ProgramData/Start Menu/Programs/Administrative Tools",
        _ => null,
    };

    private static void InstallSpecialFolders()
    {
        Add(typeof(Environment).GetMethod(nameof(Environment.GetFolderPath), new[] { typeof(Environment.SpecialFolder), typeof(Environment.SpecialFolderOption) })!,
            (Func<Environment.SpecialFolder, Environment.SpecialFolderOption, string> orig, Environment.SpecialFolder folder, Environment.SpecialFolderOption option) =>
            {
                string? relative = CommonFolder(folder);
                if (relative == null) return orig(folder, option);
                string path = Path.Combine(s_state, relative);
                if (option != Environment.SpecialFolderOption.DoNotVerify) Directory.CreateDirectory(path);
                return path;
            });
        Add(typeof(Environment).GetMethod(nameof(Environment.GetFolderPath), new[] { typeof(Environment.SpecialFolder) })!,
            (Func<Environment.SpecialFolder, string> orig, Environment.SpecialFolder folder) =>
            {
                string? relative = CommonFolder(folder);
                if (relative == null) return orig(folder);
                string path = Path.Combine(s_state, relative);
                Directory.CreateDirectory(path);
                return path;
            });
    }
}
