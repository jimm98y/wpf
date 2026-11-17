// A colour key (a WinForms TransparencyKey, a layered window's LWA_COLORKEY) as a shader effect over a picture.

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal static class ColorKey
    {
        // The key as a shader effect over the window's picture: a pixel of the key's colour, compared as the bytes it
        // was drawn with (the layer may hold them as sRGB or linear: either matches), becomes transparent.
        internal const string Wgsl = @"
struct VSOut {
    @builtin(position) pos : vec4<f32>,
    @location(0) color : vec4<f32>,
    @location(1) uv : vec2<f32>,
};
@vertex
fn vs_main(@location(0) pos : vec2<f32>, @location(1) color : vec4<f32>, @location(2) uv : vec2<f32>) -> VSOut {
    var o : VSOut;
    o.pos = vec4<f32>(pos, 0.0, 1.0);
    o.color = color;
    o.uv = uv;
    return o;
}
@group(0) @binding(0) var<uniform> effConst : array<vec4<f32>, 1>;
@group(0) @binding(1) var effTex0 : texture_2d<f32>;
@group(0) @binding(2) var effSamp0 : sampler;
fn toSrgb(c : vec3<f32>) -> vec3<f32> {
    let lo = c * 12.92;
    let hi = 1.055 * pow(max(c, vec3<f32>(0.0)), vec3<f32>(1.0 / 2.4)) - 0.055;
    return select(hi, lo, c <= vec3<f32>(0.0031308));
}
@fragment
fn fs_effect(in : VSOut) -> @location(0) vec4<f32> {
    let p = textureSample(effTex0, effSamp0, in.uv);
    if (p.a <= 0.0) { return p; }
    let c = p.rgb / p.a;
    let key = effConst[0].rgb;
    let tol = vec3<f32>(0.75 / 255.0);
    if (all(abs(c - key) <= tol) || all(abs(toSrgb(c) - key) <= tol)) { return vec4<f32>(0.0); }
    return p;
}";


        /// <summary>The effect keying out pixels of exactly (<paramref name="r"/>, <paramref name="g"/>, <paramref name="b"/>).</summary>
        internal static ShaderEffectDef Effect(byte r, byte g, byte b)
            => new ShaderEffectDef(0x7C01_0001, Wgsl, new[] { r / 255f, g / 255f, b / 255f, 1f }, new[] { 0 });
    }
}
