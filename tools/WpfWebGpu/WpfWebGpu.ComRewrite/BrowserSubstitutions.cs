// An app's calls to members .NET's browser build refuses for settings the browser takes care of itself,
// rewritten at build onto WpfWebGpu.ComInterop's BrowserApis (Documentation/pinvoke-rewriter.md). A method
// of BrowserApis named <Type>_<member> stands in for that member: Type's short name, the member's own name
// (set_KeepAliveInterval, get_DefaultConnectionLimit), the instance first for an instance member. So the
// table is BrowserApis itself, and a member is supported by adding its method there.

using Mono.Cecil;
using Mono.Cecil.Cil;

namespace WpfWebGpu.ComRewrite;

public sealed class BrowserSubstitutions
{
    private static readonly HashSet<string> s_types = new(StringComparer.Ordinal)
    {
        "System.Net.WebSockets.ClientWebSocketOptions", "System.Net.ServicePointManager", "System.Runtime.InteropServices.Marshal",
        "System.Environment",
    };

    private readonly ModuleDefinition _apis;
    private readonly Dictionary<string, MethodDefinition> _byMember = new(StringComparer.Ordinal);

    private BrowserSubstitutions(ModuleDefinition apis)
    {
        _apis = apis;
        TypeDefinition? t = apis.GetType("WpfWebGpu.ComInterop.BrowserApis");
        if (t == null) return;
        foreach (MethodDefinition m in t.Methods)
        {
            int at = m.Name.IndexOf('_');
            if (!m.IsStatic || !m.IsPublic || at <= 0) continue;
            _byMember[Key(m.Name[..at], m.Name[(at + 1)..], m.Parameters.Count, m.GenericParameters.Count)] = m;
        }
    }

    // a member by its type, name, parameters (the instance counted) and generic parameters: what tells
    // the overloads apart that are stood in for (Marshal.GetFunctionPointerForDelegate and <TDelegate>)
    private static string Key(string type, string member, int parameters, int generic) => $"{type}::{member}/{parameters}`{generic}";

    /// <summary>The substitutions WpfWebGpu.ComInterop in <paramref name="searchDirs"/> offers; null without it.</summary>
    public static BrowserSubstitutions? For(IEnumerable<string> searchDirs)
    {
        foreach (string d in searchDirs)
        {
            string p = Path.Combine(d, "WpfWebGpu.ComInterop.dll");
            if (File.Exists(p)) return new BrowserSubstitutions(ModuleDefinition.ReadModule(p, new ReaderParameters { InMemory = true }));
        }
        return null;
    }

    private MethodDefinition? For(MethodReference r)
    {
        if (!s_types.Contains(r.DeclaringType.FullName)) return null;
        int count = r.Parameters.Count + (r.HasThis ? 1 : 0);
        MethodReference e = r is GenericInstanceMethod g ? g.ElementMethod : r;
        return _byMember.TryGetValue(Key(r.DeclaringType.Name, r.Name, count, e.GenericParameters.Count), out var m) ? m : null;
    }

    /// <summary>Whether <paramref name="module"/> calls a member this substitutes (metadata only).</summary>
    public bool Uses(ModuleDefinition module) => module.GetMemberReferences().OfType<MethodReference>().Any(r => For(r) != null);

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
                    if ((i.OpCode != OpCodes.Call && i.OpCode != OpCodes.Callvirt) || i.Operand is not MethodReference r || For(r) is not { } stand) continue;
                    i.OpCode = OpCodes.Call;
                    MethodReference to = module.ImportReference(stand);
                    if (r is GenericInstanceMethod g)
                    {
                        var gi = new GenericInstanceMethod(to);
                        foreach (TypeReference a in g.GenericArguments) gi.GenericArguments.Add(a);
                        to = gi;
                    }
                    i.Operand = to;
                    n++;
                }
            }
        return n;
    }
}
