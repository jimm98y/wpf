// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The RDEF chunk of a DXBC container: resource bindings and constant-buffer layouts, with variable
// types and struct members (Documentation/d3d11-webgpu-shim.md, section 9). The translator does not
// need it (the declarations carry what it uses); D3DReflect does, and the tests drive shaders from it.
//
// Layout (as fxc writes it): a header of counts and offsets, constant-buffer descriptors (24 bytes),
// binding descriptors (32 bytes; 40 from 5.1, with space and id), variable descriptors (24 bytes,
// 40 from 5.0) and type descriptors (16 bytes, 36 from 5.0, with a name). Every offset is from the
// start of the chunk's data.
//

using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc
{
    internal sealed class DxbcReflection
    {
        internal sealed class Type
        {
            public int Class, Kind, Rows, Columns, Elements;   // D3D_SHADER_VARIABLE_CLASS, D3D_SHADER_VARIABLE_TYPE
            public string Name = "";
            public List<(string Name, int Offset, Type Type)> Members = new();
        }

        internal sealed class Variable
        {
            public string Name = "";
            public int Offset, Size, Flags;
            public Type Type = new();
            public byte[]? Default;
        }

        internal sealed class ConstantBuffer
        {
            public string Name = "";
            public int Size, Flags, Kind;                       // D3D_CBUFFER_TYPE: 0 cbuffer, 1 tbuffer, 2 interface pointers, 3 resource bind info
            public List<Variable> Variables = new();
        }

        internal sealed class Binding
        {
            public string Name = "";
            /// <summary>D3D_SHADER_INPUT_TYPE: 0 cbuffer, 1 tbuffer, 2 texture, 3 sampler, 4 UAV typed,
            /// 5 structured, 6 UAV structured, 7 byte address, 8 UAV byte address, 9 append, 10 consume,
            /// 11 UAV structured with counter.</summary>
            public int Kind;
            public int ReturnType, Dimension, Samples, Register, Count, Flags, Space, Id;
        }

        public int Major, Minor, ProgramType, Flags;
        public string Creator = "";
        public readonly List<ConstantBuffer> ConstantBuffers = new();
        public readonly List<Binding> Bindings = new();

        private static int I32(ReadOnlySpan<byte> d, int o) => BitConverter.ToInt32(d[o..]);
        private static ushort U16(ReadOnlySpan<byte> d, int o) => BitConverter.ToUInt16(d[o..]);

        private static string Str(ReadOnlySpan<byte> d, int o)
        {
            if (o <= 0 || o >= d.Length) return "";
            int e = o;
            while (e < d.Length && d[e] != 0) e++;
            return Encoding.ASCII.GetString(d[o..e]);
        }

        public static DxbcReflection Read(ReadOnlySpan<byte> d)
        {
            var r = new DxbcReflection();
            int cbCount = I32(d, 0), cbOff = I32(d, 4), bindCount = I32(d, 8), bindOff = I32(d, 12);
            r.Minor = d[16]; r.Major = d[17]; r.ProgramType = U16(d, 18);
            r.Flags = I32(d, 20);
            r.Creator = Str(d, I32(d, 24));
            bool v51 = r.Major > 5 || (r.Major == 5 && r.Minor >= 1);
            int bindSize = v51 ? 40 : 32;
            int varSize = r.Major >= 5 ? 40 : 24;
            for (int i = 0; i < bindCount; i++)
            {
                int o = bindOff + i * bindSize;
                var b = new Binding
                {
                    Name = Str(d, I32(d, o)), Kind = I32(d, o + 4), ReturnType = I32(d, o + 8), Dimension = I32(d, o + 12),
                    Samples = I32(d, o + 16), Register = I32(d, o + 20), Count = I32(d, o + 24), Flags = I32(d, o + 28),
                };
                if (v51) { b.Space = I32(d, o + 32); b.Id = I32(d, o + 36); }
                r.Bindings.Add(b);
            }
            var types = new Dictionary<int, Type>();
            for (int i = 0; i < cbCount; i++)
            {
                int o = cbOff + i * 24;
                var cb = new ConstantBuffer { Name = Str(d, I32(d, o)), Size = I32(d, o + 12), Flags = I32(d, o + 16), Kind = I32(d, o + 20) };
                int vars = I32(d, o + 4), vOff = I32(d, o + 8);
                for (int v = 0; v < vars; v++)
                {
                    int vo = vOff + v * varSize;
                    var va = new Variable { Name = Str(d, I32(d, vo)), Offset = I32(d, vo + 4), Size = I32(d, vo + 8), Flags = I32(d, vo + 12) };
                    va.Type = ReadType(d, I32(d, vo + 16), r.Major >= 5, types, 0);
                    int def = I32(d, vo + 20);
                    if (def > 0 && def + va.Size <= d.Length) va.Default = d.Slice(def, va.Size).ToArray();
                    cb.Variables.Add(va);
                }
                r.ConstantBuffers.Add(cb);
            }
            return r;
        }

        private static Type ReadType(ReadOnlySpan<byte> d, int o, bool v5, Dictionary<int, Type> seen, int depth)
        {
            if (seen.TryGetValue(o, out var known)) return known;
            var t = new Type();
            seen[o] = t;
            if (o <= 0 || o + 16 > d.Length || depth > 32) return t;
            t.Class = U16(d, o); t.Kind = U16(d, o + 2); t.Rows = U16(d, o + 4); t.Columns = U16(d, o + 6);
            t.Elements = U16(d, o + 8);
            int members = U16(d, o + 10), mOff = I32(d, o + 12);
            if (v5 && o + 36 <= d.Length) t.Name = Str(d, I32(d, o + 32));
            for (int m = 0; m < members; m++)
            {
                int mo = mOff + m * 12;
                if (mo + 12 > d.Length) break;
                t.Members.Add((Str(d, I32(d, mo)), I32(d, mo + 8), ReadType(d, I32(d, mo + 4), v5, seen, depth + 1)));
            }
            return t;
        }
    }
}
