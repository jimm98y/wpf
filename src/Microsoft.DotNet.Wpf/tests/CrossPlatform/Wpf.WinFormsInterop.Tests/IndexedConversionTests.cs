// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Conversions INTO 1, 4 and 8bpp indexed formats held to REAL GDI+, byte for byte.
//
// Bitmap.Clone (Rectangle and RectangleF), LockBits whole and from an odd column, pixels of another
// format written into an indexed bitmap through a lock, and Image.Save as GIF -- from 24/32bpp RGB,
// ARGB and PARGB, 16bpp 555/565/1555, 48/64bpp and from 1/4/8bpp bitmaps with their own palettes
// (opaque, translucent, grey, short), over gradients, noise, photo-like content, alpha ramps with
// holes and odd widths. Every result is reduced to its pixel format, size, stride, flags, palette
// and pixel bits (never the padding: GDI+ leaves its heap there) and hashed; a GIF is hashed as the
// file it is. The fixtures were written by IndexedBattery below compiled against .NET Framework's
// System.Drawing on Windows 11 26100 (gdiplus.dll 10.0.26100, arm64): the same text, C# 5 so it
// compiles there too.
//
// What GDI+ does, in short: ConvertFormat's halftone (a 4096-entry nearest-colour table indexed by
// the top four bits of each channel) for Clone and LockBits; the bitmap's own palette for a lock
// only when it has one; and for a GIF of a non-indexed bitmap WIC's FixedHalftone252 palette plus
// a transparent entry with serpentine Floyd-Steinberg error diffusion (GdipPixels has the details).
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
    public sealed class IndexedConversionTests
    {
        // name>destination -> digest, as gdiplus.dll produced them.
        private static readonly Dictionary<string, string> GdiPlus = new()
        {
            { "32a_k0_37x23>i1", "92ac70239b7061bd" },
            { "32a_k0_37x23>i4", "eedbdde19bd3f194" },
            { "32a_k0_37x23>i8", "a84bc13f2c6828b6" },
            { "32a_k0_37x23>gif", "b5eb60ee7cc42819" },
            { "32a_k1_37x23>i1", "1cea6d704538d65d" },
            { "32a_k1_37x23>i4", "b5952639db3a150e" },
            { "32a_k1_37x23>i8", "11aa9e4d29577e66" },
            { "32a_k1_37x23>gif", "d34bd3292fa60d6b" },
            { "32a_k2_37x23>i1", "77499ec8eb34b08b" },
            { "32a_k2_37x23>i4", "93dd8692ca2c30bf" },
            { "32a_k2_37x23>i8", "857e3868fe317d8b" },
            { "32a_k2_37x23>gif", "cce91f034f34df43" },
            { "32a_k3_37x23>i1", "adce6dde5ec5cf6c" },
            { "32a_k3_37x23>i4", "00652a11c3e657ca" },
            { "32a_k3_37x23>i8", "5aed2a518de0e5ca" },
            { "32a_k3_37x23>gif", "204a238ee82d58f9" },
            { "32p_k0_37x23>i1", "92ac70239b7061bd" },
            { "32p_k0_37x23>i4", "eedbdde19bd3f194" },
            { "32p_k0_37x23>i8", "a84bc13f2c6828b6" },
            { "32p_k0_37x23>gif", "b5eb60ee7cc42819" },
            { "32p_k1_37x23>i1", "bf91a063e0c91a81" },
            { "32p_k1_37x23>i4", "6efc9e29f25a5afb" },
            { "32p_k1_37x23>i8", "85060f45705dc650" },
            { "32p_k1_37x23>gif", "08ad2e26c3f8448c" },
            { "32p_k2_37x23>i1", "2dbac26ce6d744f0" },
            { "32p_k2_37x23>i4", "28029f5495130bdb" },
            { "32p_k2_37x23>i8", "7957b8d47800eca1" },
            { "32p_k2_37x23>gif", "96ad1d2f6a1b4feb" },
            { "32p_k3_37x23>i1", "306e535a34addcd1" },
            { "32p_k3_37x23>i4", "d1e4c5a4892fb180" },
            { "32p_k3_37x23>i8", "7e0acc9a75abecba" },
            { "32p_k3_37x23>gif", "999deab32054747f" },
            { "64a_k0_37x23>i1", "92ac70239b7061bd" },
            { "64a_k0_37x23>i4", "eedbdde19bd3f194" },
            { "64a_k0_37x23>i8", "a84bc13f2c6828b6" },
            { "64a_k0_37x23>gif", "b5eb60ee7cc42819" },
            { "64a_k1_37x23>i1", "6b3e56a5f49778d9" },
            { "64a_k1_37x23>i4", "91633aeb4430c80e" },
            { "64a_k1_37x23>i8", "3f831dd667836bfa" },
            { "64a_k1_37x23>gif", "205fda969f9b47e1" },
            { "64a_k2_37x23>i1", "ab44dd60778e3dac" },
            { "64a_k2_37x23>i4", "f0c51eb470ca2d65" },
            { "64a_k2_37x23>i8", "dfbab0c54701415f" },
            { "64a_k2_37x23>gif", "5c46295c03c6c0f9" },
            { "64a_k3_37x23>i1", "9211a875c4022d36" },
            { "64a_k3_37x23>i4", "7d4b7ddd98ae9409" },
            { "64a_k3_37x23>i8", "73aa7f9f87f6ddbe" },
            { "64a_k3_37x23>gif", "bb0758c806ee0c4b" },
            { "64p_k0_37x23>i1", "92ac70239b7061bd" },
            { "64p_k0_37x23>i4", "eedbdde19bd3f194" },
            { "64p_k0_37x23>i8", "a84bc13f2c6828b6" },
            { "64p_k0_37x23>gif", "b5eb60ee7cc42819" },
            { "64p_k1_37x23>i1", "c20c94a70c3326b6" },
            { "64p_k1_37x23>i4", "eebc564666c0e81e" },
            { "64p_k1_37x23>i8", "a4eb7f14060be7fc" },
            { "64p_k1_37x23>gif", "99560923fb452cda" },
            { "64p_k2_37x23>i1", "dfd9c44aeef2ee2a" },
            { "64p_k2_37x23>i4", "929d3292c9528d31" },
            { "64p_k2_37x23>i8", "58a918678d3c2b18" },
            { "64p_k2_37x23>gif", "42d7e83dc4bac8bb" },
            { "64p_k3_37x23>i1", "9057aac1138a766f" },
            { "64p_k3_37x23>i4", "3e44429023008ce3" },
            { "64p_k3_37x23>i8", "48f8b5070efda96f" },
            { "64p_k3_37x23>gif", "aa8bd362652e261b" },
            { "1555_k0_37x23>i1", "92ac70239b7061bd" },
            { "1555_k0_37x23>i4", "eedbdde19bd3f194" },
            { "1555_k0_37x23>i8", "a84bc13f2c6828b6" },
            { "1555_k0_37x23>gif", "a0d66f9d7b948f7f" },
            { "1555_k1_37x23>i1", "409c0ba323369ced" },
            { "1555_k1_37x23>i4", "5b7931a14eb702eb" },
            { "1555_k1_37x23>i8", "2223980f15edc623" },
            { "1555_k1_37x23>gif", "976d74c0ed47c724" },
            { "1555_k2_37x23>i1", "10d55038d94c5574" },
            { "1555_k2_37x23>i4", "48b5e64294cff3f3" },
            { "1555_k2_37x23>i8", "3ba6f25cf4e7baf2" },
            { "1555_k2_37x23>gif", "f4620697fdc87435" },
            { "1555_k3_37x23>i1", "db291988f26ec59a" },
            { "1555_k3_37x23>i4", "bc7e266f8bbff648" },
            { "1555_k3_37x23>i8", "b7670f58a06fd13a" },
            { "1555_k3_37x23>gif", "992fdfcb321c3177" },
            { "24_k1_37x23>i1", "802052f617166269" },
            { "24_k1_37x23>i4", "fa0f5ef308a6b4aa" },
            { "24_k1_37x23>i8", "d1eda66c27174f59" },
            { "24_k1_37x23>gif", "6d7906464d42a356" },
            { "24_k2_37x23>i1", "a9018d334c0ea708" },
            { "24_k2_37x23>i4", "6a67a5f21adbeda8" },
            { "24_k2_37x23>i8", "ab1906074bd075bc" },
            { "24_k2_37x23>gif", "b2f186eb0986bd53" },
            { "24_k4_37x23>i1", "3c8973e339aee4cc" },
            { "24_k4_37x23>i4", "be54f79d5cf62759" },
            { "24_k4_37x23>i8", "c0e406ff061630b0" },
            { "24_k4_37x23>gif", "65012f800f564233" },
            { "32rgb_k1_37x23>i1", "a569b9b8102a3ead" },
            { "32rgb_k1_37x23>i4", "def486c9af1f46d4" },
            { "32rgb_k1_37x23>i8", "fee7b7e8ecea6f0f" },
            { "32rgb_k1_37x23>gif", "a4ce3d043a7351a4" },
            { "32rgb_k2_37x23>i1", "d054b85ec70f4cd3" },
            { "32rgb_k2_37x23>i4", "a9025b0e4b5c5ea6" },
            { "32rgb_k2_37x23>i8", "df01d6f085860a1c" },
            { "32rgb_k2_37x23>gif", "4f47b11f60011c9e" },
            { "32rgb_k4_37x23>i1", "3c8973e339aee4cc" },
            { "32rgb_k4_37x23>i4", "be54f79d5cf62759" },
            { "32rgb_k4_37x23>i8", "c0e406ff061630b0" },
            { "32rgb_k4_37x23>gif", "65012f800f564233" },
            { "555_k1_37x23>i1", "54d73562e7327670" },
            { "555_k1_37x23>i4", "b83f2693a54caaa6" },
            { "555_k1_37x23>i8", "0f52209ce46f5621" },
            { "555_k1_37x23>gif", "b1c143e5e102fb48" },
            { "555_k2_37x23>i1", "177366ae5df7ae43" },
            { "555_k2_37x23>i4", "a2e95f1cce285226" },
            { "555_k2_37x23>i8", "a3093df5fe4655e4" },
            { "555_k2_37x23>gif", "3fe916eb696b1bd2" },
            { "555_k4_37x23>i1", "3c8973e339aee4cc" },
            { "555_k4_37x23>i4", "be54f79d5cf62759" },
            { "555_k4_37x23>i8", "c0e406ff061630b0" },
            { "555_k4_37x23>gif", "5187d2bcf7463040" },
            { "565_k1_37x23>i1", "d7b90ab5f9013c7e" },
            { "565_k1_37x23>i4", "13c7dcb3e030c7e5" },
            { "565_k1_37x23>i8", "f8f7a40f65cb23d4" },
            { "565_k1_37x23>gif", "5b01ceda0ae5f446" },
            { "565_k2_37x23>i1", "06bd9bca0db5ff63" },
            { "565_k2_37x23>i4", "37ec1b9482997282" },
            { "565_k2_37x23>i8", "2cd0fe38badd0d04" },
            { "565_k2_37x23>gif", "4a1168da63a60cf2" },
            { "565_k4_37x23>i1", "3c8973e339aee4cc" },
            { "565_k4_37x23>i4", "be54f79d5cf62759" },
            { "565_k4_37x23>i8", "c0e406ff061630b0" },
            { "565_k4_37x23>gif", "e77ca823b85f168a" },
            { "48_k1_37x23>i1", "c60ff57bd0689a74" },
            { "48_k1_37x23>i4", "6df7803be07772b5" },
            { "48_k1_37x23>i8", "141e1914fbad1c67" },
            { "48_k1_37x23>gif", "cd8dfaae3c30a81b" },
            { "48_k2_37x23>i1", "8fe1543361caa1fa" },
            { "48_k2_37x23>i4", "59af1392b7d81363" },
            { "48_k2_37x23>i8", "495023304b2436ea" },
            { "48_k2_37x23>gif", "a083a7f9d1a72fa4" },
            { "48_k4_37x23>i1", "3c8973e339aee4cc" },
            { "48_k4_37x23>i4", "be54f79d5cf62759" },
            { "48_k4_37x23>i8", "c0e406ff061630b0" },
            { "48_k4_37x23>gif", "65012f800f564233" },
            { "i1_k0_p0_37x23>i1", "1057bfa36d20b272" },
            { "i1_k0_p0_37x23>i4", "f44591875f7f34d5" },
            { "i1_k0_p0_37x23>i8", "ebab4b16a582e7c4" },
            { "i1_k0_p0_37x23>gif", "a5fcd1deb1aa3505" },
            { "i1_k1_p0_37x23>i1", "5cafb31b61d8fc28" },
            { "i1_k1_p0_37x23>i4", "53548c66046a819e" },
            { "i1_k1_p0_37x23>i8", "5e42ec073c82865e" },
            { "i1_k1_p0_37x23>gif", "0a9dfecac2808ec8" },
            { "i1_k0_p1_37x23>i1", "98cc9f7f08cb1eb8" },
            { "i1_k0_p1_37x23>i4", "056bef8a2d0ad7d3" },
            { "i1_k0_p1_37x23>i8", "b9b998af7719fe4b" },
            { "i1_k0_p1_37x23>gif", "e0c3ef14abc6eaf9" },
            { "i1_k1_p1_37x23>i1", "a86e3720d39e894b" },
            { "i1_k1_p1_37x23>i4", "98ea987b0ba53f0c" },
            { "i1_k1_p1_37x23>i8", "04e2591b8fc85f6a" },
            { "i1_k1_p1_37x23>gif", "71135a8afb1fd47e" },
            { "i1_k0_p2_37x23>i1", "c8db132b4307c46e" },
            { "i1_k0_p2_37x23>i4", "dae61359ea23e4ed" },
            { "i1_k0_p2_37x23>i8", "32e1235832a21d9e" },
            { "i1_k0_p2_37x23>gif", "0c950e4e07c4d593" },
            { "i1_k1_p2_37x23>i1", "fec4be8d599f1b6c" },
            { "i1_k1_p2_37x23>i4", "fd84b699eb359233" },
            { "i1_k1_p2_37x23>i8", "cc1cb38f355cc9d5" },
            { "i1_k1_p2_37x23>gif", "7d524beb5b0e4f00" },
            { "i1_k0_p3_37x23>i1", "a0915c5286cfb8ae" },
            { "i1_k0_p3_37x23>i4", "228f771bfa676c46" },
            { "i1_k0_p3_37x23>i8", "637d4edaea4f7348" },
            { "i1_k0_p3_37x23>gif", "a5fcd1deb1aa3505" },
            { "i1_k1_p3_37x23>i1", "5871007d68ea7db2" },
            { "i1_k1_p3_37x23>i4", "2308a1e336e9531d" },
            { "i1_k1_p3_37x23>i8", "9bd471cfb67008fb" },
            { "i1_k1_p3_37x23>gif", "2f8391108e096233" },
            { "i1_k0_p4_37x23>i1", "360ad43799553c31" },
            { "i1_k0_p4_37x23>i4", "01093ffd241b0748" },
            { "i1_k0_p4_37x23>i8", "4accb8691042f982" },
            { "i1_k0_p4_37x23>gif", "a5debaf27d435e2e" },
            { "i1_k1_p4_37x23>i1", "203876aafc6af064" },
            { "i1_k1_p4_37x23>i4", "81514c8bb0abfcd2" },
            { "i1_k1_p4_37x23>i8", "f51f8d92916ff93c" },
            { "i1_k1_p4_37x23>gif", "0a6501d8f9bad491" },
            { "i4_k0_p0_37x23>i1", "e2e18f37162fc81c" },
            { "i4_k0_p0_37x23>i4", "73d611cf83c2c518" },
            { "i4_k0_p0_37x23>i8", "aba25a0f1c51b088" },
            { "i4_k0_p0_37x23>gif", "62fc7fac4f079b1c" },
            { "i4_k1_p0_37x23>i1", "ae2d9834f92e34da" },
            { "i4_k1_p0_37x23>i4", "df9733858e6c1821" },
            { "i4_k1_p0_37x23>i8", "7aa174012040b92f" },
            { "i4_k1_p0_37x23>gif", "cbfe496da5eda147" },
            { "i4_k0_p1_37x23>i1", "7380bbca57b6bd24" },
            { "i4_k0_p1_37x23>i4", "21255148ed0fc5f6" },
            { "i4_k0_p1_37x23>i8", "76fac12ad01c670c" },
            { "i4_k0_p1_37x23>gif", "3538e2c1ce52b7ff" },
            { "i4_k1_p1_37x23>i1", "9b24b2b755902357" },
            { "i4_k1_p1_37x23>i4", "4afd0b7ae29c2bfb" },
            { "i4_k1_p1_37x23>i8", "28177d43e414ce03" },
            { "i4_k1_p1_37x23>gif", "0469a64f584509e5" },
            { "i4_k0_p2_37x23>i1", "6aea4668472279f8" },
            { "i4_k0_p2_37x23>i4", "7d8fa9a2b3f2421c" },
            { "i4_k0_p2_37x23>i8", "d9f4c05e4777898c" },
            { "i4_k0_p2_37x23>gif", "f88fba0ac01d29c7" },
            { "i4_k1_p2_37x23>i1", "88c02d950c6a754d" },
            { "i4_k1_p2_37x23>i4", "4dd05b6419a86b54" },
            { "i4_k1_p2_37x23>i8", "ebf339d720766391" },
            { "i4_k1_p2_37x23>gif", "d30c3238bd5f7059" },
            { "i4_k0_p3_37x23>i1", "6a1abf36619577c5" },
            { "i4_k0_p3_37x23>i4", "7ad13188bf06e77f" },
            { "i4_k0_p3_37x23>i8", "d7e1a964b3ec52ad" },
            { "i4_k0_p3_37x23>gif", "934d7a9cf97b53fd" },
            { "i4_k1_p3_37x23>i1", "111a68223111f8b2" },
            { "i4_k1_p3_37x23>i4", "5cfcc3a7f2bb84bd" },
            { "i4_k1_p3_37x23>i8", "72b891bf004aefa5" },
            { "i4_k1_p3_37x23>gif", "83f1bc2f78efc57a" },
            { "i4_k0_p4_37x23>i1", "29b4577b508452c4" },
            { "i4_k0_p4_37x23>i4", "6242ab5825777b49" },
            { "i4_k0_p4_37x23>i8", "53e21caafee0e907" },
            { "i4_k0_p4_37x23>gif", "bdb8493bf253a510" },
            { "i4_k1_p4_37x23>i1", "1769dba03ea188cc" },
            { "i4_k1_p4_37x23>i4", "d2f1692ebf9e59bf" },
            { "i4_k1_p4_37x23>i8", "845816bbaaf02cf7" },
            { "i4_k1_p4_37x23>gif", "842295db5c932d5a" },
            { "i8_k0_p0_37x23>i1", "71cef26a2c90c3c2" },
            { "i8_k0_p0_37x23>i4", "efca828fc88a6f57" },
            { "i8_k0_p0_37x23>i8", "a35567a60f1c9b07" },
            { "i8_k0_p0_37x23>gif", "79da1ee97eeac4e8" },
            { "i8_k1_p0_37x23>i1", "010bcf9ee7bc2271" },
            { "i8_k1_p0_37x23>i4", "3a0f5e45e070db2b" },
            { "i8_k1_p0_37x23>i8", "9717b27c104cb2f6" },
            { "i8_k1_p0_37x23>gif", "b139ca78eb3c9533" },
            { "i8_k0_p1_37x23>i1", "048a56998d77c26e" },
            { "i8_k0_p1_37x23>i4", "d1b364a7a509feff" },
            { "i8_k0_p1_37x23>i8", "90428ac41879b2c6" },
            { "i8_k0_p1_37x23>gif", "b3f2b4db5e0fce67" },
            { "i8_k1_p1_37x23>i1", "8c156209b6832853" },
            { "i8_k1_p1_37x23>i4", "43bd1827504822c1" },
            { "i8_k1_p1_37x23>i8", "b271ec836d2a247b" },
            { "i8_k1_p1_37x23>gif", "739247ac3d473b52" },
            { "i8_k0_p2_37x23>i1", "ccb5bfe3385f0af6" },
            { "i8_k0_p2_37x23>i4", "075484825721113f" },
            { "i8_k0_p2_37x23>i8", "8e5a8d32eab0b9c5" },
            { "i8_k0_p2_37x23>gif", "6dea0e5dd26f9c8b" },
            { "i8_k1_p2_37x23>i1", "cf288c580305088d" },
            { "i8_k1_p2_37x23>i4", "e72b30f1e914f9cb" },
            { "i8_k1_p2_37x23>i8", "2810f484a8150c9f" },
            { "i8_k1_p2_37x23>gif", "ac37cef7b416cc6c" },
            { "i8_k0_p3_37x23>i1", "7e7e5fdcda398635" },
            { "i8_k0_p3_37x23>i4", "fbea6a8674259ebc" },
            { "i8_k0_p3_37x23>i8", "4b8089c29b5f71db" },
            { "i8_k0_p3_37x23>gif", "cc502710dddbddb1" },
            { "i8_k1_p3_37x23>i1", "f445a957aea203af" },
            { "i8_k1_p3_37x23>i4", "0dfa5adb0bbbc974" },
            { "i8_k1_p3_37x23>i8", "f8cec0695985a171" },
            { "i8_k1_p3_37x23>gif", "b9573471af28496f" },
            { "i8_k0_p4_37x23>i1", "40b9269f40010df6" },
            { "i8_k0_p4_37x23>i4", "3bd05f6b56592b4b" },
            { "i8_k0_p4_37x23>i8", "6109ef59ade1689e" },
            { "i8_k0_p4_37x23>gif", "1c4ed49f6f141781" },
            { "i8_k1_p4_37x23>i1", "7e5f8677f45a0a2c" },
            { "i8_k1_p4_37x23>i4", "d49c0c3887296faf" },
            { "i8_k1_p4_37x23>i8", "e6a9e374ef55b3f1" },
            { "i8_k1_p4_37x23>gif", "7e17c21addec58c2" },
            { "32a_k1_13x5>i1", "b6b7d925aef0d9c3" },
            { "32a_k1_13x5>i4", "eda2c154dfdc40e7" },
            { "32a_k1_13x5>i8", "9fa9d8c065aa2f00" },
            { "32a_k1_13x5>gif", "a972c6e7013fa3bf" },
            { "32a_k1_1x3>i1", "0258d8bec7e7b746" },
            { "32a_k1_1x3>i4", "b943f671e2834eb4" },
            { "32a_k1_1x3>i8", "402291e282910dbf" },
            { "32a_k1_1x3>gif", "1533fabd12c6df65" },
            { "32a_k1_64x48>i1", "7379e05dc46f4d83" },
            { "32a_k1_64x48>i4", "944f6fabe74902b3" },
            { "32a_k1_64x48>i8", "c59bc53f34372423" },
            { "32a_k1_64x48>gif", "1a95e336ce076cf9" },
            { "24_k1_13x5>i1", "5d0cbd204392796c" },
            { "24_k1_13x5>i4", "2a45a6f176743e3e" },
            { "24_k1_13x5>i8", "9f13ee1d90d1da0e" },
            { "24_k1_13x5>gif", "d5883864387bbd8b" },
            { "24_k1_1x3>i1", "13ef5f30d88714f9" },
            { "24_k1_1x3>i4", "e6a8716091efa2cb" },
            { "24_k1_1x3>i8", "89c96f951ea17009" },
            { "24_k1_1x3>gif", "8f50b4fbe27303e4" },
            { "24_k1_64x48>i1", "fe75c23150d71be8" },
            { "24_k1_64x48>i4", "35d0454cd2e498c7" },
            { "24_k1_64x48>i8", "67363ce6d7b70593" },
            { "24_k1_64x48>gif", "bfc95ad34c062740" },
            { "i1_k1_p0_13x5>i1", "396d412288551c94" },
            { "i1_k1_p0_13x5>i4", "ef6ea180e3d2bc3e" },
            { "i1_k1_p0_13x5>i8", "cb7a884b883f750c" },
            { "i1_k1_p0_13x5>gif", "c17f4e641665eb2d" },
            { "i1_k1_p0_1x3>i1", "576bd11c328fa6e0" },
            { "i1_k1_p0_1x3>i4", "52c1e798a8d3361b" },
            { "i1_k1_p0_1x3>i8", "d84ef15226addb37" },
            { "i1_k1_p0_1x3>gif", "07c6b1ba536363bc" },
            { "i1_k1_p0_64x48>i1", "80ab3f3da6ac2da6" },
            { "i1_k1_p0_64x48>i4", "fbe6aa41d27b77a9" },
            { "i1_k1_p0_64x48>i8", "dcaa095c9df423cf" },
            { "i1_k1_p0_64x48>gif", "1bbb2077a7ace38f" },
            { "i4_k1_p1_13x5>i1", "a52f4bb65ee559fe" },
            { "i4_k1_p1_13x5>i4", "4efbf1b09fc5486e" },
            { "i4_k1_p1_13x5>i8", "d9883b9c2ae29e4c" },
            { "i4_k1_p1_13x5>gif", "6e64a21cc47f6fdf" },
            { "i4_k1_p1_1x3>i1", "1e06f5e058dc3f7a" },
            { "i4_k1_p1_1x3>i4", "43c5fe5fe2a5c1a7" },
            { "i4_k1_p1_1x3>i8", "3feb59399d2460a9" },
            { "i4_k1_p1_1x3>gif", "74e5b998e4ad22cb" },
            { "i4_k1_p1_64x48>i1", "9a2893058cf816a5" },
            { "i4_k1_p1_64x48>i4", "4306327ebdf94a67" },
            { "i4_k1_p1_64x48>i8", "108ef9ce5dd364c1" },
            { "i4_k1_p1_64x48>gif", "b30796ca26098ec0" },
            { "24_k2_64x48>i1", "ea76de5d75325e1b" },
            { "24_k2_64x48>i4", "c813deffc07cef14" },
            { "24_k2_64x48>i8", "1d1b0e0012ae60ce" },
            { "24_k2_64x48>gif", "a0d37f3089be5fc0" },
        };

        [Fact]
        public void ConversionsIntoIndexedFormats_AreGdiPlusBytes()
        {
            var bad = new List<string>();
            int n = 0;
            foreach (KeyValuePair<string, string> kv in IndexedBattery.Run())
            {
                n++;
                if (!GdiPlus.TryGetValue(kv.Key, out string want)) bad.Add(kv.Key + ": no fixture");
                else if (want != kv.Value) bad.Add(kv.Key);
            }
            Assert.Equal(GdiPlus.Count, n);
            Assert.True(bad.Count == 0, bad.Count + " of " + n + " differ from GDI+: " + string.Join(", ", bad));
        }
    }

    internal static class IndexedBattery
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
            default: { int k = (x / 3 + y / 2) % 6; int[] pal = { 0xff0000, 0x00ff00, 0x0000ff, 0xffffff, 0x000000, 0x808080 }; r = pal [k] >> 16; g = (pal [k] >> 8) & 255; b = pal [k] & 255; break; }
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
        static ushort Lin (int c) { return s_lin [c]; }

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
                    case PixelFormat.Format1bppIndexed: { int n = pal != null ? Math.Min (2, pal.Length) : 2; int i = (int) ((uint) (r * 7 + g * 3 + b + a) % (uint) n); row [x >> 3] |= (byte) (i << (7 - (x & 7))); break; }
                    case PixelFormat.Format4bppIndexed: { int i = kind == 1 ? (int) (Rnd () & 15) : (r / 64) * 4 + g / 64; row [x >> 1] |= (byte) (i << ((x & 1) == 0 ? 4 : 0)); break; }
                    case PixelFormat.Format8bppIndexed: row [x] = (byte) (kind == 1 ? (int) (Rnd () & 255) : (r / 32) * 32 + (g / 32) * 4 + b / 64); break;
                    case PixelFormat.Format16bppRgb555: { int v = (r >> 3) << 10 | (g >> 3) << 5 | b >> 3; Put16 (row, x * 2, v); break; }
                    case PixelFormat.Format16bppArgb1555: { int v = (a >= 128 ? 0x8000 : 0) | (r >> 3) << 10 | (g >> 3) << 5 | b >> 3; Put16 (row, x * 2, v); break; }
                    case PixelFormat.Format16bppRgb565: { int v = (r >> 3) << 11 | (g >> 2) << 5 | b >> 3; Put16 (row, x * 2, v); break; }
                    case PixelFormat.Format24bppRgb: row [x * 3] = (byte) b; row [x * 3 + 1] = (byte) g; row [x * 3 + 2] = (byte) r; break;
                    case PixelFormat.Format32bppRgb:
                    case PixelFormat.Format32bppArgb: row [x * 4] = (byte) b; row [x * 4 + 1] = (byte) g; row [x * 4 + 2] = (byte) r; row [x * 4 + 3] = (byte) a; break;
                    case PixelFormat.Format32bppPArgb: row [x * 4] = (byte) ((b * a + 127) / 255); row [x * 4 + 1] = (byte) ((g * a + 127) / 255); row [x * 4 + 2] = (byte) ((r * a + 127) / 255); row [x * 4 + 3] = (byte) a; break;
                    case PixelFormat.Format48bppRgb: Put16 (row, x * 6, Lin (b)); Put16 (row, x * 6 + 2, Lin (g)); Put16 (row, x * 6 + 4, Lin (r)); break;
                    case PixelFormat.Format64bppArgb:
                    case PixelFormat.Format64bppPArgb: {
                        int a13 = a * 8192 / 255; int lb = Lin (b), lg = Lin (g), lr = Lin (r);
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
        static Color[] Palette (PixelFormat f, int which, uint seed)
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

        // The bytes a result is held to: format, size, stride, flags, palette, and every row's PIXELS
        // (never the padding, nor the bits after the last pixel: GDI+ leaves its heap there).
        static void Canon (BinaryWriter o, string op, Bitmap b, Rectangle r, PixelFormat f, bool palette)
        {
            BitmapData d = b.LockBits (r, ImageLockMode.ReadOnly, f);
            o.Write (op); o.Write ((int) f); o.Write (r.Width); o.Write (r.Height); o.Write (d.Stride); o.Write ((int) b.Flags);
            int bits = r.Width * Image.GetPixelFormatSize (f), full = bits / 8, rem = bits % 8;
            var row = new byte [Math.Abs (d.Stride)];
            for (int y = 0; y < r.Height; y++) {
                Marshal.Copy (new IntPtr (d.Scan0.ToInt64 () + (long) y * d.Stride), row, 0, row.Length);
                o.Write (row, 0, full);
                if (rem != 0) o.Write ((byte) (row [full] & (0xff << (8 - rem))));
            }
            b.UnlockBits (d);
            if (palette) {
                ColorPalette p = b.Palette;
                o.Write (p.Flags); o.Write (p.Entries.Length);
                foreach (Color c in p.Entries) o.Write (c.ToArgb ());
            }
        }

        static readonly PixelFormat[] s_dst = { PixelFormat.Format1bppIndexed, PixelFormat.Format4bppIndexed, PixelFormat.Format8bppIndexed };

        static string Short (PixelFormat f)
        {
            switch (f) {
            case PixelFormat.Format1bppIndexed: return "i1"; case PixelFormat.Format4bppIndexed: return "i4"; case PixelFormat.Format8bppIndexed: return "i8";
            case PixelFormat.Format16bppRgb555: return "555"; case PixelFormat.Format16bppRgb565: return "565"; case PixelFormat.Format16bppArgb1555: return "1555";
            case PixelFormat.Format24bppRgb: return "24"; case PixelFormat.Format32bppRgb: return "32rgb"; case PixelFormat.Format32bppArgb: return "32a"; case PixelFormat.Format32bppPArgb: return "32p";
            case PixelFormat.Format48bppRgb: return "48"; case PixelFormat.Format64bppArgb: return "64a"; case PixelFormat.Format64bppPArgb: return "64p";
            }
            return ((int) f).ToString ("x");
        }

        // One source's results into one destination format: Clone of the whole, Clone of a rectangle at
        // an odd column, Clone(RectangleF), LockBits whole and at an odd column, and (indexed sources)
        // pixels of another format written in through a lock.
        static void Ops (BinaryWriter o, PixelFormat sf, int w, int h, int kind, uint seed, Color[] pal, PixelFormat df)
        {
            bool idx = (sf & PixelFormat.Indexed) != 0;
            for (int op = 0; op < 6; op++) {
                if ((op == 1 || op == 4) && w < 5) continue;
                if (op == 5 && !idx) continue;
                try {
                    using (Bitmap src = Source (sf, w, h, kind, seed, pal)) {
                        Rectangle r = op == 1 || op == 4 ? new Rectangle (3, 1, w - 4, h - 1) : new Rectangle (0, 0, w, h);
                        if (op == 5) {
                            PixelFormat wf = df == PixelFormat.Format1bppIndexed ? PixelFormat.Format32bppArgb : df == PixelFormat.Format4bppIndexed ? PixelFormat.Format24bppRgb : PixelFormat.Format8bppIndexed;
                            if (wf == sf) wf = PixelFormat.Format16bppRgb565;
                            byte[] wbits; int ws;
                            using (Bitmap pix = Source (wf, w, h, (kind + 1) % 6, seed + 1, null)) {
                                BitmapData pd = pix.LockBits (new Rectangle (0, 0, w, h), ImageLockMode.ReadOnly, wf);
                                ws = pd.Stride; wbits = new byte [ws * h];
                                Marshal.Copy (pd.Scan0, wbits, 0, wbits.Length);
                                pix.UnlockBits (pd);
                            }
                            BitmapData d = src.LockBits (r, ImageLockMode.WriteOnly, wf);
                            for (int y = 0; y < h; y++) Marshal.Copy (wbits, y * ws, new IntPtr (d.Scan0.ToInt64 () + (long) y * d.Stride), Math.Min (ws, d.Stride));
                            src.UnlockBits (d);
                            Canon (o, "write", src, r, sf, true);
                        } else if (op == 3 || op == 4) {
                            Canon (o, "lock" + op, src, r, df, false);
                        } else {
                            Bitmap c = op == 2 ? src.Clone (new RectangleF (0.6f, 0.4f, w - 0.7f, h - 0.2f), df) : src.Clone (r, df);
                            using (c) Canon (o, "clone" + op, c, new Rectangle (0, 0, c.Width, c.Height), c.PixelFormat, true);
                        }
                    }
                } catch (Exception e) {
                    o.Write ("throws " + op + " " + e.GetType ().Name);
                }
            }
        }

        static byte[] Gif (PixelFormat sf, int w, int h, int kind, uint seed, Color[] pal)
        {
            using (Bitmap src = Source (sf, w, h, kind, seed, pal)) {
                var ms = new MemoryStream ();
                src.Save (ms, ImageFormat.Gif);
                return ms.ToArray ();
            }
        }

        static string Digest (byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create ()) {
                byte[] hsh = sha.ComputeHash (bytes);
                var sb = new StringBuilder ();
                for (int i = 0; i < 8; i++) sb.Append (hsh [i].ToString ("x2"));
                return sb.ToString ();
            }
        }

        public struct Case { public string Name; public PixelFormat Src; public int W, H, Kind, Pal; public uint Seed; }

        public static List<Case> Cases ()
        {
            var all = new List<Case> ();
            PixelFormat[] alpha = { PixelFormat.Format32bppArgb, PixelFormat.Format32bppPArgb, PixelFormat.Format64bppArgb, PixelFormat.Format64bppPArgb, PixelFormat.Format16bppArgb1555 };
            PixelFormat[] opaque = { PixelFormat.Format24bppRgb, PixelFormat.Format32bppRgb, PixelFormat.Format16bppRgb555, PixelFormat.Format16bppRgb565, PixelFormat.Format48bppRgb };
            PixelFormat[] indexed = { PixelFormat.Format1bppIndexed, PixelFormat.Format4bppIndexed, PixelFormat.Format8bppIndexed };
            foreach (PixelFormat f in alpha) foreach (int k in new [] { 0, 1, 2, 3 }) Add (all, f, 37, 23, k, 0);
            foreach (PixelFormat f in opaque) foreach (int k in new [] { 1, 2, 4 }) Add (all, f, 37, 23, k, 0);
            foreach (PixelFormat f in indexed) for (int p = 0; p < 5; p++) foreach (int k in new [] { 0, 1 }) Add (all, f, 37, 23, k, p);
            foreach (PixelFormat f in new [] { PixelFormat.Format32bppArgb, PixelFormat.Format24bppRgb, PixelFormat.Format1bppIndexed, PixelFormat.Format4bppIndexed })
                foreach (int[] s in new [] { new [] { 13, 5 }, new [] { 1, 3 }, new [] { 64, 48 } }) Add (all, f, s [0], s [1], 1, f == PixelFormat.Format4bppIndexed ? 1 : 0);
            Add (all, PixelFormat.Format24bppRgb, 64, 48, 2, 0);
            return all;
        }

        static void Add (List<Case> all, PixelFormat f, int w, int h, int kind, int pal)
        {
            var c = new Case ();
            c.Src = f; c.W = w; c.H = h; c.Kind = kind; c.Pal = pal;
            c.Seed = (uint) (kind * 7919 + pal * 104729 + w * 31 + h + (int) f);
            bool idx = (f & PixelFormat.Indexed) != 0;
            c.Name = Short (f) + "_k" + kind + (idx ? "_p" + pal : "") + "_" + w + "x" + h;
            all.Add (c);
        }

        /// <summary>name -> digest for every case: one per (source, destination format) over its
        /// clone/lock/write results, and one per source over its GIF file.</summary>
        public static List<KeyValuePair<string, string>> Run ()
        {
            var res = new List<KeyValuePair<string, string>> ();
            foreach (Case c in Cases ()) {
                Color[] pal = (c.Src & PixelFormat.Indexed) != 0 ? Palette (c.Src, c.Pal, c.Seed ^ 0x5a5a) : null;
                foreach (PixelFormat df in s_dst) {
                    var ms = new MemoryStream ();
                    using (var o = new BinaryWriter (ms)) Ops (o, c.Src, c.W, c.H, c.Kind, c.Seed, pal, df);
                    res.Add (new KeyValuePair<string, string> (c.Name + ">" + Short (df), Digest (ms.ToArray ())));
                }
                res.Add (new KeyValuePair<string, string> (c.Name + ">gif", Digest (Gif (c.Src, c.W, c.H, c.Kind, c.Seed, pal))));
            }
            return res;
        }
    }
}
