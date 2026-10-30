#version 460
// See-through layers' count (layers.glsl, Frame/Renderer.Layers.cs): the blended faces drawn nearest first, depth-tested
// against the opaque scene, no colour. A fragment counts if it would show at alpha 0.1 or more, by what it hides
// (-ln(1 - alpha)) but at least the cap's share of a layer (VIEW.layerMin units: N layers spend the budget), until the
// pixel's budget is spent. Its alpha is read as alpha.frag reads it, or less (materialF's glare can only add): a counted
// layer is always at least as opaque as it's counted, whichever fragments get counted first.
#include "common.glsl"
#include "layers.glsl"

#ifndef INSTANCE
#define INSTANCE(id) (INSTANCES[id])
#define MATERIAL(id) (MATERIALS[id])
#endif

layout(early_fragment_tests) in;

layout(location = 3) in vec2 vUv;
layout(location = 4) flat in uint vInstance;

void main()
{
    if (VIEW.layerMin == 0u) return;
    Instance inst = INSTANCE(vInstance);
    Material m = MATERIAL(inst.material);
    // the texture's level from these gradients, taken while the whole quad is here (the returns below would leave
    // implicit ones undefined): the alpha alpha.frag reads
    vec2 uv = (m.flags & MAT_PBR) != 0u ? khrTransform(vUv, m.diffuse) : teTransform(vUv, m.diffuse);
    vec2 du = dFdx(uv), dv = dFdy(uv);
    if (lensHidden()) return;
    // a pixel whose budget is spent takes nothing more: nearest first, most fragments of a deep stack stop here
    LayerRef records = layerRecords();
    uint i = layerIndex();
    uint old = records.p[i];
    if ((old >> 24) >= LAYER_FULL) return;
    if (!gl_FrontFacing && (m.flags & (MAT_DOUBLE_SIDED | MAT_AVATAR)) == 0u) return;   // alpha.frag drops it
    float a = (m.diffuse.index < 0 ? 1.0 : textureGrad(textures[nonuniformEXT(m.diffuse.index)], uv, du, dv).a) * m.color.a;
    if (a < LAYER_MIN_ALPHA || ((m.flags & MAT_AVATAR) != 0u && a < m.alphaCutoff)) return;
    uint w = a >= 0.99 ? LAYER_FULL : max(uint(-log(1.0 - a) * LAYER_UNITS), VIEW.layerMin);

    uint key = layerKey(gl_FragCoord.z);
    for (int tries = 0; tries < 8; tries++)   // bounded: a fragment not counted only means less is dropped
    {
        uint spent = old >> 24;
        if (spent >= LAYER_FULL) return;
        uint farthest = spent == 0u ? key : min(old & 0xFFFFFFu, key);
        uint seen = atomicCompSwap(records.p[i], old, (min(spent + w, 255u) << 24) | farthest);
        if (seen == old) return;
        old = seen;
    }
}
