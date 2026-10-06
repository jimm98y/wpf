// GPU-rasterization proof. Draws classic Win32 push-buttons (raised + pressed) — 3D bevels and
// centered text — entirely as WgpuSceneRenderer scene primitives via SceneGraphics, renders them
// with WebGPU (RenderToRgba, offscreen — no window, no surface), and writes the GPU's output to a
// PNG with a built-in encoder. NOTHING here touches libgdiplus: the control pixels are produced by
// WGSL shaders. This de-risks the System.Drawing-backend swap (the next tier) — it proves WinForms
// theme drawing (rects, bevels, text) maps onto the GPU scene graph.

using System;
using System.Numerics;
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
        static readonly RgbaColor White         = RgbaColor.FromBytes(255, 255, 255, 255);
        static readonly RgbaColor Highlight     = RgbaColor.FromBytes( 49, 106, 197, 255);  // selection blue

        private static int Main(string[] args)
        {
            const int W = 460, H = 300;
            string outPath = args.Length > 0 ? args[0] : "gpu-form.png";

            var root = new SceneVisual();
            var g = new SceneGraphics(root);

            // A whole WinForms-style dialog drawn ONLY as WebGPU scene primitives (rects, ellipses,
            // polygons, gradients, text) — the same vocabulary Mono's ThemeWin32Classic uses.
            g.FillRectangle(Face, 0, 0, W, H);

            DrawGroupBox(g, new Rect(12, 8, 210, 120), "Options");
            DrawRadio(g, 26, 34, "Fast", selected: true);
            DrawRadio(g, 26, 60, "Accurate", selected: false);
            DrawCheck(g, 26, 90, "Verbose", checkedState: true);

            DrawCombo(g, new Rect(236, 12, 200, 22), "Metal");
            DrawListBox(g, new Rect(236, 48, 200, 96),
                new[] { "alpha", "bravo", "charlie", "delta", "echo" }, selected: 1);

            g.DrawString("Name:", 12, 152, 12f, Text);
            DrawTextBox(g, new Rect(70, 147, 150, 22), "Ada Lovelace");
            DrawProgressBar(g, new Rect(12, 182, 210, 20), 0.4f);
            DrawButton(g, new Rect(12, 220, 120, 32), "Click me", pressed: false);
            g.DrawString("Clicks: 0", 148, 228, 12f, Text);

            using var ctx = WgpuContext.Create();
            // A real TrueType font so lowercase renders (the BuiltinBitmapFont is uppercase-only);
            // glyphs are rasterized on the GPU from the font outlines — still no libgdiplus.
            const string fontPath = "/System/Library/Fonts/Supplemental/Arial.ttf";
            var font = new TrueTypeFont(File.ReadAllBytes(fontPath));
            using var renderer = new WgpuSceneRenderer(ctx, font, new SimpleTextShaper());
            byte[] rgba = renderer.RenderToRgba(root, W, H, Face);   // GPU rasterization, offscreen

            WritePng(outPath, rgba, W, H);
            Console.WriteLine($"GPU-rasterized {rgba.Length / 4} px form -> {outPath} (no libgdiplus)");
            return 0;
        }

        // GroupBox: an etched (two-tone) rectangle with a label gap in the top edge.
        private static void DrawGroupBox(SceneGraphics g, Rect b, string title)
        {
            float x = b.X, y = b.Y + 6, w = b.Width, h = b.Height - 6;
            g.DrawHLine(Dark, x, x + w, y);       g.DrawHLine(LightLight, x, x + w, y + 1);
            g.DrawHLine(Dark, x, x + w, y + h);   g.DrawHLine(LightLight, x, x + w, y + h + 1);
            g.DrawVLine(Dark, x, y, y + h);       g.DrawVLine(LightLight, x + 1, y, y + h);
            g.DrawVLine(Dark, x + w, y, y + h);   g.DrawVLine(LightLight, x + w + 1, y, y + h);
            g.FillRectangle(Face, b.X + 8, b.Y, title.Length * 7 + 6, 12);   // gap for the title
            g.DrawString(title, b.X + 12, b.Y, 12f, Text);
        }

        // RadioButton: a white circle with a gray ring, a black dot when selected, and a label.
        private static void DrawRadio(SceneGraphics g, float x, float y, string label, bool selected)
        {
            float cx = x + 6, cy = y + 6;
            g.DrawEllipse(Dark, White, cx, cy, 6, 6);
            if (selected) g.FillEllipse(Text, cx, cy, 2.5f, 2.5f);
            g.DrawString(label, x + 16, y - 1, 12f, Text);
        }

        // CheckBox: a sunken white box, a checkmark polygon when checked, and a label.
        private static void DrawCheck(SceneGraphics g, float x, float y, string label, bool checkedState)
        {
            g.FillRectangle(White, x, y, 13, 13);
            // sunken border: dark top/left, light bottom/right.
            g.DrawHLine(Dark, x, x + 12, y);      g.DrawVLine(Dark, x, y, y + 12);
            g.DrawHLine(LightLight, x, x + 13, y + 13); g.DrawVLine(LightLight, x + 13, y, y + 13);
            if (checkedState)
                g.FillPolygon(Text,
                    new Vector2(x + 3, y + 6), new Vector2(x + 5, y + 8), new Vector2(x + 10, y + 3),
                    new Vector2(x + 10, y + 5), new Vector2(x + 5, y + 10), new Vector2(x + 3, y + 8));
            g.DrawString(label, x + 18, y - 1, 12f, Text);
        }

        // ComboBox (DropDownList): a white field with the selection text + a raised drop-arrow button.
        private static void DrawCombo(SceneGraphics g, Rect b, string text)
        {
            g.FillRectangle(White, b.X, b.Y, b.Width, b.Height);
            Sunken(g, b);
            g.DrawString(text, b.X + 4, b.Y + 4, 12f, Text);
            var btn = new Rect(b.X + b.Width - 18, b.Y + 1, 16, b.Height - 2);
            g.FillRectangle(Face, btn.X, btn.Y, btn.Width, btn.Height);
            Raised(g, btn);
            float ax = btn.X + 8, ay = btn.Y + btn.Height / 2 + 1;   // down triangle
            g.FillPolygon(Text, new Vector2(ax - 3, ay - 2), new Vector2(ax + 3, ay - 2), new Vector2(ax, ay + 2));
        }

        // ListBox: a white field with items; the selected row is a blue bar with white text.
        private static void DrawListBox(SceneGraphics g, Rect b, string[] items, int selected)
        {
            g.FillRectangle(White, b.X, b.Y, b.Width, b.Height);
            Sunken(g, b);
            for (int i = 0; i < items.Length; i++)
            {
                float iy = b.Y + 2 + i * 16;
                if (i == selected) { g.FillRectangle(Highlight, b.X + 2, iy, b.Width - 4, 16); }
                g.DrawString(items[i], b.X + 4, iy, 12f, i == selected ? White : Text);
            }
        }

        // TextBox: a white sunken field with left-aligned text.
        private static void DrawTextBox(SceneGraphics g, Rect b, string text)
        {
            g.FillRectangle(White, b.X, b.Y, b.Width, b.Height);
            Sunken(g, b);
            g.DrawString(text, b.X + 4, b.Y + 4, 12f, Text);
        }

        // ProgressBar: a sunken trough with classic segmented blue chunks.
        private static void DrawProgressBar(SceneGraphics g, Rect b, float fraction)
        {
            g.FillRectangle(White, b.X, b.Y, b.Width, b.Height);
            Sunken(g, b);
            int chunks = (int)((b.Width - 4) * fraction / 8);
            for (int i = 0; i < chunks; i++)
                g.FillRectangle(Highlight, b.X + 3 + i * 8, b.Y + 3, 6, b.Height - 6);
        }

        // Sunken 3D border (fields): dark top/left, white bottom/right.
        private static void Sunken(SceneGraphics g, Rect b)
        {
            g.DrawHLine(Dark, b.X, b.X + b.Width - 1, b.Y);
            g.DrawVLine(Dark, b.X, b.Y, b.Y + b.Height - 1);
            g.DrawHLine(LightLight, b.X, b.X + b.Width - 1, b.Y + b.Height - 1);
            g.DrawVLine(LightLight, b.X + b.Width - 1, b.Y, b.Y + b.Height - 1);
        }

        // Raised 3D border (buttons): white top/left, dark bottom/right.
        private static void Raised(SceneGraphics g, Rect b)
        {
            g.DrawHLine(LightLight, b.X, b.X + b.Width - 1, b.Y);
            g.DrawVLine(LightLight, b.X, b.Y, b.Y + b.Height - 1);
            g.DrawHLine(DarkDark, b.X, b.X + b.Width - 1, b.Y + b.Height - 1);
            g.DrawVLine(DarkDark, b.X + b.Width - 1, b.Y, b.Y + b.Height - 1);
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
