using WpfWebGpu.Packager.Desktop;
using Xunit;

namespace WpfWebGpu.Tests;

public class LicenseTests
{
    // "\u" as RTF writes it (C# reads \uXXXX in any literal as the character)
    private const string U = "\\" + "u";

    [Fact]
    public void A_text_license_becomes_RTF_its_specials_escaped()
    {
        string path = Path.Combine(TestEnvironment.NewDirectory("license"), "license.txt");
        File.WriteAllText(path, "Terms {of use}\r\nno\\warranty, café\t€");
        string rtf = License.Rtf(path, "Helvetica", 26);
        Assert.StartsWith(@"{\rtf1", rtf);
        Assert.Contains(@"\fswiss Helvetica;", rtf);
        Assert.Contains(@"\fs26 Terms \{of use\}\par", rtf);
        Assert.Contains(@"no\\warranty, caf" + U + "233?" + @"\tab " + U + "8364?}", rtf);
        Assert.Equal("Terms {of use}\nno\\warranty, café\t€", License.Text(path));
    }

    [Fact]
    public void An_RTF_license_s_text_is_its_paragraphs_without_tables_and_pictures()
    {
        string path = Path.Combine(TestEnvironment.NewDirectory("license"), "license.rtf");
        File.WriteAllText(path, """
            {\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil\fcharset0 Calibri;}}{\colortbl ;\red0\green0\blue0;}
            {\*\generator Riched20 10.0}\viewkind4\uc1\pard\b END-USER LICENSE\b0\par
            \par
            Caf\'e9 EURO \ldblquote quoted\rdblquote\tab tab\line next\~word \{braces\}\par
            {\pict\pngblip 89504e47}{\*\userprops ignored}Last.\par
            }
            """.Replace("EURO", U + "8364?"));
        Assert.Equal("END-USER LICENSE\n\nCafé € “quoted”\ttab\nnext word {braces}\nLast.", License.Text(path));
        Assert.StartsWith(@"{\rtf1", License.Rtf(path, "x", 1));
    }
}
