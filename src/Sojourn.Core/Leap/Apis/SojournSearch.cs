using System.Text.Json.Nodes;
using Sojourn.Core.Agents;

namespace Sojourn.Core.Leap;

/// <summary>SL's own search (the viewers' legacy search): places, events, classifieds, each with where it is
/// (Agents/SearchOps.cs). People and groups are TesselAgent's and TesselGroups'. Claude Opus 5.5, 2026-10-26.</summary>
public sealed class SojournSearch : SessionApi
{
    public override string Name => "SojournSearch";
    public override string Desc => "SL's search: places, events, classifieds, with where each is (go_to: \"Region x,y,z\")";

    public SojournSearch(AgentSession s) : base(s)
    {
        var o = s.Search;
        Op("places", $"{{text (3+ characters), category?: {string.Join("|", SearchOps.PlaceCategories)}, for_sale?, limit=8, start?}} -> "
            + "{places: [{name, traffic, region, go_to, about, moderate_or_adult, for_sale?}], found}: busiest first",
            async (a, ct) => await o.PlacesAsync(a, ct));
        Op("events", $"{{text?, when?: now (on now and later today, the default)|today|tomorrow|days from today, category?: "
            + $"{string.Join("|", SearchOps.EventCategories)}, limit=8, start?}} -> {{events: [{{name, starts (local time), on_now?, "
            + "minutes, region, go_to, about, category, cover_charge?, rating}], found}: soonest first",
            async (a, ct) => await o.EventsAsync(a, ct));
        Op("classifieds", $"{{text (3+ characters), category?: {string.Join("|", SearchOps.ClassifiedCategories)}, limit=8}} -> "
            + "{classifieds: [{name, region, parcel, go_to, about}], found}: the ads residents pay to list",
            async (a, ct) => await o.ClassifiedsAsync(a, ct));
        Op("land", $"{{type?: {string.Join("|", SearchOps.LandTypes)}, max_price?, min_area?, sort?: {string.Join("|", SearchOps.LandSorts)}, "
            + "limit=8, start?} -> {land: [{name, price, area, price_per_m, auction?, region, go_to, about}], found}: land for sale "
            + "(SojournLand buy, there, buys it)", async (a, ct) => await o.LandAsync(a, ct));
    }
}
