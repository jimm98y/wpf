// `wpfrun audit`: what an unmodified app needs that the WebGPU stack (on this OS) does not have.
//
// Static, so it sees every code path, not just the ones a test run happens to reach, and it runs over
// EVERY managed assembly in the folder rather than walking references from Main, because plugin hosts
// load assemblies by name at run time. The numbers it reports are the progress metric for the shims.

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;

namespace WpfWebGpu.Audit;

internal sealed class Auditor
{
    public sealed class Report
    {
        public List<string> Assemblies { get; } = new();
        public List<(string Assembly, string Kind)> NotAnyCpu { get; } = new();
        public SortedDictionary<string, SortedSet<string>> MissingAssemblies { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>References stock cannot resolve either: not in the app's folder nor in the frameworks it runs on.</summary>
        public SortedDictionary<string, SortedSet<string>> AbsentForStock { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> StockFrameworks { get; } = new();
        public SortedDictionary<string, SortedSet<string>> UnifiedAssemblies { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedDictionary<string, SortedSet<string>> MissingTypes { get; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, SortedSet<string>> MissingMembers { get; } = new(StringComparer.Ordinal);
        /// <summary>module (lower case) -> entry point -> referencing assemblies.</summary>
        public SortedDictionary<string, SortedDictionary<string, SortedSet<string>>> PInvokes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedDictionary<string, SortedSet<string>> NotableApis { get; } = new(StringComparer.Ordinal);
        public List<string> Notes { get; } = new();
    }

    private readonly Resolver _resolver;
    private readonly string _appDir;
    private readonly HashSet<string> _appNativeFiles;
    private readonly StockSet? _stock;

    public Auditor(Resolver resolver, string appDir, StockSet? stock = null)
    {
        _resolver = resolver;
        _appDir = appDir;
        _stock = stock is { Known: true } ? stock : null;
        _appNativeFiles = Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetFileName(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public Report Run()
    {
        var report = new Report();
        if (_stock != null) report.StockFrameworks.AddRange(_stock.Frameworks);
        foreach (string file in Directory.EnumerateFiles(_appDir, "*.dll").Concat(Directory.EnumerateFiles(_appDir, "*.exe")).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            using var asm = AssemblyIndex.TryOpen(file);
            if (asm == null || !asm.Reader.IsAssembly) continue;
            // An assembly the overlay replaces is not the app's code any more.
            if (_resolver.Set.ByName.TryGetValue(asm.Name, out var entry) && entry.Origin is "overlay" && !SamePath(entry.Path, file)) continue;
            report.Assemblies.Add(asm.Name);
            Inspect(asm, report);
        }
        return report;
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private void Inspect(AssemblyIndex asm, Report report)
    {
        var md = asm.Reader;
        if (!AssemblyIndex.IsAnyCpuIl(asm.Headers, out string kind)) report.NotAnyCpu.Add((asm.Name, kind));

        // Assembly references.
        foreach (var h in md.AssemblyReferences)
        {
            var r = md.GetAssemblyReference(h);
            string name = md.GetString(r.Name);
            if (!_resolver.Set.ByName.TryGetValue(name, out var e))
            {
                Add(_stock != null && !_stock.Contains(name) ? report.AbsentForStock : report.MissingAssemblies, $"{name} {r.Version}", asm.Name);
            }
            else if (e.Version < r.Version)
            {
                Add(report.UnifiedAssemblies, $"{name} {r.Version} -> {e.Version} ({e.Origin})", asm.Name);
            }
        }

        // Type references into assemblies the stack or the framework supplies.
        foreach (var h in md.TypeReferences)
        {
            string refAsm = _resolver.AssemblyRefName(asm, h);
            if (!Supplied(refAsm)) continue;
            if (_resolver.Resolve(asm, h) == null && _resolver.Open(refAsm) != null)
            {
                Add(report.MissingTypes, $"[{refAsm}]{asm.FullName(h)}", asm.Name);
            }
        }

        // Member references, compared by signature with every type resolved to where it is defined.
        var names = new SignatureNames(_resolver, asm);
        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            Resolver.Resolved? owner = mr.Parent.Kind switch
            {
                HandleKind.TypeReference => _resolver.Resolve(asm, (TypeReferenceHandle)mr.Parent),
                HandleKind.TypeSpecification => _resolver.GenericDefinitionOf(asm, (TypeSpecificationHandle)mr.Parent),
                _ => null,
            };
            if (owner == null) continue;               // reported as a missing type, or app-internal
            string ownerAsm = owner.Value.Assembly.Name;
            string memberName = md.GetString(mr.Name);
            if (!Supplied(ownerAsm))
            {
                // App-local copies of Windows-only packages (System.Management, ...) still count as use.
                Notable(report, owner.Value, memberName, "", asm.Name);
                continue;
            }

            string sig;
            try
            {
                sig = mr.GetKind() == MemberReferenceKind.Method
                    ? SignatureNames.Method(mr.DecodeMethodSignature(names, null))
                    : "field " + mr.DecodeFieldSignature(names, null);
            }
            catch
            {
                continue;
            }
            if (!HasMember(owner.Value, memberName, sig, mr.GetKind() == MemberReferenceKind.Method))
            {
                Add(report.MissingMembers, $"{owner.Value.Key}::{memberName}{(sig.StartsWith("field ") ? " : " + sig[6..] : sig)}", asm.Name);
            }
            Notable(report, owner.Value, memberName, sig, asm.Name);
        }

        // P/Invokes into modules the app does not carry itself: Windows system DLLs.
        foreach (var mh in md.MethodDefinitions)
        {
            var m = md.GetMethodDefinition(mh);
            var import = m.GetImport();
            if (import.Module.IsNil) continue;
            string module = md.GetString(md.GetModuleReference(import.Module).Name);
            string entry = import.Name.IsNil ? md.GetString(m.Name) : md.GetString(import.Name);
            string file = module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? module : module + ".dll";
            if (_appNativeFiles.Contains(file) || _appNativeFiles.Contains(module)) continue;
            string key = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (!report.PInvokes.TryGetValue(key, out var entries)) report.PInvokes[key] = entries = new(StringComparer.Ordinal);
            Add(entries, entry, asm.Name);
        }
    }

    /// <summary>Only references into what the stack or the shared framework supplies are this tool's business.</summary>
    private bool Supplied(string assemblyName) =>
        _resolver.Set.ByName.TryGetValue(assemblyName, out var e) && e.Origin is "overlay" or "framework";

    private bool HasMember(Resolver.Resolved type, string name, string sig, bool method)
    {
        for (Resolver.Resolved? t = type; t != null; t = _resolver.BaseOf(t.Value))
        {
            var asm = t.Value.Assembly;
            var md = asm.Reader;
            var def = md.GetTypeDefinition(t.Value.Handle);
            var names = new SignatureNames(_resolver, asm);
            if (method)
            {
                foreach (var mh in def.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    if (!md.StringComparer.Equals(m.Name, name)) continue;
                    try { if (SignatureNames.Method(m.DecodeSignature(names, null)) == sig) return true; } catch { }
                }
            }
            else
            {
                foreach (var fh in def.GetFields())
                {
                    var f = md.GetFieldDefinition(fh);
                    if (!md.StringComparer.Equals(f.Name, name)) continue;
                    try { if ("field " + f.DecodeSignature(names, null) == sig) return true; } catch { }
                }
            }
            if (name == ".ctor") break;                 // constructors are not inherited
        }
        return false;
    }

    // Windows-only APIs worth knowing about before a run: each needs a shim or a detour off Windows.
    private static readonly (string TypePrefix, string? Member, string Label)[] s_notable =
    {
        ("[Microsoft.Win32.Registry]Microsoft.Win32.Registry", null, "registry"),
        ("[System.Diagnostics.EventLog]", null, "event log"),
        ("[System.Diagnostics.PerformanceCounter]", null, "performance counters"),
        ("[System.Management]", null, "WMI (System.Management)"),
        ("[System.Security.Cryptography.ProtectedData]", null, "DPAPI (ProtectedData)"),
        ("[System.ServiceProcess.ServiceController]", null, "Windows services"),
        ("[System.Security.Principal.Windows]System.Security.Principal.WindowsIdentity", null, "WindowsIdentity"),
        ("[System.IO.MemoryMappedFiles]System.IO.MemoryMappedFiles.MemoryMappedFile", "CreateOrOpen", "named memory-mapped files"),
        ("[System.IO.MemoryMappedFiles]System.IO.MemoryMappedFiles.MemoryMappedFile", "OpenExisting", "named memory-mapped files"),
        ("[System.Private.CoreLib]System.Threading.EventWaitHandle", null, "EventWaitHandle"),
        ("[System.Private.CoreLib]System.Threading.Semaphore", null, "Semaphore"),
        ("[System.Private.CoreLib]System.Environment", "GetFolderPath", "Environment.GetFolderPath"),
    };

    private static void Notable(Report report, Resolver.Resolved owner, string member, string sig, string user)
    {
        string key = owner.Key;
        foreach (var (prefix, m, label) in s_notable)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (m != null && member != m) continue;
            // Only the NAMED forms of the sync primitives are Windows-only.
            if (label is "EventWaitHandle" or "Semaphore" && !(member is ".ctor" or "OpenExisting" or "TryOpenExisting" && sig.Contains("System.String", StringComparison.Ordinal))) continue;
            Add(report.NotableApis, label, user);
        }
    }

    private static void Add(IDictionary<string, SortedSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var set)) map[key] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(value);
    }

    public static string ToMarkdown(Report r, string appName, string rid, Func<string, string, bool>? shimHas = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# wpfrun audit: {appName} ({rid})");
        sb.AppendLine();
        sb.AppendLine($"{r.Assemblies.Count} managed assemblies inspected.");
        sb.AppendLine();
        sb.AppendLine("| | count |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| missing assemblies | {r.MissingAssemblies.Count} |");
        if (r.AbsentForStock.Count > 0) sb.AppendLine($"| absent for stock too (not gaps) | {r.AbsentForStock.Count} |");
        sb.AppendLine($"| missing types | {r.MissingTypes.Count} |");
        sb.AppendLine($"| missing members | {r.MissingMembers.Count} |");
        sb.AppendLine($"| Windows DLLs P/Invoked | {r.PInvokes.Count} ({r.PInvokes.Sum(p => p.Value.Count)} entry points) |");
        sb.AppendLine($"| not AnyCPU | {r.NotAnyCpu.Count} |");
        sb.AppendLine();

        Section(sb, "Missing assemblies", r.MissingAssemblies);
        if (r.AbsentForStock.Count > 0)
        {
            sb.AppendLine($"## Absent for stock too ({r.AbsentForStock.Count})");
            sb.AppendLine($"Referenced but shipped neither in the app's folder nor in the frameworks its runtimeconfig.json runs on ({string.Join(", ", r.StockFrameworks)}), so stock fails the same way on those code paths. Not stack gaps.");
            sb.AppendLine();
            foreach (var (key, users) in r.AbsentForStock) sb.AppendLine($"- `{key}` — {Users(users)}");
            sb.AppendLine();
        }
        Section(sb, "Missing types", r.MissingTypes);
        Section(sb, "Missing members", r.MissingMembers);
        Section(sb, "Windows-only APIs used", r.NotableApis);
        if (r.NotAnyCpu.Count > 0)
        {
            sb.AppendLine("## Not AnyCPU (cannot load on this host)");
            foreach (var (a, k) in r.NotAnyCpu) sb.AppendLine($"- {a}: {k}");
            sb.AppendLine();
        }
        if (r.PInvokes.Count > 0)
        {
            sb.AppendLine("## P/Invokes into Windows DLLs");
            foreach (var (module, entries) in r.PInvokes)
            {
                sb.AppendLine($"### {module} ({entries.Count})");
                foreach (var (entry, users) in entries)
                {
                    string mark = shimHas == null ? "" : shimHas(module, entry) ? " ✓" : " ✗";
                    sb.AppendLine($"- `{entry}`{mark} — {Users(users)}");
                }
            }
            sb.AppendLine();
        }
        Section(sb, "References bound to a lower version (unified at run time)", r.UnifiedAssemblies);
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, IDictionary<string, SortedSet<string>> map)
    {
        if (map.Count == 0) return;
        sb.AppendLine($"## {title} ({map.Count})");
        foreach (var (key, users) in map) sb.AppendLine($"- `{key}` — {Users(users)}");
        sb.AppendLine();
    }

    private static string Users(SortedSet<string> users) =>
        users.Count <= 4 ? string.Join(", ", users) : string.Join(", ", users.Take(4)) + $" and {users.Count - 4} more";
}
