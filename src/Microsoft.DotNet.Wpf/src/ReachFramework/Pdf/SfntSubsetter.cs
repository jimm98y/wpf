// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A font cut down to the glyphs a document drew, for embedding.
//
// A page of text uses a few dozen glyphs of a face that has thousands; embedding the whole file is
// correct and costs a megabyte for Arial and nineteen for Noto Sans CJK. What PDF needs of an embedded
// face with Identity-H codes is only that each glyph id the content stream names still finds its
// outline -- so the subset KEEPS GLYPH IDS: every glyph the document did not use is emptied rather
// than removed, the face is truncated after the highest one it did use, and nothing in the content
// stream, the /W array or the ToUnicode map needs to change. The characters behind the glyphs stay
// in the font's ToUnicode CMap, so the text still searches and copies.
//
//   TrueType ('glyf')   the used glyphs and every component a composite one draws; loca rewritten
//       (long offsets), hmtx/hhea/maxp cut to the new glyph count, 'post' reduced to version 3 (no
//       names). The hinting programs (fpgm, prep, cvt) and OS/2, name, head stay -- a reader's
//       rasterizer runs them. Everything that is about laying text out (cmap, GSUB, GPOS, GDEF,
//       kern) or indexed by the old glyph count (hdmx, LTSH, VDMX, the bitmap strikes) goes: PDF
//       has already laid the text out, and a table describing glyphs that are not there is a lie a
//       strict reader rejects.
//   CFF                 the CharStrings of unused glyphs replaced by a bare endchar; the kept ones
//       interpreted far enough (Type 2 numbers, stem hints for the hint masks, callsubr/callgsubr
//       with their bias) to know which global and local subroutines they reach, and every other
//       subroutine replaced by a bare return -- subroutine numbers, like glyph ids, unchanged. The
//       charset, FDSelect, font DICTs and private DICTs are carried over with the offsets in the
//       DICTs that point at them rewritten, each private DICT now followed directly by its Subrs.
//       Noto Sans CJK: 16.4 MB -> 0.67 MB for a few characters. The glyph count stays.
//
// Anything not understood (CFF2, a malformed table) returns null and the face is embedded whole.
//

using System;
using System.Collections.Generic;

namespace System.Windows.Xps.Pdf
{
    internal sealed partial class SfntReader
    {
        // Tables a PDF reader can use for an embedded TrueType or CFF face.
        private static readonly string[] s_keepTrueType = { "OS/2", "cvt ", "fpgm", "glyf", "head", "hhea", "hmtx", "loca", "maxp", "name", "post", "prep" };
        private static readonly string[] s_keepCff = { "CFF ", "OS/2", "head", "hhea", "hmtx", "maxp", "name", "post" };

        /// <summary>This face reduced to <paramref name="glyphs"/> (glyph ids kept), or null when
        /// it cannot be subset.</summary>
        internal byte[] Subset(IEnumerable<ushort> glyphs)
        {
            try
            {
                if (!_tables.TryGetValue("maxp", out (int Offset, int Length) maxp) || maxp.Length < 6) return null;
                int count = ReadUInt16(_data, maxp.Offset + 4);
                if (count == 0) return null;
                var used = new SortedSet<int> { 0 };
                foreach (ushort g in glyphs) if (g < count) used.Add(g);

                return _tables.ContainsKey("glyf") ? SubsetTrueType(used, count) : IsCff ? SubsetCff(used, count) : null;
            }
            catch (IndexOutOfRangeException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private byte[] Table(string tag)
        {
            if (!_tables.TryGetValue(tag, out (int Offset, int Length) t)) return null;
            var b = new byte[t.Length];
            Buffer.BlockCopy(_data, t.Offset, b, 0, t.Length);
            return b;
        }

        // ---- TrueType ----------------------------------------------------------------------------

        private byte[] SubsetTrueType(SortedSet<int> used, int count)
        {
            if (!_tables.TryGetValue("loca", out (int Offset, int Length) loca) ||
                !_tables.TryGetValue("glyf", out (int Offset, int Length) glyf) ||
                !_tables.TryGetValue("head", out (int Offset, int Length) head) || head.Length < 54 ||
                !_tables.TryGetValue("hhea", out (int Offset, int Length) hhea) || hhea.Length < 36 ||
                !_tables.TryGetValue("hmtx", out (int Offset, int Length) hmtx))
            {
                return null;
            }

            bool longLoca = ReadInt16(_data, head.Offset + 50) != 0;
            if (loca.Length < (count + 1) * (longLoca ? 4 : 2)) return null;
            int GlyphStart(int g) => longLoca ? (int)ReadUInt32(_data, loca.Offset + g * 4) : ReadUInt16(_data, loca.Offset + g * 2) * 2;

            // The closure: a composite glyph draws other glyphs, which must come too.
            var pending = new Stack<int>(used);
            while (pending.Count > 0)
            {
                int g = pending.Pop();
                int start = GlyphStart(g), end = GlyphStart(g + 1);
                if (end - start < 10 || start < 0 || glyf.Offset + end > glyf.Offset + glyf.Length) continue;
                int p = glyf.Offset + start;
                if (ReadInt16(_data, p) >= 0) continue;
                p += 10;
                while (true)
                {
                    int flags = ReadUInt16(_data, p);
                    int component = ReadUInt16(_data, p + 2);
                    if (component < count && used.Add(component)) pending.Push(component);
                    p += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
                    if ((flags & 0x0008) != 0) p += 2;
                    else if ((flags & 0x0040) != 0) p += 4;
                    else if ((flags & 0x0080) != 0) p += 8;
                    if ((flags & 0x0020) == 0) break;
                }
            }

            int newCount = used.Max + 1;

            // glyf and loca: the used glyphs' bytes, each four-byte aligned; the rest empty.
            int size = 0;
            foreach (int g in used) size += Align4(Math.Max(0, GlyphStart(g + 1) - GlyphStart(g)));
            var newGlyf = new byte[Math.Max(size, 4)];
            var newLoca = new byte[(newCount + 1) * 4];
            int at = 0;
            for (int g = 0; g < newCount; g++)
            {
                WriteUInt32(newLoca, g * 4, (uint)at);
                if (!used.Contains(g)) continue;
                int start = GlyphStart(g), length = Math.Max(0, GlyphStart(g + 1) - start);
                Buffer.BlockCopy(_data, glyf.Offset + start, newGlyf, at, length);
                at += Align4(length);
            }
            WriteUInt32(newLoca, newCount * 4, (uint)at);
            if (at < newGlyf.Length) Array.Resize(ref newGlyf, Math.Max(at, 4));

            var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["glyf"] = newGlyf,
                ["loca"] = newLoca,
            };
            CommonTables(tables, newCount, hhea, hmtx, head, longLocaOut: true);
            foreach (string tag in s_keepTrueType)
                if (!tables.ContainsKey(tag) && Table(tag) is byte[] t) tables[tag] = t;
            return Assemble(ReadUInt32(_data, _origin), tables);
        }

        // hmtx, hhea, maxp, head and post for a face of newCount glyphs.
        private void CommonTables(Dictionary<string, byte[]> tables, int newCount, (int Offset, int Length) hhea,
                                  (int Offset, int Length) hmtx, (int Offset, int Length) head, bool longLocaOut)
        {
            int metrics = ReadUInt16(_data, hhea.Offset + 34);
            if (metrics == 0) throw new InvalidOperationException();
            int newMetrics = Math.Min(metrics, newCount);
            var newHmtx = new byte[newMetrics * 4 + (newCount - newMetrics) * 2];
            for (int g = 0; g < newCount; g++)
            {
                int lsbAt = g < metrics ? hmtx.Offset + g * 4 + 2 : hmtx.Offset + metrics * 4 + (g - metrics) * 2;
                if (lsbAt + 2 > hmtx.Offset + hmtx.Length) break;
                if (g < newMetrics)
                {
                    newHmtx[g * 4] = _data[hmtx.Offset + g * 4];
                    newHmtx[g * 4 + 1] = _data[hmtx.Offset + g * 4 + 1];
                    newHmtx[g * 4 + 2] = _data[lsbAt];
                    newHmtx[g * 4 + 3] = _data[lsbAt + 1];
                }
                else
                {
                    int o = newMetrics * 4 + (g - newMetrics) * 2;
                    newHmtx[o] = _data[lsbAt];
                    newHmtx[o + 1] = _data[lsbAt + 1];
                }
            }
            tables["hmtx"] = newHmtx;

            byte[] newHhea = Table("hhea");
            WriteUInt16(newHhea, 34, (ushort)newMetrics);
            tables["hhea"] = newHhea;

            byte[] newMaxp = Table("maxp");
            WriteUInt16(newMaxp, 4, (ushort)newCount);
            tables["maxp"] = newMaxp;

            byte[] newHead = Table("head");
            WriteUInt32(newHead, 8, 0);   // checkSumAdjustment, set once the file is assembled
            if (longLocaOut) WriteUInt16(newHead, 50, 1);
            tables["head"] = newHead;

            if (Table("post") is byte[] post && post.Length >= 32)
            {
                var newPost = new byte[32];
                Buffer.BlockCopy(post, 0, newPost, 0, 32);
                WriteUInt32(newPost, 0, 0x00030000);   // no glyph names
                tables["post"] = newPost;
            }
        }

        // An sfnt of the given tables: the directory sorted by tag, each table's checksum, and the
        // head's checkSumAdjustment over the whole file.
        private static byte[] Assemble(uint version, Dictionary<string, byte[]> tables)
        {
            var tags = new List<string>(tables.Keys);
            tags.Sort(StringComparer.Ordinal);
            int directory = 12 + tags.Count * 16, total = directory;
            foreach (string tag in tags) total += Align4(tables[tag].Length);
            var output = new byte[total];
            WriteUInt32(output, 0, version);
            WriteUInt16(output, 4, (ushort)tags.Count);
            int power = 1, selector = 0;
            while (power * 2 <= tags.Count) { power *= 2; selector++; }
            WriteUInt16(output, 6, (ushort)(power * 16));
            WriteUInt16(output, 8, (ushort)selector);
            WriteUInt16(output, 10, (ushort)((tags.Count - power) * 16));
            int entry = 12, cursor = directory, headAt = -1;
            foreach (string tag in tags)
            {
                byte[] t = tables[tag];
                for (int i = 0; i < 4; i++) output[entry + i] = (byte)tag[i];
                Buffer.BlockCopy(t, 0, output, cursor, t.Length);
                WriteUInt32(output, entry + 4, Checksum(output, cursor, Align4(t.Length)));
                WriteUInt32(output, entry + 8, (uint)cursor);
                WriteUInt32(output, entry + 12, (uint)t.Length);
                if (tag == "head") headAt = cursor;
                cursor += Align4(t.Length);
                entry += 16;
            }
            if (headAt >= 0) WriteUInt32(output, headAt + 8, unchecked(0xB1B0AFBAu - Checksum(output, 0, output.Length)));
            return output;
        }

        private static uint Checksum(byte[] d, int at, int length)
        {
            uint sum = 0;
            for (int i = 0; i + 3 < length; i += 4) sum = unchecked(sum + ReadUInt32(d, at + i));
            return sum;
        }

        // ---- CFF -----------------------------------------------------------------------------------

        private byte[] SubsetCff(SortedSet<int> used, int count)
        {
            if (!_tables.TryGetValue("CFF ", out (int Offset, int Length) cffTable) ||
                !_tables.TryGetValue("head", out (int Offset, int Length) head) || head.Length < 54 ||
                !_tables.TryGetValue("hhea", out (int Offset, int Length) hhea) || hhea.Length < 36 ||
                !_tables.TryGetValue("hmtx", out (int Offset, int Length) hmtx))
            {
                return null;
            }
            byte[] cff = Table("CFF ");
            byte[] newCff = Cff.Subset(cff, used);
            if (newCff == null) return null;

            var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["CFF "] = newCff };
            // The glyph count stays (CFF glyphs are only emptied), so the metrics stay whole.
            CommonTables(tables, count, hhea, hmtx, head, longLocaOut: false);
            foreach (string tag in s_keepCff)
                if (!tables.ContainsKey(tag) && Table(tag) is byte[] t) tables[tag] = t;
            return Assemble(ReadUInt32(_data, _origin), tables);
        }

        /// <summary>The Compact Font Format table (Adobe TN 5176), as far as subsetting needs it.</summary>
        private static class Cff
        {
            private const int OpCharset = 15, OpEncoding = 16, OpCharStrings = 17, OpPrivate = 18, OpSubrs = 19;
            private const int OpFdArray = 0x0c24, OpFdSelect = 0x0c25, OpRos = 0x0c1e;

            private struct Index
            {
                public int Start, End;          // the INDEX's bytes
                public int Count;
                public int[] Offsets;           // Count + 1 absolute data offsets
            }

            private static Index ReadIndex(byte[] d, int at)
            {
                var ix = new Index { Start = at, Count = (d[at] << 8) | d[at + 1] };
                if (ix.Count == 0) { ix.End = at + 2; ix.Offsets = new[] { at + 2 }; return ix; }
                int offSize = d[at + 2];
                if (offSize < 1 || offSize > 4) throw new InvalidOperationException();
                int offsets = at + 3, data = offsets + (ix.Count + 1) * offSize - 1;
                ix.Offsets = new int[ix.Count + 1];
                for (int i = 0; i <= ix.Count; i++)
                {
                    int v = 0;
                    for (int k = 0; k < offSize; k++) v = (v << 8) | d[offsets + i * offSize + k];
                    ix.Offsets[i] = data + v;
                }
                ix.End = ix.Offsets[ix.Count];
                if (ix.End > d.Length) throw new InvalidOperationException();
                return ix;
            }

            private static byte[] WriteIndex(List<byte[]> items)
            {
                if (items.Count == 0) return new byte[] { 0, 0 };
                int data = 0;
                foreach (byte[] b in items) data += b.Length;
                int offSize = data + 1 <= 0xff ? 1 : data + 1 <= 0xffff ? 2 : data + 1 <= 0xffffff ? 3 : 4;
                var o = new byte[3 + (items.Count + 1) * offSize + data];
                o[0] = (byte)(items.Count >> 8); o[1] = (byte)items.Count; o[2] = (byte)offSize;
                int off = 1, p = 3 + (items.Count + 1) * offSize;
                for (int i = 0; i <= items.Count; i++)
                {
                    for (int k = 0; k < offSize; k++) o[3 + i * offSize + k] = (byte)(off >> (8 * (offSize - 1 - k)));
                    if (i < items.Count)
                    {
                        Buffer.BlockCopy(items[i], 0, o, p, items[i].Length);
                        p += items[i].Length;
                        off += items[i].Length;
                    }
                }
                return o;
            }

            // A DICT as a list of (operator, operand bytes, integer operands).
            private sealed class Entry
            {
                public int Op;
                public byte[] Raw;              // operands and operator as they were
                public List<double> Values = new List<double>();
                public int[] Replace;           // when set, written as these 5-byte integers
            }

            private static List<Entry> ReadDict(byte[] d, int start, int end)
            {
                var entries = new List<Entry>();
                var current = new Entry();
                int entryStart = start, p = start;
                while (p < end)
                {
                    int b0 = d[p];
                    if (b0 <= 21)
                    {
                        int op = b0;
                        p++;
                        if (b0 == 12) { op = 0x0c00 | d[p]; p++; }
                        current.Op = op;
                        current.Raw = new byte[p - entryStart];
                        Buffer.BlockCopy(d, entryStart, current.Raw, 0, current.Raw.Length);
                        entries.Add(current);
                        current = new Entry();
                        entryStart = p;
                    }
                    else if (b0 == 28) { current.Values.Add((short)((d[p + 1] << 8) | d[p + 2])); p += 3; }
                    else if (b0 == 29) { current.Values.Add((d[p + 1] << 24) | (d[p + 2] << 16) | (d[p + 3] << 8) | d[p + 4]); p += 5; }
                    else if (b0 == 30)
                    {
                        // A real: nibbles until 0xf; not an offset, so only skipped.
                        p++;
                        while (p < end && (d[p] & 0x0f) != 0x0f && (d[p] & 0xf0) != 0xf0) p++;
                        p++;
                        current.Values.Add(0);
                    }
                    else if (b0 >= 32 && b0 <= 246) { current.Values.Add(b0 - 139); p++; }
                    else if (b0 >= 247 && b0 <= 250) { current.Values.Add((b0 - 247) * 256 + d[p + 1] + 108); p += 2; }
                    else if (b0 >= 251 && b0 <= 254) { current.Values.Add(-(b0 - 251) * 256 - d[p + 1] - 108); p += 2; }
                    else throw new InvalidOperationException();
                }
                return entries;
            }

            private static Entry Find(List<Entry> dict, int op)
            {
                foreach (Entry e in dict) if (e.Op == op) return e;
                return null;
            }

            private static byte[] WriteDict(List<Entry> dict)
            {
                var o = new List<byte>();
                foreach (Entry e in dict)
                {
                    if (e.Replace == null) { o.AddRange(e.Raw); continue; }
                    foreach (int v in e.Replace)
                    {
                        o.Add(29);
                        o.Add((byte)(v >> 24)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 8)); o.Add((byte)v);
                    }
                    if (e.Op > 0xff) { o.Add(12); o.Add((byte)e.Op); } else o.Add((byte)e.Op);
                }
                return o.ToArray();
            }

            // charset bytes for nGlyphs glyphs (format 0, 1 or 2).
            private static int CharsetLength(byte[] d, int at, int glyphs)
            {
                int format = d[at];
                if (format == 0) return 1 + 2 * (glyphs - 1);
                int p = at + 1, covered = 1;
                while (covered < glyphs)
                {
                    int left = format == 1 ? d[p + 2] : (d[p + 2] << 8) | d[p + 3];
                    p += format == 1 ? 3 : 4;
                    covered += left + 1;
                }
                if (format != 1 && format != 2) throw new InvalidOperationException();
                return p - at;
            }

            private static int EncodingLength(byte[] d, int at)
            {
                int format = d[at] & 0x7f, p;
                if (format == 0) p = at + 2 + d[at + 1];
                else if (format == 1) p = at + 2 + 2 * d[at + 1];
                else throw new InvalidOperationException();
                if ((d[at] & 0x80) != 0) p += 1 + 3 * d[p];
                return p - at;
            }

            private static int FdSelectLength(byte[] d, int at, int glyphs)
            {
                int format = d[at];
                if (format == 0) return 1 + glyphs;
                if (format == 3) return 1 + 2 + 3 * ((d[at + 1] << 8) | d[at + 2]) + 2;
                throw new InvalidOperationException();
            }

            // A private DICT's local Subrs INDEX (null when it has none, or one before the DICT).
            private static Index? LocalSubrs(byte[] d, Entry priv)
            {
                int size = (int)priv.Values[0], start = (int)priv.Values[1];
                Entry subrs = Find(ReadDict(d, start, start + size), OpSubrs);
                if (subrs == null || subrs.Values.Count != 1 || subrs.Values[0] < size) return null;
                return ReadIndex(d, start + (int)subrs.Values[0]);
            }

            // A private DICT followed directly by its local Subrs, the subroutines no kept glyph
            // calls emptied. The Subrs offset (relative to the DICT) is rewritten as a five-byte
            // integer to the DICT's new size -- in a CID font the subroutines usually sit far from
            // their DICT, past everything else. Size is the new DICT's length.
            private static byte[] PrivateBlock(byte[] d, Entry priv, HashSet<int> called, out int size)
            {
                size = (int)priv.Values[0];
                int start = (int)priv.Values[1];
                Index? subrs = LocalSubrs(d, priv);
                if (subrs == null)
                {
                    var only = new byte[size];
                    Buffer.BlockCopy(d, start, only, 0, size);
                    return only;
                }
                List<Entry> dict = ReadDict(d, start, start + size);
                Entry op = Find(dict, OpSubrs);
                op.Replace = new[] { 0 };
                size = WriteDict(dict).Length;
                op.Replace = new[] { size };
                byte[] head = WriteDict(dict);
                byte[] index = Emptied(d, subrs.Value, called);
                var block = new byte[head.Length + index.Length];
                Buffer.BlockCopy(head, 0, block, 0, head.Length);
                Buffer.BlockCopy(index, 0, block, head.Length, index.Length);
                return block;
            }

            // A subroutine INDEX with every entry not called reduced to a bare return.
            private static byte[] Emptied(byte[] d, Index ix, HashSet<int> called)
            {
                var items = new List<byte[]>(ix.Count);
                for (int i = 0; i < ix.Count; i++)
                {
                    if (called == null || called.Contains(i))
                    {
                        var b = new byte[ix.Offsets[i + 1] - ix.Offsets[i]];
                        Buffer.BlockCopy(d, ix.Offsets[i], b, 0, b.Length);
                        items.Add(b);
                    }
                    else items.Add(new byte[] { 11 });
                }
                return WriteIndex(items);
            }


            private static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

            // The Type 2 charstring interpreter as far as the subroutines go (TN 5177): numbers onto
            // the stack, the stem hints counted for the hint masks' length, each callsubr and
            // callgsubr followed. Every subroutine a glyph reaches is recorded. False when the
            // charstring could not be followed (the caller then keeps every subroutine).
            private static bool Trace(byte[] d, int start, int end, Index global, Index? local,
                                      HashSet<int> globalCalled, HashSet<int> localCalled)
            {
                var stack = new List<double>();
                var frames = new Stack<(int Pos, int End)>();
                int pos = start, stems = 0, steps = 0;
                while (steps++ < 1 << 20)
                {
                    if (pos >= end)
                    {
                        if (frames.Count == 0) return true;
                        (pos, end) = frames.Pop();
                        continue;
                    }
                    int b0 = d[pos];
                    if (b0 == 28) { stack.Add((short)((d[pos + 1] << 8) | d[pos + 2])); pos += 3; continue; }
                    if (b0 >= 32 && b0 <= 246) { stack.Add(b0 - 139); pos++; continue; }
                    if (b0 >= 247 && b0 <= 250) { stack.Add((b0 - 247) * 256 + d[pos + 1] + 108); pos += 2; continue; }
                    if (b0 >= 251 && b0 <= 254) { stack.Add(-(b0 - 251) * 256 - d[pos + 1] - 108); pos += 2; continue; }
                    if (b0 == 255) { stack.Add(((d[pos + 1] << 24) | (d[pos + 2] << 16) | (d[pos + 3] << 8) | d[pos + 4]) / 65536.0); pos += 5; continue; }
                    pos++;
                    switch (b0)
                    {
                        case 1: case 3: case 18: case 23:       // hstem vstem hstemhm vstemhm
                            stems += stack.Count / 2;
                            stack.Clear();
                            break;
                        case 19: case 20:                       // hintmask cntrmask
                            stems += stack.Count / 2;           // an implied vstem
                            stack.Clear();
                            pos += (stems + 7) / 8;
                            break;
                        case 10: case 29:                       // callsubr callgsubr
                        {
                            if (stack.Count == 0 || (b0 == 10 && local == null)) return false;
                            Index ix = b0 == 10 ? local.Value : global;
                            int n = (int)stack[stack.Count - 1] + Bias(ix.Count);
                            stack.RemoveAt(stack.Count - 1);
                            if (n < 0 || n >= ix.Count || frames.Count > 10) return false;
                            (b0 == 10 ? localCalled : globalCalled).Add(n);
                            frames.Push((pos, end));
                            pos = ix.Offsets[n]; end = ix.Offsets[n + 1];
                            break;
                        }
                        case 11:                                // return
                            if (frames.Count == 0) return false;
                            (pos, end) = frames.Pop();
                            break;
                        case 14:                                // endchar
                            return stack.Count < 4;             // a seac's accent glyphs: not followed
                        case 12:                                // escape: arithmetic and flex, no subroutine
                            pos++;
                            stack.Clear();
                            break;
                        default:
                            stack.Clear();
                            break;
                    }
                }
                return false;
            }

            // FDSelect: the font DICT each glyph takes.
            private static int FdOf(byte[] d, int at, int glyph)
            {
                if (d[at] == 0) return d[at + 1 + glyph];
                int ranges = (d[at + 1] << 8) | d[at + 2];
                for (int r = 0; r < ranges; r++)
                {
                    int p = at + 3 + r * 3;
                    int first = (d[p] << 8) | d[p + 1], next = (d[p + 3] << 8) | d[p + 4];
                    if (glyph >= first && glyph < next) return d[p + 2];
                }
                return 0;
            }

            internal static byte[] Subset(byte[] d, SortedSet<int> used)
            {
                if (d.Length < 4 || d[0] != 1) return null;   // CFF2 and later: not handled
                int hdrSize = d[2];
                Index names = ReadIndex(d, hdrSize);
                Index tops = ReadIndex(d, names.End);
                if (tops.Count != 1) return null;
                Index strings = ReadIndex(d, tops.End);
                Index gsubrs = ReadIndex(d, strings.End);
                List<Entry> top = ReadDict(d, tops.Offsets[0], tops.Offsets[1]);

                Entry charStrings = Find(top, OpCharStrings);
                if (charStrings == null || charStrings.Values.Count != 1) return null;
                Index glyphs = ReadIndex(d, (int)charStrings.Values[0]);
                int count = glyphs.Count;

                // The charstrings: the used ones whole, every other a bare endchar.
                var items = new List<byte[]>(count);
                for (int g = 0; g < count; g++)
                {
                    if (used.Contains(g))
                    {
                        var b = new byte[glyphs.Offsets[g + 1] - glyphs.Offsets[g]];
                        Buffer.BlockCopy(d, glyphs.Offsets[g], b, 0, b.Length);
                        items.Add(b);
                    }
                    else items.Add(new byte[] { 14 });
                }
                byte[] newGlyphs = WriteIndex(items);

                // The subroutines the kept glyphs call, global and per font DICT; all of them kept
                // when a charstring cannot be followed.
                Entry fdSelectAt = Find(top, OpFdSelect), fdArrayAt = Find(top, OpFdArray), privAt = Find(top, OpPrivate);
                var fdPrivates = new List<Entry>();
                if (fdArrayAt != null)
                {
                    Index fds0 = ReadIndex(d, (int)fdArrayAt.Values[0]);
                    for (int i = 0; i < fds0.Count; i++) fdPrivates.Add(Find(ReadDict(d, fds0.Offsets[i], fds0.Offsets[i + 1]), OpPrivate));
                }
                var globalCalled = new HashSet<int>();
                var localCalled = new Dictionary<int, HashSet<int>>();
                bool followed = true;
                foreach (int g in used)
                {
                    if (g >= count) continue;
                    int fd = fdSelectAt != null ? FdOf(d, (int)fdSelectAt.Values[0], g) : -1;
                    Entry p = fd >= 0 ? (fd < fdPrivates.Count ? fdPrivates[fd] : null) : privAt;
                    Index? local = p != null && p.Values.Count == 2 ? LocalSubrs(d, p) : null;
                    if (!localCalled.TryGetValue(fd, out HashSet<int> lc)) localCalled[fd] = lc = new HashSet<int>();
                    followed &= Trace(d, glyphs.Offsets[g], glyphs.Offsets[g + 1], gsubrs, local, globalCalled, lc);
                }
                if (!followed) { globalCalled = null; localCalled.Clear(); }
                HashSet<int> LocalsOf(int fd) => !followed ? null : localCalled.TryGetValue(fd, out HashSet<int> lc) ? lc : new HashSet<int>();
                byte[] newGlobals = Emptied(d, gsubrs, globalCalled);

                // What follows the fixed head, in order: charset, encoding, FDSelect, CharStrings,
                // FDArray, the private blocks. Each piece's new offset is known once the Top DICT's
                // size is, and that is fixed: every offset is written as a five-byte integer.
                var pieces = new List<byte[]>();
                var places = new List<Action<int>>();

                void Copy(int start, int length, Action<int> place)
                {
                    var b = new byte[length];
                    Buffer.BlockCopy(d, start, b, 0, length);
                    pieces.Add(b); places.Add(place);
                }

                Entry charset = Find(top, OpCharset);
                if (charset != null && charset.Values.Count == 1 && charset.Values[0] > 2)
                {
                    int at = (int)charset.Values[0];
                    Copy(at, CharsetLength(d, at, count), o => charset.Replace = new[] { o });
                }
                Entry encoding = Find(top, OpEncoding);
                if (encoding != null && encoding.Values.Count == 1 && encoding.Values[0] > 1)
                {
                    int at = (int)encoding.Values[0];
                    Copy(at, EncodingLength(d, at), o => encoding.Replace = new[] { o });
                }
                Entry fdSelect = Find(top, OpFdSelect);
                if (fdSelect != null)
                {
                    int at = (int)fdSelect.Values[0];
                    Copy(at, FdSelectLength(d, at, count), o => fdSelect.Replace = new[] { o });
                }
                pieces.Add(newGlyphs); places.Add(o => charStrings.Replace = new[] { o });

                Entry fdArray = Find(top, OpFdArray);
                int fdIndex = -1;
                List<List<Entry>> fdDicts = null;
                if (fdArray != null)
                {
                    Index fds = ReadIndex(d, (int)fdArray.Values[0]);
                    fdDicts = new List<List<Entry>>();
                    for (int i = 0; i < fds.Count; i++) fdDicts.Add(ReadDict(d, fds.Offsets[i], fds.Offsets[i + 1]));
                    // The FDArray's size does not depend on where the private blocks go (five-byte
                    // offsets), so it is written now for its length and again once they are placed.
                    foreach (List<Entry> fd in fdDicts)
                        if (Find(fd, OpPrivate) is Entry p && p.Values.Count == 2) p.Replace = new[] { (int)p.Values[0], 0 };
                    fdIndex = pieces.Count;
                    pieces.Add(WriteIndex(fdDicts.ConvertAll(WriteDict)));
                    places.Add(o => fdArray.Replace = new[] { o });
                    for (int i = 0; i < fdDicts.Count; i++)
                    {
                        Entry p = Find(fdDicts[i], OpPrivate);
                        if (p == null || p.Values.Count != 2) continue;
                        pieces.Add(PrivateBlock(d, p, LocalsOf(i), out int size));
                        places.Add(o => p.Replace = new[] { size, o });
                    }
                }
                Entry priv = Find(top, OpPrivate);
                if (priv != null && priv.Values.Count == 2)
                {
                    pieces.Add(PrivateBlock(d, priv, LocalsOf(-1), out int size));
                    places.Add(o => priv.Replace = new[] { size, o });
                }

                // Lay out: every replaced operand set to a placeholder first so the Top DICT's size
                // is final, then the pieces placed after it.
                foreach (Action<int> place in places) place(0);
                byte[] topDict = WriteIndex(new List<byte[]> { WriteDict(top) });
                int headLength = hdrSize + (names.End - names.Start) + topDict.Length + (strings.End - strings.Start) + newGlobals.Length;
                int cursor = headLength;
                for (int i = 0; i < pieces.Count; i++)
                {
                    places[i](cursor);
                    cursor += pieces[i].Length;
                }
                if (fdDicts != null)
                {
                    byte[] placed = WriteIndex(fdDicts.ConvertAll(WriteDict));
                    if (placed.Length != pieces[fdIndex].Length) return null;
                    pieces[fdIndex] = placed;
                }
                topDict = WriteIndex(new List<byte[]> { WriteDict(top) });

                var output = new byte[cursor];
                int w = 0;
                void Put(int start, int length) { Buffer.BlockCopy(d, start, output, w, length); w += length; }
                Put(0, hdrSize);
                Put(names.Start, names.End - names.Start);
                Buffer.BlockCopy(topDict, 0, output, w, topDict.Length); w += topDict.Length;
                Put(strings.Start, strings.End - strings.Start);
                Buffer.BlockCopy(newGlobals, 0, output, w, newGlobals.Length); w += newGlobals.Length;
                foreach (byte[] piece in pieces) { Buffer.BlockCopy(piece, 0, output, w, piece.Length); w += piece.Length; }
                return w == output.Length ? output : null;
            }
        }
    }
}
