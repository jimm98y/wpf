// WQL data queries: SELECT <properties | *> FROM <class> [WHERE <condition>], the condition built
// from = <> != < > <= >=, LIKE, IS [NOT] NULL, ISA, AND, OR, NOT and parentheses. String comparisons
// ignore case, as WMI's do. Event queries and ASSOCIATORS OF / REFERENCES OF are not data queries
// this engine answers; Cimv2 reports them.

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace System.Management
{
    internal static class Wql
    {
        internal sealed class Select
        {
            public string[] Properties;          // null: *
            public string ClassName;
            public string ConditionText = "";
            public Expr Condition;              // null: every instance
        }

        internal abstract class Expr
        {
            public abstract bool Eval(Func<string, object> value);
        }

        public static Select ParseSelect(string query)
        {
            var p = new Parser(query ?? "");
            Select s = p.ParseSelect();
            return s;
        }

        private static ManagementException Invalid() => new ManagementException(ManagementStatus.InvalidQuery);

        private enum Tok { End, Ident, String, Number, Op, LParen, RParen, Comma, Star }

        private sealed class Parser
        {
            private readonly string _text;
            private int _pos;
            private Tok _tok;
            private string _val;
            private int _tokStart;

            public Parser(string text)
            {
                _text = text;
                Next();
            }

            public Select ParseSelect()
            {
                ExpectKeyword("SELECT");
                var s = new Select();
                if (_tok == Tok.Star)
                {
                    Next();
                }
                else
                {
                    var props = new List<string>();
                    do
                    {
                        if (props.Count > 0) Next();
                        if (_tok != Tok.Ident) throw Invalid();
                        props.Add(_val);
                        Next();
                    }
                    while (_tok == Tok.Comma);
                    s.Properties = props.ToArray();
                }
                ExpectKeyword("FROM");
                if (_tok != Tok.Ident) throw Invalid();
                s.ClassName = _val;
                Next();
                if (IsKeyword("WHERE"))
                {
                    int start = _pos;
                    Next();
                    s.ConditionText = _text.Substring(start).Trim();
                    s.Condition = ParseOr();
                }
                if (_tok != Tok.End) throw Invalid();
                return s;
            }

            private Expr ParseOr()
            {
                Expr left = ParseAnd();
                while (IsKeyword("OR"))
                {
                    Next();
                    Expr l = left, r = ParseAnd();
                    left = new Fn(v => l.Eval(v) || r.Eval(v));
                }
                return left;
            }

            private Expr ParseAnd()
            {
                Expr left = ParseNot();
                while (IsKeyword("AND"))
                {
                    Next();
                    Expr l = left, r = ParseNot();
                    left = new Fn(v => l.Eval(v) && r.Eval(v));
                }
                return left;
            }

            private Expr ParseNot()
            {
                if (IsKeyword("NOT"))
                {
                    Next();
                    Expr e = ParseNot();
                    return new Fn(v => !e.Eval(v));
                }
                if (_tok == Tok.LParen)
                {
                    Next();
                    Expr e = ParseOr();
                    if (_tok != Tok.RParen) throw Invalid();
                    Next();
                    return e;
                }
                return ParseComparison();
            }

            private Expr ParseComparison()
            {
                Func<Func<string, object>, object> left = ParseOperand();
                if (IsKeyword("IS"))
                {
                    Next();
                    bool not = false;
                    if (IsKeyword("NOT")) { not = true; Next(); }
                    ExpectKeyword("NULL");
                    return new Fn(v => (left(v) == null) != not);
                }
                if (IsKeyword("ISA"))
                {
                    Next();
                    Func<Func<string, object>, object> cls = ParseOperand();
                    return new Fn(v => string.Equals(Convert.ToString(left(v), CultureInfo.InvariantCulture), Convert.ToString(cls(v), CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase));
                }
                bool notLike = false;
                if (IsKeyword("NOT")) { notLike = true; Next(); if (!IsKeyword("LIKE")) throw Invalid(); }
                if (IsKeyword("LIKE"))
                {
                    Next();
                    if (_tok != Tok.String) throw Invalid();
                    Regex re = LikeToRegex(_val);
                    Next();
                    return new Fn(v => left(v) is object o && re.IsMatch(Convert.ToString(o, CultureInfo.InvariantCulture)) != notLike);
                }
                if (_tok != Tok.Op) throw Invalid();
                string op = _val;
                Next();
                Func<Func<string, object>, object> right = ParseOperand();
                return new Fn(v => Compare(left(v), op, right(v)));
            }

            private Func<Func<string, object>, object> ParseOperand()
            {
                switch (_tok)
                {
                    case Tok.String:
                    {
                        string s = _val;
                        Next();
                        return _ => s;
                    }
                    case Tok.Number:
                    {
                        string n = _val;
                        Next();
                        object num = n.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                            ? long.Parse(n.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                            : n.Contains('.') || n.Contains('e') || n.Contains('E')
                                ? double.Parse(n, CultureInfo.InvariantCulture)
                                : decimal.Parse(n, CultureInfo.InvariantCulture);
                        return _ => num;
                    }
                    case Tok.Ident:
                    {
                        string name = _val;
                        Next();
                        if (name.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) return _ => true;
                        if (name.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return _ => false;
                        if (name.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return _ => null;
                        return v => v(name);
                    }
                    default:
                        throw Invalid();
                }
            }

            private bool IsKeyword(string k) => _tok == Tok.Ident && _val.Equals(k, StringComparison.OrdinalIgnoreCase);

            private void ExpectKeyword(string k)
            {
                if (!IsKeyword(k)) throw Invalid();
                Next();
            }

            private void Next()
            {
                while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
                _tokStart = _pos;
                if (_pos >= _text.Length) { _tok = Tok.End; _val = null; return; }
                char c = _text[_pos];
                if (char.IsLetter(c) || c == '_')
                {
                    int start = _pos;
                    while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_')) _pos++;
                    _tok = Tok.Ident;
                    _val = _text.Substring(start, _pos - start);
                    return;
                }
                if (char.IsDigit(c) || ((c == '-' || c == '+') && _pos + 1 < _text.Length && char.IsDigit(_text[_pos + 1])))
                {
                    int start = _pos++;
                    while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '.')) _pos++;
                    _tok = Tok.Number;
                    _val = _text.Substring(start, _pos - start);
                    return;
                }
                if (c == '\'' || c == '"')
                {
                    var sb = new StringBuilder();
                    _pos++;
                    while (true)
                    {
                        if (_pos >= _text.Length) throw Invalid();
                        char ch = _text[_pos++];
                        if (ch == c) break;
                        if (ch == '\\' && _pos < _text.Length) ch = _text[_pos++];
                        sb.Append(ch);
                    }
                    _tok = Tok.String;
                    _val = sb.ToString();
                    return;
                }
                _pos++;
                switch (c)
                {
                    case '(': _tok = Tok.LParen; _val = "("; return;
                    case ')': _tok = Tok.RParen; _val = ")"; return;
                    case ',': _tok = Tok.Comma; _val = ","; return;
                    case '*': _tok = Tok.Star; _val = "*"; return;
                    case '=': _tok = Tok.Op; _val = "="; return;
                    case '!':
                        if (_pos < _text.Length && _text[_pos] == '=') { _pos++; _tok = Tok.Op; _val = "<>"; return; }
                        throw Invalid();
                    case '<':
                        if (_pos < _text.Length && (_text[_pos] == '=' || _text[_pos] == '>')) { _val = "<" + _text[_pos++]; _tok = Tok.Op; return; }
                        _tok = Tok.Op; _val = "<"; return;
                    case '>':
                        if (_pos < _text.Length && _text[_pos] == '=') { _pos++; _tok = Tok.Op; _val = ">="; return; }
                        _tok = Tok.Op; _val = ">"; return;
                    default:
                        throw Invalid();
                }
            }
        }

        private sealed class Fn : Expr
        {
            private readonly Func<Func<string, object>, bool> _f;

            public Fn(Func<Func<string, object>, bool> f) => _f = f;

            public override bool Eval(Func<string, object> value) => _f(value);
        }

        private static bool Compare(object a, string op, object b)
        {
            if (a == null || b == null)
            {
                // WQL: a comparison with NULL is false, except "<> NULL" between a value and null.
                return op == "<>" && (a == null) != (b == null);
            }
            int c;
            if (IsNumeric(a) && IsNumeric(b))
            {
                c = Convert.ToDecimal(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture));
            }
            else if (a is bool || b is bool)
            {
                if (!TryBool(a, out bool x) || !TryBool(b, out bool y)) return false;
                c = x.CompareTo(y);
            }
            else if (IsNumeric(a) || IsNumeric(b))
            {
                // A number against text: compare as numbers when the text is one.
                if (!decimal.TryParse(Convert.ToString(IsNumeric(a) ? b : a, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal t)) return false;
                decimal n = Convert.ToDecimal(IsNumeric(a) ? a : b, CultureInfo.InvariantCulture);
                c = IsNumeric(a) ? n.CompareTo(t) : t.CompareTo(n);
            }
            else
            {
                c = string.Compare(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
            }
            return op switch
            {
                "=" => c == 0,
                "<>" => c != 0,
                "<" => c < 0,
                ">" => c > 0,
                "<=" => c <= 0,
                ">=" => c >= 0,
                _ => false,
            };
        }

        private static bool IsNumeric(object o) => o is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

        private static bool TryBool(object o, out bool b)
        {
            if (o is bool x) { b = x; return true; }
            if (IsNumeric(o)) { b = Convert.ToDecimal(o, CultureInfo.InvariantCulture) != 0; return true; }
            return bool.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), out b);
        }

        // LIKE: % any run, _ one character, [abc] [a-z] [^abc] a set.
        private static Regex LikeToRegex(string pattern)
        {
            var sb = new StringBuilder("^");
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                switch (c)
                {
                    case '%': sb.Append(".*"); break;
                    case '_': sb.Append('.'); break;
                    case '[':
                        int end = pattern.IndexOf(']', i + 1);
                        if (end < 0) { sb.Append(@"\["); break; }
                        string set = pattern.Substring(i + 1, end - i - 1);
                        sb.Append('[').Append(set.StartsWith("^", StringComparison.Ordinal) ? "^" + Regex.Escape(set.Substring(1)).Replace(@"\-", "-") : Regex.Escape(set).Replace(@"\-", "-")).Append(']');
                        i = end;
                        break;
                    default: sb.Append(Regex.Escape(c.ToString())); break;
                }
            }
            return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        }
    }
}
