using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Access;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// Land settings with her own keys (docs/access.md > M6: the task blind residents hand to a sighted person logging in as
/// them): the About Land area, Control+L, on the parcel she stands on. As the build editor: a command box taking the
/// official viewer's About Land tab names and settings as words ("options", "name Our Garden", "fly off", "ban Name for
/// 2 hours", "for sale 500", "buy"), a settings list per tab (Enter changes a row, Right and Left step it), and the tabs
/// as a list. The parcel shown is pinned by its id, so a change acts on what she was told about even after she walks
/// off it; F5 (or `here`) reads the parcel where she stands. Every change goes through SojournLand's ops, each read back
/// by the op and said once the region shows it. What lets people in or keeps them out, reshapes the land, or changes
/// who owns it waits for her confirmation in Notices; the rest of the switches don't, as the viewer's checkboxes don't.
/// </summary>
public sealed partial class MainForm
{
    TabPage _landPage = null!;
    LineBox _landCommand = null!;
    ListView _landRows = null!;
    ComboBox _landTab = null!;
    LandLook? _land;
    Control? _beforeLand;
    TabPage? _beforeLandPage;
    bool _landIntroduced;
    bool _landTabOurs;   // the tab chosen by our own code, which reads it itself (not the list's own change)
    readonly List<string> _landHistory = new();
    int _landHistoryAt;

    string LandTabName => LandRows.Tabs[Math.Max(0, _landTab.SelectedIndex)];

    void BuildLand()
    {
        _landPage = new TabPage("About Land") { Name = "landPage", AccessibleName = "About Land" };
        _landCommand = new LineBox { Name = "landCommand", AccessibleName = "Land command", Dock = DockStyle.Fill };
        _landRows = List("landRows", "Settings");
        _landTab = new ComboBox { Name = "landTab", AccessibleName = "Tab", DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        _landTab.Items.AddRange(LandRows.Tabs);
        _landTab.SelectedIndex = 0;
        _landTab.SelectedIndexChanged += (_, _) => { if (!_landTabOurs) _ = GuardedAsync(() => ShowLandTabAsync(said: false), "About Land's tab"); };

        _landCommand.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Control && !e.Alt)
            {
                e.Handled = e.SuppressKeyPress = true;
                var text = _landCommand.Text.Trim();
                if (text.Length == 0) { Answer("Type a command: help lists them."); return; }
                _landCommand.Clear();
                _landHistory.Remove(text);
                _landHistory.Add(text);
                _landHistoryAt = _landHistory.Count;
                _ = LandCommandAsync(text);
            }
            else if (e.Control && e.KeyCode is Keys.Up or Keys.Down && _landHistory.Count > 0)
            {
                e.Handled = e.SuppressKeyPress = true;
                _landHistoryAt = Math.Clamp(_landHistoryAt + (e.KeyCode == Keys.Up ? -1 : 1), 0, _landHistory.Count);
                _landCommand.Text = _landHistoryAt < _landHistory.Count ? _landHistory[_landHistoryAt] : "";
                _landCommand.SelectionStart = _landCommand.Text.Length;
            }
        };
        _landRows.KeyDown += (_, e) => LandRowKey(e);

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = "Command", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        t.Controls.Add(_landCommand, 1, 0);
        t.Controls.Add(_landRows, 0, 1);
        t.SetColumnSpan(_landRows, 2);
        t.Controls.Add(new Label { Text = "Tab", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        t.Controls.Add(_landTab, 1, 2);
        _landCommand.TabIndex = 0; _landRows.TabIndex = 1; _landTab.TabIndex = 2;
        _landPage.Controls.Add(t);
        Fill(_landRows, new(), "Not read yet: F5 reads the parcel you're on.");
    }

    /// <summary>An op's refusal of a change, in her words: "Not changed", or, where the region only hasn't shown it yet (a busy
    /// region on her own land: ParcelOps waits 10 seconds), "Not seen yet", since it may well have changed.</summary>
    string NotChanged(string e) => e.Contains("hasn't shown", StringComparison.Ordinal) ? $"Not seen yet: {Theirs(e)}." : $"Not changed: {Theirs(e)}.";

    // --- coming and going -------------------------------------------------------------------------------------------

    /// <summary>Control+L: About Land, on the parcel she stands on (read afresh), the focus in its command box.</summary>
    void GoLand(string? tab = null)
    {
        if (!_landPage.ContainsFocus)
        {
            _beforeLand = ActiveControl;
            _beforeLandPage = _areas.SelectedTab;
        }
        if (tab != null && Array.IndexOf(LandRows.Tabs, tab) is var ti and >= 0) ChooseLandTab(ti);
        GoArea(_landPage);
        _ = GuardedAsync(() => LandArrivedAsync(sayTab: tab != null), "About Land");
    }

    async Task LandArrivedAsync(bool sayTab)
    {
        var intro = _landIntroduced ? "" : " Type a tab's name (general, covenant, objects, options, media, sound, access, region, estate), or a setting and its value; help lists the commands; Tab for the settings.";
        _landIntroduced = true;
        if (!await ReadLandAsync(here: true)) return;
        await ShowLandTabAsync(said: sayTab);
        if (!sayTab) Answer($"About Land: {LandSummary()}{intro}", Quoting());
    }

    /// <summary>Escape: back where Control+L came from.</summary>
    void LandBack()
    {
        var page = _beforeLandPage is { } p && p != _landPage ? p : _chatPage;
        _areas.SelectedTab = page;
        var back = _beforeLand is { IsDisposed: false, CanFocus: true } b && page.Contains(b) ? b : FirstControl(page);
        back?.Focus();
        FocusChanged();
    }

    /// <summary>`//land WORDS` from any input: About Land's command, without going there; alone, the area.</summary>
    Task LandWordsAsync(string words)
    {
        if (words.Trim().Length > 0) return LandCommandAsync(words);
        GoLand();
        return Task.CompletedTask;
    }

    /// <summary>The parcel's own words (its name and description, the covenant) are its owner's: the session log marks
    /// an answer quoting them.</summary>
    LineFrom Quoting() => _land != null && !_land.Mine ? LineFrom.People : LineFrom.App;

    // --- the parcel -------------------------------------------------------------------------------------------------

    /// <summary>The parcel she stands on (here), or the one shown, read afresh; false (said) when it can't be.</summary>
    async Task<bool> ReadLandAsync(bool here)
    {
        if (Host?.Session is not { Online: true } s || _ops.Client?.Network.CurrentSim is not { } sim) { Answer("Not in a region yet."); return false; }
        var args = new JsonObject();
        bool same = !here && _land != null && _land.Region == sim.Name;
        if (same) Pin(args);
        var r = await _ops.ReadAsync("SojournLand", "parcel", args);
        if (PersonOps.Error(r) is { } e) { Answer($"About Land: the region didn't say ({Theirs(e)})."); return false; }
        bool kept = same && _land!.LocalId == r.Int("local_id", -1);
        var groups = s.State.Groups.Values.Select(g => (g.ID, g.Name)).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var look = new LandLook { Parcel = r, Region = sim.Name, Me = _ops.Client.Self.AgentID, Groups = groups };
        if (kept)
        {
            // the same parcel: what the other tabs read stays until they're read again
            look.Access = _land!.Access; look.AccessWhy = _land.AccessWhy; look.Objects = _land.Objects; look.ObjectsWhy = _land.ObjectsWhy;
            look.RegionInfo = _land.RegionInfo; look.Covenant = _land.Covenant;
            look.Estate = _land.Estate; look.EstateWhy = _land.EstateWhy; look.ManagesEstate = _land.ManagesEstate;
        }
        _land = look;
        LandWhere();
        return true;
    }

    void Pin(JsonObject a)
    {
        if (_land == null) return;
        a["local_id"] = _land.LocalId;
        a["region"] = _land.Region;
    }

    /// <summary>Where she is from the parcel, from its cells as the region last sent them: on it, and its nearest edge;
    /// or off it, and its nearest part.</summary>
    void LandWhere()
    {
        if (_land == null) return;
        if (_ops.Client?.Network.CurrentSim is not { } sim || _ops.Me() is not { } me || sim.Name != _land.Region) { _land.Where = "you're in another region now"; return; }
        if (!sim.Parcels.TryGetValue(_land.LocalId, out var parcel) || parcel.Bitmap is not { Length: > 0 } bits
            || LandRows.Nearest(bits, sim.SizeX, me.Pos.X, me.Pos.Y) is not { } n)
        {
            _land.Where = "not known";
            return;
        }
        var dir = Bearings.Relative(n.X - me.Pos.X, n.Y - me.Pos.Y, 0, me.Heading);
        _land.Where = n.On ? $"you're on it; its nearest edge {dir}" : $"you're not on it; its nearest part {dir}";
    }

    /// <summary>"Access Plot, yours, 576 square metres, for sale to you for L$0; you're on it; its nearest edge ..."</summary>
    string LandSummary()
    {
        if (_land is not { } l) return "nothing read yet.";
        // trimmed first: "...the discerning avatar.  " came out as "avatar.  ." (the blind tester's club, 0.1.0.887)
        var desc = l.Parcel.Str("desc")?.Trim().TrimEnd('.').TrimEnd() is { Length: > 0 } d ? $" {Cap(d)}{(d[^1] is '!' or '?' ? "" : ".")}" : "";
        var sale = l.Sale != null ? $", for sale: {LandRows.SaleWords(l)}" : "";
        var closed = LandRows.OpenTo(l) is var open && open != "everyone" ? $", open to {open}" : "";
        return $"{l.Name}, {l.Whose}, {l.Parcel.Int("area", 0)} square metres{sale}{closed}; {l.Where ?? "where it is from you isn't known"}.{desc}";
    }

    /// <summary>What a tab reads besides the parcel (its lists, who owns the objects, the region, the covenant).</summary>
    async Task LoadLandTabAsync(string tab, bool again)
    {
        if (_land is not { } l) return;
        switch (tab)
        {
            case "Access" when again || l.Access == null && l.AccessWhy == null:
                if (!l.MayManage) { l.Access = null; l.AccessWhy = "only its owner sees them"; break; }
                var a = Pinned();
                var r = await _ops.ReadAsync("SojournLand", "access", a);
                if (PersonOps.Error(r) is { } e) { l.Access = null; l.AccessWhy = Theirs(e); } else { l.Access = r; l.AccessWhy = null; }
                break;
            case "Objects" when again || l.Objects == null && l.ObjectsWhy == null:
                if (!l.MayManage) { l.Objects = null; l.ObjectsWhy = "only its owner sees who owns what"; break; }
                var o = await _ops.ReadAsync("SojournLand", "objects", Pinned());
                if (PersonOps.Error(o) is { } oe) { l.Objects = null; l.ObjectsWhy = oe.Contains("no answer") ? "the region didn't say" : Theirs(oe); } else { l.Objects = o; l.ObjectsWhy = null; }
                break;
            case "Covenant" or "Region" or "General" when again || l.RegionInfo == null || tab != "General" && l.Covenant == null:
                if (again || l.RegionInfo == null) { var ri = await _ops.ReadAsync("SojournLand", "region"); l.RegionInfo = PersonOps.Error(ri) == null ? ri : null; }
                if (tab != "General" && (again || l.Covenant == null)) { var c = await _ops.ReadAsync("SojournLand", "covenant"); l.Covenant = PersonOps.Error(c) == null ? c : null; }
                break;
            case "Estate":
                await LoadEstateAsync(l, again);
                break;
        }
        if (tab == "Region" && _ops.Client?.Network.CurrentSim is { } sim)
        {
            var st = sim.Stats;
            l.Stats = string.Create(CultureInfo.InvariantCulture,
                $"{st.Agents} {(st.Agents == 1 ? "person" : "people")} here, {st.ActiveScripts} active scripts, time dilation {st.Dilation:0.00}");
        }
    }

    void ChooseLandTab(int index)
    {
        if (_landTab.SelectedIndex == index) return;
        _landTabOurs = true;
        try { _landTab.SelectedIndex = index; }
        finally { _landTabOurs = false; }
    }

    JsonObject Pinned() { var a = new JsonObject(); Pin(a); return a; }

    /// <summary>The settings list for the tab chosen (rows that stay the same rows keep their place as their words change).</summary>
    async Task ShowLandTabAsync(bool said, bool again = false)
    {
        var tab = LandTabName;
        if (_land == null && !await ReadLandAsync(here: true)) return;
        await LoadLandTabAsync(tab, again);
        ShowLandRows();
        if (said) SayLandTab(tab);
    }

    void ShowLandRows()
    {
        if (_land is not { } l) return;
        var tab = LandTabName;
        l.RestartAt = _restart is { } rs && rs.Region == l.Region && rs.At > DateTime.UtcNow ? rs.At : null;
        var items = LandRows.For(l, tab);
        _landRows.AccessibleName = $"{tab} of {l.Name}";
        RowsInto(_landRows, items, "None.");
    }

    void SayLandTab(string tab)
    {
        if (_land is not { } l) return;
        if (tab == "Estate") { Answer(EstateSaid(l)); return; }
        var rows = LandRows.For(l, tab);
        // (a tab whose first row is its own name, Region or Covenant, isn't named twice)
        var head = rows.Count > 0 && rows[0].Words.StartsWith(tab + ":") ? "" : $"{tab}: ";
        var words = tab == "Covenant" && rows.Count > 12
            ? string.Join("; ", rows.Take(12).Select(r => r.Words)) + $"; and {rows.Count - 12} more lines in the list (Tab)"
            : string.Join("; ", rows.Select(r => r.Words));
        Answer($"{head}{words}.", tab == "Covenant" || !l.Mine && tab == "General" ? LineFrom.People : LineFrom.App);
    }

    // --- the settings list's keys ------------------------------------------------------------------------------------

    void LandRowKey(KeyEventArgs e)
    {
        var item = _landRows.FocusedItem;
        if (e.KeyCode == Keys.F5) { e.Handled = true; _ = GuardedAsync(() => LandHereAsync(), "About Land"); return; }
        if (e.Control || e.Alt) return;
        switch (item?.Tag)
        {
            case LandPerson p:
                if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Answer($"{LandRows.PersonWords(p)}. Delete takes them off the list."); }
                else if (e.KeyCode == Keys.Delete) { e.Handled = e.SuppressKeyPress = true; _ = GuardedAsync(() => ListChangeAsync(p.Banned ? "unban" : "unallow", p.Id, p.Name, null), p.Name); }
                return;
            case LandOwner o:
                if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Answer($"{item.Text}. Delete returns them to their owner."); }
                else if (e.KeyCode == Keys.Delete) { e.Handled = e.SuppressKeyPress = true; _ = GuardedAsync(() => ReturnOwnerAsync(o), o.Name); }
                return;
            // the Estate tab's (MainForm.Estate.cs)
            case EstatePerson ep:
                if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Answer($"{item.Text}. Delete takes them off the list."); }
                else if (e.KeyCode == Keys.Delete) { e.Handled = e.SuppressKeyPress = true; _ = GuardedAsync(() => EstateRemoveAsync(ep), ep.Name); }
                return;
            case EstateListHead h when e.KeyCode == Keys.Enter:
                e.Handled = e.SuppressKeyPress = true;
                _ = GuardedAsync(() => EstateAddAsync(h), h.Label);
                return;
            case EstateAction act when e.KeyCode == Keys.Enter:
                e.Handled = e.SuppressKeyPress = true;
                _ = GuardedAsync(() => EstateActAsync(act), act.Label);
                return;
            case BuildRow r:
                switch (e.KeyCode)
                {
                    case Keys.Enter: e.Handled = e.SuppressKeyPress = true; _ = GuardedAsync(() => EnterLandRowAsync(r), r.Label); break;
                    case Keys.Space or Keys.Right: e.Handled = e.SuppressKeyPress = true; _ = GuardedAsync(() => StepLandRowAsync(r, e.Shift ? -1 : 1), r.Label); break;
                    case Keys.Left: e.Handled = e.SuppressKeyPress = true; _ = GuardedAsync(() => StepLandRowAsync(r, -1), r.Label); break;
                }
                return;
            case string when e.KeyCode == Keys.Enter && item != null:
                e.Handled = e.SuppressKeyPress = true;
                Answer(item.Text + ".", Quoting());
                return;
        }
    }

    /// <summary>F5 or `here`: the parcel where she stands now, and the tab's lists, read again.</summary>
    async Task LandHereAsync(bool said = true)
    {
        if (!await ReadLandAsync(here: true)) return;
        await ShowLandTabAsync(said: false, again: true);
        if (said) Answer(LandSummary(), Quoting());
    }

    async Task EnterLandRowAsync(BuildRow r)
    {
        switch (r.Key)
        {
            case "buy": await BuyLandAsync(); return;
            case "buy_pass": await BuyPassAsync(); return;
            case "abandon": await AbandonLandAsync(); return;
            case "deed": await DeedLandAsync(""); return;
            case "landing": await ApplyLandAsync("landing", "here"); return;
            case "where": LandWhere(); ShowLandRows(); Answer($"Where: {_land?.Where ?? "not known"}."); return;
            case "sale":
                var typed = Dialogs.Ask(this, "For sale", "A price in L$ (and to NAME, or with objects), or no", _land?.Sale is { } s ? $"{s.Int("price", 0)}" : "");
                if (typed == null) { Reader?.Answer("Not changed.", keep: false); return; }
                await SaleAsync(typed);
                return;
        }
        switch (r.Kind)
        {
            case RowKind.Info: Answer(r.Words + ".", Quoting()); return;
            case RowKind.Switch: await ApplyLandAsync(r.Key, r.Value > 0 ? "off" : "on"); return;
            case RowKind.Choice: LandChoiceMenu(r); return;
        }
        var value = Dialogs.Ask(this, r.Label, r.Ask, r.Now, allowEmpty: r.Kind == RowKind.Text);
        if (value == null) { Reader?.Answer("Not changed.", keep: false); return; }
        await ApplyLandAsync(r.Key, value.Length == 0 ? "none" : value);
    }

    void LandChoiceMenu(BuildRow r)
    {
        var menu = new ContextMenuStrip { Name = "choiceMenu", AccessibleName = r.Label };
        for (int i = 0; i < r.Choices!.Length; i++)
        {
            var name = r.Choices[i];
            menu.Items.Add(new ToolStripMenuItem(name, null, (_, _) => _ = GuardedAsync(() => ApplyLandAsync(r.Key, name), r.Label)) { Checked = i == r.Chosen });
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        var at = _landRows.FocusedItem?.Bounds ?? default;
        menu.Show(_landRows, new Point(at.Left + 10, at.Bottom));
        Mnemonics.Fix(menu);
        if (r.Chosen >= 0 && r.Chosen < menu.Items.Count) menu.Items[r.Chosen].Select();
    }

    async Task StepLandRowAsync(BuildRow r, int dir)
    {
        switch (r.Kind)
        {
            case RowKind.Switch: await ApplyLandAsync(r.Key, r.Value > 0 ? "off" : "on"); return;
            case RowKind.Choice when r.Choices is { Length: > 0 } ch:
                int i = r.Chosen < 0 ? 0 : ((r.Chosen + dir) % ch.Length + ch.Length) % ch.Length;
                await ApplyLandAsync(r.Key, ch[i]);
                return;
            case RowKind.Number when r.Step > 0:
                var v = Math.Max(0, Math.Round((r.Value + dir * r.Step) / r.Step) * r.Step);
                await ApplyLandAsync(r.Key, LandRows.N(v));
                return;
            default:
                Answer($"{r.Label}: {(r.Kind == RowKind.Info ? "read from the region" : "Enter to type it")}.");
                return;
        }
    }

    // --- a change ----------------------------------------------------------------------------------------------------

    /// <summary>A setting changed (a row or a typed command): SojournLand set (or setRegion), her confirmation first for
    /// who may enter or reshape the land; the op reads the parcel back, and the row's new words are said.</summary>
    async Task ApplyLandAsync(string key, string value)
    {
        if (_land == null && !await ReadLandAsync(here: true)) return;
        var l = _land!;
        if (key == "sale") { await SaleAsync(value); return; }
        var (op, args, confirm, error) = LandRows.Change(l, key, value);
        if (op == null || args == null) { Answer(error ?? "Not changed."); return; }
        if (op != "setRegion") Pin(args);
        var before = LandRows.Row(l, key)?.Words;
        var r = confirm ? await ConfirmedAsync("SojournLand", op, args) : await _ops.DoAsync("SojournLand", op, args);
        if (r == null) return;   // ("Cancelled." was said)
        if (PersonOps.Error(r) is { } e) { Answer(NotChanged(e)); return; }
        if (op == "setRegion") l.RegionInfo = r; else l.Parcel = r;
        if (LandRows.TabOf(key) == "Access" && l.Access != null) await LoadLandTabAsync("Access", again: true);
        LandWhere();
        ShowLandRows();
        var after = LandRows.Row(l, key)?.Words;
        Answer(after == null ? "Changed." : after == before ? $"Already so: {after}." : $"{after}.");
    }

    /// <summary>for sale PRICE [to NAME] [with objects], or not for sale: sell (her confirmation first: it can give the
    /// land away) or stopSelling.</summary>
    async Task SaleAsync(string words)
    {
        if (_land == null && !await ReadLandAsync(here: true)) return;
        var (stop, price, to, objects, error) = LandRows.Sale(words);
        if (error != null) { Answer(error); return; }
        var args = Pinned();
        JsonObject? r;
        if (stop)
        {
            if (_land!.Sale == null) { Answer("For sale: no, already."); return; }
            r = await _ops.DoAsync("SojournLand", "stopSelling", args);
        }
        else
        {
            args["price"] = price;
            if (objects) args["objects"] = true;
            if (to != null)
            {
                if (await FindAnyoneAsync(to) is not { } who) return;
                args["to"] = who.Id.ToString();
            }
            r = await ConfirmedAsync("SojournLand", "sell", args);
        }
        if (r == null) return;
        if (PersonOps.Error(r) is { } e) { Answer(NotChanged(e)); return; }
        _land!.Parcel = r;
        ShowLandRows();
        Answer($"For sale: {LandRows.SaleWords(_land)}.");
    }

    // --- typed commands ----------------------------------------------------------------------------------------------

    const string LandHelp = "About Land commands. A tab's name reads it: general, covenant, objects, options, media, sound, access, region. "
        + "A setting and its value changes it: name Our Garden, description words, category residential, fly off, scripts on, damage off, "
        + "show in search on, landing here, routing anywhere, auto return 10, music url ADDRESS, voice off, only the allowed list on, passes on, pass price 10; "
        + "a setting alone says it. allow NAME, unallow NAME, ban NAME, ban NAME for 2 hours, unban NAME, eject NAME, eject and ban NAME, freeze NAME, unfreeze NAME. "
        + "return NAME, or return others: their objects back to them. for sale 500, for sale 0 to NAME, with objects; not for sale. "
        + "buy: the land, if it's for sale to you. buy pass. abandon. deed, or deed GROUP. "
        + "divide north half (or south, east, west, ahead, behind, left, right), join north (with the parcel beyond that edge). "
        + "flatten, raise, lower, smooth, roughen or revert: the ground where you stand; add parcel for the whole parcel, for 3 seconds for longer. "
        + "my land: your land holdings. parcels: every parcel in the region. here, or F5: the parcel you're on now. where. "
        + "estate: its tab, for the estate's owner and managers; estate help lists its commands (kick, send home, restart, message). Escape goes back.";

    Task LandCommandAsync(string text) => GuardedAsync(() => LandCommandCoreAsync(text), text);

    async Task LandCommandCoreAsync(string text)
    {
        var t = text.Trim();
        var lower = t.ToLowerInvariant();
        var sp = lower.IndexOf(' ');
        var verb = sp < 0 ? lower : lower[..sp];
        var rest = sp < 0 ? "" : t[(sp + 1)..].Trim();
        var restLower = rest.ToLowerInvariant();
        var tabIndex = Array.FindIndex(LandRows.Tabs, x => x.Equals(verb, StringComparison.OrdinalIgnoreCase) || x.Equals(verb.TrimEnd('s'), StringComparison.OrdinalIgnoreCase));
        if (tabIndex >= 0 && rest.Length == 0)
        {
            if (_land == null && !await ReadLandAsync(here: true)) return;
            ChooseLandTab(tabIndex);
            await LoadLandTabAsync(LandRows.Tabs[tabIndex], again: false);
            ShowLandRows();
            SayLandTab(LandRows.Tabs[tabIndex]);
            return;
        }
        switch (verb)
        {
            case "help" or "?": Answer(LandHelp); return;
            case "done" or "close" or "exit": LandBack(); return;
            case "here" or "refresh": await LandHereAsync(); return;
            case "parcels": await _cmd.RunAsync("//parcelinfo"); return;
            case "my" when restLower is "land" or "holdings": await HoldingsAsync(); return;
            case "holdings": await HoldingsAsync(); return;
            case "covenant" or "region": break;
        }
        // the estate's (MainForm.Estate.cs): none needs the parcel
        if (await EstateWordsAsync(verb, rest)) return;
        if (_land == null && !await ReadLandAsync(here: true)) return;
        switch (verb)
        {
            case "where" when rest.Length == 0: LandWhere(); ShowLandRows(); Answer($"{_land!.Name}: {_land.Where}."); return;
            case "what" or "summary" or "status": Answer(LandSummary(), Quoting()); return;
            case "buy" when restLower is "" or "land" or "it" or "this land" or "the land": await BuyLandAsync(); return;
            case "buy" when restLower is "pass" or "a pass": await BuyPassAsync(); return;
            case "pass" when rest.Length == 0: await BuyPassAsync(); return;
            case "abandon": await AbandonLandAsync(); return;
            case "deed": await DeedLandAsync(rest); return;
            case "allow" or "unallow" or "ban" or "unban" when rest.Length > 0: await ListCommandAsync(verb, rest); return;
            case "allow" or "unallow" or "ban" or "unban": Answer($"{verb} NAME{(verb is "allow" or "ban" ? ", or " + verb + " NAME for 2 hours" : "")}."); return;
            case "eject": await EjectAsync(rest); return;
            case "freeze" or "unfreeze": await FreezeAsync(rest, verb == "freeze"); return;
            case "return": await ReturnCommandAsync(rest); return;
            case "stop" when restLower is "selling" or "sale": await SaleAsync("no"); return;
            case "divide" or "subdivide": await DivideAsync(rest); return;
            case "join": await JoinAsync(rest); return;
        }
        if (Array.IndexOf(ParcelOps.Actions, verb) >= 0) { await TerraformAsync(verb, restLower); return; }
        if (LandRows.Match(t) is not { } m) { Answer($"No About Land command or setting {verb}. help lists them."); return; }
        if (m.Value.Length == 0 && m.Key != "landing")
        {
            if (m.Key == "where") LandWhere();
            await LoadLandTabAsync(LandRows.TabOf(m.Key), again: false);
            Answer((LandRows.Row(_land!, m.Key)?.Words ?? m.Key) + ".", Quoting());
            return;
        }
        await ApplyLandAsync(m.Key, m.Value.Length == 0 ? "here" : m.Value);
    }

    // --- buying, passes, giving it up ------------------------------------------------------------------------------

    async Task BuyLandAsync()
    {
        var l = _land!;
        if (l.Mine) { Answer($"{l.Name} is yours already."); return; }
        if (l.Sale == null) { Answer($"{l.Name} isn't for sale."); return; }
        var r = await ConfirmedAsync("SojournLand", "buy", Pinned());
        if (r == null) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not bought: {Theirs(e)}."); return; }
        l.Parcel = r;
        l.Access = null; l.AccessWhy = null; l.Objects = null; l.ObjectsWhy = null;
        LandWhere();
        ShowLandRows();
        Answer($"Bought {l.Name} for L${r.Int("bought_for", 0)}: it's yours now.");
    }

    async Task BuyPassAsync()
    {
        var l = _land!;
        // the parcel shown if it sells passes, else the one that last turned her away (as the viewer offers it at the line)
        var args = l.Option("pass_list") && !l.Mine ? Pinned() : new JsonObject();
        var r = await ConfirmedAsync("SojournLand", "buyPass", args);
        if (r == null) return;
        if (PersonOps.Error(r) is { } e) { Answer($"No pass bought: {Theirs(e)}."); return; }
        double hours = r.Num("hours") ?? 0;
        Answer($"Bought a pass to {(r.Str("parcel") is { Length: > 0 } n ? n : "the parcel")} for L${r.Int("price", 0)}: {(hours == 1 ? "1 hour" : $"{LandRows.N(hours)} hours")}, until {r.Str("until") ?? "it runs out"}.");
    }

    async Task AbandonLandAsync()
    {
        var l = _land!;
        if (!l.MayManage) { Answer($"{l.Name} isn't yours to abandon: it's {l.Whose}."); return; }
        var r = await ConfirmedAsync("SojournLand", "abandon", Pinned());
        if (r == null) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not abandoned: {Theirs(e)}."); return; }
        await ReadLandAsync(here: false);
        ShowLandRows();
        Answer($"Abandoned {r.Str("abandoned") ?? l.Name}: it's {r.Str("owner_now") ?? "the estate"}'s now.");
    }

    async Task DeedLandAsync(string group)
    {
        var l = _land!;
        if (!l.Mine) { Answer($"Only its owner may deed {l.Name}."); return; }
        var args = Pinned();
        if (group.Trim().Length > 0) args["group"] = group.Trim();
        var r = await ConfirmedAsync("SojournLand", "deed", args);
        if (r == null) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not deeded: {Theirs(e)}."); return; }
        l.Parcel = r;
        ShowLandRows();
        Answer($"Deeded {l.Name}: it's owned by {l.OwnerWords} now.");
    }

    async Task HoldingsAsync()
    {
        var r = await _ops.ReadAsync("SojournLand", "holdings");
        if (PersonOps.Error(r) is { } e) { Answer($"Your land holdings didn't come: {Theirs(e)}."); return; }
        var parcels = (r["parcels"] as JsonArray ?? new()).OfType<JsonObject>().ToList();
        if (parcels.Count == 0) { Answer("You own no land."); return; }
        Answer($"Your land: {(parcels.Count == 1 ? "1 parcel" : $"{parcels.Count} parcels")}, {r.Int("total_area", 0)} square metres. "
               + string.Join(" ", parcels.Select(p => $"{p.Str("name") ?? "unnamed"} in {p.Str("region") ?? "a region"}, {p.Int("area", 0)} square metres.")));
    }

    // --- who may be here --------------------------------------------------------------------------------------------

    /// <summary>Someone by name: in the region, then anyone on the grid (the viewer's avatar picker); said when no one,
    /// or several, match.</summary>
    async Task<(UUID Id, string Name)?> FindAnyoneAsync(string words)
    {
        var w = words.Trim();
        if (UUID.TryParse(w, out var id) && id != UUID.Zero) return (id, w);
        if (await _ops.FindPersonAsync(w) is { } here && here.Names.Any(n => n.Equals(w, StringComparison.OrdinalIgnoreCase) || Norm(n) == Norm(w)
                                                                             || n != here.Username && Norm(n).StartsWith(Norm(w))))
            return (here.Id, here.Name);
        var r = await _ops.ReadAsync("TesselAgent", "findAvatar", new JsonObject { ["name"] = w, ["limit"] = 10 });
        var found = (r["people"] as JsonArray ?? new()).OfType<JsonObject>()
            .Select(p => (Id: UUID.TryParse(p.Str("id"), out var pid) ? pid : UUID.Zero, User: p.Str("username") ?? "", Shown: PersonOps.SaidName(p),
                Display: p.Str("display_name") ?? ""))
            .Where(p => p.Id != UUID.Zero).ToList();
        var exact = found.Where(p => Norm(p.User) == Norm(w) || Norm(p.Display) == Norm(w) || Norm(Sojourn.Core.World.PlainName.Of(p.Display)) == Norm(w)).ToList();
        var pick = exact.Count == 1 ? exact[0] : found.Count == 1 ? found[0] : default;
        if (pick.Id != UUID.Zero) return (pick.Id, pick.Shown);
        Answer(found.Count == 0 ? $"No one called {w} on the grid." : $"Several are called something like {w}: {string.Join(", ", found.Take(6).Select(p => p.User))}. Give the whole name.");
        return null;
        static string Norm(string s) => Regex.Replace(s.Replace('.', ' ').Replace(" Resident", "", StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant(), @"\s+", " ");
    }

    /// <summary>allow / unallow / ban / unban NAME [for N hours].</summary>
    async Task ListCommandAsync(string verb, string words)
    {
        double? hours = null;
        var hm = Regex.Match(words, @"\s+(?:for\s+)?(?<n>\d+(?:\.\d+)?)\s*(?:hours?|hrs?|h)$", RegexOptions.IgnoreCase);
        if (hm.Success && verb is "allow" or "ban")
        {
            hours = double.Parse(hm.Groups["n"].Value, CultureInfo.InvariantCulture);
            words = words[..hm.Index].Trim();
        }
        // taking someone off: the list's own names first (they may not be anywhere near)
        if (verb is "unallow" or "unban")
        {
            if (_land!.Access == null) await LoadLandTabAsync("Access", again: true);
            var listed = (_land.Access?[verb == "unban" ? "banned" : "allowed"] as JsonArray ?? new()).OfType<JsonObject>()
                .FirstOrDefault(o => (o.Str("name") ?? "").Contains(words, StringComparison.OrdinalIgnoreCase));
            if (listed != null && UUID.TryParse(listed.Str("id"), out var lid)) { await ListChangeAsync(verb, lid, listed.Str("name") ?? words, null); return; }
            if (_land.Access != null) { Answer($"No one called {words} is on the {(verb == "unban" ? "banned" : "allowed")} list."); return; }
        }
        if (await FindAnyoneAsync(words) is not { } who) return;
        await ListChangeAsync(verb, who.Id, who.Name, hours);
    }

    async Task ListChangeAsync(string op, UUID id, string name, double? hours)
    {
        var l = _land!;
        var args = Pinned();
        args["to"] = id.ToString();
        if (hours is { } h) args["hours"] = h;
        var r = await ConfirmedAsync("SojournLand", op, args);
        if (r == null) return;
        if (PersonOps.Error(r) is { } e) { Answer(NotChanged(e)); return; }
        l.Access = r;
        l.AccessWhy = null;
        if (LandTabName == "Access") ShowLandRows();
        var listName = op is "ban" or "unban" ? "banned" : "allowed";
        var entry = (r[listName] as JsonArray ?? new()).OfType<JsonObject>().FirstOrDefault(o => o.Str("id") == id.ToString());
        var until = entry?.Str("until") is { } u ? $", until {u}" : "";
        Answer(op switch
        {
            "ban" => $"Banned {name} from {l.Name}{until}.",
            "unban" => $"{name} isn't banned from {l.Name} any more.",
            "allow" => $"{name} is on the allowed list of {l.Name}{until}.{(l.Option("access_list") ? "" : " It's open to everyone now, so that matters once it's closed to the allowed list.")}",
            _ => $"{name} is off the allowed list of {l.Name}.",
        });
    }

    /// <summary>eject NAME, or eject and ban NAME: off the parcel (and banned), as About Land's people do; she has to
    /// be on her land with them.</summary>
    async Task EjectAsync(string words)
    {
        bool ban = Regex.IsMatch(words, @"^(and\s+)?ban\s+", RegexOptions.IgnoreCase);
        var name = Regex.Replace(words, @"^(and\s+)?ban\s+", "", RegexOptions.IgnoreCase).Trim();
        if (name.Length == 0) { Answer("eject NAME, or eject and ban NAME: someone in this region."); return; }
        if (await _ops.FindPersonAsync(name) is not { } p) { Answer($"No one called {name} is in this region."); return; }
        var r = await ConfirmedAsync("TesselAgent", "parcelEject", new JsonObject { ["to"] = p.Id.ToString(), ["ban"] = ban });
        if (r == null) return;
        Answer(PersonOps.Error(r) is { } e ? $"Not ejected: {Theirs(e)}." : $"Ejected {p.Name}{(ban ? " and banned them" : "")}.");
        if (ban && LandTabName == "Access") { await LoadLandTabAsync("Access", again: true); ShowLandRows(); }
    }

    async Task FreezeAsync(string name, bool freeze)
    {
        if (name.Trim().Length == 0) { Answer($"{(freeze ? "freeze" : "unfreeze")} NAME: someone on your land."); return; }
        if (await _ops.FindPersonAsync(name) is not { } p) { Answer($"No one called {name} is in this region."); return; }
        var args = new JsonObject { ["to"] = p.Id.ToString(), ["freeze"] = freeze };
        var r = freeze ? await ConfirmedAsync("SojournLand", "freeze", args) : await _ops.DoAsync("SojournLand", "freeze", args);
        if (r == null) return;
        Answer(PersonOps.Error(r) is { } e ? NotChanged(e) : freeze ? $"Asked to freeze {p.Name} where they stand." : $"Asked to free {p.Name}.");
    }

    /// <summary>return NAME (that owner's objects on the parcel) or return others (everyone's but the owner's).</summary>
    async Task ReturnCommandAsync(string words)
    {
        var w = words.Trim();
        if (w.Length == 0) { Answer("return NAME: their objects on this parcel back to them; return others: everyone's but yours."); return; }
        if (w.ToLowerInvariant() is "others" or "other" or "everyone else's" or "others'")
        {
            var args = Pinned();
            args["others"] = true;
            var r0 = await ConfirmedAsync("TesselAgent", "parcelReturn", args);
            if (r0 == null) return;
            Answer(PersonOps.Error(r0) is { } e0 ? $"Not returned: {Theirs(e0)}." : $"Asked the region to return others' objects on {_land!.Name}.");
            await AfterReturnAsync();
            return;
        }
        if (_land!.Objects == null) await LoadLandTabAsync("Objects", again: true);
        var owner = (_land.Objects?["owners"] as JsonArray ?? new()).OfType<JsonObject>()
            .FirstOrDefault(o => (o.Str("name") ?? "").Contains(w, StringComparison.OrdinalIgnoreCase));
        if (owner == null || !UUID.TryParse(owner.Str("owner_id"), out var oid))
        {
            Answer(_land.Objects == null ? $"Who owns what here isn't known: {_land.ObjectsWhy ?? "it didn't come"}." : $"No one called {w} has objects on {_land.Name}.");
            return;
        }
        await ReturnOwnerAsync(new LandOwner(oid, owner.Str("name") ?? w, owner.Int("prims", 0), owner.Bool("group_owned", false), ""));
    }

    async Task ReturnOwnerAsync(LandOwner o)
    {
        var args = Pinned();
        args["owner_id"] = o.Id.ToString();
        var r = await ConfirmedAsync("TesselAgent", "parcelReturn", args);
        if (r == null) return;
        Answer(PersonOps.Error(r) is { } e ? $"Not returned: {Theirs(e)}." : $"Asked the region to return {o.Name}'s {(o.Prims == 1 ? "1 prim" : $"{o.Prims} prims")} to them.");
        await AfterReturnAsync();
    }

    async Task AfterReturnAsync()
    {
        await Task.Delay(1000);
        await ReadLandAsync(here: false);
        await LoadLandTabAsync("Objects", again: true);
        ShowLandRows();
    }

    // --- its shape: dividing, joining, the ground --------------------------------------------------------------------

    /// <summary>A direction word as a compass point: north, south, east, west, or ahead, behind, left, right from her
    /// facing (parcels are laid out on the compass, so to the nearest of the four).</summary>
    string? CompassWord(string words)
    {
        foreach (var w in Regex.Split(words.ToLowerInvariant(), @"[^a-z]+"))
        {
            if (w is "north" or "south" or "east" or "west") return w;
            double? rel = w switch { "ahead" or "front" or "forward" => 0, "right" => 90, "behind" or "back" => 180, "left" => 270, _ => null };
            if (rel is { } r && _ops.Me() is { } me)
                return new[] { "north", "east", "south", "west" }[(int)Math.Round(Bearings.Norm(me.Heading + r) / 90) % 4];
        }
        return null;
    }

    async Task DivideAsync(string words)
    {
        var l = _land!;
        if (CompassWord(words) is not { } half) { Answer("divide north half (or south, east, west; or ahead, behind, left, right from you): that half becomes a parcel of its own."); return; }
        var args = Pinned();
        args["half"] = half;
        var r = await ConfirmedAsync("SojournLand", "divide", args);
        if (r == null) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not divided: {Theirs(e)}."); return; }
        int part = (r["new_parcel"] as JsonObject)?.Int("area", 0) ?? 0, restArea = (r["rest"] as JsonObject)?.Int("area", 0) ?? 0;
        await ReadLandAsync(here: true);
        ShowLandRows();
        Answer($"Divided {l.Name}: its {half} half, {part} square metres, is a parcel of its own now, with the same settings; the rest is {restArea}. Showing the one you're on: {LandSummary()}");
    }

    /// <summary>join DIRECTION: this parcel and the one beyond its edge that way (the viewer's join of a selection
    /// reaching across both).</summary>
    async Task JoinAsync(string words)
    {
        var l = _land!;
        if (CompassWord(words) is not { } way) { Answer("join north (or south, east, west; or ahead, behind, left, right from you): this parcel and the one beyond that edge."); return; }
        if (_ops.Client?.Network.CurrentSim is not { } sim || !sim.Parcels.TryGetValue(l.LocalId, out var p) || p.Bitmap is not { Length: > 0 } bits) { Answer("Its outline isn't known yet: F5, then again."); return; }
        var me = _ops.Me();
        // from a cell of it (the one she's on, else its middle) along that way to the first cell that isn't
        int per = (int)(sim.SizeX / 4);
        bool In(int cx, int cy) => cx >= 0 && cy >= 0 && cx < per && cy < per && (cy * per + cx) / 8 < bits.Length && (bits[(cy * per + cx) / 8] & (1 << ((cy * per + cx) % 8))) != 0;
        int x0 = (int)((me?.Pos.X ?? (p.AABBMin.X + p.AABBMax.X) / 2) / 4), y0 = (int)((me?.Pos.Y ?? (p.AABBMin.Y + p.AABBMax.Y) / 2) / 4);
        if (!In(x0, y0)) { x0 = (int)((p.AABBMin.X + p.AABBMax.X) / 8); y0 = (int)((p.AABBMin.Y + p.AABBMax.Y) / 8); }
        var (dx, dy) = way switch { "north" => (0, 1), "south" => (0, -1), "east" => (1, 0), _ => (-1, 0) };
        int cx2 = x0, cy2 = y0;
        while (In(cx2 + dx, cy2 + dy)) { cx2 += dx; cy2 += dy; }
        int bx = cx2 + dx, by = cy2 + dy;
        if (bx < 0 || by < 0 || bx >= per || by >= per) { Answer($"Its {way} edge is the region's edge: there's no parcel beyond it to join."); return; }
        var args = new JsonObject { ["at"] = new JsonArray(new JsonArray(cx2 * 4 + 2.0, cy2 * 4 + 2.0), new JsonArray(bx * 4 + 2.0, by * 4 + 2.0)) };
        var r = await ConfirmedAsync("SojournLand", "join", args);
        if (r == null) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not joined: {Theirs(e)}."); return; }
        await ReadLandAsync(here: true);
        ShowLandRows();
        Answer($"Joined with the parcel to the {way}: one parcel of {(r["parcel"] as JsonObject)?.Int("area", 0) ?? 0} square metres now. {LandSummary()}");
    }

    /// <summary>flatten, raise, lower, smooth, roughen, revert: the viewer's land brush where she stands (2 metres
    /// across, a second's stroke), or over the whole parcel (her confirmation first), or held longer.</summary>
    async Task TerraformAsync(string action, string words)
    {
        var args = new JsonObject { ["action"] = action, ["size"] = 2 };
        var secs = Regex.Match(words, @"(\d+(?:\.\d+)?)\s*(?:seconds?|secs?|s)\b");
        if (secs.Success) args["seconds"] = Math.Clamp(double.Parse(secs.Groups[1].Value, CultureInfo.InvariantCulture), 0.1, 10);
        bool whole = Regex.IsMatch(words, @"\bparcel\b");
        JsonObject? r;
        if (whole)
        {
            Pin(args);
            args["parcel"] = true;
            r = await ConfirmedAsync("SojournLand", "terraform", args);
        }
        else r = await _ops.DoAsync("SojournLand", "terraform", args);
        if (r == null) return;
        if (PersonOps.Error(r) is { } e) { Answer($"The ground didn't change: {Theirs(e)}."); return; }
        string Hm(string k) => r.Num(k) is { } v ? $"{LandRows.N(Math.Round(v, 2))} metres" : "not known";
        var where = whole ? $"over {_land?.Name ?? "the parcel"}" : "where you stand";
        var done = action switch { "raise" => "Raised", "lower" => "Lowered", "flatten" => "Flattened", "smooth" => "Smoothed", "roughen" => "Roughened", _ => "Reverted" };
        Answer($"{done} the ground {where}: at the middle it was {Hm("height_before")}, now {Hm("height_after")}.");
    }
}
