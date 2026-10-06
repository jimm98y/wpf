// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Glyph outlines from a 'CFF ' table: Type 2 charstrings.
//
// The other half of GlyphOutlines. Where 'glyf' stores points, CFF stores a little stack program
// per glyph that draws one, with cubic curves, shared subroutines and its own compression scheme
// for the common cases (a run of alternating horizontal and vertical curves is one operator). Every
// OTF font is this format, and so is most CJK type, so a stack that only read 'glyf' would render
// nothing for a large part of the world's text.
//
// What is implemented is the drawing subset, which is all an outline needs: moves, lines, the seven
// curve operators, the four flex variants, subroutine calls and endchar. Hints are parsed only far
// enough to skip them correctly -- hintmask carries a variable number of trailing bytes that
// depends on how many stems have been declared, so a reader that ignores hints without counting
// them loses its place in the stream and produces garbage.
//

using System;
using System.Buffers.Binary;

namespace MS.Internal.Text.TextInterface.Managed
{
    internal static class CffOutlines
    {
        private const int MaxCallDepth = 10;

        internal static bool TryGetOutline(OpenTypeFontData font, int glyphIndex, IGlyphOutlineSink sink)
        {
            CffTable table = font.Cff;
            if (table == null) return false;

            return table.Draw(glyphIndex, sink);
        }

        /// <summary>
        /// A parsed CFF table: the charstrings, the subroutines they call, and enough of the
        /// dictionaries to find both.
        ///
        /// Built once per font and cached, because the INDEX structures are offset tables that
        /// would otherwise be walked again for every glyph on every page.
        /// </summary>
        internal sealed class CffTable
        {
            private readonly byte[] _data;
            private readonly int _base;

            private Index _charStrings;
            private Index _globalSubrs;
            private Index _localSubrs;

            /// <summary>
            /// CID fonts keep a separate Private DICT, and so a separate set of local subroutines,
            /// per group of glyphs. Null for an ordinary font.
            /// </summary>
            private Index[] _fdLocalSubrs;
            private byte[] _fdSelect;

            private CffTable(byte[] data, int offset)
            {
                _data = data;
                _base = offset;
            }

            internal static CffTable Parse(byte[] data, int offset, int length)
            {
                if (data == null || length <= 4) return null;

                try
                {
                    var table = new CffTable(data, offset);
                    return table.Read() ? table : null;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
                catch (IndexOutOfRangeException)
                {
                    return null;
                }
            }

            private bool Read()
            {
                int headerSize = _data[_base + 2];

                int p = _base + headerSize;

                Index names = Index.Read(_data, ref p);
                Index topDicts = Index.Read(_data, ref p);
                Index strings = Index.Read(_data, ref p);

                _globalSubrs = Index.Read(_data, ref p);

                _ = names;
                _ = strings;

                if (topDicts.Count == 0) return false;

                var top = Dict.Parse(_data, topDicts.Start(0), topDicts.End(0));

                int charStringsOffset = (int)top.Operand(17, 0);
                if (charStringsOffset <= 0) return false;

                int q = _base + charStringsOffset;
                _charStrings = Index.Read(_data, ref q);

                // Private DICT is [size, offset]; the offset is from the start of the table.
                double[] priv = top.Operands(18);

                if (priv != null && priv.Length >= 2)
                {
                    _localSubrs = ReadPrivateSubrs((int)priv[1], (int)priv[0]);
                }

                // ROS marks a CID-keyed font, whose glyphs are grouped by FDSelect into font
                // dictionaries with their own Private DICTs.
                if (top.Has(12, 30)) ReadCidTables(top);

                return _charStrings.Count > 0;
            }

            private Index ReadPrivateSubrs(int offset, int size)
            {
                if (offset <= 0 || size <= 0) return default;

                int start = _base + offset;
                var dict = Dict.Parse(_data, start, start + size);

                int subrs = (int)dict.Operand(19, 0);
                if (subrs <= 0) return default;

                int p = start + subrs;
                return Index.Read(_data, ref p);
            }

            private void ReadCidTables(Dict top)
            {
                int fdArrayOffset = (int)top.Operand2(12, 36, 0);
                int fdSelectOffset = (int)top.Operand2(12, 37, 0);

                if (fdArrayOffset > 0)
                {
                    int p = _base + fdArrayOffset;
                    Index fdArray = Index.Read(_data, ref p);

                    _fdLocalSubrs = new Index[fdArray.Count];

                    for (int i = 0; i < fdArray.Count; i++)
                    {
                        var fd = Dict.Parse(_data, fdArray.Start(i), fdArray.End(i));
                        double[] priv = fd.Operands(18);

                        if (priv != null && priv.Length >= 2)
                        {
                            _fdLocalSubrs[i] = ReadPrivateSubrs((int)priv[1], (int)priv[0]);
                        }
                    }
                }

                if (fdSelectOffset > 0) ReadFdSelect(_base + fdSelectOffset);
            }

            private void ReadFdSelect(int p)
            {
                int glyphs = _charStrings.Count;
                if (glyphs <= 0) return;

                var select = new byte[glyphs];
                int format = _data[p++];

                if (format == 0)
                {
                    for (int i = 0; i < glyphs; i++) select[i] = _data[p + i];
                }
                else if (format == 3)
                {
                    int ranges = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(p));
                    p += 2;

                    int first = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(p));
                    p += 2;

                    for (int i = 0; i < ranges; i++)
                    {
                        byte fd = _data[p++];
                        int next = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(p));
                        p += 2;

                        for (int g = first; g < next && g < glyphs; g++) select[g] = fd;

                        first = next;
                    }
                }
                else
                {
                    return;
                }

                _fdSelect = select;
            }

            internal bool Draw(int glyphIndex, IGlyphOutlineSink sink)
            {
                if (glyphIndex < 0 || glyphIndex >= _charStrings.Count) return false;

                Index local = _localSubrs;

                if (_fdLocalSubrs != null && _fdSelect != null && glyphIndex < _fdSelect.Length)
                {
                    int fd = _fdSelect[glyphIndex];
                    if (fd < _fdLocalSubrs.Length) local = _fdLocalSubrs[fd];
                }

                var interpreter = new Type2Interpreter(_data, _globalSubrs, local, sink);

                interpreter.Run(_charStrings.Start(glyphIndex), _charStrings.End(glyphIndex), 0);
                return interpreter.Close();
            }
        }

        /// <summary>
        /// A CFF INDEX: a count, a table of offsets, then the data they point into.
        ///
        /// Stored as positions into the font rather than copied out. An INDEX is only ever read
        /// through, and copying the charstrings of a CJK font would be several megabytes to no end.
        /// </summary>
        private readonly struct Index
        {
            private readonly byte[] _data;
            private readonly int _offsets;
            private readonly int _offSize;
            private readonly int _dataStart;

            internal readonly int Count;

            private Index(byte[] data, int count, int offsets, int offSize, int dataStart)
            {
                _data = data;
                Count = count;
                _offsets = offsets;
                _offSize = offSize;
                _dataStart = dataStart;
            }

            internal static Index Read(byte[] data, ref int p)
            {
                int count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p));
                p += 2;

                if (count == 0) return default;

                int offSize = data[p++];
                int offsets = p;

                p += (count + 1) * offSize;

                int dataStart = p - 1;                       // offsets are 1-based into the data
                int last = OffsetAt(data, offsets, offSize, count);

                p = dataStart + last;

                return new Index(data, count, offsets, offSize, dataStart);
            }

            private static int OffsetAt(byte[] data, int offsets, int offSize, int i)
            {
                int at = offsets + i * offSize;
                int value = 0;

                for (int b = 0; b < offSize; b++) value = (value << 8) | data[at + b];

                return value;
            }

            internal int Start(int i) => _dataStart + OffsetAt(_data, _offsets, _offSize, i);

            internal int End(int i) => _dataStart + OffsetAt(_data, _offsets, _offSize, i + 1);
        }

        /// <summary>
        /// A CFF DICT: operands then an operator, repeatedly.
        ///
        /// Only queried, never enumerated, so it keeps the raw range and re-scans. A Top DICT is a
        /// few dozen bytes and is read a handful of times per font.
        /// </summary>
        private readonly struct Dict
        {
            private readonly byte[] _data;
            private readonly int _start;
            private readonly int _end;

            private Dict(byte[] data, int start, int end)
            {
                _data = data;
                _start = start;
                _end = end;
            }

            internal static Dict Parse(byte[] data, int start, int end) => new Dict(data, start, end);

            internal bool Has(int escape, int op) => Operands2(escape, op) != null;

            internal double Operand(int op, double fallback)
            {
                double[] values = Operands(op);
                return values != null && values.Length != 0 ? values[values.Length - 1] : fallback;
            }

            internal double Operand2(int escape, int op, double fallback)
            {
                double[] values = Operands2(escape, op);
                return values != null && values.Length != 0 ? values[values.Length - 1] : fallback;
            }

            internal double[] Operands(int op) => Find(op, -1);

            private double[] Operands2(int escape, int op) => Find(op, escape);

            private double[] Find(int wanted, int escape)
            {
                var operands = new double[48];
                int count = 0;
                int p = _start;

                while (p < _end)
                {
                    byte b = _data[p];

                    if (b <= 21)
                    {
                        int op = b;
                        p++;

                        bool escaped = op == 12;
                        if (escaped) op = _data[p++];

                        bool match = escape < 0 ? !escaped && op == wanted : escaped && op == wanted;

                        if (match)
                        {
                            var result = new double[count];
                            Array.Copy(operands, result, count);
                            return result;
                        }

                        count = 0;
                        continue;
                    }

                    double value = ReadOperand(_data, ref p, out bool ok);
                    if (!ok) return null;

                    if (count < operands.Length) operands[count++] = value;
                }

                return null;
            }

            /// <summary>
            /// A DICT operand. The encoding is the charstring one plus a packed-BCD real, which is
            /// where a FontMatrix or a StdVW lives.
            /// </summary>
            private static double ReadOperand(byte[] data, ref int p, out bool ok)
            {
                ok = true;
                byte b = data[p];

                if (b == 28)
                {
                    short v = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(p + 1));
                    p += 3;
                    return v;
                }

                if (b == 29)
                {
                    int v = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(p + 1));
                    p += 5;
                    return v;
                }

                if (b == 30)
                {
                    p++;
                    return ReadReal(data, ref p);
                }

                if (b >= 32 && b <= 246)
                {
                    p++;
                    return b - 139;
                }

                if (b >= 247 && b <= 250)
                {
                    int v = (b - 247) * 256 + data[p + 1] + 108;
                    p += 2;
                    return v;
                }

                if (b >= 251 && b <= 254)
                {
                    int v = -((b - 251) * 256) - data[p + 1] - 108;
                    p += 2;
                    return v;
                }

                ok = false;
                p++;
                return 0;
            }

            /// <summary>Packed BCD: two nibbles per byte, 0xf ends it.</summary>
            private static double ReadReal(byte[] data, ref int p)
            {
                Span<char> text = stackalloc char[64];
                int length = 0;
                bool done = false;

                while (!done && length < text.Length - 1)
                {
                    byte b = data[p++];

                    for (int half = 0; half < 2 && !done; half++)
                    {
                        int nibble = half == 0 ? b >> 4 : b & 0x0F;

                        switch (nibble)
                        {
                            case <= 9: text[length++] = (char)('0' + nibble); break;
                            case 0xA: text[length++] = '.'; break;
                            case 0xB: text[length++] = 'E'; break;
                            case 0xC: text[length++] = 'E'; text[length++] = '-'; break;
                            case 0xE: text[length++] = '-'; break;
                            case 0xF: done = true; break;
                            default: break;                  // 0xD is reserved
                        }
                    }
                }

                return double.TryParse(text.Slice(0, length),
                                       System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture,
                                       out double value)
                    ? value
                    : 0;
            }
        }

        /// <summary>
        /// The Type 2 charstring machine.
        ///
        /// A stack of at most 48 operands and a current point. Everything it draws goes straight to
        /// the sink; nothing is retained but the pen position and whether a figure is open.
        /// </summary>
        private ref struct Type2Interpreter
        {
            private readonly byte[] _data;
            private readonly Index _global;
            private readonly Index _local;
            private readonly IGlyphOutlineSink _sink;

            private readonly double[] _stack;
            private int _count;

            private double _x, _y;
            private bool _open;
            private bool _drew;

            /// <summary>
            /// Declared stems, which hintmask needs: it is followed by one bit per stem, rounded up
            /// to whole bytes, and those bytes are part of the instruction stream.
            /// </summary>
            private int _stems;

            /// <summary>
            /// Whether the leading width operand has been dealt with. The first stack-clearing
            /// operator may carry one extra argument in front, and it is identified only by the
            /// argument count being odd where the operator expects even (or vice versa).
            /// </summary>
            private bool _widthParsed;

            internal Type2Interpreter(byte[] data, Index global, Index local, IGlyphOutlineSink sink)
            {
                _data = data;
                _global = global;
                _local = local;
                _sink = sink;
                _stack = new double[48];
                _count = 0;
                _x = _y = 0;
                _open = false;
                _drew = false;
                _stems = 0;
                _widthParsed = false;
            }

            internal bool Close()
            {
                if (_open)
                {
                    _sink.EndFigure();
                    _open = false;
                }

                return _drew;
            }

            internal void Run(int p, int end, int depth)
            {
                if (depth > MaxCallDepth) return;

                while (p < end)
                {
                    byte b = _data[p];

                    if (b >= 32 || b == 28)
                    {
                        Push(ReadNumber(ref p));
                        continue;
                    }

                    p++;

                    switch (b)
                    {
                        case 1:                              // hstem
                        case 3:                              // vstem
                        case 18:                             // hstemhm
                        case 23:                             // vstemhm
                            CountStems();
                            break;

                        case 19:                             // hintmask
                        case 20:                             // cntrmask
                            CountStems();
                            p += (_stems + 7) / 8;
                            break;

                        case 21:                             // rmoveto
                            TakeWidth(2);
                            Move(Arg(0), Arg(1));
                            break;

                        case 22:                             // hmoveto
                            TakeWidth(1);
                            Move(Arg(0), 0);
                            break;

                        case 4:                              // vmoveto
                            TakeWidth(1);
                            Move(0, Arg(0));
                            break;

                        case 5:                              // rlineto
                            for (int i = 0; i + 1 < _count; i += 2) Line(_stack[i], _stack[i + 1]);
                            _count = 0;
                            break;

                        case 6:                              // hlineto, alternating
                        case 7:                              // vlineto, alternating
                        {
                            bool horizontal = b == 6;

                            for (int i = 0; i < _count; i++)
                            {
                                if (horizontal) Line(_stack[i], 0);
                                else Line(0, _stack[i]);

                                horizontal = !horizontal;
                            }

                            _count = 0;
                            break;
                        }

                        case 8:                              // rrcurveto
                            for (int i = 0; i + 5 < _count; i += 6)
                            {
                                Curve(_stack[i], _stack[i + 1], _stack[i + 2],
                                      _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                            }
                            _count = 0;
                            break;

                        case 24:                             // rcurveline
                        {
                            int i = 0;
                            for (; i + 5 < _count - 2; i += 6)
                            {
                                Curve(_stack[i], _stack[i + 1], _stack[i + 2],
                                      _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                            }
                            if (i + 1 < _count) Line(_stack[i], _stack[i + 1]);
                            _count = 0;
                            break;
                        }

                        case 25:                             // rlinecurve
                        {
                            int i = 0;
                            for (; i + 1 < _count - 6; i += 2) Line(_stack[i], _stack[i + 1]);
                            if (i + 5 < _count)
                            {
                                Curve(_stack[i], _stack[i + 1], _stack[i + 2],
                                      _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                            }
                            _count = 0;
                            break;
                        }

                        case 26:                             // vvcurveto
                        case 27:                             // hhcurveto
                        {
                            int i = 0;
                            double d1 = 0;

                            if ((_count & 1) != 0) { d1 = _stack[0]; i = 1; }

                            for (; i + 3 < _count; i += 4)
                            {
                                if (b == 26)
                                {
                                    Curve(d1, _stack[i], _stack[i + 1], _stack[i + 2], 0, _stack[i + 3]);
                                }
                                else
                                {
                                    Curve(_stack[i], d1, _stack[i + 1], _stack[i + 2], _stack[i + 3], 0);
                                }

                                d1 = 0;
                            }

                            _count = 0;
                            break;
                        }

                        case 30:                             // vhcurveto
                        case 31:                             // hvcurveto
                        {
                            bool horizontal = b == 31;
                            int i = 0;

                            while (i + 3 < _count)
                            {
                                // The very last curve may carry a fifth value: the other axis of
                                // its end point, which is otherwise zero.
                                bool last = i + 8 > _count;
                                double extra = last && i + 4 < _count ? _stack[i + 4] : 0;

                                if (horizontal)
                                {
                                    Curve(_stack[i], 0, _stack[i + 1], _stack[i + 2], extra, _stack[i + 3]);
                                }
                                else
                                {
                                    Curve(0, _stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], extra);
                                }

                                horizontal = !horizontal;
                                i += 4;
                            }

                            _count = 0;
                            break;
                        }

                        case 10:                             // callsubr
                        case 29:                             // callgsubr
                        {
                            if (_count == 0) break;

                            Index index = b == 10 ? _local : _global;
                            int number = (int)_stack[--_count] + Bias(index.Count);

                            if (number >= 0 && number < index.Count)
                            {
                                Run(index.Start(number), index.End(number), depth + 1);
                            }

                            break;
                        }

                        case 11:                             // return
                            return;

                        case 14:                             // endchar
                            TakeWidth(0);
                            if (_open) { _sink.EndFigure(); _open = false; }
                            return;

                        case 12:
                        {
                            byte op = _data[p++];
                            Escape(op);
                            break;
                        }

                        default:
                            _count = 0;
                            break;
                    }
                }
            }

            /// <summary>
            /// The flex operators: two curves that a rasterizer may flatten to a line because the
            /// bump between them is below a threshold. Drawn as the two curves they are, since this
            /// outline is going to paper or to a geometry, not to a 12-pixel screen.
            /// </summary>
            private void Escape(byte op)
            {
                switch (op)
                {
                    case 35:                                 // flex
                        if (_count >= 13)
                        {
                            Curve(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                            Curve(_stack[6], _stack[7], _stack[8], _stack[9], _stack[10], _stack[11]);
                        }
                        break;

                    case 34:                                 // hflex
                        if (_count >= 7)
                        {
                            double y = _stack[1];
                            Curve(_stack[0], 0, _stack[1], _stack[2], _stack[3], 0);
                            Curve(_stack[4], 0, _stack[5], -y, _stack[6], 0);
                        }
                        break;

                    case 36:                                 // hflex1
                        if (_count >= 9)
                        {
                            double dy = _stack[1] + _stack[3] + _stack[5] + _stack[7];
                            Curve(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], 0);
                            Curve(_stack[5], 0, _stack[6], _stack[7], _stack[8], -dy);
                        }
                        break;

                    case 37:                                 // flex1
                        if (_count >= 11)
                        {
                            double dx = _stack[0] + _stack[2] + _stack[4] + _stack[6] + _stack[8];
                            double dy = _stack[1] + _stack[3] + _stack[5] + _stack[7] + _stack[9];

                            Curve(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                            Curve(_stack[6], _stack[7], _stack[8], _stack[9], _stack[10], -dy);

                            _ = dx;
                        }
                        break;

                    default:
                        break;                               // arithmetic and the deprecated set
                }

                _count = 0;
            }

            private void CountStems()
            {
                // An odd count means the first operand is the width, not a stem edge.
                if (!_widthParsed && (_count & 1) != 0) _widthParsed = true;

                _stems += _count / 2;
                _count = 0;
                _widthParsed = true;
            }

            /// <summary>
            /// Drops the leading width operand if there is one.
            ///
            /// Only the FIRST stack-clearing operator can carry it, and only when it has one more
            /// argument than the operator itself takes.
            /// </summary>
            private void TakeWidth(int expected)
            {
                if (_widthParsed) return;

                _widthParsed = true;

                if (_count > expected && _count > 0)
                {
                    for (int i = 1; i < _count; i++) _stack[i - 1] = _stack[i];
                    _count--;
                }
            }

            private double Arg(int i) => i < _count ? _stack[i] : 0;

            private void Push(double value)
            {
                if (_count < _stack.Length) _stack[_count++] = value;
            }

            private void Move(double dx, double dy)
            {
                if (_open) _sink.EndFigure();

                _x += dx;
                _y += dy;

                _sink.BeginFigure(_x, _y);
                _open = true;
                _drew = true;
                _count = 0;
            }

            private void Line(double dx, double dy)
            {
                if (!_open) return;

                _x += dx;
                _y += dy;

                _sink.LineTo(_x, _y);
            }

            private void Curve(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
            {
                if (!_open) return;

                double c1x = _x + dx1;
                double c1y = _y + dy1;
                double c2x = c1x + dx2;
                double c2y = c1y + dy2;

                _x = c2x + dx3;
                _y = c2y + dy3;

                _sink.CubicTo(c1x, c1y, c2x, c2y, _x, _y);
            }

            private double ReadNumber(ref int p)
            {
                byte b = _data[p];

                if (b == 28)
                {
                    short v = BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(p + 1));
                    p += 3;
                    return v;
                }

                if (b <= 246)
                {
                    p++;
                    return b - 139;
                }

                if (b <= 250)
                {
                    int v = (b - 247) * 256 + _data[p + 1] + 108;
                    p += 2;
                    return v;
                }

                if (b <= 254)
                {
                    int v = -((b - 251) * 256) - _data[p + 1] - 108;
                    p += 2;
                    return v;
                }

                // 255: 16.16 fixed point.
                int fixed16 = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(p + 1));
                p += 5;
                return fixed16 / 65536.0;
            }

            /// <summary>Subroutine numbers are stored biased, by an amount that depends on how many there are.</summary>
            private static int Bias(int count)
                => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;
        }
    }
}
