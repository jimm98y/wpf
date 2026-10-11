// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GpIcm (the managed port of icm32's colour transform, which ImageAttributes' output channel
// separates through) on output profiles of the kinds icm32 builds differently from RSWOP.icm: an
// XYZ PCS with lut16 / lut8, v4 profiles with a lut16, a lutBtoA ('mBA ') of every curve / matrix /
// CLUT combination, a lutAtoB in B2A0, and profiles mscms refuses. Each profile's KYMC over a
// 33^3 grid of sRGB colours, reduced to a SHA-256 digest; the digests are mscms' own
// (CreateMultiProfileTransform(sRGB, profile, perceptual, 0x20003) + TranslateBitmapBits to
// BM_KYMCQUADS on Windows 11 26100 arm64). The profiles were crafted from RSWOP.icm's B2A0
// (Fixtures/Icm); the port matches mscms on all 16.7M colours for each.
//

#nullable disable

using System;
using System.Collections.Generic;
using System.Drawing.WebGpuBackend.Gdip;
using System.IO;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public sealed class GpIcmTests
    {
        private static readonly Dictionary<string, string> Mscms = new()
        {
            { "xyz16m", "E62199E8596E46FF6D969036291CE29EC8F37EA220AB4D1FACC62E437333AD0E" },
            { "xyz8m", "B4BDB1CF7BCB265AE729144FA33CDA2DA95F80773AF35740DB33FE52359D75EB" },
            { "v4lut16", "E313B1DF363F583D15E9B648A4FD6868315554D6B0E5B84817EB96F955B19DFF" },
            { "v4xyz16", "B928B1DF189439F95C931DB1145FC49EB7F0129AC295F52D6886B5425E7E4EE4" },
            { "v4xyz8m", "9761954A613D21E28C4DDBD136E8DB99B707C0B8D423C0540E42FFF9FEA3754E" },
            { "v4mba8", "0604713352A920D1C7867770A00CC40E27A0B2FFEBAA34ECFD05B67F2195A8AF" },
            { "v4mbaf", "D29D4CAD37F8DC9C1969D90B10287B1D1CBD24994DA9A07FC9E987AD23FC4DC2" },
            { "v4mbam", "30EB932676D92D8B11242BF59D2E8396471A6D77493856BE5AE48BE4778D4B4F" },
            { "v4mbap2", "EF8EB616E14D0598734AFA145C860BC31C5FF1CE33C47A2B85480863D6FF6F6E" },
            { "v4mbap8", "9822477CF183CC0518782FE9CDBC71EF8A019A77AE8393C018A111C64E93FA5D" },
            { "v4mbad", "B626E5F8056E837CFC90AC0811C0FD579924C8496C64634AA4E7B6BEFB406D49" },
            { "v4mbaxm", "8536370307251496215053936C37560193637DA3C59D79166B644F2840BE2D5E" },
            { "v4mabc", "9497DCAEECD6431E19E23B4A6607B89AB852DFDAFEE6FDC4965A22EAC1275777" },
        };

        // CMValidateProfile / ExtractAll_MFT_Luts refuse these: no B2A0 (no fallback), a lutBtoA in
        // a v2 profile, a lutBtoA without its A curves.
        private static readonly string[] Refused = { "nob2a0", "v2mba", "v4mbana" };

        private static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Icm");

        // The source GDI+ separates from: Windows' sRGB profile (the port reads it from the colour
        // directory as mscms does).
        private static byte[] Srgb()
        {
            if (!OperatingSystem.IsWindows()) return null;
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", "sRGB Color Space Profile.icm");
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        }

        private static string KymcDigest(GpIcm t)
        {
            var buf = new byte[33 * 33 * 33 * 4];
            int k = 0;
            for (int r = 0; r < 33; r++)
                for (int g = 0; g < 33; g++)
                    for (int b = 0; b < 33; b++)
                    {
                        uint px = 0xff000000u | (uint)Math.Min(r * 8, 255) << 16 | (uint)Math.Min(g * 8, 255) << 8 | (uint)Math.Min(b * 8, 255);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(k), t.Translate(px));
                        k += 4;
                    }
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(buf));
        }

        [Fact]
        public void Output_profiles_separate_as_mscms_does()
        {
            byte[] srgb = Srgb();
            if (srgb == null) return;
            var bad = new List<string>();
            foreach (KeyValuePair<string, string> kv in Mscms)
            {
                GpIcm t = GpIcm.Create(srgb, File.ReadAllBytes(Path.Combine(Dir, kv.Key + ".icm")));
                string got = KymcDigest(t);
                if (got != kv.Value) bad.Add(kv.Key);
            }
            Assert.True(bad.Count == 0, "differ from mscms: " + string.Join(", ", bad));
        }

        [Fact]
        public void Refused_profiles_are_refused()
        {
            byte[] srgb = Srgb();
            if (srgb == null) return;
            foreach (string name in Refused)
            {
                string path = Path.Combine(Dir, name + ".icm");
                Assert.ThrowsAny<Exception>(() => GpIcm.Create(srgb, File.ReadAllBytes(path)));
                Assert.Null(GpIcm.Setup(path, out int status));
                Assert.Equal(7, status);
                // GDI+ reports Win32Error, which System.Drawing throws as the generic error
                var e = Assert.Throws<System.Runtime.InteropServices.ExternalException>(() => new System.Drawing.Imaging.ImageAttributes().SetOutputChannelColorProfile(path));
                Assert.Equal(unchecked((int)0x80004005), e.HResult);
                Assert.Equal("A generic error occurred in GDI+.", e.Message);
            }
        }
    }
}
