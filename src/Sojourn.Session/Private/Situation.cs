using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;

namespace Sojourn.Session.Private;

/// <summary>
/// The general picture of what's going on, for private mode (SojournPrivate situation; docs/assistant-incognito.md >
/// The situation): counts and states the client works out itself, never a model's summary of anything, and never a name, a
/// word anyone said, or where. Conversations and the pointer's target come as handles of the spell, so "tell her
/// I'll be back" and "sit on that" work without the assistant learning who or what. It reads the session's event log
/// itself (the client's own read, not /events) to count chat and IMs.
/// </summary>
public sealed class Situation
{
    readonly AgentSession _s;
    readonly object _lock = new();
    // the last half hour of words, as who and when only (Priv: said while private mode was on, left out once it's off)
    readonly List<(double T, UUID From, bool Mine, bool Person, bool Priv)> _nearby = new();
    readonly List<(double T, UUID Session, string Type, UUID From, bool Mine, bool Priv)> _ims = new();
    double _lastAsked;

    /// <summary>What the pointer is on (Assistant/Pointer.cs's JSON), a confirmation waiting, whether the person is
    /// typing to someone nearby, which IM conversation the feed has open: from the client, set by the wiring.</summary>
    public Func<JsonNode?>? Pointer { get; set; }
    public Func<bool>? ConfirmationWaiting { get; set; }
    public Func<UUID, bool>? TypingNearby { get; set; }
    public Func<UUID?>? OpenConversation { get; set; }

    public Situation(AgentSession s)
    {
        _s = s;
        new Thread(Read) { IsBackground = true, Name = "private situation", Priority = ThreadPriority.BelowNormal }.Start();
    }

    void Read()
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
                if (e["data"] is not JsonObject d) continue;
                double t = e["t"]?.GetValue<double>() ?? Json.UnixNow();
                var me = _s.AgentId;
                switch (e.Str("kind"))
                {
                    case "LLChat" when d.Int("chat_type", 1) is 0 or 1 or 2 && !string.IsNullOrEmpty(d.Str("message")):
                        var from = d.Uuid("from_id") ?? UUID.Zero;
                        lock (_lock) _nearby.Add((t, from, from == me, d.Int("source", 1) == 1, _s.Private.WasPrivate(t)));
                        break;
                    case "TesselEvents" when d.Str("event") == "im" && d.Uuid("session_id") is { } sid:
                        lock (_lock) _ims.Add((t, sid, d.Str("session_type") ?? "p2p", d.Uuid("from_id") ?? UUID.Zero, d.Str("dir") == "out", _s.Private.WasPrivate(t)));
                        break;
                }
            }
            lock (_lock)
            {
                double old = Json.UnixNow() - 1800;
                _nearby.RemoveAll(x => x.T < old);
                _ims.RemoveAll(x => x.T < old);
            }
        }
    }

    async Task<JsonNode?> Internal(string pump, string op, CancellationToken ct)
    {
        // the client's own read of its ops, not a caller's: private mode's gate is for callers
        if (!_s.Api.ApiMap.TryGetValue(pump, out var api) || !api.Ops.TryGetValue(op, out var h)) return null;
        if (api.NeedsSession && !_s.Online) return null;
        using var own = Sojourn.Core.Rlv.RlvEngine.OwnRead();   // (and not RLV's to refuse: the client's own count)
        try { return await h.Run(new JsonObject(), ct).WaitAsync(TimeSpan.FromSeconds(1.5), ct); }
        catch (Exception) { return null; }
    }

    static string N(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    public async Task<JsonNode?> BuildAsync(CancellationToken ct)
    {
        double now = Json.UnixNow();
        var p = _s.Private;
        var o = new JsonObject
        {
            ["incognito"] = p.On,
            ["minutes_incognito"] = p.On ? Math.Round(p.Minutes) : null,
            ["time"] = DateTime.Now.ToString("HH:mm"),
        };
        var summary = new List<string>();
        double asked;
        lock (_lock) { asked = _lastAsked; _lastAsked = now; }
        var me = _s.AgentId;

        // nearby chat: who's talking and how much, not what
        lock (_lock)
        {
            var nearby = _nearby.Where(x => p.On || !x.Priv).ToList();
            var five = nearby.Where(x => x.T > now - 300).ToList();
            var people = five.Where(x => x.Person && !x.Mine).Select(x => x.From).Distinct().Count();
            var chat = new JsonObject
            {
                ["people_talking_last_5_min"] = people,
                ["lines_last_5_min"] = five.Count(x => !x.Mine && x.Person),
                ["object_lines_last_5_min"] = five.Count(x => !x.Person),
                ["you_spoke_last_5_min"] = five.Any(x => x.Mine),
                ["new_since_last_asked"] = nearby.Count(x => x.T > asked && !x.Mine && x.Person),
            };
            var lastOther = nearby.Where(x => !x.Mine && x.Person).Select(x => x.T).DefaultIfEmpty(0).Max();
            if (lastOther > 0) chat["last_line_s_ago"] = Math.Round(now - lastOther);
            o["nearby_chat"] = chat;
            if (people > 0 && five.Any(x => x.Mine)) summary.Add($"In a conversation nearby with {N(people, "person", "people")}");
            else if (people > 0) summary.Add($"{N(people, "person", "people")} talking nearby");
        }

        // IMs: how many conversations, how many new lines from how many people, as handles to answer into
        lock (_lock)
        {
            var convs = new JsonArray();
            UUID? open = OpenConversation?.Invoke();
            int unreadLines = 0;
            var unreadFrom = new HashSet<UUID>();
            foreach (var g in _ims.Where(x => x.T > now - 1800 && (p.On || !x.Priv)).GroupBy(x => x.Session).OrderByDescending(g => g.Max(x => x.T)).Take(8))
            {
                var fresh = g.Where(x => !x.Mine && x.T > asked).ToList();
                unreadLines += fresh.Count;
                foreach (var f in fresh) unreadFrom.Add(f.From);
                var c = new JsonObject
                {
                    ["chat"] = p.On ? p.Blind(g.Key, "chat") : g.Key.ToString(),
                    ["kind"] = g.First().Type switch { "p2p" => "one to one", var t => t },
                    ["people"] = g.Where(x => !x.Mine).Select(x => x.From).Distinct().Count(),
                    ["new_since_last_asked"] = fresh.Count,
                    ["last_s_ago"] = Math.Round(now - g.Max(x => x.T)),
                    ["you_wrote"] = g.Any(x => x.Mine),
                };
                if (open == g.Key) c["open"] = true;
                convs.Add(c);
            }
            o["ims"] = convs;
            if (unreadLines > 0) summary.Add($"{N(unreadLines, "new IM", "new IMs")} from {N(unreadFrom.Count, "person", "people")}");
            else if (convs.Count > 0) summary.Add($"{N(convs.Count, "IM conversation", "IM conversations")} in the last half hour");
        }

        // people around: counts by distance
        if (await Internal("TesselAgent", "people", ct) is JsonObject pe && pe["people"] is JsonArray list)
        {
            var dists = list.OfType<JsonObject>().Select(x => x.Num("dist", 999)).ToList();
            int close = dists.Count(d => d <= 3), chatRange = dists.Count(d => d <= 20);
            var typing = TypingNearby is { } tn
                ? list.OfType<JsonObject>().Count(x => x.Num("dist", 999) <= 20 && x.Uuid("id") is { } id && tn(id)) : 0;
            o["people"] = new JsonObject
            {
                ["within_3_m"] = close, ["within_chat_range"] = chatRange, ["around"] = dists.Count, ["typing_nearby"] = typing,
            };
            summary.Add(chatRange == 0 ? "no one within chat range" : $"{N(chatRange, "person", "people")} within chat range");
            if (typing > 0) summary.Add($"{N(typing, "person", "people")} typing nearby");
        }

        // voice: whether anyone is speaking, not who
        if (await Internal("SojournVoice", "status", ct) is JsonObject v && !v.ContainsKey("error"))
        {
            var speakers = (v["people"] as JsonArray)?.OfType<JsonObject>().Where(x => x.Bool("speaking", false)).Count() ?? 0;
            var voice = new JsonObject
            {
                // not voice's own status line: a call's names the other person
                ["connected"] = (v.Str("status") ?? "").Contains(": connected"), ["in_call"] = v["call"] is JsonObject, ["ringing"] = v["ringing"] is JsonObject,
                ["speaking_now"] = speakers, ["in_voice"] = (v["people"] as JsonArray)?.Count ?? 0,
                ["your_mic"] = v.Bool("muted", false) ? "muted" : v.Bool("mic_live", false) ? "live" : "off",
            };
            o["voice"] = voice;
            if (speakers > 0) summary.Add(speakers == 1 ? "someone nearby is speaking" : $"{speakers} people speaking");
            if (voice.Bool("in_call", false)) summary.Add("in a voice call");
            if (voice.Bool("ringing", false)) summary.Add("a call is ringing");
        }

        // the place, in general terms
        if (_s.Client is { } cl && cl.Network.CurrentSim is { } sim)
        {
            var place = new JsonObject();
            var pos = cl.Self.SimPosition;
            var local = cl.Parcels.GetParcelLocalID(sim, pos);
            if (local != 0 && sim.Parcels.TryGetValue(local, out var parcel))
            {
                bool yours = parcel.OwnerID == me || (parcel.IsGroupOwned && _s.State.Groups.ContainsKey(parcel.GroupID));
                bool restricted = (parcel.Flags & (ParcelFlags.UseAccessList | ParcelFlags.UseAccessGroup)) != 0;
                place["yours"] = yours;
                place["restricted_access"] = restricted;
                summary.Add(yours ? "on your own land" : restricted ? "on land with restricted access" : "on someone else's land");
            }
            if (_s.Region?.TerrainHeight(pos.X, pos.Y) is { } ground && pos.Z - ground > 60) { place["high_up"] = true; summary.Add("high up (a skybox or platform)"); }
            place["sitting"] = cl.Self.SittingOn != 0;
            place["flying"] = cl.Self.Movement.Fly;
            o["place"] = place;
            if (cl.Self.SittingOn != 0) summary.Add("sitting");
        }

        // waiting for the person
        var notices = _s.Notify.All.ToList();
        var waiting = new JsonObject
        {
            ["confirmation"] = ConfirmationWaiting?.Invoke() ?? false,
            ["script_menus"] = notices.Count(n => n.Str("name") is "ScriptDialog" or "ScriptDialogGroup"),
            ["other_notices"] = notices.Count(n => n.Str("name") is not ("ScriptDialog" or "ScriptDialogGroup")),
        };
        o["waiting"] = waiting;

        if (p.On && p.BlindTarget(Pointer?.Invoke()) is { } that) o["that"] = that;
        if (p.On) summary.Add($"incognito for {N((int)Math.Round(p.Minutes), "minute", "minutes")}");
        o["summary"] = summary.Count == 0 ? "nothing going on that the client can tell" : char.ToUpperInvariant(summary[0][0]) + string.Join("; ", summary)[1..] + ".";
        return o;
    }
}
