//#include _VSOut.wgsl

//#include _VertexCommon.wgsl

struct BrushParams {
    kindSpread : vec4<u32>,   // x: 1=linear 2=radial; y: 0=pad 1=reflect 2=repeat
    g0 : vec4<f32>,           // linear: start.xy, axis.xy ; radial: center.xy, radius.xy
    rect : vec4<f32>,         // brush-space rect of the quad: origin.xy, size.xy
    misc : vec4<f32>,         // x: opacity, y: linear 1/|axis|^2
};

fn brushT(params : BrushParams, local : vec2<f32>) -> f32 {
    var t = 0.0;
    if (params.kindSpread.x == 1u) {
        t = dot(local - params.g0.xy, params.g0.zw) * params.misc.y;
    } else {
        let d = (local - params.g0.xy) / params.g0.zw;
        t = length(d);
    }
    switch params.kindSpread.y {
        case 1u: {                                  // reflect
            let f = t - 2.0 * floor(t / 2.0);
            t = select(f, 2.0 - f, f > 1.0);
        }
        case 2u: { t = t - floor(t); }              // repeat
        default: { t = clamp(t, 0.0, 1.0); }        // pad
    }
    return t;
}

@group(0) @binding(0) var covTex : texture_2d<f32>;
@group(0) @binding(1) var rampTex : texture_2d<f32>;
@group(0) @binding(2) var covSamp : sampler;
@group(0) @binding(3) var rampSamp : sampler;
@group(0) @binding(4) var<uniform> params : BrushParams;

@fragment
fn fs_maskbrush(in : VSOut) -> @location(0) vec4<f32> {
    let local = params.rect.xy + in.uv * params.rect.zw;
    let t = brushT(params, local);
    let c = textureSampleLevel(rampTex, rampSamp, vec2<f32>(t, 0.5), 0.0);
    let cov = textureSampleLevel(covTex, covSamp, in.uv, 0.0).r;
    let a = cov * c.a * params.misc.x;
    return vec4<f32>(c.rgb * a, a);
}

// Image/tile brush composited with GPU-rasterized coverage. binding(1) is the image
// texture (an sRGB texture on the display path, so the hardware decodes + filters in
// linear space). kindSpread.y = TileMode (0 None -> map once across the coverage rect;
// 1 FlipX, 2 FlipY, 3 FlipXY, 4 Tile); g0.xy = (tileWidth, tileHeight). Mirrors
// EvaluateBrush's ImageBrush UV + SampleBilinear (hardware linear sampler).
@fragment
fn fs_maskimage(in : VSOut) -> @location(0) vec4<f32> {
    let mode = params.kindSpread.y;
    var uv : vec2<f32>;
    if (mode == 0u) {
        uv = in.uv;                                  // map once across geometry bounds
    } else {
        let local = params.rect.xy + in.uv * params.rect.zw;
        let tu = local.x / params.g0.x;
        let tv = local.y / params.g0.y;
        let cx = floor(tu);
        let cy = floor(tv);
        var u = tu - cx;
        var v = tv - cy;
        if ((mode == 1u || mode == 3u) && (i32(cx) & 1) != 0) { u = 1.0 - u; }  // FlipX / FlipXY
        if ((mode == 2u || mode == 3u) && (i32(cy) & 1) != 0) { v = 1.0 - v; }  // FlipY / FlipXY
        uv = vec2<f32>(u, v);
    }
    let c = textureSampleLevel(rampTex, rampSamp, uv, 0.0);   // binding(1) = image
    let cov = textureSampleLevel(covTex, covSamp, in.uv, 0.0).r;
    let a = c.a * cov * params.misc.x;
    return vec4<f32>(c.rgb * a, a);                            // premultiply
}
