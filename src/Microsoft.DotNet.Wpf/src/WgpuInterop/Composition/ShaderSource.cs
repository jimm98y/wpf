// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Loads the WGSL shader sources, which live as .wgsl files under Composition/Shaders
// and ship as embedded resources.
//
// They used to be `const string` literals inside WgpuSceneRenderer. As files they get
// WGSL tooling (an editor highlights them, a validator can be pointed at them), and --
// the reason that matters most here -- they can share text: the vertex stage and the
// VSOut struct were copy-pasted into nine separate string constants, so a change to the
// vertex ABI meant nine edits that had to stay in lockstep by hand.
//
// Sharing is a one-line directive:
//
//     //#include _VertexCommon.wgsl
//
// resolved at load time, once, with the result cached. Includes are resolved
// recursively and de-duplicated per root shader, so a file pulled in by two different
// includes is still emitted exactly once (WGSL has no include guards and would reject
// the duplicate declarations).
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal static class ShaderSource
    {
        private const string ResourcePrefix = "Microsoft.Wpf.Interop.WebGpu.Composition.Shaders.";
        private static readonly Dictionary<string, string> s_resolved = new();
        private static readonly object s_gate = new();

        /// <summary>
        /// The fully resolved WGSL for <paramref name="name"/> (without the .wgsl suffix).
        /// Throws if the shader or one of its includes is missing -- a build that shipped
        /// without its shaders cannot render anything, so failing loudly at first use beats
        /// an empty module and an opaque pipeline-creation error later.
        /// </summary>
        public static string Get(string name)
        {
            lock (s_gate)
            {
                if (s_resolved.TryGetValue(name, out string? cached)) return cached;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var sb = new StringBuilder();
                Emit(name, seen, sb);
                string text = sb.ToString();
                s_resolved[name] = text;
                return text;
            }
        }

        /// <summary>Every shader that ships, for validation sweeps.</summary>
        public static IEnumerable<string> AllNames()
        {
            foreach (string res in typeof(ShaderSource).Assembly.GetManifestResourceNames())
            {
                if (!res.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
                if (!res.EndsWith(".wgsl", StringComparison.Ordinal)) continue;
                string n = res.Substring(ResourcePrefix.Length, res.Length - ResourcePrefix.Length - 5);
                if (n.StartsWith("_", StringComparison.Ordinal)) continue;   // include-only fragment
                yield return n;
            }
        }

        private static void Emit(string name, HashSet<string> seen, StringBuilder sb)
        {
            if (!seen.Add(name)) return;              // already pulled in by another include
            // TrimEnd: a file's trailing newline would otherwise become a blank line at the
            // include site, so the spliced text would not match the inline original.
            foreach (string line in ReadRaw(name).TrimEnd('\n').Split('\n'))
            {
                string t = line.TrimStart();
                if (t.StartsWith("//#include ", StringComparison.Ordinal))
                {
                    string inc = t.Substring("//#include ".Length).Trim();
                    if (inc.EndsWith(".wgsl", StringComparison.Ordinal)) inc = inc[..^5];
                    Emit(inc, seen, sb);
                    continue;
                }
                sb.Append(line).Append('\n');
            }
        }

        private static string ReadRaw(string name)
        {
            string res = ResourcePrefix + name + ".wgsl";
            using Stream? s = typeof(ShaderSource).Assembly.GetManifestResourceStream(res)
                ?? throw new InvalidOperationException($"Missing embedded WGSL resource '{res}'.");
            using var r = new StreamReader(s);
            return r.ReadToEnd().Replace("\r\n", "\n");
        }
    }
}
