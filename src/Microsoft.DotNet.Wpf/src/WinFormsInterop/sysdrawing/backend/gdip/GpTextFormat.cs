// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What a GpStringFormat holds, as the text imagers read it (GpStringFormat::GpStringFormat
// @1800705a8, GenericTypographic @1800707c8, GetPhysicalAlignment @1802412f8):
//
//   +0x18 flags  +0x1c language  +0x20 alignment  +0x24 line alignment  +0x28/+0x2c digit
//   substitution (method, language)  +0x30 first tab offset, +0x38/+0x40 tab stops  +0x44 hotkey
//   prefix  +0x48/+0x4c leading/trailing margin (1/6 em; 0 typographic)  +0x50 tracking (1.03;
//   1 typographic)  +0x54 trimming (Character; None typographic)  +0x58.. measurable ranges
//
// A draw with no format at all (null) takes the same margins and tracking and Character trimming,
// and none of the flags.
//

using System.Drawing.Text;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GpTextFormat
    {
        public const int RightToLeft = 0x1, Vertical = 0x2, NoFitBlackBox = 0x4, DisplayFormatControl = 0x20,
                         NoFontFallback = 0x400, MeasureTrailingSpaces = 0x800, NoWrap = 0x1000, LineLimit = 0x2000,
                         NoClip = 0x4000;

        public int Flags, Align, LineAlign, Trimming = 1, Hotkey, DigitLanguage, DigitMethod = 0;
        public float FirstTab;
        public float[] Tabs = Array.Empty<float> ();
        public float LeadMargin = 1f / 6f, TrailMargin = 1f / 6f, Tracking = 1.03f;
        public CharacterRange[] Ranges = Array.Empty<CharacterRange> ();

        /// <summary>The format a StringFormat stands for (null for no format).</summary>
        public static GpTextFormat From (StringFormat f)
        {
            if (f == null) return null;
            var g = new GpTextFormat {
                Flags = (int) f.FormatFlags,
                Align = (int) f.Alignment,
                LineAlign = (int) f.LineAlignment,
                Trimming = (int) f.Trimming,
                Hotkey = (int) f.HotkeyPrefix,
                DigitLanguage = f.DigitSubstitutionLanguage,
                DigitMethod = (int) f.DigitSubstitutionMethod,
                Ranges = f.MeasurableCharacterRanges ?? Array.Empty<CharacterRange> (),
            };
            g.Tabs = f.GetTabStops (out g.FirstTab) ?? Array.Empty<float> ();
            if (f.IsTypographic) {
                // GenericTypographic: no margins or tracking (its FitBlackBox | LineLimit | NoClip
                // are flags it starts with, which a caller may change).
                g.LeadMargin = g.TrailMargin = 0f;
                g.Tracking = 1f;
            }
            return g;
        }

        /// <summary>GetPhysicalAlignment: a right-to-left horizontal format mirrors near and far.</summary>
        public int PhysicalAlignment
            => (Flags & RightToLeft) == 0 || Align == 1 || (Flags & Vertical) != 0 ? Align : Align == 0 ? 2 : 0;

        public bool IsVertical => (Flags & Vertical) != 0;
        public bool IsRightToLeft => (Flags & RightToLeft) != 0;
    }
}
