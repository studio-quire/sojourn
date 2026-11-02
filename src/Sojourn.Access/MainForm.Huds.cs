using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Session.Hud;

namespace Sojourn.Access;

/// <summary>
/// HUDs out of sight (docs/access-app.md > Parts and faces, HUDs): the renderer out of sight places the person's HUDs on a
/// 16:9 screen when something asks (Sojourn.Session's Hud/, OffscreenRenderer.Hud.cs), so a HUD part's faces are listed
/// as they show and pressed in their middle, and a HUD that is one picture of buttons (a train's driver panel, a game,
/// a fishing HUD: one prim, the script telling buttons apart by where it's touched) has its buttons read off its picture
/// by the assistant, named and placed, kept for that look of the HUD (HudButtons), and listed after its parts: "Brake,
/// button 2 of 9, at the top left". Enter presses one where it is, as a click there would. The last line of a HUD's
/// parts reads them (again). `//touch brake` finds a read button when nothing nearby has the name.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>A button read off a HUD's picture, in its parts list.</summary>
    sealed record HudButtonItem(UUID Hud, string HudName, int Number, string Name);
    /// <summary>A HUD's parts list's last line: its buttons read from its picture by the assistant.</summary>
    sealed record ReadButtonsItem(UUID Hud, string HudName, bool Again);
    /// <summary>A HUD part's face as it shows on the screen.</summary>
    sealed record HudFaceItem(Linkset.Part Part, int Face, string Words);

    UUID _readingHud;   // the HUD whose buttons were last asked to be read (its parts list shows them when they come)
    bool _hudButtonsWired;

    /// <summary>A worn HUD's read buttons and the line that reads them, for its parts list.</summary>
    async Task<List<(string Text, string Key, object Tag)>> HudButtonItemsAsync(UUID hud, string hudName)
    {
        WireHudButtons();
        var items = new List<(string, string, object)>();
        if (_view == null) return items;   // (no renderer: nothing placed, nothing read)
        var r = await _ops.ReadAsync("SojournHud", "buttons", new JsonObject { ["hud"] = hud.ToString() });
        if (PersonOps.Error(r) != null) return items;
        var list = (r["buttons"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        foreach (var b in list)
        {
            int n = b.Int("n", 0);
            string name = b.Str("name") ?? "", where = b.Str("where") ?? "";
            items.Add(($"{name}, button {n} of {list.Count}, at {where}", $"button:{n}", new HudButtonItem(hud, hudName, n, name)));
        }
        items.Add(list.Count == 0
            ? ("Read its buttons from its picture, with the assistant", "read", new ReadButtonsItem(hud, hudName, false))
            : ("Read its buttons again, with the assistant", "read", new ReadButtonsItem(hud, hudName, true)));
        return items;
    }

    /// <summary>When the assistant saves a HUD's buttons, its parts list (if showing it) shows them.</summary>
    void WireHudButtons()
    {
        if (_hudButtonsWired || Host?.HudButtons is not { } store) return;
        _hudButtonsWired = true;
        store.Saved += key => BeginInvoke(async () =>
        {
            // (a look forgotten to be read again says nothing: its new buttons will)
            if (_readingHud == UUID.Zero || _partsOf != _readingHud || IsDisposed || store.For(key) is not { Buttons.Count: > 0 }) return;
            if (Host?.Session is not { Region: { } w } s || w.PrimById(_readingHud) is not { } root || HudButtons.LookKey(s, root) != key) return;
            if (_objects.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is PersonOps.Thing t && t.Id == _readingHud)?.Tag is not PersonOps.Thing thing) return;
            _readingHud = UUID.Zero;
            bool wasIn = _parts.Focused;
            await RefreshPartsAsync(thing);
            if (wasIn && _parts.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag is HudButtonItem) is { } first)
            {
                first.Selected = true;
                first.Focused = true;
                ReaderList.MakeCurrent(first);
            }
        });
    }

    /// <summary>Enter on a read button: pressed where it is, as a click there.</summary>
    async Task PressButtonAsync(HudButtonItem b)
    {
        var r = await _ops.DoAsync("SojournHud", "press", new JsonObject { ["hud"] = b.Hud.ToString(), ["button"] = b.Number });
        Answer(PersonOps.Error(r) is { } e ? $"Didn't press {b.Name}: {e}" : r.Str("note") is { } note ? $"{b.Name}: {note}." : $"Pressed {b.Name}.");
    }

    /// <summary>Enter on the reading line: the assistant looks at the HUD and saves what buttons it sees, by name and
    /// place (brain.py's describe key with of=buttons: one lean call; or its session, through the hud tool).</summary>
    void ReadButtons(ReadButtonsItem r)
    {
        _readingHud = r.Hud;
        if (r.Again) _ = _ops.ReadAsync("SojournHud", "forget_buttons", new JsonObject { ["hud"] = r.Hud.ToString() });
        Ask($"Read the buttons on my HUD {r.HudName} and save them [describe key: subject={r.Hud}, of=buttons, detail=brief]",
            shown: $"Read the buttons on {r.HudName}.", said: $"Reading the buttons on {r.HudName}.");
    }

    /// <summary>A HUD part's faces as they show on the screen ("The front, face 4"; on a part whose faces share its
    /// front, each with where and how much: "Face 2, at the top left, about a third of it").</summary>
    async Task<List<(string Text, string Key, object Tag)>?> HudFacesAsync(Linkset.Part p)
    {
        var r = await _ops.ReadAsync("SojournHud", "faces", new JsonObject { ["part"] = p.Id.ToString() });
        if (PersonOps.Error(r) != null) return null;
        var faces = (r["faces"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        return faces.Select(f =>
        {
            int face = f.Int("face", 0);
            double share = f.Num("share", 0);
            string where = f.Str("where") ?? "";
            string words = faces.Count == 1 || share >= 0.8
                ? string.Create(CultureInfo.InvariantCulture, $"The front, face {face}")
                : string.Create(CultureInfo.InvariantCulture, $"Face {face}, at {where}, {Amount(share)}");
            return (words, face.ToString(CultureInfo.InvariantCulture), (object)new HudFaceItem(p, face, words));
        }).ToList();
    }

    static string Amount(double share) => share > 0.6 ? "most of it" : share > 0.4 ? "about half of it" : share > 0.25 ? "about a third of it"
        : share > 0.1 ? "a little of it" : "a sliver of it";

    async Task TouchHudFaceAsync(HudFaceItem f)
    {
        var r = await _ops.DoAsync("SojournHud", "press", new JsonObject { ["part"] = f.Part.Id.ToString(), ["face"] = f.Face });
        var what = $"{PartName(f.Part)}, {f.Words.Split(", at ")[0].ToLowerInvariant()}";
        Answer(PersonOps.Error(r) is { } e ? $"Didn't touch {what}: {e}" : r.Str("note") is { } note ? $"{what}: {note}." : $"Touched {what}.");
    }

    /// <summary>`//touch NAME` when nothing nearby has the name: a part or read button of a HUD worn.</summary>
    async Task<bool> TouchHudWordsAsync(string words)
    {
        if (_view == null) return false;
        var r = await _ops.DoAsync("SojournHud", "press", new JsonObject { ["name"] = words });
        if (PersonOps.Error(r) != null) return false;
        Answer(r.Str("note") is { } note ? $"{r.Str("name")}: {note}." : $"Pressed {r.Str("name") ?? words}.");
        return true;
    }
}
