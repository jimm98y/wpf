// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The PDF file itself: indirect objects, streams, the page tree, the cross-reference table.
//
// This is the layer that knows what a PDF IS, and nothing about what WPF draws -- PdfDevice sits on
// top and knows only about drawing. Keeping the two apart is what makes the format testable: a test
// can write objects here and read the bytes back without a Visual, a Dispatcher or a font anywhere
// in sight.
//
// The structure produced is deliberately plain: one cross-reference TABLE rather than a stream, no
// object streams, no compression of the document structure. Those save space in a large document and
// cost readability in every one, and a PDF that a human can open in a text editor when something is
// wrong is worth more here than a smaller file. Content streams and embedded resources ARE compressed,
// which is where essentially all the bytes are anyway.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace System.Windows.Xps.Pdf
{
    /// <summary>
    /// Builds a PDF document into a stream. Objects are allocated up front and written in any order;
    /// the cross-reference table is assembled from the offsets recorded as each one lands.
    /// </summary>
    internal sealed class PdfWriter : IDisposable
    {
        // 1/72 inch. WPF measures in 1/96 inch, so every coordinate crossing this boundary is scaled
        // by 0.75 -- see PageMatrix, which also flips the y axis.
        internal const double UnitsPerInch = 72.0;
        internal const double WpfUnitsPerInch = 96.0;
        internal const double WpfToPdf = UnitsPerInch / WpfUnitsPerInch;

        private readonly Stream _stream;
        private readonly bool _leaveOpen;

        // Offset of each object from the start of the file, indexed by object number. Entry 0 is the
        // free-list head that every PDF must carry and no reader ever follows.
        private readonly List<long> _offsets = new List<long> { 0 };

        private readonly List<int> _pages = new List<int>();
        private long _position;
        private bool _closed;

        internal PdfWriter(Stream stream, bool leaveOpen = false)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _leaveOpen = leaveOpen;

            // The binary comment on line 2 is not decoration: it is how transfer software tells that
            // the file must not be treated as text and line endings must not be rewritten.
            WriteRaw("%PDF-1.7\n");
            WriteRaw("%âãÏÓ\n");
        }

        /// <summary>Reserves an object number. Nothing is written until <see cref="BeginObject"/>.</summary>
        internal int AllocateObject()
        {
            _offsets.Add(-1);
            return _offsets.Count - 1;
        }

        /// <summary>Starts writing the body of a previously allocated object.</summary>
        internal void BeginObject(int id)
        {
            _offsets[id] = _position;
            WriteRaw(id.ToString(CultureInfo.InvariantCulture));
            WriteRaw(" 0 obj\n");
        }

        internal void EndObject()
        {
            WriteRaw("\nendobj\n");
        }

        /// <summary>Writes a complete object whose body is the given text.</summary>
        internal void WriteObject(int id, string body)
        {
            BeginObject(id);
            WriteRaw(body);
            EndObject();
        }

        /// <summary>
        /// Writes a stream object: a dictionary followed by bytes.
        ///
        /// The payload is deflated unless it is already compressed by its own filter (a JPEG, say),
        /// which <paramref name="filter"/> signals. /Length has to be exact and precede the data, so
        /// compression happens here rather than being streamed.
        /// </summary>
        internal void WriteStreamObject(int id, byte[] data, string dictionaryEntries = null, string filter = null)
        {
            bool deflate = filter == null;
            byte[] payload = deflate ? Deflate(data) : data;

            var dictionary = new StringBuilder("<< ");
            dictionary.Append("/Length ").Append(payload.Length.ToString(CultureInfo.InvariantCulture)).Append(' ');
            if (deflate) dictionary.Append("/Filter /FlateDecode ");
            else if (filter.Length != 0) dictionary.Append("/Filter ").Append(filter).Append(' ');
            if (!string.IsNullOrEmpty(dictionaryEntries)) dictionary.Append(dictionaryEntries).Append(' ');
            dictionary.Append(">>\nstream\n");

            BeginObject(id);
            WriteRaw(dictionary.ToString());
            WriteBytes(payload);
            WriteRaw("\nendstream");
            EndObject();
        }

        /// <summary>Records a page object so the page tree can name it at close.</summary>
        internal void AddPage(int pageObjectId) => _pages.Add(pageObjectId);

        internal int PageCount => _pages.Count;

        /// <summary>
        /// The matrix mapping WPF's drawing space onto the page: 96ths of an inch to 72nds, and a y
        /// axis that runs DOWN from the top onto one that runs UP from the bottom. Every page starts
        /// with this, so everything above can go on thinking in WPF coordinates.
        /// </summary>
        internal static string PageMatrix(double heightInWpfUnits)
        {
            return string.Concat(
                Number(WpfToPdf), " 0 0 ", Number(-WpfToPdf), " 0 ",
                Number(heightInWpfUnits * WpfToPdf), " cm\n");
        }

        /// <summary>
        /// Finishes the document: page tree, catalog, cross-reference table and trailer.
        /// </summary>
        internal void Close()
        {
            if (_closed) return;
            _closed = true;

            // PagesObjectId, not a fresh allocation. Every page already wrote its /Parent naming this
            // number, so allocating another here would leave the one they point at never written --
            // a dangling reference and an xref entry with a zero offset.
            int pagesId = PagesObjectId;
            int catalogId = AllocateObject();

            var kids = new StringBuilder("[ ");
            foreach (int page in _pages)
            {
                kids.Append(page.ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");
            }
            kids.Append(']');

            WriteObject(pagesId, string.Concat(
                "<< /Type /Pages /Count ", _pages.Count.ToString(CultureInfo.InvariantCulture),
                " /Kids ", kids.ToString(), " >>"));

            WriteObject(catalogId, string.Concat(
                "<< /Type /Catalog /Pages ", pagesId.ToString(CultureInfo.InvariantCulture), " 0 R >>"));

            long xref = _position;
            int count = _offsets.Count;

            WriteRaw("xref\n0 " + count.ToString(CultureInfo.InvariantCulture) + "\n");
            WriteRaw("0000000000 65535 f \n");

            for (int i = 1; i < count; i++)
            {
                long offset = _offsets[i] < 0 ? 0 : _offsets[i];
                WriteRaw(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
            }

            WriteRaw(string.Concat(
                "trailer\n<< /Size ", count.ToString(CultureInfo.InvariantCulture),
                " /Root ", catalogId.ToString(CultureInfo.InvariantCulture), " 0 R >>\n",
                "startxref\n", xref.ToString(CultureInfo.InvariantCulture), "\n%%EOF\n"));

            _stream.Flush();
        }

        /// <summary>
        /// The object number the page tree WILL have. Pages must name their parent, and the parent is
        /// written last, so the number is reserved on first use.
        /// </summary>
        internal int PagesObjectId
            => _pagesObjectId != 0 ? _pagesObjectId : (_pagesObjectId = AllocateObject());

        private int _pagesObjectId;

        public void Dispose()
        {
            Close();
            if (!_leaveOpen) _stream.Dispose();
        }

        // ---- primitives ------------------------------------------------------------

        /// <summary>
        /// A number as PDF spells it: no exponent, no thousands separator, no culture. A German
        /// locale writing "0,75" produces a file that every reader rejects, and it is exactly the
        /// kind of bug that only appears on someone else's machine.
        /// </summary>
        internal static string Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "0";

            // Four decimals is finer than a thousandth of a point at any realistic page size.
            double rounded = Math.Round(value, 4);
            if (rounded == 0) return "0";   // also folds -0

            return rounded.ToString("0.####", CultureInfo.InvariantCulture);
        }

        /// <summary>A PDF name, with the characters that would end it escaped.</summary>
        internal static string Name(string value)
        {
            var sb = new StringBuilder(value.Length + 1).Append('/');
            foreach (char c in value)
            {
                if (c is '#' or '/' or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '%' || c <= ' ' || c > '~')
                {
                    sb.Append('#').Append(((int)c & 0xFF).ToString("X2", CultureInfo.InvariantCulture));
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>A PDF literal string, parenthesized and escaped.</summary>
        internal static string LiteralString(string value)
        {
            var sb = new StringBuilder(value.Length + 2).Append('(');
            foreach (char c in value)
            {
                if (c is '(' or ')' or '\\') sb.Append('\\').Append(c);
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\n') sb.Append("\\n");
                else if (c < ' ' || c > '~') sb.Append('\\').Append(Convert.ToString((int)c & 0xFF, 8).PadLeft(3, '0'));
                else sb.Append(c);
            }
            return sb.Append(')').ToString();
        }

        private static byte[] Deflate(byte[] data)
        {
            using var output = new MemoryStream();

            // A zlib wrapper, which is what /FlateDecode means -- a bare deflate stream is rejected
            // by strict readers. .NET's ZLibStream writes the two-byte header and the Adler checksum.
            using (var deflate = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(data, 0, data.Length);
            }

            return output.ToArray();
        }

        private void WriteRaw(string text)
        {
            // Latin-1 rather than UTF-8: PDF syntax outside strings is bytes, and the binary marker
            // in the header must land as the four bytes it names rather than as their UTF-8 encoding.
            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++) bytes[i] = (byte)text[i];
            WriteBytes(bytes);
        }

        private void WriteBytes(byte[] bytes)
        {
            _stream.Write(bytes, 0, bytes.Length);
            _position += bytes.Length;
        }
    }
}
