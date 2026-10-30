#version 460
// Opaque / alpha-masked fullbright faces, drawn forward after the deferred resolve.
// Ported from the Second Life viewer's class1/deferred/fullbrightF.glsl and, for shiny faces, class3/deferred/
// fullbrightShinyF.glsl (LGPL 2.1, Linden Research, Inc.).
#include "common.glsl"
#include "probes.glsl"

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
    Material m = MATERIAL(INSTANCE(vInstance).material);
    vec4 color = sampleTex(m.diffuse.index, teTransform(vUv, m.diffuse), vec4(1));
    float final_alpha = color.a * m.color.a;
    if (MAT_ALPHA_MODE(m) == ALPHA_MASK && color.a < m.alphaCutoff) discard;
    color.rgb *= m.color.rgb;
    color.a = final_alpha;
    color.rgb = srgb_to_linear(color.rgb);
    if (MAT_SHINY(m) != 0u)
    {
        // fullbrightShinyF: environment only (no gloss), intensity = the shininess the vertex alpha carries
        vec3 norm = normalize(mat3(VIEW.view) * vNormal);
        float env = m.envIntensity;
        vec3 ambenv = vec3(1.0), glossenv = vec3(0.0), legacyenv = vec3(0.0);
        sampleReflectionProbesLegacy(ambenv, glossenv, legacyenv, vec2(0), vViewPos, norm, 0.0, env, false, vec3(0));
        applyLegacyEnv(color.rgb, legacyenv, vec4(0), vViewPos, norm, env);
        color.a = 1.0;
    }
    frag_color = max(color, vec4(0));
}
