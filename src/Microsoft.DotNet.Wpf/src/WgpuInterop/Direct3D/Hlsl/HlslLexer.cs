// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler (D3DCompile): tokens. HLSL's lexical grammar is C's -- identifiers,
// numbers with their suffixes (1.0f, 2h, 3u, 0x1F, 1e-3), strings, punctuators -- with each token
// carrying where it came from, so a diagnostic names the file, line and column as fxc's do.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl
{
    internal enum TokenKind { Identifier, Int, Float, String, Punct, Directive, NewLine, End }

    internal readonly struct SourceLocation
    {
        public readonly string File;
        public readonly int Line, Column;
        public SourceLocation(string file, int line, int column) { File = file; Line = line; Column = column; }
        public override string ToString() => $"{File}({Line},{Column})";
    }

    internal sealed class Token
    {
        public TokenKind Kind;
        public string Text = "";
        public SourceLocation At;
        /// <summary>Whitespace came before it (a function-like macro needs "(" right after its name).</summary>
        public bool SpaceBefore;
        /// <summary>A number's value: an integer's bits, a float's value (and whether it was half, double...).</summary>
        public ulong IntValue;
        public double FloatValue;
        public char Suffix;
        /// <summary>Macros already expanded into this token (no recursion: C's rule).</summary>
        public HashSet<string>? Hidden;

        public Token Clone() => (Token)MemberwiseClone();
        public bool Is(string punct) => Kind == TokenKind.Punct && Text == punct;
        public bool IsIdent(string id) => Kind == TokenKind.Identifier && Text == id;
        public override string ToString() => Text;
    }

    /// <summary>An error in the source, reported as fxc reports one.</summary>
    internal sealed class HlslException : Exception
    {
        public readonly SourceLocation At;
        public readonly int Code;
        public HlslException(SourceLocation at, string message, int code = 3000) : base(message) { At = at; Code = code; }
        public string Format() => $"{At}: error X{Code}: {Message}";
    }

    internal static class HlslLexer
    {
        private static readonly string[] Puncts =
        {
            "<<=", ">>=", "...", "##",
            "++", "--", "<<", ">>", "<=", ">=", "==", "!=", "&&", "||", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "::", "->",
            "+", "-", "*", "/", "%", "<", ">", "=", "!", "~", "&", "|", "^", "?", ":", ";", ",", ".", "(", ")", "[", "]", "{", "}", "#",
        };

        /// <summary>The tokens of one file, with line breaks as tokens (the preprocessor's lines).</summary>
        public static List<Token> Lex(string text, string file)
        {
            var tokens = new List<Token>();
            int i = 0, line = 1, col = 1;
            bool space = false, lineStart = true;
            void Advance(int n)
            {
                for (int k = 0; k < n && i < text.Length; k++, i++)
                {
                    if (text[i] == '\n') { line++; col = 1; }
                    else col++;
                }
            }
            while (i < text.Length)
            {
                char c = text[i];
                // a backslash at the end of a line continues it
                if (c == '\\' && i + 1 < text.Length && (text[i + 1] == '\n' || (text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n')))
                {
                    Advance(text[i + 1] == '\r' ? 3 : 2);
                    space = true;
                    continue;
                }
                if (c == '\n')
                {
                    tokens.Add(new Token { Kind = TokenKind.NewLine, Text = "\n", At = new SourceLocation(file, line, col) });
                    Advance(1);
                    space = false; lineStart = true;
                    continue;
                }
                if (char.IsWhiteSpace(c)) { Advance(1); space = true; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') Advance(1);
                    space = true;
                    continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    var at = new SourceLocation(file, line, col);
                    Advance(2);
                    while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/')) Advance(1);
                    if (i >= text.Length) throw new HlslException(at, "unterminated comment");
                    Advance(2);
                    space = true;
                    continue;
                }
                var loc = new SourceLocation(file, line, col);
                var t = new Token { At = loc, SpaceBefore = space };
                space = false;
                if (char.IsLetter(c) || c == '_')
                {
                    int s = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) Advance(1);
                    t.Kind = TokenKind.Identifier;
                    t.Text = text.Substring(s, i - s);
                }
                else if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
                {
                    int s = i;
                    ReadNumber(text, ref i, t, loc);
                    col += i - s;
                }
                else if (c == '"')
                {
                    var sb = new StringBuilder();
                    Advance(1);
                    while (i < text.Length && text[i] != '"' && text[i] != '\n')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[i + 1]); Advance(2); continue; }
                        sb.Append(text[i]); Advance(1);
                    }
                    if (i >= text.Length || text[i] != '"') throw new HlslException(loc, "unterminated string");
                    Advance(1);
                    t.Kind = TokenKind.String;
                    t.Text = sb.ToString();
                }
                else if (c == '#' && lineStart)
                {
                    t.Kind = TokenKind.Directive;
                    t.Text = "#";
                    Advance(1);
                }
                else
                {
                    string? p = null;
                    foreach (string q in Puncts)
                        if (string.CompareOrdinal(text, i, q, 0, q.Length) == 0) { p = q; break; }
                    if (p == null) throw new HlslException(loc, $"unexpected character '{c}'");
                    t.Kind = TokenKind.Punct;
                    t.Text = p;
                    Advance(p.Length);
                }
                lineStart = false;
                tokens.Add(t);
            }
            tokens.Add(new Token { Kind = TokenKind.NewLine, Text = "\n", At = new SourceLocation(file, line, col) });
            return tokens;
        }

        private static void ReadNumber(string text, ref int i, Token t, SourceLocation loc)
        {
            int s = i;
            bool hex = text[i] == '0' && i + 1 < text.Length && (text[i + 1] == 'x' || text[i + 1] == 'X');
            bool isFloat = false;
            if (hex)
            {
                i += 2;
                while (i < text.Length && Uri.IsHexDigit(text[i])) i++;
            }
            else
            {
                while (i < text.Length && char.IsDigit(text[i])) i++;
                if (i < text.Length && text[i] == '.') { isFloat = true; i++; while (i < text.Length && char.IsDigit(text[i])) i++; }
                if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
                {
                    int e = i + 1;
                    if (e < text.Length && (text[e] == '+' || text[e] == '-')) e++;
                    if (e < text.Length && char.IsDigit(text[e])) { isFloat = true; i = e; while (i < text.Length && char.IsDigit(text[i])) i++; }
                }
            }
            string digits = text.Substring(s, i - s);
            // suffixes: f h l (float, half, double), u l (unsigned, long)
            char suffix = '\0';
            while (i < text.Length && "fFhHlLuU".Contains(text[i]))
            {
                char x = char.ToLowerInvariant(text[i]);
                if (x is 'f' or 'h' && !hex) isFloat = true;
                if (x == 'l' && isFloat) x = 'd';
                suffix = x;
                i++;
            }
            if (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) throw new HlslException(loc, $"invalid number '{text.Substring(s, i - s + 1)}'");
            t.Text = text.Substring(s, i - s);
            t.Suffix = suffix;
            if (isFloat)
            {
                t.Kind = TokenKind.Float;
                t.FloatValue = double.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            else
            {
                t.Kind = TokenKind.Int;
                if (hex) t.IntValue = ulong.Parse(digits.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                else if (digits.Length > 1 && digits[0] == '0') t.IntValue = Convert.ToUInt64(digits, 8);
                else t.IntValue = ulong.Parse(digits, CultureInfo.InvariantCulture);
            }
        }
    }
}
