// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The instruction set. See TrueTypeInterpreter.cs for what this is and why.
//
// The opcodes are grouped the way the specification groups them rather than by number, because that
// is how they are reasoned about: setting up directions, then moving points, then the arithmetic and
// control flow the two need.
//

using System;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    internal sealed partial class TrueTypeInterpreter
    {
        /// <summary>Run a stream of instructions to its end. False when the program faulted.</summary>
        private bool Execute(byte[] code, int start)
        {
            int ip = start;
            int callDepth = 0;
            _callDepth = 0;
            var calls = new CallFrame[128];

            while (true)
            {
                if (ip >= code.Length)
                {
                    // Falling off the end of a function body is how a call returns.
                    if (callDepth == 0) return true;
                    CallFrame frame = calls[--callDepth];
                    _callDepth = callDepth;
                    if (--frame.Repeats > 0)
                    {
                        calls[callDepth++] = frame; _callDepth = callDepth;
                        code = frame.Code;
                        ip = frame.Start;
                        continue;
                    }
                    code = frame.ReturnCode;
                    ip = frame.ReturnIp;
                    continue;
                }

                if (++_steps > StepLimit) return false;

                byte op = code[ip++];
                if (_dumpActive) DumpStep(op, ip - 1);
                switch (op)
                {
                    // ---- pushing ------------------------------------------------------------------

                    case 0x40:  // NPUSHB
                        {
                            int n = code[ip++];
                            for (int i = 0; i < n; i++) Push(code[ip++]);
                            break;
                        }
                    case 0x41:  // NPUSHW
                        {
                            int n = code[ip++];
                            for (int i = 0; i < n; i++) { Push((short)((code[ip] << 8) | code[ip + 1])); ip += 2; }
                            break;
                        }
                    case >= 0xB0 and <= 0xB7:  // PUSHB[0..7]
                        {
                            int n = op - 0xB0 + 1;
                            for (int i = 0; i < n; i++) Push(code[ip++]);
                            break;
                        }
                    case >= 0xB8 and <= 0xBF:  // PUSHW[0..7]
                        {
                            int n = op - 0xB8 + 1;
                            for (int i = 0; i < n; i++) { Push((short)((code[ip] << 8) | code[ip + 1])); ip += 2; }
                            break;
                        }

                    // ---- the stack ----------------------------------------------------------------

                    case 0x20: { int v = Pop(); Push(v); Push(v); break; }              // DUP
                    case 0x21: Pop(); break;                                            // POP
                    case 0x22: _top = 0; break;                                         // CLEAR
                    case 0x23: { int a = Pop(), b = Pop(); Push(a); Push(b); break; }    // SWAP
                    case 0x24: Push(_top); break;                                       // DEPTH
                    case 0x25:                                                          // CINDEX
                        {
                            int k = Pop();
                            if (k <= 0 || k > _top) throw new IndexOutOfRangeException("CINDEX");
                            Push(_stack[_top - k]);
                            break;
                        }
                    case 0x26:                                                          // MINDEX
                        {
                            int k = Pop();
                            if (k <= 0 || k > _top) throw new IndexOutOfRangeException("MINDEX");
                            int v = _stack[_top - k];
                            Array.Copy(_stack, _top - k + 1, _stack, _top - k, k - 1);
                            _stack[_top - 1] = v;
                            Pop();
                            Push(v);
                            break;
                        }
                    case 0x8A:                                                          // ROLL
                        {
                            int a = Pop(), b = Pop(), c = Pop();
                            Push(b); Push(a); Push(c);
                            break;
                        }

                    // ---- storage and control values -----------------------------------------------

                    case 0x42:                                                          // WS
                        {
                            int v = Pop(), i = Pop();
                            if (s_traceHint)
                                Console.Error.WriteLine($"      WS storage[{i}] = {v}  (ppem {_ppem}, prep {_inPreProgram})");
                            if ((uint)i < _storage.Length) _storage[i] = v;
                            break;
                        }
                    case 0x43:                                                          // RS
                        {
                            // itrp_RS@14003d2b4 is where bit 10 turns into bit 3:
                            //     if (globals[0x1c2] & 0x400) globals[0x1c2] |= 0x8;
                            // Any RS at all does it -- the opcode's own work is unaffected. Bit 3
                            // is the bit itrp_MD then asks for, so the whole chain is: this face's
                            // function 0 is the recognised helper, the glyph program has read
                            // storage at least once, and both IUPs have run.
                            if (_fdefAddHelper) _mdBit3 = true;
                            int i = Pop();
                            Push((uint)i < _storage.Length ? _storage[i] : 0);
                            break;
                        }
                    case 0x44:                                                          // WCVTP
                        {
                            int v = Pop(), i = Pop();
                            if (s_traceHint && (uint)i < _scaledCvt.Length)
                                Console.Error.WriteLine($"      WCVTP cvt[{i}] {_scaledCvt[i] / 64f:0.0000}"
                                                        + $" -> {v / 64f:0.0000}px  (ppem {_ppem})");
                            if ((uint)i < _scaledCvt.Length) _scaledCvt[i] = v;
                            break;
                        }
                    case 0x70:                                                          // WCVTF
                        {
                            int v = Pop(), i = Pop();
                            if ((uint)i < _scaledCvt.Length) _scaledCvt[i] = Scale(v);
                            break;
                        }
                    case 0x45:                                                          // RCVT
                        {
                            int i = Pop();
                            Push((uint)i < _scaledCvt.Length ? _scaledCvt[i] : 0);
                            break;
                        }

                    // ---- the projection and freedom vectors ---------------------------------------

                    case 0x00: case 0x01:                                               // SVTCA[a]
                        // itrp_SVTCA_1 ends with *(int *)(localGS + 0xce) = 0xffffffff -- a
                        // FOUR-byte store, so it clears BOTH +0xce and +0xd0, the two points
                        // SPVTL/SDPVTL leave behind for MDRP and ALIGNRP to interpolate
                        // between. Putting the vector on an axis retires the line that was
                        // defining it. We cleared them for SPVTCA and not for SVTCA, so a
                        // proportion could still be recorded from a line the program had
                        // already abandoned.
                        SetVectorLine(-1, -1);
                        SetProjection(op == 0x01);
                        SetFreedom(op == 0x01);
                        LatchClearTypeAxis();
                        break;
                    case 0x02: case 0x03:                                               // SPVTCA[a]
                        SetVectorLine(-1, -1); SetProjection(op == 0x03); LatchClearTypeAxis(); break;
                    case 0x04: case 0x05: SetFreedom(op == 0x05); break;                // SFVTCA[a]

                    case 0x06: case 0x07:                                               // SPVTL[a]
                        {
                            (int vx, int vy) = LineVector(op == 0x07);
                            _gs.ProjX = _gs.DualX = vx;
                            _gs.ProjY = _gs.DualY = vy;
                            ResetProjection();
                            LatchClearTypeAxis();
                            break;
                        }
                    case 0x08: case 0x09:                                               // SFVTL[a]
                        {
                            (int vx, int vy) = LineVector(op == 0x09);
                            _gs.FreeX = vx;
                            _gs.FreeY = vy;
                            ResetProjection();
                            break;
                        }
                    case 0x0A:                                                          // SPVFS
                        {
                            int y = (short)Pop(), x = (short)Pop();
                            Normalize(x, y, out _gs.ProjX, out _gs.ProjY);
                            _gs.DualX = _gs.ProjX; _gs.DualY = _gs.ProjY;
                            ResetProjection();
                            break;
                        }
                    case 0x0B:                                                          // SFVFS
                        {
                            int y = (short)Pop(), x = (short)Pop();
                            Normalize(x, y, out _gs.FreeX, out _gs.FreeY);
                            ResetProjection();
                            break;
                        }
                    case 0x0C: Push(_gs.ProjX); Push(_gs.ProjY); break;                 // GPV
                    case 0x0D: Push(_gs.FreeX); Push(_gs.FreeY); break;                 // GFV
                    case 0x0E:                                                          // SFVTPV
                        _gs.FreeX = _gs.ProjX; _gs.FreeY = _gs.ProjY;
                        ResetProjection();
                        break;
                    case 0x86: case 0x87:                                               // SDPVTL[a]
                        {
                            int p1 = Pop(), p2 = Pop();
                            Zone z1 = ZoneOf(_gs.Zp1), z2 = ZoneOf(_gs.Zp2);
                            if (p1 >= z2.PointCount || p2 >= z1.PointCount) break;

                            // The DUAL vector follows the line as the OUTLINE drew it; the projection
                            // vector follows the same line as it stands now. That is the whole point
                            // of having two: a program can ask how far something has moved.
                            int dx = z1.OrgX[p2] - z2.OrgX[p1];
                            int dy = z1.OrgY[p2] - z2.OrgY[p1];
                            if (op == 0x87) { int t = dx; dx = -dy; dy = t; }
                            Normalize(dx, dy, out _gs.DualX, out _gs.DualY);

                            dx = z1.CurX[p2] - z2.CurX[p1];
                            dy = z1.CurY[p2] - z2.CurY[p1];
                            if (op == 0x87) { int t = dx; dx = -dy; dy = t; }
                            Normalize(dx, dy, out _gs.ProjX, out _gs.ProjY);
                            SetVectorLine(p2, p1);
                            ResetProjection();
                            LatchClearTypeAxis();
                            break;
                        }

                    // ---- the graphics state -------------------------------------------------------

                    case 0x10: _gs.Rp0 = Pop(); break;                                  // SRP0
                    case 0x11: _gs.Rp1 = Pop(); break;                                  // SRP1
                    case 0x12: _gs.Rp2 = Pop(); break;                                  // SRP2
                    case 0x13: _gs.Zp0 = Pop() & 1; break;                              // SZP0
                    case 0x14: _gs.Zp1 = Pop() & 1; break;                              // SZP1
                    case 0x15: _gs.Zp2 = Pop() & 1; break;                              // SZP2
                    case 0x16: _gs.Zp0 = _gs.Zp1 = _gs.Zp2 = Pop() & 1; break;          // SZPS
                    case 0x17: _gs.Loop = Math.Max(0, Pop()); break;                    // SLOOP
                    // GDI PICKS THE ROUNDING FUNCTION WHEN THE ROUND-STATE INSTRUCTION RUNS, not when a
                    // distance is rounded. itrp_RTG, RTHG, RTDG, RUTG, RDTG, ROFF, SROUND and S45ROUND each
                    // read the gs+0xcc ClearType-axis latch and install either the plain rounding function or
                    // its SP (sub-pixel) twin -- itrp_RoundToGrid against itrp_RoundToGridSP, and so on for
                    // every mode. So a face that does SVTCA[y] then RTG gets WHOLE-PIXEL rounding for every
                    // distance afterwards, even ones it later measures along x; and one that does SVTCA[x]
                    // first gets the sixteenth grid, prep included. We had been deciding the grid at rounding
                    // time from the projection in hand, which is a different rule wherever the two are set
                    // apart. MEASURED AND WRONG: latching it this way costs 1,553,651 -> 11,070,238 (10,693,848
                    // with WPF_CT_PREP=1 as well). itrp_MIRP branches on gs+0xcc ITSELF and calls
                    // itrp_RoundOffSP directly, so the sub-pixel choice really is made at USE time,
                    // the way we already had it; whatever these eight instructions read the latch
                    // for, it is not this. WPF_CT_ROUNDLATCH=1 to re-run the experiment.
                    case 0x18: LatchRoundGrid(); _gs.Round = RoundMode.ToGrid; break;                     // RTG
                    case 0x19: LatchRoundGrid(); _gs.Round = RoundMode.ToHalfGrid; break;                 // RTHG
                    case 0x1A: _gs.MinimumDistance = Pop(); break;                      // SMD
                    case 0x1D: _gs.ControlValueCutIn = Pop(); break;                    // SCVTCI
                    case 0x1E: _gs.SingleWidthCutIn = Pop(); break;                     // SSWCI
                    case 0x1F: _gs.SingleWidthValue = Scale(Pop()); break;              // SSW
                    case 0x3D: LatchRoundGrid(); _gs.Round = RoundMode.ToDoubleGrid; break;               // RTDG
                    case 0x4D: _gs.AutoFlip = true; break;                              // FLIPON
                    case 0x4E: _gs.AutoFlip = false; break;                             // FLIPOFF
                    case 0x5E:                                                        // SDB
                        _gs.DeltaBase = Pop();
                        if (_dumpActive) Console.Error.WriteLine($"      SDB {_gs.DeltaBase}");
                        break;
                    case 0x5F:                                                        // SDS
                        _gs.DeltaShift = Pop();
                        if (_dumpActive) Console.Error.WriteLine($"      SDS {_gs.DeltaShift}");
                        break;
                    case 0x7A: LatchRoundGrid(); _gs.Round = RoundMode.Off; break;                        // ROFF
                    case 0x7C: LatchRoundGrid(); _gs.Round = RoundMode.UpToGrid; break;                   // RUTG
                    case 0x7D: LatchRoundGrid(); _gs.Round = RoundMode.DownToGrid; break;                 // RDTG
                    case 0x76: LatchRoundGrid(); _gs.Round = RoundMode.Super; SetSuperRound(Pop(), 64); break;      // SROUND
                    case 0x77: LatchRoundGrid(); _gs.Round = RoundMode.Super45; SetSuperRound(Pop(), 46); break;    // S45ROUND
                    case 0x7E: Pop(); break;                                            // SANGW, obsolete
                    case 0x7F: break;                                                   // AA, obsolete
                    case 0x85: _gs.ScanControl = Pop(); break;                          // SCANCTRL
                    case 0x8D: _gs.ScanType = Pop(); break;                             // SCANTYPE
                    case 0x8E:                                                          // INSTCTRL
                        {
                            int value = Pop(), selector = Pop();
                            if (selector >= 1 && selector <= 3)
                            {
                                int mask = 1 << (selector - 1);
                                _gs.InstructControl = (_gs.InstructControl & ~mask) | (value & mask);
                            }
                            break;
                        }

                    // ---- measuring ------------------------------------------------------------------

                    case 0x2E: case 0x2F:                                               // MDAP[a]
                        {
                            int p = Pop();
                            Zone z = ZoneOf(_gs.Zp0);
                            if (p < z.PointCount)
                            {
                                if (op == 0x2F)
                                {
                                    // WPF_CT_MDAP_NOROUND=1: do not round a point onto the grid
                                    // in the ClearType direction. MEASURED AND REJECTED: 4,545,568
                                    // against 3,598,948 on HowOurWeightTracksGdis, and it barely
                                    // moves the fit -- Segoe UI 'H'@12 goes 65 135 449 519 to
                                    // 64 134 449 519 against GDI's own 78 140 435 497.
                                    // The reasoning was that GDI's fitted outline leaves p1 at its
                                    // natural 6.4336px where our MDAP[r] snaps it to 6.375, so a
                                    // subpixel-positioned axis should not snap at all. That is true
                                    // of the ONE point and false of the glyph: whatever puts our
                                    // right stem 26/64 right of GDI's before the phase runs, it is
                                    // not this rounding.
                                    if (!(s_mdapNoRoundX && InClearTypeDirection && !BiLevelPass))
                                    {
                                        int here = Project(z.CurX[p], z.CurY[p]);
                                        int snapped = RoundDistance(here, position: true, mdap: true);
                                        if (s_mdrpTrace)
                                            Console.Error.WriteLine($"   MDAP p={p} zp0={_gs.Zp0}"
                                                + $" here={here / 64f:0.####} -> {snapped / 64f:0.####}"
                                                + $" cur=({z.CurX[p] / 64f:0.####},{z.CurY[p] / 64f:0.####})"
                                                + $" round={_gs.Round} pv=({_gs.ProjX},{_gs.ProjY})"
                                                + $" ctDir={InClearTypeDirection} prep={_inPreProgram} ppem={_ppem}");
                                        MovePoint(z, p, snapped - here);
                                    }
                                }
                                else
                                {
                                    z.Tags[p] |= TouchMask();
                                }
                            }
                            _gs.Rp0 = _gs.Rp1 = p;
                            break;
                        }

                    case 0x3E: case 0x3F:                                               // MIAP[a]
                        {
                            int cvt = Pop(), p = Pop();
                            Zone z = ZoneOf(_gs.Zp0);
                            if (p >= z.PointCount) { _gs.Rp0 = _gs.Rp1 = p; break; }

                            int value = CvtFor(cvt, distance: false);
                            if (XSpace3x && IsHorizontalProjection) value *= 3;

                            // In the twilight zone the point IS the control value: there is no
                            // outline there, so the instruction places the point outright.
                            if (_gs.Zp0 == 0)
                            {
                                z.OrgX[p] = z.CurX[p] = MulFix(value, _gs.FreeX << 2);
                                z.OrgY[p] = z.CurY[p] = MulFix(value, _gs.FreeY << 2);
                            }

                            int here = Project(z.CurX[p], z.CurY[p]);
                            if (op == 0x3F)
                            {
                                // Close enough to the designer's measurement and it IS the
                                // measurement; further and the outline is telling the truth -- and
                                // the threshold is a SIXTEENTH in the ClearType direction, the same
                                // reduction SCVTCI gets there. MIRP had it and MIAP did not, which
                                // is a real gap: MIAP is what places the left edge of a round glyph
                                // against a control value, and round glyphs are exactly the ones the
                                // per-glyph offset probe finds half a pixel out at 11ppem.
                                // ...and the sixteen scales the DIFFERENCE, not the threshold,
                                // for the reason MIRP's does: 68/16 truncates to 4 rather than
                                // 4.25, so a difference of exactly 4 threw the control value away.
                                bool miapShrink = InClearTypeDirection && !s_cutInFull && !BiLevelPass;
                                bool miapOver = s_cutInExact
                                    ? (long) Math.Abs(value - here) * (miapShrink ? ClearTypeGrid : 1)
                                      > _gs.ControlValueCutIn
                                    : Math.Abs(value - here)
                                      > (miapShrink ? _gs.ControlValueCutIn / ClearTypeGrid
                                                    : _gs.ControlValueCutIn);
                                if (miapOver) value = here;
                                value = RoundDistance(value, position: true);
                            }
                            MovePoint(z, p, value - here);
                            _gs.Rp0 = _gs.Rp1 = p;
                            break;
                        }

                    case >= 0xC0 and <= 0xDF: MoveDirectRelative(op); break;            // MDRP[abcde]
                    case >= 0xE0 and <= 0xFF: MoveIndirectRelative(op); break;          // MIRP[abcde]

                    case 0x3A: case 0x3B:                                               // MSIRP[a]
                        {
                            int distance = Pop(), p = Pop();
                            Zone z = ZoneOf(_gs.Zp1);
                            if (p >= z.PointCount) break;

                            // A twilight point being placed relative to rp0 has no position of its
                            // own yet; give it one before measuring from it.
                            if (_gs.Zp1 == 0)
                            {
                                Zone zr = ZoneOf(_gs.Zp0);
                                z.OrgX[p] = zr.OrgX[_gs.Rp0] + MulFix(distance, _gs.FreeX << 2);
                                z.OrgY[p] = zr.OrgY[_gs.Rp0] + MulFix(distance, _gs.FreeY << 2);
                                z.CurX[p] = z.OrgX[p];
                                z.CurY[p] = z.OrgY[p];
                            }

                            int current = MeasureCurrent(_gs.Zp1, p, _gs.Zp0, _gs.Rp0);

                            // "Some fonts pre-calculate stroke weights and subsequently use MSIRP[.],
                            // which involves neither rounding nor CVT cut-ins. Therefore MSIRP[.] now
                            // respects the CVT cut-in" -- and only where there is a real outline
                            // distance to compare against, since "in which case we assume the context
                            // is a stroke weight, else we assume the context is an accent placement
                            // function, in which case we use the actual distance as before".
                            if (InClearTypeDirection && !NativeClearTypeMode && _gs.Zp0 == _gs.Zp1)
                            {
                                int org = MeasureOriginal(_gs.Zp1, p, _gs.Zp0, _gs.Rp0);
                                if (org != 0
                                // GDI'S EXACT FORM, the same one itrp_MIRP uses and which we already fixed there but
                                // never propagated here. itrp_MSIRP@14003be18:
                                //     w9 = distance - original;  w8 = globals[0x78]      ; the cut-in
                                //     if (w8 < w9 * 16) take original                    ; note *16 on the DIFFERENCE
                                //     else if (-w8 > w9 * 16) take original
                                // Dividing the cut-in by sixteen instead truncates -- 68/16 is 4, not 4.25 -- and `>=`
                                // discards one more control value at the boundary. WPF_CT_CUTIN_EXACT=0 restores it.
                                && (s_cutInExact
                                    ? (long) Math.Abs(distance - org) * ClearTypeGrid > (long) _gs.ControlValueCutIn
                                    : Math.Abs(distance - org) >= _gs.ControlValueCutIn / ClearTypeGrid))
                                    distance = org;
                            }

                            // itrp_MSIRP@180085fd4 DOES double-check its colour, and seeds the check with 1 (BLACK):
                            //     uVar12 = DoubleCheckLinkColor(elem, rp0, point, 1);
                            //     AddDistance(gs, elem, rp0, point, uVar12);
                            // So MSIRP can form a stem pair where MDRP, ALIGNRP and SHP -- which all pass a literal 3
                            // -- never can. It is the only other opcode besides MIRP that pairs.
                            LinkX(_gs.Zp1, p, _gs.Zp0, _gs.Rp0, doubleCheck: true, phaseType: 1);
                            MovePoint(z, p, distance - current);
                            _gs.Rp1 = _gs.Rp0;
                            _gs.Rp2 = p;
                            if (op == 0x3B) _gs.Rp0 = p;
                            break;
                        }

                    case 0x3C:                                                          // ALIGNRP
                        {
                            for (int i = 0; i < _gs.Loop; i++)
                            {
                                int p = Pop();
                                Zone z = ZoneOf(_gs.Zp1);
                                if (p >= z.PointCount) continue;
                                // THE OTHER HALF OF THE POST-IUP QUESTION. Times' bowls flatten
                                // their shoulders with an SCFS (see case 0x48) and then a LOOPCALL
                                // that ALIGNRPs the neighbouring controls onto that point in both
                                // axes -- both of them moves of UNTOUCHED points after IUP, which
                                // is the shape Microsoft describes as denting the outline.
                                // WPF_CT_ALIGNRP_TOUCHED=1 refuses those the same way.
                                if (s_alignrpTouchedOnly && !BiLevelPass && ClearTypeInfo
                                    && (IsHorizontalProjection
                                        ? _iupXDone && (z.Tags[p] & TagTouchX) == 0
                                        : _iupYDone && (z.Tags[p] & TagTouchY) == 0)) continue;
                                LinkX(_gs.Zp1, p, _gs.Zp0, _gs.Rp0, canProportion: true);
                                MovePoint(z, p, -MeasureCurrent(_gs.Zp1, p, _gs.Zp0, _gs.Rp0));
                            }
                            _gs.Loop = 1;
                            break;
                        }

                    case 0x27:                                                          // ALIGNPTS
                        {
                            int p2 = Pop(), p1 = Pop();
                            int half = MeasureCurrent(_gs.Zp0, p2, _gs.Zp1, p1) / 2;
                            MovePoint(ZoneOf(_gs.Zp1), p1, half);
                            MovePoint(ZoneOf(_gs.Zp0), p2, -half);
                            break;
                        }

                    case 0x28:                                                          // UTP
                        {
                            int p = Pop();
                            Zone z = ZoneOf(_gs.Zp0);
                            if (p < z.PointCount) z.Tags[p] &= (byte)~TouchMask();
                            break;
                        }

                    case 0x29: break;                                                   // (unused)

                    // NOT from IUP[y]. itrp_IUP gates ExecutePhaseControl on
                    //     (gs[0x1c0] & 4) != (axis & 1)
                    // -- it runs the phase only from the IUP on the CLEARTYPE axis, which is x.
                    // We ran it from whichever IUP came first and let _phaseApplied keep the
                    // rest; fonts write IUP[y] before IUP[x], so we phased before any x
                    // instruction that sits between the two, and GDI phases after all of them.
                    // WPF_CT_PHASE_IUPY=1 goes back to phasing at whichever comes first.
                    case 0x30:
                        if (s_phaseAtIupY) ApplyPhaseAtIup();
                        InterpolateUntouched(false); _iupDone = _iupYDone = true; break;      // IUP[y]
                    // IUP[x] belongs, and it was worth checking: under ClearType x is fitted only
                    // lightly, so a rasterizer might reasonably leave every point the program did
                    // not explicitly move where the scaling put it. Skipping it costs 759,520 ->
                    // 1,203,544, and the digits -- which nothing else here disturbs -- go 20,168 ->
                    // 44,940. The untouched points do get dragged along.
                    case 0x31:                                                         // IUP[x]
                        if (s_iupProbe)
                            Console.Error.WriteLine($"IUPX pts={_realPoints} applied={_phaseApplied}"
                                + $" compat64={CompatibleAdvance64} ct={ClearTypeInfo}"
                                + $" bilevel={BiLevelPass} depth={HintDepth}");
                        // ORDER. The phase writes CurX without setting TagTouchX, so running it
                        // BEFORE IUP lets IUP reposition every untouched point from its ORIGINAL
                        // coordinates and throw the compression away -- which is exactly what
                        // Arial Italic 'w'@16 shows: the tree computes d=-41..-166 per node and the
                        // rendered glyph is not compressed at all. GDI's ExecutePhaseControl runs
                        // over the finished outline. WPF_CT_PHASE_AFTERIUP=1 puts it after.
                        if (s_phaseAfterIup) { InterpolateUntouched(true); ApplyPhaseAtIup(); }
                        else { ApplyPhaseAtIup(); InterpolateUntouched(true); }
                        _iupDone = true; _iupXDone = true; break;

                    case 0x32: case 0x33: ShiftByPoint(op == 0x33); break;               // SHP[a]
                    case 0x34: case 0x35: ShiftContour(op == 0x35); break;               // SHC[a]
                    case 0x36: case 0x37: ShiftZone(op == 0x37); break;                  // SHZ[a]

                    case 0x38:                                                          // SHPIX
                        {
                            int amount = Pop();
                            int dx = MulFix(amount, _gs.FreeX << 2);
                            int dy = MulFix(amount, _gs.FreeY << 2);
                            byte touch = TouchMask();
                            Zone z = ZoneOf(_gs.Zp2);
                            for (int i = 0; i < _gs.Loop; i++)
                            {
                                int sp = Pop();
                                // Jason Campbell, on the Windows rasterizers: "only DELTAPs are not
                                // used, but SHPIX are executed." Tested, because it is a specific
                                // claim from someone who would know, and it does NOT hold against
                                // GDI's own output here: executing SHPIX costs 681,513 -> 743,730.
                                // Refusing it in the ClearType direction, as DELTAP is refused,
                                // measures better. WPF_CT_SHPIX=run tries it the other way.
                                // ...and WPF_CT_SHPIX=outline, which mode 7 of the compatible-width
                                // correction implies, executes the FRACTIONAL ones on outline points
                                // and refuses the rest. Arial writes two kinds: whole-pixel shifts
                                // gated on a ppem range -- of the advance phantom (its bi-level
                                // advance, which we lay out with already) and, at some sizes, of a
                                // stem or an arm -- and fractional nudges of a stem's side tagged
                                // with the rendering mode the face read back from GETINFO. GDI's
                                // solved edges include every nudge and none of the whole pixels:
                                // 'E' at 16ppem carries a -64 on its middle arm, and executing it
                                // puts that arm a pixel left of where GDI draws it.
                                // THE RULE AS fontdrvhost WRITES IT -- itrp_SHP_Common @ +0x3e978,
                                // read, not inferred. Its whole ClearType suppression is gated on
                                // bit 4 of +0x1C2, which itrp_CALL sets on entry and clears on
                                // return: a SHPIX written INLINE in a glyph program is never
                                // filtered. Inside a call it keeps the move only when the
                                // PROJECTION vector is exactly (0, 0x4000) -- pure positive y, the
                                // non-ClearType axis -- and the point is already touched there;
                                // everything else is skipped. (With ctflags bit 2, horizontal LCD
                                // stripes, the same test is mirrored onto x. GDI leaves that bit
                                // clear, so it is not implemented.) The composite bypass is the
                                // byte at +0x171, which fsg_CompositeInnerGridFit sets.
                                // NOTE the axis: we tested the FREEDOM vector, the scaler tests
                                // PROJECTION. WPF_CT_SHPIX_CALL=0 turns this off.
                                // ...AND THE THIRD CLAUSE, which was missing. Read again at
                                // itrp_SHP_Common +0x3e978, the gate with x as the ClearType axis
                                // (globals[0x1c0] bit 2 clear) is
                                //
                                //     apply  =  pv == (0, 0x4000)
                                //               && ( globals[0x171] != 0                  // composite
                                //                    || ( tags[pt] & 2                    // touched in y
                                //                         && (globals[0x1c2] & 2) == 0 ) ) // IUP[y] NOT run
                                //
                                // and globals[0x1c2] bit 1 is "IUP[y] has run". So it is the same
                                // THREE-part test itrp_DeltaEngine uses -- pure +y projection,
                                // touched in y, and IUP[y] not yet run -- and we had implemented
                                // only the first two. On the delta side the ablation showed the
                                // IUP[y] clause was the whole of it; it is worth as much here.
                                //
                                // Arial 'K' at 20ppem is the case that found it. Five SHPIXes run
                                // inside function 52 (which is byte-for-byte the recognised helper
                                // at +0xa87d0), all after IUP[y]: three project on x and were
                                // already skipped, and two project on pure +y -- pt3 by +32/64 and
                                // pt9 by -32/64 -- which are exactly the two points where the arms
                                // meet the stem. Those two tore the junction open by a whole pixel.
                                // The design has the arms meeting at a POINT (natural y -462 and
                                // -464); the program leaves them at 473 and 466; the SHPIXes push
                                // them to 505 and 434. GDI's own ClearType puts them at 475 and
                                // 454, i.e. it declines both -- and its BI-LEVEL pass applies them
                                // (513), which is why our bi-level matches GDI's exactly and our
                                // ClearType did not.
                                //
                                // The composite term moves inside the predicate here, because that
                                // is where the binary has it: globals[0x171] is a BYPASS that makes
                                // the move apply, not a condition on skipping at all.
                                // WPF_CT_SHPIX_IUPY=0 restores the two-clause version.
                                bool shpixApply =
                                    _gs.ProjX == 0 && _gs.ProjY == 0x4000
                                    && (_inComposite
                                        || ((uint) sp < (uint) z.PointCount
                                            && (z.Tags[sp] & TagTouchY) != 0
                                            && !(s_shpixAfterIupY && _iupYDone)));
                                if (s_shpixCallRule && ClearTypeInfo && !NativeClearTypeMode
                                    && !BiLevelPass && !_inPreProgram && _deltaFdefDepth > 0
                                    && !shpixApply)
                                {
                                    if (s_yTrace)
                                        Console.Error.WriteLine("SKIP-SHPIX pt=" + sp + " amt="
                                            + amount + " dx=" + dx + " dy=" + dy);
                                    continue;
                                }
                                if (!s_runShpix
                                    && (!s_runShpixOutline || (uint) sp >= (uint) _realPoints || amount % 64 == 0
                                        // ...and only INLINE, before the interpolation.
                                        || (s_shpixOutInline && _iupDone)
                                        // The rest narrow it further and are all off by default;
                                        // each of them carries what it measured.
                                        || (s_shpixOutPost && !_iupDone)
                                        || (s_shpixOutTouchedX && (z.Tags[sp] & TagTouchX) == 0)
                                        || (s_shpixOutTouchedY && (z.Tags[sp] & TagTouchY) == 0)
                                        || (s_shpixOutMax > 0 && (amount > s_shpixOutMax || amount < -s_shpixOutMax))
                                        || (s_shpixDiag
                                            && (amount > s_shpixDiagMax || amount < -s_shpixDiagMax)
                                            && OnDiagonalEdge(z, sp)))
                                    // NOT the RE delta rule: fontdrvhost keeps SHPIX and DELTAP in
                                    // two different functions with two different tests, and the
                                    // SHPIX one is above (MatchesSuppressedFdef). Sharing one
                                    // predicate re-broke exactly the glyphs that rule fixes --
                                    // Tahoma Bold 'W'@16 went 0 -> 4,370.
                                    && SkipDeltaInClearTypeDirection(z, sp, compositeExempt: true,
                                                                     forShpix: true)) continue;
                                // AND IN THE NON-CLEARTYPE DIRECTION, ONLY ON TOUCHED POINTS. The
                                // paper's sentence quoted below ends "we keep only deltas on touched
                                // points in the non-ClearType direction", and we were applying the
                                // touched test only in the ClearType one. Arial's 'W' at 12ppem is
                                // what that costs: a vertical SHPIX of -199/64 -- more than three
                                // pixels -- lands its bottom vertex BELOW the baseline, where GDI's
                                // W stops on it and the unhinted outline never goes below it either.
                                //
                                // A CLEARTYPE RULE, AND ONLY A CLEARTYPE RULE. GDI's bi-level
                                // rasterizer executes every SHPIX as written: Tahoma Bold's function
                                // 55 shifts a round glyph's diagonal extremes a whole pixel OUTWARD
                                // between 9 and 13ppem -- eight untouched points of 'O', 'Q', 'C',
                                // 'G', the bowls of a/d/e/g/q, the spine of 's' -- and GetGlyphOutline's
                                // fitted points carry every one of those shifts, while the SAME face's
                                // ClearType pixels do not (executing them there puts 's'@12 at 1,676
                                // differing lamps against 491). Refusing them in the bi-level pass was
                                // 49 of Tahoma Bold's 1,688 points a pixel off at 12ppem, 27 of Segoe
                                // UI's, 28 of Times'; letting the pass run them plainly leaves 7, 0, 7.
                                if (s_shpixNeedsTouch && ClearTypeInfo && !NativeClearTypeMode
                                    && !_inPreProgram && !IsHorizontalFreedom
                                    && (uint) sp < (uint) z.PointCount
                                    && (z.Tags[sp] & TagTouchY) == 0) continue;
                                // AND THE OTHER HALF OF THE SAME SENTENCE. "We keep only deltas on
                                // touched points in the NON-ClearType direction" says two things,
                                // and only one of them was here: the test above drops a VERTICAL
                                // shift on a point the program has not placed in y, but a shift
                                // along the ClearType direction itself is not kept under any
                                // condition. Tahoma Bold's function 55 is what that costs -- an
                                // `SHPIX` of -30/64 on 'W's leftmost point and +30/64 on its
                                // rightmost, both untouched in y, prising the letter 0.94px wider
                                // than the outline it was scaled from. GDI draws neither shift:
                                // its 'W' is the natural outline scaled by the compatible-width
                                // ratio about its left edge (predicted 1009.6/64 for the right
                                // edge against the solver's 1009), and ours came out 6% wide
                                // before the phase then only 2.75% of that was taken back.
                                // WPF_CT_SHPIX_X=keep restores it.
                                if (s_shpixDropX && ClearTypeInfo && !NativeClearTypeMode
                                    && !BiLevelPass && !_inPreProgram && IsHorizontalFreedom
                                    && (uint) sp < (uint) z.PointCount
                                    && (z.Tags[sp] & TagTouchY) == 0) continue;
                                // ON A DIAGONAL, A PAIR THAT OPPOSE EACH OTHER IS A WIDTH.
                                // Tahoma's 'W' at 11ppem nudges two of its diagonal points -32/64
                                // and -44/64, both the same way: the letter MOVES and keeps its
                                // shape, and GDI draws exactly that. Verdana Bold's 'W' at 12 nudges
                                // its leftmost +33/64 and its rightmost -33/64, closing the letter
                                // up by a whole pixel onto the bi-level grid, and GDI draws it two
                                // columns WIDER, at very nearly the unhinted width. A stroke that is
                                // not vertical has no width to grid-fit, so an opposing pair is not
                                // a nudge at all and none of it is run.
                                //
                                // NOTICED HERE, ACTED ON BY RUNNING THE PROGRAM AGAIN. Putting the
                                // first of the pair back where it was, mid-run, leaves an outline
                                // that no program produced: everything the face did in between --
                                // an IP through the moved point, an MDRP measured from it -- has
                                // already happened. Arial's 'w' at 11ppem is what that looked like,
                                // fitted two pixels wider than its own outline and thrown out as
                                // implausible. So the first pass only WATCHES, and RunFaceHints
                                // re-runs the glyph with RefusingDiagonalNudges set.
                                if (s_shpixPair && !_inPreProgram && ClearTypeInfo && !BiLevelPass
                                    && IsHorizontalFreedom && dx != 0 && amount % 64 != 0
                                    && _gs.Zp2 == 1 && (uint) sp < (uint) _realPoints
                                    && OnDiagonalEdge(z, sp))
                                {
                                    if (RefusingDiagonalNudges) continue;
                                    // WHAT COUNTS AS "A PAIR", narrowed by the cases that go the
                                    // other way -- and stated on the glyph's EXTREMES rather than
                                    // on a count, because a crossing letter nudges twice a side:
                                    // Tahoma Bold's 'X' at 12ppem moves its two left points -48/64
                                    // and its two right points +48/64, which a "exactly two, veto
                                    // on a third" test throws away.
                                    //   * A POINT THE PROGRAM HAS PLACED IN Y keeps its delta --
                                    //     the paper's own exception, and what separates Arial's
                                    //     'w' (leftmost touched XY, GDI runs both nudges,
                                    //     refusing them costs 480 -> 9,047) from Tahoma Bold's
                                    //     'W' (both untouched, GDI runs neither).
                                    //   * THE OUTERMOST TWO HAVE TO OPPOSE and be within a
                                    //     quarter of each other in size. Tahoma's 'v' nudges
                                    //     -34/64 then +4/64: the small one is not the other half
                                    //     of a width, and letting it veto the -34 costs 6,573.
                                    //   * AND THE WIDTH HAS TO MOVE HALF A PIXEL. Segoe UI
                                    //     opposes its diagonals too ('V' 4+4, 'Z' 12+12) and GDI
                                    //     runs every one; what it declines is the 60..66/64 class.
                                    if ((z.Tags[sp] & TagTouchY) != 0) _nudgeVetoed = true;
                                    else if (_nudgeN < _nudgeXs.Length)
                                    {
                                        _nudgeXs[_nudgeN] = z.CurX[sp];
                                        _nudgeDxs[_nudgeN] = dx;
                                        _nudgeN++;
                                        int lo = 0, hi = 0;
                                        for (int k = 1; k < _nudgeN; k++)
                                        {
                                            if (_nudgeXs[k] < _nudgeXs[lo]) lo = k;
                                            if (_nudgeXs[k] > _nudgeXs[hi]) hi = k;
                                        }
                                        int a = _nudgeDxs[lo], b = _nudgeDxs[hi];
                                        int ma = Math.Abs(a), mb = Math.Abs(b);
                                        _nudgePair = lo != hi && Math.Sign(a) != Math.Sign(b)
                                                     && ma + mb >= s_nudgePairMin
                                                     && Math.Abs(ma - mb) * 4 <= Math.Max(ma, mb);
                                    }
                                    else _nudgeVetoed = true;
                                    SawOpposingDiagonalNudges = _nudgePair && !_nudgeVetoed;
                                }
                                if (s_yTrace)
                                    Console.Error.WriteLine("SHPIX pt=" + sp + " amt=" + amount
                                        + " dx=" + dx + " dy=" + dy
                                        + " tag=" + (((uint) sp < (uint) z.PointCount
                                            && (z.Tags[sp] & TagTouchX) != 0) ? "X" : ".")
                                        + (((uint) sp < (uint) z.PointCount
                                            && (z.Tags[sp] & TagTouchY) != 0) ? "Y" : ".")
                                        + " horizFv=" + (IsHorizontalFreedom ? 1 : 0)
                                        + " diagEdge=" + (OnDiagonalEdge(z, sp) ? 1 : 0)
                                        + " x=" + (((uint) sp < (uint) z.PointCount) ? z.CurX[sp] : 0));
                                MoveDirect(z, sp, dx, dy, touch);
                            }
                            _gs.Loop = 1;
                            break;
                        }

                    case 0x39: InterpolatePoints(); break;                              // IP

                    case 0x46: case 0x47:                                               // GC[a]
                        {
                            int p = Pop();
                            Zone z = ZoneOf(_gs.Zp2);
                            if (p >= z.PointCount) { Push(0); break; }
                            Push(op == 0x46
                                 ? Project(z.CurX[p], z.CurY[p])
                                 : DualProject(z.OrgX[p], z.OrgY[p]));
                            break;
                        }

                    case 0x48:                                                          // SCFS
                        {
                            int value = Pop(), p = Pop();
                            Zone z = ZoneOf(_gs.Zp2);
                            if (p >= z.PointCount) break;
                            // In the ClearType pass, an SCFS along the
                            // NON-ClearType axis onto a point not already touched there is dropped
                            // -- the same rule this file already applies to DELTAP, and the same
                            // sentence of Microsoft's own account: "we keep only deltas on touched
                            // points in the non-ClearType direction", because on an untouched point
                            // a direct coordinate write "creates a dent in the outline".
                            // <para>Times' bowls are what asks the question. Its '0' flattens both
                            // shoulders with `GC(counterPt); SUB cvt[98]; SCFS` onto the off-curve
                            // controls, which nothing has touched, turning an oval's side into a
                            // dead straight run of 7.6px out of 10 -- and our ink comes out 1.277x
                            // GDI's with the error exactly symmetric top and bottom.</para>
                            // <para>THAT THE EFFECT IS CLEARTYPE-CONDITIONAL IS NOT AN INFERENCE
                            // FROM THE SCORE -- GDI'S OWN TWO RENDERS SAY SO. WPF_GDI_QUALITY on
                            // '0'@15 gives a BI-LEVEL raster of `..999..` over eight rows of
                            // `.9...9.`, which is exactly what a straight side from y=1.2 to 8.8
                            // produces when a pixel turns on at its centre -- the flattened
                            // shoulder, and we match it. Its CLEARTYPE raster has the left edge
                            // already moving right by two rows in from each end (col 6 reads 73
                            // where the middle rows read 111). Same glyph, same program, two
                            // different outlines: so a ClearType-conditional rule moves the
                            // shoulder, and this is the one Microsoft states.</para>
                            // <para>WHERE GDI IMPLEMENTS IT IS STILL UNKNOWN, and that is worth
                            // saying plainly. `itrp_WC` is a plain project-and-move with no gate;
                            // the move functions it reaches indirectly (`itrp_YMovePoint`,
                            // `itrp_MovePoint`) gate nothing and `itrp_SVTCA_0` installs them
                            // unconditionally; and `itrp_WC` is not among the readers of
                            // globals+0x1c2 at all, where itrp_DeltaEngine, itrp_SHP_Common and
                            // itrp_MD are. So the rule is right and its address is not yet found;
                            // the interpreter's own dispatch loop and the three opcode tables at
                            // ~0x14009a8f8 / ~0x14009b0c0 / ~0x1400bb800 are where to look next.
                            // </para>
                            // <para>MEASURED, with the ALIGNRP half below: specimen 556,716 ->
                            // 458,613 and holdout 8..24 1,849,185 -> 1,572,278. Times -92,082 and
                            // Arial -6,021, with Verdana, Tahoma, Segoe UI and Consolas moving by
                            // EXACTLY ZERO. Per glyph at 15ppem '0' goes 2,752 -> 694, '6' 1,258
                            // -> 456, '9' 913 -> 262, and 'A' -- which has no bowl and no SCFS --
                            // does not move at all.</para>
                            // AND THE SAME ON THE CLEARTYPE AXIS -- WPF_CT_SCFS_X=1, under test.
                            // The rule above is stated for the NON-ClearType axis, and GDI does
                            // fit x, so an x SCFS is kept. But the per-point oracle says the x ones
                            // are where the remaining bowl error is. Times '0'@12 solves EXACTLY
                            // (1,393 -> 0) once three points move, and those three are precisely
                            // the x-axis SCFS targets:
                            //     SCFS pt 6  -> 275/64   ours 275, GDI 239   (d -36)
                            //     SCFS pt 12 -> 275/64   ours 275, GDI 239   (d -36)
                            //     SCFS pt 14 -> 109/64   ours 109, GDI 133   (d +24)
                            // and the deltas are IDENTICAL IN 64THS at 12 and 14ppem, while '0' is
                            // already pixel-exact at 16 and 20. Every X-TOUCHED point of the glyph
                            // (0, 9, 17, 26) agrees with GDI exactly, and so does the whole
                            // bi-level fit (37 of 37 points), so nothing before IUP is in question.
                            // <para>WHAT THE BLOCK IS, AND WHY NEITHER ANSWER IS RIGHT YET.
                            // The SCFSes that matter live in a ppem-gated block of the glyph
                            // program, not in the font program: Times' '9' reaches instruction 207
                            // `RS 8` and 208 `JROF`, and storage[8] is 1 at 14ppem and 0 at 16, so
                            // at 16 the whole block from 209 to 261 is jumped over. That is exactly
                            // the size split the oracle shows -- Times' curved glyphs are almost
                            // all pixel-exact at 16ppem and wrong at 12 and 14 -- so the entire
                            // remaining Times pool is inside this block.</para>
                            // <para>The block is a helper called twice, `RCVT 98; GC; ADD; ...;
                            // SCFS; SCFS`, which sets a PAIR of points to a pair of coordinates:
                            // (36, 8) and then (31, 13). Dropping it is what we do and it is not
                            // right -- at 14ppem '9' scores 1001 with seven points wrong. APPLYING
                            // it is worse, and not marginally: 1001 -> 3315, thirteen points wrong.
                            // And the solved outline rules out the values themselves, because the
                            // helper sets pt8 and pt13 to the SAME coordinate (125 in the ClearType
                            // pass, 141 in the bi-level one) while GDI's own pixels want them far
                            // apart -- pt8 near 131 and pt13 near 150. Whatever GDI does here, it
                            // is neither "skip the block" nor "run the block as written".</para>
                            if (s_scfsXToo && !BiLevelPass && ClearTypeInfo
                                && IsHorizontalProjection
                                && (z.Tags[p] & TagTouchX) == 0) break;
                            if (s_scfsTouchedOnly && !BiLevelPass && ClearTypeInfo
                                && !IsHorizontalProjection
                                && (z.Tags[p] & TagTouchY) == 0) break;
                            MovePoint(z, p, value - Project(z.CurX[p], z.CurY[p]));

                            // A twilight point moved this way keeps the new place as its ORIGIN too:
                            // it has no outline behind it to go back to.
                            if (_gs.Zp2 == 0) { z.OrgX[p] = z.CurX[p]; z.OrgY[p] = z.CurY[p]; }
                            break;
                        }

                    case 0x49: case 0x4A:                                               // MD[a]
                        {
                            // ZP0 GOES WITH THE DEEPER ARGUMENT, ZP1 WITH THE TOP ONE, as FreeType
                            // pairs them: it bounds-checks args[0] against zp0 and args[1] against
                            // zp1 and measures PROJECT(zp0 + args[0], zp1 + args[1]). We had them
                            // the other way round, which NEGATES a directional measurement -- and MD
                            // feeds conditionals, so that is not a small shift, it takes the other
                            // arm of an IF.
                            // Arial's 'W' at 12ppem is what it cost: the wrong sign took a branch
                            // ending in a vertical SHPIX of -199/64, which put the W's bottom vertex
                            // more than three pixels BELOW THE BASELINE. Rendered, our W was a pixel
                            // taller than Windows'; corrected, it ends on the same row. Arial's
                            // per-glyph cost at 12ppem falls 15,524,188 -> 13,335,829 and 'W' and
                            // 'w' leave the twelve dearest glyphs entirely.
                            // The aggregate goes the OTHER way by about a percent -- the six-face
                            // specimen 10,053,618 -> 10,072,556, all of it Arial regular and bold --
                            // so this trades many small differences for one gross one. A spurious
                            // descender on a W is the worse defect, and this is what the spec and
                            // FreeType both say. WPF_MD_SPEC=0 restores the old pairing.
                            int top = Pop(), deep = Pop();
                            int a = s_mdOldOrder ? top : deep, b = s_mdOldOrder ? deep : top;
                            int md = op == 0x49
                                 ? MeasureCurrent(_gs.Zp0, a, _gs.Zp1, b)
                                 : MeasureOriginalExact(_gs.Zp0, a, _gs.Zp1, b);
                            // EXACTLY ONE PIXEL IS REPORTED AS 65/64, once both IUPs have run.
                            // itrp_MD@14003a1a0 does nothing else with the flags word:
                            //     if ((globals[0x1c2] & 0xB) == 0xB && dist == 0x40) dist = 0x41;
                            // 0xB is bits 0, 1 and 3: "IUP[x] ran", "IUP[y] ran", and -- traced
                            // to its source this time -- the bit itrp_RS raises for a face whose
                            // fpgm function 0 is the recognised helper (see the FDEF case). So the
                            // rule is NOT global: it needs the signature, an RS, and both IUPs. A
                            // hack that specific exists to flip ONE comparison, a program that
                            // measures a stem and tests it against a whole pixel taking the other
                            // branch after IUP. WPF_CT_MD65=0 turns it off. No glyph in the
                            // current worst pools even calls MD (Times '0', 'a', 'g' and '6' at
                            // 15ppem call it zero times), so this is here because it was read,
                            // not because it was needed.
                            if (s_md65 && md == 64 && _mdBit3 && _iupXDone && _iupYDone) md = 65;
                            Push(md);
                            break;
                        }

                    case 0x4B: Push(_ppem); break;                                      // MPPEM
                    case 0x4C: Push(_pointSize); break;                                 // MPS

                    // Whether a point is ON the curve or a control point for it. A program uses
                    // these to change the SHAPE at a size -- turning a curve into a corner where the
                    // curve would have nothing to sit on. Left as no-ops the glyph keeps its design
                    // curves and comes out visibly fatter than Windows draws it; '&' and '@' were
                    // the two that showed it.
                    case 0x80:                                                          // FLIPPT
                        {
                            for (int i = 0; i < _gs.Loop; i++)
                            {
                                int p = Pop();
                                if ((uint)p < _realPoints) _glyphZone.Tags[p] ^= TagOn;
                            }
                            _gs.Loop = 1;
                            break;
                        }
                    case 0x81: case 0x82:                                               // FLIPRGON/OFF
                        {
                            int high = Pop(), low = Pop();
                            for (int p = low; p <= high; p++)
                            {
                                if ((uint)p >= _realPoints) continue;
                                if (op == 0x81) _glyphZone.Tags[p] |= TagOn;
                                else _glyphZone.Tags[p] &= unchecked((byte)~TagOn);
                            }
                            break;
                        }

                    case 0x0F: Intersect(); break;                                      // ISECT

                    // ---- deltas ---------------------------------------------------------------------

                    case 0x5D: ApplyPointDeltas(0); break;                              // DELTAP1
                    case 0x71: ApplyPointDeltas(16); break;                             // DELTAP2
                    case 0x72: ApplyPointDeltas(32); break;                             // DELTAP3
                    case 0x73: ApplyControlValueDeltas(0); break;                       // DELTAC1
                    case 0x74: ApplyControlValueDeltas(16); break;                      // DELTAC2
                    case 0x75: ApplyControlValueDeltas(32); break;                      // DELTAC3

                    // ---- arithmetic and logic --------------------------------------------------------

                    case 0x50: { int b = Pop(), a = Pop(); Push(a < b ? 1 : 0); break; }     // LT
                    case 0x51: { int b = Pop(), a = Pop(); Push(a <= b ? 1 : 0); break; }    // LTEQ
                    case 0x52: { int b = Pop(), a = Pop(); Push(a > b ? 1 : 0); break; }     // GT
                    case 0x53: { int b = Pop(), a = Pop(); Push(a >= b ? 1 : 0); break; }    // GTEQ
                    case 0x54: { int b = Pop(), a = Pop(); Push(a == b ? 1 : 0); break; }    // EQ
                    case 0x55: { int b = Pop(), a = Pop(); Push(a != b ? 1 : 0); break; }    // NEQ
                    case 0x56: Push((RoundDistance(Pop()) & 127) == 64 ? 1 : 0); break;      // ODD
                    case 0x57: Push((RoundDistance(Pop()) & 127) == 0 ? 1 : 0); break;       // EVEN
                    case 0x5A: { int b = Pop(), a = Pop(); Push(a != 0 && b != 0 ? 1 : 0); break; }  // AND
                    case 0x5B: { int b = Pop(), a = Pop(); Push(a != 0 || b != 0 ? 1 : 0); break; }  // OR
                    case 0x5C: Push(Pop() == 0 ? 1 : 0); break;                              // NOT
                    case 0x60: { int b = Pop(), a = Pop(); Push(a + b); break; }             // ADD
                    case 0x61: { int b = Pop(), a = Pop(); Push(a - b); break; }             // SUB
                    // Both are 26.6 arithmetic and both divide by 64, and they do NOT agree on
                    // rounding: MUL rounds to the nearest 64th of a pixel, DIV throws the remainder
                    // away. That asymmetry is not a quirk to tidy up -- rounding DIV as well moves
                    // 'ô' at twelve pixels an em and 'ů' at thirteen off what Windows draws, because
                    // an accent program divides a measured width to centre something and the extra
                    // half-64th tips it. Both truncate towards ZERO, never downwards.
                    case 0x62:                                                               // DIV
                        {
                            int b = Pop(), a = Pop();
                            Push(b == 0 ? 0 : (int)(((long)a << 6) / b));
                            break;
                        }
                    case 0x63: { int b = Pop(), a = Pop(); Push(MulDiv(a, b, 64)); break; }  // MUL
                    case 0x64: Push(Math.Abs(Pop())); break;                                 // ABS
                    case 0x65: Push(-Pop()); break;                                          // NEG
                    case 0x66: Push(Floor(Pop())); break;                                    // FLOOR
                    case 0x67: Push(Ceil(Pop())); break;                                     // CEILING
                    case 0x8B: { int b = Pop(), a = Pop(); Push(Math.Max(a, b)); break; }     // MAX
                    case 0x8C: { int b = Pop(), a = Pop(); Push(Math.Min(a, b)); break; }     // MIN
                    case >= 0x68 and <= 0x6B:                                                 // ROUND[ab]
                        Push(RoundDistance(Pop(), linkType: op - 0x68, bare: true)); break;
                    case >= 0x6C and <= 0x6F: Push(Pop()); break;                             // NROUND[ab]

                    case 0x88:                                                               // GETINFO
                        {
                            int selector = Pop();
                            int result = 0;
                            // Version 35: the classic interpreter, which is the one GDI is. Saying
                            // 40 would make a ClearType-era face suppress its own horizontal hints,
                            // and GDI plainly does not -- its stems land on single columns.
                            if ((selector & 1) != 0) result |= s_rasterizerVersion;
                            // Rendering in greyscale, which GDI reports for ANTIALIASED_QUALITY.
                            // Also tried the other way, since GDI's greyscale is a supersample of a
                            // black-and-white rasterization and might have been expected to hint as
                            // one: saying no turns twenty-three disagreements with Windows into a
                            // hundred and five. It reports greyscale.
                            // AND WE NO LONGER CLAIM IT. The paragraph above is kept because it
                            // records a real measurement, but it was made against the greyscale
                            // RENDER, and the bit is only ever reachable on a pass that is not
                            // drawing ClearType -- which in this port is the BI-LEVEL MEASUREMENT
                            // pass, the one that stands in for GDI's own first pass over a glyph.
                            // Answering greyscale there put two faces on a branch GDI never takes:
                            //
                            //     bi-level fit against GDI's own, 62 glyphs at 12ppem
                            //       Segoe UI   11 of 62 exact, 640 of 1504 points differ in x
                            //       Consolas   11 of 62 exact, 1053 of 1751 points differ in x
                            //     answering NO
                            //       Segoe UI   62 of 62 exact, 0 points differ
                            //       Consolas   62 of 62 exact, 0 points differ
                            //
                            // Arial, Times, Verdana and Tahoma were already exact and are unmoved:
                            // they are pre-ClearType faces and do not ask. Segoe UI 'T' at 11ppem
                            // is the clearest case -- GDI fits it to x = 5 3 3 2 2 0 0 5, a stem
                            // exactly one pixel wide, and the greyscale branch of its prep writes
                            // 1.25px into that stem's control value, giving 7.25 4.25 4.25 3 3 0 0
                            // 7.25: a glyph two and a quarter pixels too wide inside a five-pixel
                            // advance. That is also why its measured advance disagreed with its own
                            // 'hdmx' (6 against 5), which is what first pointed here.
                            // WPF_CT_GREY=1 answers it again on the non-ClearType pass, which is
                            // what shipped; WPF_CT_GREY=always answers it on every pass.
                            if (!s_greyNever && (s_greyAlways || !ClearTypeInfo) && (selector & 32) != 0)
                                result |= 1 << 12;
                            if (ClearTypeInfo)
                            {
                                // WHAT GDI ANSWERS WHEN IT IS ACTUALLY DRAWING CLEARTYPE, which is
                                // not what it answers through GetGlyphOutline. That API renders
                                // greyscale and hints as a greyscale rasterizer, so a test of this
                                // bit against GGO output cannot see a face's ClearType branch even
                                // if it has one -- which is how the previous attempt concluded
                                // there was nothing here. Stage C is the only place it shows.
                                if ((selector & 64) != 0) result |= 1 << 13;    // ClearType enabled
                                // WPF_CT_COMPATINFO=0 answers NO. A DISCRETE PROBE, not a knob to
                                // sweep: bSetXform can only ever build four flag words, so a GDI
                                // ClearType draw can only present storage[2] as 2, 6, 130 or 134,
                                // and each is a different program. Trying them is enumeration.
                                if (s_compatWidthInfo && (selector & 128) != 0) result |= 1 << 14;
                                // NOT horizontal stripes. MEASURED off GDI with the GETINFO
                                // oracle (WhatGdiAnswersGetInfo): GDI leaves this bit CLEAR while
                                // drawing ClearType. It reads like it ought to be set -- the
                                // stripes are what ClearType is -- but the bit means the stripes
                                // run HORIZONTALLY, i.e. a display rotated a quarter turn, and
                                // this one is not.
                                if (s_stripeInfo && (selector & 256) != 0) result |= 1 << 15;
                                // Bit 18, ClearType SYMMETRIC RENDERING, "can impact the rendering
                                // of horizontal features" -- FreeType answers yes whenever it hints
                                // for an antialiased target. Answering it changes nothing measurable
                                // for Segoe UI (743,631 against 743,730, inside the noise), so it is
                                // left unanswered rather than guessed at. WPF_CT_SYMINFO=1 answers it.
                                // AND SYMMETRIC RENDERING IS SET, measured the same way. The note
                                // above -- that answering it changes nothing for Segoe UI -- was
                                // true and misleading: at version 35 the face never reaches the
                                // question, so an unexercised answer looked like an irrelevant
                                // one. WPF_CT_SYMINFO=0 turns it back off.
                                if (SymmetricRenderingAnswer && (selector & 2048) != 0) result |= 1 << 18;
                                // AND NOT 'GREYSCALE CLEARTYPE' (4096, result bit 19), which
                                // is measured and not merely unimplemented. THREE OF THE SIX
                                // specimen faces ask it -- Verdana, Tahoma and Arial all do,
                                // Segoe UI does not, which is to say every rule in this file
                                // was tuned on the one face that never asks. GDI answers it
                                // CLEAR in all three contexts the oracle can reach: drawing
                                // ClearType, through GetGlyphOutline, and through a stretched
                                // MAT2. Answering nothing is therefore answering correctly.
                            }
                            // NOT ClearType, and it was tried: saying so makes Segoe UI hint its
                            // stems to exactly one pixel where GDI's own geometry is a pixel and a
                            // half, and the page comes out too thin. GDI's stems measure the same
                            // width in both of its modes -- what changes between them is how a
                            // partly covered pixel is SHADED, not where the outline goes.
                            if (s_traceGetInfo)
                                Console.Error.WriteLine(
                                    $"      GETINFO selector={selector} -> {result}"
                                    + $"  (prep={_inPreProgram}, ct={ClearTypeInfo}, ppem={_ppem})");
                            // One line per query, in the same stream as WPF_YTRACE's point moves, so the
                            // ClearType and bi-level passes can be diffed against each other. That diff
                            // is what showed the MS core faces accumulating a RENDERING-MODE BITMASK in
                            // storage[2] out of these answers -- greyscale 1, ClearType 2, compatible
                            // widths 4, stripes 8, BGR 16, subpixel-positioned 64, symmetric 128 -- and
                            // branching their whole glyph program on `storage[2] == 2` and `== 6`.
                            if (s_yTrace) Console.Error.WriteLine("GETINFO sel=" + selector + " -> " + result);
                            Push(result);
                            break;
                        }

                    // ---- control flow ----------------------------------------------------------------

                    case 0x58:                                                               // IF
                        if (Pop() == 0) ip = SkipToElseOrEnd(code, ip);
                        break;
                    case 0x1B:                                                               // ELSE
                        ip = SkipToEnd(code, ip);
                        break;
                    case 0x59: break;                                                        // EIF

                    case 0x78:                                                               // JROT
                        {
                            int condition = Pop(), offset = Pop();
                            if (condition != 0) ip += offset - 1;
                            break;
                        }
                    case 0x79:                                                               // JROF
                        {
                            int condition = Pop(), offset = Pop();
                            if (condition == 0) ip += offset - 1;
                            break;
                        }
                    case 0x1C: ip += Pop() - 1; break;                                       // JMPR

                    case 0x2C:                                                               // FDEF
                        {
                            int id = Pop();
                            int body = ip;
                            ip = SkipToEndFunction(code, ip);
                            if ((uint)id < _functions.Length)
                            {
                                _functions[id] = new Function(code, body);
                                // THE SCALER READS THE FUNCTION'S BYTES. itrp_FDEF@+0x373b0
                                // memcmps every body it defines against a table of literal
                                // instruction sequences at fontdrvhost+0xa8770 and remembers the
                                // numbers that match (at most four, count at gs+0x1C4, list at
                                // +0x1C6) -- and itrp_SHP_Common applies its ClearType SHPIX
                                // suppression ONLY while one of those is running. See
                                // s_shpixFdefA/B for the two sequences.
                                if (MatchesSuppressedFdef(code, body, ip) && _suppressedFdefCount < 4)
                                { _suppressedFdefs[_suppressedFdefCount++] = id; }
                                // A SECOND, SEPARATE TABLE, at fontdrvhost+0xa8770. itrp_FDEF
                                // memcmps function 0's body against `45 23 46 60 20 B0 26` --
                                // RCVT SWAP GC[0] ADD DUP PUSHB[1] 38 -- and on a match sets bit
                                // 10 of gs+0x1c2. Times' function 0 IS that sequence, at fpgm
                                // offset 90, and its function 1 is the subtracting twin
                                // (`45 23 46 23 61 20 B0 26`) that plants both bowl shoulders.
                                // Arial and Consolas match too; Verdana, Tahoma and Segoe UI do
                                // not. See _mdOnePixelIs65 for what bit 10 goes on to do.
                                if (id == 0) _fdefAddHelper = StartsWith(code, body, ip, s_fdefAddHelper);
                                // The same table's other two sequences, checked for functions
                                // 0, 1, 2, 4, 7 and 8, set bit 9 instead. Verdana Bold and Tahoma
                                // define 0/1/2/4/8 all starting `01 B0 18 43 58` (SVTCA[x];
                                // PUSHB 24; RS; IF) and Segoe UI defines 0/1/2/4/7/8 starting
                                // `01 18 B0 18 43 58` (the same with an RTG) -- every one of them
                                // a mode dispatcher keyed on storage[24]. Times and Arial match
                                // neither.
                                if (id < 3 || id == 4 || id == 7 || id == 8)
                                    _fdefModeDispatch |= StartsWith(code, body, ip, s_fdefDispatchA)
                                                      || StartsWith(code, body, ip, s_fdefDispatchB);
                                // WPF_FDEF_DUMP=1: every function this face defines, with the
                                // first bytes of its body -- the only way to tell WHICH function
                                // number carries a recognised sequence, since the numbers come off
                                // the stack and cannot be read out of the fpgm statically.
                                if (s_fdefDump)
                                {
                                    var sb = new System.Text.StringBuilder($"FDEF fn{id,-4} len{ip - body,-5}");
                                    for (int k = body; k < ip && k < body + 10; k++)
                                        sb.Append($" {code[k]:X2}");
                                    Console.Error.WriteLine(sb.ToString());
                                }
                            }
                            break;
                        }
                    case 0x89:                                                               // IDEF
                        {
                            int id = Pop();
                            int body = ip;
                            ip = SkipToEndFunction(code, ip);
                            if ((uint)id < _instructionDefs.Length)
                                _instructionDefs[id] = new Function(code, body);
                            break;
                        }
                    case 0x2D:                                                               // ENDF
                        {
                            if (callDepth == 0) return true;
                            CallFrame frame = calls[--callDepth];
                            _callDepth = callDepth;
                            if (--frame.Repeats > 0)
                            {
                                calls[callDepth++] = frame; _callDepth = callDepth;
                                code = frame.Code;
                                ip = frame.Start;
                                break;
                            }
                            if (callDepth < _callDelta.Length && _callDelta[callDepth])
                            { _callDelta[callDepth] = false; if (_deltaFdefDepth > 0) _deltaFdefDepth--; }
                            if (callDepth < _callRearm.Length && _callRearm[callDepth])
                            { _callRearm[callDepth] = false; _phaseApplied = _callPhaseWas[callDepth]; }
                            code = frame.ReturnCode;
                            ip = frame.ReturnIp;
                            break;
                        }
                    case 0x2B:                                                               // CALL
                        {
                            int id = Pop();
                            if ((uint)id >= _functions.Length || !_functions[id].Defined) break;
                            if (callDepth >= calls.Length) return false;
                            calls[callDepth++] = new CallFrame(_functions[id].Code, _functions[id].Start,
                                                               code, ip, 1); _callDepth = callDepth;
                            _callDelta[callDepth - 1] = IsSuppressedFdef(id);
                            if (_callDelta[callDepth - 1]) _deltaFdefDepth++;
                            // THE PHASE PASS IS RE-ARMED AROUND A CALL TO FUNCTIONS 0, 1, 2, 4,
                            // 7 AND 8 -- the same set itrp_FDEF signature-checks. The dispatch
                            // reads `cmp #0x40 b.ge / cmp #2 b.gt / cmp #4 b.eq / cmp #7 b.ge /
                            // cmp #8 b.gt`, so 0..2, 4, 7 and 8 all reach the guard at
                            // itrp_CALL@140036450:
                            //     if ((globals[0x1c0] & 1) && !(globals[0x88] & 4)
                            //         && (globals[0x1c2] & 0x200))
                            //     { saved = elem[0x60]; elem[0x60] = 0; restoreAfter = 1; }
                            // elem[0x60] is the phase-DONE flag -- InitPhaseControl zeroes it,
                            // ExecutePhaseControl sets it, and itrp_IUP will not run the pass
                            // while it is set. So inside a recognised mode dispatcher the
                            // compatible-width phase runs AGAIN at the next IUP, and the flag goes
                            // back to what it was on return. WPF_CT_PHASE_REARM=0 turns it off.
                            // <para>NOT EXERCISED BY THE SIX SPECIMEN FACES, and measured at
                            // exactly zero because of it. The three faces that set bit 9 -- Verdana
                            // Bold, Tahoma and Segoe UI, whose functions 0/1/2/4/7/8 are all mode
                            // dispatchers keyed on storage[24] -- never CALL any of those numbers
                            // from a glyph program (checked over 17 glyphs each at 16ppem; Tahoma
                            // reaches for 59 and 133, Segoe UI for 73, 77 and 89). Times and Arial
                            // do call 0 and 1 constantly but match the fn-0 signature instead, so
                            // they never set bit 9. Kept because it is what the binary does and
                            // fonts outside this corpus will hit it -- but it has never been
                            // checked against a pixel, so treat it as a reading, not a result.</para>
                            _callRearm[callDepth - 1] = s_phaseRearm
                                && (id < 3 || id == 4 || id == 7 || id == 8) && _fdefModeDispatch
                                && ClearTypeInfo && !NativeClearTypeMode && !BiLevelPass;
                            if (_callRearm[callDepth - 1])
                            { _callPhaseWas[callDepth - 1] = _phaseApplied; _phaseApplied = false; }
                            code = _functions[id].Code;
                            ip = _functions[id].Start;
                            break;
                        }
                    case 0x2A:                                                               // LOOPCALL
                        {
                            int id = Pop(), count = Pop();
                            if (count <= 0) break;
                            if ((uint)id >= _functions.Length || !_functions[id].Defined) break;
                            if (callDepth >= calls.Length) return false;
                            calls[callDepth++] = new CallFrame(_functions[id].Code, _functions[id].Start,
                                                               code, ip, count); _callDepth = callDepth;
                            _callDelta[callDepth - 1] = IsSuppressedFdef(id);
                            if (_callDelta[callDepth - 1]) _deltaFdefDepth++;
                            code = _functions[id].Code;
                            ip = _functions[id].Start;
                            break;
                        }

                    case 0x4F: Pop(); break;                                                 // DEBUG

                    default:
                        // An opcode the face defined for itself with IDEF.
                        if (_instructionDefs[op].Defined)
                        {
                            if (callDepth >= calls.Length) return false;
                            calls[callDepth++] = new CallFrame(_instructionDefs[op].Code,
                                                               _instructionDefs[op].Start, code, ip, 1); _callDepth = callDepth;
                            code = _instructionDefs[op].Code;
                            ip = _instructionDefs[op].Start;
                            break;
                        }

                        // Otherwise it is one we do not implement, and SKIPPING IT IS NOT SAFE.
                        // Nearly every instruction takes arguments off the stack; stepping over one
                        // leaves everything it should have consumed behind, so each instruction after
                        // it reads its neighbour's values and moves points to nonsense. That is not a
                        // theory -- with this branch falling through, an 'i' in Arial came out as its
                        // own dot and an 'l' as a full stop, while Segoe UI (the only face the hinter
                        // was ever measured against) was perfect.
                        //
                        // Giving up returns the UNHINTED outline, which is the shape the face
                        // actually contains. Worse text than a correct fitting, far better than a
                        // confident wrong one.
                        NoteUnimplemented(op);
                        return false;
                }
            }
        }

        /// <summary>Opcodes met that this interpreter does not implement. Kept so the answer to
        /// "why is this face not hinted" is a list rather than an investigation; WPF_HINT_TRACE=1
        /// prints each one the first time it is seen.</summary>
        private static readonly System.Collections.Generic.HashSet<byte> s_unimplemented = new();

        /// <summary>WHY DIGITS AND LETTERS WANT DIFFERENT GRIDS, as far as it has been taken.
        /// <para>Segoe UI classifies its own stems by control value: digits fit from cvt[137] and
        /// cvt[138], capitals from cvt[125] and cvt[126], lowercase from cvt[131] and cvt[132].
        /// So the digit/letter split is visible in the FONT's data and not only in the character,
        /// which is what a principled discriminator would need.</para>
        /// <para>Sampled at the instruction, the digit errors under mode 6 are MDAP[r] landing on
        /// a lamp boundary where GDI does not: '0' pt10 arrives 0.500 and we round to 0.660 (2/3)
        /// where GDI has 0.406; '8' pt0 arrives 0.530, ours 0.660, GDI 0.390; '3' pt15 arrives
        /// 4.270, ours 4.330 (13/3), GDI 4.094. We sit about one lamp to the RIGHT, every time,
        /// and GDI's answers are on no grid at all.</para>
        /// <para>The obvious next move is therefore to stop rounding x, and it is WRONG. Measured:
        /// WPF_CT_NOROUND_X improves the parity metric under mode 6 by 86,454 (3,271,235 ->
        /// 3,184,781) and costs the live window 358,518 (985,204 -> 1,343,722), with the position
        /// half going 135,981 -> 433,676. Unrounded stems land anywhere. The two metrics disagree
        /// again and the window is the objective.</para>
        /// <para>Keying the grid on the control value INDEX would fit all of this and is not
        /// offered: cvt[137] means digits in this face and nothing anywhere else.</para></summary>

        /// <summary>WPF_STEM_NATURAL: target the natural stem width plus half a pixel, which is
        /// what GDI measurably draws, instead of a rounded control value plus s_stemFat.</summary>
        private static readonly bool s_stemNatural =
            Environment.GetEnvironmentVariable("WPF_STEM_NATURAL") == "1";

        private static readonly bool s_traceHint =
            Environment.GetEnvironmentVariable("WPF_HINT_TRACE") == "1";

        /// <summary>WPF_CT_DELTA_PREP=1: exempt the PRE-PROGRAM from the ClearType delta gate,
        /// as this code did before. See the call site for what it costs.</summary>
        private static readonly bool s_deltasFreeInPrep =
            Environment.GetEnvironmentVariable("WPF_CT_DELTA_PREP") == "1";

        /// <summary>WPF_CT_ALIGNRP_TOUCHED=0 restores the old behaviour. See the call site.</summary>
        private static readonly bool s_alignrpTouchedOnly =
            Environment.GetEnvironmentVariable("WPF_CT_ALIGNRP_TOUCHED") != "0";

        /// <summary>WPF_CT_SCFS_TOUCHED=0 restores the old behaviour. See the SCFS call site --
        /// and note that WHERE GDI implements this is still unknown.</summary>
        /// <summary>WPF_CT_SCFS_X=1: drop an SCFS on the CLEARTYPE axis onto a point not
        /// already touched there, the same way the non-ClearType axis is treated. SHIPPED; see
        /// the comment at the SCFS opcode.</summary>
        private static readonly bool s_scfsXToo =
            Environment.GetEnvironmentVariable("WPF_CT_SCFS_X") != "0";

        private static readonly bool s_scfsTouchedOnly =
            Environment.GetEnvironmentVariable("WPF_CT_SCFS_TOUCHED") != "0";

        /// <summary>WPF_CT_MD65=0 stops MD reporting an exact pixel as 65/64 after IUP.</summary>
        private static readonly bool s_md65 =
            Environment.GetEnvironmentVariable("WPF_CT_MD65") != "0";

        private static readonly bool s_mdrpTrace =
            Environment.GetEnvironmentVariable("WPF_MDRP_TRACE") == "1";

        /// <summary>Sixty-fourths to add to a control-value stroke weight on the x axis, and the ppem
        /// range to add them over. Diagnostic only -- see the note in MoveIndirectRelative.
        /// <para>NOW ZERO. The +6/64 was read off GDI's own coordinates for 'H' at 12ppem under
        /// XHintMode 6 (70/64 = the control value's 64/64 plus six), but we SHIP XHintMode 5, and
        /// the note below already says that under mode 5 the addition "disappears into the
        /// quantiser". Measured under the mode we actually ship, it does not disappear -- it
        /// costs. Over every size from 8 to 24 (306 rows) turning it off is
        /// 19,753,494 -> 19,573,677, and the breakdown is as clean as a change gets:
        /// 21 rows better, ZERO worse, 285 unchanged; only the band's own sizes move
        /// (10 -19,220, 11 -52,348, 12 -59,405, 13 -48,844); and EVERY face improves
        /// (Arial -77,020, Consolas -66,819, Segoe UI -22,521, Verdana -8,664, Times -4,793).
        /// The specimen agrees (5,485,079 -> 5,406,454) and the edge oracle is untouched --
        /// Verdana 'H'/'I'/'l'/'n' at 12ppem are bit-identical either way, which is the same
        /// quantiser fact from the other side. This also matches what the note below concluded
        /// on its own evidence: the width is PER-GLYPH, 'I' ends 5/64 BELOW the control value
        /// where 'H' ends 6/64 above it, so "any rule that widens every stem is wrong before it
        /// starts".</para>
        /// <para>AND YET IT STAYS AT SIX, because the weight sum is the WRONG INSTRUMENT for it:
        /// turning it off fails 166 tests -- TheWholeRepertoire_CoversTheSamePixelsAsWindows at
        /// ppem 10, 11, 12 and 13 (the band's own sizes) and EveryGlyph_CoversTheSamePixelsAsWindows
        /// for 'k', 'N' and others. Those ratchets compare our PIXELS against Windows' per glyph,
        /// which is stricter and more direct than a sum of |d| over a specimen, and they say the
        /// +6/64 makes individual glyphs match BETTER even while the aggregate weight gets worse.
        /// The comment further down predicted exactly this: "The per-glyph parity ratchets do --
        /// which is how the half-pixel SHPIX cap was caught". So the weight sum can improve while
        /// pixel parity regresses, and where they disagree the ratchets win.
        /// WPF_CT_STEMFAT=0 to re-measure.</para></summary>
        /// <summary>WPF_CT_STEMSUBPX=1: take a black stem's ClearType width from the OUTLINE,
        /// floored to a whole subpixel. See the note at the use site.</summary>
        private static readonly bool s_stemSubpxExact =
            Environment.GetEnvironmentVariable("WPF_CT_STEMSUBPX_EXACT") != "0";

        private static readonly int s_stemSubpxRound =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_STEMSUBPX_ROUND"), out int sr) ? sr : 0;

        private static readonly bool s_stemSubpxAdj =
            Environment.GetEnvironmentVariable("WPF_CT_STEMSUBPX_ADJ") != "0";

        private static readonly bool s_stemSubpx =
            Environment.GetEnvironmentVariable("WPF_CT_STEMSUBPX") == "1";

        /// <summary>Widen a black distance by this many 64ths inside the ppem band below.
        /// <para>ZERO once the phase pass is doing the work. This was compensation: mode 7
        /// scaled a fitted outline onto its advance about each feature's CENTRE, which pulls a
        /// stem's two sides together and leaves stems thin in the band where a stem is about
        /// one pixel. GDI's phase moves both sides of a paired stem by the SAME amount, so the
        /// width survives and the fattening is pure harm -- the sweep is monotonic in it
        /// (0: 4,283,877  2: 4,312,425  4: 4,336,228  6: 4,368,478  8: 4,392,620), the holdout
        /// agrees (15,662,723 -> 15,464,384) and 128 ratchets improve against 28.</para></summary>
        private static readonly int s_stemFat =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_STEMFAT"), out int sf) ? sf
            : Environment.GetEnvironmentVariable("WPF_CT_PHASE") != "0" ? 0 : 6;
        private static readonly int s_stemFatLo =
        // TEN, not eleven. Re-swept after the rasterizer version and the symmetric answer changed
        // the geometry underneath it: 10 measures 77,051 against 11's 77,111, and the ink error at
        // ppem 10 halves (150 -> 68). NOT taken to 9, though 9 measures identically -- identically
        // is all the suite can say, because it starts at ppem 10, and a bound set outside the
        // range that can see it is a guess wearing a measurement's clothes.
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_STEMFAT_LO"), out int sl) ? sl : 10;
        /// <summary>Whether the stroke-weight correction also applies to a ROUNDED MIRP, which is
        /// spacing rather than weight. MEASURED AND REJECTED: the window goes 1,216,292 to
        /// 1,233,117, and it does not touch the case that prompted it -- 'H' at 12ppem is
        /// unchanged, pixel for pixel, because its second stem is placed by IP and MDAP and not
        /// by a control value at all. Kept so the test does not have to be rebuilt to repeat it.
        /// WPF_CT_STEMFAT_ROUNDED=1.
        /// <para>'H' IS LIGHT AND 'l' IS NOT, and the reason written here was wrong. Dumped,
        /// 'H' at 12ppem takes MIRP 0xE9 -- round=False. Both take an UNROUNDED MIRP, so the
        /// rounded/unrounded gate is not what separates them.</para>
        /// <para>WHAT ACTUALLY SEPARATES THEM IS WHETHER THE CUT-IN REJECTS THE CONTROL VALUE,
        /// and the whole 11-13 band hangs off it. Segoe UI's pre-program forces cvt[126] to
        /// EXACTLY 1.0 pixel at every size -- the dump shows 1.0000px at ppem 12 (ratio 0.0833)
        /// and 1.0000px at ppem 16 (ratio 0.0625), a fixed pixel rather than a fixed design
        /// width. Then:</para>
        /// <para>at 12ppem  cvt 1.0000, outline 0.9844, apart by 0.016 -- inside the shrunk
        /// cut-in of 2.25/16, so the control value is TAKEN and the stem is 1.00 where GDI draws
        /// 1.1875. At 16ppem  cvt 1.0000, outline 1.3125, apart by 0.313 -- outside it, so the
        /// control value is REFUSED, the outline distance is used, and our ink per row matches
        /// GDI's exactly (2.41 against 2.41, 8.04 against 8.04 on the crossbar).</para>
        /// <para>So we agree with GDI precisely when we IGNORE this control value and disagree
        /// when we honour it. That is the whole of the anomaly: error per inked pixel is 27.9,
        /// 26.3 and 27.5 at ppem 11, 12 and 13 against 11.1, 10.4 and 9.8 at 14, 15 and 16, and
        /// 11-13 are the sizes where a 1.0px control value sits close enough to the outline to
        /// be accepted.</para>
        /// <para>The target is known: +12/64 puts the stem on 1.1875 = 19/16, which is GDI's
        /// number to four decimals and lands on the sixteenth grid ClearType rounds against.
        /// The shipped +6/64 does NOTHING -- H at 12ppem renders 1.83 ink per row at stem fat 0
        /// and 1.83 at 6, identical, because the addition quantises away -- and +12/64 gets H
        /// right while making ppem 11 and 12 worse overall (92,701 -> 104,594 and 96,586 ->
        /// 106,042), because most glyphs do not want the extra width. A constant added to every
        /// stem is still the wrong instrument. What is wanted is the rule that produces 19/16
        /// for the stems GDI widens and leaves the others alone.</para>
        /// <para>THE TARGET IS NOT INFERRED, IT IS READ OFF GDI'S LAMPS. At 12ppem GDI's 'H'
        /// stem is 73 153 255 197 111 36, which is byte for byte the synthetic-font oracle's
        /// w=1.1875 row -- so GDI's stem is 19/16 exactly, not approximately. Ours is
        /// 36 111 197 197 111 36: narrower AND differently phased, straddling two lamps where
        /// GDI saturates one. At 16 and 17ppem our lamps are byte-identical to GDI's.</para>
        /// <para>GDI HOLDS 19/16 ACROSS PPEM 11 TO 15 (ink per row for two stems: 2.157 at 11,
        /// 12, 13, 14 and 15, then 2.490 at 16 and 2.824 at 17). Ours wobbles -- 2.157 at 11 and
        /// 14, 1.799 at 12, 1.978 at 13, 2.336 at 15 -- because the cut-in takes the control
        /// value at 11-13 and refuses it from 14 up. Glyph HEIGHTS match GDI exactly at every
        /// size (WPF_HEIGHT_REPORT), so this is stem width alone.</para>
        /// <para>AND IT IS NOT THE FONT PROGRAM. Traced, Segoe UI's pre-program asks GETINFO for
        /// selectors 1, 2, 4 and 32 only -- rasterizer version, rotated, stretched, greyscale.
        /// It never asks whether ClearType is on, so it CANNOT branch on it, so the control
        /// values it computes are the same ones it computes for the bi-level pass -- and ours
        /// already reproduce GetGlyphOutline there for 55 of 62 glyphs. Our cvt[126] of 1.0 is
        /// almost certainly GDI's cvt[126] too, and the 19/16 is something GDI's RASTERIZER does
        /// with it, not something the face asked for.</para>
        /// <para>Tried and rejected against the repertoire, all worse than leaving it alone:
        /// +12/64 on every taken control value (2,237,335), the same with the cut-in divisor at
        /// 8 instead of 16 (2,408,207), the divisor alone (2,360,273), +12/64 restricted to
        /// stems landing on exactly 1.0px (2,225,357), +8/64 so restricted (2,201,229), and
        /// answering GETINFO's greyscale query yes (2,402,211), against a baseline of 2,180,771.
        /// Every one of them fixes 'H' and costs more elsewhere, which says GDI is not widening
        /// by a constant at all.</para>
        /// <para>WIDENING IS ONLY HALF OF IT; THE OTHER HALF IS PHASE. GDI's pattern SATURATES a
        /// lamp and ours is symmetric about a lamp boundary -- a stem sitting half a lamp over,
        /// not merely a narrow one.</para>
        /// <para>AND THE WIDTH IS PER-GLYPH, not per-face and not per-size -- two plain
        /// cap-height stems, two different widths, from identical MIRP inputs. Any rule that
        /// widens every stem is wrong before it starts, which is what the measurements above
        /// were saying.</para>
        /// <para>THE NUMBERS ABOVE WERE READ OFF LAMP PATTERNS AND ARE SUPERSEDED. Matching a
        /// glyph's lamps against the synthetic bar table compares two different PHASES and is
        /// only approximate; it gave 'H' as 19/16 and 'I' as 1.0. SolveTheXCoordinatesGdiFitted
        /// (WPF_SOLVEGLYPH=char@ppem) fits GDI's actual coordinates in 64ths, which is sound
        /// because the bar solver already showed our rasterizer reproduces GDI's lamps at rms 0
        /// given the right outline. Its answer, under XHintMode 6 at 12ppem:</para>
        /// <para>'l'  ours 1.000, 2.094 -> GDI 1.000, 2.094.  EXACT.<br/>
        /// 'H'  ours 1.000, 2.094, 6.656, 7.750 -> GDI 1.000, 2.094, 6.922, 8.094: the LEFT stem
        /// exact and the right one 0.266 too far left -- stem SPACING, not stem width.<br/>
        /// 'I'  ours 1.000, 2.094 -> GDI 1.000, 1.922: ours 0.172 too WIDE.<br/>
        /// 'n'  nine of eleven coordinates exact.</para>
        /// <para>So GDI's 'H' stem is 70/64 and its 'I' stem is 59/64. And 70/64 is exactly the
        /// control value of 64/64 plus s_stemFat's 6/64 -- which says the +6/64 is RIGHT, and
        /// that the note above calling it a no-op was measuring it under mode 5, where the stem
        /// sits half a lamp over and the addition disappears into the quantiser. Under mode 6 it
        /// lands on GDI's number exactly.</para>
        /// <para>What is still unexplained is 'I': same control value, same outline, same
        /// opcode, and GDI ends 5/64 BELOW the control value where 'H' ends 6/64 above it.</para>
        /// <para>Held against GDI at 12ppem: mode 5 gets 'l' exactly and misses 'H' and 'I'; mode
        /// 6 gets 'H' and 'l' exactly and makes 'I' too wide. Mode 6 is not right at 12ppem, it is
        /// right about the glyphs whose stems GDI puts on a lamp boundary and wrong about the rest
        /// -- and at 12ppem more glyphs fall the first way than the second. That is the whole of
        /// its 159,129, and the reason it does not survive to other sizes.</para>
        /// <para>That is why forcing width alone never pays, and it is measured: taking the
        /// control value always (full cut-in) costs 3,116,340, and doing that with +12/64 on top
        /// costs 3,125,569, against 2,180,771. Making every stem 19/16 at the WRONG phase is worse
        /// than leaving them alone.</para>
        /// <para>It also explains why XHintMode 6 wins at 12ppem and nowhere else: rounding on the
        /// lamp grid is what puts a stem edge on a lamp boundary, which is the phase GDI has. The
        /// open question is what GDI actually does -- it is not the face (the pre-program never
        /// asks whether ClearType is on) and it is not a constant width -- and the answer has to
        /// set the position and the width together.</para>
        /// <para>Extending the correction to rounded MIRPs is therefore exactly what 'H' needs,
        /// and it is still worse across the repertoire: 54,934 -> 56,393, 32 cases worse against
        /// 8 better. Re-measured after the BGRA fix, so this rejection rests on a comparison that
        /// has been checked. What 'H' needs is not a constant added to every rounded stem.</para>
        /// <para>(The allowance metric reads 54,934 both before and after that fix, so the 442
        /// ratcheted cases never used the swapped channels -- only stage C and CR did. Knobs
        /// rejected against WPF_ALLOW_REPORT do not need revisiting.)</para></summary>
        /// <summary>WPF_CT_NOROUND_X: leave a control-value distance unrounded in the ClearType
        /// direction, which is what VTT shows Microsoft's rasterizer doing.</summary>
        private static readonly bool s_mdapNoRoundX =
            Environment.GetEnvironmentVariable("WPF_CT_MDAP_NOROUND") == "1";

        private static readonly bool s_noRoundX =
            Environment.GetEnvironmentVariable("WPF_CT_NOROUND_X") == "1";

        private static readonly bool s_stemFatRounded =
            Environment.GetEnvironmentVariable("WPF_CT_STEMFAT_ROUNDED") == "1";

        /// <summary>Whether the stroke-weight correction is skipped on a MIRP that keeps a minimum
        /// distance. OFF: MEASURED AND REJECTED.
        /// <para>'l' takes its stroke weight through MIRP 0xE1 and 'o' through 0xE9, which keeps a
        /// minimum -- so the flag looked like a way to give the correction to the one that wants it
        /// and not the other. Read lamp by lamp it does exactly that: 'o' at 12ppem has its RIGHT
        /// stroke land on GDI's lamps precisely, and 'l' is untouched and still exact. And 'n',
        /// whose stems also keep a minimum, loses the correction and goes light. Window 1,201,143
        /// to 1,277,355, regular@12 structural 1,673 to 1,759. Both metrics agree, so it is not a
        /// case of one being blind: the correction genuinely belongs to some keepMin strokes and
        /// not others, and the opcode does not separate them.</para></summary>
        private static readonly bool s_stemFatNoMin =
            Environment.GetEnvironmentVariable("WPF_CT_STEMFAT_NOMIN") == "1";

        /// <summary>Restrict the stroke-weight correction to ONE control value. DIAGNOSTIC ONLY,
        /// and it must stay that way.
        /// <para>Segoe UI's 'l' takes its weight from cvt[132] and needs the correction; 'o' takes
        /// its from cvt[131] and does not -- turn the correction off and 'o's right stroke lands on
        /// GDI's lamps exactly. Restricting to 132 is worth 6,426 on the window (1,201,169 ->
        /// 1,194,743) and is neutral over the repertoire (41,072 -> 41,057, with @13 better by 115
        /// and @11 worse by 108).
        /// <para>It is still not shippable. A control value INDEX is a fingerprint of one font, not
        /// a rule: 132 means nothing in Arial, and a face where 132 happened to be a stroke weight
        /// would get an arbitrary sixth of a pixel for no reason. What is needed is whatever
        /// PROPERTY of that control value GDI is reading, and these are now eliminated: the opcode
        /// (0xE1 against 0xE9 -- see StemFatNoMin), the minimum-distance flag, the ppem, and the
        /// value itself. 126, 131 and 132 all arrive at prep's ROUND holding 0.9688 and all leave
        /// it holding 1.0, so nothing in the number distinguishes them.</para></summary>
        private static readonly int s_stemFatCvt =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_STEMFAT_CVT"), out int sc) ? sc : -1;

        /// <summary>Whether the stroke-weight correction is limited to a point on a STRAIGHT part of
        /// the outline -- neither neighbour in its contour an off-curve control point. OFF, and the
        /// closest thing to an answer this question has had.
        /// <para>It is the only candidate that does the right thing for all three glyphs I can read
        /// directly: 'l' stays exact, 'n's left stem stays exact, and 'o's right stroke BECOMES
        /// exact -- which is what turning the correction off entirely does for 'o', without giving
        /// up 'l' and 'n' the way the opcode gate did. It is geometry rather than a font's control
        /// value number, so unlike WPF_CT_STEMFAT_CVT it could ship.</para>
        /// <para>The measurements refuse it. Window 1,201,169 -> 1,196,814, structural 41,072 ->
        /// 41,113, and the ink ratio splits: regular@11 goes 1.0147 to exactly 1.0000 while @12
        /// goes 0.9769 to 0.9620 and @13 0.9921 to 0.9769. Three instruments, three answers.</para>
        /// <para>Reading 'o' says why, and says the premise is wrong. GDI's own bowl is ASYMMETRIC:
        /// its left stroke carries 825 of ink over six lamps and its right 707 over five, and our
        /// uncorrected right stroke matches that 707 exactly. Equal geometry cannot render
        /// asymmetrically, so GDI's 'o' is not a thicker stroke, it is a bowl sitting elsewhere on
        /// the lamp grid. This whole line of attack has been about WEIGHT and 'o' is about
        /// POSITION.</para></summary>
        private static readonly bool s_stemFatStraight =
            Environment.GetEnvironmentVariable("WPF_CT_STEMFAT_STRAIGHT") == "1";

        private static readonly bool s_stemFatExact =
            Environment.GetEnvironmentVariable("WPF_CT_STEMFAT_EXACT") == "1";
        private static readonly int s_stemFatHi =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_STEMFAT_HI"), out int sh) ? sh : 13;

        internal static System.Collections.Generic.IEnumerable<byte> UnimplementedOpcodes
        {
            get { lock (s_unimplemented) return new System.Collections.Generic.List<byte>(s_unimplemented); }
        }

        private static void NoteUnimplemented(byte op)
        {
            bool first;
            lock (s_unimplemented) first = s_unimplemented.Add(op);
            if (first && s_traceHint)
                Console.Error.WriteLine($"[hint] unimplemented opcode 0x{op:X2} -- giving up on this glyph");
        }

        /// <summary>Instruction-level trace of ONE glyph's program: the opcode, the stack it is
        /// about to read, and where the points are afterwards. Turned on by WPF_HINT_DUMP=1 around
        /// a single Hint call, which is the only way to see WHICH instruction moves a point to the
        /// wrong place -- an outline that comes out wrong says only that one of them did.</summary>
        private static readonly bool s_phaseAfterIup =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_AFTERIUP") == "1";

        private static readonly bool s_iupProbe =
            Environment.GetEnvironmentVariable("WPF_IUPX_PROBE") == "1";

        internal static bool s_dumpGlyph =
            Environment.GetEnvironmentVariable("WPF_HINT_DUMP") == "1";   // one glyph at a time

        private bool _dumpActive;

        /// <summary>How many points the instruction dump shows per axis. WPF_HINT_DUMP_POINTS.</summary>
        private static readonly int s_dumpPoints =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_HINT_DUMP_POINTS"), out int dp) && dp > 0 ? dp : 8;

        /// <summary>Where the program actually LEFT the glyph. See the call site.</summary>
        internal void DumpFinal()
        {
            var sb = new System.Text.StringBuilder("FINAL y=[");
            for (int i = 0; i < System.Math.Min(_glyphZone.CurY.Length, s_dumpPoints); i++)
                sb.Append((_glyphZone.CurY[i] / 64f).ToString("0.##")).Append(' ');
            sb.Append("] x=[");
            for (int i = 0; i < System.Math.Min(_glyphZone.CurX.Length, s_dumpPoints); i++)
                sb.Append((_glyphZone.CurX[i] / 64f).ToString("0.##")).Append(' ');
            sb.Append(']');
            Console.Error.WriteLine(sb.ToString());
        }

        private int[]? _dumpPrevX;
        private string _dumpPrevOp = "";

        /// <summary>WPF_HINT_MOVES=1: after each instruction, ATTRIBUTE the points it moved in x to
        /// it, as `MOVED by <instruction>: pt N a -> b`.
        /// <para>Without this the dump prints the whole coordinate array before every instruction
        /// and leaves the reader to diff consecutive lines -- which is fine until the font runs its
        /// work inside a FUNCTION called several times, at which point every line carries the same
        /// instruction index and the changes cannot be attributed at all. Arial Bold 'X' at 20ppem
        /// does exactly that: its second control-value pass is a loop at index 453, and hand-diffing
        /// it attributed one instruction's move to another. The diff is taken against the previous
        /// call, so the line is printed under the instruction that caused it.</para></summary>
        private static readonly bool s_dumpMoves =
            Environment.GetEnvironmentVariable("WPF_HINT_MOVES") == "1";

        private void DumpStep(byte op, int at)
        {
            if (s_dumpMoves)
            {
                int n = _glyphZone.CurX.Length;
                if (_dumpPrevX is null || _dumpPrevX.Length != n) _dumpPrevX = new int[n];
                else
                {
                    var moved = new System.Text.StringBuilder();
                    for (int i = 0; i < n; i++)
                        if (_dumpPrevX[i] != _glyphZone.CurX[i])
                            moved.Append($" pt{i} {_dumpPrevX[i]}->{_glyphZone.CurX[i]}");
                    if (moved.Length > 0)
                        Console.Error.WriteLine($"   MOVED by {_dumpPrevOp}:{moved}");
                }
                System.Array.Copy(_glyphZone.CurX, _dumpPrevX, n);
                _dumpPrevOp = $"{at,5}: {OpName(op)}";
            }
            var sb = new System.Text.StringBuilder();
            sb.Append($"{at,5}: {OpName(op)} (0x{op:X2})  stack[");
            for (int i = System.Math.Max(0, _top - 4); i < _top; i++)
                sb.Append(_stack[i]).Append(' ');
            sb.Append($"]  pv=({_gs.ProjX},{_gs.ProjY}) fv=({_gs.FreeX},{_gs.FreeY}) rp0={_gs.Rp0} rp1={_gs.Rp1} rp2={_gs.Rp2}  ");
            // BOTH AXES. This printed y only, which is no use at all for the direction most of the
            // work here is about -- an x-direction bug shows as an unchanging y column. The count is
            // WPF_HINT_DUMP_POINTS wide because eight points do not reach the interesting ones in a
            // glyph like 'o'.
            sb.Append("x=[");
            for (int i = 0; i < System.Math.Min(_glyphZone.CurX.Length, s_dumpPoints); i++)
                sb.Append((_glyphZone.CurX[i] / 64f).ToString("0.##")).Append(' ');
            sb.Append("] y=[");
            for (int i = 0; i < System.Math.Min(_glyphZone.CurY.Length, s_dumpPoints); i++)
                sb.Append((_glyphZone.CurY[i] / 64f).ToString("0.##")).Append(' ');
            // And the TOUCH flags, because "why did IUP not carry that point" is unanswerable
            // without them: an untouched point that did not move is a bug, a touched one is the
            // program's decision.
            sb.Append("] t=[");
            for (int i = 0; i < System.Math.Min(_glyphZone.Tags.Length, s_dumpPoints); i++)
            {
                byte t = _glyphZone.Tags[i];
                sb.Append((t & TagTouchX) != 0 ? 'X' : '.').Append((t & TagTouchY) != 0 ? 'Y' : '.');
                sb.Append(' ');
            }
            sb.Append(']');
            Console.Error.WriteLine(sb.ToString());
        }

        private static string OpName(byte op) => op switch
        {
            0x00 or 0x01 => "SVTCA",
            0x2E => "MDAP",
            0x2F => "MDAP[r]",
            0x3E => "MIAP",
            0x3F => "MIAP[r]",
            0xC0 or (>= 0xC0 and <= 0xDF) => "MDRP",
            >= 0xE0 => "MIRP",
            0x30 or 0x31 => "IUP",
            0x2B => "CALL",
            0x2A => "LOOPCALL",
            0x39 => "IP",
            0x32 or 0x33 or 0x34 or 0x35 or 0x36 or 0x37 => "SHP/SHC/SHZ",
            0x3A or 0x3B => "MSIRP",
            0x5D or 0x71 or 0x72 => "DELTA",
            0x40 or 0x41 => "NPUSH",
            >= 0xB0 and <= 0xBF => "PUSH",
            _ => "op",
        };

        private struct CallFrame
        {
            public byte[] Code;
            public int Start;
            public byte[] ReturnCode;
            public int ReturnIp;
            public int Repeats;

            public CallFrame(byte[] code, int start, byte[] returnCode, int returnIp, int repeats)
            {
                Code = code; Start = start; ReturnCode = returnCode; ReturnIp = returnIp; Repeats = repeats;
            }
        }

        // ---- the two workhorses -------------------------------------------------------------------

        /// <summary>MDRP: move a point so it sits the distance the OUTLINE has it from rp0, rounded
        /// as the graphics state says. This is how a program says "keep this the shape it was".</summary>
        private void MoveDirectRelative(byte op)
        {
            bool setRp0 = (op & 0x10) != 0;
            bool round = (op & 0x04) != 0;
            bool keepMinimum = (op & 0x08) != 0;

            int p = Pop();
            Zone z = ZoneOf(_gs.Zp1);
            if (p >= z.PointCount) { if (setRp0) _gs.Rp0 = p; _gs.Rp1 = _gs.Rp0; _gs.Rp2 = p; return; }

            int linkType = EffectiveLinkType(op & 3, _gs.Zp1, p, _gs.Zp0, _gs.Rp0);
            // THE DISTANCE IS TAKEN IN FONT UNITS AND SCALED ONCE, not read off the scaled points.
            // Consolas 'R' at 12ppem places its bowl's right side with MDRP[round] from the stem,
            // 596 units away: the scaled points are 287.25 -> 287 and 63.75 -> 64, sixty-fourths
            // apart 223 = 3.48px, which rounds to 3; the distance itself scales to 223.5 -> 224 =
            // 3.50px, which rounds to 4 -- and 4 is where GDI's bowl is (31 of the glyph's 37
            // points a pixel left of GDI's, whole face 45 -> 0 at 12ppem; Times B@12 4 -> 1,
            // Times I 41/20 -> 35/13, Verdana I@16 21 -> 9, Tahoma R@12 3 -> 0; the ClearType
            // weight 5,618,455 -> 5,604,278). The same for MIRP's outline distance, which only
            // feeds the cut-in test and the sign, moved nothing in either oracle and is left alone.
            // WPF_MDRP_EXACT=0 subtracts the scaled points as before.
            int original = s_mdrpExact
                ? MeasureOriginalExact(_gs.Zp1, p, _gs.Zp0, _gs.Rp0)
                : MeasureOriginal(_gs.Zp1, p, _gs.Zp0, _gs.Rp0, black: linkType == 1);

            // The single width: a face may declare one measurement that every stem of that size
            // should collapse to, and anything within the cut-in of it becomes it.
            int distance = original;
            if (_gs.SingleWidthCutIn > 0
                && Math.Abs(distance - _gs.SingleWidthValue) < _gs.SingleWidthCutIn)
                distance = distance >= 0 ? _gs.SingleWidthValue : -_gs.SingleWidthValue;

            if (round) distance = RoundDistance(distance, linkType: linkType);

            if (keepMinimum)
            {
                // MIRP has always reduced the minimum in the ClearType direction and MDRP has
                // always used it whole, which cannot both be right. WPF_CT_MINDIST_MDRP=1 makes
                // them agree, so the difference can be measured instead of inherited.
                int floor = s_minDistMdrp ? EffectiveMinimumDistance() : _gs.MinimumDistance;
                if (original >= 0) { if (distance < floor) distance = floor; }
                else { if (distance > -floor) distance = -floor; }
            }

            int current = MeasureCurrent(_gs.Zp1, p, _gs.Zp0, _gs.Rp0);
            // WPF_MDRP_TRACE=1: the whole decision in one line -- what the outline measured, what
            // the rounding made of it, and which grid did the rounding. An MDRP on a SLANTED
            // projection is how an italic places its stem tops, and nothing else in the dump says
            // whether such a move rounded on the pixel or on the sixteenth.
            if (s_mdrpTrace)
                Console.Error.WriteLine($"   MDRP p={p} rp0={_gs.Rp0} link={linkType}"
                    + $" round={round} min={keepMinimum} orig={original / 64f:0.####}"
                    + $" -> dist={distance / 64f:0.####} cur={current / 64f:0.####}"
                    + $" move={(distance - current) / 64f:0.####}"
                    + $" pv=({_gs.ProjX},{_gs.ProjY}) fv=({_gs.FreeX},{_gs.FreeY})"
                    + $" round={_gs.Round} swci={_gs.SingleWidthCutIn} sw={_gs.SingleWidthValue}"
                    + $" ctDir={InClearTypeDirection} ppem={_ppem}");
            LinkX(_gs.Zp1, p, _gs.Zp0, _gs.Rp0, linkType, canProportion: true);
            MovePoint(z, p, distance - current);

            _gs.Rp1 = _gs.Rp0;
            _gs.Rp2 = p;
            if (setRp0) _gs.Rp0 = p;
        }

        /// <summary>MIRP: the same, but the distance comes from the CONTROL VALUE TABLE rather than
        /// from the outline -- the designer's own measurement of that stem, so every stem the face
        /// meant to be the same width comes out the same width.</summary>
        private static readonly bool s_keepInlineDeltas =
            Environment.GetEnvironmentVariable("WPF_CT_DELTA") == "inline";

        /// <summary>Apply every delta, suppressing none -- what GDI's ClearType appears to do.</summary>
        /// <summary>WPF_CT_SYMINFO=1 answers GETINFO's symmetric-rendering bit.</summary>
        /// <summary>WPF_GETINFO_TRACE=1: log every GETINFO a face asks, prep included. What a
        /// face BRANCHES on is the only way its program can behave differently for us than for
        /// GDI, so it is worth being able to see.</summary>
        /// <summary>WPF_CT_GREY=1: answer the GREYSCALE bit even when drawing ClearType. NO, and
        /// measured: the six-face specimen goes 2,197,658 -> 2,286,187.
        /// <para>Worth asking because selector 32 is the ONLY channel by which our ClearType claim
        /// reaches any of these faces. Traced with WPF_GETINFO_TRACE=1, all six ask for the
        /// rasterizer VERSION and the GREYSCALE bit and nothing else -- Segoe UI, Arial and Times
        /// also ask about rotation, Consolas about rotation and stretch. NOT ONE asks for the
        /// ClearType bit, the compatible-widths bit, the stripe bit or the symmetric bit, so the
        /// four answers below them are unexercised by every face this port is measured against.
        /// Keep them, they cost nothing and another face may ask -- but do not tune against
        /// them.</para></summary>
        /// <summary>WPF_CT_GREY=always: report greyscale even while drawing ClearType.</summary>
        private static readonly bool s_greyAlways =
            Environment.GetEnvironmentVariable("WPF_CT_GREY") == "always";

        /// <summary>Whether GETINFO reports HORIZONTAL LCD stripes. Measured: GDI does not.</summary>
        private static readonly bool s_stripeInfo =
            Environment.GetEnvironmentVariable("WPF_CT_STRIPEINFO") == "1";

        /// <summary>What GETINFO answers for the rasterizer VERSION: THIRTY-FIVE, which is what GDI
        /// is. WPF_RASTERIZER sweeps it.
        /// <para>Worth a knob because Segoe UI's 'prep' asks for this THREE TIMES and asks for
        /// nothing else except rotation and stretch -- no ClearType bit, no compatible-widths bit
        /// (WPF_GETINFO_TRACE=1 shows it). So the version is the only thing that face's prep can
        /// behave differently on, and its prep is where the stem control value comes from. It does
        /// change it: at 35 the stem control value comes out 1.0000px and at 36 or above 0.9688,
        /// the face declining to round it for a newer rasterizer.</para>
        /// <para>AND SAYING 40 IS NOT WORTH IT, though the specimen total says otherwise --
        /// 2,197,699 -> 2,184,855, which is where this nearly went. The total hides a trade that is
        /// entirely between faces:
        ///     Verdana B -11,129   Tahoma B -10,959   Consolas R -6,161   Segoe UI R 9 -2,046
        ///     Segoe UI R 8.25 +13,073   Segoe UI R 12 +3,888
        /// and the parity suite, which is Segoe UI at eleven sizes, agrees with the losing half:
        /// its repertoire goes 41,294 -> 42,542. Two bold faces gain because they take a different
        /// branch when told they are on a rasterizer they are not on. That is not parity, it is a
        /// lie that happens to pay in two places, and it costs the one face measured most.</para>
        /// </summary>
        private static readonly int s_rasterizerVersion =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_RASTERIZER"), out int rv) && rv > 0
                ? rv : 42;

        // FORTY-TWO IS GDI'S, measured rather than reasoned: WhatGdiAnswersGetInfo makes a glyph
        // shift itself by what GETINFO returns and reads the number off the ink, and GDI says 42
        // on both the drawn and the GetGlyphOutline path.
        //
        // It is a GATE, not a detail. Segoe UI's fpgm tags every hinting instruction with a mode
        // number and runs it only when that number equals storage[2], and fpgm at 2270 computes
        // storage[2] from GETINFO alone -- below version 36 the face never even asks whether
        // ClearType is on, so storage[2] can only be 0 or 1 and every ClearType instruction in
        // every glyph is skipped. At 35 we ran the face's BI-LEVEL program and then applied our
        // own invented x rules to the result, which is why no rounding rule ever fitted it.
        //
        //     window   v35 987,187 -> v42 982,386      parity  80,756 -> 80,689
        //
        // Both oracles improve, which is the whole reason this moved. It is a small number for a
        // large finding because most of what the branch does, we still decline to run: see
        // s_symmetricInfo.

        /// <summary>WPF_CT_COMPATINFO=0: answer GETINFO's compatible-widths query NO.</summary>
        private static readonly bool s_compatWidthInfo =
            Environment.GetEnvironmentVariable("WPF_CT_COMPATINFO") != "0";

        internal static readonly bool s_mirpCensus =
            Environment.GetEnvironmentVariable("WPF_MIRP_CENSUS") == "1";

        internal static readonly System.Collections.Generic.Dictionary<string, long> s_mirpSeen = new();

        internal static void DumpMirpCensus()
        {
            if (!s_mirpCensus) return;
            lock (s_mirpSeen)
                foreach (var kv in s_mirpSeen) Console.Error.WriteLine($"MIRPCENSUS {kv.Key} x{kv.Value}");
        }

        private static readonly bool s_traceGetInfo =
            Environment.GetEnvironmentVariable("WPF_GETINFO_TRACE") == "1";

        /// <summary>What to answer GETINFO's symmetric-rendering query. Unset asks the FACE,
        /// which is the rule GDI follows; WPF_CT_SYMINFO=1/0 forces it.
        /// <para>Symmetric smoothing is a per-face, per-ppem entry in 'gasp', not a property of
        /// the rasterizer, and a face branches its ENTIRE hinting program on the answer: Segoe
        /// UI's storage[2] gains 128 when it is set, which takes it out of the {0, 2, 6} set that
        /// prep tests (1866-1883) before rounding 51 stem control values to whole pixels (the
        /// rounding is fpgm 2617). Segoe UI asks for it at 20ppem and up, and below 9.</para>
        /// <para>Both constants that came before this were wrong. A global NO was shipped for a
        /// long time. A global YES was then measured and was far worse -- it un-rounded every stem
        /// at every size, taking 'H'@12 from GDI's exact lamps (73 153 255 197 111 36, 2.157px) to
        /// 73 153 255 153 73 (1.848px) and costing the window 167,925 -- and was briefly written
        /// down as a deliberate deviation from GDI on the strength of an oracle reading. The
        /// oracle was right and was asking about the WRONG FONT: the synthetic probe ships no
        /// 'gasp' at all, and a face that does not say gets the bit.</para>
        /// <para>Asking the face was still 1,364 worse than a global no until the thing it exposed
        /// was fixed: at 20ppem the relaxed geometry was being rasterized with the vertical
        /// sampling meant for the UNFITTED small-size regime. See WgpuSceneRenderer.SymmetricRows.
        /// With that split, the face's own answer is both correct and best -- 77,111 against the
        /// 80,689 that shipped -- and the geometry moves toward GDI where it is checkable: at
        /// 20ppem the coordinates inside GDI's own allowed intervals go 33/62 to 43/62.</para>
        /// </summary>
        private static readonly bool? s_symmetricInfoForced =
            Environment.GetEnvironmentVariable("WPF_CT_SYMINFO") switch
            {
                "1" => true,
                "0" => false,
                _ => null,           // unset: ask the face, which is the rule
            };

        // Was a documented deviation ("GDI reports symmetric, we do not"). It was not a
        // deviation, it was a bug in the probe: see s_symmetricInfoForced above.

        /// <summary>MDRP measures its original distance in font units and scales it once, as MD
        /// does; WPF_MDRP_EXACT=0 subtracts the scaled points instead. See the MDRP site.</summary>
        private static readonly bool s_mdrpExact =
            Environment.GetEnvironmentVariable("WPF_MDRP_EXACT") != "0";

        /// <summary>WPF_MD_SPEC=0 restores the old MD operand pairing.</summary>
        private static readonly bool s_mdOldOrder =
            Environment.GetEnvironmentVariable("WPF_MD_SPEC") == "0";

        /// <summary>WPF_CT_SHPIXTOUCH=0 lets a vertical SHPIX move an untouched point.</summary>
        /// <summary>WPF_CT_PHASE_IUPY=1: let IUP[y] run the phase too, as we used to.</summary>
        /// <summary>WPF_CT_IUP_REF=scaled interpolates along the SCALED original instead of
        /// font units. itrp_IUP picks between the two -- its reference array is elem+0x10
        /// (scaled) when gs[0x196] is set and elem+0x20 (FONT UNITS) when it is clear, where
        /// gs[0x196] is 0 exactly when gs[0x171] == 0 and the grid fit was handed a non-null
        /// child-scaling argument. Which branch our rendering takes is not derivable from the
        /// code alone, so it was measured: font units 3,605,604, scaled 3,662,880. WE ALREADY
        /// TAKE THE RIGHT ONE. Kept switchable because the answer is a fact about GDI's state,
        /// not about the rule, and a different rendering path could flip it.</summary>
        /// <summary>WPF_CT_IUP_FLATTIE=0: on a flat run anchor on the FIRST touched point, as we
        /// used to, instead of itrp_IUP's non-strict tie to the second.</summary>
        private static readonly bool s_iupFlatTie =
            Environment.GetEnvironmentVariable("WPF_CT_IUP_FLATTIE") != "0";

        private static readonly bool s_iupRefScaled =
            Environment.GetEnvironmentVariable("WPF_CT_IUP_REF") == "scaled";

        /// <summary>WPF_CT_IUP_ONESTEP=0: build a 16.16 scale and multiply, as we used to, instead
        /// of itrp_IUP's single rounded division. See the comment at the interpolation.</summary>
        private static readonly bool s_iupOneStep =
            Environment.GetEnvironmentVariable("WPF_CT_IUP_ONESTEP") != "0";

        /// <summary>WPF_CT_IUP_FLAT=0: when the two interpolation references coincide, split the
        /// run between their two deltas as we used to, instead of itrp_IUP's single lower
        /// delta.</summary>
        private static readonly bool s_iupFlatLower =
            Environment.GetEnvironmentVariable("WPF_CT_IUP_FLAT") != "0";

        /// <summary>WPF_CT_IUP_UPPER=0: ask "below the lower reference" before "at or above the
        /// upper" one, as we used to. See the comment at the bracket test.</summary>
        /// <summary>WPF_IUP_TRACE=1: every contour's touched run and every interpolated span, so a
        /// point GDI places differently can be traced to the anchors it was carried between.
        /// </summary>
        /// <summary>Set while the trace should speak: Carry is static and cannot see BiLevelPass,
        /// and without this the measurement pass's runs are printed beside the real ones.</summary>
        private static bool s_iupTraceOn;

        internal static readonly bool s_iupTrace =
            Environment.GetEnvironmentVariable("WPF_IUP_TRACE") == "1";

        /// <summary>WPF_CT_IUP_GRID=1: round IUP's result onto the ClearType sixteenth of a pixel.
        /// See the comment at the interpolation.</summary>
        private static readonly bool s_iupGrid =
            Environment.GetEnvironmentVariable("WPF_CT_IUP_GRID") == "1";

        /// <summary>WPF_CT_ISECT_MUL=0: compare ISECT's two cross terms as raw products instead of
        /// through Mul26Dot6. See the comment at the comparison.</summary>
        private static readonly bool s_isectMul26 =
            Environment.GetEnvironmentVariable("WPF_CT_ISECT_MUL") != "0";

        private static readonly bool s_iupUpperFirst =
            Environment.GetEnvironmentVariable("WPF_CT_IUP_UPPER") != "0";

        private static readonly bool s_phaseAtIupY =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_IUPY") == "1";

        private static readonly bool s_shpixNeedsTouch =
            Environment.GetEnvironmentVariable("WPF_CT_SHPIXTOUCH") != "0";

        /// <summary>WPF_CT_SHPIX=run executes SHPIX in the ClearType direction instead of refusing
        /// it. Measured worse -- see the note at the SHPIX site.</summary>
        /// <summary>SHIPPED 2026-09-10: an INLINE SHPIX is never filtered, whatever its size.
        /// <para>The heuristic gate below this flag -- "execute the fractional nudges on outline
        /// points and refuse the rest" -- was the port's own invention, and it sat in FRONT of
        /// the rule read out of fontdrvhost (MatchesSuppressedFdef: suppression only inside the
        /// two recognised VTT delta helpers). Together they were double-filtering, and the
        /// heuristic's `amount % 64 == 0` clause was dropping WHOLE-PIXEL inline shifts the
        /// binary runs. Arial's 'c' is the case: ip 518 is `SHPIX pt1 -64`, the right terminal's
        /// anchor, and pt0/pt14/pt15 are all MIRPed from it, so refusing it put the whole right
        /// side 0.8px right of GDI at every even size (10,364 at 24ppem, 4,173 at 12).</para>
        /// <para>Turning the heuristic off, with the binary's rule left standing: specimen
        /// 975,462 -> 908,076, holdout 8..24 3,162,566 -> 2,908,191; 47 glyphs better against 2
        /// worse (Arial Bold 'A', +137 in total), net better on every face; 14 ratchets moved and
        /// every one was "now matches Windows in MORE pixels" (tightened). Arial 'c'@24
        /// 10,364 -> 137, 'c'@12 -> 0, Arial Bold 'y'@24 3,753 -> 0, Tahoma 'N'@12 -> 0.</para>
        /// <para>The gate's recorded justification -- Arial 'E'@16's -64 on its middle arm, said
        /// to be one GDI does not run -- was an edge-solver reading from before the FDEF rule
        /// existed, and it does not hold: 'E'@16 is exactly 0 with the gate on OR off.
        /// WPF_CT_SHPIX=outline restores the heuristic.</para></summary>
        private static readonly bool s_runShpix =
            Environment.GetEnvironmentVariable("WPF_CT_SHPIX") is null or "run";

        /// <summary>WPF_CT_SHPIX_PAIR=1: refuse a pair of fractional nudges that move two DIAGONAL
        /// points against each other. Measured, close, and not shipped.
        /// <para>It is the best account yet of which fractional nudges GDI's ClearType runs. Over
        /// eleven sizes it is worth 11,841,895 -> 11,654,976, and where it wins it wins outright:
        /// Verdana Bold -232,521 and Tahoma Bold -135,253, with Verdana Bold 'A'@16 going 10,069 ->
        /// 0, 'W'@14 12,397 -> 137, 'W'@12 10,985 -> 510 and Tahoma Bold 'X'@16 12,315 -> 765.
        /// It separates the two cases that no threshold could: Verdana Bold's 'W' at 12ppem opposes
        /// (+33/64 leftmost, -33/64 rightmost, and GDI draws the letter two columns wider) while
        /// Tahoma's 'W' at 11 does not (-32/64 and -44/64, both the same way, and GDI runs them).
        /// </para>
        /// <para>THREE COUNTEREXAMPLES keep it off. Arial's 'w' at 11ppem fits two pixels wider
        /// than its own outline without its pair and is thrown out as implausible, so the letter
        /// falls back to the unhinted fitter -- and that is not the mid-run rewind, which was the
        /// first implementation and is why this watches and re-runs instead: the second pass is a
        /// clean run and Arial still breaks. Times New Roman gives up 85,153 on its bold and 33,483
        /// on its roman, its 'W' at 13ppem going 373 -> 4,387. Segoe UI's 'V', 'Z' and 'y' lose
        /// a few pixels each. Serif diagonals and Arial's 'w' take an opposing pair that GDI
        /// plainly runs, and until something separates THOSE from Verdana's, this is a rule with a
        /// hole in it rather than a rule.</para>
        /// <para>RE-TESTED under the phase pass, because the mechanism underneath it changed
        /// and a knob measured worse under the old one is not settled. It STILL LOSES, and the
        /// aggregates still say otherwise: specimen 4,187,741 -> 4,082,699 and holdout
        /// 15,074,881 -> 14,865,877, against 19 per-glyph ratchets regressed and NOT ONE
        /// improved -- three of them by 400, 423 and 494 pixels, which is the fallback to the
        /// unhinted fitter this note already predicted. Ratchets outrank the weight sum. Do not
        /// ship it on the strength of the aggregate; the hole is still there.</para>
        /// <para>RE-TESTED AGAIN under the exact ClearType filter and the phase/delta ports, for
        /// the same reason. Same verdict, and the aggregate is more tempting than ever: the weight
        /// sum goes 1,628,346 -> 1,529,052 while the ratchets go 12,416 -> 12,720 with
        /// ZERO improved and 18 regressed (repertoire@11 229 -> 295, regular@11 154 -> 211). The
        /// hole is exactly where it was. Do not ship it.</para></summary>
        /// <summary>Drop a SHPIX along the ClearType direction on a point the program has not
        /// placed in the other one. WPF_CT_SHPIX_X=keep to restore the old behaviour.</summary>
        private static readonly bool s_shpixDropX =
            Environment.GetEnvironmentVariable("WPF_CT_SHPIX_X") == "1";
        // MEASURED AND WRONG, kept only as a knob: taking the paper's sentence at face value for
        // the ClearType direction too costs 1,439,591 -> 2,003,540 and fails 203 ratchets. GDI
        // plainly DOES run most x-direction SHPIX; what it refuses is narrower than "all of them".

        /// <summary>SUPERSEDED 2026-09-10, OFF by default; WPF_CT_SHPIX_PAIR=1 restores it.
        /// <para>This heuristic reproduced, from the outside, what the scaler does by READING THE
        /// FUNCTION'S BYTES -- see MatchesSuppressedFdef. The real rule is strictly better and
        /// needs none of the three conditions below: with it, this adds nothing on the specimen
        /// (1,081,158 either way) and costs 2,724 on the 8..24 holdout. Kept only as the record of
        /// how far a heuristic got: 1,439,591 -> 1,269,646 against the real rule's 1,081,158.</para>
        /// <para>The rule below was written, evidenced and left switched OFF, because a bare
        /// "opposing signs" test regresses as much as it fixes: it cost 18 ratchets. Three
        /// conditions were missing, each found from the case that contradicted it -- the point
        /// must be untouched in y, the two must be the same size, and together they must move at
        /// least half a pixel of width. With them:</para>
        /// <para>specimen 1,439,591 -> 1,330,751; holdout 8..24 4,615,390 -> 4,308,980;
        /// ALL 627 parity ratchets pass. Tahoma Bold 'W'@16 15,129 -> 0 and Verdana Bold
        /// 'W'@13 10,579 -> 0, both pixel-exact.</para>
        /// <para>The threshold is not a knife-edge: everything in play is either 8..24/64
        /// (Segoe UI's 'V' and 'Z', which GDI runs) or 60..66/64 (Tahoma's and Verdana's, which it
        /// does not), so 32 through 48 all measure identically and 16/24 cost 1,100 and a
        /// ratchet.</para></summary>
        private static readonly bool s_shpixPair =
            Environment.GetEnvironmentVariable("WPF_CT_SHPIX_PAIR") == "1";

        /// <summary>How much combined width an opposing diagonal pair has to move before it is
        /// treated as a bi-level width grab rather than a sub-pixel nudge. WPF_CT_SHPIX_PAIR_MIN,
        /// in 64ths.</summary>
        private static readonly int s_nudgePairMin =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_SHPIX_PAIR_MIN"), out int npm)
                ? npm : 32;

        /// <summary>How deep inside CALLed functions the interpreter is. fontdrvhost keeps the
        /// same thing as bit 4 of its +0x1C2 word -- itrp_CALL sets it on entry and clears it on
        /// return -- and itrp_SHP_Common tests THAT bit before applying any of the ClearType
        /// SHPIX suppression, so a nudge written inline in a glyph is not filtered at all.</summary>
        private int _callDepth;

        /// <summary>WPF_CT_SHPIX_CALL=0 to disable the fontdrvhost SHPIX rule above.</summary>
        private static readonly bool s_shpixCallRule =
            Environment.GetEnvironmentVariable("WPF_CT_SHPIX_CALL") != "0";

        /// <summary>WPF_CT_SHPIX_IUPY=0: drop the "IUP[y] has not run" clause from the ClearType
        /// SHPIX gate, which is how this was implemented before the third clause was read out of
        /// itrp_SHP_Common. See the comment at the SHPIX opcode.</summary>
        private static readonly bool s_shpixAfterIupY =
            Environment.GetEnvironmentVariable("WPF_CT_SHPIX_IUPY") != "0";

        /// <summary>The two function bodies fontdrvhost recognises, read out of its own table at
        /// +0xa8770. Both are the VTT "delta at this size" helper -- a ppem test around a single
        /// SHPIX -- which is bi-level pixel-flipping, and ClearType declines it.
        /// <para>MPPEM GTEQ SWAP MPPEM LT AND IF SHPIX ELSE POP POP EIF ENDF, and the shorter
        /// MPPEM EQ IF SHPIX ELSE POP POP EIF ENDF. Tahoma's function 55 is the first of them
        /// exactly.</para></summary>
        private static readonly byte[] s_shpixFdefA =
            { 0x4B, 0x54, 0x58, 0x38, 0x1B, 0x21, 0x21, 0x59, 0x2D };
        private static readonly byte[] s_shpixFdefB =
            { 0x4B, 0x53, 0x23, 0x4B, 0x51, 0x5A, 0x58, 0x38, 0x1B, 0x21, 0x21, 0x59, 0x2D };

        private readonly int[] _suppressedFdefs = new int[4];
        private int _suppressedFdefCount;

        /// <summary>`RCVT SWAP GC[0] ADD DUP PUSHB[1] 38`, the seven bytes itrp_FDEF looks for in
        /// function 0 (fontdrvhost+0xa87b0).</summary>
        private static readonly bool s_fdefDump =
            Environment.GetEnvironmentVariable("WPF_FDEF_DUMP") == "1";

        private static readonly byte[] s_fdefAddHelper =
            { 0x45, 0x23, 0x46, 0x60, 0x20, 0xB0, 0x26 };

        /// <summary>Whether this face's function 0 matched -- gs+0x1c2 bit 10. Set once by the
        /// font program, not cleared per glyph.</summary>
        private bool _fdefAddHelper;

        /// <summary>gs+0x1c2 bit 3, which itrp_RS raises from bit 10. Per glyph.</summary>
        private bool _mdBit3;

        private static bool StartsWith(byte[] code, int body, int end, byte[] want)
        {
            if (end - body < want.Length) return false;
            for (int i = 0; i < want.Length; i++) if (code[body + i] != want[i]) return false;
            return true;
        }
        /// <summary>WPF_CT_DELTA_RE=0 restores the pre-2026-09-10 delta gate.</summary>
        /// <summary>SHIPPED 2026-09-10, the rule read out of itrp_DeltaEngine. 0 restores the old
        /// gate; 2/4/5 are the ablations that showed the axis change is neutral (1,079,668) and the
        /// "IUP[y] has not run" clause is the whole of it (975,462).</summary>
        private static readonly int s_deltaReMode =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_DELTA_RE"), out int drm) ? drm : 1;
        private static readonly bool s_deltaReRule = s_deltaReMode != 0;

        private readonly bool[] _callDelta = new bool[128];
        private int _deltaFdefDepth;

        /// <summary>`SVTCA[x] PUSHB[1] 24 RS IF` and the same with an RTG in front -- the two
        /// sequences itrp_FDEF looks for in functions 0, 1, 2, 4, 7 and 8
        /// (fontdrvhost+0xa8790 and +0xa87a0).</summary>
        private static readonly byte[] s_fdefDispatchA = { 0x01, 0xB0, 0x18, 0x43, 0x58 };
        private static readonly byte[] s_fdefDispatchB = { 0x01, 0x18, 0xB0, 0x18, 0x43, 0x58 };

        /// <summary>gs+0x1c2 bit 9. Decided once by the font program.</summary>
        private bool _fdefModeDispatch;

        private readonly bool[] _callRearm = new bool[128];
        private readonly bool[] _callPhaseWas = new bool[128];

        private static readonly bool s_phaseRearm =
            Environment.GetEnvironmentVariable("WPF_CT_PHASE_REARM") != "0";

        private static bool MatchesSuppressedFdef(byte[] code, int body, int end)
        {
            int n = end - body;
            byte[]? want = n == s_shpixFdefA.Length ? s_shpixFdefA
                         : n == s_shpixFdefB.Length ? s_shpixFdefB : null;
            if (want is null || body < 0 || end > code.Length) return false;
            for (int i = 0; i < n; i++) if (code[body + i] != want[i]) return false;
            return true;
        }

        private bool IsSuppressedFdef(int id)
        {
            for (int i = 0; i < _suppressedFdefCount; i++) if (_suppressedFdefs[i] == id) return true;
            return false;
        }

        private int _nudgeCount, _nudgeDx, _nudgeN;
        private readonly int[] _nudgeXs = new int[24], _nudgeDxs = new int[24];
        private bool _nudgePair, _nudgeVetoed;

        /// <summary>Set when this glyph's program nudged two diagonal points against each other.
        /// Cleared by <see cref="ResetNudgeWatch"/>, not by each component's run, so a composite
        /// carries what its parts saw.</summary>
        internal bool SawOpposingDiagonalNudges;

        /// <summary>The second pass: refuse every fractional nudge on a diagonal.</summary>
        internal bool RefusingDiagonalNudges;

        internal void ResetNudgeWatch()
        {
            SawOpposingDiagonalNudges = false;
            _nudgePair = _nudgeVetoed = false;
            _nudgeCount = 0; _nudgeDx = 0; _nudgeN = 0;
        }

        /// <summary>WPF_CT_SHPIX_DIAG=1: refuse the exemption for a point that sits on a DIAGONAL
        /// -- neither of its outline neighbours is roughly above or below it. A nudge is for a stem
        /// side or a bowl extreme, both of which have a vertical edge through them; the half-pixel
        /// pulls Verdana Bold puts on its 'W' are on the diagonal strokes themselves.
        /// <para>Measured: refusing EVERY nudge on a diagonal is worth more than refusing only the
        /// opposing pairs -- 11,841,895 -> 11,377,713 over eleven sizes against 11,638,887 -- but it
        /// takes Tahoma's roman and italic 'W' at 11ppem from 1,278 to 17,529, and Tahoma's pair
        /// there (-32/64 and -44/64) is one GDI plainly runs. WPF_CT_SHPIX_DIAGMAX caps it by size
        /// and reaches 11,319,952 at 24/64, which is a fit, not a boundary (31 and 32 are worse).
        /// The pair rule is the one that ships.</para></summary>
        private static readonly bool s_shpixDiag =
            Environment.GetEnvironmentVariable("WPF_CT_SHPIX_DIAG") == "1";

        /// <summary>How steep an edge still counts as vertical: WPF_CT_SHPIX_DIAGRATIO to 1.</summary>
        private static readonly int s_shpixDiagRatio =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_SHPIX_DIAGRATIO"), out int dr) ? dr : 4;

        /// <summary>How big a nudge on a diagonal is still honoured, in 64ths.
        /// WPF_CT_SHPIX_DIAGMAX.</summary>
        private static readonly int s_shpixDiagMax =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_SHPIX_DIAGMAX"), out int dm) ? dm : 0;

        /// <summary>Whether both of this point's contour neighbours lie off to the side of it,
        /// rather than above or below -- measured in FONT UNITS, since at eleven pixels an em the
        /// scaled sixty-fourths quantize a bowl's vertical tangent into a slope.</summary>
        private bool OnDiagonalEdge(Zone z, int point)
        {
            if ((uint) point >= (uint) _realPoints || _contourCount == 0) return false;
            int first = 0, last = _realPoints - 1;
            for (int i = 0; i < _contourCount; i++)
            {
                int end = z.Contours[i];
                if (point <= end) { last = Math.Min(end, _realPoints - 1); break; }
                first = end + 1;
            }
            if (last <= first) return false;
            int prev = point == first ? last : point - 1;
            int next = point == last ? first : point + 1;
            return Steep(z, point, prev) && Steep(z, point, next);

            static bool Steep(Zone zz, int a, int b)
            {
                int dx = zz.OrusX[a] - zz.OrusX[b], dy = zz.OrusY[a] - zz.OrusY[b];
                if (dx < 0) dx = -dx;
                if (dy < 0) dy = -dy;
                // NOT vertical: the edge leans away from the point by more than one part in
                // s_shpixDiagRatio. A stem side has dx of nothing at all; the rightmost point of a
                // bowl has a vertical tangent and its neighbours are barely off it; a 'W' stroke
                // runs a quarter of the letter sideways for every rise.
                return dx * s_shpixDiagRatio > dy;
            }
        }

        /// <summary>HALF A PIXEL: MEASURED, REAL, AND NOT SHIPPABLE YET. WPF_CT_SHPIX_MAX, in
        /// 64ths, 0 for no cap.
        /// <para>The exemption below is for a face NUDGING a stem's side -- a sub-pixel adjustment
        /// ClearType can render because it has thirds of a pixel to render it in. A face also writes
        /// fractional SHPIXes that are not nudges but grid decisions: Verdana Bold's 'W' at 12ppem
        /// pulls its leftmost point +33/64 and its rightmost -33/64, half a pixel each, narrowing
        /// the letter by a whole pixel to fit the bi-level grid -- and GDI's ClearType draws it two
        /// columns WIDER, at very nearly the unhinted width (ours 0.89..12.67 against an unhinted
        /// 0.39..13.15).</para>
        /// <para>Half a pixel is where the line falls on the specimen's five sizes, and it falls
        /// SHARPLY: 5,485,079 at no cap, 5,254,863 at 32, 5,420,178 at 33 -- one sixty-fourth wider
        /// and two thirds of the win is gone; 31 is worse than either neighbour (5,334,450), so a
        /// nudge of exactly half a pixel is one GDI runs. Verdana Bold -97,263, Tahoma Bold -79,451,
        /// Verdana Italic -70,044, eight faces untouched.</para>
        /// <para>AND IT DESTROYS TAHOMA AT ELEVEN PIXELS AN EM, which is why it is off. Asked at 9,
        /// 11, 13, 14, 18 and 20 -- sizes the five-size specimen cannot see, which is what
        /// WPF_WEIGHT_SIZES is for -- the cap costs 125,979, ALL of it at 11 and all of it Tahoma:
        /// roman +219,204, italic +217,694, nearly every letter going from about 500 to 10-15,000.
        /// Tahoma's 'b' at 11 nudges two points by -54/64 and -46/64, both the same way, so the
        /// letter MOVES and keeps its shape, and GDI draws exactly that; the cap refuses both and
        /// leaves the unhinted outline. Same MDAP[r]-then-SHPIX shape as Verdana's 'W', opposite
        /// answer. Over all eleven sizes the cap is still ahead -- 11,841,895 -> 11,737,658, and
        /// every cap from 40 to 64 is worse -- but a rule that takes a face from exact to badly
        /// wrong at a size people read text at is not a rule yet.</para>
        /// <para>WHAT REPLACED IT is the opposing-pair rule at the SHPIX site, which separates the
        /// same two cases without a threshold: Verdana's pair oppose each other and Tahoma's do
        /// not. "A nudge never moves an outer edge" was the other reading and is measured wrong
        /// (5,641,263 alone, 5,561,076 with this cap) -- it cannot be right, because Tahoma's
        /// honoured pair is on its outer edges too.</para>
        /// </summary>
        private static readonly int s_shpixOutMax =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_SHPIX_MAX"), out int sm) ? sm : 0;

        /// <summary>WHEN A FRACTIONAL NUDGE COUNTS: only INLINE, before IUP.
        /// <para>The exemption below lets a face's fractional SHPIX through in the ClearType
        /// direction where a whole-pixel one is refused. Taken over the whole program it also let
        /// through every nudge written AFTER the interpolation, and those are the ones the ClearType
        /// paper says the rasterizer drops -- the same inline/post-IUP line SkipDeltaInClearTypeDirection
        /// draws for DELTAP ("an inline delta is a delta that occurs before the IUP instruction on a
        /// previously touched point"). Drawing it for SHPIX as well is worth 5,600,974 -> 5,485,079
        /// on the specimen: Verdana Italic 518,271 -> 480,236, Verdana Bold -22,467, Arial Italic
        /// -15,003, nine faces better, six untouched, and the three that lose give up 1,549 between
        /// the worst of them. It is the DIAGONAL letters it helps -- W w v x y k K Z A X 7 carry
        /// 94,075 of the 115,895 -- which is where the post-IUP nudges are: a face has no stem to
        /// nudge in a 'W', so what it writes there is a diagonal adjustment meant for the bi-level
        /// grid, and GDI's ClearType does not run it.</para>
        /// <para>WHAT IT COSTS, and it is not free: Arial's roman and bold write a nudge on the
        /// apex of 'A' -- and on '5' and '6' -- after the interpolation, and GDI draws it (Arial R
        /// 'A'@16 goes 510 -> 2,196). Verdana's and Tahoma's post-IUP nudges are the other way about
        /// by an order of magnitude (Verdana B 'v'@12 5,376 -> 375, 'y'@12 5,949 -> 510, Verdana I
        /// 'K'@24 11,086 -> 4,109, Arial R 'X'@24 13,316 -> 7,734), so the line is drawn where it
        /// is until something separates those two cases.</para>
        /// <para>WPF_CT_SHPIX_OUT=all restores the old behaviour; touchedx / touchedy / post narrow
        /// it other ways, and relaxing "inline" to also admit a nudge on an already-touched point
        /// gives back most of the win. All measured worse: 5,487,903 / 5,833,707 / 5,934,331, and
        /// 5,588,808 (x-touched) / 5,550,695 (y-touched).</para>
        /// <para>ASKING THE FREEDOM VECTOR was the first guess and is measured to change NOTHING.
        /// A diagonal freedom vector counts as the ClearType direction whenever it leans that way,
        /// so the exemption looked as though it might be letting a face's DIAGONAL control through
        /// as well -- but every SHPIX that reaches it has FreeY of exactly zero, and refusing the
        /// leaning ones moves the specimen by not one pixel. No knob is kept for it.</para>
        /// </summary>
        private static readonly string s_shpixOutWhen =
            Environment.GetEnvironmentVariable("WPF_CT_SHPIX_OUT") ?? "inline";

        // Read once. These are asked of every nudged point, and a string scan there is four of them.
        private static readonly bool s_shpixOutInline = s_shpixOutWhen.Contains("inline");
        private static readonly bool s_shpixOutPost = s_shpixOutWhen.Contains("post");
        private static readonly bool s_shpixOutTouchedX = s_shpixOutWhen.Contains("touchedx");
        private static readonly bool s_shpixOutTouchedY = s_shpixOutWhen.Contains("touchedy");

        private static readonly bool s_runShpixOutline =
            Environment.GetEnvironmentVariable("WPF_CT_SHPIX") == "outline"
            || (Environment.GetEnvironmentVariable("WPF_CT_SHPIX") is null
                && (Environment.GetEnvironmentVariable("WPF_CT_PHASE") != "0" || TrueTypeFont.CompatibleWidthMode is 7 or 8 or 9));

        /// <summary>WPF_CT_CUTIN_DIV: what the control-value cut-in is divided by in the ClearType
        /// direction. 16 is the paper's sixteenth.</summary>
        /// <para>Settable, so a test can fit the same glyph both ways in one process and ask
        /// GDI's own pixels which one it used.</para>
        /// <summary>WPF_CT_CUTIN_EXACT=0 goes back to dividing the threshold and testing >=.</summary>
        /// <summary>WPF_CT_CUTIN_SCOPE=gdi uses GDI's ACTUAL scope for the unrounded cut-in:
        /// it applies whenever INSTCTRL selector 3 is clear, with no exception for links to a
        /// PHANTOM point. That is what itrp_MIRP does and it measures WORSE -- 3,845,472 with
        /// our phantom exception against 3,990,903 with GDI's rule.
        /// <para>Which is evidence, not noise. The rule is not in doubt; if applying the cut-in
        /// to a phantom link hurts, our PHANTOMS are not where GDI's are, and that is upstream
        /// of MIRP. Worth reopening once the phantom setup is read end to end -- the advance is
        /// right now (lamp grid, WPF_PP2_ROUND=5) but the LSB and the two y phantoms have not
        /// been checked against scl_RoundCurrentSideBearingPnt's second half, which rounds them
        /// to WHOLE pixels.</para></summary>
        private static readonly bool s_cutInGdiScope =
            Environment.GetEnvironmentVariable("WPF_CT_CUTIN_SCOPE") == "gdi";

        private static readonly bool s_cutInExact =
            Environment.GetEnvironmentVariable("WPF_CT_CUTIN_EXACT") != "0";

        /// <summary>WPF_CT_CUTIN_AXIS=exact scales the cut-in only on an axis-EXACT projection,
        /// which is what itrp_MIRP's localGS+0xcc branch does.</summary>
        private static readonly bool s_cutInAxisExact =
            Environment.GetEnvironmentVariable("WPF_CT_CUTIN_AXIS") == "exact";

        internal static int s_cutInDivisor =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_CUTIN_DIV"), out int cd) && cd > 0
                ? cd : ClearTypeGrid;

        /// <summary>Answer GETINFO "not greyscale" however the rest of the configuration
        /// reads. WPF_CT_GREY=0. See the call site.</summary>
        /// <summary>WPF_CT_SIGNCV=outline: when auto-flip is OFF and the control value points
        /// the other way from the outline, use the outline distance.
        /// <para>MEASURED AND NOT SHIPPED, and worth keeping for the case that produced it. Times
        /// New Roman's ITALIC 'i' at 12ppem comes out a whole pixel left of GDI at every one of its
        /// 42 points, while being EXACT at 11, 13, 14 and 16. Traced instruction by instruction
        /// against the exact oracle: the face turns auto-flip off, anchors the glyph from the
        /// phantom origin with cvt[25] = -0.4844px against an outline distance of +0.5156px, and
        /// the two straddle the half pixel -- the control value rounds to 0 and the outline
        /// distance to +1. GDI has +1, so GDI used the outline. FreeType would produce 0 here as
        /// well, so this is a genuine GDI divergence and not an ordinary bug.</para>
        /// <para>But it does not generalise, which is why it is off. Over six faces at 12ppem the
        /// points differing from GDI go 243 -> 230: Times italic gains 41, and Times roman loses 17
        /// and Arial 11. A rule that fixes one face by breaking two is not the rule, and the
        /// narrower reading does not help -- WPF_CT_SIGNCV=phantom, restricting it to distances
        /// measured from a phantom point, measures IDENTICALLY, because every sign-disagreeing
        /// MIRP in this corpus is already anchored to one.</para>
        /// <para>What the case does establish is that a whole-pixel error can survive in a single
        /// glyph at a single size, invisible in every aggregate, and that it can now be run to the
        /// instruction that causes it. That is what the oracle is for.</para></summary>
        private static readonly bool s_signCvOutline =
            Environment.GetEnvironmentVariable("WPF_CT_SIGNCV") == "outline";

        /// <summary>WPF_CT_SIGNCV=phantom: the same, but only when the distance is measured
        /// from a PHANTOM point -- the glyph origin or its advance, which are not outline
        /// points and whose "distance" is the side bearing rather than a stem.</summary>
        private static readonly bool s_signCvPhantom =
            Environment.GetEnvironmentVariable("WPF_CT_SIGNCV") == "phantom";

        /// <summary>Never report greyscale -- the default. WPF_CT_GREY=1 restores the old
        /// answer, which was yes on any pass not drawing ClearType. See GETINFO.</summary>
        private static readonly bool s_greyNever =
            Environment.GetEnvironmentVariable("WPF_CT_GREY") is not ("1" or "always");

        private static readonly bool s_keepAllDeltas =
            Environment.GetEnvironmentVariable("WPF_CT_DELTA") == "all";

        /// <summary>The floor a fitting instruction may not move a point closer than.
        /// <para>A minimum distance of one PIXEL is a bi-level idea: it exists so a stem cannot
        /// vanish between two sample points. ClearType samples x three times as finely, so the
        /// same floor is three times too coarse there -- and since GDI neither rounds x nor listens
        /// to the control values in that direction, this clamp is very nearly the ONLY thing left
        /// that moves x at all. WPF_CT_MINDIST_DIV divides it; the shipped 2 is no longer
        /// inherited -- `itrp_MIRP` halves it in all three of its shapes, `if (localGS+0xcc != 0)
        /// v = v / 2`, and localGS+0xcc is the ClearType axis. The 2 is the binary's.</para>
        /// </summary>
        private int EffectiveMinimumDistance()
            => InClearTypeDirection && !s_fullMinDistance && !BiLevelPass && s_minDistDiv > 1
                   ? _gs.MinimumDistance / s_minDistDiv
                   : _gs.MinimumDistance;

        /// <summary>WPF_CT_MINDIST_DIV: what the minimum distance is divided by in the ClearType
        /// direction. 1 leaves it whole; 3 is the lamp, which is what the extra resolution is.
        /// <para>MEASURED AND INERT, which is the point of recording it. On the text specimen at
        /// 12ppem, divisors 1, 2, 3 and 4 all give 2,343,248 -- not close, IDENTICAL -- and making
        /// MDRP agree with MIRP moves it by 26 at divisor 3 and by nothing at divisor 2. The clamp
        /// binds too rarely at these sizes to matter, because the distances a program asks for are
        /// already a pixel or more.</para>
        /// <para>So the reasoning that led here was wrong. Having excluded the rounding, the
        /// control values and the deltas, the minimum distance looked like the only thing left that
        /// could still move x. It is not moving anything either.</para>
        /// <para>What is actually true is smaller and stranger: the whole x fitting moves a point
        /// 0.092px on average in Segoe UI and 0.144px in Tahoma. A tenth of a pixel is a THIRD OF A
        /// LAMP, and ClearType turns that into a visible change of colour, which is why displacing
        /// points by so little is worth 1.65 million on the specimen against not displacing them at
        /// all. The remaining difference is therefore a precision problem in the range of a few
        /// sixty-fourths, not a missing mechanism -- 65 to 78 per cent of our coordinates already
        /// land inside the interval GDI's own pixels allow.</para></summary>
        private static readonly int s_minDistDiv =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_MINDIST_DIV"), out int md) && md > 0
                ? md : 2;

        /// <summary>WPF_CT_MINDIST_MDRP=0: go back to MDRP and MIRP disagreeing about it.
        /// <para>They HAD been disagreeing: MIRP halved the minimum distance in the ClearType
        /// direction and MDRP used it whole, which cannot both be right -- the spec gives the
        /// two opcodes one minimum-distance rule, not two. Making them agree measures
        /// 4,283,877 -> 4,187,741 on the specimen and 15,464,384 -> 15,074,881 on the holdout,
        /// with 22 ratchets improved against 15.</para>
        /// <para>Gated on WPF_CT_PHASE only so that WPF_CT_PHASE=0 stays an EXACT restore of
        /// the configuration that shipped before the phase (5,485,079 to the digit). Nothing
        /// about this rule depends on the phase.</para></summary>
        private static readonly bool s_minDistMdrp =
            Environment.GetEnvironmentVariable("WPF_CT_MINDIST_MDRP") is { } mm ? mm == "1"
            : Environment.GetEnvironmentVariable("WPF_CT_PHASE") != "0";

        /// <summary>WPF_CT_DELTA_SCALE: the fraction of an x delta to keep, in thousandths, where
        /// the ClearType rule would drop it. 0 drops it, which is what ships.
        /// <para>Asked because the specimen's error is not monotonic in size. It peaks at ppem 12,
        /// 16 and 20 and dips at 10, 14 and 18 -- 2,882,808 at 16 against 1,982,506 at 14 and
        /// 1,990,770 at 18, uniformly across all six faces and all four styles, so not a layout
        /// artefact. Those peaks are 9pt, 12pt and 15pt: the common UI sizes, which is where a
        /// face carries the most hand-tuned deltas, and x deltas are the one thing we drop
        /// wholesale.</para>
        /// <para>MEASURED AND WRONG, and the finer instrument matters: only "all", "none" and
        /// "touched" had ever been compared, so "a fraction" was untested. At ppem 16, scales of
        /// 0.1, 0.2, 0.333 and 0.5 give 2,900,810 / 3,008,225 / 3,225,611 / 3,565,972 against
        /// 2,882,808 for dropping them. Monotonic from the very first tenth, so it is not that
        /// these deltas are too big -- GDI does not want them in x AT ALL, and the rule that ships
        /// is right at every granularity now tested.</para>
        /// <para>The size pattern it was built to explain is real and remains open. Specimen by
        /// size: 1,623,912 at 10ppem, 2,343,248 at 12, 1,982,506 at 14, 2,882,808 at 16,
        /// 1,990,770 at 18, 2,331,757 at 20. Not monotonic, and 16 is forty per cent above BOTH
        /// its neighbours -- uniformly across all six faces and all four styles, so not a row
        /// artefact, and Segoe UI is exempt (its italic holds 0.013 and 0.007 error per ink at
        /// 16). Not placement either: every band answers a rigid shift of zero at 16 as at 12.
        /// The peaks are 9pt, 12pt and 15pt, which is suggestive and so far no more than
        /// that.</para>
        /// <para>Also not the stem-fat band, which is gated to ppem 10-13 and so inactive at the
        /// spike. Widening it does buy 7,920 at 16 -- and costs 12,050 at 14, 38,222 at 18 and
        /// 85,810 at 20, for a net loss of 128,241 across the six sizes. The shipped band is the
        /// optimum.</para></summary>
        private static readonly int s_deltaScale =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_DELTA_SCALE"), out int ds) ? ds : 0;

        /// <summary>Do NOT halve the minimum distance in the ClearType direction.</summary>
        internal static readonly bool s_fullMinDistance =
            Environment.GetEnvironmentVariable("WPF_CT_MINDIST") == "full";

        private static readonly bool s_keepTouchedDeltas =
            Environment.GetEnvironmentVariable("WPF_CT_DELTA") == "touched";

        /// <summary>WPF_CT_CUTIN_UNROUNDED: shrink the cut-in only for MIRPs that do NOT round --
        /// the reading in which the sixteenth is about stroke weights and not about spacing.</summary>
        private static readonly bool s_cutInUnroundedOnly =
            Environment.GetEnvironmentVariable("WPF_CT_CUTIN_UNROUNDED") == "1";

        /// <summary>Whether a MIRP on a DIAGONAL projection vector may take its control value in
        /// the ClearType pass. 1 (default) keeps today's behaviour; 0 makes it keep the outline
        /// distance. WPF_CT_DIAG_CVT.</summary>
        private static readonly int s_diagCvt =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_DIAG_CVT"), out int dc) ? dc : 1;

        private static readonly bool s_cutInFull =
            Environment.GetEnvironmentVariable("WPF_CT_CUTIN_FULL") == "1";

        /// <summary>WPF_CT_CUTIN_ROUNDONLY=1: apply the control-value cut-in ONLY where the MIRP
        /// asked for rounding, in ClearType too -- the specification's rule, against the ClearType
        /// paper's "do CVT cut-in ALWAYS". The bi-level pass always follows the specification.</summary>
        private static readonly bool s_cutInRoundedOnly =
            Environment.GetEnvironmentVariable("WPF_CT_CUTIN_ROUNDONLY") == "1";

        /// <summary>WPF_CT_CUTIN_SCOPE: which UNROUNDED MIRPs get the ClearType cut-in --
        /// "nophantom" (default: any link between real outline points), "all" (phantom links too),
        /// "black" (effective black links only).</summary>
        private static readonly string s_cutInUnroundedScope =
            Environment.GetEnvironmentVariable("WPF_CT_CUTIN_SCOPE") ?? "nophantom";

        /// <summary>Whether the point sits on a CURVE -- either of its neighbours in the same
        /// contour is an off-curve control point -- as opposed to a corner of a straight edge.
        /// </summary>
        private static bool TouchesACurve(Zone z, int p)
        {
            if (z.Contours == null || z.Contours.Length == 0 || p < 0 || p >= z.PointCount) return false;
            int start = 0;
            for (int c = 0; c < z.Contours.Length; c++)
            {
                int end = z.Contours[c];
                if (p <= end)
                {
                    if (end <= start) return false;
                    int prev = p == start ? end : p - 1;
                    int next = p == end ? start : p + 1;
                    return (z.Tags[prev] & TagOn) == 0 || (z.Tags[next] & TagOn) == 0;
                }
                start = end + 1;
            }
            return false;
        }

        private void MoveIndirectRelative(byte op)
        {
            bool setRp0 = (op & 0x10) != 0;
            bool round = (op & 0x04) != 0;
            bool keepMinimum = (op & 0x08) != 0;

            int cvt = Pop(), p = Pop();
            Zone z = ZoneOf(_gs.Zp1);
            int value = CvtFor(cvt);
            int linkType = EffectiveLinkType(op & 3, _gs.Zp1, p, _gs.Zp0, _gs.Rp0);

            // EVERY PIXEL DISTANCE belongs to the stretched space, not just the control value. The
            // cut-ins and the minimum distance are measured against distances that are now three
            // times bigger, so leaving them at their old size makes the cut-in fire on almost every
            // stem and the program fall back to the outline: measured, tripling only the control
            // value takes Consolas from 6,835 to 11,911 against stage D's reference while helping
            // Segoe UI, which is the shape of a threshold misfiring rather than a fitting choice.
            int stretch = XSpace3x && IsHorizontalProjection ? 3 : 1;
            value *= stretch;

            // MODE 9: a control value that is a POSITION (white or grey link) belongs to the
            // pre-scaled space too -- Arial's lsb is a MIRP from the left phantom whose cvt wins
            // the cut-in, so without this the pre-scale never reaches the stem.
            if (s_scaleWhiteCvt && _preScaled && linkType != 1 && IsHorizontalProjection)
                value = (int) MathF.Round(value * _preScaleRatio);

            // "SCVTCI[] reduces CVT cut-in to one sixteenth of its actual value ... SMD[] reduces
            // minimum distance to 1/2 of its actual value" -- Microsoft, TrueType and ClearType.
            // The sixteenth applies to the UNROUNDED case the paper is about -- "we may honor a CVT
            // cut-in even though the round-off flag would require not doing so ... we assume that the
            // context is a STROKE WEIGHT". Applying it to a rounded MIRP as well collapses the
            // SPACING between stems onto the outline: Segoe UI's 'H' at 12ppem came out 3..8 where
            // Windows draws 3..9, because the second stem is placed by a rounded MIRP whose control
            // value is 0.56px from the outline -- far outside a 0.14px cut-in and comfortably inside
            // the 2.25px one the face actually asks for. WPF_CT_CUTIN=all restores the broad reading.
            // "With ClearType on, do CVT cut-in ALWAYS" -- taken at its word, which also measures
            // better: with the fitting kept, parity is 3,629,242 against 3,764,189 for the narrower
            // reading (the paper's section is about UNROUNDED MIRP, so restricting it there was worth
            // testing). WPF_CT_CUTIN=unrounded selects that narrower reading.
            // A SIXTEENTH, as the paper says, and the rendered pixels agree even though the
            // reported outline does not. Not shrinking it makes our stem WIDTHS equal the ones
            // GetGlyphOutline reports at every size (1.0px, where shrinking leaves us on the outline
            // distance and drifting 0.97 -> 1.45) -- and measured on the text specimen that is
            // WORSE, 1,654,115 against 1,410,303. One more piece of evidence that GGO reports the
            // greyscale fit and ClearType draws something else, and a reminder that the specimen is
            // the authority here, not the outline API. WPF_CT_CUTIN_FULL=1 restores the full cut-in.
            // The divisor decides whether a stem takes the CONTROL VALUE or the OUTLINE distance,
            // and SIXTEEN is an optimum with both sides measured. Solving Arial's stems out of GDI's
            // pixels at 12ppem gives 1.110 for 'l' and 'i' and 1.249/1.296 for 'H' -- varied, which
            // is what taking the outline looks like -- where every stem we fit is exactly 1.0625px,
            // which is what taking one control value looks like. So a SMALLER cut-in (take the
            // outline more often) was the obvious move, and it is worse in both directions:
            //     full  2,841,228     /16  2,773,440     /32  2,787,681
            //     /64   2,788,094     /256 2,790,898              (Arial, three styles)
            // Whatever makes GDI's stems vary, it is not this threshold. WPF_CT_CUTIN_DIV sweeps it.
            //
            // RE-MEASURED after the baseline, advance, kerning and y-cut-in fixes moved every number
            // in this file, because a threshold chosen against a broken landscape is not a threshold
            // that was ever really chosen. All three of the decisions here survive it:
            //     divisor   /1 3,391,753  /2 3,320,982  /4 2,934,769  /8 2,392,364
            //               /16 2,197,658 (best)        /32 2,274,459
            //     stem snap  off 2,197,658   64 3,068,065   96 3,221,476   128 3,392,644
            //     distances on the physical grid  3,810,559
            //     narrow reading (unrounded only) 3,319,606 against 2,197,690
            // Sixteen, no stem snapping, and the broad reading -- by a wide margin in each case.
            // So are the other four: positions on the 16 grid (physical 3,805,858, /1 3,805,855,
            // /2 2,853,991, /3 2,620,282), the minimum distance HALVED in the ClearType direction
            // (full 2,284,820), the three-tap box filter, and no run offset. Eight parameters, all
            // already at their optimum, which is the useful part of the result: WHAT IS LEFT IS NOT
            // REACHABLE FROM ANY OF THEM.
            //
            // What is left, measured. Take every maximal run of inked lamps in a band, and histogram
            // (a) its ink centroid modulo one pixel and (b) its total coverage. On Segoe UI ITALIC,
            // which is our best band anywhere at 9,760, our two histograms equal Windows' bin for
            // bin -- so the instrument is sound and the pipeline can match GDI exactly. On Segoe UI
            // REGULAR the coverage histogram peaks one bin BELOW Windows'. Read the PEAK shift as
            // the size of the gap and you get half a lamp; the MEAN shift is 0.17 lamps -- a third
            // of that -- and the two distributions overlap heavily (ours 25/272/83/34/22 across
            // bins 4..8, Windows' 13/136/202/50/12). It is a difference in the SHAPE of the
            // distribution, not a uniform deficit.
            //
            // Which the pixels then confirm, because a uniform deficit would be curable by widening
            // every stem and NOTHING THAT WIDENS THEM HELPS. Lowering the half-lamp threshold, which
            // buys exactly one extra half-lamp per stem: 128 is the optimum, 112 costs 33,000 and 64
            // costs 316,000. Dropout control, which widens only the thin ones: a shallow best at one
            // subpixel worth 2,617 out of 2,197,658, a tenth of a percent.
            //
            // HOW MUCH NARROWER, EXACTLY. Segoe UI's 'l' is the clean case: the face's control
            // value is 164 units, 0.96px at 12ppem, prep rounds it to 1.0, and the MIRP does not
            // round, so we draw exactly 1.0px. GDI's reads 3.49 lamps against our 3.00 -- and a
            // 1.0px stem covers SIX half-lamps, so 3.49 is seven of them lit, not a wider stem.
            // Seven can be lit two ways, and the LIT COUNT DOES NOT TELL THEM APART -- only the
            // shape of the filtered profile does:
            //     A  a 1.0px stem shifted half a half-lamp: raw lamps [.75, 1, .75]
            //        -> through the box, 0.25 0.58 0.83 0.58 0.25, FIVE lamps and symmetric
            //     B  a wider stem still on the lamp grid:   raw lamps [1, 1, 1, .5]
            //        -> 0.33 0.67 1.00 0.83 0.50 0.17, SIX lamps and asymmetric
            // Windows measures 0.33 0.56 1.00 0.78 0.44 0.11 -- six lamps, asymmetric. It is B.
            // Ours measures 0.33 0.56 1.00 0.56 0.33, five and symmetric, which is [1, 1, 1].
            //
            // So GDI's stem IS wider: seven half-lamps against our six, both starting on the grid,
            // which puts it at 1.083px or more against our exact 1.0. (An earlier reading here said
            // the widths agreed and only a phase differed -- that came from counting lit half-lamps
            // and finding a phase that produces seven, without checking that the phase also
            // produces the profile that was measured. It does not.)
            //
            // Not the threshold, either: at 127 instead of 128 our stems are still 3.00 at 11, 12
            // and 13ppem and the specimen is worse (2,199,781 against 2,197,658).
            //
            // So it is not that our stems are narrow. Some are and some are not, and the ones that
            // are are not narrow for a reason any global widening can reach. Italics escape whatever
            // it is entirely -- they have no vertical stems for a control value to govern -- and
            // Segoe UI's control value for 'l' at 12ppem is exactly 1.0px on a MIRP that does not
            // round, so for that glyph we draw precisely what the face asks and GDI draws wider than
            // both the control value and the outline. That is the open question, and ten parameters
            // are now known not to be the answer.
            // AND ONLY IN THE CLEARTYPE DIRECTION, like the minimum distance below it. Everything
            // in the paragraphs above is about x: the sixteenth is what ClearType does to the cut-in
            // along the axis it oversamples. There is no ClearType in y -- a scan line is a scan
            // line -- so a y-direction MIRP must see the cut-in the face actually asked for.
            //
            // Applied to y as well, a 2px cut-in became 0.125px, every stroke weight missed it, and
            // the outline distance was used instead: Tahoma Bold's 'H' crossbar asks for a control
            // value of 0.64px (one pixel, rounded) and got the outline's 1.66 (TWO). Every
            // horizontal stroke in Segoe UI Bold, Tahoma Bold and Verdana Bold came out a pixel too
            // thick. Arial Bold was right, which is what hid it: its program places both crossbar
            // edges explicitly instead of leaning on a control value.
            // WPF_CT_CUTIN_UNROUNDED is the paper's NARROW reading, put back so it can be measured
            // again: the sixteenth is written about the case where "we may honor a CVT cut-in even
            // though the round-off flag would require not doing so ... we assume that the context is
            // a STROKE WEIGHT", which is an UNROUNDED MIRP. A rounded one is spacing, and spacing
            // would keep the cut-in the face asked for.
            bool shrink = !s_cutInFull && !BiLevelPass
                          && (s_cutInAxisExact ? OnClearTypeAxis : InClearTypeDirection)
                          && !(s_cutInUnroundedOnly && round);
            int cutIn = (shrink ? _gs.ControlValueCutIn / s_cutInDivisor
                                : _gs.ControlValueCutIn) * stretch;
            int minimum = EffectiveMinimumDistance() * stretch;

            if (_gs.SingleWidthCutIn > 0
                && Math.Abs(value - _gs.SingleWidthValue * stretch) < _gs.SingleWidthCutIn * stretch)
            {
                if (_dumpActive)
                    Console.Error.WriteLine($"      SINGLE WIDTH snap cvt[{cvt}] {value / 64f:0.0000}px"
                        + $" -> {_gs.SingleWidthValue * stretch / 64f:0.0000}px"
                        + $" (cut-in {_gs.SingleWidthCutIn / 64f:0.0000})");
                value = value >= 0 ? _gs.SingleWidthValue * stretch : -_gs.SingleWidthValue * stretch;
            }

            if (p >= z.PointCount) { if (setRp0) _gs.Rp0 = p; _gs.Rp1 = _gs.Rp0; _gs.Rp2 = p; return; }

            // A twilight point has no outline position, so the control value IS its position.
            if (_gs.Zp1 == 0)
            {
                Zone zr = ZoneOf(_gs.Zp0);
                z.OrgX[p] = zr.OrgX[_gs.Rp0] + MulFix(value, _gs.FreeX << 2);
                z.OrgY[p] = zr.OrgY[_gs.Rp0] + MulFix(value, _gs.FreeY << 2);
                z.CurX[p] = z.OrgX[p];
                z.CurY[p] = z.OrgY[p];
            }

            int original = MeasureOriginal(_gs.Zp1, p, _gs.Zp0, _gs.Rp0, black: linkType == 1);
            int current = MeasureCurrent(_gs.Zp1, p, _gs.Zp0, _gs.Rp0);

            if (_dumpActive)
                Console.Error.WriteLine($"      MIRP cvt[{cvt}]={value / 64f:0.0000}px"
                    + $" rawCvt={((uint) cvt < (uint) _controlValues.Length ? _controlValues[cvt] : -9999)}"
                    + $" scaledCvt={CvtFor(cvt)}"
                    + $" outline={original / 64f:0.0000}px round={round}"
                    + $" cutIn={_gs.ControlValueCutIn / 64f:0.0000}px"
                    + $" zp0={_gs.Zp0} zp1={_gs.Zp1} rp0={_gs.Rp0}"
                    + $" minDist={_gs.MinimumDistance / 64f:0.0000} keepMin={keepMinimum}"
                    + $" op=0x{op:X2}({Convert.ToString(op & 0x1F, 2).PadLeft(5, '0')}) roundState={_gs.Round}"
                    + $" instrCtrl={_gs.InstructControl}"
                    + $" swValue={_gs.SingleWidthValue / 64f:0.0000} swCutIn={_gs.SingleWidthCutIn / 64f:0.0000}"
                    + $" autoFlip={_gs.AutoFlip} point={p}"
                    + $" axis={(IsHorizontalProjection ? "x" : "y")}");

            // A control value pointing the other way from the outline is the wrong one to use; with
            // auto-flip on, take its size and the outline's direction.
            if (_gs.AutoFlip && (original ^ value) < 0) value = -value;

            // AND WHEN AUTO-FLIP IS OFF, GDI APPEARS TO REJECT IT ALTOGETHER rather than move
            // the point backwards. WPF_CT_SIGNCV=outline. Found on Times New Roman's italic
            // 'i' at 12ppem, which we draw a whole pixel left of GDI at every one of its 42
            // points while being exact at 11, 13, 14 and 16: the face turns auto-flip off,
            // anchors the glyph from the phantom origin with cvt[25] = -0.4844px against an
            // outline distance of +0.5156px, and the two roundings differ by exactly the
            // pixel -- the control value rounds to 0 and the outline distance to +1.
            if (!_gs.AutoFlip && (original ^ value) < 0
                && (s_signCvOutline
                    || (s_signCvPhantom && _gs.Rp0 >= ZoneOf(_gs.Zp0).PointCount - 4)))
                value = original;

            // A control value spent on an x distance is a stem width, and under ClearType those are
            // whole pixels whether or not the instruction asked for rounding.
            if (XPixelWidths && TrueTypeFont.SubpixelFitting && IsHorizontalProjection && !round)
            {
                int w = value < 0 ? -value : value;
                w = (w + 32) & ~63;
                if (w < 64) w = 64;
                value = value < 0 ? -w : w;
            }

            if (XOutlineWidths && !round && TrueTypeFont.SubpixelFitting && IsHorizontalProjection)
                value = original;

            // "With ClearType on, do CVT cut-in ALWAYS" -- the paper is explicit, and its reason is
            // exactly our bug: "un-rounded MIRPs now respect CVT cut-in ... it unnecessarily
            // quantizes stroke weights in ClearType". MIRP 0xE1 has the round bit off, so we applied
            // the control value raw and Segoe UI's 'l' came out 1.469px where GDI draws 1.0. BOTH
            // halves are needed -- the test has to happen at all, and the threshold has to be a
            // sixteenth, because the face sets the cut-in to 2.25px and nothing ever reaches that.
            // ...WITH CLEARTYPE ON, AND BETWEEN REAL POINTS. Without ClearType the cut-in belongs
            // to the ROUNDED MIRP alone, as the specification says, and an unrounded one takes its
            // control value whatever the outline measures. Verdana Italic depends on that: function
            // 58 parks the advance phantom at (cvt[100], cvt[3]) -- 2.5 by 12 pixels at 16ppem, the
            // italic angle -- so that SPVTL[1] on the two phantoms yields a projection PERPENDICULAR
            // TO THE STEMS, and every stem is then positioned along it. Both MIRPs are unrounded and
            // both control values are miles from the phantom's outline distance (the advance, and
            // zero), so cutting them in left the phantom on the baseline, the projection exactly
            // vertical, and every ascender a pixel off GDI's: 'L' at 16ppem measured +62/64 on its
            // stem top while every curve in the face was exact. Against GDI's own fitted points
            // (bi-level GGO, Verdana Italic 16ppem) this takes the face from 685 points differing in
            // x to 41, none by more than 2/64 except one 'Y' point.
            // In ClearType the paper's "always" is real for stroke weights -- honouring the round
            // bit there costs Times Italic 14k and Segoe UI 12k (5,845,962 against 5,644,642) -- but
            // GDI still lets the phantom trick through, or Verdana Italic's stems would lean at the
            // advance's angle. So a link touching a PHANTOM point is exempt: a phantom has no stroke
            // to weigh. Black links only (the paper's "we assume the context is a stroke weight")
            // measures the same everywhere but Consolas 'k'@16, whose grey pt3->pt10 link GDI does
            // cut in (3,616 -> 1,340 with it) -- so it is the phantom, not the colour, that decides.
            // Weight 5,847,329 -> 5,644,642, all of it Verdana Italic (752,685 -> 549,998).
            // WPF_CT_CUTIN_SCOPE=all restores the cut-in on phantom links; =black restricts it.
            int distance = value;
            bool tookControlValue = true;
            bool phantomLink = _gs.Zp1 == 1 && (p >= _realPoints || _gs.Rp0 >= _realPoints);
            bool unroundedScope = s_cutInUnroundedScope switch
            {
                "all" => true,
                "black" => linkType == 1,
                _ => !phantomLink,
            };
            // WHEN the unrounded cut-in applies is not a question about phantoms. itrp_MIRP
            // runs it unconditionally on the ClearType axis while gs[0x88] bit 2 is CLEAR, and
            // only inside the round branch while it is SET -- and gs[0x88] is itrp_INSTCTRL's
            // word, so bit 2 is selector 3, NATIVE CLEARTYPE MODE, which we already read.
            // WPF_CT_CUTIN_SCOPE goes back to the phantom-link guess.
            bool cutInApplies = round || (!BiLevelPass && InClearTypeDirection && !s_cutInRoundedOnly
                && (s_cutInGdiScope ? !NativeClearTypeMode : unroundedScope));
            // CONFIRMED by reading itrp_MIRP end to end (fontdrvhost+0x3ae90): both halves of
            // this are the binary's. The scope test is `globals[0x88] & 4` -- clear, and the
            // cut-in runs BEFORE the round branch, so on every MIRP; set, and it runs only inside
            // it -- and globals+0x88 is itrp_INSTCTRL's word, so bit 2 is selector 3, native
            // ClearType mode. And:
            // itrp_MIRP scales the DIFFERENCE, not the threshold, and compares STRICTLY:
            //     off the ClearType axis   cutIn <  (cvt - orig)        -> take the outline
            //     on it                    cutIn < ((cvt - orig) * 16)
            // Dividing the threshold instead truncates -- the default cut-in is 68, and 68/16
            // is 4, not 4.25 -- so a difference of exactly 4 discarded a control value GDI
            // keeps, and >= discarded one more at the boundary itself.
            bool over = s_cutInExact
                ? (long) Math.Abs(value - original) * (shrink ? s_cutInDivisor : 1)
                  > (long) _gs.ControlValueCutIn * stretch
                : Math.Abs(value - original) >= cutIn;
            if (cutInApplies && _gs.Zp0 == _gs.Zp1 && over)
            { distance = original; tookControlValue = false; }
            // DIAGONAL STROKE-WEIGHT CONTROL, under test. Verdana's 'x' sets a projection vector
            // along each arm with SDPVTL and then MIRPs the far side to cvt[26] = 1.0px, which
            // snaps an arm whose natural weight is 1.0938 -- and the measured deficit is exactly
            // that, our arms one 6x sample narrower than GDI's at every diagonal edge while the
            // orthogonal glyphs are pixel-exact. This asks whether GDI leaves a control value
            // alone when the vector is neither axis: WPF_CT_DIAG_CVT=0 keeps the outline distance.
            // 0 = any non-axis projection; 2 = only the DIAGONAL-CONTROL IDIOM, a projection
            // vector set along the stroke by SDPVTL while the freedom vector stays on x. Verdana's
            // 'x' is that idiom exactly; a glyph that measures AND moves along the same diagonal is
            // doing something else, and taking 0 to the whole oracle costs 382k.
            if (s_diagCvt != 1 && !BiLevelPass && ClearTypeInfo && tookControlValue
                && !(_gs.ProjX == 0 && _gs.ProjY == 0x4000)
                && !(_gs.ProjX == 0x4000 && _gs.ProjY == 0)
                && (s_diagCvt == 0 || (_gs.FreeX == 0x4000 && _gs.FreeY == 0)))
            { distance = original; tookControlValue = false; }
            // WPF_CT_NOROUND_X=1: do not round a control-value distance in the ClearType
            // direction. Visual TrueType, driven to Arial 'H' at 9pt/12ppem with pixels shown,
            // puts the cap stem's left edge ON a pixel boundary and its right edge PAST the
            // next one -- Microsoft's own rasterizer does not snap it to a whole pixel, and our
            // measured widths agree (GDI 1.18px against our 1.00). Adding a constant to rounded
            // stems was tried and is worse (s_stemFatRounded); not rounding them is the other
            // reading, and it is worse too -- 54,934 -> 55,545, 30 cases worse against 11 better.
            // Less wrong than the constant (+611 against +1,459) and still wrong. Rounding is
            // right for most stems and wrong for the cap stem, so the decision depends on
            // something per-stem that neither switch expresses. Kept so the third person to
            // look at VTT's picture does not spend the evening rediscovering it.
            // WPF_MIRP_CENSUS=1: which ROUND STATE a MIRP rounds with, and on which axis.
            // itrp_MIRP has three shapes chosen by localGS+0xa4: 0 calls the round function pointer
            // at globals+0x90, while 1 and 2 do the rounding INLINE -- `(v+2)&~3` (the sixteenth)
            // when localGS+0xcc is set, `(v+0x20)&~0x3f` (the whole pixel) when it is not -- and
            // never consult gs+0x90 at all. That looked like a divergence worth porting, because it
            // would mean RTG, SROUND, RTHG, RDTG and ROFF alike are IGNORED there while we honour
            // the state. The census says it would touch 12% of rounded x ClearType MIRPs: 1,040
            // ROFF, 290 ToHalfGrid and 4 UpToGrid against 9,947 ToGrid over five sizes.
            //
            // DO NOT PORT IT. Every writer of localGS+0xa4 stores ZERO -- RDTG, ROFF, RTDG, RTHG,
            // RUTG, SROUND, S45ROUND, LSW, LSWCI, SFVTCA_0/_1, SPVTCA_0/_1, SPVTL, SFVTL, SDPVTL,
            // SFVTPV, WFV, WPV and SetElementPtr -- and the only nonzero writers are SVTCA_0/_1,
            // which rewrite it to 2 (y) or 1 (x) ONLY IF IT IS ALREADY NONZERO. itrp_RTG is the one
            // round-state opcode that does not clear it. So the field means "the round state is
            // plain RTG and the vectors are axis-aligned", and whenever an inline path can run the
            // state is necessarily ToGrid -- exactly where inline rounding and honouring the state
            // are the same thing. The census is kept because it is the measurement that proves the
            // change would be a 1,334-MIRP no-op at best.
            // (itrp_SetRoundValues and itrp_SROUND also write [x9/x10, #0xa4], but that is
            // GLOBALS+0xa4, the SROUND phase -- a different struct at the same offset.)
            if (s_mirpCensus && round)
            {
                string key = $"{_gs.Round}|{(IsHorizontalProjection ? "x" : "y")}|ct={InClearTypeDirection}";
                lock (s_mirpSeen) s_mirpSeen[key] = s_mirpSeen.TryGetValue(key, out long n) ? n + 1 : 1;
            }
            if (round && !(s_noRoundX && tookControlValue && !BiLevelPass && InClearTypeDirection))
                distance = RoundDistance(distance, linkType: linkType);

            // WPF_CT_STEMSUBPX=1: a stem's WIDTH in ClearType is the OUTLINE distance floored to a
            // whole SUBPIXEL. Measured off GDI's own pixels with the standalone PoC, which counts
            // saturated subpixels in a stem row (a box of N subpixels filters to N-2 saturated).
            // Verdana 'H', design stem 0.09766em, natural width in subpixels against what GDI draws:
            //     ppem     11    12    13    14    16    18    24
            //     natural 3.22  3.52  3.81  4.10  4.69  5.27  7.03
            //     floor      3     3     3     4     4     5     7
            //     GDI        3     3     3     4     4     5     7      <- exact
            // So ClearType quantises stem width to a THIRD of a pixel, not to a whole one, and it
            // measures the stroke rather than the control value. This is the surgical form of that:
            // XHintMode 14 expresses the same idea but moves POSITIONS onto the third grid as well
            // and fails 361 tests, so change only the width.
            // ...and only for a link GDI would call a STEM. DoubleCheckLinkColor accepts a pair
            // only when the two points are ADJACENT ON THE SAME CONTOUR and the segment between
            // them is no steeper than 2:1 -- which is exactly a stroke's two sides joined across
            // its end ('H's stem edges are adjacent across the stem's foot). Without this the rule
            // fires on every black link, most of which are spacing rather than weight.
            if (s_stemSubpx && !BiLevelPass && InClearTypeDirection && tookControlValue
                && _gs.Zp0 == _gs.Zp1 && !phantomLink && linkType == 1
                && (!s_stemSubpxAdj || PhaseAdjacent(_gs.Rp0, p, _realPoints)))
            {
                // Quantise the EXACT natural width. `original` comes from OrgX, which is the
                // scaled outline already rounded into 26.6; the PoC measured GDI against the
                // unrounded design width, and near a subpixel boundary that rounding decides
                // which way floor() falls. MeasureOriginalExact keeps the font units.
                int exact = s_stemSubpxExact ? MeasureOriginalExact(_gs.Zp1, p, _gs.Zp0, _gs.Rp0) : original;
                int mag = exact < 0 ? -exact : exact;
                int thirds = s_stemSubpxRound == 1 ? (int) (((long) mag * 3 + 32) / 64)
                           : s_stemSubpxRound == 2 ? (int) (((long) mag * 3 + 63) / 64)
                           : (int) ((long) mag * 3 / 64);         // 0 floor, 1 nearest, 2 ceil
                int snapped = (int) ((long) thirds * 64 / 3);
                if (snapped > 0) distance = exact < 0 ? -snapped : snapped;
            }

            // THE PPEM GATE BELOW IS NOT ABOUT STROKE WEIGHT. Read every glyph in the lamp report
            // against GDI, per size, and 11-13 is where nearly everything goes wrong -- straight
            // stems and CURVES alike, with the curves exact on either side of it:
            //     'l'   10 --   11 --   12 --   13 --   14 --      (this correction; exact)
            //     'o'   10 --   11  6   12  4   13  6   14  2
            //     'e'   10 --   11  3   12  2   13  4   14 --
            //     'c'   10 --   11  3   12  3   13  3   14 --
            // ('--' is every lamp identical; a number is columns that differ.) A bowl has no
            // control-value stroke weight on the side that is wrong, so this correction cannot be
            // what those need -- and yet they fail over exactly the sizes it covers and are perfect
            // outside it. That is one anomaly at 11, 12 and 13 with two symptoms, and the six
            // sixty-fourths below is a patch on the one symptom it happens to fit.
            //
            // AND THE GATE BELOW IS NOT WHAT CAUSES IT, which had to be checked because this
            // correction is gated to those same three sizes and could have been manufacturing the
            // pattern it is quoted as evidence for. Turned OFF, the curves keep it:
            //     'e'   10 --   11  3   12  2   13  4   14 --
            //     'c'   10 --   11  3   12  3   13  3   14 --
            //     'l'   10 --   11  2   12  2   13  2   14 --   (what this correction fixes)
            // Exact at 10 and at 14, wrong at 11, 12 and 13, with nothing of ours applied.
            //
            // The gasp table is not the boundary either: Segoe UI reads GRIDFIT|DOGRAY|SYM_GRIDFIT
            // flat from 9ppem to 16 and only gains SYM_SMOOTHING at 20. (Which is its own question --
            // we do not implement symmetric smoothing, and 20ppem and up is where GDI turns it on.)
            //            // AND IT IS NOT A BRANCH WE MISS. Comparing every control value prep writes at 12ppem
            // against 14, exactly one is treated differently across the boundary -- cvt[240], which
            // the pre-program adjusts by a whole pixel under an explicit 'if ppem is 11 to 13' and
            // leaves alone at 14. We take that branch and apply that pixel; the trace shows it
            // (2.734px -> 1.734px). Thirty-one control values carry a per-ppem adjustment at 12 and
            // thirty-one at 14, and the sets differ by that one entry and one other.
            //
            // So the anomaly is not the font steering us somewhere we do not go. Whatever GDI does
            // at these three sizes, it does it with the same control values we end up holding:
            // cvt[131] and cvt[132] both arrive at 0.9688 and both leave prep's ROUND at 1.0.
            //            // 'I' is the exception that keeps the story honest: it is wrong at EVERY size, and that
            // is the separate MDAP position fault (1.109 rounded up to 1.125 where GDI is at or
            // below 1.0), which has nothing to do with the range.
            //            // SIX SIXTY-FOURTHS ON A CONTROL-VALUE STROKE WEIGHT, and the six is not fitted: it is
            // what GDI's own geometry says. The bar solver reproduces GDI's lamps at residual zero,
            // so its answer IS GDI's outline, and for Segoe UI's 'l' at 12ppem it returns a stem of
            // 1.000 -> 2.094 where the program hands us 1.000 -> 2.000. 2.094 - 2.000 is 6/64.
            // Swept independently against the window it is also the optimum, and a sharp one:
            //     3  1,299,900   4  1,299,074   5  1,316,092   6  1,216,266   7  1,239,087   8  1,341,169
            // The two agreeing is the whole reason to believe this rather than the 11/64 I tried
            // first -- 11 lights the same lamps in the probe, so the probe cannot tell them apart,
            // and it costs the window 37,000 where 6 earns it 99,000.
            //
            // It is SELF-LIMITING, which is why it is not a Segoe UI hack: it fires only where the
            // control value wins the cut-in on an UNROUNDED x MIRP. Measured on Arial, Tahoma,
            // Verdana and Times, every stem at every size from 8 to 24 is byte-identical with it
            // and without -- none of them takes that path at these sizes.
            //
            // THE PPEM GATE IS EMPIRICAL AND I CANNOT DERIVE IT. Ungated, the only thing that
            // breaks is Segoe UI at 14 and 24, where we already match GDI exactly and the widening
            // makes us a half-lamp too fat. Nothing in the control value separates those from 11
            // to 13 -- 11 and 14 round by the SAME 0.125, in the same direction, to the same 1.0px
            // -- so the boundary is a measured fact without a mechanism. If someone finds the rule,
            // this gate is what it has to reproduce.
            // WPF_STEM_NATURAL: the measured rule rather than the fitted one. Three column
            // profiles of Segoe UI 'l' say GDI's greyscale stem is the NATURAL width plus about
            // half a pixel at every size from 9 to 18 (quantised to an eighth), where ours is a
            // control value rounded to the grid and then nudged by s_stemFat. So take the natural
            // distance and add half a pixel, skipping the control value and the rounding both.
            // MEASURED AND REJECTED, and kept here so it is not re-derived. Against the glyph
            // parity repertoire it takes the disagreement from 54,934 to 89,871 -- 207 cases worse,
            // 28 better. The regressions are concentrated in the BOLD faces (repertoire@17b +1,426,
            // @18b +1,563, @20b +1,393, 'b'@11 +1,614), which is the tell: half a pixel on top of
            // an already-wide bold stem overshoots, and the rule was read off ONE glyph of ONE face
            // -- Segoe UI Regular 'l'. The measurement is right and the generalisation was not
            // tested before being believed.
            // <para>Even for the regular face it is mixed: regular@10 183->48, @16 103->16, @17
            // 44->16, @18 50->18 all improve, while regular@13 goes 79->1,893. So "natural + half a
            // pixel" is the right description of what GDI does to Segoe UI Regular's 'l' and is NOT
            // a rule our fitting can adopt wholesale.</para>
            // <para>WHY it fails on bold is now measured too, and the two weights behave OPPOSITELY
            // -- stem widths in pixels, 'l':</para>
            // <code>  regular @12  hinted 1.00  natural 0.97  GDI 1.50   wider than both
            //         regular @16  hinted 1.00  natural 1.25  GDI 1.75   wider than both
            //         bold    @12  hinted 2.00  natural 1.84  GDI 2.00   equals the hinted stem
            //         bold    @16  hinted 2.00  natural 2.42  GDI 2.00   NARROWER than natural</code>
            // <para>For bold GDI's greyscale stem is exactly its whole-pixel hinted stem, and at 16
            // that is narrower than the natural outline. For regular it is wider than the hinted
            // AND the natural one. So there is no single "GDI widens by w" to find: adding half a
            // pixel to bold pushes a stem that GDI is holding at 2.00 up to 2.34, which is what
            // those +1,400 bold regressions are.</para>
            if (s_stemNatural && tookControlValue && !BiLevelPass && InClearTypeDirection)
                distance = original + (original < 0 ? -32 : 32);

            if (s_stemFat != 0 && !s_stemNatural && tookControlValue && (!round || s_stemFatRounded) && !BiLevelPass
                && !(s_stemFatNoMin && keepMinimum)
                && (s_stemFatCvt < 0 || cvt == s_stemFatCvt)
                && (!s_stemFatStraight || !TouchesACurve(z, p))
                && InClearTypeDirection && _ppem >= s_stemFatLo && _ppem <= s_stemFatHi
                && (!s_stemFatExact || distance == 64 || distance == -64))
                distance += distance < 0 ? -s_stemFat : s_stemFat;

            if (keepMinimum)
            {
                int floor = minimum;
                if (original >= 0) { if (distance < floor) distance = floor; }
                else { if (distance > -floor) distance = -floor; }
            }

            // The colour handed to DoubleCheckLinkColor is the RAW opcode bits -- itrp_MIRP passes
            // `local_6c & 3`, where local_6c is MIRP[abcde]'s flag byte -- not our EffectiveLinkType,
            // which reinterprets a black link by probing the ink and is ours, not GDI's. The colour
            // only survives the double check when the two points are not contour neighbours, but that
            // is exactly the case that decides whether a stem pair forms.
            // WPF_MDRP_TRACE=1 covers MIRP too: it is what places a stem, so a stem that comes out
            // the wrong width or in the wrong column is almost always one of these lines.
            if (s_mdrpTrace)
                Console.Error.WriteLine($"   MIRP p={p} rp0={_gs.Rp0} cvt={cvt} link={linkType}"
                    + $" round={round} min={keepMinimum} cvtval={value / 64f:0.####}"
                    + $" orig={original / 64f:0.####} -> dist={distance / 64f:0.####}"
                    + $" cur={current / 64f:0.####} move={(distance - current) / 64f:0.####}"
                    + $" pv=({_gs.ProjX},{_gs.ProjY}) ctDir={InClearTypeDirection} ppem={_ppem}");
            LinkX(_gs.Zp1, p, _gs.Zp0, _gs.Rp0, linkType, doubleCheck: true, phaseType: op & 3);
            MovePoint(z, p, distance - current);

            _gs.Rp1 = _gs.Rp0;
            _gs.Rp2 = p;
            if (setRp0) _gs.Rp0 = p;
        }

        // ---- shifting and interpolating -------------------------------------------------------------

        private byte TouchMask()
        {
            byte mask = 0;
            if (_gs.FreeX != 0) mask |= TagTouchX;
            if (_gs.FreeY != 0) mask |= TagTouchY;
            return mask;
        }

        /// <summary>How far the reference point of SHP/SHC/SHZ has already been moved: everything
        /// they shift moves by exactly that, so a whole feature travels together.</summary>
        private bool ReferenceShift(bool useRp1, out int dx, out int dy, out int zone, out int point)
        {
            zone = useRp1 ? _gs.Zp0 : _gs.Zp1;
            point = useRp1 ? _gs.Rp1 : _gs.Rp2;
            dx = dy = 0;

            Zone z = ZoneOf(zone);
            if (point >= z.PointCount) return false;

            int moved = Project(z.CurX[point] - z.OrgX[point], z.CurY[point] - z.OrgY[point]);
            dx = MulDiv(moved, _gs.FreeX, _dotProduct);
            dy = MulDiv(moved, _gs.FreeY, _dotProduct);
            return true;
        }

        /// <summary>Whether SHP moves and touches a looped point that IS the reference point,
        /// as FreeType does, instead of skipping it.</summary>
        private static readonly bool s_shpMovesRefPoint =
            Environment.GetEnvironmentVariable("WPF_CT_SHP_REF") == "move";

        private void ShiftByPoint(bool useRp1)
        {
            if (!ReferenceShift(useRp1, out int dx, out int dy, out int refZone, out int refPoint))
            {
                for (int i = 0; i < _gs.Loop; i++) Pop();
                _gs.Loop = 1;
                return;
            }

            byte touch = TouchMask();
            Zone z = ZoneOf(_gs.Zp2);
            for (int i = 0; i < _gs.Loop; i++)
            {
                int p = Pop();
                // SKIPPING THE REFERENCE POINT ALSO SKIPS TOUCHING IT, and a point's touch state
                // outlives this instruction: IUP interpolates between TOUCHED points, so a point
                // GDI touches and we do not becomes an anchor there and an interpolated point
                // here. FreeType's Ins_SHP has no such special case -- it moves and touches every
                // point in the loop. WPF_CT_SHP_REF=move drops the guard, and MEASURED it changes
                // nothing at all: the parity total is 3,271,235 either way, byte for byte, and
                // 'n' at 12ppem solves to the same coordinates. The guard is a real deviation from
                // the spec and it never fires on this corpus, so it is not the cause of the
                // touch-set difference -- kept expressible so the next reader need not re-derive
                // that.
                if (p == refPoint && _gs.Zp2 == refZone && !s_shpMovesRefPoint) continue;
                LinkX(_gs.Zp2, p, refZone, refPoint);
                MoveDirect(z, p, dx, dy, touch);
            }
            _gs.Loop = 1;
        }

        private void ShiftContour(bool useRp1)
        {
            int contour = Pop();
            if (!ReferenceShift(useRp1, out int dx, out int dy, out int refZone, out int refPoint)) return;

            // itrp_SHC@180088890 RUNS THE PHASE PASS ITSELF, on the same gate IUP[x] uses, and then
            // ADDS THE REFERENCE POINT'S STORED PHASE to the x it is about to shift the contour by:
            //     ExecutePhaseControl(gs, elem);
            //     iVar12 = nodes[refPoint].value + iVar12;
            // SHC shifts a contour by how far its reference point moved, so if the phase is what just
            // moved that reference point, the contour has to follow it. Only SHC and IUP[x] do this;
            // every other opcode leaves the phase to the end of the program.
            if (s_phaseAtShc && !_phaseApplied && _gs.Zp2 == 1 && refZone == 1)
            {
                ApplyPhaseAtIup();
                if (_phaseApplied && (uint) refPoint < (uint) _phaseVal.Length) dx += _phaseVal[refPoint];
            }
            Zone z = ZoneOf(_gs.Zp2);
            int first = contour == 0 ? 0 : _glyphZone.Contours[Math.Min(contour - 1, _contourCount - 1)] + 1;
            int last = _gs.Zp2 == 0 ? z.PointCount - 1
                                    : _glyphZone.Contours[Math.Min(contour, _contourCount - 1)];
            if (_gs.Zp2 == 0) first = 0;

            for (int p = first; p <= last && p < z.PointCount; p++)
                if (p != refPoint || _gs.Zp2 != refZone)
                {
                    LinkX(_gs.Zp2, p, refZone, refPoint);
                    MoveDirect(z, p, dx, dy, 0);
                }
        }

        private void ShiftZone(bool useRp1)
        {
            int which = Pop() & 1;
            if (!ReferenceShift(useRp1, out int dx, out int dy, out _, out _)) return;

            Zone z = ZoneOf(which);
            int last = which == 0 ? z.PointCount - 1 : _realPoints - 1;
            for (int p = 0; p <= last && p < z.PointCount; p++)
                MoveDirect(z, p, dx, dy, 0);
        }

        /// <summary>IP: put each point back in the same PROPORTION between rp1 and rp2 that it held
        /// in the outline. This is what keeps the middle of a curve where it belongs once the two
        /// ends have been pushed onto the grid.
        /// <para>The proportion is taken in FONT UNITS -- the outline as the designer drew it, not
        /// as it was scaled -- for both the span and each point's place along it. Taking it from the
        /// scaled outline instead loses to rounding exactly where the span is short, and short spans
        /// are what IP is for. '&amp;' and '@' lean on it hardest, nine times each, and were the two
        /// glyphs in the whole font that came out the wrong shape.</para>
        /// <para>And nothing is clamped. A point outside the pair is EXTRAPOLATED by the same ratio;
        /// carrying it along with the nearer reference instead is a reasonable-sounding rule that
        /// the rasterizer this has to agree with does not have.</para></summary>
        /// <summary>WHY THE TRACE ABOVE EXISTS, and what it settled about Tahoma's 'H'.
        /// <para>That glyph's left stem is exactly where GDI has it and its whole RIGHT stem sits
        /// about 0.11px too far right, which looked at first like a rigid displacement of the
        /// glyph and is not. Followed to the instruction, the right stem is placed by an IP whose
        /// two references are BOTH PHANTOM POINTS -- rp1 the origin, rp2 the advance -- so what
        /// that instruction does is scale the glyph into its compatible width, and the ratio is
        /// the whole story.</para>
        /// <para>Measured rather than inferred, which mattered: the face's design advance is 1383
        /// font units and the advance phantom sits at 8.000px, so we compress by 8/8.10 = 0.988
        /// where GDI's own intervals want about 0.964. Quantizing the phantom differently is not
        /// the answer -- WPF_PP2_ROUND measures 2,343,248 rounded, 2,482,248 not quantized at all,
        /// and 7,182,196 ceiled -- so rounding it is right and already what ships.</para>
        /// <para>AND THE RATIO IS NOT SYSTEMATIC, which is what kills the idea. Solved against
        /// GDI's pixels at 12ppem, Tahoma's 'H' and 'n' are too far right on their right-hand side,
        /// its 'o' is 0.68px WIDER than GDI's overall, and its 'm' sits up to 0.6px LEFT. One
        /// compression ratio cannot produce all three. What is left is per-glyph shape.</para>
        /// <para>Also dead, and reverted rather than shipped: rounding a distance measured from a
        /// PHANTOM point on the whole pixel while everything else keeps the fine grid. It reads
        /// well -- such a distance is a side bearing, which decides where a glyph sits between its
        /// neighbours rather than how it is shaped -- and it changes NOTHING, 2,343,248 either way,
        /// because the instructions that place these edges measure from outline points. The 'H'
        /// instruction this was built for has rp0=1.</para></summary>
        private void InterpolatePoints()
        {
            Zone z0 = ZoneOf(_gs.Zp0), z1 = ZoneOf(_gs.Zp1), z2 = ZoneOf(_gs.Zp2);

            // A twilight point has no font-unit original -- it was invented by the program, and its
            // 'orus' would read as the origin for every one of them -- so there the scaled positions
            // are all there is.
            bool twilight = _gs.Zp0 == 0 || _gs.Zp1 == 0 || _gs.Zp2 == 0;

            if (_gs.Rp1 >= z0.PointCount || _gs.Rp2 >= z1.PointCount)
            {
                for (int i = 0; i < _gs.Loop; i++) Pop();
                _gs.Loop = 1;
                return;
            }

            int baseX = twilight ? z0.OrgX[_gs.Rp1] : z0.OrusX[_gs.Rp1];
            int baseY = twilight ? z0.OrgY[_gs.Rp1] : z0.OrusY[_gs.Rp1];
            int baseCurX = z0.CurX[_gs.Rp1], baseCurY = z0.CurY[_gs.Rp1];

            int oldRange = twilight
                ? DualProject(z1.OrgX[_gs.Rp2] - baseX, z1.OrgY[_gs.Rp2] - baseY)
                : DualProject(z1.OrusX[_gs.Rp2] - baseX, z1.OrusY[_gs.Rp2] - baseY);
            int curRange = Project(z1.CurX[_gs.Rp2] - baseCurX, z1.CurY[_gs.Rp2] - baseCurY);

            if (_dumpActive)
                Console.Error.WriteLine($"      IP between rp1={_gs.Rp1} and rp2={_gs.Rp2}:"
                    + $" font units {baseX} to {(twilight ? z1.OrgX[_gs.Rp2] : z1.OrusX[_gs.Rp2])}"
                    + $" (range {oldRange}), pixels {baseCurX / 64f:0.000} to"
                    + $" {z1.CurX[_gs.Rp2] / 64f:0.000} (range {curRange / 64f:0.000})"
                    + $" -- scaling by {(oldRange == 0 ? 0 : 64.0 * curRange / oldRange):0.0000}"
                    + $" px per font unit");

            for (int i = 0; i < _gs.Loop; i++)
            {
                int p = Pop();
                if (p >= z2.PointCount) continue;

                int orgDist = twilight
                    ? DualProject(z2.OrgX[p] - baseX, z2.OrgY[p] - baseY)
                    : DualProject(z2.OrusX[p] - baseX, z2.OrusY[p] - baseY);
                int curDist = Project(z2.CurX[p] - baseCurX, z2.CurY[p] - baseCurY);

                int newDist;
                if (orgDist == 0)
                    newDist = 0;
                else if (oldRange != 0)
                    newDist = MulDiv(orgDist, curRange, oldRange);
                else
                    // Both references on the same spot, which is a glyph saying something
                    // meaningless. Shift rather than divide by nothing.
                    newDist = orgDist - oldRange + curRange;

                MovePoint(z2, p, newDist - curDist);
                // PHASE tree: IP places p BETWEEN two references, so it takes both as parents --
                // GDI's AddProportion, which itrp_IP calls under ClearType. The guards are its
                // own: indices distinct, no cycle, and only if BOTH slots are still empty.
                if (_gs.Zp2 == 1) PhaseProportion(_gs.Rp1, p, _gs.Rp2);
            }
            _gs.Loop = 1;
        }

        /// <summary>IUP: everything the program did NOT touch is carried between the points it did,
        /// contour by contour. Without it a glyph comes apart -- the hinted points move and the
        /// curves between them stay where they were.</summary>
        private void InterpolateUntouched(bool horizontal)
        {
            Zone z = _glyphZone;
            byte mask = horizontal ? TagTouchX : TagTouchY;
            int[] cur = horizontal ? z.CurX : z.CurY;
            int[] org = horizontal ? z.OrgX : z.OrgY;
            int[] orus = horizontal ? z.OrusX : z.OrusY;

            s_iupTraceOn = s_iupTrace && !BiLevelPass;
            if (s_iupTraceOn)
                Console.Error.WriteLine($"=== IUP[{(horizontal ? 'x' : 'y')}]");
            int point = 0;
            for (int contour = 0; contour < _contourCount; contour++)
            {
                int endPoint = Math.Min(z.Contours[contour], _realPoints - 1);
                int firstPoint = point;
                if (endPoint < firstPoint) continue;

                while (point <= endPoint && (z.Tags[point] & mask) == 0) point++;
                if (s_iupTraceOn && contour == 0)
                    for (int k = 0; k < _realPoints; k++)
                        Console.Error.WriteLine($"  P{k,3} org({z.OrgX[k],5},{z.OrgY[k],5})"
                            + $" orus({z.OrusX[k],6},{z.OrusY[k],6})"
                            + $" cur({z.CurX[k],5},{z.CurY[k],5})"
                            + $" {((z.Tags[k] & TagTouchX) != 0 ? "X" : ".")}"
                            + $"{((z.Tags[k] & TagTouchY) != 0 ? "Y" : ".")}"
                            + $"{((z.Tags[k] & TagOn) != 0 ? "o" : "-")}");
                if (s_iupTraceOn)
                    Console.Error.WriteLine($"IUP[{(horizontal ? 'x' : 'y')}] contour {contour}"
                        + $" {firstPoint}..{endPoint}"
                        + (point > endPoint ? "  NO TOUCHED POINT, left alone"
                           : $"  first touched {point}"));
                if (point > endPoint) continue;

                int firstTouched = point, lastTouched = point;
                point++;
                while (point <= endPoint)
                {
                    if ((z.Tags[point] & mask) != 0)
                    {
                        Carry(cur, org, orus, lastTouched + 1, point - 1, lastTouched, point);
                        lastTouched = point;
                    }
                    point++;
                }

                if (lastTouched == firstTouched)
                {
                    // Exactly one touched point: the whole contour rides with it.
                    int shift = cur[lastTouched] - org[lastTouched];
                    if (shift != 0)
                        for (int i = firstPoint; i <= endPoint; i++)
                            if (i != lastTouched) cur[i] = org[i] + shift;
                }
                else
                {
                    // The run that wraps past the end of the contour, in the two pieces it really
                    // is: after the last touched point, and before the first.
                    Carry(cur, org, orus, lastTouched + 1, endPoint, lastTouched, firstTouched);
                    if (firstTouched > 0)
                        Carry(cur, org, orus, firstPoint, firstTouched - 1, lastTouched, firstTouched);
                }
            }
        }

        /// <summary>The points from <paramref name="from"/> to <paramref name="to"/>, moved to hold
        /// the place they held between two points that HAVE been moved.
        /// <para>The proportion is taken in FONT UNITS, not in the scaled outline. The two disagree:
        /// scaling rounds, and two references a whisker apart in the design can land on the same
        /// scaled coordinate, at which point a proportion computed from the scaled pair divides by
        /// nothing and every point between them piles onto one spot. It is the glyphs with the most
        /// interpolation in them that show it.</para></summary>
        /// <summary>Place a run of untouched points between two touched ones.
        /// <para>READ OUT OF itrp_IUP@140039020 AND CONFIRMED CLAUSE BY CLAUSE, so the shape below
        /// is not a reconstruction from behaviour. The binary's inner loop is
        /// <code>
        ///     lo = a; hi = b;  if (ref[b] &lt;= ref[a]) { lo = b; hi = a; }
        ///     den  = |ref[a] - ref[b]|;   span = cur[hi] - cur[lo];
        ///     for each untouched i:
        ///         if (org[lo] &lt; org[i]) {
        ///             if (org[i] &lt; org[hi]) cur[i] = ((ref[i]-ref[lo])*span + den/2)/den + cur[lo];
        ///             else                  cur[i] = org[i] + (cur[hi] - org[hi]);
        ///         } else if (org[hi] &lt;= org[i]) cur[i] = org[i] + (cur[hi] - org[hi]);
        ///         else                          cur[i] = org[i] + (cur[lo] - org[lo]);
        /// </code>
        /// and the two arrays really are different ones. The disassembly at 1400390c0 loads
        /// <c>ldr x13,[x7,#0x10]</c> -- elem+0x10, the SCALED original -- as the array every
        /// bracket test and every outside-the-bracket shift is written in, and
        /// <c>ldr x2,[x7,#0x20]</c> -- elem+0x20, ORUS, the design units -- as the array the ratio
        /// is taken in, the second being overwritten by the first when gs[0x196] is set. So IUP
        /// brackets on the grid and interpolates in the design, which is what this does.</para>
        /// <para>WORTH KNOWING BECAUSE IT RETIRES A SUSPECT. Times New Roman is the largest
        /// remaining pool and it is an interpolation problem -- at 14ppem '9' every one of the
        /// eight x-touched points agrees with GDI EXACTLY and all seven differences are on points
        /// IUP placed -- so "our interpolation is subtly wrong" was the obvious reading. It is not:
        /// the formula, the two arrays, the rounding, the bracket order and the tie are now all
        /// the binary's. Whatever Times needs is in WHICH POINTS ARE TOUCHED, not in what happens
        /// to the ones that are not. See SolveGdisOutlineXy, which reports the interpreter's own
        /// point index, its touch flag and its on/off-curve flag for exactly this question.</para>
        /// </summary>
        private static void Carry(int[] cur, int[] org, int[] orus, int from, int to, int ref1, int ref2)
        {
            if (from > to) return;
            if (s_iupTrace && s_iupTraceOn)
                Console.Error.WriteLine($"   run {from}..{to} between {ref1} and {ref2}"
                    + $"  org({org[ref1]},{org[ref2]}) cur({cur[ref1]},{cur[ref2]})"
                    + $" orus({orus[ref1]},{orus[ref2]})");

            // A TIE GOES TO THE SECOND REFERENCE, NOT THE FIRST. itrp_IUP picks its lower
            // endpoint with
            //     lo = a; hi = b;  if (ref[b] <= ref[a]) { lo = b; hi = a; }
            // -- a NON-STRICT comparison, so when the two references share a reference coordinate
            // the run is anchored on `b`, the touched point that ENDS it. We swapped on `>` only,
            // which leaves `a` as the anchor and takes `a`'s delta.
            // <para>It is visible only on a FLAT run, because equal references are exactly the
            // den == 0 case, and there the whole run is shifted rigidly by the anchor's delta --
            // so the choice of anchor is the entire result, not a rounding of it. Flat runs are
            // serifs and bars: two touched points that differ in the design but land on the same
            // reference coordinate. WPF_CT_IUP_FLATTIE=0 restores the old tie.</para>
            int orus1 = orus[ref1], orus2 = orus[ref2];
            if (s_iupFlatTie ? orus1 >= orus2 : orus1 > orus2)
            {
                (orus1, orus2) = (orus2, orus1);
                (ref1, ref2) = (ref2, ref1);
            }

            int org1 = org[ref1], org2 = org[ref2];
            int delta1 = cur[ref1] - org1, delta2 = cur[ref2] - org2;

            if (orus1 == orus2)
            {
                // NOTHING TO INTERPOLATE ALONG, AND GDI SHIFTS THE WHOLE RUN BY THE LOWER
                // ENDPOINT'S DELTA. itrp_IUP reaches this as its `den == 0` branch and does
                // `cur[i] += iVar30` for every point of the run, where iVar30 is
                // `cur[lower] - scaledOrg[lower]` -- one delta, applied to all of them, with no
                // test of which side a point sits on. We split the run between the two deltas by
                // comparing each point against the lower bound, which is a different rule and is
                // not what the binary does. It arises on a FLAT edge, where two references that
                // differ in the design land on the same reference coordinate -- so it is the serifs
                // and bars, which is where the residual is.
                // WPF_CT_IUP_FLAT=0 restores the split.
                for (int i = from; i <= to; i++)
                    cur[i] = org[i] + (s_iupFlatLower ? delta1
                                                      : (org[i] <= org1 ? delta1 : delta2));
                return;
            }

            int scale = 0;
            bool haveScale = false;
            for (int i = from; i <= to; i++)
            {
                // THE UPPER TEST COMES FIRST. itrp_IUP asks
                //     if (org[lo] < org[i]) { if (org[i] < org[hi]) interpolate; else upper; }
                //     else                  { if (org[hi] <= org[i]) upper; else lower; }
                // which is "at or past the upper reference -> upper delta" checked BEFORE the
                // lower test, not after it. The two orders differ only when org[lo] > org[hi] --
                // the endpoints are ordered by the REFERENCE array, which is font units, and
                // scaling rounds, so two coordinates a whisker apart in the design can land the
                // other way round once scaled. A point between them then satisfies BOTH tests and
                // the order decides which delta it takes. WPF_CT_IUP_UPPER=0 asks the lower first.
                // <para>MEASURED EXACTLY NEUTRAL on the holdout -- 843,447 either way -- so the
                // case does not arise in the specimen. Kept because it is what the binary does
                // and it costs nothing; do not re-measure it looking for a win.</para>
                int x = org[i];
                if (s_iupUpperFirst)
                {
                    if (x >= org2) { cur[i] = x + delta2; continue; }
                    if (x <= org1) { cur[i] = x + delta1; continue; }
                }
                else
                {
                    if (x <= org1) { cur[i] = x + delta1; continue; }
                    if (x >= org2) { cur[i] = x + delta2; continue; }
                }

                // ONE DIVISION, ROUNDED HALF-UP -- not a fixed-point scale and then a multiply.
                // itrp_IUP's inner loop is
                //     v = ((ref[i] - refLo) * (curHi - curLo) + (den >> 1)) / den;  v += curLo;
                // with `den = refHi - refLo`, so it rounds ONCE, at the end, adding half the
                // denominator before an integer divide. We built a 16.16 scale with DivFix and
                // then applied it with MulFix, which rounds TWICE -- once into the scale and again
                // out of the product -- and the two do not agree on a 26.6 coordinate.
                // <para>It matters because IUP places most of the points in most glyphs: at 13ppem
                // Times' 'z' has six of its twenty-five points explicitly placed and the rest
                // interpolated, and the whole of that glyph's error is one interpolated point.
                // WPF_CT_IUP_ONESTEP=0 restores the two-step form.</para>
                if (s_iupOneStep)
                {
                    long den = s_iupRefScaled ? org2 - org1 : orus2 - orus1;
                    long num = s_iupRefScaled ? org[i] - org1 : orus[i] - orus1;
                    long span = (long) (org2 + delta2) - (org1 + delta1);
                    cur[i] = den == 0 ? org1 + delta1
                           : (int) ((num * span + (den >> 1)) / den) + org1 + delta1;
                    // WPF_CT_IUP_GRID=1: round the result onto the CLEARTYPE SIXTEENTH. GDI's
                    // glyph programs round on the sixteenth of a pixel, not the sixty-fourth we
                    // carry -- measured off its own pixels for prep (whole pixels) against the
                    // glyph programs (sixteenths). If its coordinates live on that grid then every
                    // interpolated point does too, which would leave the TOUCHED points right (they
                    // are placed by rounding anyway) and put the untouched ones a few sixty-fourths
                    // out, which is exactly the Times signature. REFUTED: 2,240,792 against
                    // 611,135 on the holdout. Whatever grid GDI's programs round on, the
                    // INTERPOLATION does not land on it -- consistent with itrp_IUP, whose inner
                    // loop is one rounded division by the reference span and nothing else.
                    if (s_iupGrid) cur[i] = ((cur[i] < 0 ? cur[i] - 2 : cur[i] + 2) / 4) * 4;
                    continue;
                }
                if (!haveScale)
                {
                    haveScale = true;
                    // itrp_IUP brackets by the SCALED original and then interpolates along ONE
                    // reference array, which is the scaled original too unless gs[0x196] is
                    // clear (a child-scaling case). We always ran the ratio on FONT UNITS.
                    // WPF_CT_IUP_REF=orus keeps that.
                    scale = s_iupRefScaled
                        ? DivFix((org2 + delta2) - (org1 + delta1), org2 - org1)
                        : DivFix((org2 + delta2) - (org1 + delta1), orus2 - orus1);
                }
                cur[i] = org1 + delta1 + MulFix(s_iupRefScaled ? org[i] - org1 : orus[i] - orus1, scale);
            }
        }


        /// <summary>ISECT: put a point where two lines cross. Used to build reference positions in
        /// the twilight zone out of directions the program has worked out for itself.</summary>
        private static readonly bool s_isectProportion =
            Environment.GetEnvironmentVariable("WPF_CT_ISECT_PROP") != "0";

        internal static int s_isectCount;

        /// <summary>CompDiv@140026480: the scaler's rounded divide. It adds half the denominator,
        /// signed to match the numerator, and then divides truncating -- round to nearest, ties
        /// away from zero -- and answers +-0x7fffffff for a zero denominator.</summary>
        private static long CompDiv(long numerator, long denominator)
        {
            if (denominator == 0) return numerator < 0 ? -0x7fffffff : 0x7fffffff;
            long half = denominator / 2;
            return ((numerator < 0) == (denominator < 0) ? numerator + half : numerator - half)
                   / denominator;
        }

        private void Intersect()
        {
            s_isectCount++;
            int b1 = Pop(), b0 = Pop(), a1 = Pop(), a0 = Pop(), p = Pop();
            Zone zp = ZoneOf(_gs.Zp2), za = ZoneOf(_gs.Zp1), zb = ZoneOf(_gs.Zp0);
            if (p >= zp.PointCount || a0 >= za.PointCount || a1 >= za.PointCount
                || b0 >= zb.PointCount || b1 >= zb.PointCount) return;

            // itrp_ISECT records the placed point as a PROPORTION between the FIRST line's two
            // endpoints, under the same mode-2 / axis-latch / flags-bit-1 gate as every other
            // recorder. Read from the ARM64 because Ghidra reuses the argument registers for
            // Mul26Dot6 results and the decompiled names are not point indices:
            //     140038c18  sub w14,w8,w23     ; dx = x[w5] - x[w7]   -> w7 is a0, w5 is a1
            //     140038c3c  mov w26,w5 ; mov w27,w7
            //     140038ecc  mov w4,w26 ; mov w2,w27 ; bl AddProportion   -> (a=w27, placed, b=w26)
            // so it is AddProportion(a0, p, a1). We recorded nothing at all here, which left every
            // ISECT-placed point out of the phase tree. Not rare: the opcode is in Arial (10 glyphs),
            // Times New Roman (8), Consolas (2), Verdana (1) and in the fpgm of Arial, Times and
            // Tahoma. WPF_CT_ISECT_PROP=0 goes back to recording nothing.
            // WHICH line the proportion is recorded against is chosen, not fixed: itrp_ISECT
            // compares the two cross terms and takes the b-line's endpoints unless the a-line's
            // term is the larger (140038de8: `cmp w8,w5 ; csel w26,w6,w26,gt`).
            if (s_isectProportion && _gs.Zp2 == 1 && _gs.Zp1 == 1 && ClearTypeInfo && !BiLevelPass)
            {
                // THROUGH Mul26Dot6, which is what itrp_ISECT does to both terms before
                // comparing them -- a 26.6 multiply, so the product is divided by 64 and ROUNDED.
                // Scaling both sides cannot change which is larger, but the rounding can, exactly
                // where the two terms are within a 64th of each other; and this opcode decides
                // which line a crossing point is phased against, so a tie going the other way
                // moves the point. MEASURED EXACTLY NEUTRAL -- 611,135 on the holdout and 10,327
                // on Arial Bold at 20ppem either way -- so no tie in this specimen is close enough
                // to turn. Kept because it is what the binary does and it is free; do not
                // re-measure it looking for a win. WPF_CT_ISECT_MUL=0 compares the raw products.
                long ra = (long) (za.CurX[a1] - za.CurX[a0]) * (zb.CurY[b1] - zb.CurY[b0]);
                long rb = (long) (za.CurY[a1] - za.CurY[a0]) * (zb.CurX[b1] - zb.CurX[b0]);
                if (s_isectMul26)
                {
                    ra = (ra + (ra < 0 ? -32 : 32)) / 64;
                    rb = (rb + (rb < 0 ? -32 : 32)) / 64;
                }
                if (Math.Abs(rb) > Math.Abs(ra)) PhaseProportion(a0, p, a1, axisGate: false);
                else PhaseProportion(b0, p, b1, axisGate: false);
            }
            // itrp_ISECT@140038a60, ported as it is written rather than as the specification
            // describes it. GDI does NOT evaluate a cross-product parameter: it ELIMINATES along
            // whichever axis the FIRST line (the b-line, whose points are popped first) runs
            // more along, with a rounded divide at every step, and then walks the SECOND line
            // from its own origin. The two orders are not the same arithmetic -- our single
            // 16.14 parameter accumulated error an intersection amplifies, and Arial 'X'@24 put
            // its crossing 29/64 from where GDI's pixels say it is.
            int dxb = zb.CurX[b1] - zb.CurX[b0], dyb = zb.CurY[b1] - zb.CurY[b0];
            int dxa = za.CurX[a1] - za.CurX[a0], dya = za.CurY[a1] - za.CurY[a0];
            int xb0 = zb.CurX[b0], yb0 = zb.CurY[b0];
            int xa0 = za.CurX[a0], ya0 = za.CurY[a0];

            long num, den;
            bool placed = false;
            if (dyb == 0)                                  // the b-line is horizontal
            {
                if (dxa == 0) { zp.CurX[p] = xa0; zp.CurY[p] = yb0; placed = true; num = den = 0; }
                else { num = ya0 - yb0; den = -dya; }
            }
            else if (dxb == 0)                             // ...or vertical
            {
                if (dya == 0) { zp.CurX[p] = xb0; zp.CurY[p] = ya0; placed = true; num = den = 0; }
                else { num = xa0 - xb0; den = -dxa; }
            }
            else if (Math.Abs(dxb) >= Math.Abs(dyb))       // eliminate along x
            {
                num = (ya0 - yb0) - CompDiv((long) (xa0 - xb0) * dyb, dxb);
                den = CompDiv((long) dxa * dyb, dxb) - dya;
            }
            else                                           // ...or along y
            {
                num = CompDiv((long) (ya0 - yb0) * dxb, dyb) + (xb0 - xa0);
                den = dxa - CompDiv((long) dya * dxb, dyb);
            }
            if (!placed)
            {
                if (den == 0)
                {
                    // Parallel. NOT "midway between the two line starts" as the specification
                    // says: GDI averages the two lines' MIDPOINTS, ((dA/2 + dB/2) + A0 + B0) / 2.
                    zp.CurY[p] = ((dya >> 1) + (dyb >> 1) + yb0 + ya0) >> 1;
                    zp.CurX[p] = ((dxa >> 1) + (dxb >> 1) + xb0 + xa0) >> 1;
                }
                else
                {
                    zp.CurY[p] = (int) CompDiv((long) dya * num, den) + ya0;
                    zp.CurX[p] = (int) CompDiv((long) dxa * num, den) + xa0;
                }
            }
            zp.Tags[p] |= TagTouchBoth;
        }

        // ---- deltas ----------------------------------------------------------------------------------
        //
        // The designer's last word: at THIS size, move THIS point by THIS fraction of a pixel. Used
        // where nothing general got it right, which at small sizes is a great deal of the time.

        private void ApplyPointDeltas(int rangeOffset)
        {
            int count = Pop();
            byte touch = TouchMask();
            for (int i = 0; i < count; i++)
            {
                // The pair goes onto the stack as (argument, point), so the POINT comes off first.
                // Reversed -- and it is worth saying, because the specification's own wording reads
                // the other way -- eighty-four of the ninety-five glyphs stop matching Windows.
                int p = Pop(), spec = Pop();
                bool fires = DeltaApplies(spec, rangeOffset, out int amount);
                if (_dumpActive)
                    Console.Error.WriteLine($"      DELTAP pt{p} spec=0x{spec:X2}"
                        + $" ppem={((spec >> 4) & 0x0F) + _gs.DeltaBase + rangeOffset}"
                        + $" (base {_gs.DeltaBase}, shift {_gs.DeltaShift}, range +{rangeOffset}, we are {_ppem})"
                        + $" {(fires ? $"FIRES {amount / 64f:0.0000}px" : "no")}");
                if (!fires) continue;

                Zone z = ZoneOf(_gs.Zp0);
                // itrp_DeltaEngine@140036b98 gates the OTHER direction too, and we had been running those
                // unconditionally. Under ClearType (0x1c0 bit0 set, bit2 clear) it applies a delta only
                // when the projection is on the non-ClearType axis AND the point's flag bit 1 -- TOUCHED
                // IN Y -- is already set:
                //     if (pv.y == 0x4000 && pv.x == 0) { if (globals[0x171]) apply;
                //         else if ((pointFlags[pt] >> 1 & 1) && !(globals[0x1c2] >> 1 & 1)) apply; else skip; }
                //     else skip;
                // which is the paper's 'it creates a dent in the outline' case: a post-IUP delta landing on
                // a point the program never placed. WPF_CT_DELTA_UNTOUCHED=1 runs them again.
                if (!s_deltaOnUntouchedY && !BiLevelPass && ClearTypeInfo && !IsHorizontalProjection
                    && (uint) p < (uint) z.PointCount && (z.Tags[p] & TagTouchY) == 0)
                    continue;
                if (SkipDeltaInClearTypeDirection(z, p, compositeExempt: false)
                    && (!s_yTrace || Skipped("DELTA", p, amount)))
                {
                    // SCALED RATHER THAN DROPPED. Microsoft's own account of why ClearType
                    // discards these is not that they mean nothing but that they are too big:
                    // "previous usage with bi-level rendering was relatively sloppy leading to
                    // extreme exaggeration of delta like instructions". A delta written to flip
                    // one whole pixel is three lamps' worth of movement in a direction that now
                    // has three times the resolution -- so a fraction of it is the reading that
                    // sits between dropping it (what ships) and running it whole (measured much
                    // worse). WPF_CT_DELTA_SCALE is that fraction in thousandths.
                    if (s_deltaScale <= 0) continue;
                    amount = (int) (((long) amount * s_deltaScale) / 1000);
                    if (amount == 0) continue;
                }
                // A DELTA IS A DISTANCE ALONG THE PROJECTION VECTOR, realised by moving along the
                // freedom vector by however much that takes -- the same division by fv.pv that every
                // MIRP and MDRP makes, and both Windows rasterizers make it here too (FreeType:
                // Ins_DELTAP goes through func_move, and Direct_Move divides by F_dot_P). This used
                // to move the raw amount along freedom, which is right only while the two vectors
                // coincide. Tahoma's 'V' at 12ppem is where they do not: its inner diagonals are
                // MIRPed perpendicular to the OUTER ones with freedom on x, and the right-hand
                // projection points LEFT, so fv.pv is -0.952 -- GDI's DELTAP of -1/8 there moves
                // the point +8/64 in x and ours moved it -8/64, sixteen sixty-fourths apart, and
                // the inner vertex it then positions off both diagonals landed 22/64 too high.
                // Where the vectors merely lean (Verdana Italic's stems, fv.pv 0.966) the deltas
                // were a sixty-fourth short: -16/64 asked, -17/64 in GDI.
                MoveDirect(z, p, MulDiv(amount, _gs.FreeX, _dotProduct), MulDiv(amount, _gs.FreeY, _dotProduct), touch);
            }
        }

        /// <summary>Whether a DELTAP or SHPIX moving a point along the CLEARTYPE DIRECTION is to be
        /// dropped, as GDI drops it.
        /// <para>Microsoft, "TrueType and ClearType": "all DELTAPs are skipped, except DELTAPs on
        /// previously touched points in the non-ClearType direction and DELTACs" -- and for SHPIX,
        /// "if such a delta occurs on an untouched point ... it creates a dent in the outline. While
        /// for bi-level this is intended to flip one or more pixels, it distorts the stroke in
        /// ClearType ... Therefore we keep only deltas on touched points in the non-ClearType
        /// direction."</para>
        /// <para>The reason is that the extra x resolution makes a delta far more accurate than the
        /// sloppy pixel-flipping it was written for: "previous usage with bi-level rendering was
        /// relatively sloppy leading to extreme exaggeration of delta like instructions". A face as
        /// heavily delta-hinted as Segoe UI therefore renders quite differently under ClearType, and
        /// running every delta as written is not a small error.</para>
        /// <para>The composite rule differs between the two, and the paper says so in two places:
        /// DELTAPs are "also skipped in composite glyphs if they are in the ClearType direction",
        /// while for SHPIX "for composites, the touched/untouched rule does not apply the same way
        /// ... hence we also keep deltas in composites" -- there a point flagged untouched may have
        /// been touched while its component ran, so the delta moves the whole outline rather than
        /// denting it, which is how diacritics keep clear of their base.</para>
        private static readonly bool s_deltaOnUntouchedY =
            Environment.GetEnvironmentVariable("WPF_CT_DELTA_UNTOUCHED") == "1";

        /// </summary>
        private bool Skipped(string what, int p, int amount)
        {
            Zone z = ZoneOf(_gs.Zp0);
            bool tY = (uint) p < (uint) z.PointCount && (z.Tags[p] & TagTouchY) != 0;
            Console.Error.WriteLine("SKIP-" + what + " pt=" + p + " amt=" + amount
                + " pv=(" + _gs.ProjX + "," + _gs.ProjY + ") fv=(" + _gs.FreeX + "," + _gs.FreeY + ")"
                + " touchedY=" + (tY ? 1 : 0) + " iupY=" + (_iupYDone ? 1 : 0)
                + " inCall=" + _callDepth + " comp=" + (_inComposite ? 1 : 0));
            return true;
        }

        private bool SkipDeltaInClearTypeDirection(Zone z, int point, bool compositeExempt,
                                                   bool forShpix = false)
        {
            // THE SCALER'S OWN TEST, read from itrp_DeltaEngine@+0x36a18. Unlike SHPIX there is no
            // recognised-FDEF gate here: the suppression is on for every delta once ClearType is
            // on and INSTCTRL's native bit is not. What survives it is narrow -- the PROJECTION
            // vector exactly (0, 0x4000), the point ALREADY TOUCHED in y, and IUP[y] not yet run
            // -- or a composite (+0x171). WPF_CT_DELTA_RE=0 falls back to the older reading below,
            // which tested the FREEDOM vector and asked neither of the last two.
            if (s_deltaReRule && !forShpix)
            {
                if (!ClearTypeInfo || NativeClearTypeMode || BiLevelPass || s_keepAllDeltas) return false;
                // THE PRE-PROGRAM IS NOT EXEMPT, and the exemption that used to be here was
                // borrowed from a different rule. `InClearTypeDirection` excludes prep because
                // prep ROUNDS on the whole-pixel grid -- which is measured, GDI's own prep rounds
                // a control value to a whole pixel while a glyph rounds on the sixteenth (see
                // HowGdiRoundsAControlValue). The DELTA ENGINE is a separate test with a separate
                // origin: itrp_DeltaEngine is installed on the transform and asks only about the
                // projection vector, the point's touched-y flag and IUP[y]. It has no notion of
                // where it is being called from.
                // <para>Times New Roman ITALIC is what this costs. Its prep builds the face's
                // ITALIC VECTOR in the twilight zone -- two points, one aligned to the other along
                // the design slant, its x rounded by MDAP[r], the direction read back out with
                // SPVTL and stored in storage[6..7] -- and every stem in every glyph is then moved
                // along that stored vector. Two DELTAPs sit on that point, at ppem 12 and 13, each
                // -1 pixel, on a PURE X projection. Running them turns a rise of 3 pixels in 9
                // into 2 in 9: the face's 16.33-degree slant becomes 12.5, every stem leans wrong
                // above the baseline, and Times Italic scores 107,035 at 12ppem and 116,452 at 13
                // against 3,871 at 11 and 5,976 at 14 -- where the same deltas do not fire.
                // WPF_CT_DELTA_PREP=1 restores the exemption.
                if (s_deltasFreeInPrep && _inPreProgram) return false;
                if (compositeExempt && _inComposite) return false;
                bool axis = s_deltaReMode == 3
                            ? !(_gs.FreeX == 0 && _gs.FreeY == 0x4000)
                            : !(_gs.ProjX == 0 && _gs.ProjY == 0x4000);
                if (axis) return true;
                if (s_deltaReMode == 2) return false;          // axis test only
                bool untouched = (uint) point >= (uint) z.PointCount
                                 || (z.Tags[point] & TagTouchY) == 0;
                if (s_deltaReMode == 4) return untouched;      // touched-y only
                if (s_deltaReMode == 5) return _iupYDone;      // post-IUP[y] only
                return _iupYDone || untouched;
            }
            if (!DeltaInClearTypeDirection || NativeClearTypeMode || s_keepAllDeltas || BiLevelPass) return false;
            if (compositeExempt && _inComposite) return false;
            if ((uint) point >= (uint) z.PointCount) return false;
            // EVERY delta in this direction goes, not just those on points untouched in the other
            // one. The paper's headline is "all DELTAPs are skipped" and its exception is written for
            // INLINE deltas specifically; measured on the text specimen, taking the headline plainly
            // is better -- 1,074,897 against 1,112,253 for the narrower reading.
            // WPF_CT_DELTA=touched restores the exception.
            // INLINE vs POST-IUP. "An inline delta is a delta that occurs before the IUP
            // instruction on a previously touched point. A post-IUP delta occurs after the IUP
            // instruction" -- and the paper keeps inline ones ("inline deltas are sometimes used to
            // adjust the position of horizontal strokes ... hence they are kept"). WPF_CT_DELTA=inline
            // skips only what comes after IUP.
            if (s_keepInlineDeltas && !_iupDone) return false;
            return !s_keepTouchedDeltas || (z.Tags[point] & TagTouchY) == 0;
        }

        private void ApplyControlValueDeltas(int rangeOffset)
        {
            int count = Pop();
            for (int i = 0; i < count; i++)
            {
                int index = Pop(), spec = Pop();
                bool fires = DeltaApplies(spec, rangeOffset, out int amount);
                if (s_traceHint)
                    Console.Error.WriteLine($"      DELTAC cvt[{index}] spec=0x{spec:X2} "
                        + $"ppem={((spec >> 4) & 0x0F) + _gs.DeltaBase + rangeOffset} (we are {_ppem}) "
                        + $"{(fires ? $"FIRES {amount / 64f:0.0000}px" : "no")}"
                        + $" cvt now {((uint)index < _scaledCvt.Length ? _scaledCvt[index] / 64f : 0):0.0000}px");
                if (!fires) continue;
                if ((uint)index < _scaledCvt.Length) _scaledCvt[index] += amount;
            }
        }

        /// <summary>Unpack one delta: the high nibble says which size it is for, the low nibble how
        /// far to move, in steps of a fraction of a pixel the program chose with SDS.</summary>
        private bool DeltaApplies(int spec, int rangeOffset, out int amount)
        {
            amount = 0;
            int ppem = ((spec >> 4) & 0x0F) + _gs.DeltaBase + rangeOffset;
            if (ppem != _ppem) return false;

            int steps = spec & 0x0F;
            steps -= 8;
            if (steps >= 0) steps += 1;           // there is no "no move" step; the scale skips zero
            amount = steps * (64 >> Math.Clamp(_gs.DeltaShift, 0, 6));
            return true;
        }

        // ---- skipping over branches ---------------------------------------------------------------

        private static int SkipToElseOrEnd(byte[] code, int ip)
        {
            int depth = 1;
            while (ip < code.Length)
            {
                byte op = code[ip++];
                if (op == 0x58) depth++;                                   // IF
                else if (op == 0x59) { if (--depth == 0) return ip; }       // EIF
                else if (op == 0x1B && depth == 1) return ip;              // ELSE
                else ip = SkipOperands(code, ip, op);
            }
            return ip;
        }

        private static int SkipToEnd(byte[] code, int ip)
        {
            int depth = 1;
            while (ip < code.Length)
            {
                byte op = code[ip++];
                if (op == 0x58) depth++;
                else if (op == 0x59) { if (--depth == 0) return ip; }
                else ip = SkipOperands(code, ip, op);
            }
            return ip;
        }

        private static int SkipToEndFunction(byte[] code, int ip)
        {
            while (ip < code.Length)
            {
                byte op = code[ip++];
                if (op == 0x2D) return ip;                                 // ENDF
                ip = SkipOperands(code, ip, op);
            }
            return ip;
        }

        /// <summary>Step past the bytes an instruction carries INLINE. Only the push instructions do,
        /// and missing that is how a scan for the matching EIF wanders into the middle of some data
        /// and reads it as code.</summary>
        private static int SkipOperands(byte[] code, int ip, byte op)
        {
            if (op == 0x40) return ip + 1 + (ip < code.Length ? code[ip] : 0);            // NPUSHB
            if (op == 0x41) return ip + 1 + 2 * (ip < code.Length ? code[ip] : 0);        // NPUSHW
            if (op >= 0xB0 && op <= 0xB7) return ip + (op - 0xB0 + 1);                    // PUSHB
            if (op >= 0xB8 && op <= 0xBF) return ip + 2 * (op - 0xB8 + 1);                // PUSHW
            return ip;
        }

        // ---- setting the vectors to an axis or a line -------------------------------------------------

        private void SetProjection(bool xAxis)
        {
            _gs.ProjX = _gs.DualX = xAxis ? 0x4000 : 0;
            _gs.ProjY = _gs.DualY = xAxis ? 0 : 0x4000;
            ResetProjection();
        }

        private void SetFreedom(bool xAxis)
        {
            _gs.FreeX = xAxis ? 0x4000 : 0;
            _gs.FreeY = xAxis ? 0 : 0x4000;
            ResetProjection();
        }

        /// <summary>The direction of the line between two points, or the perpendicular to it.
        /// <para>Which point belongs to which zone is not symmetric and not guessable: the one on
        /// TOP of the stack is read from zp2, the one under it from zp1, and the line runs from the
        /// second to the first. Getting the pair the wrong way round leaves the vector pointing
        /// backwards, which only shows on glyphs with diagonals in them.</para></summary>
        private (int X, int Y) LineVector(bool perpendicular)
        {
            int p1 = Pop(), p2 = Pop();
            Zone z1 = ZoneOf(_gs.Zp1), z2 = ZoneOf(_gs.Zp2);
            if (p1 >= z2.PointCount || p2 >= z1.PointCount) return (0x4000, 0);
            SetVectorLine(p2, p1);

            int dx = z1.CurX[p2] - z2.CurX[p1];
            int dy = z1.CurY[p2] - z2.CurY[p1];
            if (perpendicular) { int t = dx; dx = -dy; dy = t; }
            Normalize(dx, dy, out int nx, out int ny);
            return (nx, ny);
        }
    }
}
