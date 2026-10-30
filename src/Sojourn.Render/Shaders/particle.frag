#version 460
// Particles in the alpha pass. Lit particles take class2/deferred/alphaF.glsl with the vertex colour and the normal
// the viewer gives them (facing the camera: -LLViewerCamera X axis); emissive ones class1/deferred/fullbrightF.glsl
// (IS_ALPHA, HAS_ALPHA_MASK). Ported from the Second Life viewer (LGPL 2.1, Linden Research, Inc.).
#include "common.glsl"
#include "atmospherics.glsl"
#include "probes.glsl"
#include "shadows.glsl"
#include "alpha_lit.glsl"

layout(location = 0) in vec3 vViewPos;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec4 vColor;
layout(location = 3) in float vGlow;
layout(location = 4) flat in uint vFlags;
layout(location = 5) flat in int vTex;
layout(location = 0) out vec4 frag_color;

#define PART_EMISSIVE 1u
#define PART_NO_MIN_ALPHA 2u

void main()
{
    if (lensHidden()) discard;   // never seen (Frame/Renderer.Lens.cs)
    vec4 tex = sampleTex(vTex, vUv, vec4(1));
    // MINIMUM_ALPHA, or 0 for a custom blend that may need "invisible" fragments (LLDrawPoolAlpha::renderAlpha)
    float minimum_alpha = (vFlags & PART_NO_MIN_ALPHA) != 0u ? 0.0 : 0.004;
    vec3 pos = vViewPos;
    if ((vFlags & PART_EMISSIVE) != 0u)
    {
        if (tex.a < minimum_alpha) discard;
        vec4 color = tex;
        float final_alpha = color.a * vColor.a;
        color.rgb *= vColor.rgb;
        color.a = final_alpha;
        color.rgb = srgb_to_linear(color.rgb);
        vec3 sunlit, amblit, additive, atten;
        calcAtmosphericVars(pos, vec3(0), 1.0, sunlit, amblit, additive, atten);
        color.rgb = applySkyAndWaterFog(pos, additive, atten, color).rgb;
        frag_color = max(color, vec4(0));
        return;
    }
    float final_alpha = tex.a * vColor.a;
    if (final_alpha < minimum_alpha) discard;
    vec3 diffuse_linear = srgb_to_linear(tex.rgb * vColor.rgb);
    vec3 norm = vec3(0.0, 0.0, 1.0);
    float shadow = sampleDirectionalShadow(pos, norm, gl_FragCoord.xy * VIEW.viewport.zw);
    frag_color = alphaLitLegacy(vec4(diffuse_linear, final_alpha), pos, norm, shadow);
}
