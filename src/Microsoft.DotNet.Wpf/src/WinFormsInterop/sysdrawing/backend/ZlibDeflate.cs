// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// zlib's deflate, ported line for line (deflate.c and trees.c of zlib 1.3.1; the WIC copy GDI+'s
// PNG encoder links -- WindowsCodecs.dll's deflate / deflateInit2_ / _tr_flush_block -- is the
// same algorithm). It exists because a deflate stream is not unique: GDI+ embeds the PNG it
// encodes in EMF+ image objects, and only zlib's own match finder, lazy evaluation and tree
// builder produce zlib's bytes. System.IO.Compression's native zlib-ng does not.
//
// The port keeps zlib's state layout where it changes the output: ct_data's two unions (Freq/Code
// and Dad/Len share storage, and scan_tree's guard lands in Len), the window and hash chains, the
// symbol buffer. What only affects speed or memory (pending-buffer overlap, high-water zeroing of
// a window that is zero already, data-type detection) is left out. Level 0 (stored) is not ported:
// nothing here asks for it.
//

using System.IO;

namespace System.Drawing.WebGpuBackend
{
    internal sealed class ZlibDeflate
    {
        public const int NoFlush = 0, Finish = 4;
        public const int DefaultStrategy = 0, Filtered = 1, HuffmanOnly = 2, Rle = 3, Fixed = 4;

        const int MinMatch = 3, MaxMatch = 258, MinLookahead = MaxMatch + MinMatch + 1, TooFar = 4096;
        const int LengthCodes = 29, Literals = 256, LCodes = Literals + 1 + LengthCodes, DCodes = 30, BlCodes = 19;
        const int HeapSize = 2 * LCodes + 1, MaxBits = 15, MaxBlBits = 7, EndBlock = 256;
        const int Rep3_6 = 16, Repz3_10 = 17, Repz11_138 = 18, BufSize = 16;

        enum BlockState { NeedMore, BlockDone, FinishStarted, FinishDone }

        // configuration_table: good_length, max_lazy, nice_length, max_chain, func (0 stored, 1 fast, 2 slow).
        static readonly int[,] s_config =
        {
            { 0, 0, 0, 0, 0 }, { 4, 4, 8, 4, 1 }, { 4, 5, 16, 8, 1 }, { 4, 6, 32, 32, 1 },
            { 4, 4, 16, 16, 2 }, { 8, 16, 32, 32, 2 }, { 8, 16, 128, 128, 2 }, { 8, 32, 128, 256, 2 },
            { 32, 128, 258, 1024, 2 }, { 32, 258, 258, 4096, 2 },
        };

        static readonly int[] s_extraLbits = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        static readonly int[] s_extraDbits = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
        static readonly int[] s_extraBlbits = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 3, 7 };
        static readonly byte[] s_blOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        // tr_static_init's tables.
        static readonly ushort[] s_staticLCode = new ushort[LCodes + 2], s_staticLLen = new ushort[LCodes + 2];
        static readonly ushort[] s_staticDCode = new ushort[DCodes], s_staticDLen = new ushort[DCodes];
        static readonly byte[] s_distCode = new byte[512], s_lengthCode = new byte[MaxMatch - MinMatch + 1];
        static readonly int[] s_baseLength = new int[LengthCodes], s_baseDist = new int[DCodes];

        static ZlibDeflate()
        {
            int length = 0, code;
            for (code = 0; code < LengthCodes - 1; code++)
            {
                s_baseLength[code] = length;
                for (int n = 0; n < (1 << s_extraLbits[code]); n++) s_lengthCode[length++] = (byte)code;
            }
            s_lengthCode[length - 1] = (byte)code;
            int dist = 0;
            for (code = 0; code < 16; code++)
            {
                s_baseDist[code] = dist;
                for (int n = 0; n < (1 << s_extraDbits[code]); n++) s_distCode[dist++] = (byte)code;
            }
            dist >>= 7;
            for (; code < DCodes; code++)
            {
                s_baseDist[code] = dist << 7;
                for (int n = 0; n < (1 << (s_extraDbits[code] - 7)); n++) s_distCode[256 + dist++] = (byte)code;
            }
            var blCount = new ushort[MaxBits + 1];
            int i = 0;
            while (i <= 143) { s_staticLLen[i++] = 8; blCount[8]++; }
            while (i <= 255) { s_staticLLen[i++] = 9; blCount[9]++; }
            while (i <= 279) { s_staticLLen[i++] = 7; blCount[7]++; }
            while (i <= 287) { s_staticLLen[i++] = 8; blCount[8]++; }
            GenCodes(s_staticLCode, s_staticLLen, LCodes + 1, blCount);
            for (i = 0; i < DCodes; i++) { s_staticDLen[i] = 5; s_staticDCode[i] = (ushort)BiReverse(i, 5); }
        }

        /// <summary>A tree of ct_data: <c>Fc</c> is Freq/Code, <c>Dl</c> is Dad/Len (the two unions).</summary>
        sealed class Tree
        {
            public readonly ushort[] Fc, Dl;
            public Tree(int n) { Fc = new ushort[n]; Dl = new ushort[n]; }
        }

        sealed class Desc
        {
            public Tree Dyn;
            public int MaxCode;
            public ushort[] StaticLen;      // null for the bit-length tree
            public int[] Extra;
            public int ExtraBase, Elems, MaxLength;
        }

        // ---- deflate_state --------------------------------------------------------------------

        readonly MemoryStream _out = new MemoryStream();
        readonly int _wBits, _wSize, _wMask, _hashBits, _hashSize, _hashMask, _hashShift, _litBufsize, _symEnd;
        readonly int _level, _strategy;
        readonly bool _crcHash;
        readonly int _maxLazy, _goodMatch, _niceMatch, _maxChain, _func;
        readonly byte[] _window;
        readonly long _windowSize;
        readonly ushort[] _prev, _head;
        readonly byte[] _symBuf;
        int _symNext;
        int _insH, _matchLength, _prevMatch, _matchAvailable, _strstart, _matchStart, _lookahead, _prevLength, _insert;
        long _blockStart;
        uint _adler = 1;
        bool _headerDone, _finished;

        readonly Tree _dynL = new Tree(HeapSize), _dynD = new Tree(2 * DCodes + 1), _bl = new Tree(2 * BlCodes + 1);
        readonly Desc _lDesc, _dDesc, _blDesc;
        readonly ushort[] _blCount = new ushort[MaxBits + 1];
        readonly int[] _heap = new int[2 * LCodes + 1];
        int _heapLen, _heapMax;
        readonly byte[] _depth = new byte[2 * LCodes + 1];
        long _optLen, _staticLen;
        int _biBuf, _biValid;

        // the input of the current call (strm->next_in / avail_in)
        byte[] _in;
        int _inPos, _inAvail;

        /// <summary>deflateInit2(level, Z_DEFLATED, windowBits, memLevel, strategy), zlib wrapper.</summary>
        /// <summary>deflateInit2(level, Z_DEFLATED, windowBits, memLevel, strategy), zlib wrapper.
        /// <paramref name="crcHash"/> is Chromium's string hash, which the WIC copy GDI+ links uses
        /// on every CPU with a CRC32C instruction (arm_cpu_enable_crc32, x86_cpu_enable_simd):
        /// a CRC32C of the four bytes at the string -- three above level 5 -- instead of zlib's
        /// rolling three-byte hash, with at least 15 hash bits (deflateInit2_ @1800a29d0,
        /// insert_string in deflate_slow @1800a31d0, fill_window @1800a3d00 of WindowsCodecs.dll).</summary>
        public ZlibDeflate(int level, int windowBits, int memLevel, int strategy, bool crcHash)
        {
            if (level < 1 || level > 9) throw new ArgumentOutOfRangeException(nameof(level));
            if (windowBits < 8 || windowBits > 15 || memLevel < 1 || memLevel > 9 || strategy < 0 || strategy > Fixed)
                throw new ArgumentOutOfRangeException(nameof(windowBits));
            if (windowBits == 8) windowBits = 9;    // until 256-byte window bug fixed
            _wBits = windowBits; _wSize = 1 << _wBits; _wMask = _wSize - 1;
            _crcHash = crcHash;
            _hashBits = memLevel + 7;
            if (crcHash && _hashBits < 15) _hashBits = 15;
            _hashSize = 1 << _hashBits; _hashMask = _hashSize - 1;
            _hashShift = (_hashBits + MinMatch - 1) / MinMatch;
            _window = new byte[_wSize * 2 + 16];   // window_padding: the hash reads 4 bytes
            _prev = new ushort[_wSize];
            _head = new ushort[_hashSize];
            _litBufsize = 1 << (memLevel + 6);
            _symBuf = new byte[_litBufsize * 3];
            _symEnd = (_litBufsize - 1) * 3;
            _level = level; _strategy = strategy;

            _lDesc = new Desc { Dyn = _dynL, StaticLen = s_staticLLen, Extra = s_extraLbits, ExtraBase = Literals + 1, Elems = LCodes, MaxLength = MaxBits };
            _dDesc = new Desc { Dyn = _dynD, StaticLen = s_staticDLen, Extra = s_extraDbits, ExtraBase = 0, Elems = DCodes, MaxLength = MaxBits };
            _blDesc = new Desc { Dyn = _bl, StaticLen = null, Extra = s_extraBlbits, ExtraBase = 0, Elems = BlCodes, MaxLength = MaxBlBits };
            InitBlock();

            // lm_init
            _windowSize = 2L * _wSize;
            _maxLazy = s_config[level, 1]; _goodMatch = s_config[level, 0];
            _niceMatch = s_config[level, 2]; _maxChain = s_config[level, 3]; _func = s_config[level, 4];
            _matchLength = _prevLength = MinMatch - 1;
        }

        /// <summary>Everything written so far: header, blocks and, after <see cref="Finish"/>, the Adler-32.</summary>
        public byte[] ToArray() => _out.ToArray();

        public static byte[] Compress(byte[] data, int level, int windowBits, int memLevel, int strategy, bool crcHash)
        {
            var z = new ZlibDeflate(level, windowBits, memLevel, strategy, crcHash);
            z.Deflate(data, 0, data.Length, Finish);
            return z.ToArray();
        }

        /// <summary>deflate(strm, flush) with unbounded output: the input is consumed whole.</summary>
        public void Deflate(byte[] input, int offset, int count, int flush)
        {
            if (_finished) throw new InvalidOperationException("The stream is finished.");
            if (flush != NoFlush && flush != Finish) throw new ArgumentOutOfRangeException(nameof(flush));
            _in = input; _inPos = offset; _inAvail = count;
            if (!_headerDone)
            {
                int header = (8 + ((_wBits - 8) << 4)) << 8;
                int levelFlags = _strategy >= HuffmanOnly || _level < 2 ? 0 : _level < 6 ? 1 : _level == 6 ? 2 : 3;
                header |= levelFlags << 6;
                header += 31 - (header % 31);
                _out.WriteByte((byte)(header >> 8)); _out.WriteByte((byte)header);
                _headerDone = true;
            }
            // With only Z_NO_FLUSH and Z_FINISH and room for all output, the block loop ends in
            // need_more or finish_done; block_done (a sync/full flush) cannot happen.
            if (_inAvail != 0 || _lookahead != 0 || flush != NoFlush)
            {
                if (_strategy == HuffmanOnly) DeflateHuff(flush);
                else if (_strategy == Rle) DeflateRle(flush);
                else if (_func == 1) DeflateFast(flush);
                else DeflateSlow(flush);
            }
            _in = null;
            if (flush != Finish) return;
            _out.WriteByte((byte)(_adler >> 24)); _out.WriteByte((byte)(_adler >> 16));
            _out.WriteByte((byte)(_adler >> 8)); _out.WriteByte((byte)_adler);
            _finished = true;
        }

        // ---- input and window -----------------------------------------------------------------

        int ReadBuf(int at, int size)
        {
            int len = Math.Min(_inAvail, size);
            if (len == 0) return 0;
            Buffer.BlockCopy(_in, _inPos, _window, at, len);
            _adler = Adler32(_adler, _in, _inPos, len);
            _inPos += len; _inAvail -= len;
            return len;
        }

        static uint Adler32(uint adler, byte[] buf, int off, int len)
        {
            uint a = adler & 0xffff, b = adler >> 16;
            for (int i = 0; i < len; i++)
            {
                a = (a + buf[off + i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        int MaxDist => _wSize - MinLookahead;

        void SlideHash()
        {
            for (int n = 0; n < _hashSize; n++) { int m = _head[n]; _head[n] = (ushort)(m >= _wSize ? m - _wSize : 0); }
            for (int n = 0; n < _wSize; n++) { int m = _prev[n]; _prev[n] = (ushort)(m >= _wSize ? m - _wSize : 0); }
        }

        void FillWindow()
        {
            int wsize = _wSize;
            do
            {
                int more = (int)(_windowSize - _lookahead - _strstart);
                if (_strstart >= wsize + MaxDist)
                {
                    Buffer.BlockCopy(_window, wsize, _window, 0, wsize - more);
                    _matchStart -= wsize;
                    _strstart -= wsize;
                    _blockStart -= wsize;
                    if (_insert > _strstart) _insert = _strstart;
                    SlideHash();
                    more += wsize;
                }
                if (_inAvail == 0) break;
                int n = ReadBuf(_strstart + _lookahead, more);
                _lookahead += n;
                if (_crcHash)
                {
                    // The Chromium hash needs four bytes.
                    if (_lookahead + _insert > MinMatch)
                    {
                        int str = _strstart - _insert;
                        do
                        {
                            if (_insert == 0) break;
                            InsertString(str);
                            str++;
                            _insert--;
                        } while (_lookahead + _insert > MinMatch);
                    }
                }
                else if (_lookahead + _insert >= MinMatch)
                {
                    int str = _strstart - _insert;
                    _insH = _window[str];
                    _insH = ((_insH << _hashShift) ^ _window[str + 1]) & _hashMask;
                    while (_insert != 0)
                    {
                        _insH = ((_insH << _hashShift) ^ _window[str + MinMatch - 1]) & _hashMask;
                        _prev[str & _wMask] = _head[_insH];
                        _head[_insH] = (ushort)str;
                        str++;
                        _insert--;
                        if (_lookahead + _insert < MinMatch) break;
                    }
                }
            } while (_lookahead < MinLookahead && _inAvail != 0);
        }

        /// <summary>INSERT_STRING: hashes the three bytes at <paramref name="str"/> and returns the chain head.</summary>
        int InsertString(int str)
        {
            if (_crcHash)
            {
                uint v = (uint)(_window[str] | _window[str + 1] << 8 | _window[str + 2] << 16 | _window[str + 3] << 24);
                if (_level > 5) v &= 0xffffff;
                int h = (int)(Crc32c(v) & (uint)_hashMask);
                int old = _head[h];
                _head[h] = (ushort)str;
                _prev[str & _wMask] = (ushort)old;
                return old;
            }
            _insH = ((_insH << _hashShift) ^ _window[str + MinMatch - 1]) & _hashMask;
            int head = _head[_insH];
            _prev[str & _wMask] = (ushort)head;
            _head[_insH] = (ushort)str;
            return head;
        }

        int LongestMatch(int curMatch)
        {
            int chainLength = _maxChain;
            int scan = _strstart;
            int bestLen = _prevLength;
            int niceMatch = _niceMatch;
            int limit = _strstart > MaxDist ? _strstart - MaxDist : 0;
            int strend = _strstart + MaxMatch;
            byte[] w = _window;
            byte scanEnd1 = w[scan + bestLen - 1];
            byte scanEnd = w[scan + bestLen];
            if (_prevLength >= _goodMatch) chainLength >>= 2;
            if (niceMatch > _lookahead) niceMatch = _lookahead;
            do
            {
                int match = curMatch;
                if (w[match + bestLen] != scanEnd || w[match + bestLen - 1] != scanEnd1
                    || w[match] != w[scan] || w[++match] != w[scan + 1])
                    continue;
                int s = scan + 2;
                match++;
                // do {} while (*++scan == *++match ... eight times ... && scan < strend);
                while (true)
                {
                    bool eq = true;
                    for (int k = 0; k < 8; k++)
                    {
                        if (w[++s] != w[++match]) { eq = false; break; }
                    }
                    if (!eq || s >= strend) break;
                }
                int len = MaxMatch - (strend - s);
                if (len > bestLen)
                {
                    _matchStart = curMatch;
                    bestLen = len;
                    if (len >= niceMatch) break;
                    scanEnd1 = w[scan + bestLen - 1];
                    scanEnd = w[scan + bestLen];
                }
            } while ((curMatch = _prev[curMatch & _wMask]) > limit && --chainLength != 0);
            return bestLen <= _lookahead ? bestLen : _lookahead;
        }

        // ---- the block loops ------------------------------------------------------------------

        bool TallyLit(byte c)
        {
            _symBuf[_symNext++] = 0; _symBuf[_symNext++] = 0; _symBuf[_symNext++] = c;
            _dynL.Fc[c]++;
            return _symNext == _symEnd;
        }

        bool TallyDist(int distance, int length)
        {
            _symBuf[_symNext++] = (byte)distance; _symBuf[_symNext++] = (byte)(distance >> 8); _symBuf[_symNext++] = (byte)length;
            distance--;
            _dynL.Fc[s_lengthCode[length] + Literals + 1]++;
            _dynD.Fc[DCode(distance)]++;
            return _symNext == _symEnd;
        }

        static readonly uint[] s_crc32c = BuildCrc32c();

        static uint[] BuildCrc32c()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0x82F63B78 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        /// <summary>CRC32CW with a zero accumulator: the four bytes of <paramref name="v"/>, low first,
        /// no pre- or post-inversion.</summary>
        static uint Crc32c(uint v)
        {
            uint c = 0;
            for (int i = 0; i < 4; i++) { c = s_crc32c[(c ^ v) & 0xff] ^ (c >> 8); v >>= 8; }
            return c;
        }

        static int DCode(int dist) => dist < 256 ? s_distCode[dist] : s_distCode[256 + (dist >> 7)];

        void FlushBlockOnly(bool last)
        {
            if (_blockStart >= 0) TrFlushBlock((int)_blockStart, _strstart - _blockStart, last);
            else TrFlushBlock(-1, _strstart - _blockStart, last);
            _blockStart = _strstart;
        }

        BlockState DeflateFast(int flush)
        {
            for (;;)
            {
                if (_lookahead < MinLookahead)
                {
                    FillWindow();
                    if (_lookahead < MinLookahead && flush == NoFlush) return BlockState.NeedMore;
                    if (_lookahead == 0) break;
                }
                int hashHead = 0;
                if (_lookahead >= MinMatch) hashHead = InsertString(_strstart);
                if (hashHead != 0 && _strstart - hashHead <= MaxDist)
                    _matchLength = LongestMatch(hashHead);
                bool bflush;
                if (_matchLength >= MinMatch)
                {
                    bflush = TallyDist(_strstart - _matchStart, _matchLength - MinMatch);
                    _lookahead -= _matchLength;
                    if (_matchLength <= _maxLazy && _lookahead >= MinMatch)
                    {
                        _matchLength--;
                        do { _strstart++; InsertString(_strstart); } while (--_matchLength != 0);
                        _strstart++;
                    }
                    else
                    {
                        _strstart += _matchLength;
                        _matchLength = 0;
                        _insH = _window[_strstart];
                        _insH = ((_insH << _hashShift) ^ _window[_strstart + 1]) & _hashMask;
                    }
                }
                else
                {
                    bflush = TallyLit(_window[_strstart]);
                    _lookahead--;
                    _strstart++;
                }
                if (bflush) FlushBlockOnly(false);
            }
            _insert = _strstart < MinMatch - 1 ? _strstart : MinMatch - 1;
            if (flush == Finish) { FlushBlockOnly(true); return BlockState.FinishDone; }
            if (_symNext != 0) FlushBlockOnly(false);
            return BlockState.BlockDone;
        }

        BlockState DeflateSlow(int flush)
        {
            for (;;)
            {
                if (_lookahead < MinLookahead)
                {
                    FillWindow();
                    if (_lookahead < MinLookahead && flush == NoFlush) return BlockState.NeedMore;
                    if (_lookahead == 0) break;
                }
                int hashHead = 0;
                if (_lookahead >= MinMatch) hashHead = InsertString(_strstart);
                _prevLength = _matchLength; _prevMatch = _matchStart;
                _matchLength = MinMatch - 1;
                if (hashHead != 0 && _prevLength < _maxLazy && _strstart - hashHead <= MaxDist)
                {
                    _matchLength = LongestMatch(hashHead);
                    if (_matchLength <= 5 && (_strategy == Filtered
                        || (_matchLength == MinMatch && _strstart - _matchStart > TooFar)))
                        _matchLength = MinMatch - 1;
                }
                if (_prevLength >= MinMatch && _matchLength <= _prevLength)
                {
                    int maxInsert = _strstart + _lookahead - MinMatch;
                    bool bflush = TallyDist(_strstart - 1 - _prevMatch, _prevLength - MinMatch);
                    _lookahead -= _prevLength - 1;
                    _prevLength -= 2;
                    do
                    {
                        if (++_strstart <= maxInsert) InsertString(_strstart);
                    } while (--_prevLength != 0);
                    _matchAvailable = 0;
                    _matchLength = MinMatch - 1;
                    _strstart++;
                    if (bflush) FlushBlockOnly(false);
                }
                else if (_matchAvailable != 0)
                {
                    if (TallyLit(_window[_strstart - 1])) FlushBlockOnly(false);
                    _strstart++;
                    _lookahead--;
                }
                else
                {
                    _matchAvailable = 1;
                    _strstart++;
                    _lookahead--;
                }
            }
            if (_matchAvailable != 0)
            {
                TallyLit(_window[_strstart - 1]);
                _matchAvailable = 0;
            }
            _insert = _strstart < MinMatch - 1 ? _strstart : MinMatch - 1;
            if (flush == Finish) { FlushBlockOnly(true); return BlockState.FinishDone; }
            if (_symNext != 0) FlushBlockOnly(false);
            return BlockState.BlockDone;
        }

        BlockState DeflateRle(int flush)
        {
            for (;;)
            {
                if (_lookahead <= MaxMatch)
                {
                    FillWindow();
                    if (_lookahead <= MaxMatch && flush == NoFlush) return BlockState.NeedMore;
                    if (_lookahead == 0) break;
                }
                _matchLength = 0;
                if (_lookahead >= MinMatch && _strstart > 0)
                {
                    int scan = _strstart - 1;
                    byte prev = _window[scan];
                    if (prev == _window[++scan] && prev == _window[++scan] && prev == _window[++scan])
                    {
                        int strend = _strstart + MaxMatch;
                        while (true)
                        {
                            bool eq = true;
                            for (int k = 0; k < 8; k++) if (prev != _window[++scan]) { eq = false; break; }
                            if (!eq || scan >= strend) break;
                        }
                        _matchLength = MaxMatch - (strend - scan);
                        if (_matchLength > _lookahead) _matchLength = _lookahead;
                    }
                }
                bool bflush;
                if (_matchLength >= MinMatch)
                {
                    bflush = TallyDist(1, _matchLength - MinMatch);
                    _lookahead -= _matchLength;
                    _strstart += _matchLength;
                    _matchLength = 0;
                }
                else
                {
                    bflush = TallyLit(_window[_strstart]);
                    _lookahead--;
                    _strstart++;
                }
                if (bflush) FlushBlockOnly(false);
            }
            _insert = 0;
            if (flush == Finish) { FlushBlockOnly(true); return BlockState.FinishDone; }
            if (_symNext != 0) FlushBlockOnly(false);
            return BlockState.BlockDone;
        }

        BlockState DeflateHuff(int flush)
        {
            for (;;)
            {
                if (_lookahead == 0)
                {
                    FillWindow();
                    if (_lookahead == 0)
                    {
                        if (flush == NoFlush) return BlockState.NeedMore;
                        break;
                    }
                }
                _matchLength = 0;
                bool bflush = TallyLit(_window[_strstart]);
                _lookahead--;
                _strstart++;
                if (bflush) FlushBlockOnly(false);
            }
            _insert = 0;
            if (flush == Finish) { FlushBlockOnly(true); return BlockState.FinishDone; }
            if (_symNext != 0) FlushBlockOnly(false);
            return BlockState.BlockDone;
        }

        // ---- trees.c --------------------------------------------------------------------------

        void InitBlock()
        {
            for (int n = 0; n < LCodes; n++) _dynL.Fc[n] = 0;
            for (int n = 0; n < DCodes; n++) _dynD.Fc[n] = 0;
            for (int n = 0; n < BlCodes; n++) _bl.Fc[n] = 0;
            _dynL.Fc[EndBlock] = 1;
            _optLen = _staticLen = 0;
            _symNext = 0;
        }

        bool Smaller(Tree t, int n, int m) => t.Fc[n] < t.Fc[m] || (t.Fc[n] == t.Fc[m] && _depth[n] <= _depth[m]);

        void PqDownHeap(Tree t, int k)
        {
            int v = _heap[k];
            int j = k << 1;
            while (j <= _heapLen)
            {
                if (j < _heapLen && Smaller(t, _heap[j + 1], _heap[j])) j++;
                if (Smaller(t, v, _heap[j])) break;
                _heap[k] = _heap[j]; k = j;
                j <<= 1;
            }
            _heap[k] = v;
        }

        void GenBitlen(Desc desc)
        {
            Tree tree = desc.Dyn;
            int maxCode = desc.MaxCode;
            ushort[] stree = desc.StaticLen;
            int[] extra = desc.Extra;
            int xbase = desc.ExtraBase, maxLength = desc.MaxLength;
            int h, n, m, bits, overflow = 0;
            for (bits = 0; bits <= MaxBits; bits++) _blCount[bits] = 0;
            tree.Dl[_heap[_heapMax]] = 0;
            for (h = _heapMax + 1; h < HeapSize; h++)
            {
                n = _heap[h];
                bits = tree.Dl[tree.Dl[n]] + 1;
                if (bits > maxLength) { bits = maxLength; overflow++; }
                tree.Dl[n] = (ushort)bits;
                if (n > maxCode) continue;
                _blCount[bits]++;
                int xbits = 0;
                if (n >= xbase) xbits = extra[n - xbase];
                int f = tree.Fc[n];
                _optLen += (long)f * (bits + xbits);
                if (stree != null) _staticLen += (long)f * (stree[n] + xbits);
            }
            if (overflow == 0) return;
            do
            {
                bits = maxLength - 1;
                while (_blCount[bits] == 0) bits--;
                _blCount[bits]--;
                _blCount[bits + 1] += 2;
                _blCount[maxLength]--;
                overflow -= 2;
            } while (overflow > 0);
            for (bits = maxLength; bits != 0; bits--)
            {
                n = _blCount[bits];
                while (n != 0)
                {
                    m = _heap[--h];
                    if (m > maxCode) continue;
                    if (tree.Dl[m] != bits)
                    {
                        _optLen += ((long)bits - tree.Dl[m]) * tree.Fc[m];
                        tree.Dl[m] = (ushort)bits;
                    }
                    n--;
                }
            }
        }

        static void GenCodes(ushort[] code, ushort[] len, int maxCode, ushort[] blCount)
        {
            var nextCode = new int[MaxBits + 1];
            int c = 0;
            for (int bits = 1; bits <= MaxBits; bits++)
            {
                c = (c + blCount[bits - 1]) << 1;
                nextCode[bits] = (ushort)c;
            }
            for (int n = 0; n <= maxCode; n++)
            {
                int l = len[n];
                if (l == 0) continue;
                code[n] = (ushort)BiReverse(nextCode[l]++, l);
            }
        }

        static int BiReverse(int code, int len)
        {
            int res = 0;
            do { res |= code & 1; code >>= 1; res <<= 1; } while (--len > 0);
            return res >> 1;
        }

        void BuildTree(Desc desc)
        {
            Tree tree = desc.Dyn;
            ushort[] stree = desc.StaticLen;
            int elems = desc.Elems;
            int n, m, maxCode = -1, node;
            _heapLen = 0; _heapMax = HeapSize;
            for (n = 0; n < elems; n++)
            {
                if (tree.Fc[n] != 0) { _heap[++_heapLen] = maxCode = n; _depth[n] = 0; }
                else tree.Dl[n] = 0;
            }
            while (_heapLen < 2)
            {
                node = _heap[++_heapLen] = maxCode < 2 ? ++maxCode : 0;
                tree.Fc[node] = 1;
                _depth[node] = 0;
                _optLen--;
                if (stree != null) _staticLen -= stree[node];
            }
            desc.MaxCode = maxCode;
            for (n = _heapLen / 2; n >= 1; n--) PqDownHeap(tree, n);
            node = elems;
            do
            {
                n = _heap[1]; _heap[1] = _heap[_heapLen--]; PqDownHeap(tree, 1);
                m = _heap[1];
                _heap[--_heapMax] = n;
                _heap[--_heapMax] = m;
                tree.Fc[node] = (ushort)(tree.Fc[n] + tree.Fc[m]);
                _depth[node] = (byte)((_depth[n] >= _depth[m] ? _depth[n] : _depth[m]) + 1);
                tree.Dl[n] = tree.Dl[m] = (ushort)node;
                _heap[1] = node++;
                PqDownHeap(tree, 1);
            } while (_heapLen >= 2);
            _heap[--_heapMax] = _heap[1];
            GenBitlen(desc);
            GenCodes(tree.Fc, tree.Dl, maxCode, _blCount);
        }

        void ScanTree(Tree tree, int maxCode)
        {
            int prevlen = -1, nextlen = tree.Dl[0], count = 0, maxCount = 7, minCount = 4;
            if (nextlen == 0) { maxCount = 138; minCount = 3; }
            tree.Dl[maxCode + 1] = 0xffff;
            for (int n = 0; n <= maxCode; n++)
            {
                int curlen = nextlen; nextlen = tree.Dl[n + 1];
                if (++count < maxCount && curlen == nextlen) continue;
                else if (count < minCount) _bl.Fc[curlen] += (ushort)count;
                else if (curlen != 0)
                {
                    if (curlen != prevlen) _bl.Fc[curlen]++;
                    _bl.Fc[Rep3_6]++;
                }
                else if (count <= 10) _bl.Fc[Repz3_10]++;
                else _bl.Fc[Repz11_138]++;
                count = 0; prevlen = curlen;
                if (nextlen == 0) { maxCount = 138; minCount = 3; }
                else if (curlen == nextlen) { maxCount = 6; minCount = 3; }
                else { maxCount = 7; minCount = 4; }
            }
        }

        void SendTree(Tree tree, int maxCode)
        {
            int prevlen = -1, nextlen = tree.Dl[0], count = 0, maxCount = 7, minCount = 4;
            if (nextlen == 0) { maxCount = 138; minCount = 3; }
            for (int n = 0; n <= maxCode; n++)
            {
                int curlen = nextlen; nextlen = tree.Dl[n + 1];
                if (++count < maxCount && curlen == nextlen) continue;
                else if (count < minCount) { do { SendCode(curlen, _bl.Fc, _bl.Dl); } while (--count != 0); }
                else if (curlen != 0)
                {
                    if (curlen != prevlen) { SendCode(curlen, _bl.Fc, _bl.Dl); count--; }
                    SendCode(Rep3_6, _bl.Fc, _bl.Dl); SendBits(count - 3, 2);
                }
                else if (count <= 10) { SendCode(Repz3_10, _bl.Fc, _bl.Dl); SendBits(count - 3, 3); }
                else { SendCode(Repz11_138, _bl.Fc, _bl.Dl); SendBits(count - 11, 7); }
                count = 0; prevlen = curlen;
                if (nextlen == 0) { maxCount = 138; minCount = 3; }
                else if (curlen == nextlen) { maxCount = 6; minCount = 3; }
                else { maxCount = 7; minCount = 4; }
            }
        }

        int BuildBlTree()
        {
            ScanTree(_dynL, _lDesc.MaxCode);
            ScanTree(_dynD, _dDesc.MaxCode);
            BuildTree(_blDesc);
            int maxBlindex;
            for (maxBlindex = BlCodes - 1; maxBlindex >= 3; maxBlindex--)
                if (_bl.Dl[s_blOrder[maxBlindex]] != 0) break;
            _optLen += 3 * ((long)maxBlindex + 1) + 5 + 5 + 4;
            return maxBlindex;
        }

        void SendAllTrees(int lcodes, int dcodes, int blcodes)
        {
            SendBits(lcodes - 257, 5);
            SendBits(dcodes - 1, 5);
            SendBits(blcodes - 4, 4);
            for (int rank = 0; rank < blcodes; rank++) SendBits(_bl.Dl[s_blOrder[rank]], 3);
            SendTree(_dynL, lcodes - 1);
            SendTree(_dynD, dcodes - 1);
        }

        void TrStoredBlock(byte[] buf, int start, int storedLen, bool last)
        {
            SendBits((0 << 1) + (last ? 1 : 0), 3);
            BiWindup();
            PutShort(storedLen); PutShort(~storedLen);
            if (storedLen != 0) _out.Write(buf, start, storedLen);
        }

        void TrFlushBlock(int start, long storedLen, bool last)
        {
            BuildTree(_lDesc);
            BuildTree(_dDesc);
            int maxBlindex = BuildBlTree();
            long optLenb = (_optLen + 3 + 7) >> 3;
            long staticLenb = (_staticLen + 3 + 7) >> 3;
            if (staticLenb <= optLenb || _strategy == Fixed) optLenb = staticLenb;
            if (storedLen + 4 <= optLenb && start >= 0)
            {
                TrStoredBlock(_window, start, (int)storedLen, last);
            }
            else if (staticLenb == optLenb)
            {
                SendBits((1 << 1) + (last ? 1 : 0), 3);
                CompressBlock(s_staticLCode, s_staticLLen, s_staticDCode, s_staticDLen);
            }
            else
            {
                SendBits((2 << 1) + (last ? 1 : 0), 3);
                SendAllTrees(_lDesc.MaxCode + 1, _dDesc.MaxCode + 1, maxBlindex + 1);
                CompressBlock(_dynL.Fc, _dynL.Dl, _dynD.Fc, _dynD.Dl);
            }
            InitBlock();
            if (last) BiWindup();
        }

        void CompressBlock(ushort[] lcode, ushort[] llen, ushort[] dcode, ushort[] dlen)
        {
            int sx = 0;
            if (_symNext != 0)
            {
                do
                {
                    int dist = _symBuf[sx++];
                    dist += _symBuf[sx++] << 8;
                    int lc = _symBuf[sx++];
                    if (dist == 0) SendCode(lc, lcode, llen);
                    else
                    {
                        int code = s_lengthCode[lc];
                        SendCode(code + Literals + 1, lcode, llen);
                        int extra = s_extraLbits[code];
                        if (extra != 0) { lc -= s_baseLength[code]; SendBits(lc, extra); }
                        dist--;
                        code = DCode(dist);
                        SendCode(code, dcode, dlen);
                        extra = s_extraDbits[code];
                        if (extra != 0) { dist -= s_baseDist[code]; SendBits(dist, extra); }
                    }
                } while (sx < _symNext);
            }
            SendCode(EndBlock, lcode, llen);
        }

        void SendCode(int c, ushort[] code, ushort[] len) => SendBits(code[c], len[c]);

        void SendBits(int value, int length)
        {
            if (_biValid > BufSize - length)
            {
                _biBuf |= (value << _biValid) & 0xffff;
                PutShort(_biBuf);
                _biBuf = (value & 0xffff) >> (BufSize - _biValid);
                _biValid += length - BufSize;
            }
            else
            {
                _biBuf |= (value << _biValid) & 0xffff;
                _biValid += length;
            }
        }

        void PutShort(int w) { _out.WriteByte((byte)w); _out.WriteByte((byte)(w >> 8)); }

        void BiWindup()
        {
            if (_biValid > 8) PutShort(_biBuf);
            else if (_biValid > 0) _out.WriteByte((byte)_biBuf);
            _biBuf = 0;
            _biValid = 0;
        }
    }
}
