// Where a face the stack stands in with puts its underline: where the Windows face it stands in for puts it.
//
// A menu's mnemonic underline is the face's own ('post' underlinePosition, below the baseline), and two kinds
// of stand-in carry the wrong one:
//
//   Selawik, for Segoe UI: its own sits less than half as far down; Segoe UI's is taken from Segoe UI
//   (StandInVdmx, by the file's 'head' checkSumAdjustment).
//
//   Liberation, for Arial, Times New Roman and Courier New (and for Tahoma and Microsoft Sans Serif, where a
//   system has neither): every Liberation face states its underline exactly one underline thickness higher
//   than the face it clones does -- Liberation Sans -67 (Arial -217,
//   thickness 150), its bold -2 (-217, 215), Serif -123 (Times New Roman -223, 100), Mono -393 (Courier New
//   -477, 84), Sans Narrow likewise. At a menu's eight or nine points that rounded onto the baseline, and
//   the line ran through the bottoms of the letters. Known by its family name, since a Linux box has its own
//   build of Liberation, not the stack's.

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    internal static class StandInUnderline
    {
        /// <summary>Corrects <paramref name="position"/> and <paramref name="thickness"/> (the face's 'post'
        /// values, font units) for a stand-in face: the face at <paramref name="sfnt"/> in <paramref name="data"/>,
        /// its 'head' table at <paramref name="head"/> (negative when it has none).</summary>
        internal static void Apply(byte[] data, int sfnt, int head, ref short position, ref short thickness)
        {
            if (head >= 0 && head + 12 <= data.Length)
            {
                uint key = (uint)(data[head + 8] << 24 | data[head + 9] << 16 | data[head + 10] << 8 | data[head + 11]);
                if (StandInVdmx.Underline(key, out short p, out short t))
                {
                    position = p;
                    thickness = t;
                    return;
                }
            }
            if (thickness > 0 && FontFiles.ReadNames(data, sfnt, out string? family, out _, out _)
                && family is not null && family.StartsWith("Liberation ", System.StringComparison.OrdinalIgnoreCase))
                position = (short)(position - thickness);
        }
    }
}
