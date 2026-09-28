// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Windows 11 theme's image parts (Win11Frames) against the theme's own frames.
//
// uxtheme draws a push button, check box or radio button by blitting a small premultiplied bitmap
// out of aero.msstyles. The port draws the same frames from geometry, on every head, and this holds
// them to the installed theme's -- read here, on Windows only, and never shipped.
//
// The theme file is a resource-only PE:
//   CMAP           the class names, UTF-16, each NUL-terminated and padded to 8 bytes; a class's
//                  index in this list is its id in the property table.
//   VARIANT/NORMAL the property table: records of a 32-byte header -- property id, primitive type,
//                  class index, part, state, short value, reserved, size -- followed by `size`
//                  bytes padded to 8. A record whose short value is non-zero carries it in the
//                  header and has no body; for a FILENAME property (type 206) it is the id of the
//                  IMAGE resource.
//   IMAGE/<id>     PNG, straight RGBA in form but holding PREMULTIPLIED values (a corner pixel of
//                  alpha 0x20 has its colour scaled to 0x1A), which is how uxtheme alpha-blends them.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms.VisualStyles;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class ThemeFrameTests
    {
        /// <summary>Sum of |d| over the four premultiplied channels of every frame of the part,
        /// ours against the theme's. A ceiling: the corners of the theme's frames were rendered
        /// with a sample pattern no simple one reproduces, so a few edge pixels differ by a
        /// sixteenth of coverage. Lower it when a frame gets closer.</summary>
        private static readonly Dictionary<int, long> Ceiling = new()
        {
            [1] = 1239,     // PUSHBUTTON, 6 frames of 13x11
            [2] = 1813,     // RADIOBUTTON, 8 frames of 13x13
            [3] = 2823,     // CHECKBOX, 20 frames of 13x13
        };

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void ButtonFrames_AreTheThemes(int part)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the reference is the installed Windows theme");
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                       @"Resources\Themes\aero\aero.msstyles");
            Assert.SkipUnless(File.Exists(path), "no aero.msstyles");

            using var theme = new ThemeFile(path);
            int image = theme.ImageOf("Button", part);
            Assert.True(image != 0, $"no IMAGEFILE for Button part {part} in the theme");
            using var atlas = new Bitmap(new MemoryStream(theme.Resource("IMAGE", image)));

            int frames = part == 1 ? 6 : part == 2 ? 8 : 20;
            int fh = atlas.Height / frames;
            long total = 0;
            var report = new StringBuilder();
            for (int f = 0; f < frames; f++)
            {
                Win11Frames.Frame ours = Win11Frames.Get("BUTTON", part, f + 1);
                Assert.Equal(atlas.Width, ours.Width);
                Assert.Equal(fh, ours.Height);
                long d = 0;
                for (int y = 0; y < fh; y++)
                    for (int x = 0; x < atlas.Width; x++)
                    {
                        Color c = atlas.GetPixel(x, f * fh + y);
                        uint o = ours.Pixels[y * ours.Width + x];
                        d += Math.Abs(c.A - (int)(o >> 24)) + Math.Abs(c.R - (int)(o >> 16 & 255))
                           + Math.Abs(c.G - (int)(o >> 8 & 255)) + Math.Abs(c.B - (int)(o & 255));
                    }
                report.Append($" {d}");
                total += d;
            }
            Assert.True(total <= Ceiling[part],
                $"Button part {part}: {total} against a ceiling of {Ceiling[part]} (per frame:{report})");
            Assert.True(total >= Ceiling[part] * 9 / 10,
                $"Button part {part} improved to {total} (per frame:{report}) -- lower its ceiling");
        }

        /// <summary>Just enough of the msstyles format to find a part's image.</summary>
        private sealed class ThemeFile : IDisposable
        {
            [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
            [DllImport("kernel32")] private static extern bool FreeLibrary(IntPtr module);
            [DllImport("kernel32", CharSet = CharSet.Unicode)]
            private static extern IntPtr FindResource(IntPtr module, string name, string type);
            [DllImport("kernel32", CharSet = CharSet.Unicode, EntryPoint = "FindResourceW")]
            private static extern IntPtr FindResourceById(IntPtr module, IntPtr name, string type);
            [DllImport("kernel32")] private static extern IntPtr LoadResource(IntPtr module, IntPtr info);
            [DllImport("kernel32")] private static extern IntPtr LockResource(IntPtr data);
            [DllImport("kernel32")] private static extern uint SizeofResource(IntPtr module, IntPtr info);

            private readonly IntPtr _module;

            public ThemeFile(string path)
            {
                // LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE: resources only, no code.
                _module = LoadLibraryEx(path, IntPtr.Zero, 0x22);
                if (_module == IntPtr.Zero)
                    throw new InvalidOperationException($"LoadLibraryEx({path}) failed: {Marshal.GetLastWin32Error()}");
            }

            public void Dispose() => FreeLibrary(_module);

            public byte[] Resource(string type, string name) => Read(FindResource(_module, name, type));
            public byte[] Resource(string type, int id) => Read(FindResourceById(_module, (IntPtr)id, type));

            private byte[] Read(IntPtr info)
            {
                if (info == IntPtr.Zero)
                    throw new InvalidOperationException("resource not found");
                var bytes = new byte[SizeofResource(_module, info)];
                Marshal.Copy(LockResource(LoadResource(_module, info)), bytes, 0, bytes.Length);
                return bytes;
            }

            /// <summary>The IMAGE id of a part's 96-dpi image: IMAGEFILE (3001), else IMAGEFILE1
            /// (3002) -- a part that picks its image by DPI names the 96-dpi one first.</summary>
            public int ImageOf(string cls, int part)
            {
                int index = Classes().IndexOf(cls);
                byte[] v = Resource("VARIANT", "NORMAL");
                int file = 0, file1 = 0;
                for (int i = 0; i + 32 <= v.Length;)
                {
                    int name = BitConverter.ToInt32(v, i), type = BitConverter.ToInt32(v, i + 4);
                    int c = BitConverter.ToInt32(v, i + 8), p = BitConverter.ToInt32(v, i + 12);
                    int s = BitConverter.ToInt32(v, i + 16), shortValue = BitConverter.ToInt32(v, i + 20);
                    int size = BitConverter.ToInt32(v, i + 28);
                    i += 32;
                    if (shortValue == 0)
                        i += (size + 7) & ~7;
                    if (c == index && p == part && s == 0 && type == 206 && shortValue != 0)
                    {
                        if (name == 3001) file = shortValue;
                        if (name == 3002) file1 = shortValue;
                    }
                }
                return file != 0 ? file : file1;
            }

            private List<string> Classes()
            {
                byte[] c = Resource("CMAP", "CMAP");
                var names = new List<string>();
                for (int i = 0; i < c.Length;)
                {
                    int j = i;
                    while (j + 1 < c.Length && (c[j] != 0 || c[j + 1] != 0))
                        j += 2;
                    names.Add(Encoding.Unicode.GetString(c, i, j - i));
                    i = (j + 2 + 7) & ~7;
                }
                return names;
            }
        }
    }
}
