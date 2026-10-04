// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI's geometric-pen widener, pathwide::bWiden @1401cc888 (win32kfull), ported from the binary.
// A wide line is the outline of a pen polygon dragged along the path, filled with WINDING:
//
//   WIDEPENOBJ::bPolygonizePen @1401cc210  the pen: the world width as a device vector pair; a
//                                          circular pen under 6.5 px is one of six Hobby polygons
//                                          (bHobbyize @1401cbd10, tables at 14034b380), a pen
//                                          thinner than a pixel one way a parallelogram (bThicken
//                                          @1401cc418), otherwise the half ellipse flattened from
//                                          two Beziers (bPenFlatten @1401cc030). Only half the
//                                          polygon is kept: P[0] = -P[n-2] and P[n-1] = -P[1], the
//                                          other half is its negation
//   vDetermineDrawVertex @1401cdf50        the pen vertex a line's tangent draws with (a binary
//                                          search for the sign change of the edge cross products)
//   WIDENER::vVecDrawCompute / vVecPerpCompute / vVecSquareCompute @1401cef20 / @1401cefb0 /
//                                          @1401cf140: the offsets, snapped to half pixels
//   LINER::vNextEvent / vNextPoint @1401ce318 / @1401ce460, STYLER::vNextStyleEvent @1401ce7e8: the
//                                          path walked as events (start, join, Bezier join, close,
//                                          finish, ...), Beziers flattened as they come
//   WIDENER::bWiden @1401cc668             the events: a right side and a left side; joins
//                                          (vAddJoin @1401cd098, vAddRoundJoin @1401cd828,
//                                          cptAddRound @1401cc9c8), caps (vAddStartCap @1401cdc00,
//                                          vAddEndCap @1401ccd40, vAddRoundEndCap @1401cd608), the
//                                          left side reversed onto the right
//

using System.Collections.Generic;

namespace System.Drawing.WebGpuBackend.Gdip
{
    /// <summary>A LINEATTRS: the geometric pen.</summary>
    internal struct GdiLineAttrs
    {
        public int Join;          // JOIN_ROUND 0, JOIN_BEVEL 1, JOIN_MITER 2
        public int EndCap;        // ENDCAP_ROUND 0, ENDCAP_SQUARE 1, ENDCAP_BUTT 2
        public float Width;       // elWidth, world units
        public float MiterLimit;  // eMiterLimit
        public float[] Style;     // pstyle (world units), or null
        public float StyleState;  // elStyleState
    }

    internal static class GdiWiden
    {
        /// <summary>pathwide::bWiden: the outline of <paramref name="path"/> stroked with the pen,
        /// to be filled with WINDING. <paramref name="xform"/> is the world-to-device matrix; null
        /// when the widener fails.</summary>
        public static GdiPath Widen(GdiPath path, in GdiXform xform, in GdiLineAttrs la)
        {
            var w = new Widener(path, xform, la);
            return w.Valid ? w.Result() : null;
        }

        struct V
        {
            public int X, Y;
            public V(int x, int y) { X = x; Y = y; }
            public bool IsZero => X == 0 && Y == 0;
            public static V operator -(V a) => new V(-a.X, -a.Y);
        }

        /// <summary>LINEDATA.</summary>
        sealed class LineData
        {
            public int Fl;               // 1 flipped, 2 square, 4 perp, 8 draw, 0x10 normalised
            public int Vertex;           // the draw vertex, an index into the pen's points
            public long DqDraw, DqNext;  // |cross| of the edges into and out of the draw vertex
            public V Line, Tangent, Square, Perp, Draw;
            public float NormX, NormY;
            public LineData Clone() => (LineData)MemberwiseClone();
            public void CopyFrom(LineData o)
            {
                Fl = o.Fl; Vertex = o.Vertex; DqDraw = o.DqDraw; DqNext = o.DqNext;
                Line = o.Line; Tangent = o.Tangent; Square = o.Square; Perp = o.Perp; Draw = o.Draw;
                NormX = o.NormX; NormY = o.NormY;
            }
        }

        /// <summary>A WIDEPATHOBJ: subpaths (flags BEGINSUBPATH 1, ENDSUBPATH 2, CLOSEFIGURE 8) of
        /// device points.</summary>
        sealed class WidePath
        {
            public sealed class Sub { public readonly List<V> P = new List<V>(); public int Flags; }
            public readonly List<Sub> Subs = new List<Sub>();
            public int FigureStart = -1;           // the subpath the current figure began with
            Sub Last => Subs[Subs.Count - 1];

            public void BeginFigure() => Subs.Add(new Sub { Flags = 1 });
            public void EndFigure() => Last.Flags |= 2;
            public void Close() => Last.Flags |= 8;
            public void Add(V p) => Last.P.Add(p);
            public void Add(V p, V v, bool minus) => Last.P.Add(minus ? new V(p.X - v.X, p.Y - v.Y) : new V(p.X + v.X, p.Y + v.Y));

            /// <summary>vReverseConcatenate: the other path's points, last to first, onto this one's
            /// last subpath; the other is emptied.</summary>
            public void ReverseConcatenate(WidePath o)
            {
                for (int s = o.Subs.Count - 1; s >= 0; s--)
                    for (int i = o.Subs[s].P.Count - 1; i >= 0; i--) Last.P.Add(o.Subs[s].P[i]);
                o.Subs.Clear();
            }

            /// <summary>vPrependBeforeFigure: the last subpath put at the head of the figure's first
            /// subpath, one subpath with it.</summary>
            public void PrependBeforeFigure()
            {
                Sub moved = Last;
                Subs.RemoveAt(Subs.Count - 1);
                Sub head = Subs[FigureStart];
                head.P.InsertRange(0, moved.P);
                head.Flags = (head.Flags & ~1) | (moved.Flags & 1);
            }

            /// <summary>vPrependBeforeSubpath: the last subpath put at the head of the one before it.</summary>
            public void PrependBeforeSubpath()
            {
                Sub moved = Last;
                Subs.RemoveAt(Subs.Count - 1);
                Sub head = Last;
                head.P.InsertRange(0, moved.P);
                head.Flags = (head.Flags & ~1) | (moved.Flags & 1);
            }
        }

        sealed class Widener
        {
            // LINER / READER
            readonly GdiPath _path;
            int _fig = -1, _pt;
            bool _figClosed;
            V _this, _startFigure;        // ptfxThis, ptfxStartFigure
            int _lineState;               // 0 in figure, 1 figure begun, 2 figure done, 3 in Bezier, 4 path done
            BezierFlattener _bez;
            readonly int[] _bezBuf = new int[2];
            readonly LineData _ldStart = new LineData(), _ldBuf0 = new LineData(), _ldBuf1 = new LineData();
            readonly LineData _ldEnd = new LineData(), _ldBezStart = new LineData();
            int _we;                      // the event
            V _pt0;                       // ptfx: the event's point
            LineData _in, _out;
            // STYLER
            readonly bool _styled;
            bool _inLine;                 // fl & 4: a line's style lengths are being walked
            readonly float[] _style;      // the style entries, world units
            int _sIdx;
            float _remaining, _styleLen, _done, _lineLen;
            V _lineStart;
            // WIDENER
            int _fl;                      // 8 dash finished, 0x10 draw vectors (round join and round or square cap)
            readonly int _join, _endCap;
            readonly float _halfWidth, _miterSq;
            GdiXform _inverse;
            bool _haveInverse;
            readonly V[] _pen;            // the half pen polygon
            readonly WidePath _left = new WidePath(), _right = new WidePath();
            bool _bad;

            public bool Valid => !_bad;

            public Widener(GdiPath path, in GdiXform xform, in GdiLineAttrs la)
            {
                _path = path;
                // LINER: the first figure's first point.
                if (NextFigure()) { _this = ReadPoint(); _startFigure = _this; _lineState = 1; }
                else _lineState = 4;
                _styled = la.Style != null && la.Style.Length > 0;
                _style = la.Style;
                _endCap = la.EndCap;
                _join = la.Join;
                if (_join == 0 && _endCap < 2) _fl |= 0x10;
                float width = la.Width;
                int lWidth = RoundF(width);
                _halfWidth = width * 0.5f;
                if (_join == 2)
                {
                    float m = la.MiterLimit * width * 0.5f;
                    _miterSq = m * m;
                }
                if (_styled || _endCap == 1 || _join == 2)
                {
                    _haveInverse = xform.Inverse(out _inverse);
                    if (!_haveInverse) { _bad = true; return; }
                }
                _pen = PolygonizePen(xform, lWidth);
                if (_pen == null) { _bad = true; return; }
                Widen();
            }

            /// <summary>The custom float-to-long of the widener's constructor (round half up of |v|).</summary>
            static int RoundF(float v) => GdiXform.FToL(v);

            public GdiPath Result()
            {
                var r = new GdiPath();
                foreach (WidePath.Sub s in _right.Subs)
                {
                    if (s.P.Count == 0) continue;
                    var f = new GdiPath.Figure { Closed = true };
                    foreach (V p in s.P) { f.X.Add(p.X); f.Y.Add(p.Y); f.Bezier.Add(false); }
                    r.Figures.Add(f);
                }
                return r;
            }

            // ---- READER ---------------------------------------------------------------------

            bool NextFigure()
            {
                while (++_fig < _path.Figures.Count)
                {
                    if (_path.Figures[_fig].Count == 0) continue;
                    _pt = 0;
                    _figClosed = _path.Figures[_fig].Closed;
                    return true;
                }
                return false;
            }

            V ReadPoint()
            {
                GdiPath.Figure f = _path.Figures[_fig];
                var p = new V(f.X[_pt], f.Y[_pt]);
                _pt++;
                return p;
            }

            bool MorePoints => _pt < _path.Figures[_fig].Count;
            bool PointIsBezier => _path.Figures[_fig].Bezier[_pt - 1];

            // ---- LINER --------------------------------------------------------------------

            void SetLine(LineData ld)
            {
                ld.Fl = 0;
                ld.Line = new V(_this.X - _pt0.X, _this.Y - _pt0.Y);
                ld.Tangent = ld.Line;
            }

            bool BezNext(out V p)
            {
                bool more;
                _bez.Flatten(_bezBuf, 0, 1, out more);
                p = new V(_bezBuf[0], _bezBuf[1]);
                return more;
            }

            void NextPoint()
            {
                _out = ReferenceEquals(_in, _ldBuf0) ? _ldBuf1 : _ldBuf0;
                switch (_lineState)
                {
                    case 0:
                    case 1:
                        _we = _lineState == 0 ? 3 : 0;
                        if (_lineState == 1) _lineState = 0;
                        if (MorePoints)
                        {
                            _this = ReadPoint();
                            if (PointIsBezier && _pt + 1 < _path.Figures[_fig].Count)
                            {
                                V c1 = _this, p0 = _pt0;
                                V c2 = ReadPoint(), p3 = ReadPoint();
                                _bez = new BezierFlattener(new[] { p0.X, p0.Y, c1.X, c1.Y, c2.X, c2.Y, p3.X, p3.Y }, null, true);
                                bool more = BezNext(out _this);
                                SetLine(_out);
                                if (more)
                                {
                                    _ldBezStart.CopyFrom(_out);
                                    _ldBezStart.Tangent = new V(c1.X - p0.X, c1.Y - p0.Y);
                                    _ldEnd.Fl = 0;
                                    _ldEnd.Line = new V(p3.X - c2.X, p3.Y - c2.Y);
                                    _ldEnd.Tangent = _ldEnd.Line;
                                    _out = _ldBezStart;
                                    _lineState = 3;
                                    return;
                                }
                                _lineState = 0;
                                return;
                            }
                        }
                        else
                        {
                            _lineState = 2;
                            _this = _startFigure;
                            if (!_figClosed) { _we = 1; return; }
                            _we = 3;
                        }
                        SetLine(_out);
                        return;
                    case 2:
                        _we = _figClosed ? 2 : 8;
                        _out = _ldStart;
                        if (NextFigure()) { _this = ReadPoint(); _startFigure = _this; _lineState = 1; }
                        else _lineState = 4;
                        return;
                    case 3:
                        _we = 4;
                        if (!BezNext(out _this))
                        {
                            _lineState = 0;
                            SetLine(_out);
                            _out.Tangent = _ldEnd.Tangent;
                            return;
                        }
                        SetLine(_out);
                        return;
                    default:
                        _we = 9;
                        return;
                }
            }

            void NextEvent()
            {
                _pt0 = _this;
                _in = _out;
                NextPoint();
                int first = _we;
                while (_out.Line.IsZero && (_we == 0 || _we == 3 || _we == 4)) NextPoint();
                if (_out.Tangent.IsZero) _out.Tangent = _out.Line;
                if (first == 0)
                {
                    if (_we != 2 && _we != 1)
                    {
                        _ldStart.CopyFrom(_out);
                        _out = _ldStart;
                        _we = 0;
                        return;
                    }
                    if (_we == 1) NextPoint();
                    _ldStart.Fl = 0;
                    _ldStart.Line = new V(16, 0);
                    _ldStart.Tangent = new V(16, 0);
                    _in = _ldStart;
                    _out = _ldStart;
                    _we = 7;
                }
            }

            // ---- STYLER ------------------------------------------------------------------

            /// <summary>The point the given world length into the current line (STYLER's
            /// normalised line vector, rounded).</summary>
            V AtDone(LineData ld)
            {
                if (_lineLen == 0f) return _lineStart;
                if ((ld.Fl & 0x10) == 0)
                {
                    float k = 1f / _lineLen;
                    ld.NormX = k * (float)ld.Line.X;
                    ld.NormY = k * (float)ld.Line.Y;
                    ld.Fl |= 0x10;
                }
                return new V(_lineStart.X + RoundF(ld.NormX * _done), _lineStart.Y + RoundF(ld.NormY * _done));
            }

            float NextStyle()
            {
                float v = _style[_sIdx];
                if (++_sIdx >= _style.Length) _sIdx = 0;
                return v;
            }

            void BeginLine()
            {
                _done = 0f;
                float len = WorldLength(_out.Line);
                _lineStart = _pt0;
                _remaining = len;
                _lineLen = len;
                _inLine = true;
            }

            /// <summary>STYLER::vNextStyleEvent @1401ce7e8: the LINER's events cut into dashes
            /// (WE_STOPDASH 5, WE_STARTDASH 6).</summary>
            void NextStyleEvent()
            {
                if (!_styled) { NextEvent(); return; }
                if (_inLine)
                {
                    if (_we == 5)
                    {
                        while (true)
                        {
                            if (_styleLen < _remaining) break;
                            _styleLen -= _remaining;
                            NextEvent();
                            if (_we != 3 && _we != 4)
                            {
                                if (_we == 1) NextEvent();
                                _inLine = false;
                                _we = 8;
                                return;
                            }
                            _done = 0f;
                            float len = WorldLength(_out.Line);
                            _lineLen = len;
                            _remaining = len;
                            _lineStart = _pt0;
                        }
                        _remaining -= _styleLen;
                        _done += _styleLen;
                        _pt0 = AtDone(_out);
                        _styleLen = NextStyle();
                        _we = 6;
                        return;
                    }
                    if (_styleLen <= _remaining)
                    {
                        _remaining -= _styleLen;
                        _done += _styleLen;
                        _pt0 = AtDone(_out);
                        _in = _out;
                        _styleLen = NextStyle();
                        _we = 5;
                        return;
                    }
                    _styleLen -= _remaining;
                }
                NextEvent();
                if (_we == 0)
                {
                    _sIdx = 0;
                    _styleLen = NextStyle();
                }
                else if (_we != 3 && _we != 4)
                {
                    _inLine = false;
                    return;
                }
                BeginLine();
            }

            // ---- WIDENER ------------------------------------------------------------------

            void Widen()
            {
                while (true)
                {
                    NextStyleEvent();
                    switch (_we)
                    {
                        case 0:
                            _left.BeginFigure(); _right.BeginFigure();
                            _fl &= ~8;
                            _right.FigureStart = _right.Subs.Count - 1;
                            break;
                        case 1:
                        case 5:
                            AddEndCap();
                            _left.EndFigure();
                            _right.ReverseConcatenate(_left);
                            _right.EndFigure();
                            _right.Close();
                            _fl |= 8;
                            break;
                        case 2:
                            AddJoin(false);
                            _left.EndFigure();
                            if ((_fl & 8) == 0)
                            {
                                _right.EndFigure();
                                _right.Close();
                                _right.BeginFigure();
                                _right.ReverseConcatenate(_left);
                                _right.EndFigure();
                                _right.Close();
                            }
                            else
                            {
                                _right.EndFigure();
                                _right.BeginFigure();
                                _right.ReverseConcatenate(_left);
                                _right.EndFigure();
                                _right.PrependBeforeSubpath();
                                _right.PrependBeforeFigure();
                                _right.Close();
                            }
                            break;
                        case 3: AddJoin(false); break;
                        case 4: AddJoin(true); break;
                        case 6:
                            _left.BeginFigure(); _right.BeginFigure();
                            AddStartCap();
                            break;
                        case 7:
                            if (_endCap == 0)
                            {
                                _right.BeginFigure();
                                AddStartCap();
                                AddEndCap();
                                _right.EndFigure();
                                _right.Close();
                            }
                            break;
                        case 8:
                            _right.BeginFigure();
                            AddStartCap();
                            _right.EndFigure();
                            _right.PrependBeforeFigure();
                            break;
                        case 9:
                            return;
                    }
                }
            }

            static bool TurnLeft(V a, V b)
            {
                if ((b.X ^ a.Y ^ a.X ^ b.Y) < 0) return ((a.X ^ b.Y) >> 31) != 0;
                return (long)a.X * b.Y < (long)b.X * a.Y;
            }

            static int Round8(int v) => (v + (v >> 31) + 4) & ~7;

            void DetermineDrawVertex(LineData ld)
            {
                V vec = ld.Tangent;
                V[] P = _pen;
                long Cross(int i) => (long)(P[i + 1].Y - P[i].Y) * vec.X - (long)(P[i + 1].X - P[i].X) * vec.Y;
                long c = Cross(0);
                ld.DqDraw = c;
                ld.DqNext = -c;
                int sign = c < 0 ? 1 : 0;
                ld.Fl = (ld.Fl & ~1) | sign;
                int lo = 0, hi = P.Length - 2, mid;
                do
                {
                    while (true)
                    {
                        mid = lo + (hi - lo) / 2;
                        long m = Cross(mid);
                        if ((m < 0 ? 1 : 0) == sign) { ld.DqDraw = m; lo = mid; break; }
                        ld.DqNext = m;
                        hi = mid;
                        if (mid == lo + 1) goto done;
                    }
                } while (hi != mid + 1);
            done:
                ld.Vertex = hi;
                ld.DqDraw = Math.Abs(ld.DqDraw);
                ld.DqNext = Math.Abs(ld.DqNext);
            }

            void VecDrawCompute(LineData ld)
            {
                DetermineDrawVertex(ld);
                V p = _pen[ld.Vertex];
                if ((ld.Fl & 1) != 0) p = -p;
                ld.Fl |= 8;
                ld.Draw = new V(Round8(p.X), Round8(p.Y));
            }

            V DrawOf(LineData ld)
            {
                if ((ld.Fl & 8) == 0) VecDrawCompute(ld);
                return ld.Draw;
            }

            void VecPerpCompute(LineData ld)
            {
                if ((ld.Fl & 8) == 0) VecDrawCompute(ld);
                V p = _pen[ld.Vertex];
                long dqDraw = ld.DqDraw;
                int ex, ey;
                if (ld.DqNext < dqDraw) { ex = _pen[ld.Vertex + 1].X - p.X; ey = _pen[ld.Vertex + 1].Y - p.Y; }
                else { ex = p.X - _pen[ld.Vertex - 1].X; ey = p.Y - _pen[ld.Vertex - 1].Y; }
                int aex = Math.Abs(ex), aey = Math.Abs(ey);
                long den = ld.DqNext + dqDraw;
                long numX = aex * dqDraw, numY = aey * dqDraw;
                int qx, qy;
                uint rx = 0, ry = 0;
                uint d32 = (uint)den;
                if (den != 0)
                {
                    if (den < 0xffffffffL)
                    {
                        ulong ud = (ulong)(uint)den;
                        if (numX < 0) { ulong u = (ulong)(-numX); qx = -(int)(u / ud); rx = (uint)u - (uint)(int)(u / ud) * d32; }
                        else { ulong u = (ulong)numX; qx = (int)(u / ud); rx = (uint)u - (uint)qx * d32; }
                        if (numY < 0) { ulong u = (ulong)(-numY); qy = -(int)(u / ud); ry = (uint)u - (uint)(int)(u / ud) * d32; }
                        else { ulong u = (ulong)numY; qy = (int)(u / ud); ry = (uint)u - (uint)qy * d32; }
                    }
                    else
                    {
                        qx = (int)(numX / den);
                        qy = (int)(numY / den);
                    }
                }
                else { qx = (int)numX; qy = (int)numY; }
                if (d32 >> 1 <= rx) qx++;
                if (d32 >> 1 <= ry) qy++;
                int ox = (ex >= 0 ? qx : -qx) + (p.X - (ex >> 1));
                int oy = (ey >= 0 ? qy : -qy) + (p.Y - (ey >> 1));
                if ((ld.Fl & 1) != 0) { ox = -ox; oy = -oy; }
                ld.Fl |= 4;
                ld.Perp = new V(((ox - (ox >> 31)) + 3) & ~7, (oy + (oy >> 31) + 4) & ~7);
            }

            V PerpOf(LineData ld)
            {
                if ((ld.Fl & 4) == 0) VecPerpCompute(ld);
                return ld.Perp;
            }

            /// <summary>STYLER::efWorldLength: a FIX vector's length in world units.</summary>
            float WorldLength(V v)
            {
                _inverse.VectorToLogical(v.X, v.Y, out int x, out int y);
                if (x == 0 && y == 0) return 0f;
                float l = (float)y * (float)y + (float)x * (float)x;
                return Sqrt(l);
            }

            /// <summary>EFLOAT::vSqrt @14018a078: a 24-step digit-by-digit root of the mantissa.</summary>
            internal static float Sqrt(float f)
            {
                uint bits = (uint)BitConverter.SingleToInt32Bits(f);
                uint e = (bits >> 23) & 0xff;
                uint mant = (bits & 0x7fffff) | 0x800000;
                uint odd = e & 1;
                uint ne = (e + odd + 0x7e) >> 1;
                uint w8 = odd != 0 ? mant << 7 : mant << 8;
                uint rem = 0, root = 0;
                for (int i = 0; i < 24; i++)
                {
                    uint r2 = root << 1;
                    uint cur = (rem << 2) | (w8 >> 30);
                    uint trial = (root << 2) + 1;
                    if (trial > cur) { rem = cur; root = r2; }
                    else { rem = cur - trial; root = r2 + 1; }
                    w8 <<= 2;
                }
                uint r = (root & 0x7fffff) | ((ne & 0x1ff) << 23);
                return BitConverter.Int32BitsToSingle((int)r);
            }

            void VecSquareCompute(LineData ld)
            {
                float hw = _halfWidth;
                float l = WorldLength(ld.Tangent);
                float k = l == 0f ? 0f : hw / l;
                GdiXform.FToL((float)ld.Tangent.X * k, out int sx);
                GdiXform.FToL((float)ld.Tangent.Y * k, out int sy);
                ld.Square = new V(sx, sy);
                ld.Fl |= 2;
            }

            V SquareOf(LineData ld)
            {
                if ((ld.Fl & 2) == 0) VecSquareCompute(ld);
                return ld.Square;
            }

            static void AddNice(WidePath path, V pt, V v, bool neg)
            {
                if (((pt.X | pt.Y) & 15) != 0) { path.Add(pt, v, neg); return; }
                if (neg) v = -v;
                if (v.X > 0) v.X--; else if (v.X < 0) v.X++;
                if (v.Y > 0) v.Y--; else if (v.Y < 0) v.Y++;
                path.Add(pt, v, false);
            }

            /// <summary>WIDEPENOBJ::cptAddRound: the pen vertices from one line's draw vertex to the
            /// next's, on the left (going back) or the right (going on).</summary>
            int AddRound(LineData ldIn, LineData ldOut, bool left, bool inPerp, bool outPerp)
            {
                int i = ldIn.Vertex;
                bool flip = (ldIn.Fl & 1) != 0;
                bool outFlip = (ldOut.Fl & 1) != 0;
                if (i == ldOut.Vertex && flip == outFlip) return 0;
                int n = 0, m = _pen.Length;
                if (!left)
                {
                    if (inPerp && ldIn.DqNext < ldIn.DqDraw) AddNice(_left, _pt0, _pen[i], !flip);
                    i--;
                    while (i < ldOut.Vertex || flip != outFlip)
                    {
                        while (1 < i) { AddNice(_left, _pt0, _pen[i], !flip); n++; i--; }
                        flip = !flip;
                        i = m - 1;
                    }
                    while (ldOut.Vertex < i) { AddNice(_left, _pt0, _pen[i], !flip); n++; i--; }
                    if (!outPerp || ldOut.DqNext < ldOut.DqDraw) return n;
                    AddNice(_left, _pt0, _pen[i], !flip);
                }
                else
                {
                    if (inPerp && ldIn.DqDraw <= ldIn.DqNext) AddNice(_right, _pt0, _pen[i], flip);
                    i++;
                    while (ldOut.Vertex < i || flip != outFlip)
                    {
                        while (i < m - 1) { AddNice(_right, _pt0, _pen[i], flip); n++; i++; }
                        flip = !flip;
                        i = 1;
                    }
                    for (; i < ldOut.Vertex; i++) { AddNice(_right, _pt0, _pen[i], flip); n++; }
                    if (!outPerp || ldOut.DqDraw <= ldOut.DqNext) return n;
                    AddNice(_right, _pt0, _pen[i], flip);
                }
                return n;
            }

            /// <summary>WIDEPENOBJ::vAddRoundEndCap: half the pen from the draw vertex round to the
            /// opposite one, on the right.</summary>
            void AddRoundEndCap(LineData ld, bool start, bool drawVectors)
            {
                int i = ld.Vertex, m = _pen.Length;
                bool flip = ((ld.Fl & 1) != 0) ^ start;
                if (!drawVectors && ld.DqDraw <= ld.DqNext) AddNice(_right, _pt0, _pen[i], flip);
                i++;
                while (ld.Vertex < i)
                {
                    for (; i < m - 1; i++) AddNice(_right, _pt0, _pen[i], flip);
                    flip = !flip;
                    i = 1;
                }
                for (; i < ld.Vertex; i++) AddNice(_right, _pt0, _pen[i], flip);
                if (!drawVectors && ld.DqNext < ld.DqDraw) AddNice(_right, _pt0, _pen[i], flip);
            }

            void AddEndCap()
            {
                switch (_endCap)
                {
                    case 0:
                        {
                            V v = (_fl & 0x10) == 0 ? PerpOf(_in) : DrawOf(_in);
                            _right.Add(_pt0, v, false);
                            AddRoundEndCap(_in, false, (_fl & 0x10) != 0);
                            _right.Add(_pt0, v, true);
                            return;
                        }
                    case 1:
                        {
                            V sq = SquareOf(_in), perp = PerpOf(_in);
                            _right.Add(_pt0, new V(perp.X + sq.X, perp.Y + sq.Y), false);
                            _right.Add(_pt0, new V(sq.X - perp.X, sq.Y - perp.Y), false);
                            return;
                        }
                    case 2:
                        {
                            V perp = PerpOf(_in);
                            _right.Add(_pt0, perp, false);
                            _right.Add(_pt0, perp, true);
                            return;
                        }
                }
            }

            void AddStartCap()
            {
                switch (_endCap)
                {
                    case 0:
                        {
                            V v = (_fl & 0x10) == 0 ? PerpOf(_out) : DrawOf(_out);
                            _right.Add(_pt0, v, true);
                            AddRoundEndCap(_out, true, (_fl & 0x10) != 0);
                            _right.Add(_pt0, v, false);
                            return;
                        }
                    case 1:
                        {
                            V sq = -SquareOf(_out), perp = PerpOf(_out);
                            _right.Add(_pt0, new V(sq.X - perp.X, sq.Y - perp.Y), false);
                            _right.Add(_pt0, new V(sq.X + perp.X, sq.Y + perp.Y), false);
                            return;
                        }
                    case 2:
                        {
                            V perp = PerpOf(_out);
                            _right.Add(_pt0, perp, true);
                            _right.Add(_pt0, perp, false);
                            return;
                        }
                }
            }

            void AddJoin(bool bezier)
            {
                if (_join == 0 || bezier) { AddRoundJoin(bezier); return; }
                bool turnLeft = TurnLeft(_in.Tangent, _out.Tangent);
                if (_join == 1)
                {
                    V vIn = PerpOf(_in), vOut = PerpOf(_out);
                    _left.Add(_pt0, vIn, true);
                    _right.Add(_pt0, vIn, false);
                    if (vIn.X == vOut.X && vIn.Y == vOut.Y) return;
                    (turnLeft ? _left : _right).Add(_pt0);
                    _right.Add(_pt0, vOut, false);
                    _left.Add(_pt0, vOut, true);
                    return;
                }
                {
                    V vIn = PerpOf(_in), vOut = PerpOf(_out);
                    _left.Add(_pt0, vIn, true);
                    _right.Add(_pt0, vIn, false);
                    if (vIn.X == vOut.X && vIn.Y == vOut.Y) return;
                    WidePath pivot;
                    if (!ComputeIntersect(vIn, _in.Line, vOut, _out.Line, out V miter) || !MiterInLimit(miter))
                        pivot = turnLeft ? _left : _right;
                    else if (!turnLeft)
                    {
                        _left.Add(_pt0, miter, true);
                        pivot = _right;
                    }
                    else
                    {
                        _right.Add(_pt0, miter, false);
                        pivot = _left;
                    }
                    pivot.Add(_pt0);
                    _left.Add(_pt0, vOut, true);
                    _right.Add(_pt0, vOut, false);
                }
            }

            /// <summary>pathwide::bComputeIntersect @1401cb6d8: where the two offset lines meet.</summary>
            static bool ComputeIntersect(V a, V la, V b, V lb, out V r)
            {
                r = default;
                float den = (float)la.Y * (float)lb.X + (float)la.X * (float)(-lb.Y);
                if (den == 0f) return false;
                float t = ((float)(b.Y - a.Y) * (float)lb.X + (float)(b.X - a.X) * (float)(-lb.Y)) / den;
                if (!GdiXform.FToL(t * (float)la.X, out int fx)) return false;
                if (!GdiXform.FToL(t * (float)la.Y, out int fy)) return false;
                const uint lim = 0x7ffffffd;
                if ((uint)(fx + 0x3fffffff) > lim || (uint)(fy + 0x3fffffff) > lim) return false;
                r = new V(a.X + fx, a.Y + fy);
                return (uint)(r.X + 0x3fffffff) <= lim && (uint)(r.Y + 0x3fffffff) <= lim;
            }

            bool MiterInLimit(V v)
            {
                _inverse.VectorToLogical(v.X, v.Y, out int x, out int y);
                return (float)y * (float)y + (float)x * (float)x <= _miterSq;
            }

            void AddRoundJoin(bool bezier)
            {
                bool turnLeft = TurnLeft(_in.Tangent, _out.Tangent);
                V vIn, vOut;
                bool perp;
                if ((_fl & 0x10) == 0 && !bezier) { vIn = PerpOf(_in); vOut = PerpOf(_out); perp = true; }
                else { vIn = DrawOf(_in); vOut = DrawOf(_out); perp = false; }
                _right.Add(_pt0, vIn, false);
                _left.Add(_pt0, vIn, true);
                if (vIn.X == vOut.X && vIn.Y == vOut.Y) return;
                int n = AddRound(_in, _out, turnLeft, perp, perp);
                if (!turnLeft)
                {
                    _right.Add(_pt0);
                    if ((_fl & 0x10) == 0)
                    {
                        _right.Add(_pt0, vOut, false);
                        if (n != 0) AddRound(_out, _in, true, perp, perp);
                        _right.Add(_pt0, vIn, false);
                        _right.Add(_pt0);
                    }
                }
                else
                {
                    _left.Add(_pt0);
                    if ((_fl & 0x10) == 0)
                    {
                        _left.Add(_pt0, vOut, true);
                        if (n != 0) AddRound(_out, _in, false, perp, perp);
                        _left.Add(_pt0, vIn, true);
                        _left.Add(_pt0);
                    }
                }
                _right.Add(_pt0, vOut, false);
                _left.Add(_pt0, vOut, true);
            }

            // ---- the pen ------------------------------------------------------------------

            static readonly int[][] Hobby =
            {
                new[] { 0, 8, 8, 0, 0, -8, -8, 0 },
                new[] { 8, 16, 16, 0, 8, -16, -8, -16, -16, 0 },
                new[] { 24, 8, 24, -8, 8, -24, -8, -24, -24, -8, -24, 8 },
                new[] { 32, 8, 32, -8, 24, -24, 8, -32, -8, -32, -24, -24, -32, -8, -32, 8 },
                new[] { 40, 8, 40, -8, 32, -24, 24, -32, 8, -40, -8, -40, -24, -32, -32, -24, -40, -8, -40, 8 },
                new[] { 48, 8, 48, -8, 40, -24, 24, -40, 8, -48, -8, -48, -24, -40, -40, -24, -48, -8, -48, 8 },
            };

            static V[] Points(int[] a)
            {
                var r = new V[a.Length / 2];
                for (int i = 0; i < r.Length; i++) r[i] = new V(a[2 * i], a[2 * i + 1]);
                return r;
            }

            static V[] PolygonizePen(in GdiXform xform, int width)
            {
                xform.Vector(width, 0, out int ax, out int ay);
                xform.Vector(0, -width, out int bx, out int by);
                if ((ax == by && ay == -bx) || (ax == -by && ay == bx))
                {
                    int r = Math.Max(Math.Abs(ax), Math.Abs(ay));
                    if (r < 0x68)
                    {
                        int d = ax * ax + ay * ay;
                        if (d < 0x2a40)
                        {
                            int k = d < 0x240 ? 0 : d < 0x640 ? 1 : d < 0xc40 ? 2 : d < 0x1440 ? 3 : d < 0x1e40 ? 4 : 5;
                            return Points(Hobby[k]);
                        }
                    }
                }
                V[] thick = Thicken(new V(ax, ay), new V(bx, by));
                if (thick != null) return thick;
                var A = new V(ax, ay);
                var B = new V(bx, by);
                if (!TurnLeft(A, B)) B = -B;
                A = Halve(A); B = Halve(B);
                const long k32 = 0x729d7775;
                int Shrink(int v) => v - (int)((v * k32) >> 32);
                int iA_x = Shrink(A.X), iA_y = Shrink(A.Y), iB_y = Shrink(B.Y), iB_x = Shrink(B.X);
                var c = new V[7];
                c[0] = A;
                c[1] = new V(iB_x + A.X, iB_y + A.Y);
                c[2] = new V(iA_x + B.X, iA_y + B.Y);
                c[3] = B;
                c[4] = new V(B.X - iA_x, B.Y - iA_y);
                c[5] = new V(iB_x - A.X, iB_y - A.Y);
                c[6] = -A;
                return PenFlatten(c);
            }

            static V Halve(V v) => new V((v.X >= 0 ? v.X + 1 : v.X) >> 1, (v.Y >= 0 ? v.Y + 1 : v.Y) >> 1);

            /// <summary>WIDEPENOBJ::bThicken: a pen thinner than a pixel one way.</summary>
            static V[] Thicken(V a, V b)
            {
                a = Halve(a); b = Halve(b);
                if (((Math.Abs(a.X) | Math.Abs(a.Y) | Math.Abs(b.X) | Math.Abs(b.Y)) & ~0xfff) != 0) return null;
                int la = a.Y * a.Y + a.X * a.X;
                int lb = b.Y * b.Y + b.X * b.X;
                V v;
                int len;
                if (lb < la)
                {
                    long c = (long)(b.Y * a.X - a.Y * b.X);
                    c *= c;
                    if (c - (long)la * 16 != 0 && (long)la * 16 <= c) return null;
                    v = a; len = la;
                }
                else
                {
                    long c = (long)(a.Y * b.X - b.Y * a.X);
                    c *= c;
                    if (c - (long)lb * 16 != 0 && (long)lb * 16 <= c) return null;
                    v = b; len = lb;
                }
                if (len <= 0x3f) v = new V(8, 0);
                int ax = Math.Abs(v.X), ay = Math.Abs(v.Y);
                V u;
                if (v.X < ay)
                {
                    if (-v.Y < ax) u = -v.X < ay ? new V(8, 0) : new V(0, 8);
                    else u = new V(-8, 0);
                }
                else u = new V(0, -8);
                return new[] { v, u, -v, -u };
            }

            /// <summary>WIDEPENOBJ::bPenFlatten: the half ellipse's two Beziers flattened, the first
            /// point the negation of the next to last.</summary>
            static V[] PenFlatten(V[] c)
            {
                var pts = new List<V> { default, c[0] };
                var buf = new int[2];
                for (int b = 0; b < 2; b++)
                {
                    V p0 = c[3 * b], p1 = c[3 * b + 1], p2 = c[3 * b + 2], p3 = c[3 * b + 3];
                    var flat = new BezierFlattener(new[] { p0.X, p0.Y, p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y }, null, true);
                    bool more;
                    do
                    {
                        flat.Flatten(buf, 0, 1, out more);
                        pts.Add(new V(buf[0], buf[1]));
                    } while (more);
                }
                pts[0] = -pts[pts.Count - 2];
                return pts.ToArray();
            }
        }
    }
}
