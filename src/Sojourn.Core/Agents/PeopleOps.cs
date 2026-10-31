using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Friends and the people around them, as the viewers keep them: removing a friend and the rights you give one (the
/// viewer's profile Permissions floater: see me online, find me on the map, edit my objects; GrantUserRights, the map
/// only with online, as its checkbox is), Firestorm's contact sets (named, coloured groups of people, friends or not,
/// and pseudonyms; local to the account, lggcontactsets.cpp), an object's pay price buttons (llSetPayPrice, asked as
/// the pay floater asks: RequestPayPrice), and the favourites bar (landmarks in the Favorites folder).
/// Removing, rights and favourites taken away wait for the person at the client's confirmation gate.
/// Claude Opus 5.5, 2026-10-27.
/// </summary>
public sealed class PeopleOps
{
    readonly AgentSession _s;
    public PeopleOps(AgentSession s) => _s = s;
    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");
    PeopleStore Store => _s.PeopleStore;

    // ---------- friends ----------

    /// <summary>A friend by id or by a name they go by (display name, username, legacy name), or null.</summary>
    public FriendInfo? Friend(string? who)
    {
        if (string.IsNullOrWhiteSpace(who) || _s.Client is not { } c) return null;
        if (UUID.TryParse(who, out var id)) return c.Friends.FriendList.TryGetValue(id, out var f) ? f : null;
        var want = who.Trim();
        bool Is(string? s) => s is { Length: > 0 } && string.Equals(s.Trim(), want, StringComparison.OrdinalIgnoreCase);
        var all = c.Friends.FriendList.Values.ToList();
        return all.FirstOrDefault(f => Is(f.Name) || Is(_s.Names.DisplayName(f.UUID)) || Is(_s.Names.Legacy(f.UUID))
                                       || Is(_s.Names.Legacy(f.UUID) is { } l ? World.NameCache.UserName(l) : null) || Is(Pseudonym(f.UUID)));
    }

    FriendInfo FriendOrThrow(JsonObject a) =>
        Friend(a.Str("to") ?? a.Str("id")) ?? throw new LeapError("not one of my friends (to: their id or name)");

    public string NameOf(UUID id) => Pseudonym(id) ?? _s.Names.DisplayName(id) ?? _s.Names.Legacy(id)
        ?? (_s.Client?.Friends.FriendList.TryGetValue(id, out var f) == true && f.Name is { Length: > 0 } n ? n : id.ToString()[..8]);

    public static JsonObject RightsJson(FriendRights r) => new()
    {
        ["online"] = (r & FriendRights.CanSeeOnline) != 0,
        ["map"] = (r & FriendRights.CanSeeOnMap) != 0,
        ["modify"] = (r & FriendRights.CanModifyObjects) != 0,
    };

    /// <summary>The rights a friendRights call would leave them with (what's given, the rest as they are; map off
    /// with online off, as the viewer's floater turns it off).</summary>
    public FriendRights WantedRights(FriendInfo f, JsonObject a)
    {
        var r = f.TheirFriendRights;
        FriendRights Flip(FriendRights cur, FriendRights bit, bool? on) => on is null ? cur : on.Value ? cur | bit : cur & ~bit;
        r = Flip(r, FriendRights.CanSeeOnline, a.Bool("online"));
        r = Flip(r, FriendRights.CanSeeOnMap, a.Bool("map"));
        r = Flip(r, FriendRights.CanModifyObjects, a.Bool("modify"));
        if ((r & FriendRights.CanSeeOnline) == 0) r &= ~FriendRights.CanSeeOnMap;
        return r;
    }

    /// <summary>Friend rights: what they may do with me (rights_given) and I with them (rights_had); a call with any of
    /// online, map, modify changes what I give them.</summary>
    public async Task<JsonObject> RightsAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var f = FriendOrThrow(a);
        if (a.Bool("map") == true && a.Bool("online") != true && (f.TheirFriendRights & FriendRights.CanSeeOnline) == 0)
            throw new LeapError("they can only find you on the map if they can see you online: give online too");
        var want = WantedRights(f, a);
        bool changing = want != f.TheirFriendRights;
        bool confirmed = true;
        if (changing)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void On(object? _, FriendInfoEventArgs e) { if (e.Friend.UUID == f.UUID && e.Friend.TheirFriendRights == want) tcs.TrySetResult(); }
            c.Friends.FriendRightsUpdate += On;
            try
            {
                c.Friends.GrantRights(f.UUID, want);
                try { await tcs.Task.WaitAsync(TimeSpan.FromSeconds(6), ct); }
                catch (TimeoutException)
                {
                    // the grid may not echo it (the viewer updates its own copy as it sends); ours follows what was sent
                    confirmed = false;
                    f.TheirFriendRights = want;
                }
            }
            finally { c.Friends.FriendRightsUpdate -= On; }
        }
        var o = new JsonObject
        {
            ["id"] = f.UUID.ToString(), ["name"] = NameOf(f.UUID), ["online"] = f.IsOnline,
            ["rights_given"] = RightsJson(f.TheirFriendRights), ["rights_had"] = RightsJson(f.MyFriendRights),
        };
        if (changing) o[confirmed ? "changed" : "sent"] = true;
        return o;
    }

    /// <summary>Ends a friendship (TerminateFriendship), taking edit rights back first as the viewer's handleRemove does.</summary>
    public async Task<JsonObject> RemoveFriendAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var f = FriendOrThrow(a);
        var name = NameOf(f.UUID);
        if ((f.TheirFriendRights & FriendRights.CanModifyObjects) != 0)
        {
            c.Friends.GrantRights(f.UUID, f.TheirFriendRights & ~FriendRights.CanModifyObjects);
            await Task.Delay(300, ct);
        }
        c.Friends.TerminateFriendship(f.UUID);
        return new JsonObject { ["removed"] = name, ["id"] = f.UUID.ToString() };
    }

    // ---------- contact sets and pseudonyms (Firestorm's, local to the account) ----------

    const int MaxSets = 64, MaxNameLength = 64;

    static JsonObject Sets(JsonObject d) => d["contact_sets"] as JsonObject ?? (JsonObject)(d["contact_sets"] = new JsonObject());
    static JsonObject Pseudonyms(JsonObject d) => d["pseudonyms"] as JsonObject ?? (JsonObject)(d["pseudonyms"] = new JsonObject());

    /// <summary>A set by its name, as stored (names are matched without regard to case).</summary>
    static string? SetKey(JsonObject sets, string? name) =>
        name is null ? null : sets.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public string? Pseudonym(UUID id) => Store.Read(d => (d["pseudonyms"] as JsonObject)?.Str(id.ToString()));

    /// <summary>The colour of the first set (by name) someone is in, for name tags and the feed: [r, g, b] 0-1, or null.</summary>
    public float[]? SetColor(UUID id) => Store.Read(d =>
    {
        if (d["contact_sets"] is not JsonObject sets) return null;
        foreach (var (_, v) in sets.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            if (v is JsonObject s && (s["members"] as JsonArray)?.Any(m => (string?)m == id.ToString()) == true && s["color"] is JsonArray col && col.Count >= 3)
                return col.Take(3).Select(x => (float)(Json.AsDouble(x) ?? 1)).ToArray();
        return null;
    });

    /// <summary>The sets someone is in, by name.</summary>
    public List<string> SetsOf(UUID id) => Store.Read(d => (d["contact_sets"] as JsonObject ?? new JsonObject())
        .Where(kv => kv.Value is JsonObject s && (s["members"] as JsonArray)?.Any(m => (string?)m == id.ToString()) == true)
        .Select(kv => kv.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList());

    public JsonObject ContactSets(JsonObject a)
    {
        var only = a.Str("name");
        var copy = Store.Read(d => (JsonObject)Sets(d).DeepClone());
        var list = new JsonArray();
        foreach (var (name, v) in copy.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (only != null && !string.Equals(name, only, StringComparison.OrdinalIgnoreCase)) continue;
            var s = v as JsonObject ?? new JsonObject();
            var members = new JsonArray();
            foreach (var m in s["members"] as JsonArray ?? new JsonArray())
                if (UUID.TryParse((string?)m ?? "", out var id))
                    members.Add(new JsonObject { ["id"] = id.ToString(), ["name"] = NameOf(id), ["friend"] = _s.Client?.Friends.FriendList.ContainsKey(id) == true });
            list.Add(new JsonObject { ["name"] = name, ["color"] = s["color"]?.DeepClone(), ["members"] = members });
        }
        if (only != null && list.Count == 0) throw new LeapError($"no contact set \"{only}\"");
        var names = Store.Read(d => (JsonObject)Pseudonyms(d).DeepClone());
        var pseudo = new JsonArray(names.Select(kv => (JsonNode)new JsonObject
        {
            ["id"] = kv.Key, ["pseudonym"] = kv.Value?.DeepClone(),
            ["name"] = UUID.TryParse(kv.Key, out var pid) ? _s.Names.DisplayName(pid) ?? _s.Names.Legacy(pid) : null,
        }).ToArray());
        return new JsonObject { ["sets"] = list, ["pseudonyms"] = pseudo };
    }

    static JsonArray Color(JsonNode? n)
    {
        if (n is JsonArray arr && arr.Count >= 3 && arr.Take(3).All(x => Json.AsDouble(x) is >= 0 and <= 1))
            return new JsonArray(arr.Take(3).Select(x => (JsonNode)Math.Round(Json.AsDouble(x)!.Value, 3)).ToArray());
        if (n is JsonValue v && v.TryGetValue<string>(out var hex) && hex.TrimStart('#') is { Length: 6 } h
            && int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return new JsonArray(Math.Round((rgb >> 16 & 255) / 255.0, 3), Math.Round((rgb >> 8 & 255) / 255.0, 3), Math.Round((rgb & 255) / 255.0, 3));
        throw new LeapError("color: [r, g, b] from 0 to 1, or \"#rrggbb\"");
    }

    /// <summary>Makes, changes (colour, a new name) or deletes a contact set.</summary>
    public JsonObject ContactSet(JsonObject a)
    {
        var name = (a.Str("name") ?? "").Trim();
        if (name.Length == 0) throw new LeapError("need name");
        if (name.Length > MaxNameLength) throw new LeapError($"a set's name: {MaxNameLength} characters at most");
        JsonArray? color = a.Has("color") ? Color(a["color"]) : null;
        var rename = a.Str("rename")?.Trim();
        bool remove = a.Bool("remove", false);
        return Store.Change(d =>
        {
            var sets = Sets(d);
            var key = SetKey(sets, name);
            if (remove)
            {
                if (key is null) throw new LeapError($"no contact set \"{name}\"");
                sets.Remove(key);
                return new JsonObject { ["removed"] = key };
            }
            if (key is null)
            {
                if (sets.Count >= MaxSets) throw new LeapError($"{MaxSets} contact sets at most");
                sets[name] = new JsonObject { ["color"] = color ?? new JsonArray(1.0, 1.0, 1.0), ["members"] = new JsonArray() };
                key = name;
            }
            else if (color != null) ((JsonObject)sets[key]!)["color"] = color;
            if (rename is { Length: > 0 } && rename != key)
            {
                if (rename.Length > MaxNameLength) throw new LeapError($"a set's name: {MaxNameLength} characters at most");
                if (SetKey(sets, rename) is { } other && other != key) throw new LeapError($"there's already a set \"{other}\"");
                var node = sets[key]!.DeepClone();
                sets.Remove(key);
                sets[rename] = node;
                key = rename;
            }
            return new JsonObject { ["name"] = key, ["color"] = ((JsonObject)sets[key]!)["color"]?.DeepClone() };
        });
    }

    /// <summary>People into or out of a set (anyone, as Firestorm's "Add to contact set" on any avatar).</summary>
    public JsonObject Members(JsonObject a, bool add)
    {
        var name = a.Str("name") ?? a.Str("set") ?? throw new LeapError("need name (the set)");
        var ids = a.UuidList("ids");
        if (a.Str("to") is { } to)
        {
            if (UUID.TryParse(to, out var tid)) ids.Add(tid);
            else if (Friend(to) is { } f) ids.Add(f.UUID);
            else throw new LeapError($"\"{to}\" isn't an id or a friend's name");
        }
        if (ids.Count == 0) throw new LeapError("need to (an id or a friend's name) or ids");
        return Store.Change(d =>
        {
            var sets = Sets(d);
            var key = SetKey(sets, name) ?? throw new LeapError($"no contact set \"{name}\" (contactSet makes one)");
            var s = (JsonObject)sets[key]!;
            var members = s["members"] as JsonArray ?? (JsonArray)(s["members"] = new JsonArray());
            var have = members.Select(m => (string?)m).ToHashSet();
            foreach (var id in ids.Distinct())
            {
                if (add && have.Add(id.ToString())) members.Add(id.ToString());
                if (!add && have.Remove(id.ToString()))
                    foreach (var m in members.Where(m => (string?)m == id.ToString()).ToList()) members.Remove(m);
            }
            return new JsonObject { ["name"] = key, ["members"] = members.Count };
        });
    }

    /// <summary>A pseudonym (Firestorm's alias, shown instead of their name to the person only); empty clears it.</summary>
    public JsonObject SetPseudonym(JsonObject a)
    {
        var who = a.Str("to") ?? throw new LeapError("need to (an id or a friend's name)");
        var id = UUID.TryParse(who, out var tid) ? tid : Friend(who)?.UUID ?? throw new LeapError($"\"{who}\" isn't an id or a friend's name");
        var name = (a.Str("name") ?? "").Trim();
        if (name.Length > MaxNameLength) throw new LeapError($"{MaxNameLength} characters at most");
        Store.Change(d =>
        {
            if (name.Length == 0) Pseudonyms(d).Remove(id.ToString());
            else Pseudonyms(d)[id.ToString()] = name;
        });
        if (name.Length > 0) PseudonymSet?.Invoke(id, name);
        return new JsonObject { ["id"] = id.ToString(), ["pseudonym"] = name.Length > 0 ? name : null };
    }

    /// <summary>A pseudonym given (for the session log's names, as <see cref="World.NameCache.Learned"/>).</summary>
    public event Action<UUID, string>? PseudonymSet;

    /// <summary>Every pseudonym kept for this account: id, name.</summary>
    public List<(UUID Id, string Name)> AllPseudonyms() => Store.Read(d => Pseudonyms(d)
        .Select(kv => (UUID.TryParse(kv.Key, out var id) ? id : UUID.Zero, kv.Value?.ToString() ?? ""))
        .Where(p => p.Item1 != UUID.Zero && p.Item2.Length > 0).ToList());

    // ---------- pay price buttons ----------

    sealed record Prices(int Default, int[] Buttons, DateTime At);
    readonly Dictionary<UUID, Prices> _prices = new();

    public const int PayPriceHide = -1, PayPriceDefault = -2;

    /// <summary>An object's pay prices (llSetPayPrice) as the pay floater reads them, asked afresh unless seen in the
    /// last 30 s; null when the region doesn't answer in a few seconds (an object without a money() event may not).</summary>
    public async Task<(int Default, int[] Buttons)?> PayPricesAsync(UUID id, CancellationToken ct)
    {
        lock (_prices)
            if (_prices.TryGetValue(id, out var p) && DateTime.UtcNow - p.At < TimeSpan.FromSeconds(30)) return (p.Default, p.Buttons);
        var c = C;
        var sim = _s.Region is { } w && w.PrimById(id) is { } ? c.Network.CurrentSim : null;
        // a neighbour's object: its own region answers
        foreach (var r in _s.VisibleRegions())
            if (r.PrimById(id) is not null) { sim = c.Network.Simulators.FirstOrDefault(x => x.Handle == r.Handle) ?? sim; break; }
        if (sim is null) throw new LeapError("that object isn't in view");
        var tcs = new TaskCompletionSource<PayPriceReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(object? _, PayPriceReplyEventArgs e) { if (e.ObjectID == id) tcs.TrySetResult(e); }
        c.Objects.PayPriceReply += On;
        try
        {
            c.Objects.RequestPayPrice(sim, id);
            var e = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(4), ct);
            var got = new Prices(e.DefaultPrice, e.ButtonPrices ?? Array.Empty<int>(), DateTime.UtcNow);
            lock (_prices) _prices[id] = got;
            return (got.Default, got.Buttons);
        }
        catch (TimeoutException) { return null; }
        finally { c.Objects.PayPriceReply -= On; }
    }

    public static JsonObject PricesJson(int dflt, int[] buttons) => new()
    {
        // the amount field: hidden (only the buttons), any amount, or prefilled with a price
        ["any_amount"] = dflt != PayPriceHide,
        ["suggested"] = dflt > 0 ? dflt : null,
        ["buttons"] = new JsonArray(buttons.Where(b => b > 0).Select(b => (JsonNode)b).ToArray()),
    };

    /// <summary>Why paying this object this amount is refused, as the pay floater wouldn't offer it (its amount field
    /// hidden and no button for it), or null.</summary>
    public static string? PriceRefusal(int dflt, int[] buttons, int amount)
    {
        if (dflt != PayPriceHide) return null;
        var ok = buttons.Where(b => b > 0).ToList();
        if (ok.Contains(amount)) return null;
        return ok.Count == 0 ? "that object takes no payments now (its pay window has no amount and no buttons)"
            : $"that object takes only {string.Join(", ", ok.Select(b => $"L${b}"))} (its pay buttons); L${amount} isn't one";
    }

    public async Task<JsonObject> PayPricesJsonAsync(JsonObject a, CancellationToken ct)
    {
        var id = a.Uuid("id") ?? throw new LeapError("need id (an object)");
        if (await PayPricesAsync(id, ct) is not { } p) return new JsonObject { ["id"] = id.ToString(), ["answered"] = false, ["note"] = "the region didn't say; any amount may work" };
        var o = PricesJson(p.Default, p.Buttons);
        o["id"] = id.ToString();
        return o;
    }

    // ---------- favourites (the favourites bar: landmarks in the Favorites folder) ----------

    UUID FavoritesFolder => _s.Inventory.FolderFor(FolderType.Favorites);

    public async Task<JsonObject> FavoritesAsync(CancellationToken ct)
    {
        var c = C;
        var folder = FavoritesFolder;
        if (folder == UUID.Zero) throw new LeapError("no Favorites folder in the inventory");
        await _s.Inventory.EnsureLoadedAsync(folder, ct);
        var list = new JsonArray();
        foreach (var it in (c.Inventory.Store?.GetContents(folder) ?? new List<InventoryBase>()).OfType<InventoryItem>())
        {
            var target = it.AssetType == AssetType.Link && _s.Inventory.Node(it.AssetUUID) is InventoryItem t ? t : it;
            if (target.AssetType != AssetType.Landmark) continue;
            list.Add(new JsonObject { ["item_id"] = it.UUID.ToString(), ["name"] = it.Name, ["desc"] = target.Description, ["link"] = it.AssetType == AssetType.Link });
        }
        return new JsonObject { ["favorites"] = list, ["folder_id"] = folder.ToString() };
    }

    /// <summary>A favourite: a landmark of here made in Favorites (the viewer's star in the location bar), or a copy of a
    /// landmark of mine put there (dragging one onto the favourites bar copies it).</summary>
    public async Task<JsonObject> AddFavoriteAsync(JsonObject a, CancellationToken ct)
    {
        var folder = FavoritesFolder;
        if (folder == UUID.Zero) throw new LeapError("no Favorites folder in the inventory");
        if (a.Has("item_id"))
        {
            var it = await _s.InventoryOps.ItemAsync(a, ct);
            var target = it.AssetType == AssetType.Link && _s.Inventory.Node(it.AssetUUID) is InventoryItem t ? t : it;
            if (target.AssetType != AssetType.Landmark) throw new LeapError($"{it.Name} isn't a landmark");
            if ((target.Permissions.OwnerMask & PermissionMask.Copy) == 0)
            {
                _s.InventoryOps.Move(new JsonObject { ["id"] = target.UUID.ToString(), ["folder_id"] = folder.ToString() });
                return new JsonObject { ["item_id"] = target.UUID.ToString(), ["name"] = target.Name, ["moved"] = true };
            }
            var copy = await _s.InventoryOps.CopyAsync(new JsonObject { ["id"] = target.UUID.ToString(), ["dest_id"] = folder.ToString(), ["name"] = a.Str("name") ?? target.Name }, ct);
            return copy;
        }
        return await _s.InventoryOps.CreateLandmarkAsync(new JsonObject { ["folder_id"] = folder.ToString(), ["name"] = a.Str("name") }, ct);
    }

    /// <summary>A favourite taken off the bar: it goes to Trash, as the viewer's Delete on it.</summary>
    public async Task<JsonObject> RemoveFavoriteAsync(JsonObject a, CancellationToken ct)
    {
        var it = await _s.InventoryOps.ItemAsync(a, ct);
        if (it.ParentUUID != FavoritesFolder) throw new LeapError($"{it.Name} isn't one of the favourites");
        await _s.InventoryOps.DeleteAsync(new JsonObject { ["id"] = it.UUID.ToString() }, ct);
        return new JsonObject { ["removed"] = it.Name };
    }

    public bool IsFavorite(UUID item) => _s.Inventory.Node(item) is { } n && n.ParentUUID == FavoritesFolder && FavoritesFolder != UUID.Zero;
}
