// Forward lighting for blended faces and particles: local lights as the viewer hands them to its alpha shaders, and
// class2/deferred/alphaF.glsl's legacy (no material) path. Ported from the Second Life viewer (LGPL 2.1, Linden
// Research, Inc.). Needs common.glsl, atmospherics.glsl, probes.glsl, shadows.glsl.

// the viewer feeds alpha shaders the nearest local lights through GL lights 2-7 (setupHWLights; light 1 is the
// avatar backlight, which enableLightsDynamic turns off)
#define MAX_FORWARD_LIGHTS 6u

vec3 calcPointLightOrSpotLight(vec3 light_col, vec3 diffuse, vec3 v, vec3 n, vec4 lp, vec3 ln, float la, float fa, float is_pointlight, float ambiance)
{
    // SL-14895 inverted attenuation work-around (see alphaF.glsl)
    float falloff_factor = (12.0 * fa) - 9.0;
    float inverted_la = falloff_factor / la;

    vec3 col = vec3(0);
    vec3 lv = lp.xyz-v;
    float dist = length(lv);
    float da = 1.0;

    if (dist > 0.0 && inverted_la > 0.0)
    {
        dist /= inverted_la;
        lv = normalize(lv);

        float dist_atten = clamp(1.0-(dist-1.0*(1.0-fa))/fa, 0.0, 1.0);
        dist_atten *= dist_atten;
        dist_atten *= 2.0f;

        if (dist_atten <= 0.0)
        {
           return col;
        }

        float spot = max(dot(-ln, lv), is_pointlight);
        da *= spot*spot; // GL_SPOT_EXPONENT=2
        da *= dot(n, lv);
        da = max(0.0, da);

        float lit = 0.0f;
        if (da > 0)
        {
            lit = clamp(da * dist_atten, 0.0, 1.0);
            col = lit * light_col * diffuse;
        }
    }

    float final_scale = 1.0;
    if (SKY.classic_mode > 0)
        final_scale = 0.9;
    return max(col * final_scale, vec3(0));
}

vec3 localLights(vec3 diffuse_linear, vec3 pos, vec3 norm)
{
    vec3 light = vec3(0);
    uint n = min(pc.i0, MAX_FORWARD_LIGHTS);
    for (uint i = 0u; i < n; ++i)
    {
        Light L = LIGHTS[i];
        vec4 lp = VIEW.view * vec4(L.positionRadius.xyz, 1.0);
        vec3 ln = normalize(mat3(VIEW.view) * L.directionCutoff.xyz);
        light += calcPointLightOrSpotLight(L.colorFalloff.rgb, diffuse_linear, pos, norm, lp, ln, L.params.x, L.params.y, L.params.z, L.params.w);
    }
    return light;
}

// alphaF.glsl (USE_DIFFUSE_TEX / USE_VERTEX_COLOR): diffuse_linear already holds texture times colour, linear.
vec4 alphaLitLegacy(vec4 diffuse_linear, vec3 pos, vec3 norm, float shadow)
{
    vec3 light_dir = (SKY.sun_up_factor == 1) ? SKY.sun_dir : SKY.moon_dir;
    vec3 sunlit, amblit, additive, atten;
    calcAtmosphericVarsLinear(pos.xyz, norm, light_dir, sunlit, amblit, additive, atten);
    if (SKY.classic_mode > 0)
        sunlit *= 1.35;
    vec3 sunlit_linear = sunlit;
    vec3 amblit_linear = amblit;
    vec3 irradiance = amblit;
    vec3 glossenv = vec3(0.0);
    vec3 legacyenv = vec3(0.0);
    sampleReflectionProbesLegacy(irradiance, glossenv, legacyenv, vec2(0), pos.xyz, norm.xyz, 0.0, 0.0, true, amblit_linear);

    float da = clamp(dot(norm.xyz, light_dir.xyz), -1.0, 1.0);
    float final_da = clamp(da, 0.0f, 1.0f);

    vec4 color = vec4(0.0);
    color.a   = diffuse_linear.a;
    color.rgb = irradiance;
    if (SKY.classic_mode > 0)
    {
        final_da = pow(final_da,1.2);
        vec3 sun_contrib = vec3(min(final_da, shadow));
        color.rgb = srgb_to_linear(color.rgb * 0.9 + linear_to_srgb(sun_contrib) * sunlit_linear * 0.7);
        sunlit_linear = srgb_to_linear(sunlit_linear);
    }
    else
    {
        vec3 sun_contrib = min(final_da, shadow) * sunlit_linear;
        color.rgb += sun_contrib;
    }
    color.rgb *= diffuse_linear.rgb;
    color.rgb += localLights(diffuse_linear.rgb, pos, norm);
    color.rgb = applySkyAndWaterFog(pos.xyz, additive, atten, color).rgb;

    float final_scale = 1;
    if (SKY.classic_mode > 0)
        final_scale = 1.1;
    color.rgb *= final_scale;
    return max(color, vec4(0));
}

// alphaLitLegacy with its ambient (the probes'), sunlight, local lights and atmospherics computed per vertex
// (alpha_simple.vert, the simpler lighting for blended faces); the sun's angle and shadow per pixel. Claude Opus 5.5.
vec4 alphaLitSimple(vec4 diffuse_linear, vec3 pos, vec3 norm, float shadow, vec3 irradiance, vec3 sunlit, vec3 local, vec3 additive, vec3 atten)
{
    vec3 light_dir = (SKY.sun_up_factor == 1) ? SKY.sun_dir : SKY.moon_dir;
    if (SKY.classic_mode > 0)
        sunlit *= 1.35;
    vec3 sunlit_linear = sunlit;
    float final_da = clamp(dot(norm.xyz, light_dir.xyz), 0.0, 1.0);
    vec4 color = vec4(irradiance, diffuse_linear.a);
    if (SKY.classic_mode > 0)
    {
        final_da = pow(final_da, 1.2);
        vec3 sun_contrib = vec3(min(final_da, shadow));
        color.rgb = srgb_to_linear(color.rgb * 0.9 + linear_to_srgb(sun_contrib) * sunlit_linear * 0.7);
    }
    else
        color.rgb += min(final_da, shadow) * sunlit_linear;
    color.rgb *= diffuse_linear.rgb;
    color.rgb += local * diffuse_linear.rgb;
    color.rgb = applySkyAndWaterFog(pos.xyz, additive, atten, color).rgb;
    if (SKY.classic_mode > 0)
        color.rgb *= 1.1;
    return max(color, vec4(0));
}
