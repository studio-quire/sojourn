// The WindLight cloud layer on the sky dome. Ported from the Second Life viewer's class1/deferred/cloudsV.glsl
// and cloudsF.glsl (LGPL 2.1, Linden Research, Inc.). The dome's planar UVs come from the unit direction of the
// dome vertex (LLVOWLSky::buildStripsBuffer): ((-z0 + 1) / 2, (-x0 + 1) / 2) in the dome's y-up frame.
// Push block i1 = cloud_noise_texture | cloud_noise_texture_next << 16 (0xFFFF = none).

vec4 cloudNoise(vec2 uv)
{
    int a = int(pc.i1 & 0xFFFFu), b = int(pc.i1 >> 16);
    vec4 sa = a != 0xFFFF ? texture(textures[nonuniformEXT(a)], uv) : vec4(0.5);
    vec4 sb = b != 0xFFFF ? texture(textures[nonuniformEXT(b)], uv) : sa;
    return mix(sa, sb, SKY.cloud_blend_factor);
}

// pos: dome-frame position of the dome point (y up); domeDir: unit direction of that point from the dome centre.
// Returns cloud colour (rgb, as cloudsF writes it) and its alpha.
vec4 cloudColor(vec3 pos, vec3 domeDir)
{
    if (SKY.cloud_scale < 0.001 || SKY.clouds_visible < 0.5) return vec4(0);

    // cloudsV
    vec2 texcoord0 = vec2((-domeDir.z + 1.0) / 2.0, (-domeDir.x + 1.0) / 2.0);
    vec2 vary_texcoord0 = vec2(-texcoord0.x, texcoord0.y);
    vary_texcoord0.xy -= 0.5;
    vary_texcoord0.xy /= SKY.cloud_scale;
    vary_texcoord0.xy += 0.5;
    vec2 vary_texcoord1 = vary_texcoord0;
    vary_texcoord1.x += SKY.lightnorm.x * 0.0125;
    vary_texcoord1.y += SKY.lightnorm.z * 0.0125;
    vec2 vary_texcoord2 = vary_texcoord0 * 16.;
    vec2 vary_texcoord3 = vary_texcoord1 * 16.;

    // cloudsF
    vec2 uv1 = vary_texcoord0.xy;
    vec2 uv2 = vary_texcoord1.xy;
    vec2 uv3 = vary_texcoord2.xy;
    vec2 uv4 = vary_texcoord3.xy;

    vec3 camPosLocal = vec3(0, SKY.cam_height, 0);
    vec3 rel_pos = pos.xyz - camPosLocal.xyz + vec3(0, 50, 0);
    float altitude_blend_factor = clamp((rel_pos.y + 512.0) / SKY.max_y, 0.0, 1.0);

    if (rel_pos.y > 0)
    {
        rel_pos *= (SKY.max_y / rel_pos.y);
    }
    if (rel_pos.y < 0)
    {
        altitude_blend_factor = 0; // SL-11589 Fix clouds drooping below horizon
        rel_pos *= (-32000. / rel_pos.y);
    }

    vec3  rel_pos_norm = normalize(rel_pos);
    float rel_pos_len  = length(rel_pos);

    vec3 sunlight = SKY.sky_sunlight_color;
    vec3 light_atten;

    light_atten = (SKY.blue_density + vec3(SKY.haze_density * 0.25)) * (SKY.density_multiplier * SKY.max_y);

    vec3 combined_haze = abs(SKY.blue_density) + vec3(abs(SKY.haze_density));
    vec3 blue_weight   = SKY.blue_density / combined_haze;
    vec3 haze_weight   = SKY.haze_density / combined_haze;

    float off_axis = 1.0 / max(1e-6, max(0., rel_pos_norm.y) + SKY.lightnorm.y);
    sunlight *= exp(-light_atten * off_axis);

    float density_dist = rel_pos_len * SKY.density_multiplier;

    combined_haze = exp(-combined_haze * density_dist);

    float haze_glow = 1.0 - dot(rel_pos_norm, SKY.lightnorm.xyz);
    haze_glow = max(haze_glow, .001);
    haze_glow *= SKY.glow.x;
    haze_glow = pow(haze_glow, SKY.glow.z);
    haze_glow *= SKY.sun_moon_glow_factor;
    haze_glow = (SKY.sun_moon_glow_factor < 1.0) ? 0.0 : (haze_glow + 0.25);

    vec3 tmpAmbient = SKY.ambient_color;
    tmpAmbient += (1. - tmpAmbient) * SKY.cloud_shadow * 0.5;

    sunlight *= (1. - SKY.cloud_shadow);

    vec3 additiveColorBelowCloud =
        (SKY.blue_horizon * blue_weight * (sunlight + tmpAmbient) + (SKY.haze_horizon * haze_weight) * (sunlight * haze_glow + tmpAmbient));

    // CLOUDS
    sunlight = SKY.sky_sunlight_color;
    off_axis = 1.0 / max(1e-6, SKY.lightnorm.y * 2.);
    sunlight *= exp(-light_atten * off_axis);

    vec3 cloudColorSun = (sunlight * haze_glow) * SKY.cloud_color;
    vec3 cloudColorAmbient = tmpAmbient * SKY.cloud_color;

    combined_haze = sqrt(combined_haze);
    cloudColorSun *= combined_haze;
    cloudColorAmbient *= combined_haze;
    vec3 oHazeColorBelowCloud = additiveColorBelowCloud * (1. - combined_haze);

    float cloudDensity = 2. * (SKY.cloud_shadow - 0.25);

    cloudColorAmbient += oHazeColorBelowCloud;

    vec2 disturbance  = vec2(cloudNoise(uv1 / 8.0f).x, cloudNoise((uv3 + uv1) / 16.0f).x) * SKY.cloud_variance * (1.0f - SKY.cloud_scale * 0.25f);
    vec2 disturbance2 = vec2(cloudNoise((uv1 + uv3) / 4.0f).x, cloudNoise((uv4 + uv2) / 8.0f).x) * SKY.cloud_variance * (1.0f - SKY.cloud_scale * 0.25f);

    uv1 += SKY.cloud_pos_density1.xy + (disturbance * 0.2);
    uv2 += SKY.cloud_pos_density1.xy;
    uv3 += SKY.cloud_pos_density2.xy;
    uv4 += SKY.cloud_pos_density2.xy;

    float density_variance = min(1.0, (disturbance.x* 2.0 + disturbance.y* 2.0 + disturbance2.x + disturbance2.y) * 4.0);

    cloudDensity *= 1.0 - (density_variance * density_variance);

    float alpha1 = (cloudNoise(uv1).x - 0.5) + (cloudNoise(uv3).x - 0.5) * SKY.cloud_pos_density2.z;
    alpha1 = min(max(alpha1 + cloudDensity, 0.) * 10 * SKY.cloud_pos_density1.z, 1.);
    alpha1 = 1. - alpha1 * alpha1;
    alpha1 = 1. - alpha1 * alpha1;
    alpha1 *= altitude_blend_factor;
    alpha1 = clamp(alpha1, 0.0, 1.0);

    float alpha2 = (cloudNoise(uv2).x - 0.5);
    alpha2 = min(max(alpha2 + cloudDensity, 0.) * 2.5 * SKY.cloud_pos_density1.z, 1.);
    alpha2 = 1. - alpha2;
    alpha2 = 1. - alpha2 * alpha2;

    vec3 color;
    color = (cloudColorSun*(1.-alpha2) + cloudColorAmbient);
    color.rgb = clamp(color.rgb, vec3(0), vec3(1));
    color.rgb *= 2.0;
    return vec4(color, alpha1);
}
