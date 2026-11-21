// The product's license agreement (the manifest's product.license: an .rtf, or a text file), as each installer shows
// it: RTF for Windows Installer's license page and macOS Installer's, plain text for Setup.exe's console.

using System.Text;
using WpfWebGpu.Packager.Desktop.Msi;

namespace WpfWebGpu.Packager.Desktop;

public static class License
{
    /// <summary>The license as RTF: an .rtf as it is, a text file made RTF in the font and size (half points) given.</summary>
    public static string Rtf(string path, string font, int halfPoints)
    {
        if (IsRtf(path))
        {
            string rtf = File.ReadAllText(Existing(path), Encoding.Latin1);
            if (!rtf.StartsWith(@"{\rtf", StringComparison.Ordinal)) throw new PackagerError($"{path}: not an RTF document");
            return rtf;
        }
        var sb = new StringBuilder($@"{{\rtf1\ansi\ansicpg1252\deff0\uc1{{\fonttbl{{\f0\fswiss {font};}}}}\viewkind4\pard\f0\fs{halfPoints} ");
        foreach (char c in File.ReadAllText(Existing(path)).Replace("\r\n", "\n").TrimEnd())
            sb.Append(c switch
            {
                '\\' or '{' or '}' => "\\" + c,
                '\n' => "\\par\n",
                '\t' => "\\tab ",
                '\r' => "",
                > (char)127 => $"\\u{(short)c}?",
                _ => c.ToString(),
            });
        return sb.Append('}').ToString();
    }

    /// <summary>The license as plain text: a text file as it is, an .rtf's text.</summary>
    public static string Text(string path) =>
        IsRtf(path) ? RtfText(Rtf(path, "", 0)) : File.ReadAllText(Existing(path)).Replace("\r\n", "\n").TrimEnd();

    private static bool IsRtf(string path) => path.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase);

    private static string Existing(string path) => File.Exists(path) ? path : throw new PackagerError($"no such license file: {path}");

    // the groups that are not text: tables, pictures, document information, headers and footers
    private static readonly HashSet<string> s_destinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "header", "footer", "headerl", "headerr", "headerf", "footerl",
        "footerr", "footerf", "listtable", "listoverridetable", "rsidtbl", "generator", "xmlnstbl", "themedata",
        "colorschememapping", "latentstyles", "datastore", "object", "fldinst", "filetbl", "revtbl",
    };

    /// <summary>An RTF document's text: its paragraphs and tabs, its characters (\'hh in the document's codepage,
    /// \uN), without what is not text.</summary>
    internal static string RtfText(string rtf)
    {
        var sb = new StringBuilder();
        var stack = new Stack<(bool Skip, int Uc)>();
        bool skip = false, groupStart = false;
        int uc = 1, pendingSkip = 0;
        Encoding codepage = MsiDatabase.Encode(1252);
        for (int i = 0; i < rtf.Length;)
        {
            char c = rtf[i];
            if (c == '{') { stack.Push((skip, uc)); groupStart = true; i++; continue; }
            if (c == '}') { (skip, uc) = stack.Count > 0 ? stack.Pop() : (false, 1); groupStart = false; i++; continue; }
            if (c is '\r' or '\n') { i++; continue; }
            bool start = groupStart;
            groupStart = false;
            if (c != '\\')
            {
                if (pendingSkip > 0) pendingSkip--;
                else if (!skip) sb.Append(c);
                i++;
                continue;
            }
            if (i + 1 >= rtf.Length) break;
            char n = rtf[i + 1];
            if (n is '\\' or '{' or '}') { if (pendingSkip > 0) pendingSkip--; else if (!skip) sb.Append(n); i += 2; continue; }
            if (n == '*') { skip = true; i += 2; continue; }
            if (n == '\'')
            {
                if (i + 3 < rtf.Length && byte.TryParse(rtf.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
                {
                    if (pendingSkip > 0) pendingSkip--;
                    else if (!skip) sb.Append(codepage.GetString(new[] { b }));
                }
                i += 4;
                continue;
            }
            if (!char.IsAsciiLetter(n))
            {
                if (!skip && n == '~') sb.Append(' ');
                i += 2;
                continue;
            }
            int w = i + 1;
            while (w < rtf.Length && char.IsAsciiLetter(rtf[w])) w++;
            string word = rtf[(i + 1)..w];
            int p = w;
            if (p < rtf.Length && (rtf[p] == '-' || char.IsAsciiDigit(rtf[p]))) { p++; while (p < rtf.Length && char.IsAsciiDigit(rtf[p])) p++; }
            int? param = p > w && int.TryParse(rtf.AsSpan(w, p - w), out int v) ? v : null;
            if (p < rtf.Length && rtf[p] == ' ') p++;
            i = p;
            if (start && s_destinations.Contains(word)) { skip = true; continue; }
            if (skip) continue;
            switch (word)
            {
                case "par" or "line" or "sect" or "page": sb.Append('\n'); break;
                case "tab": sb.Append('\t'); break;
                case "emdash": sb.Append('\u2014'); break;
                case "endash": sb.Append('\u2013'); break;
                case "bullet": sb.Append('\u2022'); break;
                case "lquote": sb.Append('\u2018'); break;
                case "rquote": sb.Append('\u2019'); break;
                case "ldblquote": sb.Append('\u201C'); break;
                case "rdblquote": sb.Append('\u201D'); break;
                case "uc": uc = param ?? 1; break;
                case "u" when param is { } u:
                    sb.Append((char)(u < 0 ? u + 65536 : u));
                    pendingSkip = uc;
                    break;
            }
        }
        return string.Join('\n', sb.ToString().Split('\n').Select(l => l.TrimEnd())).Trim('\n');
    }
}
