using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using LMediaEntry = LibreMetaverse.MediaEntry;

namespace Sojourn.Client.Media;

/// <summary>
/// A face's media settings, as the viewer's Media Settings floater edits them (llfloatermediasettings.cpp and its
/// General, Customize/Security and Permissions panels; llmediaentry.cpp for the limits, read 2026-10-29): home and
/// current URL, auto loop, play, scale and zoom, first click interact, size in pixels, the controls' style, who may
/// use and who may control it, the whitelist. Read on any face; changed (or media put on a face, or taken off) only
/// on objects the person may modify, through the region's ObjectMedia capability with every other face's entry as
/// the region has it, as LLVOVolume::sendMediaDataUpdate sends them. The confirmation gate asks before changing who
/// may use or control a face's media (or its whitelist) and before taking media off a face (MediaWiring's case).
/// Claude Opus 5.5, 2026-10-29 (batch B10).
/// </summary>
public sealed class MediaFaceSettings
{
    readonly AgentSession _s;
    readonly MediaWiring _w;
    readonly Func<JsonObject, MediaItem> _find;
    MediaManager M => _w.Manager;

    // llmediaentry.cpp
    const int MaxUrl = 1024, MaxPixels = 2048, MaxWhitelistCount = 64, MaxWhitelistChars = 1024;

    public MediaFaceSettings(AgentSession s, MediaWiring w, Func<JsonObject, MediaItem> find)
    {
        _s = s;
        _w = w;
        _find = find;
    }

    public const string Which = "object?: an object's id, face: its face number | media?: key, object id or words (as the other ops); ";

    static readonly string[] Keys =
    [
        "current_url", "home_url", "auto_play", "auto_loop", "auto_scale", "auto_zoom", "first_click_interact", "width", "height",
        "controls", "perms_interact", "perms_control", "whitelist_enable", "whitelist", "alt_image",
    ];

    public static bool ChangesPermissions(JsonObject a) => a.Has("perms_interact") || a.Has("perms_control") || a.Has("whitelist") || a.Has("whitelist_enable");

    sealed record Target(Simulator Sim, Primitive Prim, int Face, string Name, bool Mine, bool CanEdit);

    /// <summary>The prim and face meant: an object's id and a face number, or media named as the other ops name it.</summary>
    Target Resolve(JsonObject a)
    {
        var c = _s.Client ?? throw new NotConnected("viewer not connected");
        UUID id;
        int face;
        if (a.Uuid("object") is { } oid)
        {
            id = oid;
            face = a.Int("face", -1);
            if (face is < 0 or > 31) throw new LeapError("face: the face's number (0-31; the pointer's hit says which)");
        }
        else
        {
            var i = _find(a);
            if (i.Kind != MediaItemKind.Prim) throw new LeapError("that media isn't on an object's face (parcel media is set in About Land)");
            (id, face) = (i.PrimId, i.Face);
        }
        Simulator[] sims;
        lock (c.Network.Simulators) sims = c.Network.Simulators.ToArray();
        foreach (var sim in sims)
            if (_s.World.Region(_s.GridKey, sim.Handle)?.PrimById(id) is { } p)
            {
                var flags = _s.State.PrimFlagsOf(p);
                string name = p.Properties?.Name is { Length: > 0 } n ? n : "the object";
                bool mine = (flags & PrimFlags.ObjectYouOwner) != 0 || p.Properties?.OwnerID == c.Self.AgentID;
                return new Target(sim, p, face, name, mine, (flags & PrimFlags.ObjectModify) != 0);
            }
        throw new LeapError("that object isn't in view");
    }

    /// <summary>Every face's entry as the region has it (fetched by the media manager when it has them, else asked).</summary>
    async Task<LMediaEntry?[]> EntriesAsync(Target t, CancellationToken ct)
    {
        if (M.Locked(() => M.Fetched(t.Sim.Handle, t.Prim.LocalID)) is { } have) return have;
        var c = _s.Client ?? throw new NotConnected("viewer not connected");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var (ok, _, faces) = await c.Objects.RequestObjectMediaAsync(t.Prim.ID, t.Sim, cts.Token);
        if (!ok) throw new LeapError("the region didn't say what media the object has");
        return faces ?? [];
    }

    static string[] PermNames(MediaPermission p)
    {
        var l = new List<string>();
        if ((p & MediaPermission.Owner) != 0) l.Add("owner");
        if ((p & MediaPermission.Group) != 0) l.Add("group");
        if ((p & MediaPermission.Anyone) != 0) l.Add("anyone");
        return l.Count == 0 ? ["none"] : l.ToArray();
    }

    static JsonObject Json(LMediaEntry e, int face) => new()
    {
        ["face"] = face, ["has_media"] = true,
        ["current_url"] = e.CurrentURL, ["home_url"] = e.HomeURL,
        ["auto_play"] = e.AutoPlay, ["auto_loop"] = e.AutoLoop, ["auto_scale"] = e.AutoScale, ["auto_zoom"] = e.AutoZoom,
        ["first_click_interact"] = e.InteractOnFirstClick, ["width"] = e.Width, ["height"] = e.Height,
        ["controls"] = e.Controls == MediaControls.Mini ? "mini" : "standard",
        ["perms_interact"] = new JsonArray(PermNames(e.InteractPermissions).Select(x => (JsonNode)x).ToArray()),
        ["perms_control"] = new JsonArray(PermNames(e.ControlPermissions).Select(x => (JsonNode)x).ToArray()),
        ["whitelist_enable"] = e.EnableWhiteList, ["whitelist"] = new JsonArray((e.WhiteList ?? []).Select(x => (JsonNode)x).ToArray()),
        ["alt_image"] = e.EnableAlternativeImage,
    };

    public async Task<JsonNode?> ReadAsync(JsonObject a, CancellationToken ct)
    {
        var t = Resolve(a);
        var entries = await EntriesAsync(t, ct);
        var o = t.Face < entries.Length && entries[t.Face] is { } e ? Json(e, t.Face) : new JsonObject { ["face"] = t.Face, ["has_media"] = false };
        o["object"] = t.Name;
        o["object_id"] = t.Prim.ID.ToString();
        o["can_edit"] = t.CanEdit;
        o["faces_with_media"] = new JsonArray(entries.Select((x, i) => (x, i)).Where(p => p.x != null).Select(p => (JsonNode)p.i).ToArray());
        return o;
    }

    static MediaPermission Perms(JsonNode? v, string key)
    {
        var words = v switch
        {
            JsonArray arr => arr.Select(x => x?.ToString() ?? "").ToArray(),
            JsonValue s when s.TryGetValue<string>(out var str) => str.Split([',', ' ', '+'], StringSplitOptions.RemoveEmptyEntries),
            _ => throw new LeapError($"{key}: a list of owner, group, anyone (or none)"),
        };
        var p = MediaPermission.None;
        foreach (var w in words.Select(x => x.Trim().ToLowerInvariant()))
            p |= w switch
            {
                "owner" => MediaPermission.Owner, "group" => MediaPermission.Group, "anyone" or "everyone" or "all" => MediaPermission.Anyone,
                "none" or "nobody" => MediaPermission.None,
                _ => throw new LeapError($"{key}: owner, group, anyone or none, not \"{w}\""),
            };
        return p;
    }

    static string Url(JsonObject a, string key)
    {
        var u = (a.Str(key) ?? "").Trim();
        if (u.Length > MaxUrl) throw new LeapError($"{key} is longer than {MaxUrl} characters");
        if (u.Length > 0 && !u.Contains(':')) u = "https://" + u;
        if (u.Length > 0 && !Uri.TryCreate(u, UriKind.Absolute, out _)) throw new LeapError($"{key} isn't an address");
        return u;
    }

    /// <summary>The entry the op would leave on the face: the current one (or the viewer's defaults for a face without
    /// media) with the op's changes, checked against llmediaentry's limits.</summary>
    static LMediaEntry Changed(LMediaEntry? was, JsonObject a)
    {
        var e = was is null
            ? new LMediaEntry { InteractPermissions = MediaPermission.All, ControlPermissions = MediaPermission.All, Controls = MediaControls.Standard }
            : LMediaEntry.FromOSD(was.GetOSD());
        if (a.Has("current_url")) e.CurrentURL = Url(a, "current_url");
        if (a.Has("home_url")) e.HomeURL = Url(a, "home_url");
        if (a.Bool("auto_play") is { } ap) e.AutoPlay = ap;
        if (a.Bool("auto_loop") is { } al) e.AutoLoop = al;
        if (a.Bool("auto_scale") is { } asc) e.AutoScale = asc;
        if (a.Bool("auto_zoom") is { } az) e.AutoZoom = az;
        if (a.Bool("first_click_interact") is { } fc) e.InteractOnFirstClick = fc;
        if (a.Bool("alt_image") is { } alt) e.EnableAlternativeImage = alt;
        if (a.Has("width")) e.Width = a.Int("width", -1) is var w and >= 0 and <= MaxPixels ? w : throw new LeapError($"width: 0 (as it comes) to {MaxPixels} pixels");
        if (a.Has("height")) e.Height = a.Int("height", -1) is var h and >= 0 and <= MaxPixels ? h : throw new LeapError($"height: 0 (as it comes) to {MaxPixels} pixels");
        if (a.Str("controls") is { } ctl)
            e.Controls = ctl.Trim().ToLowerInvariant() switch
            {
                "standard" => MediaControls.Standard, "mini" => MediaControls.Mini, _ => throw new LeapError("controls: standard or mini"),
            };
        if (a.Has("perms_interact")) e.InteractPermissions = Perms(a["perms_interact"], "perms_interact");
        if (a.Has("perms_control")) e.ControlPermissions = Perms(a["perms_control"], "perms_control");
        if (a.Bool("whitelist_enable") is { } wle) e.EnableWhiteList = wle;
        if (a.Has("whitelist"))
        {
            var list = a["whitelist"] switch
            {
                JsonArray arr => arr.Select(x => x?.ToString().Trim() ?? "").Where(x => x.Length > 0).ToArray(),
                JsonValue s when s.TryGetValue<string>(out var str) => str.Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries),
                _ => throw new LeapError("whitelist: a list of sites (example.com, *.example.com/path)"),
            };
            if (list.Length > MaxWhitelistCount) throw new LeapError($"a whitelist holds at most {MaxWhitelistCount} entries");
            if (list.Sum(x => x.Length) > MaxWhitelistChars) throw new LeapError($"a whitelist holds at most {MaxWhitelistChars} characters");
            e.WhiteList = list;
        }
        if (e.CurrentURL.Length == 0 && e.HomeURL.Length == 0) throw new LeapError("a face's media needs current_url or home_url");
        // the viewer's general panel won't take an address its own whitelist refuses
        if (e.EnableWhiteList && e.WhiteList.Length > 0)
            foreach (var u in new[] { e.HomeURL, e.CurrentURL }.Where(x => x.Length > 0))
                if (!MediaUrls.WhitelistAllows(e.WhiteList, u)) throw new LeapError($"{u} isn't on the face's whitelist");
        return e;
    }

    public async Task<JsonNode?> ConfigureAsync(JsonObject a, CancellationToken ct)
    {
        if (!Keys.Any(a.Has)) throw new LeapError("nothing to change: " + string.Join(", ", Keys));
        var t = Resolve(a);
        if (!t.CanEdit) throw new LeapError($"the person can't modify {t.Name}");
        var entries = await EntriesAsync(t, ct);
        var send = new LMediaEntry?[Math.Max(entries.Length, t.Face + 1)];
        Array.Copy(entries, send, entries.Length);
        bool added = send[t.Face] is null;
        send[t.Face] = Changed(send[t.Face], a);
        await SendAsync(t, send, ct);
        var after = await EntriesAfterAsync(t, ct);
        bool seen = after is { } af && t.Face < af.Length && af[t.Face] is { } e && e.CurrentURL == send[t.Face]!.CurrentURL
                    && e.InteractPermissions == send[t.Face]!.InteractPermissions && e.ControlPermissions == send[t.Face]!.ControlPermissions;
        var o = Json(send[t.Face]!, t.Face);
        o["object"] = t.Name;
        o["added"] = added;
        o["confirmed_by_region"] = seen;
        return o;
    }

    public async Task<JsonNode?> RemoveAsync(JsonObject a, CancellationToken ct)
    {
        var t = Resolve(a);
        if (!t.CanEdit) throw new LeapError($"the person can't modify {t.Name}");
        var entries = await EntriesAsync(t, ct);
        if (t.Face >= entries.Length || entries[t.Face] is null) throw new LeapError($"face {t.Face} of {t.Name} has no media");
        var send = (LMediaEntry?[])entries.Clone();
        send[t.Face] = null;
        await SendAsync(t, send, ct);
        var after = await EntriesAfterAsync(t, ct);
        return new JsonObject
        {
            ["object"] = t.Name, ["face"] = t.Face, ["removed"] = true,
            ["confirmed_by_region"] = after is { } af && (t.Face >= af.Length || af[t.Face] is null),
        };
    }

    async Task SendAsync(Target t, LMediaEntry?[] faces, CancellationToken ct)
    {
        var c = _s.Client ?? throw new NotConnected("viewer not connected");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        await c.Objects.UpdateObjectMediaAsync(t.Prim.ID, faces!, t.Sim, cts.Token);
    }

    /// <summary>What the region has after a change (asked again a moment later), or null if it didn't answer.</summary>
    async Task<LMediaEntry?[]?> EntriesAfterAsync(Target t, CancellationToken ct)
    {
        if (_s.Client is not { } c) return null;
        await Task.Delay(1200, ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var (ok, _, faces) = await c.Objects.RequestObjectMediaAsync(t.Prim.ID, t.Sim, cts.Token);
            return ok ? faces ?? [] : null;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
    }

    /// <summary>What the person is asked (MediaWiring's confirmation case): who may use or control it, its whitelist,
    /// before and after; or the media that will be taken off.</summary>
    public async Task<(string Title, string Detail)> DescribeAsync(string op, JsonObject a)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Target t;
        LMediaEntry? was;
        try
        {
            t = Resolve(a);
            var entries = await EntriesAsync(t, cts.Token);
            was = t.Face < entries.Length ? entries[t.Face] : null;
            // pinned: the op acts on the face the person was shown
            a["object"] = t.Prim.ID.ToString();
            a["face"] = t.Face;
            a.Remove("media");
        }
        catch (Exception e) when (e is LeapError or NotConnected or OperationCanceledException)
        {
            return (op == "remove_media" ? "Take media off a face" : "Change who may use a face's media", $"{e.Message}: this will likely fail.");
        }
        string where = $"{t.Name}, face {t.Face}{(t.Mine ? "" : " (not the person's own object)")}";
        if (op == "remove_media")
            return ($"Take the media off {where}", was is null ? "It has none." : $"It shows {Clip(was.CurrentURL.Length > 0 ? was.CurrentURL : was.HomeURL)}; everyone sees it go.");
        LMediaEntry now;
        try { now = Changed(was, a); }
        catch (LeapError e) { return ($"Change the media on {where}", $"{e.Message}: this will fail."); }
        var lines = new List<string>();
        string P(MediaPermission p) => string.Join(", ", PermNames(p));
        if (a.Has("perms_interact")) lines.Add($"Who may use it: {(was is null ? "" : P(was.InteractPermissions) + " -> ")}{P(now.InteractPermissions)}.");
        if (a.Has("perms_control")) lines.Add($"Who may change what it shows: {(was is null ? "" : P(was.ControlPermissions) + " -> ")}{P(now.ControlPermissions)}.");
        if (a.Has("whitelist_enable") || a.Has("whitelist"))
            lines.Add($"Whitelist {(now.EnableWhiteList ? "on" : "off")}{(now.WhiteList.Length > 0 ? ": " + Clip(string.Join(", ", now.WhiteList)) : "")}.");
        if (was is null) lines.Add($"New media on this face: {Clip(now.CurrentURL.Length > 0 ? now.CurrentURL : now.HomeURL)}.");
        return ($"Change who may use the media on {where}", string.Join(" ", lines) + " Everyone nearby is affected.");
    }

    static string Clip(string s, int n = 80) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
