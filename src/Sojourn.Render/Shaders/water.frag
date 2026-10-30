#version 460
// The water surface (TRANSPARENT_WATER, WATER_MINIMAL). Ported from the Second Life viewer's
// class3/environment/waterF.glsl (LGPL 2.1, Linden Research, Inc.).
// Push block: i1 = bumpMap | bumpMap2 << 16 (bindless slots), i2 = screenTex | depthMap << 16, i3 = exclusionTex.
#include "common.glsl"
#include "gbuffer_util.glsl"
#include "atmospherics.glsl"
#include "pbr.glsl"
#include "probes.glsl"
#include "shadows.glsl"
#include "water_mask.glsl"

layout(location = 0) in vec4 refCoord;
layout(location = 1) in vec4 littleWave;
layout(location = 2) in vec4 view;
layout(location = 3) in vec3 vary_position;
layout(location = 4) in vec3 vary_light_dir;
layout(location = 5) in vec3 vary_tangent;
layout(location = 6) in vec3 vary_normal;
layout(location = 0) out vec4 frag_color;

#define bumpMapSlot  int(pc.i1 & 0xFFFFu)
#define bumpMap2Slot int(pc.i1 >> 16)
#define screenTexSlot int(pc.i2 & 0xFFFFu)
#define depthMapSlot  int(pc.i2 >> 16)

vec3 BlendNormal(vec3 bump1, vec3 bump2)
{
    return mix(bump1, bump2, SKY.water_blend_factor);
}

vec3 transform_normal(vec3 vN, vec3 vT, vec3 vB, vec3 vNt)
{
    return normalize(vNt.x * vT + vNt.y * vB + vNt.z * vN);
}

vec3 getPositionWithNDC(vec3 ndc)
{
    vec4 p = VIEW.invProj * vec4(ndc, 1.0);
    return p.xyz / p.w;
}

vec4 bump(int slot, vec2 uv)
{
    return slot >= 0 && slot < 0xFFFF ? texture(textures[nonuniformEXT(slot)], uv) : vec4(0.5, 0.5, 1.0, 1.0);
}

void generateWaveNormals(out vec3 wave1, out vec3 wave2, out vec3 wave3)
{
    vec2 bigwave = vec2(refCoord.w, view.w);
    vec3 wave1_a = bump(bumpMapSlot, bigwave).xyz * 2.0 - 1.0;
    vec3 wave2_a = bump(bumpMapSlot, littleWave.xy).xyz * 2.0 - 1.0;
    vec3 wave3_a = bump(bumpMapSlot, littleWave.zw).xyz * 2.0 - 1.0;
    vec3 wave1_b = bump(bumpMap2Slot, bigwave).xyz * 2.0 - 1.0;
    vec3 wave2_b = bump(bumpMap2Slot, littleWave.xy).xyz * 2.0 - 1.0;
    vec3 wave3_b = bump(bumpMap2Slot, littleWave.zw).xyz * 2.0 - 1.0;
    wave1 = BlendNormal(wave1_a, wave1_b);
    wave2 = BlendNormal(wave2_a, wave2_b);
    wave3 = BlendNormal(wave3_a, wave3_b);
}

void calculateFresnelFactors(out vec3 df3, out vec2 df2, vec3 viewVec, vec3 wave1, vec3 wave2, vec3 wave3, vec3 wavef)
{
    df3 = max(vec3(0), vec3(
        dot(viewVec, wave1),
        dot(viewVec, (wave2 + wave3) * 0.5),
        dot(viewVec, wave3)
    ) * SKY.fresnelScale + SKY.fresnelOffset);
    df3 *= df3;
    df2 = max(vec2(0), vec2(
        df3.x + df3.y + df3.z,
        dot(viewVec, wavef) * SKY.fresnelScale + SKY.fresnelOffset
    ));
}

void main()
{
    if (lensHidden()) discard;   // never seen (Frame/Renderer.Lens.cs)
    if (length(vary_position.xyz) > GL_FAR_CLIP) discard;   // a sphere about the eye, not the far plane
    vec3 vN = vary_normal;
    vec3 vT = vary_tangent;
    vec3 vB = cross(vN, vT);

    vec3 pos = vary_position.xyz;
    float dist = length(pos.xyz);
    vec3 viewVec = normalize(pos.xyz);

    vec3 wave1 = vec3(0, 0, 1);
    vec3 wave2 = vec3(0, 0, 1);
    vec3 wave3 = vec3(0, 0, 1);
    generateWaveNormals(wave1, wave2, wave3);

    float dmod = sqrt(dist);
    vec2 distort = (refCoord.xy/refCoord.z) * 0.5 + 0.5;

    vec3 wavef = (wave1 + wave2 * 0.4 + wave3 * 0.6) * 0.5;

    vec3 df3 = vec3(0);
    vec2 df2 = vec2(0);

    vec3 sunlit;
    vec3 amblit;
    vec3 additive;
    vec3 atten;
    calcAtmosphericVarsLinear(pos.xyz, wavef, vary_light_dir, sunlit, amblit, additive, atten);

    calculateFresnelFactors(df3, df2, normalize(view.xyz), wave1, wave2, wave3, wavef);

    vec3 waver = wavef*3;

    vec3 up = transform_normal(vN, vT, vB, vec3(0,0,1));
    float vdu = -dot(viewVec, up)*2;

    vec3 wave_ibl = wavef * SKY.normScale;
    wave_ibl.z *= 2.0;
    wave_ibl = transform_normal(vN, vT, vB, normalize(wave_ibl));

    vec3 norm = transform_normal(vN, vT, vB, normalize(wavef));

    vdu = clamp(vdu, 0, 1);

    wavef = normalize(wavef);
    wavef = transform_normal(vN, vT, vB, wavef);

    dist = max(dist, 5.0);

    //figure out distortion vector (ripply)
    vec2 distort2 = distort + waver.xy * SKY.refScale / max(dmod, 1.0) * 2;
    distort2 = clamp(distort2, vec2(0), vec2(0.999));

    float shadow = 1.0f;
    // HAS_SUN_SHADOW, which the viewer defines whenever sun shadows are on: no glints where the water is in shadow
    shadow = sampleDirectionalShadow(pos.xyz, norm.xyz, distort);
    float water_mask = waterExclusionMask(int(pc.i3 & 0xFFFFu), distort);

    vec3 sunlit_linear = sunlit;
    float fade = 1;

    // TRANSPARENT_WATER: refraction from the scene copy, depth from the depth copy (reverse-Z: larger is nearer)
    float depth = texture(textures[nonuniformEXT(depthMapSlot)], distort).r;
    vec3 refPos = getPositionWithNDC(vec3(distort*2.0-vec2(1.0), max(depth, 1e-7)));

    // SHORELINE_FADE is never defined by the viewer (no permutation sets it), so fade stays 1: crisp shorelines

    fade *= water_mask;

    distort2 = mix(distort, distort2, min(1, fade * 10));
    depth = texture(textures[nonuniformEXT(depthMapSlot)], distort2).r;

    refPos = getPositionWithNDC(vec3(distort2 * 2.0 - vec2(1.0), max(depth, 1e-7)));

    if (pos.z < refPos.z - 0.05)
    {
        distort2 = distort;
    }

    vec4 fb = texture(textures[nonuniformEXT(screenTexSlot)], distort2);

    float metallic = 1.0;
    float perceptualRoughness = SKY.blurMultiplier;
    float gloss      = 1 - perceptualRoughness;

    vec3  irradiance = vec3(0);
    vec3  radiance  = vec3(0);

    // WATER_MINIMAL
    sampleReflectionProbesWater(irradiance, radiance, distort2, pos.xyz, wave_ibl.xyz, gloss, amblit);

    vec3 diffuseColor = vec3(0);
    vec3 specularColor = vec3(0);
    vec3 specular_linear = srgb_to_linear(SKY.water_specular);
    calcDiffuseSpecular(specular_linear, metallic, diffuseColor, specularColor);

    vec3 v = -normalize(pos.xyz);

    vec3 light_dir = transform_normal(vN, vT, vB, SKY.water_light_dir);

    float nl = 0;
    vec3 diffPunc = vec3(0);
    vec3 specPunc = vec3(0);

    pbrPunctual(diffuseColor, specularColor, perceptualRoughness, metallic, normalize(wavef+up*max(dist, 32.0)/32.0*(1.0-vdu)), v, normalize(light_dir), nl, diffPunc, specPunc);

    vec3 punctual = clamp(nl * (diffPunc + specPunc), vec3(0), vec3(10)) * sunlit_linear * shadow * atten;

    radiance *= df2.y;

    vec3 color = vec3(0);
    color = mix(fb.rgb, radiance, min(1, df2.x)) + punctual.rgb;

    fade *= 60;
    fade = min(1, fade);
    color = mix(fb.rgb, color, fade);

    float spec = min(max(max(punctual.r, punctual.g), punctual.b), 0);

    frag_color = min(vec4(1),max(vec4(color.rgb, spec * water_mask), vec4(0)));
}
