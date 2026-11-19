// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The browser flavor's calls through native function pointers (IL calli: the COM shims' vtable calls). Mono on
// WebAssembly calls a native function only through an interp-to-native trampoline of its signature, and its build
// makes one for each signature a P/Invoke has, never for a calli's (aot-runtime-wasm.c: the runtime stops). The
// stack's assembly is not rewritten for a bundle (the SDK's own), so it declares the signatures itself
// (Browser/CalliSignatures.cs). This test reads every calli of the built browser assembly and checks each
// signature -- as Mono writes it: I for 32-bit values and pointers, L for 64-bit, F, D, V -- is declared there.
// MF_WRITE_CALLI=1 writes the file anew.
//

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Xunit;

namespace WgpuInterop.Tests.Browser
{
    public sealed class CalliSignatureTests
    {
        private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        private static string Dll => Path.Combine(Root, "Browser", "bin", "Release", "net10.0", "Microsoft.Wpf.Interop.WebGpu.dll");
        private static string Declarations => Path.Combine(Root, "Browser", "CalliSignatures.cs");

        /// <summary>A type as Mono's trampoline signatures write it; '?' of a struct by value (no trampoline: not to be used).</summary>
        private sealed class Letters : ISignatureTypeProvider<char, object?>
        {
            private readonly MetadataReader _r;
            public Letters(MetadataReader r) { _r = r; }
            public char GetPrimitiveType(PrimitiveTypeCode t) => t switch
            {
                PrimitiveTypeCode.Void => 'V',
                PrimitiveTypeCode.Int64 or PrimitiveTypeCode.UInt64 => 'L',
                PrimitiveTypeCode.Single => 'F',
                PrimitiveTypeCode.Double => 'D',
                PrimitiveTypeCode.IntPtr or PrimitiveTypeCode.UIntPtr => 'I',
                PrimitiveTypeCode.TypedReference or PrimitiveTypeCode.String or PrimitiveTypeCode.Object => 'I',
                _ => 'I',
            };
            public char GetPointerType(char e) => 'I';
            public char GetByReferenceType(char e) => 'I';
            public char GetFunctionPointerType(MethodSignature<char> s) => 'I';
            public char GetSZArrayType(char e) => 'I';
            public char GetArrayType(char e, ArrayShape s) => 'I';
            public char GetGenericInstantiation(char g, ImmutableArray<char> a) => '?';
            public char GetGenericMethodParameter(object? c, int i) => '?';
            public char GetGenericTypeParameter(object? c, int i) => '?';
            public char GetModifiedType(char m, char u, bool req) => u;
            public char GetPinnedType(char e) => e;
            public char GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) => '?';
            public char GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte kind) => kind == (byte)SignatureTypeKind.ValueType ? Enum(null) : 'I';

            public char GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte kind)
            {
                if (kind != (byte)SignatureTypeKind.ValueType) return 'I';
                var t = r.GetTypeDefinition(h);
                if (t.BaseType.Kind == HandleKind.TypeReference && r.GetString(r.GetTypeReference((TypeReferenceHandle)t.BaseType).Name) == "Enum") return Enum(t);
                return '?';
            }

            /// <summary>An enum: of its value__ field's type (int when it is another assembly's).</summary>
            private char Enum(TypeDefinition? t)
            {
                if (t is not { } d) return 'I';
                foreach (var fh in d.GetFields())
                {
                    var f = _r.GetFieldDefinition(fh);
                    if ((f.Attributes & System.Reflection.FieldAttributes.Static) != 0) continue;
                    return f.DecodeSignature(this, null);
                }
                return 'I';
            }
        }

        /// <summary>Every unmanaged calli's signature in the assembly: the return's letter, then the parameters'.</summary>
        internal static SortedSet<string> Signatures(string dll, out List<string> unsupported)
        {
            var found = new SortedSet<string>(StringComparer.Ordinal);
            unsupported = new List<string>();
            using var pe = new PEReader(File.OpenRead(dll));
            var r = pe.GetMetadataReader();
            var letters = new Letters(r);
            foreach (var mh in r.MethodDefinitions)
            {
                var m = r.GetMethodDefinition(mh);
                if (m.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILReader();
                while (il.RemainingBytes > 0)
                {
                    int op = il.ReadByte();
                    if (op == 0xFE) { int op2 = il.ReadByte(); Skip(ref il, 0xFE00 | op2); continue; }
                    if (op == 0x29)                                       // calli <StandAloneSig>
                    {
                        var sh = (StandaloneSignatureHandle)MetadataTokens.EntityHandle(il.ReadInt32());
                        var sig = r.GetStandaloneSignature(sh).DecodeMethodSignature(letters, null);
                        if (sig.Header.CallingConvention is SignatureCallingConvention.Default or SignatureCallingConvention.VarArgs) continue;
                        string s = sig.ReturnType + new string(sig.ParameterTypes.ToArray());
                        if (s.Contains('?')) unsupported.Add($"{r.GetString(r.GetTypeDefinition(m.GetDeclaringType()).Name)}.{r.GetString(m.Name)}: {s}");
                        else found.Add(s);
                        continue;
                    }
                    Skip(ref il, op);
                }
            }
            return found;
        }

        /// <summary>An instruction's operand passed over.</summary>
        private static void Skip(ref BlobReader il, int op)
        {
            int size = op switch
            {
                0x20 or 0x22 or 0x28 or 0x27 or 0x38 or 0x39 or 0x3A or 0x3B or 0x3C or 0x3D or 0x3E or 0x3F or 0x40 or 0x41 or 0x42 or 0x43 or 0x44
                    or 0x6F or 0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75 or 0x79 or 0x7B or 0x7C or 0x7D or 0x7E or 0x7F or 0x80 or 0x81 or 0x8C or 0x8D or 0x8F
                    or 0xA3 or 0xA4 or 0xA5 or 0xC2 or 0xC6 or 0xD0 or 0xDD or 0xFE06 or 0xFE07 or 0xFE15 or 0xFE16 or 0xFE1C => 4,
                0x21 or 0x23 => 8,
                0x0E or 0x0F or 0x10 or 0x11 or 0x12 or 0x13 or 0x1F or 0x2B or 0x2C or 0x2D or 0x2E or 0x2F or 0x30 or 0x31 or 0x32 or 0x33 or 0x34
                    or 0x35 or 0x36 or 0x37 or 0xDE or 0xFE12 or 0xFE19 => 1,
                0xFE09 or 0xFE0A or 0xFE0B or 0xFE0C or 0xFE0D or 0xFE0E => 2,
                0x45 => -1,
                _ => 0,
            };
            if (size == -1) { int n = il.ReadInt32(); il.Offset += n * 4; return; }
            il.Offset += size;
        }

        private static string CsType(char c) => c switch { 'I' => "int", 'L' => "long", 'F' => "float", 'D' => "double", _ => "void" };

        /// <summary>The declarations' file: a P/Invoke of each signature, never called, never bound.</summary>
        internal static string File_(IEnumerable<string> sigs)
        {
            var b = new StringBuilder();
            b.AppendLine("// Licensed to the .NET Foundation under one or more agreements.");
            b.AppendLine("// The .NET Foundation licenses this file to you under the MIT license.");
            b.AppendLine();
            b.AppendLine("//");
            b.AppendLine("// The signatures of the browser flavor's calls through native function pointers (IL calli), as P/Invokes that are");
            b.AppendLine("// never called and never bound: Mono on WebAssembly makes an interp-to-native trampoline for each signature a P/Invoke");
            b.AppendLine("// has and for none a calli has, and stops at a calli of no trampoline. Written by CalliSignatureTests (MF_WRITE_CALLI=1),");
            b.AppendLine("// which checks that it holds every signature of the built assembly: the letters are Mono's (I 32-bit and pointers, L");
            b.AppendLine("// 64-bit, F, D, V void), the return's first.");
            b.AppendLine("//");
            b.AppendLine();
            b.AppendLine("#if WGPU_BROWSER");
            b.AppendLine("using System.Runtime.InteropServices;");
            b.AppendLine();
            b.AppendLine("namespace Microsoft.Wpf.Interop.WebGpu.Browser");
            b.AppendLine("{");
            b.AppendLine("    internal static class CalliSignatures");
            b.AppendLine("    {");
            foreach (var s in sigs)
            {
                var ps = string.Join(", ", s.Skip(1).Select((c, i) => $"{CsType(c)} a{i}"));
                b.AppendLine($"        [DllImport(\"wpfwebgpu-calli-signatures\")] internal static extern {CsType(s[0])} Sig_{s}({ps});");
            }
            b.AppendLine("    }");
            b.AppendLine("}");
            b.AppendLine("#endif");
            return b.ToString();
        }

        [Fact]
        public void TheBrowserAssembly_DeclaresEveryCalliSignatureItCalls()
        {
            Assert.SkipWhen(!File.Exists(Dll), $"the browser flavor is not built ({Dll})");
            var sigs = Signatures(Dll, out var unsupported);
            Assert.True(unsupported.Count == 0, "calls through function pointers with a struct by value or generics, which no trampoline takes in the browser:\n" + string.Join("\n", unsupported));
            if (Environment.GetEnvironmentVariable("MF_WRITE_CALLI") == "1") File.WriteAllText(Declarations, File_(sigs));
            var declared = File.Exists(Declarations)
                ? File.ReadAllLines(Declarations).Where(l => l.Contains(" Sig_")).Select(l => l[(l.IndexOf(" Sig_") + 5)..l.IndexOf('(', l.IndexOf(" Sig_"))]).ToHashSet()
                : new HashSet<string>();
            var missing = sigs.Where(s => !declared.Contains(s)).ToList();
            Assert.True(missing.Count == 0, $"Browser/CalliSignatures.cs lacks {missing.Count} signatures ({string.Join(" ", missing)}): build the browser flavor, run this test with MF_WRITE_CALLI=1, build again");
        }
    }
}
