#version 460
// Forward-lit blended faces, sorted far to near. One shader covers the viewer's alpha paths:
// legacy (class2/deferred/alphaF.glsl), legacy with materials (class3/deferred/materialF.glsl blend path),
// fullbright alpha (class1/deferred/fullbrightF.glsl IS_ALPHA) and PBR (pbralphaF, approximated through
// pbrBaseLight until the full port). Ported from the Second Life viewer (LGPL 2.1, Linden Research, Inc.).
#include "common.glsl"
#include "gbuffer_util.glsl"
#include "atmospherics.glsl"
#include "pbr.glsl"
#include "probes.glsl"
#include "shadows.glsl"
#include "alpha_lit.glsl"
#include "layers.glsl"

layout(location = 0) in vec3 vWorldPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec4 vTangent;
layout(location = 3) in vec2 vUv;
layout(location = 4) flat in uint vInstance;
layout(location = 5) in vec3 vViewPos;
#ifdef SIMPLE_LIGHT
// simpler lighting (Frame/Renderer.Layers.cs, alpha_simple.vert): ambient with the probes', the atmospherics and the
// local lights per vertex; the texture, the sun's angle and shadow, and reflections per pixel
layout(location = 6) in vec3 vAmbient;
layout(location = 7) in vec3 vSunlit;
layout(location = 8) in vec3 vAdditive;
layout(location = 9) in vec3 vAtten;
layout(location = 10) in vec3 vLocal;
#endif
layout(location = 0) out vec4 frag_color;

// behind see-through layers that let through less than the cap allows (layers.glsl): checked once a fragment is known
// to show, so the texels that are discarded anyway don't pay for the look-up
#if defined(LAYER_CUT) && defined(LAYER_DEBUG)
#define CUT_LAYER() if (layerDropped()) { frag_color = vec4(1, 0, 1, 1); return; }
#elif defined(LAYER_CUT)
#define CUT_LAYER() if (layerDropped()) discard
#else
#define CUT_LAYER()
#endif

// LLDrawPoolAlpha's MINIMUM_ALPHA (~1/255): what's fainter is discarded
const float MINIMUM_ALPHA = 0.004;

void main()
{
    // the viewer's waterClip: the pre- and post-water pools each keep to their side of the water (pc.v1.z the side's
    // sign, pc.v1.w the water height; 0 when the frame isn't split). First, before anything is read: a face reaching
    // across the water draws in both pools, and half its fragments end here
    if (pc.v1.z != 0.0 && pc.v1.z * (vWorldPos.z - pc.v1.w) < 0.0) discard;
    if (lensHidden()) discard;   // never seen (Frame/Renderer.Lens.cs)
    Instance inst = INSTANCE(vInstance);
    Material m = MATERIAL(inst.material);
    // the blended pass doesn't cull, as LLDrawPoolAlpha doesn't for a double-sided GLTF material; every other face's
    // back is dropped here, as culling would (avatars' own pipelines cull for themselves)
    if (!gl_FrontFacing && (m.flags & (MAT_DOUBLE_SIDED | MAT_AVATAR)) == 0u) discard;
    vec3 pos = vViewPos;
    vec3 vN = normalize(mat3(VIEW.view) * vNormal);
    // avatar skin (alphaF IS_AVATAR_SKIN) keeps the vertex normal on back faces
    vec3 norm = (gl_FrontFacing || (m.flags & MAT_AVATAR) != 0u) ? vN : -vN;
    vec3 light_dir = (SKY.sun_up_factor == 1) ? SKY.sun_dir : SKY.moon_dir;
    // the sun shadow is sampled only once a fragment is known to show (and not for fullbright): close-up layers of
    // flowers and glass are mostly transparent texels, and each layer paid for its shadow before being discarded
    vec2 screen = gl_FragCoord.xy * VIEW.viewport.zw;

    vec3 sunlit, amblit, additive, atten;

#if !defined(ALPHA_PATH) || ALPHA_PATH == 1
    if ((m.flags & MAT_PBR) != 0u)
    {
        vec4 basecolor = sampleTex(m.diffuse.index, khrTransform(vUv, m.diffuse), vec4(1));
        basecolor.rgb = srgb_to_linear(basecolor.rgb);
        basecolor *= m.color;
        if (basecolor.a < MINIMUM_ALPHA) discard;   // pbralphaF: minimum_alpha
        CUT_LAYER();
        float shadow = sampleDirectionalShadow(pos, norm, screen);
        vec3 vT = normalize(mat3(VIEW.view) * vTangent.xyz);
        vec3 vNt = sampleTex(m.normal.index, khrTransform(vUv, m.normal), vec4(0.5, 0.5, 1.0, 1.0)).xyz * 2.0 - 1.0;
        vec3 vB = vTangent.w * cross(vN, vT);
        vec3 tnorm = normalize(vNt.x * vT + vNt.y * vB + vNt.z * vN);
        tnorm *= gl_FrontFacing ? 1.0 : -1.0;
        vec3 orm = sampleTex(m.specular.index, khrTransform(vUv, m.specular), vec4(1)).rgb;
        float perceptualRoughness = orm.g * m.roughness;
        float metallic = orm.b * m.metallic;
        float ao = orm.r;
        vec3 emissive = m.specColor.rgb * srgb_to_linear(sampleTex(m.emissive.index, khrTransform(vUv, m.emissive), vec4(1)).rgb);

#ifdef SIMPLE_LIGHT
        sunlit = vSunlit; additive = vAdditive; atten = vAtten;
        vec3 irradiance = vAmbient;
        vec3 radiance = sampleRadiance(pos, tnorm, 1.0 - perceptualRoughness);
#else
        calcAtmosphericVarsLinear(pos, tnorm, light_dir, sunlit, amblit, additive, atten);
        vec3 irradiance = amblit;
        vec3 radiance = vec3(0);
        sampleReflectionProbes(irradiance, radiance, vec2(0), pos, tnorm, 1.0 - perceptualRoughness, true, amblit);
#endif
        vec3 diffuseColor, specularColor;
        calcDiffuseSpecular(basecolor.rgb, metallic, diffuseColor, specularColor);
        vec3 v = -normalize(pos);
        vec3 color = pbrBaseLight(diffuseColor, specularColor, metallic, v, tnorm, perceptualRoughness, light_dir, sunlit, shadow, radiance, irradiance, emissive, ao, additive, atten);
        color = applySkyAndWaterFog(pos, additive, atten, vec4(color, 1.0)).rgb;
        frag_color = max(vec4(color, basecolor.a), vec4(0));
        return;
    }

#endif
#if !defined(ALPHA_PATH) || ALPHA_PATH != 1
    vec4 diffuse_srgb = sampleTex(m.diffuse.index, teTransform(vUv, m.diffuse), vec4(1));
#endif

#if !defined(ALPHA_PATH) || ALPHA_PATH == 2
    if ((m.flags & MAT_FULLBRIGHT) != 0u && (m.flags & MAT_LEGACY_MATERIAL) == 0u)
    {
        vec4 color = diffuse_srgb;
        float final_alpha = color.a * m.color.a;
        if (final_alpha < MINIMUM_ALPHA) discard;   // fullbrightF IS_ALPHA: minimum_alpha
        CUT_LAYER();
        color.rgb *= m.color.rgb;
        color.a = final_alpha;
        color.rgb = srgb_to_linear(color.rgb);
        calcAtmosphericVars(pos.xyz, vec3(0), 1.0, sunlit, amblit, additive, atten);
        color.rgb = applySkyAndWaterFog(pos, additive, atten, color).rgb;
        frag_color = max(color, vec4(0));
        return;
    }

#endif
#if !defined(ALPHA_PATH) || ALPHA_PATH == 3
    if ((m.flags & MAT_LEGACY_MATERIAL) == 0u)
    {
        // alphaF.glsl
        vec4 diffuse_linear = vec4(srgb_to_linear(diffuse_srgb.rgb), diffuse_srgb.a);
        float final_alpha = diffuse_linear.a * m.color.a;
        if (final_alpha < MINIMUM_ALPHA) discard;
        CUT_LAYER();
        if ((m.flags & MAT_AVATAR) != 0u && final_alpha < m.alphaCutoff) discard;   // IS_AVATAR_SKIN minimum_alpha
        diffuse_srgb.rgb *= m.color.rgb;
        diffuse_linear.rgb = srgb_to_linear(diffuse_srgb.rgb);

#ifdef SIMPLE_LIGHT
        frag_color = alphaLitSimple(vec4(diffuse_linear.rgb, final_alpha), pos, norm, sampleDirectionalShadow(pos, norm, screen),
                                    vAmbient, vSunlit, vLocal, vAdditive, vAtten);
#else
        frag_color = alphaLitLegacy(vec4(diffuse_linear.rgb, final_alpha), pos, norm, sampleDirectionalShadow(pos, norm, screen));
#endif
        return;
    }

#endif
#if !defined(ALPHA_PATH) || ALPHA_PATH == 4
    // materialF.glsl, DIFFUSE_ALPHA_MODE_BLEND
    CUT_LAYER();
    float shadow = sampleDirectionalShadow(pos, norm, screen);
    vec4 diffcol = diffuse_srgb;
    diffcol.rgb *= m.color.rgb;
    float glossiness = m.specColor.a;
    vec4 spec = vec4(m.specColor.rgb, 1.0);
    if (m.specular.index >= 0)
    {
        spec = texture(textures[nonuniformEXT(m.specular.index)], teTransform(vUv, m.specular));
        spec.rgb *= m.specColor.rgb;
    }
    float env = m.envIntensity * spec.a;
    if (m.normal.index >= 0)
    {
        vec4 vNt = texture(textures[nonuniformEXT(m.normal.index)], teTransform(vUv, m.normal));
        glossiness *= vNt.a;
        vNt.xyz = vNt.xyz * 2.0 - 1.0;
        vec3 vT = normalize(mat3(VIEW.view) * vTangent.xyz);
        vec3 vB = vTangent.w * cross(vN, vT);
        norm = normalize(vNt.x * vT + vNt.y * vB + vNt.z * vN);
    }
    float emissive = (m.flags & MAT_FULLBRIGHT) != 0u ? 1.0 : 0.0;

    diffcol.rgb = srgb_to_linear(diffcol.rgb);
    spec.rgb = srgb_to_linear(spec.rgb);
    spec.a = glossiness;

#ifdef SIMPLE_LIGHT
    sunlit = vSunlit; additive = vAdditive; atten = vAtten;
#else
    calcAtmosphericVarsLinear(pos.xyz, norm.xyz, light_dir, sunlit, amblit, additive, atten);
#endif
    if (SKY.classic_mode > 0)
        sunlit *= 1.35;
    vec3 sunlit_linear = sunlit;

    vec3 glossenv = vec3(0.0);
    vec3 legacyenv = vec3(0.0);
#ifdef SIMPLE_LIGHT
    vec3 ambenv = vAmbient;
    sampleReflectionsLegacy(glossenv, legacyenv, pos.xyz, norm.xyz, glossiness, env);
#else
    vec3 amblit_linear = amblit;
    vec3 ambenv = amblit;
    sampleReflectionProbesLegacy(ambenv, glossenv, legacyenv, vec2(0), pos.xyz, norm.xyz, glossiness, env, true, amblit_linear);
#endif

    vec3 color = ambenv;
    float da = clamp(dot(norm.xyz, light_dir.xyz), 0.0, 1.0);
    if (SKY.classic_mode > 0)
    {
        da = pow(da,1.2);
        vec3 sun_contrib = vec3(min(da, shadow));
        color.rgb = srgb_to_linear(color.rgb * 0.9 + linear_to_srgb(sun_contrib) * sunlit_linear * 0.7);
        sunlit_linear = srgb_to_linear(sunlit_linear);
    }
    else
    {
        vec3 sun_contrib = min(da, shadow) * sunlit_linear;
        color.rgb += sun_contrib;
    }
    color *= diffcol.rgb;

    float glare = 0.0;
    if (glossiness > 0.0)
    {
        vec3  lv = light_dir.xyz;
        vec3  h, l, v = -normalize(pos.xyz);
        float nh, nl, nv, vh, lightDist;
        vec3 n = norm.xyz;
        calcHalfVectors(lv, n, v, h, l, nh, nl, nv, vh, lightDist);
        if (nl > 0.0 && nh > 0.0)
        {
            float lit = min(nl*6.0, 1.0);
            float fres = pow(1 - vh, 5) * 0.4+0.5;
            float gtdenom = 2 * nh;
            float gt = max(0,(min(gtdenom * nv / vh, gtdenom * nl / vh)));
            float scol = shadow*fres*lightFuncLookup(vec2(nh, glossiness))*gt/(nh*nl);
            color.rgb += lit*scol*sunlit_linear.rgb*spec.rgb;
        }
        applyGlossEnv(color, glossenv, spec, pos.xyz, norm.xyz);
    }

    color = mix(color.rgb, diffcol.rgb, emissive);

    if (env > 0.0)
    {
        applyLegacyEnv(color, legacyenv, spec, pos.xyz, norm.xyz, env);
        float cur_glare = max(max(legacyenv.r, legacyenv.g), legacyenv.b);
        cur_glare = clamp(cur_glare, 0, 1);
        cur_glare *= env;
        glare += cur_glare;
    }

#ifdef SIMPLE_LIGHT
    color += vLocal * diffcol.rgb;
#else
    color += localLights(diffcol.rgb, pos, norm);
#endif
    color.rgb = applySkyAndWaterFog(pos.xyz, additive, atten, vec4(color, 1.0)).rgb;

    glare *= 1.0-emissive;
    glare = min(glare, 1.0);
    float al = max(diffcol.a, glare) * m.color.a;
    float final_scale = 1;
    if (SKY.classic_mode > 0)
        final_scale = 1.1;
    frag_color = max(vec4(color * final_scale, al), vec4(0));
#endif
}
