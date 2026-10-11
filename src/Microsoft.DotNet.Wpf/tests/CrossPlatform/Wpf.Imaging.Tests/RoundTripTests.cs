// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Encode, then decode, then compare with what went in.
//
// This is the check whose absence let GIF and TIFF ship with encoders and no decoders: both formats
// could be WRITTEN for months and neither could be read back, and the build was green throughout
// because nothing ever asked for the round trip. A test that only encodes proves the encoder does
// not throw; a test that only decodes needs a fixture file somebody has to produce. The pair is what
// has an exact answer.
//
// Where a format is lossless the comparison is PIXEL-EXACT, alpha included, at several sizes
// including deliberately awkward ones -- 13x7 and 17x33 exercise the padded right and bottom edges
// where a stride bug hides, and 1x1 catches the degenerate case.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Wpf.Imaging.Tests
{
    public class RoundTripTests
    {
        public static TheoryData<int, int> Sizes => new()
        {
            { 1, 1 },
            { 8, 8 },
            { 13, 7 },      // right edge lands mid-byte and mid-MCU
            { 17, 33 },     // and the bottom edge too
            { 64, 48 },
        };

        // ---- lossless formats ------------------------------------------------------------

        [Theory]
        [MemberData(nameof(Sizes))]
        public void PngRoundTripsExactly(int width, int height)
        {
            AssertLosslessRoundTrip(new PngBitmapEncoder(), width, height);
        }

        [Theory]
        [MemberData(nameof(Sizes))]
        public void BmpRoundTripsExactly(int width, int height)
        {
            AssertLosslessRoundTrip(new BmpBitmapEncoder(), width, height);
        }

        [Theory]
        [MemberData(nameof(Sizes))]
        public void TiffRoundTripsExactly(int width, int height)
        {
            AssertLosslessRoundTrip(new TiffBitmapEncoder(), width, height);
        }

        /// <summary>
        ///  GIF is lossless only when the image has few enough distinct colours to keep them all;
        ///  beyond that the encoder runs median cut and the comparison would be measuring the
        ///  quantiser rather than the codec. A four-colour source stays exact.
        /// </summary>
        [Theory]
        [MemberData(nameof(Sizes))]
        public void GifRoundTripsExactlyForFewColours(int width, int height)
        {
            byte[] expected = FourColourImage(width, height);
            BitmapSource source = Materialize(expected, width, height);

            byte[] decoded = DecodeToBgra(Encode(new GifBitmapEncoder(), source), out int w, out int h);

            Assert.Equal(width, w);
            Assert.Equal(height, h);
            AssertPixelsEqual(expected, decoded, width, height);
        }

        // ---- lossy ----------------------------------------------------------------------

        /// <summary>
        ///  JPEG is a DCT codec, so exactness is the wrong question; what matters is that the image
        ///  survives recognisably. A flat block should come back within a couple of levels.
        /// </summary>
        [Theory]
        [MemberData(nameof(Sizes))]
        public void JpegRoundTripsApproximately(int width, int height)
        {
            byte[] expected = FlatImage(width, height, 200, 100, 50);
            BitmapSource source = Materialize(expected, width, height);

            byte[] decoded = DecodeToBgra(Encode(new JpegBitmapEncoder(), source), out int w, out int h);

            Assert.Equal(width, w);
            Assert.Equal(height, h);

            for (int i = 0; i < decoded.Length; i += 4)
            {
                Assert.InRange(decoded[i + 0], expected[i + 0] - 6, expected[i + 0] + 6);
                Assert.InRange(decoded[i + 1], expected[i + 1] - 6, expected[i + 1] + 6);
                Assert.InRange(decoded[i + 2], expected[i + 2] - 6, expected[i + 2] + 6);
                Assert.Equal(255, decoded[i + 3]);      // JPEG has no alpha; it decodes opaque
            }
        }

        // ---- what the gap actually looked like -------------------------------------------

        /// <summary>
        ///  The regression guard, stated as the property that was false: for every format this stack
        ///  can encode, BitmapDecoder.Create can read the result. GIF and TIFF both failed this with
        ///  NotSupportedException while their encoders worked.
        /// </summary>
        [Fact]
        public void EveryEncoderProducesSomethingTheDecoderCanRead()
        {
            BitmapSource source = Materialize(FourColourImage(16, 16), 16, 16);

            var encoders = new Dictionary<string, BitmapEncoder>
            {
                ["PNG"] = new PngBitmapEncoder(),
                ["JPEG"] = new JpegBitmapEncoder(),
                ["BMP"] = new BmpBitmapEncoder(),
                ["TIFF"] = new TiffBitmapEncoder(),
                ["GIF"] = new GifBitmapEncoder(),
            };

            foreach ((string name, BitmapEncoder encoder) in encoders)
            {
                byte[] encoded = Encode(encoder, source);

                using var stream = new MemoryStream(encoded);
                BitmapDecoder decoder = BitmapDecoder.Create(
                    stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

                Assert.True(decoder.Frames.Count >= 1, $"{name} decoded to no frames.");
                Assert.Equal(16, decoder.Frames[0].PixelWidth);
                Assert.Equal(16, decoder.Frames[0].PixelHeight);
            }
        }

        // ---- multi-frame -----------------------------------------------------------------

        /// <summary>
        ///  A multi-page TIFF must come back as multiple frames. Keeping only the first is the
        ///  plausible-looking failure: the image loads, nothing throws, and the other pages are
        ///  silently gone.
        /// </summary>
        [Fact]
        public void MultiPageTiffKeepsEveryPage()
        {
            BitmapSource red = Materialize(FlatImage(8, 8, 0, 0, 255), 8, 8);
            BitmapSource green = Materialize(FlatImage(8, 8, 0, 255, 0), 8, 8);
            BitmapSource blue = Materialize(FlatImage(8, 8, 255, 0, 0), 8, 8);

            var encoder = new TiffBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(red));
            encoder.Frames.Add(BitmapFrame.Create(green));
            encoder.Frames.Add(BitmapFrame.Create(blue));

            using var written = new MemoryStream();
            encoder.Save(written);

            using var read = new MemoryStream(written.ToArray());
            BitmapDecoder decoder = BitmapDecoder.Create(read, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            Assert.Equal(3, decoder.Frames.Count);

            // Each page keeps its own colour, in order. BGRA, so index 2 is red and 1 is green.
            Assert.Equal(255, PixelAt(decoder.Frames[0], 0, 0)[2]);
            Assert.Equal(255, PixelAt(decoder.Frames[1], 0, 0)[1]);
            Assert.Equal(255, PixelAt(decoder.Frames[2], 0, 0)[0]);
        }

        // ---- helpers ---------------------------------------------------------------------

        private static void AssertLosslessRoundTrip(BitmapEncoder encoder, int width, int height)
        {
            byte[] expected = GradientImage(width, height);
            BitmapSource source = Materialize(expected, width, height);

            byte[] decoded = DecodeToBgra(Encode(encoder, source), out int w, out int h);

            Assert.Equal(width, w);
            Assert.Equal(height, h);
            AssertPixelsEqual(expected, decoded, width, height);
        }

        private static void AssertPixelsEqual(byte[] expected, byte[] actual, int width, int height)
        {
            Assert.Equal(expected.Length, actual.Length);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;

                    // Reported per pixel rather than as a byte-array equality: "expected 4096 bytes,
                    // got 4096 bytes" says nothing about WHERE a stride bug put the pixels.
                    Assert.True(
                        expected[i] == actual[i]
                        && expected[i + 1] == actual[i + 1]
                        && expected[i + 2] == actual[i + 2]
                        && expected[i + 3] == actual[i + 3],
                        $"pixel ({x},{y}) is "
                        + $"BGRA({actual[i]},{actual[i + 1]},{actual[i + 2]},{actual[i + 3]}), expected "
                        + $"BGRA({expected[i]},{expected[i + 1]},{expected[i + 2]},{expected[i + 3]}).");
                }
            }
        }

        private static byte[] Encode(BitmapEncoder encoder, BitmapSource source)
        {
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        private static byte[] DecodeToBgra(byte[] encoded, out int width, out int height)
        {
            using var stream = new MemoryStream(encoded);
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            BitmapFrame frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;

            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[width * height * 4];
            converted.CopyPixels(pixels, width * 4, 0);
            return pixels;
        }

        private static byte[] PixelAt(BitmapSource source, int x, int y)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4];
            converted.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return pixel;
        }

        private static BitmapSource Materialize(byte[] bgra, int width, int height)
        {
            BitmapSource source = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
            source.Freeze();
            return source;
        }

        /// <summary>Every channel varying across the image, so a swapped axis or channel shows up.</summary>
        private static byte[] GradientImage(int width, int height)
        {
            var bgra = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    bgra[i + 0] = (byte)(x * 255 / Math.Max(1, width - 1));
                    bgra[i + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                    bgra[i + 2] = (byte)((x + y) * 255 / Math.Max(1, width + height - 2));
                    bgra[i + 3] = 255;
                }
            }
            return bgra;
        }

        /// <summary>Four distinct colours in quadrants: inside GIF's palette, and position-sensitive.</summary>
        private static byte[] FourColourImage(int width, int height)
        {
            var bgra = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    bool right = x >= width / 2;
                    bool bottom = y >= height / 2;

                    (byte b, byte g, byte r) = (right, bottom) switch
                    {
                        (false, false) => ((byte)0, (byte)0, (byte)255),
                        (true, false) => ((byte)0, (byte)255, (byte)0),
                        (false, true) => ((byte)255, (byte)0, (byte)0),
                        (true, true) => ((byte)255, (byte)255, (byte)255),
                    };

                    bgra[i + 0] = b;
                    bgra[i + 1] = g;
                    bgra[i + 2] = r;
                    bgra[i + 3] = 255;
                }
            }
            return bgra;
        }

        private static byte[] FlatImage(int width, int height, byte b, byte g, byte r)
        {
            var bgra = new byte[width * height * 4];
            for (int i = 0; i < bgra.Length; i += 4)
            {
                bgra[i + 0] = b;
                bgra[i + 1] = g;
                bgra[i + 2] = r;
                bgra[i + 3] = 255;
            }
            return bgra;
        }
    }
}
