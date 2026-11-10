using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// Putting herself right, and the inventory's housekeeping: what you do when someone says you're grey or stuck in a pose,
/// and can't see whether it's so. World's menu and `//` commands: rebake, stop animations, hover height, display name, the
/// rating of regions she goes into, emptying the Trash. Attach to a point, Play for sounds and animations.
/// </summary>
public sealed partial class MainForm
{
    async Task SelfWordsAsync(string verb, string words)
    {
        words = words.Trim();
        switch (verb)
        {
            case "rebake": await RebakeAsync(); break;
            case "stopanimations": await StopAnimationsAsync(); break;
            case "hover": await HoverAsync(words); break;
            case "displayname": await DisplayNameAsync(words); break;
            case "maturity": await MaturityAsync(words); break;
            case "emptytrash": await EmptyTrashAsync(); break;
            case "emptylostandfound": await EmptyLostAndFoundAsync(); break;
            case "run": AlwaysRunWords(words); break;
            case "walkround": WalkRoundWords(words); break;
            case "saveoutfit": await SaveOutfitAsync(words); break;
        }
    }

    async Task RebakeAsync()
    {
        var r = await _ops.DoAsync("TesselAgent", "rebake");
        Answer(PersonOps.Error(r) is { } e ? $"Didn't rebake: {e}" : "Rebaking: your look goes to the grid again. People near you see it within a minute or so.");
    }

    async Task StopAnimationsAsync()
    {
        var r = await _ops.DoAsync("LLAgent", "stopAnimating");
        if (PersonOps.Error(r) is { } e) { Answer($"Didn't stop: {e}"); return; }
        int n = (r["stopped"] as JsonArray)?.Count ?? 0;
        var stood = r["stood_up"]?.GetValue<bool>() == true ? " You stood up too." : "";
        Answer(n == 0 ? $"Nothing was animating you.{stood}" : $"Stopped {(n == 1 ? "1 animation" : $"{n} animations")}, and what started them can't start them again without asking.{stood}");
    }

    /// <summary>//hover: says it; //hover N: sets it, -2 to 2 metres. Kept with the account.</summary>
    async Task HoverAsync(string words)
    {
        if (words.Length == 0)
        {
            var now = await _ops.ReadAsync("SojournPeople", "hoverHeight");
            Answer(PersonOps.Error(now) is { } ne ? $"Couldn't read it: {ne}" : $"Hover height: {now["hover_height"]} metres. //hover and a number from -2 to 2 changes it.");
            return;
        }
        if (!double.TryParse(words.Replace("metres", "").Replace("m", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
        {
            Answer($"{words} isn't a height: a number of metres, from -2 to 2.");
            return;
        }
        var r = await _ops.DoAsync("SojournPeople", "hoverHeight", new JsonObject { ["height"] = h });
        Answer(PersonOps.Error(r) is { } e ? $"Not changed: {e}" : $"Hover height: {r["hover_height"]} metres.");
    }

    void AskHover()
    {
        var text = Dialogs.Ask(this, "Hover height", "Metres, -2 to 2");
        if (text == null) { Reader?.Answer("Not changed.", keep: false); return; }
        _ = HoverAsync(text);
    }

    /// <summary>//displayname: says it and when it can change; //displayname NAME changes it, her confirmation first.</summary>
    async Task DisplayNameAsync(string words)
    {
        if (words.Length == 0)
        {
            var now = await _ops.ReadAsync("SojournPeople", "displayName");
            if (PersonOps.Error(now) is { } ne) { Answer($"Couldn't read it: {ne}"); return; }
            var next = now["can_change_now"]?.GetValue<bool>() == false && now.Str("next_change") is { Length: > 0 } when ? $" It can change again {when}." : "";
            Answer($"Your display name: {now.Str("display_name")}. Your username: {now.Str("username")}.{next} //displayname and a name changes it.");
            return;
        }
        if (await ConfirmedAsync("SojournPeople", "setDisplayName", new JsonObject { ["name"] = words }) is not { } r) return;
        Answer(PersonOps.Error(r) is { } e ? $"Not changed: {e}" : $"Your display name is {r.Str("display_name") ?? words} now.");
    }

    async Task AskDisplayNameAsync()
    {
        var now = await _ops.ReadAsync("SojournPeople", "displayName");
        if (PersonOps.Error(now) is { } e) { Answer($"Couldn't read it: {e}"); return; }
        var text = Dialogs.Ask(this, "Display name", "Display name, 31 characters at most", now.Str("display_name") ?? "");
        if (text == null || text == now.Str("display_name")) { Reader?.Answer("Not changed.", keep: false); return; }
        await DisplayNameAsync(text);
    }

    /// <summary>//maturity: says the rating and how high the account may go; //maturity WORD sets it.</summary>
    async Task MaturityAsync(string words)
    {
        var s = Host!.Session;
        static string Word(string? m) => m is { Length: > 0 } ? char.ToUpperInvariant(m[0]) + m[1..] : "not known";
        if (words.Length == 0)
        {
            Answer($"You go into regions rated up to {Word(s.Maturity)}. This account may go up to {Word(s.MaturityMax)}. //maturity general, moderate or adult changes it.");
            return;
        }
        var r = await SettingsOp(new JsonObject { ["maturity"] = words.ToLowerInvariant() }, "set");
        Answer(PersonOps.Error(r) is { } e ? $"Not changed: {e}" : $"You go into regions rated up to {Word(s.Maturity)}.");
    }

    void AskMaturity()
    {
        var s = Host!.Session;
        var all = AgentSession.Maturities.Select(m => char.ToUpperInvariant(m[0]) + m[1..]).ToList();
        var at = Dialogs.Choose(this, "Highest rating of region you go into", "Ratings", all, Math.Max(0, Array.IndexOf(AgentSession.Maturities, s.Maturity ?? "")));
        if (at == null) { Reader?.Answer("Not changed.", keep: false); return; }
        _ = MaturityAsync(AgentSession.Maturities[at.Value]);
    }

    /// <summary>//run: Always run turned the other way; //run on, //run off: set. Said either way.</summary>
    void AlwaysRunWords(string words)
    {
        switch (words.ToLowerInvariant())
        {
            case "": SetAlwaysRun(!_set.WalkAlwaysRun); break;
            case "on": SetAlwaysRun(true); break;
            case "off": SetAlwaysRun(false); break;
            default: Answer("//run alone turns Always run on or off; //run on and //run off set it."); break;
        }
    }

    void ToggleAlwaysRun() => SetAlwaysRun(!_set.WalkAlwaysRun);

    void SetAlwaysRun(bool on)
    {
        _set.SetAlwaysRun(on);
        KeepAlwaysRun();
        Answer(on ? "Always running: walking, //go and following run now. //run again walks." : "Walking again: Shift with the arrows still runs in walk mode.");
    }

    /// <summary>//walkround: walking round people (Commands.WalkPeople.cs) turned the other way; //walkround on,
    /// //walkround off: set. Said either way. Kept (walk_round_people); a walk under way goes by it from its next look.</summary>
    void WalkRoundWords(string words)
    {
        switch (words.ToLowerInvariant())
        {
            case "": SetWalkRound(!_set.WalkRoundPeople); break;
            case "on": SetWalkRound(true); break;
            case "off": SetWalkRound(false); break;
            default: Answer("//walkround alone turns walking round people on or off; //walkround on and //walkround off set it."); break;
        }
    }

    void ToggleWalkRound() => SetWalkRound(!_set.WalkRoundPeople);

    void SetWalkRound(bool on)
    {
        _set.SetWalkRoundPeople(on);
        Answer(on ? "Walking round people: //go and Walk to go round anyone standing in the way. //walkround again walks straight."
            : "Walking straight: //go and Walk to take no notice of people, and may walk into someone standing in the way. //walkround again goes round them.");
    }

    (bool On, GridClient? Client, Simulator? Sim) _runSent;

    /// <summary>The slow tick: Always run told to the region when the setting changes, and again while it's on whenever
    /// the region does (a teleport or a crossing, a new login), since the region keeps it, not the viewer. Off, a new
    /// region isn't told, so walk mode's Shift held across a crossing isn't undone.</summary>
    void KeepAlwaysRun()
    {
        if (_ops?.Client is not { } c || c.Network.CurrentSim is not { } sim) return;
        bool on = _set.WalkAlwaysRun;
        bool moved = !ReferenceEquals(_runSent.Client, c) || !ReferenceEquals(_runSent.Sim, sim);
        if (_runSent.On != on || on && moved) c.Self.Movement.AlwaysRun = on;
        _runSent = (on, c, sim);
    }

    /// <summary>Everything in the Trash gone for good, behind her confirmation.</summary>
    async Task EmptyTrashAsync()
    {
        if (await ConfirmedAsync("TesselInventory", "emptyTrash", new JsonObject()) is not { } r) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not emptied: {e}"); return; }
        Answer("The Trash is empty.");
        await Task.Delay(500);
        RefreshInventory(_ops.Session.Inventory.FolderFor(FolderType.Trash));
    }

    /// <summary>Everything in Lost and Found (what's returned from the world lands there) gone for good, behind her
    /// confirmation, as the viewer's and Radegast's Empty Lost and Found.</summary>
    async Task EmptyLostAndFoundAsync()
    {
        if (await ConfirmedAsync("TesselInventory", "emptyLostAndFound", new JsonObject()) is not { } r) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not emptied: {e}"); return; }
        Answer("Lost and Found is empty.");
        await Task.Delay(500);
        RefreshInventory(_ops.Session.Inventory.FolderFor(FolderType.LostAndFound));
    }

    /// <summary>Whether something is in Lost and Found, or a folder in it (the folder itself not counted).</summary>
    bool InLostAndFound(InventoryBase n)
    {
        var lf = _ops.Session.Inventory.FolderFor(FolderType.LostAndFound);
        if (Store is not { } store || lf == UUID.Zero || lf == store.RootFolder?.UUID) return false;
        var id = n.ParentUUID;
        for (int i = 0; i < 64 && id != UUID.Zero; i++)
        {
            if (id == lf) return true;
            if (!store.TryGetValue<InventoryBase>(id, out var up)) return false;
            id = up.ParentUUID;
        }
        return false;
    }

    /// <summary>Attach to a point the person picks, beside what's on it already: how to wear something whose own point is wrong.</summary>
    async Task AttachToAsync(InvPane p, InventoryItem item)
    {
        var r = await _ops.ReadAsync("TesselAgent", "attachPoints");
        if (PersonOps.Error(r) is { } re) { Answer($"Couldn't list them: {re}"); return; }
        var points = (r["points"] as JsonArray ?? new()).OfType<JsonObject>().ToList();
        var words = points.Select(o => (o["objects"] as JsonArray)?.OfType<JsonObject>().Select(x => x.Str("name")).ToList() is { Count: > 0 } on
            ? $"{o.Str("name")}, holding {string.Join(", ", on)}" : o.Str("name") ?? "").ToList();
        var at = Dialogs.Choose(this, $"Attach {item.Name} to", "Attachment points", words, 0);
        if (at == null) { Reader?.Answer("Not attached.", keep: false); return; }
        var point = points[at.Value].Str("name") ?? "";
        var w = await _ops.DoAsync("TesselAgent", "attach", new JsonObject { ["item_id"] = item.UUID.ToString(), ["point"] = point, ["replace"] = false });
        Answer(PersonOps.Error(w) is { } e ? $"Didn't attach {item.Name}: {e}" : $"Wearing {item.Name} on {point}.");
        Unmark(p);
        await Task.Delay(1500);
        RefreshInventory(item.ParentUUID);
    }

    /// <summary>A sound played as the viewer's Play does, in the world where she stands: the only way to tell what one is.</summary>
    async Task PlaySoundAsync(InventoryItem item)
    {
        var r = await _ops.DoAsync("TesselAgent", "playSound", new JsonObject { ["item_id"] = item.UUID.ToString() });
        Answer(PersonOps.Error(r) is { } e ? $"Didn't play {item.Name}: {e}" : $"Playing {item.Name} where you stand. People near you hear it too.");
    }

    async Task PlayAnimationAsync(InventoryItem item, bool play)
    {
        var r = await _ops.DoAsync("LLAgent", play ? "playAnimation" : "stopAnimation", new JsonObject { ["item_id"] = item.UUID.ToString() });
        Answer(PersonOps.Error(r) is { } e ? $"Didn't {(play ? "play" : "stop")} {item.Name}: {e}"
            : play ? $"Playing {item.Name} on your avatar. Stop it is on this menu." : $"Stopped {item.Name}.");
    }

    /// <summary>F5: the grid asked for every folder again, as Radegast's reload, how stale folders get mended: what it has
    /// comes in, and what it no longer has (deleted or moved in another viewer) goes from where it was; then both panes
    /// shown again.</summary>
    async Task ReloadInventoryAsync(InvPane p)
    {
        Reader?.Answer("Asking the grid for your inventory again.", keep: false);
        var inv = _ops.Session.Inventory;
        var whole = await inv.ReloadAsync(CancellationToken.None);
        foreach (var pane in Panes)
            if (pane == p || pane.ShownOnce) ShowInventory(pane, said: false);
        int gone = inv.LastReloadDropped;
        var dropped = gone == 0 ? "" : gone == 1 ? " 1 thing wasn't there any more, and is gone from the list." : $" {gone} things weren't there any more, and are gone from the list.";
        Reader?.Answer((whole ? "Inventory read again from the grid." : "Inventory read again; some folders didn't answer, so F5 again may help.") + dropped, keep: false);
    }
}
