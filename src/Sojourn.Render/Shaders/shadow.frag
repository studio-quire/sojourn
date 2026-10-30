#version 460
// Shadow casters' alpha rules (the viewer's shadowAlphaMaskF / shadowAlphaBlend shaders): masked faces cut at
// their own cutoff; blended faces drop texture alpha below 0.598, skip combined alpha below 0.05, cast on every
// other shadow-map column between 0.05 and 0.88, and solid above.
#include "common.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 1) flat in uint vInstance;

// A caster's texture as the cascades see it. The coarse far map (pc.i0 6) picks the mip the full-detail cascades would
// at that spot: its own, a few levels down, averages leaf cards' alpha below their cutoff and thins foliage's shadows.
vec4 casterTex(int index, vec2 uv)
{
    if (index < 0) return vec4(1);
    if (pc.i0 != 6u) return texture(textures[nonuniformEXT(index)], uv);
    float k = SKY.shadow_far_res.x / max(SKY.shadow_res.x, SKY.shadow_res.y);
    return textureGrad(textures[nonuniformEXT(index)], uv, dFdx(uv) * k, dFdy(uv) * k);
}

void main()
{
    Material m = MATERIAL(INSTANCE(vInstance).material);
    if ((m.flags & MAT_WATER) != 0u) discard;
    uint mode = MAT_ALPHA_MODE(m);
    if ((m.flags & MAT_AVATAR) != 0u)
    {
        // avatarShadowF casts the whole body (no alpha test); hair, eyelashes and skirt drop alpha below 0.05
        // (avatarAlphaShadowF; its stipple threshold is never set)
        if (mode != ALPHA_BLEND) return;
        if (casterTex(m.diffuse.index, vUv).a < 0.05) discard;
        return;
    }
    if (mode == ALPHA_MASK || mode == ALPHA_BLEND)
    {
        bool pbr = (m.flags & MAT_PBR) != 0u;
        vec2 uv = pbr ? khrTransform(vUv, m.diffuse) : teTransform(vUv, m.diffuse);
        float ta = casterTex(m.diffuse.index, uv).a;
        if ((m.flags & MAT_FOLIAGE) != 0u)
        {
            if (ta < m.shadowCutoff) discard;   // treeShadowF: trees 0.5, grass 0.598
            return;
        }
        if (mode == ALPHA_MASK)
        {
            if (ta * (pbr ? m.color.a : 1.0) < max(m.alphaCutoff, 0.004)) discard;
            return;
        }
        if (ta < 0.598) discard;
        float a = ta * m.color.a;
        if (a < 0.05) discard;
        if (a < 0.88 && (int(gl_FragCoord.x) & 1) == 1) discard;
    }
}
