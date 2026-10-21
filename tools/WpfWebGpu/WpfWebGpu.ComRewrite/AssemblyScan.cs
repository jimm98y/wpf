// What the rewriters need to know of an app assembly before judging it, from its metadata alone (no IL
// is decoded, nothing is generated): whether it declares COM types, whether it could make built-in RCWs
// of its own without declaring any, and whether it may ask other assemblies where they are. Read by the
// launcher when it prepares an app (ComImportStep) and by the build of a bundled head (BuildRewrite).

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace WpfWebGpu.ComRewrite;

public sealed class AssemblyScan
{
    public required string File { get; init; }
    public required string Name { get; init; }
    /// <summary>The file's content hash (24 hex digits).</summary>
    public required string Hash { get; init; }
    /// <summary>IL only: not a mixed-mode (C++/CLI) image, which Cecil cannot write.</summary>
    public bool ILOnly { get; init; }
    /// <summary>It declares [ComImport] types.</summary>
    public bool DeclaresCom { get; init; }
    /// <summary>It may read assembly locations (LocationCallers.MayRead).</summary>
    public bool MayReadLocations { get; init; }
    public IReadOnlyList<string> References { get; init; } = Array.Empty<string>();
    /// <summary>Ways it could make built-in RCWs of its own: Marshal's RCW makers, Type.GetTypeFromCLSID/ProgID.</summary>
    public bool RcwApis { get; private set; }
    /// <summary>It calls Marshal APIs that only work on built-in RCWs (ReleaseComObject, IsComObject, ...).</summary>
    public bool ComObjectApis { get; private set; }
    /// <summary>A P/Invoke of its passes a delegate of its own (a callback native code may call with interface pointers).</summary>
    public bool PInvokeDelegates { get; private set; }
    /// <summary>The assemblies whose types its P/Invokes' signatures name.</summary>
    public HashSet<string> PInvokeScopes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The scan of <paramref name="file"/>, or null when it is not an assembly.</summary>
    public static AssemblyScan? Read(string file)
    {
        try
        {
            byte[] bytes = System.IO.File.ReadAllBytes(file);
            using var pe = new PEReader(new MemoryStream(bytes));
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return null;
            bool declaresCom = false;
            foreach (var h in md.TypeDefinitions)
                if ((md.GetTypeDefinition(h).Attributes & System.Reflection.TypeAttributes.Import) != 0) { declaresCom = true; break; }
            var scan = new AssemblyScan
            {
                File = file,
                Name = md.GetString(md.GetAssemblyDefinition().Name),
                Hash = Convert.ToHexString(SHA256.HashData(bytes))[..24].ToLowerInvariant(),
                ILOnly = (pe.PEHeaders.CorHeader?.Flags & CorFlags.ILOnly) != 0,
                DeclaresCom = declaresCom,
                MayReadLocations = LocationCallers.MayRead(md),
                References = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList(),
            };
            scan.ScanRcwMakers(md);
            return scan;
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or InvalidOperationException) { return null; }
    }

    private static readonly HashSet<string> s_rcwMarshal = new(StringComparer.Ordinal)
    {
        "GetObjectForIUnknown", "GetTypedObjectForIUnknown", "GetUniqueObjectForIUnknown", "GetObjectForNativeVariant",
        "GetObjectsForNativeVariants", "CreateWrapperOfType", "BindToMoniker", "GetActiveObject", "GetDelegateForFunctionPointer",
        "GetFunctionPointerForDelegate",
    };

    private static readonly HashSet<string> s_comObjectMarshal = new(StringComparer.Ordinal)
    {
        "ReleaseComObject", "FinalReleaseComObject", "IsComObject", "GetIUnknownForObject", "GetComInterfaceForObject",
    };

    private void ScanRcwMakers(MetadataReader md)
    {
        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            if (mr.Parent.Kind != HandleKind.TypeReference) continue;
            var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
            string type = md.GetString(tr.Namespace) + "." + md.GetString(tr.Name), name = md.GetString(mr.Name);
            if ((type == "System.Runtime.InteropServices.Marshal" && s_rcwMarshal.Contains(name))
                || (type == "System.Type" && name is "GetTypeFromCLSID" or "GetTypeFromProgID"))
                RcwApis = true;
            else if (type == "System.Runtime.InteropServices.Marshal" && s_comObjectMarshal.Contains(name))
                ComObjectApis = true;
        }
        var provider = new ScopeCollector(md);
        foreach (var h in md.MethodDefinitions)
        {
            var m = md.GetMethodDefinition(h);
            if ((m.Attributes & System.Reflection.MethodAttributes.PinvokeImpl) == 0) continue;
            try { m.DecodeSignature(provider, null); }
            catch (BadImageFormatException) { PInvokeDelegates = true; }
        }
        PInvokeScopes.UnionWith(provider.Scopes);
        if (provider.Delegates) PInvokeDelegates = true;
    }

    /// <summary>A signature's types: the assemblies of those it references, and whether one is a delegate of
    /// the file's own.</summary>
    private sealed class ScopeCollector : ISignatureTypeProvider<int, object?>
    {
        public readonly HashSet<string> Scopes = new(StringComparer.OrdinalIgnoreCase);
        public bool Delegates;
        public ScopeCollector(MetadataReader md) { }
        public int GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k)
        {
            var scope = r.GetTypeReference(h).ResolutionScope;
            while (scope.Kind == HandleKind.TypeReference) scope = r.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
            if (scope.Kind == HandleKind.AssemblyReference) Scopes.Add(r.GetString(r.GetAssemblyReference((AssemblyReferenceHandle)scope).Name));
            return 0;
        }
        public int GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k)
        {
            var b = r.GetTypeDefinition(h).BaseType;
            if (b.Kind == HandleKind.TypeReference && r.GetString(r.GetTypeReference((TypeReferenceHandle)b).Name) is "MulticastDelegate" or "Delegate") Delegates = true;
            return 0;
        }
        public int GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) => r.GetTypeSpecification(h).DecodeSignature(this, c);
        public int GetPrimitiveType(PrimitiveTypeCode t) => 0;
        public int GetSZArrayType(int e) => 0;
        public int GetArrayType(int e, ArrayShape s) => 0;
        public int GetByReferenceType(int e) => 0;
        public int GetPointerType(int e) => 0;
        public int GetPinnedType(int e) => 0;
        public int GetGenericInstantiation(int g, System.Collections.Immutable.ImmutableArray<int> a) => 0;
        public int GetGenericMethodParameter(object? c, int i) => 0;
        public int GetGenericTypeParameter(object? c, int i) => 0;
        public int GetFunctionPointerType(MethodSignature<int> s) => 0;
        public int GetModifiedType(int m, int u, bool r) => 0;
    }
}
