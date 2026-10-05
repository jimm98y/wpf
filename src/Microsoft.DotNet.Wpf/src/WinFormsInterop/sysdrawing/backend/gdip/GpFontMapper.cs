// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI's font mapper (win32kfull.sys 10.0.26100 arm64, MAPPER / PFEOBJ), over the port's own font
// list: which installed face a LOGFONT realizes to. GDI+ asks GDI for a face whenever it hands GDI
// a LOGFONT -- the down-level text of a metafile (its '@' vertical face above all), the LOGFONT an
// EMF record carries when it is played -- and GDI answers with this walk, not with the name.
//
//   ppfeGetAMatch @1401b5b40   the order of the attempts:
//       1. bFoundExactMatch over the family hash, then the face-name hash (a TrueType face's IFI
//          face name is its family, so FHOBJ::bInsert @140193b08 leaves it out: "Times New Roman
//          Bold" is not a name GDI knows), the family fallback allowed (an empty table: see
//          LoadRegistryTables).
//       2. bGetFaceName @1401b4828 -> FindFaceName @1401b3610: a default face for the charset and
//          pitch/family (the FontMapper registry table, InitializeFontSignatures @1403bd4a0 /
//          DefaultFontQueryRoutine @1403bbd60: value = charset | 0x8000 fixed | 0x4000 roman |
//          0x2000 vertical); the exact match again over that name (no fallback). An unknown key is
//          the empty name, which matches nothing.
//       3. vEmergency @1401b6118: every face, charset mismatches allowed (65000), the name now
//          scored (10000, 9000 for the family's default face, 1 for a FAMILY_EQUIV alias).
//   bFoundExactMatch @1401b3c50   the name through FontSubstitutes first (a charset-specific
//       "Name,cs" entry for the asked charset, else a generic one): a substitute whose target
//       names a charset replaces the name; one that does not is tried after it. A bucket of
//       alias names (flag 2) only when no primary bucket has the name, scored one more.
//   bNearMatch @1401b4990   the penalty of one face (TrueType faces: the size, aspect and
//       orientation terms are a raster font's and do not apply):
//         pitch      DEFAULT asked, fixed face 1; FIXED asked, variable face 15000; VARIABLE
//                    asked, fixed face 350
//         family     none asked takes the face's own (SYMBOL) or SWISS (ROMAN for "Tms Rmn");
//                    a face of no family 8000, another family 9000 (+50 across MODERN)
//         charset    the face's IFI charsets, then its linked faces' (FontLink\SystemLink);
//                    DEFAULT asked is the system charset, 2 when the face lacks it; another
//                    charset rejects the face (65000 in the emergency walk or for "Symbol")
//         vertical   an '@' name takes only '@' faces and a plain name only plain ones
//         italic     upright face asked italic 1 (simulated) or 4; italic face asked upright 4
//         weight     |dw| * 73 >> 8, less 120 first when a lighter face can be emboldened
//                    (dw > 150); a LOGFONT of weight 0 asks 400 and scores |dw| * 19 >> 7
//       Ties: the face loaded first (PFE +0x50, PFEMEMOBJ::bInit @1401d3ab0's load counter).
//
// The faces (fontdrvhost vFill_IFIMETRICS @140018db0, vFillIFICharsets @140018370):
//   family    name ID 1 in the UI language, else US English; other languages' ID 1 are aliases
//   charsets  the ANSI code page's charset first when the font covers it (not Hebrew, Arabic or
//             Thai), then the ulCodePageRange1 bits in fontdrvhost's table order, then OEM (255)
//             when ulCodePageRange2 has the OEM code page's bit
//   pitch     fixed when post.isFixedPitch (or a DBCS face with PANOSE proportion 9), else variable
//   family    PANOSE: script 0x40, decorative 0x50, monospaced 0x30, serif styles 2..10 roman and
//             11..15 swiss (modern for a Japanese or Korean face)
//   weight    usWeightClass (1..9 through fontdrvhost's table)
//   vertical  every face with a DBCS charset has an '@' twin.
//   variable  one face per fvar named instance, named from STAT (the hash keys a name's first 31
//             characters; the link table is looked up by the whole name).
//
// The port's font list is TrueType/OpenType only: GDI's raster and vector .fon faces are not in it,
// so where GDI's answer is one of them (or bGetFaceName's bitmap pass, MAPPER::bFindBitmapFont
// @1401b3ae0, finds a .fon of the asked pixel size for a weight other than 400 or 700) this
// answers with the TrueType face GDI would take without them. GDI+ asks for neither.
//
// The machine's tables are the registry's on Windows (FontMapper, FamilyDefaults,
// FontSubstitutes, FontMapperFamilyFallback, FontLink\SystemLink); elsewhere Windows' defaults.
//

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static class GpFontMapper
    {
        /// <summary>One PFE: a face as GDI's font table holds it.</summary>
        internal sealed class Face
        {
            public string Family = "";       // IFIMETRICS dpwszFamilyName ('@' for the vertical twin)
            public string FullName = "";     // dpwszFaceName (name ID 4)
            public string[] Aliases = Array.Empty<string> ();   // FM_INFO_FAMILY_EQUIV names
            public bool Vertical;
            public byte PitchFamily;         // jWinPitchAndFamily
            public int Weight;               // usWinWeight
            public int FsSelection;          // bit 0 italic, bit 5 bold
            public byte WinCharSet;          // jWinCharSet
            public byte[] Charsets = Array.Empty<byte> ();     // dpCharSets, DEFAULT_CHARSET ends it
            public int Order;                // PFE +0x50
            public string Path = "";
            public int Index;
            internal Face[] Links;

            public bool Italic => (FsSelection & 1) != 0;
            /// <summary>IFIOBJ::bSimItalic: an italic simulation exists (none for an italic face).</summary>
            public bool CanSimItalic => (FsSelection & 0x21) == 0 || (FsSelection & 0x21) == 0x20;
            /// <summary>IFIOBJ::pvSimBold: an emboldening exists (none for a bold face).</summary>
            public bool CanSimBold => (FsSelection & 0x21) == 0 || (FsSelection & 0x21) == 1;
            /// <summary>The family without the vertical '@'.</summary>
            public string BaseFamily => Vertical ? Family.Substring (1) : Family;
            public override string ToString () => Family + " " + Weight + (Italic ? " italic" : "");
        }

        /// <summary>What the mapper realized: the face, the charset it answers with, and the
        /// simulations it chose (+0xbc: 0x2000 bold, 0x4000 italic).</summary>
        internal sealed class Match
        {
            public Face Face;
            public byte Charset;
            public bool SimBold, SimItalic;
            public int Penalty;
        }

        // ---- the request ------------------------------------------------------------------------------

        const uint FWeightUnset = 0x200000, FAliasBucket = 0x400000, FTmsRmn = 0x800000,
            FVertical = 0x2000000, FEmergency = 0x4000000, FShellDlg = 0x8000000,
            FFaceNameAsked = 0x20000, FNameMatched = 0x40000000, FCanonical = 0x80000000;

        sealed class Mapper
        {
            public string Name;              // +0x10
            public byte Charset;             // +0x114 (the slot's)
            public byte AskedCharset;        // the LOGFONT's
            public byte PitchFamily;         // LOGFONT lfPitchAndFamily
            public bool Italic;
            public byte OutPrecision;
            public int Weight;               // +0xac
            public uint Flags;               // +0xf4
            public uint Best = 0xfffffffe;   // +0xb4
            public uint Cur;                 // +0xb8
            public Face BestFace;            // +0xc8
            public int BestOrder = -1;       // +0xd0 (unsigned: -1 is the largest)
            public uint Sims, BestSims;      // +0xbc
            public byte BestCharset;

            /// <summary>bNoMatch-style check after a penalty was added: false when the face is out.</summary>
            public bool Keep (Face f)
            {
                if (Best > Cur) return true;
                if (Cur != Best) return false;
                return (uint) BestOrder > (uint) f.Order;
            }
        }

        // ---- the tables -------------------------------------------------------------------------------

        static readonly object s_lock = new object ();
        static List<Face> s_faces;
        static Dictionary<string, List<Face>> s_family, s_familyAlias, s_face;
        static (string Name, uint Key)[] s_fontMapper;
        static string[] s_familyDefaults;
        static List<Substitute> s_substitutes;
        static Dictionary<string, string> s_familyFallback;
        static byte s_systemCharset;

        struct Substitute
        {
            public string Name;      // upcased
            public byte Charset;
            public bool AnyCharset;  // +0x81 bit 0: the entry names no charset
            public string Alt;
            public byte AltCharset;
            public bool AltAnyCharset;   // +0xc3 bit 0
        }

        /// <summary>The faces in load order (for a test or a dump).</summary>
        internal static IReadOnlyList<Face> Faces { get { Init (); return s_faces; } }

        static void Init ()
        {
            lock (s_lock) {
                if (s_faces != null) return;
                LoadRegistryTables ();
                var faces = new List<Face> ();
                int order = 0;
                foreach (string path in FontFiles.LoadOrder ()) {
                    try { ScanFile (path, faces, ref order); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                var fam = new Dictionary<string, List<Face>> (StringComparer.OrdinalIgnoreCase);
                var alias = new Dictionary<string, List<Face>> (StringComparer.OrdinalIgnoreCase);
                var full = new Dictionary<string, List<Face>> (StringComparer.OrdinalIgnoreCase);
                foreach (Face f in faces) {
                    Add (fam, Key (f.Family), f);
                    foreach (string a in f.Aliases) Add (alias, Key (f.Vertical ? "@" + a : a), f);
                    // FHOBJ::bInsert: the face-name hash leaves out a face named as its family,
                    // which a TrueType face's IFIMETRICS always is ("Times New Roman Bold" is
                    // not a name GDI finds).
                }
                s_family = fam; s_familyAlias = alias; s_face = full;
                s_faces = faces;
            }

            // FHOBJ::bInsert hashes the name's first 31 characters (a LOGFONT's lfFaceName).
            static string Key (string s) => s.Length > 31 ? s.Substring (0, 31) : s;

            static void Add (Dictionary<string, List<Face>> d, string k, Face f)
            {
                if (!d.TryGetValue (k, out List<Face> l)) d [k] = l = new List<Face> ();
                l.Add (f);
            }
        }

        // ---- ppfeGetAMatch ----------------------------------------------------------------------------

        /// <summary>The face GDI realizes a LOGFONT to (null when no face is installed at all).</summary>
        public static Match Map (string faceName, byte charset, byte pitchFamily, int weight, bool italic, byte outPrecision = 0)
        {
            Init ();
            if (s_faces.Count == 0) return null;
            var m = new Mapper {
                Name = faceName ?? "", AskedCharset = charset, Charset = charset, PitchFamily = pitchFamily,
                Italic = italic, OutPrecision = outPrecision, Weight = weight,
            };
            // MAPPER::MAPPER @1401b31a0: the name's special cases and the weight.
            string up = m.Name.ToUpperInvariant ();
            if (up == "MS SHELL DLG") m.Flags |= FShellDlg;
            else if (up == "SYMBOL") m.Flags |= FEmergency;
            else if (up == "TMS RMN") m.Flags |= FTmsRmn;
            else if (m.Name.StartsWith ("@", StringComparison.Ordinal)) m.Flags |= FVertical;
            if (m.Weight == 0) { m.Flags |= FWeightUnset; m.Weight = 400; }
            if (m.Name.Length == 0) GetFaceName (m);

            if (FoundExactMatch (m, s_family, s_familyAlias, true) || FoundExactMatch (m, s_face, null, true) || m.BestFace != null)
                return Result (m);
            if ((m.Flags & FFaceNameAsked) == 0) {
                GetFaceName (m);
                m.Best = 0xfffffffe; m.BestFace = null; m.BestOrder = -1; m.BestSims = 0;
                if (FoundExactMatch (m, s_family, s_familyAlias, false) || FoundExactMatch (m, s_face, null, false) || m.BestFace != null)
                    return Result (m);
            }
            m.Flags |= FEmergency;
            Emergency (m);
            return m.BestFace == null ? null : Result (m);
        }

        static Match Result (Mapper m) => new Match {
            Face = m.BestFace, Charset = m.BestCharset, SimBold = (m.BestSims & 0x2000) != 0,
            SimItalic = (m.BestSims & 0x4000) != 0, Penalty = (int) m.Best,
        };

        /// <summary>MAPPER::bGetFaceName: the FontMapper table's default face for the charset and
        /// pitch/family.</summary>
        static void GetFaceName (Mapper m)
        {
            byte cs = m.Charset == 1 ? s_systemCharset : m.Charset;
            uint key = cs;
            int pf = m.PitchFamily;
            if ((pf & 3) == 1) key |= 0x8000;
            if ((pf & 0x70) == 0x10) key |= 0x4000;
            else if ((pf & 3) == 0 && (pf & 0x70) == 0x30) key |= 0x8000;
            if ((m.Flags & FVertical) != 0) key |= 0x2000;
            m.Flags |= FFaceNameAsked;
            string name = "";
            foreach ((string n, uint k) in s_fontMapper)
                if (k == key) { name = n; break; }
            if (name.Length != 0 || (m.Flags & FNameMatched) == 0) m.Name = name;
        }

        /// <summary>MAPPER::bFoundExactMatch: true for a perfect face; an imperfect one stays the
        /// mapper's best.</summary>
        static bool FoundExactMatch (Mapper m, Dictionary<string, List<Face>> hash, Dictionary<string, List<Face>> aliases, bool fallback)
        {
            byte asked = m.AskedCharset;
            string name = m.Name;
            if (name.Length > 31) name = name.Substring (0, 31);
            string upName = name.ToUpperInvariant ();
            // FontSubstitutes: a "Name,cs" entry for the asked charset, else the last generic one.
            Substitute? specific = null, generic = null;
            foreach (Substitute s in s_substitutes) {
                if (s.Name != upName) continue;
                if (!s.AnyCharset) { if (s.Charset == asked) specific = s; }
                else generic = s;
            }
            var slots = new (List<Face> Faces, bool Alias, byte Charset)[3];
            Substitute? sub = specific ?? generic;
            if (sub == null) {
                slots [0] = Lookup (hash, aliases, name, asked);
                if (fallback && slots [0].Faces == null && s_familyFallback.TryGetValue (upName, out string fb))
                    slots [1] = Lookup (hash, aliases, fb, asked);
            } else if (!sub.Value.AltAnyCharset) {
                slots [1] = Lookup (hash, aliases, sub.Value.Alt, sub.Value.AltCharset);
            } else {
                slots [0] = Lookup (hash, aliases, name, asked);
                slots [1] = Lookup (hash, aliases, sub.Value.Alt, asked);
            }
            if (slots [0].Faces == null && slots [1].Faces == null) return false;
            m.Flags |= FNameMatched;
            for (int slot = 0; slot < 3; slot++) {
                if (slots [slot].Faces == null) continue;
                m.Charset = slots [slot].Charset;
                m.Flags = slots [slot].Alias ? m.Flags | FAliasBucket : m.Flags & ~FAliasBucket;
                m.Flags = slot == 2 ? m.Flags | FCanonical : m.Flags & ~FCanonical;
                foreach (Face f in slots [slot].Faces) {
                    if (!NearMatch (m, f, out byte cs, false)) continue;
                    SetBest (m, f, cs);
                    if (m.Cur == 0) { m.Flags &= ~FAliasBucket; return true; }
                    m.Best = m.Cur;
                }
            }
            m.Flags &= ~FAliasBucket;
            return false;
        }

        static (List<Face>, bool, byte) Lookup (Dictionary<string, List<Face>> hash, Dictionary<string, List<Face>> aliases, string name, byte cs)
        {
            if (string.IsNullOrEmpty (name)) return (null, false, cs);
            if (hash.TryGetValue (name, out List<Face> l)) return (l, false, cs);
            if (aliases != null && aliases.TryGetValue (name, out l)) return (l, true, cs);
            return (null, false, cs);
        }

        static void SetBest (Mapper m, Face f, byte cs)
        {
            m.BestFace = f; m.BestOrder = f.Order; m.BestSims = m.Sims; m.BestCharset = cs;
        }

        /// <summary>MAPPER::vEmergency: every face, the best by penalty and then load order.</summary>
        static void Emergency (Mapper m)
        {
            // The charset stays the one the last exact attempt's slot left (+0x114).
            m.BestFace = null; m.BestOrder = -1; m.Best = 0xfffffffe; m.BestSims = 0;
            foreach (Face f in s_faces) {
                if (!NearMatch (m, f, out byte cs, true)) continue;
                SetBest (m, f, cs);
                if (m.Cur == 0) return;
                m.Best = m.Cur;
            }
        }

        /// <summary>MAPPER::bNearMatch for a TrueType face: false when the face is out (worse than
        /// the best, or not a candidate at all); else m.Cur is its penalty.</summary>
        static bool NearMatch (Mapper m, Face f, out byte outCharset, bool scoreName)
        {
            outCharset = 0;
            m.Cur = 0; m.Sims = 0;
            int pf = m.PitchFamily;
            byte facePf = f.PitchFamily;
            // Pitch.
            uint p = 0;
            if ((pf & 3) == 0) p = (facePf & 1) != 0 ? 1u : 0u;
            else if ((pf & 3) == 1) p = (facePf & 2) != 0 ? 15000u : 0u;
            else p = (facePf & 2) == 0 ? 350u : 0u;
            if (p != 0) { m.Cur = p; if (!m.Keep (f)) return false; }
            // Family.
            int faceFam = facePf & 0x70;
            int want = pf & 0x70;
            if (want == 0) {
                if (m.Charset == 2) want = faceFam;
                else if (faceFam != 0) want = (m.Flags & FTmsRmn) != 0 ? 0x10 : 0x20;
            }
            string familyDefault = want < 0x60 ? s_familyDefaults [want >> 4] : null;
            if (want != faceFam) {
                uint d;
                if (faceFam == 0) d = 8000;
                else d = 9000u + (want < 0x31 ? (faceFam > 0x30 ? 50u : 0u) : (faceFam < 0x31 ? 50u : 0u));
                m.Cur += d;
                if (!m.Keep (f)) return false;
            }
            // Charset.
            byte req = m.Charset;
            if (req == 1 || (m.Flags & FShellDlg) != 0) {
                byte look = req == 1 ? s_systemCharset : req;
                byte got = FaceCharset (f, look);
                outCharset = got;
                if (req == 1 && (m.Flags & FShellDlg) == 0 && s_systemCharset != got) {
                    m.Cur += 2;
                    if (!m.Keep (f)) return false;
                }
            } else {
                byte got = FaceCharset (f, req);
                outCharset = got;
                if (got != req) {
                    if ((m.Flags & FEmergency) == 0) return Out (m);
                    m.Cur += 65000;
                    if (!m.Keep (f)) return false;
                }
            }
            // The name, in the emergency walk.
            if (scoreName && !string.Equals (m.Name, f.Family, StringComparison.OrdinalIgnoreCase)) {
                bool alias = false;
                foreach (string a in f.Aliases)
                    if (string.Equals (m.Name, f.Vertical ? "@" + a : a, StringComparison.OrdinalIgnoreCase)) { alias = true; break; }
                if (alias) m.Cur += 1;
                else m.Cur += familyDefault != null && string.Equals (f.Family, familyDefault, StringComparison.OrdinalIgnoreCase) ? 9000u : 10000u;
                if (!m.Keep (f)) return false;
            }
            // Vertical: an '@' name takes '@' faces only, any other name plain faces only.
            if (((m.Flags & FVertical) != 0) != f.Vertical) return Out (m);
            // lfOutPrecision OUT_PS_ONLY_PRECIS wants a PostScript outline.
            if (m.OutPrecision == 10) return Out (m);
            // Italic.
            if (!m.Italic) {
                if (f.Italic) { m.Cur += 4; if (!m.Keep (f)) return false; }
            } else if (!f.Italic) {
                if (f.CanSimItalic) { m.Cur += 1; m.Sims |= 0x4000; } else m.Cur += 4;
                if (!m.Keep (f)) return false;
            }
            // Weight.
            int dw = f.Weight - m.Weight;
            if ((m.Flags & FWeightUnset) == 0) {
                if (dw != 0) {
                    if (dw < 0) {
                        dw = -dw;
                        if (dw > 150 && f.CanSimBold) { dw -= 120; m.Sims |= 0x2000; }
                    }
                    m.Cur += (uint) (dw * 0x49 >> 8);
                    if (!m.Keep (f)) return false;
                }
            } else {
                m.Cur += (uint) (Math.Abs (dw) * 0x13 >> 7);
                if (!m.Keep (f)) return false;
            }
            // A face matched through an alias bucket.
            if ((m.Flags & FAliasBucket) != 0) {
                m.Cur += 1;
                if (!m.Keep (f)) return false;
            }
            return true;
        }

        static bool Out (Mapper m) { m.Cur = 0xfffffffe; return false; }

        /// <summary>The charset the face answers a request for <paramref name="cs"/> with: cs when
        /// the face or a face linked to it has it, else the face's first.</summary>
        static byte FaceCharset (Face f, byte cs)
        {
            if (cs == 0xfe) return 0xfe;
            if (HasCharset (f, cs)) return cs;
            foreach (Face l in Links (f))
                if (HasCharset (l, cs)) return cs;
            return f.Charsets.Length > 0 ? f.Charsets [0] : f.WinCharSet;
        }

        static bool HasCharset (Face f, byte cs)
        {
            foreach (byte c in f.Charsets) {
                if (c == cs) return true;
                if (c == 1) break;
            }
            return false;
        }

        /// <summary>PFE +0x78: the faces FontLink\SystemLink links to the face's family.</summary>
        static Face[] Links (Face f)
        {
            if (f.Links != null) return f.Links;
            var list = new List<Face> ();
            foreach (string linked in FontFiles.SystemLink (f.BaseFamily))
                if (s_family.TryGetValue (linked, out List<Face> l) && l.Count > 0) list.Add (l [0]);
            return f.Links = list.ToArray ();
        }

        // ---- the faces (fontdrvhost's IFIMETRICS) -----------------------------------------------------

        static void ScanFile (string path, List<Face> into, ref int order)
        {
            using Microsoft.Win32.SafeHandles.SafeFileHandle file = File.OpenHandle (path);
            long length = RandomAccess.GetLength (file);
            byte[] Read (long offset, int count)
            {
                if (offset < 0 || count < 0 || offset + count > length) throw new IOException ("truncated font");
                var b = new byte [count];
                int done = 0;
                while (done < count) {
                    int n = RandomAccess.Read (file, b.AsSpan (done), offset + done);
                    if (n <= 0) throw new IOException ("truncated font");
                    done += n;
                }
                return b;
            }
            if (length < 12) return;
            byte[] top = Read (0, 12);
            var offsets = new List<long> ();
            if (top [0] == 't' && top [1] == 't' && top [2] == 'c' && top [3] == 'f') {
                int n = Be32 (top, 8);
                byte[] dir = Read (12, 4 * n);
                for (int i = 0; i < n; i++) offsets.Add ((uint) Be32 (dir, 4 * i));
            } else offsets.Add (0);
            for (int idx = 0; idx < offsets.Count; idx++) {
                long sfnt = offsets [idx];
                byte[] head = Read (sfnt, 12);
                int numTables = Be16 (head, 4);
                byte[] recs = Read (sfnt + 12, 16 * numTables);
                byte[] os2 = null, post = null, name = null, hd = null, cmap = null, fvar = null, stat = null;
                for (int t = 0; t < numTables; t++) {
                    uint tag = (uint) Be32 (recs, 16 * t);
                    long off = (uint) Be32 (recs, 16 * t + 8);
                    int len = Be32 (recs, 16 * t + 12);
                    if (len <= 0 || off + len > length) continue;
                    switch (tag) {
                    case 0x4F532F32: os2 = Read (off, Math.Min (len, 100)); break;
                    case 0x706F7374: post = Read (off, Math.Min (len, 32)); break;
                    case 0x6E616D65: name = Read (off, len); break;
                    case 0x68656164: hd = Read (off, Math.Min (len, 54)); break;
                    case 0x636D6170: cmap = Read (off, Math.Min (len, 4 + 8 * 64)); break;
                    case 0x66766172: fvar = Read (off, len); break;
                    case 0x53544154: stat = Read (off, len); break;
                    }
                }
                if (name == null || hd == null) continue;
                Face f = MakeFace (os2, post, name, hd, cmap);
                if (f == null) continue;
                f.Path = path; f.Index = idx;
                List<Face> faces = fvar != null ? Instances (f, name, fvar, stat) : null;
                if (faces == null || faces.Count == 0) faces = new List<Face> { f };
                foreach (Face g in faces) {
                    g.Order = ++order;
                    into.Add (g);
                    if (IsAnyCharsetDbcs (g)) {
                        // The vertical twin ttfd exposes for a far-east face.
                        var v = new Face {
                            Family = "@" + g.Family, Aliases = g.Aliases, Vertical = true,
                            PitchFamily = g.PitchFamily, Weight = g.Weight, FsSelection = g.FsSelection, WinCharSet = g.WinCharSet,
                            Charsets = g.Charsets, Path = path, Index = idx, Order = ++order,
                        };
                        into.Add (v);
                    }
                }
            }
        }

        /// <summary>A variable font is one face per fvar named instance (vFill_IFIMETRICS with a
        /// NamedInstanceBuilder): the family is the typographic family plus the instance's STAT
        /// value names (elidable ones dropped, axes in STAT's ordering) less a 700 weight and the
        /// italic axis; the weight is the wght coordinate, bold set above 400.</summary>
        static List<Face> Instances (Face file, byte[] name, byte[] fvar, byte[] stat)
        {
            if (fvar.Length < 16) return null;
            int axOff = Be16 (fvar, 4), axCount = Be16 (fvar, 8), axSize = Be16 (fvar, 10), iCount = Be16 (fvar, 12), iSize = Be16 (fvar, 14);
            if (axCount == 0 || iCount == 0) return null;
            var tags = new string [axCount];
            for (int i = 0; i < axCount; i++) {
                if (axOff + i * axSize + 4 > fvar.Length) return null;
                tags [i] = System.Text.Encoding.ASCII.GetString (fvar, axOff + i * axSize, 4);
            }
            // STAT: the design axes (tag, ordering) and the axis values.
            var statAxes = new List<(string Tag, int Ordering)> ();
            var values = new List<(int Format, int Axis, int Flags, int NameId, double Value, double Min, double Max)> ();
            if (stat != null && stat.Length >= 18) {
                int aSize = Be16 (stat, 4), aCount = Be16 (stat, 6), vCount = Be16 (stat, 12);
                int aOff = Be32 (stat, 8), vOff = Be32 (stat, 14);
                for (int i = 0; i < aCount && aOff + i * aSize + 8 <= stat.Length; i++)
                    statAxes.Add ((System.Text.Encoding.ASCII.GetString (stat, aOff + i * aSize, 4), Be16 (stat, aOff + i * aSize + 6)));
                for (int i = 0; i < vCount && vOff + 2 * i + 2 <= stat.Length; i++) {
                    int v = vOff + Be16 (stat, vOff + 2 * i);
                    if (v + 12 > stat.Length) continue;
                    int fmt = Be16 (stat, v);
                    if (fmt < 1 || fmt > 3) continue;
                    double val = Be32 (stat, v + 8) / 65536.0, min = 0, max = 0;
                    if (fmt == 2 && v + 20 <= stat.Length) { min = Be32 (stat, v + 12) / 65536.0; max = Be32 (stat, v + 16) / 65536.0; }
                    values.Add ((fmt, Be16 (stat, v + 2), Be16 (stat, v + 4), Be16 (stat, v + 6), val, min, max));
                }
            }
            int ui = UiLangId;
            string typo = NameById (name, 16, ui) ?? NameById (name, 1, ui) ?? file.Family;
            var axes = new List<int> ();
            for (int a = 0; a < statAxes.Count; a++) axes.Add (a);
            axes.Sort ((x, y) => statAxes [x].Ordering != statAxes [y].Ordering ? statAxes [x].Ordering.CompareTo (statAxes [y].Ordering) : x.CompareTo (y));
            var list = new List<Face> ();
            for (int k = 0; k < iCount; k++) {
                int at = axOff + axCount * axSize + k * iSize;
                if (at + 4 + 4 * axCount > fvar.Length) break;
                var coords = new Dictionary<string, double> ();
                for (int a = 0; a < axCount; a++) coords [tags [a]] = Be32 (fvar, at + 4 + 4 * a) / 65536.0;
                int w = coords.TryGetValue ("wght", out double wv) ? (int) Math.Round (wv) : file.Weight;
                var famParts = new List<string> ();
                if (statAxes.Count > 0) {
                    foreach (int a in axes) {
                        string tag = statAxes [a].Tag;
                        bool inFvar = coords.TryGetValue (tag, out double c);
                        (int Format, int Axis, int Flags, int NameId, double Value, double Min, double Max)? hit = null;
                        foreach (var v in values) {
                            if (v.Axis != a) continue;
                            if (!inFvar) { hit = v; break; }
                            if (v.Format == 2 ? (v.Min <= c && c <= v.Max) : Math.Abs (v.Value - c) < 1e-3) { hit = v; break; }
                        }
                        if (hit == null || (hit.Value.Flags & 2) != 0) continue;
                        string nm = NameById (name, hit.Value.NameId, 0x409) ?? "";
                        if (!(tag == "wght" && w == 700) && tag != "ital") famParts.Add (nm);
                    }
                } else {
                    string sub = NameById (name, Be16 (fvar, at), 0x409) ?? "";
                    if (sub.Length > 0 && sub != "Regular") famParts.Add (sub);
                }
                string fam = famParts.Count > 0 ? typo + " " + string.Join (" ", famParts) : typo;
                bool italic = (file.FsSelection & 1) != 0 || (coords.TryGetValue ("ital", out double it) && it >= 1)
                    || (coords.TryGetValue ("slnt", out double sl) && sl != 0);
                list.Add (new Face {
                    Family = fam, Aliases = Array.Empty<string> (),
                    PitchFamily = file.PitchFamily, Weight = w, FsSelection = (italic ? 1 : 0) | (w > 400 ? 0x20 : 0),
                    WinCharSet = file.WinCharSet, Charsets = file.Charsets, Path = file.Path, Index = file.Index,
                });
            }
            return list;
        }

        /// <summary>A platform-3 name in the language, else US English, else any English, else the first.</summary>
        static string NameById (byte[] d, int id, int lang)
        {
            if (d.Length < 6) return null;
            int count = Be16 (d, 2), strings = Be16 (d, 4);
            string english = null, anyEnglish = null, first = null;
            for (int i = 0; i < count; i++) {
                int rec = 6 + i * 12;
                if (rec + 12 > d.Length) break;
                int platform = Be16 (d, rec), enc = Be16 (d, rec + 2), lid = Be16 (d, rec + 4), nid = Be16 (d, rec + 6);
                int len = Be16 (d, rec + 8), off = strings + Be16 (d, rec + 10);
                if (nid != id || platform != 3 || enc > 1 || off + len > d.Length) continue;
                string v = System.Text.Encoding.BigEndianUnicode.GetString (d, off, len & ~1);
                if (lid == lang) return v;
                if (lid == 0x409) english ??= v;
                if ((lid & 0x3ff) == 9) anyEnglish ??= v;
                first ??= v;
            }
            return english ?? anyEnglish ?? first;
        }

        static bool IsAnyCharsetDbcs (Face f)
        {
            static bool Dbcs (byte c) => c == 128 || c == 129 || c == 134 || c == 136;
            if (Dbcs (f.WinCharSet)) return true;
            foreach (byte c in f.Charsets) {
                if (Dbcs (c)) return true;
                if (c == 1) break;
            }
            return false;
        }

        // fontdrvhost's 'fs' / 'charsets' tables (@1400a72d0 / @1400a7320) and its OEM code pages
        // (@1400a6f60, ulCodePageRange2 from bit 31 down).
        static readonly uint[] s_fs = { 0x1, 0x20000, 0x80000, 0x200000, 0x40000, 0x100000, 0x20, 0x40, 0x8, 0x10, 0x80, 0x2, 0x4, 0x10000, 0x100, 0x80000000, 0x04000000 };
        static readonly byte[] s_charsets = { 0, 128, 129, 130, 134, 136, 177, 178, 161, 162, 186, 238, 204, 222, 163, 2, 254 };
        static readonly int[] s_oemPages = { 437, 850, 708, 737, 775, 852, 855, 857, 860, 861, 862, 863, 864, 865, 866, 869 };
        // usWeightClass 1..9 (@1400a7108) and the PANOSE serif style to family (@1400a7120, and
        // @1400a70a8 for a SHIFTJIS or HANGUL face).
        static readonly int[] s_weights = { 0, 100, 200, 300, 350, 400, 600, 700, 800, 900 };
        static readonly byte[] s_serifFamily = { 0, 0, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x20, 0x20, 0x20, 0x20, 0x20 };
        static readonly byte[] s_serifFamilyDbcs = { 0, 0, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x30, 0x30, 0x30, 0x30, 0x30 };

        static Face MakeFace (byte[] os2, byte[] post, byte[] name, byte[] hd, byte[] cmap)
        {
            var f = new Face ();
            ReadNames (name, out string family, out string full, out string[] aliases);
            if (string.IsNullOrEmpty (family)) return null;
            f.Family = family;
            f.FullName = full ?? "";
            f.Aliases = aliases;
            bool hasOs2 = os2 != null && os2.Length >= 68;
            // fsSelection (OS/2), else head.macStyle.
            if (hasOs2) f.FsSelection = Be16 (os2, 62);
            else {
                int ms = hd [45];
                f.FsSelection = ((ms & 1) << 5) | ((ms & 2) != 0 ? 1 : 0);
            }
            // Weight.
            if (hasOs2) {
                int w = Be16 (os2, 4);
                f.Weight = w < 10 ? s_weights [w] : w;
            } else f.Weight = s_weights [(hd [45] & 1) != 0 ? 8 : 5];
            bool symbolCmap = false;
            if (cmap != null && cmap.Length >= 4) {
                int nt = Be16 (cmap, 2);
                for (int k = 0; k < nt && 4 + 8 * k + 4 <= cmap.Length; k++)
                    if (Be16 (cmap, 4 + 8 * k) == 3 && Be16 (cmap, 6 + 8 * k) == 0) symbolCmap = true;
            }
            int version = hasOs2 ? Be16 (os2, 0) : -1;
            uint cp1 = hasOs2 && os2.Length >= 82 && version >= 1 ? (uint) Be32 (os2, 78) : 0;
            uint cp2 = hasOs2 && os2.Length >= 86 && version >= 1 ? (uint) Be32 (os2, 82) : 0;
            byte panFamily = hasOs2 ? os2 [32] : (byte) 2, panSerif = hasOs2 ? os2 [33] : (byte) 0, panProp = hasOs2 ? os2 [35] : (byte) 0;
            // jWinCharSet: the ACP's charset when the font covers it, else Latin 1, else the
            // first far-east code page; a font with no code pages is SYMBOL for a symbol cmap.
            (uint acpBit, byte acpCs) = AcpSignature (Acp);
            byte win;
            if (cp1 == 0) win = symbolCmap ? (byte) 2 : (byte) 0;
            else if (((cp1 >> 16) & 0x1e) == 0) win = 0;
            else if ((cp1 & acpBit) != 0) win = acpCs;
            else if ((cp1 & 1) != 0) win = 0;
            else if ((cp1 & 0x20000) != 0) win = 0x80;
            else if ((cp1 & 0x100000) != 0) win = 0x88;
            else if ((cp1 & 0x40000) != 0) win = 0x86;
            else win = 0x81;
            if (win == 0 && panFamily == 5 && symbolCmap) win = 2;
            f.WinCharSet = win;
            f.Charsets = Charsets (cp1, cp2, ref f.WinCharSet, symbolCmap);
            // jWinPitchAndFamily.
            bool fixedPitch = post != null && post.Length >= 16 && Be32 (post, 12) != 0;
            bool dbcsWin = f.WinCharSet == 0x80 || f.WinCharSet == 0x81;
            byte fam;
            if (dbcsWin) {
                fam = panFamily == 3 ? (byte) 0x40 : panSerif < 16 ? s_serifFamilyDbcs [panSerif] : (byte) 0;
                if (panProp == 9) fixedPitch = true;
            } else if (panFamily == 4) fam = 0x50;
            else if (panFamily == 3) fam = 0x40;
            else if (panProp == 9) fam = 0x30;
            else fam = panSerif < 16 ? s_serifFamily [panSerif] : (byte) 0;
            var tmp = new Face { WinCharSet = f.WinCharSet, Charsets = f.Charsets };
            if (IsAnyCharsetDbcs (tmp) && panProp == 9) fixedPitch = true;
            f.PitchFamily = (byte) (fam | (fixedPitch ? 1 : 2));
            return f;
        }

        /// <summary>vFillIFICharsets: up to 16 charsets, DEFAULT_CHARSET (1) after the last.</summary>
        static byte[] Charsets (uint cp1, uint cp2, ref byte win, bool symbolCmap)
        {
            var list = new List<byte> ();
            if (cp1 == 0) list.Add (symbolCmap ? (byte) 2 : (byte) 0);
            else {
                (uint sig, byte cur) = AcpSignature (Acp);
                if ((sig & cp1) != 0 && (sig & 0x10060) == 0) list.Add (cur);
                for (int i = 0; i < s_fs.Length; i++)
                    if ((s_fs [i] != sig || (sig & 0x10060) != 0) && (s_fs [i] & cp1) != 0 && list.Count < 16)
                        list.Add (s_charsets [i]);
                if (cp2 != 0) {
                    int oem = Oem;
                    uint bit = 0x80000000;
                    for (int i = 0; i < 16; i++, bit >>= 1)
                        if (s_oemPages [i] == oem) { if ((bit & cp2) != 0 && list.Count < 16) list.Add (255); break; }
                }
                if (!list.Contains (win)) win = list.Count == 0 ? (byte) 1 : list [0];
            }
            while (list.Count < 16) list.Add (1);
            return list.ToArray ();
        }

        static void ReadNames (byte[] d, out string family, out string full, out string[] aliases)
        {
            family = null; full = null;
            var families = new List<(int Lang, string Value)> ();
            var fulls = new List<(int Lang, string Value)> ();
            if (d.Length >= 6) {
                int count = Be16 (d, 2), strings = Be16 (d, 4);
                for (int i = 0; i < count; i++) {
                    int rec = 6 + i * 12;
                    if (rec + 12 > d.Length) break;
                    int platform = Be16 (d, rec), enc = Be16 (d, rec + 2), lang = Be16 (d, rec + 4), id = Be16 (d, rec + 6);
                    int len = Be16 (d, rec + 8), off = strings + Be16 (d, rec + 10);
                    if (platform != 3 || (enc != 0 && enc != 1) || (id != 1 && id != 4)) continue;
                    if (len <= 0 || off + len > d.Length) continue;
                    string v = System.Text.Encoding.BigEndianUnicode.GetString (d, off, len);
                    (id == 1 ? families : fulls).Add ((lang, v));
                }
            }
            int ui = UiLangId;
            family = Pick (families, ui);
            full = Pick (fulls, ui);
            var al = new List<string> ();
            foreach ((int _, string v) in families)
                if (v != family && !al.Contains (v) && !string.Equals (v, family, StringComparison.OrdinalIgnoreCase)) al.Add (v);
            aliases = al.ToArray ();

            static string Pick (List<(int Lang, string Value)> l, int ui)
            {
                foreach ((int lang, string v) in l) if (lang == ui) return v;
                foreach ((int lang, string v) in l) if (lang == 0x409) return v;
                return l.Count > 0 ? l [0].Value : null;
            }
        }

        // ---- the machine ------------------------------------------------------------------------------

        static int UiLangId
        {
            get {
                try { return CultureInfo.CurrentUICulture.LCID & 0xffff; } catch (Exception) { return 0x409; }
            }
        }

        [DllImport ("kernel32.dll")] static extern int GetACP ();
        [DllImport ("kernel32.dll")] static extern int GetOEMCP ();

        static int Acp
        {
            get {
                try { return OperatingSystem.IsWindows () ? GetACP () : CultureInfo.CurrentCulture.TextInfo.ANSICodePage; }
                catch (Exception) { return 1252; }
            }
        }

        static int Oem
        {
            get {
                try { return OperatingSystem.IsWindows () ? GetOEMCP () : CultureInfo.CurrentCulture.TextInfo.OEMCodePage; }
                catch (Exception) { return 437; }
            }
        }

        static (uint Bit, byte CharSet) AcpSignature (int acp) => acp switch {
            1250 => (0x2, 238), 1251 => (0x4, 204), 1253 => (0x8, 161), 1254 => (0x10, 162), 1255 => (0x20, 177), 1256 => (0x40, 178),
            1257 => (0x80, 186), 1258 => (0x100, 163), 874 => (0x10000, 222), 932 => (0x20000, 128), 936 => (0x40000, 134),
            949 => (0x80000, 129), 950 => (0x100000, 136), 1361 => (0x200000, 130), _ => (0x1, 0)
        };

        // Windows' defaults, for a machine with no registry.
        static readonly (string, uint)[] s_defaultFontMapper = {
            ("@MS Gothic", 41088), ("@MS PGothic", 8320), ("@NSimSun", 41094), ("@SimSun", 8326), ("ARIAL", 0), ("COURIER", 34816),
            ("COURIER NEW", 32768), ("FIXEDSYS", 36864), ("MS Gothic", 32896), ("MS PGothic", 128), ("MS SANS SERIF", 4096),
            ("MS SERIF", 20480), ("NSimSun", 32902), ("SimSun", 134), ("SMALL FONTS", 2048), ("SYMBOL", 16386), ("SYMBOL", 40962),
            ("TIMES NEW ROMAN", 16384), ("WINGDINGS", 2),
        };
        static readonly string[] s_defaultFamilyDefaults = { "Arial", "Times New Roman", "Arial", "Courier New", "Segoe Script", "Impact" };
        static readonly string[] s_defaultSubstitutes = {
            "Arabic Transparent=Arial", "Arabic Transparent Bold=Arial Bold", "Arabic Transparent Bold,0=Arial Bold,178",
            "Arabic Transparent,0=Arial,178", "Arial Baltic,186=Arial,186", "Arial CE,238=Arial,238", "Arial CYR,204=Arial,204",
            "Arial Greek,161=Arial,161", "Arial TUR,162=Arial,162", "Courier New Baltic,186=Courier New,186",
            "Courier New CE,238=Courier New,238", "Courier New CYR,204=Courier New,204", "Courier New Greek,161=Courier New,161",
            "Courier New TUR,162=Courier New,162", "Helv=MS Sans Serif", "Helvetica=Arial", "MS Shell Dlg 2=Tahoma",
            "Tahoma Armenian=Tahoma", "Times=Times New Roman", "Times New Roman Baltic,186=Times New Roman,186",
            "Times New Roman CE,238=Times New Roman,238", "Times New Roman CYR,204=Times New Roman,204",
            "Times New Roman Greek,161=Times New Roman,161", "Times New Roman TUR,162=Times New Roman,162", "Tms Rmn=MS Serif",
            "MS Shell Dlg=Microsoft Sans Serif",
        };

        const string Nt = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\";

        static void LoadRegistryTables ()
        {
            (uint _, byte acpCs) = AcpSignature (Acp);
            s_systemCharset = Acp == 65001 ? (byte) 0xfe : acpCs;
            var fm = new List<(string, uint)> ();
            List<(string Name, object Value)> values = Values (Nt + "FontMapper");
            if (values.Count == 0) fm.AddRange (s_defaultFontMapper);
            foreach ((string n, object v) in values) {
                if (v is not int k) continue;
                if (string.Equals (n, "DEFAULT", StringComparison.OrdinalIgnoreCase)) { s_systemCharset = (byte) k; continue; }
                // DefaultFontQueryRoutine drops a trailing digit ("SYMBOL1").
                string name = n.Length > 0 && n [^1] >= '0' && n [^1] <= '9' ? n.Substring (0, n.Length - 1) : n;
                fm.Add ((name, (uint) k));
            }
            s_fontMapper = fm.ToArray ();
            // RtlGetDefaultCodePage: a UTF-8 ANSI code page makes the system charset 0xfe.
            if (Acp == 65001) s_systemCharset = 0xfe;

            s_familyDefaults = (string[]) s_defaultFamilyDefaults.Clone ();
            List<(string Name, object Value)> fd = Values (Nt + @"FontMapper\FamilyDefaults");
            if (fd.Count > 0) {
                string[] keys = { "", "Roman", "Swiss", "Modern", "Script", "Decorative" };
                for (int i = 0; i < keys.Length; i++) {
                    s_familyDefaults [i] = null;
                    foreach ((string n, object v) in fd)
                        if (string.Equals (n, keys [i], StringComparison.OrdinalIgnoreCase) && v is string s) s_familyDefaults [i] = s;
                }
            }

            s_substitutes = new List<Substitute> ();
            List<(string Name, object Value)> subs = Values (Nt + "FontSubstitutes");
            if (subs.Count == 0)
                foreach (string e in s_defaultSubstitutes) { int eq = e.IndexOf ('='); subs.Add ((e.Substring (0, eq), e.Substring (eq + 1))); }
            foreach ((string n, object v) in subs) {
                if (v is not string alt) continue;
                var s = new Substitute ();
                SplitCharset (n, out s.Name, out s.Charset, out s.AnyCharset);
                SplitCharset (alt, out s.Alt, out s.AltCharset, out s.AltAnyCharset);
                s.Name = s.Name.ToUpperInvariant ();
                if (s.Name.Length == 0 || s.Alt.Length == 0) continue;
                s_substitutes.Add (s);
            }

            // vInitFontMapperFamilyFallbackTable @1403bd818 reads FontMapperFamilyFallbackDEPRECATED,
            // which Windows 11 does not have: the table is empty and no family falls back (the
            // FontMapperFamilyFallback key beside it is DirectWrite's).
            s_familyFallback = new Dictionary<string, string> (StringComparer.OrdinalIgnoreCase);
            List<(string Name, object Value)> ff = Values (Nt + "FontMapperFamilyFallbackDeprecated");
            foreach ((string n, object v) in ff)
                if (v is string s && !s_familyFallback.ContainsKey (n.ToUpperInvariant ())) s_familyFallback [n.ToUpperInvariant ()] = s;
        }

        static void SplitCharset (string s, out string name, out byte cs, out bool any)
        {
            int c = s.LastIndexOf (',');
            any = true; cs = 1; name = s.Trim ();
            if (c >= 0 && int.TryParse (s.Substring (c + 1).Trim (), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) {
                name = s.Substring (0, c).Trim (); cs = (byte) v; any = false;
            }
        }

        // ---- registry (Windows only) ------------------------------------------------------------------

        static List<(string, object)> Values (string subKey)
        {
            var list = new List<(string, object)> ();
            if (!OperatingSystem.IsWindows ()) return list;
            const int HkeyLocalMachine = unchecked((int) 0x80000002);
            if (RegOpenKeyExW ((IntPtr) HkeyLocalMachine, subKey, 0, 0x20019, out IntPtr key) != 0) return list;
            try {
                var name = new char [512];
                var data = new byte [2048];
                for (int i = 0; ; i++) {
                    int nameLen = name.Length, dataLen = data.Length;
                    if (RegEnumValueW (key, i, name, ref nameLen, IntPtr.Zero, out int type, data, ref dataLen) != 0) break;
                    string n = new string (name, 0, nameLen);
                    if (type == 4 && dataLen >= 4) list.Add ((n, BitConverter.ToInt32 (data, 0)));
                    else if (type == 1 || type == 2) list.Add ((n, System.Text.Encoding.Unicode.GetString (data, 0, Math.Max (0, dataLen)).TrimEnd ('\0')));
                }
            } finally { RegCloseKey (key); }
            return list;
        }

        [DllImport ("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegOpenKeyExW (IntPtr key, string subKey, int options, int desired, out IntPtr result);
        [DllImport ("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegEnumValueW (IntPtr key, int index, char[] name, ref int nameLen, IntPtr reserved, out int type, byte[] data, ref int dataLen);
        [DllImport ("advapi32.dll")]
        static extern int RegCloseKey (IntPtr key);

        static int Be16 (byte[] d, int o) => (d [o] << 8) | d [o + 1];
        static int Be32 (byte[] d, int o) => (d [o] << 24) | (d [o + 1] << 16) | (d [o + 2] << 8) | d [o + 3];
    }
}
