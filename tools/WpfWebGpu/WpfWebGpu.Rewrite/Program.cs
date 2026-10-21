// wpf-rewrite --out[=]<dir> [--search[=]<dir>]... <assembly | @response-file>...
//
// Rewrites what of the given app assemblies needs it -- their COM types, their Windows P/Invokes -- into
// <dir>, under the same file names (BuildRewrite.cs). <dir> is emptied first, so what is in it afterwards
// is exactly this run's: the build replaces each assembly that has a copy there. The search folders hold
// the assemblies' references and the implementation assemblies (WpfWebGpu.Win32, the WebGPU interop).
// A response file lists one argument per line.

using WpfWebGpu.ComRewrite;

var inputs = new List<string>();
var search = new List<string>();
string? outDir = null;
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
    var done = BuildRewrite.Run(inputs, search, outDir, name => name.StartsWith("WpfWebGpu.", StringComparison.Ordinal), Console.WriteLine);
    Console.WriteLine($"wpf-rewrite: {inputs.Count} assemblies read, {done.Count} rewritten");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"wpf-rewrite: error: {e}");
    return 1;
}
