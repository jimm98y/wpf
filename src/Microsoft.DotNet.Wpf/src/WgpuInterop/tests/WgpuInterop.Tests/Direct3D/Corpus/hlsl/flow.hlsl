// The DXBC translator's own corpus: control flow.

cbuffer Params : register(b0)
{
    float4 f4;
    int4 i4;
    uint count;
    uint selector;
};

// if/else, loop with break/continue/breakc/continuec, nested loops
float4 ps_loops(float4 pos : SV_Position, float4 a : TEXCOORD0) : SV_Target
{
    float4 r = 0;
    [loop] for (uint i = 0; i < count; i++)
    {
        if (i == 3) continue;
        if (r.x > 10) break;
        [loop] for (uint j = 0; j < i; j++)
        {
            r += a * j;
            if (r.y > f4.y) break;
        }
        if (a.x > 0.5) r += 1; else r -= 0.5;
    }
    uint k = 0;
    [loop] while (k < 4) { r.z += k; k++; }
    [loop] do { r.w -= 1; } while (r.w > f4.w);
    return r;
}

// switch with cases, a case with two labels, and default
float4 ps_switch(float4 pos : SV_Position, float4 a : TEXCOORD0) : SV_Target
{
    float4 r = a;
    [forcecase] switch (selector)
    {
        case 0: r *= 2; break;
        case 1: r += 1; break;
        case 2: r -= 3; break;
        case 7: case 9: r = r.wzyx; break;
        default: r = 0; break;
    }
    return r;
}

// discard, return early (retc), and a call to a function that is not inlined ([call])
float4 helper(float4 v) { return v * v + f4; }

float4 ps_discard(float4 pos : SV_Position, float4 a : TEXCOORD0) : SV_Target
{
    if (a.x < 0.1) discard;
    clip(a.y - 0.2);
    if (a.z > 0.9) return float4(1, 0, 0, 1);
    [call] if (a.w > 0.5) return helper(a);
    return a;
}

// else, continuec and retc kept as branches ([branch] stops fxc turning them into selects)
float4 ps_branches(float4 pos : SV_Position, float4 a : TEXCOORD0) : SV_Target
{
    float4 r = a;
    [branch] if (a.x > f4.x) { r = sqrt(abs(a)) * f4; } else { r = a.wzyx * f4.y; }
    [loop] for (uint i = 0; i < count; i++)
    {
        [branch] if (r.x > (float)i) continue;
        r += i;
    }
    [branch] if (r.y < 0) return r;
    return r * 2;
}
