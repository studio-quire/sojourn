using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;

namespace Sojourn.Session.Social;

/// <summary>
/// Firestorm's radar reports (FSRadarReportChatRangeEnter/Leave, DrawRangeEnter/Leave, SimRangeEnter/Leave, all off by
/// default there and here): someone coming into or leaving chat range (20 m), the draw distance, or the region, as a
/// notice on the feed ("RadarReport"), which the person's screen reader reads; and its RadarAvatarAgeAlert, someone
/// this many days old or younger coming within draw distance. Distances from the person's avatar to theirs, as
/// Firestorm's radar measures. Who's there when the person arrives (logging in, a teleport, a region crossing) is
/// taken as it is, not reported: an arrival at a club would be a flood. Ticked once a second (SocialWiring).
/// Claude Opus 5.5, 2026-10-28 (batch B4).
/// </summary>
public sealed class Radar
{
    const float ChatRange = 20;
    const double Settle = 8;   // seconds after an arrival before changes are told

    readonly AgentSession _s;
    readonly SocialSettings _opt;
    readonly Dictionary<UUID, (bool Chat, bool Draw, bool Region, float D)> _was = new();
    readonly HashSet<UUID> _aged = new();
    ulong _region;
    DateTime _arrived = DateTime.MinValue;
    int _reports;

    public Radar(AgentSession s, SocialSettings opt)
    {
        _s = s;
        _opt = opt;
    }

    public int Reports => _reports;

    bool AnyOn => _opt.RadarChat != "off" || _opt.RadarDraw != "off" || _opt.RadarRegion != "off" || _opt.RadarAgeDays > 0;

    /// <summary>Everyone the radar sees now: distance and which ranges they're in.</summary>
    public List<(UUID Id, float D, bool Chat, bool Draw, bool Region)> Now()
    {
        var list = new List<(UUID, float, bool, bool, bool)>();
        if (_s.Client is not { Network.CurrentSim: { } cur } c) return list;
        var me = c.Self.SimPosition;
        float draw = _s.Camera.FarClip;
        var seen = new HashSet<UUID> { c.Self.AgentID };
        void Add(UUID id, Vector3 pos, bool here)
        {
            if (!seen.Add(id)) return;
            float d = Vector3.Distance(pos, me);
            list.Add((id, d, d <= ChatRange, d <= draw, here));
        }
        foreach (var rw in _s.VisibleRegions())
        {
            var off = _s.FrameOffset(rw);
            bool here = rw.Handle == cur.Handle;
            foreach (var av in rw.Avatars.Values.ToArray()) Add(av.ID, Pos(rw, av) + off, here);
            foreach (var (id, pos) in rw.Coarse) Add(id, pos + off, here);
        }
        return list;
    }

    static Vector3 Pos(Sojourn.Core.World.RegionWorld w, Avatar av)
    {
        if (av.ParentID == 0) return av.Position;
        return w.Prims.TryGetValue(av.ParentID, out var seat) ? w.RegionPosition(seat) + av.Position * w.RegionRotation(seat) : av.Position;
    }

    public void Tick()
    {
        if (_s.Client is not { Network.CurrentSim: { } cur }) { _was.Clear(); _region = 0; return; }
        if (!AnyOn) { _was.Clear(); return; }
        var now = Now();
        // an arrival: what's there is taken as it is, and kept so for a few seconds while the region fills in
        bool arriving = cur.Handle != _region || DateTime.UtcNow - _arrived < TimeSpan.FromSeconds(Settle);
        if (cur.Handle != _region) { _region = cur.Handle; _arrived = DateTime.UtcNow; }
        if (_was.Count == 0 && !arriving) arriving = true;   // just turned on
        var next = now.ToDictionary(n => n.Id, n => (n.Chat, n.Draw, n.Region, n.D));
        if (!arriving)
        {
            foreach (var (id, n) in next)
            {
                var w = _was.TryGetValue(id, out var had) ? had : (Chat: false, Draw: false, Region: false, D: 0f);
                if (n.Chat && !w.Chat && SocialSettings.Enters(_opt.RadarChat)) Report(id, "chat", true, n.D, $"came into chat range ({n.D:0} m)");
                if (n.Draw && !w.Draw && SocialSettings.Enters(_opt.RadarDraw)) Report(id, "draw", true, n.D, $"came within draw distance ({n.D:0} m)");
                if (n.Region && !w.Region && SocialSettings.Enters(_opt.RadarRegion)) Report(id, "region", true, n.D, "arrived in the region");
                if (n.Draw && !w.Draw && _opt.RadarAgeDays > 0) _ = AgeAsync(id);
            }
            foreach (var (id, w) in _was)
            {
                var n = next.TryGetValue(id, out var have) ? have : (Chat: false, Draw: false, Region: false, D: 0f);
                if (w.Chat && !n.Chat && SocialSettings.Leaves(_opt.RadarChat)) Report(id, "chat", false, n.D, "left chat range");
                if (w.Draw && !n.Draw && SocialSettings.Leaves(_opt.RadarDraw)) Report(id, "draw", false, n.D, "left draw distance");
                // (gone from the region by crossing out of it, or logging off: the viewer can't tell either)
                if (w.Region && !n.Region && SocialSettings.Leaves(_opt.RadarRegion)) Report(id, "region", false, n.D, "left the region");
            }
        }
        _was.Clear();
        foreach (var (id, n) in next) _was[id] = n;
    }

    void Report(UUID id, string range, bool entered, float d, string what)
    {
        _reports++;
        var name = Name(id);
        if (name == null) _ = _s.Client is { } c ? _s.Names.ResolveAsync(c, new[] { id }, TimeSpan.FromSeconds(4)) : Task.CompletedTask;
        _s.Notify.Add("RadarReport", "notifytip", $"{name ?? "Someone"} {what}.", Notifications.None,
            payload: new JsonObject
            {
                ["from_id"] = id.ToString(), ["from_name"] = name ?? "", ["range"] = range, ["entered"] = entered,
                ["distance"] = Math.Round(d, 1), ["what"] = what,
            },
            substitutions: new JsonObject { ["NAME"] = name ?? "Someone" });
    }

    string? Name(UUID id) => _s.People.Pseudonym(id) ?? _s.Names.DisplayName(id) ?? _s.Names.Legacy(id);

    /// <summary>The age alert: their profile's born date, asked once a session per person.</summary>
    async Task AgeAsync(UUID id)
    {
        if (!_aged.Add(id)) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var p = await _s.Profiles.ProfileAsync(new JsonObject { ["id"] = id.ToString() }, cts.Token);
            if (!DateTime.TryParseExact((string?)p["born"] ?? "", new[] { "M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd" }, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var born)) return;
            int days = (int)(DateTime.UtcNow.Date - born.Date).TotalDays;
            if (days > _opt.RadarAgeDays) return;
            _reports++;
            var name = Name(id);
            var what = days == 0 ? "is a new account, made today" : $"is a new account, {days} day{(days == 1 ? "" : "s")} old";
            _s.Notify.Add("RadarReport", "notifytip", $"{name ?? "Someone"} {what}.", Notifications.None,
                payload: new JsonObject { ["from_id"] = id.ToString(), ["from_name"] = name ?? "", ["range"] = "age", ["days"] = days, ["what"] = what },
                substitutions: new JsonObject { ["NAME"] = name ?? "Someone" });
        }
        catch { /* no profile service, or no answer: nothing to tell */ }
    }
}
