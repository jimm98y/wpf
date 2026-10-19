// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Every opcode of the SM4.0 / 4.1 / 5.0 / 5.1 token format (d3d11TokenizedProgramFormat.hpp), with
// what the decoder and the coverage report need to know of it: its class, how many of its leading
// operands are destinations, and how its operands are typed (Documentation/d3d11-webgpu-shim.md,
// section 9). One row per opcode, so an opcode cannot go unnoticed: the decoder reads any of them,
// and an opcode the translator has no emitter for fails the translation by name.
//

using System;
using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc
{
    internal enum OpClass
    {
        Float, Int, UInt, Bit, Convert, Double, Flow, Texture, Uav, Atomic, Declaration, Stream, Sync, HullPhase, Other,
    }

    internal readonly record struct OpInfo(Opcode Op, string Name, OpClass Class, int Destinations);

    internal static class DxbcOpcodes
    {
        private static readonly Dictionary<Opcode, OpInfo> s_table = Build();

        public static OpInfo Info(Opcode op) => s_table.TryGetValue(op, out var i) ? i : new OpInfo(op, $"opcode{(int)op}", OpClass.Other, 0);

        public static IEnumerable<OpInfo> All => s_table.Values;

        public static bool IsKnown(Opcode op) => s_table.ContainsKey(op);

        private static Dictionary<Opcode, OpInfo> Build()
        {
            var t = new Dictionary<Opcode, OpInfo>();
            void A(Opcode op, string name, OpClass c, int dests = 1) => t[op] = new OpInfo(op, name, c, dests);
            const OpClass F = OpClass.Float, I = OpClass.Int, U = OpClass.UInt, B = OpClass.Bit, C = OpClass.Convert, D = OpClass.Double,
                Fl = OpClass.Flow, Tx = OpClass.Texture, Uv = OpClass.Uav, At = OpClass.Atomic, Dc = OpClass.Declaration, St = OpClass.Stream,
                Sy = OpClass.Sync, Hp = OpClass.HullPhase, O = OpClass.Other;

            // SM4.0
            A(Opcode.Add, "add", F); A(Opcode.And, "and", B); A(Opcode.Break, "break", Fl, 0); A(Opcode.Breakc, "breakc", Fl, 0);
            A(Opcode.Call, "call", Fl, 0); A(Opcode.Callc, "callc", Fl, 0); A(Opcode.Case, "case", Fl, 0); A(Opcode.Continue, "continue", Fl, 0);
            A(Opcode.Continuec, "continuec", Fl, 0); A(Opcode.Cut, "cut", St, 0); A(Opcode.Default, "default", Fl, 0);
            A(Opcode.DerivRtx, "deriv_rtx", F); A(Opcode.DerivRty, "deriv_rty", F); A(Opcode.Discard, "discard", Fl, 0);
            A(Opcode.Div, "div", F); A(Opcode.Dp2, "dp2", F); A(Opcode.Dp3, "dp3", F); A(Opcode.Dp4, "dp4", F); A(Opcode.Else, "else", Fl, 0);
            A(Opcode.Emit, "emit", St, 0); A(Opcode.EmitThenCut, "emit_then_cut", St, 0); A(Opcode.EndIf, "endif", Fl, 0);
            A(Opcode.EndLoop, "endloop", Fl, 0); A(Opcode.EndSwitch, "endswitch", Fl, 0); A(Opcode.Eq, "eq", F); A(Opcode.Exp, "exp", F);
            A(Opcode.Frc, "frc", F); A(Opcode.Ftoi, "ftoi", C); A(Opcode.Ftou, "ftou", C); A(Opcode.Ge, "ge", F); A(Opcode.IAdd, "iadd", I);
            A(Opcode.If, "if", Fl, 0); A(Opcode.IEq, "ieq", I); A(Opcode.IGe, "ige", I); A(Opcode.ILt, "ilt", I); A(Opcode.IMad, "imad", I);
            A(Opcode.IMax, "imax", I); A(Opcode.IMin, "imin", I); A(Opcode.IMul, "imul", I, 2); A(Opcode.INe, "ine", I); A(Opcode.INeg, "ineg", I);
            A(Opcode.IShl, "ishl", I); A(Opcode.IShr, "ishr", I); A(Opcode.Itof, "itof", C); A(Opcode.Label, "label", Fl, 0);
            A(Opcode.Ld, "ld", Tx); A(Opcode.LdMs, "ld2dms", Tx); A(Opcode.Log, "log", F); A(Opcode.Loop, "loop", Fl, 0); A(Opcode.Lt, "lt", F);
            A(Opcode.Mad, "mad", F); A(Opcode.Min, "min", F); A(Opcode.Max, "max", F); A(Opcode.CustomData, "customdata", O, 0);
            A(Opcode.Mov, "mov", F); A(Opcode.Movc, "movc", F); A(Opcode.Mul, "mul", F); A(Opcode.Ne, "ne", F); A(Opcode.Nop, "nop", O, 0);
            A(Opcode.Not, "not", B); A(Opcode.Or, "or", B); A(Opcode.ResInfo, "resinfo", Tx); A(Opcode.Ret, "ret", Fl, 0); A(Opcode.Retc, "retc", Fl, 0);
            A(Opcode.RoundNe, "round_ne", F); A(Opcode.RoundNi, "round_ni", F); A(Opcode.RoundPi, "round_pi", F); A(Opcode.RoundZ, "round_z", F);
            A(Opcode.Rsq, "rsq", F); A(Opcode.Sample, "sample", Tx); A(Opcode.SampleC, "sample_c", Tx); A(Opcode.SampleCLz, "sample_c_lz", Tx);
            A(Opcode.SampleL, "sample_l", Tx); A(Opcode.SampleD, "sample_d", Tx); A(Opcode.SampleB, "sample_b", Tx); A(Opcode.Sqrt, "sqrt", F);
            A(Opcode.Switch, "switch", Fl, 0); A(Opcode.SinCos, "sincos", F, 2); A(Opcode.UDiv, "udiv", U, 2); A(Opcode.ULt, "ult", U);
            A(Opcode.UGe, "uge", U); A(Opcode.UMul, "umul", U, 2); A(Opcode.UMad, "umad", U); A(Opcode.UMax, "umax", U); A(Opcode.UMin, "umin", U);
            A(Opcode.UShr, "ushr", U); A(Opcode.Utof, "utof", C); A(Opcode.Xor, "xor", B);
            A(Opcode.DclResource, "dcl_resource", Dc, 0); A(Opcode.DclConstantBuffer, "dcl_constantbuffer", Dc, 0); A(Opcode.DclSampler, "dcl_sampler", Dc, 0);
            A(Opcode.DclIndexRange, "dcl_indexrange", Dc, 0); A(Opcode.DclGsOutputTopology, "dcl_outputtopology", Dc, 0);
            A(Opcode.DclGsInputPrimitive, "dcl_inputprimitive", Dc, 0); A(Opcode.DclMaxOutputVertexCount, "dcl_maxout", Dc, 0);
            A(Opcode.DclInput, "dcl_input", Dc, 0); A(Opcode.DclInputSgv, "dcl_input_sgv", Dc, 0); A(Opcode.DclInputSiv, "dcl_input_siv", Dc, 0);
            A(Opcode.DclInputPs, "dcl_input_ps", Dc, 0); A(Opcode.DclInputPsSgv, "dcl_input_ps_sgv", Dc, 0); A(Opcode.DclInputPsSiv, "dcl_input_ps_siv", Dc, 0);
            A(Opcode.DclOutput, "dcl_output", Dc, 0); A(Opcode.DclOutputSgv, "dcl_output_sgv", Dc, 0); A(Opcode.DclOutputSiv, "dcl_output_siv", Dc, 0);
            A(Opcode.DclTemps, "dcl_temps", Dc, 0); A(Opcode.DclIndexableTemp, "dcl_indexableTemp", Dc, 0); A(Opcode.DclGlobalFlags, "dcl_globalFlags", Dc, 0);
            // SM4.1
            A(Opcode.Lod, "lod", Tx); A(Opcode.Gather4, "gather4", Tx); A(Opcode.SamplePos, "samplepos", Tx); A(Opcode.SampleInfo, "sampleinfo", Tx);
            // SM5.0
            A(Opcode.HsDecls, "hs_decls", Hp, 0); A(Opcode.HsControlPointPhase, "hs_control_point_phase", Hp, 0);
            A(Opcode.HsForkPhase, "hs_fork_phase", Hp, 0); A(Opcode.HsJoinPhase, "hs_join_phase", Hp, 0);
            A(Opcode.EmitStream, "emit_stream", St, 0); A(Opcode.CutStream, "cut_stream", St, 0); A(Opcode.EmitThenCutStream, "emitThenCut_stream", St, 0);
            A(Opcode.InterfaceCall, "fcall", Fl, 0); A(Opcode.BufInfo, "bufinfo", Tx);
            A(Opcode.DerivRtxCoarse, "deriv_rtx_coarse", F); A(Opcode.DerivRtxFine, "deriv_rtx_fine", F);
            A(Opcode.DerivRtyCoarse, "deriv_rty_coarse", F); A(Opcode.DerivRtyFine, "deriv_rty_fine", F);
            A(Opcode.Gather4C, "gather4_c", Tx); A(Opcode.Gather4Po, "gather4_po", Tx); A(Opcode.Gather4PoC, "gather4_po_c", Tx);
            A(Opcode.Rcp, "rcp", F); A(Opcode.F32toF16, "f32tof16", C); A(Opcode.F16toF32, "f16tof32", C);
            A(Opcode.UAddc, "uaddc", U, 2); A(Opcode.USubb, "usubb", U, 2); A(Opcode.CountBits, "countbits", B);
            A(Opcode.FirstBitHi, "firstbit_hi", B); A(Opcode.FirstBitLo, "firstbit_lo", B); A(Opcode.FirstBitShi, "firstbit_shi", B);
            A(Opcode.UBfe, "ubfe", B); A(Opcode.IBfe, "ibfe", B); A(Opcode.Bfi, "bfi", B); A(Opcode.BfRev, "bfrev", B); A(Opcode.Swapc, "swapc", F, 2);
            A(Opcode.DclStream, "dcl_stream", Dc, 0); A(Opcode.DclFunctionBody, "dcl_function_body", Dc, 0);
            A(Opcode.DclFunctionTable, "dcl_function_table", Dc, 0); A(Opcode.DclInterface, "dcl_interface", Dc, 0);
            A(Opcode.DclInputControlPointCount, "dcl_input_control_point_count", Dc, 0);
            A(Opcode.DclOutputControlPointCount, "dcl_output_control_point_count", Dc, 0);
            A(Opcode.DclTessDomain, "dcl_tessellator_domain", Dc, 0); A(Opcode.DclTessPartitioning, "dcl_tessellator_partitioning", Dc, 0);
            A(Opcode.DclTessOutputPrimitive, "dcl_tessellator_output_primitive", Dc, 0); A(Opcode.DclHsMaxTessFactor, "dcl_hs_max_tessfactor", Dc, 0);
            A(Opcode.DclHsForkPhaseInstanceCount, "dcl_hs_fork_phase_instance_count", Dc, 0);
            A(Opcode.DclHsJoinPhaseInstanceCount, "dcl_hs_join_phase_instance_count", Dc, 0);
            A(Opcode.DclThreadGroup, "dcl_thread_group", Dc, 0); A(Opcode.DclUnorderedAccessViewTyped, "dcl_uav_typed", Dc, 0);
            A(Opcode.DclUnorderedAccessViewRaw, "dcl_uav_raw", Dc, 0); A(Opcode.DclUnorderedAccessViewStructured, "dcl_uav_structured", Dc, 0);
            A(Opcode.DclThreadGroupSharedMemoryRaw, "dcl_tgsm_raw", Dc, 0); A(Opcode.DclThreadGroupSharedMemoryStructured, "dcl_tgsm_structured", Dc, 0);
            A(Opcode.DclResourceRaw, "dcl_resource_raw", Dc, 0); A(Opcode.DclResourceStructured, "dcl_resource_structured", Dc, 0);
            A(Opcode.LdUavTyped, "ld_uav_typed", Uv); A(Opcode.StoreUavTyped, "store_uav_typed", Uv); A(Opcode.LdRaw, "ld_raw", Uv);
            A(Opcode.StoreRaw, "store_raw", Uv); A(Opcode.LdStructured, "ld_structured", Uv); A(Opcode.StoreStructured, "store_structured", Uv);
            A(Opcode.AtomicAnd, "atomic_and", At); A(Opcode.AtomicOr, "atomic_or", At); A(Opcode.AtomicXor, "atomic_xor", At);
            A(Opcode.AtomicCmpStore, "atomic_cmp_store", At); A(Opcode.AtomicIAdd, "atomic_iadd", At); A(Opcode.AtomicIMax, "atomic_imax", At);
            A(Opcode.AtomicIMin, "atomic_imin", At); A(Opcode.AtomicUMax, "atomic_umax", At); A(Opcode.AtomicUMin, "atomic_umin", At);
            A(Opcode.ImmAtomicAlloc, "imm_atomic_alloc", At); A(Opcode.ImmAtomicConsume, "imm_atomic_consume", At);
            A(Opcode.ImmAtomicIAdd, "imm_atomic_iadd", At); A(Opcode.ImmAtomicAnd, "imm_atomic_and", At); A(Opcode.ImmAtomicOr, "imm_atomic_or", At);
            A(Opcode.ImmAtomicXor, "imm_atomic_xor", At); A(Opcode.ImmAtomicExch, "imm_atomic_exch", At); A(Opcode.ImmAtomicCmpExch, "imm_atomic_cmp_exch", At);
            A(Opcode.ImmAtomicIMax, "imm_atomic_imax", At); A(Opcode.ImmAtomicIMin, "imm_atomic_imin", At);
            A(Opcode.ImmAtomicUMax, "imm_atomic_umax", At); A(Opcode.ImmAtomicUMin, "imm_atomic_umin", At);
            A(Opcode.Sync, "sync", Sy, 0);
            A(Opcode.DAdd, "dadd", D); A(Opcode.DMax, "dmax", D); A(Opcode.DMin, "dmin", D); A(Opcode.DMul, "dmul", D); A(Opcode.DEq, "deq", D);
            A(Opcode.DGe, "dge", D); A(Opcode.DLt, "dlt", D); A(Opcode.DNe, "dne", D); A(Opcode.DMov, "dmov", D); A(Opcode.DMovc, "dmovc", D);
            A(Opcode.DtoF, "dtof", D); A(Opcode.FtoD, "ftod", D);
            A(Opcode.EvalSnapped, "eval_snapped", F); A(Opcode.EvalSampleIndex, "eval_sample_index", F); A(Opcode.EvalCentroid, "eval_centroid", F);
            A(Opcode.DclGsInstanceCount, "dcl_gsinstances", Dc, 0);
            A(Opcode.Abort, "abort", O, 0); A(Opcode.DebugBreak, "debug_break", O, 0);
            // SM5.0 with the 11.1 runtime (doubles extensions, msad)
            A(Opcode.DDiv, "ddiv", D); A(Opcode.DFma, "dfma", D); A(Opcode.DRcp, "drcp", D); A(Opcode.Msad, "msad", U);
            A(Opcode.DtoI, "dtoi", D); A(Opcode.DtoU, "dtou", D); A(Opcode.ItoD, "itod", D); A(Opcode.UtoD, "utod", D);
            // WDDM 1.3: tiled-resource feedback variants (a residency code as a second destination)
            A(Opcode.Gather4Feedback, "gather4_s", Tx, 2); A(Opcode.Gather4CFeedback, "gather4_c_s", Tx, 2);
            A(Opcode.Gather4PoFeedback, "gather4_po_s", Tx, 2); A(Opcode.Gather4PoCFeedback, "gather4_po_c_s", Tx, 2);
            A(Opcode.LdFeedback, "ld_s", Tx, 2); A(Opcode.LdMsFeedback, "ld2dms_s", Tx, 2); A(Opcode.LdUavTypedFeedback, "ld_uav_typed_s", Uv, 2);
            A(Opcode.LdRawFeedback, "ld_raw_s", Uv, 2); A(Opcode.LdStructuredFeedback, "ld_structured_s", Uv, 2);
            A(Opcode.SampleLFeedback, "sample_l_s", Tx, 2); A(Opcode.SampleCLzFeedback, "sample_c_lz_s", Tx, 2);
            A(Opcode.SampleClampFeedback, "sample_cl_s", Tx, 2); A(Opcode.SampleBClampFeedback, "sample_b_cl_s", Tx, 2);
            A(Opcode.SampleDClampFeedback, "sample_d_cl_s", Tx, 2); A(Opcode.SampleCClampFeedback, "sample_c_cl_s", Tx, 2);
            A(Opcode.CheckAccessFullyMapped, "check_access_fully_mapped", B);
            return t;
        }
    }
}
