// A projector (spot light with a texture) as the draw list writes it (Frame/DrawList projectors), shared by
// projectors.frag and tilecull.comp.
struct Projector {
    mat4 regionToTc;      // trans * perspective * inverse(light frame): region -> projector texture space
    vec4 centerSize;      // light position, size (radius * 1.5)
    vec4 colorFalloff;    // linear colour * intensity, falloff (DEFERRED_LIGHT_FALLOFF)
    vec4 originRange;     // frustum apex, proj_range (far - near)
    vec4 normalFocus;     // proj_n, proj_focus
    vec4 planeAmbiance;   // proj_p, proj_ambiance
    int texture;          // bindless slot, -1 = white
    float lod;            // proj_lod: log2(texture width)
    int shadowIndex;      // proj_shadow_idx (-1: unshadowed)
    float shadowFade;
};
layout(buffer_reference, scalar) readonly buffer ProjectorRef { Projector p[]; };
