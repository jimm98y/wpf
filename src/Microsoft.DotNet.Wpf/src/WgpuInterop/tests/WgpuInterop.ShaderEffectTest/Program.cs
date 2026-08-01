// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// D3D9 pixel-shader bytecode -> WGSL translation (the core of WPF's ShaderEffect).
//
// TESTING CONSTRAINT, stated plainly: there is no fxc and no .ps asset anywhere on a
// macOS box, so this cannot be fed real compiler output. The bytecode below is
// hand-assembled from the documented D3D9 token format by a builder written against
// that spec, NOT against the translator. That checks the translator against the format;
// it does NOT prove compatibility with what fxc actually emits, and the first real .ps
// file is likely to find something here.
//
// Three independent checks per case, because each alone is weak:
//   1. PARSE   -- the sampler set and constant-register count the translator reports.
//   2. COMPILE -- the emitted WGSL goes through wgpu's own frontend (naga). Text that
//                 looks right but does not compile is worthless.
//   3. SEMANTICS -- the emitted expression is compared against the WGSL a reader of the
//                 D3D9 spec would write for that instruction.
//
// Unsupported constructs must be REJECTED with a reason, never partially translated:
// a silently wrong effect is worse than a visibly missing one.
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private static int _failures;
    private static WgpuSceneRenderer _renderer = null!;

    private static int Main()
    {
        using var ctx = WgpuContext.Create();
        _renderer = new WgpuSceneRenderer(ctx);

        // --- A tint effect: texld r0, t0, s0 / mul r0, r0, c0 / mov oC0, r0 ---
        // This is the shape of almost every shipping WPF ShaderEffect.
        var tint = new Ps()
            .TexLd(Dst.Temp(0), Src.Texture(0), Src.Sampler(0))
            .Op(Ps.MUL, Dst.Temp(0), Src.Temp(0), Src.Const(0))
            .Op(Ps.MOV, Dst.ColorOut(), Src.Temp(0))
            .Build();
        Case("tint (texld/mul/mov)", tint, samplers: new[] { 0 }, constCount: 1,
             expect: new[] { "textureSample(effTex0, effSamp0", "(r0 * effConst[0])", "oC0 = r0" });

        // --- Saturate + partial write mask + swizzle ---
        var masked = new Ps()
            .TexLd(Dst.Temp(0), Src.Texture(0), Src.Sampler(0))
            .Op(Ps.ADD, Dst.Temp(1).Mask(0x3).Saturated(), Src.Temp(0).Swizzle("yxzw"), Src.Const(2))
            .Op(Ps.MOV, Dst.ColorOut(), Src.Temp(1))
            .Build();
        Case("saturate + write mask + swizzle", masked, samplers: new[] { 0 }, constCount: 3,
             expect: new[] { "clamp(", "(r0).yxzw", "r1.x = _v.x; r1.y = _v.y;" });

        // --- DEF literal must inline, not allocate a uniform slot ---
        var defd = new Ps()
            .Def(5, 0.25f, 0.5f, 0.75f, 1f)
            .Op(Ps.MOV, Dst.ColorOut(), Src.Const(5))
            .Build();
        Case("DEF literal inlines", defd, samplers: Array.Empty<int>(), constCount: 0,
             expect: new[] { "vec4<f32>(0.25, 0.5, 0.75, 1.0)" });

        // --- Two samplers (a blend effect) ---
        var two = new Ps()
            .TexLd(Dst.Temp(0), Src.Texture(0), Src.Sampler(0))
            .TexLd(Dst.Temp(1), Src.Texture(0), Src.Sampler(1))
            .Op(Ps.LRP, Dst.ColorOut(), Src.Const(0), Src.Temp(0), Src.Temp(1))
            .Build();
        Case("two samplers + lrp", two, samplers: new[] { 0, 1 }, constCount: 1,
             expect: new[] { "effTex1", "mix(r1, r0, effConst[0])" });

        // --- Rejections ---
        Reject("flow control (if)", new Ps().Raw(Ps.IF, 1, 0).Op(Ps.MOV, Dst.ColorOut(), Src.Const(0)).Build(),
               contains: "unsupported opcode");
        Reject("vertex shader bytecode", new Ps().AsVertexShader().Op(Ps.MOV, Dst.ColorOut(), Src.Const(0)).Build(),
               contains: "not a pixel shader");
        Reject("no oC0 write", new Ps().Op(Ps.MOV, Dst.Temp(0), Src.Const(0)).Build(),
               contains: "never writes oC0");
        Reject("ps_1_1", new Ps().Version(1, 1).Op(Ps.MOV, Dst.ColorOut(), Src.Const(0)).Build(),
               contains: "unsupported shader model");

        // --- END TO END: render real content through a translated shader ---
        EndToEnd();

        // --- The real WPF path: MILCMD_PIXELSHADER + MILCMD_SHADEREFFECT ---
        MilcorePath();

        Console.WriteLine();
        if (_failures > 0) { Console.WriteLine($"SHADER EFFECT TEST FAILED: {_failures} problem(s)."); return 1; }
        Console.WriteLine("SHADER EFFECT TEST PASSED: bytecode translates, compiles through naga, and rejects what it cannot do.");
        return 0;
    }

    // Renders a known input through a translated shader and checks the PIXELS. Translation
    // that compiles but computes the wrong thing would pass every check above; only running
    // it catches that. The shader multiplies the input by c0, so the expected output is
    // arithmetic this test can predict exactly.
    private static void EndToEnd()
    {
        const int S = 64;
        byte[] tintCode = new Ps()
            .TexLd(Dst.Temp(0), Src.Texture(0), Src.Sampler(0))
            .Op(Ps.MUL, Dst.ColorOut(), Src.Temp(0), Src.Const(0))
            .Build();
        if (!D3D9ShaderTranslator.TryTranslate(tintCode, out TranslatedShader tr, out string why))
        {
            Fail($"end-to-end: translation failed -- {why}");
            return;
        }

        // c0 = (1, 0, 0, 1): keep red, zero green and blue. Applied to a WHITE square on a
        // white background, the square must come out pure red and the background unchanged.
        var def = new ShaderEffectDef(1, tr.Wgsl, new[] { 1f, 0f, 0f, 1f }, tr.Samplers.ToArray());

        var root = new SceneVisual();
        var child = new SceneVisual { Effect = def };
        child.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(16, 16, 32, 32)),
            RgbaColor.FromBytes(255, 255, 255, 255)));
        root.Children.Add(child);

        byte[] px = _renderer.RenderToRgba(root, S, S, RgbaColor.FromBytes(0, 0, 128, 255));
        int o = (32 * S + 32) * 4;
        int r = px[o], g = px[o + 1], b = px[o + 2];
        Check(r > 200 && g < 40 && b < 40,
            $"end-to-end: white square through mul-by-(1,0,0,1) is red, got ({r},{g},{b})");

        // Outside the effected subtree the background must be untouched.
        int ob = (4 * S + 4) * 4;
        Check(px[ob] < 40 && px[ob + 2] > 90,
            $"end-to-end: background outside the effect is unchanged, got ({px[ob]},{px[ob + 1]},{px[ob + 2]})");
    }

    // Drives the SAME shader through the actual milcore command stream a WPF app produces,
    // rather than constructing ShaderEffectDef directly. This is the part that decides whether
    // a real ShaderEffect renders at all: the payload layout of MILCMD_SHADEREFFECT is easy to
    // get subtly wrong, and a misparse silently yields no effect rather than an error.
    private static void MilcorePath()
    {
        const int S = 80;
        byte[] code = new Ps()
            .TexLd(Dst.Temp(0), Src.Texture(0), Src.Sampler(0))
            .Op(Ps.MUL, Dst.ColorOut(), Src.Temp(0), Src.Const(0))
            .Build();

        var engine = new MilcoreEngine();
        // A white square on visual 2, then the shader resources, then bind the effect.
        engine.CreateOrAddRef(2, MilResourceTypeId.Visual);
        engine.SubmitCommand(SolidColorBrush(3, 1, 1f, 1f, 1f, 1f));
        engine.CreateOrAddRef(5, MilResourceTypeId.RenderData);
        byte[] payload = DrawRectangleRecord(20, 20, 32, 32, 3);
        engine.BeginCommand(RenderDataHeader(5, payload.Length));
        engine.AppendCommandData(payload);
        engine.EndCommand();
        engine.SubmitCommand(VisualSetContent(2, 5));

        engine.SubmitCommand(PixelShaderCmd(11, code));
        engine.SubmitCommand(ShaderEffectCmd(12, 11, register: 0, r: 0f, g: 1f, bl: 0f, a: 1f));
        engine.SubmitCommand(VisualSetEffect(2, 12));
        engine.Realize();

        SceneVisual? v = engine.VisualByHandle(2);
        if (v is null) { Fail("milcore path: no visual"); return; }
        Check(v.Effect is ShaderEffectDef, "milcore path: MILCMD_SHADEREFFECT decodes to a ShaderEffectDef");
        if (v.Effect is not ShaderEffectDef def) return;
        Check(def.FloatConstants.Length >= 4 && def.FloatConstants[0] == 0f && def.FloatConstants[1] == 1f,
              $"milcore path: c0 unpacks to (0,1,0,1), got ({def.FloatConstants[0]},{def.FloatConstants[1]},"
              + $"{def.FloatConstants[2]},{def.FloatConstants[3]})");

        byte[] px = _renderer.RenderToRgba(v, S, S, RgbaColor.FromBytes(0, 0, 128, 255));
        int o = (36 * S + 36) * 4;
        Check(px[o] < 40 && px[o + 1] > 200 && px[o + 2] < 40,
              $"milcore path: white square through mul-by-(0,1,0,1) is green, got ({px[o]},{px[o + 1]},{px[o + 2]})");
    }

    // MILCMD_PIXELSHADER: Handle@4, ShaderRenderMode@8, BytecodeSize@12,
    // CompileSoftwareShader@16, then the bytecode blob.
    private static byte[] PixelShaderCmd(uint handle, byte[] code)
    {
        var b = new Buf();
        b.U32(0x6c); b.U32(handle);
        b.U32(0);                       // ShaderRenderMode
        b.U32((uint)code.Length);
        b.U32(1);                       // CompileSoftwareShader
        b.Raw(code);
        return b.ToArray();
    }

    // MILCMD_SHADEREFFECT: Handle@4, 4 doubles of padding, hPixelShader@40,
    // DdxUvDdyUvRegisterIndex@44, eight payload sizes, then the payloads.
    private static byte[] ShaderEffectCmd(uint handle, uint hShader, ushort register,
        float r, float g, float bl, float a)
    {
        var b = new Buf();
        b.U32(0x70); b.U32(handle);
        b.F64(0); b.F64(0); b.F64(0); b.F64(0);       // padding
        b.U32(hShader);
        b.U32(unchecked((uint)-1));                    // DdxUvDdyUvRegisterIndex
        b.U32(2);                                      // float register indices: 1 x Int16
        b.U32(16);                                     // float values: 1 x float4
        b.U32(0); b.U32(0);                            // int registers / values
        b.U32(0); b.U32(0);                            // bool registers / values
        b.U32(0); b.U32(0);                            // sampler info / values
        b.U16(register);
        b.F32(r); b.F32(g); b.F32(bl); b.F32(a);
        return b.ToArray();
    }

    private static byte[] SolidColorBrush(uint handle, double opacity, float r, float g, float bl, float a)
    {
        var buf = new Buf(); buf.U32(0x7e); buf.U32(handle); buf.F64(opacity);
        buf.F32(r); buf.F32(g); buf.F32(bl); buf.F32(a);
        buf.U32(0); buf.U32(0); buf.U32(0); buf.U32(0);
        return buf.ToArray();
    }

    private static byte[] RenderDataHeader(uint handle, int cbData)
    { var b = new Buf(); b.U32(0x18); b.U32(handle); b.U32((uint)cbData); return b.ToArray(); }

    private static byte[] DrawRectangleRecord(double x, double y, double w, double h, uint hBrush)
    {
        var rec = new Buf(); rec.F64(x); rec.F64(y); rec.F64(w); rec.F64(h); rec.U32(hBrush); rec.U32(0);
        byte[] p = rec.ToArray();
        var b = new Buf(); b.U32((uint)(p.Length + 8)); b.U32(0x40); b.Raw(p);
        return b.ToArray();
    }

    private static byte[] VisualSetContent(uint handle, uint hContent)
    { var b = new Buf(); b.U32(0x22); b.U32(handle); b.U32(hContent); return b.ToArray(); }

    private static byte[] VisualSetEffect(uint handle, uint hEffect)
    { var b = new Buf(); b.U32(0x1d); b.U32(handle); b.U32(hEffect); return b.ToArray(); }

    private static void Case(string name, byte[] bytecode, int[] samplers, int constCount, string[] expect)
    {
        if (!D3D9ShaderTranslator.TryTranslate(bytecode, out TranslatedShader r, out string reason))
        {
            Fail($"{name}: translation failed -- {reason}");
            return;
        }

        // 1. parse
        bool okSamplers = string.Join(",", r.Samplers) == string.Join(",", samplers);
        Check(okSamplers, $"{name}: samplers [{string.Join(",", r.Samplers)}] == [{string.Join(",", samplers)}]");
        Check(r.FloatRegisterCount == constCount, $"{name}: float registers {r.FloatRegisterCount} == {constCount}");

        // 2. compile through wgpu's own WGSL frontend
        bool compiles = _renderer.CompileShaderForTest(r.Wgsl) != IntPtr.Zero;
        Check(compiles, $"{name}: emitted WGSL compiles");

        // 3. semantics
        foreach (string e in expect)
            Check(r.Wgsl.Contains(e, StringComparison.Ordinal), $"{name}: emits \"{e}\"");
    }

    private static void Reject(string name, byte[] bytecode, string contains)
    {
        bool ok = !D3D9ShaderTranslator.TryTranslate(bytecode, out _, out string reason)
                  && reason.Contains(contains, StringComparison.OrdinalIgnoreCase);
        Check(ok, $"reject {name}: \"{reason}\"");
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "FAIL")}] {what}");
        if (!ok) _failures++;
    }

    private static void Fail(string what) { Console.WriteLine($"  [FAIL] {what}"); _failures++; }

    private sealed class Buf
    {
        private readonly List<byte> _b = new();
        public void U32(uint v) => _b.AddRange(BitConverter.GetBytes(v));
        public void U16(ushort v) => _b.AddRange(BitConverter.GetBytes(v));
        public void F32(float v) => _b.AddRange(BitConverter.GetBytes(v));
        public void F64(double v) => _b.AddRange(BitConverter.GetBytes(v));
        public void Raw(byte[] v) => _b.AddRange(v);
        public byte[] ToArray() => _b.ToArray();
    }

    // ---- D3D9 bytecode assembler, written from the documented token format ----

    private sealed class Dst
    {
        public uint Token;
        private Dst(int type, int reg)
        {
            // bit31 set; type split across [30:28] and [12:11]; default write mask .xyzw
            Token = 0x80000000u | (uint)(reg & 0x7FF)
                  | ((uint)(type & 0x7) << 28) | ((uint)((type >> 3) & 0x3) << 11)
                  | (0xFu << 16);
        }
        public static Dst Temp(int r) => new(0, r);
        public static Dst ColorOut() => new(8, 0);
        public Dst Mask(int m) { Token = (Token & ~(0xFu << 16)) | ((uint)m << 16); return this; }
        public Dst Saturated() { Token |= 1u << 20; return this; }
    }

    private sealed class Src
    {
        public uint Token;
        private Src(int type, int reg)
        {
            Token = 0x80000000u | (uint)(reg & 0x7FF)
                  | ((uint)(type & 0x7) << 28) | ((uint)((type >> 3) & 0x3) << 11)
                  | (0xE4u << 16);                       // .xyzw
        }
        public static Src Temp(int r) => new(0, r);
        public static Src Const(int r) => new(2, r);
        public static Src Texture(int r) => new(3, r);
        public static Src Sampler(int r) => new(10, r);
        public Src Swizzle(string s)
        {
            uint sw = 0;
            for (int i = 0; i < 4; i++) sw |= (uint)("xyzw".IndexOf(s[i]) & 3) << (i * 2);
            Token = (Token & ~(0xFFu << 16)) | (sw << 16);
            return this;
        }
    }

    private sealed class Ps
    {
        // D3DSIO_DEF is 81 (0x51). 40 is D3DSIO_IF -- they are NOT the same opcode,
        // which is a mistake easy to make in both the assembler and the translator at once.
        public const int MOV = 1, ADD = 2, MUL = 5, LRP = 18, DEF = 81, TEX = 66, IF = 40;
        private readonly List<uint> _t = new();
        private bool _versioned;

        private void EnsureVersion() { if (!_versioned) { _t.Add(0xFFFF0200u); _versioned = true; } }
        public Ps Version(int major, int minor) { _t.Add((uint)(0xFFFF0000u | (major << 8) | minor)); _versioned = true; return this; }
        public Ps AsVertexShader() { _t.Add(0xFFFE0200u); _versioned = true; return this; }

        public Ps Op(int opcode, Dst d, params Src[] s)
        {
            EnsureVersion();
            _t.Add((uint)(opcode | ((1 + s.Length) << 24)));
            _t.Add(d.Token);
            foreach (Src x in s) _t.Add(x.Token);
            return this;
        }

        public Ps TexLd(Dst d, Src coord, Src samp) => Op(TEX, d, coord, samp);

        public Ps Def(int reg, float a, float b, float c, float e)
        {
            EnsureVersion();
            _t.Add((uint)(DEF | (5 << 24)));
            _t.Add(Dst.Temp(reg).Token & ~(0x70000000u | 0x1800u) | (2u << 28));   // c# register
            _t.Add(BitConverter.SingleToUInt32Bits(a));
            _t.Add(BitConverter.SingleToUInt32Bits(b));
            _t.Add(BitConverter.SingleToUInt32Bits(c));
            _t.Add(BitConverter.SingleToUInt32Bits(e));
            return this;
        }

        public Ps Raw(int opcode, int paramCount, uint fill)
        {
            EnsureVersion();
            _t.Add((uint)(opcode | (paramCount << 24)));
            for (int i = 0; i < paramCount; i++) _t.Add(fill);
            return this;
        }

        public byte[] Build()
        {
            EnsureVersion();
            _t.Add(0x0000FFFFu);                       // END
            var b = new byte[_t.Count * 4];
            for (int i = 0; i < _t.Count; i++) BitConverter.GetBytes(_t[i]).CopyTo(b, i * 4);
            return b;
        }
    }
}
