// An avatar impostor's box (Frame/Renderer.Impostors.cs): its instance's model rows are the box's half axes (right,
// up, toward the camera at capture) and its centre. Drawn back faces only, so it covers the box wherever it is on screen.
#include "common.glsl"

layout(location = 0) out vec3 vWorldPos;
layout(location = 1) flat out uint vInstance;

// a unit cube's corners (bit 0 x, bit 1 y, bit 2 z) and its faces wound counter-clockwise from outside
const int Faces[36] = int[36](1, 3, 7, 1, 7, 5,  0, 4, 6, 0, 6, 2,  2, 6, 7, 2, 7, 3,  0, 1, 5, 0, 5, 4,  4, 5, 7, 4, 7, 6,  0, 2, 3, 0, 3, 1);

void main()
{
    Instance inst = INSTANCE(gl_InstanceIndex);
    int k = Faces[gl_VertexIndex];
    vec3 p = vec3((k & 1) != 0 ? 1.0 : -1.0, (k & 2) != 0 ? 1.0 : -1.0, (k & 4) != 0 ? 1.0 : -1.0);
    vec3 world = inst.model[3].xyz + p.x * inst.model[0].xyz + p.y * inst.model[1].xyz + p.z * inst.model[2].xyz;
    vWorldPos = world;
    vInstance = gl_InstanceIndex;
    gl_Position = VIEW.proj * (VIEW.view * vec4(world, 1.0));
}
