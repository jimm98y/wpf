// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed GIF decoder for platforms without native WIC.
//
// ManagedGifEncoder has shipped for a while, so off Windows this stack could WRITE a GIF it could
// not READ back -- BitmapDecoder threw NotSupportedException for the one format it had just
// produced. This is the other half.
//
// Every frame is decoded, not just the first, and each is COMPOSED onto the logical screen rather
// than returned raw. That distinction is the whole difficulty of GIF: after frame 0 an encoder is
// free to store only the rectangle that changed, so a decoder that hands back sub-images produces a
// stack of small tiles at the wrong offsets instead of an animation. Composition needs the frame's
// position, the previous frame's disposal method and (for disposal 3) a snapshot of the canvas
// before the frame was drawn, all of which are tracked below.
//
// Deliberately not handled: the Plain Text extension, which specifies rendering text with a font the
// format never carries. No encoder emits it and no decoder in current use renders it; it is skipped
// as an unknown extension, which is what its own specification recommends.
//

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;

namespace System.Windows.Media.Imaging
{
    /// <summary>One decoded GIF frame: BGRA32 pixels the size of the logical screen.</summary>
    internal readonly struct ManagedGifFrame
    {
        internal ManagedGifFrame(byte[] bgra, int delayMilliseconds)
            : this(bgra, delayMilliseconds, 0, -1)
        {
        }

        internal ManagedGifFrame(byte[] bgra, int delayMilliseconds, int disposal, int transparentIndex)
        {
            Bgra = bgra;
            DelayMilliseconds = delayMilliseconds;
            Disposal = disposal;
            TransparentIndex = transparentIndex;
        }

        /// <summary>Composed canvas for this frame, logical-screen sized, 4 bytes per pixel.</summary>
        internal byte[] Bgra { get; }

        /// <summary>How long this frame is shown. Zero when the file did not say.</summary>
        internal int DelayMilliseconds { get; }

        /// <summary>The graphic control extension's disposal method (0 when there was none).</summary>
        internal int Disposal { get; }

        /// <summary>The frame's transparent colour index, or -1.</summary>
        internal int TransparentIndex { get; }
    }

    /// <summary>What a GIF says about itself beyond its frames -- what System.Drawing hands out as
    /// the image's property items (loop count, global palette, background index).</summary>
    internal sealed class ManagedGifInfo
    {
        /// <summary>The NETSCAPE2.0 loop count, or -1 when the file carries no such extension.</summary>
        internal int LoopCount = -1;
        /// <summary>The global colour table as RGB triples, or null.</summary>
        internal byte[]? GlobalTable;
        internal int BackgroundIndex;
        /// <summary>Image blocks in the file.</summary>
        internal int ImageCount;
    }

    internal static class ManagedGifDecoder
    {
        // Set by DecodeImage for a lone full-screen image; read by Decode once the file is known to
        // hold exactly one. Not thread safe, and neither is anything else on this path: a decode is
        // one call on one thread.
        [ThreadStatic] private static byte[]? LastFullScreenIndices;
        [ThreadStatic] private static byte[]? LastFullScreenPalette;
        [ThreadStatic] private static int LastFullScreenTransparentIndex;

        // Block introducers.
        private const byte Extension = 0x21;
        private const byte ImageDescriptor = 0x2C;
        private const byte Trailer = 0x3B;

        // Extension labels.
        private const byte GraphicControlLabel = 0xF9;
        private const byte ApplicationLabel = 0xFF;

        // Disposal methods, from the graphic control extension's packed field.
        private const int DisposalNone = 0;         // "unspecified" -- treat as leave-in-place
        private const int DisposalLeave = 1;
        private const int DisposalRestoreBackground = 2;
        private const int DisposalRestorePrevious = 3;

        /// <summary>True when the bytes open with a GIF87a or GIF89a signature.</summary>
        internal static bool IsGif(byte[] data) =>
            data.Length > 6
            && data[0] == 'G' && data[1] == 'I' && data[2] == 'F'
            && data[3] == '8' && (data[4] == '7' || data[4] == '9') && data[5] == 'a';

        /// <summary>
        ///  Decodes every frame. Each is the full logical screen, already composed over its
        ///  predecessors, so callers can show any one of them without replaying the animation.
        /// </summary>
        internal static List<ManagedGifFrame> Decode(byte[] data, out int width, out int height)
            => Decode(data, out width, out height, out _, out _, out _);

        /// <summary>
        /// As above, and additionally hands back the file's own indexed picture when it holds a
        /// SINGLE full-screen image -- the shape a static GIF has. <paramref name="indexed"/> is
        /// null for an animation, whose composed canvas is not any one palette's image. The
        /// palette is 0xAARRGGBB words, the transparent index an entry of alpha zero.
        /// </summary>
        internal static List<ManagedGifFrame> Decode(byte[] data, out int width, out int height,
            out byte[]? indexed, out uint[]? indexedPalette)
            => Decode(data, out width, out height, out indexed, out indexedPalette, out _);

        /// <summary>As above, and also what the file says about itself.</summary>
        internal static List<ManagedGifFrame> Decode(byte[] data, out int width, out int height,
            out byte[]? indexed, out uint[]? indexedPalette, out ManagedGifInfo info,
            bool restoreToBackgroundColor = false)
        {
            info = new ManagedGifInfo();
            LastFullScreenIndices = null;
            LastFullScreenPalette = null;
            LastFullScreenTransparentIndex = -1;
            if (!IsGif(data))
            {
                throw new InvalidDataException("not a GIF: the signature did not match.");
            }

            int pos = 6;

            width = ReadUInt16(data, ref pos);
            height = ReadUInt16(data, ref pos);
            if (width <= 0 || height <= 0)
            {
                throw new InvalidDataException($"GIF logical screen is {width}x{height}.");
            }

            byte packed = ReadByte(data, ref pos);
            info.BackgroundIndex = pos < data.Length ? data[pos] : 0;
            pos += 2;                                   // background colour index, pixel aspect ratio

            byte[]? globalTable = null;
            if ((packed & 0x80) != 0)
            {
                globalTable = ReadColorTable(data, ref pos, 2 << (packed & 0x07));
            }
            info.GlobalTable = globalTable;

            int imageCount = 0;
            var frames = new List<ManagedGifFrame>();

            // The canvas every frame is composed onto. GIF has a background colour index, but the
            // format cannot say whether it means "opaque background" or "nothing here yet", and
            // every decoder in current use starts transparent -- matching them keeps images that
            // were authored against a browser looking the way their author saw them.
            byte[] canvas = new byte[width * height * 4];

            // Carried from the graphic control extension, which precedes the image it describes.
            int transparentIndex = -1;
            int delayMilliseconds = 0;
            int disposal = DisposalNone;

            while (pos < data.Length)
            {
                byte block = ReadByte(data, ref pos);

                if (block == Trailer)
                {
                    break;
                }

                if (block == Extension)
                {
                    byte label = ReadByte(data, ref pos);
                    if (label == GraphicControlLabel)
                    {
                        ReadGraphicControl(data, ref pos, out transparentIndex, out delayMilliseconds, out disposal);
                    }
                    else if (label == ApplicationLabel && TryReadLoopCount(data, pos, out int loops))
                    {
                        info.LoopCount = loops;
                        SkipSubBlocks(data, ref pos);
                    }
                    else
                    {
                        SkipSubBlocks(data, ref pos);
                    }
                    continue;
                }

                if (block != ImageDescriptor)
                {
                    // A byte that is none of the three legal block introducers means the stream is
                    // out of step. Stopping keeps whatever decoded cleanly rather than walking off
                    // into the rest of the file interpreting noise as pixels.
                    break;
                }

                // Snapshot BEFORE drawing: disposal 3 restores the canvas to exactly this.
                byte[]? restorePoint = disposal == DisposalRestorePrevious ? (byte[])canvas.Clone() : null;

                imageCount++;
                DecodeImage(data, ref pos, canvas, width, height, globalTable, transparentIndex,
                            out int frameLeft, out int frameTop, out int frameWidth, out int frameHeight);

                frames.Add(new ManagedGifFrame((byte[])canvas.Clone(), delayMilliseconds, disposal, transparentIndex));

                // Apply this frame's disposal so the NEXT one starts from the right canvas.
                switch (disposal)
                {
                    case DisposalRestoreBackground:
                        // restoreToBackgroundColor: to the screen's background colour, opaque (as
                        // GDI+ composes); otherwise to nothing, as every browser does.
                        uint fill = 0;
                        if (restoreToBackgroundColor && globalTable != null && info.BackgroundIndex * 3 + 2 < globalTable.Length)
                        {
                            int bi = info.BackgroundIndex * 3;
                            fill = 0xFF000000u | ((uint)globalTable[bi] << 16) | ((uint)globalTable[bi + 1] << 8) | globalTable[bi + 2];
                        }
                        ClearRect(canvas, width, frameLeft, frameTop, frameWidth, frameHeight, fill);
                        break;

                    case DisposalRestorePrevious when restorePoint is not null:
                        Array.Copy(restorePoint, canvas, canvas.Length);
                        break;

                    case DisposalNone:
                    case DisposalLeave:
                    default:
                        break;   // leave the frame in place
                }

                // The graphic control extension applies to one image only.
                transparentIndex = -1;
                delayMilliseconds = 0;
                disposal = DisposalNone;
            }

            if (frames.Count == 0)
            {
                throw new InvalidDataException("the GIF contained no image blocks.");
            }

            indexed = null;
            indexedPalette = null;
            info.ImageCount = imageCount;
            if (imageCount == 1 && LastFullScreenIndices != null && LastFullScreenPalette != null)
            {
                indexed = LastFullScreenIndices;

                int entries = LastFullScreenPalette.Length / 3;
                var colors = new uint[entries];
                for (int i = 0; i < entries; i++)
                {
                    // The transparent index is a hole in the picture; in an indexed bitmap that is
                    // an entry whose alpha is zero.
                    uint a = i == LastFullScreenTransparentIndex ? 0u : 255u;
                    colors[i] = (a << 24) | ((uint)LastFullScreenPalette[i * 3] << 16)
                              | ((uint)LastFullScreenPalette[i * 3 + 1] << 8) | LastFullScreenPalette[i * 3 + 2];
                }
                indexedPalette = colors;
            }

            LastFullScreenIndices = null;
            LastFullScreenPalette = null;

            return frames;
        }

        // ---- blocks ----------------------------------------------------------------------

        /// <summary>
        ///  The NETSCAPE2.0 (or ANIMEXTS1.0) application extension at <paramref name="pos"/>: an
        ///  11-byte identifier block, then a sub-block whose first byte is 1 and whose next two are
        ///  the loop count, 0 meaning forever.
        /// </summary>
        private static bool TryReadLoopCount(byte[] data, int pos, out int loops)
        {
            loops = 0;
            if (pos + 16 > data.Length || data[pos] != 11) return false;
            string id = System.Text.Encoding.ASCII.GetString(data, pos + 1, 11);
            if (id != "NETSCAPE2.0" && id != "ANIMEXTS1.0") return false;
            int sub = pos + 12;
            if (data[sub] < 3 || data[sub + 1] != 1) return false;
            loops = data[sub + 2] | (data[sub + 3] << 8);
            return true;
        }

        private static void ReadGraphicControl(byte[] data, ref int pos,
                                               out int transparentIndex, out int delayMilliseconds, out int disposal)
        {
            transparentIndex = -1;
            delayMilliseconds = 0;
            disposal = DisposalNone;

            int size = ReadByte(data, ref pos);
            int end = pos + size;
            if (size < 4 || end > data.Length)
            {
                pos = Math.Min(end, data.Length);
                SkipSubBlocks(data, ref pos);
                return;
            }

            byte packed = data[pos];
            // Delay is in HUNDREDTHS of a second, which is the commonest single mistake made
            // reading this format.
            delayMilliseconds = (data[pos + 1] | (data[pos + 2] << 8)) * 10;
            byte index = data[pos + 3];

            disposal = (packed >> 2) & 0x07;
            if ((packed & 0x01) != 0)
            {
                transparentIndex = index;
            }

            pos = end;
            SkipSubBlocks(data, ref pos);   // consumes the block terminator
        }

        private static void DecodeImage(byte[] data, ref int pos, byte[] canvas, int canvasWidth, int canvasHeight,
                                        byte[]? globalTable, int transparentIndex,
                                        out int left, out int top, out int frameWidth, out int frameHeight)
        {
            left = ReadUInt16(data, ref pos);
            top = ReadUInt16(data, ref pos);
            frameWidth = ReadUInt16(data, ref pos);
            frameHeight = ReadUInt16(data, ref pos);
            byte packed = ReadByte(data, ref pos);

            byte[]? localTable = null;
            if ((packed & 0x80) != 0)
            {
                localTable = ReadColorTable(data, ref pos, 2 << (packed & 0x07));
            }

            bool interlaced = (packed & 0x40) != 0;
            byte[] palette = localTable ?? globalTable
                ?? throw new InvalidDataException("the GIF frame has neither a local nor a global colour table.");

            byte minCodeSize = ReadByte(data, ref pos);
            byte[] indices = LzwDecode(data, ref pos, minCodeSize, frameWidth * frameHeight);

            int paletteEntries = palette.Length / 3;

            // A GIF is an indexed image, and when the file is a single picture covering the whole
            // logical screen the LZW output IS that picture -- one byte per pixel, in the palette
            // the file carries. Handing that back lets a static GIF (the overwhelming majority of
            // them) keep its own Indexed8 format instead of being expanded to 32bpp.
            //
            // Only for that shape. An ANIMATION is composed frame over frame with transparency and
            // disposal, and successive frames may carry different local palettes, so the composed
            // canvas genuinely is not any one palette's image. Partial frames (a small update rect)
            // are the same story.
            if (left == 0 && top == 0 && frameWidth == canvasWidth && frameHeight == canvasHeight)
            {
                byte[] full = indices;
                if (interlaced)
                {
                    // Unscramble the four passes into picture order.
                    full = new byte[canvasWidth * canvasHeight];
                    for (int row = 0; row < frameHeight; row++)
                    {
                        int target = InterlacedRow(row, frameHeight);
                        if (target < canvasHeight && (row + 1) * frameWidth <= indices.Length)
                        {
                            Array.Copy(indices, row * frameWidth, full, target * canvasWidth, frameWidth);
                        }
                    }
                }
                else if (indices.Length < canvasWidth * canvasHeight)
                {
                    full = new byte[canvasWidth * canvasHeight];
                    Array.Copy(indices, full, indices.Length);
                }

                LastFullScreenIndices = full;
                LastFullScreenPalette = palette;
                LastFullScreenTransparentIndex = transparentIndex;
            }

            for (int row = 0; row < frameHeight; row++)
            {
                int sourceRow = interlaced ? InterlacedRow(row, frameHeight) : row;
                int y = top + sourceRow;
                if (y < 0 || y >= canvasHeight)
                {
                    continue;
                }

                int sourceOffset = row * frameWidth;
                int destinationRow = y * canvasWidth * 4;

                for (int column = 0; column < frameWidth; column++)
                {
                    int x = left + column;
                    if (x < 0 || x >= canvasWidth)
                    {
                        continue;
                    }

                    int sourceIndex = sourceOffset + column;
                    if (sourceIndex >= indices.Length)
                    {
                        break;   // a truncated frame keeps what it managed to decode
                    }

                    byte index = indices[sourceIndex];

                    // A transparent pixel is a HOLE, not a colour: whatever the previous frames
                    // left on the canvas shows through it. Skipping the write is the composition.
                    if (index == transparentIndex || index >= paletteEntries)
                    {
                        continue;
                    }

                    int destination = destinationRow + x * 4;
                    int entry = index * 3;
                    canvas[destination + 0] = palette[entry + 2];   // B
                    canvas[destination + 1] = palette[entry + 1];   // G
                    canvas[destination + 2] = palette[entry + 0];   // R
                    canvas[destination + 3] = 255;
                }
            }
        }

        /// <summary>
        ///  Maps a row of an interlaced frame to its position in the image. GIF stores interlaced
        ///  rows in four passes (every 8th from 0, every 8th from 4, every 4th from 2, every 2nd
        ///  from 1) so a partially-loaded image over a slow link still showed the whole picture.
        /// </summary>
        private static int InterlacedRow(int row, int height)
        {
            int pass1 = (height + 7) / 8;
            int pass2 = (height + 3) / 8;
            int pass3 = (height + 1) / 4;

            if (row < pass1) return row * 8;
            row -= pass1;
            if (row < pass2) return row * 8 + 4;
            row -= pass2;
            if (row < pass3) return row * 4 + 2;
            row -= pass3;
            return row * 2 + 1;
        }

        private static void ClearRect(byte[] canvas, int canvasWidth, int left, int top, int width, int height, uint fill = 0)
        {
            for (int y = top; y < top + height; y++)
            {
                if (y < 0 || (y + 1) * canvasWidth * 4 > canvas.Length)
                {
                    continue;
                }

                int rowStart = y * canvasWidth * 4;
                for (int x = left; x < left + width; x++)
                {
                    if (x < 0 || x >= canvasWidth)
                    {
                        continue;
                    }

                    int offset = rowStart + x * 4;
                    canvas[offset + 0] = (byte)fill;
                    canvas[offset + 1] = (byte)(fill >> 8);
                    canvas[offset + 2] = (byte)(fill >> 16);
                    canvas[offset + 3] = (byte)(fill >> 24);
                }
            }
        }

        // ---- LZW -------------------------------------------------------------------------

        /// <summary>
        ///  GIF's variable-width LZW. Codes are packed LSB-first and span byte boundaries, and the
        ///  data arrives as a chain of length-prefixed sub-blocks that the bit reader has to cross
        ///  without treating the length bytes as data.
        /// </summary>
        private static byte[] LzwDecode(byte[] data, ref int pos, byte minCodeSize, int expectedPixels)
        {
            if (minCodeSize is < 2 or > 11)
            {
                throw new InvalidDataException($"GIF LZW minimum code size {minCodeSize} is out of range.");
            }

            int clearCode = 1 << minCodeSize;
            int endCode = clearCode + 1;

            // 4096 is the format's ceiling: codes are at most 12 bits.
            const int MaxCodes = 4096;
            var prefix = new short[MaxCodes];
            var suffix = new byte[MaxCodes];
            var stack = new byte[MaxCodes];

            for (int i = 0; i < clearCode; i++)
            {
                prefix[i] = -1;
                suffix[i] = (byte)i;
            }

            var output = new byte[expectedPixels];
            int written = 0;

            int codeSize = minCodeSize + 1;
            int nextCode = endCode + 1;
            int previous = -1;

            int bitBuffer = 0;
            int bitCount = 0;

            // Sub-block cursor.
            int blockRemaining = 0;

            while (true)
            {
                while (bitCount < codeSize)
                {
                    if (blockRemaining == 0)
                    {
                        if (pos >= data.Length)
                        {
                            goto done;      // truncated stream: keep what decoded
                        }

                        blockRemaining = data[pos++];
                        if (blockRemaining == 0)
                        {
                            goto done;      // block terminator: the image is complete
                        }
                    }

                    if (pos >= data.Length)
                    {
                        goto done;
                    }

                    bitBuffer |= data[pos++] << bitCount;
                    bitCount += 8;
                    blockRemaining--;
                }

                int code = bitBuffer & ((1 << codeSize) - 1);
                bitBuffer >>= codeSize;
                bitCount -= codeSize;

                if (code == clearCode)
                {
                    codeSize = minCodeSize + 1;
                    nextCode = endCode + 1;
                    previous = -1;
                    continue;
                }

                if (code == endCode)
                {
                    break;
                }

                int current;
                int stackTop = 0;

                if (code < nextCode && (code < clearCode || prefix[code] != -1 || code > endCode))
                {
                    current = code;
                }
                else if (previous >= 0)
                {
                    // The KwKwK case: a code that refers to the entry being defined right now. Its
                    // expansion is the previous string plus that string's own first character.
                    stack[stackTop++] = FirstCharacter(prefix, suffix, previous);
                    current = previous;
                }
                else
                {
                    break;   // a forward reference with no previous code: the stream is corrupt
                }

                // Walk the prefix chain, which yields the string backwards.
                while (current >= clearCode)
                {
                    if (stackTop >= stack.Length || current >= MaxCodes)
                    {
                        goto done;
                    }

                    stack[stackTop++] = suffix[current];
                    current = prefix[current];

                    if (current < 0)
                    {
                        goto done;
                    }
                }

                stack[stackTop++] = suffix[current];

                for (int i = stackTop - 1; i >= 0 && written < output.Length; i--)
                {
                    output[written++] = stack[i];
                }

                if (previous >= 0 && nextCode < MaxCodes)
                {
                    prefix[nextCode] = (short)previous;
                    suffix[nextCode] = suffix[current];
                    nextCode++;

                    if (nextCode < MaxCodes && (nextCode & (nextCode - 1)) == 0 && codeSize < 12)
                    {
                        codeSize++;
                    }
                }

                previous = code;

                if (written >= output.Length)
                {
                    break;
                }
            }

        done:
            // Consume whatever is left of the sub-block chain so the caller resumes at the next
            // block, however the loop above exited.
            pos += blockRemaining;
            SkipSubBlocks(data, ref pos);
            return output;
        }

        private static byte FirstCharacter(short[] prefix, byte[] suffix, int code)
        {
            int guard = 0;
            while (prefix[code] >= 0 && guard++ < 4096)
            {
                code = prefix[code];
            }
            return suffix[code];
        }

        // ---- primitives ------------------------------------------------------------------

        private static byte[] ReadColorTable(byte[] data, ref int pos, int entries)
        {
            int bytes = entries * 3;
            if (pos + bytes > data.Length)
            {
                throw new InvalidDataException("the GIF colour table runs past the end of the file.");
            }

            var table = new byte[bytes];
            Array.Copy(data, pos, table, 0, bytes);
            pos += bytes;
            return table;
        }

        private static void SkipSubBlocks(byte[] data, ref int pos)
        {
            while (pos < data.Length)
            {
                int size = data[pos++];
                if (size == 0)
                {
                    return;
                }
                pos += size;
            }
        }

        private static byte ReadByte(byte[] data, ref int pos)
        {
            if (pos >= data.Length)
            {
                throw new InvalidDataException("the GIF ended in the middle of a block.");
            }
            return data[pos++];
        }

        private static int ReadUInt16(byte[] data, ref int pos)
        {
            if (pos + 2 > data.Length)
            {
                throw new InvalidDataException("the GIF ended in the middle of a block.");
            }
            int value = data[pos] | (data[pos + 1] << 8);
            pos += 2;
            return value;
        }
    }
}
