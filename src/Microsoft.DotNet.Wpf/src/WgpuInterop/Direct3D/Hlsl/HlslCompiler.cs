// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler, D3DCompile's engine: source, defines and an include handler in, a
// DXBC container (or fxc-style error text) out. The profile ("vs_5_0", "ps_4_0_level_9_3"...) picks
// the stage and the shader model; the D3DCOMPILE_* flags that change the code are honoured
// (matrix packing, backwards compatibility), the optimisation ones are accepted and ignored.
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl
{
    internal static class HlslCompiler
    {
        public const uint Debug = 1, SkipValidation = 2, SkipOptimization = 4;
        public const uint PackMatrixRowMajor = 8, PackMatrixColumnMajor = 16;
        public const uint EnableStrictness = 1u << 11, EnableBackwardsCompatibility = 1u << 12;

        internal readonly struct Result
        {
            public readonly byte[]? Code;
            public readonly string Messages;
            public Result(byte[]? code, string messages) { Code = code; Messages = messages; }
            public bool Succeeded => Code != null;
        }

        /// <summary>The profile's stage and shader model, or null when it names none we compile.</summary>
        internal static (ShaderStage Stage, int Major, int Minor)? ParseProfile(string profile)
        {
            var p = profile.Split('_');
            if (p.Length < 3 || !int.TryParse(p[1], out int major)) return null;
            ShaderStage? stage = p[0] switch
            {
                "vs" => ShaderStage.Vertex, "ps" => ShaderStage.Pixel, "gs" => ShaderStage.Geometry,
                "hs" => ShaderStage.Hull, "ds" => ShaderStage.Domain, "cs" => ShaderStage.Compute, _ => null,
            };
            if (stage == null) return null;
            // ps_2_a / ps_2_b / vs_2_a: shader model 2.x, version 2.1 in the bytecode
            int minor = p[2] == "x" ? 0 : p[2] is "a" or "b" && major == 2 ? 1 : int.TryParse(p[2], out int m) ? m : -1;
            if (minor < 0) return null;
            return (stage.Value, major, minor);
        }

        public static Result Compile(string source, string fileName, IEnumerable<(string Name, string Value)>? defines,
            Func<string, bool, string, string?>? include, string entry, string profile, uint flags)
        {
            var prof = ParseProfile(profile);
            if (prof == null) return new Result(null, $"error X3501: invalid target '{profile}'\n");
            var (stage, major, minor) = prof.Value;
            if (major < 4 && (stage is not (ShaderStage.Vertex or ShaderStage.Pixel) || major < 2))
                return new Result(null, $"{fileName}: error X3501: target '{profile}': shader model {major} has vertex and pixel shaders from 2_0\n");
            try
            {
                var tokens = new HlslPreprocessor(defines, include).Run(source, fileName);
                var unit = new HlslParser(tokens).Parse();
                var code = new DxbcCodegen(unit, stage, major, minor, flags).Compile(entry);
                return new Result(code, "");
            }
            catch (HlslException e)
            {
                return new Result(null, e.Format() + "\n");
            }
        }
    }
}
