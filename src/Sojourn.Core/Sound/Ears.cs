using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Agents;
using Sojourn.Core.World;

namespace Sojourn.Core.Sound;

/// <summary>
/// One resident's hearing. A headless resident has no mixed audio, so every sound its session receives becomes an
/// event in its log, placed relative to the resident and attenuated the way the viewer's OpenAL setup would
/// (docs/sound.md): `sound` events, `heard_rhythm` for claps and knocks (hear.py's shape), `heard` when a sound
/// holds speech and a transcriber is configured, and parcel music stream changes.
/// </summary>
public sealed class Ears
{
    readonly AgentSession _s;
    readonly object _lock = new();
    readonly Dictionary<UUID, LoopState> _loops = new();             // object id -> what it's looping
    readonly Dictionary<(UUID, UUID), Habit> _recent = new();       // (source, sound) -> how often I've heard it
    readonly Dictionary<UUID, string> _names = new();                // sound asset -> a name from my own inventory
    readonly Dictionary<UUID, Burst> _bursts = new();                // source avatar -> a phrase of claps
    readonly Dictionary<UUID, DateTime> _steps = new();              // avatar -> last footsteps told
    string? _musicUrl;
    DateTime _arrived = DateTime.UtcNow;
    double _sinceScan;

    // the viewer's mix with default settings: SFX volume 0.5, master 1, OpenAL inverse distance clamped, ref 1 m, rolloff 1
    const double SfxVolume = 0.5, AmbientVolume = 0.5;
    const double Inaudible = -72;          // dB at the ear; below this a sound isn't reported
    const double AssumedRms = -20;         // dBFS for an asset we couldn't decode
    static readonly TimeSpan Novel = TimeSpan.FromMinutes(10), Coalesce = TimeSpan.FromSeconds(5);

    sealed class LoopState
    {
        public UUID Sound;
        public float Gain;
        public bool Audible;
        public bool Started;               // started while I was here (rather than found when I arrived)
        public DateTime ToldAt;
    }

    /// <summary>Habituation: a sound repeating from one source is told three times per ten minutes, then counted.</summary>
    sealed class Habit
    {
        public DateTime LastHeard, WindowStart;
        public int Told, Suppressed;
    }

    sealed class Burst
    {
        public UUID Sound;
        public readonly List<DateTime> Hits = new();
        public double Db = double.NegativeInfinity;
        public JsonObject? Where;
        public bool Mine;
    }

    public Ears(AgentSession s) => _s = s;

    SoundCatalog Catalog => _s.World.Sounds(_s.GridKey);

    /// <summary>A sound's name as this resident could know it: its own inventory, else the well-known sounds.</summary>
    public string? Name(UUID sound)
    {
        lock (_lock) if (_names.TryGetValue(sound, out var n)) return n;
        return Catalog.Name(sound);
    }

    public void Learn(UUID sound, string? name)
    {
        if (sound == UUID.Zero || string.IsNullOrWhiteSpace(name)) return;
        lock (_lock) _names.TryAdd(sound, name.Trim());
    }

    public void Wire(GridClient c)
    {
        c.Sound.SoundTrigger += (_, e) => _s.Guard("sound", () => OnTrigger(c, e));
        c.Sound.AttachedSound += (_, e) => _s.Guard("sound", () => OnAttached(c, e));
        c.Sound.AttachedSoundGainChange += (_, e) => _s.Guard("sound", () =>
        {
            lock (_lock) if (_loops.TryGetValue(e.ObjectID, out var l)) l.Gain = e.Gain;
        });
        c.Objects.ObjectUpdate += (_, e) => _s.Guard("sound", () => OnPrim(c, e.Simulator, e.Prim));
        c.Parcels.ParcelProperties += (_, e) => _s.Guard("sound", () => OnParcel(c, e));
        c.Avatars.AvatarAnimation += (_, e) => _s.Guard("sound", () => OnAnimation(c, e));
    }

    public void Reset()
    {
        lock (_lock)
        {
            _loops.Clear(); _recent.Clear(); _bursts.Clear(); _steps.Clear();
            _musicUrl = null;
            _arrived = DateTime.UtcNow;
        }
    }

    /// <summary>A teleport or crossing: loops found in the next seconds were already playing there.</summary>
    public void Arrived() { lock (_lock) _arrived = DateTime.UtcNow; }

    /// <summary>On the host clock: loops coming into or out of earshot, and claps phrases that have ended.</summary>
    public void Tick(GridClient c, double dt)
    {
        _sinceScan += dt;
        if (_sinceScan < 1) return;
        _sinceScan = 0;
        FlushBursts(c, force: false);
        ScanLoops(c);
    }

    // ---------- geometry and loudness ----------

    /// <summary>The ear: my head (avatar position + 0.6 m), in global metres; facing from my body rotation.</summary>
    static Vector3d Ear(GridClient c) => c.Self.GlobalPosition + new Vector3d(0, 0, 0.6);

    static Vector3d Global(ulong handle, Vector3 local)
    {
        Utils.LongToUInts(handle, out var x, out var y);
        return new Vector3d(x + local.X, y + local.Y, local.Z);
    }

    /// <summary>Viewer attenuation: inverse distance clamped at 1 m (OpenAL defaults the viewer leaves alone).</summary>
    public static double GainDb(double gain, double dist, double typeVolume = SfxVolume) =>
        20 * Math.Log10(Math.Max(1e-6, Math.Clamp(gain, 0, 1) * typeVolume / Math.Max(1, dist)));

    /// <summary>Where a point is from me: distance, bearing (+ = right, as earlevel's balance), elevation, words.</summary>
    JsonObject Place(GridClient c, Vector3d src, ulong handle, Vector3 local, out double dist)
    {
        var ear = Ear(c);
        var d = src - ear;
        dist = d.Length();
        var rel = new Vector3((float)d.X, (float)d.Y, (float)d.Z) * Quaternion.Inverse(c.Self.SimRotation);
        var bearing = Math.Atan2(-rel.Y, rel.X) * 180 / Math.PI;
        var horiz = Math.Sqrt(rel.X * rel.X + rel.Y * rel.Y);
        var elev = Math.Atan2(rel.Z, horiz) * 180 / Math.PI;
        var region = _s.World.Region(_s.GridKey, handle)?.Name ?? c.Network.CurrentSim?.Name ?? "";
        return new JsonObject
        {
            ["region"] = region,
            ["pos"] = Json.Arr(local),
            ["dist"] = Math.Round(dist, 1),
            ["dir"] = Words(bearing, elev, dist),
            ["bearing"] = Math.Round(bearing),
            ["elev"] = Math.Round(elev),
        };
    }

    public static string Words(double bearing, double elev, double dist)
    {
        if (dist < 1.2) return "right here";
        var a = Math.Abs(bearing);
        var side = bearing > 0 ? "right" : "left";
        var w = a <= 22.5 ? "ahead" : a <= 67.5 ? $"ahead-{side}" : a <= 112.5 ? $"to the {side}" : a <= 157.5 ? $"behind-{side}" : "behind";
        if (elev > 35) w = elev > 70 ? "above" : w + ", above";
        else if (elev < -35) w = elev < -70 ? "below" : w + ", below";
        return w;
    }

    // ---------- sources ----------

    /// <summary>Describes who or what made a sound: an avatar (gestures), one of their attachments, or an object.</summary>
    JsonObject Source(GridClient c, RegionWorld? w, UUID objectId, UUID ownerId, out UUID wearer, out Primitive? prim)
    {
        wearer = UUID.Zero;
        prim = null;
        var names = _s.Names;
        if (objectId == ownerId || objectId == UUID.Zero)
            return new JsonObject { ["kind"] = "avatar", ["id"] = ownerId.ToString(), ["name"] = names.Legacy(ownerId) };
        prim = w?.PrimById(objectId);
        var o = new JsonObject { ["kind"] = "object", ["id"] = objectId.ToString() };
        if (prim is not null && w is not null)
        {
            var root = _s.Objects.RootOf(w, prim);
            if (_s.Objects.IsAttachment(w, prim, out var av, out _) && av is not null)
            {
                o["kind"] = "attachment";
                wearer = av.ID;
                ownerId = av.ID;
            }
            o["name"] = root.Properties?.Name ?? prim.Properties?.Name;
            if (ownerId == UUID.Zero) ownerId = root.Properties?.OwnerID ?? UUID.Zero;
        }
        if (ownerId != UUID.Zero)
        {
            o["owner_id"] = ownerId.ToString();
            o["owner"] = names.Legacy(ownerId);
        }
        return o;
    }

    /// <summary>Fills names that weren't known yet (avatar names, object names), waiting briefly.</summary>
    async Task CompleteAsync(GridClient c, RegionWorld? w, JsonObject src, Primitive? prim)
    {
        var ids = new List<UUID>();
        foreach (var k in new[] { "id", "owner_id" })
            if (src[k] is JsonValue v && UUID.TryParse(v.GetValue<string>(), out var id) && (k == "owner_id" || src["kind"]?.GetValue<string>() == "avatar"))
                if (_s.Names.Legacy(id) is null) ids.Add(id);
        var tasks = new List<Task>();
        if (ids.Count > 0) tasks.Add(_s.Names.ResolveAsync(c, ids, TimeSpan.FromSeconds(2)));
        if (prim is not null && w is not null && src["name"] is null)
        {
            var root = _s.Objects.RootOf(w, prim);
            var sim = c.Network.Simulators.ToArray().FirstOrDefault(x => x.Handle == w.Handle);
            tasks.Add(_s.Objects.FetchAsync(w, new[] { root }, TimeSpan.FromSeconds(2), CancellationToken.None, sim: sim));
        }
        if (tasks.Count == 0) return;
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        if (src["kind"]?.GetValue<string>() == "avatar" && UUID.TryParse(src["id"]?.GetValue<string>(), out var aid))
            src["name"] = _s.Names.Legacy(aid);
        if (UUID.TryParse(src["owner_id"]?.GetValue<string>() ?? "", out var oid)) src["owner"] = _s.Names.Legacy(oid);
        if (prim is not null && w is not null && src["name"] is null)
            src["name"] = _s.Objects.RootOf(w, prim).Properties?.Name;
    }

    // ---------- one-shots ----------

    void OnTrigger(GridClient c, SoundTriggerEventArgs e)
    {
        if (e.SoundID == UUID.Zero) return;
        var w = _s.World.Region(_s.GridKey, e.RegionHandle);
        var src = Source(c, w, e.ObjectID, e.OwnerID, out var wearer, out var prim);
        var pos = Global(e.RegionHandle, e.Position);
        var where = Place(c, pos, e.RegionHandle, e.Position, out var dist);
        var me = c.Self.AgentID;
        var mine = e.OwnerID == me || wearer == me;
        var what = Catalog.IsCollision(e.SoundID) ? "collision" : "trigger";
        _ = EmitAsync(c, w, what, e.SoundID, e.Gain, dist, where, src, prim, mine, avatarSound: src["kind"]?.GetValue<string>() == "avatar");
    }

    void OnAttached(GridClient c, AttachedSoundEventArgs e)
    {
        var w = _s.World.Region(_s.GridKey, e.Simulator.Handle);
        var stop = e.SoundID == UUID.Zero || (e.Flags & SoundFlags.Stop) != 0;
        if (stop || (e.Flags & SoundFlags.Loop) != 0)
        {
            SetLoop(c, e.ObjectID, stop ? UUID.Zero : e.SoundID, e.Gain, started: true);
            return;
        }
        if (w?.PrimById(e.ObjectID) is not { } p) return;   // not in view yet; the viewer would postpone, a one-shot is gone by then
        var src = Source(c, w, e.ObjectID, e.OwnerID, out var wearer, out var prim);
        var at = w.RegionPosition(p);
        var where = Place(c, Global(w.Handle, at), w.Handle, at, out var dist);
        if (p.SoundRadius > 0.1f && dist >= p.SoundRadius) return;   // the viewer's cutoff
        var mine = e.OwnerID == c.Self.AgentID || wearer == c.Self.AgentID;
        _ = EmitAsync(c, w, "play", e.SoundID, e.Gain, dist, where, src, prim, mine, avatarSound: false);
    }

    async Task EmitAsync(GridClient c, RegionWorld? w, string what, UUID sound, float gain, double dist, JsonObject where,
        JsonObject src, Primitive? prim, bool mine, bool avatarSound)
    {
        try
        {
            var info = await Catalog.InfoAsync(sound).WaitAsync(TimeSpan.FromSeconds(3)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
            var db = Math.Round((info?.RmsDb ?? AssumedRms) + GainDb(gain, dist), 1);
            if (db < Inaudible) return;
            var srcId = UUID.TryParse(src["id"]?.GetValue<string>() ?? "", out var sid) ? sid : UUID.Zero;

            // claps and knocks from people (gesture sounds are short): hear.py's rhythm events, one per phrase
            if (avatarSound && (info is null || info.Duration <= 0.8))
            {
                lock (_lock)
                {
                    if (!_bursts.TryGetValue(srcId, out var b)) _bursts[srcId] = b = new Burst { Sound = sound, Mine = mine };
                    b.Hits.Add(DateTime.UtcNow);
                    b.Db = Math.Max(b.Db, db);
                    b.Where = where;
                    b.Sound = sound;
                }
                await CompleteAsync(c, w, src, prim);
                lock (_lock) if (_bursts.TryGetValue(srcId, out var b2)) b2.Where!["from"] = src["name"]?.GetValue<string>();
                return;
            }
            if (mine && avatarSound) return;   // my own gesture sounds come back from the region

            // the same sound from the same source: a burst within a few seconds is one event; after three in ten
            // minutes it's habituated (counted, reported with the next event after the window)
            int repeats;
            bool novel, habituated;
            lock (_lock)
            {
                var key = (srcId, sound);
                var now = DateTime.UtcNow;
                if (!_recent.TryGetValue(key, out var h)) _recent[key] = h = new Habit { WindowStart = now, LastHeard = DateTime.MinValue };
                novel = now - h.LastHeard > Novel;
                if (now - h.LastHeard < Coalesce) { h.Suppressed++; h.LastHeard = now; return; }
                if (now - h.WindowStart > Novel) { h.WindowStart = now; h.Told = 0; }
                h.LastHeard = now;
                if (h.Told >= 3) { h.Suppressed++; return; }
                h.Told++;
                habituated = h.Told == 3;
                repeats = h.Suppressed;
                h.Suppressed = 0;
                if (_recent.Count > 2000) foreach (var k in _recent.Where(kv => now - kv.Value.LastHeard > Novel).Select(kv => kv.Key).ToList()) _recent.Remove(k);
            }
            await CompleteAsync(c, w, src, prim);
            var d = new JsonObject { ["what"] = what, ["sound_id"] = sound.ToString() };
            if (Name(sound) is { } name) d["name"] = name;
            if (info is not null) d["dur"] = info.Duration;
            d["source"] = src;
            foreach (var (k, v) in where) d[k] = v?.DeepClone();
            d["gain"] = Math.Round(gain, 2);
            d["db"] = db;
            if (repeats > 0) d["repeats_before"] = repeats;
            if (habituated) d["habituated"] = true;   // more of this from here in the next ten minutes go unreported
            if (mine) d["mine"] = true;
            var notable = !mine && (what == "collision"
                ? dist < 5 && db > -30
                : avatarSound || src["kind"]?.GetValue<string>() == "attachment" ? dist < 20 : novel && db > -35);
            d["notable"] = notable;

            // speech in a sound (a spoken WAV an object plays): transcribed once per asset, if a transcriber is set up
            string? text = null;
            if (info is { Duration: >= 1.0 } && Transcriber.Shared is { } t && !mine)
                text = await Catalog.SpeechAsync(sound, t);
            if (!string.IsNullOrWhiteSpace(text)) d["text"] = text;
            _s.Events.Log("sound", d);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var first = _s.Config.First;
                _s.Events.Log("heard", new JsonObject
                {
                    ["text"] = text, ["dur"] = info!.Duration, ["db"] = db,
                    ["me"] = System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(first)}\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                    ["from"] = src["name"]?.DeepClone() ?? src["owner"]?.DeepClone(), ["dist"] = where["dist"]?.DeepClone(),
                    ["dir"] = where["dir"]?.DeepClone(), ["sound_id"] = sound.ToString(),
                });
            }
        }
        catch (Exception e) { _s.Log.LogDebug(e, "sound event"); }
    }

    void FlushBursts(GridClient c, bool force)
    {
        List<Burst> done;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var ended = _bursts.Where(kv => force || now - kv.Value.Hits[^1] > TimeSpan.FromSeconds(1.6)).ToList();
            foreach (var kv in ended) _bursts.Remove(kv.Key);
            done = ended.Select(kv => kv.Value).Where(b => b.Hits.Count <= 40).ToList();   // a flood is rain or a drum loop
        }
        foreach (var b in done)
        {
            var iv = b.Hits.Zip(b.Hits.Skip(1), (x, y) => (int)Math.Round((y - x).TotalMilliseconds)).ToList();
            const int grid = 125;
            var pattern = "x" + string.Concat(iv.Select(g => new string('.', Math.Max(0, (int)Math.Round(g / (double)grid) - 1)) + "x"));
            var mean = iv.Count > 0 ? iv.Average() : 0;
            var tempo = iv.Count == 0 ? "" : mean <= 330 ? "fast" : mean >= 650 ? "slow" : "steady";
            var d = new JsonObject
            {
                ["count"] = b.Hits.Count, ["intervals_ms"] = new JsonArray(iv.Select(x => (JsonNode)x).ToArray()),
                ["pattern"] = pattern, ["tempo"] = tempo, ["db"] = Math.Round(b.Db, 1),
                ["ago_s"] = Math.Round((DateTime.UtcNow - b.Hits[^1]).TotalSeconds, 1),
                ["mine"] = b.Mine, ["sound_id"] = b.Sound.ToString(),
            };
            if (Name(b.Sound) is { } n) d["sound"] = n;
            if (b.Where is { } w)
            {
                d["from"] = w["from"]?.DeepClone();
                d["dist"] = w["dist"]?.DeepClone();
                d["dir"] = w["dir"]?.DeepClone();
            }
            _s.Events.Log("heard_rhythm", d);
        }
    }

    // ---------- loops ----------

    void OnPrim(GridClient c, Simulator sim, Primitive p)
    {
        var looping = p.Sound != UUID.Zero && (p.SoundFlags & SoundFlags.Stop) == 0 && (p.SoundFlags & SoundFlags.Loop) != 0;
        bool known;
        lock (_lock) known = _loops.ContainsKey(p.ID);
        if (!looping && !known) return;
        // a loop seen in the first seconds after arriving was already playing; later ones started while I was here
        SetLoop(c, p.ID, looping ? p.Sound : UUID.Zero, p.SoundGain, started: DateTime.UtcNow - _arrived > TimeSpan.FromSeconds(15));
    }

    void SetLoop(GridClient c, UUID objectId, UUID sound, float gain, bool started)
    {
        LoopState? stopped = null;
        lock (_lock)
        {
            _loops.TryGetValue(objectId, out var l);
            if (sound == UUID.Zero)
            {
                if (l is null) return;
                _loops.Remove(objectId);
                if (l.Audible) stopped = l;
            }
            else if (l is null || l.Sound != sound)
            {
                _loops[objectId] = new LoopState { Sound = sound, Gain = gain, Started = started };
                return;   // the scan tells me about it when it's in earshot
            }
            else { l.Gain = gain; return; }
        }
        if (stopped is not null) _ = StopAsync(c, objectId, stopped);
    }

    async Task StopAsync(GridClient c, UUID objectId, LoopState l)
    {
        var w = _s.VisibleRegions().FirstOrDefault(r => r.PrimById(objectId) is not null);
        var p = w?.PrimById(objectId);
        var src = Source(c, w, objectId, UUID.Zero, out var wearer, out var prim);
        await CompleteAsync(c, w, src, prim);
        var d = new JsonObject { ["what"] = "stop", ["sound_id"] = l.Sound.ToString() };
        if (Name(l.Sound) is { } n) d["name"] = n;
        d["source"] = src;
        if (p is not null && w is not null)
        {
            var at = w.RegionPosition(p);
            foreach (var (k, v) in Place(c, Global(w.Handle, at), w.Handle, at, out _)) d[k] = v?.DeepClone();
        }
        if (wearer == c.Self.AgentID || src["owner_id"]?.GetValue<string>() == c.Self.AgentID.ToString()) d["mine"] = true;
        d["notable"] = false;
        _s.Events.Log("sound", d);
    }

    void ScanLoops(GridClient c)
    {
        List<(UUID Id, LoopState L)> loops;
        lock (_lock) loops = _loops.Select(kv => (kv.Key, kv.Value)).ToList();
        if (loops.Count == 0) return;
        var regions = _s.VisibleRegions().ToList();
        foreach (var (id, l) in loops)
        {
            var w = regions.FirstOrDefault(r => r.PrimById(id) is not null);
            if (w?.PrimById(id) is not { } p) continue;
            var at = w.RegionPosition(p);
            var g = Global(w.Handle, at);
            var dist = (g - Ear(c)).Length();
            var cutoffFrom = dist;
            if (_s.Objects.IsAttachment(w, p, out var av, out _) && av is not null)
                cutoffFrom = (Global(w.Handle, av.Position) - Ear(c)).Length();
            var info = Catalog.InfoAsync(l.Sound);
            var rms = info.IsCompletedSuccessfully && info.Result is { } i ? i.RmsDb : AssumedRms;
            var db = rms + GainDb(l.Gain, dist);
            var audible = db >= Inaudible && !(p.SoundRadius > 0.1f && cutoffFrom >= p.SoundRadius);
            if (audible == l.Audible) continue;
            l.Audible = audible;
            if (!audible) continue;
            if (DateTime.UtcNow - l.ToldAt < Novel) continue;   // walked out and back in: I already know it's there
            l.ToldAt = DateTime.UtcNow;
            _ = LoopHeardAsync(c, w, id, p, l, dist, db);
        }
    }

    async Task LoopHeardAsync(GridClient c, RegionWorld w, UUID id, Primitive p, LoopState l, double dist, double db)
    {
        try
        {
            var src = Source(c, w, id, UUID.Zero, out var wearer, out var prim);
            await CompleteAsync(c, w, src, prim);
            var info = await Catalog.InfoAsync(l.Sound).WaitAsync(TimeSpan.FromSeconds(3)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
            if (info is not null) db = info.RmsDb + GainDb(l.Gain, dist);
            var at = w.RegionPosition(p);
            var d = new JsonObject { ["what"] = "loop", ["sound_id"] = l.Sound.ToString() };
            if (Name(l.Sound) is { } n) d["name"] = n;
            if (info is not null) d["dur"] = info.Duration;
            d["source"] = src;
            foreach (var (k, v) in Place(c, Global(w.Handle, at), w.Handle, at, out _)) d[k] = v?.DeepClone();
            d["gain"] = Math.Round(l.Gain, 2);
            d["db"] = Math.Round(db, 1);
            var mine = wearer == c.Self.AgentID || src["owner_id"]?.GetValue<string>() == c.Self.AgentID.ToString();
            if (mine) d["mine"] = true;
            d["started"] = l.Started;   // false: it was already playing when I came within earshot
            d["notable"] = !mine && l.Started && db > -35;
            _s.Events.Log("sound", d);
        }
        catch (Exception e) { _s.Log.LogDebug(e, "loop event"); }
    }

    /// <summary>What I can hear looping right now (the `sounds` op).</summary>
    public JsonArray Soundscape(GridClient c)
    {
        var list = new List<(double Db, JsonObject O)>();
        List<(UUID Id, LoopState L)> loops;
        lock (_lock) loops = _loops.Select(kv => (kv.Key, kv.Value)).ToList();
        var regions = _s.VisibleRegions().ToList();
        foreach (var (id, l) in loops)
        {
            if (!l.Audible) continue;
            var w = regions.FirstOrDefault(r => r.PrimById(id) is not null);
            if (w?.PrimById(id) is not { } p) continue;
            var at = w.RegionPosition(p);
            var src = Source(c, w, id, UUID.Zero, out _, out _);
            var where = Place(c, Global(w.Handle, at), w.Handle, at, out var dist);
            var info = Catalog.InfoAsync(l.Sound);
            var rms = info.IsCompletedSuccessfully && info.Result is { } i ? i.RmsDb : AssumedRms;
            var db = Math.Round(rms + GainDb(l.Gain, dist), 1);
            var o = new JsonObject { ["sound_id"] = l.Sound.ToString(), ["name"] = Name(l.Sound), ["source"] = src };
            foreach (var (k, v) in where) o[k] = v?.DeepClone();
            o["db"] = db;
            list.Add((db, o));
        }
        return new JsonArray(list.OrderByDescending(x => x.Db).Select(x => (JsonNode)x.O).ToArray());
    }

    public string? MusicUrl => _musicUrl;

    // ---------- music stream ----------

    void OnParcel(GridClient c, ParcelPropertiesEventArgs e)
    {
        if (e.Simulator.Handle != c.Network.CurrentSim?.Handle || e.Parcel is not { } parcel) return;
        var pos = c.Self.SimPosition;
        if (!Contains(parcel, pos)) return;
        var url = parcel.MusicURL?.Trim() ?? "";
        // the viewer plays a URL only if it's longer than 12 characters and starts with http(s)
        if (url.Length <= 12 || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "";
        string? prev;
        lock (_lock) { prev = _musicUrl; _musicUrl = url; }
        if ((prev ?? "") == url) return;
        if (prev is null && url == "") return;   // first parcel with no stream: nothing to say
        _s.Events.Log("sound", new JsonObject
        {
            ["what"] = "stream", ["url"] = url, ["parcel"] = parcel.Name, ["region"] = e.Simulator.Name, ["notable"] = false,
        });
    }

    static bool Contains(Parcel p, Vector3 pos)
    {
        var bm = p.Bitmap;
        if (bm is not { Length: >= 512 }) return pos.X >= p.AABBMin.X && pos.X <= p.AABBMax.X && pos.Y >= p.AABBMin.Y && pos.Y <= p.AABBMax.Y;
        var x = Math.Clamp((int)(pos.X / 4), 0, 63);
        var y = Math.Clamp((int)(pos.Y / 4), 0, 63);
        var bit = y * 64 + x;
        return (bm[bit / 8] & (1 << (bit % 8))) != 0;
    }

    // ---------- footsteps (each viewer makes these itself, from the walk animations) ----------

    static readonly HashSet<UUID> Walking = new()
    {
        Animations.WALK, Animations.RUN, Animations.FEMALE_WALK, Animations.CROUCHWALK,
        // walk_new, female_walk_new, run_new, female_run_new (llanimationstates.cpp)
        new UUID("33339176-7ddc-9397-94a4-bf3403cbc8f5"), new UUID("d60c41d2-7c24-7074-d3fa-6101cea22a51"),
        new UUID("1ab1b236-cd08-21e6-0cbc-0d923fc6eca2"), new UUID("85995026-eade-5d78-d364-94a64512cb66"),
    };

    void OnAnimation(GridClient c, AvatarAnimationEventArgs e)
    {
        if (e.AvatarID == c.Self.AgentID || !e.Animations.Any(a => Walking.Contains(a.AnimationID))) return;
        lock (_lock)
        {
            if (_steps.TryGetValue(e.AvatarID, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(60)) return;
        }
        var w = _s.Region;
        if (w?.AvatarById(e.AvatarID) is not { } av) return;
        var where = Place(c, Global(w.Handle, av.Position), w.Handle, av.Position, out var dist);
        var db = Math.Round(-16 + GainDb(0.1, dist, AmbientVolume), 1);   // gain 0.1, ambient; footstep samples are loud
        if (db < -60) return;   // footsteps carry only a few metres
        lock (_lock) _steps[e.AvatarID] = DateTime.UtcNow;
        var name = _s.Names.Legacy(e.AvatarID);
        var d = new JsonObject
        {
            ["what"] = "footsteps", ["name"] = "footsteps",
            ["source"] = new JsonObject { ["kind"] = "avatar", ["id"] = e.AvatarID.ToString(), ["name"] = name },
        };
        foreach (var (k, v) in where) d[k] = v?.DeepClone();
        d["db"] = db;
        d["notable"] = false;
        _s.Events.Log("sound", d);
    }
}
