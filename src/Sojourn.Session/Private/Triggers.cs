using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Session.Control;
using Sojourn.Core;
using Sojourn.Core.Agents;

namespace Sojourn.Session.Private;

/// <summary>Someone private mode comes on with (vr-settings.json).</summary>
public sealed class AlwaysPerson
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>A place (a parcel: its region's id and local id) private mode comes on at.</summary>
public sealed class AlwaysPlace
{
    public string Region { get; set; } = "";
    public int Parcel { get; set; }
    public string Name { get; set; } = "";
    public string RegionName { get; set; } = "";

    public bool Is(string region, int parcel) => Region == region && Parcel == parcel;
}

/// <summary>
/// Private mode coming on by itself (docs/assistant-incognito.md > In the person's hands): with someone the person keeps it with (within
/// chat range, a one-to-one IM with them in the last 10 minutes, or their IM open on the feed), at a place they keep it
/// at (the parcel), and, if they chose, while an IM is open on the feed. A spell this started ends by itself once none
/// of that holds any more; one the person or the assistant started stays until the person ends it. When the person
/// ends a spell while a trigger still holds, that trigger rests until it stops holding (leaving and coming back, the
/// person going and returning, ten quiet minutes), so it doesn't come straight back on.
/// Someone's words are checked before they're logged (PrivateMode.WordsFrom), so the line that brings it on is sealed;
/// nearness and the place are checked twice a second.
/// </summary>
public sealed class Triggers
{
    public const float ChatRange = 20;
    const double ImQuiet = 600;

    readonly AgentSession _s;
    readonly VrSettings _st;
    readonly Action _save;
    readonly object _lock = new();
    readonly Dictionary<UUID, double> _imWith = new();   // the last one-to-one IM line with them, either way
    HashSet<UUID> _near = new();
    List<(UUID Id, string Name, double Dist)> _around = new();
    HashSet<string> _resting = new();
    int _autoSpell = -1;
    int _switching;   // the thread switching it, so the person's own switch on another thread isn't taken for this

    /// <summary>Who the one-to-one IM open on the feed is with, and whether any IM conversation is open there (from the feed).</summary>
    public Func<UUID?>? OpenImWith { get; set; }
    public Func<bool>? AnyImOpen { get; set; }

    /// <summary>The lists, or who's around, changed: the menu redraws.</summary>
    public event Action? Changed;

    /// <summary>The spell under way is one this started (it ends by itself).</summary>
    public bool Automatic => _s.Private.On && _s.Private.Spell == _autoSpell;

    public Triggers(AgentSession s, VrSettings st, Action save)
    {
        _s = s;
        _st = st;
        _save = save;
        s.Private.WordsFrom = Heard;
        s.Private.Changed += (on, _, _) =>
        {
            if (on || _switching == Environment.CurrentManagedThreadId) return;
            // ended by the person (or anyone but this): what holds now rests until it stops holding
            lock (_lock) _resting = Active().Select(a => a.Key).ToHashSet();
        };
        new Thread(Watch) { IsBackground = true, Name = "private triggers", Priority = ThreadPriority.BelowNormal }.Start();
        new Thread(ReadIms) { IsBackground = true, Name = "private triggers' IMs", Priority = ThreadPriority.BelowNormal }.Start();
    }

    // --- the lists ------------------------------------------------------------------------------------------------

    public IReadOnlyList<AlwaysPerson> People => _st.PrivatePeople;
    public IReadOnlyList<AlwaysPlace> Places => _st.PrivatePlaces;

    /// <summary>Within chat range now, nearest first (for the menu: people to keep it with).</summary>
    public List<(UUID Id, string Name, double Dist)> Around { get { lock (_lock) return _around.ToList(); } }

    public bool WhileIm => _st.PrivateWhileIm;

    public bool With(UUID id) => _st.PrivatePeople.Any(p => p.Id == id.ToString());

    /// <summary>Keep private mode with someone, or not. The lists are replaced, never changed in place (a save may be
    /// writing the old one on another thread).</summary>
    public void SetPerson(UUID id, bool on)
    {
        lock (_lock)
        {
            var list = _st.PrivatePeople.Where(p => p.Id != id.ToString()).ToList();
            if (on) list.Add(new AlwaysPerson { Id = id.ToString(), Name = _s.Names.DisplayName(id) ?? _s.Names.Legacy(id) ?? "someone" });
            _st.PrivatePeople = list;
        }
        Done();
    }

    /// <summary>Keep private mode at the parcel the person is on, or not; false if where they are isn't known yet.</summary>
    public bool SetHere(bool on)
    {
        if (Here() is not { } h) return false;
        SetPlace(h.Region, h.Parcel, on, h.Name, h.RegionName);
        return true;
    }

    public void SetPlace(string region, int parcel, bool on, string name = "", string regionName = "")
    {
        lock (_lock)
        {
            var list = _st.PrivatePlaces.Where(p => !p.Is(region, parcel)).ToList();
            if (on) list.Add(new AlwaysPlace { Region = region, Parcel = parcel, Name = name, RegionName = regionName });
            _st.PrivatePlaces = list;
        }
        Done();
    }

    public void SetWhileIm(bool on)
    {
        _st.PrivateWhileIm = on;
        Done();
    }

    void Done()
    {
        _save();
        Changed?.Invoke();
        Evaluate();
    }

    /// <summary>SojournPrivate always (PrivateApi.Remember): who, or here; on or off. A refusal, or null.</summary>
    public string? Remember(UUID? who, bool here, bool on)
    {
        if (who is { } id)
        {
            if (id == UUID.Zero || id == _s.AgentId) return "not a person to keep it with";
            SetPerson(id, on);
        }
        if (here && !SetHere(on)) return "where the person is isn't known yet; try again in a moment";
        return null;
    }

    /// <summary>The parcel the person is on: its region's id, local id, name and the region's name.</summary>
    public (string Region, int Parcel, string Name, string RegionName)? Here()
    {
        try
        {
            if (_s.Client is not { } c || c.Network.CurrentSim is not { } sim) return null;
            int local = c.Parcels.GetParcelLocalID(sim, c.Self.SimPosition);
            if (local == 0) return null;
            string name = sim.Parcels.TryGetValue(local, out var parcel) ? parcel.Name : "";
            return (sim.RegionID.ToString(), local, name, sim.Name);
        }
        catch (Exception) { return null; }
    }

    // --- what holds now ---------------------------------------------------------------------------------------------

    /// <summary>The triggers holding now, each with what it says turned private mode on.</summary>
    List<(string Key, string Why)> Active(UUID? heard = null)
    {
        var active = new List<(string, string)>();
        if (Here() is { } h && _st.PrivatePlaces.Any(p => p.Is(h.Region, h.Parcel))) active.Add(($"place:{h.Region}/{h.Parcel}", "place"));
        double now = Json.UnixNow();
        UUID? open = null;
        try { open = OpenImWith?.Invoke(); } catch (Exception) { }
        foreach (var p in _st.PrivatePeople)
        {
            if (!UUID.TryParse(p.Id, out var id)) continue;
            bool holds;
            lock (_lock) holds = _near.Contains(id) || (_imWith.TryGetValue(id, out var t) && now - t < ImQuiet);
            if (holds || id == heard || id == open) active.Add(($"person:{id}", "person"));
        }
        bool imOpen = false;
        try { imOpen = _st.PrivateWhileIm && AnyImOpen?.Invoke() == true; } catch (Exception) { }
        if (imOpen) active.Add(("im", "im"));
        return active;
    }

    readonly object _evaluating = new();

    void Evaluate(UUID? heard = null)
    {
        lock (_evaluating)
        {
            var active = Active(heard);
            var p = _s.Private;
            (string Key, string Why)? fresh;
            lock (_lock)
            {
                _resting.IntersectWith(active.Select(a => a.Key));
                fresh = active.Where(a => !_resting.Contains(a.Key)).Select(a => ((string, string)?)a).FirstOrDefault();
            }
            _switching = Environment.CurrentManagedThreadId;
            try
            {
                if (p.On)
                {
                    if (p.Spell == _autoSpell && p.Automatic && active.Count == 0) p.Set(false, "auto");
                }
                else if (fresh is { } f && p.Set(true, f.Why)) _autoSpell = p.Spell;
            }
            finally { _switching = 0; }
        }
    }

    /// <summary>Someone's words, before they're kept (the network thread).</summary>
    void Heard(UUID from, string kind)
    {
        if (kind == "im") lock (_lock) _imWith[from] = Json.UnixNow();
        if (_st.PrivatePeople.Count == 0 && _st.PrivatePlaces.Count == 0) return;
        // heard nearby counts as near for this line (they may have just arrived); a one-to-one IM is the conversation
        Evaluate(kind is "chat" or "im" ? from : null);
    }

    void Watch()
    {
        long round = 0;
        while (true)
        {
            Thread.Sleep(500);
            try
            {
                // who's within chat range, every other round
                if (round++ % 2 == 0 && _s.Online && _s.Api.ApiMap.TryGetValue("TesselAgent", out var api) && api.Ops.TryGetValue("people", out var op))
                {
                    JsonNode? r;
                    using (Sojourn.Core.Rlv.RlvEngine.OwnRead())   // (the app's own count, not RLV's to hide: docs/rlv.md)
                        r = op.Run(new JsonObject { ["dist"] = ChatRange }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                    var around = (r as JsonObject)?["people"] is JsonArray list
                        ? list.OfType<JsonObject>().Where(x => x.Uuid("id") is { } id && id != _s.AgentId)
                            .Select(x => (x.Uuid("id")!.Value, _s.Names.DisplayName(x.Uuid("id")!.Value) ?? x.Str("display_name") ?? x.Str("name") ?? "someone", x.Num("dist", 999)))
                            .Where(x => x.Item3 <= ChatRange).ToList()
                        : new();
                    bool changed;
                    lock (_lock)
                    {
                        var near = around.Select(a => a.Item1).ToHashSet();
                        changed = !near.SetEquals(_near);
                        _near = near;
                        _around = around;
                    }
                    if (changed) Changed?.Invoke();
                }
                if (_st.PrivatePeople.Count > 0 || _st.PrivatePlaces.Count > 0 || _st.PrivateWhileIm || Automatic) Evaluate();
            }
            catch (Exception) { /* the next round tries again */ }
        }
    }

    /// <summary>One-to-one IMs either way, from the session's own read of its log (it sees sealed events too).</summary>
    void ReadIms()
    {
        long since = _s.Events.Seq;
        while (true)
        {
            List<JsonObject> evs;
            try { evs = _s.Events.GetAsync(since, TimeSpan.FromSeconds(20)).GetAwaiter().GetResult(); }
            catch (Exception) { Thread.Sleep(1000); continue; }
            foreach (var e in evs)
            {
                since = Math.Max(since, (long)e["seq"]!);
                if (e.Str("kind") != "TesselEvents" || e["data"] is not JsonObject d || d.Str("event") != "im" || d.Str("session_type") != "p2p") continue;
                var other = d.Str("dir") == "out" ? d.Uuid("to_id") : d.Uuid("from_id");
                if (other is { } o && o != UUID.Zero) lock (_lock) _imWith[o] = Json.UnixNow();
            }
        }
    }
}
