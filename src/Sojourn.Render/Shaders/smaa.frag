#version 460
#include "smaa_common.glsl"

layout(location = 0) in vec2 vary_texcoord0;
layout(location = 1) in vec2 vary_pixcoord;
layout(location = 2) in vec4 vary_offset[3];
layout(location = 0) out vec4 frag_color;

void main()
{
#if SMAA_PASS == 0
    // no edges in the lenses' masked area or along its border (Frame/Renderer.Lens.cs): it's black, never seen, and its
    // border would be an edge the whole way round
    if (VIEW.lensMask != 0u)
    {
        ivec2 p = ivec2(gl_FragCoord.xy);
        int m = int(VIEW.lensMask - 1u);
        if (texelFetch(textures[nonuniformEXT(m)], p, 0).r > 0.5 || texelFetch(textures[nonuniformEXT(m)], max(p - ivec2(1, 0), 0), 0).r > 0.5
            || texelFetch(textures[nonuniformEXT(m)], max(p - ivec2(0, 1), 0), 0).r > 0.5) discard;
    }
    // SMAAEdgeDetectF: colour edges on the gamma-encoded image
    frag_color = vec4(SMAAColorEdgeDetectionPS(vary_texcoord0, vary_offset, SMAA_TEX(pc.i1)), 0.0, 0.0);
#elif SMAA_PASS == 1
    frag_color = SMAABlendingWeightCalculationPS(vary_texcoord0, vary_pixcoord, vary_offset,
        SMAA_TEX(pc.i1), SMAA_TEX(pc.i2), SMAA_TEX(pc.i3), vec4(0.0));
#else
    frag_color = SMAANeighborhoodBlendingPS(vary_texcoord0, vary_offset[0], SMAA_TEX(pc.i1), SMAA_TEX(pc.i2));
#endif
}
