#version 460
// The water surface seen from below (TRANSPARENT_WATER). Ported from the Second Life viewer's
// class3/environment/underWaterF.glsl (LGPL 2.1, Linden Research, Inc.), drawn with water.vert as the viewer's
// gUnderWaterProgram is with waterV.glsl. Push block as water.frag's: i1 = bumpMap (low half), i2 = screenTex (low
// half), i3 = exclusionTex.
#include "common.glsl"
#include "atmospherics.glsl"
#include "water_mask.glsl"

layout(location = 0) in vec4 refCoord;
layout(location = 1) in vec4 littleWave;
layout(location = 2) in vec4 view;
layout(location = 3) in vec3 vary_position;
layout(location = 0) out vec4 frag_color;

vec4 bump(int slot, vec2 uv)
{
    return slot >= 0 && slot < 0xFFFF ? texture(textures[nonuniformEXT(slot)], uv) : vec4(0.5, 0.5, 1.0, 1.0);
}

void main()
{
    if (lensHidden()) discard;   // never seen (Frame/Renderer.Lens.cs)
    if (length(vary_position.xyz) > GL_FAR_CLIP) discard;   // a sphere about the eye, not the far plane
    vec2 screen_tc = (refCoord.xy/refCoord.z) * 0.5 + 0.5;
    float water_mask = waterExclusionMask(int(pc.i3 & 0xFFFFu), screen_tc);

    //get detail normals
    int bumpMap = int(pc.i1 & 0xFFFFu);
    vec3 wave1 = bump(bumpMap, vec2(refCoord.w, view.w)).xyz*2.0-1.0;
    vec3 wave2 = bump(bumpMap, littleWave.xy).xyz*2.0-1.0;
    vec3 wave3 = bump(bumpMap, littleWave.zw).xyz*2.0-1.0;
    vec3 wavef = normalize(wave1+wave2+wave3);

    //figure out distortion vector (ripply)
    vec2 distort = screen_tc;
    distort = mix(distort, distort+wavef.xy*SKY.refScale, water_mask);

    vec4 fb = texture(textures[nonuniformEXT(int(pc.i2 & 0xFFFFu))], distort);

    fb = applyWaterFogViewLinearNoClip(vary_position, fb);

    frag_color = max(fb, vec4(0));
}
