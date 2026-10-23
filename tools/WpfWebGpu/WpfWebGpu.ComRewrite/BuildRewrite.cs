// The rewrite at build time, for the heads that bundle the app and compile it ahead of time -- the
// browser, iOS, Android (Documentation/pinvoke-rewriter.md). The same rewrites the launcher's step and
// the loader make when an app is started (ComImportStep, ComImportLoader), over the app's assemblies
// before they are trimmed and compiled, chosen as off Windows:
//
// - an assembly that declares COM types is rewritten for them, with the assemblies that use its COM
//   types and can make RCWs of their own (judged as members of its group);
// - an assembly that P/Invokes a Windows DLL is rewritten for those P/Invokes.
//
// - an assembly that may ask where an assembly is (Assembly.Location, CodeBase) is rewritten for those
//   calls: a bundle's assemblies have no file, and the answer is their place in the app's folder
//   (WpfWebGpu.ComInterop's Com.OriginalPath), as the launcher answers for the assemblies it rewrites.
//
// The entry assembly is not left out: nothing has loaded it before the rewrite.

using Mono.Cecil;

namespace WpfWebGpu.ComRewrite;

public static class BuildRewrite
{
    /// <summary>One assembly rewritten: its original, the rewritten copy, and what was rewritten.</summary>
    public sealed record Rewritten(string Input, string Output, string Summary);

    /// <summary>
    /// Rewrites what of <paramref name="inputs"/> needs it into <paramref name="outDir"/> (same file names,
    /// with their .pdb when the original has one). <paramref name="searchDirs"/>: where references and the
    /// implementation assemblies (Win32Catalog.ImplementationAssemblies) are found. Assemblies of the stack
    /// itself (<paramref name="isStack"/>) are never rewritten.
    /// </summary>
    public static List<Rewritten> Run(IEnumerable<string> inputs, IEnumerable<string> searchDirs, string outDir, Func<string, bool> isStack, Action<string> log)
    {
        var dirs = searchDirs.ToList();
        var files = new List<AssemblyScan>();
        foreach (string input in inputs.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (AssemblyScan.Read(input) is not { } scan || isStack(scan.Name)) continue;
            if (files.Any(f => string.Equals(f.Name, scan.Name, StringComparison.OrdinalIgnoreCase))) continue;   // the same assembly twice: the first
            files.Add(scan);
        }

        Win32Catalog catalog = Win32Catalog.For(dirs);
        foreach (string problem in catalog.Problems) log($"pinvoke: {problem}");
        var pinvokes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if (!ComRewriter.ImportsWindows(f.File, catalog, windows: false)) continue;
            if (!f.ILOnly) { log($"pinvoke {f.Name}: mixed-mode (not IL-only): its Windows P/Invokes are not rewritten"); continue; }
            pinvokes.Add(f.Name);
        }

        // COM: what declares COM types, and those of its users that can make RCWs of their own (the
        // launcher's group members, ComImportStep), which go with it when they use its COM types
        var comNames = files.Where(f => f.DeclaresCom).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var com = new HashSet<string>(comNames, StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if (f.DeclaresCom || !f.References.Any(comNames.Contains)) continue;
            if (!(f.RcwApis || f.ComObjectApis || f.PInvokeDelegates || f.PInvokeScopes.Overlaps(comNames))) continue;
            try
            {
                var verdict = ComRewriter.Analyze(f.File, dirs, null, force: true);
                if (verdict.UsesComTypesOf.Count > 0) com.Add(f.Name);
            }
            catch (Exception e) { log($"comimport {f.Name}: could not be judged ({e.GetType().Name}: {e.Message}); left"); }
        }

        // members the browser build refuses, for settings the browser takes care of (BrowserSubstitutions)
        BrowserSubstitutions? subs = BrowserSubstitutions.For(dirs);

        Directory.CreateDirectory(outDir);
        var done = new List<Rewritten>();
        foreach (var f in files)
        {
            bool c = com.Contains(f.Name), p = pinvokes.Contains(f.Name);
            // every location call, its own assembly's too: no assembly of a bundle has a file
            bool l = !c && f.MayReadLocations;
            bool b = false;
            if (subs != null)
                using (var probe = ModuleDefinition.ReadModule(f.File, new ReaderParameters { ReadingMode = ReadingMode.Deferred }))
                    b = subs.Uses(probe);
            if (!c && !p && !l && !b) continue;
            if (!f.ILOnly) { log($"comimport {f.Name}: mixed-mode (not IL-only): not rewritten"); continue; }
            string output = Path.Combine(outDir, Path.GetFileName(f.File));
            string summary = "";
            if (c || p || l)
            {
                var (image, symbols, result) = ComRewriter.RewriteToMemory(f.File, dirs, locationOnly: l, com: c, pinvokes: p, windows: false, catalog: catalog);
                using (image)
                using (symbols)
                {
                    Write(output, image);
                    if (symbols != null) Write(Path.ChangeExtension(output, ".pdb"), symbols);
                }
                summary = (c ? result.ToString() : l ? $"its location calls ({result.CallSites})" : "") + (p ? (c || l ? "; " : "") + result.Win32Summary : "");
            }
            if (b)
            {
                // over what was just written, or the original (without symbols: the image is not the one they describe)
                string from = File.Exists(output) ? output : f.File;
                var resolver = new DefaultAssemblyResolver();
                foreach (string d in dirs) if (Directory.Exists(d)) resolver.AddSearchDirectory(d);
                using var m = ModuleDefinition.ReadModule(from, new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
                int n = subs!.Apply(m);
                m.Attributes &= ~ModuleAttributes.StrongNameSigned;
                var ms = new MemoryStream();
                m.Write(ms);
                ms.Position = 0;
                Write(output, ms);
                summary += (summary.Length > 0 ? "; " : "") + $"{n} calls of members the browser refuses";
            }
            done.Add(new Rewritten(f.File, output, summary));
            log($"rewriter: {f.Name}: {summary}");
        }
        return done;
    }

    private static void Write(string path, MemoryStream s)
    {
        string temp = path + "." + Environment.ProcessId + ".tmp";
        using (var f = File.Create(temp)) s.WriteTo(f);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// System.Drawing.Primitives' system colours, asked of user32 as on Windows. dotnet/runtime builds the
/// assembly once per OS: on Windows the system colours' entries of KnownColorTable.ColorValueTable hold
/// COLOR_* indices, and GetSystemColorArgb asks GetSysColor for one each time it is called (so a colour
/// follows the system's); everywhere else they hold a palette compiled in, Windows XP's (a beige #ECE9D8
/// Control), and GetSystemColorArgb reads it. The launcher's heads detour that method onto the stack's
/// user32 (WpfWebGpu.Detours, SystemColors). The browser has no detours, so its copy is made the Windows
/// build: the indices in the table, and the method's body Windows' -- the dark set first
/// (Application.SetColorMode), then GetSysColor through a user32 P/Invoke, which the rewrite then routes
/// to the stack's user32 as it routes an app's (BuildRewrite.Run over this copy).
/// </summary>
public static class SystemColorsPatch
{
    // KnownColor -> COLOR_*: ActiveBorder (1) .. WindowText (26), ButtonFace (168) .. MenuHighlight (174)
    private static readonly int[] s_first = { 10, 2, 9, 12, 15, 16, 21, 22, 20, 18, 1, 17, 13, 14, 26, 11, 3, 19, 24, 23, 4, 7, 0, 5, 6, 8 };
    private static readonly int[] s_later = { 15, 20, 16, 27, 28, 30, 29 };

    /// <summary>Writes the patched copy of <paramref name="primitives"/> into <paramref name="outDir"/> and
    /// returns its path; null (nothing written) when the assembly is not the shape this knows.</summary>
    public static string? Write(string primitives, string outDir, Action<string> log)
    {
        using var module = ModuleDefinition.ReadModule(primitives, new ReaderParameters { InMemory = true });
        TypeDefinition? table = module.GetType("System.Drawing.KnownColorTable");
        MethodDefinition? values = table?.Methods.FirstOrDefault(m => m.Name == "get_ColorValueTable" && m.HasBody);
        MethodDefinition? system = table?.Methods.FirstOrDefault(m => m.Name == "GetSystemColorArgb" && m.HasBody && m.Parameters.Count == 1);
        MethodDefinition? alternate = table?.Methods.FirstOrDefault(m => m.Name == "GetAlternateSystemColorArgb" && m.Parameters.Count == 1);
        FieldDefinition? useAlternate = module.GetType("System.Drawing.SystemColors")?.Fields.FirstOrDefault(f => f.Name == "s_useAlternativeColorSet" && f.IsStatic);
        var data = values?.Body.Instructions.FirstOrDefault(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Ldtoken)?.Operand as FieldDefinition;
        // 176 entries: KnownColor.Empty .. RebeccaPurple
        if (table == null || values == null || system == null || alternate == null || useAlternate == null || data?.InitialValue is not { Length: 176 * 4 } bytes)
        {
            log("system colours: System.Drawing.Primitives is not the shape known here; its colours are left as they are");
            return null;
        }

        // the table as Windows' build has it: a system colour's entry is its COLOR_* index
        for (int i = 0; i < s_first.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan((1 + i) * 4, 4), (uint)s_first[i]);
        for (int i = 0; i < s_later.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan((168 + i) * 4, 4), (uint)s_later[i]);
        data.InitialValue = bytes;

        // [DllImport("user32.dll")] static extern uint GetSysColor(int nIndex), as Interop.User32's
        var user32 = new ModuleReference("user32.dll");
        module.ModuleReferences.Add(user32);
        var getSysColor = new MethodDefinition("GetSysColor",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.PInvokeImpl, module.TypeSystem.UInt32)
        {
            PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvWinapi | PInvokeAttributes.NoMangle, "GetSysColor", user32),
            IsPreserveSig = true,
        };
        getSysColor.Parameters.Add(new ParameterDefinition("nIndex", ParameterAttributes.None, module.TypeSystem.Int32));
        table.Methods.Add(getSysColor);

        // GetSystemColorArgb(color):
        //   if (SystemColors.s_useAlternativeColorSet) return GetAlternateSystemColorArgb(color);
        //   uint bgr = GetSysColor((int)ColorValueTable[(int)color]);     // COLORREF 0x00BBGGRR
        //   return 0xFF000000 | (bgr & 0xFF) << 16 | (bgr & 0xFF00) | (bgr >> 16) & 0xFF;
        var oldGetItem = system.Body.Instructions.First(i => i.Operand is MethodReference r && r.Name == "get_Item").Operand;
        var body = system.Body;
        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        var span = new Mono.Cecil.Cil.VariableDefinition(values.ReturnType);
        body.Variables.Add(span);
        body.InitLocals = true;
        var il = body.GetILProcessor();
        var notAlternate = il.Create(Mono.Cecil.Cil.OpCodes.Nop);
        il.Emit(Mono.Cecil.Cil.OpCodes.Ldsfld, useAlternate);
        il.Emit(Mono.Cecil.Cil.OpCodes.Brfalse_S, notAlternate);
        il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
        il.Emit(Mono.Cecil.Cil.OpCodes.Call, alternate);
        il.Emit(Mono.Cecil.Cil.OpCodes.Ret);
        il.Append(notAlternate);
        il.Emit(Mono.Cecil.Cil.OpCodes.Call, values);
        il.Emit(Mono.Cecil.Cil.OpCodes.Stloc, span);
        il.Emit(Mono.Cecil.Cil.OpCodes.Ldloca, span);
        il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
        il.Emit(Mono.Cecil.Cil.OpCodes.Call, (MethodReference)oldGetItem);
        il.Emit(Mono.Cecil.Cil.OpCodes.Ldind_U4);
        il.Emit(Mono.Cecil.Cil.OpCodes.Call, getSysColor);
        var bgr = new Mono.Cecil.Cil.VariableDefinition(module.TypeSystem.UInt32);
        body.Variables.Add(bgr);
        il.Emit(Mono.Cecil.Cil.OpCodes.Stloc, bgr);
        il.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4, unchecked((int)0xFF000000));
        il.Emit(Mono.Cecil.Cil.OpCodes.Ldloc, bgr); il.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4, 0xFF); il.Emit(Mono.Cecil.Cil.OpCodes.And);
        il.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_S, (sbyte)16); il.Emit(Mono.Cecil.Cil.OpCodes.Shl); il.Emit(Mono.Cecil.Cil.OpCodes.Or);
        il.Emit(Mono.Cecil.Cil.OpCodes.Ldloc, bgr); il.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4, 0xFF00); il.Emit(Mono.Cecil.Cil.OpCodes.And); il.Emit(Mono.Cecil.Cil.OpCodes.Or);
        il.Emit(Mono.Cecil.Cil.OpCodes.Ldloc, bgr); il.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_S, (sbyte)16); il.Emit(Mono.Cecil.Cil.OpCodes.Shr_Un);
        il.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4, 0xFF); il.Emit(Mono.Cecil.Cil.OpCodes.And); il.Emit(Mono.Cecil.Cil.OpCodes.Or);
        il.Emit(Mono.Cecil.Cil.OpCodes.Ret);

        module.Attributes &= ~ModuleAttributes.StrongNameSigned;
        Directory.CreateDirectory(outDir);
        string output = Path.Combine(outDir, Path.GetFileName(primitives));
        module.Write(output);
        log("system colours: System.Drawing.Primitives asks user32's GetSysColor for them, as its Windows build does");
        return output;
    }
}

/// <summary>
/// The multithreaded browser runtime's System.Private.CoreLib, made to tell its threads apart. Its
/// System.Threading.Lock knows a thread by the OS thread id (Interop.Sys.TryGetUInt32OSThreadId), and on
/// that runtime every thread -- the page's, each worker, the pool's -- reports 1. A thread finding a lock
/// held by "its own" id enters it as a re-entry, so no Lock excludes another thread: the timer queue's
/// shared lock was released by a thread that did not hold it, and the runtime aborted within seconds of
/// timers running (dotnet/runtime#129900). With no OS id (-1) Lock takes the managed thread id, which is
/// unique; that is what the patch gives it, in the one method that asks.
/// </summary>
public static class CoreLibPatch
{
    /// <summary>Writes the patched copy of <paramref name="corelib"/> into <paramref name="outDir"/>; false (and
    /// nothing written) when it has nothing to patch.</summary>
    public static bool Write(string corelib, string outDir, Action<string> log)
    {
        using var module = Mono.Cecil.ModuleDefinition.ReadModule(corelib, new Mono.Cecil.ReaderParameters { InMemory = true });
        var method = module.GetType("System.Threading.Lock")?.NestedTypes.FirstOrDefault(t => t.Name == "ThreadId")
            ?.Methods.FirstOrDefault(m => m.Name == "InitializeForCurrentThread" && m.HasBody);
        if (method == null) { log("corelib: no Lock.ThreadId.InitializeForCurrentThread; left as it is"); return false; }
        int patched = 0;
        foreach (var i in method.Body.Instructions)
        {
            if (i.Operand is Mono.Cecil.MethodReference r && r.Name == "TryGetUInt32OSThreadId" && r.Parameters.Count == 0)
            {
                i.OpCode = Mono.Cecil.Cil.OpCodes.Ldc_I4_M1;     // "no OS thread id": uint.MaxValue, which the method checks
                i.Operand = null;
                patched++;
            }
        }
        if (patched == 0) { log("corelib: Lock.ThreadId does not ask for the OS thread id; left as it is"); return false; }
        module.Attributes &= ~Mono.Cecil.ModuleAttributes.StrongNameSigned;
        Directory.CreateDirectory(outDir);
        module.Write(Path.Combine(outDir, Path.GetFileName(corelib)));
        log("corelib: System.Threading.Lock tells threads apart by their managed id (the OS id is 1 on every thread here)");
        return true;
    }
}
