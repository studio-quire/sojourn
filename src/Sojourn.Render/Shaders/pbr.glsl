// Ported from the Second Life viewer's class1/deferred/deferredUtil.glsl (LGPL 2.1, Linden Research, Inc.):
// half vectors, legacy distance attenuation, GLTF PBR punctual/IBL terms.
const float M_PI = 3.14159265;

float calcLegacyDistanceAttenuation(float distance, float falloff)
{
    float dist_atten = 1.0 - clamp((distance + falloff)/(1.0 + falloff), 0.0, 1.0);
    dist_atten *= dist_atten;
    // Tweak falloff slightly to match pre-EEP attenuation
    dist_atten *= 2.0;
    return dist_atten;
}

void calcHalfVectors(vec3 lv, vec3 n, vec3 v,
    out vec3 h, out vec3 l, out float nh, out float nl, out float nv, out float vh, out float lightDist)
{
    l  = normalize(lv);
    h  = normalize(l + v);
    float eps = 0.000001;
    nh = clamp(dot(n, h), eps, 1.0);
    nl = clamp(dot(n, l), eps, 1.0);
    nv = clamp(dot(n, v), eps, 1.0);
    vh = clamp(dot(v, h), eps, 1.0);
    lightDist = length(lv);
}

// shared lookup textures: bindless slots fixed at startup (Frame/Luts.cs)
#define SLOT_BRDF_LUT   4
#define SLOT_LIGHT_FUNC 5

vec2 BRDF(float NoV, float roughness)
{
    return texture(textures[SLOT_BRDF_LUT], vec2(NoV, roughness)).rg;
}

float lightFuncLookup(vec2 uv)
{
    return texture(textures[SLOT_LIGHT_FUNC], uv).r;
}

void pbrIbl(vec3 diffuseColor, vec3 specularColor, vec3 radiance, vec3 irradiance, float ao, float nv,
            float perceptualRough, out vec3 diffuseOut, out vec3 specularOut)
{
    vec2 brdf = BRDF(clamp(nv, 0, 1), 1.0-perceptualRough);
    vec3 diffuseLight = irradiance;
    vec3 specularLight = radiance;
    vec3 diffuse = diffuseLight * diffuseColor;
    vec3 specular = specularLight * (specularColor * brdf.x + brdf.y);
    diffuseOut = diffuse * ao;
    specularOut = specular * ao;
}

struct PBRInfo
{
    float NdotL;
    float NdotV;
    float NdotH;
    float LdotH;
    float VdotH;
    float perceptualRoughness;
    float metalness;
    vec3 reflectance0;
    vec3 reflectance90;
    float alphaRoughness;
    vec3 diffuseColor;
    vec3 specularColor;
};

vec3 pbrDiffuse(PBRInfo pbrInputs)
{
    return pbrInputs.diffuseColor / M_PI;
}

vec3 specularReflection(PBRInfo pbrInputs)
{
    return pbrInputs.reflectance0 + (pbrInputs.reflectance90 - pbrInputs.reflectance0) * pow(clamp(1.0 - pbrInputs.VdotH, 0.0, 1.0), 5.0);
}

float geometricOcclusion(PBRInfo pbrInputs)
{
    float NdotL = pbrInputs.NdotL;
    float NdotV = pbrInputs.NdotV;
    float r = pbrInputs.alphaRoughness;
    float attenuationL = 2.0 * NdotL / (NdotL + sqrt(r * r + (1.0 - r * r) * (NdotL * NdotL)));
    float attenuationV = 2.0 * NdotV / (NdotV + sqrt(r * r + (1.0 - r * r) * (NdotV * NdotV)));
    return attenuationL * attenuationV;
}

float microfacetDistribution(PBRInfo pbrInputs)
{
    float roughnessSq = pbrInputs.alphaRoughness * pbrInputs.alphaRoughness;
    float f = (pbrInputs.NdotH * roughnessSq - pbrInputs.NdotH) * pbrInputs.NdotH + 1.0;
    return roughnessSq / (M_PI * f * f);
}

void pbrPunctual(vec3 diffuseColor, vec3 specularColor, float perceptualRoughness, float metallic,
                    vec3 n, vec3 v, vec3 l, out float nl, out vec3 diff, out vec3 spec)
{
    perceptualRoughness = max(perceptualRoughness, 8.0/255.0);
    float alphaRoughness = perceptualRoughness * perceptualRoughness;
    float reflectance = max(max(specularColor.r, specularColor.g), specularColor.b);
    float reflectance90 = clamp(reflectance * 25.0, 0.0, 1.0);
    vec3 specularEnvironmentR0 = specularColor.rgb;
    vec3 specularEnvironmentR90 = vec3(1.0, 1.0, 1.0) * reflectance90;

    vec3 h = normalize(l+v);
    float NdotL = clamp(dot(n, l), 0.001, 1.0);
    float NdotV = clamp(abs(dot(n, v)), 0.001, 1.0);
    float NdotH = clamp(dot(n, h), 0.0, 1.0);
    float LdotH = clamp(dot(l, h), 0.0, 1.0);
    float VdotH = clamp(dot(v, h), 0.0, 1.0);

    PBRInfo pbrInputs = PBRInfo(NdotL, NdotV, NdotH, LdotH, VdotH, perceptualRoughness, metallic,
        specularEnvironmentR0, specularEnvironmentR90, alphaRoughness, diffuseColor, specularColor);

    vec3 F = specularReflection(pbrInputs);
    float G = geometricOcclusion(pbrInputs);
    float D = microfacetDistribution(pbrInputs);

    vec3 diffuseContrib = (1.0 - F) * pbrDiffuse(pbrInputs);
    vec3 specContrib = F * G * D / (4.0 * NdotL * NdotV);

    nl = NdotL;
    diff = diffuseContrib;
    spec = specContrib;
}

vec3 pbrCalcPointLightOrSpotLight(vec3 diffuseColor, vec3 specularColor, float perceptualRoughness, float metallic,
                    vec3 n, vec3 p, vec3 v, vec3 lp, vec3 ld, vec3 lightColor,
                    float lightSize, float falloff, float is_pointlight, float ambiance)
{
    vec3 color = vec3(0,0,0);
    vec3 lv = lp.xyz - p;
    float lightDist = length(lv);
    float dist = lightDist / lightSize;
    if (dist <= 1.0)
    {
        lv /= lightDist;
        float dist_atten = calcLegacyDistanceAttenuation(dist, falloff);
        float spot = max(dot(-ld, lv), is_pointlight);
        float spot_atten = spot*spot;
        vec3 intensity = spot_atten * dist_atten * lightColor * 3.0; //magic number to balance with legacy materials
        float nl = 0;
        vec3 diffPunc = vec3(0);
        vec3 specPunc = vec3(0);
        pbrPunctual(diffuseColor, specularColor, perceptualRoughness, metallic, n.xyz, v, lv, nl, diffPunc, specPunc);
        color = intensity * clamp(nl * (diffPunc + specPunc), vec3(0), vec3(10));
    }
    float final_scale = 1.0;
    if (SKY.classic_mode > 0)
        final_scale = 0.9;
    return color * final_scale;
}

void calcDiffuseSpecular(vec3 baseColor, float metallic, inout vec3 diffuseColor, inout vec3 specularColor)
{
    vec3 f0 = vec3(0.04);
    diffuseColor = baseColor*(vec3(1.0)-f0);
    diffuseColor *= 1.0 - metallic;
    specularColor = mix(f0, baseColor, metallic);
}

vec3 pbrBaseLight(vec3 diffuseColor, vec3 specularColor, float metallic, vec3 v, vec3 norm, float perceptualRoughness,
                  vec3 light_dir, vec3 sunlit, float scol, vec3 radiance, vec3 irradiance, vec3 colorEmissive, float ao,
                  vec3 additive, vec3 atten)
{
    vec3 color = vec3(0);
    float NdotV = clamp(abs(dot(norm, v)), 0.001, 1.0);
    vec3 iblDiff = vec3(0);
    vec3 iblSpec = vec3(0);
    pbrIbl(diffuseColor, specularColor, radiance, irradiance, ao, NdotV, perceptualRoughness, iblDiff, iblSpec);

    color += iblDiff;

    float nl = 0;
    vec3 diffPunc = vec3(0);
    vec3 specPunc = vec3(0);
    pbrPunctual(diffuseColor, specularColor, perceptualRoughness, metallic, norm, v, normalize(light_dir), nl, diffPunc, specPunc);

    if (SKY.classic_mode > 0)
    {
        irradiance.rgb = srgb_to_linear(irradiance * 0.9);
        float da = pow(nl, 1.2);
        vec3 sun_contrib = vec3(min(da, scol));
        sun_contrib = srgb_to_linear(linear_to_srgb(sun_contrib) * sunlit * 0.7) * M_PI;
        vec3 finalAmbient = irradiance.rgb * diffuseColor.rgb;
        vec3 finalSun = clamp(sun_contrib * ((diffPunc.rgb + specPunc.rgb) * scol), vec3(0), vec3(10));
        color.rgb = srgb_to_linear(linear_to_srgb(finalAmbient) + (linear_to_srgb(finalSun) * 1.1));
    }
    else
    {
        color += clamp(nl * (diffPunc + specPunc), vec3(0), vec3(10)) * sunlit * 3.0 * scol;
    }

    color.rgb += iblSpec.rgb;
    color += colorEmissive;
    return color;
}
