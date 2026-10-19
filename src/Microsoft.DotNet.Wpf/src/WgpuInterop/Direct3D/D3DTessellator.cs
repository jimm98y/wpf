// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Direct3D 11 fixed-function tessellator: a port of Microsoft's reference implementation
// (CHWTessellator in tessellator.cpp, as Mesa carries it in src/gallium/auxiliary/tessellator),
// which is published under this notice:
//
//     Copyright (c) Microsoft Corporation
//
//     Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
//     associated documentation files (the "Software"), to deal in the Software without restriction,
//     including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
//     and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
//     subject to the following conditions:
//
//     The above copyright notice and this permission notice shall be included in all copies or substantial
//     portions of the Software.
//
//     THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
//     NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
//     IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
//     WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
//     SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
// The port keeps the reference's arithmetic exactly: 16.16 unsigned fixed point for every location
// (FXP is an unsigned int there, and its wrap-arounds are kept), the same point order and the same
// triangulation, so a patch tessellates here into what a D3D11 device makes of it.
//

using System;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal enum TessPartitioning { Integer = 0, Pow2 = 1, FractionalOdd = 2, FractionalEven = 3 }
    internal enum TessOutput { Point = 0, Line = 1, TriangleCw = 2, TriangleCcw = 3 }

    internal sealed class D3DTessellator
    {
        private const int MinOdd = 1, MaxOdd = 63, MinEven = 2, MaxEven = 64, MinIsolineDensity = 1, MaxIsolineDensity = 64, MaxFactor = 64;
        public const int MaxPoints = (MaxFactor + 1) * (MaxFactor + 1), MaxIndices = MaxFactor * MaxFactor * 2 * 3;

        private const int U = 0, V = 1, Ueq0 = 0, Veq0 = 1, Weq0 = 2, Ueq1 = 2, Veq1 = 3, QuadAxes = 2, QuadEdges = 4, TriEdges = 3;
        private const bool Even = false, OddParity = true;

        private const uint FxpFractionBits = 16, FxpFractionMask = 0x0000ffff, FxpIntegerMask = 0x7fff0000;
        private const uint FxpOne = 1u << 16, FxpOneThird = 0x00005555, FxpTwoThirds = 0x0000aaaa, FxpOneHalf = 0x00008000;
        private const float Epsilon = 0.0000152587890625f, MinOddPlusHalfEpsilon = MinOdd + Epsilon / 2;

        private static readonly uint[] s_fixedReciprocal =
        {
            0xffffffff,
            0x10000, 0x8000, 0x5555, 0x4000, 0x3333, 0x2aab, 0x2492, 0x2000,
            0x1c72, 0x199a, 0x1746, 0x1555, 0x13b1, 0x1249, 0x1111, 0x1000,
            0xf0f, 0xe39, 0xd79, 0xccd, 0xc31, 0xba3, 0xb21, 0xaab,
            0xa3d, 0x9d9, 0x97b, 0x925, 0x8d4, 0x889, 0x842, 0x800,
            0x7c2, 0x788, 0x750, 0x71c, 0x6eb, 0x6bd, 0x690, 0x666,
            0x63e, 0x618, 0x5f4, 0x5d1, 0x5b0, 0x591, 0x572, 0x555,
            0x539, 0x51f, 0x505, 0x4ec, 0x4d5, 0x4be, 0x4a8, 0x492,
            0x47e, 0x46a, 0x457, 0x444, 0x432, 0x421, 0x410, 0x400,
        };

        private bool _originalParity, _parity;                 // true: odd
        private TessPartitioning _originalPartitioning, _partitioning;
        private TessOutput _output;
        public readonly float[] PointsU = new float[MaxPoints], PointsV = new float[MaxPoints];
        public readonly int[] Indices = new int[MaxIndices];
        public int PointCount, IndexCount;

        // ---- D3D-compliant float helpers ---------------------------------------------------------

        private static bool IsNaN(float a) { int i = BitConverter.SingleToInt32Bits(a); return (i & 0x7f800000) == 0x7f800000 && (i & 0x007fffff) != 0; }

        private static float Flush(float a)
        {
            int i = BitConverter.SingleToInt32Bits(a);
            int b = i & 0x7fffffff;
            if (b < 0x00800000) return BitConverter.Int32BitsToSingle(unchecked((int)0x80000000) & i);
            return a;
        }

        private static float FMin(float a, float b)
        {
            float x = Flush(a), y = Flush(b);
            if (IsNaN(y)) return a;
            if (x == 0 && y == 0) return (BitConverter.SingleToInt32Bits(x) & unchecked((int)0x80000000)) != 0 ? a : b;
            return x < y ? a : b;
        }

        private static float FMax(float a, float b)
        {
            float x = Flush(a), y = Flush(b);
            if (IsNaN(y)) return a;
            if (x == 0 && y == 0) return (BitConverter.SingleToInt32Bits(y) & unchecked((int)0x80000000)) != 0 ? a : b;
            return x >= y ? a : b;
        }

        // ---- fixed point ---------------------------------------------------------------------------

        /// <summary>floatToIDotF&lt;15, 16, unsigned&gt;: float to 16.16 with round to nearest even, NaN 0, clamped.</summary>
        private static uint FloatToFixed(float input)
        {
            const int IBits = 15, FBits = 16, MantissaBits = 23, Bias = 127;
            const int HiddenBit = 1 << MantissaBits, MantissaMask = HiddenBit - 1, ExponentMask = 0xff << MantissaBits, SignBit = unchecked((int)0x80000000);
            const int MinResult = 0, MaxResult = ~MinResult;
            int maxPos = (Bias + IBits) << MantissaBits;
            int shift = MantissaBits + 1 - IBits - FBits;
            if (shift >= 0) maxPos -= 1 << shift;
            const int maxNeg = 0;

            int output = BitConverter.SingleToInt32Bits(input);
            int unbiased = ((output & ExponentMask) >> MantissaBits) - Bias;
            int negative = output & SignBit;
            if (unbiased == Bias + 1 && (output & MantissaMask) != 0) output = 0;
            else if (negative == 0 && output >= maxPos) output = MaxResult;
            else if (negative != 0 && (output & ~SignBit) >= maxNeg) output = MinResult;
            else if (unbiased < -FBits - 1) output = 0;
            else
            {
                output = (output & MantissaMask) | HiddenBit;
                int extra = MantissaBits - FBits - unbiased;
                if (extra >= 0)
                {
                    if (negative != 0) output = ~output + 1;
                    int lsb = 1 << extra, extraMask = lsb - 1, half = lsb >> 1;
                    if ((output & lsb) != 0 || (output & extraMask) > half) output += half;
                    output >>= extra;
                }
                else
                {
                    output <<= -extra;
                    if (negative != 0) output = ~output + 1;
                }
            }
            return unchecked((uint)output);
        }

        private static float FixedToFloat(uint input) => (float)(input >> (int)FxpFractionBits) + (float)(input & FxpFractionMask) / (1 << (int)FxpFractionBits);
        private static bool IsEven(float input) => (((int)input) & 1) == 0;
        private static uint FxpCeil(uint input) => (input & FxpFractionMask) != 0 ? (input & FxpIntegerMask) + FxpOne : input;
        private static uint FxpFloor(uint input) => input & FxpIntegerMask;

        // ---- state -----------------------------------------------------------------------------------

        private struct TessFactorContext
        {
            public uint InvNumSegmentsOnFloor, InvNumSegmentsOnCeil, HalfTessFactorFraction;
            public int NumHalfTessFactorPoints, SplitPointOnFloorHalfTessFactor;
        }

        private sealed class TriFactors
        {
            public readonly uint[] Outside = new uint[TriEdges];
            public uint Inside;
            public readonly bool[] OutsideParity = new bool[TriEdges];
            public bool InsideParity;
            public readonly TessFactorContext[] OutsideCtx = new TessFactorContext[TriEdges];
            public TessFactorContext InsideCtx;
            public bool JustDoMinimum, Culled;
            public readonly int[] NumPointsForOutsideEdge = new int[TriEdges];
            public int NumPointsForInside, InsideEdgePointBaseOffset;
        }

        private sealed class QuadFactors
        {
            public readonly uint[] Outside = new uint[QuadEdges];
            public readonly uint[] Inside = new uint[QuadAxes];
            public readonly bool[] OutsideParity = new bool[QuadEdges];
            public readonly bool[] InsideParity = new bool[QuadAxes];
            public readonly TessFactorContext[] OutsideCtx = new TessFactorContext[QuadEdges];
            public readonly TessFactorContext[] InsideCtx = new TessFactorContext[QuadAxes];
            public bool JustDoMinimum, Culled;
            public readonly int[] NumPointsForOutsideEdge = new int[QuadEdges];
            public readonly int[] NumPointsForInside = new int[QuadAxes];
            public int InsideEdgePointBaseOffset;
        }

        private sealed class IsoLineFactors
        {
            public bool DensityParity, DetailParity;
            public TessFactorContext DensityCtx, DetailCtx;
            public bool Culled;
            public int NumPointsPerLine, NumLines;
        }

        private bool Odd => _parity;
        private bool HWIntegerPartitioning => _partitioning is TessPartitioning.Integer or TessPartitioning.Pow2;

        public void Init(TessPartitioning partitioning, TessOutput output)
        {
            _partitioning = _originalPartitioning = partitioning;
            if (partitioning == TessPartitioning.FractionalOdd) _parity = OddParity;
            else if (partitioning == TessPartitioning.FractionalEven) _parity = Even;
            _originalParity = _parity;
            _output = output;
            PointCount = IndexCount = 0;
        }

        // ---- quad ------------------------------------------------------------------------------------

        public void TessellateQuadDomain(float ueq0, float veq0, float ueq1, float veq1, float insideU, float insideV)
        {
            var f = new QuadFactors();
            QuadProcessTessFactors(ueq0, veq0, ueq1, veq1, insideU, insideV, f);
            if (f.Culled) { PointCount = IndexCount = 0; return; }
            if (f.JustDoMinimum)
            {
                DefinePoint(0, 0, 0);
                DefinePoint(FxpOne, 0, 1);
                DefinePoint(FxpOne, FxpOne, 2);
                DefinePoint(0, FxpOne, 3);
                PointCount = 4;
                switch (_output)
                {
                    case TessOutput.TriangleCw: case TessOutput.TriangleCcw:
                        DefineClockwiseTriangle(0, 1, 3, 0);
                        DefineClockwiseTriangle(1, 2, 3, 3);
                        IndexCount = 6;
                        break;
                    case TessOutput.Point: DumpAllPoints(); break;
                    case TessOutput.Line: DumpAllPointsAsInOrderLineList(); break;
                }
                return;
            }
            QuadGeneratePoints(f);
            if (_output == TessOutput.Point) { DumpAllPoints(); return; }
            if (_output == TessOutput.Line) { DumpAllPointsAsInOrderLineList(); return; }
            QuadGenerateConnectivity(f);
        }

        private void Bounds(out float lower, out float upper)
        {
            switch (_originalPartitioning)
            {
                case TessPartitioning.FractionalEven: lower = MinEven; upper = MaxEven; break;
                case TessPartitioning.FractionalOdd: lower = MinOdd; upper = MaxOdd; break;
                default: lower = MinOdd; upper = MaxEven; break;
            }
        }

        private void QuadProcessTessFactors(float ueq0, float veq0, float ueq1, float veq1, float insideU, float insideV, QuadFactors f)
        {
            if (!(ueq0 > 0) || !(veq0 > 0) || !(ueq1 > 0) || !(veq1 > 0)) { f.Culled = true; return; }
            f.Culled = false;
            Bounds(out float lower, out float upper);
            ueq0 = FMin(upper, FMax(lower, ueq0));
            veq0 = FMin(upper, FMax(lower, veq0));
            ueq1 = FMin(upper, FMax(lower, ueq1));
            veq1 = FMin(upper, FMax(lower, veq1));
            if (HWIntegerPartitioning)
            {
                ueq0 = MathF.Ceiling(ueq0); veq0 = MathF.Ceiling(veq0); ueq1 = MathF.Ceiling(ueq1); veq1 = MathF.Ceiling(veq1);
            }
            if (_originalPartitioning == TessPartitioning.FractionalOdd)
            {
                if (ueq0 > MinOddPlusHalfEpsilon || veq0 > MinOddPlusHalfEpsilon || ueq1 > MinOddPlusHalfEpsilon || veq1 > MinOddPlusHalfEpsilon
                    || insideU > MinOddPlusHalfEpsilon || insideV > MinOddPlusHalfEpsilon)
                    lower = MinOdd + Epsilon;
            }
            insideU = FMin(upper, FMax(lower, insideU));
            insideV = FMin(upper, FMax(lower, insideV));
            if (HWIntegerPartitioning) { insideU = MathF.Ceiling(insideU); insideV = MathF.Ceiling(insideV); }

            PointCount = 0;
            IndexCount = 0;
            float[] outside = { ueq0, veq0, ueq1, veq1 };
            float[] inside = { insideU, insideV };
            if (HWIntegerPartitioning)
            {
                for (int e = 0; e < QuadEdges; e++) f.OutsideParity[e] = !IsEven(outside[e]);
                for (int a = 0; a < QuadAxes; a++) f.InsideParity[a] = !(IsEven(inside[a]) || inside[a] == 1.0f);
            }
            else
            {
                for (int e = 0; e < QuadEdges; e++) f.OutsideParity[e] = _originalParity;
                f.InsideParity[U] = f.InsideParity[V] = _originalParity;
            }
            for (int e = 0; e < QuadEdges; e++) f.Outside[e] = FloatToFixed(outside[e]);
            for (int a = 0; a < QuadAxes; a++) f.Inside[a] = FloatToFixed(inside[a]);
            if (HWIntegerPartitioning || Odd)
            {
                if (f.Inside[U] == FxpOne && f.Inside[V] == FxpOne && f.Outside[Ueq0] == FxpOne && f.Outside[Veq0] == FxpOne && f.Outside[Ueq1] == FxpOne && f.Outside[Veq1] == FxpOne)
                {
                    f.JustDoMinimum = true;
                    return;
                }
            }
            f.JustDoMinimum = false;
            for (int e = 0; e < QuadEdges; e++) { _parity = f.OutsideParity[e]; ComputeTessFactorContext(f.Outside[e], ref f.OutsideCtx[e]); }
            for (int a = 0; a < QuadAxes; a++) { _parity = f.InsideParity[a]; ComputeTessFactorContext(f.Inside[a], ref f.InsideCtx[a]); }
            for (int e = 0; e < QuadEdges; e++)
            {
                _parity = f.OutsideParity[e];
                f.NumPointsForOutsideEdge[e] = NumPointsForTessFactor(f.Outside[e]);
                PointCount += f.NumPointsForOutsideEdge[e];
            }
            PointCount -= 4;
            for (int a = 0; a < QuadAxes; a++)
            {
                _parity = f.InsideParity[a];
                f.NumPointsForInside[a] = NumPointsForTessFactor(f.Inside[a]);
                int min = f.InsideParity[a] == OddParity ? 4 : 3;
                f.NumPointsForInside[a] = Math.Max(min, f.NumPointsForInside[a]);
            }
            f.InsideEdgePointBaseOffset = PointCount;
            PointCount += (f.NumPointsForInside[U] - 2) * (f.NumPointsForInside[V] - 2);
        }

        private void QuadGeneratePoints(QuadFactors f)
        {
            int pointOffset = 0;
            for (int edge = 0; edge < QuadEdges; edge++)
            {
                int parity = edge & 1;
                int endPoint = f.NumPointsForOutsideEdge[edge] - 1;
                for (int p = 0; p < endPoint; p++, pointOffset++)
                {
                    int q = edge == 1 || edge == 2 ? p : endPoint - p;
                    _parity = f.OutsideParity[edge];
                    PlacePointIn1D(f.OutsideCtx[edge], q, out uint param);
                    if (parity != 0) DefinePoint(param, edge == 3 ? FxpOne : 0, pointOffset);
                    else DefinePoint(edge == 2 ? FxpOne : 0, param, pointOffset);
                }
            }
            const int startRing = 1;
            int minNum = Math.Min(f.NumPointsForInside[U], f.NumPointsForInside[V]);
            int numRings = minNum >> 1;
            for (int ring = startRing; ring < numRings; ring++)
            {
                int startPoint = ring;
                int[] endPoint = { f.NumPointsForInside[U] - 1 - startPoint, f.NumPointsForInside[V] - 1 - startPoint };
                for (int edge = 0; edge < QuadEdges; edge++)
                {
                    int p0 = edge & 1, p1 = (edge + 1) & 1;
                    int perpendicularAxisPoint = edge < 2 ? startPoint : endPoint[p0];
                    _parity = f.InsideParity[p0];
                    PlacePointIn1D(f.InsideCtx[p0], perpendicularAxisPoint, out uint perp);
                    _parity = f.InsideParity[p1];
                    for (int p = startPoint; p < endPoint[p1]; p++, pointOffset++)
                    {
                        int q = edge == 1 || edge == 2 ? p : endPoint[p1] - (p - startPoint);
                        PlacePointIn1D(f.InsideCtx[p1], q, out uint param);
                        if (p1 != 0) DefinePoint(perp, param, pointOffset);
                        else DefinePoint(param, perp, pointOffset);
                    }
                }
            }
            if (f.NumPointsForInside[U] > f.NumPointsForInside[V] && f.InsideParity[V] == Even)
            {
                int startPoint = numRings;
                int endPoint = f.NumPointsForInside[U] - 1 - startPoint;
                _parity = f.InsideParity[U];
                for (int p = startPoint; p <= endPoint; p++, pointOffset++)
                {
                    PlacePointIn1D(f.InsideCtx[U], p, out uint param);
                    DefinePoint(param, FxpOneHalf, pointOffset);
                }
            }
            else if (f.NumPointsForInside[V] >= f.NumPointsForInside[U] && f.InsideParity[U] == Even)
            {
                int startPoint = numRings;
                int endPoint = f.NumPointsForInside[V] - 1 - startPoint;
                _parity = f.InsideParity[V];
                for (int p = endPoint; p >= startPoint; p--, pointOffset++)
                {
                    PlacePointIn1D(f.InsideCtx[V], p, out uint param);
                    DefinePoint(FxpOneHalf, param, pointOffset);
                }
            }
        }

        private void QuadGenerateConnectivity(QuadFactors f)
        {
            const int startRing = 1;
            int[] rowsToCenter = { (f.NumPointsForInside[U] + 1) >> 1, (f.NumPointsForInside[V] + 1) >> 1 };
            int numRings = Math.Min(rowsToCenter[U], rowsToCenter[V]);
            int[] degenerateRing =
            {
                f.InsideParity[V] == Even ? rowsToCenter[V] - 1 : -1,
                f.InsideParity[U] == Even ? rowsToCenter[U] - 1 : -1,
            };
            var outsideCtx = new TessFactorContext[QuadEdges];
            var outsideParity = new bool[QuadEdges];
            var numOutside = new int[QuadEdges];
            for (int e = 0; e < QuadEdges; e++) { outsideCtx[e] = f.OutsideCtx[e]; outsideParity[e] = f.OutsideParity[e]; numOutside[e] = f.NumPointsForOutsideEdge[e]; }

            int insideBase = f.InsideEdgePointBaseOffset, outsideBase = 0;
            for (int ring = startRing; ring < numRings; ring++)
            {
                int[] numInside = { f.NumPointsForInside[U] - 2 * ring, f.NumPointsForInside[V] - 2 * ring };
                int edge0InsideBase = insideBase, edge0OutsideBase = outsideBase;
                for (int edge = 0; edge < QuadEdges; edge++)
                {
                    int parity = (edge + 1) & 1;
                    int numTriangles = numInside[parity] + numOutside[edge] - 2;
                    int insideBaseOffset, outsideBaseOffset;
                    if (edge == 3)
                    {
                        if (ring == degenerateRing[parity])
                        {
                            _patch2BaseIndexToInvert = insideBase + 1;
                            _patch2CornerCaseBadValue = outsideBase + numOutside[edge] - 1;
                            _patch2CornerCaseReplacementValue = edge0OutsideBase;
                            _patch2IndexInversionEndPoint = (_patch2BaseIndexToInvert << 1) - 1;
                            insideBaseOffset = _patch2BaseIndexToInvert;
                            outsideBaseOffset = outsideBase;
                            _usingPatched2 = true;
                        }
                        else
                        {
                            _patchInsideDelta = insideBase;
                            _patchInsideBad = numInside[parity] - 1;
                            _patchInsideReplacement = edge0InsideBase;
                            _patchOutsideBase = _patchInsideBad + 1;
                            _patchOutsideDelta = outsideBase - _patchOutsideBase;
                            _patchOutsideBad = _patchOutsideBase + numOutside[edge] - 1;
                            _patchOutsideReplacement = edge0OutsideBase;
                            insideBaseOffset = 0;
                            outsideBaseOffset = _patchOutsideBase;
                            _usingPatched = true;
                        }
                    }
                    else if (edge == 2 && ring == degenerateRing[parity])
                    {
                        _patch2BaseIndexToInvert = insideBase;
                        _patch2CornerCaseBadValue = -1;
                        _patch2CornerCaseReplacementValue = -1;
                        _patch2IndexInversionEndPoint = _patch2BaseIndexToInvert << 1;
                        insideBaseOffset = _patch2BaseIndexToInvert;
                        outsideBaseOffset = outsideBase;
                        _usingPatched2 = true;
                    }
                    else
                    {
                        insideBaseOffset = insideBase;
                        outsideBaseOffset = outsideBase;
                    }
                    if (ring == startRing)
                        StitchTransition(IndexCount, insideBaseOffset, f.InsideCtx[parity].NumHalfTessFactorPoints, f.InsideParity[parity],
                                         outsideBaseOffset, outsideCtx[edge].NumHalfTessFactorPoints, outsideParity[edge]);
                    else
                        StitchRegular(true, Diagonals.Mirrored, IndexCount, numInside[parity], insideBaseOffset, outsideBaseOffset);
                    _usingPatched = false;
                    _usingPatched2 = false;
                    IndexCount += numTriangles * 3;
                    outsideBase += numOutside[edge] - 1;
                    if (edge == 2 && ring == degenerateRing[parity]) insideBase -= numInside[parity] - 1;
                    else insideBase += numInside[parity] - 1;
                    numOutside[edge] = numInside[parity];
                }
                if (ring == startRing)
                    for (int edge = 0; edge < QuadEdges; edge++) { outsideCtx[edge] = f.InsideCtx[edge & 1]; outsideParity[edge] = f.InsideParity[edge & 1]; }
            }

            if (f.NumPointsForInside[U] > f.NumPointsForInside[V] && f.InsideParity[V] == OddParity)
            {
                _usingPatched2 = true;
                int stripNumQuads = (((f.NumPointsForInside[U] >> 1) - (f.NumPointsForInside[V] >> 1)) << 1) + (f.InsideParity[U] == Even ? 2 : 1);
                _patch2BaseIndexToInvert = outsideBase + stripNumQuads + 2;
                _patch2CornerCaseBadValue = _patch2BaseIndexToInvert;
                _patch2CornerCaseReplacementValue = outsideBase;
                _patch2IndexInversionEndPoint = _patch2BaseIndexToInvert + _patch2BaseIndexToInvert + stripNumQuads;
                StitchRegular(false, Diagonals.InsideToOutside, IndexCount, stripNumQuads + 1, _patch2BaseIndexToInvert, outsideBase + 1);
                _usingPatched2 = false;
                IndexCount += stripNumQuads * 6;
            }
            else if (f.NumPointsForInside[V] >= f.NumPointsForInside[U] && f.InsideParity[U] == OddParity)
            {
                _usingPatched2 = true;
                int stripNumQuads = (((f.NumPointsForInside[V] >> 1) - (f.NumPointsForInside[U] >> 1)) << 1) + (f.InsideParity[V] == Even ? 2 : 1);
                _patch2BaseIndexToInvert = outsideBase + stripNumQuads + 1;
                _patch2CornerCaseBadValue = -1;
                _patch2IndexInversionEndPoint = _patch2BaseIndexToInvert + _patch2BaseIndexToInvert + stripNumQuads;
                var diag = f.InsideParity[V] == Even ? Diagonals.InsideToOutside : Diagonals.InsideToOutsideExceptMiddle;
                StitchRegular(false, diag, IndexCount, stripNumQuads + 1, _patch2BaseIndexToInvert, outsideBase);
                _usingPatched2 = false;
                IndexCount += stripNumQuads * 6;
            }
        }

        // ---- triangle ----------------------------------------------------------------------------------

        public void TessellateTriDomain(float ueq0, float veq0, float weq0, float inside)
        {
            var f = new TriFactors();
            TriProcessTessFactors(ueq0, veq0, weq0, inside, f);
            if (f.Culled) { PointCount = IndexCount = 0; return; }
            if (f.JustDoMinimum)
            {
                DefinePoint(0, FxpOne, 0);
                DefinePoint(0, 0, 1);
                DefinePoint(FxpOne, 0, 2);
                PointCount = 3;
                switch (_output)
                {
                    case TessOutput.TriangleCw: case TessOutput.TriangleCcw:
                        DefineClockwiseTriangle(0, 1, 2, IndexCount);
                        IndexCount = 3;
                        break;
                    case TessOutput.Point: DumpAllPoints(); break;
                    case TessOutput.Line: DumpAllPointsAsInOrderLineList(); break;
                }
                return;
            }
            TriGeneratePoints(f);
            if (_output == TessOutput.Point) { DumpAllPoints(); return; }
            if (_output == TessOutput.Line) { DumpAllPointsAsInOrderLineList(); return; }
            TriGenerateConnectivity(f);
        }

        private void TriProcessTessFactors(float ueq0, float veq0, float weq0, float inside, TriFactors f)
        {
            if (!(ueq0 > 0) || !(veq0 > 0) || !(weq0 > 0)) { f.Culled = true; return; }
            f.Culled = false;
            Bounds(out float lower, out float upper);
            ueq0 = FMin(upper, FMax(lower, ueq0));
            veq0 = FMin(upper, FMax(lower, veq0));
            weq0 = FMin(upper, FMax(lower, weq0));
            if (HWIntegerPartitioning) { ueq0 = MathF.Ceiling(ueq0); veq0 = MathF.Ceiling(veq0); weq0 = MathF.Ceiling(weq0); }
            if (_originalPartitioning == TessPartitioning.FractionalOdd)
            {
                if (ueq0 > MinOddPlusHalfEpsilon || veq0 > MinOddPlusHalfEpsilon || weq0 > MinOddPlusHalfEpsilon) lower = MinOdd + Epsilon;
            }
            inside = FMin(upper, FMax(lower, inside));
            if (HWIntegerPartitioning) inside = MathF.Ceiling(inside);

            PointCount = 0;
            IndexCount = 0;
            float[] outside = { ueq0, veq0, weq0 };
            if (HWIntegerPartitioning)
            {
                for (int e = 0; e < TriEdges; e++) f.OutsideParity[e] = !IsEven(outside[e]);
                f.InsideParity = !(IsEven(inside) || inside == 1.0f);
            }
            else
            {
                for (int e = 0; e < TriEdges; e++) f.OutsideParity[e] = _originalParity;
                f.InsideParity = _originalParity;
            }
            for (int e = 0; e < TriEdges; e++) f.Outside[e] = FloatToFixed(outside[e]);
            f.Inside = FloatToFixed(inside);
            if (HWIntegerPartitioning || Odd)
            {
                if (f.Inside == FxpOne && f.Outside[Ueq0] == FxpOne && f.Outside[Veq0] == FxpOne && f.Outside[Weq0] == FxpOne)
                {
                    f.JustDoMinimum = true;
                    return;
                }
            }
            f.JustDoMinimum = false;
            for (int e = 0; e < TriEdges; e++) { _parity = f.OutsideParity[e]; ComputeTessFactorContext(f.Outside[e], ref f.OutsideCtx[e]); }
            _parity = f.InsideParity;
            ComputeTessFactorContext(f.Inside, ref f.InsideCtx);
            for (int e = 0; e < TriEdges; e++)
            {
                _parity = f.OutsideParity[e];
                f.NumPointsForOutsideEdge[e] = NumPointsForTessFactor(f.Outside[e]);
                PointCount += f.NumPointsForOutsideEdge[e];
            }
            PointCount -= 3;
            _parity = f.InsideParity;
            f.NumPointsForInside = NumPointsForTessFactor(f.Inside);
            f.NumPointsForInside = Math.Max(Odd ? 4 : 3, f.NumPointsForInside);
            f.InsideEdgePointBaseOffset = PointCount;
            int rings = (f.NumPointsForInside >> 1) - 1;
            PointCount += Odd ? TriEdges * (rings * (rings + 1) - rings) : TriEdges * (rings * (rings + 1)) + 1;
        }

        private void TriGeneratePoints(TriFactors f)
        {
            int pointOffset = 0;
            for (int edge = 0; edge < TriEdges; edge++)
            {
                int parity = edge & 1;
                int endPoint = f.NumPointsForOutsideEdge[edge] - 1;
                for (int p = 0; p < endPoint; p++, pointOffset++)
                {
                    int q = parity != 0 ? p : endPoint - p;
                    _parity = f.OutsideParity[edge];
                    PlacePointIn1D(f.OutsideCtx[edge], q, out uint param);
                    if (edge == 0) DefinePoint(0, param, pointOffset);
                    else DefinePoint(param, edge == 2 ? FxpOne - param : 0, pointOffset);
                }
            }
            _parity = f.InsideParity;
            const int startRing = 1;
            int numRings = f.NumPointsForInside >> 1;
            for (int ring = startRing; ring < numRings; ring++)
            {
                int startPoint = ring;
                int endPoint = f.NumPointsForInside - 1 - startPoint;
                for (int edge = 0; edge < TriEdges; edge++)
                {
                    int parity = edge & 1;
                    PlacePointIn1D(f.InsideCtx, startPoint, out uint perp);
                    unchecked
                    {
                        perp *= FxpTwoThirds;
                        perp = (perp + FxpOneHalf) >> (int)FxpFractionBits;
                    }
                    for (int p = startPoint; p < endPoint; p++, pointOffset++)
                    {
                        int q = parity != 0 ? p : endPoint - (p - startPoint);
                        PlacePointIn1D(f.InsideCtx, q, out uint param);
                        const uint deriv = 2;
                        unchecked
                        {
                            switch (edge)
                            {
                                case 0: DefinePoint(perp, param - (perp + 1) / deriv, pointOffset); break;
                                case 1: DefinePoint(param - (perp + 1) / deriv, perp, pointOffset); break;
                                case 2: DefinePoint(param - (perp + 1) / deriv, FxpOne - (param - (perp + 1) / deriv) - perp, pointOffset); break;
                            }
                        }
                    }
                }
            }
            if (!Odd) DefinePoint(FxpOneThird, FxpOneThird, pointOffset);
        }

        private void TriGenerateConnectivity(TriFactors f)
        {
            const int startRing = 1;
            int numRings = (f.NumPointsForInside + 1) >> 1;
            var outsideCtx = new TessFactorContext[TriEdges];
            var outsideParity = new bool[TriEdges];
            var numOutside = new int[TriEdges];
            for (int e = 0; e < TriEdges; e++) { outsideCtx[e] = f.OutsideCtx[e]; outsideParity[e] = f.OutsideParity[e]; numOutside[e] = f.NumPointsForOutsideEdge[e]; }
            int insideBase = f.InsideEdgePointBaseOffset, outsideBase = 0;
            for (int ring = startRing; ring < numRings; ring++)
            {
                int numInside = f.NumPointsForInside - 2 * ring;
                int edge0InsideBase = insideBase, edge0OutsideBase = outsideBase;
                for (int edge = 0; edge < TriEdges; edge++)
                {
                    int numTriangles = numInside + numOutside[edge] - 2;
                    int insideBaseOffset, outsideBaseOffset;
                    if (edge == 2)
                    {
                        _patchInsideDelta = insideBase;
                        _patchInsideBad = numInside - 1;
                        _patchInsideReplacement = edge0InsideBase;
                        _patchOutsideBase = _patchInsideBad + 1;
                        _patchOutsideDelta = outsideBase - _patchOutsideBase;
                        _patchOutsideBad = _patchOutsideBase + numOutside[edge] - 1;
                        _patchOutsideReplacement = edge0OutsideBase;
                        _usingPatched = true;
                        insideBaseOffset = 0;
                        outsideBaseOffset = _patchOutsideBase;
                    }
                    else
                    {
                        insideBaseOffset = insideBase;
                        outsideBaseOffset = outsideBase;
                    }
                    if (ring == startRing)
                        StitchTransition(IndexCount, insideBaseOffset, f.InsideCtx.NumHalfTessFactorPoints, f.InsideParity,
                                         outsideBaseOffset, outsideCtx[edge].NumHalfTessFactorPoints, outsideParity[edge]);
                    else
                        StitchRegular(true, Diagonals.Mirrored, IndexCount, numInside, insideBaseOffset, outsideBaseOffset);
                    if (edge == 2) _usingPatched = false;
                    IndexCount += numTriangles * 3;
                    outsideBase += numOutside[edge] - 1;
                    insideBase += numInside - 1;
                    numOutside[edge] = numInside;
                }
                if (ring == startRing)
                    for (int edge = 0; edge < TriEdges; edge++) { outsideCtx[edge] = f.InsideCtx; outsideParity[edge] = f.InsideParity; }
            }
            if (Odd)
            {
                DefineClockwiseTriangle(outsideBase, outsideBase + 1, outsideBase + 2, IndexCount);
                IndexCount += 3;
            }
        }

        // ---- isoline --------------------------------------------------------------------------------------

        public void TessellateIsoLineDomain(float density, float detail)
        {
            var f = new IsoLineFactors();
            IsoLineProcessTessFactors(density, detail, f);
            if (f.Culled) { PointCount = IndexCount = 0; return; }
            IsoLineGeneratePoints(f);
            IsoLineGenerateConnectivity(f);
        }

        private void IsoLineProcessTessFactors(float density, float detail, IsoLineFactors f)
        {
            if (!(density > 0) || !(detail > 0)) { f.Culled = true; return; }
            f.Culled = false;
            Bounds(out float lower, out float upper);
            density = FMin(MaxIsolineDensity, FMax(MinIsolineDensity, density));
            detail = FMin(upper, FMax(lower, detail));
            PointCount = 0;
            IndexCount = 0;
            if (HWIntegerPartitioning)
            {
                detail = MathF.Ceiling(detail);
                f.DetailParity = !IsEven(detail);
            }
            else f.DetailParity = _originalParity;
            uint fxpDetail = FloatToFixed(detail);
            _parity = f.DetailParity;
            ComputeTessFactorContext(fxpDetail, ref f.DetailCtx);
            f.NumPointsPerLine = NumPointsForTessFactor(fxpDetail);
            _partitioning = TessPartitioning.Integer;
            density = MathF.Ceiling(density);
            f.DensityParity = !IsEven(density);
            _parity = f.DensityParity;
            uint fxpDensity = FloatToFixed(density);
            ComputeTessFactorContext(fxpDensity, ref f.DensityCtx);
            f.NumLines = NumPointsForTessFactor(fxpDensity) - 1;
            _partitioning = _originalPartitioning;
            PointCount = f.NumPointsPerLine * f.NumLines;
            IndexCount = _output == TessOutput.Point ? PointCount : f.NumLines * (f.NumPointsPerLine - 1) * 2;
        }

        private void IsoLineGeneratePoints(IsoLineFactors f)
        {
            int pointOffset = 0;
            for (int line = 0; line < f.NumLines; line++)
                for (int point = 0; point < f.NumPointsPerLine; point++)
                {
                    _parity = f.DensityParity;
                    PlacePointIn1D(f.DensityCtx, line, out uint v);
                    _parity = f.DetailParity;
                    PlacePointIn1D(f.DetailCtx, point, out uint u);
                    DefinePoint(u, v, pointOffset++);
                }
        }

        private void IsoLineGenerateConnectivity(IsoLineFactors f)
        {
            int pointOffset = 0, indexOffset = 0;
            if (_output == TessOutput.Point)
            {
                for (int line = 0; line < f.NumLines; line++)
                    for (int point = 0; point < f.NumPointsPerLine; point++) DefineIndex(pointOffset++, indexOffset++);
            }
            else
            {
                for (int line = 0; line < f.NumLines; line++)
                    for (int point = 0; point < f.NumPointsPerLine; point++)
                    {
                        if (point > 0) { DefineIndex(pointOffset - 1, indexOffset++); DefineIndex(pointOffset, indexOffset++); }
                        pointOffset++;
                    }
            }
        }

        // ---- points and indices ------------------------------------------------------------------------------

        private void DefinePoint(uint u, uint v, int at)
        {
            PointsU[at] = FixedToFloat(u);
            PointsV[at] = FixedToFloat(v);
        }

        private void DefineIndex(int index, int at) => Indices[at] = PatchIndexValue(index);

        private void DefineClockwiseTriangle(int i0, int i1, int i2, int at)
        {
            DefineIndex(i0, at);
            if (_output == TessOutput.TriangleCw) { DefineIndex(i1, at + 1); DefineIndex(i2, at + 2); }
            else { DefineIndex(i2, at + 1); DefineIndex(i1, at + 2); }
        }

        private void DumpAllPoints() { for (int p = 0; p < PointCount; p++) DefineIndex(p, IndexCount++); }

        private void DumpAllPointsAsInOrderLineList()
        {
            for (int p = 1; p < PointCount; p++) { DefineIndex(p - 1, IndexCount++); DefineIndex(p, IndexCount++); }
        }

        private static int RemoveMSB(int val)
        {
            int check;
            if (val <= 0x0000ffff) check = val <= 0x000000ff ? 0x00000080 : 0x00008000;
            else check = val <= 0x00ffffff ? 0x00800000 : unchecked((int)0x80000000);
            for (int i = 0; i < 8; i++, check = (int)((uint)check >> 1)) if ((val & check) != 0) return val & ~check;
            return 0;
        }

        private int NumPointsForTessFactor(uint fxp)
        {
            unchecked
            {
                if (Odd) return (int)((FxpCeil(FxpOneHalf + (fxp + 1) / 2) * 2) >> (int)FxpFractionBits);
                return (int)((FxpCeil((fxp + 1) / 2) * 2) >> (int)FxpFractionBits) + 1;
            }
        }

        private void ComputeTessFactorContext(uint fxp, ref TessFactorContext ctx)
        {
            unchecked
            {
                uint half = (fxp + 1) / 2;
                if (Odd || half == FxpOneHalf) half += FxpOneHalf;
                uint floorHalf = FxpFloor(half), ceilHalf = FxpCeil(half);
                ctx.HalfTessFactorFraction = half - floorHalf;
                ctx.NumHalfTessFactorPoints = (int)(ceilHalf >> (int)FxpFractionBits);
                if (ceilHalf == floorHalf) ctx.SplitPointOnFloorHalfTessFactor = ctx.NumHalfTessFactorPoints + 1;
                else if (Odd)
                    ctx.SplitPointOnFloorHalfTessFactor = floorHalf == FxpOne ? 0 : (RemoveMSB((int)(floorHalf >> (int)FxpFractionBits) - 1) << 1) + 1;
                else ctx.SplitPointOnFloorHalfTessFactor = (RemoveMSB((int)(floorHalf >> (int)FxpFractionBits)) << 1) + 1;
                int numFloorSegments = (int)((floorHalf * 2) >> (int)FxpFractionBits);
                int numCeilSegments = (int)((ceilHalf * 2) >> (int)FxpFractionBits);
                if (Odd) { numFloorSegments -= 1; numCeilSegments -= 1; }
                ctx.InvNumSegmentsOnFloor = s_fixedReciprocal[numFloorSegments];
                ctx.InvNumSegmentsOnCeil = s_fixedReciprocal[numCeilSegments];
            }
        }

        private void PlacePointIn1D(in TessFactorContext ctx, int point, out uint location)
        {
            bool flip;
            if (point >= ctx.NumHalfTessFactorPoints)
            {
                point = (ctx.NumHalfTessFactorPoints << 1) - point;
                if (Odd) point -= 1;
                flip = true;
            }
            else flip = false;
            if (point == ctx.NumHalfTessFactorPoints) { location = FxpOneHalf; return; }
            unchecked
            {
                uint onCeil = (uint)point, onFloor = onCeil;
                if (point > ctx.SplitPointOnFloorHalfTessFactor) onFloor -= 1;
                uint locFloor = onFloor * ctx.InvNumSegmentsOnFloor;
                uint locCeil = onCeil * ctx.InvNumSegmentsOnCeil;
                location = locFloor * (FxpOne - ctx.HalfTessFactorFraction) + locCeil * ctx.HalfTessFactorFraction;
                location = (location + FxpOneHalf) >> (int)FxpFractionBits;
                if (flip) location = FxpOne - location;
            }
        }

        // ---- stitching --------------------------------------------------------------------------------------

        private enum Diagonals { InsideToOutside, InsideToOutsideExceptMiddle, Mirrored }

        private void StitchRegular(bool trapezoid, Diagonals diagonals, int at, int numInsideEdgePoints, int insideBase, int outsideBase)
        {
            int inside = insideBase, outside = outsideBase;
            if (trapezoid) { DefineClockwiseTriangle(outside, outside + 1, inside, at); at += 3; outside++; }
            int p;
            switch (diagonals)
            {
                case Diagonals.InsideToOutside:
                    for (p = 0; p < numInsideEdgePoints - 1; p++)
                    {
                        DefineClockwiseTriangle(inside, outside, outside + 1, at); at += 3;
                        DefineClockwiseTriangle(inside, outside + 1, inside + 1, at); at += 3;
                        inside++; outside++;
                    }
                    break;
                case Diagonals.InsideToOutsideExceptMiddle:
                    for (p = 0; p < numInsideEdgePoints / 2 - 1; p++)
                    {
                        DefineClockwiseTriangle(outside, outside + 1, inside, at); at += 3;
                        DefineClockwiseTriangle(inside, outside + 1, inside + 1, at); at += 3;
                        inside++; outside++;
                    }
                    DefineClockwiseTriangle(outside, inside + 1, inside, at); at += 3;
                    DefineClockwiseTriangle(outside, outside + 1, inside + 1, at); at += 3;
                    inside++; outside++; p += 2;
                    for (; p < numInsideEdgePoints; p++)
                    {
                        DefineClockwiseTriangle(outside, outside + 1, inside, at); at += 3;
                        DefineClockwiseTriangle(inside, outside + 1, inside + 1, at); at += 3;
                        inside++; outside++;
                    }
                    break;
                case Diagonals.Mirrored:
                    for (p = 0; p < numInsideEdgePoints / 2; p++)
                    {
                        DefineClockwiseTriangle(outside, inside + 1, inside, at); at += 3;
                        DefineClockwiseTriangle(outside, outside + 1, inside + 1, at); at += 3;
                        inside++; outside++;
                    }
                    for (; p < numInsideEdgePoints - 1; p++)
                    {
                        DefineClockwiseTriangle(inside, outside, outside + 1, at); at += 3;
                        DefineClockwiseTriangle(inside, outside + 1, inside + 1, at); at += 3;
                        inside++; outside++;
                    }
                    break;
            }
            if (trapezoid) { DefineClockwiseTriangle(outside, outside + 1, inside, at); at += 3; }
        }

        private static readonly int[] s_finalPointPosition = { 0, 32, 16, 8, 17, 4, 18, 9, 19, 2, 20, 10, 21, 5, 22, 11, 23, 1, 24, 12, 25, 6, 26, 13, 27, 3, 28, 14, 29, 7, 30, 15, 31 };
        private static readonly int[] s_loopStart = { 1, 1, 17, 9, 9, 5, 5, 5, 5, 3, 3, 3, 3, 3, 3, 3, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 };
        private static readonly int[] s_loopEnd = { 0, 0, 17, 17, 25, 25, 25, 25, 29, 29, 29, 29, 29, 29, 29, 29, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 31, 32 };

        private void StitchTransition(int at, int insideBase, int insideHalf, bool insideParity, int outsideBase, int outsideHalf, bool outsideParity)
        {
            if (insideParity == OddParity) insideHalf -= 1;
            if (outsideParity == OddParity) outsideHalf -= 1;
            int outside = outsideBase, inside = insideBase;
            int iStart = Math.Min(s_loopStart[insideHalf], s_loopStart[outsideHalf]);
            int iEnd = Math.Max(s_loopEnd[insideHalf], s_loopEnd[outsideHalf]);
            if (s_finalPointPosition[0] < outsideHalf) { DefineClockwiseTriangle(outside, outside + 1, inside, at); at += 3; outside++; }
            for (int i = iStart; i <= iEnd; i++)
            {
                if (s_finalPointPosition[i] < insideHalf) { DefineClockwiseTriangle(inside, outside, inside + 1, at); at += 3; inside++; }
                if (s_finalPointPosition[i] < outsideHalf) { DefineClockwiseTriangle(outside, outside + 1, inside, at); at += 3; outside++; }
            }
            if (insideParity != outsideParity || insideParity == OddParity)
            {
                if (insideParity == outsideParity)
                {
                    DefineClockwiseTriangle(inside, outside, inside + 1, at); at += 3;
                    DefineClockwiseTriangle(inside + 1, outside, outside + 1, at); at += 3;
                    inside++; outside++;
                }
                else if (insideParity == Even) { DefineClockwiseTriangle(inside, outside, outside + 1, at); at += 3; outside++; }
                else { DefineClockwiseTriangle(inside, outside, inside + 1, at); at += 3; inside++; }
            }
            for (int i = iEnd; i >= iStart; i--)
            {
                if (s_finalPointPosition[i] < outsideHalf) { DefineClockwiseTriangle(outside, outside + 1, inside, at); at += 3; outside++; }
                if (s_finalPointPosition[i] < insideHalf) { DefineClockwiseTriangle(inside, outside, inside + 1, at); at += 3; inside++; }
            }
            if (s_finalPointPosition[0] < outsideHalf) { DefineClockwiseTriangle(outside, outside + 1, inside, at); at += 3; outside++; }
        }

        // ---- index patching ------------------------------------------------------------------------------------

        private bool _usingPatched, _usingPatched2;
        private int _patchInsideDelta, _patchInsideBad, _patchInsideReplacement, _patchOutsideBase, _patchOutsideDelta, _patchOutsideBad, _patchOutsideReplacement;
        private int _patch2BaseIndexToInvert, _patch2IndexInversionEndPoint, _patch2CornerCaseBadValue, _patch2CornerCaseReplacementValue;

        private int PatchIndexValue(int index)
        {
            if (_usingPatched)
            {
                if (index >= _patchOutsideBase)
                    index = index == _patchOutsideBad ? _patchOutsideReplacement : index + _patchOutsideDelta;
                else
                    index = index == _patchInsideBad ? _patchInsideReplacement : index + _patchInsideDelta;
            }
            else if (_usingPatched2)
            {
                if (index >= _patch2BaseIndexToInvert)
                    index = index == _patch2CornerCaseBadValue ? _patch2CornerCaseReplacementValue : _patch2IndexInversionEndPoint - index;
                else if (index == _patch2CornerCaseBadValue)
                    index = _patch2CornerCaseReplacementValue;
            }
            return index;
        }
    }
}
