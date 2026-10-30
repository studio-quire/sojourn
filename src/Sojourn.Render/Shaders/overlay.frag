#version 460
// Styles: 0 solid, 1 the panel texture, 2 a beam (soft across its width), 3 a dot (soft disc), 4 frost: the world
// behind the panel blurred (backdrop: the eye image downsampled into mips), masked by the panel's own alpha; a token
// (OverlayGlyph): 5 its disc, 6 a microphone, 7 a muted microphone, 8 its level ring; 9 the labels atlas (name tags),
// 10 the feed (chat), 11 frost masked by the feed's alpha, 12 the assistant's elements (their atlas), 13 frost masked by
// that atlas's alpha; 14 the HUD token's glyph (a screen with two HUDs on it); 15 a page shown up close (media), 16 frost
// masked by its alpha; 20-23 more tokens' glyphs; 24 the reader, 25 frost masked by its alpha; 26 the client's boards
// (their atlas: the map board, the location line), 27 frost masked by that atlas's alpha; 28 the camera token's glyph;
// 29 the handheld camera's live preview (its finished image, opaque: that image's alpha is the glow mask), 30 the
// preview's footprint, nothing in the eye but whole in the hands' mask (HELD_CUT).
// Premultiplied alpha. For frost the colour carries (target width, target height, mip level) and alpha the panel
// alpha where the frost reaches full; a negative width says the backdrop has the hands taken out (its alpha is what's
// left of each pixel, Overlay.Hands.cs), so the blur is divided by it.
// handMask: where the person's hands show over the headset's menus (Overlay.Hands.cs; a 1x1 zero without them): the
// frost and the menus' previews leave those pixels alone. HELD_CUT: the hands' held things (the pointer's beam, the
// wrist's tokens) drawn into that mask instead, where they're in front of the menus. CARVE: depth only, the menus'
// footprints for the compositor (Overlay.Walls.cs).
layout(set = 0, binding = 0) uniform sampler2D panel;
layout(set = 0, binding = 1) uniform sampler2D backdrop;
layout(set = 0, binding = 2) uniform sampler2D labels;
layout(set = 0, binding = 3) uniform sampler2D feed;
layout(set = 0, binding = 4) uniform sampler2D shown;
layout(set = 0, binding = 5) uniform sampler2D page;
layout(set = 0, binding = 6) uniform sampler2D handMask;
layout(set = 0, binding = 7) uniform sampler2D reader;
layout(set = 0, binding = 8) uniform sampler2D boards;
layout(set = 0, binding = 9) uniform sampler2D preview;
#ifdef HELD_CUT
#define CUT_SET 1
#include "handcut.glsl"
#endif

float handCover()
{
#ifdef HELD_CUT
    return 0.0;   // the mask is what's being drawn
#else
    ivec2 p = ivec2(gl_FragCoord.xy);
    return all(lessThan(p, textureSize(handMask, 0))) ? texelFetch(handMask, p, 0).r : 0.0;
#endif
}

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;
layout(location = 2) flat in int vStyle;
layout(location = 0) out vec4 outColor;

vec4 disc(vec2 uv, float lod)
{
    // a disc of 13 taps a couple of texels across, on a mip that's already smooth
    vec2 t = 1.0 / vec2(textureSize(backdrop, int(lod)));
    vec4 sum = textureLod(backdrop, uv, lod) * 2.0;
    float w = 2.0;
    for (int i = 0; i < 6; i++)
    {
        float a = float(i) * 1.0471976;
        vec2 d1 = vec2(cos(a), sin(a)) * 1.25;
        vec2 d2 = vec2(cos(a + 0.5235988), sin(a + 0.5235988)) * 2.5;
        sum += textureLod(backdrop, uv + d1 * t, lod) * 1.5;
        sum += textureLod(backdrop, uv + d2 * t, lod);
        w += 2.5;
    }
    return sum / w;
}

vec3 frost(vec2 uv, float lod, bool weighted)
{
    // the near blur, mixed with one two mips deeper: a bright sky over dark ground behind the panel becomes a gentle
    // change of light across it instead of a line
    vec4 m = mix(disc(uv, lod), disc(uv, lod + 2.0), 0.6);
    vec3 c = weighted ? m.rgb / max(m.a, 1e-3) : m.rgb;
    // glass lifts colour a little
    float l = dot(c, vec3(0.2126, 0.7152, 0.0722));
    return clamp(mix(vec3(l), c, 1.15), 0.0, 1.0);
}

float segment(vec2 p, vec2 a, vec2 b)
{
    vec2 pa = p - a, ba = b - a;
    return length(pa - ba * clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0));
}

// A microphone in [-1, 1] (y up), as a signed distance: a capsule head, the cradle's U under it, a stem and a foot.
// Muted: a slash through it, with a gap cut around the slash.
float mic(vec2 p, bool muted)
{
    p.y -= 0.04;
    const float w = 0.06;
    float d = segment(p, vec2(0.0, 0.08), vec2(0.0, 0.36)) - 0.17;
    vec2 c = vec2(0.0, 0.16);
    float u = p.y > c.y ? length(vec2(abs(p.x) - 0.32, p.y - c.y)) : abs(length(p - c) - 0.32);
    d = min(d, u - w);
    d = min(d, segment(p, vec2(0.0, -0.16), vec2(0.0, -0.40)) - w);
    d = min(d, segment(p, vec2(-0.18, -0.40), vec2(0.18, -0.40)) - w);
    if (muted)
    {
        float s = segment(p, vec2(-0.44, 0.50), vec2(0.44, -0.46));
        d = max(d, -(s - w - 0.07));
        d = min(d, s - w);
    }
    return d;
}

// The HUD token's glyph in [-1, 1] (y up): a screen's rounded frame, a small block in its top right corner and one at
// its bottom left, where HUDs sit.
float hudGlyph(vec2 p)
{
    const float w = 0.06;
    vec2 d = abs(p) - vec2(0.42, 0.30);
    float frame = abs(length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - 0.08) - w;
    vec2 a = abs(p - vec2(0.19, 0.11)) - vec2(0.10, 0.07);
    vec2 b = abs(p - vec2(-0.19, -0.11)) - vec2(0.10, 0.07);
    float blocks = min(length(max(a, 0.0)) + min(max(a.x, a.y), 0.0), length(max(b, 0.0)) + min(max(b.x, b.y), 0.0)) - 0.02;
    return min(frame, blocks);
}

// The stand key's glyph in [-1, 1] (y up): an arrow pointing up.
float standGlyph(vec2 p)
{
    const float w = 0.075;
    vec2 s = abs(p - vec2(0.0, -0.1)) - vec2(0.0, 0.36);
    float shaft = length(max(s, 0.0)) + min(max(s.x, s.y), 0.0) - w;
    vec2 q = vec2(abs(p.x), p.y);
    vec2 a = vec2(0.0, 0.46), ba = vec2(0.30, 0.16) - a, pa = q - a;
    float head = length(pa - ba * clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0)) - w;
    return min(shaft, head);
}

// The notices token's glyph in [-1, 1] (y up): a bell, its rim and its clapper.
float bellGlyph(vec2 p)
{
    float dome = length(p - vec2(0.0, 0.10)) - 0.32;
    float t = clamp((0.10 - p.y) / 0.40, 0.0, 1.0);
    vec2 s = vec2(abs(p.x) - (0.32 + 0.12 * t), abs(p.y + 0.10) - 0.20);
    float skirt = length(max(s, 0.0)) + min(max(s.x, s.y), 0.0);
    vec2 r = abs(p - vec2(0.0, -0.32)) - vec2(0.50, 0.035);
    float rim = length(max(r, 0.0)) + min(max(r.x, r.y), 0.0) - 0.02;
    float clapper = length(p - vec2(0.0, -0.47)) - 0.09;
    return min(min(dome, skirt), min(rim, clapper));
}

float box(vec2 p, vec2 h, float r)
{
    vec2 d = abs(p) - h + r;
    return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - r;
}

// The incognito token's glyph in [-1, 1] (y up): a hat (crown and brim) over round glasses, their lenses dark while
// the assistant is kept blind, open rings while the person lets it see (Private/PanelPrivate.cs draws the same).
float incognitoGlyph(vec2 p, bool sees)
{
    float hat = min(box(p - vec2(0.0, 0.31), vec2(0.36, 0.21), 0.09), box(p - vec2(0.0, 0.05), vec2(0.66, 0.05), 0.05));
    vec2 q = vec2(abs(p.x) - 0.32, p.y + 0.38);
    float lens = sees ? abs(length(q) - 0.22) - 0.05 : length(q) - 0.27;
    float bridge = segment(p, vec2(-0.08, -0.35), vec2(0.08, -0.35)) - 0.05;
    return min(hat, min(lens, bridge));
}

// The camera token's glyph in [-1, 1] (y up): a camera's body with its viewfinder's hump, a ring of lens cut into it
// and the lens's centre.
float cameraGlyph(vec2 p)
{
    float body = min(box(p - vec2(0.0, -0.06), vec2(0.66, 0.40), 0.10), box(p - vec2(-0.26, 0.38), vec2(0.18, 0.10), 0.04));
    float lens = length(p - vec2(0.0, -0.06));
    return min(max(body, -(lens - 0.27)), lens - 0.14);
}

float cover(float d)
{
    return clamp(0.5 - d / max(fwidth(d), 1e-4), 0.0, 1.0);
}

void main()
{
#ifdef CARVE
    // a menu's footprint written into the compositor's depth as far (Overlay.Walls.cs), but where the hands are in front
    if (handCover() > 0.5) discard;
    return;
#endif
    vec4 c = vec4(vColor.rgb * vColor.a, vColor.a);
    vec2 q = vec2(vUv.x * 2.0 - 1.0, 1.0 - vUv.y * 2.0);   // the quad in [-1, 1], y up
    if (vStyle == 5)
    {
        // a token's disc: a soft rim of light at the edge and a little more across the top, like a glass bead
        float r = length(q);
        vec3 rgb = mix(vColor.rgb, vec3(1.0), 0.28 * smoothstep(0.6, 0.92, r) + 0.12 * clamp(q.y, 0.0, 1.0));
        float a = vColor.a * cover(r - 0.92);
        c = vec4(rgb * a, a);
    }
    else if (vStyle == 6 || vStyle == 7) c *= cover(mic(q, vStyle == 7));
    else if (vStyle == 14) c *= cover(hudGlyph(q));
    else if (vStyle == 20) c *= cover(standGlyph(q));
    else if (vStyle == 21) c *= cover(bellGlyph(q));
    else if (vStyle == 22 || vStyle == 23) c *= cover(incognitoGlyph(q, vStyle == 23));
    else if (vStyle == 28) c *= cover(cameraGlyph(q));
    else if (vStyle == 8)
    {
        // a token's level ring: soft on both sides
        float r = length(q);
        c *= smoothstep(0.72, 0.86, r) * (1.0 - smoothstep(0.9, 1.0, r));
    }
    if (vStyle == 1) c = texture(panel, vUv) * vColor.a * (1.0 - handCover());
    else if (vStyle == 9) c = texture(labels, vUv) * vColor.a;
    else if (vStyle == 10) c = texture(feed, vUv) * vColor.a * (1.0 - handCover());
    else if (vStyle == 12) c = texture(shown, vUv) * vColor.a * (1.0 - handCover());
    else if (vStyle == 15) c = texture(page, vUv) * vColor.a * (1.0 - handCover());
    else if (vStyle == 24) c = texture(reader, vUv) * vColor.a * (1.0 - handCover());
    else if (vStyle == 26) c = texture(boards, vUv) * vColor.a * (1.0 - handCover());
    else if (vStyle == 29) c = vec4(texture(preview, vUv).rgb, 1.0) * vColor.a;
#ifdef HELD_CUT
    else if (vStyle == 30) c = vec4(1.0);
#else
    else if (vStyle == 30) c = vec4(0.0);
#endif
    else if (vStyle == 2) c *= smoothstep(0.0, 0.45, 1.0 - abs(vUv.y * 2.0 - 1.0));
    else if (vStyle == 3) c *= 1.0 - smoothstep(0.55, 1.0, length(vUv * 2.0 - 1.0));
    else if (vStyle == 4 || vStyle == 11 || vStyle == 13 || vStyle == 16 || vStyle == 25 || vStyle == 27)
    {
        float mask = vStyle == 4 ? texture(panel, vUv).a : vStyle == 11 ? texture(feed, vUv).a : vStyle == 16 ? texture(page, vUv).a
            : vStyle == 25 ? texture(reader, vUv).a : vStyle == 27 ? texture(boards, vUv).a : texture(shown, vUv).a;
        float cover = smoothstep(0.0, max(vColor.a, 0.05), mask) * (1.0 - handCover());
        c = vec4(frost(gl_FragCoord.xy / abs(vColor.xy), vColor.z, vColor.x < 0.0) * cover, cover);
    }
    outColor = c;
#ifdef HELD_CUT
    // the pointer's beam and dot by their shape alone, not their faint alpha: the hands' layer then shows the eye there
    // whole (the beam over what's behind the menu), so it looks as it does out in the world; scaled by its alpha too,
    // the whitish beam came out at about a quarter of itself, faint against the light feed and reader
    float held = (vStyle == 2 || vStyle == 3) ? (vColor.a > 0.0 ? min(c.a / vColor.a, 1.0) : 0.0) : c.a;
    outColor = vec4(held * cutShows(gl_FragCoord.xy, gl_FragCoord.z, 0.03));
#endif
#ifdef WORLD_DEPTH
    if (c.a < 0.1) discard;   // world labels write their depth only where they show (Overlay.cs)
#endif
}
