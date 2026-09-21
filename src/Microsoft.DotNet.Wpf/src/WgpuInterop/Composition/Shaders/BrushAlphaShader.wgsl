//#include _VSOut.wgsl

//#include _VertexCommon.wgsl

//#include _BrushParams.wgsl

fn brushT(params : BrushParams, local : vec2<f32>) -> f32 {
    var t = 0.0;
    if (params.kindSpread.x == 1u) {
        t = dot(local - params.g0.xy, params.g0.zw) * params.misc.y;
    } else {
        let d = (local - params.g0.xy) / params.g0.zw;
        t = length(d);
    }
    switch params.kindSpread.y {
        case 1u: {
            let f = t - 2.0 * floor(t / 2.0);
            t = select(f, 2.0 - f, f > 1.0);
        }
        case 2u: { t = t - floor(t); }
        default: { t = clamp(t, 0.0, 1.0); }
    }
    return t;
}

//#include _BrushRampBindings.wgsl

@fragment
fn fs_brushalpha(in : VSOut) -> @location(0) vec4<f32> {
    let local = params.rect.xy + in.uv * params.rect.zw;
    let a = textureSampleLevel(rampTex, rampSamp, vec2<f32>(brushT(params, local), 0.5), 0.0).a;
    return vec4<f32>(a, 0.0, 0.0, 1.0);
}
