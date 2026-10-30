using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Places the person might go to that the viewers hand them: the teleport history (LLTeleportHistory and its storage:
/// each arrival by teleport and at login, kept per account, one place within 5 m kept once with its latest time), and
/// offers of a place on the feed with a Teleport key: a script's llMapDestination (the viewer opens the world map with
/// a beacon and a Teleport button, process_script_teleport_request), and SLurls in chat, IMs and notecards (links the
/// viewer makes clickable: a click offers the place). Here they become a notice the person's own press answers, or the
/// assistant's answer when the person asks. Nothing from an incognito spell is recorded. Claude Opus 5.5, 2026-10-27.
/// </summary>
public sealed class Places
{
    readonly AgentSession _s;
    public Places(AgentSession s) => _s = s;

    // ---------- SLurls ----------

    public sealed record Place(string Region, Vector3 Pos)
    {
        public string GoTo => $"{Region} {(int)Pos.X},{(int)Pos.Y},{(int)Pos.Z}";
        public string Slurl => $"secondlife:///app/teleport/{Uri.EscapeDataString(Region)}/{(int)Pos.X}/{(int)Pos.Y}/{(int)Pos.Z}";
    }

    // the forms LLSLURL reads: secondlife:///app/teleport|region|worldmap/..., secondlife://Region/x/y/z, and the web's
    // maps.secondlife.com / slurl.com /secondlife/Region/x/y/z
    static readonly Regex Slurls = new(
        @"(?:secondlife:///app/(?:teleport|region|worldmap)/|secondlife://(?!/)|https?://(?:maps\.secondlife\.com|(?:www\.)?slurl\.com)/secondlife/)" +
        @"(?<region>[^/\s""'<>]+)(?:/(?<x>-?\d+(?:\.\d+)?)(?:/(?<y>-?\d+(?:\.\d+)?)(?:/(?<z>-?\d+(?:\.\d+)?))?)?)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Every place a text links to, in order, once each.</summary>
    public static List<Place> Parse(string? text)
    {
        var found = new List<Place>();
        foreach (var (_, _, p) in Find(text))
            if (!found.Any(f => Same(f, p))) found.Add(p);
        return found;
    }

    /// <summary>Each SLurl in a text where it stands (start, length) and the place it names, in order (the feed and
    /// the reader draw them as links: Client/Social/Links.cs).</summary>
    public static List<(int Start, int Length, Place Place)> Find(string? text)
    {
        var found = new List<(int, int, Place)>();
        if (string.IsNullOrEmpty(text)) return found;
        foreach (Match m in Slurls.Matches(text))
        {
            var region = Uri.UnescapeDataString(m.Groups["region"].Value.Replace('+', ' ')).Trim();
            if (region.Length is 0 or > 64 || region.Equals("app", StringComparison.OrdinalIgnoreCase)) continue;
            float N(string g, float d) => float.TryParse(m.Groups[g].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
            var pos = new Vector3(Math.Clamp(N("x", 128), 0, 256), Math.Clamp(N("y", 128), 0, 256), Math.Clamp(N("z", 0), 0, 4096));
            found.Add((m.Index, m.Length, new Place(region, pos)));
        }
        return found;
    }

    static bool Same(Place a, Place b) =>
        string.Equals(a.Region, b.Region, StringComparison.OrdinalIgnoreCase) && Vector3.Distance(a.Pos, b.Pos) < 5;

    // ---------- offers on the feed ----------

    const int MaxOffers = 4;
    static readonly TimeSpan OfferKept = TimeSpan.FromMinutes(3);
    readonly List<(Guid Id, Place Place, DateTime At)> _offers = new();

    /// <summary>A place offered to the person: a notice with a Teleport key (and its × to put it away). The same place
    /// again replaces it; at most four wait, the oldest going first; unanswered, one goes after three minutes.</summary>
    public Guid Offer(Place p, string source, string from, UUID fromId = default)
    {
        var key = $"place:{p.Region.ToLowerInvariant()}/{(int)p.Pos.X / 5}/{(int)p.Pos.Y / 5}";
        var where = $"{p.Region} ({(int)p.Pos.X}, {(int)p.Pos.Y}, {(int)p.Pos.Z})";
        var id = _s.Notify.Add("PlaceOffered", "offer", $"{(from.Length > 0 ? from : "Something")} offers a place: {where}",
            new[] { new NotifyButton("Teleport", "Teleport", 0, true), new NotifyButton("Cancel", "Cancel", -1) },
            payload: new JsonObject
            {
                ["region"] = p.Region, ["pos"] = Json.Arr(p.Pos), ["go_to"] = p.GoTo, ["slurl"] = p.Slurl, ["source"] = source,
                ["from_name"] = from, ["from_id"] = fromId == default ? null : fromId.ToString(),
            },
            substitutions: new JsonObject { ["NAME"] = from, ["LOCATION"] = where, ["SOURCE"] = source },
            key: key,
            respond: (button, _) =>
            {
                if (button == "Teleport")
                    _s.TeleportAsync(p.Region, p.Pos).ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully && !t.Result.Ok) _s.Log.LogInformation("teleport to an offered place failed: {Why}", t.Result.Reason);
                    });
                return Task.CompletedTask;
            });
        lock (_offers)
        {
            _offers.RemoveAll(o => o.Id == id || Same(o.Place, p) || _s.Notify.Get(o.Id) == null);
            _offers.Add((id, p, DateTime.UtcNow));
            while (_offers.Count > MaxOffers) { _s.Notify.Dismiss(_offers[0].Id); _offers.RemoveAt(0); }
        }
        return id;
    }

    /// <summary>The places a text links to, offered (at most three a text; the assistant can offer the rest).</summary>
    public int OfferFrom(string text, string source, string from, UUID fromId = default)
    {
        int n = 0;
        foreach (var p in Parse(text).Take(3)) { Offer(p, source, from, fromId); n++; }
        return n;
    }

    /// <summary>llMapDestination (ScriptTeleportRequest): the script's object names a region and a point.</summary>
    void OnScriptTeleport(PacketReceivedEventArgs e)
    {
        if (e.Packet is not ScriptTeleportRequestPacket p) return;
        var obj = Utils.BytesToString(p.Data.ObjectName);
        var region = Utils.BytesToString(p.Data.SimName);
        if (region.Length == 0 || _s.Mutes.IsMuted(UUID.Zero, obj)) return;
        Offer(new Place(region, p.Data.SimPosition), "map", obj);
        _s.Log.LogInformation("{Object} offers a map destination: {Region} {Pos}", obj, region, p.Data.SimPosition);
    }

    // ---------- the teleport history ----------

    const int MaxHistory = 200;
    readonly object _hlock = new();
    List<JsonObject>? _history;
    DateTime _sessionStart = DateTime.UtcNow;

    string HistoryFile => Path.Combine(_s.StateDir, "teleport-history.json");

    List<JsonObject> History()
    {
        if (_history != null) return _history;
        try
        {
            _history = File.Exists(HistoryFile)
                ? (JsonNode.Parse(File.ReadAllText(HistoryFile)) as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList()
                : new List<JsonObject>();
        }
        catch (Exception e) { _s.Log.LogWarning(e, "teleport history"); _history = new List<JsonObject>(); }
        return _history;
    }

    void SaveHistory()
    {
        try
        {
            var tmp = HistoryFile + ".tmp";
            File.WriteAllText(tmp, new JsonArray(History().Select(o => (JsonNode)o.DeepClone()).ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, HistoryFile, overwrite: true);
        }
        catch (Exception e) { _s.Log.LogWarning(e, "saving the teleport history"); }
    }

    /// <summary>Records where the person has arrived (after a teleport or at login), once the region has said where
    /// they stand and what the parcel is called.</summary>
    void Arrived(GridClient c, string how)
    {
        if (_s.Private.On) return;
        var when = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _ = Task.Run(async () =>
        {
            try
            {
                // this arrival's region and spot, held as the wait goes on: a teleport onward before the parcel's name
                // came would otherwise record the next place in place of this one (and lose this stop)
                Simulator? sim = null;
                var pos = Vector3.Zero;
                string? parcel = null;
                for (int i = 0; i < 12; i++)
                {
                    await Task.Delay(500);
                    if (c.Network.CurrentSim is not { } now) return;
                    var at = c.Self.SimPosition;
                    if (sim == null) sim = now;
                    else if (now != sim) break;
                    // moved on within the region (a walk, or a teleport there before the parcel's name came, which
                    // recorded the next place in place of this one, 2026-10-29): this stop is where it landed
                    else if (new Vector2(at.X - pos.X, at.Y - pos.Y).Length() > 2) break;
                    pos = at;   // (settling: a fall to the ground)
                    var local = c.Parcels.GetParcelLocalID(sim, pos);
                    if (local != 0 && sim.Parcels.TryGetValue(local, out var pl) && pl.Name is { Length: > 0 } pn) { parcel = pn; break; }
                }
                if (_s.Private.On || sim == null) return;
                Utils.LongToUInts(sim.Handle, out var gx, out var gy);
                Record(sim.Name, sim.ID, pos, parcel, how, (gx, gy), when);
            }
            catch (Exception e) { _s.Log.LogDebug(e, "teleport history"); }
        });
    }

    void Record(string region, UUID regionId, Vector3 pos, string? parcel, string how, (uint X, uint Y) corner, long now)
    {
        lock (_hlock)
        {
            var h = History();
            int at = h.FindIndex(o => string.Equals(o.Str("region"), region, StringComparison.OrdinalIgnoreCase)
                                     && o["pos"] is JsonArray p && p.Count == 3
                                     && Vector3.Distance(new Vector3((float)(Json.AsDouble(p[0]) ?? 0), (float)(Json.AsDouble(p[1]) ?? 0), (float)(Json.AsDouble(p[2]) ?? 0)), pos) < 5);
            // the same place again: kept once, at its first position (a teleport there lands a little higher each time)
            JsonObject entry;
            if (at >= 0) { entry = h[at]; h.RemoveAt(at); }
            else entry = new JsonObject { ["region"] = region, ["pos"] = Json.Arr(new Vector3(MathF.Round(pos.X), MathF.Round(pos.Y), MathF.Round(pos.Z))) };
            entry["region_id"] = regionId.ToString();
            entry["global"] = new JsonArray(corner.X + (double)Math.Round(pos.X), corner.Y + (double)Math.Round(pos.Y));
            if (parcel != null) entry["parcel"] = parcel;
            entry["when"] = now;
            entry["how"] = how;
            // by arrival: a quick stop's record can come after the next place's
            h.Insert(h.FindLastIndex(o => (o.Num("when") ?? 0) <= now) + 1, entry);
            if (h.Count > MaxHistory) h.RemoveRange(0, h.Count - MaxHistory);
            SaveHistory();
        }
    }

    public JsonObject HistoryJson(JsonObject a)
    {
        int count = Math.Clamp(a.Int("count", 20), 1, MaxHistory);
        var since = new DateTimeOffset(_sessionStart).ToUnixTimeSeconds();
        lock (_hlock)
        {
            var list = new JsonArray();
            var h = History();
            for (int i = h.Count - 1, n = 0; i >= 0 && n < count; i--, n++)
            {
                var o = (JsonObject)h[i].DeepClone();
                var p = o["pos"] as JsonArray;
                o["go_to"] = $"{o.Str("region")} {(int)(Json.AsDouble(p?[0]) ?? 128)},{(int)(Json.AsDouble(p?[1]) ?? 128)},{(int)(Json.AsDouble(p?[2]) ?? 0)}";
                var when = o.Num("when") ?? 0;
                o["when"] = DateTimeOffset.FromUnixTimeSeconds((long)when).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                o["this_session"] = when >= since;
                if (n == 0) o["here"] = true;
                list.Add(o);
            }
            return new JsonObject { ["places"] = list, ["kept"] = h.Count };
        }
    }

    /// <summary>The latest place in the teleport history in none of the regions in <paramref name="notRegions"/>
    /// (where the way out goes when home is refused), or null.</summary>
    public Place? Elsewhere(ICollection<string> notRegions)
    {
        lock (_hlock)
        {
            var h = History();
            for (int i = h.Count - 1; i >= 0; i--)
            {
                var region = h[i].Str("region");
                if (string.IsNullOrEmpty(region) || notRegions.Any(n => string.Equals(region, n, StringComparison.OrdinalIgnoreCase))) continue;
                var p = h[i]["pos"] as JsonArray;
                return new Place(region!, new Vector3((float)(Json.AsDouble(p?[0]) ?? 128), (float)(Json.AsDouble(p?[1]) ?? 128), (float)(Json.AsDouble(p?[2]) ?? 30)));
            }
            return null;
        }
    }

    public JsonObject ClearHistory()
    {
        lock (_hlock)
        {
            int n = History().Count;
            History().Clear();
            SaveHistory();
            return new JsonObject { ["cleared"] = n };
        }
    }

    // ---------- wiring ----------

    internal void Wire(GridClient c)
    {
        c.Network.RegisterCallback(PacketType.ScriptTeleportRequest, (_, e) =>
        {
            try { OnScriptTeleport(e); } catch (Exception x) { _s.Log.LogWarning(x, "map destination"); }
        });
        c.Self.TeleportProgress += (_, e) => { if (e.Status == TeleportStatus.Finished) Arrived(c, "teleport"); };
    }

    internal void OnLoggedIn(GridClient c)
    {
        _sessionStart = DateTime.UtcNow;
        Arrived(c, "login");
    }

    /// <summary>Twice a second (the client's timer): offers nobody answered go after a while.</summary>
    public void Tick()
    {
        lock (_offers)
        {
            var now = DateTime.UtcNow;
            foreach (var o in _offers.Where(o => now - o.At > OfferKept).ToList()) { _s.Notify.Dismiss(o.Id); _offers.Remove(o); }
            _offers.RemoveAll(o => _s.Notify.Get(o.Id) == null);
        }
    }
}
