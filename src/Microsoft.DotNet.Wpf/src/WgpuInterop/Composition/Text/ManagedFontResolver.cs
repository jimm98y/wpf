// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Cross-platform, COM-free font resolution. WPF shapes text with its own font
// stack and, when the managed WebGPU backend is active, sends each glyph run's
// font as a *managed descriptor* -- the font file path, the face index within a
// collection, and the style-simulation flags (bold/italic synthesis) -- sourced
// from the managed GlyphTypeface (FontUri / FaceIndex / StyleSimulations). This
// resolver turns that descriptor into a glyph-outline font with no platform
// font API: it reads the file, picks the right face in a .ttc/.otc collection,
// selects the CFF vs TrueType reader, and applies synthetic bold/oblique.
//
// This replaces the old DWriteFontResolver, which dereferenced a native
// IDWriteFont* via COM and was therefore Windows-only. It is the last COM
// dependency in the engine; with it gone the renderer is fully cross-platform.
//

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Text
{
    /// <summary>
    /// A managed, platform-neutral description of the font a glyph run needs:
    /// the local font file, the face index (for .ttc/.otc collections), and the
    /// WPF StyleSimulations flags (1=Bold, 2=Italic; bitwise-or'd).
    /// </summary>
    internal readonly struct FontDescriptor : IEquatable<FontDescriptor>
    {
        public readonly string Path;
        public readonly int FaceIndex;
        public readonly int Simulations;   // WPF StyleSimulations: 1=Bold, 2=Italic

        public FontDescriptor(string path, int faceIndex, int simulations)
        {
            Path = path;
            FaceIndex = faceIndex;
            Simulations = simulations;
        }

        public bool Bold => (Simulations & 1) != 0;
        public bool Oblique => (Simulations & 2) != 0;

        public bool Equals(FontDescriptor o) =>
            FaceIndex == o.FaceIndex && Simulations == o.Simulations &&
            string.Equals(Path, o.Path, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object? o) => o is FontDescriptor d && Equals(d);
        public override int GetHashCode() =>
            HashCode.Combine(Path?.ToLowerInvariant(), FaceIndex, Simulations);
    }

    /// <summary>
    /// Resolves a <see cref="FontDescriptor"/> to a glyph-outline font (cached;
    /// null on any failure, in which case the glyph run is skipped). Pure managed
    /// -- no COM, no DirectWrite -- so it works on any platform wgpu runs on.
    /// </summary>
    internal sealed class ManagedFontResolver
    {
        private readonly Dictionary<FontDescriptor, IGlyphOutlineFont?> _cache = new();

        public IGlyphOutlineFont? Resolve(FontDescriptor desc)
        {
            if (string.IsNullOrEmpty(desc.Path)) return null;
            if (_cache.TryGetValue(desc, out IGlyphOutlineFont? cached)) return cached;

            IGlyphOutlineFont? font = null;
            try
            {
                font = Load(desc);
                if (font != null) Log($"resolved font {desc.Path} face {desc.FaceIndex} sim {desc.Simulations}");
            }
            catch (Exception ex)
            {
                Log($"font resolve failed {desc.Path}: {ex.GetType().Name}: {ex.Message}");
                font = null;
            }
            _cache[desc] = font;
            return font;
        }

        private static IGlyphOutlineFont? Load(FontDescriptor desc)
        {
            string path = desc.Path;
            // The descriptor's path may be a file:// URI (WPF GlyphTypeface.FontUri)
            // or a plain filesystem path; accept both.
            if (Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) && uri.IsFile)
                path = uri.LocalPath;

            if (!File.Exists(path)) return null;
            byte[] bytes = File.ReadAllBytes(path);

            // A TrueType/OpenType Collection ('ttcf'/'otto' wrapper) holds several
            // faces; map the face index to that face's sfnt-header offset. A plain
            // single-face file has sfntOffset 0.
            int sfntOffset = ResolveSfntOffset(bytes, desc.FaceIndex);
            if (sfntOffset < 0) return null;

            return CffFont.IsCff(bytes, sfntOffset)
                ? new CffFont(bytes, desc.Bold, desc.Oblique, sfntOffset)
                : new TrueTypeFont(bytes, desc.Bold, desc.Oblique, sfntOffset);
        }

        // Returns the sfnt-header byte offset for the given face index, or 0 for a
        // single-face file. Returns -1 if the requested face is out of range.
        private static int ResolveSfntOffset(byte[] bytes, int faceIndex)
        {
            if (bytes.Length >= 12 &&
                bytes[0] == (byte)'t' && bytes[1] == (byte)'t' &&
                bytes[2] == (byte)'c' && bytes[3] == (byte)'f')
            {
                uint numFonts = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8));
                if (faceIndex < 0 || (uint)faceIndex >= numFonts) return -1;
                int dirPos = 12 + faceIndex * 4;
                if (dirPos + 4 > bytes.Length) return -1;
                return (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(dirPos));
            }
            // Not a collection: only face 0 is meaningful.
            return faceIndex <= 0 ? 0 : -1;
        }

        private static readonly string? s_log = Environment.GetEnvironmentVariable("WPF_WEBGPU_SINK_LOG");
        private static void Log(string m)
        {
            if (s_log != null) try { File.AppendAllText(s_log, m + Environment.NewLine); } catch { }
        }
    }
}
