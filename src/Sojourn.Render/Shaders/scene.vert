#version 460
#include "common.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec2 inUv;

layout(location = 0) out vec3 vWorldPos;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec4 vTangent;
layout(location = 3) out vec2 vUv;
layout(location = 4) flat out uint vInstance;
layout(location = 5) out vec3 vViewPos;
out float gl_ClipDistance[1];

void main()
{
    uint id = gl_InstanceIndex;
    Instance inst = INSTANCE(id);
    vec4 world = inst.model * vec4(inPosition, 1.0);
    // n0-n2 are the rows of System.Numerics' Transpose(Invert(model)), a row-vector matrix: as columns here they make
    // the column-vector inverse transpose itself (transposed again, every turned prim's normals turned the other way:
    // a box turned 90 degrees lit as if it faced the opposite side, found 2026-11-11 by a light at the camera)
    mat3 nm = mat3(inst.n0.xyz, inst.n1.xyz, inst.n2.xyz);
    vNormal = normalize(nm * inNormal);
    vTangent = vec4(normalize(mat3(inst.model) * inTangent.xyz), inTangent.w);
    vWorldPos = world.xyz;
    vUv = inUv;
    vInstance = id;
    vec4 vp = VIEW.view * world;
    vViewPos = vp.xyz;
    gl_Position = VIEW.proj * vp;
    gl_ClipDistance[0] = viewClip(world.xyz, inst.flags);
}
