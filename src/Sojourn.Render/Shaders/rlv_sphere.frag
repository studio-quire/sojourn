#version 460
// RLV's vision spheres (@setsphere): every pixel changed by how far what it shows is from the sphere's origin, in each
// eye from that eye's own depth and view, so the sphere is one surface in the world for both. After Firestorm's RLVa
// rlvF.glsl (LGPL 2.1, Kitty Barnett); Frame/Renderer.Spheres.cs runs it, docs/rlv.md says what each mode is.
// pc.i1 = source slot; pc.i3 = depth slot | mode << 16 | dist extend << 20 | blur pass << 24;
// pc.v0.xyz = the origin (region space); pc.v1 = (value min, dist min, value max, dist max);
// pc.i0, pc.i2 = the effect's params (colour for blend) as half floats (xy, zw).
#include "common.glsl"
#include "deferred.glsl"

layout(location = 0) in vec2 vary_fragcoord;
layout(location = 0) out vec4 frag_color;

#define SRC textures[nonuniformEXT(int(pc.i1 & 0xFFFFu))]

vec3 blur13(vec2 tc, vec2 direction, vec2 res)
{
    vec4 color = vec4(0.0);
    vec2 off1 = vec2(1.411764705882353) * direction;
    vec2 off2 = vec2(3.2941176470588234) * direction;
    vec2 off3 = vec2(5.176470588235294) * direction;
    color += texture(SRC, tc) * 0.1964825501511404;
    color += texture(SRC, tc + off1 / res) * 0.2969069646728344;
    color += texture(SRC, tc - off1 / res) * 0.2969069646728344;
    color += texture(SRC, tc + off2 / res) * 0.09447039785044732;
    color += texture(SRC, tc - off2 / res) * 0.09447039785044732;
    color += texture(SRC, tc + off3 / res) * 0.010381362401148057;
    color += texture(SRC, tc - off3 / res) * 0.010381362401148057;
    return color.xyz;
}

vec3 blurVariable(vec2 tc, float kernelSize, vec2 direction, float strength, vec2 res)
{
    const float pi = 3.14159265;
    float side = min(floor(kernelSize / 2.0), 32.0);
    strength = max(strength, 0.0001);
    vec3 g;
    g.x = 1.0 / (sqrt(2.0 * pi) * strength);
    g.y = exp(-0.5 / (strength * strength));
    g.z = g.y * g.y;
    vec4 sum = texture(SRC, tc) * g.x;
    float weight = g.x;
    g.xy *= g.yz;
    for (float i = 1.0; i <= side; i++)
    {
        sum += texture(SRC, tc - i * direction / res) * g.x;
        sum += texture(SRC, tc + i * direction / res) * g.x;
        weight += 2.0 * g.x;
        g.xy *= g.yz;
    }
    return (sum / weight).rgb;
}

void main()
{
    vec2 tc = vary_fragcoord.xy;
    vec2 res = VIEW.viewport.xy;
    uint mode = (pc.i3 >> 16) & 0xFu;
    uint extend = (pc.i3 >> 20) & 0xFu;
    vec2 dir = ((pc.i3 >> 24) & 1u) == 0u ? vec2(1.0, 0.0) : vec2(0.0, 1.0);
    vec4 params = vec4(unpackHalf2x16(pc.i0), unpackHalf2x16(pc.i2));

    float depth = getDepth(tc);
    float distance;
    if (isSkyDepth(depth)) distance = 1.0e9;
    else
    {
        vec3 pos = getPositionWithDepth(tc, depth).xyz;
        vec3 origin = (VIEW.view * vec4(pc.v0.xyz, 1.0)).xyz;
        distance = length(pos - origin);
    }

    float valueMin = pc.v1.x, distMin = pc.v1.y, valueMax = pc.v1.z, distMax = max(pc.v1.w, pc.v1.y);
    // linear from min to max between the distances; below min and beyond max the value holds only where it extends
    // (extend: 1 beyond max, 2 below min, as RLVa's ESphereDistExtend)
    float t = distMax > distMin ? clamp((distance - distMin) / (distMax - distMin), 0.0, 1.0) : (distance >= distMax ? 1.0 : 0.0);
    float strength = mix(valueMin, valueMax, t);
    if (distance < distMin) strength = (extend & 2u) != 0u ? valueMin : 0.0;
    if (distance > distMax) strength = (extend & 1u) != 0u ? valueMax : 0.0;

    vec3 color = texture(SRC, tc).rgb;
    if (mode == 0u)
        color = mix(color, params.rgb, clamp(strength, 0.0, 1.0));
    else if (mode == 1u)
        color = blur13(tc, dir * strength, res);
    else if (mode == 2u)
        color = strength > 0.0 ? blurVariable(tc, params.x, dir, strength, res) : color;
    else if (mode == 3u)
    {
        vec3 drift = vec3(texture(SRC, tc + params.xy / res).r, color.g, texture(SRC, tc + params.zw / res).b);
        color = mix(color, drift, clamp(strength, 0.0, 1.0));
    }
    else if (mode == 4u)
    {
        float s = sign(strength);
        vec2 px = vec2(max(1.0, round(params.x * s)), max(1.0, round(params.y * s))) / res;
        color = texture(SRC, px * floor(tc / px)).rgb;
    }
    frag_color = vec4(color, texture(SRC, tc).a);
}
