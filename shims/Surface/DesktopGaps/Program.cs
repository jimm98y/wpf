// What of the Windows Desktop Runtime (Microsoft.WindowsDesktop.App) the stack does not have: every public type and
// member of its reference assemblies, looked up in the stack's own assemblies (and what those forward to). Missing: no
// such type or member, which an app compiled against it fails on (TypeLoadException, MissingMethodException).
// Throws: a short body that throws PlatformNotSupportedException or NotImplementedException. A member that is there and
// does nothing is not found by this.
//   DesktopGaps <reference dir> <report file> <assembly dir>...   (the first directory holding an assembly wins)
// Run through shims/Surface/desktop-gaps.sh.
using Mono.Cecil;
using Mono.Cecil.Cil;
// args: refDir outFile implDir...
string refDir = args[0], outFile = args[1];
var implDirs = args.Skip(2).ToArray();
var resolver = new DefaultAssemblyResolver(); foreach (var d in implDirs) resolver.AddSearchDirectory(d);
var rp = new ReaderParameters { AssemblyResolver = resolver };
var cache = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
AssemblyDefinition Load(string name)
{
    if (cache.TryGetValue(name, out var a)) return a;
    foreach (var d in implDirs) { var f = Path.Combine(d, name + ".dll"); if (File.Exists(f)) { try { a = AssemblyDefinition.ReadAssembly(f, rp); } catch { } break; } }
    return cache[name] = a;
}
TypeDefinition Find(string asm, string fullName, int depth = 0)
{
    if (depth > 5) return null;
    var a = Load(asm); if (a == null) return null;
    var m = a.MainModule;
    var t = m.GetType(fullName.Replace('+', '/')) ?? m.GetType(fullName);
    if (t != null) return t;
    string top = fullName.Split('/')[0];
    foreach (var e in m.ExportedTypes)
        if (e.FullName == top && e.Scope is AssemblyNameReference r)
        {
            var tt = Find(r.Name, top, depth + 1);
            if (tt == null) return null;
            if (top == fullName) return tt;
            return tt.Module.GetType(fullName);
        }
    return null;
}
string TN(TypeReference t) => t switch
{
    GenericParameter g => (g.Owner is MethodReference ? "!!" : "!") + g.Position,
    ByReferenceType b => TN(b.ElementType) + "&",
    ArrayType ar => TN(ar.ElementType) + "[]",
    PointerType p => TN(p.ElementType) + "*",
    GenericInstanceType gi => gi.ElementType.Name + "<" + string.Join(",", gi.GenericArguments.Select(TN)) + ">",
    RequiredModifierType rm => TN(rm.ElementType),
    OptionalModifierType om => TN(om.ElementType),
    _ => t.Name,
};
string Sig(MethodDefinition m) => m.Name + "`" + m.GenericParameters.Count + "(" + string.Join(",", m.Parameters.Select(p => TN(p.ParameterType))) + ")";
bool Visible(MethodDefinition m) => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly;
bool Stub(MethodDefinition m)
{
    if (!m.HasBody) return false;
    var ins = m.Body.Instructions;
    if (ins.Count > 14) return false;
    return ins.Any(i => i.OpCode == OpCodes.Throw) && ins.Any(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference c &&
        (c.DeclaringType.Name is "PlatformNotSupportedException" or "NotImplementedException"));
}
IEnumerable<TypeDefinition> All(TypeDefinition t) { yield return t; foreach (var n in t.NestedTypes.Where(n => n.IsNestedPublic || n.IsNestedFamily || n.IsNestedFamilyOrAssembly)) foreach (var x in All(n)) yield return x; }
var w = new StreamWriter(outFile);
Console.WriteLine($"{"assembly",-45} {"types",6} {"missT",6} {"members",8} {"missM",6} {"stubM",6}");
foreach (var f in Directory.GetFiles(refDir, "*.dll").OrderBy(x => x))
{
    var refAsm = AssemblyDefinition.ReadAssembly(f);
    string name = refAsm.Name.Name;
    int types = 0, missT = 0, members = 0, missM = 0, stubM = 0;
    var implAsm = Load(name);
    w.WriteLine($"## {name}{(implAsm == null ? " (NO ASSEMBLY)" : "")}");
    var refTypes = refAsm.MainModule.Types.Where(t => t.IsPublic).SelectMany(All).ToList();
    // forwarded types in the ref: follow them as well (System.Drawing -> Drawing.Common etc. are facades)
    foreach (var rt in refTypes)
    {
        types++;
        var it = implAsm == null ? null : Find(name, rt.FullName);
        var rms = rt.Methods.Where(Visible).Select(Sig).ToList();
        rms.AddRange(rt.Fields.Where(x => x.IsPublic || x.IsFamily).Select(x => "F:" + x.Name));
        rms.AddRange(rt.Events.Where(x => x.AddMethod != null && Visible(x.AddMethod)).Select(x => "E:" + x.Name));
        if (it == null) { missT++; members += rms.Count; missM += rms.Count; w.WriteLine($"  MISSING TYPE {rt.FullName}"); continue; }
        var im = new Dictionary<string, MethodDefinition>(); var ifsBase = new HashSet<string>();
        for (var cur = it; cur != null;)
        {
            foreach (var m in cur.Methods) im.TryAdd(Sig(m), m);
            if (cur.BaseType == null) break;
            try { cur = cur.BaseType.Resolve(); } catch { cur = null; }
            if (cur != null) { foreach (var f2 in cur.Fields) ifsBase.Add("F:" + f2.Name); foreach (var e2 in cur.Events) ifsBase.Add("E:" + e2.Name); }
        }
        var ifs = new HashSet<string>(it.Fields.Select(x => "F:" + x.Name).Concat(it.Events.Select(x => "E:" + x.Name)).Concat(ifsBase));
        var miss = new List<string>(); var stubs = new List<string>();
        foreach (var s in rms)
        {
            members++;
            if (s.StartsWith("F:", StringComparison.Ordinal) || s.StartsWith("E:", StringComparison.Ordinal)) { if (!ifs.Contains(s)) { missM++; miss.Add(s); } continue; }
            if (!im.TryGetValue(s, out var md)) { missM++; miss.Add(s); }
            else if (Stub(md)) { stubM++; stubs.Add(s); }
        }
        if (miss.Count > 0) w.WriteLine($"  {rt.FullName}: missing {string.Join("; ", miss)}");
        if (stubs.Count > 0) w.WriteLine($"  {rt.FullName}: throws {string.Join("; ", stubs)}");
    }
    Console.WriteLine($"{name,-45} {types,6} {missT,6} {members,8} {missM,6} {stubM,6}");
}
w.Close();
