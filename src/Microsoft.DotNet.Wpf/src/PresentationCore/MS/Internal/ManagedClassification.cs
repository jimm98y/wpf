// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed replacement for the native Unicode classification tables (PresentationNative
// MILGetClassificationTables), used off-Windows. WPF's text fast path asks Classification
// for each character's class and that class's CharacterAttribute (Script/ItemClass/Flags/
// BiDi/BreakType). The native tables are a compressed per-codepoint trie; here we classify
// by Unicode range into a small set of attribute templates. The "class" value we return is
// just an index into that template array -- callers always immediately look up
// CharAttributeOf(class), so any self-consistent numbering works.
//
// Coverage is complete for Latin/Western text (the common case). Complex scripts are marked
// CharacterComplex so WPF routes them to the (managed) shaping path rather than the fast path.
//

using System.Globalization;

namespace MS.Internal
{
    internal static class ManagedClassification
    {
        // Template indices (also the "unicode class" value returned to WPF).
        private const int C_Control = 0;
        private const int C_CRLF = 1;
        private const int C_Space = 2;
        private const int C_Digit = 3;
        private const int C_Latin = 4;
        private const int C_Punct = 5;
        private const int C_SimpleLetter = 6;   // Greek/Cyrillic/Armenian/Georgian etc. (no shaping)
        private const int C_Complex = 7;        // Indic/Thai/etc. (requires shaping)
        private const int C_ComplexRTL = 8;     // Arabic/Hebrew (RTL + shaping)
        private const int C_Ideographic = 9;    // CJK
        private const int C_Combining = 10;     // combining marks

        // Enum numeric values (from MS/internal/UnicodeClasses.cs), inlined to avoid a hard
        // dependency ordering; kept in sync with those enums.
        // ItemClass: Digit=0, Weak=6, Strong=5, SimpleMark=7, Control=9
        // ScriptID:  Default=0, CJKIdeographic=0xA, Cyrillic=0xD, Greek=0x14, Latin=0x1F,
        //            Arabic=0x1, Hebrew=0x19, Digit=0x3D, Control=0x3E
        // Flags:     Complex=0x1, RTL=0x2, LineBreak=0x4, FastText=0x10, Ideo=0x20,
        //            Space=0x80, Digit=0x100, ParaBreak=0x200, CRLF=0x400, Letter=0x800
        // BiDi (DirectionClass): Left=0, Right=1, EuropeanNumber=3, NonSpacingMark=8,
        //            BoundaryNeutral=9, ParagraphSeparator=11, WhiteSpace=18, OtherNeutral=19
        // BreakType: NoBreak=0, ControlBreak=1, DigitBreak=2

        private static readonly CharacterAttribute[] s_templates = BuildTemplates();

        private static CharacterAttribute[] BuildTemplates()
        {
            var t = new CharacterAttribute[11];
            // NOTE: CharacterLineBreak (0x4) must ONLY be on actual break characters
            // (CR/LF/NEL/VT/FF/LS/PS -> C_CRLF). Putting it on spaces/controls makes
            // TextStore substitute them with U+2028 markers, which hard-breaks the
            // line after every word in the managed LineServices shim.
            t[C_Control]      = Make(0x3E, 9, 0x0000,             1, 9);   // Control: neutral (incl. tab)
            t[C_CRLF]         = Make(0x3E, 9, 0x0400 | 0x0200 | 0x0004, 1, 11);
            t[C_Space]        = Make(0x1F, 6, 0x0080,             0, 18);
            t[C_Digit]        = Make(0x3D, 0, 0x0100,             2, 3);
            t[C_Latin]        = Make(0x1F, 5, 0x0800,             0, 0);
            t[C_Punct]        = Make(0x00, 6, 0x0000,             0, 19);
            t[C_SimpleLetter] = Make(0x00, 5, 0x0800,             0, 0);
            t[C_Complex]      = Make(0x00, 5, 0x0001 | 0x0800,   0, 0);   // CharacterComplex
            t[C_ComplexRTL]   = Make(0x01, 5, 0x0001 | 0x0002 | 0x0800, 0, 1);
            t[C_Ideographic]  = Make(0x0A, 5, 0x0020 | 0x0800,   0, 0);   // CharacterIdeo
            t[C_Combining]    = Make(0x00, 7, 0x0000,             0, 8);
            return t;
        }

        private static CharacterAttribute Make(byte script, byte itemClass, ushort flags, byte breakType, byte bidi)
        {
            return new CharacterAttribute
            {
                Script = script,
                ItemClass = itemClass,
                Flags = flags,
                BreakType = breakType,
                BiDi = (DirectionClass)bidi,
                LineBreak = 0,
            };
        }

        internal static CharacterAttribute Attr(int charClass)
        {
            if ((uint)charClass < (uint)s_templates.Length)
                return s_templates[charClass];
            return s_templates[C_Punct];
        }

        internal static short GetClass(int cp)
        {
            // Fast ASCII path.
            if (cp < 0x80)
            {
                if (cp == '\r' || cp == '\n') return C_CRLF;
                if (cp == 0x0B || cp == 0x0C) return C_CRLF;    // VT / FF are line breaks
                if (cp < 0x20 || cp == 0x7F) return C_Control;
                if (cp == 0x20) return C_Space;
                if (cp >= '0' && cp <= '9') return C_Digit;
                if ((cp >= 'A' && cp <= 'Z') || (cp >= 'a' && cp <= 'z')) return C_Latin;
                return C_Punct;
            }

            // Common Latin-1 / Latin Extended / punctuation.
            if (cp <= 0x24F)
            {
                if (cp == 0x85) return C_CRLF;                  // NEL is a line break
                if (cp == 0xA0) return C_Space;                 // no-break space
                if (cp == 0xAD) return C_Punct;                 // soft hyphen
                if (cp is 0xD7 or 0xF7) return C_Punct;         // × ÷
                var cat = CharUnicodeInfo.GetUnicodeCategory((char)cp);
                return CategoryToClass(cat);
            }

            // Combining diacritical marks.
            if (cp >= 0x0300 && cp <= 0x036F) return C_Combining;

            // Greek + Coptic, Cyrillic, Armenian, Georgian, Latin Extended Additional: simple LTR.
            if ((cp >= 0x0370 && cp <= 0x058F) || (cp >= 0x10A0 && cp <= 0x10FF) ||
                (cp >= 0x1E00 && cp <= 0x1FFF) || (cp >= 0x2C00 && cp <= 0x2C5F))
                return C_SimpleLetter;

            // Hebrew + Arabic: RTL + shaping.
            if (cp >= 0x0590 && cp <= 0x08FF) return C_ComplexRTL;

            // General punctuation / symbols / currency.
            if ((cp >= 0x2000 && cp <= 0x206F) || (cp >= 0x20A0 && cp <= 0x20CF) ||
                (cp >= 0x2100 && cp <= 0x2BFF))
            {
                if (cp == 0x2028) return C_CRLF;                // line separator
                if (cp == 0x2029) return C_CRLF;                // paragraph separator
                return C_Punct;
            }

            // CJK / Kana / Hangul (ideographic; no per-glyph shaping needed here).
            if ((cp >= 0x2E80 && cp <= 0xA4CF) || (cp >= 0xAC00 && cp <= 0xD7AF) ||
                (cp >= 0xF900 && cp <= 0xFAFF) || (cp >= 0x20000 && cp <= 0x2FA1F))
                return C_Ideographic;

            // Indic, Thai, Lao, Tibetan, Myanmar, Khmer, etc.: complex shaping.
            if (cp >= 0x0900 && cp <= 0x109F) return C_Complex;
            if (cp >= 0x1780 && cp <= 0x17FF) return C_Complex;

            // Everything else: treat as a neutral simple character (never blocks the fast path).
            var gc = cp <= 0xFFFF ? CharUnicodeInfo.GetUnicodeCategory((char)cp) : UnicodeCategory.OtherLetter;
            return CategoryToClass(gc);
        }

        private static short CategoryToClass(UnicodeCategory cat)
        {
            switch (cat)
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter:
                case UnicodeCategory.OtherLetter:
                    return C_Latin;
                case UnicodeCategory.DecimalDigitNumber:
                    return C_Digit;
                case UnicodeCategory.SpaceSeparator:
                    return C_Space;
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.SpacingCombiningMark:
                case UnicodeCategory.EnclosingMark:
                    return C_Combining;
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                    return C_Control;
                default:
                    return C_Punct;
            }
        }
    }
}
