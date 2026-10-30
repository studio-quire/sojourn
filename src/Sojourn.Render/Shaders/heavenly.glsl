// Sun and moon discs as the viewer draws them (LLVOSky::updateHeavenlyBodyGeometry quads, sunDiscF.glsl, moonF.glsl;
// Second Life viewer, LGPL 2.1, Linden Research, Inc.), here as ray-quad hits so the fullscreen sky pass can draw
// them and the star pass can tell where they cover the stars. Directions are region frame (z up), camera-relative.

// The quad's texture coordinates where direction d meets it (xy) and whether it does (z = 1). Quad: centre c, half
// extents r (u 0..1) and u (v 0..1), uv (0,0) at c - r - u as the viewer's v[1].
vec3 bodyQuadUv(vec3 d, vec3 c, vec3 r, vec3 u)
{
    vec3 n = cross(r, u);
    float dn = dot(d, n);
    float t = abs(dn) > 1e-9 ? dot(c, n) / dn : -1.0;
    vec3 p = d * max(t, 0.0) - c;
    vec2 xy = vec2(dot(p, r) / dot(r, r), dot(p, u) / dot(u, u));
    float inside = (t > 0.0 && abs(xy.x) <= 1.0 && abs(xy.y) <= 1.0) ? 1.0 : 0.0;
    return vec3(xy * 0.5 + 0.5, inside);
}

// The disc textures are clamped (TAM_CLAMP); keep bilinear taps inside the image.
vec4 sampleBody(int slot, vec2 uv, vec2 dx, vec2 dy)
{
    vec2 half_texel = 0.5 / vec2(textureSize(textures[nonuniformEXT(slot)], 0));
    return textureGrad(textures[nonuniformEXT(slot)], clamp(uv, half_texel, 1.0 - half_texel), dx, dy);
}

bool sunDiscDrawn() { return (SKY.heavenly_flags & 1) != 0 && SKY.sun_tex_a >= 0; }
bool moonDiscDrawn() { return (SKY.heavenly_flags & 2) != 0 && SKY.moon_tex >= 0; }

vec3 sunDiscUv(vec3 d) { return bodyQuadUv(d, SKY.sun_disc_center, SKY.sun_disc_right, SKY.sun_disc_up); }
vec3 moonDiscUv(vec3 d) { return bodyQuadUv(d, SKY.moon_disc_center, SKY.moon_disc_right, SKY.moon_disc_up); }

// sunDiscF: the blend of the current and next sun textures (alpha-blended over the dome)
vec4 sunDiscColor(vec2 uv, vec2 dx, vec2 dy)
{
    vec4 a = sampleBody(SKY.sun_tex_a, uv, dx, dy);
    vec4 b = sampleBody(SKY.sun_tex_b, uv, dx, dy);
    return mix(a, b, SKY.sun_disc_blend);
}

// moonF: texels at or below 2/255 alpha are discarded (alpha 0 here), the rest dimmed by moon_brightness and faded
// near the horizon
vec4 moonDiscColor(vec2 uv, vec2 dx, vec2 dy)
{
    float fade = 1.0;
    if (SKY.moon_dir_z > 0.0)
        fade = clamp(SKY.moon_dir_z * SKY.moon_dir_z * 4.0, 0.0, 1.0);
    vec4 c = sampleBody(SKY.moon_tex, uv, dx, dy);
    if (c.a <= 2.0 / 255.0) return vec4(0);
    c.rgb *= SKY.moon_brightness;
    c.a *= fade;
    return c;
}

// The viewer's star pass is depth-tested against the sun quad (all of it) and the moon's undiscarded texels.
bool starHidden(vec3 d)
{
    if (sunDiscDrawn() && sunDiscUv(d).z > 0.0) return true;
    if (moonDiscDrawn())
    {
        vec3 m = moonDiscUv(d);
        if (m.z > 0.0)
        {
            vec2 half_texel = 0.5 / vec2(textureSize(textures[nonuniformEXT(SKY.moon_tex)], 0));
            if (textureLod(textures[nonuniformEXT(SKY.moon_tex)], clamp(m.xy, half_texel, 1.0 - half_texel), 0.0).a > 2.0 / 255.0)
                return true;
        }
    }
    return false;
}
