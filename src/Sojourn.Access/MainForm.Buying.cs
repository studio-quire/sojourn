using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Session.Objects;

namespace Sojourn.Access;

/// <summary>
/// Paying and buying things in the world (docs/access-app.md > Objects, the blind tester's tip jars, 2026-11-05): in
/// Objects (Alt+7) and Parts, as the viewer's pie menu offers them (ThingActions.For): Pay on what has a money event, Buy
/// on what's for sale and not hers, Take a copy where she may. A pay goes to the part with the money event, else the
/// root; a buy is the root's. Both wait for her own key on the confirmation, as paying a person does. //pay and //buy
/// reach them by name. Claude Opus 5.5, 2026-11-06.
/// </summary>
public sealed partial class MainForm
{
    long _ownPayUntil;

    /// <summary>Her own payment, through her confirmation. Second Life's "You paid ..." notice for it says what the answer
    /// ("Paid Kestrel L$50. You have L$1480.") already does, and was said again after a key cut it off: three times for
    /// each tip in the blind tester's log (2026-11-10). So while it's under way and for 10 s after a confirmed answer, that
    /// notice isn't read (OwnPayNotice); it's still a line in the history. One the grid hasn't confirmed is read as ever.</summary>
    async Task<JsonObject?> PayConfirmedAsync(JsonObject args)
    {
        Interlocked.Exchange(ref _ownPayUntil, long.MaxValue);
        JsonObject? r = null;
        try { return r = await ConfirmedAsync("TesselAgent", "pay", args); }
        finally
        {
            bool answered = r != null && PersonOps.Error(r) is null && r["balance"] != null;
            Interlocked.Exchange(ref _ownPayUntil, answered ? Environment.TickCount64 + 10_000 : 0);
        }
    }

    bool OwnPayNotice(Sojourn.Session.Social.FeedEntry e) => e.NoticeName == "PaymentSent" && Environment.TickCount64 < Interlocked.Read(ref _ownPayUntil);

    /// <summary>What a thing's line adds for paying and buying.</summary>
    static string DealWords(PersonOps.Thing t) =>
        (t.Sale != null && !t.Mine ? (t.Price == 0 ? ", for sale, free" : $", for sale, L${t.Price}") : "") + (t.Money ? ", takes payment" : "");

    /// <summary>The amount asked, saying what its pay window offers; then her confirmation; then what the grid said.
    /// amount: already given (//pay NAME 50): only what's left to confirm.</summary>
    async Task PayThingAsync(UUID to, string name, int? amount = null)
    {
        var prices = await _ops.ReadAsync("SojournPeople", "payPrices", new JsonObject { ["id"] = to.ToString() });
        var buttons = (prices["buttons"] as JsonArray ?? new()).Select(n => n?.GetValue<int>() ?? 0).Where(b => b > 0).ToList();
        bool answered = prices["answered"]?.GetValue<bool>() != false && PersonOps.Error(prices) is null;
        bool any = !answered || prices["any_amount"]?.GetValue<bool>() != false;
        if (!any && buttons.Count == 0) { Answer($"{name} takes no payments now."); return; }
        if (amount is null)
        {
            string offers = string.Join(", ", buttons.Select(b => $"L${b}"));
            string label = !answered || buttons.Count == 0 ? "Amount in L$"
                : any ? $"Amount in L$. Its pay buttons: {offers}, or any amount" : $"Amount in L$. It takes only {offers}";
            int? suggested = prices["suggested"]?.GetValue<int>();
            var text = Dialogs.Ask(this, $"Pay {name}", label, (suggested ?? (buttons.Count == 1 ? buttons[0] : null))?.ToString() ?? "");
            if (text is null) return;
            if (!int.TryParse(text, out int typed) || typed <= 0) { Answer("That isn't an amount."); return; }
            amount = typed;
        }
        if (await PayConfirmedAsync(new JsonObject { ["to"] = to.ToString(), ["amount"] = amount }) is not { } r) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not paid: {e}"); return; }
        Answer(r["balance"] is { } b ? $"Paid {name} L${amount}. You have L${b}." : $"Paying {name} L${amount}; the grid hasn't confirmed yet.");
    }

    ThingActions? _thingActions;

    /// <summary>What's for sale asked afresh (the price confirmed is what's asked now), then her confirmation.</summary>
    async Task BuyThingAsync(UUID root, string name)
    {
        if (Host?.Session is not { } s || s.Region is not { } w) return;
        var actions = _thingActions ??= new ThingActions(s);
        if (actions.Facts(w.Handle, root) is not { } known) { Answer($"{name} isn't in view."); return; }
        var f = await actions.FetchAsync(known);
        if (!f.Known) { Answer($"{name}: Second Life hasn't said what it sells for yet. Try again in a moment."); return; }
        if (f.Mine) { Answer($"{f.Name} is yours already."); return; }
        if (f.Sale == SaleType.Not) { Answer($"{f.Name} isn't for sale."); return; }
        if (await ConfirmedAsync("TesselObjects", "buy", new JsonObject { ["id"] = root.ToString(), ["price"] = f.Price }) is not { } r) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not bought: {e}"); return; }
        Answer(f.Price == 0 ? $"Bought {f.Name}, free: it comes to your inventory if the region lets you take it."
            : r["balance"] is { } b ? $"Bought {f.Name} for L${f.Price}. You have L${b}." : $"Buying {f.Name} for L${f.Price}; the grid hasn't confirmed yet.");
    }

    async Task TakeCopyAsync(UUID root, string name)
    {
        var r = await _ops.DoAsync("TesselBuild", "take", new JsonObject { ["ids"] = new JsonArray(root.ToString()), ["copy"] = true });
        Answer(PersonOps.Error(r) is { } e ? $"No copy taken: {e}" : $"A copy of {name} is in your inventory.");
    }

    /// <summary>//pay NAME [AMOUNT]: someone in the region by name, else a thing within 30 metres, else a named part
    /// within 10. With no amount it's asked; given, it goes straight to her confirmation.</summary>
    async Task PayWordsAsync(string words)
    {
        var w = words.Trim();
        int? amount = null;
        int space = w.LastIndexOf(' ');
        if (space > 0 && int.TryParse(w[(space + 1)..], out int given))
        {
            if (given <= 0) { Answer("That isn't an amount."); return; }
            amount = given;
            w = w[..space].Trim();
        }
        if (w.Length == 0) { Answer("//pay NAME, or //pay NAME 50: someone here, or something that takes payment."); return; }
        var person = (await _ops.PeopleAsync()).FirstOrDefault(p => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (person != null) { await PayAsync(person.Id, person.Name, amount); return; }
        var thing = Named(await _ops.ObjectsAsync(30, 60, w), w);
        if (thing is { Money: true }) { await PayThingAsync(thing.PayTo, thing.Name, amount); return; }
        if (await FindPartAsync(w) is { Money: true } part) { await PayThingAsync(part.Id, PartName(part), amount); return; }
        Answer(thing != null ? $"{thing.Name} doesn't take payment." : $"Nobody and nothing called {w} here.");
    }

    /// <summary>//buy NAME: the nearest thing with that name within 30 metres.</summary>
    async Task BuyWordsAsync(string words)
    {
        var w = words.Trim();
        if (w.Length == 0) { Answer("//buy NAME: something for sale within 30 metres."); return; }
        if (Named(await _ops.ObjectsAsync(30, 60, w), w) is not { } t) { Answer($"Nothing called {w} within 30 metres."); return; }
        await BuyThingAsync(t.Id, t.Name);
    }

    /// <summary>The nearest with that name, exactly if there's one; what a query found by its description isn't it.</summary>
    static PersonOps.Thing? Named(List<PersonOps.Thing> things, string name) =>
        things.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? things.FirstOrDefault(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
}
