// Shared setup for the three SMAA passes (viewer: SMAAEdgeDetect, SMAABlendWeights, SMAANeighborhoodBlend V/F).
// Includer defines SMAA_PASS (0 edges, 1 blend weights, 2 neighbourhood blending) and one SMAA_PRESET_*.
// Push block: v0 = (1/w, 1/h, w, h); i1..i3 = input texture slots (see Renderer.RecordSmaa).
#include "common.glsl"
#define SMAA_GLSL_4 1
#define SMAA_PREDICATION 0
#define SMAA_REPROJECTION 0
#define SMAA_RT_METRICS (pc.v0)
#ifdef VERTEX_SHADER
#define SMAA_INCLUDE_VS 1
#define SMAA_INCLUDE_PS 0
#else
#define SMAA_INCLUDE_VS 0
#define SMAA_INCLUDE_PS 1
#endif
#include "smaa.glsl"
#define SMAA_TEX(i) textures[int(i)]
