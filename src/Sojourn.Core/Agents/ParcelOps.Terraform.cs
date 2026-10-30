using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Terraforming as the viewer's land brush (lltoolbrush.cpp): ModifyLand at a point with a brush of 1, 2 or 4 m, or
/// over a parcel or rectangle, flattening (toward the height where it starts, or a height given), raising, lowering,
/// smoothing, roughening or reverting to the region's last bake. The viewer sends one message a frame while the button
/// is held, each worth the frame's time times LandBrushForce (1); here a stroke of `seconds` goes as pulses ten a
/// second, so the land changes as it would under the hand. The client shows the brush on the ground meanwhile (World/
/// ParcelLines.cs) and the terrain rebuilds as the region's patches come back (World/Terrain.cs). Claude Opus 5.5,
/// 2026-10-28 (batch B5).
/// </summary>
public sealed partial class ParcelOps
{
    public static readonly string[] Actions = { "flatten", "raise", "lower", "smooth", "roughen", "revert" };
    static readonly float[] BrushSizes = { 1f, 2f, 4f };   // LAND_BRUSH_SIZE

    /// <summary>The client's: the brush on the ground (centre, half-size in metres; null centre: a rectangle w,s,e,n)
    /// for this long, and the terrain hurried meanwhile.</summary>
    public Action<Vector3, float, (float W, float S, float E, float N)?, double>? Brushing { get; set; }

    /// <summary>Whether a terraform reshapes a whole area (a parcel or rectangle), which waits for the person.</summary>
    public static bool AreaTerraform(JsonObject a) => a.Bool("parcel", false) || a.Has("west") || a.Has("north");

    static byte Action(JsonObject a) => (a.Str("action") ?? "").Trim().ToLowerInvariant() switch
    {
        "flatten" or "level" => 0,
        "raise" => 1,
        "lower" => 2,
        "smooth" => 3,
        "roughen" or "noise" => 4,
        "revert" => 5,
        _ => throw new LeapError($"action: {string.Join(" | ", Actions)}"),
    };

    public async Task<JsonObject> TerraformAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var sim = c.Network.CurrentSim ?? throw new LeapError("no region");
        var w = _s.Region ?? throw new LeapError("no region");
        byte action = Action(a);
        if ((sim.Flags & RegionFlags.BlockTerraform) != 0) throw new LeapError("this region doesn't allow terraforming");
        float size = (float)Math.Clamp(a.Num("size", 2), 0.5, 4);
        // getBrushIndex: the last size the brush is bigger than (compatibility with old regions; the float goes too)
        byte index = 0;
        for (byte i = 0; i < BrushSizes.Length; i++) if (size > BrushSizes[i]) index = i;
        double seconds = Math.Clamp(a.Num("seconds", 1), 0.05, 10);

        int localId = -1;
        (float W, float S, float E, float N)? area = null;
        Vector3 centre;
        Parcel? parcel = null;
        if (a.Bool("parcel", false))
        {
            (_, parcel) = await ParcelAsync(PointOf(a), ct);
            localId = parcel.LocalID;
            area = (parcel.AABBMin.X, parcel.AABBMin.Y, parcel.AABBMax.X, parcel.AABBMax.Y);
        }
        else if (a.Has("west") || a.Has("north"))
            area = Rect(a, null, sim.SizeX) ?? throw new LeapError("an area needs west, south, east, north (metres)");
        if (area is { } r) centre = new Vector3((r.W + r.E) / 2, (r.S + r.N) / 2, 0);
        else
        {
            centre = Point(a) ?? c.Self.SimPosition;
            // the viewer's brush sits on whole metres
            centre = new Vector3(MathF.Floor(centre.X + 0.5f), MathF.Floor(centre.Y + 0.5f), 0);
            (_, parcel) = await ParcelAsync(new JsonObject { ["at"] = new JsonArray((double)centre.X, (double)centre.Y) }, ct);
        }
        if (centre.X < 0 || centre.Y < 0 || centre.X >= sim.SizeX || centre.Y >= sim.SizeY) throw new LeapError("that's outside this region");
        // the estate's owner and managers may terraform anywhere in it (canTerraformParcel)
        if (parcel != null && !Mine(parcel) && !_s.Estate.CanManage && (parcel.Flags & ParcelFlags.AllowTerraform) == 0
            && !(parcel.IsGroupOwned && GroupPower(parcel.OwnerID, GroupPowers.AllowEditLand)))
            throw new LeapError($"{parcel.Name} isn't yours and doesn't let others terraform (owner {NameOf(parcel.OwnerID)})");

        float before = w.TerrainHeight(centre.X, centre.Y) ?? float.NaN;
        // where the stroke starts is what flatten levels toward (mStartingZ), unless a height is given
        float height = a.Num("height") is { } h ? (float)h : float.IsNaN(before) ? sim.WaterHeight : before;
        // over an area the viewer weighs each action (modifyLandInSelectionGlobal)
        double weight = area is null ? 1 : action switch { 3 => 5, 4 => 0.5, 5 => 1, _ => 0.25 };
        int pulses = Math.Max(1, (int)Math.Ceiling(seconds * 10));
        float each = (float)(action == 5 && area is not null ? 0.5 : seconds * weight / pulses);
        if (action == 5 && area is not null) pulses = 1;

        Brushing?.Invoke(centre, size, area, pulses * 0.1 + 3);
        var since = DateTime.UtcNow;
        for (int i = 0; i < pulses; i++)
        {
            var (west, south, east, north) = area ?? (centre.X, centre.Y, centre.X, centre.Y);
            c.Network.SendPacket(new ModifyLandPacket
            {
                AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
                ModifyBlock = { Action = action, BrushSize = index, Seconds = each, Height = height },
                ParcelData = new[] { new ModifyLandPacket.ParcelDataBlock { LocalID = localId, West = west, South = south, East = east, North = north } },
                ModifyBlockExtended = new[] { new ModifyLandPacket.ModifyBlockExtendedBlock { BrushSize = size } },
            }, sim);
            await Task.Delay(100, ct);
        }
        // the region sends the changed patches back
        await Task.Delay(1500, ct);
        float after = w.TerrainHeight(centre.X, centre.Y) ?? float.NaN;
        var res = new JsonObject
        {
            ["action"] = Actions[action], ["at"] = Json.Arr(centre.X, centre.Y),
            ["height_before"] = float.IsNaN(before) ? null : Json.R(before, 2), ["height_after"] = float.IsNaN(after) ? null : Json.R(after, 2),
        };
        if (area is { } ar) res["area"] = Json.Arr(ar.W, ar.S, ar.E, ar.N); else res["size"] = size;
        if (!float.IsNaN(before) && MathF.Abs(after - before) < 0.005f && action is 1 or 2)
            res["note"] = $"the ground didn't move here{Said(since)} (the parcel's terraform option, the estate's raise and lower limits, or the region may hold it)";
        return res;
    }
}
