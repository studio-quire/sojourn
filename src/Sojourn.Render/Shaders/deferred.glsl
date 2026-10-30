// Screen-space helpers for fullscreen deferred passes (the viewer's deferredUtil.glsl getters).
// G-buffer bindless slots arrive packed in the push block: i1 = diffuse | specular << 16,
// i2 = normal | emissive << 16, i3 = depth | light map << 16.
#include "gbuffer_util.glsl"

#define SLOT_DIFFUSE  int(pc.i1 & 0xFFFFu)
#define SLOT_SPECULAR int(pc.i1 >> 16)
#define SLOT_NORMAL   int(pc.i2 & 0xFFFFu)
#define SLOT_EMISSIVE int(pc.i2 >> 16)
#define SLOT_DEPTH    int(pc.i3 & 0xFFFFu)
#define SLOT_LIGHTMAP int(pc.i3 >> 16)

float getDepth(vec2 tc)
{
    return texture(textures[nonuniformEXT(SLOT_DEPTH)], tc).r;
}

// eye-space position from a reverse-Z depth sample. tc has y down (Vulkan); clip y is flipped in Proj.
vec4 getPositionWithDepth(vec2 tc, float depth)
{
    vec4 ndc = vec4(tc * 2.0 - 1.0, depth, 1.0);
    vec4 p = VIEW.invProj * ndc;
    return vec4(p.xyz / p.w, 1.0);
}

// true for pixels with no geometry (sky): reverse-Z clears to 0
bool isSkyDepth(float depth) { return depth <= 0.0; }

vec4 getNormRaw(vec2 tc) { return gbNormIn(texture(textures[nonuniformEXT(SLOT_NORMAL)], tc)); }
vec4 getNorm(vec2 tc) { return decodeNormal(getNormRaw(tc)); }

GBufferInfo getGBuffer(vec2 tc)
{
    GBufferInfo ret;
    vec4 normInfo = getNormRaw(tc);
    ret.albedo = texture(textures[nonuniformEXT(SLOT_DIFFUSE)], tc);
    ret.specular = texture(textures[nonuniformEXT(SLOT_SPECULAR)], tc);
    ret.emissive = texture(textures[nonuniformEXT(SLOT_EMISSIVE)], tc);
    ret.normal = decodeNormal(normInfo).xyz;
    ret.envIntensity = normInfo.b;
    ret.gbufferFlag = normInfo.w;
    return ret;
}

vec3 clampHDRRange(vec3 color)
{
    color = mix(color, vec3(1), isinf(color));
    color = mix(color, vec3(0.0), isnan(color));
    return clamp(color, vec3(0.0), vec3(11.2));
}
