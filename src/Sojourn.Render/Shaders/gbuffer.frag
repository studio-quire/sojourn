#version 460
// Deferred G-buffer writer for opaque and alpha-masked faces. One shader covers the viewer's
// diffuse/bump/materialF (legacy, gbuffer holds sRGB) and pbropaqueF (PBR, gbuffer holds linear)
// deferred paths; which one a face takes is decided on the CPU (see Scene/FaceMaterials.cs) exactly as
// LLVolumeGeometryManager::registerFace would, and recorded in the material flags.
// Ported from the Second Life viewer's class3/deferred/materialF.glsl and class1/deferred/pbropaqueF.glsl
// (LGPL 2.1, Linden Research, Inc.).
#include "common.glsl"
#include "gbuffer_util.glsl"
#include "terrain_pbr.glsl"

layout(location = 0) in vec3 vWorldPos;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec4 vTangent;
layout(location = 3) in vec2 vUv;
layout(location = 4) flat in uint vInstance;
layout(location = 5) in vec3 vViewPos;

layout(location = 0) out vec4 frag_data0;
layout(location = 1) out vec4 frag_data1;
layout(location = 2) out vec4 frag_data2;
layout(location = 3) out vec4 frag_data3;

void main()
{
    Instance inst = INSTANCE(vInstance);
    Material m = MATERIAL(inst.material);

    // eye-space basis, as the viewer's vertex shaders hand them over (normal_matrix * normal)
    vec3 vN = normalize(mat3(VIEW.view) * vNormal);
    vec3 vT = normalize(mat3(VIEW.view) * vTangent.xyz);
    float sign = vTangent.w;
    float self = (inst.flags & INSTANCE_SELF) != 0u ? GBUFFER_SELF_MARK : 0.0;   // the hands' cut (handcut.frag)

    if ((m.flags & MAT_PBR) != 0u)
    {
        vec4 basecolor = sampleTex(m.diffuse.index, khrTransform(vUv, m.diffuse), vec4(1));
        basecolor.rgb = srgb_to_linear(basecolor.rgb);
        basecolor *= m.color;
        if (MAT_ALPHA_MODE(m) == ALPHA_MASK && basecolor.a < m.alphaCutoff) discard;

        vec3 vNt = sampleTex(m.normal.index, khrTransform(vUv, m.normal), vec4(0.5, 0.5, 1.0, 1.0)).xyz * 2.0 - 1.0;
        vec3 vB = sign * cross(vN, vT);
        vec3 tnorm = normalize(vNt.x * vT + vNt.y * vB + vNt.z * vN);

        // RGB = Occlusion, Roughness, Metal (defaults 1, 1, 1 when no map; factors scale)
        vec3 spec = sampleTex(m.specular.index, khrTransform(vUv, m.specular), vec4(1)).rgb;
        spec.g *= m.roughness;
        spec.b *= m.metallic;

        vec3 emissive = m.specColor.rgb;
        emissive *= srgb_to_linear(sampleTex(m.emissive.index, khrTransform(vUv, m.emissive), vec4(1)).rgb);

        tnorm *= gl_FrontFacing ? 1.0 : -1.0;

        frag_data0 = max(vec4(basecolor.rgb, 0.0), vec4(0));
        frag_data1 = max(vec4(spec, 0.0), vec4(0));
        frag_data2 = gbNormOut(encodeNormal(tnorm, 0.0, GBUFFER_FLAG_HAS_PBR + self));
        frag_data3 = max(vec4(emissive, 0.0), vec4(0));
        return;
    }

    if ((m.flags & MAT_TERRAIN) != 0u)
    {
        // probe captures cull terrain patches at the 64 m far clip (RenderReflectionProbeDrawDistance); our
        // terrain is one mesh, so clip per pixel at the face's far plane
        if (SKY.probe_capture_far > 0.0 && -vViewPos.z > SKY.probe_capture_far) discard;
        int ramp = int(m.bump);
        vec2 tc = vUv;
        float alpha1 = texture(textures[nonuniformEXT(ramp)], tc).a;
        float alpha2 = texture(textures[nonuniformEXT(ramp)], tc - vec2(2.0, 0.0)).a;
        float alphaFinal = texture(textures[nonuniformEXT(ramp)], tc - vec2(1.0, 0.0)).a;
        if ((m.flags & MAT_TERRAIN_PBR) != 0u)
        {
            // get_terrain_mix_weights: the ramp's blend of the four slots, slots under the threshold left out
            vec4 weight = mix(mix(vec4(0, 0, 0, 1), vec4(0, 0, 1, 0), alpha2), mix(vec4(0, 1, 0, 0), vec4(1, 0, 0, 0), alpha1), alphaFinal);
            weight -= TERRAIN_RAMP_MIX_THRESHOLD;
            vec4 usage = max(vec4(0), ceil(weight));
            weight *= usage;
            weight /= (weight.x + weight.y + weight.z + weight.w);

            vec3 p = vWorldPos - inst.model[3].xyz;   // region-local, as the viewer's terrain positions
            bool triplanar = m.envIntensity > 1.5;
            TerrainPbrMix pbr = terrainPbrZero();
            vec4 minimumAlphas = vec4(0);
            for (int i = 0; i < 4; i++)
            {
                Material d = MATERIAL(inst.material + 1u + uint(i));
                minimumAlphas[i] = d.alphaCutoff;
                if (usage[i] == 0.0) continue;
                vec4 t = terrainPbrTangent(vec4(vT, sign), vN, d.diffuse);
                TerrainPbrMix s = terrainPbrSlot(d, p, vNormal, triplanar, t.w);
                s.vNt = terrainPbrNormal(s.vNt, vN, t);
                pbr = terrainPbrAdd(pbr, s, weight[i]);
            }
            if (pbr.col.a < dot(weight, minimumAlphas)) discard;
            vec3 tnorm = normalize(pbr.vNt) * (gl_FrontFacing ? 1.0 : -1.0);
            frag_data0 = max(vec4(pbr.col.rgb, 0.0), vec4(0));
            frag_data1 = max(vec4(pbr.orm, 0.0), vec4(0));
            frag_data2 = gbNormOut(encodeNormal(tnorm, 0.0, GBUFFER_FLAG_HAS_PBR));
            frag_data3 = max(vec4(pbr.emissive, 0.0), vec4(0));
            return;
        }
        // classic terrain: ported from the viewer's class1/deferred/terrainV.glsl / terrainF.glsl.
        // Detail UV from the object-plane texgen; vUv is texcoord1 (composition, noise) into the alpha ramp.
        float detailScale = m.alphaCutoff;
        vec2 duv = vec2(vWorldPos.x * detailScale + m.metallic, vWorldPos.y * detailScale + m.roughness);
        vec4 color0 = sampleTex(m.diffuse.index, duv, vec4(0.5));
        vec4 color1 = sampleTex(m.normal.index, duv, vec4(0.5));
        vec4 color2 = sampleTex(m.specular.index, duv, vec4(0.5));
        vec4 color3 = sampleTex(m.emissive.index, duv, vec4(0.5));
        vec4 outColor = mix( mix(color3, color2, alpha2), mix(color1, color0, alpha1), alphaFinal );
        outColor.a = 0.0; // yes, downstream atmospherics
        frag_data0 = max(outColor, vec4(0));
        frag_data1 = vec4(0.0,0.0,0.0,-1.0);
        frag_data2 = gbNormOut(encodeNormal(normalize(vN), 0.0, GBUFFER_FLAG_HAS_ATMOS));
        frag_data3 = vec4(0);
        return;
    }

    // legacy: diffuse map combined with TE colour (both sRGB)
    vec4 diffcol = sampleTex(m.diffuse.index, teTransform(vUv, m.diffuse), vec4(1));
    if (MAT_ALPHA_MODE(m) == ALPHA_MASK)
    {
        // trees and grass test minimum_alpha exactly (treeF / diffuseAlphaMaskF); materialF's mask has a bias
        float bias = (m.flags & (MAT_FOLIAGE | MAT_AVATAR)) != 0u ? 0.0 : 0.001953125;   // avatarF tests minimum_alpha exactly
        if (diffcol.a < m.alphaCutoff - bias) discard;
    }
    diffcol.rgb *= m.color.rgb;

    float glossiness = m.specColor.a;
    vec4 spec = vec4(m.specColor.rgb, 1.0);
    if (m.specular.index >= 0)
    {
        spec = texture(textures[nonuniformEXT(m.specular.index)], teTransform(vUv, m.specular));
        spec.rgb *= m.specColor.rgb;
    }
    float env = m.envIntensity * spec.a;

    vec3 norm = vN;
    if (m.normal.index >= 0)
    {
        vec4 vNt = texture(textures[nonuniformEXT(m.normal.index)], teTransform(vUv, m.normal));
        if ((m.flags & MAT_LEGACY_MATERIAL) != 0u) glossiness *= vNt.a;
        vNt.xyz = vNt.xyz * 2.0 - 1.0;
        vec3 vB = sign * cross(vN, vT);
        norm = normalize(vNt.x * vT + vNt.y * vB + vNt.z * vN);
    }
    else norm = normalize(vN);

    float emissive = (m.flags & MAT_FULLBRIGHT) != 0u ? 1.0 : 0.0;
    if (MAT_ALPHA_MODE(m) == ALPHA_EMISSIVE) emissive = max(diffcol.a, emissive);

    frag_data0 = max(vec4(diffcol.rgb, emissive), vec4(0));
    frag_data1 = max(vec4(spec.rgb, glossiness), vec4(0));
    frag_data2 = gbNormOut(encodeNormal(norm, env, GBUFFER_FLAG_HAS_ATMOS + self));
    frag_data3 = vec4(0);
}
