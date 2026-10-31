using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Session.Control;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Session.Assistant;

/// <summary>
/// SojournAssistant: the client's side of the assistant on the agent API. The sidecar (assistant/) fills the panel,
/// reads what the person points at, and searches the inventory here. Nothing here can answer a confirmation: those
/// are the person's alone (<see cref="Confirmations"/>).
/// </summary>
public sealed class AssistantApi : LeapApi
{
    readonly AssistantHost _host;

    public override string Name => "SojournAssistant";
    public override string Desc => "Sojourn's assistant: its panel, the pointer, inventory search";
    public override bool NeedsSession => false;
    public override bool RunsWhileHeld => true;

    public AssistantApi(AgentSession s, AssistantHost host) : base(s)
    {
        _host = host;
        Op("hello", "{mic?, notice?, provider?, hearing_ready?} -> {vr, hint, mode, effort, prefs, dev}: the sidecar is up (send every few seconds); "
            + "hearing_ready false while its recogniser loads", a =>
        {
            _host.SidecarHello(a.Str("mic"), a.Has("notice") ? a.Str("notice") : null, a["provider"] as JsonObject is { } p ? (JsonObject)p.DeepClone() : null,
                a.Bool("hearing_ready", true));
            return new JsonObject
            {
                ["vr"] = _host.InHeadset, ["hint"] = _host.Hint, ["mode"] = AssistantHost.ModelName(_host.Model),
                ["effort"] = _host.Settings.AssistantEffort.ToJson(), ["prefs"] = _host.PrefsJson(), ["dev"] = _host.Dev,
                ["app"] = _host.App,
            };
        });
        if (_host.Dev)
        {
            // dev mode (docs/dev-mode.md): the Claude Code session that started the client stands in for the assistant
            Op("dev", "{token, what: request | pick | pointed | answer | timer, text?, target?, source?, id?, label?}: the sidecar passes on what the person said, typed, "
                + "picked, pointed at or typed in answer to askTyped, as a `dev` event for the session driving the client (only the client's own sidecar, which has the token)", a =>
            {
                var given = System.Text.Encoding.UTF8.GetBytes(a.Str("token") ?? "");
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(given, System.Text.Encoding.UTF8.GetBytes(_host.DevToken!)))
                    throw new LeapError("only the client's own sidecar passes on what the person says");
                if (a.Str("what") is not ("request" or "pick" or "pointed" or "answer" or "timer")) throw new LeapError("what is request, pick, pointed, answer or timer");
                var data = new JsonObject();
                foreach (var k in new[] { "what", "text", "target", "source", "id", "label" })
                    if (a[k] is { } v) data[k] = v.DeepClone();
                _host.DevEvent(data);
                return new JsonObject();
            });
            Op("say", "{text, speak?=true}: the driving session's reply on the panel (the request's work done), read out loud too when the person has spoken replies on", a =>
            {
                var text = a.Str("text") ?? "";
                _host.Panel.Update(s => { s.Result = text; s.Status = null; s.Working = false; s.Model = null; });
                if (a.Bool("speak", true) && text.Length > 0) _host.DevSpeak(text);
                return new JsonObject();
            });
        }
        Op("prefs", "{name?, character?, speak?, voice?} -> {name, character, speak, voice}: the person's choices about the assistant, changed only when they ask: "
            + "a name to go by and a character (\"\" or null clears it back to none / plain), replies read out loud, and in which voice", a =>
        {
            var st = _host.Settings;
            if (a.ContainsKey("name"))
            {
                var n = a.Str("name")?.Trim();
                if (n is { Length: > 40 }) throw new LeapError("name: 40 characters at most");
                if (string.IsNullOrEmpty(n)) st.UseAssistantName = false;
                else { st.AssistantName = n; st.UseAssistantName = true; }
            }
            if (a.ContainsKey("character"))
            {
                var c = a.Str("character")?.Trim();
                if (c is { Length: > 600 }) throw new LeapError("character: 600 characters at most");
                if (string.IsNullOrEmpty(c)) st.UseAssistantCharacter = false;
                else { st.AssistantCharacter = c; st.UseAssistantCharacter = true; }
            }
            if (a.ContainsKey("speak")) st.SpokenReplies = a.Bool("speak", false);
            if (a.ContainsKey("voice"))
            {
                var v = a.Str("voice")?.Trim();
                if (v is { Length: > 24 } || (v is { Length: > 0 } && !v.All(ch => char.IsAsciiLetterLower(ch) || ch == '_')))
                    throw new LeapError("voice: a voice id like af_heart");
                st.AssistantVoice = string.IsNullOrEmpty(v) ? null : v;
            }
            _host.SettingsChanged?.Invoke();
            _host.PrefsChanged();
            return _host.PrefsJson();
        });
        Op("model", "{model?: quick | sonnet | opus} -> {model}: which model takes requests first; given, it changes (and is saved)", a =>
        {
            if (a.Str("model") is { } name)
            {
                if (!Enum.TryParse<AssistantModel>(name, ignoreCase: true, out var m) || !Enum.IsDefined(m))
                    throw new LeapError("model is quick, sonnet or opus");
                if (_host.ChangeModel is { } change) change(m); else _host.SetModel(m, announce: true);
            }
            return new JsonObject { ["model"] = AssistantHost.ModelName(_host.Model) };
        });
        Op("bringPanel", "-> {brought}: in the headset, glides the panel (or the settings menu showing) back in front of the person from "
            + "wherever it is; a place they put it is kept for next time. On the desktop there's nothing to bring (brought: false)", _ =>
        {
            if (_host.BringPanel is not { } bring) return new JsonObject { ["brought"] = false };
            bring();
            return new JsonObject { ["brought"] = true };
        });
        Op("resetMenus", "-> {reset}: in the headset, every menu (the panel, the settings menu, the chat feed, the cards with the "
            + "person, the reader, the HUD board, a page up close, the keyboard) back to following the person's head in its own place, "
            + "in front of them; where they put any of them is forgotten (X held on their left controller does the same). On the desktop: reset false", _ =>
        {
            if (_host.ResetMenus is not { } reset) return new JsonObject { ["reset"] = false };
            reset();
            return new JsonObject { ["reset"] = true };
        });
        Op("panel", "{heard?, status?, result?, prompt?, choices?: [{id, label, detail?}], point?, listening?, hearing?, working?, model?, jobs?, clear?}: what the panel shows (model: who's working on it; jobs: background jobs' progress, which alone doesn't bring the panel up; point: a question asking the person to point at what they mean, which puts a pointer on their hand until they point (a `pointed` event with the target) or it's cleared with null)",
            a => { Panel(a); return new JsonObject(); });
        Op("askTyped", "{question, text?} -> {}: asks the person to check a word by typing (a name nothing known matches): the question "
            + "on the panel and the typing line holding text (the best guess), the caret at its end, with the headset's keyboard up for it. "
            + "What they send comes back as a `typed_answer` event {text} ({cancelled: true}: they dismissed it, or sent nothing); "
            + "words said instead come as a request. {question: null} takes it away (and the keyboard with it)", a =>
        {
            if (a.Str("question") is not { Length: > 0 } q) { _host.ClearTypedAsk(); return new JsonObject(); }
            if (q.Length > 300) q = q[..300];
            var guess = new string((a.Str("text") ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
            if (guess.Length > 400) guess = guess[..400];
            _host.AskTyped(q, guess);
            return new JsonObject();
        });
        Op("look", "{preset?, material?, tint?, tint_opacity?, glow?, sheen?: [up to 3 colours], sheen_opacity?, frost?, rim?, ink?, dim?, faint?, accent?, "
            + "on_accent?, key?, key_opacity?, radius?, font?} -> the look now, and {readability} when text may be hard to read: how the panel "
            + "and the settings menu look, changed only when the person asks. Colours are #rrggbb; a preset (" + string.Join(", ", PanelLook.Presets.Select(p => p.Preset))
            + ") first, then the rest over it; material " + string.Join(", ", PanelLook.Materials) + "; opacities 0-1 (tint_opacity from "
            + GlassLight.MinBody.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", sheen 0-0.8); glow 0-1, the least light the body "
            + "gives over a dark world as a share of its tint (below that tint_opacity it gives off light of its own); frost 0-1 blurs the world behind; "
            + "radius 0-32; font " + string.Join(", ", PanelLook.Fonts)
            + ". A valid change always applies; readability says what would be faint, over which world, and what would help. No arguments: just the look now.", a =>
        {
            JsonObject WithAdvice(PanelLook l)
            {
                var j = l.ToJson();
                if (l.Readability() is { } note) j["readability"] = note;
                return j;
            }
            if (!PanelLook.Keys.Any(a.ContainsKey)) return WithAdvice(_host.Panel.Look);
            // a waiting confirmation is drawn as it is until the person answers it
            if (_host.Panel.Read(p => p.Confirm != null)) throw new LeapError("not while a confirmation waits for the person; try again after");
            PanelLook next;
            try { next = _host.Panel.Look.With(a); }
            catch (ArgumentException e) { throw new LeapError(e.Message); }
            if (_host.ChangeLook is { } change) change(next); else _host.Panel.Look = next;
            return WithAdvice(next);
        });
        Op("pointer", "-> {target, looking}: what the person points at now (null: nothing; in the headset the pointer is often off), and what's in the middle of their view when they aren't pointing", _ =>
            new JsonObject { ["target"] = _host.Pointer.Current?.ToJson(), ["looking"] = _host.Pointer.Looking?.ToJson(looking: true) });
        Op("findInventory", "{query, type?, limit=12} -> {items: [{id, name, type, folder, worn, desc, perms}]}: inventory items matching the words, best first",
            async (a, ct) => await FindAsync(a, ct));
        var hearing = new HearingWords(s);
        Op("hearing", "-> {people, here, friends, worn, places, kept}: names around the person for the ears (Whisper's prompt, the name pass): "
            + "people nearby nearest first, the region, parcel and neighbours, friends online first, what they wear, regions visited and "
            + "landmarks, their own inventory folders. From memory only, cheap", _ => hearing.Build());
    }

    void Panel(JsonObject a)
    {
        // a job's progress on its own is quiet: it shows when the panel is up anyway
        bool quiet = a.All(kv => kv.Key is "jobs" or "op" or "reqid" or "reply");
        if (quiet)
        {
            _host.Panel.Update(s => s.Jobs = a.Str("jobs"), show: false, news: false);
            return;
        }
        if (a.ContainsKey("jobs")) _host.Panel.Update(s => s.Jobs = a.Str("jobs"), show: false, news: false);
        if (a.Bool("clear", false)) _host.ClearTypedAsk();
        _host.Panel.Update(s =>
        {
            if (a.Bool("clear", false))
            {
                s.Heard = s.Status = s.Result = s.Prompt = s.Model = s.PointAsk = null;
                s.Choices.Clear();
            }
            if (a.ContainsKey("heard")) s.Heard = a.Str("heard");
            if (a.ContainsKey("status")) s.Status = a.Str("status");
            if (a.ContainsKey("result")) s.Result = a.Str("result");
            if (a.ContainsKey("prompt")) s.Prompt = a.Str("prompt");
            if (a.ContainsKey("listening")) s.Listening = a.Bool("listening", false);
            if (a.ContainsKey("hearing"))
            {
                s.Hearing = a.Bool("hearing", false);
                // hearing done: a dictation that heard nothing gives the panel back at once rather than after its 20 s
                // wait (the user: after dictating nothing, the assistant's messages should come back)
                if (!s.Hearing && s.Dictation == PanelDictation.Hearing) s.Dictation = PanelDictation.None;
            }
            if (a.ContainsKey("working")) s.Working = a.Bool("working", false);
            if (a.ContainsKey("model")) s.Model = a.Str("model");
            if (a.ContainsKey("point")) s.PointAsk = a.Str("point") is { Length: > 0 } ask ? (ask.Length > 300 ? ask[..300] : ask) : null;
            if (a.ContainsKey("choices"))
            {
                s.Choices.Clear();
                if (a["choices"] is JsonArray arr)
                    foreach (var c in arr.OfType<JsonObject>().Take(6))
                        s.Choices.Add(new PanelChoice(c.Str("id") ?? s.Choices.Count.ToString(), c.Str("label") ?? "", c.Str("detail")));
            }
        });
    }

    // --- inventory search ---------------------------------------------------------------------------------------

    static readonly Dictionary<string, string[]> TypeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shirt"] = new[] { "shirt", "top" }, ["pants"] = new[] { "pants", "trousers", "jeans" }, ["jacket"] = new[] { "jacket", "coat" },
        ["skirt"] = new[] { "skirt" }, ["shoes"] = new[] { "shoes", "boots" }, ["socks"] = new[] { "socks" }, ["gloves"] = new[] { "gloves" },
        ["hair"] = new[] { "hair" }, ["skin"] = new[] { "skin" }, ["shape"] = new[] { "shape", "body" }, ["eyes"] = new[] { "eyes" },
    };

    async Task<JsonNode?> FindAsync(JsonObject a, CancellationToken ct)
    {
        var c = S.Client ?? throw new NotConnected("viewer not connected");
        var store = c.Inventory.Store ?? throw new LeapError("inventory not loaded yet");
        var words = (a.Str("query") ?? "").ToLowerInvariant().Split(new[] { ' ', ',', '.', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("the" or "a" or "an" or "my" or "of")).ToArray();
        var type = a.Str("type")?.ToLowerInvariant();
        int limit = Math.Clamp(a.Int("limit", 12), 1, 50);
        var trash = S.Inventory.FolderFor(FolderType.Trash);

        var found = new List<(double Score, InventoryItem Item, string Folder)>();
        var stack = new Stack<(UUID Id, string Path)>();
        stack.Push(((store.RootFolder ?? throw new LeapError("inventory not loaded yet")).UUID, ""));
        int folders = 0;
        while (stack.Count > 0 && folders < 20000)
        {
            ct.ThrowIfCancellationRequested();
            var (fid, path) = stack.Pop();
            folders++;
            await S.Inventory.EnsureLoadedAsync(fid, ct);
            foreach (var n in store.GetContents(fid))
            {
                if (n is InventoryFolder f)
                {
                    if (f.UUID == trash || f.PreferredType == FolderType.CurrentOutfit) continue;
                    stack.Push((f.UUID, path.Length == 0 ? f.Name : path + "/" + f.Name));
                }
                else if (n is InventoryItem it && it.AssetType is not (AssetType.Link or AssetType.LinkFolder))
                {
                    string kind = KindOf(it);
                    if (type != null && !kind.Contains(type) && !(TypeWords.TryGetValue(kind, out var tw) && tw.Contains(type))) continue;
                    double score = Score(words, it, kind, path);
                    if (score > 0 || words.Length == 0) found.Add((score, it, path));
                }
            }
        }
        var best = found.OrderByDescending(f => f.Score).ThenBy(f => f.Item.Name.Length).Take(limit);
        var items = new JsonArray();
        foreach (var (score, it, folder) in best)
        {
            var perms = it.Permissions.OwnerMask;
            items.Add(new JsonObject
            {
                ["id"] = it.UUID.ToString(),
                ["name"] = it.Name,
                ["type"] = KindOf(it),
                ["folder"] = folder,
                ["worn"] = S.Inventory.IsWorn(it),
                ["desc"] = string.IsNullOrWhiteSpace(it.Description) || it.Description == "(No Description)" ? null : it.Description,
                ["score"] = Math.Round(score, 2),
                ["perms"] = $"{((perms & PermissionMask.Modify) != 0 ? "M" : "")}{((perms & PermissionMask.Copy) != 0 ? "C" : "")}{((perms & PermissionMask.Transfer) != 0 ? "T" : "")}",
            });
        }
        return new JsonObject { ["items"] = items, ["searched"] = found.Count };
    }

    static string KindOf(InventoryItem it) => it switch
    {
        InventoryWearable w => w.WearableType.ToString().ToLowerInvariant(),
        InventoryObject => "object",
        InventoryLandmark => "landmark",
        InventoryNotecard => "notecard",
        InventoryTexture => "texture",
        InventoryLSL => "script",
        InventoryGesture => "gesture",
        InventoryAnimation => "animation",
        InventorySound => "sound",
        _ => AgentInventory.AssetName(it.AssetType),
    };

    /// <summary>Every query word should show up somewhere: the name counts most (whole words more than parts), then
    /// the type, the description and the folder path. Missing words cost a lot but don't rule an item out.</summary>
    static double Score(string[] words, InventoryItem it, string kind, string folder)
    {
        if (words.Length == 0) return 0;
        var name = it.Name.ToLowerInvariant();
        var nameWords = name.Split(new[] { ' ', '-', '_', '(', ')', '[', ']', '.', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var desc = it.Description?.ToLowerInvariant() ?? "";
        var path = folder.ToLowerInvariant();
        double score = 0;
        int missing = 0;
        foreach (var w in words)
        {
            double s = 0;
            if (nameWords.Contains(w)) s = 4;
            else if (nameWords.Any(n => n.StartsWith(w) || (w.Length > 3 && n.StartsWith(w.TrimEnd('s'))))) s = 3;
            else if (name.Contains(w)) s = 2;
            if (kind == w || (TypeWords.TryGetValue(kind, out var tw) && tw.Contains(w))) s = Math.Max(s, 2.5);
            if (s == 0 && desc.Contains(w)) s = 1;
            if (s == 0 && path.Contains(w)) s = 0.8;
            if (s == 0) missing++;
            score += s;
        }
        if (score == 0) return 0;
        return score - missing * 3 + 0.5 * (words.Length - missing) / words.Length;
    }
}
