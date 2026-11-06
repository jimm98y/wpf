// The installation's machine state, made ready for a package: its registry as the hive files the registry
// stand-in reads (one per hive, FileRegistryStore's format) with its Windows paths turned into tokens, and
// its ProgramData folders as the list of files to put under ProgramData. The files are not copied here: a
// package writer streams them from where they are.

using System.Text;
using System.Text.RegularExpressions;

namespace WpfWebGpu.Packager;

public sealed class StagedState
{
    /// <summary>The folder holding the hive files (HKEY_LOCAL_MACHINE.reg, ...); empty with no registry input.</summary>
    public required string RegistryDir { get; init; }
    /// <summary>Each ProgramData file: where it is, and its path under ProgramData ('/'-separated).</summary>
    public required IReadOnlyList<(string Source, string Target)> ProgramDataFiles { get; init; }
    /// <summary>The product's icon (.ico or .png) in the work folder, or null.</summary>
    public string? Icon { get; init; }
    /// <summary>ProgramData files left out of the seed (a machine's own certificate), by target, each with why.</summary>
    public IReadOnlyDictionary<string, string> LeftOut { get; init; } = new Dictionary<string, string>();
    public int Keys { get; init; }
    public int Values { get; init; }
}

public static class Staging
{
    public static StagedState Stage(PackageManifest manifest, string workDir)
    {
        string registryDir = Path.Combine(workDir, "state", "registry");
        if (Directory.Exists(registryDir)) Directory.Delete(registryDir, recursive: true);
        Directory.CreateDirectory(registryDir);

        var tree = new RegistryTree();
        foreach (string file in manifest.Inputs.Registry)
            tree.Apply(RegText.Parse(RegText.Read(file)));
        var mapper = new PathMapper(manifest.PathMappings);
        tree.MapStrings(mapper.Map);
        tree.WriteHives(registryDir);

        var excludes = new GlobSet(manifest.Exclude);
        var files = new List<(string, string)>();
        foreach (string root in manifest.Inputs.ProgramData)
        {
            string name = Path.GetFileName(root);
            foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                if (excludes.Matches(rel) || Path.GetFileName(f) == ".DS_Store") continue;
                files.Add((f, name + "/" + rel));
            }
        }
        var machines = MachineCertificates.Find(files);
        files.RemoveAll(f => machines.ContainsKey(f.Item2));
        return new StagedState
        {
            RegistryDir = registryDir, ProgramDataFiles = files, LeftOut = machines, Icon = StageIcon(manifest, workDir), Keys = tree.KeyCount, Values = tree.ValueCount,
        };
    }

    private static string? StageIcon(PackageManifest manifest, string workDir)
    {
        if (manifest.Product.Icon.Length == 0) return null;
        string source = Path.Combine(manifest.Inputs.InstallDir, manifest.Product.Icon);
        string ext = Path.GetExtension(source).ToLowerInvariant();
        byte[]? bytes = ext == ".exe" || ext == ".dll" ? PeFile.ReadIcon(source) : File.Exists(source) ? File.ReadAllBytes(source) : null;
        if (bytes == null) return null;
        string icon = Path.Combine(workDir, "icon" + (ext == ".png" ? ".png" : ".ico"));
        File.WriteAllBytes(icon, bytes);
        return icon;
    }

    /// <summary>A staged hive file's text with its tokens resolved: where the package is installed.</summary>
    public static string Resolve(string text, IReadOnlyDictionary<string, string> tokens)
    {
        foreach (var (token, value) in tokens) text = text.Replace(token, Escape(value), StringComparison.Ordinal);
        return text;
    }

    // FileRegistryStore's escaping: the characters that would end a line or a quoted name
    internal static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is '%' or '"' or '\n' or '\r' or '\0') sb.Append('%').Append(((int)c).ToString("X2"));
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>The Windows paths in a string turned into tokens (PathMapping), longest first, ignoring case as
/// Windows does.</summary>
public sealed class PathMapper
{
    private readonly (Regex From, string To)[] _rules;

    public PathMapper(IEnumerable<PathMapping> mappings) =>
        _rules = mappings.Where(m => m.From.Length > 0).OrderByDescending(m => m.From.Length)
            // a whole path segment: C:\Program Files\A maps C:\Program Files\A\b, not C:\Program Files\AB
            .Select(m => (new Regex(Regex.Escape(m.From.TrimEnd('\\')) + @"(?=\\|$|[^\w])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), m.To))
            .ToArray();

    public string Map(string text)
    {
        foreach (var (from, to) in _rules) text = from.Replace(text, to.Replace("$", "$$"));
        return text;
    }
}

/// <summary>A registry as a .reg file builds it: keys by name ignoring case (keeping the first spelling),
/// values in the order set.</summary>
internal sealed class RegistryTree
{
    private sealed class Node
    {
        public readonly Dictionary<string, Node> Children = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> ChildOrder = new();
        public readonly Dictionary<string, (object Value, RegKind Kind)> Values = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> ValueOrder = new();
        public string Name = "";
    }

    private readonly Dictionary<string, Node> _hives = new(StringComparer.Ordinal);

    public int KeyCount { get; private set; }
    public int ValueCount { get; private set; }

    public void Apply(IEnumerable<RegEntry> entries)
    {
        Node? current = null;
        foreach (var e in entries)
        {
            switch (e.Kind)
            {
                case RegEntryKind.Key:
                    current = Open(e.Hive, e.Key, create: true);
                    break;
                case RegEntryKind.DeleteKey:
                    current = null;
                    int slash = e.Key.LastIndexOf('\\');
                    var parent = Open(e.Hive, slash < 0 ? "" : e.Key[..slash], create: false);
                    string leaf = slash < 0 ? e.Key : e.Key[(slash + 1)..];
                    if (parent != null && parent.Children.Remove(leaf)) parent.ChildOrder.RemoveAll(n => n.Equals(leaf, StringComparison.OrdinalIgnoreCase));
                    break;
                case RegEntryKind.DeleteValue:
                    if (current != null && current.Values.Remove(e.Name)) current.ValueOrder.RemoveAll(n => n.Equals(e.Name, StringComparison.OrdinalIgnoreCase));
                    break;
                case RegEntryKind.Value:
                    if (current == null) break;
                    if (!current.Values.ContainsKey(e.Name)) current.ValueOrder.Add(e.Name);
                    current.Values[e.Name] = (e.Value!, e.ValueKind);
                    break;
            }
        }
        KeyCount = ValueCount = 0;
        foreach (var hive in _hives.Values) Count(hive);
    }

    private void Count(Node n)
    {
        ValueCount += n.Values.Count;
        foreach (var c in n.Children.Values) { KeyCount++; Count(c); }
    }

    private Node? Open(string hive, string key, bool create)
    {
        if (!_hives.TryGetValue(hive, out var node))
        {
            if (!create) return null;
            _hives[hive] = node = new Node { Name = hive };
        }
        foreach (string part in key.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.Children.TryGetValue(part, out var child))
            {
                if (!create) return null;
                node.Children[part] = child = new Node { Name = part };
                node.ChildOrder.Add(part);
            }
            node = child;
        }
        return node;
    }

    public void MapStrings(Func<string, string> map)
    {
        foreach (var hive in _hives.Values) Walk(hive);
        void Walk(Node n)
        {
            foreach (string name in n.ValueOrder)
            {
                var (value, kind) = n.Values[name];
                n.Values[name] = value switch
                {
                    string s => (map(s), kind),
                    string[] m => (m.Select(map).ToArray(), kind),
                    _ => (value, kind),
                };
            }
            foreach (var c in n.Children.Values) Walk(c);
        }
    }

    /// <summary>One file per hive, as FileRegistryStore writes and reads it.</summary>
    public void WriteHives(string dir)
    {
        foreach (var (name, root) in _hives)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(name).Append('\n');
            sb.Append("# Written by the WPF-on-WebGPU Packager.\n");
            Write(sb, root, "");
            File.WriteAllText(Path.Combine(dir, name + ".reg"), sb.ToString());
        }
    }

    private static void Write(StringBuilder sb, Node key, string path)
    {
        if (key.Values.Count > 0 || path.Length > 0)
        {
            sb.Append('[').Append(path).Append("]\n");
            foreach (string name in key.ValueOrder)
            {
                var (value, kind) = key.Values[name];
                sb.Append('"').Append(Staging.Escape(name)).Append("\"=").Append(Format(value, kind)).Append('\n');
            }
        }
        foreach (string child in key.ChildOrder)
        {
            var node = key.Children[child];
            Write(sb, node, path.Length == 0 ? node.Name : path + "\\" + node.Name);
        }
    }

    private static string Format(object v, RegKind kind) => v switch
    {
        int i => "dword:" + unchecked((uint)i).ToString("x8"),
        long l => "qword:" + unchecked((ulong)l).ToString("x16"),
        byte[] b => "bin:" + Convert.ToBase64String(b),
        string[] m => "multi:" + Staging.Escape(string.Join("\0", m)),
        string s when kind == RegKind.ExpandString => "expand:" + Staging.Escape(s),
        _ => "str:" + Staging.Escape(Convert.ToString(v) ?? ""),
    };
}

/// <summary>Globs over '/'-separated relative paths: '*' within a segment, '**' across segments, '?' one
/// character; ignoring case.</summary>
public sealed class GlobSet
{
    private readonly Regex[] _globs;

    public GlobSet(IEnumerable<string> globs) => _globs = globs.Where(g => g.Length > 0).Select(ToRegex).ToArray();

    public bool Matches(string relativePath) => _globs.Any(g => g.IsMatch(relativePath));

    private static Regex ToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        string g = glob.Replace('\\', '/');
        for (int i = 0; i < g.Length; i++)
        {
            char c = g[i];
            if (c == '*' && i + 1 < g.Length && g[i + 1] == '*')
            {
                i++;
                if (i + 1 < g.Length && g[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                else sb.Append(".*");
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
