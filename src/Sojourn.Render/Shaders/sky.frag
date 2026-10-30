#version 460
// The WindLight sky, drawn into the G-buffer as the viewer's WL sky pool is (skip-atmos flag, colour in the
// emissive target), in LLDrawPoolWLSky::renderDeferred's order: dome (skyF), sun disc (sunDiscF), moon (moonF),
// [stars: stars.vert/frag, which allow for what comes after], clouds (cloudsF).
#include "common.glsl"
#include "gbuffer_util.glsl"
#include "sky_color.glsl"
#include "clouds.glsl"
#include "heavenly.glsl"

layout(location = 0) in vec2 vary_fragcoord;
layout(location = 0) out vec4 frag_data0;
layout(location = 1) out vec4 frag_data1;
layout(location = 2) out vec4 frag_data2;
layout(location = 3) out vec4 frag_data3;

void main()
{
    vec4 ndc = vec4(vary_fragcoord * 2.0 - 1.0, 0.5, 1.0);
    vec4 eye = VIEW.invProj * ndc;
    vec3 dirEye = normalize(eye.xyz / eye.w);
    vec3 d = normalize(mat3(VIEW.invView) * dirEye);
    vec3 color = skyColorForDir(d);

    // sun, then moon, alpha-blended over the dome (BT_ALPHA); uv derivatives taken outside the branches
    vec3 sun = sunDiscUv(d);
    vec3 moon = moonDiscUv(d);
    vec2 sdx = dFdx(sun.xy), sdy = dFdy(sun.xy), mdx = dFdx(moon.xy), mdy = dFdy(moon.xy);
    if (sunDiscDrawn() && sun.z > 0.0)
    {
        vec4 c = sunDiscColor(sun.xy, sdx, sdy);
        color = mix(color, c.rgb, c.a);
    }
    if (moonDiscDrawn() && moon.z > 0.0)
    {
        vec4 c = moonDiscColor(moon.xy, mdx, mdy);
        color = mix(color, c.rgb, c.a);
    }

    // the cloud layer blends over everything above the way the viewer's cloud dome pass does (alpha blend)
    vec3 dp = domePos(d);
    vec4 cloud = cloudColor(dp, normalize(dp));
    color = mix(color, cloud.rgb, cloud.a);
    frag_data0 = vec4(0);
    frag_data1 = vec4(0);
    frag_data2 = gbNormOut(vec4(0.0, 0.0, 0.0, GBUFFER_FLAG_SKIP_ATMOS));
    frag_data3 = vec4(color.rgb, 1.0);
}
