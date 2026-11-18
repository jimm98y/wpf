// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler: the RDEF chunk, the reflection D3DReflect reads -- the bindings (samplers,
// textures, UAVs, then constant buffers, as fxc orders them), the constant buffers with their
// variables (offset, size, used flag, default value) and the variables' types. The layout is the one
// Dxbc/DxbcReflection.cs reads: fixed tables first, then types, defaults and strings.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl
{
    internal sealed partial class DxbcCodegen
    {
        private const string Creator = "Microsoft (R) HLSL Shader Compiler 10.1";

        private sealed class RdefBuilder
        {
            public readonly List<byte> Tail = new();
            public int Base;
            private readonly Dictionary<string, int> _strings = new();

            public int Here => Base + Tail.Count;

            public void Align4() { while (Tail.Count % 4 != 0) Tail.Add(0xAB); }

            public void U32(int v) { Tail.AddRange(BitConverter.GetBytes(v)); }
            public void U16(int v) { Tail.AddRange(BitConverter.GetBytes((ushort)v)); }

            public int Str(string s)
            {
                if (_strings.TryGetValue(s, out int o)) return o;
                o = Here;
                Tail.AddRange(Encoding.ASCII.GetBytes(s));
                Tail.Add(0);
                _strings[s] = o;
                return o;
            }
        }

        private static int ShaderVariableType(ScalarKind s) => s switch
        {
            ScalarKind.Bool => 1, ScalarKind.Int => 2, ScalarKind.Uint => 19, ScalarKind.Double => 39, _ => 3,
        };

        private static string TypeName(HlslType t) => t switch
        {
            ArrayType a => TypeName(a.Element),
            NumericType n => n.IsMatrix ? $"{NumericType.Name(n.Scalar)}{n.Rows}x{n.Cols}" : n.ToString(),
            _ => t.ToString(),
        };

        /// <summary>A type descriptor (and its members' first), its offset in the chunk.</summary>
        private int WriteType(RdefBuilder b, HlslType type, Dictionary<string, int> written)
        {
            int elements = 0;
            var t = type;
            if (t is ArrayType at) { elements = Math.Max(1, at.Length); t = at.Element; while (t is ArrayType inner) { elements *= Math.Max(1, inner.Length); t = inner.Element; } }
            string key = $"{t}[{elements}]";
            if (written.TryGetValue(key, out int known)) return known;

            int cls, kind, rows, cols, memberCount = 0, memberOffset = 0;
            switch (t)
            {
                case NumericType n:
                    cls = n.IsMatrix ? (n.RowMajor ? 2 : 3) : n.Cols == 1 ? 0 : 1;
                    kind = ShaderVariableType(n.Scalar);
                    rows = n.Rows; cols = n.Cols;
                    break;
                case StructType s:
                {
                    cls = 5; kind = 0; rows = 1; cols = s.Components;
                    var memberTypes = s.Fields.Select(f => WriteType(b, f.Type, written)).ToList();
                    var names = s.Fields.Select(f => b.Str(f.Name)).ToList();
                    b.Align4();
                    memberOffset = b.Here;
                    int off = 0;
                    for (int i = 0; i < s.Fields.Count; i++)
                    {
                        off = Place(off, s.Fields[i].Type);
                        b.U32(names[i]); b.U32(memberTypes[i]); b.U32(off);
                        off += SizeOf(s.Fields[i].Type);
                    }
                    memberCount = s.Fields.Count;
                    break;
                }
                default:
                    cls = 4; kind = 0; rows = 0; cols = 0;
                    break;
            }
            int name = Sm5 ? b.Str(TypeName(t)) : 0;
            b.Align4();
            int at0 = b.Here;
            b.U16(cls); b.U16(kind); b.U16(rows); b.U16(cols); b.U16(elements); b.U16(memberCount); b.U32(memberOffset);
            if (Sm5) { b.U32(0); b.U32(0); b.U32(0); b.U32(0); b.U32(name); }
            written[key] = at0;
            return at0;
        }

        private static (int Kind, int Dimension) BindKind(ObjectType t) => t.Name switch
        {
            "Buffer" => (2, 1), "RWBuffer" => (4, 1),
            "Texture1D" or "sampler1D" or "texture1D" => (2, 2), "Texture1DArray" => (2, 3),
            "Texture2D" or "sampler2D" or "sampler" or "texture" or "texture2D" => (2, 4), "Texture2DArray" => (2, 5),
            "Texture2DMS" => (2, 6), "Texture2DMSArray" => (2, 7),
            "Texture3D" or "sampler3D" or "texture3D" => (2, 8), "TextureCube" or "samplerCUBE" or "textureCUBE" => (2, 9), "TextureCubeArray" => (2, 10),
            "RWTexture1D" => (4, 2), "RWTexture1DArray" => (4, 3), "RWTexture2D" => (4, 4), "RWTexture2DArray" => (4, 5), "RWTexture3D" => (4, 8),
            "StructuredBuffer" => (5, 1), "RWStructuredBuffer" => (6, 1), "ByteAddressBuffer" => (7, 1), "RWByteAddressBuffer" => (8, 1),
            "AppendStructuredBuffer" => (9, 1), "ConsumeStructuredBuffer" => (10, 1),
            _ => (2, 4),
        };

        private byte[] Rdef()
        {
            bool v51 = _major > 5 || (_major == 5 && _minor >= 1);
            int headerSize = Sm5 ? 60 : 28, bindSize = v51 ? 40 : 32, varSize = Sm5 ? 40 : 24;

            var cbs = _cbuffers.Where(c => c.Slot >= 0 && c.Used).OrderBy(c => c.DeclOrder).ToList();
            var binds = new List<(string Name, int Kind, int Ret, int Dim, int Samples, int Reg, int Count, int Flags)>();
            foreach (var r in _resources.Where(r => r.Used && r.Slot >= 0 && (r.Kind == 's' || r.Legacy)).OrderBy(r => r.Slot))
                binds.Add((r.Name, 3, 0, 0, 0, r.Slot, r.ArrayLength, r.Type.IsComparisonSampler ? 2 : 0));
            foreach (var r in _resources.Where(r => r.Used && r.Slot >= 0 && (r.Kind == 't' || r.Legacy)).OrderBy(r => r.Slot))
                binds.Add(Binding(r));
            foreach (var r in _resources.Where(r => r.Used && r.Slot >= 0 && r.Kind == 'u').OrderBy(r => r.Slot))
                binds.Add(Binding(r));
            foreach (var c in cbs.OrderBy(c => c.Slot))
                binds.Add((c.Name, c.Texture ? 1 : 0, 0, 0, 0, c.Slot, 1, c.ExplicitSlot >= 0 ? 1 : 0));

            int bindOff = headerSize, cbOff = bindOff + binds.Count * bindSize;
            int varOff = cbOff + cbs.Count * 24;
            var members = cbs.Select(c => c.Members).ToList();
            int varTotal = members.Sum(m => m.Count);
            var b = new RdefBuilder { Base = varOff + varTotal * varSize };

            // types, defaults and names into the tail
            var written = new Dictionary<string, int>();
            var varRecords = new List<int[]>();
            foreach (var c in cbs)
                foreach (var m in c.Members)
                {
                    int type = WriteType(b, m.Type, written);
                    int def = 0;
                    if (m.Default != null) { b.Align4(); def = b.Here; b.Tail.AddRange(m.Default); }
                    int name = b.Str(m.Name);
                    varRecords.Add(new[] { name, m.Offset, m.Size, m.Used ? 2 : 0, type, def });
                }
            var bindNames = binds.Select(x => b.Str(x.Name)).ToList();
            var cbNames = cbs.Select(c => b.Str(c.Name)).ToList();
            int creator = b.Str(Creator);
            b.Align4();

            var head = new List<byte>();
            void W(int v) => head.AddRange(BitConverter.GetBytes(v));
            W(cbs.Count); W(cbs.Count > 0 ? cbOff : 0); W(binds.Count); W(binds.Count > 0 ? bindOff : 0);
            head.Add((byte)_minor); head.Add((byte)_major);
            head.AddRange(BitConverter.GetBytes((ushort)(_stage switch
            {
                ShaderStage.Pixel => 0xFFFF, ShaderStage.Vertex => 0xFFFE, ShaderStage.Geometry => 0x4753,
                ShaderStage.Hull => 0x4853, ShaderStage.Domain => 0x4453, _ => 0x4353,
            })));
            W((int)(_flags & 0xFFFF) | 0x100);               // D3DCOMPILE_NO_PRESHADER, as fxc records
            W(creator);
            if (Sm5)
            {
                head.AddRange(Encoding.ASCII.GetBytes("RD11"));
                W(60); W(24); W(bindSize); W(varSize); W(36); W(12); W(0);
            }
            for (int i = 0; i < binds.Count; i++)
            {
                var x = binds[i];
                W(bindNames[i]); W(x.Kind); W(x.Ret); W(x.Dim); W(x.Samples); W(x.Reg); W(x.Count); W(x.Flags);
                if (v51) { W(0); W(i); }
            }
            int vi = 0;
            for (int i = 0; i < cbs.Count; i++)
            {
                W(cbNames[i]); W(cbs[i].Members.Count); W(cbs[i].Members.Count > 0 ? varOff + vi * varSize : 0);
                W((cbs[i].SizeBytes + 15) & ~15); W(0); W(cbs[i].Texture ? 1 : 0);
                vi += cbs[i].Members.Count;
            }
            foreach (var v in varRecords)
            {
                foreach (int x in v) W(x);
                if (Sm5) { W(-1); W(0); W(-1); W(0); }
            }
            head.AddRange(b.Tail);
            return head.ToArray();
        }

        private static (string, int, int, int, int, int, int, int) Binding(Resource r)
        {
            var (kind, dim) = BindKind(r.Type);
            var e = r.Type.Element as NumericType;
            bool structured = kind is 5 or 6 or 9 or 10;
            bool raw = kind is 7 or 8;
            int ret = structured ? 6 : raw ? 7 : e?.Scalar switch { ScalarKind.Int => 3, ScalarKind.Uint or ScalarKind.Bool => 4, ScalarKind.Double => 9, _ => 5 };
            int samples = structured ? (r.Type.Element == null ? 4 : SizeOfStructured(r.Type.Element)) : r.Type.Name.Contains("MS") ? r.Type.Count : -1;
            int flags = structured || raw ? 0 : (Math.Max(1, Math.Min(4, e?.Count ?? 4)) - 1) << 2;
            if (kind == 6 && r.Type.Name is "AppendStructuredBuffer" or "ConsumeStructuredBuffer") kind = 11;
            return (r.Name, kind, ret, dim, samples, r.Slot, r.ArrayLength, flags | (r.ExplicitSlot >= 0 ? 1 : 0));
        }
    }
}
