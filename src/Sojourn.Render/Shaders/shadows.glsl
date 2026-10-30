// Sun/moon and projector shadow lookups, shared by the deferred sun pass and the forward (alpha, particle) shaders.
// Ported from the Second Life viewer's class1/deferred/shadowUtil.glsl (LGPL 2.1, Linden Research, Inc.).
// Needs common.glsl (SKY, shadowMaps). pos is eye space; pos_screen the screen texture coordinate.
// Departure: the cascades and the shadows' reach go by distance from the eye (shadow_clip as radii; the viewer
// compares eye-space z), so turning the head never moves a cascade's edge or where shadows end
// (docs/rendering.md > Distances from the head; SunShadowSetup fits each cascade to its shell).
// Ours: a coarse far cascade (map 6, SKY.shadow_far) can carry shadows on from the fourth's end, in place of the
// viewer's fade over the fourth's far half, and fade them out further away (docs/rendering.md > Where shadows end).

// -distance from the eye: where the viewer's code reads eye-space z
float radialZ(vec3 p) { return -length(p); }

float pcfShadow(int map, vec3 norm, vec4 stc, float bias_mul, vec2 pos_screen, vec3 light_dir)
{
    vec2 shadow_res = SKY.shadow_res;
    float offset = SKY.shadow_bias * bias_mul;
    stc.xyz /= stc.w;
    stc.z += offset * 2.0;
    stc.z = clamp(stc.z, 0.0, 1.0);   // our maps are D32F: clamp like the viewer's 24-bit UNORM compare does
    stc.x = floor(stc.x*shadow_res.x + fract(pos_screen.y*shadow_res.y))/shadow_res.x; // add some chaotic jitter to X sample pos according to Y to disguise the snapping going on here
    float cs = texture(shadowMaps[map], stc.xyz);
    float shadow = cs * 4.0;
    shadow += texture(shadowMaps[map], stc.xyz+vec3( 1.5/shadow_res.x,  0.5/shadow_res.y, 0.0));
    shadow += texture(shadowMaps[map], stc.xyz+vec3( 0.5/shadow_res.x, -1.5/shadow_res.y, 0.0));
    shadow += texture(shadowMaps[map], stc.xyz+vec3(-1.5/shadow_res.x, -0.5/shadow_res.y, 0.0));
    shadow += texture(shadowMaps[map], stc.xyz+vec3(-0.5/shadow_res.x,  1.5/shadow_res.y, 0.0));
    return clamp(shadow * 0.125, 0.0, 1.0);
}

// pcfShadow for the far cascade: its map has a size of its own
float pcfFarShadow(vec4 stc, vec2 pos_screen)
{
    vec2 res = SKY.shadow_far_res;
    stc.xyz /= stc.w;
    stc.z += SKY.shadow_bias * 2.0;
    stc.z = clamp(stc.z, 0.0, 1.0);
    stc.x = floor(stc.x*res.x + fract(pos_screen.y*res.y))/res.x;
    float shadow = texture(shadowMaps[6], stc.xyz) * 4.0;
    shadow += texture(shadowMaps[6], stc.xyz+vec3( 1.5/res.x,  0.5/res.y, 0.0));
    shadow += texture(shadowMaps[6], stc.xyz+vec3( 0.5/res.x, -1.5/res.y, 0.0));
    shadow += texture(shadowMaps[6], stc.xyz+vec3(-1.5/res.x, -0.5/res.y, 0.0));
    shadow += texture(shadowMaps[6], stc.xyz+vec3(-0.5/res.x,  1.5/res.y, 0.0));
    return clamp(shadow * 0.125, 0.0, 1.0);
}

float pcfSpotShadow(int map, vec4 stc, float bias_scale, vec2 pos_screen)
{
    vec2 proj_shadow_res = SKY.proj_shadow_res;
    stc.xyz /= stc.w;
    stc.z += SKY.spot_shadow_bias * bias_scale;
    stc.z = clamp(stc.z, 0.0, 1.0);
    stc.x = floor(proj_shadow_res.x * stc.x + fract(pos_screen.y*0.666666666)) / proj_shadow_res.x; // snap

    float cs = texture(shadowMaps[map], stc.xyz);
    float shadow = cs;

    vec2 off = 1.0/proj_shadow_res;
    off.y *= 1.5;

    shadow += texture(shadowMaps[map], stc.xyz+vec3(off.x*2.0, off.y, 0.0));
    shadow += texture(shadowMaps[map], stc.xyz+vec3(off.x, -off.y, 0.0));
    shadow += texture(shadowMaps[map], stc.xyz+vec3(-off.x, off.y, 0.0));
    shadow += texture(shadowMaps[map], stc.xyz+vec3(-off.x*2.0, -off.y, 0.0));
    return shadow*0.2;
}

// the viewer passes the eye-space position as pos_screen here (spos.xy), kept as is
float sampleSpotShadow(vec3 pos, vec3 norm, int index, vec2 pos_screen)
{
    if ((SKY.spot_shadows & (1 << index)) == 0) return 1.0;
    float shadow = 0.0f;
    pos += norm * SKY.spot_shadow_offset;

    vec4 spos = vec4(pos,1.0);
    float sz = radialZ(pos);
    vec4 shadow_clip = SKY.shadow_clip;
    if (sz > -shadow_clip.w)
    {
        vec4 lpos;

        vec4 near_split = shadow_clip*-0.75;
        vec4 far_split = shadow_clip*-1.25;
        vec4 transition_domain = near_split-far_split;
        float weight = 0.0;

        {
            float w = 1.0;
            w -= max(sz-far_split.z, 0.0)/transition_domain.z;

            if (index == 0)
            {
                lpos = SKY.shadow_matrix4*spos;
                shadow += pcfSpotShadow(4, lpos, 0.8, spos.xy)*w;
            }
            else
            {
                lpos = SKY.shadow_matrix5*spos;
                shadow += pcfSpotShadow(5, lpos, 0.8, spos.xy)*w;
            }
            weight += w;
            shadow += max((sz+shadow_clip.z)/(shadow_clip.z-shadow_clip.w)*2.0-1.0, 0.0);
        }

        shadow /= weight;
    }
    else
    {
        shadow = 1.0f;
    }
    return shadow;
}

float sampleDirectionalShadow(vec3 pos, vec3 norm, vec2 pos_screen)
{
    if (SKY.shadows_on == 0) return 1.0;
    float shadow = 0.0f;
    vec3 light_dir = normalize((SKY.sun_up_factor == 1) ? SKY.sun_dir : SKY.moon_dir);

    float dp_directional_light = max(0.0, dot(norm.xyz, light_dir));
          dp_directional_light = clamp(dp_directional_light, 0.0, 1.0);

    vec3 shadow_pos = pos.xyz;

    vec3 offset = light_dir.xyz * (1.0 - dp_directional_light);

    shadow_pos += offset * SKY.shadow_offset * 2.0;

    vec4 spos = vec4(shadow_pos.xyz, 1.0);
    float sz = radialZ(shadow_pos);   // the cascade and the fade by distance; the maps by position
    vec4 shadow_clip = SKY.shadow_clip;
    // the far cascade: x where the fourth ends and it takes over (across a fifth of that), y where it starts fading
    // out, z where it ends
    vec4 far = SKY.shadow_far;
    bool far_on = far.w > 0.0;

    if (sz > -(far_on ? far.z : shadow_clip.w))
    {
        vec4 lpos;

        vec4 near_split = shadow_clip*-0.75;
        vec4 far_split = shadow_clip*-1.25;
        vec4 transition_domain = near_split-far_split;
        float weight = 0.0;

        if (far_on)
        {
            if (sz < near_split.z && -sz < far.x * 1.2)
            {
                lpos = SKY.shadow_matrix3*spos;

                float w = 1.0;
                w -= max(sz-far_split.z, 0.0)/transition_domain.z;
                w -= max(-sz - far.x, 0.0)/(far.x * 0.2);
                shadow += pcfShadow(3, norm, lpos, 1.0, pos_screen, light_dir)*w;
                weight += w;
            }
            if (-sz > far.x)
            {
                float w = min((-sz - far.x)/(far.x * 0.2), 1.0);
                shadow += pcfFarShadow(SKY.shadow_matrix6*spos, pos_screen)*w;
                weight += w;
            }
        }
        else if (sz < near_split.z)
        {
            lpos = SKY.shadow_matrix3*spos;

            float w = 1.0;
            w -= max(sz-far_split.z, 0.0)/transition_domain.z;
            float contrib = pcfShadow(3, norm, lpos, 1.0, pos_screen, light_dir)*w;
            {
                shadow += contrib;
                weight += w;
            }
            shadow += max((radialZ(pos)+shadow_clip.z)/(shadow_clip.z-shadow_clip.w)*2.0-1.0, 0.0);
        }

        if (sz < near_split.y && sz > far_split.z)
        {
            lpos = SKY.shadow_matrix2*spos;

            float w = 1.0;
            w -= max(sz-far_split.y, 0.0)/transition_domain.y;
            w -= max(near_split.z-sz, 0.0)/transition_domain.z;
            float contrib = pcfShadow(2, norm, lpos, 1.0, pos_screen, light_dir)*w;
            {
                shadow += contrib;
                weight += w;
            }
        }

        if (sz < near_split.x && sz > far_split.y)
        {
            lpos = SKY.shadow_matrix1*spos;

            float w = 1.0;
            w -= max(sz-far_split.x, 0.0)/transition_domain.x;
            w -= max(near_split.y-sz, 0.0)/transition_domain.y;
            float contrib = pcfShadow(1, norm, lpos, 1.0, pos_screen, light_dir)*w;
            {
                shadow += contrib;
                weight += w;
            }
        }

        if (sz > far_split.x)
        {
            lpos = SKY.shadow_matrix0*spos;

            float w = 1.0;
            w -= max(near_split.x-sz, 0.0)/transition_domain.x;
            float contrib = pcfShadow(0, norm, lpos, 1.0, pos_screen, light_dir)*w;
            {
                shadow += contrib;
                weight += w;
            }
        }

        shadow /= weight;
        if (far_on) shadow = mix(shadow, 1.0, clamp((-sz - far.y)/(far.z - far.y), 0.0, 1.0));
    }
    else
    {
        return 1.0f; // lit beyond the far split...
    }
    return shadow;
}

