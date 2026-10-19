// The DXBC translator's own corpus: compute shaders whose results do not depend on the order their
// threads run in, so they are executed on the shim and on WARP and their UAVs compared word for word
// (DxbcCoverageTests). Each thread writes only its own elements.

cbuffer Params : register(b0) { uint4 u4; float4 f4; int4 i4; };

StructuredBuffer<uint4> inputU : register(t0);
StructuredBuffer<float4> inputF : register(t1);
ByteAddressBuffer rawIn : register(t2);
Texture2D<float4> image : register(t3);
Texture3D<float4> volume : register(t4);
Buffer<float4> typedIn : register(t5);
SamplerState s : register(s0);

RWStructuredBuffer<uint4> outU : register(u0);
RWStructuredBuffer<float4> outF : register(u1);
RWByteAddressBuffer rawOut : register(u2);
RWTexture2D<uint> texOut : register(u3);
RWBuffer<uint> typedOut : register(u4);

// integer and bit instructions, per thread
[numthreads(32, 1, 1)]
void cs_int(uint3 dt : SV_DispatchThreadID)
{
    uint i = dt.x;
    uint4 a = inputU[i], b = inputU[i + 64] + 1;
    int4 sa = (int4)a - 32, sb = (int4)b - 16;
    uint4 r = a * b + (a ^ b) + (a | b) - (a & b) + (b >> 1) + (a << 2) + min(a, b) + max(a, b);
    r += countbits(a) + firstbithigh(a) + firstbitlow(b) + reversebits(a) + (a / max(b, 1u)) + (a % max(b, 1u));
    r += (uint4)(sa * sb + sa / (sb | 1) + (sa >> 1) + abs(sa) + min(sa, sb) + max(sa, sb));
    r += (uint4)(sa < sb) + (uint4)(a >= b) + (uint4)(sa == sb) + (uint4)(a != b);
    r += f32tof16(asfloat(a | 0x3F000000u)) + asuint(f16tof32(a));
    r += ((a >> (b & 7)) & 0x1F) + (uint4)((sa << 3) >> 5);
    outU[i] = r;
    rawOut.Store4(i * 16, r ^ u4);
    rawOut.Store(2048 + i * 4, rawIn.Load(i * 4) + rawIn.Load2(i * 8).y);
}

// float instructions, per thread
[numthreads(32, 1, 1)]
void cs_float(uint3 dt : SV_DispatchThreadID)
{
    uint i = dt.x;
    float4 a = inputF[i], b = inputF[i + 64];
    float4 r = a * b + a / b + sqrt(a) + rsqrt(b) + exp2(a) + log2(b) + frac(a * 5) + floor(b * 3) + ceil(a * 2) + round(b * 7);
    r += min(a, b) + max(a, b) + mad(a, b, f4) + dot(a, b) + dot(a.xyz, b.xyz) + saturate(a - b) + abs(a - b) + rcp(b);
    float sn, cs;
    sincos(a.x * 3, sn, cs);
    r.xy += float2(sn, cs);
    r += (float4)(int4)(a * 100) + (float4)(uint4)(b * 50) + (a < b ? f4 : -f4);
    outF[i] = r;
}

// textures read by load, level sample, 3D and typed buffers; typed UAV texture and buffer, own elements only
[numthreads(8, 8, 1)]
void cs_textures(uint3 dt : SV_DispatchThreadID, uint3 g : SV_GroupID, uint3 gt : SV_GroupThreadID, uint gi : SV_GroupIndex)
{
    uint2 p = dt.xy & 15;
    float4 v = image.Load(int3(p, 0)) + image.SampleLevel(s, (float2(p) + 0.5) / 16.0, 0) + volume.Load(int4(p & 3, gt.x & 3, 0)) + typedIn.Load(gi);
    uint w, h, levels;
    image.GetDimensions(0, w, h, levels);
    if (dt.x < 16 && dt.y < 16)
    {
        texOut[p] = texOut[p] + (uint)(v.x * 100) + w + h + levels + g.x;
        outF[dt.y * 16 + dt.x] = v;
    }
    typedOut[dt.y * 16 + dt.x] = typedOut[dt.y * 16 + dt.x] ^ (gi + g.x * 1000);
}

// group-shared memory and barriers: a reduction within each group
groupshared float partial[64];
groupshared uint words[64];

[numthreads(64, 1, 1)]
void cs_groupshared(uint3 dt : SV_DispatchThreadID, uint gi : SV_GroupIndex, uint3 g : SV_GroupID)
{
    partial[gi] = inputF[dt.x].x;
    words[gi] = inputU[dt.x].y;
    GroupMemoryBarrierWithGroupSync();
    [unroll] for (uint stride = 32; stride > 0; stride >>= 1)
    {
        if (gi < stride) { partial[gi] += partial[gi + stride]; words[gi] ^= words[gi + stride]; }
        GroupMemoryBarrierWithGroupSync();
    }
    if (gi == 0) { outF[g.x] = float4(partial[0], partial[1], 0, 0); outU[g.x] = uint4(words[0], words[1], g.x, 0); }
}

// order-independent atomics: totals, minima, maxima and bit masks, and a counted append
[numthreads(32, 1, 1)]
void cs_atomic_totals(uint3 dt : SV_DispatchThreadID)
{
    uint i = dt.x, v = inputU[i].x;
    rawOut.InterlockedAdd(0, v);
    rawOut.InterlockedMin(4, v); rawOut.InterlockedMax(8, v);
    rawOut.InterlockedMin(12, (int)v - 30); rawOut.InterlockedMax(16, (int)v - 30);
    rawOut.InterlockedAnd(20, v | 0xFFFF0000u); rawOut.InterlockedOr(24, v); rawOut.InterlockedXor(28, v);
    uint old;
    rawOut.InterlockedAdd(32, 1, old);
    rawOut.InterlockedAdd(36, old);           // the sum of the returned values is the same in any order
    InterlockedAdd(typedOut[0], v);
}
