#version 460
// The hands' cut (Realtime/Overlay.Hands.cs), one full-target pass per variant:
//   (none)   the mask: 1 where the person's own body (marked in the G-buffer), or with CUT.size.w anything, shows over
//            the menus
//   MASKOUT  the frost's backdrop (half size) with the mask's pixels taken out: colour and alpha scaled by what's left,
//            so the frost's blur averages only what's behind the hands, not the hands themselves
//   HANDS    the headset's hand layer: the finished eye where the mask is, premultiplied by it, into an sRGB swapchain
//            image (written linear, encoded by the attachment), clear elsewhere
#include "gbuffer_util.glsl"
#include "handcut.glsl"

layout(set = 0, binding = 1) uniform sampler2D depthTex;
layout(set = 0, binding = 2) uniform sampler2D normalTex;
layout(set = 0, binding = 3) uniform sampler2D maskTex;
layout(set = 0, binding = 4) uniform sampler2D eyeTex;   // the eye's finished image (gamma-encoded RGBA8)

layout(location = 0) out vec4 outColor;

void main()
{
#if defined(MASKOUT)
    // the 2x2 mask pixels under this backdrop pixel, averaged by the linear sampler
    float m = texture(maskTex, gl_FragCoord.xy * 2.0 / vec2(textureSize(maskTex, 0))).r;
    outColor = vec4(0.0, 0.0, 0.0, 1.0 - m);   // blended: colour * (1 - m), alpha = 1 - m
#elif defined(HANDS)
    ivec2 px = ivec2(gl_FragCoord.xy);
    float m = texelFetch(maskTex, px, 0).r;
    if (m <= 0.0) { outColor = vec4(0.0); return; }
    vec3 c = texelFetch(eyeTex, px, 0).rgb;
    c = mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));   // sRGB to linear, exactly
    outColor = vec4(c * m, m);
#else
    ivec2 px = ivec2(gl_FragCoord.xy);
    if (CUT.size.w < 0.5 && !GBUFFER_IS_SELF(gbNormIn(texelFetch(normalTex, px, 0)).w)) { outColor = vec4(0.0); return; }
    float m = cutShows(gl_FragCoord.xy, texelFetch(depthTex, px, 0).r);
    if (m < 0.5 && CUT.size.w >= 0.5)
    {
        // Walls hiding menus: pixels the cut misses inside what covers a menu let the menu through there. Cloth and hair
        // alpha-tested at the eye's resolution are full of pixel-sized holes (and blended faces leave the depth of what's
        // behind them), so over a dancer's dark cloak the menu's bright rim and text came through as torn, aliased
        // pixels along its edges (the session of 2026-10-28, seen in Meta's own mirror). A pixel goes with its
        // neighbours when most of them show (5 of 8): holes a pixel or two wide close, a straight silhouette doesn't
        // move (3 of 8 beside it).
        // Hair runs in strands, and the gaps between them are often two pixels wide, which 5 of 8 doesn't close (3 of
        // 8 show): a gap along a menu's side edge showed a long sliver of its rim. So a gap up to two pixels across is
        // closed too, along a row or a column, when what covers shows on both sides of it within two pixels; a
        // straight silhouette has one side only, so it still doesn't move.
        ivec2 last = ivec2(CUT.size.xy) - 1;
        int shown = 0;
        bool l = false, r = false, d = false, u = false;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                ivec2 q = clamp(px + ivec2(dx, dy), ivec2(0), last);
                if (cutShows(vec2(q) + 0.5, texelFetch(depthTex, q, 0).r) > 0.5)
                {
                    shown++;
                    if (dy == 0) { l = l || dx < 0; r = r || dx > 0; }
                    if (dx == 0) { d = d || dy < 0; u = u || dy > 0; }
                }
            }
        if (shown >= 5 || (l && r) || (d && u)) m = 1.0;
        else if (l || d || r || u)
        {
            // shown on one side along an axis: a two-pixel gap if it shows again two away on the other
            ivec2 q;
            if (l != r)
            {
                q = clamp(px + ivec2(l ? 2 : -2, 0), ivec2(0), last);
                if (cutShows(vec2(q) + 0.5, texelFetch(depthTex, q, 0).r) > 0.5) m = 1.0;
            }
            if (m < 0.5 && d != u)
            {
                q = clamp(px + ivec2(0, d ? 2 : -2), ivec2(0), last);
                if (cutShows(vec2(q) + 0.5, texelFetch(depthTex, q, 0).r) > 0.5) m = 1.0;
            }
        }
    }
    outColor = vec4(m);
#endif
}
