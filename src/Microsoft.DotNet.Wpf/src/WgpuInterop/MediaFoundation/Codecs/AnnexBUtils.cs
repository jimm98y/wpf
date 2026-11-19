// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// From SharpMediaFoundationInterop (MIT, by the same author), adapted for the shim's Media Foundation.
#nullable disable
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs
{
    internal static class AnnexBUtils
    {
        public static readonly byte[] AnnexB = [0, 0, 0, 1];

        /// <summary>Whether a NAL unit already starts with a start code, as <see cref="PrefixNalu"/> tells it.</summary>
        public static bool HasStartCode(System.ReadOnlySpan<byte> nalu)
        {
            return !(nalu.Length < 5 || nalu[0] != 0 || nalu[1] != 0 || nalu[2] != 0 || !(nalu[3] == 1 || nalu[3] == 0 && nalu[4] == 1));
        }

        public static byte[] PrefixNalu(byte[] nalu)
        {
            if (!HasStartCode(nalu))
            {
                // this little maneuver will cost us new allocation
                nalu = AnnexB.Concat(nalu).ToArray();
            }

            return nalu;
        }

        public static IEnumerable<byte[]> ParseNalu(byte[] bytes, uint length)
        {
            long index = 0;

            while (index < length)
            {
                long next = IndexOf(bytes, AnnexB, index + AnnexB.Length, length);
                if (next == -1)
                    next = length;

                yield return bytes.Skip((int)index + AnnexB.Length).Take((int)(next - index - AnnexB.Length)).ToArray();

                index = next;
            }
        }

        private static unsafe long IndexOf(byte[] haystack, byte[] needle, long startOffset, long endOffset)
        {
            fixed (byte* h = haystack)
            fixed (byte* n = needle)
            {
                for (byte* hNext = h + startOffset, hEnd = h + endOffset + 1 - needle.LongLength, nEnd = n + needle.LongLength; hNext < hEnd; hNext++)
                    for (byte* hInc = hNext, nInc = n; *nInc == *hInc; hInc++)
                        if (++nInc == nEnd)
                            return hNext - h;
                return -1;
            }
        }
    }
}
