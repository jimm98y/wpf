//#include _VSOut.wgsl

//#include _VertexCommon.wgsl

@group(0) @binding(0) var covTex : texture_2d<f32>;
@group(0) @binding(1) var covSamp : sampler;

@fragment
fn fs_id(in : VSOut) -> @location(0) vec4<f32> {
    let cov = textureSampleLevel(covTex, covSamp, in.uv, 0.0).r;
    if (cov < 0.5) { discard; }   // outside the shape -> don't claim this pixel
    return in.color;              // packed visual id (bytes / 255), a = 1
}
