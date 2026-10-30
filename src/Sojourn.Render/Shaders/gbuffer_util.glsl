// G-buffer flags and normal packing, as the viewer's LLShaderMgr header and deferredUtil define them.
#define GBUFFER_FLAG_SKIP_ATMOS   0.0
#define GBUFFER_FLAG_HAS_ATMOS    0.34
#define GBUFFER_FLAG_HAS_PBR      0.67
#define GBUFFER_FLAG_HAS_HDRI     1.0
#define GET_GBUFFER_FLAG(data, flag)    (abs(data-flag)< 0.1)
// The person's own body in the headset raises its flag a little, inside GET_GBUFFER_FLAG's tolerance, so the lighting
// reads it as before and the hands' cut (handcut.frag) can tell it apart.
#define GBUFFER_SELF_MARK 0.05
#define GBUFFER_IS_SELF(data)   (min(min(abs(data), abs(data - 0.34)), min(abs(data - 0.67), abs(data - 1.0))) > 0.025)

#include "gbuffer_format.glsl"

// The normal target as written and read: with the narrow formats (NARROW_GBUFFER: A2B10G10R10 UNORM) the octahedral
// normal goes in 10-bit r and g as [0, 1], the gbuffer flag in the 2-bit alpha (0, 1/3, 2/3, 1: within
// GET_GBUFFER_FLAG's tolerance of the viewer's 0, 0.34, 0.67, 1), and the self mark moves to b's top bit, env intensity
// in its other 9. gbNormIn gives back the wide layout (oct in [-1, 1], env, flag with the self mark), so readers are
// unchanged; without the narrow formats both are the identity.
#ifdef NARROW_GBUFFER
vec4 gbNormOut(vec4 w)
{
    float self = GBUFFER_IS_SELF(w.a) ? 512.0 : 0.0;
    float flag = clamp(round(w.a * 3.0), 0.0, 3.0) / 3.0;
    float env = round(clamp(w.b, 0.0, 1.0) * 511.0);
    return vec4(clamp(w.xy * 0.5 + 0.5, 0.0, 1.0), (env + self) / 1023.0, flag);
}
vec4 gbNormIn(vec4 r)
{
    float s = round(r.b * 1023.0);
    float self = s >= 512.0 ? 1.0 : 0.0;
    return vec4(r.xy * 2.0 - 1.0, (s - self * 512.0) / 511.0, round(r.a * 3.0) / 3.0 + self * GBUFFER_SELF_MARK);
}
#else
vec4 gbNormOut(vec4 w) { return w; }
vec4 gbNormIn(vec4 r) { return r; }
#endif

struct GBufferInfo { vec4 albedo; vec4 specular; vec3 normal; vec4 emissive; float gbufferFlag; float envIntensity; };

// octahedral encoding of an eye-space normal into rg; env intensity in b, flag in a
vec2 octWrap(vec2 v) { return (1.0 - abs(v.yx)) * vec2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0); }

vec4 encodeNormal(vec3 n, float env, float gbuffer_flag)
{
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    vec2 e = n.z >= 0.0 ? n.xy : octWrap(n.xy);
    return vec4(e, env, gbuffer_flag);
}

vec3 decodeOct(vec2 f)
{
    vec3 n = vec3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = max(-n.z, 0.0);
    n.x += n.x >= 0.0 ? -t : t;
    n.y += n.y >= 0.0 ? -t : t;
    return normalize(n);
}

vec4 decodeNormal(vec4 norm)
{
    return vec4(decodeOct(norm.xy), norm.z);
}
