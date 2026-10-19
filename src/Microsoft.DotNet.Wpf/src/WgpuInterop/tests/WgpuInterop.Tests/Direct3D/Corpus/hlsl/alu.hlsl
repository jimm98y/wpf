// The DXBC translator's own corpus (Documentation/d3d11-webgpu-shim.md, section 9): arithmetic.
// Each entry point exercises one instruction family; build.ps1 compiles them with fxc for every
// profile listed in manifest.txt.

cbuffer Params : register(b0)
{
    float4 f4;
    float4 g4;
    int4 i4;
    uint4 u4;
    float4 table[8];
};

// float ALU: add mul mad div min max dp2 dp3 dp4 frc round_* rsq rcp sqrt exp log sincos, modifiers and _sat
float4 ps_float(float4 pos : SV_Position, float4 a : TEXCOORD0, float4 b : TEXCOORD1) : SV_Target
{
    float4 r = a * b + f4;
    r += a / (abs(b) + 1.0);
    r = min(r, g4) + max(-r, abs(a));
    r.x += dot(a.xy, b.xy) + dot(a.xyz, b.xyz) + dot(a, b);
    r += frac(a * 3.7) + round(b) + floor(a) + ceil(b) + trunc(a - b);
    r += rsqrt(abs(a) + 0.5) + rcp(abs(b) + 0.5) + sqrt(abs(a));
    r += exp2(a * 0.1) + log2(abs(b) + 1.0);
    float s, c;
    sincos(a.x, s, c);
    r.yz += float2(s, c);
    return saturate(r * 0.1);
}

// derivatives: deriv_rtx/rty and the coarse/fine forms (SM5)
float4 ps_deriv(float4 pos : SV_Position, float4 a : TEXCOORD0) : SV_Target
{
    float4 r = ddx(a) + ddy(a);
#if defined(SM5)
    r += ddx_coarse(a) + ddy_coarse(a) + ddx_fine(a) + ddy_fine(a);
#endif
    return r + fwidth(a);
}

// comparisons and selects: eq ne lt ge movc
float4 ps_compare(float4 pos : SV_Position, float4 a : TEXCOORD0, float4 b : TEXCOORD1) : SV_Target
{
    float4 r = (a == b) ? f4 : g4;
    r += (a != b) ? 1 : 0;
    r += (a < b) ? a : b;
    r += (a >= b) ? 2 : 3;
    return r;
}

// integers in a pixel shader (SM4): iadd imul ineg ishl ishr ushr and or xor not imin imax umin umax ieq ine ilt ige ult uge udiv
int4 ps_int(float4 pos : SV_Position, nointerpolation int4 a : TEXCOORD0, nointerpolation uint4 b : TEXCOORD1) : SV_Target
{
    int4 r = a + i4 * a - (-a);
    r += (a << 3) + (a >> 2) + (int4)(b >> 1u);
    r ^= (a & i4) | (int4)(b ^ u4) | ~a;
    r += min(a, i4) + max(a, i4) + (int4)min(b, u4) + (int4)max(b, u4);
    r += (a == i4) + (a != i4) + (a < i4) + (a >= i4) + (b < u4) + (b >= u4);
    r += (int4)(b / max(u4, 1u)) + (int4)(b % max(u4, 1u));
    return r;
}

// conversions: ftoi ftou itof utof, and integer division by a signed value
float4 ps_convert(float4 pos : SV_Position, float4 a : TEXCOORD0) : SV_Target
{
    int4 i = (int4)(a * 100.0);
    uint4 u = (uint4)abs(a * 50.0);
    float4 r = (float4)i * 0.01 + (float4)u * 0.02;
    r += (float4)(i / max(abs(i4), 1));
    return r;
}

// SM5 bit operations: countbits firstbithigh firstbitlow reversebits ubfe ibfe bfi f32tof16 f16tof32
uint4 ps_bits(float4 pos : SV_Position, nointerpolation uint4 a : TEXCOORD0, nointerpolation int4 b : TEXCOORD1) : SV_Target
{
    uint4 r = countbits(a) + firstbithigh(a) + firstbitlow(a) + (uint4)firstbithigh(b) + reversebits(a);
    r += (a >> 3) & 0x1F;                     // ubfe
    r += (uint4)((b << 4) >> 9);              // ibfe
    r += (a & ~0xF0u) | ((b.x << 4) & 0xF0u); // bfi
    r += f32tof16(asfloat(a)) + asuint(f16tof32(a));
    return r;
}

// SM5.0 with the 11.1 runtime: msad4
uint4 ps_msad(float4 pos : SV_Position, nointerpolation uint4 a : TEXCOORD0) : SV_Target
{
    return msad4(a.x, a.yz, a);
}

// integer multiply's wide forms (imul/umul hi:lo), umad, imad
int4 ps_mul(float4 pos : SV_Position, nointerpolation int4 a : TEXCOORD0, nointerpolation uint4 b : TEXCOORD1) : SV_Target
{
    int4 r = a * i4 + a;
    uint4 u = b * u4 + b;
    return r + (int4)u;
}

// indexing: a constant buffer by a register, the immediate constant buffer, indexable temps (x#)
float4 ps_index(float4 pos : SV_Position, nointerpolation int4 a : TEXCOORD0) : SV_Target
{
    static const float4 icb[4] = { float4(1, 2, 3, 4), float4(5, 6, 7, 8), float4(0.5, 0.25, 0.125, 1), float4(9, 8, 7, 6) };
    float4 local[6];
    [unroll] for (int k = 0; k < 6; k++) local[k] = table[k] * k;
    int i = a.x & 3, j = a.y % 6, n = a.z & 7;
    local[j] += icb[i];
    return local[(j + 1) % 6] + table[n] + icb[(i + 1) & 3];
}
