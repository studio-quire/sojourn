using System.Text.Json.Nodes;
using Sojourn.Core.Agents;

namespace Sojourn.SessionProbe;

/// <summary>
/// `--answers`: how LLNotifications respond reads a response (Notifications.Choose), without a grid: a script menu with
/// numbered buttons, a text box, a permission question and an offer, answered by name, label, any case, index, map,
/// and by what answers nothing (which must be an error, never a decline). Exit 0 when all pass.
/// Written by Claude Opus 5.5, 2026-11-02, after a bare "RLV" dismissed a script's menu as Ignore.
/// </summary>
static class AnswerChecks
{
    static JsonObject B(string name, string text, int index) => new() { ["name"] = name, ["text"] = text, ["index"] = index, ["type"] = "button" };

    public static int Run()
    {
        var menu = new List<JsonObject> { B("1", "1", 0), B("2", "2", 1), B("RLV", "RLV", 2), B("Client_Side_Mute", "Block", 3), B("Client_Side_Ignore", "Ignore", 4) };
        var textBox = new List<JsonObject> { B("!!llTextBox!!", "Submit", 0), B("Client_Side_Mute", "Block", 1), B("Client_Side_Ignore", "Ignore", 2) };
        var question = new List<JsonObject> { B("Yes", "Yes", 0), B("No", "No", 1), B("Mute", "Block", 2) };
        var offer = new List<JsonObject> { B("Keep_InventoryOffered", "Keep", 0), B("Discard_InventoryOffered", "Discard", 1) };
        var alert = new List<JsonObject>();

        var results = new List<(string What, bool Ok, string Got)>();
        void Expect(string what, List<JsonObject> form, JsonNode? response, string? button, string? value = null, bool error = false)
        {
            var (b, v, e) = Notifications.Choose(form, response);
            bool ok = error ? e != null && b == null : e == null && b == button && (value == null || v?.ToString() == value);
            results.Add((what, ok, e ?? $"{b ?? "(none)"}{(v != null ? " = " + v.ToJsonString() : "")}"));
        }

        Expect("a bare name", menu, "RLV", "RLV");
        Expect("a bare name in another case", menu, "rlv", "RLV");
        Expect("a numbered menu's \"2\" is the button labelled 2", menu, "2", "2");
        Expect("a bare label", menu, "Block", "Client_Side_Mute");
        Expect("a bare label in another case", menu, "ignore", "Client_Side_Ignore");
        Expect("an unmatched string is an error", menu, "Dance", null, error: true);
        Expect("a number is an index", menu, 1, "2");
        Expect("an index past the buttons is an error", menu, 9, null, error: true);
        Expect("a map by name", menu, new JsonObject { ["RLV"] = true }, "RLV");
        Expect("a map by label", menu, new JsonObject { ["Ignore"] = true }, "Client_Side_Ignore", "true");
        Expect("a map naming no button is an error", menu, new JsonObject { ["Dance"] = true }, null, error: true);
        Expect("an empty map is an error", menu, new JsonObject(), null, error: true);
        Expect("no response is an error", menu, null, null, error: true);
        Expect("a list is an error", menu, new JsonArray("RLV"), null, error: true);
        Expect("a text box takes a bare string as typed", textBox, "hello there", "!!llTextBox!!", "hello there");
        Expect("a text box's Ignore by label", textBox, "Ignore", "Client_Side_Ignore");
        Expect("a text box by map", textBox, new JsonObject { ["!!llTextBox!!"] = "typed" }, "!!llTextBox!!");
        Expect("a permission question's yes", question, "yes", "Yes");
        Expect("a permission question's Block by label", question, "block", "Mute");
        Expect("an offer by label", offer, "keep", "Keep_InventoryOffered");
        Expect("an offer by label in a map", offer, new JsonObject { ["Discard"] = true }, "Discard_InventoryOffered", "true");
        Expect("a notice without buttons is acknowledged", alert, null, null);

        foreach (var (what, ok, got) in results) Console.WriteLine($"answers: {(ok ? "PASS" : "FAIL")} {what}: {got}");
        bool all = results.All(r => r.Ok);
        Console.WriteLine($"answers: {(all ? "all passed" : "failed")}");
        return all ? 0 : 1;
    }
}
