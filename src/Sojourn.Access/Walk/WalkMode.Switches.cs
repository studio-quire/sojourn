using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.World;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Access.Walk;

/// <summary>
/// Railway switches (docs/access-app.md > Walk mode > Rail switches): seated on something moving, a switch coming up
/// along the way ahead is said once ("Switch in 40 metres, set to the main line."), W says the next one, Shift+W throws
/// it and says the new setting once it has changed; //switch does the same from routes and notecards.
///
/// The model is the Second Life Railroad's switch (the SL wiki's SLRR standards and SLRR Switch pages, 2010-2012, which
/// the Virtual Rail Consortium's tools follow): a linkset whose root is named "Switch Stand", its description
/// "Identifier~Status~Heading Name~Mainline Name~Branch Name~channel" with Status "Main" or "Branch", a large invisible
/// part over the stand taking touches, and invisible guide rails the trains steer by: the one in use named "Guide" and
/// solid, the other renamed "Alternate Guide" and phantom. So the guides' names swap with the setting and can't say which
/// way it's set: the stand's description does. Switches of other makes (a root named "Alternate Guide" or "AlternateGuide"
/// with no stand near) are announced with the setting unknown. Not yet checked against a real switch in Second Life.
/// Written by Claude Opus 5.5, 2026-11-01.
/// </summary>
public sealed partial class WalkMode
{
    /// <summary>A switch around: its stand (or the alternate guide standing for it), where it is in the person's region's
    /// frame, and what its stand's description says.</summary>
    public sealed record RailSwitch(UUID Id, uint LocalId, ulong Region, Vector3 Pos, bool Stand, SwitchStand.Setting? Setting);

    const float SwitchAhead = 60f;        // said within this far ahead
    const float SwitchLook = 90f;         // names asked for this far ahead, so they're known by then
    const float SwitchAsk = 60f;          // W and //switch: the nearest within this far when none is ahead

    List<RailSwitch> _switches = new();
    long _switchScanAt, _switchSaidAt, _switchWatchAt, _switchMovedAt = -1_000_000;
    readonly Dictionary<UUID, long> _switchAhead = new();       // when each was last in the way ahead
    readonly HashSet<UUID> _switchPending = new();              // come into the way ahead, not said yet
    readonly Dictionary<UUID, string> _switchToldAs = new();    // the setting last said for each
    bool _switchBusy;

    /// <summary>The switches the last look found, nearest first (the status op, tests).</summary>
    public IReadOnlyList<RailSwitch> Switches => _switches;

    /// <summary>Each tick: seated, a look around for switches (every second while moving, every three for two minutes after), one
    /// said as it comes into the way ahead, and the next one ahead read again now and then in case it changed.</summary>
    void SwitchTick(Body b, long now)
    {
        if (!b.Sitting || _set.WalkMode("switch") == "off") { _switchPending.Clear(); return; }
        float speed = Speed(b);
        if (speed > 1f) _switchMovedAt = now;
        // only on something that moves (a seat at a club never looks), and for two minutes after it stops (a station)
        if (now - _switchMovedAt > 120_000 || now - _switchScanAt < (speed > 1f ? 1000 : 3000)) return;
        _switchScanAt = now;
        float yaw = speed > 0.5f ? MathF.Atan2(b.Vel.Y, b.Vel.X) : b.Yaw;
        ScanSwitches(b, yaw);

        // what's in the way ahead, kept while stopped too (a stop before a switch to throw it doesn't make it new again)
        RailSwitch? next = null;
        float nextAlong = float.MaxValue;
        foreach (var s in _switches)
        {
            if (!InWayAhead(b, yaw, s.Pos, SwitchAhead, out float along)) continue;
            bool fresh = !_switchAhead.TryGetValue(s.Id, out var last) || now - last > 60_000;
            _switchAhead[s.Id] = now;
            if (fresh) _switchPending.Add(s.Id);
            if (along < nextAlong) { next = s; nextAlong = along; }
        }
        _switchPending.RemoveWhere(id => !_switchAhead.TryGetValue(id, out var at) || at != now);
        if (_switchAhead.Count > 200)
            foreach (var id in _switchAhead.Where(x => now - x.Value > 600_000).Select(x => x.Key).ToList()) { _switchAhead.Remove(id); _switchToldAs.Remove(id); }
        if (_switchBusy || speed < 1f) return;

        // the nearest not yet said, read fresh first (one at a time, nearest first, a few seconds apart)
        var say = _switches.Where(s => _switchPending.Contains(s.Id)).Select(s => (s, d: Along(b, yaw, s.Pos))).OrderBy(x => x.d).FirstOrDefault();
        if (say.s != null && now - _switchSaidAt > 2500)
        {
            _switchPending.Remove(say.s.Id);
            _ = AnnounceAsync(say.s, b, yaw);
            return;
        }
        // the next one ahead, already said: read again every few seconds, said when it changed (another train sprang it,
        // or it went back to the main line by itself)
        if (next is { Stand: true } n && _switchToldAs.ContainsKey(n.Id) && now - _switchWatchAt > 4000)
        {
            _switchWatchAt = now;
            _ = WatchAsync(n);
        }
    }

    async Task AnnounceAsync(RailSwitch s, Body b, float yaw)
    {
        _switchBusy = true;
        try
        {
            var fresh = await ReadSwitchAsync(s);
            if (Me() is not { } now) return;
            float along = Along(now, yaw, fresh.Pos);
            if (along <= 0) return;
            _switchSaidAt = Environment.TickCount64;
            _switchToldAs[s.Id] = fresh.Setting?.Status ?? "";
            Cue("switch", CueSounds.Cue.Switch, $"Switch in {SwitchMetres(along)}, {SettingWords(fresh)}.", now, MathF.Atan2(fresh.Pos.Y - now.Pos.Y, fresh.Pos.X - now.Pos.X));
        }
        finally { _switchBusy = false; }
    }

    async Task WatchAsync(RailSwitch s)
    {
        _switchBusy = true;
        try
        {
            var fresh = await ReadSwitchAsync(s);
            var status = fresh.Setting?.Status ?? "";
            if (!_switchToldAs.TryGetValue(s.Id, out var was) || was == status || status == "") return;
            _switchToldAs[s.Id] = status;
            if (Me() is not { } b) return;
            _switchSaidAt = Environment.TickCount64;
            Cue("switch", CueSounds.Cue.Switch, $"The switch ahead is now {SettingWords(fresh)}.", b, MathF.Atan2(fresh.Pos.Y - b.Pos.Y, fresh.Pos.X - b.Pos.X));
        }
        finally { _switchBusy = false; }
    }

    // --- finding them ------------------------------------------------------------------------------------------------

    /// <summary>Roots in the regions in view, near enough: switch stands and lone alternate guides among those named,
    /// and the names of the nearest unnamed ones ahead asked for (twenty a look), so they're known by the time they're
    /// near. Region-wide scans only once a second at most (a train ride's garbage, WalkMode.SeatLinkset).</summary>
    void ScanSwitches(Body b, float yaw)
    {
        var stands = new List<RailSwitch>();
        var alternates = new List<RailSwitch>();
        var unnamed = new List<(RegionWorld W, Primitive P, float D)>();
        var dir = new Vector2(MathF.Cos(yaw), MathF.Sin(yaw));
        foreach (var w in S.VisibleRegions())
        {
            var off = S.FrameOffset(w);
            foreach (var (_, p) in w.Prims)
            {
                if (p.ParentID != 0 || p.PrimData.PCode != PCode.Prim || p.IsAttachment) continue;
                var pos = new Vector3(p.Position.X + off.X, p.Position.Y + off.Y, p.Position.Z);
                var rel = new Vector2(pos.X - b.Pos.X, pos.Y - b.Pos.Y);
                float d = rel.Length();
                if (d > SwitchLook || MathF.Abs(pos.Z - b.Pos.Z) > 30) continue;
                if (p.Properties is not { } props || props.ObjectID != p.ID)
                {
                    if (Vector2.Dot(rel, dir) > -10 || d < SwitchAsk) unnamed.Add((w, p, d));
                    continue;
                }
                var name = props.Name?.Trim() ?? "";
                if (SwitchStand.IsStand(name)) stands.Add(new RailSwitch(p.ID, p.LocalID, w.Handle, pos, true, SwitchStand.Parse(props.Description)));
                else if (SwitchStand.IsAlternateGuide(name)) alternates.Add(new RailSwitch(p.ID, p.LocalID, w.Handle, pos, false, null));
            }
        }
        // a lone alternate guide stands for a switch without a stand (one for those within 25 m of each other)
        var found = new List<RailSwitch>(stands);
        foreach (var a in alternates.OrderBy(a => Vector3.DistanceSquared(a.Pos, b.Pos)))
            if (!found.Any(f => Vector2.Distance(new(f.Pos.X, f.Pos.Y), new(a.Pos.X, a.Pos.Y)) < 25)) found.Add(a);
        _switches = found.OrderBy(s => Vector3.DistanceSquared(s.Pos, b.Pos)).ToList();
        foreach (var g in unnamed.OrderBy(x => x.D).Take(20).GroupBy(x => x.W))
            _ = S.Objects.FetchAsync(g.Key, g.Select(x => x.P).ToList(), TimeSpan.Zero, CancellationToken.None, sim: g.Key.Simulator);
    }

    static float Along(Body b, float yaw, Vector3 pos) => Vector2.Dot(new(pos.X - b.Pos.X, pos.Y - b.Pos.Y), new(MathF.Cos(yaw), MathF.Sin(yaw)));

    /// <summary>In the way ahead: up to <paramref name="range"/> along the way the person goes, within a widening band
    /// either side (6 m, and a third of the way out: a curve's switch is a little off the straight line).</summary>
    static bool InWayAhead(Body b, float yaw, Vector3 pos, float range, out float along)
    {
        var rel = new Vector2(pos.X - b.Pos.X, pos.Y - b.Pos.Y);
        var dir = new Vector2(MathF.Cos(yaw), MathF.Sin(yaw));
        along = Vector2.Dot(rel, dir);
        float side = MathF.Abs(rel.X * dir.Y - rel.Y * dir.X);
        return along > 1 && along <= range && side <= 6 + along / 3 && MathF.Abs(pos.Z - b.Pos.Z) < 20;
    }

    /// <summary>The stand's description asked again (the region sends a changed one only to whoever has it selected).</summary>
    async Task<RailSwitch> ReadSwitchAsync(RailSwitch s)
    {
        if (!s.Stand || RegionOf(s) is not { } w || w.Prims.TryGetValue(s.LocalId, out var p) is false) return s;
        try { await S.Objects.RenewAsync(w, new[] { p }, TimeSpan.FromSeconds(2), CancellationToken.None, w.Simulator); }
        catch (Exception) { return s; }
        var fresh = s with { Setting = SwitchStand.Parse(p.Properties?.Description) };
        int i = _switches.FindIndex(x => x.Id == s.Id);
        if (i >= 0) _switches[i] = fresh;
        return fresh;
    }

    RegionWorld? RegionOf(RailSwitch s) => S.VisibleRegions().FirstOrDefault(w => w.Handle == s.Region);

    static string SwitchMetres(float m) => m < 20 ? Bearings.Metres(m) : Bearings.Metres(Math.Round(m / 5) * 5);

    static string SettingWords(RailSwitch s) => s.Setting?.Status switch
    {
        "Main" => "set to the main line" + (s.Setting!.Mainline is { } to ? $", {to}" : ""),
        "Branch" => "set to the branch" + (s.Setting!.Branch is { } to ? $", {to}" : ""),
        _ => "set to: unknown",
    };

    // --- asking and throwing ------------------------------------------------------------------------------------------

    /// <summary>The switch W and Shift+W are about: the nearest in the way ahead (within 90 m), else the nearest within
    /// 60 m any way.</summary>
    async Task<RailSwitch?> TheSwitchAsync(Body b)
    {
        float yaw = b.Sitting ? (Speed(b) > 0.5f ? MathF.Atan2(b.Vel.Y, b.Vel.X) : b.Yaw) : Facing(b);
        ScanSwitches(b, yaw);
        if (_switches.Count == 0)
        {
            // names asked just now: a moment for them to come
            await Task.Delay(1200);
            if (Me() is { } again) { b = again; ScanSwitches(b, yaw); }
        }
        var ahead = _switches.Select(s => (s, ok: InWayAhead(b, yaw, s.Pos, SwitchLook, out var along), along))
            .Where(x => x.ok).OrderBy(x => x.along).Select(x => x.s).FirstOrDefault();
        return ahead ?? _switches.FirstOrDefault(s => Vector2.Distance(new(s.Pos.X, s.Pos.Y), new(b.Pos.X, b.Pos.Y)) <= SwitchAsk);
    }

    string WhereWords(Body b, RailSwitch s)
    {
        float yaw = b.Sitting ? (Speed(b) > 0.5f ? MathF.Atan2(b.Vel.Y, b.Vel.X) : b.Yaw) : Facing(b);
        if (InWayAhead(b, yaw, s.Pos, SwitchLook, out var along)) return $"{SwitchMetres(along)} ahead";
        var rel = Bearings.Norm(Bearings.Compass(b.Pos.X, b.Pos.Y, s.Pos.X, s.Pos.Y) - Compass(Facing(b)));
        float d = Vector2.Distance(new(s.Pos.X, s.Pos.Y), new(b.Pos.X, b.Pos.Y));
        return rel is > 150 and < 210 ? $"{SwitchMetres(d)} behind you" : $"{SwitchMetres(d)}, {Bearings.Clock(rel)}";
    }

    /// <summary>W: the next switch and how it's set, both ways' names where its stand has them.</summary>
    public async Task SwitchLineAsync()
    {
        if (Me() is not { } b) { Line("Not in a region yet."); return; }
        if (await TheSwitchAsync(b) is not { } s) { Line($"No switch within {Bearings.Metres(SwitchAsk)}."); return; }
        s = await ReadSwitchAsync(s);
        var line = $"Switch {WhereWords(Me() ?? b, s)}, {SettingWords(s)}.";
        if (s.Setting is { Mainline: var m, Branch: var br } && (m != null || br != null))
            line += $" Main line: {m ?? "not named"}; branch: {br ?? "not named"}.";
        if (!s.Stand) line += " No switch stand found to read or throw it.";
        Told(s);
        Line(line);
    }

    /// <summary>Shift+W, //switch flip, //switch main or branch: throws the switch W is about by touching its stand, as a
    /// person clicks it, then reads the stand until its setting changes (six seconds at most) and says the new one. To a
    /// setting it's already at, only says so.</summary>
    public async Task ThrowSwitchAsync(string? to = null)
    {
        if (_switchBusy) { Line("Still reading a switch: a moment."); return; }
        if (Me() is not { } b) { Line("Not in a region yet."); return; }
        if (await TheSwitchAsync(b) is not { } s) { Line($"No switch within {Bearings.Metres(SwitchAsk)} to throw."); return; }
        if (!s.Stand) { Line("That switch has no switch stand to throw it by."); return; }
        if (S.Region is not { } here || here.Handle != s.Region) { Line($"That switch is across the border, {WhereWords(b, s)}: throw it once you're in its region."); return; }
        _switchBusy = true;
        try
        {
            s = await ReadSwitchAsync(s);
            var was = s.Setting?.Status;
            if (to != null && was == to) { Line($"The switch is already {SettingWords(s)}."); return; }
            var guides = GuidesNear(here, s.Pos);
            var part = TouchPartOf(here, s.LocalId);
            if (part == null) { Line("The switch isn't here any more."); return; }
            var r = await _ops.DoAsync("LLAgent", "requestTouch", new JsonObject { ["obj_uuid"] = part.ID.ToString() });
            if (PersonOps.Error(r) is { } e) { Line($"Couldn't throw the switch: {e}"); return; }
            var end = Environment.TickCount64 + 6000;
            RailSwitch now = s;
            bool guidesMoved = false;
            while (Environment.TickCount64 < end)
            {
                await Task.Delay(600);
                now = await ReadSwitchAsync(s);
                guidesMoved = guides.Any(g => here.Prims.TryGetValue(g.Key, out var gp) && Phantom(gp) != g.Value);
                if (now.Setting?.Status is { } st && st != was) break;
                if (was == null && guidesMoved) break;
            }
            var status = now.Setting?.Status;
            Told(now);
            var pos = Me() is { } me ? me : b;
            float yaw = MathF.Atan2(s.Pos.Y - pos.Pos.Y, s.Pos.X - pos.Pos.X);
            if (status != null && status != was)
            {
                // the Pawpaw Wye report (SL feedback, 2025-12): a stand that changed while its guides didn't
                if (guides.Count >= 2 && !guidesMoved)
                {
                    await Task.Delay(1500);
                    guidesMoved = guides.Any(g => here.Prims.TryGetValue(g.Key, out var gp) && Phantom(gp) != g.Value);
                }
                var line = $"Switch {SettingWords(now)}.";
                if (to != null && status != to) line += $" Not the {(to == "Main" ? "main line" : "branch")}, though.";
                if (guides.Count >= 2 && !guidesMoved) line += " Its guide rails didn't change, though: the stand may be out of order.";
                Cue("switch", CueSounds.Cue.SwitchThrown, line, pos, yaw);
            }
            else if (was == null && guidesMoved) Cue("switch", CueSounds.Cue.SwitchThrown, "Switch thrown: its guide rails changed, but its stand doesn't say which way.", pos, yaw);
            else Line(was == null ? "Touched the switch stand; no change seen." : $"The switch didn't change: still {SettingWords(now)}.");
        }
        finally { _switchBusy = false; }
    }

    /// <summary>//switch [flip|main|branch].</summary>
    public Task SwitchWordsAsync(string words)
    {
        var w = words.Trim().ToLowerInvariant();
        return w switch
        {
            "" or "next" or "state" => SwitchLineAsync(),
            "flip" or "throw" or "toggle" => ThrowSwitchAsync(),
            "main" or "main line" or "mainline" => ThrowSwitchAsync("Main"),
            "branch" => ThrowSwitchAsync("Branch"),
            _ => Say("//switch says the next switch; //switch flip throws it; //switch main or //switch branch sets it."),
        };

        Task Say(string text) { Line(text); return Task.CompletedTask; }
    }

    /// <summary>Said on asking or throwing: not said again as it comes up ahead.</summary>
    void Told(RailSwitch s)
    {
        _switchToldAs[s.Id] = s.Setting?.Status ?? "";
        _switchAhead[s.Id] = Environment.TickCount64;
        _switchPending.Remove(s.Id);
    }

    bool Phantom(Primitive p) => (S.State.PrimFlagsOf(p) & PrimFlags.Phantom) != 0;

    /// <summary>The guide rails near a stand (roots named Guide or Alternate Guide within 15 m) and whether each is phantom.</summary>
    Dictionary<uint, bool> GuidesNear(RegionWorld w, Vector3 at)
    {
        var o = new Dictionary<uint, bool>();
        foreach (var (id, p) in w.Prims)
            if (p.ParentID == 0 && p.Properties?.Name?.Trim() is { } n && (SwitchStand.IsGuide(n) || SwitchStand.IsAlternateGuide(n))
                && Vector3.Distance(new(p.Position.X, p.Position.Y, p.Position.Z), at) < 15)
                o[id] = Phantom(p);
        return o;
    }

    /// <summary>What a click on the switch goes to: its part that takes touches (the standard's large invisible prim over
    /// the stand; the largest, if several do), else the stand itself.</summary>
    Primitive? TouchPartOf(RegionWorld w, uint root)
    {
        if (!w.Prims.TryGetValue(root, out var r)) return null;
        Primitive? best = null;
        float size = 0;
        foreach (var (_, p) in w.Prims)
        {
            if ((p.LocalID != root && p.ParentID != root) || (S.State.PrimFlagsOf(p) & PrimFlags.Touch) == 0) continue;
            float v = p.Scale.X * p.Scale.Y * p.Scale.Z;
            if (best == null || v > size) { best = p; size = v; }
        }
        return best ?? r;
    }
}

/// <summary>An SLRR switch stand's name and description (the SL wiki's "Second Life Railroad/SLRR Switch"): the root
/// named "Switch Stand", its description "Identifier~Status~Heading Name~Mainline Name~Branch Name~messaging channel",
/// Status "Main" or "Branch".</summary>
public static class SwitchStand
{
    public sealed record Setting(string? Status, string? Ident, string? Mainline, string? Branch, int? Channel);

    public static bool IsStand(string name) =>
        name.Contains("switch stand", StringComparison.OrdinalIgnoreCase) || name.Contains("switchstand", StringComparison.OrdinalIgnoreCase);

    public static bool IsGuide(string name) => name.Equals("Guide", StringComparison.OrdinalIgnoreCase);

    public static bool IsAlternateGuide(string name) =>
        name.Equals("Alternate Guide", StringComparison.OrdinalIgnoreCase) || name.Equals("AlternateGuide", StringComparison.OrdinalIgnoreCase);

    /// <summary>The description's fields; Status null unless it says Main or Branch.</summary>
    public static Setting? Parse(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return null;
        var f = desc.Split('~').Select(x => x.Trim()).ToArray();
        string? At(int i) => i < f.Length && f[i].Length > 0 ? f[i] : null;
        var status = At(1)?.ToLowerInvariant() switch { "main" or "mainline" => "Main", "branch" => "Branch", _ => null };
        if (status == null && f.Length < 2) return null;
        int? channel = int.TryParse(At(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ch) ? ch : null;
        return new Setting(status, At(0), At(3), At(4), channel);
    }
}
