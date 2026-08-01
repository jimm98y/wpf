// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Compiles every shipped WGSL shader through wgpu's own frontend (naga, embedded in
// wgpu-native), so a syntax or type error in a shader fails the build rather than
// surfacing as a blank draw the first time some rarely-taken path runs. Several of
// these shaders are behind off-by-default switches (fs_shapebrush) or only fire on
// specific content, so nothing else in the suite compiles them at all.
//
// Also asserts the //#include mechanism is a pure textual no-op: each resolved shader
// must match the snapshot of what the source looked like as a single inline string.
// That is what makes the shared-fragment refactor provably behaviour-preserving.
//

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Wpf.Interop.WebGpu.Composition;

internal static class Program
{
    private static int _failures;

    private static int Main(string[] args)
    {
        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);

        var names = new List<string>(ShaderSource.AllNames());
        names.Sort(StringComparer.Ordinal);
        if (names.Count == 0)
        {
            Console.WriteLine("FAIL: no embedded WGSL resources found (packaging regression).");
            return 1;
        }

        Console.WriteLine($"{"shader",-24}{"lines",8}{"compiles",12}");
        Console.WriteLine(new string('-', 44));
        foreach (string name in names)
        {
            string wgsl = ShaderSource.Get(name);
            IntPtr module = renderer.CompileShaderForTest(wgsl);
            bool ok = module != IntPtr.Zero;
            Console.WriteLine($"{name,-24}{wgsl.Split('\n').Length,8}{(ok ? "ok" : "FAIL"),12}");
            if (!ok) { Console.WriteLine($"  [FAIL] {name} did not compile"); _failures++; }
        }

        // Optional byte-for-byte check against the pre-refactor snapshot.
        int si = Array.IndexOf(args, "--snapshot");
        if (si >= 0 && si + 1 < args.Length)
        {
            string dir = args[si + 1];
            Console.WriteLine();
            Console.WriteLine($"{"shader",-24}{"vs snapshot",14}");
            Console.WriteLine(new string('-', 38));
            foreach (string name in names)
            {
                string path = Path.Combine(dir, name + ".wgsl");
                if (!File.Exists(path)) { Console.WriteLine($"{name,-24}{"(no snapshot)",14}"); continue; }
                string expect = File.ReadAllText(path).Replace("\r\n", "\n");
                string actual = ShaderSource.Get(name);
                bool same = string.Equals(expect.TrimEnd('\n'), actual.TrimEnd('\n'), StringComparison.Ordinal);
                Console.WriteLine($"{name,-24}{(same ? "identical" : "DIFFERS"),14}");
                if (!same)
                {
                    _failures++;
                    string[] a = expect.TrimEnd('\n').Split('\n'), b = actual.TrimEnd('\n').Split('\n');
                    for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
                    {
                        string x = i < a.Length ? a[i] : "<eof>";
                        string y = i < b.Length ? b[i] : "<eof>";
                        if (x != y) { Console.WriteLine($"    line {i + 1}:\n      was: {x}\n      now: {y}"); break; }
                    }
                }
            }
        }

        Console.WriteLine();
        if (_failures > 0) { Console.WriteLine($"SHADER VALIDATION FAILED: {_failures} problem(s)."); return 1; }
        Console.WriteLine($"SHADER VALIDATION PASSED: {names.Count} shaders compile.");
        return 0;
    }
}
