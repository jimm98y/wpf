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
    };

    private readonly Dictionary<string, MethodDefinition> _bySignature = new(StringComparer.Ordinal);
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

    private MethodDefinition? For(MethodReference r)
    {
        if (!s_types.Contains(r.DeclaringType.FullName) || r is GenericInstanceMethod) return null;
        IEnumerable<TypeReference> ps = r.Parameters.Select(p => p.ParameterType);
        if (r.HasThis && r.Name != ".ctor") ps = ps.Prepend(r.DeclaringType);
        return _bySignature.TryGetValue(Key(r.DeclaringType.Name, r.Name, ps), out var m) ? m : null;
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
        foreach (TypeDefinition t in module.GetTypes())
            foreach (MethodDefinition m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (Instruction i in m.Body.Instructions)
                {
                    if ((i.OpCode != OpCodes.Call && i.OpCode != OpCodes.Callvirt && i.OpCode != OpCodes.Newobj)
                        || i.Operand is not MethodReference r || For(r) is not { } stand) continue;
                    // a constructor is only stood in for where it makes the object (newobj), not where a
                    // derived type's constructor calls it
                    if (r.Name == ".ctor" && i.OpCode != OpCodes.Newobj) continue;
                    // an instance member only where it is called virtually: a plain call of one is an
                    // override's base call (XmlDocument.Load in a derived document's Load), which the
                    // stand-in -- calling the member virtually -- would turn into the override again
                    if (r.HasThis && r.Name != ".ctor" && i.OpCode != OpCodes.Callvirt) continue;
                    i.OpCode = OpCodes.Call;
                    i.Operand = module.ImportReference(stand);
                    n++;
                }
            }
        return n;
    }
}
