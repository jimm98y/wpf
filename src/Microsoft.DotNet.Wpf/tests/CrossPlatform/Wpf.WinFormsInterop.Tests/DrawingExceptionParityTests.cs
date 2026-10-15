// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// System.Drawing's failures, held to .NET 10's System.Drawing.Common on native GDI+: for every case
// in DrawingExceptionCases (several thousand: each public member called validly, with each reference
// argument null, each disposable argument disposed, size-like numbers negative, on a disposed
// receiver, on a Graphics whose HDC is out and on a Bitmap whose bits are locked, plus hand-written
// states), the exception type, message, ParamName and (for ExternalException) HRESULT must be the
// ones stock throws; where stock succeeds we must succeed, with the same value unless the value
// depends on the machine's fonts.
//
// The oracle is Fixtures/Drawing/exceptions.stock.txt, one line per case:
//     name <TAB> v|- <TAB> OK <TAB> value
//     name <TAB> v|- <TAB> exception type <TAB> message <TAB> ParamName [<TAB> 0xHRESULT]
// (v: only success is compared). CI cannot run stock GDI+, so the file is checked in.
//
// Regenerating it (Windows, the .NET 10 Windows Desktop runtime installed):
//   1. Make a console project targeting net10.0-windows with <UseWindowsForms>true</UseWindowsForms>
//      (that is what brings in the real System.Drawing.Common), compiling DrawingExceptionCases.cs
//      and DrawingExceptionCases.Hand.cs from this directory and a Main that, for each case of
//      DrawingExceptionCases.All in order, writes
//          c.Name + "\t" + (c.VolatileResult ? "v" : "-") + "\t" + DrawingExceptionCases.Outcome(c)
//      with "\n" line ends and the invariant culture as both current cultures. The recorder used for
//      the checked-in file lives in the r10exc/probe scratch directory of the change that added this
//      suite (probe/Main.cs, probe/stock/stock.csproj): it runs the cases in a child process held to
//      1.5 GB by a job object with a 60 s per-case watchdog, and records a case that hits either as
//      RESOURCE-LIMIT -- there must be none.
//   2. Run it and copy its output over Fixtures/Drawing/exceptions.stock.txt.
//   3. Build the same Main against the fork's Mono.System.Drawing (net10.0, the framework's
//      System.Drawing.* references stripped as this project strips them) to see where ours differs
//      before running this suite.
// Adding a case: add it to DrawingExceptionCases(.Hand).cs and regenerate; a case the library has and
// the fixture does not is not run (Every_case_is_recorded says so).
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public sealed class DrawingExceptionParityTests
    {
        private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Drawing", "exceptions.stock.txt");

        // No single case may allocate more than this: the suite runs in CI.
        private const long AllocationLimit = 8L << 20;

        // The first Font in a process builds the font catalog (about 12 MB, once). Paid here, so the
        // limit measures each case rather than whichever one happens to create the first Font.
        static DrawingExceptionParityTests()
        {
            using var warm = new System.Drawing.Font(System.Drawing.FontFamily.GenericSansSerif, 8.25f);
        }

        private static Dictionary<string, string[]> s_stock;

        private static Dictionary<string, string[]> Stock
        {
            get
            {
                if (s_stock == null)
                {
                    var d = new Dictionary<string, string[]>(StringComparer.Ordinal);
                    foreach (string line in File.ReadAllLines(FixturePath))
                    {
                        if (line.Length == 0) continue;
                        string[] f = line.Split('\t');
                        d[f[0]] = f;
                    }
                    s_stock = d;
                }
                return s_stock;
            }
        }

        // The recorded cases, in the order they were recorded.
        public static IEnumerable<TheoryDataRow<string>> Cases()
            => File.ReadAllLines(FixturePath).Where(l => l.Length > 0).Select(l => new TheoryDataRow<string>(l.Split('\t')[0]));

        // Where we choose not to match stock (see the reasons). Every entry names the cases it covers.
        private static string Deviation(string name)
        {
            // A NULL reference HDC records against the 96-dpi display GDI+ would ask a real DC for:
            // off Windows there is no HDC to pass, and this is how a metafile is recorded there.
            if (name.StartsWith("Metafile..ctor(IntPtr,", StringComparison.Ordinal)
                || name.StartsWith("Metafile..ctor(Stream,IntPtr", StringComparison.Ordinal)
                || name.StartsWith("Metafile..ctor(String,IntPtr", StringComparison.Ordinal)
                || name is "~Metafile ctor hdc 0" or "~Metafile ctor hdc 0 rect")
                return "a NULL reference HDC records against the default 96-dpi device";
            // Warp flattens with GpPath::Flatten, not GDI+'s own warped-Bezier (GpXPath) flattening:
            // the same failures, but other points for curves and for degenerate destinations.
            if (name is "~Path warp 2 points" or "~Path warp 5 points" or "~Path warp empty rect"
                or "~Path warp 3 points" or "~Path warp 4 points" or "~Path warp 4 points bilinear")
                return "Warp's flattening is not GDI+'s";
            return null;
        }

        // A metafile recorded against a screen-compatible DC takes its resolution from that DC's
        // PHYSICAL size (HORZRES * 25.4 / HORZSIZE, in a DPI-unaware process's units), so its value
        // is the attached display's, not GDI+'s: on the 96-dpi monitor the fixture was recorded on it
        // was 93.78 x 95.25, on a 13" 3420x2146 panel at 200% stock gives 135.73 x 136.27. Its
        // resolution is held to that formula on this machine's display (verified against stock on
        // two displays) and the values derived from it only for success.
        private static readonly string[] s_displayDependent =
        {
            "Metafile.get_HorizontalResolution()|ok", "Metafile.get_VerticalResolution()|ok",
            "Metafile.get_PhysicalDimension()|ok", "Metafile.GetBounds(GraphicsUnit&)|ok",
        };

        private static string DisplayResolution(string name)
        {
            if (!OperatingSystem.IsWindows()) return null;
            bool x = name == "Metafile.get_HorizontalResolution()|ok";
            if (!x && name != "Metafile.get_VerticalResolution()|ok") return null;
            IntPtr old = SetThreadDpiAwarenessContext((IntPtr)(-1));   // as the 96-dpi application sees it
            try
            {
                IntPtr screen = GetDC(IntPtr.Zero);
                IntPtr mem = CreateCompatibleDC(screen);
                try
                {
                    float res = GetDeviceCaps(mem, x ? 8 : 10) * 25.4f / GetDeviceCaps(mem, x ? 4 : 6);
                    return "OK\t" + res.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                finally { DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); }
            }
            finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
        }

        [System.Runtime.InteropServices.DllImport("user32")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
        [System.Runtime.InteropServices.DllImport("user32")] private static extern IntPtr GetDC(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [System.Runtime.InteropServices.DllImport("gdi32")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [System.Runtime.InteropServices.DllImport("gdi32")] private static extern bool DeleteDC(IntPtr dc);
        [System.Runtime.InteropServices.DllImport("gdi32")] private static extern int GetDeviceCaps(IntPtr dc, int i);

        // What only Windows has: GDI handles(HBITMAP, HICON, HFONT, HRGN, HPALETTE, HDC, HENHMETAFILE).
        private static bool WindowsOnly(string name)
        {
            string[] words =
            {
                "Hbitmap", "Hicon", "Hfont", "FromHicon", "FromResource", "Hrgn", "HalftonePalette", "Hdc", "hdc",
                "Henhmetafile", "Handle", "LogFont", "Icon", "Metafile", "IntPtr", "BufferedGraphics", "CopyFromScreen",
            };
            return words.Any(w => name.Contains(w, StringComparison.Ordinal));
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Throws_what_System_Drawing_Common_throws(string name)
        {
            string why = Deviation(name);
            if (why != null) Assert.Skip(why);
            if (!OperatingSystem.IsWindows() && WindowsOnly(name)) Assert.Skip("Windows GDI handles");
            string[] want = Stock[name];
            DrawingExceptionCases.Case c = DrawingExceptionCases.All.SingleOrDefault(x => x.Name == name);
            Assert.True(c != null, name + " is recorded but not in DrawingExceptionCases; regenerate the fixture");

            // Messages format numbers in the current culture; the oracle was recorded in the invariant one.
            System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.CurrentCulture, ui = System.Globalization.CultureInfo.CurrentUICulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
            string[] got;
            long allocated;
            try
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                got = (name + "\t-\t" + DrawingExceptionCases.Outcome(c)).Split('\t');
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = culture;
                System.Globalization.CultureInfo.CurrentUICulture = ui;
            }
            Assert.True(allocated <= AllocationLimit, $"{name} allocated {allocated >> 20} MB");

            string w = string.Join("\t", want.Skip(2)), g = string.Join("\t", got.Skip(2));
            if (want[2] == "OK" && got[2] == "OK" && c.VolatileResult) return;
            if (Array.IndexOf(s_displayDependent, name) >= 0 && want[2] == "OK")
            {
                Assert.Equal("OK", got[2]);
                if (DisplayResolution(name) is string display) Assert.Equal(display, g);
                return;
            }
            Assert.Equal(w, g);
        }

        [Fact]
        public void Every_recorded_case_exists()
        {
            var names = new HashSet<string>(DrawingExceptionCases.All.Select(c => c.Name), StringComparer.Ordinal);
            string[] missing = Stock.Keys.Where(k => !names.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            Assert.True(missing.Length == 0, "recorded but not in the case library: " + string.Join(", ", missing.Take(20)));
            Assert.DoesNotContain(Stock.Values, f => f.Length > 2 && f[2] == "RESOURCE-LIMIT");
        }

        [Fact]
        public void Every_case_is_recorded()
        {
            string[] unrecorded = DrawingExceptionCases.All.Select(c => c.Name).Where(n => !Stock.ContainsKey(n)).ToArray();
            Assert.True(unrecorded.Length == 0, "not in the fixture (regenerate it): " + string.Join(", ", unrecorded.Take(20)));
        }
    }
}
