#version 460
// A headset menu's part (Realtime/LayerCut.cs): blended as dst * alpha, so the picture keeps what's on the kept side of
// every line and loses the rest, over a pixel at each line. Each line is in the image's pixels, scaled to give the
// signed distance in pixels (MenuClip.InPixels): kept where it's >= 0.
layout(push_constant) uniform Cut { vec4 plane[3]; vec4 info; } k;

layout(location = 0) out vec4 outColor;

void main()
{
    float keep = 1.0;
    int n = int(k.info.x + 0.5);
    for (int i = 0; i < n; i++) keep *= clamp(dot(k.plane[i].xy, gl_FragCoord.xy) + k.plane[i].z + 0.5, 0.0, 1.0);
    outColor = vec4(0.0, 0.0, 0.0, keep);
}
