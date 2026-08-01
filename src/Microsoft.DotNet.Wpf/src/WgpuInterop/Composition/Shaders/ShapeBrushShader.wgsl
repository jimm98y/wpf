//#include _VSOutShape.wgsl

//#include _VertexShape.wgsl

//#include _BrushParams.wgsl

//#include _BrushRampBindings.wgsl

fn brushT(local : vec2<f32>) -> f32 {
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

@fragment
fn fs_shapebrush(in : VSOut) -> @location(0) vec4<f32> {
    let p = in.uv;
    let hx = in.prm.x; let hy = in.prm.y; let cr = in.prm.z; let sh = in.prm.w;
    var d : f32;
    if (cr < 0.0) {
        let qx = p.x / hx;
        let qy = p.y / hy;
        let f = qx * qx + qy * qy - 1.0;
        let g = max(length(vec2<f32>(2.0 * qx / hx, 2.0 * qy / hy)), 1e-8);
        d = f / g;
    } else {
        let q = abs(p) - vec2<f32>(hx, hy) + vec2<f32>(cr);
        d = length(max(q, vec2<f32>(0.0))) + min(max(q.x, q.y), 0.0) - cr;
    }
    let fw = max(fwidth(d), 1e-6);
    var cov : f32;
    if (sh < 0.0) {
        cov = clamp(0.5 - d / fw, 0.0, 1.0);
    } else {
        cov = clamp(0.5 - (abs(d) - sh) / fw, 0.0, 1.0);
    }
    let c = textureSampleLevel(rampTex, rampSamp, vec2<f32>(brushT(p), 0.5), 0.0);
    let a = cov * c.a * params.misc.x;
    return vec4<f32>(c.rgb * a, a);                 // premultiplied
}
