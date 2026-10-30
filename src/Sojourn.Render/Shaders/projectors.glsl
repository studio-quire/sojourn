// Projectors' light at one pixel (projectors.frag). Ported from the Second Life viewer's class3/deferred/spotLightF.glsl and the projector
// helpers of class1/deferred/deferredUtil.glsl (LGPL 2.1, Linden Research, Inc.); see projectors.frag. Needs common,
// deferred and pbr.glsl.
#include "projector.glsl"
#include "tiles.glsl"

// deferredUtil.glsl: getTexture2DLodAmbient / getTexture2DLodDiffuse / texture2DLodSpecular
vec4 projTap(int slot, vec2 tc, float lod)
{
    vec4 ret = slot >= 0 ? textureLod(textures[nonuniformEXT(slot)], tc, lod) : vec4(1.0);
    ret.rgb = srgb_to_linear(ret.rgb);
    return ret;
}

vec4 getTexture2DLodAmbient(int slot, vec2 tc, float lod)
{
    vec4 ret = projTap(slot, tc, lod);
    vec2 dist = tc-vec2(0.5);
    float d = dot(dist,dist);
    ret *= min(clamp((0.25-d)/0.25, 0.0, 1.0), 1.0);
    return ret;
}

vec4 getTexture2DLodDiffuse(int slot, vec2 tc, float lod, float proj_lod)
{
    vec4 ret = projTap(slot, tc, lod);
    vec2 dist = vec2(0.5) - abs(tc-vec2(0.5));
    float det = min(lod/(proj_lod*0.5), 1.0);
    float d = min(dist.x, dist.y);
    float edge = 0.25*det;
    ret *= clamp(d/edge, 0.0, 1.0);
    return ret;
}

vec4 texture2DLodSpecular(int slot, vec2 tc, float lod, float proj_lod)
{
    vec4 ret = projTap(slot, tc, lod);
    vec2 dist = vec2(0.5) - abs(tc-vec2(0.5));
    float det = min(lod/(proj_lod*0.5), 1.0);
    float d = min(dist.x, dist.y);
    d *= min(1, d * (proj_lod - lod)); // BUG? extra factor compared to diffuse causes N repeats (viewer comment)
    float edge = 0.25*det;
    ret *= clamp(d/edge, 0.0, 1.0);
    return ret;
}

vec3 getProjectedLightAmbiance(Projector P, float amb_da, float attenuation, float lit, float nl, float noise, vec2 projected_uv)
{
    vec4 amb_plcol = getTexture2DLodAmbient(P.texture, projected_uv, P.lod);
    vec3 amb_rgb   = amb_plcol.rgb * amb_plcol.a;

    float proj_ambiance = P.planeAmbiance.w;
    amb_da += proj_ambiance;
    amb_da += (nl*nl*0.5+0.5) * proj_ambiance;
    amb_da *= attenuation * noise;
    amb_da = min(amb_da, 1.0-lit);

    return (amb_da * P.colorFalloff.rgb * amb_rgb);
}

vec3 getProjectedLightDiffuseColor(Projector P, float light_distance, vec2 projected_uv)
{
    float diff = clamp((light_distance - P.normalFocus.w)/P.originRange.w, 0.0, 1.0);
    float lod = diff * P.lod;
    vec4 plcol = getTexture2DLodDiffuse(P.texture, projected_uv.xy, lod, P.lod);
    return P.colorFalloff.rgb * plcol.rgb * plcol.a;
}


// posEye: eye space; projectors (projectorCount of them, or the pixel's tile's)
vec3 projectorLights(vec2 tc, vec3 posEye, GBufferInfo gb, ProjectorRef projectors, uint projectorCount)
{
    // into region space, where the projectors live
    vec3 pos = (VIEW.invView * vec4(posEye, 1.0)).xyz;
    vec3 cam = VIEW.invView[3].xyz;
    vec3 n = normalize(mat3(VIEW.invView) * gb.normal);
    vec3 v = normalize(cam - pos);
    bool pbr = GET_GBUFFER_FLAG(gb.gbufferFlag, GBUFFER_FLAG_HAS_PBR);

    vec3 albedo = gb.albedo.rgb;
    vec4 specIn = gb.specular;
    float envIntensity = gb.envIntensity;
    vec2 shd = texture(textures[nonuniformEXT(SLOT_LIGHTMAP)], tc).ba;

    // the projectors that reach this pixel's tile (tiles.glsl), after its lights, or every projector
    TileListRef tiles;
    uint base;
    uint header = tileHeader(tiles, base);
    bool all = header == TILE_ALL;
    // bounded whatever the counts say (a tile record holds at most TILE_STRIDE - 1 indices)
    uint count = all ? min(projectorCount, 1024u) : min(header >> 16, TILE_STRIDE - 1u);
    if (count == 0u) return vec3(0);
    base += 1u + min(header & 0xFFFFu, TILE_STRIDE - 1u);
    vec3 total = vec3(0);
    for (uint k = 0u; k < count; ++k)
    {
        uint pi = all ? k : tiles.d[base + k];
        if (pi >= projectorCount) continue;
        // clipProjectedLightVars (the sphere first, before reading the rest of the projector)
        vec4 cs = projectors.p[pi].centerSize;
        vec3 lv = cs.xyz - pos;
        float dist = length(lv);
        if (dist >= cs.w) continue;
        Projector P = projectors.p[pi];
        float size = cs.w;
        vec3 color = P.colorFalloff.rgb;
        float falloff = P.colorFalloff.w;
        vec3 proj_n = P.normalFocus.xyz;
        dist /= size;
        float l_dist = -dot(lv, proj_n);
        vec4 proj_tc = P.regionToTc * vec4(pos, 1.0);
        if (proj_tc.z < 0.0) continue;
        proj_tc.xyz /= proj_tc.w;
        // outside the projector's frustum only a legacy face's environment reflection takes any of its light (both
        // paths' diffuse and specular need the frustum): skip the rest there, the same result for less work
        bool inFrustum = proj_tc.x > 0.0 && proj_tc.x < 1.0 && proj_tc.y > 0.0 && proj_tc.y < 1.0;
        if (!inFrustum && (pbr || envIntensity <= 0.0)) continue;

        float shadow = 1.0;
        if (P.shadowIndex >= 0)
        {
            shadow = P.shadowIndex == 0 ? shd.x : shd.y;
            shadow += P.shadowFade;
            shadow = clamp(shadow, 0.0, 1.0);
        }

        float dist_atten = calcLegacyDistanceAttenuation(dist, falloff);
        if (dist_atten <= 0.0) continue;

        lv = P.originRange.xyz - pos;
        vec3 h, l;
        float nh, nl, nv, vh, lightDist;
        calcHalfVectors(lv, n, v, h, l, nh, nl, nv, vh, lightDist);

        vec3 diffuse = albedo;
        vec4 spec = specIn;
        vec3 dlit = vec3(0);
        vec3 amb_rgb = vec3(0);
        vec3 final_color = vec3(0);

        if (pbr)
        {
            vec3 orm = spec.rgb;
            float perceptualRoughness = orm.g;
            float metallic = orm.b;
            vec3 f0 = vec3(0.04);
            vec3 baseColor = diffuse.rgb;

            vec3 diffuseColor = baseColor.rgb*(vec3(1.0)-f0);
            diffuseColor *= 1.0 - metallic;

            vec3 specularColor = mix(f0, baseColor.rgb, metallic);
            vec3 diffPunc = vec3(0);
            vec3 specPunc = vec3(0);

            if (proj_tc.x > 0.0 && proj_tc.x < 1.0 && proj_tc.y > 0.0 && proj_tc.y < 1.0)
            {
                float lit = 0.0;
                float amb_da = 0.0;

                lv = normalize(lv);

                if (nl > 0.0)
                {
                    amb_da += (nl*0.5 + 0.5) * P.planeAmbiance.w;

                    dlit = getProjectedLightDiffuseColor(P, l_dist, proj_tc.xy);

                    vec3 intensity = dist_atten * dlit * 3.25 * shadow; // Legacy attenuation, magic number to balance with legacy materials

                    pbrPunctual(diffuseColor, specularColor, perceptualRoughness, metallic, n.xyz, v, normalize(lv), nl, diffPunc, specPunc);

                    final_color += intensity * clamp(nl * (diffPunc + specPunc), vec3(0), vec3(10));
                }

                amb_rgb = getProjectedLightAmbiance(P, amb_da, dist_atten, lit, nl, 1.0, proj_tc.xy) * 3.25; //magic number to balance with legacy ambiance
                pbrPunctual(diffuseColor, specularColor, perceptualRoughness, metallic, n.xyz, v, normalize(lv), nl, diffPunc, specPunc);

                final_color += amb_rgb * clamp(nl * (diffPunc + specPunc), vec3(0), vec3(10));
            }
        }
        else
        {
            diffuse = srgb_to_linear(diffuse);
            spec.rgb = srgb_to_linear(spec.rgb);

            if (proj_tc.z > 0.0 && proj_tc.x < 1.0 && proj_tc.y < 1.0 && proj_tc.x > 0.0 && proj_tc.y > 0.0)
            {
                float amb_da = 0;
                float lit = 0.0;

                if (nl > 0.0)
                {
                    lit = nl * dist_atten;

                    dlit = getProjectedLightDiffuseColor(P, l_dist, proj_tc.xy);

                    final_color = dlit*lit*diffuse*shadow;

                    // unshadowed for consistency between forward and deferred?
                    amb_da += (nl*0.5+0.5) * P.planeAmbiance.w;
                }

                amb_rgb = getProjectedLightAmbiance(P, amb_da, dist_atten, lit, nl, 1.0, proj_tc.xy);
                final_color += diffuse.rgb * amb_rgb * max(dot(-normalize(lv), n), 0.0);
            }

            if (spec.a > 0.0)
            {
                dlit *= min(nl*6.0, 1.0) * dist_atten;

                float fres = pow(1 - vh, 5)*0.4+0.5;

                float gtdenom = 2 * nh;
                float gt = max(0, min(gtdenom * nv / vh, gtdenom * nl / vh));

                if (nh > 0.0)
                {
                    float scol = fres*lightFuncLookup(vec2(nh, spec.a))*gt/(nh*nl);
                    vec3 speccol = dlit*scol*spec.rgb*shadow;
                    speccol = clamp(speccol, vec3(0), vec3(1));
                    final_color += speccol;
                }
            }

            if (envIntensity > 0.0)
            {
                vec3 ref = reflect(-v, n);

                //project from point pos in direction ref to plane proj_p, proj_n
                vec3 pdelta = P.planeAmbiance.xyz-pos;
                float ds = dot(ref, proj_n);

                if (ds < 0.0)
                {
                    vec3 pfinal = pos + ref * dot(pdelta, proj_n)/ds;

                    vec4 stc = P.regionToTc * vec4(pfinal.xyz, 1.0);

                    if (stc.z > 0.0)
                    {
                        stc /= stc.w;

                        if (stc.x < 1.0 && stc.y < 1.0 && stc.x > 0.0 && stc.y > 0.0)
                        {
                            final_color += color.rgb * texture2DLodSpecular(P.texture, stc.xy, (1 - spec.a) * (P.lod * 0.6), P.lod).rgb * shadow * envIntensity;
                        }
                    }
                }
            }
        }

        //not sure why, but this line prevents MATBUG-194
        final_color = max(final_color, vec3(0.0));
        total += final_color;
    }

    float final_scale = 1.0;
    if (SKY.local_light_classic_mode > 0)
        final_scale = 0.9;
    //output linear
    return total * final_scale;
}
