#version 460
#include "smaa_common.glsl"

layout(location = 0) out vec2 vary_texcoord0;
layout(location = 1) out vec2 vary_pixcoord;
layout(location = 2) out vec4 vary_offset[3];

void main()
{
    vec2 uv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
    vary_texcoord0 = uv;
    vary_pixcoord = vec2(0);
#if SMAA_PASS == 0
    SMAAEdgeDetectionVS(uv, vary_offset);
#elif SMAA_PASS == 1
    SMAABlendingWeightCalculationVS(uv, vary_pixcoord, vary_offset);
#else
    SMAANeighborhoodBlendingVS(uv, vary_offset[0]);
    vary_offset[1] = vec4(0);
    vary_offset[2] = vec4(0);
#endif
}
