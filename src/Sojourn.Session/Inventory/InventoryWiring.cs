using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sojourn.Session.Assistant;
using Sojourn.Session.Control;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Session.Inventory;

/// <summary>
/// Batch B7's client half (inventory and the avatar's own), kept out of Program.cs: SojournInventory and SojournAo on
/// the agent API, the confirmations for what they delete (an AO's links and sets: Confirmations.Cases), the renderer's
/// OpenJPEG for thumbnails, and the AO's switches on SojournSettings (section you). Claude Opus 5.5, 2026-10-29.
/// </summary>
public static class InventoryWiring
{
    public static void Start(AgentSession s, AssistantHost assistant, SettingsApi settings, ILogger log)
    {
        s.Api.Register(new SojournInventory(s));
        s.Api.Register(new SojournAo(s));
        InventoryMeta.Decoder = (data, reduce) => Sojourn.Render.World.OpenJpeg.Decode(data, reduce);
        assistant.Confirmations.Cases.Add(new Confirmations.Case(Kind, (pump, op, a) => Task.FromResult(Describe(s, op, a))));
        // a protected folder (or the locked AO folder) is refused before the person is asked to confirm deleting it
        assistant.Confirmations.Refusals.Add((pump, op, a) => pump == "TesselInventory" ? s.InventoryOps.Refusal(op, a) : null);

        var ao = s.Ao;
        settings.Add("ao", new("you", "the animation overrider: the person's own stands, walks, sits and the rest from an AO folder (SojournAo; "
            + "Firestorm's #Firestorm/#AO when they have one); off by default, as in Firestorm", "on, off",
            () => JsonValue.Create(ao.On ? "on" : "off"), SettingsApi.Switch("ao", on => ao.On = on)));
        settings.Add("ao_stands", new("you", "the AO overrides standing (and turning) too; off, the region's own stands", "on, off",
            () => JsonValue.Create(ao.StandsOn ? "on" : "off"), SettingsApi.Switch("ao_stands", on => ao.StandsOn = on)));
        settings.Add("ao_opencollar", new("you", "an OpenCollar's AO commands switch the AO (through a worn relay: SojournAo relayScript)", "on, off",
            () => JsonValue.Create(ao.OpenCollar ? "on" : "off"), SettingsApi.Switch("ao_opencollar", on => ao.OpenCollar = on)));
        settings.Add("ao_lockmeister", new("you", "a LockMeister item's AO commands switch the AO (through a worn relay)", "on, off",
            () => JsonValue.Create(ao.LockMeister ? "on" : "off"), SettingsApi.Switch("ao_lockmeister", on => ao.LockMeister = on)));
        settings.Add("ao_lock_folders", new("you", "the AO's folder can't be deleted, moved or renamed by inventory changes (the AO's own still "
            + "change it), as Firestorm locks it", "on, off",
            () => JsonValue.Create(ao.LockFolders ? "on" : "off"), SettingsApi.Switch("ao_lock_folders", on => ao.LockFolders = on)));
        log.LogDebug("inventory and AO wired");
    }

    static string? Kind(string pump, string op, JsonObject a) => pump == "SojournAo" && op is "remove" or "removeSet" ? "delete" : null;

    static (string Title, string Detail) Describe(AgentSession s, string op, JsonObject a)
    {
        try
        {
            if (op == "removeSet")
            {
                var set = s.Ao.SetNamed(a.Str("name") ?? throw new LeapError("need name"));
                return (Lang.F("Remove the AO set \"{0}\"", set.Name), Lang.T("Its folder goes to Trash. The animations it links stay where they are in your inventory."));
            }
            var from = s.Ao.SetNamed(a.Str("set"));
            var kind = AoMotions.KindByName((a.Str("state") ?? "").Trim()) ?? throw new LeapError("no such state");
            var nodes = s.Ao.Removal(from, kind, a.Str("step"), a.Has("index") ? a.Int("index", 0) : null);
            var names = string.Join(", ", nodes.Select(n => n.Name));
            return (Lang.F("Remove from your AO: {0}", names), Lang.F("From {0}, {1}. Only the AO's links go; the animations stay in your inventory.", from.Name, from.State(kind).Name));
        }
        catch (Exception e) when (e is LeapError or NotConnected)
        {
            return (Lang.T("Change your AO"), Lang.F("{0}: this will likely fail.", e.Message));
        }
    }
}
