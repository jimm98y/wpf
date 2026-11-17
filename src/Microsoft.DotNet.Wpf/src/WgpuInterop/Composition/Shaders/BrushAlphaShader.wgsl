//#include _VSOut.wgsl

//#include _VertexCommon.wgsl

//#include _BrushParams.wgsl

// The ramp's position for a radial gradient with a focus (GradientOrigin) o, in the ellipse's unit space: the t at
// which p lies on the ellipse scaled by t about o -- the root of (|o|^2 - 1) t^2 + 2 (o.d) t + |d|^2 = 0, d = p - o.
// With the focus at the centre, |p|.
fn focalT(p : vec2<f32>, o : vec2<f32>) -> f32 {
    let oo = dot(o, o);
    if (oo < 1e-12) { return length(p); }
    let d = p - o;
    let a = oo - 1.0;
    let b = dot(o, d);
    let c = dot(d, d);
    if (abs(a) < 1e-6) { return select(0.0, -c / (2.0 * b), abs(b) > 1e-12); }
    return (-b - sqrt(max(b * b - a * c, 0.0))) / a;
}

fn brushT(params : BrushParams, local : vec2<f32>) -> f32 {
    var t = 0.0;
    if (params.kindSpread.x == 1u) {
        t = dot(local - params.g0.xy, params.g0.zw) * params.misc.y;
    } else {
        var off = local - params.g0.xy;
        // a turned ellipse (a rotated or skewed brush): the offset in its own frame, (cos, sin) in misc.zw
        let cs = params.misc.zw;
        if (cs.x != 0.0 || cs.y != 0.0) { off = vec2<f32>(cs.x * off.x + cs.y * off.y, -cs.y * off.x + cs.x * off.y); }
        let d = off / params.g0.zw;
        t = focalT(d, vec2<f32>(bitcast<f32>(params.kindSpread.z), bitcast<f32>(params.kindSpread.w)));
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
