using LibreMetaverse;

namespace Sojourn.Render.World;

public sealed partial class Avatars
{
    /// <summary>How an avatar was drawn in the last frame and its bakes (skin and system clothing as the region baked
    /// them) as the texture store has them, for describing (<see cref="Realtime.FrameRenderer.LoadOf"/>): drawn "full",
    /// "impostor", "jelly" (a plain stand-in over the complexity limit), "cloud" (its appearance or a body bake not in:
    /// a loading cloud, nothing of its look drawn), or null when it wasn't among the last frame's avatars (out of reach
    /// or left out of the picture). Render thread.</summary>
    internal (string? Drawn, int Bakes, int Loading, int Missing) BakesOf(UUID id)
    {
        for (int i = 0; i < _entryCount; i++)
        {
            var e = _entries[i];
            if (e.Clone || e.Av.ID != id) continue;
            var av = e.Av;
            int bakes = 0, loading = 0, missing = 0;
            bool body = false;
            var vp = av.VisualParameters;
            bool appearance = vp is { Length: > 1 } && (Defined(BakeId(av, TeHead)) || Defined(BakeId(av, TeUpper)) || Defined(BakeId(av, TeLower)));
            if (appearance)
                foreach (int te in BakeFaces)
                {
                    var bid = BakeId(av, te);
                    if (!Visible(bid)) continue;
                    bakes++;
                    var state = _world.Textures.TryGet(bid, out var t) ? t.State : TexState.Loading;
                    if (state == TexState.Loading) loading++;
                    else if (state == TexState.Missing) missing++;
                    if (state != TexState.Ready && te is TeHead or TeUpper or TeLower) body = true;
                }
            string drawn = e.Self ? "full" : e.Drawn;
            if (e.How == Draw.Jelly) drawn = "jelly";
            else if (drawn is "loading" or "checking" || !appearance || body) drawn = "cloud";
            return (drawn, bakes, loading, missing);
        }
        return (null, 0, 0, 0);
    }
}
