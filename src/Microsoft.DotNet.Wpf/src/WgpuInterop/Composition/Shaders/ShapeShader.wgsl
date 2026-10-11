//#include _VSOutShape.wgsl

//#include _VertexShape.wgsl

@fragment
fn fs_shape(in : VSOut) -> @location(0) vec4<f32> {
    let p = in.uv;
    let hx = in.prm.x; let hy = in.prm.y; let cr = in.prm.z; let sh = in.prm.w;
    var d : f32;   // signed distance to the shape's centre-line, local units (>0 outside)
    if (cr < 0.0) {
        // ellipse with radii (hx, hy); first-order distance from the implicit function and its gradient
        let qx = p.x / hx;
        let qy = p.y / hy;
        let f = qx * qx + qy * qy - 1.0;
        let g = max(length(vec2<f32>(2.0 * qx / hx, 2.0 * qy / hy)), 1e-8);
        d = f / g;
    } else {
        // rounded rectangle (exact SDF); cr == 0 gives a sharp rectangle
        let q = abs(p) - vec2<f32>(hx, hy) + vec2<f32>(cr);
        d = length(max(q, vec2<f32>(0.0))) + min(max(q.x, q.y), 0.0) - cr;
    }
    // The width of the antialiasing band: the PIXEL'S SIZE in the shape's own units, taken from the
    // derivatives of the local position -- not fwidth(d).
    //
    // d is a first-order distance, so for an exact SDF the two agree. For the ellipse they do not:
    // its gradient vanishes at the centre, d swings to a huge magnitude there, and fwidth(d) reports
    // a number to match. Coverage = 0.5 - d/fw then collapses towards a half in the middle of the
    // disc, which showed as two spurious grey pixels in the centre of every filled ellipse -- on the
    // seam between the quad's two triangles, because that is where the derivative quad straddles the
    // singularity. p is a linear function of screen position, so its derivatives are constant across
    // the primitive and have no singularity anywhere.
    let px = length(vec2<f32>(dpdx(p.x), dpdy(p.x)));
    let py = length(vec2<f32>(dpdx(p.y), dpdy(p.y)));
    let fw = max(0.5 * (px + py), 1e-6);
    var cov : f32;
    if (sh < 0.0) {
        cov = clamp(0.5 - d / fw, 0.0, 1.0);                 // filled
    } else {
        cov = clamp(0.5 - (abs(d) - sh) / fw, 0.0, 1.0);     // stroked ring/outline of half-width sh
    }
    return in.color * cov;                                    // in.color is premultiplied
}
