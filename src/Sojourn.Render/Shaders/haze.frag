#version 460
// Atmospheric haze over the deferred-lit opaque scene (blend ONE, SRC_ALPHA).
// Ported from the Second Life viewer's class3/deferred/hazeF.glsl (LGPL 2.1, Linden Research, Inc.).
#include "common.glsl"
#include "deferred.glsl"
#include "atmospherics.glsl"

layout(location = 0) in vec2 vary_fragcoord;
layout(location = 0) out vec4 frag_color;

void main()
{
    if (lensHidden()) discard;   // never seen (Frame/Renderer.Lens.cs)
    vec2  tc           = vary_fragcoord.xy;
    float depth        = getDepth(tc.xy);
    if (isSkyDepth(depth))
    {
        //should only be true of sky, clouds, sun/moon, and stars
        discard;
    }
    vec4  pos          = getPositionWithDepth(tc, depth);
    vec4  norm         = getNorm(tc);
    vec3  light_dir   = (SKY.sun_up_factor == 1) ? SKY.sun_dir : SKY.moon_dir;

    vec3  color = vec3(0);

    vec3 sunlit;
    vec3 amblit;
    vec3 additive;
    vec3 atten;

    calcAtmosphericVarsLinear(pos.xyz, norm.xyz, light_dir, sunlit, amblit, additive, atten);

    // mask off atmospherics below water (when camera is under water)
    bool do_atmospherics = false;

    if (dot(vec3(0), SKY.waterPlane.xyz) + SKY.waterPlane.w > 0.0 ||
        dot(pos.xyz, SKY.waterPlane.xyz) + SKY.waterPlane.w > 0.0)
    {
        do_atmospherics = true;
    }

    float alpha = 0.0;

    if (do_atmospherics)
    {
        alpha = atten.r;
        color = srgb_to_linear(additive*2.0);
        color *= SKY.sky_hdr_scale;
    }
    else
    {
        color = vec3(0,0,0);
        alpha = 1.0;
    }

    frag_color = max(vec4(color.rgb, alpha), vec4(0));
}
