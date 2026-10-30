// Shared declarations: bindless tables (set 0), per-frame buffers (set 1), push constants.
// Struct layouts mirror Frame/GpuStructs.cs (scalar block layout).

// The viewer's GL projection far plane (setup3DRender: MAX_FAR_CLIP * 2), independent of the draw distance; culling
// ignores it, but it clips geometry that reaches past it: the water planes, which extend 2.5 km beyond the regions.
// Here it's a sphere of this radius about the eye (water.frag, waterhaze.frag): a plane's edge swung round with the
// head (docs/rendering.md > Distances from the head).
const float GL_FAR_CLIP = 1024.0;

layout(set = 0, binding = 0) uniform sampler2D textures[];
layout(set = 0, binding = 1) uniform samplerCube cubes[];
layout(set = 0, binding = 2) uniform sampler2DShadow shadowMaps[8];

struct View {
    mat4 view, proj, viewProj, invView, invProj, invViewProj;
    vec4 cameraPos;   // xyz, w = near
    vec4 viewport;    // w, h, 1/w, 1/h
    vec4 sunDir;      // toward light, region space; w = 1 sun, 0 moon
    vec4 farClip;     // x draw distance, y water height, z time
    vec4 clipPlane;   // region space: geometry where dot(xyz, p) + w < 0 is clipped (a mirror's reflection); (0,0,0,1) none
    uint mask;        // VIEW_MAIN or VIEW_MIRROR (0 counts as main): instances flagged for the other kind aren't drawn
    uint tilesLo, tilesHi, tileCols;   // the eye's light tiles (tiles.glsl): address, tiles a row; 0 none
    uint lensMask;    // the eye's lens mask's bindless slot + 1 (Frame/Renderer.Lens.cs); 0 none
    uint layersLo, layersHi, layerMin;   // see-through layers (layers.glsl, Frame/Renderer.Layers.cs): records, least share; 0 none
};

#define VIEW_MAIN 1u
#define VIEW_MIRROR 2u
#define INSTANCE_SELF 4u   // the person's own body in the headset (InstanceFlags.Self)


struct Instance {
    mat4 model;
    vec4 n0, n1, n2;
    uint material;
    uint objectId;
    uint flags;
    float glow;
};

struct TexRef {
    int index;
    float rotation;
    vec2 scale;
    vec2 offset;
};

struct Material {
    vec4 color;
    vec4 specColor;
    TexRef diffuse;
    TexRef normal;
    TexRef specular;
    TexRef emissive;
    float glow;
    float envIntensity;
    float alphaCutoff;
    float metallic;
    float roughness;
    uint flags;
    uint bump;
    float shadowCutoff;
};

#include "sky_block.glsl"

struct Light {
    vec4 positionRadius;
    vec4 colorFalloff;
    vec4 directionCutoff;
    vec4 params;
};

layout(buffer_reference, scalar) readonly buffer ViewRef { View v[]; };
layout(buffer_reference, scalar) readonly buffer InstanceRef { Instance i[]; };
layout(buffer_reference, scalar) readonly buffer MaterialRef { Material m[]; };
layout(buffer_reference, scalar) readonly buffer LightRef { Light l[]; };
layout(buffer_reference, scalar) readonly buffer SkyRef { SkyBlock s[]; };

// Per-frame data lives in the frame arena; the push block carries device addresses into it. A multiview pass (both eyes
// at once) points viewRef and skyRef at the eyes' views and skies side by side, each eye reading its own by
// gl_ViewIndex (VIEW_INDEX, 0 elsewhere).
layout(push_constant, scalar) uniform Push {
    ViewRef viewRef;
    InstanceRef instRef;
    MaterialRef matRef;
    LightRef lightRef;
    SkyRef skyRef;
    uint i0, i1, i2, i3;
    vec4 v0;
    vec4 v1;
    InstanceRef persistentInstRef;   // the persistent tables (Frame/PersistentTable.cs): indices with PERSISTENT set
    MaterialRef persistentMatRef;
} pc;

#define VIEW (pc.viewRef.v[VIEW_INDEX])
#define PERSISTENT 0x40000000u
// an instance or material by index: the frame's own, or with PERSISTENT set the persistent table's
#define INSTANCE(id) (((id) & PERSISTENT) != 0u ? pc.persistentInstRef.i[(id) & ~PERSISTENT] : pc.instRef.i[(id)])
#define MATERIAL(id) (((id) & PERSISTENT) != 0u ? pc.persistentMatRef.m[(id) & ~PERSISTENT] : pc.matRef.m[(id)])
#define LIGHTS (pc.lightRef.l)
#define SKY (pc.skyRef.s[VIEW_INDEX])

#define MAT_ALPHA_MODE(m)   ((m).flags & 3u)
#define MAT_FULLBRIGHT      (1u << 2)
#define MAT_PBR             (1u << 3)
#define MAT_DOUBLE_SIDED    (1u << 4)
#define MAT_SHINY(m)        (((m).flags >> 5) & 3u)
#define MAT_MEDIA           (1u << 7)
#define MAT_LEGACY_MATERIAL (1u << 8)
#define MAT_TERRAIN         (1u << 9)
#define MAT_WATER           (1u << 10)
#define MAT_AVATAR          (1u << 11)
#define MAT_FOLIAGE         (1u << 13)
#define MAT_TERRAIN_PBR     (1u << 14)

#define ALPHA_NONE 0u
#define ALPHA_BLEND 1u
#define ALPHA_MASK 2u
#define ALPHA_EMISSIVE 3u

#ifdef FRAGMENT_SHADER
// a pixel the lenses hide, far enough inside the hidden area that nothing seen depends on it (Frame/Renderer.Lens.cs):
// the per-pixel passes skip it
bool lensHidden()
{
#ifdef HALF_LIGHT
    // a half-size light map's texel stands for the 2x2 block it was lit at the top left of (Renderer.HalfLight.cs)
    return VIEW.lensMask != 0u && texelFetch(textures[nonuniformEXT(int(VIEW.lensMask - 1u))], ivec2(gl_FragCoord.xy) * 2, 0).r > 0.5;
#else
    return VIEW.lensMask != 0u && texelFetch(textures[nonuniformEXT(int(VIEW.lensMask - 1u))], ivec2(gl_FragCoord.xy), 0).r > 0.5;
#endif
}
#endif

// gl_ClipDistance for a vertex at world: the view's clip plane, and -1 for an instance the view doesn't draw
float viewClip(vec3 world, uint instFlags)
{
    uint m = VIEW.mask == 0u ? VIEW_MAIN : VIEW.mask;
    uint kinds = instFlags & (VIEW_MAIN | VIEW_MIRROR);   // the other bits aren't view kinds (INSTANCE_SELF)
    if (kinds != 0u && (kinds & m) == 0u) return -1.0;
    return dot(VIEW.clipPlane.xyz, world) + VIEW.clipPlane.w;
}

// SL texture entry transform: rotate about the texture centre, then repeat and offset.
vec2 teTransform(vec2 uv, TexRef t)
{
    float c = cos(t.rotation), s = sin(t.rotation);
    vec2 d = uv - 0.5;
    vec2 r = vec2(d.x * c + d.y * s, -d.x * s + d.y * c);
    return r * t.scale + t.offset + 0.5;
}

// KHR_texture_transform as the viewer applies it to PBR materials on SL geometry
// (class1/deferred/textureUtilV.glsl texture_transform, LGPL 2.1, Linden Research, Inc.)
vec2 khrTransform(vec2 texcoord, TexRef t)
{
    texcoord.y = 1.0 - texcoord.y;
    float c = cos(t.rotation), s = sin(t.rotation);
    vec2 sc = texcoord * t.scale;
    texcoord = vec2(c * sc.x + s * sc.y, -s * sc.x + c * sc.y) + t.offset;
    texcoord.y = 1.0 - texcoord.y;
    return texcoord;
}

vec4 sampleTex(int index, vec2 uv, vec4 fallback)
{
    if (index < 0) return fallback;
    return texture(textures[nonuniformEXT(index)], uv);
}

vec3 srgb_to_linear(vec3 cs)
{
    vec3 low_range = cs / vec3(12.92);
    vec3 high_range = pow((cs + vec3(0.055)) / vec3(1.055), vec3(2.4));
    bvec3 lte = lessThanEqual(cs, vec3(0.04045));
    return mix(high_range, low_range, lte);
}

vec3 linear_to_srgb(vec3 cl)
{
    cl = clamp(cl, vec3(0), vec3(1));
    vec3 low_range = cl * 12.92;
    vec3 high_range = 1.055 * pow(cl, vec3(0.41666)) - 0.055;
    bvec3 lt = lessThan(cl, vec3(0.0031308));
    return mix(high_range, low_range, lt);
}
