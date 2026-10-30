using LibreMetaverse;
using Sojourn.Core.Agents;
using NVector3 = System.Numerics.Vector3;
using LMediaEntry = LibreMetaverse.MediaEntry;

namespace Sojourn.Session.Media;

// Where media comes from: prims whose MediaURL says they have media on a face (their entries fetched from the region's
// ObjectMedia capability, one request a second, nearest first, as LLObjectMediaDataClient paces them), the agent's
// parcel (its media URL, texture and type, and the region's ParcelMediaCommand and ParcelMediaUpdate), and navigations
// sent back (ObjectMediaNavigate).
public sealed partial class MediaManager
{
    sealed class PrimMedia
    {
        public required ulong Region;
        public required uint LocalId;
        public required UUID Id;
        public Simulator? Sim;
        public int Version = -1, Fetched = -1;
        public UUID UpdatedBy;
        public bool InFlight;
        public long RetryAt;
        public int Failures;
        public Placement? Place;
        /// <summary>The faces' entries as last fetched (set_url sends them back with one changed).</summary>
        public LMediaEntry?[]? Entries;
    }

    readonly Dictionary<(ulong, uint), PrimMedia> _prims = new();
    long _lastFetch;

    /// <summary>A prim's faces' entries as last fetched (a copy), or null if they haven't been (MediaFaceSettings).</summary>
    public LMediaEntry?[]? Fetched(ulong region, uint localId) =>
        _prims.TryGetValue((region, localId), out var pm) && pm.Entries is { } e && pm.Fetched >= pm.Version ? (LMediaEntry?[])e.Clone() : null;

    /// <summary>The MediaURL a prim carries ("x-mv:0000000003/agent-id"): the media's version and who last changed it.</summary>
    static (int Version, UUID By) ParseVersion(string s)
    {
        if (!s.StartsWith("x-mv:") || s.Length < 15 || !int.TryParse(s.AsSpan(5, 10), out int v)) return (-1, UUID.Zero);
        int slash = s.IndexOf('/');
        return (v, slash > 0 && UUID.TryParse(s[(slash + 1)..], out var by) ? by : UUID.Zero);
    }

    /// <summary>A prim with media as the scan found it: where it is and whose it is.</summary>
    readonly record struct Found(ulong Region, uint LocalId, UUID Id, Simulator Sim, string MediaUrl, Placement Place);
    readonly record struct Placement(bool Mine, bool OnHud, bool OnOthers, UUID Owner, UUID Group, NVector3 Pos, float Size, string? Name);

    /// <summary>Once a second, outside the lock (a busy region has thousands of prims): every prim in the regions we're
    /// in (and their neighbours) that has media, where it is and whose it is.</summary>
    List<Found>? Gather()
    {
        var c = _s.Client;
        var found = new List<Found>();
        if (c is not { Network.Connected: true }) return found;
        Simulator[] sims;
        lock (c.Network.Simulators) sims = c.Network.Simulators.ToArray();
        uint self = c.Self.LocalID;
        foreach (var sim in sims)
        {
            if (_s.World.Region(_s.GridKey, sim.Handle) is not { } w) continue;
            List<Primitive>? unnamed = null;
            foreach (var prim in w.Prims.Values)
            {
                if (string.IsNullOrEmpty(prim.MediaURL)) continue;
                var place = PlaceOf(w, prim, self, out var root);
                found.Add(new Found(sim.Handle, prim.LocalID, prim.ID, sim, prim.MediaURL, place));
                // the object's name, for the person and the assistant (asked of the region once, then every 5 s at most)
                if (place.Name == null && (unnamed ??= []).Count < 20) unnamed.Add(root);
            }
            if (unnamed != null) _ = _s.Objects.FetchAsync(w, unnamed, TimeSpan.FromSeconds(3), CancellationToken.None, sim: sim);
        }
        return found;
    }

    /// <summary>Under the lock: the scan's prims taken in; prims gone take their media with them.</summary>
    void Scan(List<Found> found)
    {
        var seen = new HashSet<(ulong, uint)>();
        foreach (var f in found)
        {
            var key = (f.Region, f.LocalId);
            seen.Add(key);
            if (!_prims.TryGetValue(key, out var pm)) _prims[key] = pm = new PrimMedia { Region = f.Region, LocalId = f.LocalId, Id = f.Id };
            pm.Sim = f.Sim;
            var (ver, by) = ParseVersion(f.MediaUrl);
            if (ver != pm.Version) { pm.Version = ver; pm.UpdatedBy = by; pm.Failures = 0; pm.RetryAt = 0; }
            Place(pm, f.Place);
        }
        foreach (var gone in _prims.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _prims.Remove(gone);
            foreach (var i in _items.Values.Where(i => i.Kind == MediaItemKind.Prim && (i.Region, i.LocalId) == gone).ToList()) Remove(i);
        }
    }

    /// <summary>A prim's place, owner, and whether it's worn (by the person, on a HUD, or by someone else).</summary>
    Placement PlaceOf(Sojourn.Core.World.RegionWorld w, Primitive prim, uint self, out Primitive root)
    {
        bool attached = _s.Objects.IsAttachment(w, prim, out var wearer, out root);
        bool mineFlag = ((_s.State.PrimFlagsOf(prim) | _s.State.PrimFlagsOf(root)) & PrimFlags.ObjectYouOwner) != 0;
        bool worn = attached && wearer?.LocalID == self;
        bool hud = worn && (int)root.PrimData.AttachmentPoint is >= 31 and <= 38;
        var pos = w.RegionPosition(prim);
        return new Placement(mineFlag || worn, hud, attached && !worn,
            prim.Properties?.OwnerID ?? root.Properties?.OwnerID ?? UUID.Zero, prim.Properties?.GroupID ?? root.Properties?.GroupID ?? UUID.Zero,
            new NVector3(pos.X, pos.Y, pos.Z), MathF.Max(prim.Scale.X, MathF.Max(prim.Scale.Y, prim.Scale.Z)), root.Properties?.Name ?? prim.Properties?.Name);
    }

    void Place(PrimMedia pm, Placement p)
    {
        pm.Place = p;
        foreach (var i in _items.Values)
            if (i.Kind == MediaItemKind.Prim && i.LocalId == pm.LocalId && i.Region == pm.Region) Place(i, p);
    }

    static void Place(MediaItem i, Placement p)
    {
        i.Mine = p.Mine;
        i.OnHud = p.OnHud;
        i.OnOthers = p.OnOthers;
        i.Owner = p.Owner;
        i.Group = p.Group;
        i.RegionPos = p.Pos;
        i.Size = p.Size;
        i.ObjectName = p.Name;
    }

    /// <summary>One ObjectMedia request at a time, a second apart: the nearest prim whose media changed first.</summary>
    void PumpFetches(long now)
    {
        if (now - _lastFetch < 1000 || _prims.Values.Any(p => p.InFlight)) return;
        var c = _s.Client;
        if (c is not { Network.Connected: true }) return;
        PrimMedia? best = null;
        double bestD = double.MaxValue;
        foreach (var pm in _prims.Values)
        {
            if (pm.Version <= pm.Fetched || pm.RetryAt > now || pm.Sim == null) continue;
            double d = 0;
            if (_haveEar && _s.World.Region(_s.GridKey, pm.Region)?.Prims.TryGetValue(pm.LocalId, out var prim) == true)
            {
                Utils.LongToUInts(pm.Region, out uint rx, out uint ry);
                var p = prim.Position;
                d = Math.Pow(rx + p.X - _ear.X, 2) + Math.Pow(ry + p.Y - _ear.Y, 2);
            }
            if (d < bestD) (best, bestD) = (pm, d);
        }
        if (best == null) return;
        _lastFetch = now;
        best.InFlight = true;
        var target = best;
        int version = best.Version;
        var sim = best.Sim!;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var (ok, ver, faces) = await c.Objects.RequestObjectMediaAsync(target.Id, sim, cts.Token);
                Post(() => Fetched(target, version, ok, ver, faces));
            }
            catch (Exception e)
            {
                Post(() => { target.InFlight = false; FetchFailed(target, e.Message); });
            }
        });
    }

    void FetchFailed(PrimMedia pm, string why)
    {
        pm.InFlight = false;
        pm.Failures++;
        // the viewer retries a few times, 5 s apart, then waits for the next change
        if (pm.Failures >= 4) { pm.Fetched = pm.Version; _log($"media: no media data for {pm.Id} ({why})"); }
        else pm.RetryAt = Environment.TickCount64 + 5000;
    }

    void Fetched(PrimMedia pm, int asked, bool ok, string? version, LMediaEntry[]? faces)
    {
        pm.InFlight = false;
        if (!_prims.ContainsKey((pm.Region, pm.LocalId))) return;
        if (!ok || faces == null) { FetchFailed(pm, "the region didn't answer"); return; }
        var (fetched, by) = ParseVersion(version ?? "");
        pm.Fetched = Math.Max(asked, fetched);
        bool fromMe = by != UUID.Zero && by == _s.Client?.Self.AgentID;
        var have = new HashSet<int>();
        for (int f = 0; f < faces.Length; f++)
        {
            if (faces[f] is not { } e) continue;
            have.Add(f);
            Update(pm, f, e, fromMe);
        }
        foreach (var i in _items.Values.Where(i => i.Kind == MediaItemKind.Prim && i.Region == pm.Region && i.LocalId == pm.LocalId && !have.Contains(i.Face)).ToList())
            Remove(i);
        pm.Entries = faces;
        if (pm.Place is { } place) Place(pm, place);
        Notify();
    }

    /// <summary>A face's entry from the region (LLViewerMedia::updateMediaImpl): a new URL is followed by a face that's
    /// loaded (unless this person changed it), and settles a face that would play by itself.</summary>
    void Update(PrimMedia pm, int face, LMediaEntry e, bool fromMe)
    {
        string key = PrimKey(pm.Region, pm.LocalId, face);
        if (!_items.TryGetValue(key, out var i)) _items[key] = i = new MediaItem { Key = key, Kind = MediaItemKind.Prim, Region = pm.Region, LocalId = pm.LocalId, PrimId = pm.Id, Face = face };
        string before = i.Url;
        var url = (e.CurrentURL ?? "").Trim();
        if (url.Length == 0) url = (e.HomeURL ?? "").Trim();
        if (url.Length > 0 && !url.Contains(':')) url = "https://" + url;   // as mediaNavigateBounceBack reads one without a scheme
        i.Url = url;
        i.Home = e.HomeURL ?? "";
        // a new size or auto-scale reopens a page at its new size (the viewer resizes its plugin)
        bool resized = i.Player is { Closed: false, Kind: MediaKind.Web } && (i.AutoScale != e.AutoScale || i.Width != e.Width || i.Height != e.Height);
        i.AutoPlayFlag = e.AutoPlay;
        i.Loop = e.AutoLoop;
        i.AutoScale = e.AutoScale;
        i.AutoZoom = e.AutoZoom;
        i.FirstClickInteract = e.InteractOnFirstClick;
        i.Width = e.Width;
        i.Height = e.Height;
        i.Interact = e.InteractPermissions;
        i.Control = e.ControlPermissions;
        var wl = e.EnableWhiteList && e.WhiteList is { Length: > 0 } list ? list.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() : null;
        bool allowChanged = !(wl ?? []).SequenceEqual(i.Whitelist ?? []);
        i.Whitelist = wl;
        if (resized) Unload(i);
        if (i.Player is { Closed: false } p)
        {
            if (allowChanged && p.Kind == MediaKind.Web) p.SetAllow(Allow(i));
            if (url.Length == 0) Unload(i);
            else if (url != before && !fromMe && url != i.PlayingUrl)
            {
                // the region's navigation: followed, not sent back
                if (p.Kind == MediaKind.Web && MediaUrls.KindOf(url, null) == MediaKind.Web)
                {
                    i.ServerNavUrl = url;
                    i.PlayingUrl = url;
                    p.Navigate(url);
                }
                else { Unload(i); i.ProbedUrl = null; }
            }
        }
        else if (url != before) { i.ProbedUrl = null; i.Refusal = null; }
    }

    void Remove(MediaItem i)
    {
        Unload(i);
        _items.Remove(i.Key);
        Notify();
    }

    void SendNavigate(MediaItem i, string url)
    {
        var c = _s.Client;
        Simulator? sim = null;
        if (c != null) lock (c.Network.Simulators) sim = c.Network.Simulators.FirstOrDefault(s => s.Handle == i.Region);
        if (c == null || sim == null) return;
        var id = i.PrimId;
        int face = i.Face;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await c.Objects.NavigateObjectMediaAsync(id, face, url, sim, cts.Token);
            }
            catch (Exception e) { _log($"media: navigate not sent ({e.Message})"); }
        });
    }

    // ---- the parcel ----------------------------------------------------------------------------------------------------

    Parcel? _agentParcel;
    ulong _agentParcelRegion;
    bool _agentParcelMine;
    ParcelMedia? _parcelOverride;   // a ParcelMediaUpdate for the parcel we're on
    int _parcelOverrideFor = -1;

    void ParcelTick()
    {
        var c = _s.Client;
        if (c?.Network.CurrentSim is not { } sim || sim.Handle == 0) { SetParcel(null, 0); return; }
        var pos = c.Self.SimPosition;
        Parcel? parcel = null;
        try
        {
            int local = c.Parcels.GetParcelLocalID(sim, pos);
            if (local != 0) sim.Parcels.TryGetValue(local, out parcel);
        }
        catch { }
        if (parcel != null && !ParcelContains(parcel, pos.X, pos.Y)) parcel = null;
        SetParcel(parcel, sim.Handle);
    }

    void SetParcel(Parcel? parcel, ulong region)
    {
        bool moved = (parcel?.LocalID ?? -1) != (_agentParcel?.LocalID ?? -1) || region != _agentParcelRegion;
        if (parcel == null && _agentParcel != null && region == _agentParcelRegion && !moved) return;
        _agentParcel = parcel;
        _agentParcelRegion = region;
        _agentParcelMine = parcel != null && _s.Client is { } c && parcel.OwnerID == c.Self.AgentID;
        if (moved) { _parcelOverride = null; _parcelOverrideFor = -1; }
        ParcelMedia? media = parcel == null ? null : _parcelOverrideFor == parcel.LocalID && _parcelOverride is { } o ? o : parcel.Media;
        string url = (media?.MediaURL ?? "").Trim();
        string type = (media?.MediaType ?? "").Trim();
        bool none = url.Length == 0 || type.Equals("none/none", StringComparison.OrdinalIgnoreCase);
        _items.TryGetValue("parcel", out var item);
        if (none)
        {
            if (item != null) Remove(item);
            return;
        }
        var m = media!.Value;
        bool fresh = item == null || moved || item.Url != url || item.Texture != m.MediaID || item.Mime != type;
        if (item != null && fresh) Remove(item);
        if (!fresh) return;
        // a new parcel or new media: as the viewer does, nothing carries over (started, stopped, the player)
        item = new MediaItem { Key = "parcel", Kind = MediaItemKind.Parcel, Region = region };
        item.Url = url;
        item.Texture = m.MediaID;
        item.Mime = type.Length > 0 ? type : null;
        item.Width = m.MediaWidth;
        item.Height = m.MediaHeight;
        item.Loop = m.MediaLoop;
        item.AutoScale = m.MediaAutoScale;
        item.Desc = m.MediaDesc;
        item.Owner = parcel!.OwnerID;
        item.ObjectName = parcel.Name;
        _items["parcel"] = item;
        Notify();
    }

    bool InAgentParcel(MediaItem i) =>
        _agentParcel is { } p && i.Region == _agentParcelRegion && ParcelContains(p, (i.Frame?.Centre ?? i.RegionPos).X, (i.Frame?.Centre ?? i.RegionPos).Y);

    static bool ParcelContains(Parcel p, float x, float y)
    {
        var bm = p.Bitmap;
        if (bm is not { Length: >= 512 }) return x >= p.AABBMin.X && x <= p.AABBMax.X && y >= p.AABBMin.Y && y <= p.AABBMax.Y;
        int bx = Math.Clamp((int)(x / 4), 0, 63), by = Math.Clamp((int)(y / 4), 0, 63);
        int bit = by * 64 + bx;
        return (bm[bit / 8] & (1 << (bit % 8))) != 0;
    }

    /// <summary>The region's parcel media changes (llParcelMediaCommandList and the like), for the parcel we're on.</summary>
    public void Hook(GridClient c)
    {
        c.Parcels.ParcelMediaUpdateReply += (_, e) => Post(() =>
        {
            if (_agentParcel == null || e.Simulator?.Handle != _agentParcelRegion) return;
            _parcelOverride = e.Media;
            _parcelOverrideFor = _agentParcel.LocalID;
            SetParcel(_agentParcel, _agentParcelRegion);
        });
        c.Parcels.ParcelMediaCommand += (_, e) => Post(() => ParcelCommand(e));
    }

    /// <summary>LLViewerParcelMedia::processParcelMediaCommandMessage: stop and unload stop it, pause pauses it, play and
    /// loop start it (from the start, looping), time seeks.</summary>
    void ParcelCommand(ParcelMediaCommandEventArgs e)
    {
        if (!_items.TryGetValue("parcel", out var i)) return;
        switch (e.MediaCommand)
        {
            case ParcelMediaCommand.Stop or ParcelMediaCommand.Unload:
                Unload(i);
                i.Started = false;
                i.Stopped = true;
                break;
            case ParcelMediaCommand.Pause:
                if (i.Player is { Kind: MediaKind.Video } pv) pv.Pause();
                break;
            case ParcelMediaCommand.Play or ParcelMediaCommand.Loop:
                i.Stopped = false;
                if (e.MediaCommand == ParcelMediaCommand.Loop) i.Loop = true;
                if (i.Player is { Kind: MediaKind.Video } pp) { pp.Seek(0); pp.Play(); }
                else if (AutoPlays(i) || i.Started) Start(i);   // a script's play doesn't override the person's choice
                break;
            case ParcelMediaCommand.Time:
                if (i.Player is { Kind: MediaKind.Video } pt) pt.Seek(e.Time * 1000);
                break;
        }
        Notify();
    }
}
