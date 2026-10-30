#version 460
// The direct-lighting light map: r = sun shadow, g = ambient occlusion, b/a = projector shadows.
// Ported from the Second Life viewer's class2/deferred/sunLightSSAOF.glsl, class1/deferred/shadowUtil.glsl and
// aoUtil.glsl (LGPL 2.1, Linden Research, Inc.).
#include "common.glsl"
#include "deferred.glsl"

layout(location = 0) in vec2 vary_fragcoord;
layout(location = 0) out vec4 frag_color;

#define SLOT_NOISE 6

#include "shadows.glsl"

// --- aoUtil.glsl

vec4 getPositionAo(vec2 pos_screen)
{
    float depth = getDepth(pos_screen);
    vec4 pos = getPositionWithDepth(pos_screen, max(depth, 1e-7));
    pos.w = 1.0;
    return pos;
}

vec2 getKern(int i)
{
    vec2 kern[8];
    // exponentially (^2) distant occlusion samples spread around origin
    kern[0] = vec2(-1.0, 0.0) * 0.125*0.125;
    kern[1] = vec2(1.0, 0.0) * 0.250*0.250;
    kern[2] = vec2(0.0, 1.0) * 0.375*0.375;
    kern[3] = vec2(0.0, -1.0) * 0.500*0.500;
    kern[4] = vec2(0.7071, 0.7071) * 0.625*0.625;
    kern[5] = vec2(-0.7071, -0.7071) * 0.750*0.750;
    kern[6] = vec2(-0.7071, 0.7071) * 0.875*0.875;
    kern[7] = vec2(0.7071, -0.7071) * 1.000*1.000;
    return kern[i] / VIEW.viewport.xy;
}

float calcAmbientOcclusion(vec4 pos, vec3 norm, vec2 pos_screen)
{
    if (SKY.ssao_on == 0) return 1.0;
    float ret = 1.0;
    vec3 pos_world = pos.xyz;
    vec2 noise_reflect = texture(textures[SLOT_NOISE], pos_screen.xy * (VIEW.viewport.xy / 128)).xy;

    float angle_hidden = 0.0;
    float points = 0;

    float scale = min(SKY.ssao_radius / -pos_world.z, SKY.ssao_max_radius);

    for (int i = 0; i < 8; i++)
    {
        vec2 samppos_screen = pos_screen + scale * reflect(getKern(i), noise_reflect);
        vec3 samppos_world = getPositionAo(samppos_screen).xyz;

        vec3 diff = pos_world - samppos_world;
        float dist2 = dot(diff, diff);

        float funky_val = (dot((samppos_world - 0.05*norm - pos_world), norm) > 0.0) ? 1.0 : 0.0;
        angle_hidden = angle_hidden + funky_val * min(1.0/dist2, SKY.ssao_factor_inv);

        // 'blocked' samples (significantly closer to camera relative to pos_world) are "no data", not "no occlusion"
        float diffz_val = (diff.z > -1.0) ? 1.0 : 0.0;
        points = points + diffz_val;
    }

    angle_hidden = min(SKY.ssao_factor*angle_hidden/points, 1.0);

    float points_val = (points > 0.0) ? 1.0 : 0.0;
    ret = (1.0 - (points_val * angle_hidden));

    ret = max(ret, 0.0);
    return min(ret, 1.0);
}

void main()
{
    if (lensHidden()) discard;   // never seen (Frame/Renderer.Lens.cs)
#ifdef HALF_LIGHT
    // half size (Renderer.HalfLight.cs): lit at the top left pixel of this texel's 2x2 block, exactly as there
    vec2 pos_screen = (floor(gl_FragCoord.xy) * 2.0 + 0.5) / VIEW.viewport.xy;
#else
    vec2 pos_screen = vary_fragcoord.xy;
#endif
    float depth = getDepth(pos_screen);
    if (isSkyDepth(depth)) { frag_color = vec4(1); return; }
    vec4 pos  = getPositionWithDepth(pos_screen, depth);
    vec4 norm = getNorm(pos_screen);

    vec4 col;
    col.r = sampleDirectionalShadow(pos.xyz, norm.xyz, pos_screen);
    col.g = calcAmbientOcclusion(pos, norm.xyz, pos_screen);
    col.b = sampleSpotShadow(pos.xyz, norm.xyz, 0, pos_screen);
    col.a = sampleSpotShadow(pos.xyz, norm.xyz, 1, pos_screen);

    frag_color = clamp(col, vec4(0), vec4(1));
}
