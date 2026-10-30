#version 460
// A HUD image's quad: four clip positions (top left, top right, bottom right, bottom left) at the push block's first
// address, as two triangles; perspective-correct for a quad placed in the world (the headset preview).
#include "common.glsl"

layout(buffer_reference, scalar) readonly buffer CornersRef { vec4 c[4]; };
layout(location = 0) out vec2 vUv;

void main()
{
    const int order[6] = int[6](0, 1, 2, 0, 2, 3);
    const vec2 uvs[4] = vec2[4](vec2(0, 0), vec2(1, 0), vec2(1, 1), vec2(0, 1));
    int k = order[gl_VertexIndex];
    CornersRef corners = CornersRef(pc.viewRef);
    vUv = uvs[k];
    gl_Position = corners.c[k];
}
