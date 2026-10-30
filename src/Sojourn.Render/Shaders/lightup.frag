#version 460
// The half-size sun-light map (sun shadow, SSAO, projector shadows; Frame/Renderer.HalfLight.cs) brought back to the
// eye's size, depth-aware: each pixel blends the four nearest half-size texels by their bilinear weights, each weighed
// down by how far its depth (the full-size pixel it was lit at, the top left of its 2x2 block) lies from this pixel's,
// so shadow and occlusion don't bleed across silhouettes. Slots: pc.i3 = depth | half light map << 16.
// Written by Claude Opus 5.5, 2026-10-27.
#include "common.glsl"
#include "deferred.glsl"

layout(location = 0) in vec2 vary_fragcoord;
layout(location = 0) out vec4 frag_color;

float eyeZ(ivec2 px, vec2 full)
{
    float d = texelFetch(textures[nonuniformEXT(SLOT_DEPTH)], px, 0).r;
    return getPositionWithDepth((vec2(px) + 0.5) / full, max(d, 1e-7)).z;
}

void main()
{
    if (lensHidden()) discard;   // never seen (Frame/Renderer.Lens.cs)
    vec2 full = VIEW.viewport.xy;
    ivec2 px = ivec2(gl_FragCoord.xy);
    float d = texelFetch(textures[nonuniformEXT(SLOT_DEPTH)], px, 0).r;
    if (isSkyDepth(d)) { frag_color = vec4(1); return; }
    float z = getPositionWithDepth((vec2(px) + 0.5) / full, d).z;
    ivec2 hsize = textureSize(textures[nonuniformEXT(SLOT_LIGHTMAP)], 0);
    // half texel i was lit at full pixel 2i: this pixel lies between i0 and i0 + 1 at f (0 or 0.5)
    vec2 t = vec2(px) * 0.5;
    ivec2 i0 = ivec2(floor(t));
    vec2 f = t - vec2(i0);
    // what counts as the same surface, in metres: a floor seen at a grazing angle 50 m off changes ~4% in depth
    // over the two pixels between lit texels, a silhouette usually far more
    float tol = 0.04 * abs(z) + 0.02;
    vec4 sum = vec4(0);
    float wsum = 0.0, best = 1e9;
    vec4 nearest = vec4(1);
    for (int k = 0; k < 4; k++)
    {
        ivec2 o = ivec2(k & 1, k >> 1);
        ivec2 h = min(i0 + o, hsize - 1);
        float bw = (o.x == 1 ? f.x : 1.0 - f.x) * (o.y == 1 ? f.y : 1.0 - f.y);
        if (bw <= 0.0) continue;
        ivec2 at = min(h * 2, ivec2(full) - 1);
        float dz = abs(eyeZ(at, full) - z);
        vec4 c = texelFetch(textures[nonuniformEXT(SLOT_LIGHTMAP)], h, 0);
        if (dz < best) { best = dz; nearest = c; }
        float w = bw / (1.0 + (dz / tol) * (dz / tol) * 16.0);
        sum += c * w;
        wsum += w;
    }
    // nothing of the same surface near (a thin thing between texels): the closest in depth
    frag_color = wsum > 1e-3 ? sum / wsum : nearest;
}
