using SkiaSharp;
using Sojourn.Client.Assistant;

namespace Sojourn.Client.Private;

/// <summary>`--panel-shots`: incognito's marks on the panel (idle, listening, an answer; blind and seeing), dictation's capsule in its
/// states, a draft to nearby chat and to an IM, and the settings menu's incognito section.</summary>
public static class PrivateShots
{
    public static void Write(string dir, SKBitmap? bg, float scale)
    {
        void Shot(string name, Action<PanelState> state, PanelLook? look = null)
        {
            var p = new Panel { Look = look ?? new PanelLook() };
            p.Update(state);
            PanelShots.ShotOver(dir, name, p.Draw(scale), bg, scale);
        }

        Shot("incognito-1-idle", s => s.Private = true);
        Shot("incognito-2-listening", s => { s.Private = true; s.Listening = true; s.Pointing = "Driftwood bench"; });
        Shot("incognito-3-answer", s => { s.Private = true; s.Heard = "sit on that"; s.Result = "Sitting."; s.KeyboardKey = true; });
        Shot("incognito-3-answer-sees", s => { s.Private = true; s.PrivateSees = true; s.Heard = "who is that?"; s.Result = "That's Kestrel Moonfall."; s.KeyboardKey = true; });
        Shot("incognito-5-dictation-held", s => { s.Private = true; s.Dictation = PanelDictation.Held; });
        Shot("incognito-6-dictation-hearing", s => s.Dictation = PanelDictation.Hearing);
        Shot("incognito-7-draft-nearby", s => { s.Private = true; s.Draft = new PanelDraft("I'll be back in five minutes, just grabbing some tea.", DraftTo.Nearby, null, 1); });
        Shot("incognito-8-draft-im", s => { s.Private = true; s.Draft = new PanelDraft("Yes, Saturday works. Shall we meet at the lighthouse?", DraftTo.Im, "Kestrel Moonfall", 2); });
        Shot("incognito-9-draft-group-smoke", s => { s.Private = true; s.Draft = new PanelDraft("Running late, start without me.", DraftTo.Group, "Harbour Builders", 3); },
            PanelLook.PresetNamed("smoke"));

        var tabs = new[] { "moving", "assistant", "incognito", "voice", "sound", "view" };
        var rows = new List<MenuRow>
        {
            MenuRow.Switch("incognito", true),
            MenuRow.Switch("let the assistant see", false),
            MenuRow.Switch("always here", true, note: "Lighthouse Point"),
            MenuRow.Switch("always with Kestrel Moonfall", true),
            MenuRow.Switch("always with Ardent", false),
            MenuRow.Switch("while an IM is open", false),
        };
        PanelShots.ShotOver(dir, "incognito-menu", Panel.DrawMenu(new PanelLook(), tabs, 2, rows, null, -1, scale, 1), bg, scale);
    }
}
