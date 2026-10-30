#version 460
// A headset quad layer drawn into the PC's mirror window (Realtime/LayerMirror.cs): its four corners already in the
// eye's clip space (top left, top right, bottom right, bottom left), so the picture is mapped in perspective as the
// compositor maps it; uv runs over the part of the picture image the layer shows (extent.xy); vQuad over the layer (0-1),
// for a menu's part's lines (clip, extent.z of them).
layout(push_constant) uniform Quad { vec4 corner[4]; vec4 extent; vec4 clip[3]; } q;

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec2 vQuad;

const int Corner[6] = int[](0, 1, 2, 0, 2, 3);
const vec2 Uv[4] = vec2[](vec2(0, 0), vec2(1, 0), vec2(1, 1), vec2(0, 1));

void main()
{
    int c = Corner[gl_VertexIndex];
    gl_Position = q.corner[c];
    vUv = Uv[c] * q.extent.xy;
    vQuad = Uv[c];
}
