#version 460
// The lenses' hidden area into an eye's mask (Frame/Renderer.Lens.cs): the runtime's hidden-triangle mesh, its
// vertices on the view's z = -1 plane (XR_KHR_visibility_mask), scaled out from the eye's axis by pc.v0.x so its edge
// stays a margin inside the hidden area. pc.instRef: the vertices (vec2), a triangle list.
#include "common.glsl"

layout(buffer_reference, scalar) readonly buffer LensVerts { vec2 v[]; };

void main()
{
    vec2 t = LensVerts(uint64_t(pc.instRef)).v[gl_VertexIndex] * pc.v0.x;
    gl_Position = VIEW.proj * vec4(t, -1.0, 1.0);
}
