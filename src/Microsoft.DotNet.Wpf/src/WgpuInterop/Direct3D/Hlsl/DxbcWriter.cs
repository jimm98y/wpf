// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler: DXBC out -- the container, its signatures (ISGN, OSGN, PCSG), its
// reflection (RDEF) and its program (SHDR for shader model 4, SHEX for 5), in the layout fxc writes
// and Dxbc/DxbcShader.cs and Dxbc/DxbcReflection.cs read, with the container's checksum as the
// runtime computes it.
//

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl
{
    /// <summary>An operand as it is encoded: register file, indices, components and modifier.</summary>
    internal sealed class DxOperand
    {
        public OperandType Type;
        /// <summary>0, 1 or 4 components.</summary>
        public int Components = 4;
        /// <summary>0 mask (a destination), 1 swizzle, 2 select one.</summary>
        public int Selection;
        public int Mask = 0xF;
        public byte[] Swizzle = { 0, 1, 2, 3 };
        public long[] Indices = Array.Empty<long>();
        /// <summary>A relative part of each index (null: immediate only).</summary>
        public DxOperand?[] Relative = Array.Empty<DxOperand?>();
        public OperandModifier Modifier;
        public uint[] Values = Array.Empty<uint>();

        public static DxOperand Dest(OperandType t, int mask, params long[] idx) => new() { Type = t, Selection = 0, Mask = mask, Indices = idx, Relative = new DxOperand?[idx.Length] };
        public static DxOperand Src(OperandType t, byte[] swizzle, params long[] idx) => new() { Type = t, Selection = 1, Swizzle = swizzle, Indices = idx, Relative = new DxOperand?[idx.Length] };
        public static DxOperand Scalar(OperandType t, int comp, params long[] idx) => new() { Type = t, Selection = 2, Swizzle = new[] { (byte)comp, (byte)comp, (byte)comp, (byte)comp }, Indices = idx, Relative = new DxOperand?[idx.Length] };
        public static DxOperand Imm(params uint[] v) => new() { Type = OperandType.Immediate32, Components = v.Length == 1 ? 1 : 4, Values = v };
        /// <summary>A register named by its index alone (s#, t#, u#: no components).</summary>
        public static DxOperand Bare(OperandType t, params long[] idx) => new() { Type = t, Components = 0, Indices = idx, Relative = new DxOperand?[idx.Length] };

        public void Write(List<uint> w)
        {
            uint tok = 0;
            int nc = Components == 0 ? 0 : Components == 1 ? 1 : 2;
            tok |= (uint)nc;
            if (nc == 2)
            {
                tok |= (uint)Selection << 2;
                if (Selection == 0) tok |= (uint)(Mask & 0xF) << 4;
                else if (Selection == 1) tok |= (uint)(Swizzle[0] | Swizzle[1] << 2 | Swizzle[2] << 4 | Swizzle[3] << 6) << 4;
                else tok |= (uint)Swizzle[0] << 4;
            }
            tok |= (uint)Type << 12;
            if (Type is not (OperandType.Immediate32 or OperandType.Immediate64))
            {
                tok |= (uint)Indices.Length << 20;
                for (int d = 0; d < Indices.Length; d++)
                {
                    uint rep = Relative[d] == null ? 0u : Indices[d] == 0 ? 2u : 3u;
                    tok |= rep << (22 + 3 * d);
                }
            }
            bool ext = Modifier != OperandModifier.None;
            if (ext) tok |= 0x80000000;
            w.Add(tok);
            if (ext) w.Add(1u | ((uint)Modifier << 6));
            if (Type is OperandType.Immediate32 or OperandType.Immediate64)
            {
                w.AddRange(Values);
                return;
            }
            for (int d = 0; d < Indices.Length; d++)
            {
                if (Relative[d] == null || Indices[d] != 0) w.Add((uint)Indices[d]);
                Relative[d]?.Write(w);
            }
        }
    }

    internal sealed class DxbcWriter
    {
        public ShaderStage Stage;
        public int Major = 5, Minor;
        public readonly List<uint> Code = new();
        public int InstructionCount, TempCount;

        // ---- the program ----------------------------------------------------------------------------

        /// <summary>An instruction: opcode with its control bits, operands, and extra DWORDs after them.</summary>
        public void Emit(Opcode op, uint control, params DxOperand[] operands) => EmitWith(op, control, null, operands);

        public void EmitWith(Opcode op, uint control, uint[]? before, DxOperand[] operands, uint[]? after = null)
        {
            var body = new List<uint>();
            if (before != null) body.AddRange(before);
            foreach (var o in operands) o.Write(body);
            if (after != null) body.AddRange(after);
            uint tok = (uint)op | (control << 11);
            int len = body.Count + 1;
            if (len > 127) throw new InvalidOperationException($"instruction {op} too long");
            tok |= (uint)len << 24;
            Code.Add(tok);
            Code.AddRange(body);
            if (op < Opcode.DclResource || op > Opcode.DclGlobalFlags) InstructionCount++;
        }

        /// <summary>An instruction with an extended opcode token (sample offsets, resource dimension/return type).</summary>
        public void EmitExtended(Opcode op, uint control, uint[] extended, params DxOperand[] operands)
        {
            var body = new List<uint>();
            for (int i = 0; i < extended.Length; i++) body.Add(extended[i] | (i + 1 < extended.Length ? 0x80000000u : 0));
            foreach (var o in operands) o.Write(body);
            uint tok = (uint)op | (control << 11) | 0x80000000u | ((uint)(body.Count + 1) << 24);
            Code.Add(tok);
            Code.AddRange(body);
            InstructionCount++;
        }

        /// <summary>A declaration of raw DWORDs (dcl_temps 3, dcl_thread_group 8 8 1...).</summary>
        public void Declare(Opcode op, uint control, params uint[] words)
        {
            Code.Add((uint)op | (control << 11) | ((uint)(words.Length + 1) << 24));
            Code.AddRange(words);
        }

        public void ImmediateConstantBuffer(uint[] words)
        {
            Code.Add((uint)Opcode.CustomData | (3u << 11));
            Code.Add((uint)words.Length + 2);
            Code.AddRange(words);
        }

        public byte[] Program()
        {
            var words = new List<uint> { (uint)Stage << 16 | (uint)Major << 4 | (uint)Minor, 0 };
            words.AddRange(Code);
            words[1] = (uint)words.Count;
            var b = new byte[words.Count * 4];
            Buffer.BlockCopy(words.ToArray(), 0, b, 0, b.Length);
            return b;
        }

        // ---- signatures --------------------------------------------------------------------------

        internal sealed class SigElement
        {
            public string Name = "";
            public int Index, SystemValue, ComponentType, Register, Mask, ReadWriteMask, Stream;
        }

        public static byte[] Signature(List<SigElement> elements, bool withStream)
        {
            int stride = withStream ? 28 : 24;
            var head = new List<byte>();
            var strings = new List<byte>();
            int baseOff = 8 + elements.Count * stride;
            var offsets = new Dictionary<string, int>();
            foreach (var e in elements)
            {
                if (offsets.ContainsKey(e.Name)) continue;
                offsets[e.Name] = baseOff + strings.Count;
                strings.AddRange(Encoding.ASCII.GetBytes(e.Name));
                strings.Add(0);
            }
            while (strings.Count % 4 != 0) strings.Add(0xAB);
            void I(int v) => head.AddRange(BitConverter.GetBytes(v));
            I(elements.Count); I(8);
            foreach (var e in elements)
            {
                if (withStream) I(e.Stream);
                I(offsets[e.Name]); I(e.Index); I(e.SystemValue); I(e.ComponentType); I(e.Register);
                head.Add((byte)e.Mask); head.Add((byte)e.ReadWriteMask); head.Add(0); head.Add(0);
            }
            head.AddRange(strings);
            return head.ToArray();
        }

        // ---- the container --------------------------------------------------------------------------

        public static byte[] Container(List<(string Tag, byte[] Data)> chunks)
        {
            int header = 32 + 4 * chunks.Count;
            int size = header;
            foreach (var (_, d) in chunks) size += 8 + ((d.Length + 3) & ~3);
            var b = new byte[size];
            Encoding.ASCII.GetBytes("DXBC").CopyTo(b, 0);
            BitConverter.GetBytes(1).CopyTo(b, 20);
            BitConverter.GetBytes(size).CopyTo(b, 24);
            BitConverter.GetBytes(chunks.Count).CopyTo(b, 28);
            int at = header;
            for (int i = 0; i < chunks.Count; i++)
            {
                BitConverter.GetBytes(at).CopyTo(b, 32 + 4 * i);
                Encoding.ASCII.GetBytes(chunks[i].Tag).CopyTo(b, at);
                BitConverter.GetBytes(chunks[i].Data.Length).CopyTo(b, at + 4);
                chunks[i].Data.CopyTo(b, at + 8);
                at += 8 + ((chunks[i].Data.Length + 3) & ~3);
            }
            DxbcChecksum.Compute(b).CopyTo(b, 4);
            return b;
        }
    }

    /// <summary>The DXBC container's checksum: MD5 over the bytes after it, with the runtime's own padding
    /// of the last block (the length in bits up front and as ((n &lt;&lt; 1) | 1) at the end).</summary>
    internal static class DxbcChecksum
    {
        private static readonly uint[] K = BuildK();
        private static readonly int[] R =
        {
            7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
            4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
        };

        private static uint[] BuildK()
        {
            var k = new uint[64];
            for (int i = 0; i < 64; i++) k[i] = (uint)(long)Math.Floor(Math.Abs(Math.Sin(i + 1)) * 4294967296.0);
            return k;
        }

        public static byte[] Compute(byte[] container)
        {
            var data = new ReadOnlySpan<byte>(container, 20, container.Length - 20);
            uint a0 = 0x67452301, b0 = 0xefcdab89, c0 = 0x98badcfe, d0 = 0x10325476;
            int full = data.Length / 64 * 64;
            var block = new uint[16];
            for (int off = 0; off < full; off += 64)
            {
                for (int i = 0; i < 16; i++) block[i] = BitConverter.ToUInt32(data.Slice(off + i * 4, 4));
                Round(block, ref a0, ref b0, ref c0, ref d0);
            }
            uint bits = (uint)data.Length * 8;
            int left = data.Length - full;
            var last = new byte[64];
            if (left >= 56)
            {
                data.Slice(full, left).CopyTo(last);
                last[left] = 0x80;
                for (int i = 0; i < 16; i++) block[i] = BitConverter.ToUInt32(last, i * 4);
                Round(block, ref a0, ref b0, ref c0, ref d0);
                Array.Clear(last);
                BitConverter.GetBytes(bits).CopyTo(last, 0);
                BitConverter.GetBytes((bits >> 2) | 1).CopyTo(last, 60);
            }
            else
            {
                BitConverter.GetBytes(bits).CopyTo(last, 0);
                data.Slice(full, left).CopyTo(last.AsSpan(4));
                last[4 + left] = 0x80;
                BitConverter.GetBytes((bits >> 2) | 1).CopyTo(last, 60);
            }
            for (int i = 0; i < 16; i++) block[i] = BitConverter.ToUInt32(last, i * 4);
            Round(block, ref a0, ref b0, ref c0, ref d0);
            var r = new byte[16];
            BitConverter.GetBytes(a0).CopyTo(r, 0); BitConverter.GetBytes(b0).CopyTo(r, 4);
            BitConverter.GetBytes(c0).CopyTo(r, 8); BitConverter.GetBytes(d0).CopyTo(r, 12);
            return r;
        }

        private static void Round(uint[] m, ref uint a0, ref uint b0, ref uint c0, ref uint d0)
        {
            uint a = a0, b = b0, c = c0, d = d0;
            for (int i = 0; i < 64; i++)
            {
                uint f; int g;
                if (i < 16) { f = (b & c) | (~b & d); g = i; }
                else if (i < 32) { f = (d & b) | (~d & c); g = (5 * i + 1) % 16; }
                else if (i < 48) { f = b ^ c ^ d; g = (3 * i + 5) % 16; }
                else { f = c ^ (b | ~d); g = 7 * i % 16; }
                uint t = d; d = c; c = b;
                b += RotL(a + f + K[i] + m[g], R[i]);
                a = t;
            }
            a0 += a; b0 += b; c0 += c; d0 += d;
        }

        private static uint RotL(uint x, int n) => (x << n) | (x >> (32 - n));
    }
}
