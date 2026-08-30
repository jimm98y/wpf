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
            var calls = new CallFrame[128];

            while (true)
            {
                if (ip >= code.Length)
                {
                    // Falling off the end of a function body is how a call returns.
                    if (callDepth == 0) return true;
                    CallFrame frame = calls[--callDepth];
                    if (--frame.Repeats > 0)
                    {
                        calls[callDepth++] = frame;
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
                            if ((uint)i < _storage.Length) _storage[i] = v;
                            break;
                        }
                    case 0x43:                                                          // RS
                        {
                            int i = Pop();
                            Push((uint)i < _storage.Length ? _storage[i] : 0);
                            break;
                        }
                    case 0x44:                                                          // WCVTP
                        {
                            int v = Pop(), i = Pop();
                            if ((uint)i < _scaledCvt.Length) _scaledCvt[i] = v;
                            break;
                        }
                    case 0x70:                                                          // WCVTF
                        {
                            int v = Pop(), i = Pop();
                            if ((uint)i < _scaledCvt.Length) _scaledCvt[i] = MulFix(v, _scale);
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
                        SetProjection(op == 0x01);
                        SetFreedom(op == 0x01);
                        break;
                    case 0x02: case 0x03: SetProjection(op == 0x03); break;             // SPVTCA[a]
                    case 0x04: case 0x05: SetFreedom(op == 0x05); break;                // SFVTCA[a]

                    case 0x06: case 0x07:                                               // SPVTL[a]
                        {
                            (int vx, int vy) = LineVector(op == 0x07);
                            _gs.ProjX = _gs.DualX = vx;
                            _gs.ProjY = _gs.DualY = vy;
                            ResetProjection();
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
                            ResetProjection();
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
                    case 0x18: _gs.Round = RoundMode.ToGrid; break;                     // RTG
                    case 0x19: _gs.Round = RoundMode.ToHalfGrid; break;                 // RTHG
                    case 0x1A: _gs.MinimumDistance = Pop(); break;                      // SMD
                    case 0x1D: _gs.ControlValueCutIn = Pop(); break;                    // SCVTCI
                    case 0x1E: _gs.SingleWidthCutIn = Pop(); break;                     // SSWCI
                    case 0x1F: _gs.SingleWidthValue = MulFix(Pop(), _scale); break;      // SSW
                    case 0x3D: _gs.Round = RoundMode.ToDoubleGrid; break;               // RTDG
                    case 0x4D: _gs.AutoFlip = true; break;                              // FLIPON
                    case 0x4E: _gs.AutoFlip = false; break;                             // FLIPOFF
                    case 0x5E: _gs.DeltaBase = Pop(); break;                            // SDB
                    case 0x5F: _gs.DeltaShift = Pop(); break;                           // SDS
                    case 0x7A: _gs.Round = RoundMode.Off; break;                        // ROFF
                    case 0x7C: _gs.Round = RoundMode.UpToGrid; break;                   // RUTG
                    case 0x7D: _gs.Round = RoundMode.DownToGrid; break;                 // RDTG
                    case 0x76: _gs.Round = RoundMode.Super; SetSuperRound(Pop(), 64); break;      // SROUND
                    case 0x77: _gs.Round = RoundMode.Super45; SetSuperRound(Pop(), 46); break;    // S45ROUND
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
                                    int here = Project(z.CurX[p], z.CurY[p]);
                                    MovePoint(z, p, RoundDistance(here, position: true) - here);
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

                            int value = (uint)cvt < _scaledCvt.Length ? _scaledCvt[cvt] : 0;
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
                                int miapCutIn = InClearTypeDirection && !s_cutInFull && !BiLevelPass
                                    ? _gs.ControlValueCutIn / ClearTypeGrid
                                    : _gs.ControlValueCutIn;
                                if (Math.Abs(value - here) > miapCutIn) value = here;
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
                                    && Math.Abs(distance - org) >= _gs.ControlValueCutIn / ClearTypeGrid)
                                    distance = org;
                            }

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

                    case 0x30: InterpolateUntouched(false); _iupDone = true; break;      // IUP[y]
                    // IUP[x] belongs, and it was worth checking: under ClearType x is fitted only
                    // lightly, so a rasterizer might reasonably leave every point the program did
                    // not explicitly move where the scaling put it. Skipping it costs 759,520 ->
                    // 1,203,544, and the digits -- which nothing else here disturbs -- go 20,168 ->
                    // 44,940. The untouched points do get dragged along.
                    case 0x31: InterpolateUntouched(true); _iupDone = true; break;       // IUP[x]

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
                                if (!s_runShpix && SkipDeltaInClearTypeDirection(z, sp, compositeExempt: true)) continue;
                                // AND IN THE NON-CLEARTYPE DIRECTION, ONLY ON TOUCHED POINTS. The
                                // paper's sentence quoted below ends "we keep only deltas on touched
                                // points in the non-ClearType direction", and we were applying the
                                // touched test only in the ClearType one. Arial's 'W' at 12ppem is
                                // what that costs: a vertical SHPIX of -199/64 -- more than three
                                // pixels -- lands its bottom vertex BELOW the baseline, where GDI's
                                // W stops on it and the unhinted outline never goes below it either.
                                if (s_shpixNeedsTouch && !_inPreProgram && !IsHorizontalFreedom
                                    && (uint) sp < (uint) z.PointCount
                                    && (z.Tags[sp] & TagTouchY) == 0) continue;
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
                            Push(op == 0x49
                                 ? MeasureCurrent(_gs.Zp0, a, _gs.Zp1, b)
                                 : MeasureOriginalExact(_gs.Zp0, a, _gs.Zp1, b));
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
                    case >= 0x68 and <= 0x6B: Push(RoundDistance(Pop())); break;              // ROUND[ab]
                    case >= 0x6C and <= 0x6F: Push(Pop()); break;                             // NROUND[ab]

                    case 0x88:                                                               // GETINFO
                        {
                            int selector = Pop();
                            int result = 0;
                            // Version 35: the classic interpreter, which is the one GDI is. Saying
                            // 40 would make a ClearType-era face suppress its own horizontal hints,
                            // and GDI plainly does not -- its stems land on single columns.
                            if ((selector & 1) != 0) result |= 35;
                            // Rendering in greyscale, which GDI reports for ANTIALIASED_QUALITY.
                            // Also tried the other way, since GDI's greyscale is a supersample of a
                            // black-and-white rasterization and might have been expected to hint as
                            // one: saying no turns twenty-three disagreements with Windows into a
                            // hundred and five. It reports greyscale.
                            if (!ClearTypeInfo && (selector & 32) != 0) result |= 1 << 12;
                            if (ClearTypeInfo)
                            {
                                // WHAT GDI ANSWERS WHEN IT IS ACTUALLY DRAWING CLEARTYPE, which is
                                // not what it answers through GetGlyphOutline. That API renders
                                // greyscale and hints as a greyscale rasterizer, so a test of this
                                // bit against GGO output cannot see a face's ClearType branch even
                                // if it has one -- which is how the previous attempt concluded
                                // there was nothing here. Stage C is the only place it shows.
                                if ((selector & 64) != 0) result |= 1 << 13;    // ClearType enabled
                                if ((selector & 128) != 0) result |= 1 << 14;   // compatible widths
                                if ((selector & 256) != 0) result |= 1 << 15;   // horizontal stripes
                                // Bit 18, ClearType SYMMETRIC RENDERING, "can impact the rendering
                                // of horizontal features" -- FreeType answers yes whenever it hints
                                // for an antialiased target. Answering it changes nothing measurable
                                // for Segoe UI (743,631 against 743,730, inside the noise), so it is
                                // left unanswered rather than guessed at. WPF_CT_SYMINFO=1 answers it.
                                if (s_symmetricInfo && (selector & 2048) != 0) result |= 1 << 18;
                            }
                            // NOT ClearType, and it was tried: saying so makes Segoe UI hint its
                            // stems to exactly one pixel where GDI's own geometry is a pixel and a
                            // half, and the page comes out too thin. GDI's stems measure the same
                            // width in both of its modes -- what changes between them is how a
                            // partly covered pixel is SHADED, not where the outline goes.
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
                            if ((uint)id < _functions.Length) _functions[id] = new Function(code, body);
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
                            if (--frame.Repeats > 0)
                            {
                                calls[callDepth++] = frame;
                                code = frame.Code;
                                ip = frame.Start;
                                break;
                            }
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
                                                               code, ip, 1);
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
                                                               code, ip, count);
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
                                                               _instructionDefs[op].Start, code, ip, 1);
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

        private static readonly bool s_traceHint =
            Environment.GetEnvironmentVariable("WPF_HINT_TRACE") == "1";

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
        internal static bool s_dumpGlyph;

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

        private void DumpStep(byte op, int at)
        {
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

            int original = MeasureOriginal(_gs.Zp1, p, _gs.Zp0, _gs.Rp0);

            // The single width: a face may declare one measurement that every stem of that size
            // should collapse to, and anything within the cut-in of it becomes it.
            int distance = original;
            if (_gs.SingleWidthCutIn > 0
                && Math.Abs(distance - _gs.SingleWidthValue) < _gs.SingleWidthCutIn)
                distance = distance >= 0 ? _gs.SingleWidthValue : -_gs.SingleWidthValue;

            if (round) distance = RoundDistance(distance);

            if (keepMinimum)
            {
                if (original >= 0) { if (distance < _gs.MinimumDistance) distance = _gs.MinimumDistance; }
                else { if (distance > -_gs.MinimumDistance) distance = -_gs.MinimumDistance; }
            }

            int current = MeasureCurrent(_gs.Zp1, p, _gs.Zp0, _gs.Rp0);
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
        private static readonly bool s_symmetricInfo =
            Environment.GetEnvironmentVariable("WPF_CT_SYMINFO") == "1";

        /// <summary>WPF_MD_SPEC=0 restores the old MD operand pairing.</summary>
        private static readonly bool s_mdOldOrder =
            Environment.GetEnvironmentVariable("WPF_MD_SPEC") == "0";

        /// <summary>WPF_CT_SHPIXTOUCH=0 lets a vertical SHPIX move an untouched point.</summary>
        private static readonly bool s_shpixNeedsTouch =
            Environment.GetEnvironmentVariable("WPF_CT_SHPIXTOUCH") != "0";

        /// <summary>WPF_CT_SHPIX=run executes SHPIX in the ClearType direction instead of refusing
        /// it. Measured worse -- see the note at the SHPIX site.</summary>
        private static readonly bool s_runShpix =
            Environment.GetEnvironmentVariable("WPF_CT_SHPIX") == "run";

        /// <summary>WPF_CT_CUTIN_DIV: what the control-value cut-in is divided by in the ClearType
        /// direction. 16 is the paper's sixteenth.</summary>
        private static readonly int s_cutInDivisor =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_CT_CUTIN_DIV"), out int cd) && cd > 0
                ? cd : ClearTypeGrid;

        private static readonly bool s_keepAllDeltas =
            Environment.GetEnvironmentVariable("WPF_CT_DELTA") == "all";

        /// <summary>Do NOT halve the minimum distance in the ClearType direction.</summary>
        internal static readonly bool s_fullMinDistance =
            Environment.GetEnvironmentVariable("WPF_CT_MINDIST") == "full";

        private static readonly bool s_keepTouchedDeltas =
            Environment.GetEnvironmentVariable("WPF_CT_DELTA") == "touched";

        private static readonly bool s_cutInFull =
            Environment.GetEnvironmentVariable("WPF_CT_CUTIN_FULL") == "1";

        private void MoveIndirectRelative(byte op)
        {
            bool setRp0 = (op & 0x10) != 0;
            bool round = (op & 0x04) != 0;
            bool keepMinimum = (op & 0x08) != 0;

            int cvt = Pop(), p = Pop();
            Zone z = ZoneOf(_gs.Zp1);
            int value = (uint)cvt < _scaledCvt.Length ? _scaledCvt[cvt] : 0;

            // EVERY PIXEL DISTANCE belongs to the stretched space, not just the control value. The
            // cut-ins and the minimum distance are measured against distances that are now three
            // times bigger, so leaving them at their old size makes the cut-in fire on almost every
            // stem and the program fall back to the outline: measured, tripling only the control
            // value takes Consolas from 6,835 to 11,911 against stage D's reference while helping
            // Segoe UI, which is the shape of a threshold misfiring rather than a fitting choice.
            int stretch = XSpace3x && IsHorizontalProjection ? 3 : 1;
            value *= stretch;

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
            int cutIn = (s_cutInFull || BiLevelPass || !InClearTypeDirection
                             ? _gs.ControlValueCutIn
                             : _gs.ControlValueCutIn / s_cutInDivisor) * stretch;
            int minimum = (InClearTypeDirection && !s_fullMinDistance && !BiLevelPass ? _gs.MinimumDistance / 2
                                                : _gs.MinimumDistance) * stretch;

            if (_gs.SingleWidthCutIn > 0
                && Math.Abs(value - _gs.SingleWidthValue * stretch) < _gs.SingleWidthCutIn * stretch)
                value = value >= 0 ? _gs.SingleWidthValue * stretch : -_gs.SingleWidthValue * stretch;

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

            int original = MeasureOriginal(_gs.Zp1, p, _gs.Zp0, _gs.Rp0);
            int current = MeasureCurrent(_gs.Zp1, p, _gs.Zp0, _gs.Rp0);

            if (_dumpActive)
                Console.Error.WriteLine($"      MIRP cvt[{cvt}]={value / 64f:0.0000}px"
                    + $" outline={original / 64f:0.0000}px round={round}"
                    + $" cutIn={_gs.ControlValueCutIn / 64f:0.0000}px"
                    + $" zp0={_gs.Zp0} zp1={_gs.Zp1} rp0={_gs.Rp0}"
                    + $" minDist={_gs.MinimumDistance / 64f:0.0000} keepMin={keepMinimum}"
                    + $" op=0x{op:X2}({Convert.ToString(op & 0x1F, 2).PadLeft(5, '0')}) roundState={_gs.Round}"
                    + $" instrCtrl={_gs.InstructControl}"
                    + $" axis={(IsHorizontalProjection ? "x" : "y")}");

            // A control value pointing the other way from the outline is the wrong one to use; with
            // auto-flip on, take its size and the outline's direction.
            if (_gs.AutoFlip && (original ^ value) < 0) value = -value;

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
            int distance = value;
            if (_gs.Zp0 == _gs.Zp1 && Math.Abs(value - original) >= cutIn)
                distance = original;
            if (round)
                distance = RoundDistance(distance);

            if (keepMinimum)
            {
                int floor = minimum;
                if (original >= 0) { if (distance < floor) distance = floor; }
                else { if (distance > -floor) distance = -floor; }
            }

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
                if (p == refPoint && _gs.Zp2 == refZone) continue;   // it is already where it is
                MoveDirect(z, p, dx, dy, touch);
            }
            _gs.Loop = 1;
        }

        private void ShiftContour(bool useRp1)
        {
            int contour = Pop();
            if (!ReferenceShift(useRp1, out int dx, out int dy, out int refZone, out int refPoint)) return;

            Zone z = ZoneOf(_gs.Zp2);
            int first = contour == 0 ? 0 : _glyphZone.Contours[Math.Min(contour - 1, _contourCount - 1)] + 1;
            int last = _gs.Zp2 == 0 ? z.PointCount - 1
                                    : _glyphZone.Contours[Math.Min(contour, _contourCount - 1)];
            if (_gs.Zp2 == 0) first = 0;

            for (int p = first; p <= last && p < z.PointCount; p++)
                if (p != refPoint || _gs.Zp2 != refZone)
                    MoveDirect(z, p, dx, dy, 0);
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

            int point = 0;
            for (int contour = 0; contour < _contourCount; contour++)
            {
                int endPoint = Math.Min(z.Contours[contour], _realPoints - 1);
                int firstPoint = point;
                if (endPoint < firstPoint) continue;

                while (point <= endPoint && (z.Tags[point] & mask) == 0) point++;
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
        private static void Carry(int[] cur, int[] org, int[] orus, int from, int to, int ref1, int ref2)
        {
            if (from > to) return;

            int orus1 = orus[ref1], orus2 = orus[ref2];
            if (orus1 > orus2)
            {
                (orus1, orus2) = (orus2, orus1);
                (ref1, ref2) = (ref2, ref1);
            }

            int org1 = org[ref1], org2 = org[ref2];
            int delta1 = cur[ref1] - org1, delta2 = cur[ref2] - org2;

            if (orus1 == orus2)
            {
                // Nothing to interpolate along: each point goes with whichever reference it was on
                // the near side of.
                for (int i = from; i <= to; i++)
                    cur[i] = org[i] + (org[i] <= org1 ? delta1 : delta2);
                return;
            }

            int scale = 0;
            bool haveScale = false;
            for (int i = from; i <= to; i++)
            {
                int x = org[i];
                if (x <= org1) { cur[i] = x + delta1; continue; }
                if (x >= org2) { cur[i] = x + delta2; continue; }

                if (!haveScale)
                {
                    haveScale = true;
                    scale = DivFix((org2 + delta2) - (org1 + delta1), orus2 - orus1);
                }
                cur[i] = org1 + delta1 + MulFix(orus[i] - orus1, scale);
            }
        }


        /// <summary>ISECT: put a point where two lines cross. Used to build reference positions in
        /// the twilight zone out of directions the program has worked out for itself.</summary>
        private void Intersect()
        {
            int b1 = Pop(), b0 = Pop(), a1 = Pop(), a0 = Pop(), p = Pop();
            Zone zp = ZoneOf(_gs.Zp2), za = ZoneOf(_gs.Zp1), zb = ZoneOf(_gs.Zp0);
            if (p >= zp.PointCount || a0 >= za.PointCount || a1 >= za.PointCount
                || b0 >= zb.PointCount || b1 >= zb.PointCount) return;

            int dax = za.CurX[a1] - za.CurX[a0], day = za.CurY[a1] - za.CurY[a0];
            int dbx = zb.CurX[b1] - zb.CurX[b0], dby = zb.CurY[b1] - zb.CurY[b0];
            int dx = zb.CurX[b0] - za.CurX[a0], dy = zb.CurY[b0] - za.CurY[a0];

            long cross = (long)dax * dby - (long)day * dbx;
            if (cross == 0)
            {
                // Parallel: the specification says put it midway between the two line starts.
                zp.CurX[p] = (za.CurX[a0] + zb.CurX[b0]) / 2;
                zp.CurY[p] = (za.CurY[a0] + zb.CurY[b0]) / 2;
            }
            else
            {
                long t = ((long)dx * dby - (long)dy * dbx) * 0x4000 / cross;
                zp.CurX[p] = za.CurX[a0] + (int)(t * dax / 0x4000);
                zp.CurY[p] = za.CurY[a0] + (int)(t * day / 0x4000);
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
                if (!DeltaApplies(spec, rangeOffset, out int amount)) continue;

                Zone z = ZoneOf(_gs.Zp0);
                if (SkipDeltaInClearTypeDirection(z, p, compositeExempt: false)) continue;
                MoveDirect(z, p, MulFix(amount, _gs.FreeX << 2), MulFix(amount, _gs.FreeY << 2), touch);
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
        /// </summary>
        private bool SkipDeltaInClearTypeDirection(Zone z, int point, bool compositeExempt)
        {
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
                if (!DeltaApplies(spec, rangeOffset, out int amount)) continue;
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

            int dx = z1.CurX[p2] - z2.CurX[p1];
            int dy = z1.CurY[p2] - z2.CurY[p1];
            if (perpendicular) { int t = dx; dx = -dy; dy = t; }
            Normalize(dx, dy, out int nx, out int ny);
            return (nx, ny);
        }
    }
}
