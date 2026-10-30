#version 460
// A headset quad layer in the mirror window (Realtime/LayerMirror.cs), premultiplied over the eye there.
//   (default) a menu's picture: gamma-encoded and premultiplied, as the overlay's are, with mips (the window shows it
//             far smaller than the lenses do); a slight bias keeps text crisp
//   HANDS     the hands' layer (Overlay.Hands.cs): linear and premultiplied in an sRGB image the eye's size, read
//             through its sRGB view, so it's encoded here; no mips, so four taps over the pixel's footprint
//   a menu's part (MenuClip, Realtime/LayerCut.cs): only the kept side of each of its lines (extent.z of them), over a
//             pixel of the window at each
layout(push_constant) uniform Quad { vec4 corner[4]; vec4 extent; vec4 clip[3]; } q;
layout(set = 0, binding = 0) uniform sampler2D picture;

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec2 vQuad;
layout(location = 0) out vec4 outColor;

void main()
{
#ifdef HANDS
    vec2 dx = dFdx(vUv) * 0.25, dy = dFdy(vUv) * 0.25;
    vec4 s = 0.25 * (texture(picture, vUv - dx - dy) + texture(picture, vUv + dx - dy)
                   + texture(picture, vUv - dx + dy) + texture(picture, vUv + dx + dy));
    if (s.a <= 0.002) discard;
    vec3 c = clamp(s.rgb / s.a, 0.0, 1.0);
    c = mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));   // linear to sRGB
    outColor = vec4(c * s.a, s.a);
#else
    float keep = 1.0;
    int n = int(q.extent.z + 0.5);
    for (int i = 0; i < n; i++)
    {
        float f = dot(q.clip[i].xy, vQuad) + q.clip[i].z;
        keep *= clamp(f / max(fwidth(f), 1e-6) + 0.5, 0.0, 1.0);
    }
    outColor = texture(picture, vUv, -0.5) * keep;
#endif
}
