// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler: the preprocessor, C's, as fxc runs it -- object- and function-like macros
// (# and ##, __VA_ARGS__), #if with defined() and integer arithmetic, #include through the
// application's ID3DInclude, #error, and the one #pragma that changes meaning, pack_matrix (handed to
// the parser as a token of its own). Expansion follows C's hide-set rule, so a macro never expands
// inside itself.
//

using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl
{
    internal sealed class HlslPreprocessor
    {
        private sealed class Macro
        {
            public string Name = "";
            public List<string>? Params;        // null: object-like
            public bool Variadic;
            public List<Token> Body = new();
        }

        private readonly Dictionary<string, Macro> _macros = new();
        /// <summary>An #include's source: (name, system include, the including file) to its text, or null.</summary>
        private readonly Func<string, bool, string, string?>? _include;
        private int _depth;

        public HlslPreprocessor(IEnumerable<(string Name, string Value)>? defines, Func<string, bool, string, string?>? include)
        {
            _include = include;
            Define("__HLSL_VERSION", "2018");
            if (defines != null)
                foreach (var (n, v) in defines) Define(n, string.IsNullOrEmpty(v) ? "1" : v);
        }

        public void Define(string name, string value)
        {
            var body = HlslLexer.Lex(value, "<define>").Where(t => t.Kind != TokenKind.NewLine).ToList();
            _macros[name] = new Macro { Name = name, Body = body };
        }

        /// <summary>The source, preprocessed: its tokens without line breaks or directives.</summary>
        public List<Token> Run(string text, string file)
        {
            var output = new List<Token>();
            Process(HlslLexer.Lex(text, file), file, output);
            output.Add(new Token { Kind = TokenKind.End, Text = "<end>", At = output.Count > 0 ? output[^1].At : new SourceLocation(file, 1, 1) });
            return output;
        }

        private void Process(List<Token> tokens, string file, List<Token> output)
        {
            if (++_depth > 64) throw new HlslException(tokens.Count > 0 ? tokens[0].At : default, "#include nested too deeply");
            // conditional state: each level is (this branch active, some branch taken, the enclosing level active)
            var cond = new Stack<(bool Active, bool Taken, bool Outer)>();
            bool active = true;
            var line = new List<Token>();
            int i = 0;
            while (i < tokens.Count)
            {
                // one line at a time
                line.Clear();
                while (i < tokens.Count && tokens[i].Kind != TokenKind.NewLine) line.Add(tokens[i++]);
                i++;
                if (line.Count == 0) continue;
                if (line[0].Kind == TokenKind.Directive)
                {
                    string d = line.Count > 1 ? line[1].Text : "";
                    var rest = line.Skip(2).ToList();
                    var at = line[0].At;
                    switch (d)
                    {
                        case "if":
                            cond.Push((active, false, active));
                            if (active) { bool v = Evaluate(rest, at) != 0; active = v; cond.Pop(); cond.Push((v, v, true)); }
                            continue;
                        case "ifdef":
                        case "ifndef":
                        {
                            cond.Push((active, false, active));
                            if (active)
                            {
                                if (rest.Count == 0) throw new HlslException(at, $"#{d} without a name");
                                bool v = _macros.ContainsKey(rest[0].Text) == (d == "ifdef");
                                active = v; cond.Pop(); cond.Push((v, v, true));
                            }
                            continue;
                        }
                        case "elif":
                        {
                            if (cond.Count == 0) throw new HlslException(at, "#elif without #if");
                            var (_, taken, outer) = cond.Pop();
                            bool v = outer && !taken && Evaluate(rest, at) != 0;
                            active = v;
                            cond.Push((v, taken || v, outer));
                            continue;
                        }
                        case "else":
                        {
                            if (cond.Count == 0) throw new HlslException(at, "#else without #if");
                            var (_, taken, outer) = cond.Pop();
                            active = outer && !taken;
                            cond.Push((active, true, outer));
                            continue;
                        }
                        case "endif":
                            if (cond.Count == 0) throw new HlslException(at, "#endif without #if");
                            active = cond.Pop().Outer;
                            continue;
                    }
                    if (!active) continue;
                    switch (d)
                    {
                        case "define": DefineFrom(rest, at); break;
                        case "undef": if (rest.Count > 0) _macros.Remove(rest[0].Text); break;
                        case "include": Include(rest, at, file, output); break;
                        case "error": throw new HlslException(at, "#error " + string.Join(" ", rest.Select(t => t.Text)), 1504);
                        case "pragma":
                            // pack_matrix changes how matrices are laid out from here on; the rest (warning,
                            // message, def, once) changes nothing the compiler makes
                            if (rest.Count >= 4 && rest[0].Text == "pack_matrix")
                                output.Add(new Token { Kind = TokenKind.Punct, Text = "#pack_" + rest[2].Text, At = at });
                            break;
                        case "line": break;
                        case "": break;
                        default: throw new HlslException(at, $"unknown preprocessor directive #{d}", 1501);
                    }
                    continue;
                }
                if (!active) continue;
                Expand(line, output, 0);
            }
            if (cond.Count > 0) throw new HlslException(tokens.Count > 0 ? tokens[^1].At : default, "#if without #endif", 1004);
            _depth--;
        }

        private void DefineFrom(List<Token> rest, SourceLocation at)
        {
            if (rest.Count == 0 || rest[0].Kind != TokenKind.Identifier) throw new HlslException(at, "#define without a name");
            var m = new Macro { Name = rest[0].Text };
            int k = 1;
            if (rest.Count > 1 && rest[1].Is("(") && !rest[1].SpaceBefore)
            {
                m.Params = new List<string>();
                k = 2;
                while (k < rest.Count && !rest[k].Is(")"))
                {
                    if (rest[k].Is("...")) { m.Variadic = true; m.Params.Add("__VA_ARGS__"); }
                    else if (rest[k].Kind == TokenKind.Identifier) m.Params.Add(rest[k].Text);
                    k++;
                    if (k < rest.Count && rest[k].Is(",")) k++;
                }
                if (k >= rest.Count) throw new HlslException(at, $"macro {m.Name}'s parameters are not closed");
                k++;
            }
            m.Body = rest.Skip(k).ToList();
            _macros[m.Name] = m;
        }

        private void Include(List<Token> rest, SourceLocation at, string file, List<Token> output)
        {
            string name;
            bool system = false;
            if (rest.Count > 0 && rest[0].Kind == TokenKind.String) name = rest[0].Text;
            else if (rest.Count > 0 && rest[0].Is("<"))
            {
                system = true;
                int end = rest.FindIndex(t => t.Is(">"));
                if (end < 0) throw new HlslException(at, "#include <...> not closed");
                name = string.Concat(rest.Skip(1).Take(end - 1).Select(t => t.Text));
            }
            else throw new HlslException(at, "#include expects a file name", 1507);
            string? text = _include?.Invoke(name, system, file);
            if (text == null) throw new HlslException(at, $"failed to open source file: '{name}'", 1507);
            Process(HlslLexer.Lex(text, name), name, output);
        }

        // ---- expansion --------------------------------------------------------------------------------

        private void Expand(List<Token> input, List<Token> output, int depth)
        {
            if (depth > 200) throw new HlslException(input.Count > 0 ? input[0].At : default, "macro expansion too deep");
            for (int i = 0; i < input.Count; i++)
            {
                Token t = input[i];
                if (t.Kind != TokenKind.Identifier || !_macros.TryGetValue(t.Text, out var m) || (t.Hidden?.Contains(t.Text) ?? false))
                {
                    output.Add(t);
                    continue;
                }
                var hide = new HashSet<string>(t.Hidden ?? Enumerable.Empty<string>()) { m.Name };
                List<Token> replaced;
                if (m.Params == null)
                {
                    replaced = Substitute(m, null, t.At);
                }
                else
                {
                    // a function-like macro's name without "(" after it is just a name
                    if (i + 1 >= input.Count || !input[i + 1].Is("(")) { output.Add(t); continue; }
                    var args = new List<List<Token>>();
                    int j = i + 2, nest = 0;
                    var cur = new List<Token>();
                    for (; j < input.Count; j++)
                    {
                        Token a = input[j];
                        if (a.Is("(")) nest++;
                        else if (a.Is(")")) { if (nest == 0) break; nest--; }
                        else if (a.Is(",") && nest == 0 && !(m.Variadic && args.Count >= m.Params.Count - 1)) { args.Add(cur); cur = new List<Token>(); continue; }
                        cur.Add(a);
                    }
                    if (j >= input.Count) throw new HlslException(t.At, $"unterminated call of macro {m.Name}");
                    if (cur.Count > 0 || args.Count > 0 || m.Params.Count > 0) args.Add(cur);
                    if (args.Count != m.Params.Count && !(m.Params.Count == 0 && args.Count == 1 && args[0].Count == 0))
                        throw new HlslException(t.At, $"macro {m.Name} takes {m.Params.Count} arguments, given {args.Count}", 1003);
                    replaced = Substitute(m, args, t.At);
                    i = j;
                }
                foreach (var r in replaced) { r.Hidden = new HashSet<string>(hide.Concat(r.Hidden ?? Enumerable.Empty<string>())); }
                // the replacement, then the rest of the line, rescanned together (a macro can name a function-like one)
                var tail = new List<Token>(replaced);
                tail.AddRange(input.Skip(i + 1));
                Expand(tail, output, depth + 1);
                return;
            }
        }

        private List<Token> Substitute(Macro m, List<List<Token>>? args, SourceLocation at)
        {
            var result = new List<Token>();
            var body = m.Body;
            List<Token> Arg(int k) => args![k];
            int Param(Token t) => m.Params == null || t.Kind != TokenKind.Identifier ? -1 : m.Params.IndexOf(t.Text);
            for (int i = 0; i < body.Count; i++)
            {
                Token t = body[i];
                // # param: the argument as a string
                if (t.Is("#") && m.Params != null && i + 1 < body.Count && Param(body[i + 1]) >= 0)
                {
                    string s = string.Join(" ", Arg(Param(body[i + 1])).Select(x => x.Text));
                    result.Add(new Token { Kind = TokenKind.String, Text = s, At = at });
                    i++;
                    continue;
                }
                // a ## b: pasted
                if (i + 2 < body.Count && body[i + 1].Is("##"))
                {
                    string left = Param(t) >= 0 ? string.Concat(Arg(Param(t)).Select(x => x.Text)) : t.Text;
                    Token rt = body[i + 2];
                    string right = Param(rt) >= 0 ? string.Concat(Arg(Param(rt)).Select(x => x.Text)) : rt.Text;
                    var pasted = HlslLexer.Lex(left + right, at.File).Where(x => x.Kind != TokenKind.NewLine).ToList();
                    foreach (var p in pasted) p.At = at;
                    result.AddRange(pasted);
                    i += 2;
                    continue;
                }
                int pi = Param(t);
                if (pi >= 0)
                {
                    // an argument is expanded before it is substituted
                    var expanded = new List<Token>();
                    Expand(Arg(pi).Select(x => x.Clone()).ToList(), expanded, 1);
                    result.AddRange(expanded.Select(x => x.Clone()));
                    continue;
                }
                var c = t.Clone();
                c.At = at;
                result.Add(c);
            }
            return result;
        }

        // ---- #if expressions -------------------------------------------------------------------------

        private long Evaluate(List<Token> tokens, SourceLocation at)
        {
            // defined(X) and defined X first, then macros, then every name left is 0
            var pre = new List<Token>();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].IsIdent("defined"))
                {
                    bool paren = i + 1 < tokens.Count && tokens[i + 1].Is("(");
                    int n = paren ? i + 2 : i + 1;
                    if (n >= tokens.Count) throw new HlslException(at, "defined without a name");
                    pre.Add(new Token { Kind = TokenKind.Int, IntValue = _macros.ContainsKey(tokens[n].Text) ? 1u : 0u, Text = "?", At = at });
                    i = paren ? n + 1 : n;
                    continue;
                }
                pre.Add(tokens[i]);
            }
            var expanded = new List<Token>();
            Expand(pre, expanded, 0);
            int pos = 0;
            long v = Ternary(expanded, ref pos, at);
            if (pos != expanded.Count) throw new HlslException(at, "invalid #if expression", 1017);
            return v;
        }

        private static long Ternary(List<Token> t, ref int p, SourceLocation at)
        {
            long c = Binary(t, ref p, 0, at);
            if (p < t.Count && t[p].Is("?"))
            {
                p++;
                long a = Ternary(t, ref p, at);
                if (p >= t.Count || !t[p].Is(":")) throw new HlslException(at, "invalid #if expression", 1017);
                p++;
                long b = Ternary(t, ref p, at);
                return c != 0 ? a : b;
            }
            return c;
        }

        private static readonly string[][] Levels =
        {
            new[] { "||" }, new[] { "&&" }, new[] { "|" }, new[] { "^" }, new[] { "&" }, new[] { "==", "!=" },
            new[] { "<", ">", "<=", ">=" }, new[] { "<<", ">>" }, new[] { "+", "-" }, new[] { "*", "/", "%" },
        };

        private static long Binary(List<Token> t, ref int p, int level, SourceLocation at)
        {
            if (level == Levels.Length) return Unary(t, ref p, at);
            long a = Binary(t, ref p, level + 1, at);
            while (p < t.Count && t[p].Kind == TokenKind.Punct && Array.IndexOf(Levels[level], t[p].Text) >= 0)
            {
                string op = t[p++].Text;
                long b = Binary(t, ref p, level + 1, at);
                a = op switch
                {
                    "||" => (a != 0 || b != 0) ? 1 : 0, "&&" => (a != 0 && b != 0) ? 1 : 0,
                    "|" => a | b, "^" => a ^ b, "&" => a & b, "==" => a == b ? 1 : 0, "!=" => a != b ? 1 : 0,
                    "<" => a < b ? 1 : 0, ">" => a > b ? 1 : 0, "<=" => a <= b ? 1 : 0, ">=" => a >= b ? 1 : 0,
                    "<<" => a << (int)b, ">>" => a >> (int)b, "+" => a + b, "-" => a - b, "*" => a * b,
                    "/" => b == 0 ? throw new HlslException(at, "division by zero in #if", 1017) : a / b,
                    _ => b == 0 ? throw new HlslException(at, "division by zero in #if", 1017) : a % b,
                };
            }
            return a;
        }

        private static long Unary(List<Token> t, ref int p, SourceLocation at)
        {
            if (p >= t.Count) throw new HlslException(at, "invalid #if expression", 1017);
            Token x = t[p++];
            if (x.Is("!")) return Unary(t, ref p, at) == 0 ? 1 : 0;
            if (x.Is("-")) return -Unary(t, ref p, at);
            if (x.Is("+")) return Unary(t, ref p, at);
            if (x.Is("~")) return ~Unary(t, ref p, at);
            if (x.Is("("))
            {
                long v = Ternary(t, ref p, at);
                if (p >= t.Count || !t[p].Is(")")) throw new HlslException(at, "invalid #if expression", 1017);
                p++;
                return v;
            }
            if (x.Kind == TokenKind.Int) return (long)x.IntValue;
            if (x.Kind == TokenKind.Float) return (long)x.FloatValue;
            if (x.Kind == TokenKind.Identifier) return 0;          // a name no macro defines
            throw new HlslException(at, "invalid #if expression", 1017);
        }
    }
}
