#version 460
// Particle quads, pulled from the frame arena (Particles/ParticleBuilder.cs): six vertices per quad, the viewer's
// two triangles (0,1,2) (1,3,2) over its four corners (LLParticlePartition::rebuildGeom).
#include "common.glsl"

struct ParticleVertex {
    vec3 pos;
    vec2 uv;
    uint color;      // rgba8
    uint glowFlags;  // glow | flags << 8
    int tex;
};
layout(buffer_reference, scalar) readonly buffer ParticleRef { ParticleVertex v[]; };

layout(location = 0) out vec3 vViewPos;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec4 vColor;
layout(location = 3) out float vGlow;
layout(location = 4) flat out uint vFlags;
layout(location = 5) flat out int vTex;
out float gl_ClipDistance[1];

const uint corner[6] = uint[](0u, 1u, 2u, 1u, 3u, 2u);

void main()
{
    uint quad = uint(gl_VertexIndex) / 6u;
    uint c = corner[uint(gl_VertexIndex) % 6u];
    ParticleVertex pv = ParticleRef(uint64_t(pc.instRef)).v[quad * 4u + c];
    vec4 vp = VIEW.view * vec4(pv.pos, 1.0);
    vViewPos = vp.xyz;
    vUv = pv.uv;
    vColor = unpackUnorm4x8(pv.color);
    vGlow = float(pv.glowFlags & 255u) / 255.0;
    vFlags = pv.glowFlags >> 8;
    vTex = pv.tex;
    gl_Position = VIEW.proj * vp;
    gl_ClipDistance[0] = viewClip(pv.pos, 0u);   // a mirror's reflection: nothing behind its plane
}
