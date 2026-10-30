// Ported from the Second Life viewer's class1/deferred/skyF.glsl (LGPL 2.1, Linden Research, Inc.).
// Sky colour for a world-space (region frame, z up) direction, as skyF writes it to the G-buffer.
// The viewer draws a tessellated dome in a y-up frame; we intersect the view ray with the same sphere.
// Where a world-space view direction meets the dome (the dome's y-up frame, centred at its origin).
vec3 domePos(vec3 d)
{
    // dome frame: x_d = Y, y_d = Z (up), z_d = X  (rotate 120 deg about (1,1,1))
    vec3 dl = vec3(d.y, d.z, d.x);
    vec3 camPosLocal = vec3(0, SKY.cam_height, 0);
    float R = SKY.dome_radius;
    float b = dot(camPosLocal, dl);
    float c = dot(camPosLocal, camPosLocal) - R * R;
    float t = -b + sqrt(max(b * b - c, 0.0));
    return camPosLocal + dl * t;
}

// skyF's rainbow and 22-degree halo (moisture_level / ice_level are 0 in most skies; then the maps aren't bound)
vec3 rainbow(float d)
{
    if (SKY.rainbow_tex < 0) return vec3(0);
    // 'Interior' coordinate: d = 0 at the rainbow's outer edge, 1 at its antisolar centre (SL-13629)
    d         = clamp(-0.575 - d, 0.0, 1.0);
    float interior_coord = max(0.0, d - 0.25) * 4.2857;
    d = clamp(d, 0.0, 0.25) + interior_coord;
    float rad = (SKY.droplet_radius - 5.0f) / 1024.0f;
    return pow(texture(textures[nonuniformEXT(SKY.rainbow_tex)], vec2(rad+0.5, d)).rgb, vec3(1.8)) * SKY.moisture_level;
}

vec3 halo22(float d)
{
    if (SKY.halo_tex < 0) return vec3(0);
    d       = clamp(d, 0.1, 1.0);
    float v = sqrt(clamp(1 - (d * d), 0, 1));
    return texture(textures[nonuniformEXT(SKY.halo_tex)], vec2(0, v)).rgb * SKY.ice_level;
}

vec3 skyColorForDir(vec3 d)
{
    vec3 pos = domePos(d);
    vec3 camPosLocal = vec3(0, SKY.cam_height, 0);

    // Get relative position
    vec3 rel_pos = pos.xyz - camPosLocal.xyz + vec3(0, 50, 0);

    // Adj position vector to clamp altitude
    if (rel_pos.y > 0.)
    {
        rel_pos *= (SKY.max_y / rel_pos.y);
    }
    if (rel_pos.y < 0.)
    {
        rel_pos *= (-32000. / rel_pos.y);
    }

    vec3 color;
    {
        // Normalized
        vec3  rel_pos_norm = normalize(rel_pos);
        float rel_pos_len  = length(rel_pos);

        // Grab this value and pass to frag shader for rainbows
        float rel_pos_lightnorm_dot = dot(rel_pos_norm, SKY.lightnorm.xyz);

        // Initialize temp variables
        vec3 sunlight = (SKY.sun_up_factor == 1) ? SKY.sky_sunlight_color : SKY.sky_moonlight_color * 0.7; //magic 0.7 to match legacy color

        // Sunlight attenuation effect (hue and brightness) due to atmosphere
        // this is used later for sunlight modulation at various altitudes
        vec3 light_atten = (SKY.blue_density + vec3(SKY.haze_density * 0.25)) * (SKY.density_multiplier * SKY.max_y);

        // Calculate relative weights
        vec3 combined_haze = max(abs(SKY.blue_density) + vec3(abs(SKY.haze_density)), vec3(1e-6));
        vec3 blue_weight   = SKY.blue_density / combined_haze;
        vec3 haze_weight   = SKY.haze_density / combined_haze;

        // Compute sunlight from rel_pos & lightnorm (for long rays like sky)
        float off_axis = 1.0 / max(1e-6, max(0., rel_pos_norm.y) + SKY.lightnorm.y);
        sunlight *= exp(-light_atten * off_axis);

        // Distance
        float density_dist = rel_pos_len * SKY.density_multiplier;

        // Transparency (-> combined_haze)
        combined_haze = exp(-combined_haze * density_dist);

        // Compute haze glow
        float haze_glow = 1.0 - rel_pos_lightnorm_dot;
        // haze_glow is 0 at the sun and increases away from sun
        haze_glow = max(haze_glow, .001);
        // Set a minimum "angle" (smaller glow.y allows tighter, brighter hotspot)
        haze_glow *= SKY.glow.x;
        // Higher glow.x gives dimmer glow (because next step is 1 / "angle")
        haze_glow = pow(haze_glow, SKY.glow.z);
        // glow.z should be negative, so we're doing a sort of (1 / "angle") function

        // Add "minimum anti-solar illumination"
        // For sun, add to glow.  For moon, remove glow entirely. SL-13768
        haze_glow = (SKY.sun_moon_glow_factor < 1.0) ? 0.0 : (SKY.sun_moon_glow_factor * (haze_glow + 0.25));

        // Haze color above cloud
        color = (SKY.blue_horizon * blue_weight * (sunlight + SKY.ambient_color)
                + (SKY.haze_horizon * haze_weight) * (sunlight * haze_glow + SKY.ambient_color));

        // Final atmosphere additive
        color *= (1. - combined_haze);

        // Increase ambient when there are more clouds
        vec3 ambient = SKY.ambient_color + max(vec3(0), (1. - SKY.ambient_color)) * SKY.cloud_shadow * 0.5;

        // Dim sunlight by cloud shadow percentage
        sunlight *= max(0.0, (1. - SKY.cloud_shadow));

        // Haze color below cloud
        vec3 add_below_cloud = (SKY.blue_horizon * blue_weight * (sunlight + ambient)
                            + (SKY.haze_horizon * haze_weight) * (sunlight * haze_glow + ambient));

        // Attenuate cloud color by atmosphere
        combined_haze = sqrt(combined_haze);  // less atmos opacity (more transparency) below clouds

        // At horizon, blend high altitude sky color towards the darker color below the clouds
        color += (add_below_cloud - color) * (1. - sqrt(combined_haze));

        float optic_d = rel_pos_lightnorm_dot;
        color.rgb += rainbow(optic_d);
        color.rgb += halo22(optic_d);

        color.rgb *= 2.;
        color.rgb = clamp(color.rgb, vec3(0), vec3(5));

        
    }

    return color;
}
