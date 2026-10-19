// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Reads a DXBC container -- what fxc emits and D3D11's Create*Shader take -- into its signatures and
// a decoded instruction list (Documentation/d3d11-webgpu-shim.md, section 6).
//
// SOURCE OF THE FORMAT: d3d11TokenizedProgramFormat.hpp from the Windows Driver Kit, the D3D11
// functional specification's chapters on the shader bytecode, and the reflection chunks as fxc writes
// them. Only what is needed to translate is kept: an operand is its register file, its index
// expressions, its component selection and its modifier.
//

using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc
{
    internal enum ShaderStage { Pixel = 0, Vertex = 1, Geometry = 2, Hull = 3, Domain = 4, Compute = 5 }

    internal enum OperandType
    {
        Temp = 0, Input = 1, Output = 2, IndexableTemp = 3, Immediate32 = 4, Immediate64 = 5, Sampler = 6, Resource = 7,
        ConstantBuffer = 8, ImmediateConstantBuffer = 9, Label = 10, InputPrimitiveId = 11, OutputDepth = 12, Null = 13,
        Rasterizer = 14, OutputCoverageMask = 15, Stream = 16, FunctionBody = 17, FunctionTable = 18, Interface = 19,
        FunctionInput = 20, FunctionOutput = 21, OutputControlPointId = 22, InputForkInstanceId = 23, InputJoinInstanceId = 24,
        InputControlPoint = 25, OutputControlPoint = 26, InputPatchConstant = 27, InputDomainPoint = 28, ThisPointer = 29,
        UnorderedAccessView = 30, ThreadGroupSharedMemory = 31, InputThreadId = 32, InputThreadGroupId = 33,
        InputThreadIdInGroup = 34, InputCoverageMask = 35, InputThreadIdInGroupFlattened = 36, InputGsInstanceId = 37,
        OutputDepthGreaterEqual = 38, OutputDepthLessEqual = 39, CycleCounter = 40,
    }

    internal enum OperandModifier { None = 0, Neg = 1, Abs = 2, AbsNeg = 3 }

    /// <summary>One index of an operand: an immediate, a register plus an immediate, or both.</summary>
    internal sealed class OperandIndex
    {
        public long Immediate;
        public Operand? Relative;          // a one-component operand added to Immediate
    }

    internal sealed class Operand
    {
        public OperandType Type;
        public int ComponentCount;         // 0, 1 or 4
        /// <summary>For a 4-component operand: the write mask (destination), or the swizzle, four
        /// 2-bit selectors packed little-endian (source). A select-1 operand is a swizzle of one
        /// component repeated.</summary>
        public int Mask = 0xF;
        public byte[] Swizzle = { 0, 1, 2, 3 };
        public bool IsMask;
        public OperandIndex[] Indices = Array.Empty<OperandIndex>();
        public OperandModifier Modifier;
        public uint[] Values = Array.Empty<uint>();      // immediates

        public int Index0 => Indices.Length > 0 ? (int)Indices[0].Immediate : 0;
        public int Index1 => Indices.Length > 1 ? (int)Indices[1].Immediate : 0;

        public override string ToString()
        {
            var sb = new StringBuilder(Type.ToString());
            foreach (var i in Indices) sb.Append('[').Append(i.Relative != null ? i.Relative + "+" : "").Append(i.Immediate).Append(']');
            if (IsMask) sb.Append(".m").Append(Mask);
            else if (ComponentCount == 4) sb.Append('.').Append("xyzw"[Swizzle[0]]).Append("xyzw"[Swizzle[1]]).Append("xyzw"[Swizzle[2]]).Append("xyzw"[Swizzle[3]]);
            return sb.ToString();
        }
    }

    internal sealed class Instruction
    {
        public Opcode Op;
        public uint Token;                 // the opcode token (its control bits)
        public bool Saturate;
        public bool TestNonZero;           // if / breakc / continuec / discard / retc / callc
        public int[] TexelOffset = { 0, 0, 0 };
        public Operand[] Operands = Array.Empty<Operand>();
        public uint[] Extra = Array.Empty<uint>();      // a declaration's literal dwords
        public int ResourceDimension;      // dcl_resource
        public uint ReturnType;            // dcl_resource: 4 bits per component
        public override string ToString() => Op + " " + string.Join(", ", (IEnumerable<Operand>)Operands);
    }

    internal enum Opcode
    {
        Add = 0, And = 1, Break = 2, Breakc = 3, Call = 4, Callc = 5, Case = 6, Continue = 7, Continuec = 8, Cut = 9,
        Default = 10, DerivRtx = 11, DerivRty = 12, Discard = 13, Div = 14, Dp2 = 15, Dp3 = 16, Dp4 = 17, Else = 18,
        Emit = 19, EmitThenCut = 20, EndIf = 21, EndLoop = 22, EndSwitch = 23, Eq = 24, Exp = 25, Frc = 26, Ftoi = 27,
        Ftou = 28, Ge = 29, IAdd = 30, If = 31, IEq = 32, IGe = 33, ILt = 34, IMad = 35, IMax = 36, IMin = 37, IMul = 38,
        INe = 39, INeg = 40, IShl = 41, IShr = 42, Itof = 43, Label = 44, Ld = 45, LdMs = 46, Log = 47, Loop = 48, Lt = 49,
        Mad = 50, Min = 51, Max = 52, CustomData = 53, Mov = 54, Movc = 55, Mul = 56, Ne = 57, Nop = 58, Not = 59, Or = 60,
        ResInfo = 61, Ret = 62, Retc = 63, RoundNe = 64, RoundNi = 65, RoundPi = 66, RoundZ = 67, Rsq = 68, Sample = 69,
        SampleC = 70, SampleCLz = 71, SampleL = 72, SampleD = 73, SampleB = 74, Sqrt = 75, Switch = 76, SinCos = 77,
        UDiv = 78, ULt = 79, UGe = 80, UMul = 81, UMad = 82, UMax = 83, UMin = 84, UShr = 85, Utof = 86, Xor = 87,
        DclResource = 88, DclConstantBuffer = 89, DclSampler = 90, DclIndexRange = 91, DclGsOutputTopology = 92,
        DclGsInputPrimitive = 93, DclMaxOutputVertexCount = 94, DclInput = 95, DclInputSgv = 96, DclInputSiv = 97,
        DclInputPs = 98, DclInputPsSgv = 99, DclInputPsSiv = 100, DclOutput = 101, DclOutputSgv = 102, DclOutputSiv = 103,
        DclTemps = 104, DclIndexableTemp = 105, DclGlobalFlags = 106,
        // Shader model 4.1 and 5
        Lod = 108, Gather4 = 109, SamplePos = 110, SampleInfo = 111,
        EmitStream = 117, CutStream = 118, EmitThenCutStream = 119,
        DerivRtxCoarse = 122, DerivRtxFine = 123, DerivRtyCoarse = 124, DerivRtyFine = 125, Gather4C = 126, Gather4Po = 127, Gather4PoC = 128,
        Rcp = 129, F32toF16 = 130, F16toF32 = 131, UAddc = 132, USubb = 133, CountBits = 134, FirstBitHi = 135, FirstBitLo = 136,
        FirstBitShi = 137, UBfe = 138, IBfe = 139, Bfi = 140, BfRev = 141, Swapc = 142,
        DclStream = 143, DclFunctionBody = 144, DclFunctionTable = 145, DclInterface = 146,
        DclInputControlPointCount = 147, DclOutputControlPointCount = 148, DclTessDomain = 149, DclTessPartitioning = 150,
        DclTessOutputPrimitive = 151, DclHsMaxTessFactor = 152, DclHsForkPhaseInstanceCount = 153, DclHsJoinPhaseInstanceCount = 154,
        DclThreadGroup = 155, DclUnorderedAccessViewTyped = 156, DclUnorderedAccessViewRaw = 157, DclUnorderedAccessViewStructured = 158,
        DclThreadGroupSharedMemoryRaw = 159, DclThreadGroupSharedMemoryStructured = 160, DclResourceRaw = 161, DclResourceStructured = 162,
        LdUavTyped = 163, StoreUavTyped = 164, LdRaw = 165, StoreRaw = 166, LdStructured = 167, StoreStructured = 168,
        AtomicAnd = 169, AtomicOr = 170, AtomicXor = 171, AtomicCmpStore = 172, AtomicIAdd = 173, AtomicIMax = 174,
        AtomicIMin = 175, AtomicUMax = 176, AtomicUMin = 177, ImmAtomicAlloc = 178, ImmAtomicConsume = 179,
        ImmAtomicIAdd = 180, ImmAtomicAnd = 181, ImmAtomicOr = 182, ImmAtomicXor = 183, ImmAtomicExch = 184,
        ImmAtomicCmpExch = 185, ImmAtomicIMax = 186, ImmAtomicIMin = 187, ImmAtomicUMax = 188, ImmAtomicUMin = 189,
        Sync = 190, DAdd = 191, DMax = 192, DMin = 193, DMul = 194, DEq = 195, DGe = 196, DLt = 197, DNe = 198, DMov = 199,
        DMovc = 200, DtoF = 201, FtoD = 202, EvalSnapped = 203, EvalSampleIndex = 204, EvalCentroid = 205, DclGsInstanceCount = 206,
    }

    /// <summary>An element of an input or output signature.</summary>
    internal sealed class SignatureElement
    {
        public string SemanticName = "";
        public int SemanticIndex;
        public int SystemValue;            // D3D_NAME: 0 undefined, 1 position, 6 vertex id, 8 instance id, 9 is-front-face, 64 target, 65 depth
        public int ComponentType;          // 1 uint32, 2 sint32, 3 float32
        public int Register;
        public int Mask;
        public int ReadWriteMask;
        public int Stream;
        public override string ToString() => $"{SemanticName}{SemanticIndex} r{Register} m{Mask} sv{SystemValue} t{ComponentType}";
    }

    internal sealed class DxbcShader
    {
        public ShaderStage Stage;
        public int Major, Minor;
        public readonly List<SignatureElement> Inputs = new();
        public readonly List<SignatureElement> Outputs = new();
        public readonly List<Instruction> Instructions = new();
        public uint[] ImmediateConstantBuffer = Array.Empty<uint>();
        public byte[] Bytecode = Array.Empty<byte>();
        public ulong Hash;

        public static DxbcShader Parse(ReadOnlySpan<byte> blob)
        {
            if (blob.Length < 32 || blob[0] != 'D' || blob[1] != 'X' || blob[2] != 'B' || blob[3] != 'C')
                throw new FormatException("not a DXBC container");
            var s = new DxbcShader { Bytecode = blob.ToArray() };
            s.Hash = HashOf(blob);
            int chunks = BitConverter.ToInt32(blob[28..]);
            bool code = false;
            for (int i = 0; i < chunks; i++)
            {
                int off = BitConverter.ToInt32(blob[(32 + 4 * i)..]);
                string tag = Encoding.ASCII.GetString(blob.Slice(off, 4));
                int len = BitConverter.ToInt32(blob[(off + 4)..]);
                ReadOnlySpan<byte> data = blob.Slice(off + 8, len);
                switch (tag)
                {
                    case "ISGN": ReadSignature(data, s.Inputs, false, false); break;
                    case "ISG1": ReadSignature(data, s.Inputs, false, true); break;
                    case "OSGN": ReadSignature(data, s.Outputs, false, false); break;
                    case "OSG5": ReadSignature(data, s.Outputs, true, false); break;
                    case "OSG1": ReadSignature(data, s.Outputs, true, true); break;
                    case "SHDR":
                    case "SHEX":
                        s.ReadCode(data);
                        code = true;
                        break;
                }
            }
            if (!code) throw new FormatException("DXBC without a SHDR/SHEX chunk");
            return s;
        }

        public static ulong HashOf(ReadOnlySpan<byte> b)
        {
            ulong h = 0xcbf29ce484222325UL;
            foreach (byte x in b) { h ^= x; h *= 0x100000001b3UL; }
            return h ^ (ulong)b.Length;
        }

        private static void ReadSignature(ReadOnlySpan<byte> data, List<SignatureElement> into, bool stream, bool minPrecision)
        {
            int count = BitConverter.ToInt32(data);
            int stride = (stream ? 28 : 24) + (minPrecision ? 4 : 0);
            for (int i = 0; i < count; i++)
            {
                int p = 8 + i * stride;
                var e = new SignatureElement();
                if (stream) { e.Stream = BitConverter.ToInt32(data[p..]); p += 4; }
                int nameOff = BitConverter.ToInt32(data[p..]);
                e.SemanticIndex = BitConverter.ToInt32(data[(p + 4)..]);
                e.SystemValue = BitConverter.ToInt32(data[(p + 8)..]);
                e.ComponentType = BitConverter.ToInt32(data[(p + 12)..]);
                e.Register = BitConverter.ToInt32(data[(p + 16)..]);
                e.Mask = data[p + 20];
                e.ReadWriteMask = data[p + 21];
                int end = nameOff;
                while (end < data.Length && data[end] != 0) end++;
                e.SemanticName = Encoding.ASCII.GetString(data[nameOff..end]);
                into.Add(e);
            }
        }

        private void ReadCode(ReadOnlySpan<byte> data)
        {
            uint version = BitConverter.ToUInt32(data);
            Stage = (ShaderStage)(version >> 16);
            Major = (int)((version >> 4) & 0xF);
            Minor = (int)(version & 0xF);
            int words = BitConverter.ToInt32(data[4..]);
            var t = new uint[words];
            for (int i = 0; i < words; i++) t[i] = BitConverter.ToUInt32(data[(i * 4)..]);
            int pos = 2;
            while (pos < words)
            {
                uint tok = t[pos];
                var op = (Opcode)(tok & 0x7FF);
                int length = (int)((tok >> 24) & 0x7F);
                if (op == Opcode.CustomData)
                {
                    length = (int)t[pos + 1];
                    int cls = (int)(tok >> 11);
                    if (cls == 3)                          // immediate constant buffer
                    {
                        var icb = new uint[length - 2];
                        Array.Copy(t, pos + 2, icb, 0, icb.Length);
                        ImmediateConstantBuffer = icb;
                    }
                    pos += Math.Max(length, 2);
                    continue;
                }
                if (length == 0) throw new FormatException($"zero-length instruction {op} at {pos}");
                Instructions.Add(Decode(t, pos, length));
                pos += length;
            }
        }

        private static Instruction Decode(uint[] t, int start, int length)
        {
            uint tok = t[start];
            var ins = new Instruction { Op = (Opcode)(tok & 0x7FF), Token = tok };
            ins.Saturate = ((tok >> 13) & 1) != 0;
            ins.TestNonZero = ((tok >> 18) & 1) != 0;
            int p = start + 1, end = start + length;
            bool extended = (tok & 0x80000000) != 0;
            while (extended && p < end)
            {
                uint ext = t[p++];
                extended = (ext & 0x80000000) != 0;
                if ((ext & 0x3F) == 1)                     // sample controls: aoffimmi
                {
                    ins.TexelOffset[0] = SignExtend4((int)(ext >> 9) & 0xF);
                    ins.TexelOffset[1] = SignExtend4((int)(ext >> 13) & 0xF);
                    ins.TexelOffset[2] = SignExtend4((int)(ext >> 17) & 0xF);
                }
            }
            var ops = new List<Operand>();
            switch (ins.Op)
            {
                case Opcode.DclTemps:
                case Opcode.DclMaxOutputVertexCount:
                case Opcode.DclGsInstanceCount:
                case Opcode.DclInputControlPointCount:
                case Opcode.DclOutputControlPointCount:
                case Opcode.DclHsForkPhaseInstanceCount:
                case Opcode.DclHsJoinPhaseInstanceCount:
                    ins.Extra = Slice(t, p, end);
                    break;
                case Opcode.DclIndexableTemp:
                case Opcode.DclThreadGroup:
                    ins.Extra = Slice(t, p, end);
                    break;
                case Opcode.DclGlobalFlags:
                case Opcode.DclGsInputPrimitive:
                case Opcode.DclGsOutputTopology:
                case Opcode.DclTessDomain:
                case Opcode.DclTessPartitioning:
                case Opcode.DclTessOutputPrimitive:
                    break;
                case Opcode.DclResource:
                    ins.ResourceDimension = (int)((tok >> 11) & 0x1F);
                    ops.Add(ReadOperand(t, ref p));
                    ins.ReturnType = p < end ? t[p++] : 0x5555;
                    break;
                case Opcode.DclConstantBuffer:
                case Opcode.DclSampler:
                case Opcode.DclInput:
                case Opcode.DclInputPs:
                case Opcode.DclOutput:
                case Opcode.DclIndexRange:
                case Opcode.DclResourceRaw:
                case Opcode.DclUnorderedAccessViewRaw:
                case Opcode.DclStream:
                    ops.Add(ReadOperand(t, ref p));
                    ins.Extra = Slice(t, p, end);
                    break;
                case Opcode.DclInputSgv:
                case Opcode.DclInputSiv:
                case Opcode.DclInputPsSgv:
                case Opcode.DclInputPsSiv:
                case Opcode.DclOutputSgv:
                case Opcode.DclOutputSiv:
                    ops.Add(ReadOperand(t, ref p));
                    ins.Extra = Slice(t, p, end);          // the system-value name
                    break;
                case Opcode.DclResourceStructured:
                case Opcode.DclUnorderedAccessViewTyped:
                case Opcode.DclUnorderedAccessViewStructured:
                case Opcode.DclThreadGroupSharedMemoryRaw:
                case Opcode.DclThreadGroupSharedMemoryStructured:
                    ops.Add(ReadOperand(t, ref p));
                    ins.Extra = Slice(t, p, end);
                    break;
                case Opcode.DclFunctionBody:
                case Opcode.DclFunctionTable:
                case Opcode.DclInterface:
                case Opcode.DclHsMaxTessFactor:
                    ins.Extra = Slice(t, p, end);
                    break;
                default:
                    while (p < end) ops.Add(ReadOperand(t, ref p));
                    break;
            }
            ins.Operands = ops.ToArray();
            return ins;
        }

        private static int SignExtend4(int v) => (v & 8) != 0 ? v - 16 : v;

        private static uint[] Slice(uint[] t, int from, int to)
        {
            if (to <= from) return Array.Empty<uint>();
            var r = new uint[to - from];
            Array.Copy(t, from, r, 0, r.Length);
            return r;
        }

        private static Operand ReadOperand(uint[] t, ref int p)
        {
            uint tok = t[p++];
            var o = new Operand();
            int nc = (int)(tok & 3);
            o.ComponentCount = nc switch { 0 => 0, 1 => 1, 2 => 4, _ => 4 };
            if (nc == 2)
            {
                int sel = (int)((tok >> 2) & 3);
                if (sel == 0) { o.IsMask = true; o.Mask = (int)((tok >> 4) & 0xF); }
                else if (sel == 1)
                {
                    o.Swizzle = new[] { (byte)((tok >> 4) & 3), (byte)((tok >> 6) & 3), (byte)((tok >> 8) & 3), (byte)((tok >> 10) & 3) };
                }
                else
                {
                    byte c = (byte)((tok >> 4) & 3);
                    o.Swizzle = new[] { c, c, c, c };
                }
            }
            else if (nc == 1)
            {
                o.Swizzle = new byte[] { 0, 0, 0, 0 };
            }
            o.Type = (OperandType)((tok >> 12) & 0xFF);
            int dims = (int)((tok >> 20) & 3);
            var reps = new[] { (int)((tok >> 22) & 7), (int)((tok >> 25) & 7), (int)((tok >> 28) & 7) };
            bool extended = (tok & 0x80000000) != 0;
            while (extended)
            {
                uint ext = t[p++];
                extended = (ext & 0x80000000) != 0;
                if ((ext & 0x3F) == 1) o.Modifier = (OperandModifier)((ext >> 6) & 0xFF);
            }
            if (o.Type == OperandType.Immediate32)
            {
                int n = o.ComponentCount == 4 ? 4 : 1;
                o.Values = new uint[n];
                for (int i = 0; i < n; i++) o.Values[i] = t[p++];
                return o;
            }
            if (o.Type == OperandType.Immediate64)
            {
                int n = o.ComponentCount == 4 ? 4 : 2;
                o.Values = new uint[n * 2];
                for (int i = 0; i < n * 2; i++) o.Values[i] = t[p++];
                return o;
            }
            o.Indices = new OperandIndex[dims];
            for (int d = 0; d < dims; d++)
            {
                var ix = new OperandIndex();
                switch (reps[d])
                {
                    case 0: ix.Immediate = t[p++]; break;
                    case 1: ix.Immediate = (long)(t[p] | ((ulong)t[p + 1] << 32)); p += 2; break;
                    case 2: ix.Relative = ReadOperand(t, ref p); break;
                    case 3: ix.Immediate = t[p++]; ix.Relative = ReadOperand(t, ref p); break;
                    case 4: ix.Immediate = (long)(t[p] | ((ulong)t[p + 1] << 32)); p += 2; ix.Relative = ReadOperand(t, ref p); break;
                }
                o.Indices[d] = ix;
            }
            return o;
        }
    }
}
