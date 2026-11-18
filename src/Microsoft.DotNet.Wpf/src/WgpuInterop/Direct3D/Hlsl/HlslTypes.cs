// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler: types. A numeric type is a scalar, vector (1-4) or matrix (1-4 by 1-4)
// of bool, int, uint, half (stored as float, as fxc does without min-precision), float or double;
// then arrays, structs, and the object types (textures, buffers, samplers, streams, patches).
//

using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl
{
    internal enum ScalarKind { Bool, Int, Uint, Float, Double }

    internal abstract class HlslType
    {
        public virtual bool IsNumeric => false;
        /// <summary>The 32-bit components it takes (a double two).</summary>
        public abstract int Components { get; }
        public abstract override string ToString();
    }

    internal sealed class VoidType : HlslType
    {
        public static readonly VoidType Instance = new();
        public override int Components => 0;
        public override string ToString() => "void";
    }

    internal sealed class NumericType : HlslType, IEquatable<NumericType>
    {
        public readonly ScalarKind Scalar;
        public readonly int Rows, Cols;
        /// <summary>A matrix (even 1x1 or 1xN written as one), not a vector.</summary>
        public readonly bool IsMatrix;
        public readonly bool RowMajor;

        public NumericType(ScalarKind s, int rows = 1, int cols = 1, bool matrix = false, bool rowMajor = false)
        {
            Scalar = s; Rows = rows; Cols = cols; IsMatrix = matrix; RowMajor = rowMajor;
        }

        public static NumericType Of(ScalarKind s) => new(s);
        public static NumericType Vector(ScalarKind s, int n) => new(s, 1, n);
        public static readonly NumericType Bool = new(ScalarKind.Bool), Int = new(ScalarKind.Int), Uint = new(ScalarKind.Uint),
            Float = new(ScalarKind.Float), Double = new(ScalarKind.Double);

        public override bool IsNumeric => true;
        public bool IsScalar => !IsMatrix && Cols == 1;
        public bool IsVector => !IsMatrix;
        /// <summary>The components counted as HLSL does: a vector's or scalar's length, a matrix's cells.</summary>
        public int Count => Rows * Cols;
        public override int Components => Count * (Scalar == ScalarKind.Double ? 2 : 1);
        public bool IsFloatLike => Scalar is ScalarKind.Float or ScalarKind.Double;
        public bool IsIntLike => Scalar is ScalarKind.Int or ScalarKind.Uint or ScalarKind.Bool;

        public NumericType WithScalar(ScalarKind s) => new(s, Rows, Cols, IsMatrix, RowMajor);
        public NumericType ScalarOf => new(Scalar);
        public NumericType Row => new(Scalar, 1, Cols);

        public bool Equals(NumericType? o) => o is not null && o.Scalar == Scalar && o.Rows == Rows && o.Cols == Cols && o.IsMatrix == IsMatrix;
        public override bool Equals(object? obj) => obj is NumericType n && Equals(n);
        public override int GetHashCode() => HashCode.Combine(Scalar, Rows, Cols, IsMatrix);

        public static string Name(ScalarKind s) => s switch
        {
            ScalarKind.Bool => "bool", ScalarKind.Int => "int", ScalarKind.Uint => "uint", ScalarKind.Double => "double", _ => "float",
        };

        public override string ToString() => IsMatrix ? $"{Name(Scalar)}{Rows}x{Cols}" : Cols == 1 ? Name(Scalar) : $"{Name(Scalar)}{Cols}";
    }

    internal sealed class ArrayType : HlslType
    {
        public readonly HlslType Element;
        public readonly int Length;          // 0: unsized
        public ArrayType(HlslType e, int n) { Element = e; Length = n; }
        public override int Components => Element.Components * Math.Max(Length, 1);
        public override string ToString() => $"{Element}[{Length}]";
    }

    internal sealed class StructField
    {
        public string Name = "";
        public HlslType Type = null!;
        public string? Semantic;
        public HashSet<string> Modifiers = new();
    }

    internal sealed class StructType : HlslType
    {
        public readonly string Name;
        public readonly List<StructField> Fields = new();
        public StructType(string name) { Name = name; }
        public override int Components => Fields.Sum(f => f.Type.Components);
        public StructField? Field(string n) => Fields.FirstOrDefault(f => f.Name == n);
        public override string ToString() => Name;
    }

    /// <summary>A texture, buffer, sampler, stream or patch: Name is HLSL's (Texture2D, RWBuffer...).</summary>
    internal sealed class ObjectType : HlslType
    {
        public readonly string Name;
        /// <summary>The element: a texture's texel type, a structured buffer's struct, a stream's vertex.</summary>
        public readonly HlslType? Element;
        /// <summary>Texture2DMS's samples, a patch's control points.</summary>
        public readonly int Count;

        public ObjectType(string name, HlslType? element = null, int count = 0) { Name = name; Element = element; Count = count; }

        public override int Components => 0;
        public bool IsSampler => Name is "SamplerState" or "SamplerComparisonState" or "sampler" or "sampler1D" or "sampler2D" or "sampler3D" or "samplerCUBE" or "sampler_state";
        public bool IsComparisonSampler => Name == "SamplerComparisonState";
        public bool IsTexture => Name.StartsWith("Texture", StringComparison.Ordinal) || Name.StartsWith("RWTexture", StringComparison.Ordinal) || Name.StartsWith("texture", StringComparison.Ordinal);
        public bool IsBuffer => Name.EndsWith("Buffer", StringComparison.Ordinal);
        public bool IsUav => Name.StartsWith("RW", StringComparison.Ordinal) || Name is "AppendStructuredBuffer" or "ConsumeStructuredBuffer";
        public bool IsStream => Name is "PointStream" or "LineStream" or "TriangleStream";
        public bool IsPatch => Name is "InputPatch" or "OutputPatch";
        /// <summary>A D3D9 sampler (sampler2D...): a texture and its sampler in one, tex2D's argument.</summary>
        public bool IsLegacySampler => Name.StartsWith("sampler", StringComparison.Ordinal) && Name != "sampler_state";

        public override string ToString() => Element == null ? Name : $"{Name}<{Element}{(Count > 0 ? ", " + Count : "")}>";
    }
}
