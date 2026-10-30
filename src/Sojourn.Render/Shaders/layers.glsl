// See-through layers (Frame/Renderer.Layers.cs): one record per pixel of a main eye, written by alpha_count.frag before
// the blended faces are drawn and read by alpha.frag (LAYER_CUT). A record is a budget spent and a depth: the top 8 bits
// count what the layers met so far hide, in units of LAYER_FULL = transmittance down to 1% (ln 100); the low 24 bits
// are the farthest of those layers' depths (reverse-Z, the float's bits shifted down: truncated, so never nearer than
// the layer). Once a pixel's budget is spent, a fragment behind every layer counted is dropped: what it would have added
// was let through by layers that together pass less than 1% of it, or by the cap's N layers.
// Needs common.glsl. Claude Opus 5.5, 2026-10-27.

layout(buffer_reference, scalar) buffer LayerRef { uint p[]; };

const uint LAYER_FULL = 64u;
const float LAYER_UNITS = 64.0 / 4.6051702;   // units per unit of optical depth (-ln of what a layer lets through)
const float LAYER_MIN_ALPHA = 0.1;            // fainter fragments never count as a layer

uint layerKey(float z) { return floatBitsToUint(z) >> 6; }

LayerRef layerRecords() { return LayerRef(uint64_t(VIEW.layersLo) | (uint64_t(VIEW.layersHi) << 32)); }

uint layerIndex() { return uint(gl_FragCoord.y) * uint(VIEW.viewport.x) + uint(gl_FragCoord.x); }

// alpha.frag: behind the pixel's counted layers once its budget is spent (a key's step of margin: the same face's depth in
// two pipelines may differ in its last bits)
bool layerDropped()
{
    if (VIEW.layerMin == 0u) return false;
    uint r = layerRecords().p[layerIndex()];
    return (r >> 24) >= LAYER_FULL && layerKey(gl_FragCoord.z) + 1u < (r & 0xFFFFFFu);
}
