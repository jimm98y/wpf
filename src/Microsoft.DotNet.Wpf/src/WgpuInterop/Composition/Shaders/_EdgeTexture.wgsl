// Edge / segment data delivered as a TEXTURE instead of a fragment storage buffer, because
// GL ES 3.0 / ANGLE reports max_storage_buffers_per_shader_stage = 0 (no fragment SSBOs), so a
// `var<storage, read>` binding can't create a pipeline there. Each vec2<f32> slot rides in one
// texel of an RG32Uint texture (an integer format so the bind group's sample type is
// unambiguously 'uint' — no filterable-float validation surprise); the two u32 channels are the
// bit patterns of the two f32s. Slot i maps to texel (i % EDGE_TW, i / EDGE_TW). Each mask/stroke
// binds its OWN edge texture, so indices are relative (slot 0 = that object's header) — exactly
// like the sub-range storage-buffer binding this replaces.
@group(0) @binding(0) var edgeTex : texture_2d<u32>;

// 256 texels wide => bytesPerRow = 256*8 = 2048 (a multiple of 256, required by CopyBufferToTexture)
// and a small floor for tiny masks; a mask needs ceil(slots/256) rows (well under the max height).
const EDGE_TW : u32 = 256u;

fn edgeAt(i : u32) -> vec2<f32> {
    let t = textureLoad(edgeTex, vec2<i32>(i32(i % EDGE_TW), i32(i / EDGE_TW)), 0);
    return vec2<f32>(bitcast<f32>(t.x), bitcast<f32>(t.y));
}
