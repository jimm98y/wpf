// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Double precision (Documentation/d3d11-webgpu-shim.md, section 9). WGSL has f64 only behind an
// extension that naga's WGSL front end does not take, so every double instruction runs on IEEE 754
// binary64 emulated in integer arithmetic: a double is its two 32-bit halves (lo, hi) as D3D keeps
// them in a register (.xy or .zw), and the operations follow Berkeley SoftFloat's algorithms
// (roundPackToF64, addMags/subMags, mul, the fused mulAdd; Berkeley SoftFloat 3e, BSD licence, see
// THIRD-PARTY-NOTICES.TXT) with round to nearest even and
// subnormals kept, as D3D requires of doubles; division is a restoring long division. Results are
// exact, bit for bit what an IEEE double unit makes (DoubleTests holds them to WARP's).
//
// Lanes: a double source's lane i is its swizzled components (2i, 2i+1); a double destination's
// lane j is components (2j, 2j+1) where its mask has them. An instruction with a 32-bit result
// (comparisons, dtof, dtoi, dtou) writes lane k's result to the k-th component of its mask; one with
// a 32-bit source (ftod, itod, utod) takes lane j's value from swizzled component j.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc
{
    internal sealed partial class DxbcTranslator
    {
        /// <summary>A double source operand's lane (0 or 1) as vec2&lt;u32&gt;(lo, hi), its modifier on the sign.</summary>
        private string DoubleLane(Operand o, int lane)
        {
            string u = $"bitcast<vec4<u32>>({Source(Strip(o))})";
            string c0 = "xyzw"[lane * 2].ToString(), c1 = "xyzw"[lane * 2 + 1].ToString();
            string v = $"vec2<u32>(({u}).{c0}, ({u}).{c1})";
            return o.Modifier switch
            {
                OperandModifier.Neg => $"dx_dneg({v})",
                OperandModifier.Abs => $"dx_dabs({v})",
                OperandModifier.AbsNeg => $"dx_dneg(dx_dabs({v}))",
                _ => v,
            };
        }

        private static IEnumerable<int> DoubleLanes(Operand dest)
        {
            int mask = dest.IsMask ? dest.Mask : 0xF;
            if ((mask & 0x3) != 0) yield return 0;
            if ((mask & 0xC) != 0) yield return 1;
        }

        /// <summary>dest := the double lanes <paramref name="lane"/>(j) computes (vec2&lt;u32&gt; each).</summary>
        private void StoreDoubles(Operand dest, Func<int, string> lane)
        {
            var parts = new string[2] { "vec2<u32>(0u)", "vec2<u32>(0u)" };
            foreach (int j in DoubleLanes(dest))
            {
                string t = $"_d{_tmp++}";
                Line($"let {t} = {lane(j)};");
                parts[j] = t;
            }
            StoreI(dest, $"vec4<u32>({parts[0]}, {parts[1]})");
        }

        /// <summary>dest's masked components, in order, := the 32-bit results <paramref name="lane"/>(k) (u32 each).</summary>
        private void StoreDoubleResults(Operand dest, Func<int, string> lane)
        {
            int mask = dest.IsMask ? dest.Mask : 0xF;
            var c = new string[4] { "0u", "0u", "0u", "0u" };
            int k = 0;
            for (int i = 0; i < 4; i++)
                if ((mask & (1 << i)) != 0 && k < 2)
                {
                    string t = $"_d{_tmp++}";
                    Line($"let {t} = {lane(k++)};");
                    c[i] = t;
                }
            StoreI(dest, $"vec4<u32>({c[0]}, {c[1]}, {c[2]}, {c[3]})");
        }

        private bool EmitSoftDouble(Instruction ins)
        {
            var o = ins.Operands;
            string Bin(string fn) { Helper(SoftF64Wgsl); return fn; }
            switch (ins.Op)
            {
                case Opcode.DAdd: Bin(""); StoreDoubles(o[0], j => $"dx_dadd({DoubleLane(o[1], j)}, {DoubleLane(o[2], j)})"); return true;
                case Opcode.DMul: Bin(""); StoreDoubles(o[0], j => $"dx_dmul({DoubleLane(o[1], j)}, {DoubleLane(o[2], j)})"); return true;
                case Opcode.DDiv: Bin(""); StoreDoubles(o[0], j => $"dx_ddiv({DoubleLane(o[1], j)}, {DoubleLane(o[2], j)})"); return true;
                case Opcode.DFma: Bin(""); StoreDoubles(o[0], j => $"dx_dfma({DoubleLane(o[1], j)}, {DoubleLane(o[2], j)}, {DoubleLane(o[3], j)})"); return true;
                case Opcode.DRcp: Bin(""); StoreDoubles(o[0], j => $"dx_ddiv(vec2<u32>(0u, 0x3FF00000u), {DoubleLane(o[1], j)})"); return true;
                case Opcode.DMin: Bin(""); StoreDoubles(o[0], j => $"dx_dmin({DoubleLane(o[1], j)}, {DoubleLane(o[2], j)})"); return true;
                case Opcode.DMax: Bin(""); StoreDoubles(o[0], j => $"dx_dmax({DoubleLane(o[1], j)}, {DoubleLane(o[2], j)})"); return true;
                case Opcode.DEq: Bin(""); StoreDoubleResults(o[0], k => $"select(0u, 0xFFFFFFFFu, dx_deq({DoubleLane(o[1], k)}, {DoubleLane(o[2], k)}))"); return true;
                case Opcode.DNe: Bin(""); StoreDoubleResults(o[0], k => $"select(0xFFFFFFFFu, 0u, dx_deq({DoubleLane(o[1], k)}, {DoubleLane(o[2], k)}))"); return true;
                case Opcode.DLt: Bin(""); StoreDoubleResults(o[0], k => $"select(0u, 0xFFFFFFFFu, dx_dlt({DoubleLane(o[1], k)}, {DoubleLane(o[2], k)}))"); return true;
                case Opcode.DGe: Bin(""); StoreDoubleResults(o[0], k => $"select(0u, 0xFFFFFFFFu, dx_dle({DoubleLane(o[2], k)}, {DoubleLane(o[1], k)}))"); return true;
                case Opcode.DtoF: Bin(""); StoreDoubleResults(o[0], k => $"dx_dtof({DoubleLane(o[1], k)})"); return true;
                case Opcode.DtoI: Bin(""); StoreDoubleResults(o[0], k => $"dx_dtoi({DoubleLane(o[1], k)})"); return true;
                case Opcode.DtoU: Bin(""); StoreDoubleResults(o[0], k => $"dx_dtou({DoubleLane(o[1], k)})"); return true;
                case Opcode.FtoD: Bin(""); StoreDoubles(o[0], j => $"dx_ftod({USource(o[1])}.{"xyzw"[j]})"); return true;
                case Opcode.ItoD: Bin(""); StoreDoubles(o[0], j => $"dx_itod({USource(o[1])}.{"xyzw"[j]})"); return true;
                case Opcode.UtoD: Bin(""); StoreDoubles(o[0], j => $"dx_utod({USource(o[1])}.{"xyzw"[j]})"); return true;
            }
            return false;
        }

        // IEEE 754 binary64 in u32 pairs: vec2<u32>(lo, hi). A port of SoftFloat 3e's algorithms
        // (Berkeley, BSD licence; the structure, not its code), 64-bit integers as vec2<u32>.
        internal const string SoftF64Wgsl = @"
fn u64_add(a: vec2<u32>, b: vec2<u32>) -> vec2<u32> { let lo = a.x + b.x; return vec2<u32>(lo, a.y + b.y + select(0u, 1u, lo < a.x)); }
fn u64_sub(a: vec2<u32>, b: vec2<u32>) -> vec2<u32> { return vec2<u32>(a.x - b.x, a.y - b.y - select(0u, 1u, a.x < b.x)); }
fn u64_lt(a: vec2<u32>, b: vec2<u32>) -> bool { return a.y < b.y || (a.y == b.y && a.x < b.x); }
fn u64_is0(a: vec2<u32>) -> bool { return a.x == 0u && a.y == 0u; }
fn u64_clz(a: vec2<u32>) -> u32 { if (a.y != 0u) { return countLeadingZeros(a.y); } return 32u + countLeadingZeros(a.x); }
fn u64_shl(a: vec2<u32>, n: u32) -> vec2<u32> {
    if (n == 0u) { return a; }
    if (n >= 64u) { return vec2<u32>(0u); }
    if (n >= 32u) { return vec2<u32>(0u, a.x << (n - 32u)); }
    return vec2<u32>(a.x << n, (a.y << n) | (a.x >> (32u - n)));
}
fn u64_shr(a: vec2<u32>, n: u32) -> vec2<u32> {
    if (n == 0u) { return a; }
    if (n >= 64u) { return vec2<u32>(0u); }
    if (n >= 32u) { return vec2<u32>(a.y >> (n - 32u), 0u); }
    return vec2<u32>((a.x >> n) | (a.y << (32u - n)), a.y >> n);
}
// a right shift that keeps a 1 in the lowest bit if any 1 was shifted out
fn u64_shr_jam(a: vec2<u32>, n: u32) -> vec2<u32> {
    if (n == 0u) { return a; }
    if (n >= 64u) { return vec2<u32>(select(0u, 1u, !u64_is0(a)), 0u); }
    let r = u64_shr(a, n);
    let back = u64_shl(r, n);
    return vec2<u32>(r.x | select(0u, 1u, back.x != a.x || back.y != a.y), r.y);
}
fn u32_mul(a: u32, b: u32) -> vec2<u32> {
    let al = a & 0xFFFFu; let ah = a >> 16u; let bl = b & 0xFFFFu; let bh = b >> 16u;
    let ll = al * bl; let lh = al * bh; let hl = ah * bl; let hh = ah * bh;
    let mid = lh + hl;
    let midc = select(0u, 0x10000u, mid < lh);
    let lo = ll + (mid << 16u);
    return vec2<u32>(lo, hh + (mid >> 16u) + midc + select(0u, 1u, lo < ll));
}
// 64 x 64 -> 128: vec4<u32>(w0 .. w3), w0 lowest
fn u64_mul(a: vec2<u32>, b: vec2<u32>) -> vec4<u32> {
    let p00 = u32_mul(a.x, b.x); let p01 = u32_mul(a.x, b.y); let p10 = u32_mul(a.y, b.x); let p11 = u32_mul(a.y, b.y);
    let s1 = u64_add(u64_add(vec2<u32>(p00.y, 0u), vec2<u32>(p01.x, 0u)), vec2<u32>(p10.x, 0u));
    let s2 = u64_add(u64_add(u64_add(vec2<u32>(p01.y, 0u), vec2<u32>(p10.y, 0u)), vec2<u32>(p11.x, 0u)), vec2<u32>(s1.y, 0u));
    return vec4<u32>(p00.x, s1.x, s2.x, p11.y + s2.y);
}
fn u128_add(a: vec4<u32>, b: vec4<u32>) -> vec4<u32> {
    let lo = u64_add(a.xy, b.xy);
    return vec4<u32>(lo, u64_add(u64_add(a.zw, b.zw), vec2<u32>(select(0u, 1u, u64_lt(lo, a.xy)), 0u)));
}
fn u128_sub(a: vec4<u32>, b: vec4<u32>) -> vec4<u32> {
    let lo = u64_sub(a.xy, b.xy);
    return vec4<u32>(lo, u64_sub(u64_sub(a.zw, b.zw), vec2<u32>(select(0u, 1u, u64_lt(a.xy, b.xy)), 0u)));
}
fn u128_lt(a: vec4<u32>, b: vec4<u32>) -> bool { return u64_lt(a.zw, b.zw) || (a.z == b.z && a.w == b.w && u64_lt(a.xy, b.xy)); }
fn u128_is0(a: vec4<u32>) -> bool { return u64_is0(a.xy) && u64_is0(a.zw); }
fn u128_clz(a: vec4<u32>) -> u32 { if (!u64_is0(a.zw)) { return u64_clz(a.zw); } return 64u + u64_clz(a.xy); }
fn u128_shl(a: vec4<u32>, n: u32) -> vec4<u32> {
    if (n == 0u) { return a; }
    if (n >= 128u) { return vec4<u32>(0u); }
    if (n >= 64u) { return vec4<u32>(0u, 0u, u64_shl(a.xy, n - 64u)); }
    let hi = u64_shl(a.zw, n);
    let carry = u64_shr(a.xy, 64u - n);
    return vec4<u32>(u64_shl(a.xy, n), hi.x | carry.x, hi.y | carry.y);
}
fn u128_shr_jam(a: vec4<u32>, n: u32) -> vec4<u32> {
    if (n == 0u) { return a; }
    if (n >= 128u) { return vec4<u32>(select(0u, 1u, !u128_is0(a)), 0u, 0u, 0u); }
    var r: vec4<u32>;
    if (n >= 64u) { r = vec4<u32>(u64_shr(a.zw, n - 64u), 0u, 0u); }
    else {
        let lo = u64_shr(a.xy, n);
        let carry = u64_shl(a.zw, 64u - n);
        r = vec4<u32>(lo.x | carry.x, lo.y | carry.y, u64_shr(a.zw, n));
    }
    let back = u128_shl(r, n);
    r.x = r.x | select(0u, 1u, any(back != a));
    return r;
}

fn dx_dneg(a: vec2<u32>) -> vec2<u32> { return vec2<u32>(a.x, a.y ^ 0x80000000u); }
fn dx_dabs(a: vec2<u32>) -> vec2<u32> { return vec2<u32>(a.x, a.y & 0x7FFFFFFFu); }
fn f64_sign(a: vec2<u32>) -> bool { return (a.y >> 31u) != 0u; }
fn f64_exp(a: vec2<u32>) -> i32 { return i32((a.y >> 20u) & 0x7FFu); }
fn f64_frac(a: vec2<u32>) -> vec2<u32> { return vec2<u32>(a.x, a.y & 0xFFFFFu); }
fn f64_isnan(a: vec2<u32>) -> bool { return f64_exp(a) == 0x7FF && !u64_is0(f64_frac(a)); }
const F64_NAN = vec2<u32>(0u, 0x7FF80000u);
fn f64_quiet(a: vec2<u32>) -> vec2<u32> { return vec2<u32>(a.x, a.y | 0x00080000u); }
fn f64_pick_nan(a: vec2<u32>, b: vec2<u32>) -> vec2<u32> { if (f64_isnan(a)) { return f64_quiet(a); } if (f64_isnan(b)) { return f64_quiet(b); } return F64_NAN; }
// sign, biased exponent and significand added (a significand carry moves into the exponent)
fn f64_pack(sign: bool, e: i32, sig: vec2<u32>) -> vec2<u32> {
    return u64_add(vec2<u32>(0u, select(0u, 0x80000000u, sign) | (u32(e) << 20u)), sig);
}
// sig's leading 1 at bit 62, ten rounding bits; the result is 2^(e - 1022) x sig / 2^62
fn f64_round_pack(sign: bool, e0: i32, sig0: vec2<u32>) -> vec2<u32> {
    var e = e0;
    var sig = sig0;
    var round = sig.x & 0x3FFu;
    if (u32(e) >= 0x7FDu) {
        if (e < 0) {
            sig = u64_shr_jam(sig, u32(-e));
            e = 0;
            round = sig.x & 0x3FFu;
        } else if (e > 0x7FD || !u64_lt(u64_add(sig, vec2<u32>(0x200u, 0u)), vec2<u32>(0u, 0x80000000u))) {
            return vec2<u32>(0u, select(0u, 0x80000000u, sign) | 0x7FF00000u);
        }
    }
    sig = u64_shr(u64_add(sig, vec2<u32>(0x200u, 0u)), 10u);
    if (round == 0x200u) { sig.x = sig.x & 0xFFFFFFFEu; }
    if (u64_is0(sig)) { e = 0; }
    return f64_pack(sign, e, sig);
}
fn f64_norm_round_pack(sign: bool, e0: i32, sig: vec2<u32>) -> vec2<u32> {
    let shift = i32(u64_clz(sig)) - 1;
    let e = e0 - shift;
    if (shift >= 10 && u32(e) < 0x7FDu) {
        return f64_pack(sign, select(0, e, !u64_is0(sig)), u64_shl(sig, u32(shift - 10)));
    }
    return f64_round_pack(sign, e, u64_shl(sig, u32(shift)));
}

fn f64_add_mags(a: vec2<u32>, b: vec2<u32>, sign: bool) -> vec2<u32> {
    var ea = f64_exp(a); var sa = f64_frac(a);
    var eb = f64_exp(b); var sb = f64_frac(b);
    let d = ea - eb;
    if (d == 0) {
        if (ea == 0) { return f64_pack(sign, 0, u64_add(sa, sb)); }
        if (ea == 0x7FF) { if (!u64_is0(sa) || !u64_is0(sb)) { return f64_pick_nan(a, b); } return a; }
        let sz = u64_shl(u64_add(u64_add(vec2<u32>(0u, 0x00200000u), sa), sb), 9u);
        return f64_round_pack(sign, ea, sz);
    }
    sa = u64_shl(sa, 9u);
    sb = u64_shl(sb, 9u);
    var ez: i32;
    if (d < 0) {
        if (eb == 0x7FF) { if (!u64_is0(sb)) { return f64_pick_nan(a, b); } return f64_pack(sign, 0x7FF, vec2<u32>(0u)); }
        ez = eb;
        if (ea != 0) { sa = u64_add(sa, vec2<u32>(0u, 0x20000000u)); } else { sa = u64_shl(sa, 1u); }
        sa = u64_shr_jam(sa, u32(-d));
    } else {
        if (ea == 0x7FF) { if (!u64_is0(sa)) { return f64_pick_nan(a, b); } return a; }
        ez = ea;
        if (eb != 0) { sb = u64_add(sb, vec2<u32>(0u, 0x20000000u)); } else { sb = u64_shl(sb, 1u); }
        sb = u64_shr_jam(sb, u32(d));
    }
    var sz = u64_add(u64_add(vec2<u32>(0u, 0x20000000u), sa), sb);
    if (u64_lt(sz, vec2<u32>(0u, 0x40000000u))) { ez = ez - 1; sz = u64_shl(sz, 1u); }
    return f64_round_pack(sign, ez, sz);
}

fn f64_sub_mags(a: vec2<u32>, b: vec2<u32>, sign0: bool) -> vec2<u32> {
    var sign = sign0;
    var ea = f64_exp(a); var sa = f64_frac(a);
    let eb = f64_exp(b); var sb = f64_frac(b);
    let d = ea - eb;
    if (d == 0) {
        if (ea == 0x7FF) { if (!u64_is0(sa) || !u64_is0(sb)) { return f64_pick_nan(a, b); } return F64_NAN; }
        var diff: vec2<u32>;
        if (u64_lt(sa, sb)) { sign = !sign; diff = u64_sub(sb, sa); } else { diff = u64_sub(sa, sb); }
        if (u64_is0(diff)) { return vec2<u32>(0u); }
        if (ea != 0) { ea = ea - 1; }
        var shift = i32(u64_clz(diff)) - 11;
        var ez = ea - shift;
        if (ez < 0) { shift = ea; ez = 0; }
        return f64_pack(sign, ez, u64_shl(diff, u32(shift)));
    }
    sa = u64_shl(sa, 10u);
    sb = u64_shl(sb, 10u);
    var ez: i32;
    var sz: vec2<u32>;
    if (d < 0) {
        sign = !sign;
        if (eb == 0x7FF) { if (!u64_is0(sb)) { return f64_pick_nan(a, b); } return f64_pack(sign, 0x7FF, vec2<u32>(0u)); }
        if (ea != 0) { sa = u64_add(sa, vec2<u32>(0u, 0x40000000u)); } else { sa = u64_add(sa, sa); }
        sa = u64_shr_jam(sa, u32(-d));
        sb = vec2<u32>(sb.x, sb.y | 0x40000000u);
        ez = eb;
        sz = u64_sub(sb, sa);
    } else {
        if (ea == 0x7FF) { if (!u64_is0(sa)) { return f64_pick_nan(a, b); } return a; }
        if (eb != 0) { sb = u64_add(sb, vec2<u32>(0u, 0x40000000u)); } else { sb = u64_add(sb, sb); }
        sb = u64_shr_jam(sb, u32(d));
        sa = vec2<u32>(sa.x, sa.y | 0x40000000u);
        ez = ea;
        sz = u64_sub(sa, sb);
    }
    return f64_norm_round_pack(sign, ez - 1, sz);
}

fn dx_dadd(a: vec2<u32>, b: vec2<u32>) -> vec2<u32> {
    if (f64_sign(a) == f64_sign(b)) { return f64_add_mags(a, b, f64_sign(a)); }
    return f64_sub_mags(a, b, f64_sign(a));
}

// a subnormal's significand normalised: (exponent, significand with its leading 1 at bit 52)
fn f64_norm_sub(sig: vec2<u32>) -> vec3<u32> {
    let shift = u64_clz(sig) - 11u;
    let s = u64_shl(sig, shift);
    return vec3<u32>(u32(1 - i32(shift)), s);
}

fn dx_dmul(a: vec2<u32>, b: vec2<u32>) -> vec2<u32> {
    let sign = f64_sign(a) != f64_sign(b);
    var ea = f64_exp(a); var sa = f64_frac(a);
    var eb = f64_exp(b); var sb = f64_frac(b);
    if (ea == 0x7FF) {
        if (!u64_is0(sa) || (eb == 0x7FF && !u64_is0(sb))) { return f64_pick_nan(a, b); }
        if (eb == 0 && u64_is0(sb)) { return F64_NAN; }
        return f64_pack(sign, 0x7FF, vec2<u32>(0u));
    }
    if (eb == 0x7FF) {
        if (!u64_is0(sb)) { return f64_pick_nan(a, b); }
        if (ea == 0 && u64_is0(sa)) { return F64_NAN; }
        return f64_pack(sign, 0x7FF, vec2<u32>(0u));
    }
    if (ea == 0) { if (u64_is0(sa)) { return f64_pack(sign, 0, vec2<u32>(0u)); } let n = f64_norm_sub(sa); ea = i32(n.x); sa = n.yz; }
    if (eb == 0) { if (u64_is0(sb)) { return f64_pack(sign, 0, vec2<u32>(0u)); } let n = f64_norm_sub(sb); eb = i32(n.x); sb = n.yz; }
    var ez = ea + eb - 0x3FF;
    sa = u64_shl(vec2<u32>(sa.x, sa.y | 0x00100000u), 10u);
    sb = u64_shl(vec2<u32>(sb.x, sb.y | 0x00100000u), 11u);
    let p = u64_mul(sa, sb);
    var sz = vec2<u32>(p.z | select(0u, 1u, !u64_is0(p.xy)), p.w);
    if (u64_lt(sz, vec2<u32>(0u, 0x40000000u))) { ez = ez - 1; sz = u64_shl(sz, 1u); }
    return f64_round_pack(sign, ez, sz);
}

fn dx_ddiv(a: vec2<u32>, b: vec2<u32>) -> vec2<u32> {
    let sign = f64_sign(a) != f64_sign(b);
    var ea = f64_exp(a); var sa = f64_frac(a);
    var eb = f64_exp(b); var sb = f64_frac(b);
    if (ea == 0x7FF) {
        if (!u64_is0(sa)) { return f64_pick_nan(a, b); }
        if (eb == 0x7FF) { if (!u64_is0(sb)) { return f64_pick_nan(a, b); } return F64_NAN; }
        return f64_pack(sign, 0x7FF, vec2<u32>(0u));
    }
    if (eb == 0x7FF) { if (!u64_is0(sb)) { return f64_pick_nan(a, b); } return f64_pack(sign, 0, vec2<u32>(0u)); }
    if (eb == 0) {
        if (u64_is0(sb)) { if (ea == 0 && u64_is0(sa)) { return F64_NAN; } return f64_pack(sign, 0x7FF, vec2<u32>(0u)); }
        let n = f64_norm_sub(sb); eb = i32(n.x); sb = n.yz;
    }
    if (ea == 0) { if (u64_is0(sa)) { return f64_pack(sign, 0, vec2<u32>(0u)); } let n = f64_norm_sub(sa); ea = i32(n.x); sa = n.yz; }
    var ez = ea - eb + 0x3FE;
    var x = vec2<u32>(sa.x, sa.y | 0x00100000u);
    let y = vec2<u32>(sb.x, sb.y | 0x00100000u);
    if (u64_lt(x, y)) { ez = ez - 1; x = u64_shl(x, 1u); }
    // restoring division: 63 quotient bits (the leading 1 at bit 62), the remainder as a sticky bit
    var q = vec2<u32>(0u);
    for (var i = 0u; i < 63u; i = i + 1u) {
        q = u64_shl(q, 1u);
        if (!u64_lt(x, y)) { x = u64_sub(x, y); q.x = q.x | 1u; }
        x = u64_shl(x, 1u);
    }
    if (!u64_is0(x)) { q.x = q.x | 1u; }
    return f64_round_pack(sign, ez, q);
}

// a * b + c, rounded once
fn dx_dfma(a: vec2<u32>, b: vec2<u32>, c: vec2<u32>) -> vec2<u32> {
    let sp = f64_sign(a) != f64_sign(b);
    let sc = f64_sign(c);
    var ea = f64_exp(a); var sa = f64_frac(a);
    var eb = f64_exp(b); var sb = f64_frac(b);
    var ec = f64_exp(c); var scs = f64_frac(c);
    if (f64_isnan(a) || f64_isnan(b) || f64_isnan(c)) { if (f64_isnan(a) || f64_isnan(b)) { return f64_pick_nan(a, b); } return f64_quiet(c); }
    let za = ea == 0 && u64_is0(sa);
    let zb = eb == 0 && u64_is0(sb);
    if (ea == 0x7FF || eb == 0x7FF) {
        if (za || zb) { return F64_NAN; }
        if (ec == 0x7FF && sc != sp) { return F64_NAN; }
        return f64_pack(sp, 0x7FF, vec2<u32>(0u));
    }
    if (ec == 0x7FF) { return c; }
    if (za || zb) {
        // the product is a zero of sign sp: c, or a signed zero when c is one too
        if (ec == 0 && u64_is0(scs)) { return f64_pack(sp && sc, 0, vec2<u32>(0u)); }
        return c;
    }
    if (ea == 0) { let n = f64_norm_sub(sa); ea = i32(n.x); sa = n.yz; }
    if (eb == 0) { let n = f64_norm_sub(sb); eb = i32(n.x); sb = n.yz; }
    // the exact product, its value 2^(ep - 1023) x P / 2^104, moved up 20 bits for headroom
    let ep = ea + eb - 0x3FF;
    var p = u128_shl(u64_mul(vec2<u32>(sa.x, sa.y | 0x00100000u), vec2<u32>(sb.x, sb.y | 0x00100000u)), 20u);
    var cz = ec == 0 && u64_is0(scs);
    var q = vec4<u32>(0u);
    var eq = ep;
    if (!cz) {
        if (ec == 0) { let n = f64_norm_sub(scs); ec = i32(n.x); scs = n.yz; }
        // c on the same scale: its leading 1 at bit 104 + 20
        q = u128_shl(vec4<u32>(scs.x, scs.y | 0x00100000u, 0u, 0u), 72u);
        eq = ec;
    }
    // to a common exponent: the smaller moves right, its lost bits kept as a sticky bit
    var ex = ep;
    if (!cz) {
        if (ep >= eq) { q = u128_shr_jam(q, u32(min(ep - eq, 130))); }
        else { p = u128_shr_jam(p, u32(min(eq - ep, 130))); ex = eq; }
    }
    var sign = sp;
    var v: vec4<u32>;
    if (cz || sp == sc) { v = u128_add(p, q); }
    else if (u128_lt(p, q)) { v = u128_sub(q, p); sign = sc; }
    else { v = u128_sub(p, q); }
    if (u128_is0(v)) { return vec2<u32>(0u); }
    // the leading 1 to bit 126: the top 64 bits then carry it at bit 62
    let lz = i32(u128_clz(v));
    var sl = lz - 1;
    if (sl >= 0) { v = u128_shl(v, u32(sl)); } else { v = u128_shr_jam(v, u32(-sl)); }
    let sig = vec2<u32>(v.z | select(0u, 1u, !u64_is0(v.xy)), v.w);
    return f64_round_pack(sign, ex - sl + 1, sig);
}

fn dx_deq(a: vec2<u32>, b: vec2<u32>) -> bool {
    if (f64_isnan(a) || f64_isnan(b)) { return false; }
    return (a.x == b.x && a.y == b.y) || (u64_is0(dx_dabs(a)) && u64_is0(dx_dabs(b)));
}
fn dx_dlt(a: vec2<u32>, b: vec2<u32>) -> bool {
    if (f64_isnan(a) || f64_isnan(b)) { return false; }
    let na = f64_sign(a); let nb = f64_sign(b);
    if (na != nb) { return na && !(u64_is0(dx_dabs(a)) && u64_is0(dx_dabs(b))); }
    if (a.x == b.x && a.y == b.y) { return false; }
    return na != u64_lt(a, b);
}
fn dx_dle(a: vec2<u32>, b: vec2<u32>) -> bool {
    if (f64_isnan(a) || f64_isnan(b)) { return false; }
    return dx_deq(a, b) || dx_dlt(a, b);
}
// D3D's dmin/dmax: a NaN operand gives the other
fn dx_dmin(a: vec2<u32>, b: vec2<u32>) -> vec2<u32> {
    if (f64_isnan(a)) { return b; } if (f64_isnan(b)) { return a; }
    return select(b, a, dx_dlt(a, b));
}
fn dx_dmax(a: vec2<u32>, b: vec2<u32>) -> vec2<u32> {
    if (f64_isnan(a)) { return b; } if (f64_isnan(b)) { return a; }
    return select(a, b, dx_dlt(a, b));
}

fn dx_dtof(a: vec2<u32>) -> u32 {
    let sign = select(0u, 0x80000000u, f64_sign(a));
    let e = f64_exp(a);
    let frac = f64_frac(a);
    if (e == 0x7FF) { if (!u64_is0(frac)) { return sign | 0x7FC00000u | (((frac.y << 3u) | (frac.x >> 29u)) & 0x3FFFFFu); } return sign | 0x7F800000u; }
    // 30 significant bits with a sticky bit, then roundPackToF32
    let s30 = u64_shr_jam(frac, 22u);
    if (e == 0 && s30.x == 0u) { return sign; }
    var ez = e - 0x381;
    var sig = s30.x | 0x40000000u;
    var round = sig & 0x7Fu;
    if (u32(ez) >= 0xFDu) {
        if (ez < 0) {
            let n = u32(-ez);
            if (n >= 31u) { sig = select(0u, 1u, sig != 0u); }
            else { sig = (sig >> n) | select(0u, 1u, (sig << (32u - n)) != 0u); }
            ez = 0;
            round = sig & 0x7Fu;
        } else if (ez > 0xFD || sig + 0x40u >= 0x80000000u) { return sign | 0x7F800000u; }
    }
    sig = (sig + 0x40u) >> 7u;
    if (round == 0x40u) { sig = sig & 0xFFFFFFFEu; }
    if (sig == 0u) { ez = 0; }
    return sign + (u32(ez) << 23u) + sig;
}
fn dx_ftod(f: u32) -> vec2<u32> {
    let sign = (f >> 31u) != 0u;
    let e = i32((f >> 23u) & 0xFFu);
    var frac = f & 0x7FFFFFu;
    if (e == 0xFF) { if (frac != 0u) { return vec2<u32>(frac << 29u, select(0u, 0x80000000u, sign) | 0x7FF80000u | (frac >> 3u)); } return f64_pack(sign, 0x7FF, vec2<u32>(0u)); }
    if (e == 0) {
        if (frac == 0u) { return f64_pack(sign, 0, vec2<u32>(0u)); }
        let shift = countLeadingZeros(frac) - 8u;
        return f64_pack(sign, 0x381 - i32(shift), u64_shl(vec2<u32>((frac << shift) & 0x7FFFFFu, 0u), 29u));
    }
    return f64_pack(sign, e + 0x380, u64_shl(vec2<u32>(frac, 0u), 29u));
}
fn dx_itod(i: u32) -> vec2<u32> {
    if (i == 0u) { return vec2<u32>(0u); }
    let sign = (i >> 31u) != 0u;
    let m = select(i, 0u - i, sign);
    let shift = countLeadingZeros(m) + 21u;
    return f64_pack(sign, 0x432 - i32(shift), u64_shl(vec2<u32>(m, 0u), shift));
}
fn dx_utod(u: u32) -> vec2<u32> {
    if (u == 0u) { return vec2<u32>(0u); }
    let shift = countLeadingZeros(u) + 21u;
    return f64_pack(false, 0x432 - i32(shift), u64_shl(vec2<u32>(u, 0u), shift));
}
// toward zero; a NaN gives 0, out of range saturates
fn dx_dtoi(a: vec2<u32>) -> u32 {
    if (f64_isnan(a)) { return 0u; }
    let e = f64_exp(a);
    let neg = f64_sign(a);
    if (e < 0x3FF) { return 0u; }
    if (e >= 0x3FF + 31) {
        if (neg && e == 0x3FF + 31 && u64_is0(f64_frac(a))) { return 0x80000000u; }
        return select(0x7FFFFFFFu, 0x80000000u, neg);
    }
    let m = u64_shr(vec2<u32>(a.x, (a.y & 0xFFFFFu) | 0x100000u), u32(0x433 - e));
    return select(m.x, 0u - m.x, neg);
}
fn dx_dtou(a: vec2<u32>) -> u32 {
    if (f64_isnan(a)) { return 0u; }
    let e = f64_exp(a);
    if (f64_sign(a)) { return 0u; }
    if (e < 0x3FF) { return 0u; }
    if (e >= 0x3FF + 32) { return 0xFFFFFFFFu; }
    return u64_shr(vec2<u32>(a.x, (a.y & 0xFFFFFu) | 0x100000u), u32(0x433 - e)).x;
}
";
    }
}
