@vertex
fn vs_main(@location(0) pos : vec2<f32>, @location(1) color : vec4<f32>, @location(2) uv : vec2<f32>, @location(3) prm : vec4<f32>) -> VSOut {
    var o : VSOut;
    o.pos = vec4<f32>(pos, 0.0, 1.0);
    o.color = color;
    o.uv = uv;
    o.prm = prm;
    return o;
}
