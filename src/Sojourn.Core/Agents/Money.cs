using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

/// <summary>
/// Money received and spent, as the official viewer says it (llviewermessage.cpp process_money_balance_reply and
/// _extended: PaymentReceived, PaymentSent, PaymentFailure in its own sentences, each transaction once, the reason left
/// out for a blocked payer; NotifyMoneyReceived and NotifyMoneySpend, both on by default) and Firestorm's money
/// tracker (who paid me this session). The notices go through Notifications (the feed shows them); the tracker is the
/// session's, in memory, and what came during a spell of incognito is shown only while that spell sees and is gone after.
/// Claude Opus 5.5, 2026-10-28.
/// </summary>
public sealed class Money
{
    readonly AgentSession _s;
    public Money(AgentSession s)
    {
        _s = s;
        s.Private.Changed += (on, _, _) => { if (!on) lock (_log) _log.RemoveAll(e => e.Spell != 0); };
    }
    PeopleStore Store => _s.PeopleStore;

    public static readonly string[] NoticeModes = { "all", "received", "spent", "off" };
    /// <summary>Which notices go on the feed: all (both viewers' default), received, spent or off.</summary>
    public string Notices => Store.Text("money_notices") is { } m && NoticeModes.Contains(m) ? m : "all";
    public void SetNotices(string mode)
    {
        if (!NoticeModes.Contains(mode)) throw new Leap.LeapError($"money_notices is one of {string.Join(", ", NoticeModes)}");
        Store.Set("money_notices", mode);
    }

    public sealed record Entry(DateTime At, bool In, int Amount, UUID Other, bool OtherIsGroup, string OtherName, string Reason, bool Success, int Spell);

    readonly List<Entry> _log = new();
    readonly Queue<UUID> _recent = new();
    DateTime _since = DateTime.UtcNow;

    internal void Wire(GridClient c) => c.Self.MoneyBalanceReply += (_, e) =>
    {
        try { OnMoney(c, e); } catch (Exception x) { _s.Log.LogWarning(x, "money notice"); }
    };

    internal void OnLoggedIn()
    {
        lock (_log) { _log.Clear(); _recent.Clear(); }
        _since = DateTime.UtcNow;
    }

    /// <summary>reason_from_transaction_type: the words after the amount (empty for pays to and from objects).</summary>
    static string Reason(int type, string item) => type switch
    {
        5000 => $"for {item}",                    // TRANS_OBJECT_SALE
        5002 => "for a parcel of land",           // TRANS_LAND_SALE
        5006 => "for a land access pass",         // TRANS_LAND_PASS_SALE
        6001 => "for deeding land",               // TRANS_GROUP_LAND_DEED
        1002 => "to create a group",              // TRANS_GROUP_CREATE
        1004 => "to join a group",                // TRANS_GROUP_JOIN
        1101 => "to upload",                      // TRANS_UPLOAD_CHARGE
        1103 => "to publish a classified ad",     // TRANS_CLASSIFIED_CHARGE
        5001 => item == "Payment" ? "" : item,    // TRANS_GIFT: "Payment" when the payer wrote nothing
        _ => "",
    };

    void OnMoney(GridClient c, MoneyBalanceReplyEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Description) || e.TransactionInfo is not { } ti) return;
        if (ti.SourceID == UUID.Zero && ti.DestID == UUID.Zero) return;   // a pure balance update
        lock (_log)
        {
            if (_recent.Contains(e.TransactionID)) return;   // the same transaction told twice
            _recent.Enqueue(e.TransactionID);
            while (_recent.Count > 30) _recent.Dequeue();
        }
        var me = c.Self.AgentID;
        bool spent = ti.SourceID == me;
        var other = spent ? ti.DestID : ti.SourceID;
        bool group = spent ? ti.IsDestGroup : ti.IsSourceGroup;
        bool gift = ti.TransactionType == 5001;
        var reason = Reason(ti.TransactionType, ti.ItemDescription ?? "");
        if (!spent && _s.Mutes.IsMuted(other)) reason = "";   // a blocked payer's words aren't shown
        _ = Task.Run(async () =>
        {
            // the viewer waits for the name before showing it
            string name;
            if (other == UUID.Zero) name = "";
            else if (group) name = _s.State.Groups.TryGetValue(other, out var g) ? g.Name : await GroupNameAsync(c, other);
            else
            {
                try { await _s.Names.ResolveAsync(c, new[] { other }, TimeSpan.FromSeconds(4)); } catch { }
                name = _s.Names.DisplayName(other) ?? "someone";
            }
            bool ok = spent ? e.Success : true;
            var entry = new Entry(DateTime.UtcNow, !spent, ti.Amount, other, group, name, reason, ok, _s.Private.Spell);
            lock (_log)
            {
                _log.Add(entry);
                if (_log.Count > 500) _log.RemoveRange(0, 100);
            }
            Notice(entry, gift);
        });
    }

    async Task<string> GroupNameAsync(GridClient c, UUID id)
    {
        var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(object? _, GroupNamesEventArgs a) { if (a.GroupNames.TryGetValue(id, out var n)) got.TrySetResult(n); }
        c.Groups.GroupNamesReply += On;
        try
        {
            c.Groups.RequestGroupName(id);
            return await got.Task.WaitAsync(TimeSpan.FromSeconds(4));
        }
        catch { return "a group"; }
        finally { c.Groups.GroupNamesReply -= On; }
    }

    /// <summary>The viewer's sentences (strings.xml paid_you_ldollars*, you_paid_ldollars*, you_paid_failure_ldollars*).</summary>
    public static string Sentence(Entry e, bool gift)
    {
        string why = e.Reason.Length == 0 ? "" : gift ? $": {e.Reason}" : $" {e.Reason}.";
        string end = e.Reason.Length == 0 ? "." : why;
        if (e.In) return $"{e.OtherName} paid you L${e.Amount}{end}";
        var verb = e.Success ? "You paid" : "You failed to pay";
        if (e.Other == UUID.Zero)
            return e.Reason.Length > 0 ? $"{verb} L${e.Amount} {e.Reason}." : $"{verb} L${e.Amount}.";
        return $"{verb} {e.OtherName} L${e.Amount}{end}";
    }

    void Notice(Entry e, bool gift)
    {
        var mode = Notices;
        if (mode == "off" || (e.In && mode == "spent") || (!e.In && mode == "received")) return;
        var msg = Sentence(e, gift);
        var name = e.In ? "PaymentReceived" : e.Success ? "PaymentSent" : "PaymentFailure";
        var payload = new JsonObject { ["amount"] = e.Amount, ["reason"] = e.Reason, ["name"] = e.OtherName, ["group"] = e.OtherIsGroup };
        payload[e.In ? "from_id" : "dest_id"] = e.Other.ToString();
        _s.Notify.Add(name, "notify", msg, Notifications.None, payload: payload, substitutions: new JsonObject { ["MESSAGE"] = msg });
    }

    /// <summary>Firestorm's money tracker, both ways: this session's payments, newest first, with totals.</summary>
    public JsonObject Tracker(int count)
    {
        List<Entry> shown;
        lock (_log) shown = _log.Where(e => _s.Private.ShownSpell(e.Spell)).ToList();
        JsonNode J(Entry e) => new JsonObject
        {
            ["when"] = e.At.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["amount"] = e.Amount,
            [e.In ? "from" : "to"] = e.OtherName, [e.In ? "from_id" : "to_id"] = e.Other.ToString(), ["group"] = e.OtherIsGroup,
            ["reason"] = e.Reason, ["ok"] = e.Success,
        };
        var o = new JsonObject
        {
            ["since"] = _since.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["balance"] = _s.BalanceKnown && _s.Client is { } c ? c.Self.Balance : null,
            ["total_received"] = shown.Where(e => e.In).Sum(e => e.Amount),
            ["total_spent"] = shown.Where(e => !e.In && e.Success).Sum(e => e.Amount),
            ["received"] = new JsonArray(shown.Where(e => e.In).Reverse().Take(count).Select(J).ToArray()),
            ["spent"] = new JsonArray(shown.Where(e => !e.In).Reverse().Take(count).Select(J).ToArray()),
        };
        // who paid me, summed (the tracker's list)
        o["payers"] = new JsonArray(shown.Where(e => e.In).GroupBy(e => e.Other)
            .Select(g => (JsonNode)new JsonObject { ["name"] = g.Last().OtherName, ["id"] = g.Key.ToString(), ["total"] = g.Sum(e => e.Amount), ["times"] = g.Count() })
            .OrderByDescending(n => (int)n!["total"]!).ToArray());
        return o;
    }
}
