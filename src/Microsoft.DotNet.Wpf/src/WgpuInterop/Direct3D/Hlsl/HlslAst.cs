// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler: the syntax tree the parser builds and the checker types.
//

using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl
{
    /// <summary>A type as written: a name (float4, Texture2D, a struct's), its template arguments
    /// (Texture2D&lt;float4&gt;, matrix&lt;float, 3, 3&gt;, InputPatch&lt;V, 3&gt;) and modifiers.</summary>
    internal sealed class TypeSyntax
    {
        public string Name = "";
        public List<TypeSyntax>? TypeArgs;
        public List<int>? IntArgs;
        public HashSet<string> Modifiers = new();
        public SourceLocation At;
        public override string ToString() => Name + (TypeArgs != null || IntArgs != null ? "<...>" : "");
    }

    internal abstract class Node { public SourceLocation At; }

    // ---- declarations ----------------------------------------------------------------------------

    internal abstract class Decl : Node { }

    /// <summary>register(b0) / register(t3, space1) / register(ps, s2), packoffset(c1.y).</summary>
    internal sealed class Binding
    {
        public char Kind;            // b t s u c (c: a constant register in a cbuffer, or packoffset)
        public int Index, Space;
        public int Component;        // packoffset's .x .. .w
        public string? Profile;      // register(ps_5_0, t0)
    }

    internal sealed class VarDecl : Decl
    {
        public TypeSyntax Type = new();
        public string Name = "";
        public List<Expr?> ArrayDims = new();          // null: [] (unsized)
        public string? Semantic;
        public List<Binding> Registers = new();
        public Binding? PackOffset;
        public Expr? Init;
        public HashSet<string> Modifiers => Type.Modifiers;
    }

    internal sealed class StructDecl : Decl
    {
        public string Name = "";
        public List<VarDecl> Fields = new();
        /// <summary>SM5 class linkage: an interface's or class's methods, and what a class implements.</summary>
        public List<FunctionDecl> Methods = new();
        public string? Base;
        public bool IsInterface, IsClass;
    }

    internal sealed class CBufferDecl : Decl
    {
        public string Name = "";
        public bool Texture;                            // tbuffer
        public List<Binding> Registers = new();
        public List<VarDecl> Members = new();
    }

    internal sealed class TypedefDecl : Decl
    {
        public TypeSyntax Type = new();
        public string Name = "";
        public List<Expr?> ArrayDims = new();
    }

    internal sealed class Attribute : Node
    {
        public string Name = "";
        public List<Expr> Args = new();
    }

    internal sealed class ParamDecl : Node
    {
        public TypeSyntax Type = new();
        public string Name = "";
        public List<Expr?> ArrayDims = new();
        public string? Semantic;
        public Expr? Default;
        public HashSet<string> Modifiers => Type.Modifiers;
        public bool IsOut => Modifiers.Contains("out") || Modifiers.Contains("inout");
        public bool IsIn => !Modifiers.Contains("out") || Modifiers.Contains("inout");
    }

    internal sealed class FunctionDecl : Decl
    {
        public List<Attribute> Attributes = new();
        public TypeSyntax ReturnType = new();
        public string Name = "";
        public List<ParamDecl> Params = new();
        public string? ReturnSemantic;
        public BlockStmt? Body;
    }

    internal sealed class TranslationUnit
    {
        public List<Decl> Decls = new();
    }

    // ---- statements ------------------------------------------------------------------------------

    internal abstract class Stmt : Node { public List<Attribute> Attributes = new(); }

    internal sealed class BlockStmt : Stmt { public List<Stmt> Body = new(); }
    internal sealed class DeclStmt : Stmt { public List<VarDecl> Vars = new(); }
    internal sealed class StructStmt : Stmt { public StructDecl Struct = null!; }
    internal sealed class ExprStmt : Stmt { public Expr? Expr; }
    internal sealed class IfStmt : Stmt { public Expr Cond = null!; public Stmt Then = null!; public Stmt? Else; }
    internal sealed class ForStmt : Stmt { public Stmt? Init; public Expr? Cond; public Expr? Step; public Stmt Body = null!; }
    internal sealed class WhileStmt : Stmt { public Expr Cond = null!; public Stmt Body = null!; public bool DoWhile; }
    internal sealed class SwitchStmt : Stmt { public Expr Value = null!; public List<(List<Expr?> Labels, List<Stmt> Body)> Cases = new(); }
    internal sealed class JumpStmt : Stmt { public string Kind = ""; public Expr? Value; }      // break continue return discard

    // ---- expressions -----------------------------------------------------------------------------

    internal abstract class Expr : Node
    {
        /// <summary>Set by the checker.</summary>
        public HlslType? Type;
    }

    internal sealed class LiteralExpr : Expr
    {
        public bool IsFloat, IsBool;
        public ulong IntValue;
        public double FloatValue;
        public char Suffix;
        /// <summary>A string literal's text (an attribute's argument).</summary>
        public string? Text;
    }

    internal sealed class NameExpr : Expr { public string Name = ""; }
    internal sealed class UnaryExpr : Expr { public string Op = ""; public Expr Operand = null!; public bool Postfix; }
    internal sealed class BinaryExpr : Expr { public string Op = ""; public Expr Left = null!, Right = null!; }
    internal sealed class AssignExpr : Expr { public string Op = ""; public Expr Target = null!, Value = null!; }
    internal sealed class ConditionalExpr : Expr { public Expr Cond = null!, Then = null!, Else = null!; }
    internal sealed class CommaExpr : Expr { public List<Expr> Items = new(); }
    /// <summary>A call: a function, an intrinsic, or a type's constructor (float4(...)).</summary>
    internal sealed class CallExpr : Expr { public string Name = ""; public TypeSyntax? Constructor; public List<Expr> Args = new(); }
    internal sealed class MethodExpr : Expr { public Expr Target = null!; public string Name = ""; public List<Expr> Args = new(); }
    internal sealed class MemberExpr : Expr { public Expr Target = null!; public string Name = ""; }
    internal sealed class IndexExpr : Expr { public Expr Target = null!, Index = null!; }
    internal sealed class CastExpr : Expr { public TypeSyntax To = null!; public List<Expr?> ArrayDims = new(); public Expr Value = null!; }
    internal sealed class InitListExpr : Expr { public List<Expr> Items = new(); }
}
