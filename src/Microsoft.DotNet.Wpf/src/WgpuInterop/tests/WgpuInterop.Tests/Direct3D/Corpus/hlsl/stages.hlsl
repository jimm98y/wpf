// The DXBC translator's own corpus: the stages and their system values, interpolation modifiers,
// geometry shaders and tessellation.

cbuffer Params : register(b0) { float4x4 transform; float4 tess; float4 color; };

struct VsIn { float3 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; uint vid : SV_VertexID; uint iid : SV_InstanceID; };
struct VsOut
{
    float4 position : SV_Position;
    float3 normal : NORMAL;
    noperspective float2 uv : TEXCOORD0;
    centroid float4 c : COLOR0;
    nointerpolation uint id : TEXCOORD1;
    float clip : SV_ClipDistance0;
    float cull : SV_CullDistance0;
};

VsOut vs_main(VsIn i)
{
    VsOut o;
    o.position = mul(float4(i.position, 1), transform);
    o.normal = i.normal;
    o.uv = i.uv;
    o.c = color * (float)i.vid;
    o.id = i.iid;
    o.clip = o.position.x;
    o.cull = o.position.y;
    return o;
}

struct PsOut { float4 c0 : SV_Target0; float4 c1 : SV_Target1; float depth : SV_Depth; };

// SV_IsFrontFace SV_PrimitiveID SV_Position, MRT and depth output
PsOut ps_sv(VsOut i, bool front : SV_IsFrontFace, uint prim : SV_PrimitiveID)
{
    PsOut o;
    o.c0 = i.c * (front ? 1 : 0.5);
    o.c1 = float4(i.normal, (float)prim) + float4(i.uv, i.position.xy);
    o.depth = i.position.z * 0.5;
    return o;
}

#if defined(SM5)
// SV_SampleIndex, SV_Coverage in and out, conservative depth, eval_* (5.0)
float4 ps_sample(VsOut i, uint sample : SV_SampleIndex, uint coverage : SV_Coverage, out uint outCoverage : SV_Coverage, out float d : SV_DepthGreaterEqual) : SV_Target
{
    outCoverage = coverage & 3;
    d = i.position.z;
    float4 c = EvaluateAttributeAtSample(i.c, sample) + EvaluateAttributeCentroid(i.c) + EvaluateAttributeSnapped(i.c, int2(1, -1));
    return c;
}
#endif

// geometry shaders: a point to a quad (triangle strip), a cut, the maximum vertex count
struct GsOut { float4 position : SV_Position; float2 uv : TEXCOORD0; uint layer : SV_RenderTargetArrayIndex; };

[maxvertexcount(8)]
void gs_quad(point VsOut p[1], inout TriangleStream<GsOut> stream)
{
    [unroll] for (int k = 0; k < 4; k++)
    {
        GsOut o;
        o.position = p[0].position + float4((k & 1) * 0.1, (k >> 1) * 0.1, 0, 0);
        o.uv = float2(k & 1, k >> 1);
        o.layer = p[0].id;
        stream.Append(o);
    }
    stream.RestartStrip();
}

// a line with adjacency to lines, a triangle to points
[maxvertexcount(2)]
void gs_lineadj(lineadj VsOut v[4], inout LineStream<GsOut> stream)
{
    GsOut o;
    o.position = v[1].position; o.uv = v[0].uv; o.layer = 0; stream.Append(o);
    o.position = v[2].position; o.uv = v[3].uv; stream.Append(o);
}

#if defined(SM5)
// instancing (SV_GSInstanceID) and multiple streams (5.0)
struct GsStreamOut { float4 position : SV_Position; float4 c : COLOR0; };

[instance(3)]
[maxvertexcount(3)]
void gs_instanced(triangle VsOut v[3], uint instance : SV_GSInstanceID, inout PointStream<GsStreamOut> s0, inout PointStream<GsStreamOut> s1)
{
    [unroll] for (int k = 0; k < 3; k++)
    {
        GsStreamOut o;
        o.position = v[k].position + instance;
        o.c = v[k].c;
        if (k == 0) s0.Append(o); else s1.Append(o);
    }
}

// tessellation: a triangle patch, integer partitioning; a quad patch, fractional_odd; an isoline
struct Cp { float3 position : POSITION; float2 uv : TEXCOORD0; };
struct TriConst { float edges[3] : SV_TessFactor; float inside : SV_InsideTessFactor; float3 extra : TEXCOORD5; };

TriConst hs_tri_const(InputPatch<Cp, 3> patch, uint id : SV_PrimitiveID)
{
    TriConst c;
    c.edges[0] = tess.x; c.edges[1] = tess.y; c.edges[2] = tess.z; c.inside = tess.w;
    c.extra = patch[0].position + id;
    return c;
}

[domain("tri")]
[partitioning("integer")]
[outputtopology("triangle_cw")]
[outputcontrolpoints(3)]
[patchconstantfunc("hs_tri_const")]
[maxtessfactor(16.0)]
Cp hs_tri(InputPatch<Cp, 3> patch, uint i : SV_OutputControlPointID, uint prim : SV_PrimitiveID)
{
    Cp o = patch[i];
    o.uv += prim;
    return o;
}

[domain("tri")]
VsOut ds_tri(TriConst c, float3 bary : SV_DomainLocation, const OutputPatch<Cp, 3> patch)
{
    VsOut o = (VsOut)0;
    float3 p = patch[0].position * bary.x + patch[1].position * bary.y + patch[2].position * bary.z + c.extra;
    o.position = mul(float4(p, 1), transform);
    o.uv = patch[0].uv * bary.x;
    return o;
}

struct QuadConst { float edges[4] : SV_TessFactor; float inside[2] : SV_InsideTessFactor; };

QuadConst hs_quad_const(InputPatch<Cp, 4> patch)
{
    QuadConst c;
    [unroll] for (int k = 0; k < 4; k++) c.edges[k] = tess[k];
    c.inside[0] = tess.x; c.inside[1] = tess.y;
    return c;
}

[domain("quad")]
[partitioning("fractional_odd")]
[outputtopology("triangle_ccw")]
[outputcontrolpoints(4)]
[patchconstantfunc("hs_quad_const")]
Cp hs_quad(InputPatch<Cp, 4> patch, uint i : SV_OutputControlPointID) { return patch[i]; }

[domain("quad")]
float4 ds_quad(QuadConst c, float2 uv : SV_DomainLocation, const OutputPatch<Cp, 4> patch) : SV_Position
{
    float3 p = lerp(lerp(patch[0].position, patch[1].position, uv.x), lerp(patch[3].position, patch[2].position, uv.x), uv.y);
    return mul(float4(p, 1), transform);
}

struct LineConst { float edges[2] : SV_TessFactor; };

LineConst hs_line_const(InputPatch<Cp, 2> patch) { LineConst c; c.edges[0] = tess.x; c.edges[1] = tess.y; return c; }

[domain("isoline")]
[partitioning("pow2")]
[outputtopology("line")]
[outputcontrolpoints(2)]
[patchconstantfunc("hs_line_const")]
Cp hs_line(InputPatch<Cp, 2> patch, uint i : SV_OutputControlPointID) { return patch[i]; }

[domain("isoline")]
float4 ds_line(LineConst c, float2 uv : SV_DomainLocation, const OutputPatch<Cp, 2> patch) : SV_Position
{
    return float4(lerp(patch[0].position, patch[1].position, uv.x) + uv.y, 1);
}

// class linkage: an interface called through a function table (fcall)
interface ILight { float3 Shade(float3 n); };
class Ambient : ILight { float3 tint; float3 Shade(float3 n) { return tint; } };
class Directional : ILight { float3 dir; float3 Shade(float3 n) { return saturate(dot(n, dir)); } };
ILight light;

float4 ps_interface(VsOut i) : SV_Target
{
    return float4(light.Shade(i.normal), 1);
}
#endif
