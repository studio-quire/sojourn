// Light tiles (tilecull.comp, Frame/Renderer.Tiles.cs): for each 16x16-pixel tile of an eye, the local lights and
// projectors that can reach any of its pixels. A tile's record is TILE_STRIDE uints: a header (lights | projectors
// << 16), the lights' indices, then the projectors'. TILE_ALL in the header (more than fit) or no tiles for the view
// (probe faces, mirrors) means every light. Written by Claude Opus 5.5, 2026-10-27.
#define TILE_SIZE 16u
#define TILE_STRIDE 256u
#define TILE_ALL 0xFFFFFFFFu

layout(buffer_reference, scalar) buffer TileListRef { uint d[]; };

#ifdef FRAGMENT_SHADER
// this fragment's tile record: its header (TILE_ALL when there are no tiles) and where its indices start
uint tileHeader(out TileListRef tiles, out uint base)
{
    uint64_t addr = uint64_t(VIEW.tilesLo) | (uint64_t(VIEW.tilesHi) << 32);
    base = 0u;
    tiles = TileListRef(addr);
    if (addr == 0ul) return TILE_ALL;
    uvec2 t = uvec2(gl_FragCoord.xy) / TILE_SIZE;
    base = (t.y * VIEW.tileCols + t.x) * TILE_STRIDE;
    return tiles.d[base];
}
#endif
