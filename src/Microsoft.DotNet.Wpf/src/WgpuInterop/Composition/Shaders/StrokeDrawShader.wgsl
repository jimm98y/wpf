//#include _VSOutShape.wgsl

//#include _VertexShape.wgsl

//#include _EdgeTexture.wgsl

//#include _SegDist.wgsl

@fragment
fn fs_strokedraw(in : VSOut) -> @location(0) vec4<f32> {
    let p = in.uv;                                 // device-pixel position at this fragment
    let segCount = u32(in.prm.x);
    let half = in.prm.y;
    var d = 1e30;
    for (var i = 0u; i < segCount; i = i + 1u) {
        d = min(d, segDist(p, edgeAt(2u * i), edgeAt(2u * i + 1u)));
    }
    let cov = clamp(0.5 + (half - d), 0.0, 1.0);   // 1px analytic AA ramp at the stroke edge
    return in.color * cov;                          // in.color is premultiplied
}
