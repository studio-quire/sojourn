#version 460
// Glow mask: additive into the scene's alpha channel only. Legacy faces: class1/deferred/emissiveF.glsl
// (diffuse alpha * glow); GLTF faces: class1/deferred/pbrglowF.glsl (brightest emissive channel * glow).
// Ported from the Second Life viewer (LGPL 2.1, Linden Research, Inc.).
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
    if (lensHidden()) discard;   // never seen (Frame/Renderer.Lens.cs)
    Instance inst = INSTANCE(vInstance);
    Material m = MATERIAL(inst.material);
    float a;
    if ((m.flags & MAT_PBR) != 0u)
    {
        float basecolor_a = sampleTex(m.diffuse.index, khrTransform(vUv, m.diffuse), vec4(1)).a * m.color.a;
        if (MAT_ALPHA_MODE(m) == ALPHA_MASK && basecolor_a < m.alphaCutoff) discard;
        vec3 emissive = m.specColor.rgb * srgb_to_linear(sampleTex(m.emissive.index, khrTransform(vUv, m.emissive), vec4(1)).rgb);
        a = max(max(emissive.r, emissive.g), emissive.b) * inst.glow;
    }
    else
        a = sampleTex(m.diffuse.index, teTransform(vUv, m.diffuse), vec4(1)).a * inst.glow;
    frag_color = max(vec4(0, 0, 0, a), vec4(0));
}
