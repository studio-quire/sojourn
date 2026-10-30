#version 460
// HUD attachments, as the viewer draws them under IS_HUD (class1/deferred/fullbrightF.glsl and the HUD branches of
// pbropaqueF.glsl / pbralphaF.glsl, LGPL 2.1, Linden Research, Inc.): legacy faces are fullbright, the texture times
// its tint in sRGB with no sky, fog, shine or bump; PBR faces are base colour times its factor plus emissive, unlit.
// The viewer draws them after gamma, so it blends in sRGB: an image in UNORM keeps that (pc.i2 = 0, the desktop);
// pc.i2 = 1 writes linear light for an sRGB image (the headset's layer, which its compositor blends in linear).
// pc.i3 = 1: the blended pass (premultiplied over a transparent image); otherwise opaque, masks cut out.
#include "common.glsl"

layout(location = 0) in vec3 vWorldPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec4 vTangent;
layout(location = 3) in vec2 vUv;
layout(location = 4) flat in uint vInstance;
layout(location = 5) in vec3 vViewPos;
layout(location = 0) out vec4 frag_color;

void main()
{
    Material m = MATERIAL(INSTANCE(vInstance).material);
    bool linearOut = pc.i2 == 1u;
    vec4 color;
    if ((m.flags & MAT_PBR) != 0u)
    {
        vec4 basecolor = sampleTex(m.diffuse.index, khrTransform(vUv, m.diffuse), vec4(1));
        vec3 col = srgb_to_linear(basecolor.rgb) * m.color.rgb;
        col += m.specColor.rgb * srgb_to_linear(sampleTex(m.emissive.index, khrTransform(vUv, m.emissive), vec4(1)).rgb);
        color = vec4(linearOut ? col : linear_to_srgb(col), basecolor.a * m.color.a);
    }
    else
    {
        color = sampleTex(m.diffuse.index, teTransform(vUv, m.diffuse), vec4(1)) * m.color;
        if (linearOut) color.rgb = srgb_to_linear(clamp(color.rgb, 0.0, 1.0));
    }
    if (pc.i3 == 1u)
    {
        if (color.a < 0.004) discard;   // the alpha pool's minimum
    }
    else
    {
        if (MAT_ALPHA_MODE(m) == ALPHA_MASK && color.a < m.alphaCutoff) discard;
        color.a = 1.0;
    }
    frag_color = clamp(color, 0.0, 1.0);
}
