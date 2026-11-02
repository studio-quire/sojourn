using System.Reflection;
using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Assets;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Gestures as the viewer's gesture editor makes them: a sequence of animation, sound, chat and wait steps, a chat
/// trigger, uploaded as a gesture asset into a Gestures item; activated and deactivated like the Active checkbox
/// (ActivateGestures / DeactivateGestures). Written by Claude Opus 5.5, 2026-10-16.
///
/// Steps (JSON, one key each): {"animation": X, "stop": bool?}, {"sound": X}, {"chat": "text"},
/// {"wait": seconds} or {"wait": "anims"}. X is an inventory item id (its asset is used), an asset id, or the name
/// of a built-in animation (clap, wave, bow, laugh_short, ...: LibreMetaverse's Animations list).
/// </summary>
public sealed class GestureOps
{
    readonly AgentSession _s;
    public GestureOps(AgentSession s) => _s = s;
    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    static readonly Dictionary<string, UUID> BuiltIn = typeof(Animations)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(UUID))
        .ToDictionary(f => f.Name.ToLowerInvariant(), f => (UUID)f.GetValue(null)!)
        .Concat(new[] { ("wave", Animations.HELLO), ("laugh", Animations.LAUGH_SHORT), ("nod", Animations.YES),
                        ("headshake", Animations.NO) }.Select(t => new KeyValuePair<string, UUID>(t.Item1, t.Item2)))
        .GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value);

    public static IEnumerable<string> BuiltInNames => BuiltIn.Keys.OrderBy(k => k);
    public static UUID? BuiltInId(string name) => BuiltIn.TryGetValue(name.Trim().ToLowerInvariant().Replace(' ', '_'), out var id) ? id : null;

    public static string? BuiltInName(UUID id) => BuiltIn.FirstOrDefault(kv => kv.Value == id).Key;

    // decoded gesture assets, by asset id (assets never change; an edit makes a new one)
    static readonly System.Collections.Concurrent.ConcurrentDictionary<UUID, AssetGesture> Decoded = new();

    async Task<(UUID Id, string Name)> AssetRefAsync(string what, string x, AssetType want, CancellationToken ct)
    {
        if (UUID.TryParse(x, out var id))
        {
            if (await _s.Inventory.ItemAsync(id, ct) is { } item)
            {
                if (item.AssetType != want) throw new LeapError($"{what}: {item.Name} is a {AgentInventory.AssetTypeLabel(item.AssetType)}");
                if (item.AssetUUID == UUID.Zero) throw new LeapError($"{what}: can't see the asset of {item.Name} (no-copy/no-mod items hide it)");
                return (item.AssetUUID, item.Name);
            }
            return (id, want == AssetType.Animation ? BuiltInName(id) ?? "animation" : "sound");
        }
        if (want == AssetType.Animation && BuiltIn.TryGetValue(x.Trim().ToLowerInvariant().Replace(' ', '_'), out var b))
            return (b, x.Trim().ToLowerInvariant());
        throw new LeapError($"{what}: {x} isn't an item id, an asset id{(want == AssetType.Animation ? " or a built-in animation name (TesselInventory gestureAnimations)" : "")}");
    }

    async Task<List<GestureStep>> StepsAsync(JsonArray steps, CancellationToken ct)
    {
        var list = new List<GestureStep>();
        var i = 0;
        foreach (var node in steps)
        {
            i++;
            if (node is not JsonObject s) throw new LeapError($"step {i}: give an object like {{\"animation\": \"clap\"}}");
            if (s.Str("animation") is { } anim)
            {
                var (id, name) = await AssetRefAsync($"step {i}", anim, AssetType.Animation, ct);
                list.Add(new GestureStepAnimation { ID = id, Name = name, AnimationStart = !s.Bool("stop", false) });
            }
            else if (s.Str("sound") is { } snd)
            {
                var (id, name) = await AssetRefAsync($"step {i}", snd, AssetType.Sound, ct);
                list.Add(new GestureStepSound { ID = id, Name = name });
            }
            else if (s.Str("chat") is { } text) list.Add(new GestureStepChat { Text = text });
            else if (s.Has("wait"))
            {
                if (s.Str("wait") is "anims" or "animations") list.Add(new GestureStepWait { WaitForAnimation = true });
                else if (s.Num("wait") is { } secs && secs >= 0) list.Add(new GestureStepWait { WaitForTime = true, WaitTime = (float)Math.Min(secs, 3600) });
                else throw new LeapError($"step {i}: wait takes seconds or \"anims\"");
            }
            else throw new LeapError($"step {i}: needs one of animation, sound, chat, wait");
        }
        if (list.Count == 0) throw new LeapError("a gesture needs at least one step");
        return list;
    }

    static JsonArray StepsJson(AssetGesture g)
    {
        var arr = new JsonArray();
        foreach (var st in g.Sequence)
            switch (st)
            {
                case GestureStepAnimation a:
                    var o = new JsonObject { ["animation"] = a.ID.ToString(), ["name"] = a.Name };
                    if (!a.AnimationStart) o["stop"] = true;
                    arr.Add(o);
                    break;
                case GestureStepSound s: arr.Add(new JsonObject { ["sound"] = s.ID.ToString(), ["name"] = s.Name }); break;
                case GestureStepChat c: arr.Add(new JsonObject { ["chat"] = c.Text }); break;
                case GestureStepWait w:
                    if (w.WaitForAnimation) arr.Add(new JsonObject { ["wait"] = "anims" });
                    if (w.WaitForTime) arr.Add(new JsonObject { ["wait"] = Math.Round(w.WaitTime, 3) });
                    break;
            }
        return arr;
    }

    AssetGesture Build(List<GestureStep> steps, string trigger, string replace)
    {
        var g = new AssetGesture { Trigger = trigger, ReplaceWith = replace };
        g.Sequence.AddRange(steps);
        g.Sequence.Add(new GestureStepEOF());
        g.Encode();
        return g;
    }

    /// <summary>Uploads a gesture asset into an existing gesture item (UpdateGestureAgentInventory, as the viewer
    /// saves); on a grid without the capability, an Xfer upload plus an item update.</summary>
    async Task<UUID> SaveAsync(InventoryItem item, AssetGesture g, CancellationToken ct)
    {
        var c = C;
        UUID asset;
        try
        {
            var (ok, status, _, aid) = await c.Inventory.RequestUploadGestureAssetAsync(g.AssetData, item.UUID, ct);
            if (!ok) throw new LeapError($"saving the gesture failed: {status}");
            asset = aid;
        }
        catch (InvalidOperationException)
        {
            var tx = UUID.Random();
            asset = await c.Assets.RequestUploadAsync(AssetType.Gesture, g.AssetData, false, tx, ct);
            c.Inventory.RequestUpdateItems(new List<InventoryItem> { item }, tx);
        }
        if (asset != UUID.Zero)
        {
            item.AssetUUID = asset;
            Decoded[asset] = g;
            // an active gesture plays its asset: keep the active list on the new one
            if (c.Self.ActiveGestures.ContainsKey(item.UUID)) c.Self.ActivateGesture(item.UUID, asset);
        }
        return asset;
    }

    public async Task<JsonObject> CreateAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var name = a.Str("name") ?? throw new LeapError("need name");
        var steps = await StepsAsync(a["steps"] as JsonArray ?? throw new LeapError("need steps (a list)"), ct);
        var g = Build(steps, a.Str("trigger") ?? "", a.Str("replace") ?? "");
        var folder = a.Uuid("folder_id") ?? _s.Inventory.FolderFor(FolderType.Gesture);
        var next = a.Str("next_perms") is { } np ? PermissionMask.Move | ObjectViews.PermMask(np) : _s.Ownership.DefaultsFor("gestures").NextMask;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(a.Num("timeout", 30)));
        var item = await c.Inventory.CreateItemAsync(folder, name, a.Str("desc") ?? "", AssetType.Gesture, UUID.Zero,
            InventoryType.Gesture, next, cts.Token) ?? throw new LeapError("the gesture item wasn't created");
        var asset = await SaveAsync(item, g, cts.Token);
        var active = false;
        if (a.Bool("activate", true) && asset != UUID.Zero) { Activate(item, asset); active = true; }
        LearnSounds(steps);
        return new JsonObject
        {
            ["item_id"] = item.UUID.ToString(), ["asset_id"] = asset.ToString(), ["name"] = name, ["active"] = active,
            ["trigger"] = g.Trigger, ["steps"] = StepsJson(g),
        };
    }

    public async Task<JsonObject> EditAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var item = await ItemAsync(a, ct);
        if ((item.Permissions.OwnerMask & PermissionMask.Modify) == 0) throw new LeapError($"{item.Name} is no-modify");
        var old = await DecodeAsync(item, ct);
        var steps = a["steps"] is JsonArray js ? await StepsAsync(js, ct) : old.Sequence.Where(s => s is not GestureStepEOF).ToList();
        var g = Build(steps, a.Str("trigger") ?? old.Trigger, a.Str("replace") ?? old.ReplaceWith);
        if (a.Str("name") is { } n || a.Str("desc") is not null)
        {
            item.Name = a.Str("name") ?? item.Name;
            item.Description = a.Str("desc") ?? item.Description;
            c.Inventory.RequestUpdateItem(item);
        }
        var asset = await SaveAsync(item, g, ct);
        LearnSounds(steps);
        return new JsonObject
        {
            ["item_id"] = item.UUID.ToString(), ["asset_id"] = asset.ToString(), ["name"] = item.Name,
            ["active"] = c.Self.ActiveGestures.ContainsKey(item.UUID), ["trigger"] = g.Trigger, ["steps"] = StepsJson(g),
        };
    }

    void LearnSounds(List<GestureStep> steps)
    {
        foreach (var s in steps.OfType<GestureStepSound>()) if (s.ID != UUID.Zero && !string.IsNullOrEmpty(s.Name)) _s.Ears.Learn(s.ID, s.Name);
    }

    async Task<InventoryItem> ItemAsync(JsonObject a, CancellationToken ct)
    {
        var item = await _s.InventoryOps.ItemAsync(a, ct);
        if (item.AssetType != AssetType.Gesture) throw new LeapError($"{item.Name} isn't a gesture");
        return item;
    }

    public async Task<AssetGesture> DecodeAsync(InventoryItem item, CancellationToken ct) =>
        await DecodeAssetAsync(item.AssetUUID, ct) ?? throw new LeapError($"the gesture {item.Name} couldn't be read");

    public async Task<AssetGesture?> DecodeAssetAsync(UUID asset, CancellationToken ct)
    {
        if (asset == UUID.Zero) return null;
        if (Decoded.TryGetValue(asset, out var g)) return g;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var fetched = await C.Assets.RequestAssetAsync(asset, AssetType.Gesture, true, cts.Token);
        if (fetched is not AssetGesture ag || !ag.Decode()) return null;
        Decoded[asset] = ag;
        return ag;
    }

    /// <summary>The trigger of an active gesture without waiting on the network (null until decoded once).</summary>
    public static string? KnownTrigger(UUID asset) => Decoded.TryGetValue(asset, out var g) ? g.Trigger : null;

    public async Task<JsonObject> ReadAsync(JsonObject a, CancellationToken ct)
    {
        var item = await ItemAsync(a, ct);
        var g = await DecodeAsync(item, ct);
        return new JsonObject
        {
            ["item_id"] = item.UUID.ToString(), ["name"] = item.Name, ["asset_id"] = item.AssetUUID.ToString(),
            ["active"] = C.Self.ActiveGestures.ContainsKey(item.UUID), ["trigger"] = g.Trigger, ["replace"] = g.ReplaceWith,
            ["steps"] = StepsJson(g),
        };
    }

    void Activate(InventoryItem item, UUID asset)
    {
        C.Self.ActivateGesture(item.UUID, asset);
        item.Flags |= 1;  // the server keeps the flag too; RestoreActiveGestures reads it at the next login
    }

    public async Task<JsonObject> ActivateAsync(JsonObject a, bool on, CancellationToken ct)
    {
        var c = C;
        var ids = a.UuidList("item_ids");
        if (a.Uuid("item_id") is { } one && !ids.Contains(one)) ids.Add(one);
        if (a.Uuid("id") is { } two && !ids.Contains(two)) ids.Add(two);
        if (ids.Count == 0) throw new LeapError("need item_id or item_ids");
        var done = new JsonArray();
        foreach (var id in ids)
        {
            var item = await _s.Inventory.ItemAsync(id, ct) ?? throw new LeapError($"no inventory item {id}");
            if (item.AssetType == AssetType.Link && _s.Inventory.Node(item.AssetUUID) is InventoryItem t) item = t;
            if (item.AssetType != AssetType.Gesture) throw new LeapError($"{item.Name} isn't a gesture");
            if (on)
            {
                if (_s.InventoryOps.InLibrary(item.UUID)) throw new LeapError($"{item.Name} is in the Library: copy it to my inventory first (TesselInventory copy)");
                Activate(item, item.AssetUUID);
                _ = DecodeAssetAsync(item.AssetUUID, CancellationToken.None);  // so triggers are known
            }
            else
            {
                c.Self.DeactivateGesture(item.UUID);
                item.Flags &= ~1u;
            }
            done.Add(item.UUID.ToString());
        }
        return new JsonObject { [on ? "activated" : "deactivated"] = done };
    }

    /// <summary>Every gesture in my inventory (Trash skipped), active ones first.</summary>
    public async Task<JsonObject> ListAsync(CancellationToken ct)
    {
        var c = C;
        if (_s.Inventory.Loading is { } loading) await Task.WhenAny(loading, Task.Delay(TimeSpan.FromSeconds(10), ct));
        var found = await _s.Inventory.CollectAsync(_s.Inventory.FolderFor(FolderType.Root), null, null, "gesture", "EXCLUDE_LINKS", 0, ct);
        var arr = new List<JsonObject>();
        foreach (var (id, node) in found["items"] as JsonObject ?? new JsonObject())
        {
            if (node is not JsonObject j || !UUID.TryParse(id, out var uid)) continue;
            var active = c.Self.ActiveGestures.ContainsKey(uid);
            var o = new JsonObject { ["item_id"] = id, ["name"] = j["name"]?.DeepClone(), ["active"] = active, ["asset_id"] = j["asset_id"]?.DeepClone() };
            if (UUID.TryParse(j["asset_id"]?.GetValue<string>(), out var asset))
            {
                var g = await DecodeAssetAsync(asset, ct);
                if (g is not null) o["trigger"] = g.Trigger;
            }
            arr.Add(o);
        }
        var sorted = new JsonArray();
        foreach (var o in arr.OrderByDescending(o => o["active"]!.GetValue<bool>()).ThenBy(o => o["name"]?.GetValue<string>())) sorted.Add(o);
        return new JsonObject { ["gestures"] = sorted };
    }

    // --- playing (LLGestureMgr's player): a gesture's steps run in turn, its chat said as the person's own nearby chat ---

    readonly System.Collections.Concurrent.ConcurrentDictionary<UUID, CancellationTokenSource> _playing = new();

    public bool IsPlaying(UUID itemId) => _playing.ContainsKey(itemId);

    /// <summary>Starts a gesture of mine by item id (an active one's asset, else the item's own; a link's target), its
    /// name, or null when it isn't a gesture I have. One already playing isn't started again (the viewer's rule).</summary>
    public string? Start(UUID itemId)
    {
        var c = C;
        if (_s.Inventory.Node(itemId) is InventoryItem { AssetType: AssetType.Link } link && _s.Inventory.Node(link.AssetUUID) is InventoryItem t)
            itemId = t.UUID;
        var item = _s.Inventory.Node(itemId) as InventoryItem;
        if (!c.Self.ActiveGestures.TryGetValue(itemId, out var asset))
        {
            if (item is not { AssetType: AssetType.Gesture } || item.AssetUUID == UUID.Zero) return null;
            asset = item.AssetUUID;
        }
        var name = item?.Name ?? "gesture";
        if (_s.RlvActive && _s.RlvRefused(_s.Rlv.CanGesture()) is { } rlv) throw new LeapError(rlv);   // @sendgesture
        var cts = new CancellationTokenSource();
        if (!_playing.TryAdd(itemId, cts)) return name;
        _ = Task.Run(async () =>
        {
            try { await RunAsync(c, asset, cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception e) { _s.Log.LogWarning(e, "gesture {Name}", name); }
            finally { _playing.TryRemove(new KeyValuePair<UUID, CancellationTokenSource>(itemId, cts)); }
        });
        return name;
    }

    /// <summary>Stops a gesture playing (its animations still going stopped); whether it was.</summary>
    public bool Stop(UUID itemId)
    {
        if (!_playing.TryRemove(itemId, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    /// <summary>Plays a gesture to its end (LLGesture's old way in; Start is the one to use).</summary>
    public async Task<bool> PlayAsync(UUID itemId, CancellationToken ct)
    {
        if (Start(itemId) is null) return false;
        using var reg = ct.Register(() => Stop(itemId));
        while (IsPlaying(itemId)) await Task.Delay(100, CancellationToken.None);
        return true;
    }

    /// <summary>The steps as LLGestureMgr runs them: animations started or stopped, sounds triggered in the region from
    /// where I am (the region plays them to everyone, me too), chat said as my own nearby chat ("/5 text" on its
    /// channel), waits for a time or for the animations it started to end; at the end it waits for those as the viewer
    /// does (mWaitingAtEnd), so a looping one keeps it playing until it's stopped. Stopped early, what it started stops.</summary>
    async Task RunAsync(GridClient c, UUID asset, CancellationToken ct)
    {
        var g = await DecodeAssetAsync(asset, ct) ?? throw new LeapError("the gesture couldn't be read");
        var started = new HashSet<UUID>();
        try
        {
            foreach (var step in g.Sequence)
            {
                ct.ThrowIfCancellationRequested();
                switch (step)
                {
                    case GestureStepAnimation a when a.AnimationStart:
                        c.Self.AnimationStart(a.ID, true);
                        started.Add(a.ID);
                        break;
                    case GestureStepAnimation a:
                        c.Self.AnimationStop(a.ID, true);
                        started.Remove(a.ID);
                        break;
                    case GestureStepSound s:
                        c.Sound.SendSoundTrigger(s.ID, c.Self.SimPosition, 1f);
                        break;
                    case GestureStepChat ch when !string.IsNullOrWhiteSpace(ch.Text):
                        Say(c, ch.Text);
                        break;
                    case GestureStepWait w:
                        if (w.WaitForTime) await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(w.WaitTime, 0, 3600)), ct);
                        if (w.WaitForAnimation) await AnimationsEndAsync(started, ct, TimeSpan.FromSeconds(30));
                        break;
                }
            }
            await AnimationsEndAsync(started, ct, Timeout.InfiniteTimeSpan);
        }
        catch (OperationCanceledException)
        {
            foreach (var id in started) c.Self.AnimationStop(id, true);
            throw;
        }
    }

    static readonly System.Text.RegularExpressions.Regex ChannelPrefix = new(@"^/(-?\d+)\s?(.*)$", System.Text.RegularExpressions.RegexOptions.Singleline);

    void Say(GridClient c, string text)
    {
        int channel = 0;
        if (ChannelPrefix.Match(text) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var ch) && ch >= 0)
        {
            channel = ch;
            text = m.Groups[2].Value;
        }
        var type = ChatType.Normal;
        if (_s.RlvActive)
        {
            // a gesture's words are the person's chat: RLV's chat restrictions hold (docs/rlv.md)
            string? said;
            (said, channel, type, _) = _s.Rlv.Chat(text, channel, type);
            if (said == null) return;
            text = said;
        }
        c.Self.Chat(text, channel, type);
        if (channel == 0) _s.State.LastWords = text;
    }

    /// <summary>Until none of these animations plays on me any more (one the region never listed counts as ended a
    /// second on), or the limit.</summary>
    async Task AnimationsEndAsync(HashSet<UUID> anims, CancellationToken ct, TimeSpan limit)
    {
        if (anims.Count == 0) return;
        var seen = new HashSet<UUID>();
        var from = DateTime.UtcNow;
        while (limit == Timeout.InfiniteTimeSpan || DateTime.UtcNow - from < limit)
        {
            var now = _s.Anims.Now().Select(p => p.Id).ToHashSet();
            seen.UnionWith(anims.Where(now.Contains));
            if (anims.All(a => !now.Contains(a) && (seen.Contains(a) || DateTime.UtcNow - from > TimeSpan.FromSeconds(1)))) return;
            await Task.Delay(100, ct);
        }
    }

    /// <summary>What nearby chat does with a line before it's said (LLGestureMgr::triggerAndReviseString): the first
    /// word (split at spaces, as the viewer does) that's an active gesture's trigger, case aside, plays that gesture (one
    /// at random when several share it), and the line is said with that word replaced by the gesture's replacement text,
    /// or left out when it has none; null when no word is a trigger. Only for channel 0, which is the caller's to know.</summary>
    public (UUID Item, string Name, string Revised)? Trigger(string line)
    {
        if (_s.Client is not { } c || c.Self.ActiveGestures.IsEmpty) return null;
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            var matches = c.Self.ActiveGestures
                .Where(kv => Decoded.TryGetValue(kv.Value, out var g) && g.Trigger.Length > 0 && string.Equals(g.Trigger, words[i], StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0) continue;
            var (item, asset) = matches[Random.Shared.Next(matches.Count)];
            var replace = Decoded[asset].ReplaceWith ?? "";
            // (the person's own capitals kept when the replacement is the word itself)
            var word = replace.Length == 0 ? null : string.Equals(replace, words[i], StringComparison.OrdinalIgnoreCase) ? words[i] : replace;
            var revised = string.Join(' ', words.Take(i).Concat(word is null ? Array.Empty<string>() : new[] { word }).Concat(words.Skip(i + 1)));
            var name = Start(item) ?? "gesture";
            return (item, name, revised.Trim());
        }
        return null;
    }

    /// <summary>Trigger, after reading any active gesture whose asset isn't known yet (at most <paramref name="wait"/>):
    /// the login's background read can fail or still be running, and a trigger typed then was said as text (the blind
    /// tester's "/clap", 2026-11-01).</summary>
    public async Task<(UUID Item, string Name, string Revised)?> TriggerAsync(string line, TimeSpan wait)
    {
        if (Trigger(line) is { } hit) return hit;
        if (_s.Client is not { } c) return null;
        var missing = c.Self.ActiveGestures.Values.Distinct().Where(a => a != UUID.Zero && !Decoded.ContainsKey(a)).ToList();
        if (missing.Count == 0) return null;
        using var cts = new CancellationTokenSource(wait);
        var reads = missing.Select(a => Task.Run(async () =>
        {
            try { await DecodeAssetAsync(a, cts.Token); }
            catch (Exception e) { _s.Log.LogDebug(e, "gesture asset {Asset}", a); }
        }));
        try { await Task.WhenAll(reads).WaitAsync(wait); } catch (TimeoutException) { }
        int still = missing.Count(a => !Decoded.ContainsKey(a));
        if (still > 0) _s.Log.LogInformation("gestures: {Still} of {Count} active gestures' assets not read yet; a trigger among them can't be matched", still, missing.Count);
        return Trigger(line);
    }

    /// <summary>The active gestures' assets read in the background, so their triggers are known before they're typed.</summary>
    public void WarmTriggers()
    {
        if (_s.Client is not { } c) return;
        foreach (var asset in c.Self.ActiveGestures.Values.Distinct().ToList())
            _ = Task.Run(async () =>
            {
                try { await DecodeAssetAsync(asset, CancellationToken.None); }
                catch (Exception e) { _s.Log.LogDebug(e, "gesture asset {Asset}", asset); }
            });
    }
}
