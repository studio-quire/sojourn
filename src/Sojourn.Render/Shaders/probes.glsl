// Reflection probes: ported from the Second Life viewer's class3/deferred/reflectionProbeF.glsl (LGPL 2.1, Linden
// Research, Inc.). The view's probe list (World/ProbeManager) holds the default probe (entry 0 when present: sky,
// water, clouds and far-clipped terrain seen from 64 m above the camera, radius 4096) and the automatic terrain and
// object probes and manual probes in range. Positions are region space (the view's region); the viewer works in eye
// space, which changes nothing here. Probe selection: the viewer walks depth buckets and the first influencing
// probe's neighbour list, which yields every probe whose volume holds the pixel; here a region-xy grid of probe
// indices does the same. Falls back to the probes-disabled path (sky colour) when probes are off.
#include "sky_color.glsl"

#define PROBES_ON (SKY.probes_on != 0)
#define REF_SAMPLE_COUNT 32

struct ProbeGpu
{
    vec4 sphere;    // xyz origin, w radius (refSphere)
    mat4 box;       // region -> unit cube for box probes (refBox)
    vec4 params;    // x ambiance scale, y radiance scale, z fade-in (refParams)
    ivec4 slots;    // x radiance cube (-1: none yet), y irradiance cube, z priority: 0 automatic, 1 manual, -1 manual box
};

layout(buffer_reference, scalar) readonly buffer ProbeListRef { ProbeGpu p[]; };
layout(buffer_reference, scalar) readonly buffer ProbeCellsRef { uint c[]; };

#define PROBE(i) (ProbeListRef(uint64_t(SKY.probe_list_lo) | (uint64_t(SKY.probe_list_hi) << 32)).p[i])

int probeIndex[REF_SAMPLE_COUNT + 1];
int probeInfluences = 0;
bool sample_automatic = true;

vec3 envMapSample(vec3 eyeDir)
{
    vec3 d = normalize(mat3(VIEW.invView) * eyeDir);
    return skyColorForDir(d) * 0.5;   // skyF doubles its output; the legacy sky cube holds the undoubled colour
}

// shouldSampleProbe
bool shouldSampleProbe(int i, vec3 pos)
{
    ProbeGpu p = PROBE(i);
    if (p.slots.z < 0)
    {
        vec4 v = p.box * vec4(pos, 1.0);
        if (abs(v.x) > 1 || abs(v.y) > 1 || abs(v.z) > 1)
            return false;
        // never allow automatic probes to encroach on box probes
        sample_automatic = false;
    }
    else
    {
        if (p.slots.z == 0 && !sample_automatic)
            return false;
        vec3 delta = pos - p.sphere.xyz;
        if (dot(delta, delta) > p.sphere.w * p.sphere.w)
            return false;
    }
    return true;
}

// preProbeSample: every probe whose volume holds pos (grid cell list, list order), then the default probe
void preProbeSample(vec3 pos)
{
    probeInfluences = 0;
    if (SKY.probe_grid_w > 0)
    {
        ivec2 cell = ivec2(floor((pos.xy - SKY.probe_grid_origin) / SKY.probe_grid_cell));
        if (cell.x >= 0 && cell.y >= 0 && cell.x < SKY.probe_grid_w && cell.y < SKY.probe_grid_h)
        {
            ProbeCellsRef cells = ProbeCellsRef(uint64_t(SKY.probe_cells_lo) | (uint64_t(SKY.probe_cells_hi) << 32));
            int ci = cell.y * SKY.probe_grid_w + cell.x;
            uint start = cells.c[ci], end = cells.c[ci + 1];
            for (uint k = start; k < end && probeInfluences < REF_SAMPLE_COUNT; ++k)
            {
                int i = int(cells.c[k]);
                if (shouldSampleProbe(i, pos))
                    probeIndex[probeInfluences++] = i;
            }
        }
    }
    if (sample_automatic && SKY.probe_default != 0)
        probeIndex[probeInfluences++] = 0;   // the default probe smooths out automatic probes
}

// adapted -- assume that origin is inside sphere, return intersection of ray with edge of sphere
vec3 sphereIntersect(vec3 origin, vec3 dir, vec3 center, float radius2)
{
    vec3 L = center - origin;
    float tca = dot(L, dir);
    float d2 = dot(L, L) - tca * tca;
    float thc = sqrt(radius2 - d2);
    float t1 = tca + thc;
    return origin + dir * t1;
}

// Lagarde's OBB parallax, in the probe's unit-cube space
vec3 boxIntersect(vec3 origin, vec3 dir, mat4 box, out float d, float scale)
{
    vec3 RayLS = mat3(box) * dir;
    vec3 PositionLS = (box * vec4(origin, 1.0)).xyz;
    d = 1.0 - max(max(abs(PositionLS.x), abs(PositionLS.y)), abs(PositionLS.z));
    vec3 Unitary = vec3(scale);
    vec3 FirstPlaneIntersect = (Unitary - PositionLS) / RayLS;
    vec3 SecondPlaneIntersect = (-Unitary - PositionLS) / RayLS;
    vec3 FurthestPlane = max(FirstPlaneIntersect, SecondPlaneIntersect);
    float Distance = min(FurthestPlane.x, min(FurthestPlane.y, FurthestPlane.z));
    return origin + dir * Distance;
}

// inverse distance, faded over the outer half of the radius
float sphereWeight(vec3 pos, vec3 origin, float r, vec4 params, out float dw)
{
    float r1 = r * 0.5;
    float d2 = max(length(pos - origin), 0.001);
    float atten = 1.0 - max(d2 - r1, 0.0) / max((r - r1), 0.001);
    float w = 1.0 / d2;
    w *= params.z;
    dw = w * atten * max(r, 1.0) * 4.0;
    w *= atten;
    return w;
}

vec3 tapRefMap(vec3 pos, vec3 dir, out float w, out float dw, float lod, int i)
{
    ProbeGpu p = PROBE(i);
    vec3 c = p.sphere.xyz;
    vec3 v;
    dw = 0.0;
    if (p.slots.z < 0)
    {
        float d = 0.0;
        v = boxIntersect(pos, dir, p.box, d, 1.0);
        w = max(d, 0.001);
    }
    else
    {
        float r = p.sphere.w;
        // automatic probes: effectively no parallax correction
        v = sphereIntersect(pos, dir, c, p.slots.z < 1 ? 4096.0 * 4096.0 : r * r);
        w = sphereWeight(pos, c, r, p.params, dw);
    }
    v -= c;
    if (p.slots.x < 0)
    {
        // radiance not captured yet (this capture's own first pass): the viewer leaves such probes out; the default
        // probe, which it refreshes every 2 s until settled, is stood in for by the sky it mostly holds
        if (i == 0 && p.slots.z == 0) return srgb_to_linear(skyColorForDir(normalize(v)) * 0.5) * p.params.y;
        w = 0.0; dw = 0.0;
        return vec3(0);
    }
    return textureLod(cubes[nonuniformEXT(p.slots.x)], v, lod).rgb * p.params.y;
}

vec3 tapIrradianceMap(vec3 pos, vec3 dir, out float w, out float dw, int i, vec3 amblit)
{
    ProbeGpu p = PROBE(i);
    vec3 c = p.sphere.xyz;
    vec3 v;
    dw = 0.0;
    if (p.slots.z < 0)
    {
        float d = 0.0;
        v = boxIntersect(pos, dir, p.box, d, 3.0);
        w = max(d, 0.001);
    }
    else
    {
        float r = p.sphere.w;
        v = sphereIntersect(pos, dir, c, p.slots.z < 1 ? 4096.0 * 4096.0 : r * r);
        w = sphereWeight(pos, c, r, p.params, dw);
    }
    v -= c;
    vec3 col = textureLod(cubes[nonuniformEXT(p.slots.y)], v, 0).rgb * p.params.x;
    return mix(amblit, col, min(p.params.x, 1.0));
}

vec3 mixProbeGroups(vec3 col[2], float wsum[2], float dwsum[2])
{
    // mix automatic and manual probes
    if (sample_automatic && wsum[0] > 0.0)
    {
        col[0] *= 1.0 / wsum[0];
        if (wsum[1] > 0.0)
        {
            col[1] *= 1.0 / wsum[1];
            col[1] = mix(col[0], col[1], min(dwsum[1], 1.0));
            col[0] = vec3(0);
        }
    }
    else if (wsum[1] > 0.0)
    {
        col[1] *= 1.0 / wsum[1];
        col[0] = vec3(0);
    }
    return col[1] + col[0];
}

vec3 sampleProbes(vec3 pos, vec3 dir, float lod)
{
    float wsum[2] = float[2](0.0, 0.0);
    float dwsum[2] = float[2](0.0, 0.0);
    vec3 col[2] = vec3[2](vec3(0), vec3(0));
    for (int idx = 0; idx < probeInfluences; ++idx)
    {
        int i = probeIndex[idx];
        int p = clamp(abs(PROBE(i).slots.z), 0, 1);
        if (p == 0 && !sample_automatic) continue;
        float w = 0.0, dw = 0.0;
        vec3 refcol = tapRefMap(pos, dir, w, dw, lod, i);
        col[p] += refcol * w;
        wsum[p] += w;
        dwsum[p] += dw;
    }
    return mixProbeGroups(col, wsum, dwsum);
}

vec3 sampleProbeAmbient(vec3 pos, vec3 dir, vec3 amblit)
{
    float wsum[2] = float[2](0.0, 0.0);
    float dwsum[2] = float[2](0.0, 0.0);
    vec3 col[2] = vec3[2](vec3(0), vec3(0));
    for (int idx = 0; idx < probeInfluences; ++idx)
    {
        int i = probeIndex[idx];
        int p = clamp(abs(PROBE(i).slots.z), 0, 1);
        if (p == 0 && !sample_automatic) continue;
        float w = 0.0, dw = 0.0;
        vec3 refcol = tapIrradianceMap(pos, dir, w, dw, i, amblit);
        col[p] += refcol * w;
        wsum[p] += w;
        dwsum[p] += dw;
    }
    // no probe at all (none captured yet): the atmospheric ambient
    if (wsum[0] <= 0.0 && wsum[1] <= 0.0) return amblit;
    return mixProbeGroups(col, wsum, dwsum);
}

// tapHeroProbe (HERO_PROBES): inside the mirror probe's box and in front of its plane, glossy enough surfaces take
// the mirror's reflection. The viewer samples a cube map taken from the camera mirrored in the plane; here each eye
// has its own image taken from that eye mirrored (stereo needs both), indexed by the pixel (u flipped: the mirrored
// camera's x is turned round to keep it a rotation).
void tapHeroProbe(inout vec3 glossenv, vec3 posWorld, float glossiness)
{
#ifdef FRAGMENT_SHADER   // alpha_simple.vert takes only ambient from the probes
    if (SKY.hero_on == 0) return;
    vec4 lp = SKY.hero_box * vec4(posWorld, 1.0);
    float w = max(1.0 - max(max(abs(lp.x), abs(lp.y)), abs(lp.z)), 0.0);
    float clipDist = dot(posWorld, SKY.hero_plane.xyz) + SKY.hero_plane.w;
    clipDist = clipDist * 0.95 + 0.05;
    clipDist = clamp(clipDist * 10.0, 0.0, 1.0);
    w = clamp(w * 10.0 * clipDist, 0.0, 1.0);
    w = mix(0.0, w, clamp(glossiness - 0.75, 0.0, 1.0) * 4.0);   // only the sharpest mips: rough surfaces keep the probes
    if (w <= 0.0) return;
    vec2 uv = gl_FragCoord.xy * VIEW.viewport.zw;
    uv.x = 1.0 - uv.x;
    vec3 refl = textureLod(textures[nonuniformEXT(SKY.hero_tex)], uv, (1.0 - glossiness) * SKY.hero_mips).rgb;
    glossenv = mix(glossenv, refl, w);
#endif
}

void doProbeSample(inout vec3 ambenv, inout vec3 glossenv, vec2 tc, vec3 posWorld, vec3 pos, vec3 norm, float glossiness, vec3 amblit)
{
    vec3 refnormpersp = reflect(pos.xyz, norm.xyz);
    vec3 dirWorld = normalize(mat3(VIEW.invView) * normalize(refnormpersp));
    vec3 normWorld = normalize(mat3(VIEW.invView) * norm);

    ambenv = amblit;
    if (SKY.classic_mode == 0)
        ambenv = sampleProbeAmbient(posWorld, normWorld, amblit);

    float lod = (1.0 - glossiness) * SKY.max_probe_lod;
    glossenv = sampleProbes(posWorld, dirWorld, lod);
    tapHeroProbe(glossenv, posWorld, glossiness);
}

void sampleReflectionProbes(inout vec3 ambenv, inout vec3 glossenv,
        vec2 tc, vec3 pos, vec3 norm, float glossiness, bool transparent, vec3 amblit_linear)
{
    if (!PROBES_ON)
    {
        // class2/deferred/reflectionProbeF.glsl (probes disabled)
        ambenv = mix(ambenv, vec3(SKY.probe_ambiance * 0.25), SKY.probe_ambiance);
        vec3 refnormpersp = normalize(reflect(pos.xyz, norm.xyz));
        glossenv = srgb_to_linear(envMapSample(refnormpersp));
        return;
    }
    vec3 posWorld = (VIEW.invView * vec4(pos, 1.0)).xyz;
    sample_automatic = true;
    preProbeSample(posWorld);
    doProbeSample(ambenv, glossenv, tc, posWorld, pos, norm, glossiness, amblit_linear);
}

// water: no automatic probes, but always the default (void) probe
void sampleReflectionProbesWater(inout vec3 ambenv, inout vec3 glossenv,
        vec2 tc, vec3 pos, vec3 norm, float glossiness, vec3 amblit)
{
    if (!PROBES_ON)
    {
        sampleReflectionProbes(ambenv, glossenv, tc, pos, norm, glossiness, false, amblit);
        return;
    }
    vec3 posWorld = (VIEW.invView * vec4(pos, 1.0)).xyz;
    sample_automatic = false;
    preProbeSample(posWorld);
    sample_automatic = true;
    if (SKY.probe_default != 0 && probeInfluences <= REF_SAMPLE_COUNT)
        probeIndex[probeInfluences++] = 0;
    doProbeSample(ambenv, glossenv, tc, posWorld, pos, norm, glossiness, amblit);
}

void sampleReflectionProbesLegacy(inout vec3 ambenv, inout vec3 glossenv, inout vec3 legacyenv,
        vec2 tc, vec3 pos, vec3 norm, float glossiness, float envIntensity, bool transparent, vec3 amblit)
{
    if (!PROBES_ON)
    {
        ambenv = mix(ambenv, vec3(SKY.probe_ambiance * 0.25), SKY.probe_ambiance);
        vec3 refnormpersp = normalize(reflect(pos.xyz, norm.xyz));
        legacyenv = envMapSample(refnormpersp);
        glossenv = legacyenv;
        return;
    }
    vec3 posWorld = (VIEW.invView * vec4(pos, 1.0)).xyz;
    sample_automatic = true;
    preProbeSample(posWorld);

    vec3 refnormpersp = reflect(pos.xyz, norm.xyz);
    vec3 dirWorld = normalize(mat3(VIEW.invView) * normalize(refnormpersp));
    vec3 normWorld = normalize(mat3(VIEW.invView) * norm);

    ambenv = amblit;
    if (SKY.classic_mode == 0)
        ambenv = sampleProbeAmbient(posWorld, normWorld, amblit);

    if (glossiness > 0.0)
    {
        float lod = (1.0 - glossiness) * SKY.max_probe_lod;
        glossenv = sampleProbes(posWorld, dirWorld, lod);
    }

    if (envIntensity > 0.0)
    {
        legacyenv = sampleProbes(posWorld, dirWorld, 0.0);
    }

    tapHeroProbe(glossenv, posWorld, glossiness);
    tapHeroProbe(legacyenv, posWorld, 1.0);

    glossenv = clamp(glossenv, vec3(0), vec3(10));
}

void applyGlossEnv(inout vec3 color, vec3 glossenv, vec4 spec, vec3 pos, vec3 norm)
{
    if (!PROBES_ON) return;   // the probes-disabled path adds nothing here
    glossenv *= 0.5; // fudge darker
    float fresnel = clamp(1.0+dot(normalize(pos.xyz), norm.xyz), 0.3, 1.0);
    fresnel *= fresnel;
    fresnel *= spec.a;
    glossenv *= spec.rgb*fresnel;
    glossenv *= vec3(1.0) - color; // fake energy conservation
    color.rgb += glossenv*0.5;
}

void applyLegacyEnv(inout vec3 color, vec3 legacyenv, vec4 spec, vec3 pos, vec3 norm, float envIntensity)
{
    if (!PROBES_ON)
    {
        color = srgb_to_linear(mix(linear_to_srgb(color.rgb), legacyenv*2.0, envIntensity));
        return;
    }
    vec3 reflected_color = legacyenv;
    vec3 lookAt = normalize(pos);
    float fresnel = 1.0+dot(lookAt, norm.xyz);
    fresnel *= fresnel;
    fresnel = min(fresnel+envIntensity, 1.0);
    reflected_color *= (envIntensity*fresnel);
    color = mix(color.rgb, reflected_color*0.5, envIntensity);
}

// Simpler lighting for blended faces (alpha.frag SIMPLE_LIGHT, its ambient from alpha_simple.vert): only what's
// reflected, per pixel, as doProbeSample (PBR) and sampleReflectionProbesLegacy (materialF) find it. Claude Opus 5.5.
vec3 sampleRadiance(vec3 pos, vec3 norm, float glossiness)
{
    if (!PROBES_ON) return srgb_to_linear(envMapSample(normalize(reflect(pos.xyz, norm.xyz))));
    vec3 posWorld = (VIEW.invView * vec4(pos, 1.0)).xyz;
    sample_automatic = true;
    preProbeSample(posWorld);
    vec3 dirWorld = normalize(mat3(VIEW.invView) * normalize(reflect(pos.xyz, norm.xyz)));
    vec3 radiance = sampleProbes(posWorld, dirWorld, (1.0 - glossiness) * SKY.max_probe_lod);
    tapHeroProbe(radiance, posWorld, glossiness);
    return radiance;
}

void sampleReflectionsLegacy(inout vec3 glossenv, inout vec3 legacyenv, vec3 pos, vec3 norm, float glossiness, float envIntensity)
{
    if (!PROBES_ON)
    {
        legacyenv = envMapSample(normalize(reflect(pos.xyz, norm.xyz)));
        glossenv = legacyenv;
        return;
    }
    if (glossiness <= 0.0 && envIntensity <= 0.0) return;
    vec3 posWorld = (VIEW.invView * vec4(pos, 1.0)).xyz;
    sample_automatic = true;
    preProbeSample(posWorld);
    vec3 dirWorld = normalize(mat3(VIEW.invView) * normalize(reflect(pos.xyz, norm.xyz)));
    if (glossiness > 0.0) glossenv = sampleProbes(posWorld, dirWorld, (1.0 - glossiness) * SKY.max_probe_lod);
    if (envIntensity > 0.0) legacyenv = sampleProbes(posWorld, dirWorld, 0.0);
    tapHeroProbe(glossenv, posWorld, glossiness);
    tapHeroProbe(legacyenv, posWorld, 1.0);
    glossenv = clamp(glossenv, vec3(0), vec3(10));
}

// alpha_simple.vert: the ambient a surface takes from the probes, or from the sky's ambient without them (the first
// part of sampleReflectionProbesLegacy and doProbeSample)
vec3 sampleAmbient(vec3 posWorld, vec3 normWorld, vec3 amblit)
{
    if (!PROBES_ON) return mix(amblit, vec3(SKY.probe_ambiance * 0.25), SKY.probe_ambiance);
    if (SKY.classic_mode != 0) return amblit;
    sample_automatic = true;
    preProbeSample(posWorld);
    return sampleProbeAmbient(posWorld, normWorld, amblit);
}
