// Assemblies an app references and does not ship, for the heads that bundle an app (wpf-rewrite, at
// publish: what is referenced by the assemblies published and is neither one of them nor the runtime's
// nor the stack's). A Windows install leaves out what the app
// never needs (app's configurator holds a field of a type in Ico.Microsoft.JScript, an assembly it
// does not install), and CoreCLR does not mind: a field of a class type needs nothing of that class until
// it is used, so the app runs as on Windows, and fails where the missing assembly is actually reached.
// Mono, the browser's runtime, loads a field's type with the class that holds it, and the class itself
// could not be loaded: GwxConfigControl's constructor threw, and the app never started.
//
// So a bundle gets, for each such assembly, one that holds the types the app names in it: each as what
// the app's own metadata says it is -- a class, a struct where a signature says value type, an interface
// where a type implements it -- with its generic parameters, and the methods and fields the app calls,
// each method throwing what CoreCLR throws on reaching code that needs the assembly: FileNotFoundException,
// "Could not load file or assembly '...'". An app written for Windows catches that where it means to
// carry on without the assembly; a MissingMethodException (a type without its members) it does not
// expect, and app's configurator stopped at `new JScriptCodeProvider()` where CoreCLR's exception
// let it go on.

using Mono.Cecil;

namespace WpfWebGpu.ComRewrite;

public static class Placeholders
{
    private sealed class Kind
    {
        public bool ValueType, Interface, Enum;
        public int Generic;
        public string? DeclaringType;
    }

    /// <summary>A placeholder for each of <paramref name="missing"/> (assembly names), from the types
    /// <paramref name="assemblies"/> name in it, written into <paramref name="dir"/>; their paths.</summary>
    public static List<string> Write(IReadOnlyCollection<string> missing, IEnumerable<string> assemblies, string dir)
    {
        var wanted = new HashSet<string>(missing, StringComparer.OrdinalIgnoreCase);
        var types = new Dictionary<string, Dictionary<string, Kind>>(StringComparer.OrdinalIgnoreCase);
        var versions = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        var fullNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // the members the app uses, per missing assembly; and which of its fields are static (ldsfld)
        var members = new Dictionary<string, List<MemberReference>>(StringComparer.OrdinalIgnoreCase);
        var staticFields = new HashSet<string>(StringComparer.Ordinal);

        Kind? Note(TypeReference? t)
        {
            while (t is TypeSpecification spec) t = spec.ElementType;
            if (t == null || t is GenericParameter) return null;
            TypeReference outer = t;
            while (outer.DeclaringType != null) outer = outer.DeclaringType;
            if (outer.Scope is not AssemblyNameReference scope || !wanted.Contains(scope.Name)) return null;
            if (!versions.ContainsKey(scope.Name)) versions[scope.Name] = scope.Version;
            if (!fullNames.ContainsKey(scope.Name)) fullNames[scope.Name] = scope.FullName;
            if (!types.TryGetValue(scope.Name, out var byName)) types[scope.Name] = byName = new Dictionary<string, Kind>(StringComparer.Ordinal);
            if (!byName.TryGetValue(t.FullName, out Kind? kind))
            {
                byName[t.FullName] = kind = new Kind { DeclaringType = t.DeclaringType?.FullName };
                if (t.DeclaringType != null) Note(t.DeclaringType);
            }
            kind.ValueType |= t.IsValueType;
            kind.Generic = Math.Max(kind.Generic, t.GenericParameters.Count);
            return kind;
        }

        void NoteSig(TypeReference? t)
        {
            if (t is GenericInstanceType gi)
            {
                foreach (TypeReference a in gi.GenericArguments) NoteSig(a);
                if (Note(gi.ElementType) is { } k) k.Generic = Math.Max(k.Generic, gi.GenericArguments.Count);
                return;
            }
            if (t is TypeSpecification spec) { NoteSig(spec.ElementType); return; }
            Note(t);
        }

        foreach (string file in assemblies)
        {
            using var module = ModuleDefinition.ReadModule(file, new ReaderParameters { ReadingMode = ReadingMode.Deferred });
            if (!module.AssemblyReferences.Any(r => wanted.Contains(r.Name))) continue;
            foreach (TypeReference r in module.GetTypeReferences()) Note(r);
            foreach (TypeDefinition type in module.GetTypes())
            {
                NoteSig(type.BaseType);
                foreach (InterfaceImplementation i in type.Interfaces)
                    if (Note(i.InterfaceType is GenericInstanceType g ? g.ElementType : i.InterfaceType) is { } k) k.Interface = true;
                foreach (FieldDefinition f in type.Fields) NoteSig(f.FieldType);
                foreach (PropertyDefinition p in type.Properties) NoteSig(p.PropertyType);
                foreach (MethodDefinition m in type.Methods)
                {
                    NoteSig(m.ReturnType);
                    foreach (ParameterDefinition p in m.Parameters) NoteSig(p.ParameterType);
                    if (m.HasBody)
                    {
                        foreach (var v in m.Body.Variables) NoteSig(v.VariableType);
                        foreach (var i in m.Body.Instructions)
                            if (i.Operand is FieldReference f && (i.OpCode == Mono.Cecil.Cil.OpCodes.Ldsfld || i.OpCode == Mono.Cecil.Cil.OpCodes.Stsfld || i.OpCode == Mono.Cecil.Cil.OpCodes.Ldsflda))
                                staticFields.Add(f.FullName);
                    }
                }
            }
            foreach (MemberReference r in module.GetMemberReferences())
            {
                NoteSig(r.DeclaringType);
                if (ScopeOf(r.DeclaringType) is { } owner && wanted.Contains(owner))
                {
                    if (!members.TryGetValue(owner, out var list)) members[owner] = list = new List<MemberReference>();
                    list.Add(r);
                }
                if (r is MethodReference mr)
                {
                    NoteSig(mr.ReturnType);
                    foreach (ParameterDefinition p in mr.Parameters)
                    {
                        NoteSig(p.ParameterType);
                        // a value type a constructor takes: an attribute's argument, which only an enum
                        // can be -- and which a custom attribute's blob cannot be read without
                        if (mr.Name == ".ctor" && p.ParameterType.IsValueType && Note(p.ParameterType) is { } e) e.Enum = true;
                    }
                }
                else if (r is FieldReference fr) NoteSig(fr.FieldType);
            }
        }

        Directory.CreateDirectory(dir);
        var written = new List<string>();
        foreach (string name in missing)
        {
            var version = versions.TryGetValue(name, out Version? v) ? v : new Version(0, 0, 0, 0);
            var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, version), name + ".dll", ModuleKind.Dll);
            ModuleDefinition module = assembly.MainModule;
            TypeReference obj = module.TypeSystem.Object;
            TypeReference valueType = module.ImportReference(typeof(ValueType));
            var made = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
            var kinds = types.TryGetValue(name, out var k) ? k : new Dictionary<string, Kind>();

            TypeDefinition Make(string fullName, Kind kind)
            {
                if (made.TryGetValue(fullName, out TypeDefinition? done)) return done;
                string simple = fullName;
                string ns = "";
                TypeDefinition? outer = null;
                if (kind.DeclaringType != null && kinds.TryGetValue(kind.DeclaringType, out Kind? outerKind))
                {
                    outer = Make(kind.DeclaringType, outerKind);
                    simple = fullName[(kind.DeclaringType.Length + 1)..];
                }
                else if (fullName.LastIndexOf('.') is int dot and > 0)
                {
                    ns = fullName[..dot];
                    simple = fullName[(dot + 1)..];
                }
                TypeAttributes attrs = outer != null ? TypeAttributes.NestedPublic : TypeAttributes.Public;
                TypeDefinition def;
                if (kind.Interface)
                    def = new TypeDefinition(ns, simple, attrs | TypeAttributes.Interface | TypeAttributes.Abstract);
                else if (kind.Enum)
                {
                    def = new TypeDefinition(ns, simple, attrs | TypeAttributes.Sealed, module.ImportReference(typeof(System.Enum)));
                    def.Fields.Add(new FieldDefinition("value__", FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName, module.TypeSystem.Int32));
                }
                else if (kind.ValueType)
                    def = new TypeDefinition(ns, simple, attrs | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, valueType);
                else
                    def = new TypeDefinition(ns, simple, attrs | TypeAttributes.BeforeFieldInit, obj);
                for (int i = 0; i < kind.Generic; i++) def.GenericParameters.Add(new GenericParameter("T" + i, def));
                if (outer != null) outer.NestedTypes.Add(def); else module.Types.Add(def);
                made[fullName] = def;
                return def;
            }

            foreach (var (fullName, kind) in kinds) Make(fullName, kind);
            if (members.TryGetValue(name, out var used))
                AddMembers(module, made, used, staticFields, fullNames.TryGetValue(name, out string? fn) ? fn : name);
            string path = Path.Combine(dir, name + ".dll");
            assembly.Write(path);
            written.Add(path);
        }
        return written;
    }

    /// <summary>The managed assemblies <paramref name="assemblies"/> carry as embedded resources, by their
    /// assembly name: the bytes, and which assembly's resource they are.</summary>
    public static Dictionary<string, (byte[] Bytes, string Source)> EmbeddedAssemblies(IEnumerable<string> assemblies)
    {
        var found = new Dictionary<string, (byte[], string)>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in assemblies)
        {
            try
            {
                using var module = ModuleDefinition.ReadModule(file, new ReaderParameters { ReadingMode = ReadingMode.Deferred });
                foreach (Resource r in module.Resources)
                {
                    if (r is not EmbeddedResource er || !r.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    byte[] bytes = er.GetResourceData();
                    try
                    {
                        using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(bytes));
                        if (!pe.HasMetadata) continue;
                        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                        if (!md.IsAssembly) continue;
                        string name = md.GetString(md.GetAssemblyDefinition().Name);
                        found.TryAdd(name, (bytes, $"{module.Assembly.Name.Name} ({r.Name})"));
                    }
                    catch (BadImageFormatException) { }
                }
            }
            catch (Exception e) when (e is BadImageFormatException or IOException) { }
        }
        return found;
    }

    /// <summary>For each of <paramref name="missing"/>, an embedded assembly (<see cref="EmbeddedAssemblies"/>)
    /// that is it in all but name: one that defines every type <paramref name="assemblies"/> name in it. An
    /// app that carries an assembly as a resource may load it under another name than its own (app
    /// references Ico.Microsoft.JScript, and its resolver answers with the Microsoft.JScript it embeds):
    /// the bytes, renamed to the name asked for, so the runtime binds to it without the app's resolver --
    /// which Mono, loading a field's type with its class, would not have been asked for in time.</summary>
    public static Dictionary<string, (byte[] Bytes, string Source)> MatchEmbedded(IReadOnlyCollection<string> missing, IEnumerable<string> assemblies,
        Dictionary<string, (byte[] Bytes, string Source)> embedded, IEnumerable<string> searchDirs)
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (string d in searchDirs.Distinct(StringComparer.OrdinalIgnoreCase)) if (Directory.Exists(d)) resolver.AddSearchDirectory(d);
        var result = new Dictionary<string, (byte[], string)>(StringComparer.OrdinalIgnoreCase);
        if (missing.Count == 0 || embedded.Count == 0) return result;
        var wanted = new HashSet<string>(missing, StringComparer.OrdinalIgnoreCase);
        var named = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in assemblies)
        {
            try
            {
                using var module = ModuleDefinition.ReadModule(file, new ReaderParameters { ReadingMode = ReadingMode.Deferred });
                foreach (TypeReference t in module.GetTypeReferences())
                {
                    if (t.DeclaringType != null || ScopeOf(t) is not { } scope || !wanted.Contains(scope)) continue;
                    if (!named.TryGetValue(scope, out var set)) named[scope] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(t.FullName);
                }
            }
            catch (Exception e) when (e is BadImageFormatException or IOException) { }
        }
        foreach (var (name, types) in named)
        {
            if (embedded.TryGetValue(name, out var same)) { result[name] = same; continue; }
            foreach (var (embeddedName, candidate) in embedded)
            {
                try
                {
                    using var module = ModuleDefinition.ReadModule(new MemoryStream(candidate.Bytes), new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
                    var defined = new HashSet<string>(module.Types.Where(t => t.IsPublic).Select(t => t.FullName), StringComparer.Ordinal);
                    if (!types.All(defined.Contains)) continue;
                    module.Assembly.Name.Name = name;
                    module.Name = name + ".dll";
                    SupplyFrameworkOnlyTypes(module);
                    module.Attributes &= ~ModuleAttributes.StrongNameSigned;
                    var ms = new MemoryStream();
                    module.Write(ms);
                    result[name] = (ms.ToArray(), $"{candidate.Source}, {embeddedName} renamed");
                    break;
                }
                catch (Exception e) when (e is BadImageFormatException or IOException or InvalidOperationException or AssemblyResolutionException)
                {
                    Console.WriteLine($"wpf-rewrite: {embeddedName} ({candidate.Source}) could not stand in for {name}: {e.GetType().Name}: {e.Message}");
                }
            }
        }
        return result;
    }

    /// <summary>
    /// The types an assembly built for .NET Framework names in its framework that .NET does not have
    /// (JScript's GlobalScope implements mscorlib's System.Runtime.InteropServices.Expando.IExpando), made
    /// in the assembly itself: each reference pointed at the assembly, the type defined there with the
    /// members the assembly uses of it -- an interface's methods as the interface's, anything else's
    /// throwing the TypeLoadException CoreCLR throws on reaching the missing type. CoreCLR loads a type
    /// only when it is used, and an app that never uses that part of the assembly never meets the gap;
    /// Mono loads a field's type with its class, and the class that held one could not be loaded at all.
    /// </summary>
    private static void SupplyFrameworkOnlyTypes(ModuleDefinition module)
    {
        var missing = new List<TypeReference>();
        foreach (TypeReference tr in module.GetTypeReferences())
        {
            if (tr.DeclaringType != null || tr.Scope is not AssemblyNameReference) continue;
            TypeDefinition? resolved = null;
            try { resolved = tr.Resolve(); } catch (AssemblyResolutionException) { continue; }   // an assembly not there at all: a placeholder's business
            if (resolved == null) missing.Add(tr);
        }
        if (missing.Count == 0) return;
        var interfaces = new HashSet<string>(module.GetTypes().SelectMany(t => t.Interfaces).Select(i => i.InterfaceType.FullName), StringComparer.Ordinal);
        MethodReference loadFailure = module.ImportReference(typeof(TypeLoadException).GetConstructor(new[] { typeof(string) })!);
        var byName = new Dictionary<string, (TypeDefinition Def, string From)>(StringComparer.Ordinal);
        foreach (TypeReference tr in missing)
        {
            bool isInterface = interfaces.Contains(tr.FullName);
            var attrs = TypeAttributes.Public | (isInterface ? TypeAttributes.Interface | TypeAttributes.Abstract : TypeAttributes.BeforeFieldInit);
            var def = new TypeDefinition(tr.Namespace, tr.Name, attrs, isInterface ? null : tr.IsValueType ? module.ImportReference(typeof(ValueType)) : module.TypeSystem.Object);
            module.Types.Add(def);
            byName[tr.FullName] = (def, (tr.Scope as AssemblyNameReference)?.FullName ?? "");
        }
        foreach (MemberReference r in module.GetMemberReferences().ToList())
        {
            if (r is not MethodReference mr || mr is GenericInstanceMethod || !byName.TryGetValue(mr.DeclaringType.FullName, out var target)) continue;
            TypeDefinition def = target.Def;
            if (def.Methods.Any(m => m.Name == mr.Name && m.Parameters.Count == mr.Parameters.Count)) continue;
            bool ctor = mr.Name == ".ctor";
            var attrs = MethodAttributes.Public | MethodAttributes.HideBySig | (mr.HasThis ? 0 : MethodAttributes.Static);
            if (def.IsInterface) attrs |= MethodAttributes.Virtual | MethodAttributes.Abstract | MethodAttributes.NewSlot;
            else if (ctor) attrs |= MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
            var m = new MethodDefinition(mr.Name, attrs, mr.ReturnType);
            foreach (ParameterDefinition p in mr.Parameters) m.Parameters.Add(new ParameterDefinition(p.Name, ParameterAttributes.None, p.ParameterType));
            if (!def.IsInterface)
            {
                var il = m.Body.GetILProcessor();
                il.Emit(Mono.Cecil.Cil.OpCodes.Ldstr, $"Could not load type '{def.FullName}' from assembly '{target.From}'.");
                il.Emit(Mono.Cecil.Cil.OpCodes.Newobj, loadFailure);
                il.Emit(Mono.Cecil.Cil.OpCodes.Throw);
            }
            def.Methods.Add(m);
        }
        // the references now name the assembly itself, where the types are
        foreach (TypeReference tr in missing) tr.Scope = module;
    }

    private static string? ScopeOf(TypeReference? t)
    {
        while (t is TypeSpecification spec) t = spec.ElementType;
        while (t?.DeclaringType != null) t = t.DeclaringType;
        return (t?.Scope as AssemblyNameReference)?.Name;
    }

    /// <summary>The methods and fields the app uses on the placeholder's types: each method's body throws
    /// CoreCLR's FileNotFoundException for the assembly (<paramref name="assemblyFullName"/>).</summary>
    private static void AddMembers(ModuleDefinition module, Dictionary<string, TypeDefinition> made, List<MemberReference> used,
        HashSet<string> staticFields, string assemblyFullName)
    {
        MethodReference notFound = module.ImportReference(typeof(FileNotFoundException).GetConstructor(new[] { typeof(string), typeof(string) })!);
        string message = $"Could not load file or assembly '{assemblyFullName}'. The system cannot find the file specified.";
        var seen = new HashSet<string>(StringComparer.Ordinal);

        TypeDefinition? Owner(TypeReference t)
        {
            while (t is TypeSpecification spec) t = spec.ElementType;
            return made.TryGetValue(t.FullName, out TypeDefinition? d) ? d : null;
        }

        // a signature's type, in the placeholder: its own types are the definitions made, generic
        // parameters the new member's or type's own, anything else an import
        TypeReference Map(TypeReference t, TypeDefinition type, MethodDefinition? method)
        {
            switch (t)
            {
                case GenericParameter gp:
                    if (gp.Type == GenericParameterType.Method && method != null && gp.Position < method.GenericParameters.Count) return method.GenericParameters[gp.Position];
                    if (gp.Type == GenericParameterType.Type && gp.Position < type.GenericParameters.Count) return type.GenericParameters[gp.Position];
                    return module.TypeSystem.Object;
                case ByReferenceType br: return new ByReferenceType(Map(br.ElementType, type, method));
                case ArrayType at: return new ArrayType(Map(at.ElementType, type, method), at.Rank);
                case PointerType pt: return new PointerType(Map(pt.ElementType, type, method));
                case GenericInstanceType gi:
                {
                    var inst = new GenericInstanceType(Map(gi.ElementType, type, method));
                    foreach (TypeReference a in gi.GenericArguments) inst.GenericArguments.Add(Map(a, type, method));
                    return inst;
                }
                case TypeSpecification: return module.TypeSystem.Object;     // modifiers and the like: not worth the fidelity
            }
            if (made.TryGetValue(t.FullName, out TypeDefinition? own)) return own;
            return module.ImportReference(t);
        }

        foreach (MemberReference r in used)
        {
            TypeDefinition? type = Owner(r.DeclaringType);
            if (type == null) continue;
            try
            {
                if (r is MethodReference mr)
                {
                    MethodReference e = mr is GenericInstanceMethod g ? g.ElementMethod : mr;
                    if (!seen.Add(type.FullName + "::" + e.FullName)) continue;
                    bool ctor = e.Name is ".ctor" or ".cctor";
                    var attrs = MethodAttributes.Public | MethodAttributes.HideBySig;
                    if (!e.HasThis) attrs |= MethodAttributes.Static;
                    if (ctor) attrs |= MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
                    else if (type.IsInterface) attrs |= MethodAttributes.Virtual | MethodAttributes.Abstract | MethodAttributes.NewSlot;
                    else if (e.HasThis) attrs |= MethodAttributes.Virtual;
                    var def = new MethodDefinition(e.Name, attrs, module.TypeSystem.Void);
                    for (int i = 0; i < e.GenericParameters.Count; i++) def.GenericParameters.Add(new GenericParameter("M" + i, def));
                    type.Methods.Add(def);
                    def.ReturnType = Map(e.ReturnType, type, def);
                    foreach (ParameterDefinition p in e.Parameters)
                        def.Parameters.Add(new ParameterDefinition(p.Name, ParameterAttributes.None, Map(p.ParameterType, type, def)));
                    if (!def.IsAbstract)
                    {
                        var il = def.Body.GetILProcessor();
                        il.Emit(Mono.Cecil.Cil.OpCodes.Ldstr, message);
                        il.Emit(Mono.Cecil.Cil.OpCodes.Ldstr, assemblyFullName);
                        il.Emit(Mono.Cecil.Cil.OpCodes.Newobj, notFound);
                        il.Emit(Mono.Cecil.Cil.OpCodes.Throw);
                    }
                }
                else if (r is FieldReference fr)
                {
                    if (!seen.Add(type.FullName + "::" + fr.Name) || type.IsEnum) continue;
                    var attrs = FieldAttributes.Public | (staticFields.Contains(fr.FullName) ? FieldAttributes.Static : 0);
                    type.Fields.Add(new FieldDefinition(fr.Name, attrs, Map(fr.FieldType, type, null)));
                }
            }
            catch (Exception)
            {
                // a member whose signature cannot be carried over is left out: using it is a missing member
            }
        }
    }
}
