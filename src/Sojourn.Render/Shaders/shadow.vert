#version 460
// Shadow casters into one map (pc.i0 = sun cascade 0-3, projector shadow 4-5, or the far sun cascade 6). Mirrors the viewer's class1/deferred/shadowV.glsl
// (LGPL 2.1, Linden Research, Inc.): position only, plus UVs for alpha-masked casters.
#include "common.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec2 inUv;
layout(location = 0) out vec2 vUv;
layout(location = 1) flat out uint vInstance;

void main()
{
    Instance inst = INSTANCE(gl_InstanceIndex);
    vec4 world = inst.model * vec4(inPosition, 1.0);
    mat4 vp = pc.i0 == 0u ? SKY.shadow_viewproj0 : pc.i0 == 1u ? SKY.shadow_viewproj1 : pc.i0 == 2u ? SKY.shadow_viewproj2
            : pc.i0 == 3u ? SKY.shadow_viewproj3 : pc.i0 == 4u ? SKY.shadow_viewproj4 : pc.i0 == 5u ? SKY.shadow_viewproj5
            : SKY.shadow_viewproj6;
    gl_Position = vp * world;
    vUv = inUv;
    vInstance = gl_InstanceIndex;
}
