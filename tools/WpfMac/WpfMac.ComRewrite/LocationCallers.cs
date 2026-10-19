// Limit 1 of the in-memory rewrite (Documentation/comimport-rewriter.md): a rewritten assembly has no file,
// and only its own location calls are redirected to the original. An app assembly that asks OTHER
// assemblies where they are -- GetCallingAssembly().CodeBase (IcoTrace), an Assembly parameter, the
// assemblies of AppDomain.GetAssemblies -- would get nothing for a rewritten one. Such callers are
// rewritten too ("caller rewriting"): their location calls are redirected the same way.
//
// This finds them, from the IL: a read of Location, CodeBase, EscapedCodeBase, GetName().CodeBase or
// Module.FullyQualifiedName whose Assembly (or Module) is not provably the reader's own. Own:
// GetExecutingAssembly(), typeof(OwnType).Assembly, this.GetType().Assembly, and a local assigned once from
// one of those; GetEntryAssembly() is not asked about (the entry assembly is never rewritten).

using Mono.Cecil;
using Mono.Cecil.Cil;

namespace WpfMac.ComRewrite;

public sealed record LocationAsks(bool AsksOthers, bool EnumeratesAll, string? Example);

public static class LocationCallers
{
    private static readonly HashSet<string> s_members = new(StringComparer.Ordinal)
    {
        "get_Location", "get_CodeBase", "get_EscapedCodeBase", "get_FullyQualifiedName",
    };

    /// <summary>Whether the assembly's member references could read a location at all (metadata only: the
    /// cheap filter before <see cref="Analyze"/>).</summary>
    public static bool MayRead(System.Reflection.Metadata.MetadataReader md)
    {
        foreach (var h in md.MemberReferences)
            if (s_members.Contains(md.GetString(md.GetMemberReference(h).Name))) return true;
        return false;
    }

    public static LocationAsks Analyze(string path)
    {
        using var m = ModuleDefinition.ReadModule(path, new ReaderParameters { ReadingMode = ReadingMode.Deferred, InMemory = true });
        bool all = false;
        string? example = null;
        foreach (var t in m.Types.SelectMany(ComRewriter.AllTypes))
            foreach (var md in t.Methods)
            {
                if (!md.HasBody) continue;
                foreach (var ins in md.Body.Instructions)
                {
                    if ((ins.OpCode != OpCodes.Call && ins.OpCode != OpCodes.Callvirt) || ins.Operand is not MethodReference mr) continue;
                    if (mr.Name == "GetAssemblies" && mr.DeclaringType.FullName == "System.AppDomain") all = true;
                    bool getNameCodeBase = mr.Name == "get_CodeBase" && mr.DeclaringType.FullName == "System.Reflection.AssemblyName";
                    bool read = getNameCodeBase || (s_members.Contains(mr.Name) && mr.DeclaringType.FullName is "System.Reflection.Assembly" or "System.Reflection.Module");
                    if (!read || example != null) continue;
                    string src = Source(m, md, ins.Previous, getNameCodeBase, 0);
                    if (src is not ("own" or "GetEntryAssembly")) example = $"{t.FullName}.{md.Name} reads {mr.Name} of {src}";
                }
            }
        return new LocationAsks(example != null, all, example);
    }

    private static string Source(ModuleDefinition m, MethodDefinition md, Instruction? p, bool throughGetName, int depth)
    {
        if (p == null || depth > 6) return "something";
        if (throughGetName)
        {
            if ((p.OpCode == OpCodes.Call || p.OpCode == OpCodes.Callvirt) && p.Operand is MethodReference g && g.Name == "GetName")
                return Source(m, md, p.Previous, false, depth + 1);
            return "an AssemblyName";
        }
        if ((p.OpCode == OpCodes.Call || p.OpCode == OpCodes.Callvirt) && p.Operand is MethodReference mr)
        {
            switch (mr.Name)
            {
                case "GetExecutingAssembly": return "own";
                case "GetEntryAssembly": return "GetEntryAssembly";
                case "GetCallingAssembly": return "GetCallingAssembly()";
                case "get_ManifestModule": return Source(m, md, p.Previous, false, depth + 1);
                case "get_Assembly" or "get_Module":
                {
                    var q = p.Previous;
                    if (q != null && q.OpCode == OpCodes.Call && q.Operand is MethodReference gt && gt.Name == "GetTypeFromHandle"
                        && q.Previous?.OpCode == OpCodes.Ldtoken && q.Previous.Operand is TypeReference tr)
                    {
                        TypeDefinition? d = null;
                        try { d = tr.Resolve(); } catch { }
                        return d != null && d.Module == m ? "own" : "a foreign type's assembly";
                    }
                    if (q != null && q.Operand is MethodReference gt2 && gt2.Name == "GetType" && q.Previous?.OpCode.Code == Code.Ldarg_0 && md.HasThis) return "own";
                    return "a type's assembly";
                }
            }
            return "what " + mr.Name + " returned";
        }
        switch (p.OpCode.Code)
        {
            case Code.Ldarg: case Code.Ldarg_S: case Code.Ldarg_0: case Code.Ldarg_1: case Code.Ldarg_2: case Code.Ldarg_3: return "a parameter";
            case Code.Ldfld: case Code.Ldsfld: return "a field";
            case Code.Ldloc: case Code.Ldloc_S: case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
            {
                int idx = p.OpCode.Code switch { Code.Ldloc_0 => 0, Code.Ldloc_1 => 1, Code.Ldloc_2 => 2, Code.Ldloc_3 => 3, _ => ((VariableDefinition)p.Operand).Index };
                var stores = md.Body.Instructions.Where(i => StoreIndex(i) == idx).ToList();
                return stores.Count == 1 ? Source(m, md, stores[0].Previous, false, depth + 1) : "a local";
            }
        }
        return "something";
    }

    private static int StoreIndex(Instruction i) => i.OpCode.Code switch
    {
        Code.Stloc_0 => 0, Code.Stloc_1 => 1, Code.Stloc_2 => 2, Code.Stloc_3 => 3,
        Code.Stloc or Code.Stloc_S => ((VariableDefinition)i.Operand).Index,
        _ => -1,
    };
}
