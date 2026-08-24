//#include _VSOut.wgsl

//#include _VertexCommon.wgsl

@group(0) @binding(0) var layerTex : texture_2d<f32>;
@group(0) @binding(1) var maskTex : texture_2d<f32>;
@group(0) @binding(2) var clipSamp : sampler;

@fragment
fn fs_clip(in : VSOut) -> @location(0) vec4<f32> {
    let c = textureSample(layerTex, clipSamp, in.uv);   // premultiplied layer
    let m = textureSample(maskTex, clipSamp, in.uv).r;  // clip coverage
    return c * (m * in.color.a);                         // mask * group opacity
}
