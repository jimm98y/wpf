// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s path iterator (DpPathTypeIterator / DpPathIterator), read out of gdiplus.dll:
//
//   DpPathTypeIterator ctor @180087008   index 0, subpath [0, -1], type run [0, -1], marker [0, -1];
//                                        ValidatePathTypes counts the subpaths and curves
//   NextSubpath @180036290, NextPathType @180035f80, NextMarker @1800db2d0  -- transcribed below,
//                                        including how a run of start points is folded into the
//                                        subpath that follows it
//   EnumerateWithinSubpath @180035e30, Enumerate @180035d88, CopyData @1800db090
//

using System.Collections.Generic;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GpPathIterator
    {
        readonly PointF[] _pts;
        readonly byte[] _t;
        readonly int _count;
        public readonly int SubpathCount;
        public readonly bool HasCurve;
        public readonly bool Valid;
        int _index, _subStart, _subEnd = -1, _typeStart, _typeEnd = -1, _markerStart, _markerEnd = -1;

        public GpPathIterator (GpPath path)
        {
            _pts = path?.PointArray () ?? new PointF [0];
            _t = path?.TypeArray () ?? new byte [0];
            _count = _pts.Length;
            Valid = _count > 0 && Validate (_t, _count, out SubpathCount, out HasCurve);
            if (_count == 0) Valid = true;
        }

        public int Count => Valid ? _count : 0;

        /// <summary>DpPathIterator::SetData's validation pass.</summary>
        static bool Validate (byte[] t, int n, out int subpaths, out bool curve)
        {
            subpaths = 0;
            curve = false;
            int i = 0;
            if (n == 1) { subpaths = 1; return true; }
            while (true) {
                subpaths++;
                int rem = n - i - 1;
                if (rem == 0) return true;
                byte b = t [i + 1];
                if ((t [i] & 7) == 3) { if ((b & 7) != 3) return false; }
                else if ((b & 7) == 0) return false;
                int p = i + 1;
                while (true) {
                    byte v = t [p];
                    if ((v & 7) == 1) { p++; rem--; }
                    else {
                        if ((v & 7) != 3) return false;
                        curve = true;
                        if (rem < 3) return false;
                        if ((t [p] & 7) != 3 || (t [p + 1] & 7) != 3 || (t [p + 2] & 7) != 3) return false;
                        p += 3;
                        rem -= 3;
                    }
                    if (rem == 0) return true;
                    if ((t [p] & 7) == 0) { i = p; break; }
                }
            }
        }

        public void Rewind ()
        {
            _index = 0; _subStart = 0; _subEnd = -1; _typeStart = 0; _typeEnd = -1; _markerStart = 0; _markerEnd = -1;
        }

        public int NextSubpath (out int start, out int end, out bool closed)
        {
            start = end = 0; closed = false;
            if (!Valid || _count == 0) return 0;
            int n = _count;
            if (!(_subEnd < n - 1)) return 0;
            int i;
            if (_subEnd < 1) { i = 1; _subStart = 0; }
            else { _subStart = _subEnd + 1; _subEnd = _subStart; i = _subStart + 1; }
            bool seen = false;
            while (true) {
                if (i >= n) break;
                int k = 0;
                bool stop = false;
                do {
                    if ((_t [i] & 7) != 0) break;
                    k++;
                    if (seen) { stop = true; break; }
                    _subStart = i; _subEnd = i;
                    i++;
                } while (i < n);
                if (k > 0 && seen) { _subEnd = i - 1; goto done; }
                if (stop) { _subEnd = i - 1; goto done; }
                k = 0;
                if (i >= n) break;
                do {
                    if ((_t [i] & 7) == 0) break;
                    i++; k++;
                } while (i < n);
                if (k > 0) seen = true;
            }
            _subEnd = n - 1;
        done:
            start = _subStart;
            end = _subEnd;
            int cnt = _subEnd - _subStart + 1;
            closed = cnt >= 2 && (_t [_subEnd] & 0x80) != 0;
            _typeStart = _subStart;
            _typeEnd = -1;
            _index = _subStart;
            return cnt;
        }

        public int NextPathType (out byte type, out int start, out int end)
        {
            type = 0; start = end = 0;
            if (!Valid || _count == 0) return 0;
            if (!(_typeEnd < _subEnd)) return 0;
            int limit = _subEnd + 1;
            int i = _typeEnd;
            if (i < 1) { i = _subStart; _typeEnd = i; }
            _typeStart = i;
            i++;
            int k;
            while (true) {
                if (i >= limit) goto output;
                while (i < limit && (_t [i] & 7) == 0) { _typeStart = i; _typeEnd = i; i++; }
                if (i >= limit) goto output;
                byte b = _t [i];
                k = 0;
                while (i < limit && (b & 7) == (_t [i] & 7)) { i++; k++; }
                if (k >= 1) { _typeEnd = _typeStart + k; type = (byte) (b & 7); break; }
            }
        output:
            start = _typeStart;
            end = _typeEnd;
            return _typeEnd - _typeStart + 1;
        }

        public int NextMarker (out int start, out int end)
        {
            start = end = 0;
            if (!Valid || _count == 0) return 0;
            int n = _count;
            if (!(_markerEnd < n - 1)) return 0;
            int i;
            if (_markerEnd < 1) { i = 1; _markerStart = 0; }
            else { _markerStart = _markerEnd + 1; _markerEnd = _markerStart; i = _markerEnd + 1; }
            while (i < n && (_t [i] & 0x20) == 0) i++;
            _markerEnd = i < n ? i : n - 1;
            start = _markerStart;
            end = _markerEnd;
            _typeEnd = _markerStart; _subEnd = _markerStart; _typeStart = _markerStart; _index = _markerStart; _subStart = _markerStart;
            return _markerEnd - _markerStart + 1;
        }

        /// <summary>The points of [start, end] with their types, as a new path.</summary>
        public GpPath Slice (int start, int count)
        {
            var p = new PointF [count];
            var t = new byte [count];
            Array.Copy (_pts, start, p, 0, count);
            Array.Copy (_t, start, t, 0, count);
            return new GpPath (p, t, Drawing2D.FillMode.Alternate);
        }

        int EnumerateWithinSubpath (PointF[] pts, byte[] types, int at, int room)
        {
            if (!Valid || _count == 0 || room <= 0) return 0;
            int got;
            if (_index == 0) NextSubpath (out _, out _, out _);
            if (_subEnd < _index) got = NextSubpath (out _, out _, out _);
            else got = _subEnd - _subStart + 1;
            if (got == 0) return 0;
            int n = _subEnd - _index + 1;
            if (room < n) n = room;
            if (n > 0) {
                Array.Copy (_pts, _index, pts, at, n);
                Array.Copy (_t, _index, types, at, n);
                _index += n;
            }
            return n;
        }

        public int Enumerate (PointF[] pts, byte[] types)
        {
            if (!Valid || _count == 0) return 0;
            int total = 0, room = pts.Length;
            while (true) {
                int n = EnumerateWithinSubpath (pts, types, total, room);
                if (n < 1) break;
                room -= n;
                total += n;
                if (room < 1) break;
            }
            return total;
        }

        public int CopyData (PointF[] pts, byte[] types, int start, int end)
        {
            if (!Valid || _count == 0 || start < 0 || _count <= end || end < start) return 0;
            int n = end - start + 1;
            Array.Copy (_pts, start, pts, 0, n);
            Array.Copy (_t, start, types, 0, n);
            _index += n;
            return n;
        }
    }
}
