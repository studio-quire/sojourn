#version 460
// Property and ban lines' triangles (World/ParcelLines.cs), pulled from the frame arena (pc.instRef), over the finished
// image (Frame/Renderer.ParcelLines.cs).
#include "common.glsl"

struct LineVertex {
    vec3 pos;
    vec2 uv;
    uint color;   // rgba8
    int tex;
};
layout(buffer_reference, scalar) readonly buffer LineRef { LineVertex v[]; };

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vColor;
layout(location = 2) flat out int vTex;

void main()
{
    LineVertex lv = LineRef(uint64_t(pc.instRef)).v[gl_VertexIndex];
    vUv = lv.uv;
    vColor = unpackUnorm4x8(lv.color);
    vTex = lv.tex;
    gl_Position = VIEW.proj * (VIEW.view * vec4(lv.pos, 1.0));
}
