// An app's Win32 resources in the executable it runs as (ProgramHost, Windows): its icons and its version, as the SDK
// copies them from the app's assembly into its <App>.exe -- what Explorer, the taskbar and a window without an icon of
// its own show, and the file's properties. Read and written with Windows' own resource functions.

using System.Runtime.InteropServices;

namespace WpfWebGpu;

internal static unsafe partial class WindowsResources
{
    private const uint LOAD_LIBRARY_AS_DATAFILE = 0x2, LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x20;
    // RT_ICON, RT_GROUP_ICON, RT_VERSION
    private static readonly nint[] s_types = { 3, 14, 16 };

    /// <summary>Copies <paramref name="from"/>'s icons and version resource into <paramref name="to"/>, replacing its
    /// own; nothing where <paramref name="from"/> has none.</summary>
    public static void CopyAppResources(string from, string to)
    {
        if (!OperatingSystem.IsWindows()) return;
        nint module = LoadLibraryExW(from, 0, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (module == 0) return;
        try
        {
            var found = new List<(nint Type, nint Name, ushort Language, byte[] Data)>();
            foreach (nint type in s_types)
            {
                foreach (nint name in Names(module, type))
                    foreach (ushort language in Languages(module, type, name))
                        if (Data(module, type, name, language) is { } data) found.Add((type, name, language, data));
            }
            // an app with no icon keeps the apphost's; with one, the apphost's go (its own group replaced, the
            // icons it named no longer shown)
            if (!found.Any(r => r.Type == 14)) found.RemoveAll(r => r.Type is 3);
            if (found.Count == 0) return;
            nint update = BeginUpdateResourceW(to, false);
            if (update == 0) return;
            bool ok = true;
            if (found.Any(r => r.Type == 14))
            {
                foreach (nint type in new nint[] { 3, 14 })
                    foreach (nint name in Names(0, type, to))
                        foreach (ushort language in Languages(0, type, name, to))
                            ok &= UpdateResourceW(update, type, name, language, null, 0);
            }
            foreach (var r in found)
                fixed (byte* p = r.Data) ok &= UpdateResourceW(update, r.Type, r.Name, r.Language, p, (uint)r.Data.Length);
            EndUpdateResourceW(update, !ok);
        }
        finally { FreeLibrary(module); }
    }

    // the names of a type's resources (integer ids, or strings copied out); of another file's when it is given
    private static List<nint> Names(nint module, nint type, string? file = null)
    {
        bool own = file != null;
        if (own) module = LoadLibraryExW(file!, 0, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        var names = new List<nint>();
        if (module == 0) return names;
        try
        {
            var handle = GCHandle.Alloc(names);
            try { EnumResourceNamesW(module, type, &OnName, GCHandle.ToIntPtr(handle)); }
            finally { handle.Free(); }
        }
        finally { if (own) FreeLibrary(module); }
        return names;
    }

    private static List<ushort> Languages(nint module, nint type, nint name, string? file = null)
    {
        bool own = file != null;
        if (own) module = LoadLibraryExW(file!, 0, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        var languages = new List<ushort>();
        if (module == 0) return languages;
        try
        {
            var handle = GCHandle.Alloc(languages);
            try { EnumResourceLanguagesW(module, type, name, &OnLanguage, GCHandle.ToIntPtr(handle)); }
            finally { handle.Free(); }
        }
        finally { if (own) FreeLibrary(module); }
        return languages;
    }

    private static byte[]? Data(nint module, nint type, nint name, ushort language)
    {
        nint info = FindResourceExW(module, type, name, language);
        if (info == 0) return null;
        nint loaded = LoadResource(module, info);
        uint size = SizeofResource(module, info);
        byte* p = (byte*)LockResource(loaded);
        return p == null ? null : new ReadOnlySpan<byte>(p, (int)size).ToArray();
    }

    [UnmanagedCallersOnly]
    private static int OnName(nint module, nint type, nint name, nint state)
    {
        var names = (List<nint>)GCHandle.FromIntPtr(state).Target!;
        // an integer id as it is; a string name kept as an id where it is one ("#12"), else skipped (an app's icons are numbered)
        if ((ulong)name >> 16 == 0) names.Add(name);
        return 1;
    }

    [UnmanagedCallersOnly]
    private static int OnLanguage(nint module, nint type, nint name, ushort language, nint state)
    {
        ((List<ushort>)GCHandle.FromIntPtr(state).Target!).Add(language);
        return 1;
    }

    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint LoadLibraryExW(string file, nint reserved, uint flags);
    [LibraryImport("kernel32")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool FreeLibrary(nint module);
    [LibraryImport("kernel32")] private static partial int EnumResourceNamesW(nint module, nint type, delegate* unmanaged<nint, nint, nint, nint, int> callback, nint state);
    [LibraryImport("kernel32")] private static partial int EnumResourceLanguagesW(nint module, nint type, nint name, delegate* unmanaged<nint, nint, nint, ushort, nint, int> callback, nint state);
    [LibraryImport("kernel32")] private static partial nint FindResourceExW(nint module, nint type, nint name, ushort language);
    [LibraryImport("kernel32")] private static partial nint LoadResource(nint module, nint info);
    [LibraryImport("kernel32")] private static partial uint SizeofResource(nint module, nint info);
    [LibraryImport("kernel32")] private static partial nint LockResource(nint loaded);
    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint BeginUpdateResourceW(string file, [MarshalAs(UnmanagedType.Bool)] bool deleteExisting);
    [LibraryImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateResourceW(nint update, nint type, nint name, ushort language, void* data, uint size);
    [LibraryImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EndUpdateResourceW(nint update, [MarshalAs(UnmanagedType.Bool)] bool discard);
}
