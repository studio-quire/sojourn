using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using SkiaSharp;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Core.World;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;
using Vector3 = System.Numerics.Vector3;
using LVector3 = LibreMetaverse.Vector3;

namespace Sojourn.Session.Offscreen;

/// <summary>
/// SojournDescribe (docs/access.md > M5): what the assistant needs to describe something to a person who can't see it.
/// Pictures aimed at the subject rather than wherever the avatar faces (a person framed whole and close, from their
/// front; an object framed from the person's side, a sign or poster face-on; here, four views round the person), each
/// with what's visible in it by name and where (a grid of picks through the picture), plus the facts the lists know
/// (names, descriptions, floating text, owners, what someone wears by its items' names, bearings from the person), so
/// the model names things rather than guessing who or what they are. Textures and profile pictures fetched and handed
/// over as they are. The renderer out of sight draws the pictures; without one, textures and facts still come.
/// Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class DescribeApi : LeapApi
{
    readonly OffscreenRenderer? _view;

    public override string Name => "SojournDescribe";
    public override string Desc => "what describing needs: pictures aimed at a person, an object, the person themselves or around them, with what's visible in each by name; textures and profile pictures";

    public DescribeApi(AgentSession s, OffscreenRenderer? view) : base(s)
    {
        _view = view;
        Op("look", "{subject: here | me | an avatar's or object's id, detail: brief | rich, pictures=true (false: only what the lists know)} -> {subject: what the lists know of it, "
            + "pictures: [{label, png, width, height, seen: [{name, kind, in_picture, share, where (from the person), look? (what of it hadn't loaded)}], subject_look? (what of the subject's look had loaded)}], "
            + "around?: (here) people and objects by name and bearing, notes?}: pictures framed on the subject (a person from "
            + "their front, whole and close; an object from the person's side, a flat one face-on; here: ahead, right, behind, left)",
            LookAsync);
        Op("texture", "{id, max=1024} -> {png, width, height}: a texture as uploaded (a poster's or sign's image, a snapshot)", TextureAsync);
        Op("profile", "{id} -> {name, pictures: [{label, png}], about}: a resident's profile pictures", ProfileAsync);
    }

    // --- prepared ahead: the app asks as the request goes to the model, so the tool's call finds it drawn -----------

    readonly object _lock = new();
    (string Key, Task<JsonObject> Task, long At)? _prepared;

    /// <summary>Start drawing what a look will want (Sojourn Access calls this as it sends the request); a look with the
    /// same subject and detail within 30 s takes it.</summary>
    public void Prepare(string subject, string detail)
    {
        subject = subject.Trim().ToLowerInvariant();
        var key = $"{subject}|{detail}";
        lock (_lock)
        {
            if (_prepared is { } p && p.Key == key && Environment.TickCount64 - p.At < 5000) return;
            _prepared = (key, Task.Run(() => DoLookAsync(subject, detail, true, CancellationToken.None)), Environment.TickCount64);
        }
    }

    async Task<JsonNode?> LookAsync(JsonObject a, CancellationToken ct)
    {
        var subject = (a.Str("subject") ?? "here").Trim().ToLowerInvariant();
        bool pictures = a.Bool("pictures", true);
        var detail = a.Str("detail") is "rich" or "more" or "detailed" ? "rich" : "brief";
        Task<JsonObject>? ready = null;
        lock (_lock)
            if (pictures && _prepared is { } p && p.Key == $"{subject}|{detail}" && Environment.TickCount64 - p.At < 30_000)
            {
                ready = p.Task;
                _prepared = null;
            }
        return ready != null ? await ready.WaitAsync(ct) : await DoLookAsync(subject, detail, pictures, ct);
    }

    async Task<JsonObject> DoLookAsync(string subject, string detail, bool shoot, CancellationToken ct)
    {
        var c = S.Client ?? throw new NotConnected("not logged in");
        var w = S.Region ?? throw new LeapError("not in a region");
        bool rich = detail == "rich";
        var o = new JsonObject { ["detail"] = detail };
        var notes = new JsonArray();
        Func<FrameRenderer, Simulator, IReadOnlyList<ShotSpec>?> plan;
        UUID texturePrim = UUID.Zero;
        int textureFace = -1;
        var which = subject.ToLowerInvariant() switch
        {
            "here" or "around" or "" => "here",
            "me" or "self" or "myself" => "me",
            _ => UUID.TryParse(subject, out var given) && given == c.Self.AgentID ? "me" : subject,
        };
        switch (which)
        {
            case "here":
                o["subject"] = await HereAsync(c, w, ct);
                plan = (r, sim) => HerePlan(r, sim, rich);
                break;
            case "me":
                o["subject"] = await MeAsync(c, w, ct);
                plan = (r, sim) => AvatarPlan(r, sim, c.Self.AgentID, self: true, rich, notes);
                break;
            default:
                if (!UUID.TryParse(subject, out var id)) throw new LeapError("subject: here, me, or an avatar's or object's id");
                if (w.AvatarById(id) is { } av)
                {
                    o["subject"] = await PersonAsync(c, w, av, ct);
                    plan = (r, sim) => AvatarPlan(r, sim, id, self: false, rich, notes);
                }
                else if (Hud.Huds.Mine(S, id, out var hudRoot) && hudRoot != null)
                    return await HudLookAsync(c, w, hudRoot, detail, shoot, ct);
                else if (w.PrimById(id) is { } prim)
                {
                    o["subject"] = await ObjectAsync(c, w, prim, ct);
                    plan = (r, sim) =>
                    {
                        var shots = ObjectPlan(r, sim, w, prim, rich, notes, out var tp, out var tf);
                        (texturePrim, textureFace) = (tp, tf);
                        return shots;
                    };
                }
                else if (S.Names.Has(id) || c.Friends.FriendList.ContainsKey(id))
                    throw new LeapError($"{S.Names.Complete(id, "they")} isn't in view here (too far away, or in another region): describe their profile picture instead, or go closer");
                else throw new LeapError("nothing with that id is in view here");
                break;
        }
        var pictures = new JsonArray();
        if (!shoot) { }   // only what the lists know (no assistant to look)
        else if (_view is not { Running: true })
            notes.Add(_view == null ? "this app draws no view (started without its renderer): no pictures, only what the lists know"
                : $"the view isn't running ({_view.State}): no pictures, only what the lists know");
        else if (await _view.ShootAsync(plan, rich ? 4 : 3, ct) is { } batch)
        {
            var names = await SeenNamesAsync(c, w, batch, ct);
            foreach (var shot in batch.Shots) pictures.Add(Picture(c, w, shot, names));
        }
        else notes.Add("the pictures couldn't be drawn just now");
        // a flat thing's face as uploaded: sharper text than any picture of it
        if (shoot && texturePrim != UUID.Zero && textureFace >= 0 && w.PrimById(texturePrim) is { } tp2
            && tp2.Textures?.GetFace((uint)textureFace)?.TextureID is { } tex && !Plain(tex))
        {
            try
            {
                using var bmp = await S.InventoryMeta.TextureBitmapAsync(tex, rich ? 1024 : 768, ct);
                pictures.Add(new JsonObject
                {
                    ["label"] = "the image on its front face as uploaded (flat, may be cropped, repeated or partly shown on the object; for reading text)",
                    ["png"] = Convert.ToBase64String(InventoryMeta.Png(bmp)), ["width"] = bmp.Width, ["height"] = bmp.Height,
                });
            }
            catch (LeapError e) { notes.Add($"its face's image didn't come: {e.Message}"); }
        }
        o["pictures"] = pictures;
        if (notes.Count > 0) o["notes"] = notes;
        return o;
    }

    static readonly HashSet<UUID> PlainTextures = new()
    {
        new UUID("89556747-24cb-43ed-920b-47caed15465f"),   // plywood
        new UUID("5748decc-f629-461c-9a36-a35a221fe21f"),   // blank
        new UUID("8dcd4a48-2d37-4909-9f78-f7a9eb4ef903"),   // transparent
        new UUID("8b5fec65-8d8d-9dc5-cda8-8fdf2716e361"),   // media
    };

    static bool Plain(UUID t) => t == UUID.Zero || PlainTextures.Contains(t);

    // --- where to put the camera -------------------------------------------------------------------------------------

    const float Deg = MathF.PI / 180;

    /// <summary>Four views round the person from their eyes: ahead, right, behind, left, each 90 degrees across.</summary>
    IReadOnlyList<ShotSpec>? HerePlan(FrameRenderer r, Simulator sim, bool rich)
    {
        var c = sim.Client;
        var me = S.Motion.Position(c);
        var eye = new Vector3(me.X, me.Y, me.Z + 0.72f);
        var f = Facing(c.Self.SittingOn != 0 ? c.Self.SimRotation : c.Self.Movement.BodyRotation);
        int w = rich ? 1024 : 768, h = w * 9 / 16;
        float vfov = 2 * MathF.Atan(MathF.Tan(45 * Deg) * h / w);
        var labels = new[] { "ahead of them (twelve o'clock)", "to their right (three o'clock)", "behind them (six o'clock)", "to their left (nine o'clock)" };
        var shots = new List<ShotSpec>();
        for (int i = 0; i < 4; i++)
        {
            float yaw = -i * MathF.PI / 2;   // clockwise seen from above: right of facing first
            var d = new Vector3(f.X * MathF.Cos(yaw) - f.Y * MathF.Sin(yaw), f.X * MathF.Sin(yaw) + f.Y * MathF.Cos(yaw), 0);
            shots.Add(new ShotSpec(labels[i], eye, Vector3.Normalize(d + new Vector3(0, 0, -0.12f)), vfov, w, h));
        }
        return shots;
    }

    /// <summary>A person (or the person themselves) framed from their front, whole and from the waist up; rich adds
    /// their back. Others are left out of these pictures (they'd stand in the way).</summary>
    IReadOnlyList<ShotSpec>? AvatarPlan(FrameRenderer r, Simulator sim, UUID id, bool self, bool rich, JsonArray notes)
    {
        var w = S.World.Region(S.GridKey, sim.Handle);
        if (w?.AvatarById(id) is not { } av) return null;
        var (centre, rot) = AvatarPose(r, sim, w, av);
        float height = av.Scale.Z is > 0.9f and < 3.5f ? av.Scale.Z : 1.9f;
        var f = Facing(rot);
        var others = w.Avatars.Values.Where(a => a.ID != id).Select(a => a.ID).ToHashSet();
        if (!self) others.Add(sim.Client.Self.AgentID);
        bool Theirs(Primitive p) => p.ParentID == av.LocalID || (w.Prims.TryGetValue(p.ParentID, out var root) && root.ParentID == av.LocalID);
        var shots = new List<ShotSpec>();
        void Add(string label, Vector3 target, Vector3 dir, float halfH, int pw, int ph, float vfov)
        {
            var (pos, fwd, fov, note) = Place(r, sim.Handle, target, dir, halfH, (float)pw / ph, vfov, p => !Theirs(p));
            if (note != null) notes.Add($"{label}: {note}");
            shots.Add(new ShotSpec(label, pos, fwd, fov, pw, ph) { ShowSelf = self, Hide = others, Subject = id, Daylight = true });
        }
        var whose = self ? "them" : "this person";
        Add($"{whose}, whole, from the front", centre + new Vector3(0, 0, 0.02f * height), f, height * 0.56f, 640, 960, 40 * Deg);
        Add($"{whose}, from the waist up, from the front", centre + new Vector3(0, 0, 0.25f * height), f, height * 0.34f, 768, 768, 40 * Deg);
        if (rich) Add($"{whose}, whole, from behind", centre + new Vector3(0, 0, 0.02f * height), -f, height * 0.56f, 640, 960, 40 * Deg);
        return shots;
    }

    (Vector3 Centre, Quaternion Rot) AvatarPose(FrameRenderer r, Simulator sim, RegionWorld w, Avatar av)
    {
        if (av.ParentID != 0 && w.Prims.TryGetValue(av.ParentID, out var seat))
        {
            var sr = w.RegionRotation(seat);
            var sp = r.DrawnPosition(sim.Handle, seat.LocalID) is { } d ? new LVector3(d.X, d.Y, d.Z) : w.RegionPosition(seat);
            var p = sp + av.Position * sr;
            return (new Vector3(p.X, p.Y, p.Z), av.Rotation * sr);
        }
        var raw = new Vector3(av.Position.X, av.Position.Y, av.Position.Z);
        if (av.ID == sim.Client.Self.AgentID)
        {
            var me = S.Motion.Position(sim.Client);
            return (new Vector3(me.X, me.Y, me.Z), sim.Client.Self.Movement.BodyRotation);
        }
        var at = r.Motion.Position(av.ID, sim.Handle, raw, new Vector3(av.Velocity.X, av.Velocity.Y, av.Velocity.Z));
        return (at, av.Rotation);
    }

    /// <summary>An object framed from the person's side (a little above), a flat one face-on; rich adds a second view a
    /// quarter of the way round (not for a flat one). texturePrim/face: for a flat one, the part and face its middle shows.</summary>
    IReadOnlyList<ShotSpec>? ObjectPlan(FrameRenderer r, Simulator sim, RegionWorld w, Primitive prim, bool rich, JsonArray notes,
        out UUID texturePrim, out int textureFace)
    {
        texturePrim = UUID.Zero;
        textureFace = -1;
        bool worn = S.Objects.IsAttachment(w, prim, out var wearer, out var root);
        if (!worn) root = S.Objects.RootOf(w, prim);
        var set = S.Objects.Linkset(w, root);
        var ids = set.Select(p => p.LocalID).ToHashSet();
        var c = sim.Client;
        // what those sitting on it wear goes with it (a camera from outside a vehicle the person drives isn't stopped by
        // their own hair): attachments' roots and their parts
        var riders = w.Avatars.Values.Where(a => a.ParentID != 0 && ids.Contains(a.ParentID)).Select(a => a.LocalID).ToHashSet();
        if (riders.Count > 0)
        {
            var theirs = w.Prims.Values.Where(p => riders.Contains(p.ParentID)).Select(p => p.LocalID).ToHashSet();
            ids.UnionWith(theirs);
            ids.UnionWith(w.Prims.Values.Where(p => theirs.Contains(p.ParentID)).Select(p => p.LocalID));
        }
        bool Mine(Primitive p) => ids.Contains(p.LocalID);
        bool onIt = SeatedOn(c, set);
        var me = S.Motion.Position(c);
        var eye = new Vector3(me.X, me.Y, me.Z + 0.72f);
        Vector3 centre, normal = Vector3.Zero;
        Vector3 half;   // half extents along the frame's axes: across, depth, up
        bool flat = false;   // a sign, poster or board: seen face-on, the picture shaped to it
        if (worn)
        {
            // attachments: drawn on their wearer (their own positions are relative to a joint), a sphere around the parts
            var pts = set.Select(p => (P: r.DrawnPosition(sim.Handle, p.LocalID), S: p.Scale)).Where(x => x.P != null).ToList();
            if (pts.Count == 0) { notes.Add("it isn't drawn yet"); return null; }
            centre = pts.Aggregate(Vector3.Zero, (a, x) => a + x.P!.Value) / pts.Count;
            var cc = centre;
            float rad = pts.Max(x => Vector3.Distance(x.P!.Value, cc) + new Vector3(x.S.X, x.S.Y, x.S.Z).Length() / 2);
            half = new Vector3(rad, rad, rad);
            var (_, wr) = wearer is { } wa ? AvatarPose(r, sim, w, wa) : (centre, Quaternion.Identity);
            normal = Facing(wr);   // from the wearer's front
        }
        else
        {
            // the linkset's box in the root's own frame, so a rotated sign is still thin along one axis
            var rr = w.RegionRotation(root);
            var rp = w.RegionPosition(root);
            var inv = Quaternion.Inverse(rr);
            var lo = new LVector3(float.MaxValue); var hi = new LVector3(float.MinValue);
            foreach (var p in set)
            {
                var pr = w.RegionRotation(p);
                var pp = w.RegionPosition(p);
                var s = p.Scale / 2;
                for (int k = 0; k < 8; k++)
                {
                    var corner = new LVector3((k & 1) == 0 ? -s.X : s.X, (k & 2) == 0 ? -s.Y : s.Y, (k & 4) == 0 ? -s.Z : s.Z);
                    var local = (pp + corner * pr - rp) * inv;
                    lo = LVector3.Min(lo, local);
                    hi = LVector3.Max(hi, local);
                }
            }
            var mid = (lo + hi) / 2;
            var cr = rp + mid * rr;
            centre = new Vector3(cr.X, cr.Y, cr.Z);
            var ext = hi - lo;
            var axes = new[] { (E: ext.X, A: LVector3.UnitX * rr), (E: ext.Y, A: LVector3.UnitY * rr), (E: ext.Z, A: LVector3.UnitZ * rr) }
                .OrderBy(x => x.E).ToArray();
            flat = axes[0].E < 0.2f * axes[1].E && axes[1].E > 0.15f;
            if (flat)
            {
                var n = new Vector3(axes[0].A.X, axes[0].A.Y, axes[0].A.Z);
                if (Vector3.Dot(n, eye - centre) < 0) n = -n;
                normal = n;
                // across and up on the face: the face's axis nearest upright is up
                var a1 = new Vector3(axes[1].A.X, axes[1].A.Y, axes[1].A.Z);
                var a2 = new Vector3(axes[2].A.X, axes[2].A.Y, axes[2].A.Z);
                bool a2Up = MathF.Abs(a2.Z) >= MathF.Abs(a1.Z);
                var (eUp, eAcross) = a2Up ? (axes[2].E, axes[1].E) : (axes[1].E, axes[2].E);
                if (MathF.Abs(n.Z) > 0.8f) (eUp, eAcross) = (axes[1].E, axes[2].E);   // lying flat: seen from above
                half = new Vector3(eAcross / 2, axes[0].E / 2, eUp / 2);
            }
            else
            {
                float rad = new Vector3(ext.X, ext.Y, ext.Z).Length() / 2;
                half = new Vector3(rad, rad, rad);
            }
        }
        if (normal == Vector3.Zero && onIt)
        {
            // the person is on it: from outside, ahead and to the right of the way they sit (a vehicle's front three
            // quarters), a little above, never from their own eyes inside it
            var q = w.Prims.TryGetValue(c.Self.SittingOn, out var seat) ? c.Self.SimRotation * w.RegionRotation(seat) : c.Self.SimRotation;   // (the seat's frame)
            var f = Facing(q);
            var right = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitZ));
            normal = Vector3.Normalize(f * 0.6f + right * 0.8f + new Vector3(0, 0, 0.45f));
        }
        if (normal == Vector3.Zero)
        {
            var toMe = eye - centre;
            toMe.Z = 0;
            var flatDir = toMe.LengthSquared() > 1e-4f ? Vector3.Normalize(toMe) : -Facing(c.Self.Movement.BodyRotation);
            normal = Vector3.Normalize(flatDir + new Vector3(0, 0, 0.35f));   // about 20 degrees above
        }
        int pw = 1024, ph = 768;
        if (flat)
        {
            // a flat thing: the picture's shape follows the face's
            float aspect = Math.Clamp(half.X / Math.Max(half.Z, 0.01f), 0.6f, 1.8f);
            (pw, ph) = aspect >= 1 ? (1024, (int)(1024 / aspect)) : ((int)(1024 * aspect), 1024);
        }
        float vfov = 45 * Deg;
        // the half height that has to fit: for a sphere its radius; for a face, its height or its width at this shape
        float halfFit = MathF.Max(half.Z, half.X * ph / pw) * 1.12f;
        var shots = new List<ShotSpec>();
        var label = worn ? "it, as worn, from the wearer's front" : flat ? "it, face-on" : "it, from the person's side";
        var (pos, fwd, fov, note) = Place(r, sim.Handle, centre, normal, halfFit, (float)pw / ph, vfov, p => !Mine(p));
        if (note != null) notes.Add(note);
        shots.Add(new ShotSpec(label, pos, fwd, fov, pw, ph) { Subject = root.ID, Daylight = true });
        // which part and face is in the middle of the face-on view (for its image as uploaded)
        if (flat && r.PickPrims(sim.Handle, pos, fwd, Vector3.Distance(pos, centre) + 2, Mine) is { Surface: { } surf } hit)
        {
            texturePrim = hit.Id;
            textureFace = surf.Face;
        }
        // (a flat thing's side says nothing: its rich look is the face-on picture and its image as uploaded at full size)
        if (rich && !flat)
        {
            var side = Vector3.Normalize(Vector3.Transform(normal, System.Numerics.Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 70 * Deg)));
            var (p2, f2, fov2, note2) = Place(r, sim.Handle, centre, side, MathF.Max(half.Z, MathF.Max(half.X, half.Y)) * 1.12f, 4f / 3, vfov, p => !Mine(p));
            if (note2 != null) notes.Add($"the second view: {note2}");
            shots.Add(new ShotSpec("it, from another side", p2, f2, fov2, 1024, 768) { Subject = root.ID, Daylight = true });
        }
        return shots;
    }

    /// <summary>A camera looking at <paramref name="target"/> from along <paramref name="dir"/>, far enough for
    /// <paramref name="halfH"/> to fit the height; if something stands in the way it comes closer (wider, to keep the
    /// fit), and if that's too close it tries other sides. Render thread.</summary>
    (Vector3 Pos, Vector3 Fwd, float Fov, string? Note) Place(FrameRenderer r, ulong region, Vector3 target, Vector3 dir, float halfH,
        float aspect, float vfov, Func<Primitive, bool> blocks)
    {
        // fit the height, and the width at this shape
        float d = Math.Clamp(halfH / MathF.Tan(vfov / 2), 0.5f, 60f);
        dir = Vector3.Normalize(dir);
        (Vector3 Dir, float Clear, string? Why) best = (dir, -1, null);
        var tries = new (float Yaw, string? Why)[] { (0, null), (40, "from a little to one side"), (-40, "from a little to the other side"),
            (90, "from the side"), (-90, "from the other side"), (180, "from the far side") };
        foreach (var (yaw, why) in tries)
        {
            var dd = yaw == 0 ? dir : Vector3.Normalize(Vector3.Transform(dir, System.Numerics.Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw * Deg)));
            var hit = r.PickPrims(region, target, dd, d, blocks);
            float clear = hit is { } h ? h.Distance : d;
            if (clear > best.Clear) best = (dd, clear, why);
            if (clear >= 0.85f * d) break;
        }
        float at = best.Clear >= d ? d : Math.Max(0.35f, best.Clear - 0.15f);
        float fov = at >= d ? vfov : Math.Min(2 * MathF.Atan(halfH / at), 110 * Deg);
        var pos = target + best.Dir * at;
        string? note = best.Why;
        if (at < d)
        {
            var cut = 2 * MathF.Atan(halfH / at) > 110 * Deg;
            note = (note != null ? note + "; " : "") + (cut ? "something close behind the camera: the picture is cropped and wide-angle" : "something in the way: taken closer, wider");
        }
        return (pos, Vector3.Normalize(target - pos), fov, note);
    }

    static Vector3 Facing(Quaternion q)
    {
        var x = LVector3.UnitX * q;
        var f = new Vector3(x.X, x.Y, 0);
        return f.LengthSquared() > 1e-6f ? Vector3.Normalize(f) : Vector3.UnitX;
    }

    // --- what's visible, in words --------------------------------------------------------------------------------------

    async Task<Dictionary<UUID, string>> SeenNamesAsync(GridClient c, RegionWorld w, ShotBatch batch, CancellationToken ct)
    {
        var names = new Dictionary<UUID, string>();
        var objs = batch.Shots.SelectMany(s => s.Seen).Where(s => s.Kind == PickKind.Object).Select(s => s.Id).Distinct()
            .Select(id => w.PrimById(id)).OfType<Primitive>().ToList();
        if (objs.Count > 0) await S.Objects.FetchAsync(w, objs, TimeSpan.FromSeconds(2), ct);
        var people = batch.Shots.SelectMany(s => s.Seen).Where(s => s.Kind == PickKind.Avatar).Select(s => s.Id).Distinct().ToList();
        if (people.Any(id => !S.Names.Has(id))) await S.Names.ResolveAsync(c, people, TimeSpan.FromSeconds(2), ct);
        foreach (var p in objs) names[p.ID] = ObjectName(p);
        foreach (var id in people) names[id] = S.Names.Complete(id, w.AvatarById(id)?.Name is { Length: > 0 } n ? n : "someone");
        return names;
    }

    /// <summary>Someone by name, but the person themselves as that (the describer says "you", never their own name).</summary>
    string Who(GridClient c, UUID id, string fallback) => id == c.Self.AgentID ? You : S.Names.Complete(id, fallback);

    const string You = "the person themselves (you)";

    /// <summary>The person sits on one of these prims (a vehicle they ride, a chair).</summary>
    static bool SeatedOn(GridClient c, IReadOnlyCollection<Primitive> set) => c.Self.SittingOn != 0 && set.Any(p => p.LocalID == c.Self.SittingOn);

    static string ObjectName(Primitive p) => p.Properties?.Name is { Length: > 0 } n && n != "Object" && n != "Primitive" ? n : "an unnamed object";

    JsonObject Picture(GridClient c, RegionWorld w, Shot shot, Dictionary<UUID, string> names)
    {
        var o = new JsonObject { ["label"] = shot.Spec.Label, ["width"] = shot.Width, ["height"] = shot.Height };
        if (shot.Pending >= 0) o["png"] = Convert.ToBase64String(Png(shot.Rgba, shot.Width, shot.Height));
        else o["missing"] = "this picture didn't come back from the renderer (what's seen in it is still right)";
        var seen = new JsonArray();
        foreach (var s in shot.Seen.Take(14))
        {
            var e = new JsonObject
            {
                ["name"] = names.TryGetValue(s.Id, out var n) ? n : s.Kind == PickKind.Avatar ? "someone" : "an unnamed object",
                ["kind"] = s.Kind == PickKind.Avatar ? "person" : "object",
                ["in_picture"] = Area(s.X, s.Y), ["share"] = Share(s.Cells, shot.Cells),
            };
            if (Pos(w, s) is { } p && Where(c, p) is { } where) e["where"] = where;
            if (s.Id == shot.Spec.Subject) e["subject"] = true;
            else if (shot.Loads.TryGetValue(s.Id, out var seenLoad) && SeenLook(seenLoad) is { } look) e["look"] = look;
            seen.Add(e);
        }
        o["seen"] = seen;
        if (shot.SkyCells > shot.Cells / 10) o["open_sky_or_distance"] = Share(shot.SkyCells, shot.Cells);
        if (shot.GroundCells > 0) o["ground"] = Share(shot.GroundCells, shot.Cells);
        // (the person's own avatar never counts in picks: how much of the picture they fill isn't known)
        if (shot.Spec.Subject != UUID.Zero && !shot.Spec.ShowSelf) o["subject_fills"] = Share(shot.SubjectCells, shot.Cells);
        if (shot.Relit) o["lighting"] = "relit as at midday so colours show: the place itself is dark now (night or dusk), so don't describe this light as the scene's";
        // what of the subject's look had arrived (the count of everything in view still loading, out to the draw
        // distance, is the log's: on a busy place it's thousands of far things, and said here it made every picture look
        // unfinished or none)
        if (shot.Spec.Subject != UUID.Zero && shot.Loads.TryGetValue(shot.Spec.Subject, out var load))
            o["subject_look"] = SubjectLook(w, load);
        return o;
    }

    // --- what of a look had loaded, in words ---------------------------------------------------------------------------

    /// <summary>What of the subject's look had arrived when the picture was drawn, per thing they wear (or for an object,
    /// its faces), so the describer names no colour of a surface drawn plain grey for want of its texture.</summary>
    string SubjectLook(RegionWorld w, LookLoad load)
    {
        string who = load.Person ? "their" : "its";
        if (load.Person && load.Drawn is null) return "they aren't drawn in this picture";
        if (load.Drawn == "cloud")
            return "nothing of their look had loaded yet: they show only as a loading cloud (no body, skin, hair or clothes drawn). " +
                   "Say their look hasn't loaded yet and to ask again in a moment; describe nothing of their appearance";
        if (load.Drawn == "jelly")
            return "drawn as a plain one-colour stand-in (their avatar is over this viewer's complexity limit), not their real look: " +
                   "describe no colours, skin, hair or clothes, only that they're shown as a plain figure and their rough shape and pose";
        var notes = new List<string>();
        if (load.BakesLoading + load.BakesMissing > 0)
            notes.Add("their skin and body (as Second Life bakes them) hadn't loaded: skin colour and any baked-on clothing unknown");
        var none = new List<string>(); var partly = new List<string>(); var notDrawn = new List<string>(); var blurry = new List<string>();
        int judged = 0, lacking = 0;
        foreach (var p in load.Parts)
        {
            string name = PartName(w, p.Root, load.Person);
            int unknown = p.Loading + p.Missing;
            if (p.ShapesWaiting > 0 && p.ShapesWaiting >= p.Prims) { notDrawn.Add(name); judged++; lacking++; continue; }
            if (p.Faces == 0) continue;   // nothing of it in view
            judged++;
            if (unknown * 2 >= p.Faces) { none.Add(name); lacking++; }
            else if (unknown > 0 || p.ShapesWaiting > 0) partly.Add(name);
            else if (p.Blurry * 2 >= p.Faces) blurry.Add(name);
        }
        string List(List<string> l) => string.Join("; ", l.Take(10)) + (l.Count > 10 ? $" and {l.Count - 10} more" : "");
        if (load.Person)
        {
            if (none.Count > 0) notes.Add($"colours and patterns not loaded on: {List(none)}");
            if (partly.Count > 0) notes.Add($"partly loaded: {List(partly)}");
            if (notDrawn.Count > 0) notes.Add($"not drawn yet (shape still arriving): {List(notDrawn)}");
        }
        else
        {
            var part = load.Parts.FirstOrDefault();
            if (part != null && part.ShapesWaiting > 0) notes.Add($"{part.ShapesWaiting} of its {part.Prims} parts not drawn yet (shape still arriving)");
            if (part != null && part.Loading + part.Missing > 0)
                notes.Add($"colours not loaded on {part.Loading + part.Missing} of its {part.Faces} surfaces in view" +
                          (lacking > 0 ? " (most of it)" : ""));
        }
        // blurred (memory short, or the sharper copy still on its way): colours right, fine detail not
        string blur = load.Person ? (blurry.Count > 0 ? $"blurry on {List(blurry)}: fine print and patterns there may not show" : "")
            : load.Parts.FirstOrDefault() is { } bp && bp.Loading + bp.Missing == 0 && bp.Blurry * 2 >= Math.Max(1, bp.Faces) ? "blurry: fine print and patterns may not show" : "";
        if (notes.Count == 0)
            return blur == "" ? $"{who} look had fully loaded: colours as drawn are real" : $"{who} look had loaded, colours as drawn are real, though {blur}";
        if (blur != "") notes.Add(blur);
        string unloaded = load.Person
            ? "A surface whose texture hadn't loaded is drawn plain grey or white, which is not its colour: never name a colour, pattern or material for those"
            : "Surfaces whose texture hadn't loaded are drawn plain grey or white, which is not their colour: never name a colour, pattern or material for those";
        // most of it: a person, more than half of the things they wear in view and their body's bakes (as one) lacking
        // their colours or not drawn; a thing, more than half its surfaces in view
        bool most = load.Person
            ? (lacking + (load.BakesLoading + load.BakesMissing > 0 ? 1 : 0)) * 2 > judged + (load.Bakes > 0 ? 1 : 0)
            : load.Parts.Sum(p => p.Loading + p.Missing) * 2 > load.Parts.Sum(p => p.Faces);
        string advice = most
            ? $"Most of {who} look hadn't loaded: say what the shapes show ({(load.Person ? "build, pose, the cut of garments where the shapes show it" : "its shape and parts")}), say the colours aren't known yet, and suggest asking again in a moment"
            : "Describe what had loaded as it is, and say which parts' colours aren't known yet";
        return $"{who} look hadn't fully loaded when this was drawn: {string.Join("; ", notes)}. {unloaded}. {advice}.";
    }

    /// <summary>A thing seen in a picture whose look hadn't arrived, briefly (null: as it is).</summary>
    static string? SeenLook(LookLoad load)
    {
        if (load.Person)
            return load.Drawn switch
            {
                "cloud" => "their look hadn't loaded: a loading cloud, nothing of their appearance drawn",
                "jelly" => "shown as a plain one-colour stand-in, not their real look",
                null => null,
                _ => load.BakesLoading > 0 || load.Faces > 0 && load.Loading * 2 >= load.Faces
                    ? "their look hadn't loaded: drawn plain grey or white, colours unknown" : null,
            };
        var p = load.Parts.FirstOrDefault();
        if (p == null || p.Faces == 0) return p is { ShapesWaiting: > 0 } ? "its shape hadn't arrived: not drawn yet" : null;
        return p.Loading + p.Missing == 0 ? null
            : (p.Loading + p.Missing) * 2 >= p.Faces ? "its colours hadn't loaded: drawn plain grey or white, colours unknown"
            : "partly loaded: some of its surfaces plain grey or white, their colours unknown";
    }

    /// <summary>A worn thing by its name and where it's worn; an object's part by its name.</summary>
    string PartName(RegionWorld w, UUID root, bool worn)
    {
        if (w.PrimById(root) is not { } p) return "something";
        var name = ObjectName(p);
        if (name == "an unnamed object") name = "an unnamed item";
        return worn ? $"{name} (on {ObjectViews.AttachPointName((int)p.PrimData.AttachmentPoint)})" : name;
    }

    static string Share(int n, int of) => $"{(int)Math.Round(100.0 * n / Math.Max(1, of))}%";

    static string Area(float x, float y) =>
        (y < 0.36f ? "top " : y > 0.64f ? "bottom " : "") + (x < 0.36f ? "left" : x > 0.64f ? "right" : "middle");

    static LVector3? Pos(RegionWorld w, SeenThing s) =>
        s.Kind == PickKind.Avatar ? (w.AvatarById(s.Id) is { } a ? AvatarRegionPos(w, a) : null)
        : w.PrimById(s.Id) is { } p ? w.RegionPosition(p) : null;

    static LVector3 AvatarRegionPos(RegionWorld w, Avatar av) =>
        av.ParentID != 0 && w.Prims.TryGetValue(av.ParentID, out var seat) ? w.RegionPosition(seat) + av.Position * w.RegionRotation(seat) : av.Position;

    // --- what the lists know -----------------------------------------------------------------------------------------

    /// <summary>"4 metres, two o'clock" from the person, as Sojourn Access's lists say it (Bearings.Relative).</summary>
    string? Where(GridClient c, LVector3 pos)
    {
        var me = S.Motion.Position(c);
        var q = c.Self.SittingOn != 0 ? c.Self.SimRotation : c.Self.Movement.BodyRotation;
        double heading = Motion.Heading(q);
        double dx = pos.X - me.X, dy = pos.Y - me.Y, dz = pos.Z - me.Z;
        double d = Math.Sqrt(dx * dx + dy * dy + dz * dz), flat = Math.Sqrt(dx * dx + dy * dy);
        if (flat < 0.5) return $"{Metres(d)}, {(dz > 0 ? "above" : "below")} them";
        double compass = ((90 - Math.Atan2(dy, dx) * 180 / Math.PI) % 360 + 360) % 360;
        var s = $"{Metres(d)}, {Clock(compass - heading)}";
        if (Math.Abs(dz) >= 2) s += $", {Metres(Math.Abs(dz))} {(dz > 0 ? "above" : "below")}";
        return s;
    }

    static readonly string[] Hours = { "twelve", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven" };
    static string Clock(double rel) => $"{Hours[(int)Math.Round((rel % 360 + 360) % 360 / 30) % 12]} o'clock";
    static string Metres(double m) => m < 1 ? "under a metre" : Math.Round(m) == 1 ? "1 metre" : string.Create(CultureInfo.InvariantCulture, $"{Math.Round(m):0} metres");

    async Task<JsonObject> HereAsync(GridClient c, RegionWorld w, CancellationToken ct)
    {
        var o = new JsonObject { ["what"] = "where the person is", ["region"] = c.Network.CurrentSim?.Name };
        try
        {
            if (await S.Api.CallAsync("TesselAgent", new JsonObject { ["op"] = "parcel" }, true, TimeSpan.FromSeconds(3), ct) is JsonObject pr
                && pr["parcel"] is JsonObject parcel)
            {
                o["parcel"] = parcel.Str("name");
                if (parcel.Str("desc") is { Length: > 0 } pd) o["parcel_description"] = pd;
            }
        }
        catch (Exception e) when (e is LeapError or TimeoutException or OperationCanceledException) { }
        var me = S.Motion.Position(c);
        var people = new JsonArray();
        foreach (var av in w.Avatars.Values.Where(a => a.ID != c.Self.AgentID).Select(a => (A: a, P: AvatarRegionPos(w, a)))
                     .Where(x => LVector3.Distance(x.P, me) <= 30).OrderBy(x => LVector3.Distance(x.P, me)).Take(12))
            people.Add($"{S.Names.Complete(av.A.ID, av.A.Name is { Length: > 0 } n ? n : "someone")}, {Where(c, av.P)}"
                       + (av.A.ParentID != 0 ? ", sitting" : ""));
        o["people_within_30_metres"] = people;
        var near = w.Prims.Values.Where(p => p.ParentID == 0 && p.PrimData.PCode == PCode.Prim)
            .Select(p => (P: p, D: LVector3.Distance(w.RegionPosition(p), me))).Where(x => x.D <= 20).OrderBy(x => x.D).Take(40).ToList();
        if (near.Count > 0) await S.Objects.FetchAsync(w, near.Select(x => x.P).ToList(), TimeSpan.FromSeconds(2), ct);
        var things = new JsonArray();
        foreach (var (p, _) in near.Where(x => ObjectName(x.P) != "an unnamed object").Take(25))
            things.Add($"{ObjectName(p)}, {Where(c, w.RegionPosition(p))}" + (string.IsNullOrEmpty(p.Text) ? "" : $" (floating text: {Clip(p.Text, 80)})"));
        o["named_objects_within_20_metres"] = things;
        int unnamed = near.Count(x => ObjectName(x.P) == "an unnamed object");
        if (unnamed > 0) o["unnamed_objects_within_20_metres"] = unnamed;
        return o;
    }

    async Task<JsonObject> PersonAsync(GridClient c, RegionWorld w, Avatar av, CancellationToken ct)
    {
        if (!S.Names.Has(av.ID)) await S.Names.ResolveAsync(c, new[] { av.ID }, TimeSpan.FromSeconds(2), ct);
        var o = new JsonObject { ["what"] = "a person", ["name"] = S.Names.Complete(av.ID, av.Name ?? "someone"), ["id"] = av.ID.ToString() };
        o["where"] = Where(c, AvatarRegionPos(w, av));
        if (av.ParentID != 0 && w.Prims.TryGetValue(av.ParentID, out var seat)) o["sitting_on"] = SittingOn(w, seat);
        o["wearing_items_named"] = await AttachmentsAsync(w, av, ct);
        return o;
    }

    /// <summary>What someone sits on, with what sitting means in Second Life: a pose ball, a dance ball or a seat that
    /// animates them, so the picture may show them standing, dancing or lying (a tester's description reported that as a
    /// contradiction, 2026-11-01).</summary>
    string SittingOn(RegionWorld w, Primitive seat) =>
        $"{ObjectName(S.Objects.RootOf(w, seat))} (sitting in Second Life's sense: on a seat, pose ball or dance ball that animates " +
        "them, so they may look standing, dancing or lying; describe the pose the pictures show, not a contradiction)";

    async Task<JsonObject> MeAsync(GridClient c, RegionWorld w, CancellationToken ct)
    {
        var o = new JsonObject { ["what"] = "the person themselves: their own avatar", ["name"] = S.Names.Complete(c.Self.AgentID, c.Self.Name) };
        if (w.AvatarById(c.Self.AgentID) is { } av)
        {
            if (av.ParentID != 0 && w.Prims.TryGetValue(av.ParentID, out var seat)) o["sitting_on"] = SittingOn(w, seat);
            o["wearing_items_named"] = await AttachmentsAsync(w, av, ct);
        }
        // clothing and body parts (the Current Outfit folder's links)
        var layers = new JsonArray();
        var cof = c.Inventory.FindFolderForType(FolderType.CurrentOutfit);
        if (cof != UUID.Zero && c.Inventory.Store is { } store)
            foreach (var l in store.GetContents(cof).OfType<InventoryItem>())
            {
                var target = store.Contains(l.AssetUUID) ? store[l.AssetUUID] as InventoryItem : null;
                if ((target ?? l) is InventoryWearable wear) layers.Add($"{wear.Name} ({wear.WearableType.ToString().ToLowerInvariant()})");
                else if (l.AssetType == AssetType.Link && target is InventoryWearable tw) layers.Add($"{tw.Name} ({tw.WearableType.ToString().ToLowerInvariant()})");
            }
        o["clothing_and_body_parts"] = layers;
        return o;
    }

    async Task<JsonArray> AttachmentsAsync(RegionWorld w, Avatar av, CancellationToken ct)
    {
        var roots = w.Prims.Values.Where(p => p.ParentID == av.LocalID).ToList();
        if (roots.Count > 0) await S.Objects.FetchAsync(w, roots, TimeSpan.FromSeconds(2), ct);
        var list = new JsonArray();
        foreach (var p in roots.OrderBy(p => (int)p.PrimData.AttachmentPoint))
        {
            int point = (int)p.PrimData.AttachmentPoint;
            if (point is >= 31 and <= 38) continue;   // HUDs: not seen by anyone
            list.Add($"{ObjectName(p)} (on {ObjectViews.AttachPointName(point)})");
        }
        return list;
    }

    async Task<JsonObject> ObjectAsync(GridClient c, RegionWorld w, Primitive prim, CancellationToken ct)
    {
        bool worn = S.Objects.IsAttachment(w, prim, out var wearer, out var root);
        if (!worn) root = S.Objects.RootOf(w, prim);
        await S.Objects.FetchAsync(w, new[] { root }, TimeSpan.FromSeconds(3), ct);
        var o = new JsonObject { ["what"] = worn ? "something a person is wearing" : "an object", ["name"] = ObjectName(root), ["id"] = root.ID.ToString() };
        if (root.Properties?.Description is { Length: > 0 } d && d != "(No Description)") o["its_own_description"] = d;
        if (!string.IsNullOrEmpty(root.Text)) o["floating_text"] = root.Text;
        if (worn && wearer != null)
        {
            o["worn_by"] = wearer.ID == c.Self.AgentID ? "the person themselves" : S.Names.Complete(wearer.ID, wearer.Name ?? "someone");
            o["worn_on"] = ObjectViews.AttachPointName((int)root.PrimData.AttachmentPoint);
        }
        else
        {
            o["where"] = Where(c, w.RegionPosition(root));
            if (root.Properties is { } pr && pr.OwnerID != UUID.Zero) o["owner"] = Who(c, pr.OwnerID, "unknown");
        }
        var set = S.Objects.Linkset(w, root);
        o["parts"] = set.Count;
        var s = root.Scale;
        if (set.Count == 1) o["size_metres"] = string.Create(CultureInfo.InvariantCulture, $"{s.X:0.##} x {s.Y:0.##} x {s.Z:0.##}");
        var seated = w.Avatars.Values.Where(a => a.ParentID != 0 && set.Any(l => l.LocalID == a.ParentID)).Select(a => Who(c, a.ID, a.Name ?? "someone")).ToList();
        if (seated.Count > 0) o["seated_on_it"] = new JsonArray(seated.Select(n => (JsonNode)n).ToArray());
        if (SeatedOn(c, set)) o["the_person_is_on_it"] = "they're sitting on it themselves (riding or driving it, if it moves): say so first, as 'you're riding ...'";
        if (root.Properties is { SaleType: not SaleType.Not } sp) o["for_sale"] = $"L${sp.SalePrice}";
        return o;
    }

    /// <summary>One of the person's HUDs: no camera in the world sees it, so its picture is the HUD as it shows on
    /// their screen, drawn bigger (OffscreenRenderer.Hud.cs), with its named parts placed on it; the facts its name,
    /// where on the screen, its parts' names and floating text.</summary>
    async Task<JsonObject> HudLookAsync(GridClient c, RegionWorld w, Primitive root, string detail, bool shoot, CancellationToken ct)
    {
        var o = new JsonObject { ["detail"] = detail };
        var facts = await ObjectAsync(c, w, root, ct);
        facts["what"] = "a HUD the person wears: a panel of controls or a display on their own screen, which only they see";
        facts.Remove("worn_by");
        var set = S.Objects.Linkset(w, root);
        try { await S.Objects.FetchFullAsync(w, set, TimeSpan.FromSeconds(3), ct); } catch (LeapError) { }
        string rootName = ObjectName(root);
        var named = set.Where(p => p.LocalID != root.LocalID && p.Properties?.Name is { Length: > 0 } n && n is not ("Object" or "Primitive") && n != rootName)
            .Select(p => p.Properties!.Name).Distinct().Take(40).ToList();
        if (named.Count > 0) facts["named_parts"] = new JsonArray(named.Select(n => (JsonNode)n).ToArray());
        var texts = set.Where(p => !string.IsNullOrWhiteSpace(p.Text)).Select(p => Clip(p.Text, 200)).Distinct().Take(10).ToList();
        if (texts.Count > 0) facts["text_on_it"] = new JsonArray(texts.Select(t => (JsonNode)t).ToArray());
        o["subject"] = facts;
        var notes = new JsonArray();
        var pictures = new JsonArray();
        if (!shoot) { }
        else if (_view is not { Running: true })
            notes.Add(_view == null ? "this app draws no view (started without its renderer): no picture, only what the lists know"
                : $"the view isn't running ({_view.State}): no picture, only what the lists know");
        else
        {
            var placed = await _view.WithHudsAsync((p, _) => p, HudsPlaced.None, ct);
            if (placed.Objects.FirstOrDefault(h => h.Id == root.ID) is not { } hud) notes.Add("it isn't drawn yet (its shape may still be arriving): no picture");
            else if (await _view.PictureAsync(hud, false, ct) is not { } got) notes.Add("its picture couldn't be drawn just now");
            else
            {
                var (pic, png) = got;
                float across = 0.5f - (hud.Min.Y + hud.Max.Y) / 2 / placed.Aspect, down = 0.5f - (hud.Min.Z + hud.Max.Z) / 2;
                var seen = new JsonArray();
                foreach (var part in hud.Parts.Skip(1))
                {
                    if (w.PrimById(part.Id)?.Properties?.Name is not { Length: > 0 } n || n is "Object" or "Primitive" || n == rootName) continue;
                    if (pic.Rect(part.Min, part.Max) is not { } r) continue;
                    seen.Add(new JsonObject
                    {
                        ["name"] = n, ["kind"] = "part of the HUD",
                        ["in_picture"] = Area((r.X + r.W / 2f) / pic.Width, (r.Y + r.H / 2f) / pic.Height),
                        ["share"] = Share(r.W * r.H, pic.Width * pic.Height),
                    });
                    if (seen.Count >= 14) break;
                }
                pictures.Add(new JsonObject
                {
                    ["label"] = $"the HUD as it shows on their screen ({Hud.Huds.Where(across, down)} of it), drawn bigger, over a dark grey where it's see-through",
                    ["png"] = Convert.ToBase64String(png), ["width"] = pic.Width, ["height"] = pic.Height, ["seen"] = seen,
                });
            }
        }
        o["pictures"] = pictures;
        if (notes.Count > 0) o["notes"] = notes;
        return o;
    }

    static string Clip(string s, int n) => s.Length <= n ? s.Replace('\n', ' ') : s[..n].Replace('\n', ' ') + "...";

    static byte[] Png(byte[] rgba, int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        // the frame's alpha is the scene's, not a picture's: opaque
        var px = rgba.ToArray();
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), Math.Min(px.Length, w * h * 4));
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    // --- textures and profiles ---------------------------------------------------------------------------------------

    async Task<JsonNode?> TextureAsync(JsonObject a, CancellationToken ct)
    {
        if (!UUID.TryParse(a.Str("id"), out var id)) throw new LeapError("id: a texture's id");
        using var bmp = await S.InventoryMeta.TextureBitmapAsync(id, Math.Clamp(a.Int("max", 1024), 64, 2048), ct);
        return new JsonObject { ["png"] = Convert.ToBase64String(InventoryMeta.Png(bmp)), ["width"] = bmp.Width, ["height"] = bmp.Height };
    }

    async Task<JsonNode?> ProfileAsync(JsonObject a, CancellationToken ct)
    {
        if (!UUID.TryParse(a.Str("id"), out var id)) throw new LeapError("id: a resident's id");
        var p = await S.Profiles.ProfileAsync(new JsonObject { ["id"] = id.ToString() }, ct);
        var o = new JsonObject { ["name"] = p.Str("name") ?? S.Names.Complete(id, "someone") };
        if (p.Str("about") is { Length: > 0 } about) o["about"] = Clip(about, 400);
        var pics = new JsonArray();
        foreach (var (key, label) in new[] { ("image_id", "profile picture"), ("first_life_image_id", "first life picture") })
        {
            if (!UUID.TryParse(p.Str(key), out var tex) || tex == UUID.Zero) continue;
            try
            {
                using var bmp = await S.InventoryMeta.TextureBitmapAsync(tex, 768, ct);
                pics.Add(new JsonObject { ["label"] = label, ["png"] = Convert.ToBase64String(InventoryMeta.Png(bmp)), ["width"] = bmp.Width, ["height"] = bmp.Height });
            }
            catch (LeapError e) { pics.Add(new JsonObject { ["label"] = label, ["error"] = e.Message }); }
        }
        o["pictures"] = pics;
        if (pics.Count == 0) o["note"] = "their profile has no pictures";
        return o;
    }
}
