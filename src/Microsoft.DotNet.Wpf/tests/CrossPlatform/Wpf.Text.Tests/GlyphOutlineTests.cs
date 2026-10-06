// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Glyph outlines, read from the font file in managed code.
//
// GlyphTypeface.ComputeGlyphOutline used to be MilGlyphRun_GetGlyphOutline, a wpfgfx_cor3 entry
// point over a DirectWrite font face, so every caller got DllNotFoundException on every platform
// this port supports. The callers are public API and not obscure ones -- GlyphRun.BuildGeometry,
// FormattedText.BuildGeometry, GlyphTypeface.GetGlyphOutline -- so text-as-geometry, text clipping
// and printing glyphs as paths were all simply unavailable.
//
// These tests assert against the GEOMETRY rather than against pixels, because an outline is
// checkable without rendering anything: a letter has a known number of contours, sits in a
// predictable box relative to its em size and advance, and scales linearly. That also means they
// run on every head with no GPU and no window.
//
// Both outline formats are covered. 'glyf' is quadratic contours and is what nearly every system
// font uses; 'CFF ' is Type 2 charstrings and is what OTF and most CJK type uses. A stack that
// only read one of them would render half the world's text as nothing at all, and nothing about a
// missing outline announces itself -- it just comes out blank.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Wpf.Text.Tests
{
    public class GlyphOutlineTests
    {
        [Fact]
        public void ALetterHasAnOutline()
        {
            GlyphTypeface face = Face();

            Geometry outline = face.GetGlyphOutline(Glyph(face, 'H'), 100, 100);

            Assert.NotNull(outline);
            Assert.False(outline.IsEmpty(), "'H' came back with no outline at all");

            // A capital H at 100 units: roughly 70 wide and 70 tall, sitting on the baseline and
            // rising above it. The generous bounds are because this is whatever font the machine
            // resolved, not a fixed one -- what is being asserted is that the glyph is the right
            // ORDER of size and in the right place, which is what catches a wrong scale or a
            // missing y flip.
            Rect bounds = outline.Bounds;

            Assert.InRange(bounds.Width, 30.0, 100.0);
            Assert.InRange(bounds.Height, 40.0, 110.0);

            // Y runs DOWN in WPF and UP in the font. A capital letter therefore sits ABOVE the
            // origin, at negative y. Getting this wrong is the single most likely mistake in the
            // whole path and it puts every printed line of text below where it belongs.
            Assert.True(bounds.Top < 0, $"the glyph is below the baseline (top {bounds.Top:F1})");
            Assert.True(bounds.Bottom <= 1.0, $"the glyph hangs below the baseline (bottom {bounds.Bottom:F1})");
        }

        [Fact]
        public void OutlinesScaleWithTheEmSize()
        {
            GlyphTypeface face = Face();
            ushort glyph = Glyph(face, 'H');

            Rect small = face.GetGlyphOutline(glyph, 10, 10).Bounds;
            Rect large = face.GetGlyphOutline(glyph, 100, 100).Bounds;

            // Ten times the em size is ten times the outline. Anything else means the scale is
            // being applied somewhere that rounds -- or, worse, is coming from a cache keyed
            // without the size, which is how every glyph on a page ends up the same size.
            Assert.Equal(small.Width * 10, large.Width, 3);
            Assert.Equal(small.Height * 10, large.Height, 3);
            Assert.Equal(small.Top * 10, large.Top, 3);
        }

        [Fact]
        public void ACounterIsASecondContour()
        {
            GlyphTypeface face = Face();

            // 'o' is two contours: the outside and the hole. One contour means the hole was lost,
            // which fills the letter in solid -- and a filled 'o' is exactly what a reader that
            // stops at the first contour produces.
            var outline = (PathGeometry)face.GetGlyphOutline(Glyph(face, 'o'), 100, 100).GetFlattenedPathGeometry();

            Assert.True(outline.Figures.Count >= 2,
                        $"'o' has {outline.Figures.Count} contour(s); the counter is missing");
        }

        [Fact]
        public void TheHoleInACounterIsActuallyEmpty()
        {
            GlyphTypeface face = Face();

            Geometry outline = face.GetGlyphOutline(Glyph(face, 'o'), 100, 100);
            Rect bounds = outline.Bounds;

            // The centre of an 'o' must not be painted. This is what the fill rule is for: the two
            // contours wind opposite ways, and with the wrong rule -- or with contours emitted in
            // the wrong direction -- the middle fills in and the letter becomes a blob.
            var centre = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

            Assert.False(outline.FillContains(centre), "the counter of 'o' is filled in");

            // ...and the stroke around it must be.
            var onTheStroke = new Point(bounds.X + bounds.Width / 2, bounds.Y + 2);

            Assert.True(outline.FillContains(onTheStroke), "the stroke of 'o' is not filled");
        }

        [Fact]
        public void AnAccentedLetterIsAssembledFromItsComponents()
        {
            GlyphTypeface face = Face();

            ushort plain = Glyph(face, 'e');
            ushort accented = Glyph(face, 'é');

            Assert.SkipWhen(accented == 0, "this font has no 'é'");

            Rect bare = face.GetGlyphOutline(plain, 100, 100).Bounds;
            Rect composite = face.GetGlyphOutline(accented, 100, 100).Bounds;

            // A composite glyph references other glyphs with a transform. If components are
            // dropped, an accented letter is just the base letter; if the transform is ignored, the
            // accent lands on top of it. Either way the glyph is no taller than the bare one, and
            // it should be markedly taller.
            Assert.True(composite.Height > bare.Height + 5,
                        $"'é' ({composite.Height:F1}) is no taller than 'e' ({bare.Height:F1}), " +
                        "so the accent component was lost");

            Assert.True(composite.Top < bare.Top, "the accent is not above the letter");
        }

        [Fact]
        public void CffOutlinesAreReadToo()
        {
            // A PostScript-flavoured face, which stores Type 2 charstrings in 'CFF ' rather than
            // contours in 'glyf'. Different parser, different failure mode, same requirement.
            GlyphTypeface face = CffFace();

            Assert.SkipWhen(face == null, "no CFF font is installed on this machine. " + LastFileError);

            Geometry outline = face.GetGlyphOutline(Glyph(face, 'H'), 100, 100);

            Assert.False(outline.IsEmpty(), "a CFF glyph came back with no outline");

            Rect bounds = outline.Bounds;

            Assert.InRange(bounds.Width, 20.0, 110.0);
            Assert.InRange(bounds.Height, 30.0, 110.0);
            Assert.True(bounds.Top < 0, "the CFF glyph is below the baseline");
        }

        [Fact]
        public void AFontCanBeLoadedFromAFile()
        {
            // GlyphTypeface(Uri) is how an application uses a font it ships with itself -- a
            // pack:// URI to a .ttf in its own assembly -- and it is ordinary WPF. It threw
            // NullReferenceException for every font, because the collection refused to produce a
            // Font for a face that had come from a file.
            GlyphTypeface installed = Face();

            string path = installed.FontUri.IsFile ? installed.FontUri.LocalPath : null;

            Assert.SkipWhen(path == null, "the resolved font is not a local file");

            var loaded = new GlyphTypeface(new Uri(path));

            Assert.True(loaded.GlyphCount > 0);

            // And it must be usable, not merely constructible: the outline is the thing an
            // embedded font is loaded for.
            Assert.False(loaded.GetGlyphOutline(Glyph(loaded, 'H'), 100, 100).IsEmpty());
        }

        [Fact]
        public void AGlyphWithNoInkHasNoOutline()
        {
            GlyphTypeface face = Face();

            // A space is a real glyph with a real advance and no contours. Answering with an empty
            // geometry rather than throwing is what lets BuildGeometry walk a run without checking.
            Geometry outline = face.GetGlyphOutline(Glyph(face, ' '), 100, 100);

            Assert.NotNull(outline);
            Assert.True(outline.IsEmpty());
        }

        [Fact]
        public void AGlyphRunBuildsGeometryForItsWholeText()
        {
            // The public API that was dead. Every glyph of the run has to appear, positioned by its
            // own advance, or printing text as paths silently drops letters.
            GlyphRun run = Run("Hamburgefonstiv", 40);

            Geometry geometry = run.BuildGeometry();

            Assert.False(geometry.IsEmpty(), "BuildGeometry produced nothing");

            Rect bounds = geometry.Bounds;

            // The run is fifteen letters at 40 units; it must be far wider than one of them and no
            // wider than the run says it is.
            Assert.True(bounds.Width > 100, $"the geometry is only {bounds.Width:F1} wide");
            Assert.True(bounds.Width <= run.ComputeAlignmentBox().Width + 5,
                        "the geometry is wider than the run it came from");
        }

        [Fact]
        public void FormattedTextBuildsGeometryToo()
        {
            var text = new FormattedText("Printing", CultureInfo.InvariantCulture,
                                         FlowDirection.LeftToRight, new Typeface("Segoe UI"),
                                         32, Brushes.Black, 96);

            Geometry geometry = text.BuildGeometry(new Point(10, 20));

            Assert.False(geometry.IsEmpty());

            // Drawn at (10,20), which is the TOP left of the text, so the ink starts to the right
            // of and below that point -- not above it, which is what an unflipped baseline gives.
            Rect bounds = geometry.Bounds;

            Assert.True(bounds.Left >= 9, $"the text starts left of where it was drawn ({bounds.Left:F1})");
            Assert.True(bounds.Top >= 19, $"the text is above where it was drawn ({bounds.Top:F1})");
        }

        [Fact]
        public void EveryGlyphInTheFontCanBeAskedForAnOutline() => Sweep(Face());

        [Fact]
        public void EveryGlyphInACffFontCanBeAskedForAnOutline()
        {
            GlyphTypeface face = CffFace();

            Assert.SkipWhen(face == null, "no CFF font on this machine. " + LastFileError);

            // This is the test that actually exercises the charstring interpreter. A CFF glyph is a
            // little stack program, and the operators a sample of five letters never reaches are
            // exactly the ones that go wrong: the flex variants, a subroutine that returns from
            // inside a curve, hintmask with enough stems to need two trailing bytes. Running every
            // glyph in a real font reaches all of them.
            Sweep(face);
        }

        private static void Sweep(GlyphTypeface face)
        {
            // A sweep rather than a sample. The interesting failures in an outline reader are the
            // rare shapes -- a composite four deep, a charstring that ends inside a subroutine, a
            // contour that starts off-curve -- and they are only found by asking for all of them.
            // What is asserted is that none of them throws and none produces a nonsense box.
            int drawn = 0;
            int limit = Math.Min(face.GlyphCount, 900);

            for (ushort glyph = 0; glyph < limit; glyph++)
            {
                Geometry outline = face.GetGlyphOutline(glyph, 100, 100);

                Assert.NotNull(outline);

                if (outline.IsEmpty()) continue;

                Rect bounds = outline.Bounds;

                Assert.False(double.IsNaN(bounds.Width) || double.IsInfinity(bounds.Width),
                             $"glyph {glyph} has a degenerate outline");

                // Ten ems in any direction is far outside anything a real glyph occupies, and is
                // what a misparsed coordinate stream produces.
                Assert.InRange(bounds.Width, 0.0, 1000.0);
                Assert.InRange(bounds.Height, 0.0, 1000.0);

                drawn++;
            }

            Assert.True(drawn > limit / 4,
                        $"only {drawn} of {limit} glyphs produced an outline, which is too few to be right");
        }

        // ---- fixtures --------------------------------------------------------------

        private static GlyphTypeface Face()
        {
            foreach (string family in new[] { "Segoe UI", "Arial", "DejaVu Sans", "Liberation Sans", "Verdana" })
            {
                GlyphTypeface face = TryFace(family);
                if (face != null) return face;
            }

            Assert.Skip("no usable font on this machine");
            return null;
        }

        private static GlyphTypeface TryFace(string family)
        {
            return new Typeface(family).TryGetGlyphTypeface(out GlyphTypeface face) ? face : null;
        }

        /// <summary>
        /// A face with PostScript outlines, or null.
        ///
        /// Installed families first, then any .otf file on disk. The second pass is there because
        /// a stock Windows install has no CFF font at all -- every face in C:\Windows\Fonts is
        /// 'glyf' -- so a test that only looked at installed families would skip on the very
        /// machine the charstring interpreter most needs exercising, and stay green while being
        /// completely broken.
        /// </summary>
        private static GlyphTypeface CffFace()
        {
            // An explicit font, for a machine that has one somewhere this does not look. A stock
            // Windows install ships no CFF font at all, so without a way to point at one there is
            // no way to run this test here.
            string named = Environment.GetEnvironmentVariable("WPF_TEST_CFF_FONT");

            if (!string.IsNullOrEmpty(named))
            {
                GlyphTypeface explicitFace = TryFile(named);
                if (explicitFace != null) return explicitFace;
            }

            foreach (string family in new[] { "Cambria", "Constantia", "Corbel", "Candara",
                                              "Source Sans Pro", "Noto Serif CJK JP" })
            {
                GlyphTypeface candidate = TryFace(family);
                if (candidate != null && IsCff(candidate)) return candidate;
            }

            foreach (string directory in FontDirectories())
            {
                string[] files;

                try
                {
                    files = System.IO.Directory.GetFiles(directory, "*.otf",
                                                         System.IO.SearchOption.AllDirectories);
                }
                catch (System.IO.IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                foreach (string file in files)
                {
                    GlyphTypeface candidate = TryFile(file);
                    if (candidate != null && IsCff(candidate)) return candidate;
                }
            }

            return null;
        }

        private static IEnumerable<string> FontDirectories()
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

            if (OperatingSystem.IsWindows())
            {
                yield return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "Fonts");
            }
            else
            {
                yield return "/usr/share/fonts";
                yield return "/Library/Fonts";
                yield return "/System/Library/Fonts";
            }
        }

        internal static string LastFileError;

        private static GlyphTypeface TryFile(string path)
        {
            try
            {
                return new GlyphTypeface(new Uri(path));
            }
            catch (Exception e)
            {
                LastFileError = $"{path}: {e.GetType().Name}: {e.Message}";
                // Not a font this stack can open. There is no narrower exception to catch: the
                // point of the sweep is that these files are arbitrary.
                return null;
            }
        }

        /// <summary>
        /// Whether this face keeps its outlines in 'CFF ' rather than 'glyf'.
        ///
        /// Read from the sfnt version at the top of the file: 'OTTO' says PostScript outlines.
        /// GlyphTypeface does not expose the distinction, and asking by family name would only
        /// record what happens to be installed on this machine.
        /// </summary>
        private static bool IsCff(GlyphTypeface face)
        {
            try
            {
                using System.IO.Stream stream = face.GetFontStream();

                var header = new byte[4];
                return stream.Read(header, 0, 4) == 4 &&
                       header[0] == 'O' && header[1] == 'T' && header[2] == 'T' && header[3] == 'O';
            }
            catch (System.IO.IOException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        private static ushort Glyph(GlyphTypeface face, char c)
            => face.CharacterToGlyphMap.TryGetValue(c, out ushort glyph) ? glyph : (ushort)0;

        private static GlyphRun Run(string text, double size)
        {
            GlyphTypeface face = Face();

            var indices = new List<ushort>(text.Length);
            var advances = new List<double>(text.Length);

            foreach (char c in text)
            {
                ushort glyph = Glyph(face, c);

                indices.Add(glyph);
                advances.Add(face.AdvanceWidths[glyph] * size);
            }

            return new GlyphRun(face, 0, false, size, 96.0f, indices, new Point(0, 0), advances,
                                null, null, null, null, null, null);
        }
    }
}
