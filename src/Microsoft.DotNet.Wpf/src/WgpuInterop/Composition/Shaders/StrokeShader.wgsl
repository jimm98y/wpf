//#include _VSOut.wgsl

//#include _VertexCommon.wgsl

@group(0) @binding(0) var<storage, read> spts : array<vec2<f32>>;   // 2 per segment: a, b

//#include _SegDist.wgsl

@fragment
fn fs_stroke(in : VSOut) -> @location(0) vec4<f32> {
    let p = vec2<f32>(floor(in.uv.x) + 0.5, floor(in.uv.y) + 0.5);
    let segCount = u32(in.color.x);
    let half = in.color.y;
    var d = 1e30;
    for (var i = 0u; i < segCount; i = i + 1u) {
        d = min(d, segDist(p, spts[2u * i], spts[2u * i + 1u]));
    }
    let cov = clamp(0.5 + (half - d), 0.0, 1.0);   // 1px analytic AA ramp at the stroke edge
    return vec4<f32>(cov, 0.0, 0.0, 1.0);
}
