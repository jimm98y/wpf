// An app's calls of the members that take a path, rewritten onto WpfWebGpu.ComInterop's WindowsPaths, which
// reads a path as Windows does ('\' a separator as well as '/') and calls the member itself. A method of
// WindowsPaths named <Type>_<member> (a constructor: <Type>_new) stands in for that member of the type,
// with the same parameters -- the instance first for an instance member -- so the table is WindowsPaths
// itself, and a member is covered by adding its method there.
//
// Every head rewrites them, Windows too, where the stand-ins are the members themselves: one app image
// everywhere (the launcher's step and loader at load, BuildRewrite at build).

using Mono.Cecil;
using Mono.Cecil.Cil;

namespace WpfWebGpu.ComRewrite;

public sealed class PathSubstitutions
{
    private const string StandIns = "WpfWebGpu.ComInterop.WindowsPaths";

    private static readonly HashSet<string> s_types = new(StringComparer.Ordinal)
    {
        "System.IO.Path", "System.IO.File", "System.IO.Directory", "System.IO.FileInfo", "System.IO.DirectoryInfo",
        "System.IO.FileStream", "System.IO.StreamReader", "System.IO.StreamWriter",
        "System.Xml.XmlDocument", "System.Xml.XmlReader", "System.Xml.XmlWriter", "System.Xml.XmlTextReader", "System.Xml.XmlTextWriter",
        "System.Xml.Linq.XDocument", "System.Xml.Linq.XElement", "System.Reflection.Assembly", "System.Uri",
        "System.Diagnostics.Process", "System.Environment", "System.IO.MemoryMappedFiles.MemoryMappedFile",
        "System.Threading.Thread", "Microsoft.Extensions.FileProviders.PhysicalFileProvider",
        // the machine's certificate stores (WindowsPaths: MachineCertificateStore)
        "System.Security.Cryptography.X509Certificates.X509Store",
        // COM classes by ProgID or CLSID (WindowsPaths: the registry's, and the servers run here)
        "System.Type",
        // the operating system as the app asks about it (WindowsPaths: WPFWEBGPU_OS_IDENTITY)
        "System.OperatingSystem", "System.Runtime.InteropServices.RuntimeInformation",
    };

    // .NET's own assemblies, and Microsoft's libraries an app carries (a SQL client choosing its native code by
    // the system): their questions about the system get its own answers, never a stand-in's
    private static readonly HashSet<string> s_osTypes = new(StringComparer.Ordinal) { "System.OperatingSystem", "System.Runtime.InteropServices.RuntimeInformation" };
    private static readonly HashSet<string> s_microsoftKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "b03f5f7f11d50a3a", "31bf3856ad364e35", "cc7b13ffcd2ddd51", "7cec85d7bea7798e", "adb9793829ddae60", "b77a5c561934e089",
    };

    private static bool IsMicrosofts(ModuleDefinition module)
    {
        byte[]? token = module.Assembly?.Name?.PublicKeyToken;
        return token is { Length: 8 } && s_microsoftKeys.Contains(Convert.ToHexString(token));
    }

    // code that asks about macOS, Linux or another system itself (an OPC UA stack loading a key as macOS's .NET
    // can) was written for it: there its questions get the system's own answers. Which systems a method knows is
    // handed to the stand-ins (their variant with a last int knownSystems, WindowsPaths' KnownSystem bits), which
    // tell Windows only to code that does not know the system it runs on. A method, not its assembly: a licence
    // library with a macOS path for its USB keys still reads the machine's identity as on Windows alone (WMI,
    // which answers here), and is told Windows there.
    private static readonly Dictionary<string, int> s_systemChecks = new(StringComparer.Ordinal)
    {
        ["System.Runtime.InteropServices.OSPlatform::get_OSX"] = 1, ["System.OperatingSystem::IsMacOS"] = 1, ["System.OperatingSystem::IsMacOSVersionAtLeast"] = 1,
        ["System.Runtime.InteropServices.OSPlatform::get_Linux"] = 2, ["System.OperatingSystem::IsLinux"] = 2,
        ["System.OperatingSystem::IsIOS"] = 4, ["System.OperatingSystem::IsIOSVersionAtLeast"] = 4,
        ["System.OperatingSystem::IsMacCatalyst"] = 4, ["System.OperatingSystem::IsMacCatalystVersionAtLeast"] = 4,
        ["System.OperatingSystem::IsTvOS"] = 4, ["System.OperatingSystem::IsTvOSVersionAtLeast"] = 4,
        ["System.OperatingSystem::IsWatchOS"] = 4, ["System.OperatingSystem::IsWatchOSVersionAtLeast"] = 4,
        ["System.OperatingSystem::IsAndroid"] = 8, ["System.OperatingSystem::IsAndroidVersionAtLeast"] = 8,
        ["System.Runtime.InteropServices.OSPlatform::get_FreeBSD"] = 16, ["System.OperatingSystem::IsFreeBSD"] = 16, ["System.OperatingSystem::IsFreeBSDVersionAtLeast"] = 16,
        ["System.OperatingSystem::IsBrowser"] = 32, ["System.OperatingSystem::IsWasi"] = 32,
    };

    /// <summary>The systems the method asks about by name (bits as WindowsPaths' KnownSystem).</summary>
    internal static int KnownSystems(MethodDefinition method)
    {
        int known = 0;
        foreach (Instruction i in method.Body.Instructions)
            if (i.Operand is MethodReference r && s_systemChecks.TryGetValue(r.DeclaringType.FullName + "::" + r.Name, out int bit)) known |= bit;
        return known;
    }

    private readonly Dictionary<string, MethodDefinition> _bySignature = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MethodDefinition> _knowing = new(StringComparer.Ordinal);
    // (type, member) of every stand-in: the cheap test of whether an assembly may call one (MayUse)
    private readonly HashSet<string> _members = new(StringComparer.Ordinal);

    private PathSubstitutions(ModuleDefinition apis)
    {
        if (apis.GetType(StandIns) is not { } t) return;
        foreach (MethodDefinition m in t.Methods)
        {
            int at = m.Name.IndexOf('_');
            if (!m.IsStatic || !m.IsPublic || at <= 0) continue;
            string type = m.Name[..at], member = m.Name[(at + 1)..];
            if (member == "new") member = ".ctor";
            // an OS stand-in's variant told the systems its caller knows: keyed without that last parameter
            if (m.Parameters.Count > 0 && m.Parameters[^1].Name == "knownSystems")
            {
                _knowing[Key(type, member, m.Parameters.Take(m.Parameters.Count - 1).Select(p => p.ParameterType))] = m;
                continue;
            }
            _bySignature[Key(type, member, m.Parameters.Select(p => p.ParameterType))] = m;
            _members.Add(type + "::" + member);
        }
    }

    // a member by its type's short name, its name and its parameters' types, the instance's first
    private static string Key(string type, string member, IEnumerable<TypeReference> parameters)
        => $"{type}::{member}({string.Join(",", parameters.Select(p => p.FullName))})";

    private static readonly Dictionary<string, PathSubstitutions?> s_byFile = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The substitutions WpfWebGpu.ComInterop in <paramref name="searchDirs"/> offers; null without it.</summary>
    public static PathSubstitutions? For(IEnumerable<string> searchDirs)
    {
        foreach (string d in searchDirs)
        {
            string p = Path.Combine(d, "WpfWebGpu.ComInterop.dll");
            if (!File.Exists(p)) continue;
            lock (s_byFile)
            {
                if (!s_byFile.TryGetValue(p, out var subs))
                    s_byFile[p] = subs = new PathSubstitutions(ModuleDefinition.ReadModule(p, new ReaderParameters { InMemory = true }));
                return subs;
            }
        }
        return null;
    }

    private MethodDefinition? For(MethodReference r, bool knowing = false)
    {
        if (!s_types.Contains(r.DeclaringType.FullName) || r is GenericInstanceMethod) return null;
        IEnumerable<TypeReference> ps = r.Parameters.Select(p => p.ParameterType);
        if (r.HasThis && r.Name != ".ctor") ps = ps.Prepend(r.DeclaringType);
        return (knowing ? _knowing : _bySignature).TryGetValue(Key(r.DeclaringType.Name, r.Name, ps), out var m) ? m : null;
    }

    /// <summary>Whether the file may call a member this substitutes: a reference to one by type and name
    /// (metadata only, the overloads told apart by Apply).</summary>
    public bool MayUse(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
            if (!pe.HasMetadata) return false;
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            foreach (var h in md.MemberReferences)
            {
                var mr = md.GetMemberReference(h);
                if (mr.Parent.Kind != System.Reflection.Metadata.HandleKind.TypeReference) continue;
                var tr = md.GetTypeReference((System.Reflection.Metadata.TypeReferenceHandle)mr.Parent);
                string ns = md.GetString(tr.Namespace), name = md.GetString(tr.Name);
                if (!s_types.Contains(ns + "." + name)) continue;
                if (_members.Contains(name + "::" + md.GetString(mr.Name))) return true;
            }
            return false;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Points every call of a substituted member at its stand-in; how many were.</summary>
    public int Apply(ModuleDefinition module)
    {
        int n = 0;
        // .NET's assemblies and Microsoft's libraries: the system's own answers
        bool microsofts = IsMicrosofts(module);
        foreach (TypeDefinition t in module.GetTypes())
            foreach (MethodDefinition m in t.Methods)
            {
                if (!m.HasBody) continue;
                int known = microsofts ? 0 : KnownSystems(m);
                var told = new List<(Instruction Call, MethodDefinition Stand)>();
                foreach (Instruction i in m.Body.Instructions)
                {
                    if ((i.OpCode != OpCodes.Call && i.OpCode != OpCodes.Callvirt && i.OpCode != OpCodes.Newobj)
                        || i.Operand is not MethodReference r || For(r) is not { } stand) continue;
                    bool os = s_osTypes.Contains(r.DeclaringType.FullName) || r.Name == "get_OSVersion";
                    if (microsofts && os) continue;
                    // a constructor is only stood in for where it makes the object (newobj), not where a
                    // derived type's constructor calls it
                    if (r.Name == ".ctor" && i.OpCode != OpCodes.Newobj) continue;
                    // an instance member only where it is called virtually: a plain call of one is an
                    // override's base call (XmlDocument.Load in a derived document's Load), which the
                    // stand-in -- calling the member virtually -- would turn into the override again
                    if (r.HasThis && r.Name != ".ctor" && i.OpCode != OpCodes.Callvirt) continue;
                    if (os && known != 0 && For(r, knowing: true) is { } knowing) { told.Add((i, knowing)); continue; }
                    i.OpCode = OpCodes.Call;
                    i.Operand = module.ImportReference(stand);
                    n++;
                }
                // the systems the assembly knows, pushed last: the call's own instruction becomes the push (a branch
                // to the call reaches it), the stand-in's call after it
                foreach (var (call, stand) in told)
                {
                    var il = m.Body.GetILProcessor();
                    var invoke = il.Create(OpCodes.Call, module.ImportReference(stand));
                    call.OpCode = OpCodes.Ldc_I4;
                    call.Operand = known;
                    il.InsertAfter(call, invoke);
                    n++;
                }
            }
        return n;
    }
}
