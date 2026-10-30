#version 460
// Sojourn's overlay (Realtime/Overlay.cs): quads already in normalised device coordinates, drawn over a finished eye
// image. The scene vertex layout is reused: position = NDC (z: a world label's depth, else 0), normal = colour,
// tangent = (alpha, style, clip w or 0, 0), uv. A sheet at world corners carries each corner's clip w, multiplied back
// here so its picture is interpolated in perspective (Overlay.QuadW); 0 means flat (w 1).
layout(location = 0) in vec3 inPos;
layout(location = 1) in vec3 inColor;
layout(location = 2) in vec4 inExtra;
layout(location = 3) in vec2 inUv;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) flat out int vStyle;

void main()
{
    float w = inExtra.z > 0.0 ? inExtra.z : 1.0;
    gl_Position = vec4(inPos.xy, inPos.z > 0.0 ? min(inPos.z, 1.0) : 0.5, 1.0) * w;   // world labels (and held things) carry their depth (reverse-Z)
    vColor = vec4(inColor, inExtra.x);
    vUv = inUv;
    vStyle = int(inExtra.y + 0.5);
}
