// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The managed Image layer held to REAL GDI+.
//
// Fixtures/Images holds small samples -- written by gdiplus.dll itself where it can write them
// (PNG at 1/4/8/24/32 bits, BMP at every depth, GIF, JPEG with and without EXIF, TIFF one and three
// pages) and by hand where it cannot (greyscale and 16-bit PNG, tRNS, Adam7, a V5 alpha BMP, RLE8,
// an animated GIF with disposal and a local palette, icons with mixed entries) -- and oracle.json,
// what GDI+ says about each: pixel format, raw format, flags, resolution, palette, property items,
// every frame's pixels, and how its Icon class picks and copies an entry. It also records GDI+'s
// answers for the API itself: a new bitmap of every format, SetPixel/GetPixel through every format,
// LockBits converting to every format and back, Clone, RotateFlip, MakeTransparent.
//
// Everything is compared EXACTLY except a JPEG's pixels, which are decoded by a different IDCT and
// upsampler than gdiplus.dll's and are held to a measured bound instead.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public sealed class ManagedImageTests
    {
        private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Images");
        private static readonly Lazy<JsonDocument> Oracle =
            new(() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "oracle.json"))));

        private static JsonElement Files => Oracle.Value.RootElement.GetProperty("files");
        private static JsonElement Api => Oracle.Value.RootElement.GetProperty("api");

        public static TheoryData<string> FileNames()
        {
            var d = new TheoryData<string>();
            foreach (JsonProperty p in JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "oracle.json")))
                         .RootElement.GetProperty("files").EnumerateObject())
                d.Add(p.Name);
            return d;
        }

        public static TheoryData<string> IconNames()
        {
            var d = new TheoryData<string>();
            foreach (JsonProperty p in JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "oracle.json")))
                         .RootElement.GetProperty("files").EnumerateObject())
                if (p.Name.EndsWith(".ico")) d.Add(p.Name);
            return d;
        }

        // ---- helpers ----------------------------------------------------------------------------

        private static string Hex(byte[] b)
        {
            var s = new StringBuilder(b.Length * 2);
            foreach (byte x in b) s.Append(x.ToString("x2"));
            return s.ToString();
        }

        private static byte[] Unhex(string s)
        {
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = byte.Parse(s.AsSpan(i * 2, 2), NumberStyles.HexNumber);
            return b;
        }

        /// <summary>A lock of the whole bitmap as the oracle writes it: "stride=N:" then the rows,
        /// each cut to its pixels.</summary>
        /// <summary>A <see cref="Lock"/>-style "...:hex" string with the bits after each row's last
        /// pixel cleared, for formats whose pixels do not fill a byte: GDI+ leaves its heap there.</summary>
        private static string MaskTail(string s, int width, PixelFormat f)
        {
            int bits = Image.GetPixelFormatSize(f) * width, rem = bits % 8, colon = s.LastIndexOf(':');
            if (rem == 0 || colon < 0 || s.StartsWith("EX")) return s;
            int rowBytes = (bits + 7) / 8;
            byte[] all = Convert.FromHexString(s.Substring(colon + 1));
            for (int i = rowBytes - 1; i < all.Length; i += rowBytes) all[i] &= (byte)(0xff << (8 - rem));
            return s.Substring(0, colon + 1) + Hex(all);
        }

        private static string Lock(Bitmap b, PixelFormat f)
        {
            try
            {
                BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, f);
                int rowBytes = (b.Width * Image.GetPixelFormatSize(f) + 7) / 8;
                var all = new byte[rowBytes * b.Height];
                for (int y = 0; y < b.Height; y++) Marshal.Copy(d.Scan0 + y * d.Stride, all, y * rowBytes, rowBytes);
                b.UnlockBits(d);
                return "stride=" + d.Stride + ":" + Hex(all);
            }
            catch (Exception e)
            {
                return "EX " + e.GetType().Name + " " + e.Message;
            }
        }

        private static float F(JsonElement e) => float.Parse(e.GetRawText(), CultureInfo.InvariantCulture);

        /// <summary>Every way <paramref name="img"/> differs from GDI+'s dump of the same image.</summary>
        private static void Compare(string what, Image img, JsonElement o, List<string> bad, bool jpeg, bool frames)
        {
            void Check(string field, object ours, object theirs)
            {
                if (Equals(ours, theirs)) return;
                if (ours is string a && theirs is string b && a.StartsWith("stride=") && b.StartsWith("stride="))
                {
                    bad.Add($"{what}.{field}: {PixelDifference(a, b)}");
                    return;
                }
                bad.Add($"{what}.{field}: ours {ours}, GDI+ {theirs}");
            }
            Check("w", img.Width, o.GetProperty("w").GetInt32());
            Check("h", img.Height, o.GetProperty("h").GetInt32());
            Check("fmt", img.PixelFormat.ToString(), o.GetProperty("fmt").GetString());
            Check("raw", img.RawFormat.Guid.ToString(), o.GetProperty("raw").GetString());
            Check("flags", img.Flags, o.GetProperty("flags").GetInt32());
            Check("dpix", img.HorizontalResolution, F(o.GetProperty("dpix")));
            Check("dpiy", img.VerticalResolution, F(o.GetProperty("dpiy")));
            SizeF pd = img.PhysicalDimension;
            Check("phys", $"{pd.Width},{pd.Height}", $"{F(o.GetProperty("phys")[0])},{F(o.GetProperty("phys")[1])}");
            Check("dims", string.Join(",", img.FrameDimensionsList),
                  string.Join(",", o.GetProperty("dims").EnumerateArray().Select(x => x.GetString())));
            if (o.GetProperty("palette").ValueKind == JsonValueKind.Object)
            {
                ColorPalette p = img.Palette;
                var entries = o.GetProperty("palette").GetProperty("entries").EnumerateArray().Select(x => x.GetString()).ToArray();
                Check("palette.count", p.Entries.Length, entries.Length);
                // GDI+ leaves the flags of an EMPTY palette uninitialised; they mean something only
                // when there are entries.
                if (entries.Length > 0)
                {
                    Check("palette.flags", p.Flags, o.GetProperty("palette").GetProperty("flags").GetInt32());
                    Check("palette.entries", string.Join(" ", p.Entries.Select(c => ((uint)c.ToArgb()).ToString("x8"))), string.Join(" ", entries));
                }
            }
            var props = o.GetProperty("props").EnumerateArray()
                .Select(x => $"{x.GetProperty("id").GetInt32():x}/{x.GetProperty("type").GetInt32()}/{x.GetProperty("len").GetInt32()}/{x.GetProperty("value").GetString()}")
                .ToArray();
            var ours = img.PropertyItems.Select(x => $"{x.Id:x}/{x.Type}/{x.Len}/{Hex(x.Value ?? Array.Empty<byte>())}").ToArray();
            Check("props", string.Join(" ", ours), string.Join(" ", props));
            if (img is Bitmap bmp && o.TryGetProperty("argb", out JsonElement argb))
            {
                string mine = Lock(bmp, PixelFormat.Format32bppArgb);
                if (jpeg) CompareJpeg(what, mine, argb.GetString()!, bad);
                else Check("argb", mine, argb.GetString());
                if (!jpeg && o.TryGetProperty("native", out JsonElement native))
                    Check("native", MaskTail(Lock(bmp, bmp.PixelFormat), bmp.Width, bmp.PixelFormat), MaskTail(native.GetString()!, bmp.Width, bmp.PixelFormat));
            }
            if (!frames) return;
            foreach (Guid g in img.FrameDimensionsList)
            {
                var fd = new FrameDimension(g);
                int n = img.GetFrameCount(fd);
                Check("count_" + g, n, o.GetProperty("count_" + g).GetInt32());
                if (n > 1 && img is Bitmap fb && o.TryGetProperty("frames_" + g, out JsonElement fr))
                {
                    for (int i = 0; i < n && i < fr.GetArrayLength(); i++)
                    {
                        img.SelectActiveFrame(fd, i);
                        JsonElement f = fr[i];
                        Check($"frame{i}.size", $"{img.Width}x{img.Height}", $"{f.GetProperty("w").GetInt32()}x{f.GetProperty("h").GetInt32()}");
                        Check($"frame{i}.fmt", img.PixelFormat.ToString(), f.GetProperty("fmt").GetString());
                        Check($"frame{i}.argb", Lock(fb, PixelFormat.Format32bppArgb), f.GetProperty("argb").GetString());
                    }
                    img.SelectActiveFrame(fd, 0);
                }
            }
        }

        // Two locks' bytes, said briefly: how many bytes differ, and the first few, by offset.
        private static string PixelDifference(string ours, string theirs)
        {
            string[] a = ours.Split(':'), b = theirs.Split(':');
            if (a[0] != b[0] || a[1].Length != b[1].Length) return $"layout ours {a[0]} ({a[1].Length / 2} bytes), GDI+ {b[0]} ({b[1].Length / 2} bytes)";
            var sb = new StringBuilder();
            int n = 0;
            for (int i = 0; i < a[1].Length; i += 2)
                if (string.CompareOrdinal(a[1], i, b[1], i, 2) != 0)
                {
                    if (n++ < 6) sb.Append($" @{i / 2}: {a[1].Substring(i, 2)}/{b[1].Substring(i, 2)}");
                }
            return $"{n} bytes differ (ours/GDI+){sb}";
        }

        // The largest channel difference, and how many channels differ, between two JPEG decodes.
        internal static readonly Dictionary<string, (int Max, int Count)> JpegError = new();

        private static void CompareJpeg(string what, string ours, string theirs, List<string> bad)
        {
            string[] a = ours.Split(':'), b = theirs.Split(':');
            if (a[0] != b[0] || a.Length < 2 || b.Length < 2 || a[1].Length != b[1].Length)
            {
                bad.Add($"{what}.argb: layout ours {a[0]}, GDI+ {b[0]}");
                return;
            }
            byte[] x = Unhex(a[1]), y = Unhex(b[1]);
            int max = 0, count = 0;
            for (int i = 0; i < x.Length; i++)
            {
                int d = Math.Abs(x[i] - y[i]);
                if (d > 0) count++;
                max = Math.Max(max, d);
            }
            lock (JpegError) JpegError[what] = (max, count);
            // The libjpeg-compatible reconstruction (islow IDCT, fancy upsampling, jdcolor tables)
            // is exact: any difference at all is a regression.
            if (max > 0) bad.Add($"{what}.argb: a channel {max} levels from GDI+'s ({count} channels differ)");
        }

        private static void AssertNone(List<string> bad) => Assert.True(bad.Count == 0, string.Join("\n", bad));

        // ---- files ------------------------------------------------------------------------------

        [Theory]
        [MemberData(nameof(FileNames))]
        public void EachFile_DecodesAsGdiPlusDecodesIt(string name)
        {
            JsonElement entry = Files.GetProperty(name).GetProperty("image");
            byte[] data = File.ReadAllBytes(Path.Combine(Dir, name));
            var bad = new List<string>();
            using (Image img = Image.FromStream(new MemoryStream(data)))
                Compare(name, img, entry, bad, name.EndsWith(".jpg"), frames: true);
            AssertNone(bad);
        }

        [Theory]
        [MemberData(nameof(IconNames))]
        public void EachIcon_IsPickedAndCopiedAsDotNetDoes(string name)
        {
            JsonElement entry = Files.GetProperty(name);
            byte[] data = File.ReadAllBytes(Path.Combine(Dir, name));
            var bad = new List<string>();
            foreach (int size in new[] { 16, 24, 32, 48, 20, 0 })
            {
                JsonElement o = entry.GetProperty("icon" + size);
                using Icon icon = size == 0 ? new Icon(new MemoryStream(data)) : new Icon(new MemoryStream(data), size, size);
                string s = $"{icon.Width},{icon.Height}";
                string want = $"{o.GetProperty("size")[0].GetInt32()},{o.GetProperty("size")[1].GetInt32()}";
                if (s != want) bad.Add($"icon{size}.size: ours {s}, .NET {want}");
                // A PNG entry: .NET (Core) hands back the PNG's own pixels; the .NET Framework the
                // oracle ran on cannot read one and draws garbage. Its size and format still count.
                bool pngEntry = name == "hand_png.ico" && icon.Width == 20;
                using (Bitmap b = icon.ToBitmap())
                {
                    if (pngEntry)
                    {
                        if (b.PixelFormat != PixelFormat.Format32bppArgb || b.Size != new Size(20, 20)) bad.Add($"icon{size}.ToBitmap: {b.PixelFormat} {b.Size}");
                    }
                    else Compare($"icon{size}.ToBitmap", b, o.GetProperty("bmp"), bad, jpeg: false, frames: false);
                }
                var ms = new MemoryStream();
                icon.Save(ms);
                if (Hex(ms.ToArray()) != o.GetProperty("saved").GetString()) bad.Add($"icon{size}.Save: bytes differ");
            }
            AssertNone(bad);
        }

        // ---- the API ----------------------------------------------------------------------------

        private static readonly PixelFormat[] Formats =
        {
            PixelFormat.Format1bppIndexed, PixelFormat.Format4bppIndexed, PixelFormat.Format8bppIndexed,
            PixelFormat.Format16bppRgb555, PixelFormat.Format16bppRgb565, PixelFormat.Format16bppArgb1555, PixelFormat.Format16bppGrayScale,
            PixelFormat.Format24bppRgb, PixelFormat.Format32bppRgb, PixelFormat.Format32bppArgb, PixelFormat.Format32bppPArgb,
            PixelFormat.Format48bppRgb, PixelFormat.Format64bppArgb, PixelFormat.Format64bppPArgb,
        };

        // The oracle's sample bitmap: a gradient with alpha, built the way Oracle.cs builds it.
        private static Color Px(int x, int y, int w, int h, bool alpha)
        {
            int r = (x * 255) / Math.Max(1, w - 1), g = (y * 255) / Math.Max(1, h - 1), b = ((x + y) * 37) & 255;
            int a = alpha ? ((x * 3 + y * 5) % 4 == 0 ? 0 : (x * 40 + y * 30 + 30) & 255) : 255;
            if (alpha && a == 0) { r = 0; g = 0; b = 0; }
            return Color.FromArgb(a, r, g, b);
        }

        private static Bitmap Sample(int w, int h, PixelFormat f, bool alpha)
        {
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) b.SetPixel(x, y, Px(x, y, w, h, alpha));
            if (f == PixelFormat.Format32bppArgb) return b;
            Bitmap c = b.Clone(new Rectangle(0, 0, w, h), f);
            b.Dispose();
            return c;
        }

        [Fact]
        public void NewBitmaps_OfEveryFormat_AreGdiPlusOnes()
        {
            var bad = new List<string>();
            foreach (PixelFormat pf in Formats)
            {
                using var b = new Bitmap(5, 3, pf);
                Compare("new_" + pf, b, Api.GetProperty("new_" + pf), bad, jpeg: false, frames: false);
            }
            AssertNone(bad);
        }

        [Fact]
        public void SetPixelGetPixel_RoundTrip_AsGdiPlusRoundsThem()
        {
            Color[] probe = { Color.FromArgb(128, 10, 130, 250), Color.FromArgb(255, 1, 2, 3), Color.FromArgb(0, 200, 100, 50),
                              Color.FromArgb(77, 255, 255, 255), Color.FromArgb(255, 7, 135, 252), Color.FromArgb(1, 254, 128, 3) };
            var bad = new List<string>();
            foreach (PixelFormat pf in Formats)
            {
                JsonElement o = Api.GetProperty("setget_" + pf);
                for (int i = 0; i < probe.Length; i++)
                {
                    string ours;
                    try
                    {
                        using var b = new Bitmap(2, 2, pf);
                        b.SetPixel(1, 0, probe[i]);
                        ours = ((uint)b.GetPixel(1, 0).ToArgb()).ToString("x8") + " " + Lock(b, pf);
                    }
                    catch (Exception e) { ours = "EX " + e.GetType().Name; }
                    if (ours != o[i].GetString()) bad.Add($"{pf} {probe[i]}: ours {ours}, GDI+ {o[i].GetString()}");
                }
            }
            AssertNone(bad);
        }

        [Fact]
        public void LockBits_ConvertsAsGdiPlusConverts()
        {
            var bad = new List<string>();
            using var src = Sample(6, 4, PixelFormat.Format32bppArgb, true);
            if (Lock(src, PixelFormat.Format32bppArgb) != Api.GetProperty("lock_src").GetString()) bad.Add("the sample itself differs");
            foreach (PixelFormat pf in Formats)
            {
                string ours;
                try
                {
                    BitmapData d = src.LockBits(new Rectangle(1, 1, 3, 2), ImageLockMode.ReadOnly, pf);
                    int rb = (3 * Image.GetPixelFormatSize(pf) + 7) / 8;
                    var all = new byte[rb * 2];
                    for (int y = 0; y < 2; y++) Marshal.Copy(d.Scan0 + y * d.Stride, all, y * rb, rb);
                    ours = "stride=" + d.Stride + " w=" + d.Width + " h=" + d.Height + " fmt=" + d.PixelFormat + ":" + Hex(all);
                    src.UnlockBits(d);
                }
                catch (Exception e) { ours = "EX " + e.GetType().Name; }
                string theirs = Api.GetProperty("lock_from32_" + pf).GetString()!;
                // The bits after a row's last 1/4bpp pixel are whatever gdiplus.dll's heap held.
                ours = MaskTail(ours, 3, pf); theirs = MaskTail(theirs, 3, pf);
                if (ours != theirs) bad.Add($"lock {pf}: ours {ours}, GDI+ {theirs}");
            }
            foreach (PixelFormat pf in new[] { PixelFormat.Format24bppRgb, PixelFormat.Format16bppRgb565, PixelFormat.Format32bppPArgb, PixelFormat.Format64bppArgb, PixelFormat.Format48bppRgb, PixelFormat.Format16bppArgb1555 })
            {
                using var dst = Sample(4, 3, PixelFormat.Format32bppArgb, true);
                BitmapData d = dst.LockBits(new Rectangle(1, 0, 2, 2), ImageLockMode.WriteOnly, pf);
                int bpp = Image.GetPixelFormatSize(pf) / 8;
                for (int y = 0; y < 2; y++)
                {
                    var row = new byte[2 * bpp];
                    for (int i = 0; i < row.Length; i++) row[i] = (byte)(i * 37 + y * 101 + 13);
                    if (pf == PixelFormat.Format64bppArgb || pf == PixelFormat.Format48bppRgb) for (int i = 1; i < row.Length; i += 2) row[i] &= 0x1f;
                    Marshal.Copy(row, 0, d.Scan0 + y * d.Stride, row.Length);
                }
                dst.UnlockBits(d);
                string ours = Lock(dst, PixelFormat.Format32bppArgb), theirs = Api.GetProperty("write_into32_" + pf).GetString()!;
                if (ours != theirs) bad.Add($"write {pf}: ours {ours}, GDI+ {theirs}");
            }
            using (var c = (Bitmap)src.Clone())
            {
                BitmapData d = c.LockBits(new Rectangle(0, 0, 6, 4), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
                c.UnlockBits(d);
                if (Lock(c, PixelFormat.Format32bppArgb) != Api.GetProperty("readwrite_parg_roundtrip").GetString())
                    bad.Add("a ReadWrite PArgb lock does not lose what GDI+'s loses");
            }
            AssertNone(bad);
        }

        [Fact]
        public void CloneRotateFlipAndFriends_AreGdiPlusOnes()
        {
            var bad = new List<string>();
            using var src = Sample(6, 4, PixelFormat.Format32bppArgb, true);
            foreach (PixelFormat pf in Formats)
            {
                JsonElement o = Api.GetProperty("clone_" + pf);
                if (o.ValueKind == JsonValueKind.String)
                {
                    string ours;
                    try { using var c = src.Clone(new Rectangle(1, 0, 4, 3), pf); ours = "ok"; }
                    catch (Exception e) { ours = "EX " + e.GetType().Name; }
                    if (ours != o.GetString()) bad.Add($"clone {pf}: ours {ours}, GDI+ {o.GetString()}");
                    continue;
                }
                using var clone = src.Clone(new Rectangle(1, 0, 4, 3), pf);
                Compare("clone_" + pf, clone, o, bad, jpeg: false, frames: false);
            }
            foreach (RotateFlipType rf in Enum.GetValues(typeof(RotateFlipType)).Cast<RotateFlipType>().Distinct())
            {
                using var c = (Bitmap)src.Clone();
                c.RotateFlip(rf);
                string ours = c.Width + "x" + c.Height + " " + Lock(c, PixelFormat.Format32bppArgb);
                if (ours != Api.GetProperty("rotate_" + (int)rf).GetString()) bad.Add($"RotateFlip {rf} differs");
            }
            using (var c = Sample(6, 4, PixelFormat.Format24bppRgb, false))
            {
                c.MakeTransparent();
                Compare("maketransparent_default", c, Api.GetProperty("maketransparent_default"), bad, false, false);
            }
            using (var c = Sample(6, 4, PixelFormat.Format32bppArgb, false))
            {
                c.MakeTransparent(Px(2, 1, 6, 4, false));
                Compare("maketransparent_color", c, Api.GetProperty("maketransparent_color"), bad, false, false);
            }
            using (Image t = src.GetThumbnailImage(3, 2, null, IntPtr.Zero))
            {
                string ours = t.Width + "x" + t.Height + " " + t.PixelFormat + " " + t.HorizontalResolution.ToString(CultureInfo.InvariantCulture);
                if (ours != Api.GetProperty("thumb").GetString()) bad.Add($"thumbnail: ours {ours}, GDI+ {Api.GetProperty("thumb").GetString()}");
            }
            using (var c = (Bitmap)src.Clone())
            {
                c.SetResolution(150.5f, 33f);
                string ours = c.HorizontalResolution.ToString(CultureInfo.InvariantCulture) + " " + c.VerticalResolution.ToString(CultureInfo.InvariantCulture)
                              + " " + c.PhysicalDimension.Width.ToString(CultureInfo.InvariantCulture);
                if (ours != Api.GetProperty("setres").GetString()) bad.Add($"SetResolution: ours {ours}, GDI+ {Api.GetProperty("setres").GetString()}");
            }
            using (var c = new Bitmap(src))
                Compare("bitmap_from_image", c, Api.GetProperty("bitmap_from_image"), bad, false, false);
            using (var s24 = Sample(5, 3, PixelFormat.Format24bppRgb, false))
            using (var c = new Bitmap(s24))
                Compare("bitmap_24_from_image", c, Api.GetProperty("bitmap_24_from_image"), bad, false, false);
            {
                GraphicsUnit u = GraphicsUnit.Display;
                RectangleF r = src.GetBounds(ref u);
                if (r + " " + u != Api.GetProperty("bounds").GetString()) bad.Add($"GetBounds: {r} {u}");
            }
            try
            {
                using var b = new Bitmap(2, 2, PixelFormat.Format8bppIndexed);
                b.SetPixel(0, 0, Color.Red);
                bad.Add("SetPixel on an indexed bitmap did not throw");
            }
            catch (InvalidOperationException e)
            {
                if ("EX InvalidOperationException " + e.Message != Api.GetProperty("indexed_setpixel").GetString()) bad.Add("indexed SetPixel: " + e.Message);
            }
            AssertNone(bad);
        }

        [Fact]
        public void Codecs_AreListedAsGdiPlusListsThem()
        {
            var bad = new List<string>();
            string Codecs(IEnumerable<ImageCodecInfo> list) =>
                string.Join("|", list.Select(c => $"{c.Clsid} {c.FormatID} {c.CodecName} {c.FormatDescription} {c.FilenameExtension} {c.MimeType} {(int)c.Flags}"));
            string Oracle(string key) =>
                string.Join("|", Api.GetProperty(key).EnumerateArray().Select(c =>
                    $"{c.GetProperty("clsid").GetString()} {c.GetProperty("fmt").GetString()} {c.GetProperty("name").GetString()} {c.GetProperty("desc").GetString()} {c.GetProperty("ext").GetString()} {c.GetProperty("mime").GetString()} {c.GetProperty("flags").GetInt32()}"));
            if (Codecs(ImageCodecInfo.GetImageEncoders()) != Oracle("encoders")) bad.Add("encoders differ");
            if (Codecs(ImageCodecInfo.GetImageDecoders()) != Oracle("decoders")) bad.Add("decoders differ");
            using (var b = new Bitmap(2, 2))
            {
                if (b.RawFormat.Guid.ToString() != Api.GetProperty("raw_memorybmp").GetString()) bad.Add("a new bitmap's RawFormat");
                if (b.Flags != Api.GetProperty("flags_new32").GetInt32()) bad.Add("a new bitmap's Flags");
            }
            AssertNone(bad);
        }

        private static Bitmap Indexed(int w, int h, PixelFormat f)
        {
            var b = new Bitmap(w, h, f);
            int bits = Image.GetPixelFormatSize(f), n = 1 << bits;
            ColorPalette pal = b.Palette;
            for (int i = 0; i < pal.Entries.Length; i++)
                pal.Entries[i] = Color.FromArgb(255, (i * 67) & 255, (i * 151) & 255, (i * 29 + 40) & 255);
            b.Palette = pal;
            BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, f);
            var row = new byte[d.Stride];
            for (int y = 0; y < h; y++)
            {
                Array.Clear(row, 0, row.Length);
                for (int x = 0; x < w; x++)
                {
                    int v = (x + y * 3) % n;
                    if (bits == 8) row[x] = (byte)v;
                    else if (bits == 4) row[x >> 1] |= (byte)(v << ((x & 1) == 0 ? 4 : 0));
                    else row[x >> 3] |= (byte)(v << (7 - (x & 7)));
                }
                Marshal.Copy(row, 0, d.Scan0 + y * d.Stride, d.Stride);
            }
            b.UnlockBits(d);
            return b;
        }

        /// <summary>Saved and read back, each pixel format comes back in the format, and at the
        /// resolution, a GDI+ round trip gives it -- and the pixels survive wherever the format is
        /// lossless.</summary>
        [Fact]
        public void SaveAndReload_RoundTripsAsGdiPlusDoes()
        {
            var bad = new List<string>();
            foreach (PixelFormat pf in new[] { PixelFormat.Format32bppArgb, PixelFormat.Format24bppRgb, PixelFormat.Format32bppRgb, PixelFormat.Format8bppIndexed, PixelFormat.Format16bppRgb565 })
                foreach (ImageFormat imf in new[] { ImageFormat.Png, ImageFormat.Bmp, ImageFormat.Gif, ImageFormat.Tiff, ImageFormat.Jpeg })
                {
                    using Bitmap b = pf == PixelFormat.Format8bppIndexed ? Indexed(4, 3, pf) : Sample(4, 3, pf, pf == PixelFormat.Format32bppArgb);
                    var ms = new MemoryStream();
                    b.Save(ms, imf);
                    ms.Position = 0;
                    using Image r = Image.FromStream(ms);
                    string[] theirs = Api.GetProperty("resave_" + pf + "_" + imf).GetString()!.Split(' ');
                    string ours = r.PixelFormat + " " + r.RawFormat.Guid;
                    // The resolution as the Framework printed it, to seven digits (the exact float is held to
                    // GDI+ by the file tests, which have it in full).
                    if (ours != theirs[0] + " " + theirs[1] || Math.Abs(r.HorizontalResolution - float.Parse(theirs[2], CultureInfo.InvariantCulture)) > 1e-3f)
                        bad.Add($"{pf} as {imf}: ours {ours} {r.HorizontalResolution}, GDI+ {string.Join(" ", theirs)}");
                    bool lossless = imf == ImageFormat.Png || imf == ImageFormat.Tiff
                                    || (imf == ImageFormat.Bmp && pf != PixelFormat.Format32bppArgb)
                                    || (imf == ImageFormat.Gif && pf == PixelFormat.Format8bppIndexed);
                    if (lossless && Lock((Bitmap)r, PixelFormat.Format32bppArgb).Split(':')[1] != Lock(b, PixelFormat.Format32bppArgb).Split(':')[1])
                        bad.Add($"{pf} as {imf}: the pixels did not survive");
                }
            AssertNone(bad);
        }

        [Fact]
        public void MultiPageTiff_SavedWithSaveAdd_ReadsBackEveryPage()
        {
            ImageCodecInfo tiff = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/tiff");
            using var a = Sample(7, 5, PixelFormat.Format24bppRgb, false);
            using var b = Sample(5, 4, PixelFormat.Format32bppArgb, true);
            var ms = new MemoryStream();
            var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
            a.Save(ms, tiff, p);
            p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.FrameDimensionPage);
            a.SaveAdd(b, p);
            p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.Flush);
            a.SaveAdd(p);
            ms.Position = 0;
            using var r = (Bitmap)Image.FromStream(ms);
            Assert.Equal(2, r.GetFrameCount(FrameDimension.Page));
            Assert.Equal(Lock(a, PixelFormat.Format32bppArgb), Lock(r, PixelFormat.Format32bppArgb));
            r.SelectActiveFrame(FrameDimension.Page, 1);
            Assert.Equal(new Size(5, 4), r.Size);
            Assert.Equal(Lock(b, PixelFormat.Format32bppArgb), Lock(r, PixelFormat.Format32bppArgb));
        }

        [Fact]
        public void JpegQuality_IsHonoured()
        {
            using var b = Sample(16, 16, PixelFormat.Format24bppRgb, false);
            ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            long Size(long q)
            {
                var p = new EncoderParameters(1);
                p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, q);
                var ms = new MemoryStream();
                b.Save(ms, jpeg, p);
                return ms.Length;
            }
            Assert.True(Size(10) < Size(95));
        }

        /// <summary>Drawing INTO a bitmap and reading it back: the recorded scene is rendered onto
        /// the pixels when they are read, and an image drawn 1:1 comes out as GDI+ writes it --
        /// through the premultiplied blend, so a translucent pixel takes the round trip.</summary>
        [Fact]
        public void GraphicsFromImage_RendersOnto_ThePixels()
        {
            using var src = Sample(6, 4, PixelFormat.Format32bppArgb, true);
            using var dst = new Bitmap(6, 4, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
                g.DrawImage(src, 0, 0, 6, 4);
            // What GDI+'s Bitmap(Image) produced, which is this same draw.
            using var reference = new Bitmap(src);
            Assert.Equal(Lock(reference, PixelFormat.Format32bppArgb), Lock(dst, PixelFormat.Format32bppArgb));

            using var bmp = new Bitmap(10, 10, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.FillRectangle(Brushes.Red, 2, 3, 4, 5);
            }
            Assert.Equal(Color.FromArgb(255, 255, 0, 0).ToArgb(), bmp.GetPixel(2, 3).ToArgb());
            Assert.Equal(Color.FromArgb(255, 255, 0, 0).ToArgb(), bmp.GetPixel(5, 7).ToArgb());
            Assert.Equal(Color.White.ToArgb(), bmp.GetPixel(6, 3).ToArgb());
            Assert.Equal(Color.White.ToArgb(), bmp.GetPixel(2, 8).ToArgb());
        }

        [Fact]
        public void Icon_FromHandle_RoundTrips()
        {
            using var b = Sample(16, 16, PixelFormat.Format32bppArgb, true);
            IntPtr h = b.GetHicon();
            using Icon icon = Icon.FromHandle(h);
            Assert.Equal(new Size(16, 16), icon.Size);
            using Bitmap back = icon.ToBitmap();
            Assert.Equal(Lock(b, PixelFormat.Format32bppArgb), Lock(back, PixelFormat.Format32bppArgb));
        }

        [Fact]
        public void TextureBrush_Fills_FromItsImage()
        {
            using var tile = new Bitmap(2, 2, PixelFormat.Format32bppArgb);
            tile.SetPixel(0, 0, Color.Red); tile.SetPixel(1, 0, Color.Lime); tile.SetPixel(0, 1, Color.Blue); tile.SetPixel(1, 1, Color.White);
            using var brush = new TextureBrush(tile);
            using var dst = new Bitmap(6, 6, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
                g.FillRectangle(brush, 0, 0, 6, 6);
            Assert.Equal(Color.Red.ToArgb(), dst.GetPixel(2, 2).ToArgb());
            Assert.Equal(Color.Lime.ToArgb(), dst.GetPixel(3, 4).ToArgb());
            Assert.Equal(Color.White.ToArgb(), dst.GetPixel(5, 5).ToArgb());
        }
    }
}
