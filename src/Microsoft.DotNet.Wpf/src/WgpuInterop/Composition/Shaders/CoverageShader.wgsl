//#include _VSOut.wgsl

//#include _VertexCommon.wgsl

// Path outline as CUBIC Bézier segments, 4 vec2 each (p0, c1, c2, p1), grouped into
// per-scanline BANDS. Lines and quadratics are degree-elevated to cubics on the CPU,
// exactly, so one code path here covers every segment kind with no type tag and no
// accuracy loss.
//
// Buffer layout (see WgpuSceneRenderer.BuildScanlineBands):
//   slot 0             header: (bandCount as u32 bits, rows per band as f32, integral)
//   slots 1..bandCount per band: (first segment slot, segment count) as u32 bits
//   rest               each band's segments, contiguous, 4 slots each
//
// The band lookup is an exact optimisation, not an approximation. The crossing test below
// rejects any segment whose endpoints do not straddle the scanline, and a segment can only
// straddle it if its y-range contains it -- so restricting the loop to the band's list
// examines strictly fewer segments and reaches the identical result.
@group(0) @binding(0) var<storage, read> segs : array<vec2<f32>>;

const MAX_PIXEL_CROSSINGS : u32 = 16u;

// Solves y(t) = yTarget on a y-MONOTONE cubic whose endpoints straddle the yTarget, so the
// root is unique and bracketed in [0,1].
//
// Safeguarded Newton rather than a closed-form cubic solve, deliberately. Most segments
// reaching here are degenerate cubics -- an elevated line has zero cubic AND quadratic
// term, an elevated quadratic has zero cubic term -- and Cardano's formula needs explicit
// branches for exactly those cases. Branchy numerical code on this path is what produced
// the jagged-coverage bugs the quadratic solve had to be rewritten to avoid. Newton needs
// no case analysis, and the bisection bracket makes it unconditionally convergent: a step
// that leaves the bracket (an inflection, a near-zero derivative) is replaced by the
// midpoint, so it can never diverge or stall.
fn solve_monotone_cubic(y0 : f32, y1 : f32, y2 : f32, y3 : f32, yTarget : f32) -> f32 {
    // Bracket oriented so f is increasing in t.
    let ascending = y3 > y0;
    var lo = 0.0;
    var hi = 1.0;
    // Secant-style first guess from the endpoints; far better than 0.5 for the near-linear
    // segments that dominate, which then converge in one or two steps.
    let span = y3 - y0;
    var t = select(0.5, clamp((yTarget - y0) / span, 0.0, 1.0), abs(span) > 1e-20);

    // Five iterations. Chosen by measurement, not headroom: sweeping the count against the
    // whole-image baselines, 5 leaves a worst-case difference of 1/255 on any pixel of any
    // scene while 3 breaks 36 pixels by up to 58/255 and 2 is plainly wrong. Four also passes
    // but lands exactly on the comparison tolerance, which is too little margin for a
    // different GPU's floating point. Cost is linear in this count -- it is the dominant term
    // in the loop, since banding already removed the segments that exit early.
    for (var it = 0u; it < 5u; it = it + 1u) {
        let mt = 1.0 - t;
        let f = mt * mt * mt * y0 + 3.0 * mt * mt * t * y1 + 3.0 * mt * t * t * y2 + t * t * t * y3 - yTarget;

        // Tighten the bracket with the sign of f, accounting for the direction of travel:
        // on an ascending segment f > 0 means t is past the root, on a descending one it
        // means t is short of it. (Written without select() on purpose -- `select(f < 0.0,
        // f > 0.0, ...)` is ambiguous with WGSL's `<...>` template syntax and will not parse.)
        let above = (f > 0.0) == ascending;
        lo = select(lo, t, !above);
        hi = select(hi, t, above);

        let d = 3.0 * (mt * mt * (y1 - y0) + 2.0 * mt * t * (y2 - y1) + t * t * (y3 - y2));
        // A zero/tiny derivative would send Newton to infinity; NaN-free fallback is the
        // bracket midpoint, which always makes progress.
        var next = select(0.5 * (lo + hi), t - f / d, abs(d) > 1e-20);
        // INCLUSIVE containment. The bracket update above may have just set lo or hi to t
        // itself, so a strict test rejects next == t -- which is exactly what happens on the
        // common case: a line degree-elevated to a cubic has an EXACT first guess, and a
        // strict test would throw it away and bisect away from the root, leaving ~(1-t)/2^8
        // of residual. That showed up as a 2.8x worse stroke edge at high zoom.
        if (!(next >= lo && next <= hi)) { next = 0.5 * (lo + hi); }   // also catches NaN
        t = next;
    }
    return clamp(t, 0.0, 1.0);
}

@fragment
fn fs_coverage(in : VSOut) -> @location(0) vec4<f32> {
    let px = floor(in.uv.x);
    let py = floor(in.uv.y);
    let flags = u32(in.color.y);          // 1 = even-odd fill, 2 = text gamma, 4 = aliased
    let evenOdd = (flags & 1u) != 0u;

    // This fragment's band. Bands are a whole number of pixel rows (the CPU side enforces
    // it), so all four subsample rows of this pixel fall in the same band and one lookup
    // serves them; a fractional band height would let rows past a boundary consult the wrong
    // segment list and drop crossings.
    let bandCount = bitcast<u32>(segs[0].x);
    let bandRows = segs[0].y;
    let band = min(u32(max(py, 0.0) / bandRows), bandCount - 1u);
    let range = segs[1u + band];
    let segBase = bitcast<u32>(range.x);
    let segCount = bitcast<u32>(range.y);
    var cov = 0.0;
    for (var s = 0u; s < 4u; s = s + 1u) {
        let sy = py + (f32(s) + 0.5) / 4.0;
        var w = 0;
        var cxs : array<f32, MAX_PIXEL_CROSSINGS>;
        var cds : array<i32, MAX_PIXEL_CROSSINGS>;
        var n = 0u;
        for (var i = 0u; i < segCount; i = i + 1u) {
            let at = segBase + 4u * i;
            let p0 = segs[at];
            let c1 = segs[at + 1u];
            let c2 = segs[at + 2u];
            let p1 = segs[at + 3u];
            // Segments are y-monotone (split at y-extrema on the CPU), so the crossing test is the
            // robust endpoint half-open rule: exactly the segments whose endpoints straddle sy cross
            // it. This uses exact float comparisons (no t-boundary epsilon), so a vertex shared by
            // two segments is counted once when the path is monotone through it and twice at an
            // extremum -- eliminating the missed/over-counted crossings that streak the fill.
            let b0 = p0.y <= sy;
            let b1 = p1.y <= sy;
            if (b0 == b1) { continue; }
            let t = solve_monotone_cubic(p0.y, c1.y, c2.y, p1.y, sy);
            let mt = 1.0 - t;
            let x = mt * mt * mt * p0.x + 3.0 * mt * mt * t * c1.x
                  + 3.0 * mt * t * t * c2.x + t * t * t * p1.x;
            let dir = select(-1, 1, p1.y > p0.y);         // whole segment runs one y-direction
            if (x <= px) {
                w = w + dir;
            } else if (x < px + 1.0 && n < MAX_PIXEL_CROSSINGS) {
                var j = n;
                loop {
                    if (j == 0u) { break; }
                    if (cxs[j - 1u] <= x) { break; }
                    cxs[j] = cxs[j - 1u];
                    cds[j] = cds[j - 1u];
                    j = j - 1u;
                }
                cxs[j] = x;
                cds[j] = dir;
                n = n + 1u;
            }
        }
        var covered = 0.0;
        var prev = px;
        for (var k = 0u; k < n; k = k + 1u) {
            let inside = select(w != 0, (w & 1) != 0, evenOdd);
            if (inside) { covered = covered + (cxs[k] - prev); }
            w = w + cds[k];
            prev = cxs[k];
        }
        let insideEnd = select(w != 0, (w & 1) != 0, evenOdd);
        if (insideEnd) { covered = covered + (px + 1.0 - prev); }
        cov = cov + covered * 0.25;
    }
    cov = clamp(cov, 0.0, 1.0);
    // RenderOptions.EdgeMode=Aliased: the app asked for hard edges, so collapse the
    // anti-aliased coverage to a binary in/out decision at the half-covered mark.
    if ((flags & 4u) != 0u) { cov = select(0.0, 1.0, cov >= 0.5); }
    // Text gamma: WPF blends glyph coverage in gamma space; cov^(1/2.2) matches the
    // CPU rasterizer's LUT on the display-destined sRGB path.
    if ((flags & 2u) != 0u) { cov = pow(cov, 1.0 / 2.2); }
    return vec4<f32>(cov, 0.0, 0.0, 1.0);
}
