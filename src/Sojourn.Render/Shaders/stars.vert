#version 460
// The viewer's star field (LLVOWLSky::initStars / updateStarGeometry, starsV.glsl; Second Life viewer, LGPL 2.1,
// Linden Research, Inc.): 1000 stars on the upper hemisphere of the 15000 m dome, each a corner-anchored quad of
// 16-36 m, of which the viewer draws the first 4000 vertices (666 stars and a triangle). The viewer seeds them
// randomly per session; here a fixed hash of the star index stands in for ll_frand, so every render agrees.
// Push: the sky pass's block (cloud noise slots in i1, for the cloud cover in stars.frag).
#include "common.glsl"

layout(location = 0) out vec2 vary_texcoord0;
layout(location = 1) out vec3 vertex_color;
layout(location = 2) out vec2 screenpos;
layout(location = 3) out vec3 vary_dir;

uint pcg(uint v)
{
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}
float frand(uint star, uint k) { return float(pcg(star * 8u + k + 1013u) >> 8) / 16777216.0; }
float u8(float x) { return floor(x * 255.0 + 0.5) / 255.0; }   // colours travel as LLColor4U

void main()
{
    uint star = uint(gl_VertexIndex) / 6u, corner = uint(gl_VertexIndex) % 6u;
    const float DISTANCE_TO_STARS = 15000.0;   // LLSettingsSky::DOME_RADIUS
    vec3 at = normalize(vec3(frand(star, 0u) - 0.5, frand(star, 1u) - 0.5, frand(star, 2u) * 0.5));
    vec3 p = at * DISTANCE_TO_STARS;
    vec3 left = cross(at, vec3(0, 0, 1));   // not normalised, as the viewer's: smaller towards the zenith
    vec3 up = cross(at, left);
    float sc = 16.0 + frand(star, 5u) * 20.0;
    left *= sc;
    up *= sc;
    // P, P+up, P+left+up,  P, P+left+up, P+left
    vec3 v = p;
    vec2 uv = vec2(1, 0);
    if (corner == 1u) { v = p + up; uv = vec2(1, 1); }
    else if (corner == 2u || corner == 4u) { v = p + left + up; uv = vec2(0, 1); }
    else if (corner == 5u) { v = p + left; uv = vec2(0, 0); }

    // the pool's model matrix: around the camera, rotated about +Z by gFrameTimeSeconds * 0.01 degrees
    float c = cos(SKY.star_rotation), s = sin(SKY.star_rotation);
    vec3 r = vec3(c * v.x - s * v.y, s * v.x + c * v.y, v.z);
    gl_Position = VIEW.viewProj * vec4(VIEW.cameraPos.xyz + r, 1.0);
    gl_Position.z = 0.0;   // far plane (reverse-Z); the pass has no depth test

    float t = mod(SKY.star_time, 1.25);
    screenpos = v.xy * vec2(t, t);
    vary_texcoord0 = uv;
    vertex_color = vec3(u8(0.75 + frand(star, 3u) * 0.25), 1.0, u8(0.75 + frand(star, 4u) * 0.25));
    vary_dir = r;
}
