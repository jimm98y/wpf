// Metadata of one assembly, read once, with the lookups the audit needs: types by full name, where
// a forwarded type really lives, and every member's signature in a form that can be compared ACROSS
// assemblies.
//
// That last part is the point. A reference compiled against .NET's WinForms names
// Control.set_BackColor([System.Drawing.Primitives]System.Drawing.Color); the stack's WinForms defines
// set_BackColor([Mono.System.Drawing]System.Drawing.Color). Same text, different type, and the runtime
// throws MissingMethodException. So every type in a signature is written with the assembly it is
// finally DEFINED in, after following type forwarders through the same load set the app runs with.

using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace WpfWebGpu.Audit;

internal sealed class AssemblyIndex : IDisposable
{
    private readonly PEReader _pe;
    private Dictionary<string, TypeDefinitionHandle>? _types;
    private Dictionary<string, ExportedTypeHandle>? _exported;

    public string Path { get; }
    public string Name { get; }
    public MetadataReader Reader { get; }
    public PEHeaders Headers => _pe.PEHeaders;

    private AssemblyIndex(string path, PEReader pe)
    {
        Path = path;
        _pe = pe;
        Reader = pe.GetMetadataReader();
        Name = Reader.IsAssembly ? Reader.GetString(Reader.GetAssemblyDefinition().Name) : System.IO.Path.GetFileNameWithoutExtension(path);
    }

    public static AssemblyIndex? TryOpen(string path)
    {
        try
        {
            // The whole image in memory: an audit holds hundreds of assemblies open at once, far past
            // macOS's default limit of 256 open files, and a PEReader over a stream keeps its file open.
            var pe = new PEReader(ImmutableArray.Create(File.ReadAllBytes(path)));
            if (!pe.HasMetadata) { pe.Dispose(); return null; }
            return new AssemblyIndex(path, pe);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _pe.Dispose();

    public Dictionary<string, TypeDefinitionHandle> Types
    {
        get
        {
            if (_types != null) return _types;
            var map = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);
            foreach (var h in Reader.TypeDefinitions) map[FullName(h)] = h;
            return _types = map;
        }
    }

    public Dictionary<string, ExportedTypeHandle> Exported
    {
        get
        {
            if (_exported != null) return _exported;
            var map = new Dictionary<string, ExportedTypeHandle>(StringComparer.Ordinal);
            foreach (var h in Reader.ExportedTypes) map[FullName(h)] = h;
            return _exported = map;
        }
    }

    public string FullName(TypeDefinitionHandle h)
    {
        var t = Reader.GetTypeDefinition(h);
        string name = Reader.GetString(t.Name);
        if (t.IsNested) return FullName(t.GetDeclaringType()) + "/" + name;
        string ns = Reader.GetString(t.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public string FullName(ExportedTypeHandle h)
    {
        var t = Reader.GetExportedType(h);
        string name = Reader.GetString(t.Name);
        if (t.Implementation.Kind == HandleKind.ExportedType) return FullName((ExportedTypeHandle)t.Implementation) + "/" + name;
        string ns = Reader.GetString(t.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public string FullName(TypeReferenceHandle h)
    {
        var t = Reader.GetTypeReference(h);
        string name = Reader.GetString(t.Name);
        if (t.ResolutionScope.Kind == HandleKind.TypeReference) return FullName((TypeReferenceHandle)t.ResolutionScope) + "/" + name;
        string ns = Reader.GetString(t.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public static bool IsAnyCpuIl(PEHeaders headers, out string description)
    {
        var cor = headers.CorHeader;
        if (cor == null) { description = "native"; return false; }
        bool ilOnly = (cor.Flags & CorFlags.ILOnly) != 0;
        bool req32 = (cor.Flags & CorFlags.Requires32Bit) != 0;
        var machine = headers.CoffHeader.Machine;
        if (!ilOnly) { description = "mixed-mode (C++/CLI)"; return false; }
        if (req32) { description = "x86-only"; return false; }
        if (machine != Machine.I386) { description = $"{machine}-only"; return false; }
        description = "AnyCPU";
        return true;
    }
}

/// <summary>Opens assemblies by simple name through a <see cref="LoadSet"/>, and resolves types across them.</summary>
internal sealed class Resolver : IDisposable
{
    private readonly LoadSet _set;
    private readonly Dictionary<string, AssemblyIndex?> _open = new(StringComparer.OrdinalIgnoreCase);

    public Resolver(LoadSet set) => _set = set;

    public LoadSet Set => _set;

    public AssemblyIndex? Open(string simpleName)
    {
        if (_open.TryGetValue(simpleName, out var idx)) return idx;
        idx = _set.ByName.TryGetValue(simpleName, out var e) ? AssemblyIndex.TryOpen(e.Path) : null;
        _open[simpleName] = idx;
        return idx;
    }

    public void Dispose()
    {
        foreach (var i in _open.Values) i?.Dispose();
    }

    /// <summary>A type, as (defining assembly, its index, its handle), or null with the reason.</summary>
    public readonly record struct Resolved(AssemblyIndex Assembly, TypeDefinitionHandle Handle)
    {
        public string Key => $"[{Assembly.Name}]{Assembly.FullName(Handle)}";
    }

    public Resolved? ResolveName(string assemblyName, string fullName, int depth = 0)
    {
        if (depth > 8) return null;
        var asm = Open(assemblyName);
        if (asm == null) return null;
        if (asm.Types.TryGetValue(fullName, out var h)) return new Resolved(asm, h);
        if (asm.Exported.TryGetValue(fullName, out var eh))
        {
            // Follow the forwarder; nested exported types forward with their outermost type.
            var et = asm.Reader.GetExportedType(eh);
            EntityHandle impl = et.Implementation;
            while (impl.Kind == HandleKind.ExportedType) impl = asm.Reader.GetExportedType((ExportedTypeHandle)impl).Implementation;
            if (impl.Kind == HandleKind.AssemblyReference)
            {
                string target = asm.Reader.GetString(asm.Reader.GetAssemblyReference((AssemblyReferenceHandle)impl).Name);
                return ResolveName(target, fullName, depth + 1);
            }
        }
        return null;
    }

    /// <summary>Resolves a TypeRef as seen from <paramref name="from"/>.</summary>
    public Resolved? Resolve(AssemblyIndex from, TypeReferenceHandle h)
    {
        var tr = from.Reader.GetTypeReference(h);
        string fullName = from.FullName(h);
        EntityHandle scope = tr.ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference) scope = from.Reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        switch (scope.Kind)
        {
            case HandleKind.AssemblyReference:
                return ResolveName(from.Reader.GetString(from.Reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name), fullName);
            case HandleKind.ModuleDefinition:
            case HandleKind.ModuleReference:
                return from.Types.TryGetValue(fullName, out var def) ? new Resolved(from, def) : null;
            default:
                return null;
        }
    }

    public string AssemblyRefName(AssemblyIndex from, TypeReferenceHandle h)
    {
        EntityHandle scope = from.Reader.GetTypeReference(h).ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference) scope = from.Reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        return scope.Kind == HandleKind.AssemblyReference
            ? from.Reader.GetString(from.Reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)
            : from.Name;
    }

    /// <summary>The base type of a resolved type, resolved in turn.</summary>
    public Resolved? BaseOf(Resolved t)
    {
        var def = t.Assembly.Reader.GetTypeDefinition(t.Handle);
        EntityHandle b = def.BaseType;
        if (b.IsNil) return null;                   // System.Object, interfaces
        return b.Kind switch
        {
            HandleKind.TypeDefinition => new Resolved(t.Assembly, (TypeDefinitionHandle)b),
            HandleKind.TypeReference => Resolve(t.Assembly, (TypeReferenceHandle)b),
            HandleKind.TypeSpecification => GenericDefinitionOf(t.Assembly, (TypeSpecificationHandle)b),
            _ => null,
        };
    }

    public Resolved? GenericDefinitionOf(AssemblyIndex from, TypeSpecificationHandle h)
    {
        var blob = from.Reader.GetBlobReader(from.Reader.GetTypeSpecification(h).Signature);
        var code = blob.ReadSignatureTypeCode();
        if (code != SignatureTypeCode.GenericTypeInstance) return null;
        blob.ReadSignatureTypeCode();                       // class / valuetype
        EntityHandle generic = blob.ReadTypeHandle();
        return generic.Kind switch
        {
            HandleKind.TypeDefinition => new Resolved(from, (TypeDefinitionHandle)generic),
            HandleKind.TypeReference => Resolve(from, (TypeReferenceHandle)generic),
            _ => null,
        };
    }
}

/// <summary>Writes signature types as "[DefiningAssembly]Full.Name", resolved through the load set.</summary>
internal sealed class SignatureNames : ISignatureTypeProvider<string, object?>
{
    private readonly Resolver _resolver;
    private readonly AssemblyIndex _context;

    public SignatureNames(Resolver resolver, AssemblyIndex context)
    {
        _resolver = resolver;
        _context = context;
    }

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode switch
    {
        PrimitiveTypeCode.IntPtr => "IntPtr",
        PrimitiveTypeCode.UIntPtr => "UIntPtr",
        PrimitiveTypeCode.TypedReference => "TypedReference",
        _ => typeCode.ToString(),
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        $"[{_context.Name}]{_context.FullName(handle)}";

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var r = _resolver.Resolve(_context, handle);
        // Unresolvable: keep the name the reference asked for, so the mismatch is visible in reports.
        return r?.Key ?? $"?[{_resolver.AssemblyRefName(_context, handle)}]{_context.FullName(handle)}";
    }

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPinnedType(string elementType) => elementType;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr(" + string.Join(",", signature.ParameterTypes) + ")" + signature.ReturnType;

    public static string Method(MethodSignature<string> sig) =>
        (sig.GenericParameterCount > 0 ? "`" + sig.GenericParameterCount : "") + "(" + string.Join(",", sig.ParameterTypes) + ")" + sig.ReturnType;
}
