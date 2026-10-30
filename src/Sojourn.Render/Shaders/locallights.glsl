// The local (point) lights' light at one pixel (locallights.frag). Ported from the Second Life viewer's class3/deferred/pointLightF.glsl /
// multiPointLightF.glsl (LGPL 2.1, Linden Research, Inc.). Needs common, deferred and pbr.glsl.
#include "tiles.glsl"

// pos: eye space; the lights in LIGHTS (lightCount of them, or the pixel's tile's)
vec3 localLights(vec3 pos, GBufferInfo gb, uint lightCount)
{
    vec3 n = gb.normal;
    vec3 diffuse = gb.albedo.rgb;
    vec4 spec    = gb.specular;
    vec3 v = -normalize(pos);
    bool pbr = GET_GBUFFER_FLAG(gb.gbufferFlag, GBUFFER_FLAG_HAS_PBR);

    vec3 diffuseColor = vec3(0), specularColor = vec3(0);
    float perceptualRoughness = 0, metallic = 0;
    if (pbr)
    {
        vec3 orm = spec.rgb;
        perceptualRoughness = orm.g;
        metallic = orm.b;
        vec3 f0 = vec3(0.04);
        diffuseColor = diffuse.rgb*(vec3(1.0)-f0);
        diffuseColor *= 1.0 - metallic;
        specularColor = mix(f0, diffuse.rgb, metallic);
    }
    else
    {
        diffuse = srgb_to_linear(diffuse);
        spec.rgb = srgb_to_linear(spec.rgb);
    }

    vec3 final_color = vec3(0);
    // the lights that reach this pixel's tile (tiles.glsl), or every light
    TileListRef tiles;
    uint base;
    uint header = tileHeader(tiles, base);
    bool all = header == TILE_ALL;
    // bounded whatever the counts say: a tile record holds at most TILE_STRIDE - 1 indices, a view at most 256 lights
    uint count = all ? min(lightCount, 1024u) : min(header & 0xFFFFu, TILE_STRIDE - 1u);
    if (count == 0u) return vec3(0);
    // most lights don't reach most pixels: test the distance in region space first (one subtraction and a dot
    // product a light), before turning the light into view space
    vec3 world = (VIEW.invView * vec4(pos, 1.0)).xyz;
    for (uint k = 0u; k < count; ++k)
    {
        uint i = all ? k : tiles.d[base + 1u + k];
        if (i >= lightCount) continue;
        vec4 pr = LIGHTS[i].positionRadius;
        vec3 dw = pr.xyz - world;
        if (dot(dw, dw) >= pr.w * pr.w) continue;
        Light L = LIGHTS[i];
        if (L.directionCutoff.w > -1.5) continue;   // spotlights: projector pass (not yet)
        vec3 center = (VIEW.view * vec4(L.positionRadius.xyz, 1.0)).xyz;
        float size = L.positionRadius.w;
        vec3 color = L.colorFalloff.rgb;
        float falloff = L.colorFalloff.w;

        vec3 lv = center - pos;
        vec3 h, l;
        float nh, nl, nv, vh, lightDist;
        calcHalfVectors(lv, n, v, h, l, nh, nl, nv, vh, lightDist);
        if (lightDist >= size) continue;

        float dist = lightDist / size;
        float dist_atten = calcLegacyDistanceAttenuation(dist, falloff);

        if (pbr)
        {
            vec3 intensity = dist_atten * color * 3.25;
            float pnl = 0;
            vec3 diffPunc = vec3(0);
            vec3 specPunc = vec3(0);
            pbrPunctual(diffuseColor, specularColor, perceptualRoughness, metallic, n.xyz, v, normalize(lv), pnl, diffPunc, specPunc);
            final_color += intensity * clamp(pnl * (diffPunc + specPunc), vec3(0), vec3(10));
        }
        else
        {
            if (dot(n, lv) <= 0.0) continue;
            float lit = nl * dist_atten;
            vec3 col = color.rgb*lit*diffuse;
            if (spec.a > 0.0)
            {
                lit = min(nl*6.0, 1.0) * dist_atten;
                float fres = pow(1 - vh, 5) * 0.4+0.5;
                float gtdenom = 2 * nh;
                float gt = max(0,(min(gtdenom * nv / vh, gtdenom * nl / vh)));
                if (nh > 0.0)
                {
                    float scol = fres*lightFuncLookup(vec2(nh, spec.a))*gt/(nh*nl);
                    col += lit*scol*color.rgb*spec.rgb;
                }
            }
            final_color += col;
        }
    }

    float final_scale = 1.0;
    if (SKY.local_light_classic_mode > 0)
        final_scale = 0.9;
    return max(final_color * final_scale, vec3(0));
}
