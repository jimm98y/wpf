// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s character tables (GpTextTables.Data.cs, generated from gdiplus.dll) and the lookups it
// makes through them:
//
//   CharClassFromCh @18004a038   a BMP character's class through pccUnicodeClass's 256 pages; above
//       the BMP 0x101 for 0x20000..0x3FFFF (the CJK extensions), 0x100 otherwise
//   CharacterAttributes          per class: the ItemScript (byte 0), the itemizer's state class
//       (byte 1), and the flags (bits 16..): 0x80 the string needs the full imager, 0x100 a digit
//   BreakClassFromCharClass{Narrow,Wide}   the Line Services breaking class (FullTextImager's
//       +0x280; Wide when the face says IDWriteGdiPlusFontFace::IsWide)
//   LineBreakBehavior / the break conditions   whether Line Services may break between two classes
//       directly, and whether across spaces (TryPrevBreakRegular @18011d338)
//

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static partial class GpTextTables
    {
        public const int ScriptLatin = 1, ScriptControl = 0x2c;

        /// <summary>CharClassFromCh.</summary>
        public static int CharClass (int ch)
        {
            if (ch < 0x10000) {
                int p = ClassIndex [ch >> 8];
                return p < 0 ? -1 - p : ClassPages [p * 256 + (ch & 0xff)];
            }
            if (ch >= 0x20000 && ch < 0x40000) return 0x101;
            return 0x100;
        }

        public static uint Attributes (int ch) => CharacterAttributes [CharClass (ch)];

        /// <summary>The ItemScript of a character's class.</summary>
        public static int Script (int ch) => (int) (Attributes (ch) & 0xff);

        /// <summary>The itemizer's state class of a character (ItemizationFiniteStateMachine).</summary>
        public static int StateClass (int ch) => (int) ((Attributes (ch) >> 8) & 0xff);

        /// <summary>The flags (CharacterAttributes bits 16..): 0x80 full imager, 0x100 digit.</summary>
        public static int Flags (int ch) => (int) (Attributes (ch) >> 16);

        public static int BreakClass (int ch, bool wide) => (wide ? BreakClassWide : BreakClassNarrow) [CharClass (ch)];

        /// <summary>LineBreakBehavior's condition between a character of class <paramref name="before"/>
        /// and one of class <paramref name="after"/>: whether a break may come between them.</summary>
        public static bool CanBreakDirect (int before, int after) => BreakConditions [LineBreakBehavior [before * 23 + after] * 2] != 0;

        /// <summary>The same condition: whether a break may come across spaces between them.</summary>
        public static bool CanBreakAcrossSpaces (int before, int after) => BreakConditions [LineBreakBehavior [before * 23 + after] * 2 + 1] != 0;

        // ---- secondary itemization (digits, mirrored characters, vertical upright runs) ----------------

        /// <summary>SecondaryClassificationLookup: a code point's secondary class (3 digits, 1 digit
        /// separators, 5 currency and percent, 10 mirrored brackets, 6 CJK...); above the BMP 8 for
        /// 0x20000..0x3FFFF and 9 otherwise.</summary>
        public static int SecondaryClass (int cp)
        {
            if (cp >= 0x10000) return (uint) (cp - 0x20000) < 0x20000u ? 8 : 9;
            int lo = 0, hi = SecondaryRanges.Length / 3 - 1;
            while (lo <= hi) {
                int mid = (lo + hi) >> 1;
                if (cp < SecondaryRanges [mid * 3]) hi = mid - 1;
                else if (cp > SecondaryRanges [mid * 3 + 1]) lo = mid + 1;
                else return SecondaryRanges [mid * 3 + 2];
            }
            return 9;
        }

        /// <summary>The secondary state machine's column of a code point under a class mask
        /// (ScFlagsToScFE[ScBaseToScFlags[class] &amp; mask]).</summary>
        public static int SecondaryColumn (int cp, int mask) => ScFlagsToScFE [ScBaseToScFlags [SecondaryClass (cp)] & mask];

        public static int SecondaryAct (int column, int state) => SecondaryAction [column * 7 + state];
        public static int SecondaryNextState (int column, int state) => SecondaryNext [column * 7 + state];

        /// <summary>GetDigitSubstitutionsScript @1800eec58: the ItemScript a format's digits take, 0
        /// for none (StringDigitSubstitute User 0, None 1, National 2, Traditional 3).</summary>
        public static int DigitSubstitutionsScript (int method, int language)
        {
            int lang = language & 0xffff;
            if ((lang & 0x3ff) == 0) {
                int sub = lang >> 10;
                lang = sub == 1 ? UserDefaultLangId : sub == 2 ? SystemDefaultLangId : 9;
            }
            if (method == 1 || ((lang & 0x3ff) == 9 && method != 0)) return 0;
            if (method == 0) {
                int user = UserDefaultLangId;
                int shape = UserDigitSubstitute;
                if (shape == 0) return (user & 0x3ff) == 1 || (user & 0x3ff) == 0x29 ? 0x40 : 0;
                return shape == 2 ? NationalDigitScript (user) : 0;
            }
            if (method == 2) return NationalDigitScript (lang);
            if (method == 3 && (lang & 0x3ff) < 0x79) {
                int s = LanguageDigitScript [lang & 0x3ff];
                if (ScriptZeroDigit [s] != 0) return s;
            }
            return 0;
        }

        /// <summary>GetNationalDigitScript @1800eed60: the language's digit script when its locale's
        /// native digits (LOCALE_SNATIVEDIGITS) are not the ASCII ones.</summary>
        static int NationalDigitScript (int lang)
        {
            if ((lang & 0x3ff) >= 0x79) return 0;
            try {
                if (OperatingSystem.IsWindows ()) {
                    // The system's own NLS data (ICU's Thai and Hindi native digits are ASCII).
                    if (!IsValidLocale ((uint) lang, 1)) return 0;
                    var buf = new char [20];
                    int got = GetLocaleInfoW ((uint) lang, 0x13, buf, buf.Length);
                    if (got <= 1 || buf [1] == '1') return 0;
                } else {
                    string[] native = System.Globalization.CultureInfo.GetCultureInfo (lang).NumberFormat.NativeDigits;
                    if (native == null || native.Length < 2 || native [1] == "1") return 0;
                }
            } catch (Exception) { return 0; }
            int s = LanguageDigitScript [lang & 0x3ff];
            return ScriptZeroDigit [s] != 0 ? s : 0;
        }

        [System.Runtime.InteropServices.DllImport ("kernel32.dll")]
        static extern bool IsValidLocale (uint lcid, uint flags);
        [System.Runtime.InteropServices.DllImport ("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int GetLocaleInfoW (uint lcid, uint type, char[] data, int count);

        static int UserDefaultLangId
        {
            get { try { return System.Globalization.CultureInfo.CurrentCulture.LCID & 0xffff; } catch (Exception) { return 0x409; } }
        }

        static int SystemDefaultLangId
        {
            get { try { return System.Globalization.CultureInfo.InstalledUICulture.LCID & 0xffff; } catch (Exception) { return 0x409; } }
        }

        /// <summary>The user's LOCALE_IDIGITSUBSTITUTION: 0 context, 1 none, 2 national.</summary>
        static int UserDigitSubstitute
        {
            get { try { return (int) System.Globalization.CultureInfo.CurrentCulture.NumberFormat.DigitSubstitution; } catch (Exception) { return 1; } }
        }
    }
}
