// Ported from the Second Life viewer (LGPL 2.1, Linden Research, Inc.):
// class1/windlight/atmosphericsFuncs.glsl, atmosphericsF.glsl, class1/environment/waterFogF.glsl.
// Uniforms read from the per-view SkyBlock (SKY.name == the viewer's uniform `name`).

float getAmbientClamp() { return 1.0f; }

// return colors in sRGB space
void calcAtmosphericVars(vec3 inPositionEye, vec3 light_dir, float ambFactor, out vec3 sunlit, out vec3 amblit, out vec3 additive,
                         out vec3 atten)
{
    vec3 rel_pos = inPositionEye;

    //(TERRAIN) limit altitude
    if (abs(rel_pos.y) > SKY.max_y) rel_pos *= (SKY.max_y / rel_pos.y);

    vec3  rel_pos_norm = normalize(rel_pos);
    float rel_pos_len  = length(rel_pos);

    vec3  sunlight     = (SKY.sun_up_factor == 1) ? SKY.sunlight_color : SKY.moonlight_color;

    // sunlight attenuation effect (hue and brightness) due to atmosphere
    // this is used later for sunlight modulation at various altitudes
    vec3 light_atten = (SKY.blue_density + vec3(SKY.haze_density * 0.25)) * (SKY.density_multiplier * SKY.max_y);

    vec3 combined_haze = max(SKY.blue_density + vec3(SKY.haze_density), vec3(1e-6));
    vec3 blue_weight   = SKY.blue_density / combined_haze;
    vec3 haze_weight   = vec3(SKY.haze_density) / combined_haze;

    //(TERRAIN) compute sunlight from lightnorm y component. Factor is roughly cosecant(sun elevation) (for short rays like terrain)
    float above_horizon_factor = 1.0 / max(1e-6, SKY.lightnorm.y);
    sunlight *= exp(-light_atten * above_horizon_factor);  // for sun [horizon..overhead] this maps to an exp curve [0..1]

    // main atmospheric scattering line integral
    float density_dist = rel_pos_len * SKY.density_multiplier;

    combined_haze = exp(-combined_haze * density_dist * SKY.distance_multiplier);

    // final atmosphere attenuation factor
    atten = combined_haze.rgb;

    // compute haze glow
    float haze_glow = dot(rel_pos_norm, SKY.lightnorm.xyz);

    // dampen sun additive contrib when not facing it...
    haze_glow *= max(0.0f, dot(light_dir, rel_pos_norm));

    haze_glow = 1. - haze_glow;
    // haze_glow is 0 at the sun and increases away from sun
    haze_glow = max(haze_glow, .001);  // set a minimum "angle" (smaller glow.y allows tighter, brighter hotspot)
    haze_glow *= SKY.glow.x;
    // higher glow.x gives dimmer glow (because next step is 1 / "angle")
    haze_glow = clamp(pow(haze_glow, SKY.glow.z), -100000, 100000);
    // glow.z should be negative, so we're doing a sort of (1 / "angle") function

    // add "minimum anti-solar illumination"
    haze_glow += .25;

    haze_glow *= SKY.sun_moon_glow_factor;

    vec3 amb_color = SKY.ambient_color;

    // increase ambient when there are more clouds
    vec3 tmpAmbient = amb_color + (vec3(1.) - amb_color) * SKY.cloud_shadow * 0.5;

    // haze color
    vec3 cs = sunlight.rgb * (1. - SKY.cloud_shadow);
    additive = (SKY.blue_horizon.rgb * blue_weight.rgb) * (cs + tmpAmbient.rgb) + (SKY.haze_horizon * haze_weight.rgb) * (cs * haze_glow + tmpAmbient.rgb);

    // brightness of surface both sunlight and ambient
    sunlit = sunlight.rgb;
    amblit = pow(tmpAmbient.rgb, vec3(0.9)) * 0.57;

    additive *= vec3(1.0 - combined_haze);

    // sanity clamp haze contribution
    additive = min(additive, vec3(10));
}

// provide a touch of lighting in the opposite direction of the sun light
// so areas in shadow don't lose all detail
float ambientLighting(vec3 norm, vec3 light_dir)
{
    float ambient = min(abs(dot(norm.xyz, light_dir.xyz)), 1.0);
    ambient *= 0.5;
    ambient *= ambient;
    ambient = (1.0 - ambient);
    return ambient;
}

// return lit amblit in linear space, leave sunlit and additive in sRGB space
void calcAtmosphericVarsLinear(vec3 inPositionEye, vec3 norm, vec3 light_dir, out vec3 sunlit, out vec3 amblit, out vec3 additive,
                         out vec3 atten)
{
    calcAtmosphericVars(inPositionEye, light_dir, 1.0, sunlit, amblit, additive, atten);

    amblit *= ambientLighting(norm, light_dir);

    if (SKY.classic_mode < 1)
    {
        amblit = srgb_to_linear(amblit);
        amblit = vec3(dot(amblit, vec3(0.2126, 0.7152, 0.0722)));
        sunlit = srgb_to_linear(sunlit);
    }

    // multiply to get similar colors as when the "scaleSoftClip" implementation was doubling color values
    // (allows for mixing of light sources other than sunlight e.g. reflection probes)
    sunlit *= SKY.sky_sunlight_scale;
    amblit *= SKY.sky_ambient_scale;
}

vec3 atmosFragLighting(vec3 light, vec3 additive, vec3 atten)
{
    light *= atten.r;
    additive = srgb_to_linear(additive*2.0);
    additive *= SKY.sky_hdr_scale;
    light += additive;
    return light;
}

vec3 atmosFragLightingLinear(vec3 light, vec3 additive, vec3 atten)
{
    return atmosFragLighting(light, additive, atten);
}

// --- water fog (waterFogF.glsl)

vec4 getWaterFogViewNoClip(vec3 pos)
{
    vec3 view = normalize(pos);
    //normalize view vector
    float es = -(dot(view, SKY.waterPlane.xyz));

    //find intersection point with water plane and eye vector

    //get eye depth
    float e0 = max(-SKY.waterPlane.w, 0.0);

    vec3 int_v = SKY.waterPlane.w > 0.0 ? view * SKY.waterPlane.w/es : vec3(0.0, 0.0, 0.0);

    //get object depth
    float depth = length(pos - int_v);

    //get "thickness" of water
    float l = max(depth, 0.1);

    float kd = SKY.waterFogDensity;
    float ks = SKY.waterFogKS;
    vec4 kc = SKY.waterFogColor;

    float F = 0.98;

    float t1 = -kd * pow(F, ks * e0);
    float t2 = kd + ks * es;
    float t3 = pow(F, t2*l) - 1.0;

    float L = pow(min(t1/t2*t3, 1.0), 1.0/1.7);

    float D = pow(0.98, l*kd);

    return vec4(srgb_to_linear(kc.rgb)*L, D);
}

vec4 getWaterFogView(vec3 pos)
{
    if (dot(pos, SKY.waterPlane.xyz) + SKY.waterPlane.w > 0.0)
    {
        return vec4(0,0,0,1);
    }

    return getWaterFogViewNoClip(pos);
}

vec4 applyWaterFogViewLinearNoClip(vec3 pos, vec4 color)
{
    vec4 fogged = getWaterFogViewNoClip(pos);
    color.rgb *= fogged.a;
    color.rgb += fogged.rgb;
    return color;
}

vec4 applyWaterFogViewLinear(vec3 pos, vec4 color)
{
    if (dot(pos, SKY.waterPlane.xyz) + SKY.waterPlane.w > 0.0)
    {
        return color;
    }

    return applyWaterFogViewLinearNoClip(pos, color);
}

// for post deferred shaders, apply sky and water fog in a way that is consistent with
// the deferred rendering haze post effects
vec4 applySkyAndWaterFog(vec3 pos, vec3 additive, vec3 atten, vec4 color)
{
    bool eye_above_water = dot(vec3(0), SKY.waterPlane.xyz) + SKY.waterPlane.w > 0.0;
    bool obj_above_water = dot(pos.xyz, SKY.waterPlane.xyz) + SKY.waterPlane.w > 0.0;

    if (eye_above_water)
    {
        if (!obj_above_water)
        {
            color.rgb = applyWaterFogViewLinearNoClip(pos, color).rgb;
        }
        else
        {
            color.rgb = atmosFragLighting(color.rgb, additive, atten);
        }
    }
    else
    {
        if (obj_above_water)
        {
            color.rgb = atmosFragLighting(color.rgb, additive, atten);
        }
        else
        {
            color.rgb = applyWaterFogViewLinearNoClip(pos, color).rgb;
        }
    }

    return color;
}
