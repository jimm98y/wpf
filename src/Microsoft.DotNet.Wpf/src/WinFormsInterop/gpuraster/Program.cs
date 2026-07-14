// GPU-rasterization proof. Draws classic Win32 push-buttons (raised + pressed) — 3D bevels and
// centered text — entirely as WgpuSceneRenderer scene primitives via SceneGraphics, renders them
// with WebGPU (RenderToRgba, offscreen — no window, no surface), and writes the GPU's output to a
// PNG with a built-in encoder. NOTHING here touches libgdiplus: the control pixels are produced by
// WGSL shaders. This de-risks the System.Drawing-backend swap (the next tier) — it proves WinForms
// theme drawing (rects, bevels, text) maps onto the GPU scene graph.

using System;
using System.IO;
using System.IO.Compression;
using Microsoft.Wpf.Interop.WebGpu;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

namespace WinFormsGpuRaster
{
    internal static class Program
    {
        // Classic Win32 (ThemeWin32Classic) palette.
        static readonly RgbaColor Face          = RgbaColor.FromBytes(212, 208, 200, 255);
        static readonly RgbaColor LightLight    = RgbaColor.FromBytes(255, 255, 255, 255);
        static readonly RgbaColor Light         = RgbaColor.FromBytes(223, 223, 223, 255);
        static readonly RgbaColor Dark          = RgbaColor.FromBytes(128, 128, 128, 255);
        static readonly RgbaColor DarkDark      = RgbaColor.FromBytes( 64,  64,  64, 255);
        static readonly RgbaColor Text          = RgbaColor.FromBytes(  0,   0,   0, 255);

        private static int Main(string[] args)
        {
            const int W = 300, H = 140;
            string outPath = args.Length > 0 ? args[0] : "gpu-button.png";

            var root = new SceneVisual();
            var g = new SceneGraphics(root);

            // Dialog background.
            g.FillRectangle(Face, 0, 0, W, H);

            // A raised push-button and a pressed one (bevel inverted + label nudged), like a WinForms
            // Button's normal vs MouseDown paint.
            DrawButton(g, new Rect(30, 30, 110, 34), "Click me", pressed: false);
            DrawButton(g, new Rect(30, 84, 110, 34), "Pressed",  pressed: true);

            using var ctx = WgpuContext.Create();
            // A real TrueType font so lowercase renders (the BuiltinBitmapFont is uppercase-only);
            // glyphs are rasterized on the GPU from the font outlines — still no libgdiplus.
            const string fontPath = "/System/Library/Fonts/Supplemental/Arial.ttf";
            var font = new TrueTypeFont(File.ReadAllBytes(fontPath));
            using var renderer = new WgpuSceneRenderer(ctx, font, new SimpleTextShaper());
            byte[] rgba = renderer.RenderToRgba(root, W, H, Face);   // GPU rasterization, offscreen

            WritePng(outPath, rgba, W, H);
            Console.WriteLine($"GPU-rasterized {rgba.Length / 4} px -> {outPath} (no libgdiplus)");
            return 0;
        }

        // Classic raised/sunken 3D button (CPDrawBorder3D-style): face fill, a two-tone bevel on each
        // pair of edges, and centered text.
        private static void DrawButton(SceneGraphics g, Rect b, string label, bool pressed)
        {
            float x = b.X, y = b.Y, w = b.Width, h = b.Height;
            g.FillRectangle(Face, x, y, w, h);

            RgbaColor tlOuter = pressed ? DarkDark : LightLight;   // top/left
            RgbaColor tlInner = pressed ? Dark     : Light;
            RgbaColor brOuter = pressed ? LightLight : DarkDark;   // bottom/right
            RgbaColor brInner = pressed ? Light      : Dark;

            // Outer bevel.
            g.DrawHLine(tlOuter, x, x + w - 1, y);
            g.DrawVLine(tlOuter, x, y, y + h - 1);
            g.DrawHLine(brOuter, x, x + w - 1, y + h - 1);
            g.DrawVLine(brOuter, x + w - 1, y, y + h - 1);
            // Inner bevel (inset 1px).
            g.DrawHLine(tlInner, x + 1, x + w - 2, y + 1);
            g.DrawVLine(tlInner, x + 1, y + 1, y + h - 2);
            g.DrawHLine(brInner, x + 1, x + w - 2, y + h - 2);
            g.DrawVLine(brInner, x + w - 2, y + 1, y + h - 2);

            // Centered label; sunken button nudges its text down-right by 1px.
            var textBounds = pressed ? new Rect(b.X + 1, b.Y + 1, b.Width, b.Height) : b;
            g.DrawStringCentered(label, textBounds, 12f, Text);
        }

        // ---- minimal PNG encoder (RGBA, no libgdiplus) --------------------------------
        private static void WritePng(string path, byte[] rgba, int w, int h)
        {
            using var fs = new FileStream(path, FileMode.Create);
            fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            var ihdr = new byte[13];
            WriteBE(ihdr, 0, (uint)w); WriteBE(ihdr, 4, (uint)h);
            ihdr[8] = 8;   // bit depth
            ihdr[9] = 6;   // color type: RGBA
            WriteChunk(fs, "IHDR", ihdr);

            // Filtered scanlines (filter 0) then zlib-compress.
            var raw = new byte[h * (1 + w * 4)];
            for (int y = 0; y < h; y++)
            {
                raw[y * (1 + w * 4)] = 0;
                Array.Copy(rgba, y * w * 4, raw, y * (1 + w * 4) + 1, w * 4);
            }
            using var comp = new MemoryStream();
            using (var z = new ZLibStream(comp, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw, 0, raw.Length);
            WriteChunk(fs, "IDAT", comp.ToArray());
            WriteChunk(fs, "IEND", Array.Empty<byte>());
        }

        private static void WriteBE(byte[] a, int o, uint v)
        { a[o] = (byte)(v >> 24); a[o + 1] = (byte)(v >> 16); a[o + 2] = (byte)(v >> 8); a[o + 3] = (byte)v; }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; WriteBE(len, 0, (uint)data.Length); s.Write(len);
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(t); s.Write(data);
            // PNG CRC-32 covers the chunk type + data.
            uint crc = (Crc32(data, Crc32(t, 0xFFFFFFFF))) ^ 0xFFFFFFFF;
            var c = new byte[4]; WriteBE(c, 0, crc); s.Write(c);
        }

        private static uint[] _crcTable;
        private static uint Crc32(byte[] data, uint crc)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    _crcTable[n] = c;
                }
            }
            foreach (byte bb in data) crc = _crcTable[(crc ^ bb) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
