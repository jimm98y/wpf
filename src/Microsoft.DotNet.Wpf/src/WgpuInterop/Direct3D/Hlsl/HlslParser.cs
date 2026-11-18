// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler: the parser, over the preprocessor's tokens, into HlslAst's tree.
//
// HLSL is C with its own type names, so a parser has to know which names are types: the built-in
// ones (float4, matrix<float,3,3>, Texture2D<uint>...) and the structs and typedefs declared so far.
// That tells a declaration from an expression and a cast from a parenthesised value. Effect-file
// syntax fxc accepts and a shader target ignores -- technique and pass blocks, sampler_state
// initialisers, annotations -- is skipped as fxc skips it.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl
{
    internal sealed class HlslParser
    {
        private readonly List<Token> _t;
        private int _p;
        private readonly HashSet<string> _types = new();
        /// <summary>#pragma pack_matrix's current packing (null: column-major, the default).</summary>
        private string? _packMatrix;

        private static readonly HashSet<string> Scalars = new()
        {
            "bool", "int", "uint", "dword", "half", "float", "double", "min16float", "min10float", "min16int", "min12int", "min16uint",
            "int16_t", "uint16_t", "int32_t", "uint32_t", "int64_t", "uint64_t", "float16_t", "float32_t", "float64_t",
        };

        internal static readonly HashSet<string> ObjectTypes = new()
        {
            "Texture1D", "Texture1DArray", "Texture2D", "Texture2DArray", "Texture2DMS", "Texture2DMSArray", "Texture3D", "TextureCube", "TextureCubeArray",
            "RWTexture1D", "RWTexture1DArray", "RWTexture2D", "RWTexture2DArray", "RWTexture3D",
            "Buffer", "RWBuffer", "StructuredBuffer", "RWStructuredBuffer", "AppendStructuredBuffer", "ConsumeStructuredBuffer",
            "ByteAddressBuffer", "RWByteAddressBuffer",
            "SamplerState", "SamplerComparisonState", "sampler", "sampler1D", "sampler2D", "sampler3D", "samplerCUBE", "sampler_state",
            "texture", "texture1D", "texture2D", "texture3D", "textureCUBE",
            "PointStream", "LineStream", "TriangleStream", "InputPatch", "OutputPatch", "string",
        };

        private static readonly HashSet<string> Modifiers = new()
        {
            "static", "uniform", "extern", "const", "volatile", "shared", "groupshared", "precise", "row_major", "column_major", "snorm", "unorm",
            "linear", "centroid", "nointerpolation", "noperspective", "sample", "in", "out", "inout", "inline", "globallycoherent",
            "point", "line", "triangle", "lineadj", "triangleadj", "export",
        };

        public HlslParser(List<Token> tokens) { _t = tokens; }

        private Token Peek(int k = 0) => _t[System.Math.Min(_p + k, _t.Count - 1)];
        private Token Next() => _t[System.Math.Min(_p++, _t.Count - 1)];
        private bool Accept(string punct) { if (Peek().Is(punct)) { _p++; return true; } return false; }
        private Token Expect(string punct)
        {
            if (!Peek().Is(punct)) throw Error(Peek(), $"syntax error: unexpected token '{Peek().Text}', expected '{punct}'");
            return Next();
        }
        private string ExpectIdent()
        {
            if (Peek().Kind != TokenKind.Identifier) throw Error(Peek(), $"syntax error: unexpected token '{Peek().Text}', expected an identifier");
            return Next().Text;
        }
        private static HlslException Error(Token at, string message) => new(at.At, message);

        public static bool IsBuiltinTypeName(string n)
        {
            if (n is "void" or "vector" or "matrix") return true;
            if (Scalars.Contains(n) || ObjectTypes.Contains(n)) return true;
            // floatN, floatNxM
            foreach (string s in Scalars)
            {
                if (!n.StartsWith(s, System.StringComparison.Ordinal) || n.Length == s.Length) continue;
                string rest = n.Substring(s.Length);
                if (rest.Length == 1 && rest[0] is >= '1' and <= '4') return true;
                if (rest.Length == 3 && rest[0] is >= '1' and <= '4' && rest[1] == 'x' && rest[2] is >= '1' and <= '4') return true;
            }
            return false;
        }

        private bool IsTypeName(Token t) => t.Kind == TokenKind.Identifier && (IsBuiltinTypeName(t.Text) || _types.Contains(t.Text) || t.Text == "struct");

        /// <summary>Whether a type (with modifiers) starts here: a declaration, not an expression.</summary>
        private bool StartsDeclaration()
        {
            int k = 0;
            while (Peek(k).Kind == TokenKind.Identifier && Modifiers.Contains(Peek(k).Text) && !IsCallLike(k)) k++;
            return IsTypeName(Peek(k)) && !(Peek(k + 1).Is("(") && k == 0 && !IsTypeName(Peek(k + 2)) && Peek(k).Text != "struct")
                || (k > 0 && Peek(k).Kind == TokenKind.Identifier);
        }

        // "line(...)" in an expression is a call, not the modifier
        private bool IsCallLike(int k) => Peek(k + 1).Is("(") && !Peek(k).Text.StartsWith("triangle", System.StringComparison.Ordinal) && Peek(k).Text is "line" or "point" or "sample";

        // ---- the translation unit ---------------------------------------------------------------------

        public TranslationUnit Parse()
        {
            var unit = new TranslationUnit();
            while (Peek().Kind != TokenKind.End)
            {
                if (Peek().Kind == TokenKind.Punct && Peek().Text.StartsWith("#pack_", System.StringComparison.Ordinal))
                {
                    _packMatrix = Next().Text == "#pack_row_major" ? "row_major" : null;
                    continue;
                }
                if (Accept(";")) continue;
                if (Peek().IsIdent("technique") || Peek().IsIdent("technique10") || Peek().IsIdent("technique11") || Peek().IsIdent("pass"))
                {
                    // an effect's techniques: nothing a shader target compiles
                    while (!Peek().Is("{") && Peek().Kind != TokenKind.End) Next();
                    SkipBraces();
                    continue;
                }
                unit.Decls.AddRange(TopLevel());
            }
            return unit;
        }

        private void SkipBraces()
        {
            Expect("{");
            int depth = 1;
            while (depth > 0 && Peek().Kind != TokenKind.End)
            {
                Token t = Next();
                if (t.Is("{")) depth++;
                else if (t.Is("}")) depth--;
            }
        }

        private IEnumerable<Decl> TopLevel()
        {
            var at = Peek().At;
            if (Peek().IsIdent("cbuffer") || Peek().IsIdent("tbuffer"))
            {
                var cb = new CBufferDecl { At = at, Texture = Next().Text == "tbuffer", Name = ExpectIdent() };
                cb.Registers = Registers(out _, out _);
                Expect("{");
                while (!Accept("}"))
                {
                    if (Accept(";")) continue;
                    if (Peek().IsIdent("struct")) { var s = StructDef(); yield return s; Accept(";"); continue; }
                    foreach (var v in Declarators(TypeOf(), allowInit: true)) cb.Members.Add(v);
                    Expect(";");
                }
                Accept(";");
                yield return cb;
                yield break;
            }
            if (Peek().IsIdent("typedef"))
            {
                Next();
                var type = TypeOf();
                do
                {
                    var td = new TypedefDecl { At = Peek().At, Type = type, Name = ExpectIdent() };
                    td.ArrayDims = ArrayDims();
                    _types.Add(td.Name);
                    yield return td;
                } while (Accept(","));
                Expect(";");
                yield break;
            }
            var attrs = Attributes();
            if ((Peek().IsIdent("struct") || Peek().IsIdent("interface") || Peek().IsIdent("class")) && (Peek(2).Is("{") || Peek(1).Is("{") || Peek(2).Is(":")))
            {
                var s = StructDef();
                yield return s;
                if (Accept(";")) yield break;
                // struct S { ... } s;: a variable of it
                var ts = new TypeSyntax { Name = s.Name, At = s.At };
                foreach (var v in Declarators(ts, allowInit: true)) yield return v;
                Expect(";");
                yield break;
            }
            var t = TypeOf();
            string name = ExpectIdent();
            if (Peek().Is("("))
            {
                yield return Function(attrs, t, name, at);
                yield break;
            }
            foreach (var v in Declarators(t, allowInit: true, first: name)) yield return v;
            Expect(";");
        }

        private StructDecl StructDef()
        {
            var kw = Next();                        // struct, interface, class
            var s = new StructDecl { At = kw.At, IsInterface = kw.Text == "interface", IsClass = kw.Text == "class", Name = Peek().Kind == TokenKind.Identifier ? Next().Text : $"<anonymous{_p}>" };
            _types.Add(s.Name);
            if (Accept(":")) s.Base = ExpectIdent();
            Expect("{");
            while (!Accept("}"))
            {
                if (Accept(";")) continue;
                var type = TypeOf();
                string name = ExpectIdent();
                if (Peek().Is("("))
                {
                    s.Methods.Add(Function(new List<Attribute>(), type, name, type.At));
                    continue;
                }
                foreach (var v in Declarators(type, allowInit: false, first: name)) s.Fields.Add(v);
                Expect(";");
            }
            return s;
        }

        private FunctionDecl Function(List<Attribute> attrs, TypeSyntax ret, string name, SourceLocation at)
        {
            var f = new FunctionDecl { At = at, Attributes = attrs, ReturnType = ret, Name = name };
            Expect("(");
            if (!(Peek().IsIdent("void") && Peek(1).Is(")")))
            {
                while (!Peek().Is(")"))
                {
                    var p = new ParamDecl { At = Peek().At, Type = TypeOf() };
                    p.Name = Peek().Kind == TokenKind.Identifier ? Next().Text : "";
                    p.ArrayDims = ArrayDims();
                    if (Accept(":")) p.Semantic = ExpectIdent();
                    if (Accept("=")) p.Default = Assignment();
                    f.Params.Add(p);
                    if (!Accept(",")) break;
                }
            }
            else Next();
            Expect(")");
            if (Accept(":")) f.ReturnSemantic = ExpectIdent();
            if (Accept(";")) return f;          // a prototype
            f.Body = Block();
            return f;
        }

        private List<Attribute> Attributes()
        {
            var list = new List<Attribute>();
            while (Peek().Is("[") && Peek(1).Kind == TokenKind.Identifier && (Peek(2).Is("]") || Peek(2).Is("(")))
            {
                Next();
                var a = new Attribute { At = Peek().At, Name = ExpectIdent() };
                if (Accept("("))
                {
                    while (!Peek().Is(")")) { a.Args.Add(Assignment()); if (!Accept(",")) break; }
                    Expect(")");
                }
                Expect("]");
                list.Add(a);
            }
            return list;
        }

        // ---- types and declarators ----------------------------------------------------------------------

        private TypeSyntax TypeOf()
        {
            var mods = new HashSet<string>();
            while (Peek().Kind == TokenKind.Identifier && Modifiers.Contains(Peek().Text)) mods.Add(Next().Text);
            var at = Peek().At;
            if (Peek().IsIdent("struct") && Peek(1).Kind == TokenKind.Identifier && !Peek(2).Is("{")) Next();     // struct S s;
            if (Peek().Kind != TokenKind.Identifier) throw Error(Peek(), $"syntax error: unexpected token '{Peek().Text}', expected a type");
            var t = new TypeSyntax { Name = Next().Text, At = at, Modifiers = mods };
            if (!IsBuiltinTypeName(t.Name) && !_types.Contains(t.Name)) throw new HlslException(at, $"unrecognized identifier '{t.Name}'", 3000);
            if (Peek().Is("<") && t.Name != "sampler_state")
            {
                Next();
                while (!Peek().Is(">"))
                {
                    if (Peek().Kind == TokenKind.Int) (t.IntArgs ??= new()).Add((int)Next().IntValue);
                    else (t.TypeArgs ??= new()).Add(TypeOf());
                    if (!Accept(",")) break;
                }
                Expect(">");
            }
            if (_packMatrix != null && !mods.Contains("column_major") && !mods.Contains("row_major")) mods.Add(_packMatrix);
            return t;
        }

        private List<Expr?> ArrayDims()
        {
            var dims = new List<Expr?>();
            while (Accept("["))
            {
                dims.Add(Peek().Is("]") ? null : Expression());
                Expect("]");
            }
            return dims;
        }

        private IEnumerable<VarDecl> Declarators(TypeSyntax type, bool allowInit, string? first = null)
        {
            var list = new List<VarDecl>();
            bool firstOne = true;
            do
            {
                var v = new VarDecl { At = Peek().At, Type = type };
                v.Name = firstOne && first != null ? first : ExpectIdent();
                firstOne = false;
                v.ArrayDims = ArrayDims();
                v.Registers = Registers(out v.Semantic, out v.PackOffset);
                // an effect's annotations: <string x = "...";>
                if (Peek().Is("<") && (Peek(1).Kind == TokenKind.Identifier || Peek(1).Is(">")))
                {
                    while (!Accept(">") && Peek().Kind != TokenKind.End) Next();
                }
                if (allowInit && Accept("="))
                {
                    if (Peek().IsIdent("sampler_state") || (Peek().Kind == TokenKind.Identifier && ObjectTypes.Contains(Peek().Text) && Peek(1).Is("{")))
                    {
                        Next();
                        SkipBraces();
                    }
                    else v.Init = Peek().Is("{") ? InitList() : Assignment();
                }
                else if (Peek().Is("{") && ObjectTypes.Contains(type.Name))
                {
                    SkipBraces();             // SamplerState s { Filter = ...; };: an effect's state
                }
                list.Add(v);
            } while (Accept(","));
            return list;
        }

        /// <summary>: SEMANTIC, : register(...) and : packoffset(...), in any order.</summary>
        private List<Binding> Registers(out string? semantic, out Binding? packoffset)
        {
            semantic = null;
            packoffset = null;
            var list = new List<Binding>();
            while (Accept(":"))
            {
                if (Peek().IsIdent("register"))
                {
                    Next(); Expect("(");
                    string? profile = null;
                    // register(ps_5_0, t0) / register(ps, s1): the binding for that profile only
                    if (Peek().Kind == TokenKind.Identifier && Peek(1).Is(",") && (Peek().Text.Contains('_') || Peek().Text is "vs" or "ps" or "gs" or "hs" or "ds" or "cs"))
                    {
                        profile = Next().Text; Expect(",");
                    }
                    list.Add(RegisterName(ExpectIdent(), profile));
                    if (Accept(","))
                    {
                        string sp = ExpectIdent();
                        if (sp.StartsWith("space", System.StringComparison.Ordinal)) list[^1].Space = int.Parse(sp.AsSpan(5), CultureInfo.InvariantCulture);
                    }
                    Expect(")");
                }
                else if (Peek().IsIdent("packoffset"))
                {
                    Next(); Expect("(");
                    var b = RegisterName(ExpectIdent(), null);
                    if (Accept(".")) b.Component = "xyzw".IndexOf(ExpectIdent()[0]);
                    Expect(")");
                    packoffset = b;
                }
                else semantic = ExpectIdent();
            }
            return list;
        }

        private Binding RegisterName(string r, string? profile)
        {
            if (r.Length < 2 || !int.TryParse(r.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                throw Error(Peek(), $"invalid register specification '{r}'");
            return new Binding { Kind = char.ToLowerInvariant(r[0]), Index = n, Profile = profile };
        }

        // ---- statements ------------------------------------------------------------------------------

        private BlockStmt Block()
        {
            var b = new BlockStmt { At = Expect("{").At };
            while (!Accept("}"))
            {
                if (Peek().Kind == TokenKind.End) throw Error(Peek(), "syntax error: unexpected end of file");
                b.Body.Add(Statement());
            }
            return b;
        }

        private Stmt Statement()
        {
            var attrs = Attributes();
            Stmt s = StatementCore();
            s.Attributes = attrs;
            return s;
        }

        private Stmt StatementCore()
        {
            var at = Peek().At;
            if (Peek().Is("{")) return Block();
            if (Accept(";")) return new ExprStmt { At = at };
            Token t = Peek();
            if (t.Kind == TokenKind.Identifier)
            {
                switch (t.Text)
                {
                    case "if":
                    {
                        Next(); Expect("(");
                        var s = new IfStmt { At = at, Cond = Expression() };
                        Expect(")");
                        s.Then = Statement();
                        if (Peek().IsIdent("else")) { Next(); s.Else = Statement(); }
                        return s;
                    }
                    case "for":
                    {
                        Next(); Expect("(");
                        var s = new ForStmt { At = at };
                        if (!Accept(";"))
                        {
                            s.Init = StartsDeclaration() ? new DeclStmt { At = Peek().At, Vars = Declarators(TypeOf(), true).ToList() } : new ExprStmt { At = Peek().At, Expr = Expression() };
                            Expect(";");
                        }
                        if (!Peek().Is(";")) s.Cond = Expression();
                        Expect(";");
                        if (!Peek().Is(")")) s.Step = Expression();
                        Expect(")");
                        s.Body = Statement();
                        return s;
                    }
                    case "while":
                    {
                        Next(); Expect("(");
                        var s = new WhileStmt { At = at, Cond = Expression() };
                        Expect(")");
                        s.Body = Statement();
                        return s;
                    }
                    case "do":
                    {
                        Next();
                        var s = new WhileStmt { At = at, DoWhile = true, Body = Statement() };
                        if (!Peek().IsIdent("while")) throw Error(Peek(), "syntax error: expected 'while'");
                        Next(); Expect("(");
                        s.Cond = Expression();
                        Expect(")"); Expect(";");
                        return s;
                    }
                    case "switch":
                    {
                        Next(); Expect("(");
                        var s = new SwitchStmt { At = at, Value = Expression() };
                        Expect(")"); Expect("{");
                        while (!Accept("}"))
                        {
                            var labels = new List<Expr?>();
                            while (Peek().IsIdent("case") || Peek().IsIdent("default"))
                            {
                                if (Next().Text == "case") labels.Add(Expression());
                                else labels.Add(null);
                                Expect(":");
                            }
                            if (labels.Count == 0) throw Error(Peek(), "syntax error: expected 'case' or 'default'");
                            var body = new List<Stmt>();
                            while (!Peek().IsIdent("case") && !Peek().IsIdent("default") && !Peek().Is("}")) body.Add(Statement());
                            s.Cases.Add((labels, body));
                        }
                        return s;
                    }
                    case "break":
                    case "continue":
                    case "discard":
                        Next(); Expect(";");
                        return new JumpStmt { At = at, Kind = t.Text };
                    case "return":
                    {
                        Next();
                        var s = new JumpStmt { At = at, Kind = "return" };
                        if (!Peek().Is(";")) s.Value = Expression();
                        Expect(";");
                        return s;
                    }
                    case "struct":
                    {
                        var sd = StructDef();
                        if (Accept(";")) return new StructStmt { At = at, Struct = sd };
                        var ds = new DeclStmt { At = at, Vars = Declarators(new TypeSyntax { Name = sd.Name, At = at }, true).ToList() };
                        Expect(";");
                        return new BlockStmt { At = at, Body = { new StructStmt { At = at, Struct = sd }, ds } };
                    }
                    case "typedef":
                    {
                        Next();
                        var type = TypeOf();
                        do { _types.Add(ExpectIdent()); ArrayDims(); } while (Accept(","));
                        Expect(";");
                        throw Error(t, "a typedef inside a function is not supported");
                    }
                }
                if (StartsDeclaration())
                {
                    var d = new DeclStmt { At = at, Vars = Declarators(TypeOf(), true).ToList() };
                    Expect(";");
                    return d;
                }
            }
            var e = new ExprStmt { At = at, Expr = Expression() };
            Expect(";");
            return e;
        }

        // ---- expressions ------------------------------------------------------------------------------

        private Expr Expression()
        {
            var e = Assignment();
            if (!Peek().Is(",")) return e;
            var c = new CommaExpr { At = e.At, Items = { e } };
            while (Accept(",")) c.Items.Add(Assignment());
            return c;
        }

        private static readonly HashSet<string> AssignOps = new() { "=", "+=", "-=", "*=", "/=", "%=", "<<=", ">>=", "&=", "|=", "^=" };

        private Expr Assignment()
        {
            var left = Conditional();
            if (Peek().Kind == TokenKind.Punct && AssignOps.Contains(Peek().Text))
            {
                string op = Next().Text;
                return new AssignExpr { At = left.At, Op = op, Target = left, Value = Assignment() };
            }
            return left;
        }

        private Expr Conditional()
        {
            var c = BinaryLevel(0);
            if (!Accept("?")) return c;
            var then = Assignment();
            Expect(":");
            return new ConditionalExpr { At = c.At, Cond = c, Then = then, Else = Assignment() };
        }

        private static readonly string[][] Levels =
        {
            new[] { "||" }, new[] { "&&" }, new[] { "|" }, new[] { "^" }, new[] { "&" }, new[] { "==", "!=" },
            new[] { "<", ">", "<=", ">=" }, new[] { "<<", ">>" }, new[] { "+", "-" }, new[] { "*", "/", "%" },
        };

        private Expr BinaryLevel(int level)
        {
            if (level == Levels.Length) return UnaryExpression();
            var left = BinaryLevel(level + 1);
            while (Peek().Kind == TokenKind.Punct && System.Array.IndexOf(Levels[level], Peek().Text) >= 0)
            {
                string op = Next().Text;
                left = new BinaryExpr { At = left.At, Op = op, Left = left, Right = BinaryLevel(level + 1) };
            }
            return left;
        }

        private Expr UnaryExpression()
        {
            var at = Peek().At;
            if (Peek().Kind == TokenKind.Punct && Peek().Text is "-" or "+" or "!" or "~" or "++" or "--")
            {
                string op = Next().Text;
                return new UnaryExpr { At = at, Op = op, Operand = UnaryExpression() };
            }
            // a cast: (type) or (type[N])
            if (Peek().Is("(") && IsTypeName(Peek(1)) && !(Peek(1).Kind == TokenKind.Identifier && Peek(2).Is("(")) || Peek().Is("(") && IsTypeName(Peek(1)) && Peek(2).Is(")"))
            {
                int save = _p;
                Next();
                var mods = new HashSet<string>();
                var type = TypeOf();
                var dims = ArrayDims();
                if (Accept(")"))
                    return new CastExpr { At = at, To = type, ArrayDims = dims, Value = UnaryExpression() };
                _p = save;
            }
            return Postfix(Primary());
        }

        private Expr Postfix(Expr e)
        {
            while (true)
            {
                if (Accept("["))
                {
                    var idx = Expression();
                    Expect("]");
                    e = new IndexExpr { At = e.At, Target = e, Index = idx };
                }
                else if (Accept("."))
                {
                    string name = ExpectIdent();
                    if (Peek().Is("(") || Peek().Is("<") && Peek(2).Is(">") && Peek(3).Is("("))
                    {
                        if (Accept("<")) { Next(); Expect(">"); }          // t.Load<uint>(...): the template argument is the method's
                        var m = new MethodExpr { At = e.At, Target = e, Name = name };
                        Expect("(");
                        while (!Peek().Is(")")) { m.Args.Add(Assignment()); if (!Accept(",")) break; }
                        Expect(")");
                        e = m;
                    }
                    else e = new MemberExpr { At = e.At, Target = e, Name = name };
                }
                else if (Peek().Is("++") || Peek().Is("--"))
                {
                    e = new UnaryExpr { At = e.At, Op = Next().Text, Operand = e, Postfix = true };
                }
                else return e;
            }
        }

        private Expr Primary()
        {
            Token t = Peek();
            var at = t.At;
            switch (t.Kind)
            {
                case TokenKind.Int:
                    Next();
                    return new LiteralExpr { At = at, IntValue = t.IntValue, Suffix = t.Suffix };
                case TokenKind.Float:
                    Next();
                    return new LiteralExpr { At = at, IsFloat = true, FloatValue = t.FloatValue, Suffix = t.Suffix };
                case TokenKind.String:
                    Next();
                    return new LiteralExpr { At = at, Suffix = 's', Text = t.Text.Length >= 2 && t.Text[0] == '"' ? t.Text[1..^1] : t.Text };
            }
            if (Accept("("))
            {
                var e = Expression();
                Expect(")");
                return e;
            }
            if (t.Kind == TokenKind.Identifier)
            {
                if (t.Text is "true" or "false") { Next(); return new LiteralExpr { At = at, IsBool = true, IntValue = t.Text == "true" ? 1u : 0u }; }
                // a constructor: float4(...), vector<float, 3>(...)
                if (IsBuiltinTypeName(t.Text) && t.Text != "void")
                {
                    var type = TypeOf();
                    var c = new CallExpr { At = at, Name = type.Name, Constructor = type };
                    Expect("(");
                    while (!Peek().Is(")")) { c.Args.Add(Assignment()); if (!Accept(",")) break; }
                    Expect(")");
                    return c;
                }
                Next();
                if (Peek().Is("("))
                {
                    var c = new CallExpr { At = at, Name = t.Text };
                    Next();
                    while (!Peek().Is(")")) { c.Args.Add(Assignment()); if (!Accept(",")) break; }
                    Expect(")");
                    return c;
                }
                return new NameExpr { At = at, Name = t.Text };
            }
            throw Error(t, $"syntax error: unexpected token '{t.Text}'");
        }

        private InitListExpr InitList()
        {
            var l = new InitListExpr { At = Expect("{").At };
            while (!Peek().Is("}"))
            {
                l.Items.Add(Peek().Is("{") ? InitList() : Assignment());
                if (!Accept(",")) break;
            }
            Expect("}");
            return l;
        }
    }
}
