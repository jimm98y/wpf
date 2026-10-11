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
    // Premultiplied to EIGHT BITS, as GDI+ premultiplies an ARGB image before it blends it (and
    // as WPF's PBGRA textures hold it): the blend then adds the destination term to a whole
    // level, so a translucent icon lands where GDI+'s round(c*a) + round(dst*(1-a)) puts it --
    // a pixel of #81 at 0xB3 on #F0 is 163 there, 162 blended in float.
    return vec4<f32>(round(s.rgb * a * 255.0) / 255.0, a);
}

@fragment
fn fs_text(in : VSOut) -> @location(0) vec4<f32> {
    let coverage = textureSample(tex, samp, in.uv).r;   // R8 glyph coverage
    let a = coverage * in.color.a;                      // coverage * brush alpha * opacity
    return vec4<f32>(in.color.rgb * a, a);              // premultiplied brush colour
}

// ---- ClearType -------------------------------------------------------------------------------
//
// Subpixel text needs a DIFFERENT COVERAGE PER CHANNEL, and ordinary blending cannot express that:
// the destination factor is one number, and here red, green and blue each need their own. There is a
// blend factor that would do it in one pass (dual-source, OneMinusSrc1) but it is an optional
// feature these bindings do not declare and the browser cannot provide at all.
//
// So the quad is drawn TWICE over the same vertices:
//
//   dst = dst * (1 - cov)          the multiply pass, blend (Zero, OneMinusSrc)
//   dst = dst + colour * cov       the add pass, blend (One, One)
//
// which together are exactly dst = colour*cov + dst*(1-cov), per channel, with no feature to ask
// for. The mask is RGBA: one coverage per lamp, and their mean in alpha for the destination's own
// alpha to be blended by.

@fragment
fn fs_text_subpixel_multiply(in : VSOut) -> @location(0) vec4<f32> {
    let cov = textureSample(tex, samp, in.uv);
    // The brush's alpha scales how much of the destination is taken out, so that half-transparent
    // text dims what is behind it by half as much.
    return vec4<f32>(cov.rgb * in.color.a, cov.a * in.color.a);
}

@fragment
fn fs_text_subpixel_add(in : VSOut) -> @location(0) vec4<f32> {
    let cov = textureSample(tex, samp, in.uv);
    let a = in.color.a;
    return vec4<f32>(in.color.rgb * cov.rgb * a, cov.a * a);
}

@fragment
fn fs_layer(in : VSOut) -> @location(0) vec4<f32> {
    // The layer texture is already premultiplied; scale it by the group opacity.
    return textureSample(tex, samp, in.uv) * in.color.a;
}

// GDI's constant-alpha AlphaBlend onto a WINDOW surface, measured on a cloaked window over every
// source/destination pair: floor(s a / 255) + floor(d (255 - a) / 255) -- each term truncated on its
// own, where a memory DIB rounds the sum. Two draws make it out of fixed-function blending: the
// first scales the destination by (1 - a) and takes WINDOW_BIAS off (reverse-subtract), which the
// 8-bit store then rounds to exactly floor(d (255 - a) / 255); the second adds the picture's
// s a / 255 less the same, which lands on the now whole destination as floor(s a / 255). Rounding
// x - b is floor(x) for every x in steps of 1/255 only when b lies in (254/255 - 1/2, 1/2) of a level
// -- a quarter level rounded 150 * 224 / 255 = 131.76 up to 132.
const WINDOW_BIAS : f32 = 0.498 / 255.0;

// The source pixel GDI's stretches take for this destination pixel: the centre pick
// c(i) = ((2i + 1) s) / (2d) in INTEGERS. A float sampler at the pixel centre lands a hair under
// the exact ties -- (2 x 99 + 1) x 106 / (2 x 199) is 53 exactly -- and took the row before.
fn gdi_pick(uv : vec2<f32>) -> vec2<i32> {
    let size = vec2<i32>(textureDimensions(tex));
    let d = vec2<i32>(round(1.0 / abs(vec2<f32>(dpdx(uv.x), dpdy(uv.y)))));
    let i = clamp(vec2<i32>(floor(uv * vec2<f32>(d))), vec2<i32>(0), d - vec2<i32>(1));
    return min(((2 * i + vec2<i32>(1)) * size) / (2 * d), size - vec2<i32>(1));
}

@fragment
fn fs_layer_point(in : VSOut) -> @location(0) vec4<f32> {
    let keep = textureSampleLevel(tex, samp, in.uv, 0.0) * 0.0;
    return (textureLoad(tex, gdi_pick(in.uv), 0) + keep) * in.color.a;
}

@fragment
fn fs_window_fade(in : VSOut) -> @location(0) vec4<f32> {
    // Only where the picture has something: a pixel its drawing never reached is transparent, and
    // there the destination must stay as it is (GDI's pictures are opaque everywhere).
    let keep = textureSampleLevel(tex, samp, in.uv, 0.0) * 0.0;
    let t = textureLoad(tex, gdi_pick(in.uv), 0) + keep;
    let bias = select(0.0, WINDOW_BIAS, t.a > 0.0);
    return vec4<f32>(bias, bias, bias, in.color.a * t.a);
}

@fragment
fn fs_window_add(in : VSOut) -> @location(0) vec4<f32> {
    let keep = textureSampleLevel(tex, samp, in.uv, 0.0) * 0.0;
    let t = textureLoad(tex, gdi_pick(in.uv), 0) + keep;
    return vec4<f32>(max(t.rgb * in.color.a - vec3<f32>(WINDOW_BIAS), vec3<f32>(0.0)), 0.0);
}

// GDI's StretchBlt in the BLACKONWHITE mode a fresh DC starts in, measured off gdi32 itself with
// a bitmap whose every pixel names its own row and column: destination pixel i takes the source
// the centre DDA picks, c(i) = ((2i + 1) * s) / (2d), ANDed bit by bit with every source pixel
// the DDA stepped over since the previous one -- (c(i-1), c(i)] -- on both axes. Enlarging steps
// over nothing, so that is plain replication. The destination size comes from the quad's own UV
// slope, the source size from the texture.
fn and_range(i : i32, s : i32, d : i32) -> vec2<i32> {
    let c = ((2 * i + 1) * s) / (2 * d);
    var lo = 0;
    if (i > 0) { lo = min(((2 * i - 1) * s) / (2 * d) + 1, c); }
    return vec2<i32>(lo, c);
}

@fragment
fn fs_layer_and(in : VSOut) -> @location(0) vec4<f32> {
    let size = vec2<i32>(textureDimensions(tex));
    let d = vec2<i32>(round(1.0 / abs(vec2<f32>(dpdx(in.uv.x), dpdy(in.uv.y)))));
    let i = clamp(vec2<i32>(floor(in.uv * vec2<f32>(d))), vec2<i32>(0), d - vec2<i32>(1));
    let rx = and_range(i.x, size.x, d.x);
    let ry = and_range(i.y, size.y, d.y);
    var v = vec4<u32>(255u);
    for (var y = ry.x; y <= ry.y; y = y + 1) {
        for (var x = rx.x; x <= rx.y; x = x + 1) {
            let p = vec4<u32>(round(textureLoad(tex, vec2<i32>(x, y), 0) * 255.0));
            v = v & p;
        }
    }
    // Keeps the sampler in the pipeline's layout, which the bind group is made against.
    let keep = textureSampleLevel(tex, samp, in.uv, 0.0) * 0.0;
    return (vec4<f32>(v) / 255.0 + keep) * in.color.a;
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
