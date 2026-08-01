//#include _VSOut.wgsl

//#include _VertexCommon.wgsl

@fragment
fn fs_solid(in : VSOut) -> @location(0) vec4<f32> {
    return in.color;          // already premultiplied
}

@group(0) @binding(0) var tex : texture_2d<f32>;
@group(0) @binding(1) var samp : sampler;

@fragment
fn fs_textured(in : VSOut) -> @location(0) vec4<f32> {
    let s = textureSample(tex, samp, in.uv);
    let a = s.a * in.color.a;          // image/ramp alpha * accumulated opacity
    return vec4<f32>(s.rgb * a, a);    // premultiply
}

@fragment
fn fs_text(in : VSOut) -> @location(0) vec4<f32> {
    let coverage = textureSample(tex, samp, in.uv).r;   // R8 glyph coverage
    let a = coverage * in.color.a;                      // coverage * brush alpha * opacity
    return vec4<f32>(in.color.rgb * a, a);              // premultiplied brush colour
}

@fragment
fn fs_layer(in : VSOut) -> @location(0) vec4<f32> {
    // The layer texture is already premultiplied; scale it by the group opacity.
    return textureSample(tex, samp, in.uv) * in.color.a;
}

// Separable blur. The blur axis step (uv units), sigma and tap radius are carried in
// the (constant) vertex colour, so no uniform buffer is needed.
//
// A NEGATIVE sigma selects a BOX kernel (uniform weights) instead of a Gaussian one --
// WPF's BlurEffect.KernelType. A Gaussian sigma is always positive, so the sign is free
// to carry this and no extra vertex channel is needed.
@fragment
fn fs_blur(in : VSOut) -> @location(0) vec4<f32> {
    let step = in.color.xy;
    let sigma = in.color.z;
    let radius = i32(in.color.w);
    let gaussian = sigma > 0.0;
    let denom = 2.0 * sigma * sigma;
    var sum = vec4<f32>(0.0);
    var wsum = 0.0;
    // textureSampleLevel (not textureSample): the loop bound is per-fragment data,
    // and browser WGSL (Tint) rejects implicit-derivative sampling in non-uniform
    // control flow. The blur inputs are single-mip, so level 0 is identical.
    for (var i = -radius; i <= radius; i = i + 1) {
        let w = select(1.0, exp(-f32(i * i) / denom), gaussian);
        sum = sum + textureSampleLevel(tex, samp, in.uv + step * f32(i), 0.0) * w;
        wsum = wsum + w;
    }
    return sum / wsum;
}

// Drop-shadow tint: use the (blurred) source alpha as coverage and paint it the
// shadow colour (in vertex colour), premultiplied by colour.a (shadow alpha).
@fragment
fn fs_shadow(in : VSOut) -> @location(0) vec4<f32> {
    let cov = textureSample(tex, samp, in.uv).a;
    let a = cov * in.color.a;
    return vec4<f32>(in.color.rgb * a, a);
}
