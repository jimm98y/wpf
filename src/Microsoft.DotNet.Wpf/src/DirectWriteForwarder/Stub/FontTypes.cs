// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Off-Windows managed implementation of the MS.Internal.Text.TextInterface font surface that
// PresentationCore consumes from DirectWriteForwarder. On Windows the real C++/CLI vcxproj wraps
// DirectWrite; here each type is backed by the managed OpenType parser + system-font catalog in
// the Managed/ subfolder (no DirectWrite, no COM). The native IDWrite* constructors are retained
// only so PresentationCore's (compiled-for-macOS but never-executed) COM code paths still compile.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using MS.Internal.Text.TextInterface.Managed;

namespace MS.Internal.Text.TextInterface
{
    public sealed class Font
    {
        private readonly FaceRecord _face;

        // COM-compat ctor (never invoked off-Windows).
        public unsafe Font(Native.IDWriteFont* font) { }

        internal Font(FaceRecord face) { _face = face; }

        internal FaceRecord Face => _face;

        public IntPtr DWriteFontAddRef => IntPtr.Zero;

        public FontFamily Family => new FontFamily(FontFactoryState.LookupFamily(_face.FamilyName));

        public FontWeight Weight => _face.Weight;

        public FontStretch Stretch => _face.Stretch;

        public FontStyle Style => _face.Style;

        public bool IsSymbolFont => _face.IsSymbol;

        public LocalizedStrings FaceNames => LocalizedStrings.FromString(_face.FaceName);

        public FontSimulations SimulationFlags => FontSimulations.None;

        public FontMetrics Metrics => FontMetricsBuilder.Build(_face.GetData());

        public double Version => _face.GetData().FontRevision;

        public FontMetrics DisplayMetrics(float emSize, float pixelsPerDip)
            => FontMetricsBuilder.Build(_face.GetData());

        public static void ResetFontFaceCache()
        {
            // Cache-maintenance hook called at the end of each layout pass; nothing to trim.
        }

        public FontFace GetFontFace()
        {
            FontSimulations sims = FontSimulations.None;
            return new FontFace(_face, sims);
        }

        public bool GetInformationalStrings(InformationalStringID informationalStringID, out LocalizedStrings informationalStrings)
        {
            OpenTypeFontData d = _face.GetData();
            string s = informationalStringID switch
            {
                InformationalStringID.WIN32FamilyNames => d.FamilyName,
                InformationalStringID.PreferredFamilyNames => d.TypographicFamilyName ?? d.FamilyName,
                InformationalStringID.Win32SubFamilyNames => d.SubfamilyName,
                InformationalStringID.PreferredSubFamilyNames => d.TypographicSubfamilyName ?? d.SubfamilyName,
                _ => null,
            };
            if (string.IsNullOrEmpty(s)) { informationalStrings = null; return false; }
            informationalStrings = LocalizedStrings.FromString(s);
            return true;
        }

        public bool HasCharacter(uint unicodeValue) => _face.GetData().HasCharacter(unicodeValue);
    }

    public sealed unsafe class FontFace : IDisposable
    {
        private readonly FaceRecord _face;
        private readonly FontSimulations _sims;
        private OpenTypeFontData _data;

        public FontFace(Native.IDWriteFontFace* fontFace) { }

        internal FontFace(FaceRecord face, FontSimulations sims)
        {
            _face = face;
            _sims = sims;
        }

        /// <summary>The record this face was built from, so a Font can be made for the same file.</summary>
        internal FaceRecord Record => _face;

        private OpenTypeFontData Data => _data ??= _face.GetData();

        public void Dispose() { }

        public Native.IDWriteFontFace* DWriteFontFaceNoAddRef => null;

        public IntPtr DWriteFontFaceAddRef => IntPtr.Zero;

        public FontFaceType Type => Data.IsCff ? FontFaceType.CFF : FontFaceType.TrueType;

        public uint Index => (uint)_face.FaceIndex;

        public FontSimulations SimulationFlags => _sims;

        public bool IsSymbolFont => Data.IsSymbolFont;

        public FontMetrics Metrics => FontMetricsBuilder.Build(Data);

        public ushort GlyphCount => Data.NumGlyphs;

        public FontFile GetFileZero() => new FontFile(new Uri(_face.FilePath));

        public void AddRef() { }

        public void Release() { }

        public void GetDesignGlyphMetrics(ushort* glyphIndices, uint glyphCount, GlyphMetrics* glyphMetrics)
        {
            OpenTypeFontData d = Data;
            ushort advH = d.UnitsPerEm;
            for (uint i = 0; i < glyphCount; i++)
            {
                ushort gid = glyphIndices[i];
                ushort adv = d.AdvanceWidth(gid);
                short lsb = d.LeftSideBearing(gid);
                glyphMetrics[i] = new GlyphMetrics
                {
                    AdvanceWidth = adv,
                    LeftSideBearing = lsb,
                    // Ink-bbox side-bearings are approximated (0); WPF layout uses AdvanceWidth,
                    // and the renderer computes exact ink bounds from the outline itself.
                    RightSideBearing = 0,
                    TopSideBearing = 0,
                    BottomSideBearing = 0,
                    AdvanceHeight = advH,
                    VerticalOriginY = d.Ascender,
                };
            }
        }

        public void GetDisplayGlyphMetrics(ushort* glyphIndices, uint glyphCount, GlyphMetrics* glyphMetrics,
            float emSize, bool useDisplayNatural, bool isSideways, float pixelsPerDip)
        {
            // Without hinting/gridfitting we return the design metrics; visually adequate at UI sizes.
            GetDesignGlyphMetrics(glyphIndices, glyphCount, glyphMetrics);
        }

        public void GetArrayOfGlyphIndices(uint* codePoints, uint glyphCount, ushort* glyphIndices)
        {
            OpenTypeFontData d = Data;
            for (uint i = 0; i < glyphCount; i++)
                glyphIndices[i] = d.GlyphIndex(codePoints[i]);
        }

        /// <summary>The em square in font units, which is what an outline's coordinates are in.</summary>
        public ushort DesignUnitsPerEm => Data.UnitsPerEm;

        /// <summary>
        /// Writes a glyph's outline to the sink, in font units with y up.
        ///
        /// The managed replacement for MilGlyphRun_GetGlyphOutline, which was a wpfgfx entry point
        /// over a DirectWrite font face and therefore threw on every platform this port supports.
        /// False means the glyph has no outline at all -- a space, or any glyph of a bitmap-only
        /// colour font -- which is a normal answer and not a failure.
        /// </summary>
        public bool TryGetGlyphOutline(ushort glyphIndex, Managed.IGlyphOutlineSink sink)
            => Managed.GlyphOutlines.TryGetOutline(Data, glyphIndex, sink);

        public bool TryGetFontTable(OpenTypeTableTag openTypeTableTag, out byte[] tableData)
        {
            string tag = TagToString((uint)openTypeTableTag);
            tableData = Data.GetTableBytes(tag);
            return tableData != null;
        }

        // OpenTypeTableTag values are DWRITE_MAKE_OPENTYPE_TAG(a,b,c,d) = a|b<<8|c<<16|d<<24,
        // so the four ASCII bytes are little-endian.
        private static string TagToString(uint tag)
        {
            Span<char> c = stackalloc char[4];
            c[0] = (char)(tag & 0xFF);
            c[1] = (char)((tag >> 8) & 0xFF);
            c[2] = (char)((tag >> 16) & 0xFF);
            c[3] = (char)((tag >> 24) & 0xFF);
            return new string(c);
        }

        public bool ReadFontEmbeddingRights(out ushort fsType)
        {
            fsType = Data.FsType;
            return true;
        }
    }

    public class FontList : IEnumerable<Font>
    {
        // Backing list of faces for enumeration (a family's faces, or a match result).
        private protected readonly List<FaceRecord> _faces;

        public unsafe FontList(Native.IDWriteFactory* fontList) { _faces = new List<FaceRecord>(); }

        internal FontList(List<FaceRecord> faces) { _faces = faces; }

        public Font this[uint index] => new Font(_faces[(int)index]);

        public uint Count => (uint)_faces.Count;

        public FontCollection FontsCollection => FontFactoryState.SystemCollection;

        public virtual IEnumerator<Font> GetEnumerator()
        {
            foreach (FaceRecord f in _faces) yield return new Font(f);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class FontFamily : FontList
    {
        private readonly FamilyRecord _family;

        public unsafe FontFamily(Native.IDWriteFactory* fontFamily) : base(fontFamily) { }

        internal FontFamily(FamilyRecord family) : base(family?.Faces ?? new List<FaceRecord>())
        {
            _family = family;
        }

        public LocalizedStrings FamilyNames => LocalizedStrings.FromString(_family?.Name ?? string.Empty);

        public bool IsPhysical => true;

        public bool IsComposite => false;

        public string OrdinalName => _family?.Name ?? string.Empty;

        public new FontMetrics Metrics
            => FontMetricsBuilder.Build(GetRepresentativeFace().GetData());

        public new FontMetrics DisplayMetrics(float emSize, float pixelsPerDip)
            => FontMetricsBuilder.Build(GetRepresentativeFace().GetData());

        private FaceRecord GetRepresentativeFace()
            => MatchFace(FontWeight.Normal, FontStretch.Normal, FontStyle.Normal);

        public Font GetFirstMatchingFont(FontWeight weight, FontStretch stretch, FontStyle style)
            => new Font(MatchFace(weight, stretch, style));

        public FontList GetMatchingFonts(FontWeight weight, FontStretch stretch, FontStyle style)
        {
            // Order the faces by closeness to the request (DWrite returns a ranked list).
            var ordered = new List<FaceRecord>(_family.Faces);
            ordered.Sort((a, b) => MatchScore(a, weight, stretch, style).CompareTo(MatchScore(b, weight, stretch, style)));
            return new FontList(ordered);
        }

        // Weighted nearest-match over (style, stretch, weight), mirroring DWrite's matching order.
        private FaceRecord MatchFace(FontWeight weight, FontStretch stretch, FontStyle style)
        {
            FaceRecord best = null;
            int bestScore = int.MaxValue;
            foreach (FaceRecord f in _family.Faces)
            {
                int score = MatchScore(f, weight, stretch, style);
                if (score < bestScore) { bestScore = score; best = f; }
            }
            return best ?? _family.Faces[0];
        }

        private static int MatchScore(FaceRecord f, FontWeight weight, FontStretch stretch, FontStyle style)
        {
            int styleDist = f.Style == style ? 0 : ((f.Style != FontStyle.Normal) == (style != FontStyle.Normal) ? 1 : 3);
            int stretchDist = Math.Abs((int)f.Stretch - (int)stretch);
            int weightDist = Math.Abs((int)f.Weight - (int)weight) / 100;
            return styleDist * 10000 + stretchDist * 100 + weightDist;
        }
    }

    public sealed unsafe class FontCollection
    {
        private readonly SystemFontCatalog _catalog;

        public FontCollection(Native.IDWriteFontCollection* fontCollection) { }

        internal FontCollection(SystemFontCatalog catalog) { _catalog = catalog; }

        public uint FamilyCount => (uint)_catalog.FamilyCount;

        public FontFamily this[uint familyIndex] => new FontFamily(_catalog[(int)familyIndex]);

        public FontFamily this[string familyName]
        {
            get
            {
                // Strict: this indexer is what FamilyCollection.LookupFamily uses to walk a
                // composite font's fallback Target list, and a null answer is how it learns to
                // move on to the next candidate. Answering "yes, here is some Latin face" for a
                // family that is not installed ends the walk at the wrong font.
                FamilyRecord fam = FontFactoryState.LookupFamily(familyName, strict: true);
                return fam == null ? null : new FontFamily(fam);
            }
        }

        public bool FindFamilyName(string familyName, out uint index)
        {
            for (int i = 0; i < _catalog.FamilyCount; i++)
            {
                if (string.Equals(_catalog[i].Name, familyName, StringComparison.OrdinalIgnoreCase))
                {
                    index = (uint)i;
                    return true;
                }
            }
            index = 0;
            return false;
        }

        /// <summary>
        /// The Font for a face, including one that came from a file rather than from this
        /// collection.
        ///
        /// Returning null here -- which is what it did -- broke GlyphTypeface(Uri) for every font,
        /// not only ones outside the collection: Initialize assigns this to _font and then builds a
        /// FontFaceLayoutInfo from it, so the constructor threw NullReferenceException. That is the
        /// public API an application uses to load a font it ships with itself, which in WPF is an
        /// ordinary thing to do -- a pack:// URI to a .ttf in the assembly.
        ///
        /// A face carries its own record, so this needs no lookup and works for a file the
        /// collection has never seen.
        /// </summary>
        public Font GetFontFromFontFace(FontFace fontFace) => fontFace?.Record is FaceRecord record
            ? new Font(record)
            : null;
    }

    public sealed unsafe class FontFile : IDisposable
    {
        private readonly Uri _uri;

        public FontFile(Native.IDWriteFontFile* fontFile) { }

        internal FontFile(Uri uri) { _uri = uri; }

        public Native.IDWriteFontFile* DWriteFontFileNoAddRef => null;

        public bool Analyze(
            out Native.DWRITE_FONT_FILE_TYPE dwriteFontFileType,
            out Native.DWRITE_FONT_FACE_TYPE dwriteFontFaceType,
            out uint numberOfFaces,
            int* hr)
        {
            dwriteFontFileType = Native.DWRITE_FONT_FILE_TYPE.TrueType;
            dwriteFontFaceType = Native.DWRITE_FONT_FACE_TYPE.TrueType;
            numberOfFaces = 1;
            *hr = 0;
            return true;
        }

        public string GetUriPath() => _uri?.IsFile == true ? _uri.LocalPath : _uri?.AbsoluteUri;

        public void Dispose() { }
    }
}
