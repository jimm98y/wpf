// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A PDF reader, sufficient to check that what we wrote can be read.
//
// Deliberately independent of the writer: it starts from the END of the file, exactly as a real
// reader does -- find startxref, follow it to the cross-reference table, resolve the trailer's /Root,
// walk the page tree. A test that reused the writer's own bookkeeping would pass even if the file
// were internally inconsistent, which is the one failure mode that matters most in a binary format.
//
// It understands only what these tests assert on: dictionaries, arrays, names, numbers, strings,
// references and streams. No encryption, no object streams, no incremental updates -- none of which
// the writer produces.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Wpf.Printing.Tests
{
    internal sealed class PdfDocument
    {
        private readonly byte[] _bytes;
        private readonly Dictionary<int, long> _xref = new Dictionary<int, long>();

        internal PdfDictionary Trailer { get; private set; }

        internal string Header { get; private set; }

        private PdfDocument(byte[] bytes)
        {
            _bytes = bytes;
        }

        internal static PdfDocument Parse(byte[] bytes)
        {
            var document = new PdfDocument(bytes);
            document.ReadHeader();
            document.ReadCrossReferenceTable();
            return document;
        }

        internal IEnumerable<int> ObjectNumbers => _xref.Keys;

        /// <summary>The document catalog, reached the way a reader reaches it.</summary>
        internal PdfDictionary Catalog => (PdfDictionary)Resolve(Trailer["Root"]);

        /// <summary>Every page, in order, flattened out of the page tree.</summary>
        internal List<PdfDictionary> Pages()
        {
            var pages = new List<PdfDictionary>();
            Collect((PdfDictionary)Resolve(Catalog["Pages"]), pages);
            return pages;
        }

        private void Collect(PdfDictionary node, List<PdfDictionary> pages)
        {
            if (node == null) return;

            if (node.TryGetValue("Kids", out object kids))
            {
                foreach (object kid in (List<object>)Resolve(kids))
                {
                    Collect((PdfDictionary)Resolve(kid), pages);
                }
            }
            else
            {
                pages.Add(node);
            }
        }

        /// <summary>Follows an indirect reference; anything else is returned unchanged.</summary>
        internal object Resolve(object value)
        {
            while (value is PdfReference reference)
            {
                if (!_xref.TryGetValue(reference.Number, out long offset))
                {
                    throw new InvalidDataException($"object {reference.Number} is referenced but not in the xref table");
                }

                value = ParseObjectAt(offset, reference.Number);
            }

            return value;
        }

        /// <summary>A stream object's data, inflated if it was deflated.</summary>
        internal byte[] StreamData(object value)
        {
            var stream = Resolve(value) as PdfStream;
            if (stream == null) return null;

            if (stream.Dictionary.TryGetValue("Filter", out object filter) &&
                Resolve(filter) is string name && name == "FlateDecode")
            {
                using var input = new MemoryStream(stream.Data);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                return output.ToArray();
            }

            return stream.Data;
        }

        internal string StreamText(object value)
        {
            byte[] data = StreamData(value);
            if (data == null) return null;

            var sb = new StringBuilder(data.Length);
            foreach (byte b in data) sb.Append((char)b);
            return sb.ToString();
        }

        // ---- file structure --------------------------------------------------------

        private void ReadHeader()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(_bytes.Length, 16) && _bytes[i] >= ' '; i++) sb.Append((char)_bytes[i]);
            Header = sb.ToString();
        }

        private void ReadCrossReferenceTable()
        {
            int startxref = LastIndexOf("startxref");
            if (startxref < 0) throw new InvalidDataException("no startxref: the file has no cross-reference table");

            int at = startxref + "startxref".Length;
            long offset = (long)ReadNumberAt(ref at);

            if (offset <= 0 || offset >= _bytes.Length)
            {
                throw new InvalidDataException($"startxref points at {offset}, outside a {_bytes.Length}-byte file");
            }

            int cursor = (int)offset;
            Expect(ref cursor, "xref");

            while (true)
            {
                SkipWhitespace(ref cursor);
                if (Matches(cursor, "trailer")) break;

                int first = (int)ReadNumberAt(ref cursor);
                int count = (int)ReadNumberAt(ref cursor);

                for (int i = 0; i < count; i++)
                {
                    SkipWhitespace(ref cursor);

                    long entryOffset = (long)ReadNumberAt(ref cursor);
                    ReadNumberAt(ref cursor);                       // generation
                    SkipWhitespace(ref cursor);

                    char kind = (char)_bytes[cursor++];
                    if (kind == 'n') _xref[first + i] = entryOffset;
                }
            }

            Expect(ref cursor, "trailer");
            Trailer = (PdfDictionary)ParseValue(ref cursor);
        }

        private object ParseObjectAt(long offset, int expectedNumber)
        {
            int cursor = (int)offset;

            int number = (int)ReadNumberAt(ref cursor);
            if (number != expectedNumber)
            {
                throw new InvalidDataException(
                    $"the xref table says object {expectedNumber} is at {offset}, but object {number} is");
            }

            ReadNumberAt(ref cursor);                                // generation
            Expect(ref cursor, "obj");

            object value = ParseValue(ref cursor);

            SkipWhitespace(ref cursor);
            if (Matches(cursor, "stream"))
            {
                cursor += "stream".Length;
                if (cursor < _bytes.Length && _bytes[cursor] == '\r') cursor++;
                if (cursor < _bytes.Length && _bytes[cursor] == '\n') cursor++;

                var dictionary = (PdfDictionary)value;
                int length = Convert.ToInt32(Resolve(dictionary["Length"]), CultureInfo.InvariantCulture);

                if (cursor + length > _bytes.Length)
                {
                    throw new InvalidDataException($"stream in object {number} claims {length} bytes, past the end of the file");
                }

                var data = new byte[length];
                Buffer.BlockCopy(_bytes, cursor, data, 0, length);
                return new PdfStream { Dictionary = dictionary, Data = data };
            }

            return value;
        }

        // ---- object syntax ---------------------------------------------------------

        private object ParseValue(ref int at)
        {
            SkipWhitespace(ref at);
            if (at >= _bytes.Length) return null;

            char c = (char)_bytes[at];

            if (c == '<' && at + 1 < _bytes.Length && _bytes[at + 1] == '<') return ParseDictionary(ref at);
            if (c == '<') return ParseHexString(ref at);
            if (c == '[') return ParseArray(ref at);
            if (c == '/') return ParseName(ref at);
            if (c == '(') return ParseLiteralString(ref at);

            if (Matches(at, "true")) { at += 4; return true; }
            if (Matches(at, "false")) { at += 5; return false; }
            if (Matches(at, "null")) { at += 4; return null; }

            return ParseNumberOrReference(ref at);
        }

        private PdfDictionary ParseDictionary(ref int at)
        {
            at += 2;
            var dictionary = new PdfDictionary();

            while (true)
            {
                SkipWhitespace(ref at);
                if (at + 1 < _bytes.Length && _bytes[at] == '>' && _bytes[at + 1] == '>') { at += 2; break; }
                if (at >= _bytes.Length) throw new InvalidDataException("unterminated dictionary");

                string key = ParseName(ref at);
                dictionary[key] = ParseValue(ref at);
            }

            return dictionary;
        }

        private List<object> ParseArray(ref int at)
        {
            at++;
            var items = new List<object>();

            while (true)
            {
                SkipWhitespace(ref at);
                if (at >= _bytes.Length) throw new InvalidDataException("unterminated array");
                if (_bytes[at] == ']') { at++; break; }

                items.Add(ParseValue(ref at));
            }

            return items;
        }

        private string ParseName(ref int at)
        {
            at++;   // the slash
            var sb = new StringBuilder();

            while (at < _bytes.Length)
            {
                char c = (char)_bytes[at];
                if (IsDelimiter(c) || IsWhitespace(c)) break;

                if (c == '#' && at + 2 < _bytes.Length)
                {
                    sb.Append((char)Convert.ToInt32(new string(new[] { (char)_bytes[at + 1], (char)_bytes[at + 2] }), 16));
                    at += 3;
                    continue;
                }

                sb.Append(c);
                at++;
            }

            return sb.ToString();
        }

        private string ParseLiteralString(ref int at)
        {
            at++;
            var sb = new StringBuilder();
            int depth = 1;

            while (at < _bytes.Length)
            {
                char c = (char)_bytes[at++];

                if (c == '\\' && at < _bytes.Length) { sb.Append((char)_bytes[at++]); continue; }
                if (c == '(') depth++;
                if (c == ')' && --depth == 0) break;

                sb.Append(c);
            }

            return sb.ToString();
        }

        private byte[] ParseHexString(ref int at)
        {
            at++;
            var digits = new StringBuilder();

            while (at < _bytes.Length && _bytes[at] != '>')
            {
                char c = (char)_bytes[at++];
                if (Uri.IsHexDigit(c)) digits.Append(c);
            }
            at++;

            if ((digits.Length & 1) != 0) digits.Append('0');

            var bytes = new byte[digits.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(digits.ToString(i * 2, 2), 16);
            }

            return bytes;
        }

        private object ParseNumberOrReference(ref int at)
        {
            int save = at;
            double first = ReadNumberAt(ref at);

            // "12 0 R" is a reference; "12 0" is two numbers. The only way to tell is to look ahead.
            int probe = at;
            SkipWhitespace(ref probe);

            if (probe < _bytes.Length && char.IsDigit((char)_bytes[probe]))
            {
                int afterGeneration = probe;
                ReadNumberAt(ref afterGeneration);
                SkipWhitespace(ref afterGeneration);

                if (afterGeneration < _bytes.Length && _bytes[afterGeneration] == 'R')
                {
                    at = afterGeneration + 1;
                    return new PdfReference { Number = (int)first };
                }
            }

            at = save;
            return ReadNumberAt(ref at);
        }

        private double ReadNumberAt(ref int at)
        {
            SkipWhitespace(ref at);

            int start = at;
            while (at < _bytes.Length)
            {
                char c = (char)_bytes[at];
                if (!char.IsDigit(c) && c != '+' && c != '-' && c != '.') break;
                at++;
            }

            if (at == start) throw new InvalidDataException($"expected a number at byte {start}");

            var text = new StringBuilder(at - start);
            for (int i = start; i < at; i++) text.Append((char)_bytes[i]);

            return double.Parse(text.ToString(), CultureInfo.InvariantCulture);
        }

        // ---- lexing helpers --------------------------------------------------------

        private void Expect(ref int at, string keyword)
        {
            SkipWhitespace(ref at);
            if (!Matches(at, keyword)) throw new InvalidDataException($"expected '{keyword}' at byte {at}");
            at += keyword.Length;
        }

        private bool Matches(int at, string text)
        {
            if (at + text.Length > _bytes.Length) return false;
            for (int i = 0; i < text.Length; i++)
            {
                if (_bytes[at + i] != text[i]) return false;
            }
            return true;
        }

        private void SkipWhitespace(ref int at)
        {
            while (at < _bytes.Length)
            {
                char c = (char)_bytes[at];

                if (c == '%')                        // a comment runs to end of line
                {
                    while (at < _bytes.Length && _bytes[at] != '\n' && _bytes[at] != '\r') at++;
                    continue;
                }

                if (!IsWhitespace(c)) break;
                at++;
            }
        }

        private int LastIndexOf(string text)
        {
            for (int at = _bytes.Length - text.Length; at >= 0; at--)
            {
                if (Matches(at, text)) return at;
            }
            return -1;
        }

        private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\r' or '\n' or '\f' or '\0';

        private static bool IsDelimiter(char c) => c is '/' or '<' or '>' or '[' or ']' or '(' or ')' or '{' or '}' or '%';
    }

    internal sealed class PdfDictionary : Dictionary<string, object>
    {
    }

    internal sealed class PdfReference
    {
        internal int Number;

        public override string ToString() => Number + " 0 R";
    }

    internal sealed class PdfStream
    {
        internal PdfDictionary Dictionary;
        internal byte[] Data;
    }
}
