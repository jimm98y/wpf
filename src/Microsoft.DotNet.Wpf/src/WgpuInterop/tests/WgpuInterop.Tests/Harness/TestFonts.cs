// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Finding a real TrueType font to test with, on any of the three operating systems.
//
// Seven standalone apps each carried their own copy of this search, and they had drifted: some
// looked only in the Windows fonts folder and hard-failed elsewhere, which is why several text tests
// simply could not run off-Windows. The search order here is: an explicit override, then the fonts
// the repo BUNDLES (deterministic across machines, which is what a test wants), then the OS font
// directories as a fallback.
//
// Preferring the bundled fonts matters for more than convenience: a text test that resolves a
// different face per platform is not comparing the same thing per platform, and this suite exists
// to compare platforms.
//

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using Xunit;

namespace WgpuInterop.Tests.Harness
{
    internal static class TestFonts
    {
        private static readonly Lazy<string?> s_path = new(Find);

        /// <summary>A TrueType file to test with, or null if the machine has none.</summary>
        public static string? Path => s_path.Value;

        /// <summary>Skip the calling test when no font could be found.</summary>
        public static string Require()
        {
            Assert.SkipWhen(Path is null,
                "no TrueType font found on this machine (set WGPU_TEST_FONT to one)");
            return Path!;
        }

        /// <summary>The font, loaded. Cached: parsing is not what these tests are measuring.</summary>
        public static TrueTypeFont Load()
        {
            string p = Require();
            return s_loaded.TryGetValue(p, out TrueTypeFont? f)
                ? f
                : s_loaded[p] = new TrueTypeFont(File.ReadAllBytes(p));
        }

        private static readonly Dictionary<string, TrueTypeFont> s_loaded = new();

        /// <summary>
        /// A font VENDORED IN THIS REPO (sdk/WpfWebGpu.Sdk/web/fonts), located by walking up from the
        /// test binary. Tests that guard the substitution tables need the exact file the SDK ships,
        /// not whatever the machine happens to have, or they assert nothing about what users get.
        /// Returns null when the repo layout is not above the binary (e.g. a packaged test run).
        /// </summary>
        public static string? RepoFont(string fileName)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 12 && d is not null; i++, d = d.Parent)
            {
                string p = System.IO.Path.Combine(d.FullName, "sdk", "WpfWebGpu.Sdk", "web", "fonts", fileName);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        /// <summary>
        /// A CFF / OpenType-PostScript face (.otf), or null. A different outline format from the
        /// TrueType path -- Type 2 charstrings rather than quadratic glyf contours -- so it needs its
        /// own file; a .ttf will not exercise CffFont at all.
        /// </summary>
        public static string? FindCff()
        {
            string? env = Environment.GetEnvironmentVariable("WGPU_TEST_CFF");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

            // STIX ships with macOS and many Linux distros and is a real CFF face WITH LATIN COVERAGE.
            // That last part matters: the Noto *-Regular.otf files present on many boxes are
            // script-specific and have no 'o', so they would fail the counter check for the wrong reason.
            foreach (string c in new[]
            {
                "/System/Library/Fonts/Supplemental/STIXGeneral.otf",
                "/usr/share/fonts/opentype/stix/STIXGeneral.otf",
                @"C:\Program Files\Adobe\Acrobat DC\Acrobat\CrashReporterResources\AdobeClean-Regular.otf",
            })
                if (File.Exists(c)) return c;

            foreach (string f in Scan("*.otf")) return f;

            // A .ttf can still be CFF-flavoured: the sfnt tag is 'OTTO'.
            foreach (string f in Scan("*.ttf"))
            {
                try
                {
                    byte[] head = new byte[4];
                    using FileStream fs = File.OpenRead(f);
                    if (fs.Read(head, 0, 4) == 4 && head[0] == (byte)'O' && head[1] == (byte)'T'
                        && head[2] == (byte)'T' && head[3] == (byte)'O') return f;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// A font that might carry COLR/CPAL colour layers, or null.
        ///
        /// "Might" is deliberate. COLR/CPAL is the Windows/Google flavour (Segoe UI Emoji, some Noto
        /// builds); Apple ships sbix and many Linux distros ship CBDT/CBLC bitmaps instead. Finding
        /// an emoji font therefore does NOT mean finding a COLR one, so callers have to check for
        /// layers and skip separately -- see ColorGlyphTests.
        /// </summary>
        public static string? FindEmojiFont()
        {
            string? env = Environment.GetEnvironmentVariable("WGPU_TEST_EMOJI");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

            // The VENDORED COLR face first, and for the usual reason (see this file's header): it is
            // the same file on every machine, so the colour-glyph tests compare the same thing
            // everywhere instead of testing whatever emoji font the OS happens to have.
            //
            // It also makes them RUN everywhere. The system emoji fonts are mostly not COLR at all --
            // Noto Color Emoji is CBDT/CBLC and Apple Color Emoji is sbix, neither of which loads as
            // an outline font -- so off Windows these tests used to skip themselves with an accurate
            // but unhelpful "it is a bitmap emoji font" message, and the colour path went untested on
            // the very platforms that ship the font this SDK now has to compensate for.
            string? vendored = RepoFont("TwemojiMozilla.ttf");
            if (vendored is not null) return vendored;

            foreach (string c in new[]
            {
                @"C:\Windows\Fonts\seguiemj.ttf",
                "/usr/share/fonts/truetype/noto/NotoColorEmoji.ttf",
                "/usr/share/fonts/truetype/noto/NotoColorEmoji-Regular.ttf",
            })
                if (File.Exists(c)) return c;

            foreach (string f in Scan("*Emoji*.ttf")) return f;
            return null;
        }

        /// <summary>
        /// A colour BITMAP emoji font (CBDT/CBLC), or null.
        ///
        /// Deliberately NOT the vendored COLR face: this is the other format, the one Linux and
        /// Android actually ship, and the point of the tests that use it is that a font with no
        /// outlines at all can be loaded and drawn. It is not vendored either -- ~10 MB to test a
        /// parser with is not worth carrying -- so these tests skip where the machine has none,
        /// which on Windows is always.
        /// </summary>
        public static string? FindBitmapEmojiFont()
        {
            string? env = Environment.GetEnvironmentVariable("WGPU_TEST_EMOJI_BITMAP");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

            foreach (string c in new[]
            {
                "/usr/share/fonts/truetype/noto/NotoColorEmoji.ttf",
                "/usr/share/fonts/google-noto-emoji/NotoColorEmoji.ttf",
                "/usr/share/fonts/noto/NotoColorEmoji.ttf",
                "/system/fonts/NotoColorEmoji.ttf",
            })
                if (File.Exists(c)) return c;

            return null;
        }

        /// <summary>A TrueType/OpenType Collection (.ttc), or null.</summary>
        public static string? FindTtc()
        {
            string? env = Environment.GetEnvironmentVariable("WGPU_TEST_TTC");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

            foreach (string c in new[]
            {
                "/System/Library/Fonts/AppleSDGothicNeo.ttc",
                "/System/Library/Fonts/Helvetica.ttc",
            })
                if (File.Exists(c)) return c;

            foreach (string f in Scan("*.ttc")) return f;
            return null;
        }


        /// <summary>
        /// Every font file matching a pattern, across the OS font directories, RECURSIVELY.
        ///
        /// Recursion and the explicit /usr/share/fonts roots both matter. An earlier version searched
        /// only Environment.SpecialFolder.Fonts and the top level at that, and on this Linux box it
        /// found nothing -- so the CFF and collection tests reported "no such font on this machine"
        /// and skipped, while /usr/share/fonts/opentype/urw-base35 held 30 CFF faces and
        /// .../noto held several collections. That is the worst kind of skip: it reads as a
        /// principled gate and is actually lost coverage.
        /// </summary>
        private static IEnumerable<string> Scan(string pattern)
        {
            var roots = new List<string>();
            try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.Fonts)); } catch { }
            roots.AddRange(new[]
            {
                "/usr/share/fonts", "/usr/local/share/fonts",
                "/System/Library/Fonts", "/Library/Fonts",
            });

            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                string[] found;
                try { found = Directory.GetFiles(root, pattern, SearchOption.AllDirectories); }
                catch { continue; }
                Array.Sort(found, StringComparer.Ordinal);   // deterministic across runs
                foreach (string f in found) yield return f;
            }
        }

        /// <summary>Like <see cref="RepoFont"/>, but skips the test when the repo copy is not present.</summary>
        public static TrueTypeFont RequireRepoFont(string fileName)
        {
            string? p = RepoFont(fileName);
            Assert.SkipWhen(p is null, $"vendored {fileName} not found under sdk/WpfWebGpu.Sdk/web/fonts");
            return s_loaded.TryGetValue(p!, out TrueTypeFont? f)
                ? f
                : s_loaded[p!] = new TrueTypeFont(File.ReadAllBytes(p!));
        }

        private static string? Find()
        {
            string? env = Environment.GetEnvironmentVariable("WGPU_TEST_FONT");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

            // Fonts this repo bundles and deploys beside apps: identical bytes on every OS.
            foreach (string dir in new[]
            {
                System.IO.Path.Combine(AppContext.BaseDirectory, "fonts"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "..", "..",
                                       "sdk", "WpfWebGpu.Sdk", "web", "fonts"),
            })
            {
                foreach (string name in new[] { "Selawik-Regular.ttf", "CascadiaCode-Regular.ttf", "Symbols.ttf" })
                {
                    string c = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, name));
                    if (File.Exists(c)) return c;
                }
            }

            string sys = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (string name in new[] { "arial.ttf", "segoeui.ttf", "tahoma.ttf", "verdana.ttf" })
            {
                string c = System.IO.Path.Combine(sys, name);
                if (File.Exists(c)) return c;
            }

            foreach (string c in new[]
            {
                "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
                "/usr/share/fonts/TTF/DejaVuSans.ttf",
                "/Library/Fonts/Arial.ttf",
                "/System/Library/Fonts/Supplemental/Arial.ttf",
                "/System/Library/Fonts/Helvetica.ttc",
            })
                if (File.Exists(c)) return c;

            return null;
        }
    }
}
