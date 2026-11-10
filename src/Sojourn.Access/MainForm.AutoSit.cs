using System.Text.Json;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Session.Control;

namespace Sojourn.Access;

/// <summary>
/// Auto-sit (Radegast's AutoSit, with Radegast issue #156's asks: a seat in each of several places, and a key to turn it
/// on and off): her seat for a region (a chair at home, a stool at the club), sat on again when she arrives there by
/// logging in (or the session logging in again after a drop) or by a teleport from another region, or teleports home.
/// Not when she walks or drives in, nor on a teleport within the region (she meant to go where it took her). Once per
/// arrival: it waits up to a minute and a half for the seat to appear (a big region's objects come slowly), and gives
/// up if she moves off or sits on something first; each outcome is said. Radegast sits her again every ten seconds for
/// as long as it's on, so standing up needs it off; here she stands up when she likes. Off until she sets a seat or
/// turns it on (Control+Shift+S, World > Auto-sit, Alt+0's My avatar, //autosit). One seat a region; kept per account in
/// its state dir, since a seat is an object on one grid. The seat is the prim she sits on; one taken and put out again
/// is found by its name within 3 m of where it was. Radegast's PseudoHome and LSL Helper aren't here
/// (docs/access-gaps.md). Claude Opus 5.5, 2026-11-09.
/// </summary>
public sealed partial class MainForm
{
    sealed class AutoSitState
    {
        public bool On { get; set; }
        public List<Seat> Seats { get; set; } = new();
    }

    sealed record Seat(string Id, string Name, string Region, double X, double Y, double Z);

    AutoSitState _autoSit = new();
    string _autoSitPath = "";
    int _autoSitGen;
    long _autoSitArmedAt;
    bool _autoSitKeyTold;
    ulong _teleportFrom;
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<GridClient, object> _teleportsHooked = new();

    const string AutoSitKey = "Control+Shift+S";

    void StartAutoSit()
    {
        _autoSitPath = Path.Combine(Host!.Session.StateDir, "autosit.json");
        try { if (File.Exists(_autoSitPath)) _autoSit = JsonSerializer.Deserialize<AutoSitState>(File.ReadAllText(_autoSitPath)) ?? new(); }
        catch (Exception) { _autoSit = new(); }
        lock (AgentSession.ClientHooks) AgentSession.ClientHooks.Add(HookTeleports);
        if (Host.Session.Client is { } c) HookTeleports(c);
        AddAutoSitSettings(Host.SettingsApi);
        _cmd.AutoSit = words => (Task)Invoke(() => AutoSitWordsAsync(words));
    }

    void SaveAutoSit()
    {
        try { File.WriteAllText(_autoSitPath, JsonSerializer.Serialize(_autoSit, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception) { /* a setting not kept isn't worth stopping for */ }
    }

    /// <summary>Where a teleport set out from (its start, or its first word of progress), so its end can tell another
    /// region from this one.</summary>
    void HookTeleports(GridClient c)
    {
        lock (_teleportsHooked)
        {
            if (_teleportsHooked.TryGetValue(c, out _)) return;
            _teleportsHooked.Add(c, new object());
        }
        c.Self.TeleportProgress += (_, e) =>
        {
            switch (e.Status)
            {
                case TeleportStatus.Start or TeleportStatus.Progress:
                    if (_teleportFrom == 0) _teleportFrom = c.Network.CurrentSim?.Handle ?? 0;
                    break;
                case TeleportStatus.Finished:
                    var from = _teleportFrom;
                    _teleportFrom = 0;
                    bool home = (e.Flags & TeleportFlags.ViaHome) != 0;
                    BeginInvoke(() => AutoSitArrived(home ? "home" : "teleport", from));
                    break;
                default:
                    _teleportFrom = 0;
                    break;
            }
        };
    }

    /// <summary>A seat that's this object, or a part of it.</summary>
    Seat? SeatFor(UUID id)
    {
        if (Host?.Session is not { } s || s.Region is not { } w || w.PrimById(id) is not { } p) return _autoSit.Seats.FirstOrDefault(x => x.Id == id.ToString());
        var set = s.Objects.Linkset(w, s.Objects.RootOf(w, p)).Select(x => x.ID.ToString()).ToHashSet();
        return _autoSit.Seats.FirstOrDefault(x => set.Contains(x.Id));
    }

    Seat? SeatIn(string region) => _autoSit.Seats.FirstOrDefault(s => s.Region.Equals(region, StringComparison.OrdinalIgnoreCase));

    /// <summary>Arrived somewhere: "login" (the first, or logging in again), "teleport" (from region handle from), "home".</summary>
    void AutoSitArrived(string how, ulong from)
    {
        if (!_autoSit.On || _autoSit.Seats.Count == 0) return;
        // (Control+Shift+H's own word on a home teleport the grid sent no teleport event for, after one it did)
        if (how == "home" && from == 0 && Environment.TickCount64 - _autoSitArmedAt < 5000) return;
        _autoSitArmedAt = Environment.TickCount64;
        int gen = ++_autoSitGen;
        _ = GuardedAsync(() => AutoSitAsync(how, from, gen), "auto-sit");
    }

    async Task AutoSitAsync(string how, ulong from, int gen)
    {
        await Task.Delay(1000);
        if (gen != _autoSitGen || _ops.Client?.Network.CurrentSim is not { } sim) return;
        if (how == "teleport" && (from == 0 || sim.Handle == from)) return;
        if (SeatIn(sim.Name) is not { } seat) return;
        Vector3? arrived = null;
        long until = Environment.TickCount64 + 90_000;
        while (gen == _autoSitGen && _autoSit.On && Environment.TickCount64 < until)
        {
            if (_ops.Client is not { Network.Connected: true } c || c.Network.CurrentSim?.Handle != sim.Handle) return;
            if (c.Self.SittingOn != 0) return;
            if (_ops.Me() is { } me)
            {
                arrived ??= me.Pos;
                if (Across(me.Pos, arrived.Value) > 4) { Announce($"Auto-sit: you've moved off, so you're not sat on {seat.Name}."); return; }
            }
            if (await FindSeatAsync(seat) is { } id)
            {
                var r = await _ops.SetUpAsync("LLAgent", "requestSit", new JsonObject { ["obj_uuid"] = id.ToString() });
                if (gen != _autoSitGen) return;
                if (PersonOps.Error(r) is { } e) { Announce($"Auto-sit: couldn't sit on {seat.Name}: {e}."); return; }
                Announce($"Auto-sit: sitting on {seat.Name}.{(_autoSitKeyTold ? "" : $" {AutoSitKey} turns auto-sit off.")}");
                _autoSitKeyTold = true;
                return;
            }
            await Task.Delay(2000);
        }
        if (gen == _autoSitGen && _autoSit.On)
            Announce($"Auto-sit: {seat.Name} hasn't appeared here in a minute and a half, so you're not sat on it. //autosit now tries again.");
    }

    static double Across(Vector3 a, Vector3 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>The seat's prim in this region: by its key, else (taken and put out again) the same name within 3 m of
    /// where it was.</summary>
    async Task<UUID?> FindSeatAsync(Seat seat)
    {
        if (!UUID.TryParse(seat.Id, out var id) || Host?.Session.Region is not { } w) return null;
        if (w.PrimById(id) != null) return id;
        var at = new Vector3((float)seat.X, (float)seat.Y, (float)seat.Z);
        double reach = _ops.Me() is { } me ? Vector3.Distance(me.Pos, at) + 5 : 96;
        var near = await _ops.ObjectsAsync(Math.Clamp(reach, 10, 256), 20, seat.Name);
        return near.Where(t => t.Name.Equals(seat.Name, StringComparison.OrdinalIgnoreCase) && Vector3.Distance(t.Pos, at) < 3)
            .OrderBy(t => Vector3.Distance(t.Pos, at)).Select(t => (UUID?)t.Id).FirstOrDefault();
    }

    // --- setting it up: her own keys and words ----------------------------------------------------------------------

    /// <summary>A thing's name as a seat: a part's own, unless it's only "Object", then its object's.</summary>
    string SeatName(Primitive p)
    {
        var own = p.Properties?.Name;
        if (own is { Length: > 0 } && own is not ("Object" or "Primitive")) return own;
        if (Host?.Session is { } s && s.Region is { } w && s.Objects.RootOf(w, p).Properties?.Name is { Length: > 0 } root) return root;
        return own is { Length: > 0 } ? own : "the seat";
    }

    /// <summary>This region's seat: the thing (a prim) given. A first seat turns auto-sit on, said; after that the switch
    /// stays as she left it.</summary>
    void SetSeat(UUID id, string? name = null)
    {
        if (Host?.Session.Region is not { } w || w.PrimById(id) is not { } p || _ops.Me() is not { } me) { Answer($"{name ?? "That"} isn't here any more."); return; }
        name ??= SeatName(p);
        var pos = w.RegionPosition(p);
        var was = SeatIn(me.Region);
        bool first = _autoSit.Seats.Count == 0 && !_autoSit.On;
        _autoSit.Seats = _autoSit.Seats.Where(s => s != was).Append(new Seat(id.ToString(), name, me.Region, pos.X, pos.Y, pos.Z)).ToList();
        if (first) _autoSit.On = true;
        SaveAutoSit();
        RefreshSettingsIfShown();
        var instead = was != null && was.Id != id.ToString() ? $", in place of {was.Name}" : "";
        Answer($"Auto-sit on {name}{instead}: when you log in or teleport into {me.Region}, you're sat on it."
               + (first ? $" Auto-sit is on now; {AutoSitKey} turns it off." : _autoSit.On ? "" : $" Auto-sit is off: {AutoSitKey} turns it on."));
    }

    void RefreshSettingsIfShown()
    {
        if (_areas.SelectedTab == _settingsPage) _ = RefreshSettings();
    }

    /// <summary>What she's sitting on, as this region's seat.</summary>
    void SeatHere()
    {
        if (_ops.Client is not { } c || Host?.Session.Region is not { } w) { Answer("Not in a region yet."); return; }
        if (c.Self.SittingOn == 0 || !w.Prims.TryGetValue(c.Self.SittingOn, out var p))
        {
            Answer("You're not sitting on anything: sit on your seat first, or in Objects (Alt+7) its Applications key has Auto-sit on it.");
            return;
        }
        SetSeat(p.ID);
    }

    /// <summary>Control+Shift+S, World > Auto-sit > On, Alt+0's row, //autosit on and off.</summary>
    void SetAutoSitOn(bool on, bool say = true)
    {
        _autoSit.On = on;
        if (!on) _autoSitGen++;   // a wait for a seat to appear ends with it
        SaveAutoSit();
        RefreshSettingsIfShown();
        if (!say) return;
        if (!on) { Answer("Auto-sit off."); return; }
        Answer(_autoSit.Seats.Count == 0
            ? "Auto-sit on, with no seat yet: in Objects (Alt+7), a seat's Applications key has Auto-sit on it, or sitting on it, //autosit here."
            : $"Auto-sit on: {SeatsWords()}." + (_ops.Me() is { } me && SeatIn(me.Region) is { } here ? $" //autosit now sits you on {here.Name} now." : ""));
    }

    void ToggleAutoSit() => SetAutoSitOn(!_autoSit.On);

    string SeatsWords() => _autoSit.Seats.Count == 0 ? "no seats"
        : string.Join("; ", _autoSit.Seats.Select(s => $"{s.Name} in {s.Region}"));

    /// <summary>Alt+0's seats row as it reads.</summary>
    string AutoSitSeatsRow() => _autoSit.Seats.Count == 0
        ? "none (Enter: what you can do; an object's Applications key in Objects has Auto-sit on it)"
        : $"{SeatsWords()} (Enter: forget one, or set one)";

    void ForgetSeat(Seat s)
    {
        _autoSit.Seats = _autoSit.Seats.Where(x => x != s).ToList();
        SaveAutoSit();
        RefreshSettingsIfShown();
        Answer($"Forgot {s.Name} in {s.Region}: {(_autoSit.Seats.Count == 0 ? "no auto-sit seats now" : $"auto-sit's seats are {SeatsWords()}")}.");
    }

    /// <summary>Sits her on this region's seat now (//autosit now; Radegast's Sit button), as her own press.</summary>
    async Task SeatNowAsync()
    {
        if (_ops.Me() is not { } me) { Answer("Not in a region yet."); return; }
        if (SeatIn(me.Region) is not { } seat) { Answer($"No auto-sit seat in {me.Region}."); return; }
        if (await FindSeatAsync(seat) is not { } id) { Answer($"{seat.Name} isn't here: it may have been taken in, or not appeared yet."); return; }
        var r = await _ops.DoAsync("LLAgent", "requestSit", new JsonObject { ["obj_uuid"] = id.ToString() });
        Answer(PersonOps.Error(r) is { } e ? $"Couldn't sit on {seat.Name}: {e}" : $"Sitting on {seat.Name}.");
    }

    /// <summary>World > Auto-sit, and Enter on Alt+0's seats row: on or off, the seat here from what she's sitting on, sit on
    /// it now, and each seat to forget.</summary>
    void FillAutoSitMenu(ToolStripItemCollection items)
    {
        items.Clear();
        items.Add(new ToolStripMenuItem("&On", null, (_, _) => ToggleAutoSit()) { Checked = _autoSit.On, ShortcutKeyDisplayString = AutoSitKey });
        items.Add(new ToolStripMenuItem("Sit back on &what you're sitting on, here", null, (_, _) => SeatHere()) { ShortcutKeyDisplayString = "//autosit here" });
        if (_ops?.Me() is { } me && SeatIn(me.Region) is { } here)
            items.Add(new ToolStripMenuItem($"&Sit on {here.Name} now", null, (_, _) => _ = SeatNowAsync()) { ShortcutKeyDisplayString = "//autosit now" });
        foreach (var s in _autoSit.Seats)
        {
            var seat = s;
            items.Add(new ToolStripMenuItem($"&Forget {s.Name}, in {s.Region}", null, (_, _) => ForgetSeat(seat)));
        }
    }

    ContextMenuStrip? _autoSitRowMenu;

    /// <summary>Enter or Space on Alt+0's seats row: the same as World > Auto-sit, where the focus is.</summary>
    bool AutoSitSeatsKey(KeyEventArgs e)
    {
        if (e.KeyCode is not (Keys.Enter or Keys.Space) || e.Control || e.Alt) return false;
        e.Handled = e.SuppressKeyPress = true;
        _autoSitRowMenu ??= new ContextMenuStrip { Name = "autoSitMenu", AccessibleName = "Auto-sit" };
        FillAutoSitMenu(_autoSitRowMenu.Items);
        var at = ActiveControl ?? (Control)this;
        _autoSitRowMenu.Show(at, new Point(10, Math.Min(at.Height, 30)));
        Mnemonics.Fix(_autoSitRowMenu);
        return true;
    }

    /// <summary>`//autosit` and its words: how it stands; on, off; here (what she's sitting on); now; forget [NAME, here,
    /// all]; or a name, the nearest thing with it as this region's seat.</summary>
    async Task AutoSitWordsAsync(string words)
    {
        var w = words.Trim();
        var sp = w.IndexOf(' ');
        var verb = (sp < 0 ? w : w[..sp]).ToLowerInvariant();
        var rest = sp < 0 ? "" : w[(sp + 1)..].Trim();
        switch (verb)
        {
            case "":
                var here = _ops.Me() is { } me && SeatIn(me.Region) is { } s ? $" This region's is {s.Name}." : "";
                Answer(_autoSit.Seats.Count == 0
                    ? $"Auto-sit is {(_autoSit.On ? "on" : "off")}, with no seats. //autosit here, sitting on one, sets this region's."
                    : $"Auto-sit is {(_autoSit.On ? "on" : "off")}: {SeatsWords()}.{here} {AutoSitKey} turns it {(_autoSit.On ? "off" : "on")}.");
                return;
            case "on" or "off": SetAutoSitOn(verb == "on"); return;
            case "here" or "this": SeatHere(); return;
            case "now" or "sit": await SeatNowAsync(); return;
            case "forget" or "remove" or "clear" or "delete":
            {
                if (rest.Equals("all", StringComparison.OrdinalIgnoreCase) || verb == "clear" && rest.Length == 0)
                {
                    int n = _autoSit.Seats.Count;
                    _autoSit.Seats = new();
                    SaveAutoSit();
                    RefreshSettingsIfShown();
                    Answer(n == 0 ? "No auto-sit seats to forget." : $"Forgot all {n} auto-sit seats.");
                    return;
                }
                Seat? seat = rest.Length == 0 || rest.Equals("here", StringComparison.OrdinalIgnoreCase)
                    ? (_ops.Me() is { } m2 ? SeatIn(m2.Region) : null)
                    : _autoSit.Seats.FirstOrDefault(x => x.Name.Equals(rest, StringComparison.OrdinalIgnoreCase))
                      ?? _autoSit.Seats.FirstOrDefault(x => x.Region.Equals(rest, StringComparison.OrdinalIgnoreCase))
                      ?? _autoSit.Seats.FirstOrDefault(x => x.Name.Contains(rest, StringComparison.OrdinalIgnoreCase));
                if (seat == null) { Answer(rest.Length == 0 ? "No auto-sit seat in this region." : $"No auto-sit seat called {rest}: {SeatsWords()}."); return; }
                ForgetSeat(seat);
                return;
            }
            case "help": await _cmd.RunAsync("//help autosit"); return;
        }
        var t = (await _ops.ObjectsAsync(96, 5, w)).FirstOrDefault();
        if (t == null) { Answer($"Nothing called {w} nearby. //autosit help says the rest."); return; }
        SetSeat(t.Id, t.Name);
    }

    // --- Alt+0 and the agent API -------------------------------------------------------------------------------------

    void AddAutoSitSettings(SettingsApi api)
    {
        api.Add("access_autosit", new SettingsApi.Setting("access", "Sojourn Access: auto-sit (Radegast's AutoSit): arriving in a region "
            + "by logging in (or logging in again after a drop) or by a teleport from another region, or teleporting home, the person "
            + "is sat on their seat for that region if they have one: once, said, and not if they move off or sit first; never on "
            + "walking or driving in. Control+Shift+S turns it on and off", "on, off", () => _autoSit.On,
            SettingsApi.Switch("access_autosit", on => SetAutoSitOn(on, say: false))));
        api.Add("access_autosit_seats", new SettingsApi.Setting("access", "Sojourn Access: auto-sit's seats, one a region: "
            + "[{\"id\": THE PRIM'S KEY, \"name\": \"Comfy Chair\", \"region\": \"Bay City\", \"pos\": [x, y, z]}]; a seat taken and put out "
            + "again is found by its name within 3 m of pos. Setting replaces the list; [] forgets them all", "a list",
            () => new JsonArray(_autoSit.Seats.Select(s => (JsonNode)new JsonObject
            {
                ["id"] = s.Id, ["name"] = s.Name, ["region"] = s.Region, ["pos"] = new JsonArray(s.X, s.Y, s.Z),
            }).ToArray()),
            v =>
            {
                if (v is not JsonArray a) return "access_autosit_seats is a list of {id, name, region, pos}";
                var seats = new List<Seat>();
                foreach (var x in a)
                {
                    if (x is not JsonObject o || !UUID.TryParse(o.Str("id"), out var id) || id == UUID.Zero || o.Str("region") is not { Length: > 0 } region)
                        return "access_autosit_seats: each seat needs id (the prim's key) and region";
                    var pos = o["pos"] as JsonArray;
                    double P(int i) => pos is { Count: 3 } && pos[i] is JsonValue pv && pv.TryGetValue<double>(out var d) ? d : 0;
                    seats.RemoveAll(s => s.Region.Equals(region, StringComparison.OrdinalIgnoreCase));
                    seats.Add(new Seat(id.ToString(), o.Str("name") is { Length: > 0 } n ? n : "the seat", region, P(0), P(1), P(2)));
                }
                _autoSit.Seats = seats;
                SaveAutoSit();
                RefreshSettingsIfShown();
                return null;
            }));
    }
}
