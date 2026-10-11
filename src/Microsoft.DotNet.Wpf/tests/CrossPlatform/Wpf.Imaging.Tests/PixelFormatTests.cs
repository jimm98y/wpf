// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Bitmaps whose pixels are not already 32 bits wide.
//
// Off-Windows there is no WIC, so every format conversion runs in managed code, and the managed
// converter was written for the case that was in front of it: a source that is already 32bpp. It
// steps the source four bytes per pixel unconditionally. Every other format therefore reads the
// wrong bytes -- Bgr24 skews progressively across each row, Gray8 reads four pixels' worth per
// pixel, and the SUB-BYTE formats (BlackWhite, Gray2, Gray4, Indexed1/2/4, where a byte holds two,
// four or eight pixels) are not merely wrong but read far past the end of the buffer.
//
// Those formats are not exotic. A BlackWhite or Indexed4 bitmap is what a fax, an icon mask, a
// scanned document or any palettised PNG or BMP decodes to, and CopyPixelsForManagedComposition
// routes every one of them through this converter on its way to the screen.
//
// The assertions are exact. A 1bpp bitmap's pixels are black or white and nothing else, so there is
// no tolerance to hide behind: either the bits were unpacked in the right order or the picture is
// wrong. Odd widths are used throughout because the interesting bugs live in the last, partly-used
// byte of each row -- a 12-pixel-wide 1bpp row is one and a half bytes, and the stride rounds up.
//

using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Wpf.Imaging.Tests
{
    public class PixelFormatTests
    {
        /// <summary>Straight BGRA32 bytes of <paramref name="source"/>, via the managed converter.</summary>
        private static byte[] ToBgra32(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            return pixels;
        }

        private static (byte B, byte G, byte R, byte A) PixelAt(byte[] bgra, int width, int x, int y)
        {
            int i = (y * width + x) * 4;
            return (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
        }

        // ---- 1 bit per pixel -------------------------------------------------------------

        /// <summary>
        /// BlackWhite: one bit per pixel, MOST significant bit first, 0 = black and 1 = white. A
        /// converter that unpacked the bits in the other order would still produce a plausible
        /// black-and-white picture, which is why the pattern below is asymmetric.
        /// </summary>
        [Fact]
        public void BlackWhiteUnpacksEachBitInOrder()
        {
            const int W = 12, H = 3;            // 12 bits = 1.5 bytes per row, so the row ends mid-byte
            int stride = (W + 7) / 8;           // 2
            var bits = new byte[stride * H];

            // 1011 0000 1111 ....  -- deliberately not a palindrome in either direction
            bool[] row = { true, false, true, true, false, false, false, false, true, true, true, true };
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (row[x])
                        bits[y * stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));

            var src = BitmapSource.Create(W, H, 96, 96, PixelFormats.BlackWhite, null, bits, stride);
            byte[] bgra = ToBgra32(src);

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    (byte b, byte g, byte r, byte a) = PixelAt(bgra, W, x, y);
                    byte expected = row[x] ? (byte)255 : (byte)0;
                    Assert.True(b == expected && g == expected && r == expected,
                        $"({x},{y}) should be {(row[x] ? "white" : "black")}, was ({b},{g},{r})");
                    Assert.Equal(255, a);
                }
            }
        }

        /// <summary>
        /// Indexed1 with a palette that is NOT black and white, so a converter that quietly treated
        /// an indexed bitmap as greyscale -- or ignored the palette -- cannot pass.
        /// </summary>
        [Fact]
        public void Indexed1LooksUpItsPalette()
        {
            const int W = 9, H = 2;
            int stride = (W + 7) / 8;           // 2, with 7 bits of the second byte unused
            var bits = new byte[stride * H];
            bool[] row = { false, true, true, false, true, false, false, true, true };
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (row[x])
                        bits[y * stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));

            var palette = new BitmapPalette(new[]
            {
                Color.FromRgb(0x20, 0x40, 0x60),
                Color.FromRgb(0xE0, 0xC0, 0xA0),
            });

            var src = BitmapSource.Create(W, H, 96, 96, PixelFormats.Indexed1, palette, bits, stride);
            byte[] bgra = ToBgra32(src);

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    (byte b, byte g, byte r, _) = PixelAt(bgra, W, x, y);
                    Color expected = palette.Colors[row[x] ? 1 : 0];
                    Assert.True(r == expected.R && g == expected.G && b == expected.B,
                        $"({x},{y}) should be palette entry {(row[x] ? 1 : 0)} " +
                        $"({expected.R},{expected.G},{expected.B}), was ({r},{g},{b})");
                }
            }
        }

        // ---- 2 and 4 bits per pixel ------------------------------------------------------

        /// <summary>
        /// Gray4: four bits per pixel, two pixels to a byte, high nibble first. The values chosen
        /// distinguish the two nibbles AND catch a converter that forgot to scale 0..15 up to 0..255.
        /// </summary>
        [Fact]
        public void Gray4UnpacksBothNibblesAndScalesTo8Bit()
        {
            const int W = 6, H = 1;
            int stride = (W * 4 + 7) / 8;       // 3
            byte[] levels = { 0, 15, 5, 10, 1, 14 };
            var packed = new byte[stride * H];
            for (int x = 0; x < W; x++)
            {
                int shift = (x & 1) == 0 ? 4 : 0;
                packed[x / 2] |= (byte)(levels[x] << shift);
            }

            var src = BitmapSource.Create(W, H, 96, 96, PixelFormats.Gray4, null, packed, stride);
            byte[] bgra = ToBgra32(src);

            for (int x = 0; x < W; x++)
            {
                (byte b, byte g, byte r, _) = PixelAt(bgra, W, x, 0);
                // 0..15 spread over 0..255: 15 must reach full white and 0 must be full black,
                // which a naive (v << 4) gets wrong at the top end (15 -> 240).
                int expected = levels[x] * 255 / 15;
                Assert.True(Math.Abs(r - expected) <= 1 && r == g && g == b,
                    $"pixel {x} (level {levels[x]}/15) should be {expected}, was ({r},{g},{b})");
            }
        }

        [Fact]
        public void Gray2UnpacksFourPixelsPerByte()
        {
            const int W = 4, H = 1;
            byte[] levels = { 0, 1, 2, 3 };
            var packed = new byte[1];
            for (int x = 0; x < W; x++) packed[0] |= (byte)(levels[x] << (6 - x * 2));

            var src = BitmapSource.Create(W, H, 96, 96, PixelFormats.Gray2, null, packed, 1);
            byte[] bgra = ToBgra32(src);

            for (int x = 0; x < W; x++)
            {
                (_, _, byte r, _) = PixelAt(bgra, W, x, 0);
                int expected = levels[x] * 255 / 3;
                Assert.True(Math.Abs(r - expected) <= 1,
                    $"pixel {x} (level {levels[x]}/3) should be {expected}, was {r}");
            }
        }

        [Fact]
        public void Indexed4LooksUpItsPalette()
        {
            const int W = 5, H = 2;
            int stride = (W * 4 + 7) / 8;       // 3, last nibble unused
            byte[] indices = { 0, 3, 1, 2, 3 };
            var packed = new byte[stride * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    packed[y * stride + x / 2] |= (byte)(indices[x] << ((x & 1) == 0 ? 4 : 0));

            var palette = new BitmapPalette(new[]
            {
                Color.FromRgb(10, 20, 30), Color.FromRgb(40, 50, 60),
                Color.FromRgb(70, 80, 90), Color.FromRgb(200, 210, 220),
            });

            var src = BitmapSource.Create(W, H, 96, 96, PixelFormats.Indexed4, palette, packed, stride);
            byte[] bgra = ToBgra32(src);

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    (byte b, byte g, byte r, _) = PixelAt(bgra, W, x, y);
                    Color expected = palette.Colors[indices[x]];
                    Assert.True(r == expected.R && g == expected.G && b == expected.B,
                        $"({x},{y}) should be palette entry {indices[x]}, was ({r},{g},{b})");
                }
            }
        }

        /// <summary>
        /// CopyPixels of a sub-rect whose X does NOT land on a byte boundary. For a 1bpp bitmap only
        /// every eighth column does, so this is the ordinary case rather than a corner one, and the
        /// bits have to be shifted rather than the enclosing bytes copied.
        /// </summary>
        [Fact]
        public void CopyPixelsFromAMidByteColumnShiftsTheBits()
        {
            const int W = 16, H = 1;
            int stride = 2;
            // 1100 1010 0110 0001
            bool[] row = { true, true, false, false, true, false, true, false,
                           false, true, true, false, false, false, false, true };
            var bits = new byte[stride * H];
            for (int x = 0; x < W; x++)
                if (row[x]) bits[x >> 3] |= (byte)(0x80 >> (x & 7));

            var src = BitmapSource.Create(W, H, 96, 96, PixelFormats.BlackWhite, null, bits, stride);

            // Three pixels starting at column 3: expect bits 3,4,5 == false,true,false, packed into
            // the TOP of the first returned byte.
            var outBits = new byte[1];
            src.CopyPixels(new System.Windows.Int32Rect(3, 0, 3, 1), outBits, 1, 0);

            for (int i = 0; i < 3; i++)
            {
                bool got = (outBits[0] & (0x80 >> i)) != 0;
                Assert.True(got == row[3 + i],
                    $"bit {i} of the copied run should be pixel {3 + i} ({row[3 + i]}), was {got}");
            }
        }

        // ---- converting TO a narrow format -------------------------------------------------

        /// <summary>
        /// Asking for a sub-byte destination has to give back a bitmap that IS that format -- packed
        /// pixels, and Format saying so. Returning 32bpp under the requested format's name meant an
        /// application that quantised an image and then read Format, or saved it, was told something
        /// untrue while the bytes said otherwise.
        /// </summary>
        [Fact]
        public void ConvertingToBlackWhitePacksOneBitPerPixel()
        {
            const int W = 8, H = 2;
            var bgra = new byte[W * H * 4];
            // Alternate near-black and near-white so the luminance threshold has an obvious answer.
            for (int i = 0; i < W * H; i++)
            {
                byte v = (i % 2 == 0) ? (byte)20 : (byte)230;
                bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = v;
                bgra[i * 4 + 3] = 255;
            }
            var src = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, bgra, W * 4);

            var bw = new FormatConvertedBitmap(src, PixelFormats.BlackWhite, null, 0);

            Assert.Equal(PixelFormats.BlackWhite, bw.Format);
            Assert.Equal(1, bw.Format.BitsPerPixel);

            var packed = new byte[H];               // one byte per 8-pixel row
            bw.CopyPixels(packed, 1, 0);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    bool bit = (packed[y] & (0x80 >> x)) != 0;
                    bool expected = ((y * W + x) % 2) != 0;
                    Assert.True(bit == expected, $"({x},{y}) should be {(expected ? "white" : "black")}");
                }
        }

        /// <summary>
        /// An indexed destination matches each colour to its NEAREST palette entry. The source
        /// colours here are deliberately not exact palette entries, so a converter that only handled
        /// exact matches, or that ignored the palette and wrote a luminance ramp, cannot pass.
        /// </summary>
        [Fact]
        public void ConvertingToIndexed4PicksTheNearestPaletteEntry()
        {
            var palette = new BitmapPalette(new[]
            {
                Color.FromRgb(0, 0, 0), Color.FromRgb(255, 0, 0),
                Color.FromRgb(0, 255, 0), Color.FromRgb(0, 0, 255),
            });

            const int W = 4, H = 1;
            // Nearly-black, a dull red, a dull green, a dull blue -- as (R,G,B), none of them an
            // exact palette entry, so only real nearest-colour matching lands on the right index.
            (byte R, byte G, byte B)[] wanted =
            {
                (8, 8, 8), (200, 10, 10), (10, 200, 10), (10, 10, 200),
            };
            int[] expectedIndex = { 0, 1, 2, 3 };

            var bgra = new byte[W * 4];
            for (int x = 0; x < W; x++)
            {
                bgra[x * 4] = wanted[x].B;
                bgra[x * 4 + 1] = wanted[x].G;
                bgra[x * 4 + 2] = wanted[x].R;
                bgra[x * 4 + 3] = 255;
            }
            var src = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, bgra, W * 4);

            var indexed = new FormatConvertedBitmap(src, PixelFormats.Indexed4, palette, 0);

            Assert.Equal(PixelFormats.Indexed4, indexed.Format);
            Assert.NotNull(indexed.Palette);

            var packed = new byte[2];               // 4 pixels x 4 bits
            indexed.CopyPixels(packed, 2, 0);
            for (int x = 0; x < W; x++)
            {
                int got = (packed[x / 2] >> ((x & 1) == 0 ? 4 : 0)) & 0xF;
                Assert.True(got == expectedIndex[x],
                    $"pixel {x} should map to palette entry {expectedIndex[x]}, got {got}");
            }
        }

        /// <summary>
        /// The round trip, which is what actually proves the two halves agree: an Indexed4 bitmap
        /// converted to Bgra32 and back must land on the same indices. Either direction alone can be
        /// self-consistently wrong -- both wrong in the same way cancels out only if they really are
        /// inverses.
        /// </summary>
        [Fact]
        public void IndexedSurvivesARoundTripThroughBgra32()
        {
            var palette = new BitmapPalette(new[]
            {
                Color.FromRgb(12, 34, 56), Color.FromRgb(200, 30, 40),
                Color.FromRgb(30, 200, 40), Color.FromRgb(240, 240, 240),
            });

            const int W = 6, H = 3;
            int stride = (W * 4 + 7) / 8;
            byte[] indices = { 0, 1, 2, 3, 2, 1 };
            var packed = new byte[stride * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    packed[y * stride + x / 2] |= (byte)(indices[x] << ((x & 1) == 0 ? 4 : 0));

            var src = BitmapSource.Create(W, H, 96, 96, PixelFormats.Indexed4, palette, packed, stride);

            var expanded = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            var back = new FormatConvertedBitmap(expanded, PixelFormats.Indexed4, palette, 0);

            Assert.Equal(PixelFormats.Indexed4, back.Format);
            var got = new byte[stride * H];
            back.CopyPixels(got, stride, 0);
            Assert.Equal(packed, got);
        }

        /// <summary>Gray4 as a destination: luminance quantised to sixteen levels, two to a byte.</summary>
        [Fact]
        public void ConvertingToGray4QuantisesLuminance()
        {
            const int W = 4, H = 1;
            byte[] luma = { 0, 85, 170, 255 };
            var bgra = new byte[W * 4];
            for (int x = 0; x < W; x++)
            {
                bgra[x * 4] = bgra[x * 4 + 1] = bgra[x * 4 + 2] = luma[x];
                bgra[x * 4 + 3] = 255;
            }
            var src = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, bgra, W * 4);

            var gray = new FormatConvertedBitmap(src, PixelFormats.Gray4, null, 0);

            Assert.Equal(PixelFormats.Gray4, gray.Format);
            var packed = new byte[2];
            gray.CopyPixels(packed, 2, 0);
            for (int x = 0; x < W; x++)
            {
                int got = (packed[x / 2] >> ((x & 1) == 0 ? 4 : 0)) & 0xF;
                int expected = luma[x] * 15 / 255;
                Assert.True(Math.Abs(got - expected) <= 1,
                    $"pixel {x} (luma {luma[x]}) should quantise to {expected}/15, got {got}");
            }
        }

        // ---- premultiplication ------------------------------------------------------------

        /// <summary>
        /// Converting TO Pbgra32 must give back premultiplied bytes, and say so. The XPS/PDF image
        /// path asks for Pbgra32 exactly so it can undo the premultiplication itself, so handing it
        /// straight colour under a premultiplied label makes it divide by alpha a second time and
        /// wash every half-transparent pixel out.
        /// </summary>
        [Fact]
        public void ConvertingToPbgra32PremultipliesAndSaysSo()
        {
            // Straight mid-grey at half alpha.
            var straight = new byte[] { 128, 128, 128, 128 };
            var src = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, straight, 4);

            var converted = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
            var got = new byte[4];
            converted.CopyPixels(got, 4, 0);

            Assert.Equal(PixelFormats.Pbgra32, converted.Format);
            Assert.Equal(128, got[3]);
            // 128 * 128/255 == 64, give or take the rounding.
            Assert.True(Math.Abs(got[0] - 64) <= 2, $"blue should be premultiplied to ~64, was {got[0]}");
            Assert.True(Math.Abs(got[1] - 64) <= 2, $"green should be premultiplied to ~64, was {got[1]}");
            Assert.True(Math.Abs(got[2] - 64) <= 2, $"red should be premultiplied to ~64, was {got[2]}");
        }

        /// <summary>And the other direction: a premultiplied SOURCE is undone exactly once.</summary>
        [Fact]
        public void ConvertingFromPbgra32UnpremultipliesOnce()
        {
            // Premultiplied: a mid-grey at half alpha is stored at quarter strength.
            var premultiplied = new byte[] { 64, 64, 64, 128 };
            var src = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, premultiplied, 4);

            byte[] bgra = ToBgra32(src);

            Assert.Equal(128, bgra[3]);
            // 64 at an alpha of 128 is a mid-grey at full strength -- ~127, not 64 and not 253.
            Assert.True(Math.Abs(bgra[0] - 127) <= 2, $"blue should un-premultiply to ~127, was {bgra[0]}");
            Assert.True(Math.Abs(bgra[2] - 127) <= 2, $"red should un-premultiply to ~127, was {bgra[2]}");
        }

        // ---- the path the screen actually uses -------------------------------------------

        /// <summary>
        /// CopyPixelsForManagedComposition is what hands a bitmap to the compositor, and it is the
        /// reason any of this matters: a sub-byte bitmap that converts correctly in isolation is
        /// still wrong on screen if the route to the backend does not use the same expansion. It is
        /// internal, so this reaches it by reflection rather than re-deriving what it does.
        /// </summary>
        [Fact]
        public void ASubByteBitmapReachesTheCompositorAsCorrectBgra()
        {
            const int W = 8, H = 2;
            bool[] row = { true, false, false, true, true, true, false, true };
            var bits = new byte[H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (row[x]) bits[y] |= (byte)(0x80 >> x);

            var src = BitmapSource.Create(W, H, 96, 96, PixelFormats.BlackWhite, null, bits, 1);

            var method = typeof(BitmapSource).GetMethod(
                "CopyPixelsForManagedComposition",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);

            object[] args = { 0, 0, 0 };
            var pixels = (byte[])method.Invoke(src, args);
            int width = (int)args[0], height = (int)args[1];

            Assert.Equal(W, width);
            Assert.Equal(H, height);
            Assert.NotNull(pixels);

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    byte expected = row[x] ? (byte)255 : (byte)0;
                    Assert.True(pixels[i] == expected && pixels[i + 1] == expected && pixels[i + 2] == expected,
                        $"({x},{y}) reached the compositor as ({pixels[i]},{pixels[i + 1]},{pixels[i + 2]}), " +
                        $"expected {expected}");
                    Assert.Equal(255, pixels[i + 3]);
                }
            }
        }

        /// <summary>
        /// A palettized WriteableBitmap. Creating one used to throw outright, so nothing downstream
        /// of it was ever reached; this checks the packed bytes survive a write-and-read round trip
        /// and that the palette is still attached afterwards.
        /// </summary>
        [Fact]
        public void APalettizedWriteableBitmapRoundTripsItsIndices()
        {
            var palette = new BitmapPalette(new[]
            {
                Color.FromRgb(0, 0, 0), Color.FromRgb(255, 0, 0),
                Color.FromRgb(0, 255, 0), Color.FromRgb(0, 0, 255),
            });

            const int W = 4, H = 2;
            var wb = new WriteableBitmap(W, H, 96, 96, PixelFormats.Indexed8, palette);
            byte[] indices = { 0, 1, 2, 3, 3, 2, 1, 0 };
            wb.WritePixels(new System.Windows.Int32Rect(0, 0, W, H), indices, W, 0);

            var read = new byte[W * H];
            wb.CopyPixels(read, W, 0);
            Assert.Equal(indices, read);

            Assert.NotNull(wb.Palette);
            Assert.Equal(4, wb.Palette.Colors.Count);
        }

        // ---- writing a sub-byte rectangle that does not start on a byte -------------------
        //
        // WritePixels turns its rectangle into a bit offset and a bit count (X * bitsPerPixel), and
        // for a 4bpp or 1bpp format an odd X or an odd Width lands mid-byte. That used to throw
        // PlatformNotSupportedException("Sub-byte pixel copies require native milcore.") -- so a
        // WriteableBitmap of one of these formats could be created and read, but only written in
        // whole bytes. The byte a copy starts or ends in is shared with pixels the caller did not
        // ask to touch, which is what makes these assertions worth stating exactly: the neighbours
        // must come back unchanged, not merely approximately right.

        private static BitmapPalette SixteenColours()
        {
            var colours = new Color[16];
            for (int i = 0; i < colours.Length; i++) colours[i] = Color.FromRgb((byte)(i * 17), 0, 0);
            return new BitmapPalette(colours);
        }

        /// <summary>Two 4bpp pixels written at an odd X: the copy starts mid-byte and crosses into the next.</summary>
        [Fact]
        public void WritingIndexed4AtAnOddXLeavesTheNeighbouringPixelsAlone()
        {
            var wb = new WriteableBitmap(4, 1, 96, 96, PixelFormats.Indexed4, SixteenColours());

            // Pixels 1,2,3,4 -- two per byte, high nibble first.
            wb.WritePixels(new System.Windows.Int32Rect(0, 0, 4, 1), new byte[] { 0x12, 0x34 }, 2, 0);

            // Overwrite the middle two with A,B, starting at X=1 (bit offset 4).
            wb.WritePixels(new System.Windows.Int32Rect(0, 0, 2, 1), new byte[] { 0xAB }, 1,
                           destinationX: 1, destinationY: 0);

            var read = new byte[2];
            wb.CopyPixels(read, 2, 0);
            Assert.Equal(new byte[] { 0x1A, 0xB4 }, read);
        }

        /// <summary>
        /// Three 1bpp pixels read from an odd X and written to a different odd X -- source and
        /// destination misaligned by different amounts, and a width that is not a whole byte.
        /// </summary>
        [Fact]
        public void WritingBlackWhiteAcrossDifferentBitOffsetsMovesOnlyTheRequestedBits()
        {
            var wb = new WriteableBitmap(8, 1, 96, 96, PixelFormats.BlackWhite, null);
            wb.WritePixels(new System.Windows.Int32Rect(0, 0, 8, 1), new byte[] { 0xAA }, 1, 0);

            // Source 0b0001_1100: bits 3,4,5 are set. Take those three, put them at bit 2.
            wb.WritePixels(new System.Windows.Int32Rect(3, 0, 3, 1), new byte[] { 0x1C }, 1,
                           destinationX: 2, destinationY: 0);

            var read = new byte[1];
            wb.CopyPixels(read, 1, 0);

            // 0b1010_1010 with bits 2,3,4 set -> 0b1011_1010.
            Assert.Equal(new byte[] { 0xBA }, read);
        }

        /// <summary>The same misaligned write over several rows, so each row is offset independently.</summary>
        [Fact]
        public void AMisalignedSubByteWriteAppliesToEveryRow()
        {
            var wb = new WriteableBitmap(4, 3, 96, 96, PixelFormats.Indexed4, SixteenColours());
            wb.WritePixels(new System.Windows.Int32Rect(0, 0, 4, 3),
                           new byte[] { 0x12, 0x34, 0x12, 0x34, 0x12, 0x34 }, 2, 0);

            wb.WritePixels(new System.Windows.Int32Rect(0, 0, 2, 3),
                           new byte[] { 0xAB, 0xCD, 0xEF }, 1,
                           destinationX: 1, destinationY: 0);

            var read = new byte[6];
            wb.CopyPixels(read, 2, 0);
            Assert.Equal(new byte[] { 0x1A, 0xB4, 0x1C, 0xD4, 0x1E, 0xF4 }, read);
        }

        // ---- the byte-wide formats, which are broken the same way ------------------------

        [Fact]
        public void Gray8BecomesNeutralGrey()
        {
            const int W = 4, H = 2;
            byte[] grey = { 0, 64, 128, 255, 255, 128, 64, 0 };
            var src = BitmapSource.Create(W, H, 96, 96, PixelFormats.Gray8, null, grey, W);
            byte[] bgra = ToBgra32(src);

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    (byte b, byte g, byte r, byte a) = PixelAt(bgra, W, x, y);
                    byte expected = grey[y * W + x];
                    Assert.True(r == expected && g == expected && b == expected,
                        $"({x},{y}) should be grey {expected}, was ({r},{g},{b})");
                    Assert.Equal(255, a);
                }
            }
        }

        /// <summary>
        /// Bgr24 with an odd width, so the row is 15 bytes and the stride pads to 16. Reading four
        /// bytes per pixel here skews the image progressively along each row -- the classic symptom.
        /// </summary>
        [Fact]
        public void Bgr24KeepsItsChannelsAndRowPadding()
        {
            const int W = 5, H = 3;
            int stride = (W * 3 + 3) & ~3;      // 16: pad each row to a 4-byte boundary
            var bytes = new byte[stride * H];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int i = y * stride + x * 3;
                    bytes[i] = (byte)(x * 40);              // B
                    bytes[i + 1] = (byte)(y * 60);          // G
                    bytes[i + 2] = (byte)(255 - x * 40);    // R
                }
            }

            var src = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgr24, null, bytes, stride);
            byte[] bgra = ToBgra32(src);

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    (byte b, byte g, byte r, byte a) = PixelAt(bgra, W, x, y);
                    Assert.True(b == x * 40 && g == y * 60 && r == 255 - x * 40,
                        $"({x},{y}) should be ({x * 40},{y * 60},{255 - x * 40}) BGR, was ({b},{g},{r})");
                    Assert.Equal(255, a);
                }
            }
        }
    }
}
