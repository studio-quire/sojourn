// Reflection probes disabled path: ported from the Second Life viewer's class2/deferred/reflectionProbeF.glsl
// (LGPL 2.1, Linden Research, Inc.). The viewer's environmentMap (the legacy sky cube) is evaluated
// analytically from the sky model in the reflected world direction.
#include "sky_color.glsl"

vec3 envMapSample(vec3 eyeDir)
{
    vec3 d = normalize(mat3(VIEW.invView) * eyeDir);
    return skyColorForDir(d) * 0.5;   // skyF doubles its output; the sky cube holds the undoubled colour
}

void sampleReflectionProbes(inout vec3 ambenv, inout vec3 glossenv,
        vec2 tc, vec3 pos, vec3 norm, float glossiness, bool transparent, vec3 amblit_linear)
{
    ambenv = mix(ambenv, vec3(SKY.probe_ambiance * 0.25), SKY.probe_ambiance);
    vec3 refnormpersp = normalize(reflect(pos.xyz, norm.xyz));
    glossenv = srgb_to_linear(envMapSample(refnormpersp));
}

void sampleReflectionProbesLegacy(inout vec3 ambenv, inout vec3 glossenv, inout vec3 legacyenv,
        vec2 tc, vec3 pos, vec3 norm, float glossiness, float envIntensity, bool transparent, vec3 amblit_linear)
{
    ambenv = mix(ambenv, vec3(SKY.probe_ambiance * 0.25), SKY.probe_ambiance);
    vec3 refnormpersp = normalize(reflect(pos.xyz, norm.xyz));
    legacyenv = envMapSample(refnormpersp);
    glossenv = legacyenv;
}

void applyGlossEnv(inout vec3 color, vec3 glossenv, vec4 spec, vec3 pos, vec3 norm)
{
}

void applyLegacyEnv(inout vec3 color, vec3 legacyenv, vec4 spec, vec3 pos, vec3 norm, float envIntensity)
{
    color = srgb_to_linear(mix(linear_to_srgb(color.rgb), legacyenv*2.0, envIntensity));
}
