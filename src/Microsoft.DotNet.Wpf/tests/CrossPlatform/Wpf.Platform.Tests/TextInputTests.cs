// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The input-method channel's unit arithmetic (zwp_text_input_v3).
//
// The protocol counts in UTF-8 BYTES -- preedit cursor offsets and delete_surrounding_text lengths
// both -- while everything above is UTF-16. For Latin the two agree and a confusion between them is
// invisible; for CJK every character is three bytes, so getting it wrong puts the caret a third of
// the way through the composition and deletes three times too much. These are the conversions, and
// they are exactly the part a real IME exercises on every keystroke.
//

using System;
using MS.Internal.Interop.Wayland;
using Xunit;

namespace Wpf.Platform.Tests
{
    public class TextInputTests
    {
        [Theory]
        // Offsets on character boundaries, in a string of three-byte characters.
        [InlineData("日本語", 0, 0)]
        [InlineData("日本語", 3, 1)]
        [InlineData("日本語", 6, 2)]
        [InlineData("日本語", 9, 3)]
        // The protocol's hidden-cursor sentinel.
        [InlineData("日本語", -1, -1)]
        // An offset inside a character is malformed; rejected rather than rounded, because rounding
        // would silently put the caret somewhere the input method did not ask for.
        [InlineData("日本語", 4, -1)]
        [InlineData("日本語", 100, -1)]
        // Mixed widths: ASCII is one byte, kana three, and an emoji four bytes / two UTF-16 chars.
        [InlineData("aあ", 1, 1)]
        [InlineData("aあ", 4, 2)]
        [InlineData("\U0001F600あ", 4, 2)]
        [InlineData("\U0001F600あ", 7, 3)]
        public void PreeditCursorOffsetsAreByteOffsets(string preedit, int byteOffset, int expectedCharIndex)
        {
            Assert.Equal(expectedCharIndex, WaylandTextInput.ByteOffsetToCharIndexForTest(preedit, byteOffset));
        }

        [Fact]
        public void NullPreeditHasNoCursor()
        {
            Assert.Equal(-1, WaylandTextInput.ByteOffsetToCharIndexForTest(null, 0));
        }
    }
}
