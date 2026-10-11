// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Bitmap.ConvertFormat, ColorPalette(PaletteType), ColorPalette.CreateOptimalPalette and the decoders'
// palettes and flags, held to REAL GDI+ byte for byte.
//
// ConvertFormat into 1, 4 and 8bpp with every dither type (none, solid, ordered 4x4/8x8/16x16,
// spiral, dual spiral, error diffusion) against no palette, the source's optimal palette (as Custom
// and as Optimal, with and without a transparent entry), every fixed halftone palette and a
// mismatched one, at alpha thresholds from below 0% to above 100%; into every direct format with the
// dithers that matter there (Ordered4x4's 16bpp dither) and with a palette; the one-argument
// overload; Clone and a read lock into every direct format; optimal palettes of 1 to 257 colours; the
// fixed palettes -- from 24/32bpp RGB, ARGB and PARGB, 16bpp 555/565/1555, 48/64bpp and 1/4/8bpp with
// their own palettes, over gradients, noise, photo-like content, alpha ramps, a few flat colours and
// odd sizes. Then files written by hand (BMP indexed/RLE/direct, PNG of every colour type and depth,
// palettes with tRNS, interlaced, TIFF palette/grey both ways round/2-bit/16-bit/associated alpha):
// their decoded format, flags and palette, and conversions of them.
//
// Every result is reduced to its pixel format, size, image flags, raw format, palette and pixel bits
// (never the padding, nor the bits after the last pixel: GDI+ leaves its heap there; nor the flags of
// an EMPTY palette, which System.Drawing.Common reads from a buffer GDI+ never wrote) and hashed per
// source and destination. The fixtures were written by ConvertBattery below compiled against the
// real System.Drawing.Common (net10.0-windows) on Windows 11 26100, gdiplus.dll 10.0.26100.9444
// arm64. GdipHalftone has what GDI+ does.
//

#nullable disable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public sealed class ConvertFormatTests
    {
        // group -> digest, as gdiplus.dll produced them.
        private static readonly Dictionary<string, string> GdiPlus = new()
        {
            { "palette", "14bf62d046794b98" },
            { "32a_k0_37x23>opt", "4ec058d263931dfa" },
            { "32a_k0_37x23>auto", "344433376919bb45" },
            { "32a_k0_37x23>i1", "ad54857a6c665a2f" },
            { "32a_k0_37x23>i4", "e7b1197bbcfef790" },
            { "32a_k0_37x23>i8", "735642f9e65384a1" },
            { "32a_k0_37x23>clone", "72e9376537e4d3a3" },
            { "32a_k0_37x23>lock", "dfb6db6a4d78dedc" },
            { "32a_k0_37x23>555", "20ad51061c75691f" },
            { "32a_k0_37x23>565", "9727c955a7f31dbc" },
            { "32a_k0_37x23>1555", "17697379902bdd2c" },
            { "32a_k0_37x23>g16", "6482917d7d8dd74e" },
            { "32a_k0_37x23>24", "a763606fecfce520" },
            { "32a_k0_37x23>32rgb", "69bb00d58779a277" },
            { "32a_k0_37x23>32a", "ffe50fb1dc27ec5b" },
            { "32a_k0_37x23>32p", "b665475b9aaa939e" },
            { "32a_k0_37x23>48", "0a366e87aabb2a07" },
            { "32a_k0_37x23>64a", "cfdbc2dd7677441e" },
            { "32a_k0_37x23>64p", "96ce4829f8913755" },
            { "32a_k1_37x23>opt", "745c226c22d3f098" },
            { "32a_k1_37x23>auto", "eb64173f02561ca0" },
            { "32a_k1_37x23>i1", "053ddd305d5ff7b8" },
            { "32a_k1_37x23>i4", "2c56f0e524384a58" },
            { "32a_k1_37x23>i8", "f481082b7b21291d" },
            { "32a_k1_37x23>clone", "7d7eea375fe48c71" },
            { "32a_k1_37x23>lock", "8a9c37628a803a68" },
            { "32a_k1_37x23>555", "80b810a2f6f92cfc" },
            { "32a_k1_37x23>565", "f052d2a0e99cee94" },
            { "32a_k1_37x23>1555", "c469552a1fa087b9" },
            { "32a_k1_37x23>g16", "6482917d7d8dd74e" },
            { "32a_k1_37x23>24", "1b9ecb437fb0ee59" },
            { "32a_k1_37x23>32rgb", "740f3b4f41123772" },
            { "32a_k1_37x23>32a", "cd67d9c8ae5216f8" },
            { "32a_k1_37x23>32p", "55b4f6be32603fdc" },
            { "32a_k1_37x23>48", "5e003e6cabe30555" },
            { "32a_k1_37x23>64a", "2e7263b3946cc54c" },
            { "32a_k1_37x23>64p", "b5683cd6d323d8d2" },
            { "32a_k2_37x23>opt", "ba1fd53ece9dd1ae" },
            { "32a_k2_37x23>auto", "0a7fa1d62ea35bc3" },
            { "32a_k2_37x23>i1", "6e1420e44f833fb1" },
            { "32a_k2_37x23>i4", "276a7041b6769e1c" },
            { "32a_k2_37x23>i8", "561e99b1e48700ee" },
            { "32a_k2_37x23>clone", "40032c54355493e3" },
            { "32a_k2_37x23>lock", "808924be6cd39384" },
            { "32a_k2_37x23>555", "c1a68254a832df7e" },
            { "32a_k2_37x23>565", "9022889d4d8bf196" },
            { "32a_k2_37x23>1555", "a949ad19154f21ed" },
            { "32a_k2_37x23>g16", "6482917d7d8dd74e" },
            { "32a_k2_37x23>24", "43ae8215bc885474" },
            { "32a_k2_37x23>32rgb", "cfd438f5478ffffd" },
            { "32a_k2_37x23>32a", "37ded3f049e017e3" },
            { "32a_k2_37x23>32p", "7eb1c401e61664cf" },
            { "32a_k2_37x23>48", "2b60c740788a26d5" },
            { "32a_k2_37x23>64a", "c2d4dad589e3a8aa" },
            { "32a_k2_37x23>64p", "62250be7e3aec9ea" },
            { "32a_k3_37x23>opt", "7d616e49a9caa0ef" },
            { "32a_k3_37x23>auto", "1678230af4dcf5ec" },
            { "32a_k3_37x23>i1", "3f774579034757df" },
            { "32a_k3_37x23>i4", "e1244e986f0589bd" },
            { "32a_k3_37x23>i8", "56b0ced8d0e038ec" },
            { "32a_k3_37x23>clone", "47c74981633f4786" },
            { "32a_k3_37x23>lock", "34269abc44bb8f70" },
            { "32a_k3_37x23>555", "9dc02c30087080b5" },
            { "32a_k3_37x23>565", "9311160768bce17a" },
            { "32a_k3_37x23>1555", "bdf11b395c8bf1bb" },
            { "32a_k3_37x23>g16", "6482917d7d8dd74e" },
            { "32a_k3_37x23>24", "8952d7f20b7621e1" },
            { "32a_k3_37x23>32rgb", "ed15b1dff484a76a" },
            { "32a_k3_37x23>32a", "31c3946b4153e3e3" },
            { "32a_k3_37x23>32p", "3de34b41b87effa3" },
            { "32a_k3_37x23>48", "5ad697596e108e12" },
            { "32a_k3_37x23>64a", "eee530179613fdee" },
            { "32a_k3_37x23>64p", "9030d01b9ab9c0f3" },
            { "32a_k5_37x23>opt", "7e254c1cbf511ed0" },
            { "32a_k5_37x23>auto", "5341d597bc66a45d" },
            { "32a_k5_37x23>i1", "c45867a9e8e2e356" },
            { "32a_k5_37x23>i4", "d20f5a23d8be1eda" },
            { "32a_k5_37x23>i8", "f0597a56bea38489" },
            { "32a_k5_37x23>clone", "106811ad141a49f9" },
            { "32a_k5_37x23>lock", "c65bfade83db1cd9" },
            { "32a_k5_37x23>555", "1be745f183db295c" },
            { "32a_k5_37x23>565", "2de96f98195b7f5a" },
            { "32a_k5_37x23>1555", "a183c400e63db959" },
            { "32a_k5_37x23>g16", "6482917d7d8dd74e" },
            { "32a_k5_37x23>24", "bcb525ce3695202f" },
            { "32a_k5_37x23>32rgb", "4cbeaaa1a40991b1" },
            { "32a_k5_37x23>32a", "70d3910615c8330a" },
            { "32a_k5_37x23>32p", "0a176a939cb4cdd5" },
            { "32a_k5_37x23>48", "6a8bd40fdc5eb856" },
            { "32a_k5_37x23>64a", "fbf25e2617dce885" },
            { "32a_k5_37x23>64p", "c49901164ba73503" },
            { "32p_k1_37x23>opt", "7c20a055aa204efb" },
            { "32p_k1_37x23>auto", "0ae484075fb581d6" },
            { "32p_k1_37x23>i1", "76cfee9eb5a3e4cf" },
            { "32p_k1_37x23>i4", "833d669fdf71bf1d" },
            { "32p_k1_37x23>i8", "04b5f36a626ed351" },
            { "32p_k1_37x23>clone", "5c5681857cad9446" },
            { "32p_k1_37x23>lock", "8d2edea866941538" },
            { "32p_k1_37x23>555", "ec3ec92cb906d3de" },
            { "32p_k1_37x23>565", "733d63eabf3728cc" },
            { "32p_k1_37x23>1555", "477c89239cbacc5c" },
            { "32p_k1_37x23>g16", "6482917d7d8dd74e" },
            { "32p_k1_37x23>24", "94aa30456b953db6" },
            { "32p_k1_37x23>32rgb", "c3ec24c692057ca7" },
            { "32p_k1_37x23>32a", "01936e80e576ace1" },
            { "32p_k1_37x23>32p", "5413c3d052a80300" },
            { "32p_k1_37x23>48", "0e385b10ac9180a2" },
            { "32p_k1_37x23>64a", "069d467cf8159246" },
            { "32p_k1_37x23>64p", "250ad0933c916184" },
            { "24_k2_37x23>opt", "965fb67ee030dae7" },
            { "24_k2_37x23>auto", "240be31519235e5c" },
            { "24_k2_37x23>i1", "f98c16dc2058ef32" },
            { "24_k2_37x23>i4", "a77e0a6ee9964286" },
            { "24_k2_37x23>i8", "6dca5d9dc8686342" },
            { "24_k2_37x23>clone", "4ae73b1d720e29bb" },
            { "24_k2_37x23>lock", "9a3ccb80b7d977f9" },
            { "24_k2_37x23>555", "05b005fe67dd35be" },
            { "24_k2_37x23>565", "36ca11a59b396b51" },
            { "24_k2_37x23>1555", "771eacb3095ed616" },
            { "24_k2_37x23>g16", "6482917d7d8dd74e" },
            { "24_k2_37x23>24", "369d20c08f252fe4" },
            { "24_k2_37x23>32rgb", "3eb0eabfb71d9941" },
            { "24_k2_37x23>32a", "df9c4bec2e841cb0" },
            { "24_k2_37x23>32p", "44afd5a9de91c017" },
            { "24_k2_37x23>48", "8558bbea93302fc4" },
            { "24_k2_37x23>64a", "fdd78381233061f4" },
            { "24_k2_37x23>64p", "a9761f722e9466a7" },
            { "24_k4_37x23>opt", "25d9c332982f6e14" },
            { "24_k4_37x23>auto", "6e4362a242c696c3" },
            { "24_k4_37x23>i1", "5c6d22ea3a00a5a3" },
            { "24_k4_37x23>i4", "5ac30b9e197851e5" },
            { "24_k4_37x23>i8", "b6634630eb1a81e2" },
            { "24_k4_37x23>clone", "5400c22772cfd688" },
            { "24_k4_37x23>lock", "09301924708b100a" },
            { "24_k4_37x23>555", "185b1b3195710828" },
            { "24_k4_37x23>565", "410a47319aa6c39d" },
            { "24_k4_37x23>1555", "b7feb226a1dee3c0" },
            { "24_k4_37x23>g16", "6482917d7d8dd74e" },
            { "24_k4_37x23>24", "9231178bccd73825" },
            { "24_k4_37x23>32rgb", "457c937dcb3c8bcc" },
            { "24_k4_37x23>32a", "bb41b426b4a79698" },
            { "24_k4_37x23>32p", "735727436248d8e5" },
            { "24_k4_37x23>48", "6ef85cab7da4a90a" },
            { "24_k4_37x23>64a", "617a610150636b41" },
            { "24_k4_37x23>64p", "a9db523e2a13f89c" },
            { "32rgb_k1_37x23>opt", "31616baeac7c02db" },
            { "32rgb_k1_37x23>auto", "5c10465837cb1b45" },
            { "32rgb_k1_37x23>i1", "0afb658fbd84df5e" },
            { "32rgb_k1_37x23>i4", "d6b1473c6221b73e" },
            { "32rgb_k1_37x23>i8", "61944276f83cfc7c" },
            { "32rgb_k1_37x23>clone", "21beff55c9c6e473" },
            { "32rgb_k1_37x23>lock", "2ae0d9eab8a2ae33" },
            { "32rgb_k1_37x23>555", "f3cf940b4b4f6f96" },
            { "32rgb_k1_37x23>565", "5280b810a926040d" },
            { "32rgb_k1_37x23>1555", "2b43933ad0554f33" },
            { "32rgb_k1_37x23>g16", "6482917d7d8dd74e" },
            { "32rgb_k1_37x23>24", "11cda340e31d539e" },
            { "32rgb_k1_37x23>32rgb", "b5de851c638389fa" },
            { "32rgb_k1_37x23>32a", "eafd7c88e4c800d9" },
            { "32rgb_k1_37x23>32p", "4339338a81d556b3" },
            { "32rgb_k1_37x23>48", "166f5c8600f0d488" },
            { "32rgb_k1_37x23>64a", "e1927e232394686e" },
            { "32rgb_k1_37x23>64p", "6391b5e2ecf2f9dc" },
            { "555_k2_37x23>opt", "344770fc810bc9f5" },
            { "555_k2_37x23>auto", "8229c9129c75794a" },
            { "555_k2_37x23>i1", "61f8527ef25a1fef" },
            { "555_k2_37x23>i4", "aafbd676703be639" },
            { "555_k2_37x23>i8", "9b460e42374fbaf4" },
            { "555_k2_37x23>clone", "9a252f55d2686d37" },
            { "555_k2_37x23>lock", "ad43ceef3b083f2d" },
            { "555_k2_37x23>555", "0bac2b4d7d616b58" },
            { "555_k2_37x23>565", "9111e9ba4cdf4449" },
            { "555_k2_37x23>1555", "d75086e0481d6863" },
            { "555_k2_37x23>g16", "6482917d7d8dd74e" },
            { "555_k2_37x23>24", "d1e3be76ee487c62" },
            { "555_k2_37x23>32rgb", "bc66d2c282555b4a" },
            { "555_k2_37x23>32a", "a5344923ff1cc51a" },
            { "555_k2_37x23>32p", "607084f67bfaca1e" },
            { "555_k2_37x23>48", "1a394da2b34015f5" },
            { "555_k2_37x23>64a", "a475b90b94f4386b" },
            { "555_k2_37x23>64p", "6c1311eb0b3b96a3" },
            { "565_k0_37x23>opt", "4ec058d263931dfa" },
            { "565_k0_37x23>auto", "87ac1bf4d307e410" },
            { "565_k0_37x23>i1", "b94af173bdcc193d" },
            { "565_k0_37x23>i4", "1f08aa556dbab666" },
            { "565_k0_37x23>i8", "958cb7d1ff263adb" },
            { "565_k0_37x23>clone", "f56a6660e393c09e" },
            { "565_k0_37x23>lock", "1260a50b59dd8393" },
            { "565_k0_37x23>555", "9289a891b4fd52e2" },
            { "565_k0_37x23>565", "304ba8569f04b869" },
            { "565_k0_37x23>1555", "8420f6719be80de0" },
            { "565_k0_37x23>g16", "6482917d7d8dd74e" },
            { "565_k0_37x23>24", "675d9d3f6ab50162" },
            { "565_k0_37x23>32rgb", "2fee0b9ee804ae1b" },
            { "565_k0_37x23>32a", "103d5d7c106bcf16" },
            { "565_k0_37x23>32p", "52a6e37c19aa07f2" },
            { "565_k0_37x23>48", "9f076bf8af5baf89" },
            { "565_k0_37x23>64a", "ba6a5c5cd336fe0f" },
            { "565_k0_37x23>64p", "3972adab2f11d17e" },
            { "1555_k3_37x23>opt", "a7d37dfc3f389dd7" },
            { "1555_k3_37x23>auto", "36dce2cfe3252978" },
            { "1555_k3_37x23>i1", "985b214156ef58de" },
            { "1555_k3_37x23>i4", "94488b677c084842" },
            { "1555_k3_37x23>i8", "fb81babd3c04a52c" },
            { "1555_k3_37x23>clone", "4510d4204a2c999e" },
            { "1555_k3_37x23>lock", "d96be7089c376c04" },
            { "1555_k3_37x23>555", "2144f33b5bf4ed03" },
            { "1555_k3_37x23>565", "40e3e253bf520f82" },
            { "1555_k3_37x23>1555", "164188475c7e7f7f" },
            { "1555_k3_37x23>g16", "6482917d7d8dd74e" },
            { "1555_k3_37x23>24", "5252b20c6d914f81" },
            { "1555_k3_37x23>32rgb", "502411acfa5e370b" },
            { "1555_k3_37x23>32a", "f0e2c489bb0a1195" },
            { "1555_k3_37x23>32p", "931dc3a3197e9539" },
            { "1555_k3_37x23>48", "7e02c1f413b59548" },
            { "1555_k3_37x23>64a", "cb71b4ebfbfe3b7a" },
            { "1555_k3_37x23>64p", "da97671a253f6519" },
            { "48_k2_37x23>opt", "78836da5ecee3171" },
            { "48_k2_37x23>auto", "2b40d4fc1b30d824" },
            { "48_k2_37x23>i1", "bcc5e0b87623c31b" },
            { "48_k2_37x23>i4", "81e227d118a53b30" },
            { "48_k2_37x23>i8", "c2222b549128f9f0" },
            { "48_k2_37x23>clone", "23829834d502d7da" },
            { "48_k2_37x23>lock", "eb19901718f1b5bb" },
            { "48_k2_37x23>555", "dc3b714491385d77" },
            { "48_k2_37x23>565", "53509d4281f79c70" },
            { "48_k2_37x23>1555", "9a96b3ba221e0f51" },
            { "48_k2_37x23>g16", "6482917d7d8dd74e" },
            { "48_k2_37x23>24", "f7315150e00a9c52" },
            { "48_k2_37x23>32rgb", "b76c36b9c9b79272" },
            { "48_k2_37x23>32a", "2fb8bd08d6136ad8" },
            { "48_k2_37x23>32p", "9956b30df2ec7785" },
            { "48_k2_37x23>48", "ce872b3e29dba60a" },
            { "48_k2_37x23>64a", "009916bacd591f49" },
            { "48_k2_37x23>64p", "d7b7a2db6e615631" },
            { "64a_k3_37x23>opt", "5370cbe18a585656" },
            { "64a_k3_37x23>auto", "94d33764575a5c0b" },
            { "64a_k3_37x23>i1", "c3879bca6ba5855d" },
            { "64a_k3_37x23>i4", "18fe2833760113fb" },
            { "64a_k3_37x23>i8", "e7a008333e9a3fdd" },
            { "64a_k3_37x23>clone", "d4b86c76184801f4" },
            { "64a_k3_37x23>lock", "2a52aac64857ea08" },
            { "64a_k3_37x23>555", "4fb94d92f21626c5" },
            { "64a_k3_37x23>565", "d86aea0d1ff55a74" },
            { "64a_k3_37x23>1555", "340dc994166a9c18" },
            { "64a_k3_37x23>g16", "6482917d7d8dd74e" },
            { "64a_k3_37x23>24", "01042999d32e1915" },
            { "64a_k3_37x23>32rgb", "008eaa0eff9b2fff" },
            { "64a_k3_37x23>32a", "dce5ba0c1bf16191" },
            { "64a_k3_37x23>32p", "93355e212c6bbe4c" },
            { "64a_k3_37x23>48", "7318baf4938770d5" },
            { "64a_k3_37x23>64a", "71b25d9c491da8f4" },
            { "64a_k3_37x23>64p", "95ca0838ee796a9d" },
            { "64p_k1_37x23>opt", "87257fef48432900" },
            { "64p_k1_37x23>auto", "d7d9a0b7cc8aecf9" },
            { "64p_k1_37x23>i1", "cce3d091ca9f99bc" },
            { "64p_k1_37x23>i4", "c6243e567f69f3ab" },
            { "64p_k1_37x23>i8", "76c92e542a2407b6" },
            { "64p_k1_37x23>clone", "25141c12ad410ecf" },
            { "64p_k1_37x23>lock", "d47508704f4bf2d2" },
            { "64p_k1_37x23>555", "c8a482eea0181fa7" },
            { "64p_k1_37x23>565", "5b5f20921d8b8ff8" },
            { "64p_k1_37x23>1555", "520ce27d976a90b5" },
            { "64p_k1_37x23>g16", "6482917d7d8dd74e" },
            { "64p_k1_37x23>24", "de9ada5fa46d99e4" },
            { "64p_k1_37x23>32rgb", "05371f67b3bfa575" },
            { "64p_k1_37x23>32a", "b656c17df894f7ea" },
            { "64p_k1_37x23>32p", "9a688dc69f001003" },
            { "64p_k1_37x23>48", "3f43e50a9d6fab96" },
            { "64p_k1_37x23>64a", "a0cd268fd7e50a1a" },
            { "64p_k1_37x23>64p", "1b33a1df208ca935" },
            { "i1_k0_p1_37x23>opt", "1ad32f9aa2a6b002" },
            { "i1_k0_p1_37x23>auto", "7806d3aa1260e281" },
            { "i1_k0_p1_37x23>i1", "449a126f686c36d3" },
            { "i1_k0_p1_37x23>i4", "c7342155be97e16f" },
            { "i1_k0_p1_37x23>i8", "4a231df4344d5c10" },
            { "i1_k0_p1_37x23>clone", "9a48fd2a3824f784" },
            { "i1_k0_p1_37x23>lock", "da9918dd662e30bc" },
            { "i1_k0_p1_37x23>555", "519476177d9ef93d" },
            { "i1_k0_p1_37x23>565", "fa6fee76df016953" },
            { "i1_k0_p1_37x23>1555", "09e8498a1e14eafa" },
            { "i1_k0_p1_37x23>g16", "6482917d7d8dd74e" },
            { "i1_k0_p1_37x23>24", "b96f73c7990e7e4f" },
            { "i1_k0_p1_37x23>32rgb", "a054dc7411f06323" },
            { "i1_k0_p1_37x23>32a", "6115b2ae53505871" },
            { "i1_k0_p1_37x23>32p", "6918cfb99762f457" },
            { "i1_k0_p1_37x23>48", "590aee4be3823644" },
            { "i1_k0_p1_37x23>64a", "38124aefe5ac2d25" },
            { "i1_k0_p1_37x23>64p", "0d4c9c7ef6357e56" },
            { "i4_k1_p0_37x23>opt", "ee9a7ae612b0820a" },
            { "i4_k1_p0_37x23>auto", "808e308c12f3e6a4" },
            { "i4_k1_p0_37x23>i1", "b81a8dd7ebb19f52" },
            { "i4_k1_p0_37x23>i4", "53d483fc7ffc66fa" },
            { "i4_k1_p0_37x23>i8", "55f3f147bb189026" },
            { "i4_k1_p0_37x23>clone", "b54a1d20e5675ea9" },
            { "i4_k1_p0_37x23>lock", "873a51ab6164d822" },
            { "i4_k1_p0_37x23>555", "7b5fbe1d93f3d4d9" },
            { "i4_k1_p0_37x23>565", "3a04470e4e8f4ad1" },
            { "i4_k1_p0_37x23>1555", "89a50a920ec2420a" },
            { "i4_k1_p0_37x23>g16", "6482917d7d8dd74e" },
            { "i4_k1_p0_37x23>24", "34642a000e11fd96" },
            { "i4_k1_p0_37x23>32rgb", "677d9ea24a52b11b" },
            { "i4_k1_p0_37x23>32a", "2eedc3be58ff9212" },
            { "i4_k1_p0_37x23>32p", "6b8384ed9d95d613" },
            { "i4_k1_p0_37x23>48", "90e1a98d66818d06" },
            { "i4_k1_p0_37x23>64a", "8fb5c3ead6818a2d" },
            { "i4_k1_p0_37x23>64p", "b1edb339d446d5e7" },
            { "i4_k1_p2_37x23>opt", "281a401ca33d9d55" },
            { "i4_k1_p2_37x23>auto", "45cc7d5a5ddb715c" },
            { "i4_k1_p2_37x23>i1", "55b054a1e1358aaf" },
            { "i4_k1_p2_37x23>i4", "c268f85d3a275ffa" },
            { "i4_k1_p2_37x23>i8", "c7088e579739904b" },
            { "i4_k1_p2_37x23>clone", "e85bafb3f81d9756" },
            { "i4_k1_p2_37x23>lock", "34802c1969c30895" },
            { "i4_k1_p2_37x23>555", "873f34bb5de14edb" },
            { "i4_k1_p2_37x23>565", "5b37e844eff70a13" },
            { "i4_k1_p2_37x23>1555", "d7372468b8342a2e" },
            { "i4_k1_p2_37x23>g16", "6482917d7d8dd74e" },
            { "i4_k1_p2_37x23>24", "e242fb4cc81ac72e" },
            { "i4_k1_p2_37x23>32rgb", "f929d467d555ac8d" },
            { "i4_k1_p2_37x23>32a", "ed14430fd431d599" },
            { "i4_k1_p2_37x23>32p", "af5429a3c7627083" },
            { "i4_k1_p2_37x23>48", "c856b1c185cd0e82" },
            { "i4_k1_p2_37x23>64a", "7c5dd5806c16ff9e" },
            { "i4_k1_p2_37x23>64p", "531cf2f9145065c1" },
            { "i8_k1_p0_37x23>opt", "fa4cc653a02b8c67" },
            { "i8_k1_p0_37x23>auto", "4bd892dcc9de0b93" },
            { "i8_k1_p0_37x23>i1", "bd272570e0746bf0" },
            { "i8_k1_p0_37x23>i4", "95178321fe9d073e" },
            { "i8_k1_p0_37x23>i8", "0780a05a30096ebe" },
            { "i8_k1_p0_37x23>clone", "9ffb521240084423" },
            { "i8_k1_p0_37x23>lock", "c348022577ff0444" },
            { "i8_k1_p0_37x23>555", "b4d00363418d4bd2" },
            { "i8_k1_p0_37x23>565", "786c8f10fe2c5d7e" },
            { "i8_k1_p0_37x23>1555", "531583694a2b5e3a" },
            { "i8_k1_p0_37x23>g16", "6482917d7d8dd74e" },
            { "i8_k1_p0_37x23>24", "f200fcb5e18ce968" },
            { "i8_k1_p0_37x23>32rgb", "282159ac5a591499" },
            { "i8_k1_p0_37x23>32a", "326858c32062e00b" },
            { "i8_k1_p0_37x23>32p", "cf26f965224b37a9" },
            { "i8_k1_p0_37x23>48", "ced2d8c92bd1a789" },
            { "i8_k1_p0_37x23>64a", "5a54ea9b77bbb706" },
            { "i8_k1_p0_37x23>64p", "cbb9e0aad7f4c7b5" },
            { "i8_k0_p3_37x23>opt", "5c583a6eccb6c47b" },
            { "i8_k0_p3_37x23>auto", "4f7754b2e40eb4b3" },
            { "i8_k0_p3_37x23>i1", "f1f3c93064fd80cd" },
            { "i8_k0_p3_37x23>i4", "d96d87840ae8dd90" },
            { "i8_k0_p3_37x23>i8", "3c831260949452ce" },
            { "i8_k0_p3_37x23>clone", "7ba6c86ddb48b841" },
            { "i8_k0_p3_37x23>lock", "05efbaa4914449dc" },
            { "i8_k0_p3_37x23>555", "08d49a7058ef171c" },
            { "i8_k0_p3_37x23>565", "2fdefd75d9e67e9a" },
            { "i8_k0_p3_37x23>1555", "fe59142c970bb5fa" },
            { "i8_k0_p3_37x23>g16", "6482917d7d8dd74e" },
            { "i8_k0_p3_37x23>24", "4b0a2f5f6732480c" },
            { "i8_k0_p3_37x23>32rgb", "0b16dc50d721ed65" },
            { "i8_k0_p3_37x23>32a", "cc245621fe64166c" },
            { "i8_k0_p3_37x23>32p", "ba1144ead8e4dee7" },
            { "i8_k0_p3_37x23>48", "d8eba57716a00760" },
            { "i8_k0_p3_37x23>64a", "17477259f0cf1df4" },
            { "i8_k0_p3_37x23>64p", "b8b46128e51bd68d" },
            { "i8_k1_p4_37x23>opt", "ee87595131f851ae" },
            { "i8_k1_p4_37x23>auto", "f7d25bd4fe19d86d" },
            { "i8_k1_p4_37x23>i1", "fe82d02f7bec4b1b" },
            { "i8_k1_p4_37x23>i4", "0294b5a1d143dc32" },
            { "i8_k1_p4_37x23>i8", "e235b4a269a2e567" },
            { "i8_k1_p4_37x23>clone", "84d055d500118b30" },
            { "i8_k1_p4_37x23>lock", "dff347e1cc6d2def" },
            { "i8_k1_p4_37x23>555", "c1b60a765b4f985c" },
            { "i8_k1_p4_37x23>565", "49c8fac569f73072" },
            { "i8_k1_p4_37x23>1555", "f37bb4d5a6812e5f" },
            { "i8_k1_p4_37x23>g16", "6482917d7d8dd74e" },
            { "i8_k1_p4_37x23>24", "8bde16935d421901" },
            { "i8_k1_p4_37x23>32rgb", "3a63046b73b7cf5e" },
            { "i8_k1_p4_37x23>32a", "da919260bd180650" },
            { "i8_k1_p4_37x23>32p", "b9ee08208465467e" },
            { "i8_k1_p4_37x23>48", "c89de9b845184be3" },
            { "i8_k1_p4_37x23>64a", "93e7a193cc4e5429" },
            { "i8_k1_p4_37x23>64p", "5db92fccaed3a559" },
            { "32a_k1_13x5>opt", "68feca9a5de1ba89" },
            { "32a_k1_13x5>auto", "9bfdc75c03f51b47" },
            { "32a_k1_13x5>i1", "7fc04178b0b21b46" },
            { "32a_k1_13x5>i4", "0ef9a33330e38e38" },
            { "32a_k1_13x5>i8", "30f9e101eadc474e" },
            { "32a_k1_13x5>clone", "46e6b1419522273a" },
            { "32a_k1_13x5>lock", "85b40eac005d892c" },
            { "32a_k1_13x5>555", "7714ffff8e91536b" },
            { "32a_k1_13x5>565", "26731d408cd51eb1" },
            { "32a_k1_13x5>1555", "e4196ae3fe33b998" },
            { "32a_k1_13x5>g16", "6482917d7d8dd74e" },
            { "32a_k1_13x5>24", "0b59293e5b4e2588" },
            { "32a_k1_13x5>32rgb", "3ccf4463d5446786" },
            { "32a_k1_13x5>32a", "aebb6c2374aa745c" },
            { "32a_k1_13x5>32p", "1caf975950e8e220" },
            { "32a_k1_13x5>48", "c2038ae1ffc9e83c" },
            { "32a_k1_13x5>64a", "a86cbbf43c502bc4" },
            { "32a_k1_13x5>64p", "2ff6ec3b04fffb35" },
            { "32a_k1_1x3>opt", "990e656fd5f6968a" },
            { "32a_k1_1x3>auto", "752072fa090c899a" },
            { "32a_k1_1x3>i1", "55eb24963332b8f4" },
            { "32a_k1_1x3>i4", "d5ea13d8361a7673" },
            { "32a_k1_1x3>i8", "c3af1f4982836097" },
            { "32a_k1_1x3>clone", "5ec228f06a148c81" },
            { "32a_k1_1x3>lock", "abf39f8a3e3f5778" },
            { "32a_k1_1x3>555", "68e21e80a6947db7" },
            { "32a_k1_1x3>565", "8a93fa6e9e7ee6fb" },
            { "32a_k1_1x3>1555", "d8f669d6b52fa456" },
            { "32a_k1_1x3>g16", "6482917d7d8dd74e" },
            { "32a_k1_1x3>24", "eec5d6c84bb2da97" },
            { "32a_k1_1x3>32rgb", "3814b7219df23886" },
            { "32a_k1_1x3>32a", "70b479d89ef86a16" },
            { "32a_k1_1x3>32p", "208433b21c9ef19f" },
            { "32a_k1_1x3>48", "a63f24e5859591a0" },
            { "32a_k1_1x3>64a", "41582d809aeaf75c" },
            { "32a_k1_1x3>64p", "d441593f0758da54" },
            { "24_k2_64x48>opt", "74468c73bd217570" },
            { "24_k2_64x48>auto", "4a5aee984ebdb5ea" },
            { "24_k2_64x48>i1", "a400ab00558ab8ad" },
            { "24_k2_64x48>i4", "ec73cbb2767973ac" },
            { "24_k2_64x48>i8", "fcbf58ecf88fe7a8" },
            { "24_k2_64x48>clone", "e61bd969ce9a403d" },
            { "24_k2_64x48>lock", "e4f84a25fa0b246b" },
            { "24_k2_64x48>555", "a39773703497be11" },
            { "24_k2_64x48>565", "a166ad8ebec567e8" },
            { "24_k2_64x48>1555", "c1bcb52656a3046e" },
            { "24_k2_64x48>g16", "6482917d7d8dd74e" },
            { "24_k2_64x48>24", "e2b2d809d9cd0fc6" },
            { "24_k2_64x48>32rgb", "7fd2d46cbce75d96" },
            { "24_k2_64x48>32a", "f06fc8fbb17bbc6b" },
            { "24_k2_64x48>32p", "cda77b91cf393b71" },
            { "24_k2_64x48>48", "e4b007cf4675bfb9" },
            { "24_k2_64x48>64a", "60aa77665c1ef79b" },
            { "24_k2_64x48>64p", "4addfd035d6df22c" },
            { "24_k0_96x80>opt", "9e5078b969bb9783" },
            { "24_k0_96x80>auto", "479d8c63b4b30905" },
            { "24_k0_96x80>i1", "210bf45d1e2a872e" },
            { "24_k0_96x80>i4", "469e11801c5978b0" },
            { "24_k0_96x80>i8", "5785248a9ae2f496" },
            { "24_k0_96x80>clone", "71dc7df0ecd58586" },
            { "24_k0_96x80>lock", "cee7e7abab759083" },
            { "24_k0_96x80>555", "05389c7d2813563b" },
            { "24_k0_96x80>565", "206f8759dd7836d2" },
            { "24_k0_96x80>1555", "58c8a83c45008d3c" },
            { "24_k0_96x80>g16", "6482917d7d8dd74e" },
            { "24_k0_96x80>24", "5e4a9ad44091124c" },
            { "24_k0_96x80>32rgb", "25920830036c9e5d" },
            { "24_k0_96x80>32a", "0b013552fe337fbb" },
            { "24_k0_96x80>32p", "6299c6f757785729" },
            { "24_k0_96x80>48", "c3acd3d9adef1ac0" },
            { "24_k0_96x80>64a", "1cecb16ca90f24fd" },
            { "24_k0_96x80>64p", "9fa1135e5e93dbcc" },
            { "file_bmp1_bw", "dd5f63bce6c63d68" },
            { "file_bmp1_wb", "558c09f1260bb7cb" },
            { "file_bmp1_col", "98faedea59d6fcf6" },
            { "file_bmp1_grey", "d3bb523dca91bc1b" },
            { "file_bmp4_grey", "b5b2190e3e7cd04e" },
            { "file_bmp4_col", "7f2c3f8bdaee1ae2" },
            { "file_bmp4_res", "724165342245724c" },
            { "file_bmp4_short", "da309c377ba34f19" },
            { "file_bmp8_grey", "e1fbb6fd95067f23" },
            { "file_bmp8_col", "338992de4cbfa90e" },
            { "file_bmp8_res", "ec0ee859646a1bd9" },
            { "file_bmp8_short", "48e4a4f8c4e45355" },
            { "file_bmp8_grey2", "5607651fe86a037b" },
            { "file_bmp24", "b8f1540edc85f341" },
            { "file_bmp32", "6fdfd6e7359c7ace" },
            { "file_bmp32a", "10d3350573c77fd1" },
            { "file_bmp16", "4e2330b4ba45a9c5" },
            { "file_bmp_rle8", "b95d294c7d1f6957" },
            { "file_bmp_rle8grey", "409f5f42c66e17da" },
            { "file_bmp_rle4", "79cf2c5be77c5097" },
            { "file_png1_pgrey", "80c5badb9922d3f0" },
            { "file_png1_pcol", "3b230e26d1d9b140" },
            { "file_png1_pshort", "37d1cd5212467bc5" },
            { "file_png1_ptrns", "79bf458940fd82ae" },
            { "file_png1_ptrnsff", "37c175ffacac50e7" },
            { "file_png1_pgreytrns", "7423e605fc997d44" },
            { "file_png1_grey", "775990feffbd3b5d" },
            { "file_png1_greytrns", "b81100714b409072" },
            { "file_png2_pgrey", "956c0c24646131b3" },
            { "file_png2_pcol", "9945874be689ba55" },
            { "file_png2_pshort", "14c7dff665fc8025" },
            { "file_png2_ptrns", "f0abada89f24fd6f" },
            { "file_png2_ptrnsff", "00c6166a83542c2b" },
            { "file_png2_pgreytrns", "ecb31bdc799d5e0b" },
            { "file_png2_grey", "ffb9db3b3b9a4a99" },
            { "file_png2_greytrns", "d1cdbb309267ef53" },
            { "file_png4_pgrey", "b901267faf0825a0" },
            { "file_png4_pcol", "85ab82eee5efc22c" },
            { "file_png4_pshort", "692da037d7e8e49f" },
            { "file_png4_ptrns", "66504383a383ada2" },
            { "file_png4_ptrnsff", "7dd02aadff796fd5" },
            { "file_png4_pgreytrns", "04a1ca265a224ada" },
            { "file_png4_grey", "387a70ec60dfee32" },
            { "file_png4_greytrns", "1f1dcf0a0f14abc1" },
            { "file_png8_pgrey", "05eea83655a2137c" },
            { "file_png8_pcol", "b6caa595554e356d" },
            { "file_png8_pshort", "727e8b20cd2f9f97" },
            { "file_png8_ptrns", "e6f1f479673948e7" },
            { "file_png8_ptrnsff", "9f5985eb57ec4698" },
            { "file_png8_pgreytrns", "18997ddec8eab376" },
            { "file_png8_grey", "5476d960bace2111" },
            { "file_png8_greytrns", "02eaddacb563c6c3" },
            { "file_png16_grey", "3c4a903f408e2562" },
            { "file_png8_rgb", "d76e55106e324dee" },
            { "file_png8_rgba", "487f3aff55ac9273" },
            { "file_png8_ga", "cc4716d3f45464c3" },
            { "file_png16_rgb", "3a705dca6801cf53" },
            { "file_png16_rgba", "e73cd7498eac71ca" },
            { "file_png16_ga", "55b5985e6d9488b0" },
            { "file_png1_grey_i", "71ed282e5de80c0b" },
            { "file_png4_pgrey_i", "a9ae4c6cde975833" },
            { "file_png8_pcol_i", "0f77d592dc0d9312" },
            { "file_png8_ptrns_i", "31b290a4b9d25a38" },
            { "file_png8_grey_i", "25dd802b22339f37" },
            { "file_png1_pbw", "348a849a18242c84" },
            { "file_png8_pgrey3", "14593d392cba5a68" },
            { "file_tif1_pcol", "e05cad86821ae05b" },
            { "file_tif1_pgrey", "2bb88b5ec1ec1005" },
            { "file_tif1_minblack", "8cd491779780431f" },
            { "file_tif1_minwhite", "0abea8d535096a44" },
            { "file_tif2_pcol", "b247bcf777d8911e" },
            { "file_tif2_pgrey", "73ab4d8f6ecdf9c9" },
            { "file_tif2_minblack", "6a4d88fb489c8cf1" },
            { "file_tif2_minwhite", "ec9316f7e6e48145" },
            { "file_tif4_pcol", "cdef402841909e51" },
            { "file_tif4_pgrey", "50ffc95377c78d2e" },
            { "file_tif4_minblack", "23f2a6965035007e" },
            { "file_tif4_minwhite", "43ea9249a2ad65f4" },
            { "file_tif8_pcol", "cb47e5a3b535085f" },
            { "file_tif8_pgrey", "0dd64ad6a228bdab" },
            { "file_tif8_minblack", "077a5992948385b5" },
            { "file_tif8_minwhite", "eba7092bf375906d" },
            { "file_tif8_rgb", "44e482cc42748271" },
            { "file_tif8_rgba", "f71c392d00e82667" },
            { "file_tif8_rgbx", "e6b53b29fc808c96" },
            { "file_tif8_rgbpa", "aa7deb83533e0f2b" },
            { "file_tif16_grey", "8e706230129956c3" },
            { "file_tif8_greya", "9dee782371c4c1ac" },
            { "file_tif8_greypa", "9dee782371c4c1ac" },
            { "file_tif8_pgrey3", "490c65bd13e8b1a3" },
            { "file_tif4_pbw", "5f084634f75b34e8" },
            { "file_tif1_pcol2", "c6e10e2b917cca50" },
        };

        [Fact]
        public void ConvertFormatPalettesAndDecoderFlags_AreGdiPlusBytes()
        {
            var bad = new List<string>();
            int n = 0;
            foreach (KeyValuePair<string, string> kv in ConvertBattery.Digests())
            {
                n++;
                if (!GdiPlus.TryGetValue(kv.Key, out string want)) bad.Add(kv.Key + ": no fixture");
                else if (want != kv.Value) bad.Add(kv.Key);
            }
            Assert.Equal(GdiPlus.Count, n);
            Assert.True(bad.Count == 0, bad.Count + " of " + n + " differ from GDI+: " + string.Join(", ", bad));
        }
    }

    internal static class ConvertBattery
    {
        static uint s_seed;
        static uint Rnd () { s_seed = s_seed * 1103515245u + 12345u; return s_seed >> 8; }
        static int Clamp (int v) { return v < 0 ? 0 : v > 255 ? 255 : v; }

        // 0 gradient, 1 noise with random alpha, 2 photo-like (smooth bands plus grain), 3 alpha ramp
        // with holes, 4 grey ramp, 5 a few flat colours. Integer arithmetic only.
        static void Pixel (int kind, int x, int y, int w, int h, out int a, out int r, out int g, out int b)
        {
            a = 255;
            switch (kind) {
            case 0: r = x * 255 / Math.Max (1, w - 1); g = y * 255 / Math.Max (1, h - 1); b = (x + y) * 255 / Math.Max (1, w + h - 2); break;
            case 1: { uint v = Rnd (); r = (int) (v & 255); g = (int) ((v >> 8) & 255); b = (int) ((v >> 16) & 255); a = (int) (Rnd () & 255); break; }
            case 2: {
                int t = (x * 41 + y * 23) % 512, u = (x * 13 + y * y * 3) % 400;
                r = Clamp ((t < 256 ? t : 511 - t) - 10 + (int) (Rnd () % 9));
                g = Clamp (60 + (u < 200 ? u : 399 - u) - 4 + (int) (Rnd () % 9));
                b = Clamp (200 - (x * y) % 170 + (int) (Rnd () % 9) - 4);
                break; }
            case 3: r = x * 255 / Math.Max (1, w - 1); g = (int) (Rnd () & 255); b = y * 255 / Math.Max (1, h - 1); a = ((x + 2 * y) * 255) / Math.Max (1, w + 2 * h - 3); if ((x + y) % 7 == 0) a = 0; break;
            case 4: r = g = b = (x + y * w) * 255 / Math.Max (1, w * h - 1); break;
            default: { int k = (x / 3 + y / 2) % 6; int[] pal = { 0xff0000, 0x00ff00, 0x0000ff, 0xffffff, 0x000000, 0x808080 }; r = pal [k] >> 16; g = (pal [k] >> 8) & 255; b = pal [k] & 255; if (k == 5 && (x & 1) == 0) a = 0; break; }
            }
        }

        // sRGB byte -> GDI+'s 13-bit linear light, written out so no libm decides a fixture.
        static readonly ushort[] s_lin = {
            0, 2, 5, 7, 10, 12, 15, 17, 20, 22, 25, 27, 30, 33, 36, 39, 42, 46, 50, 53, 57, 61, 66, 70, 75, 80, 85, 90, 95, 101, 106, 112,
            118, 125, 131, 138, 145, 152, 159, 166, 174, 182, 190, 198, 206, 215, 224, 233, 242, 252, 261, 271, 281, 292, 302, 313, 324, 335, 347, 358, 370, 382, 395, 407,
            420, 433, 446, 460, 474, 488, 502, 516, 531, 546, 561, 576, 592, 608, 624, 641, 657, 674, 691, 709, 726, 744, 762, 781, 799, 818, 838, 857, 877, 897, 917, 937,
            958, 979, 1001, 1022, 1044, 1066, 1088, 1111, 1134, 1157, 1181, 1204, 1228, 1253, 1277, 1302, 1327, 1353, 1378, 1404, 1431, 1457, 1484, 1511, 1539, 1566, 1594, 1623, 1651, 1680, 1709, 1739,
            1768, 1798, 1829, 1859, 1890, 1921, 1953, 1985, 2017, 2049, 2082, 2115, 2148, 2182, 2216, 2250, 2285, 2320, 2355, 2390, 2426, 2462, 2498, 2535, 2572, 2610, 2647, 2685, 2723, 2762, 2801, 2840,
            2880, 2920, 2960, 3000, 3041, 3082, 3124, 3166, 3208, 3250, 3293, 3336, 3380, 3423, 3467, 3512, 3557, 3602, 3647, 3693, 3739, 3785, 3832, 3879, 3927, 3974, 4022, 4071, 4120, 4169, 4218, 4268,
            4318, 4369, 4419, 4471, 4522, 4574, 4626, 4679, 4732, 4785, 4838, 4892, 4947, 5001, 5056, 5111, 5167, 5223, 5280, 5336, 5393, 5451, 5509, 5567, 5625, 5684, 5743, 5803, 5863, 5923, 5984, 6045,
            6106, 6168, 6230, 6293, 6356, 6419, 6482, 6546, 6611, 6675, 6740, 6806, 6871, 6938, 7004, 7071, 7138, 7206, 7274, 7342, 7411, 7480, 7550, 7619, 7690, 7760, 7831, 7903, 7974, 8047, 8119, 8192,
        };

        static void Put16 (byte[] b, int o, int v) { b [o] = (byte) v; b [o + 1] = (byte) (v >> 8); }

        static Bitmap Source (PixelFormat f, int w, int h, int kind, uint seed, Color[] pal)
        {
            s_seed = seed;
            var bmp = new Bitmap (w, h, f);
            if ((f & PixelFormat.Indexed) != 0 && pal != null) {
                ColorPalette cp = bmp.Palette;
                for (int i = 0; i < cp.Entries.Length && i < pal.Length; i++) cp.Entries [i] = pal [i];
                bmp.Palette = cp;
            }
            BitmapData d = bmp.LockBits (new Rectangle (0, 0, w, h), ImageLockMode.WriteOnly, f);
            var row = new byte [d.Stride];
            for (int y = 0; y < h; y++) {
                Array.Clear (row, 0, row.Length);
                for (int x = 0; x < w; x++) {
                    int a, r, g, b;
                    Pixel (kind, x, y, w, h, out a, out r, out g, out b);
                    switch (f) {
                    case PixelFormat.Format1bppIndexed: { int i = (int) ((uint) (r * 7 + g * 3 + b + a) % 2u); row [x >> 3] |= (byte) (i << (7 - (x & 7))); break; }
                    case PixelFormat.Format4bppIndexed: { int i = kind == 1 ? (int) (Rnd () & 15) : (r / 64) * 4 + g / 64; row [x >> 1] |= (byte) (i << ((x & 1) == 0 ? 4 : 0)); break; }
                    case PixelFormat.Format8bppIndexed: row [x] = (byte) (kind == 1 ? (int) (Rnd () & 255) : (r / 32) * 32 + (g / 32) * 4 + b / 64); break;
                    case PixelFormat.Format16bppRgb555: { int v = (r >> 3) << 10 | (g >> 3) << 5 | b >> 3; Put16 (row, x * 2, v); break; }
                    case PixelFormat.Format16bppArgb1555: { int v = (a >= 128 ? 0x8000 : 0) | (r >> 3) << 10 | (g >> 3) << 5 | b >> 3; Put16 (row, x * 2, v); break; }
                    case PixelFormat.Format16bppRgb565: { int v = (r >> 3) << 11 | (g >> 2) << 5 | b >> 3; Put16 (row, x * 2, v); break; }
                    case PixelFormat.Format24bppRgb: row [x * 3] = (byte) b; row [x * 3 + 1] = (byte) g; row [x * 3 + 2] = (byte) r; break;
                    case PixelFormat.Format32bppRgb:
                    case PixelFormat.Format32bppArgb: row [x * 4] = (byte) b; row [x * 4 + 1] = (byte) g; row [x * 4 + 2] = (byte) r; row [x * 4 + 3] = (byte) a; break;
                    case PixelFormat.Format32bppPArgb: row [x * 4] = (byte) ((b * a + 127) / 255); row [x * 4 + 1] = (byte) ((g * a + 127) / 255); row [x * 4 + 2] = (byte) ((r * a + 127) / 255); row [x * 4 + 3] = (byte) a; break;
                    case PixelFormat.Format48bppRgb: Put16 (row, x * 6, s_lin [b]); Put16 (row, x * 6 + 2, s_lin [g]); Put16 (row, x * 6 + 4, s_lin [r]); break;
                    case PixelFormat.Format64bppArgb:
                    case PixelFormat.Format64bppPArgb: {
                        int a13 = a * 8192 / 255; int lb = s_lin [b], lg = s_lin [g], lr = s_lin [r];
                        if (f == PixelFormat.Format64bppPArgb) { lb = lb * a13 >> 13; lg = lg * a13 >> 13; lr = lr * a13 >> 13; }
                        Put16 (row, x * 8, lb); Put16 (row, x * 8 + 2, lg); Put16 (row, x * 8 + 4, lr); Put16 (row, x * 8 + 6, a13); break; }
                    }
                }
                Marshal.Copy (row, 0, new IntPtr (d.Scan0.ToInt64 () + (long) y * d.Stride), d.Stride);
            }
            bmp.UnlockBits (d);
            return bmp;
        }

        // custom palettes: 0 = the format's own, 1 = random opaque, 2 = random with alpha, 3 = greys, 4 = short
        static Color[] SourcePalette (PixelFormat f, int which, uint seed)
        {
            if (which == 0) return null;
            s_seed = seed;
            int n = 1 << Image.GetPixelFormatSize (f);
            if (which == 4) n = Math.Max (2, n / 3);
            var p = new Color [n];
            for (int i = 0; i < n; i++) {
                uint v = Rnd ();
                if (which == 3) { int g = i * 255 / (n - 1); p [i] = Color.FromArgb (255, g, g, g); }
                else p [i] = Color.FromArgb (which == 2 ? (int) (Rnd () & 255) : 255, (int) (v & 255), (int) ((v >> 8) & 255), (int) ((v >> 16) & 255));
            }
            return p;
        }

        // ---- canonical forms ----------------------------------------------------------------------

        // An image as the bytes it is held to: format, size, stride, image flags, raw format, palette, and
        // every row's PIXELS read through a lock in its own format (never the padding, nor the bits after
        // the last pixel: GDI+ leaves its heap there).
        static byte[] Canon (Bitmap b)
        {
            var ms = new MemoryStream ();
            var o = new BinaryWriter (ms);
            PixelFormat f = b.PixelFormat;
            o.Write (Encoding.ASCII.GetBytes ("IMG1"));
            o.Write ((int) f); o.Write (b.Width); o.Write (b.Height); o.Write ((int) b.Flags);
            o.Write (b.RawFormat.Guid.ToByteArray ());
            WritePalette (o, b.Palette);
            try {
                BitmapData d = b.LockBits (new Rectangle (0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, f);
                o.Write (d.Stride);
                int bits = b.Width * Image.GetPixelFormatSize (f), full = bits / 8, rem = bits % 8;
                var row = new byte [Math.Abs (d.Stride)];
                for (int y = 0; y < b.Height; y++) {
                    Marshal.Copy (new IntPtr (d.Scan0.ToInt64 () + (long) y * d.Stride), row, 0, row.Length);
                    o.Write (row, 0, full);
                    if (rem != 0) o.Write ((byte) (row [full] & (0xff << (8 - rem))));
                }
                b.UnlockBits (d);
            } catch (Exception e) {
                o.Write (-1); o.Write ("lock throws " + e.GetType ().Name);
            }
            o.Flush ();
            return ms.ToArray ();
        }

        // An empty palette's flags are whatever System.Drawing.Common's buffer held before (GDI+ writes
        // only the count), so they are not held to anything.
        static void WritePalette (BinaryWriter o, ColorPalette p)
        {
            o.Write (p.Entries.Length == 0 ? 0 : p.Flags); o.Write (p.Entries.Length);
            foreach (Color c in p.Entries) o.Write (c.ToArgb ());
        }

        static byte[] CanonPalette (ColorPalette p)
        {
            var ms = new MemoryStream ();
            var o = new BinaryWriter (ms);
            o.Write (Encoding.ASCII.GetBytes ("PAL1"));
            WritePalette (o, p);
            o.Flush ();
            return ms.ToArray ();
        }

        static byte[] Throws (Exception e)
        {
            return Encoding.ASCII.GetBytes ("ERR1" + e.GetType ().Name);
        }

        // ---- the cases -------------------------------------------------------------------------------

        static string Short (PixelFormat f)
        {
            switch (f) {
            case PixelFormat.Format1bppIndexed: return "i1"; case PixelFormat.Format4bppIndexed: return "i4"; case PixelFormat.Format8bppIndexed: return "i8";
            case PixelFormat.Format16bppRgb555: return "555"; case PixelFormat.Format16bppRgb565: return "565"; case PixelFormat.Format16bppArgb1555: return "1555";
            case PixelFormat.Format16bppGrayScale: return "g16";
            case PixelFormat.Format24bppRgb: return "24"; case PixelFormat.Format32bppRgb: return "32rgb"; case PixelFormat.Format32bppArgb: return "32a"; case PixelFormat.Format32bppPArgb: return "32p";
            case PixelFormat.Format48bppRgb: return "48"; case PixelFormat.Format64bppArgb: return "64a"; case PixelFormat.Format64bppPArgb: return "64p";
            }
            return ((int) f).ToString ("x");
        }

        public struct Src { public string Name; public PixelFormat Format; public int W, H, Kind, Pal; public uint Seed; }

        public static List<Src> Sources ()
        {
            var all = new List<Src> ();
            Add (all, PixelFormat.Format32bppArgb, 37, 23, 0, 0);
            Add (all, PixelFormat.Format32bppArgb, 37, 23, 1, 0);
            Add (all, PixelFormat.Format32bppArgb, 37, 23, 2, 0);
            Add (all, PixelFormat.Format32bppArgb, 37, 23, 3, 0);
            Add (all, PixelFormat.Format32bppArgb, 37, 23, 5, 0);
            Add (all, PixelFormat.Format32bppPArgb, 37, 23, 1, 0);
            Add (all, PixelFormat.Format24bppRgb, 37, 23, 2, 0);
            Add (all, PixelFormat.Format24bppRgb, 37, 23, 4, 0);
            Add (all, PixelFormat.Format32bppRgb, 37, 23, 1, 0);
            Add (all, PixelFormat.Format16bppRgb555, 37, 23, 2, 0);
            Add (all, PixelFormat.Format16bppRgb565, 37, 23, 0, 0);
            Add (all, PixelFormat.Format16bppArgb1555, 37, 23, 3, 0);
            Add (all, PixelFormat.Format48bppRgb, 37, 23, 2, 0);
            Add (all, PixelFormat.Format64bppArgb, 37, 23, 3, 0);
            Add (all, PixelFormat.Format64bppPArgb, 37, 23, 1, 0);
            Add (all, PixelFormat.Format1bppIndexed, 37, 23, 0, 1);
            Add (all, PixelFormat.Format4bppIndexed, 37, 23, 1, 0);
            Add (all, PixelFormat.Format4bppIndexed, 37, 23, 1, 2);
            Add (all, PixelFormat.Format8bppIndexed, 37, 23, 1, 0);
            Add (all, PixelFormat.Format8bppIndexed, 37, 23, 0, 3);
            Add (all, PixelFormat.Format8bppIndexed, 37, 23, 1, 4);
            Add (all, PixelFormat.Format32bppArgb, 13, 5, 1, 0);
            Add (all, PixelFormat.Format32bppArgb, 1, 3, 1, 0);
            Add (all, PixelFormat.Format24bppRgb, 64, 48, 2, 0);
            Add (all, PixelFormat.Format24bppRgb, 96, 80, 0, 0);
            return all;
        }

        static void Add (List<Src> all, PixelFormat f, int w, int h, int kind, int pal)
        {
            var c = new Src ();
            c.Format = f; c.W = w; c.H = h; c.Kind = kind; c.Pal = pal;
            c.Seed = (uint) (kind * 7919 + pal * 104729 + w * 31 + h + (int) f);
            bool idx = (f & PixelFormat.Indexed) != 0;
            c.Name = Short (f) + "_k" + kind + (idx ? "_p" + pal : "") + "_" + w + "x" + h;
            all.Add (c);
        }

        static Bitmap Make (Src s)
        {
            Color[] pal = (s.Format & PixelFormat.Indexed) != 0 ? SourcePalette (s.Format, s.Pal, s.Seed ^ 0x5a5a) : null;
            return Source (s.Format, s.W, s.H, s.Kind, s.Seed, pal);
        }

        static readonly PixelFormat[] s_indexed = { PixelFormat.Format1bppIndexed, PixelFormat.Format4bppIndexed, PixelFormat.Format8bppIndexed };
        static readonly PixelFormat[] s_direct = {
            PixelFormat.Format16bppRgb555, PixelFormat.Format16bppRgb565, PixelFormat.Format16bppArgb1555, PixelFormat.Format16bppGrayScale,
            PixelFormat.Format24bppRgb, PixelFormat.Format32bppRgb, PixelFormat.Format32bppArgb, PixelFormat.Format32bppPArgb,
            PixelFormat.Format48bppRgb, PixelFormat.Format64bppArgb, PixelFormat.Format64bppPArgb,
        };

        public delegate void Sink (string group, string op, byte[] canon);

        static void Try (Sink sink, string group, string op, Func<byte[]> f)
        {
            byte[] r;
            try { r = f (); } catch (Exception e) { r = Throws (e); }
            sink (group, op, r);
        }

        static byte[] Converted (Src s, PixelFormat df, DitherType dt, PaletteType pt, ColorPalette pal, float alpha)
        {
            using (Bitmap b = Make (s)) {
                b.ConvertFormat (df, dt, pt, pal, alpha);
                return Canon (b);
            }
        }

        // A palette as the battery wants it: n entries of an optimal palette for the source, a fixed
        // one, or none.
        static ColorPalette Optimal (Src s, int n, bool transparent)
        {
            using (Bitmap b = Make (s)) return ColorPalette.CreateOptimalPalette (n, transparent, b);
        }

        public static void Run (Sink sink)
        {
            // Fixed palettes on their own.
            for (int t = 1; t <= 9; t++) {
                int tt = t;
                Try (sink, "palette", "fixed" + t, () => CanonPalette (new ColorPalette ((PaletteType) tt)));
            }
            foreach (Src s in Sources ()) {
                // Optimal palettes of the source.
                foreach (int n in new [] { 1, 2, 3, 16, 17, 100, 255, 256, 257 })
                    foreach (bool t in new [] { false, true }) {
                        int nn = n; bool tr = t;
                        Try (sink, s.Name + ">opt", "n" + n + (t ? "t" : ""), () => CanonPalette (Optimal (s, nn, tr)));
                    }
                // The one-argument overload, every destination.
                foreach (PixelFormat df in s_indexed) {
                    PixelFormat d = df;
                    Try (sink, s.Name + ">auto", Short (df), () => { using (Bitmap b = Make (s)) { b.ConvertFormat (d); return Canon (b); } });
                }
                foreach (PixelFormat df in s_direct) {
                    PixelFormat d = df;
                    Try (sink, s.Name + ">auto", Short (df), () => { using (Bitmap b = Make (s)) { b.ConvertFormat (d); return Canon (b); } });
                }
                // Into indexed formats: every dither, against no palette, the source's optimal palette
                // (as Custom and as Optimal), every fixed palette (as its own type), and an optimal
                // palette with a fixed type's dither.
                foreach (PixelFormat df in s_indexed) {
                    int bpp = Image.GetPixelFormatSize (df), n = 1 << bpp;
                    string g = s.Name + ">" + Short (df);
                    ColorPalette opt = Optimal (s, n, false), optT = Optimal (s, n, true);
                    for (int dt = 0; dt <= 9; dt++) {
                        DitherType d = (DitherType) dt; PixelFormat f = df;
                        Try (sink, g, "d" + dt + "_null", () => Converted (s, f, d, PaletteType.Custom, null, 0));
                        Try (sink, g, "d" + dt + "_opt", () => Converted (s, f, d, PaletteType.Custom, opt, 0));
                        Try (sink, g, "d" + dt + "_optO", () => Converted (s, f, d, (PaletteType) 1, opt, 0));
                        Try (sink, g, "d" + dt + "_optT50", () => Converted (s, f, d, PaletteType.Custom, optT, 50));
                        for (int t = 2; t <= 9; t++) {
                            PaletteType pt = (PaletteType) t;
                            ColorPalette fp = new ColorPalette (pt);
                            Try (sink, g, "d" + dt + "_fix" + t, () => Converted (s, f, d, pt, fp, 0));
                        }
                        Try (sink, g, "d" + dt + "_opt7", () => Converted (s, f, d, PaletteType.FixedHalftone216, opt, 0));
                    }
                    foreach (float a in new [] { 0.25f, 25f, 49.9f, 100f, 150f, -5f }) {
                        PixelFormat f = df; float aa = a;
                        Try (sink, g, "a" + a + "_d1", () => Converted (s, f, DitherType.Solid, PaletteType.Custom, optT, aa));
                        Try (sink, g, "a" + a + "_d2", () => Converted (s, f, DitherType.Ordered4x4, PaletteType.FixedHalftone27, new ColorPalette (PaletteType.FixedHalftone27), aa));
                        Try (sink, g, "a" + a + "_d4", () => Converted (s, f, DitherType.Ordered16x16, PaletteType.FixedHalftone256, new ColorPalette (PaletteType.FixedHalftone256), aa));
                        Try (sink, g, "a" + a + "_d9", () => Converted (s, f, DitherType.ErrorDiffusion, PaletteType.Custom, optT, aa));
                    }
                }
                // Clone and a read lock into every direct format: the same conversions as ConvertFormat's.
                foreach (PixelFormat df in s_direct) {
                    PixelFormat d = df;
                    Try (sink, s.Name + ">clone", Short (df), () => { using (Bitmap b = Make (s)) using (Bitmap c = b.Clone (new Rectangle (0, 0, b.Width, b.Height), d)) return Canon (c); });
                }
                foreach (PixelFormat df in s_direct) {
                    PixelFormat d = df;
                    Try (sink, s.Name + ">lock", Short (df), () => {
                        using (Bitmap b = Make (s)) {
                            BitmapData bd = b.LockBits (new Rectangle (0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, d);
                            int bits = b.Width * Image.GetPixelFormatSize (d), full = (bits + 7) / 8;
                            var ms = new MemoryStream ();
                            ms.Write (Encoding.ASCII.GetBytes ("RAW1"), 0, 4);
                            var row = new byte [full];
                            for (int y = 0; y < b.Height; y++) {
                                Marshal.Copy (new IntPtr (bd.Scan0.ToInt64 () + (long) y * bd.Stride), row, 0, full);
                                ms.Write (row, 0, full);
                            }
                            b.UnlockBits (bd);
                            return ms.ToArray ();
                        }
                    });
                }
                // Into direct formats: the dithers that matter (none, solid, ordered 4x4 -- the 16bpp
                // dither -- and one that does not), with and without a palette.
                foreach (PixelFormat df in s_direct) {
                    string g = s.Name + ">" + Short (df);
                    foreach (int dt in new [] { 0, 1, 2, 3, 9 }) {
                        DitherType d = (DitherType) dt; PixelFormat f = df;
                        Try (sink, g, "d" + dt + "_null", () => Converted (s, f, d, PaletteType.Custom, null, 0));
                        Try (sink, g, "d" + dt + "_fix7", () => Converted (s, f, d, PaletteType.FixedHalftone216, new ColorPalette (PaletteType.FixedHalftone216), 0));
                    }
                }
            }
            // Conversions of a decoded file keep what the file said about itself.
            foreach (KeyValuePair<string, byte[]> file in Files ()) {
                byte[] bytes = file.Value;
                string g = "file_" + file.Key;
                Try (sink, g, "decoded", () => { using (var ms = new MemoryStream (bytes)) using (var b = new Bitmap (ms)) return Canon (b); });
                foreach (PixelFormat df in new [] { PixelFormat.Format8bppIndexed, PixelFormat.Format1bppIndexed, PixelFormat.Format24bppRgb, PixelFormat.Format32bppArgb }) {
                    PixelFormat d = df;
                    Try (sink, g, "auto" + Short (df), () => { using (var ms = new MemoryStream (bytes)) using (var b = new Bitmap (ms)) { b.ConvertFormat (d); return Canon (b); } });
                }
                Try (sink, g, "fix9", () => {
                    using (var ms = new MemoryStream (bytes)) using (var b = new Bitmap (ms)) {
                        b.ConvertFormat (PixelFormat.Format8bppIndexed, DitherType.Ordered8x8, PaletteType.FixedHalftone256, new ColorPalette (PaletteType.FixedHalftone256), 0);
                        return Canon (b);
                    }
                });
            }
        }

        // ---- files, written by hand so no encoder decides a fixture ---------------------------------

        static void Le16 (List<byte> o, int v) { o.Add ((byte) v); o.Add ((byte) (v >> 8)); }
        static void Le32 (List<byte> o, int v) { o.Add ((byte) v); o.Add ((byte) (v >> 8)); o.Add ((byte) (v >> 16)); o.Add ((byte) (v >> 24)); }
        static void Be32 (List<byte> o, uint v) { o.Add ((byte) (v >> 24)); o.Add ((byte) (v >> 16)); o.Add ((byte) (v >> 8)); o.Add ((byte) v); }

        // Rows of indices (or samples), packed MSB first at bpp bits.
        static byte[] PackRow (int[] v, int bpp)
        {
            var r = new byte [(v.Length * bpp + 7) / 8];
            for (int i = 0; i < v.Length; i++) {
                if (bpp == 8) r [i] = (byte) v [i];
                else if (bpp == 16) { r [i * 2] = (byte) (v [i] >> 8); r [i * 2 + 1] = (byte) v [i]; }
                else { int bit = i * bpp; r [bit >> 3] |= (byte) ((v [i] & ((1 << bpp) - 1)) << (8 - bpp - (bit & 7))); }
            }
            return r;
        }

        static int[] Indices (int w, int y, int levels, uint seed)
        {
            s_seed = seed + (uint) y * 977u;
            var v = new int [w];
            for (int x = 0; x < w; x++) v [x] = (int) (Rnd () % (uint) levels);
            return v;
        }

        static byte[] Bmp (int w, int h, int bpp, uint[] palette, int clrUsed, bool alphaMask, uint seed)
        {
            var o = new List<byte> ();
            int stride = ((w * bpp + 31) / 32) * 4;
            int hdr = alphaMask ? 124 : 40;
            int palBytes = palette == null ? 0 : palette.Length * 4;
            int off = 14 + hdr + palBytes;
            o.Add ((byte) 'B'); o.Add ((byte) 'M'); Le32 (o, off + stride * h); Le32 (o, 0); Le32 (o, off);
            Le32 (o, hdr); Le32 (o, w); Le32 (o, h); Le16 (o, 1); Le16 (o, bpp); Le32 (o, alphaMask ? 3 : 0); Le32 (o, stride * h);
            Le32 (o, 2835); Le32 (o, 2835); Le32 (o, clrUsed); Le32 (o, 0);
            if (alphaMask) {
                Le32 (o, 0x00ff0000); Le32 (o, 0x0000ff00); Le32 (o, 0x000000ff); Le32 (o, unchecked ((int) 0xff000000));
                Le32 (o, 0x73524742); for (int i = 0; i < 9; i++) Le32 (o, 0); Le32 (o, 0); Le32 (o, 0); Le32 (o, 0);
                Le32 (o, 4); Le32 (o, 0); Le32 (o, 0); Le32 (o, 0);
            }
            if (palette != null) foreach (uint c in palette) Le32 (o, (int) c);
            int levels = palette != null ? Math.Max (1, clrUsed != 0 ? clrUsed : palette.Length) : 256;
            for (int y = h - 1; y >= 0; y--) {
                byte[] row;
                if (bpp <= 8) row = PackRow (Indices (w, y, levels, seed), bpp);
                else {
                    int bytes = bpp / 8;
                    row = new byte [w * bytes];
                    s_seed = seed + (uint) y * 31u;
                    for (int i = 0; i < row.Length; i++) row [i] = (byte) Rnd ();
                }
                o.AddRange (row);
                for (int p = row.Length; p < stride; p++) o.Add (0);
            }
            return o.ToArray ();
        }

        // An RLE8 (bpp 8) or RLE4 (bpp 4) BMP: runs of one index, absolute runs, end of line, end of
        // bitmap, rows bottom-up.
        static byte[] BmpRle (int w, int h, int bpp, uint[] palette, uint seed)
        {
            var px = new List<byte> ();
            int levels = palette.Length;
            for (int y = h - 1; y >= 0; y--) {
                int[] v = Indices (w, y, levels, seed);
                for (int x = 0; x < w; ) {
                    int run = 1;
                    while (x + run < w && run < 6 && v [x + run] == v [x]) run++;
                    if (run >= 2 || w - x < 3) {
                        px.Add ((byte) run);
                        px.Add (bpp == 8 ? (byte) v [x] : (byte) (v [x] << 4 | v [x]));
                        x += run;
                    } else {
                        int n = Math.Min (w - x, 5);
                        px.Add (0); px.Add ((byte) n);
                        var abs = new List<byte> ();
                        if (bpp == 8) for (int i = 0; i < n; i++) abs.Add ((byte) v [x + i]);
                        else for (int i = 0; i < n; i += 2) abs.Add ((byte) (v [x + i] << 4 | (i + 1 < n ? v [x + i + 1] : 0)));
                        px.AddRange (abs);
                        if ((abs.Count & 1) != 0) px.Add (0);
                        x += n;
                    }
                }
                px.Add (0); px.Add (0);
            }
            px.Add (0); px.Add (1);
            var o = new List<byte> ();
            int off = 14 + 40 + palette.Length * 4;
            o.Add ((byte) 'B'); o.Add ((byte) 'M'); Le32 (o, off + px.Count); Le32 (o, 0); Le32 (o, off);
            Le32 (o, 40); Le32 (o, w); Le32 (o, h); Le16 (o, 1); Le16 (o, bpp); Le32 (o, bpp == 8 ? 1 : 2); Le32 (o, px.Count);
            Le32 (o, 0); Le32 (o, 0); Le32 (o, palette.Length); Le32 (o, 0);
            foreach (uint c in palette) Le32 (o, (int) c);
            o.AddRange (px);
            return o.ToArray ();
        }

        static readonly uint[] s_crc = MakeCrc ();
        static uint[] MakeCrc ()
        {
            var t = new uint [256];
            for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1; t [n] = c; }
            return t;
        }

        static void Chunk (List<byte> o, string type, List<byte> data)
        {
            Be32 (o, (uint) data.Count);
            var td = new List<byte> (Encoding.ASCII.GetBytes (type));
            td.AddRange (data);
            uint c = 0xffffffffu;
            foreach (byte b in td) c = s_crc [(c ^ b) & 0xff] ^ (c >> 8);
            o.AddRange (td);
            Be32 (o, c ^ 0xffffffffu);
        }

        static byte[] Png (int w, int h, int depth, int colorType, uint[] plte, byte[] trns, uint seed, bool interlace = false)
        {
            var o = new List<byte> { 137, 80, 78, 71, 13, 10, 26, 10 };
            var ihdr = new List<byte> ();
            Be32 (ihdr, (uint) w); Be32 (ihdr, (uint) h); ihdr.Add ((byte) depth); ihdr.Add ((byte) colorType); ihdr.Add (0); ihdr.Add (0); ihdr.Add ((byte) (interlace ? 1 : 0));
            Chunk (o, "IHDR", ihdr);
            if (plte != null) {
                var p = new List<byte> ();
                foreach (uint c in plte) { p.Add ((byte) (c >> 16)); p.Add ((byte) (c >> 8)); p.Add ((byte) c); }
                Chunk (o, "PLTE", p);
            }
            if (trns != null) Chunk (o, "tRNS", new List<byte> (trns));
            int channels = colorType == 0 || colorType == 3 ? 1 : colorType == 2 ? 3 : colorType == 4 ? 2 : 4;
            var raw = new List<byte> ();
            int lv = colorType == 3 ? plte.Length : depth == 16 ? 65536 : 1 << depth;
            if (!interlace) {
                for (int y = 0; y < h; y++) {
                    raw.Add (0);
                    raw.AddRange (PackRow (Indices (w * channels, y, lv, seed), depth));
                }
            } else {
                // Adam7: the same samples, the seven passes' sub-images one after another.
                int[] x0 = { 0, 4, 0, 2, 0, 1, 0 }, y0 = { 0, 0, 4, 0, 2, 0, 1 }, dx = { 8, 8, 4, 4, 2, 2, 1 }, dy = { 8, 8, 8, 4, 4, 2, 2 };
                for (int pass = 0; pass < 7; pass++) {
                    if (x0 [pass] >= w || y0 [pass] >= h) continue;
                    for (int y = y0 [pass]; y < h; y += dy [pass]) {
                        int[] full = Indices (w * channels, y, lv, seed);
                        var sub = new List<int> ();
                        for (int x = x0 [pass]; x < w; x += dx [pass]) for (int c = 0; c < channels; c++) sub.Add (full [x * channels + c]);
                        raw.Add (0);
                        raw.AddRange (PackRow (sub.ToArray (), depth));
                    }
                }
            }
            var z = new List<byte> { 0x78, 0x01 };
            for (int i = 0; i < raw.Count; i += 65535) {
                int n = Math.Min (65535, raw.Count - i);
                z.Add ((byte) (i + n >= raw.Count ? 1 : 0)); Le16 (z, n); Le16 (z, ~n & 0xffff);
                z.AddRange (raw.GetRange (i, n));
            }
            uint a = 1, b2 = 0;
            foreach (byte x in raw) { a = (a + x) % 65521; b2 = (b2 + a) % 65521; }
            Be32 (z, b2 << 16 | a);
            Chunk (o, "IDAT", z);
            Chunk (o, "IEND", new List<byte> ());
            return o.ToArray ();
        }

        // An uncompressed little-endian TIFF: one strip, bits per sample, photometric, samples, an
        // optional colour map (16-bit entries) and extra samples.
        static byte[] Tiff (int w, int h, int bps, int photometric, int spp, ushort[] colorMap, int extra, uint seed)
        {
            int rowBytes = (w * bps * spp + 7) / 8;
            var data = new List<byte> ();
            for (int y = 0; y < h; y++) data.AddRange (PackRow (Indices (w * spp, y, 1 << bps, seed), bps));
            var entries = new List<int[]> ();   // tag, type, count, value-or-offset placeholder
            var extraData = new List<byte> ();
            int ifdOff = 8 + data.Count + (data.Count & 1);
            int nEntries = 12 + (colorMap != null ? 1 : 0) + (extra >= 0 ? 1 : 0);
            int extraOff = ifdOff + 2 + nEntries * 12 + 4;
            Func<List<byte>, int> Blob = (bytes) => { int at = extraOff + extraData.Count; extraData.AddRange (bytes); if ((extraData.Count & 1) != 0) extraData.Add (0); return at; };
            entries.Add (new [] { 256, 4, 1, w });
            entries.Add (new [] { 257, 4, 1, h });
            if (spp == 1) entries.Add (new [] { 258, 3, 1, bps });
            else { var b = new List<byte> (); for (int i = 0; i < spp; i++) Le16 (b, bps); entries.Add (new [] { 258, 3, spp, Blob (b) }); }
            entries.Add (new [] { 259, 3, 1, 1 });
            entries.Add (new [] { 262, 3, 1, photometric });
            entries.Add (new [] { 273, 4, 1, 8 });
            entries.Add (new [] { 277, 3, 1, spp });
            entries.Add (new [] { 278, 4, 1, h });
            entries.Add (new [] { 279, 4, 1, data.Count });
            { var b = new List<byte> (); Le32 (b, 72); Le32 (b, 1); entries.Add (new [] { 282, 5, 1, Blob (b) }); }
            { var b = new List<byte> (); Le32 (b, 72); Le32 (b, 1); entries.Add (new [] { 283, 5, 1, Blob (b) }); }
            entries.Add (new [] { 296, 3, 1, 2 });
            if (colorMap != null) { var b = new List<byte> (); foreach (ushort v in colorMap) Le16 (b, v); entries.Add (new [] { 320, 3, colorMap.Length, Blob (b) }); }
            if (extra >= 0) entries.Add (new [] { 338, 3, 1, extra });
            var o = new List<byte> { (byte) 'I', (byte) 'I', 42, 0 };
            Le32 (o, ifdOff);
            o.AddRange (data);
            if ((data.Count & 1) != 0) o.Add (0);
            Le16 (o, entries.Count);
            foreach (int[] e in entries) {
                Le16 (o, e [0]); Le16 (o, e [1]); Le32 (o, e [2]);
                if (e [1] == 3 && e [2] == 1) { Le16 (o, e [3]); Le16 (o, 0); } else Le32 (o, e [3]);
            }
            Le32 (o, 0);
            o.AddRange (extraData);
            return o.ToArray ();
        }

        static uint[] GreyPalette (int n) { var p = new uint [n]; for (int i = 0; i < n; i++) { uint g = (uint) (i * 255 / Math.Max (1, n - 1)); p [i] = 0xff000000u | g << 16 | g << 8 | g; } return p; }
        static uint[] RandomPalette (int n, uint seed, bool alpha) { s_seed = seed; var p = new uint [n]; for (int i = 0; i < n; i++) p [i] = (alpha ? (Rnd () & 0xff) << 24 : 0xff000000u) | (Rnd () & 0xffffff); return p; }

        static ushort[] ColorMap (uint[] pal)
        {
            int n = pal.Length;
            var m = new ushort [n * 3];
            for (int i = 0; i < n; i++) {
                m [i] = (ushort) (((pal [i] >> 16) & 0xff) * 257);
                m [n + i] = (ushort) (((pal [i] >> 8) & 0xff) * 257);
                m [2 * n + i] = (ushort) ((pal [i] & 0xff) * 257);
            }
            return m;
        }

        public static List<KeyValuePair<string, byte[]>> Files ()
        {
            var f = new List<KeyValuePair<string, byte[]>> ();
            Action<string, byte[]> add = (n, b) => f.Add (new KeyValuePair<string, byte[]> (n, b));
            // BMP: indexed with grey, black-and-white, coloured and short palettes; the reserved byte
            // set (a would-be alpha); direct formats.
            add ("bmp1_bw", Bmp (21, 7, 1, new [] { 0x000000u, 0xffffffu }, 0, false, 1));
            add ("bmp1_wb", Bmp (21, 7, 1, new [] { 0xffffffu, 0x000000u }, 0, false, 2));
            add ("bmp1_col", Bmp (21, 7, 1, new [] { 0x123456u, 0xfedcbau }, 0, false, 3));
            add ("bmp1_grey", Bmp (21, 7, 1, new [] { 0x404040u, 0xc0c0c0u }, 0, false, 4));
            add ("bmp4_grey", Bmp (21, 7, 4, GreyPalette (16), 0, false, 5));
            add ("bmp4_col", Bmp (21, 7, 4, RandomPalette (16, 6, false), 0, false, 6));
            add ("bmp4_res", Bmp (21, 7, 4, RandomPalette (16, 7, true), 0, false, 7));
            add ("bmp4_short", Bmp (21, 7, 4, GreyPalette (3), 3, false, 8));
            add ("bmp8_grey", Bmp (21, 7, 8, GreyPalette (256), 0, false, 9));
            add ("bmp8_col", Bmp (21, 7, 8, RandomPalette (256, 10, false), 0, false, 10));
            add ("bmp8_res", Bmp (21, 7, 8, RandomPalette (256, 11, true), 0, false, 11));
            add ("bmp8_short", Bmp (21, 7, 8, RandomPalette (20, 12, false), 20, false, 12));
            add ("bmp8_grey2", Bmp (21, 7, 8, GreyPalette (2), 2, false, 13));
            add ("bmp24", Bmp (21, 7, 24, null, 0, false, 14));
            add ("bmp32", Bmp (21, 7, 32, null, 0, false, 15));
            add ("bmp32a", Bmp (21, 7, 32, null, 0, true, 16));
            add ("bmp16", Bmp (21, 7, 16, null, 0, false, 17));
            add ("bmp_rle8", BmpRle (21, 7, 8, RandomPalette (12, 18, false), 18));
            add ("bmp_rle8grey", BmpRle (21, 7, 8, GreyPalette (12), 19));
            add ("bmp_rle4", BmpRle (21, 7, 4, RandomPalette (16, 20, false), 20));
            // PNG: palettes of every depth (grey, coloured, with and without tRNS), grey of every depth
            // (with a tRNS key too), RGB, RGBA, grey+alpha.
            foreach (int d in new [] { 1, 2, 4, 8 }) {
                int n = 1 << d;
                add ("png" + d + "_pgrey", Png (19, 6, d, 3, GreyPalette (n), null, (uint) (20 + d)));
                add ("png" + d + "_pcol", Png (19, 6, d, 3, RandomPalette (n, (uint) (30 + d), false), null, (uint) (30 + d)));
                add ("png" + d + "_pshort", Png (19, 6, d, 3, RandomPalette (Math.Max (2, n / 2 + 1), (uint) (40 + d), false), null, (uint) (40 + d)));
                add ("png" + d + "_ptrns", Png (19, 6, d, 3, RandomPalette (n, (uint) (50 + d), false), d == 1 ? new byte [] { 0, 128 } : new byte [] { 0, 128, 255 }, (uint) (50 + d)));
                add ("png" + d + "_ptrnsff", Png (19, 6, d, 3, RandomPalette (n, (uint) (60 + d), false), new byte [] { 255, 255 }, (uint) (60 + d)));
                add ("png" + d + "_pgreytrns", Png (19, 6, d, 3, GreyPalette (n), new byte [] { 0 }, (uint) (70 + d)));
                add ("png" + d + "_grey", Png (19, 6, d, 0, null, null, (uint) (80 + d)));
                add ("png" + d + "_greytrns", Png (19, 6, d, 0, null, new byte [] { 0, 1 }, (uint) (90 + d)));
            }
            add ("png16_grey", Png (19, 6, 16, 0, null, null, 100));
            add ("png8_rgb", Png (19, 6, 8, 2, null, null, 101));
            add ("png8_rgba", Png (19, 6, 8, 6, null, null, 102));
            add ("png8_ga", Png (19, 6, 8, 4, null, null, 103));
            add ("png16_rgb", Png (19, 6, 16, 2, null, null, 104));
            add ("png16_rgba", Png (19, 6, 16, 6, null, null, 105));
            add ("png16_ga", Png (19, 6, 16, 4, null, null, 106));
            add ("png1_grey_i", Png (19, 6, 1, 0, null, null, 107, true));
            add ("png4_pgrey_i", Png (19, 6, 4, 3, GreyPalette (16), null, 108, true));
            add ("png8_pcol_i", Png (19, 6, 8, 3, RandomPalette (256, 109, false), null, 109, true));
            add ("png8_ptrns_i", Png (19, 6, 8, 3, RandomPalette (256, 110, false), new byte [] { 0, 7 }, 110, true));
            add ("png8_grey_i", Png (19, 6, 8, 0, null, null, 111, true));
            add ("png1_pbw", Png (19, 6, 1, 3, new [] { 0xff000000u, 0xffffffffu }, null, 112));
            add ("png8_pgrey3", Png (19, 6, 8, 3, GreyPalette (3), null, 113));
            // TIFF: palette 1/4/8 bit (grey, coloured), grey 1/4/8 bit both ways round, RGB, RGBA.
            foreach (int d in new [] { 1, 2, 4, 8 }) {
                int n = 1 << d;
                add ("tif" + d + "_pcol", Tiff (17, 5, d, 3, 1, ColorMap (RandomPalette (n, (uint) (110 + d), false)), -1, (uint) (110 + d)));
                add ("tif" + d + "_pgrey", Tiff (17, 5, d, 3, 1, ColorMap (GreyPalette (n)), -1, (uint) (120 + d)));
                add ("tif" + d + "_minblack", Tiff (17, 5, d, 1, 1, null, -1, (uint) (130 + d)));
                add ("tif" + d + "_minwhite", Tiff (17, 5, d, 0, 1, null, -1, (uint) (140 + d)));
            }
            add ("tif8_rgb", Tiff (17, 5, 8, 2, 3, null, -1, 150));
            add ("tif8_rgba", Tiff (17, 5, 8, 2, 4, null, 2, 151));
            add ("tif8_rgbx", Tiff (17, 5, 8, 2, 4, null, 0, 152));
            add ("tif8_rgbpa", Tiff (17, 5, 8, 2, 4, null, 1, 153));
            add ("tif16_grey", Tiff (17, 5, 16, 1, 1, null, -1, 154));
            add ("tif8_greya", Tiff (17, 5, 8, 1, 2, null, 2, 155));
            add ("tif8_greypa", Tiff (17, 5, 8, 1, 2, null, 1, 156));
            add ("tif8_pgrey3", Tiff (17, 5, 8, 3, 1, ColorMap (GreyPalette (256)), -1, 157));
            add ("tif4_pbw", Tiff (17, 5, 4, 3, 1, ColorMap (new uint [] { 0xff000000, 0xffffffff, 0xff000000, 0xffffffff, 0xff000000, 0xffffffff, 0xff000000, 0xffffffff, 0xff000000, 0xffffffff, 0xff000000, 0xffffffff, 0xff000000, 0xffffffff, 0xff000000, 0xffffffff }), -1, 158));
            add ("tif1_pcol2", Tiff (17, 5, 1, 3, 1, ColorMap (RandomPalette (2, 159, false)), -1, 159));
            return f;
        }

        // ---- digests -------------------------------------------------------------------------------

        public static string Digest (byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create ()) {
                byte[] hsh = sha.ComputeHash (bytes);
                var sb = new StringBuilder ();
                for (int i = 0; i < 8; i++) sb.Append (hsh [i].ToString ("x2"));
                return sb.ToString ();
            }
        }

        /// <summary>group -> digest over every op of the group, in order.</summary>
        public static List<KeyValuePair<string, string>> Digests ()
        {
            var res = new List<KeyValuePair<string, string>> ();
            string cur = null;
            MemoryStream acc = null;
            Sink sink = (g, op, canon) => {
                if (g != cur) {
                    if (cur != null) res.Add (new KeyValuePair<string, string> (cur, Digest (acc.ToArray ())));
                    cur = g; acc = new MemoryStream ();
                }
                byte[] n = Encoding.ASCII.GetBytes (op);
                acc.Write (n, 0, n.Length); acc.WriteByte (0);
                acc.Write (BitConverter.GetBytes (canon.Length), 0, 4);
                acc.Write (canon, 0, canon.Length);
            };
            Run (sink);
            if (cur != null) res.Add (new KeyValuePair<string, string> (cur, Digest (acc.ToArray ())));
            return res;
        }
    }
}
