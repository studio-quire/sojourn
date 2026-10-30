using System.Text.Json;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Render.World;

namespace Sojourn.Session.Control;

/// <summary>
/// SojournAvatars: how other people's avatars are drawn (World/Avatars.Crowd.cs). Who's around with their rendering
/// complexity and whether they're drawn whole, as an impostor or as a jelly doll; and the person's own choice per
/// person, the viewer's Render Normally / Always Render Fully / Do Not Render, kept with the account
/// (avatar-render.json in its state folder). The limits themselves are settings (full_avatars, avatar_complexity).
/// </summary>
public sealed partial class AvatarsApi : LeapApi
{
    readonly AvatarLimits _limits;
    readonly string _file;

    public override string Name => "SojournAvatars";
    public override string Desc => "other people's avatars as drawn: complexity, full / impostor / jelly doll, and the person's always-render / don't-render choices";

    public AvatarsApi(AgentSession s, AvatarLimits limits) : base(s)
    {
        _limits = limits;
        _file = Path.Combine(s.StateDir, "avatar-render.json");
        Load();
        Op("list", "{within?: metres} -> {avatars: [{id, name, distance, complexity, known, drawn: full|impostor|jelly|checking|loading, setting}], yours, "
            + "complexity_limit, full_avatars}: everyone drawn lately, nearest first; known: false while their attachments' costs are still "
            + "arriving (checking: shown as a cloud until then); loading: attachments its appearance lists still on their way (a cloud, a minute at most); yours: the person's own avatar's complexity", List);
        Op("render", "{id | name, as: normal|always|never}: always draws them whole whatever their complexity; never draws them as a grey "
            + "jelly doll without attachments; normal by the limits", Render);
        Op("inspect", "{id | name} -> everything the renderer has for one avatar drawn nearby: its visual params, bakes and their state, "
            + "the system meshes its attachments hide, the attachments the region lists against those that arrived, each worn prim "
            + "(mesh, skin, detail levels, each face's texture and material, bakes on mesh marked), the joints worn meshes move, its "
            + "animations; also kept in avatar-reports/ in the state folder. For when someone looks wrong", Inspect);
        FixOps();   // reset skeleton, refresh attachments, resync animations (AvatarsApi.Fix.cs)
    }

    JsonNode? List(JsonObject a)
    {
        double within = a.Num("within", 1e9);
        var all = _limits.Status;
        var arr = new JsonArray();
        int yours = 0;
        foreach (var st in all.OrderBy(x => x.Distance))
        {
            if (st.Id == _limits.Self) { yours = st.Complexity; continue; }
            if (st.Distance > within) continue;
            arr.Add(new JsonObject
            {
                ["id"] = st.Id.ToString(), ["name"] = st.Name, ["distance"] = Math.Round(st.Distance, 1),
                ["complexity"] = st.Complexity, ["known"] = st.Known, ["drawn"] = st.Drawn,
                ["setting"] = st.Setting.ToString().ToLowerInvariant(),
            });
        }
        return new JsonObject
        {
            ["avatars"] = arr, ["yours"] = yours,
            ["complexity_limit"] = _limits.MaxComplexity, ["full_avatars"] = _limits.MaxFull,
        };
    }

    JsonNode? Render(JsonObject a)
    {
        var how = (a.Str("as") ?? "").Trim().ToLowerInvariant() switch
        {
            "normal" or "normally" => AvatarRender.Normal,
            "always" or "full" or "fully" => AvatarRender.Always,
            "never" or "jelly" or "off" => AvatarRender.Never,
            var x => throw new LeapError($"as: normal, always or never (not '{x}')"),
        };
        var (id, name) = Who(a);
        if (id == _limits.Self) throw new LeapError("that's the person's own avatar, which is always drawn whole");
        if (how == AvatarRender.Normal) _limits.Overrides.TryRemove(id, out _);
        else _limits.Overrides[id] = how;
        Save();
        return new JsonObject { ["id"] = id.ToString(), ["name"] = name, ["setting"] = how.ToString().ToLowerInvariant() };
    }

    JsonNode? Inspect(JsonObject a)
    {
        var (id, name) = Who(a, self: true);
        var report = _limits.Inspection.Get(id, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult()
            ?? throw new LeapError($"{name} isn't drawn nearby just now");
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(_file)!, "avatar-reports");
            Directory.CreateDirectory(dir);
            var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}.json");
            File.WriteAllText(path, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            report["saved"] = path;
        }
        catch (Exception) { }
        return report;
    }

    (UUID Id, string Name) Who(JsonObject a, bool self = false)
    {
        UUID id;
        string name;
        if (a.Str("id") is { } sid && UUID.TryParse(sid, out id))
            name = _limits.Status.FirstOrDefault(x => x.Id == id).Name ?? sid;
        else if (a.Str("name") is { Length: > 0 } words)
        {
            var hits = _limits.Status.Where(x => (self || x.Id != _limits.Self) && x.Name.Contains(words, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) throw new LeapError($"no one called '{words}' is drawn nearby");
            if (hits.Count > 1 && !hits.Any(x => x.Name.Equals(words, StringComparison.OrdinalIgnoreCase)))
                throw new LeapError("several fit: " + string.Join(", ", hits.Select(x => $"{x.Name} ({x.Id})")));
            var hit = hits.FirstOrDefault(x => x.Name.Equals(words, StringComparison.OrdinalIgnoreCase));
            if (hit.Id == UUID.Zero) hit = hits[0];
            (id, name) = (hit.Id, hit.Name);
        }
        else throw new LeapError("id or name");
        return (id, name);
    }

    void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file)) ?? new();
            foreach (var (k, v) in map)
                if (UUID.TryParse(k, out var id) && Enum.TryParse<AvatarRender>(v, true, out var how) && how != AvatarRender.Normal)
                    _limits.Overrides[id] = how;
        }
        catch (Exception) { /* a broken file: start without choices */ }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var map = _limits.Overrides.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.ToString().ToLowerInvariant());
            File.WriteAllText(_file, JsonSerializer.Serialize(map));
        }
        catch (Exception) { }
    }
}
