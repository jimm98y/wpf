// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF's half of the font cache: a GlyphTypeface described as PdfFontSource says fonts are described.
// PdfFontCache.cs itself is shared with WinForms printing and must not see a WPF type.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Media;

namespace System.Windows.Xps.Pdf
{
    internal sealed partial class PdfFontCache
    {
        /// <summary>The font resource for a typeface, creating it on first sight. Null if unusable.</summary>
        internal PdfFont For(GlyphTypeface typeface)
        {
            if (typeface == null) return null;
            return For(typeface, () => Describe(typeface));
        }

        private static PdfFontSource Describe(GlyphTypeface typeface)
        {
            IDictionary<ushort, double> advances = null;
            try { advances = typeface.AdvanceWidths; }
            catch (NotSupportedException) { }

            return new PdfFontSource
            {
                ReadFile = () => (ReadFontFile(typeface), FaceIndex(typeface)),
                FamilyName = BaseName(typeface),
                Ascent = typeface.Baseline,
                Descent = typeface.Baseline - typeface.Height,
                CapHeight = typeface.CapsHeight,
                Advance = advances == null ? null
                    : glyph => advances.TryGetValue(glyph, out double a) ? a : (double?)null,
            };
        }

        /// <summary>
        /// The font's bytes. Returns null when the font cannot be embedded, either because it does
        /// not permit it or because its file cannot be read.
        /// </summary>
        private static byte[] ReadFontFile(GlyphTypeface typeface)
        {
            try
            {
                // A font may forbid embedding outright, and honouring that is not optional.
                FontEmbeddingRight rights = typeface.EmbeddingRights;
                if (rights == FontEmbeddingRight.RestrictedLicense) return null;

                using Stream stream = typeface.GetFontStream();
                if (stream == null) return null;

                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (IOException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>
        /// Which face of a collection this typeface is. WPF puts it in the font URI's fragment, so
        /// "file:///.../NotoSansCJK-Regular.ttc#2" is the third face.
        /// </summary>
        private static int FaceIndex(GlyphTypeface typeface)
        {
            try
            {
                string fragment = typeface.FontUri?.Fragment;
                if (string.IsNullOrEmpty(fragment) || fragment.Length < 2) return 0;

                return int.TryParse(fragment.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture,
                                    out int index) ? index : 0;
            }
            catch (InvalidOperationException) { return 0; }
        }

        private static string BaseName(GlyphTypeface typeface)
        {
            try
            {
                foreach (KeyValuePair<Globalization.CultureInfo, string> name in typeface.FamilyNames)
                {
                    if (!string.IsNullOrEmpty(name.Value)) return name.Value;
                }
            }
            catch (NotSupportedException) { }

            return "Embedded";
        }
    }
}
