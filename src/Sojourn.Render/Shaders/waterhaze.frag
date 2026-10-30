#version 460
// Water fog over the scene below the water surface, blend ONE / SRC_ALPHA. Ported from the Second Life viewer's
// class3/deferred/waterHazeF.glsl (LGPL 2.1, Linden Research, Inc.). Camera above water: drawn with the water plane,
// and not where the water exclusion mask hides the water. UNDER_WATER (camera below it): over the whole screen
// (doWaterHaze's full-screen blit), the mask unused, as the viewer's. pc.i3 = scene depth copy | exclusionTex << 16.
#include "common.glsl"
#include "deferred.glsl"
#include "atmospherics.glsl"
#include "water_mask.glsl"

#ifdef UNDER_WATER
layout(location = 0) in vec2 vary_fragcoord;
#else
layout(location = 0) in vec3 vWorldPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec4 vTangent;
layout(location = 3) in vec2 vUv;
layout(location = 4) flat in uint vInstance;
layout(location = 5) in vec3 vViewPos;
#endif
layout(location = 0) out vec4 frag_color;

void main()
{
    if (lensHidden()) discard;   // never seen (Frame/Renderer.Lens.cs)
    vec2 tc = gl_FragCoord.xy * VIEW.viewport.zw;
    float depth = getDepth(tc);
#ifndef UNDER_WATER
    if (length(vViewPos) > GL_FAR_CLIP) discard;   // a sphere about the eye, not the far plane
    // Just discard if we're in the exclusion mask (the viewer's comment: its old invisiprim hack also raised the fog's
    // density, which would cost more in the mask).
    if (waterExclusionMask(int(pc.i3 >> 16), tc) < 1.0) discard;
    // (the viewer depth-tests by hand here; our hardware depth test against the scene does the same)
#endif
    vec4 pos = getPositionWithDepth(tc, max(depth, 1e-7));
    vec4 fogged = getWaterFogView(pos.xyz);
    fogged.a = max(pow(fogged.a, 1.7), 0);
    frag_color = max(fogged, vec4(0));
}
