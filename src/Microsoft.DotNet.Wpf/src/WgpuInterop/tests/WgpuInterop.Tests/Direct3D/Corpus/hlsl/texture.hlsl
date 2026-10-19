// The DXBC translator's own corpus: texture instructions of every dimension.

Texture1D<float4> t1 : register(t0);
Texture1DArray<float4> t1a : register(t1);
Texture2D<float4> t2 : register(t2);
Texture2DArray<float4> t2a : register(t3);
Texture3D<float4> t3 : register(t4);
TextureCube<float4> tc : register(t5);
#if defined(SM41) || defined(SM5)
Texture2DMS<float4> tms : register(t6);
#else
Texture2DMS<float4, 4> tms : register(t6);   // 4.0 takes only sized MSAA textures
#endif
Texture2D<float> depth : register(t7);
Buffer<float4> buf : register(t8);
Texture2D<uint4> tu : register(t9);
Texture2D<int4> ti : register(t10);
#if defined(SM41) || defined(SM5)
TextureCubeArray<float4> tca : register(t11);
#endif
SamplerState s : register(s0);
SamplerComparisonState sc : register(s1);

cbuffer Params : register(b0) { float bias; float level; float cmp; int sampleIndex; };

// sample sample_b sample_l sample_d with offsets, on every dimension
float4 ps_sample(float4 pos : SV_Position, float4 uv : TEXCOORD0) : SV_Target
{
    float4 r = t1.Sample(s, uv.x) + t1a.Sample(s, uv.xy) + t2.Sample(s, uv.xy) + t2a.Sample(s, uv.xyz) + t3.Sample(s, uv.xyz) + tc.Sample(s, uv.xyz);
    r += t2.Sample(s, uv.xy, int2(1, -2));
    r += t2.SampleBias(s, uv.xy, bias) + t2.SampleLevel(s, uv.xy, level) + t2.SampleGrad(s, uv.xy, ddx(uv.xy), ddy(uv.xy));
    r += t3.SampleLevel(s, uv.xyz, level, int3(1, 0, -1)) + tc.SampleBias(s, uv.xyz, bias) + t2a.SampleLevel(s, uv.xyz, 0);
#if defined(SM41) || defined(SM5)
    r += tca.Sample(s, float4(uv.xyz, 1));
#endif
    return r;
}

// sample_c sample_c_lz
float4 ps_compare(float4 pos : SV_Position, float4 uv : TEXCOORD0) : SV_Target
{
    return depth.SampleCmp(sc, uv.xy, cmp) + depth.SampleCmpLevelZero(sc, uv.zw, cmp, int2(1, 1));
}

// ld ld2dms resinfo bufinfo sampleinfo samplepos, integer textures, buffer loads
float4 ps_load(float4 pos : SV_Position, nointerpolation int4 c : TEXCOORD0) : SV_Target
{
    float4 r = t2.Load(c.xyz) + t2.Load(c.xyz, int2(-1, 1)) + t1.Load(c.xy) + t2a.Load(c.xyzw) + t3.Load(c.xyzw);
#if defined(SM41) || defined(SM5)
    r += tms.Load(c.xy, sampleIndex);         // a dynamic sample index (4.1)
#else
    r += tms.Load(c.xy, 1);
#endif
    r += buf.Load(c.x) + (float4)tu.Load(c.xyz) + (float4)ti.Load(c.xyz);
    uint w, h, levels, elements, samples;
    t2.GetDimensions(0, w, h, levels);
    r.x += w + h + levels;
    t2a.GetDimensions(w, h, elements);
    r.y += elements;
#if defined(SM5)
    buf.GetDimensions(w);                     // bufinfo (5.0)
    r.z += w;
#endif
#if defined(SM41) || defined(SM5)
    tms.GetDimensions(w, h, samples);         // sampleinfo (4.1)
    r.w += samples;
    r.xy += tms.GetSamplePosition(sampleIndex);
#endif
    return r;
}

#if defined(SM41) || defined(SM5)
// gather4 (4.1), gather4_c gather4_po gather4_po_c and the per-channel forms (5.0), lod
float4 ps_gather(float4 pos : SV_Position, float4 uv : TEXCOORD0, nointerpolation int2 o : TEXCOORD1) : SV_Target
{
    float4 r = t2.Gather(s, uv.xy);
#if defined(SM5)
    r += t2.GatherRed(s, uv.xy, int2(1, 0)) + t2.GatherGreen(s, uv.xy) + t2.GatherBlue(s, uv.xy) + t2.GatherAlpha(s, uv.xy);
    r += t2.Gather(s, uv.xy, o) + depth.GatherCmp(sc, uv.xy, cmp) + depth.GatherCmpRed(sc, uv.xy, cmp, o);
    r += tc.Gather(s, uv.xyz) + t2a.Gather(s, uv.xyz);
#endif
    r.x += t2.CalculateLevelOfDetail(s, uv.xy) + t2.CalculateLevelOfDetailUnclamped(s, uv.xy);
    return r;
}
#endif
