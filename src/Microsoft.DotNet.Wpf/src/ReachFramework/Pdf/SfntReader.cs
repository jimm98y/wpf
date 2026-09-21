// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Just enough OpenType to embed a font in a PDF.
//
// Two things are needed that a GlyphRun does not carry. First, the metrics a PDF FontDescriptor is
// required to state -- units per em and the font bounding box -- which live in the 'head' table.
// Second, and less obviously, a way to pull ONE font out of a collection.
//
// That second one is not a corner case here: this fork bundles Noto Sans CJK as a .ttc, so the CJK
// text a Japanese document is made of resolves to a font whose file holds several faces sharing
// their glyph data. PDF has no way to say "face 2 of this collection" -- FontFile2 is a single sfnt
// -- so the face has to be rebuilt as a standalone font. That is what ExtractFace does, and without
// it every Japanese PDF this port produces would embed a font no reader could parse.
//
// Deliberately not here: subsetting. Rewriting 'glyf' and 'loca' for a reduced glyph set is a
// separate piece of work, and the platform's own subsetter (GlyphTypeface.ComputeSubset) throws off
// Windows anyway. Whole fonts are embedded for now, which is correct and large.
//

using System;
using System.Collections.Generic;

namespace System.Windows.Xps.Pdf
{
    /// <summary>Reads an sfnt (TrueType/OpenType) file well enough to describe and re-emit one face.</summary>
    internal sealed class SfntReader
    {
        private readonly byte[] _data;
        private readonly int _origin;                       // where this face's table directory starts
        private readonly Dictionary<string, (int Offset, int Length)> _tables =
            new Dictionary<string, (int, int)>(StringComparer.Ordinal);

        private SfntReader(byte[] data, int origin)
        {
            _data = data;
            _origin = origin;
        }

        /// <summary>
        /// Opens one face. <paramref name="faceIndex"/> selects within a collection and is ignored for
        /// a plain font file. Returns null for anything that is not an sfnt.
        /// </summary>
        internal static SfntReader Open(byte[] data, int faceIndex)
        {
            if (data == null || data.Length < 12) return null;

            int origin = 0;
            uint tag = ReadUInt32(data, 0);

            if (tag == 0x74746366)   // 'ttcf'
            {
                if (data.Length < 16) return null;

                uint faces = ReadUInt32(data, 8);
                if (faceIndex < 0 || (uint)faceIndex >= faces) faceIndex = 0;

                int entry = 12 + faceIndex * 4;
                if (entry + 4 > data.Length) return null;

                origin = (int)ReadUInt32(data, entry);
                if (origin < 0 || origin + 12 > data.Length) return null;

                tag = ReadUInt32(data, origin);
            }

            // 0x00010000 is TrueType outlines; 'true' is the old Apple spelling; 'OTTO' is CFF.
            if (tag != 0x00010000 && tag != 0x74727565 && tag != 0x4F54544F) return null;

            var reader = new SfntReader(data, origin);
            return reader.ReadTableDirectory() ? reader : null;
        }

        private bool ReadTableDirectory()
        {
            int count = ReadUInt16(_data, _origin + 4);
            int entry = _origin + 12;

            for (int i = 0; i < count; i++, entry += 16)
            {
                if (entry + 16 > _data.Length) return false;

                string tag = new string(new[]
                {
                    (char)_data[entry], (char)_data[entry + 1], (char)_data[entry + 2], (char)_data[entry + 3],
                });

                int offset = (int)ReadUInt32(_data, entry + 8);
                int length = (int)ReadUInt32(_data, entry + 12);

                if (offset < 0 || length < 0 || (long)offset + length > _data.Length) continue;
                _tables[tag] = (offset, length);
            }

            return _tables.Count > 0;
        }

        /// <summary>True when the outlines are CFF, which PDF embeds as FontFile3 rather than FontFile2.</summary>
        internal bool IsCff => _tables.ContainsKey("CFF ") && !_tables.ContainsKey("glyf");

        /// <summary>Design units per em, from 'head'. 1000 for PostScript-flavoured fonts, usually 2048 for TrueType.</summary>
        internal int UnitsPerEm
        {
            get
            {
                if (!_tables.TryGetValue("head", out (int Offset, int Length) head) || head.Length < 54) return 1000;
                int units = ReadUInt16(_data, head.Offset + 18);
                return units > 0 ? units : 1000;
            }
        }

        /// <summary>
        /// The font bounding box in PDF's 1/1000 em units, from 'head'. Readers use it to decide how
        /// much of the page a glyph may touch, so a wrong one can clip text in some viewers.
        /// </summary>
        internal (int XMin, int YMin, int XMax, int YMax) BoundingBox
        {
            get
            {
                if (!_tables.TryGetValue("head", out (int Offset, int Length) head) || head.Length < 54)
                {
                    return (-500, -300, 1500, 1000);
                }

                double scale = 1000.0 / UnitsPerEm;
                return (
                    (int)Math.Floor(ReadInt16(_data, head.Offset + 36) * scale),
                    (int)Math.Floor(ReadInt16(_data, head.Offset + 38) * scale),
                    (int)Math.Ceiling(ReadInt16(_data, head.Offset + 40) * scale),
                    (int)Math.Ceiling(ReadInt16(_data, head.Offset + 42) * scale));
            }
        }

        /// <summary>Italic angle in degrees, from 'post'. Zero when the table is missing.</summary>
        internal double ItalicAngle
        {
            get
            {
                if (!_tables.TryGetValue("post", out (int Offset, int Length) post) || post.Length < 12) return 0;

                // A 16.16 fixed-point value.
                return ReadInt32(_data, post.Offset + 4) / 65536.0;
            }
        }

        /// <summary>
        /// This face as a standalone sfnt.
        ///
        /// For a plain font file the bytes are already standalone and are returned as they are. For a
        /// collection the tables belonging to this face are copied into a fresh file with a rebuilt
        /// directory -- which is the only way to name one face of a .ttc in a PDF.
        /// </summary>
        internal byte[] ExtractFace()
        {
            if (_origin == 0 && ReadUInt32(_data, 0) != 0x74746366) return _data;

            var tags = new List<string>(_tables.Keys);
            tags.Sort(StringComparer.Ordinal);   // the specification requires the directory be sorted by tag

            int directory = 12 + tags.Count * 16;
            int total = directory;
            foreach (string tag in tags) total += Align4(_tables[tag].Length);

            var output = new byte[total];

            WriteUInt32(output, 0, ReadUInt32(_data, _origin));   // keep the face's own sfnt version
            WriteUInt16(output, 4, (ushort)tags.Count);

            // searchRange, entrySelector and rangeShift: derived values no modern reader consults,
            // but malformed ones do trip validators, so they are computed properly.
            int power = 1, selector = 0;
            while (power * 2 <= tags.Count) { power *= 2; selector++; }
            WriteUInt16(output, 6, (ushort)(power * 16));
            WriteUInt16(output, 8, (ushort)selector);
            WriteUInt16(output, 10, (ushort)((tags.Count - power) * 16));

            int entry = 12;
            int cursor = directory;

            foreach (string tag in tags)
            {
                (int Offset, int Length) table = _tables[tag];

                output[entry] = (byte)tag[0];
                output[entry + 1] = (byte)tag[1];
                output[entry + 2] = (byte)tag[2];
                output[entry + 3] = (byte)tag[3];

                WriteUInt32(output, entry + 4, ReadUInt32(_data, TableDirectoryEntry(tag) + 8));   // checksum, unchanged
                WriteUInt32(output, entry + 8, (uint)cursor);
                WriteUInt32(output, entry + 12, (uint)table.Length);

                Buffer.BlockCopy(_data, table.Offset, output, cursor, table.Length);

                cursor += Align4(table.Length);
                entry += 16;
            }

            return output;
        }

        /// <summary>Offset of a tag's entry in the SOURCE directory, so its checksum can be copied.</summary>
        private int TableDirectoryEntry(string tag)
        {
            int count = ReadUInt16(_data, _origin + 4);
            int entry = _origin + 12;

            for (int i = 0; i < count; i++, entry += 16)
            {
                if (_data[entry] == (byte)tag[0] && _data[entry + 1] == (byte)tag[1] &&
                    _data[entry + 2] == (byte)tag[2] && _data[entry + 3] == (byte)tag[3])
                {
                    return entry;
                }
            }

            return _origin + 12;
        }

        private static int Align4(int value) => (value + 3) & ~3;

        private static uint ReadUInt32(byte[] d, int at)
            => (uint)((d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3]);

        private static int ReadInt32(byte[] d, int at) => (int)ReadUInt32(d, at);

        private static int ReadUInt16(byte[] d, int at) => (d[at] << 8) | d[at + 1];

        private static int ReadInt16(byte[] d, int at) => (short)((d[at] << 8) | d[at + 1]);

        private static void WriteUInt32(byte[] d, int at, uint value)
        {
            d[at] = (byte)(value >> 24);
            d[at + 1] = (byte)(value >> 16);
            d[at + 2] = (byte)(value >> 8);
            d[at + 3] = (byte)value;
        }

        private static void WriteUInt16(byte[] d, int at, ushort value)
        {
            d[at] = (byte)(value >> 8);
            d[at + 1] = (byte)value;
        }
    }
}
