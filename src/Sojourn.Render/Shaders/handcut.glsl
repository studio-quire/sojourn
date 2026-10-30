// The person's hands over the headset's menus (Realtime/Overlay.Hands.cs): the menus are the compositor's quad layers,
// over the eye images, so the hands are drawn again on a layer above them, where they're in front of a menu. A point
// of the eye image shows there when, seen from the eye, it's inside some menu's outline and nearer than every menu
// whose outline it's inside: a hand behind a menu stays hidden by it, one pushing through shows up to the surface.
#ifndef CUT_SET
#define CUT_SET 0
#endif
#define CUT_MENUS 16
#define CUT_EDGE 0.0025   // ~3 of the Quest 3's eye pixels at full size
// How far past a menu's outline (radians) what's in front of its plane still goes on the layer over the menus. Outside
// every menu that layer shows the eye over the eye, so it changes nothing there until the frame is reprojected: both
// projection layers are moved by their depth, and something near the eye (the person's own hair, a hand) moves
// across the menu's edge while the quads stay put. Cut at the outline, the strip it moved into showed the menu through
// it (the left and right edges tearing through hair, as heads mostly move sideways). Overlay.Hands.cs's box grows by
// as much (CutReach).
#define CUT_REACH 0.05

layout(set = CUT_SET, binding = 0, std140) uniform CutBlock
{
    mat4 invViewProj;   // the eye's normalised device coordinates (reverse-Z) to the frame's region
    vec4 eye;           // xyz: the eye in the region; w: how many menus
    vec4 size;          // the eye image's width and height; w: 1 where anything in front of a menu shows over it (not only the own body)
    vec4 menus[CUT_MENUS * 3];   // each: its centre, half its width along its right, half its height along its up
} CUT;

// 1 where the eye image's point at this fragment and depth shows over the menus, else 0. margin: how far behind a
// menu's plane, as a share of the way from the eye, a point still counts as in front of it (held things: the pointer's
// beam ends on a menu, its dot sits on it, and the layer's plane can be a hair nearer than the ray's hit)
float cutShows(vec2 frag, float depth, float margin)
{
    if (depth <= 0.0) return 0.0;   // infinitely far (the sky)
    vec4 p = CUT.invViewProj * vec4(frag / CUT.size.xy * 2.0 - 1.0, depth, 1.0);
    vec3 e = CUT.eye.xyz;
    vec3 d = p.xyz / p.w - e;
    bool over = false;
    int n = int(CUT.eye.w + 0.5);
    for (int i = 0; i < n; i++)
    {
        vec3 c = CUT.menus[i * 3].xyz, r = CUT.menus[i * 3 + 1].xyz, u = CUT.menus[i * 3 + 2].xyz;
        vec3 nrm = cross(r, u);
        float den = dot(d, nrm);
        if (abs(den) < 1e-12) continue;
        float s = dot(c - e, nrm) / den;   // where the eye's ray meets the menu's plane, as a share of the way to the point
        if (s <= 0.0) continue;
        vec3 x = e + d * s - c;
        float xr = abs(dot(x, r)) / length(r) - length(r), xu = abs(dot(x, u)) / length(u) - length(u);   // metres outside the outline
        float dist = length(d * s);
        bool nearer = s < 1.0001 - margin;   // the menu is nearer than the point (or the point lies on it)
        // the outline a few pixels wider (CUT_EDGE radians at the menu's distance): the compositor filters the quad's
        // edge across a pixel or two, which a cut at the exact outline left showing over what's in front
        float pad = CUT_EDGE * dist;
        if (nearer)
        {
            if (xr <= pad && xu <= pad) return 0.0;   // it covers the point
            continue;
        }
        if (xr <= CUT_REACH * dist && xu <= CUT_REACH * dist) over = true;   // in front of it, or near its edge (CUT_REACH)
    }
    return over ? 1.0 : 0.0;
}

float cutShows(vec2 frag, float depth) { return cutShows(frag, depth, 0.0); }

// Whether the eye's ray through this fragment meets some menu, its outline grown by a factor (Overlay.Walls.cs).
bool cutUnderMenu(vec2 frag, float grow)
{
    vec4 p = CUT.invViewProj * vec4(frag / CUT.size.xy * 2.0 - 1.0, 0.5, 1.0);
    vec3 e = CUT.eye.xyz;
    vec3 d = p.xyz / p.w - e;
    int n = int(CUT.eye.w + 0.5);
    for (int i = 0; i < n; i++)
    {
        vec3 c = CUT.menus[i * 3].xyz, r = CUT.menus[i * 3 + 1].xyz, u = CUT.menus[i * 3 + 2].xyz;
        vec3 nrm = cross(r, u);
        float den = dot(d, nrm);
        if (abs(den) < 1e-12) continue;
        float s = dot(c - e, nrm) / den;
        if (s <= 0.0) continue;
        vec3 x = e + d * s - c;
        if (abs(dot(x, r)) <= dot(r, r) * grow && abs(dot(x, u)) <= dot(u, u) * grow) return true;
    }
    return false;
}
