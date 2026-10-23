// wpf-rewrite --out[=]<dir> [--search[=]<dir>]... [--corelib=<System.Private.CoreLib.dll>] [--primitives=<System.Drawing.Primitives.dll>] <assembly | @response-file>...
// wpf-rewrite --placeholders --out[=]<dir> [--published=<name>]... <assembly | @response-file>...
//
// Rewrites what of the given app assemblies needs it -- their COM types, their Windows P/Invokes -- into
// <dir>, under the same file names (BuildRewrite.cs). <dir> is emptied first, so what is in it afterwards
// is exactly this run's: the build replaces each assembly that has a copy there. The search folders hold
// the assemblies' references and the implementation assemblies (WpfWebGpu.Win32, the WebGPU interop).
// A response file lists one argument per line.

using WpfWebGpu.ComRewrite;

var inputs = new List<string>();
var search = new List<string>();
var publishedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
bool placeholdersOnly = false;
string? outDir = null, corelib = null, primitives = null;
var queue = new Queue<string>(args);
while (queue.Count > 0)
{
    string a = queue.Dequeue();
    if (a.StartsWith('@'))
    {
        foreach (string line in File.ReadAllLines(a[1..]))
            if (line.Trim() is { Length: > 0 } l) queue.Enqueue(l);
    }
    else if (a == "--out" && queue.Count > 0) outDir = queue.Dequeue();
    else if (a.StartsWith("--out=", StringComparison.Ordinal)) outDir = a["--out=".Length..];
    else if (a == "--search" && queue.Count > 0) search.Add(queue.Dequeue());
    else if (a.StartsWith("--search=", StringComparison.Ordinal)) search.Add(a["--search=".Length..]);
    else if (a.StartsWith("--corelib=", StringComparison.Ordinal)) corelib = a["--corelib=".Length..];
    else if (a.StartsWith("--primitives=", StringComparison.Ordinal)) primitives = a["--primitives=".Length..];
    else if (a.StartsWith("--published=", StringComparison.Ordinal)) publishedNames.Add(a["--published=".Length..]);
    else if (a == "--placeholders") placeholdersOnly = true;
    else inputs.Add(a);
}
if (outDir == null)
{
    Console.Error.WriteLine("usage: wpf-rewrite --out <dir> [--search <dir>]... <assembly | @response-file>...");
    return 2;
}

if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
// an input's own folder is searched too: a package's assembly references its neighbours
search.AddRange(inputs.Select(i => Path.GetDirectoryName(Path.GetFullPath(i))!).Distinct());
try
{
    if (placeholdersOnly)
    {
        // what the app's assemblies reference and nothing provides -- none of them and none of what else
        // the app is built with (--published): placeholders holding the types they name, in <dir> (Placeholders)
        var scans = inputs.Select(i => AssemblyScan.Read(i)).Where(x => x != null).Select(x => x!).ToList();
        var provided = new HashSet<string>(scans.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        provided.UnionWith(publishedNames);
        // An app may carry an assembly inside another, as a resource it loads itself when the name is
        // asked for (GraphWorX's script engine embeds its JScript and resolves it from the resource): that
        // assembly is the app's, not missing, and ships as it is -- what the app would load anyway. What
        // it references is looked at in turn.
        var embedded = Placeholders.EmbeddedAssemblies(scans.Select(x => x.File));
        List<string> missing;
        while (true)
        {
            missing = scans.SelectMany(x => x.References).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(r => !provided.Contains(r)).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
            var found = Placeholders.MatchEmbedded(missing, scans.Select(x => x.File), embedded, search);
            if (found.Count == 0) break;
            Directory.CreateDirectory(outDir);
            foreach (var (name, (bytes, source)) in found)
            {
                string path = Path.Combine(outDir, name + ".dll");
                File.WriteAllBytes(path, bytes);
                provided.Add(name);
                if (AssemblyScan.Read(path) is { } scan) scans.Add(scan);
                Console.WriteLine($"wpf-rewrite: {name}, referenced and not shipped, is the app's own from {source}: bundled from there");
            }
        }
        if (missing.Count > 0)
        {
            Placeholders.Write(missing, scans.Select(x => x.File), outDir);
            Console.WriteLine($"wpf-rewrite: referenced and not shipped, placeholders of the types named in them: {string.Join(", ", missing)}");
        }
        return 0;
    }
    // System.Drawing.Primitives made its Windows build (SystemColorsPatch): its user32 GetSysColor is
    // then rewritten with the app's P/Invokes, onto the stack's user32
    if (primitives != null && SystemColorsPatch.Write(primitives, Path.Combine(outDir, ".primitives"), Console.WriteLine) is { } patched)
        inputs.Add(patched);
    var done = BuildRewrite.Run(inputs, search, outDir, name => name.StartsWith("WpfWebGpu.", StringComparison.Ordinal), Console.WriteLine);
    // the multithreaded runtime's CoreLib, its Lock made to tell threads apart (CoreLibPatch)
    if (corelib != null) CoreLibPatch.Write(corelib, outDir, Console.WriteLine);
    Console.WriteLine($"wpf-rewrite: {inputs.Count} assemblies read, {done.Count} rewritten");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"wpf-rewrite: error: {e}");
    return 1;
}
