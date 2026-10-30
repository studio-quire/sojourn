// The water exclusion mask (Frame/Renderer.Water.cs, the viewer's mWaterExclusionMask read as exclusionTex): 1 where
// water shows, 0 where a water exclusion face is in front of the water's surface. Stored inverted (cleared to 0, the
// faces write 1), so this returns the viewer's value; a slot of 0xFFFF (nothing excludes water this frame) is all 1.
float waterExclusionMask(int slot, vec2 tc)
{
    return slot >= 0 && slot < 0xFFFF ? 1.0 - texture(textures[nonuniformEXT(slot)], tc).r : 1.0;
}
