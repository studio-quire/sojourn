#version 460
// scene.vert for blended faces lit more simply (the person's setting; Frame/Renderer.Layers.cs): the light that varies
// slowly over a surface is computed here, per vertex, and interpolated: the ambient (with the reflection probes'), the
// atmospherics (sunlight, haze, its fading), and the local lights. alpha.frag (SIMPLE_LIGHT) keeps the texture, the
// sun's angle, its shadow and reflections per pixel. Back faces of double-sided ones take their front's ambient.
// Claude Opus 5.5, 2026-10-27.
#include "common.glsl"
#include "gbuffer_util.glsl"
#include "atmospherics.glsl"
#include "probes.glsl"
#include "alpha_lit.glsl"

#ifndef INSTANCE
#define INSTANCE(id) (INSTANCES[id])
#endif

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
layout(location = 6) out vec3 vAmbient;
layout(location = 7) out vec3 vSunlit;
layout(location = 8) out vec3 vAdditive;
layout(location = 9) out vec3 vAtten;
layout(location = 10) out vec3 vLocal;
out float gl_ClipDistance[1];

void main()
{
    // as scene.vert (the same position: alpha_count.frag's layers are drawn with it)
    uint id = gl_InstanceIndex;
    Instance inst = INSTANCE(id);
    vec4 world = inst.model * vec4(inPosition, 1.0);
    mat3 nm = mat3(inst.n0.xyz, inst.n1.xyz, inst.n2.xyz);
    vNormal = normalize(transpose(nm) * inNormal);
    vTangent = vec4(normalize(mat3(inst.model) * inTangent.xyz), inTangent.w);
    vWorldPos = world.xyz;
    vUv = inUv;
    vInstance = id;
    vec4 vp = VIEW.view * world;
    vViewPos = vp.xyz;
    gl_Position = VIEW.proj * vp;
    gl_ClipDistance[0] = viewClip(world.xyz, inst.flags);

    vec3 n = normalize(mat3(VIEW.view) * vNormal);
    vec3 light_dir = (SKY.sun_up_factor == 1) ? SKY.sun_dir : SKY.moon_dir;
    vec3 amblit;
    calcAtmosphericVarsLinear(vp.xyz, n, light_dir, vSunlit, amblit, vAdditive, vAtten);
    vAmbient = sampleAmbient(world.xyz, vNormal, amblit);
    vLocal = localLights(vec3(1.0), vp.xyz, n);
}
