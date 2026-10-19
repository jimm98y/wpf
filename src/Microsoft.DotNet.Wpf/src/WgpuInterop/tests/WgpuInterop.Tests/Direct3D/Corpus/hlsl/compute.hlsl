// The DXBC translator's own corpus: compute, UAVs, atomics, groupshared memory, doubles.

RWTexture1D<float4> u1 : register(u0);
RWTexture2D<float4> u2 : register(u1);
RWTexture3D<uint> u3 : register(u2);
RWBuffer<uint> ub : register(u3);
RWByteAddressBuffer raw : register(u4);
struct Item { float3 position; uint id; float weight; };
RWStructuredBuffer<Item> items : register(u5);
AppendStructuredBuffer<Item> appended : register(u6);
ConsumeStructuredBuffer<Item> consumed : register(u7);
StructuredBuffer<Item> source : register(t0);
ByteAddressBuffer rawSource : register(t1);
Texture2D<float4> image : register(t2);
SamplerState s : register(s0);

cbuffer Params : register(b0) { uint4 sizes; float4 scale; double2 dparams; };

groupshared float shared1[64];
groupshared Item sharedItems[16];

// thread ids, typed UAV load/store, raw and structured buffers, groupshared and barriers
[numthreads(8, 8, 1)]
void cs_uav(uint3 dt : SV_DispatchThreadID, uint3 gt : SV_GroupThreadID, uint3 g : SV_GroupID, uint gi : SV_GroupIndex)
{
    shared1[gi] = (float)dt.x * scale.x;
    if (gi < 16) sharedItems[gi] = source[gi];
    GroupMemoryBarrierWithGroupSync();
    float4 v = image.SampleLevel(s, (float2)dt.xy / (float2)sizes.xy, 0);
    u2[dt.xy] = v + shared1[(gi + 1) & 63] + sharedItems[gi & 15].weight;
    u1[dt.x] = u2[dt.xy] * 2;
    u3[dt] = u3[dt] + g.x + gt.y;
    ub[dt.x] = ub[dt.x] ^ gi;
    uint4 r4 = raw.Load4(dt.x * 16);
    raw.Store2(dt.x * 8, r4.xy + rawSource.Load2(gi * 8));
    raw.Store(gi * 4, rawSource.Load(gi * 4) + r4.z);
    Item it = items[dt.x];
    it.position += source[gi].position;
    items[dt.y] = it;
    DeviceMemoryBarrier();
    AllMemoryBarrierWithGroupSync();
    GroupMemoryBarrier();
    uint n, stride;
    items.GetDimensions(n, stride);
    raw.Store(0, n + stride);
}

// every atomic and immediate atomic, on a raw buffer, a typed UAV and groupshared memory; append/consume and counters
groupshared uint sharedCounter;

[numthreads(64, 1, 1)]
void cs_atomics(uint3 dt : SV_DispatchThreadID, uint gi : SV_GroupIndex)
{
    uint old;
    raw.InterlockedAdd(0, 1);
    raw.InterlockedAdd(4, dt.x, old);
    raw.InterlockedMin(8, dt.x); raw.InterlockedMax(12, dt.x);
    raw.InterlockedMin(16, (int)dt.x - 32); raw.InterlockedMax(20, (int)dt.x - 32);
    raw.InterlockedAnd(24, dt.x, old); raw.InterlockedOr(28, dt.x); raw.InterlockedXor(32, dt.x);
    raw.InterlockedExchange(36, dt.x, old);
    raw.InterlockedCompareExchange(40, 5, dt.x, old);
    raw.InterlockedCompareStore(44, 7, dt.x);
    InterlockedAdd(ub[1], 1); InterlockedMax(u3[uint3(0, 0, 0)], dt.x);
    if (gi == 0) sharedCounter = 0;
    GroupMemoryBarrierWithGroupSync();
    InterlockedAdd(sharedCounter, 1, old);
    uint id = items.IncrementCounter();
    items[id].id = old;
    if (dt.x & 1) { Item it = consumed.Consume(); appended.Append(it); }
}

RWStructuredBuffer<Item> released : register(u0);

// imm_atomic_consume on a counter
[numthreads(32, 1, 1)]
void cs_counter_down(uint3 dt : SV_DispatchThreadID)
{
    uint id = released.DecrementCounter();
    released[id].weight = (float)dt.x;
}

// doubles: dadd dmul dmin dmax deq dge dlt dne dmov dmovc dtof ftod
[numthreads(1, 1, 1)]
void cs_double(uint3 dt : SV_DispatchThreadID)
{
    double a = dparams.x + (double)dt.x, b = dparams.y * a;
    double c = min(a, b) + max(a, b);
    if (a == b || a < b || a >= b || a != c) c += a;
    c = a > b ? c : a;
    raw.Store(0, asuint((float)c));
}

#if defined(DOUBLE_EXT)
// the 11.1 double extensions: ddiv dfma drcp dtoi dtou itod utod
[numthreads(1, 1, 1)]
void cs_double_ext(uint3 dt : SV_DispatchThreadID)
{
    double a = dparams.x + (double)(int)dt.x, b = dparams.y + (double)dt.y;
    double c = fma(a, b, a / b) + rcp(b);
    raw.Store(0, (uint)c + (uint)(int)c);
}
#endif
