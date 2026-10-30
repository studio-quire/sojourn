using System.Text.Json.Nodes;
using Sojourn.Core.Agents;

namespace Sojourn.Core.Leap;

/// <summary>sojourn's inventory API: what the viewer's inventory window, notecard and gesture editors do.
/// Not in the Tessel viewer; a headless resident has no window to do these with. Claude Opus 5.5, 2026-10-16.</summary>
public sealed class TesselInventory : SessionApi
{
    public override string Name => "TesselInventory";
    public override string Desc => "sojourn: folders, moving, renaming, permissions, trash, copying (the Library too), notecards, landmarks, gestures";

    public TesselInventory(AgentSession s) : base(s)
    {
        var inv = s.InventoryOps;
        var g = s.Gestures;
        Op("library", "-> {root_id, owner_id}: the grid's shared Library (browse with LLInventory getDirectDescendants; copy to use)", _ => inv.Library());
        Op("createFolder", "{name, parent_id?=root} -> {folder_id}", a => inv.CreateFolder(a));
        Op("move", "{id | ids, folder_id}: items or folders into a folder", a => inv.Move(a));
        Op("update", "{id, name?, desc?, next_perms? (\"MCT\" letters), everyone_copy?, group_perms?}: rename an item or folder, set an item's permissions",
            (a, ct) => J(inv.UpdateAsync(a, ct)));
        Op("delete", "{id | ids, purge=false}: to Trash, or gone for good with purge", (a, ct) => J(inv.DeleteAsync(a, ct)));
        Op("emptyTrash", "purge everything in Trash", (_, ct) => J(inv.EmptyTrashAsync(ct)));
        Op("copy", "{id, dest_id?, name?} -> item (or {folder_id, items}): copy an item or folder, mine or the Library's, into my inventory",
            (a, ct) => J(inv.CopyAsync(a, ct)));
        Op("createNotecard", "{name, text?, desc?, folder_id?, next_perms?} -> {item_id, asset_id}", (a, ct) => J(inv.CreateNotecardAsync(a, ct)));
        Op("readNotecard", "{item_id} -> {text, embedded?}", (a, ct) => J(inv.ReadNotecardAsync(a, ct)));
        Op("copyFromNotecard", "{item_id, embedded_id | name, folder_id?} -> item: copy an item embedded in a notecard into my inventory",
            (a, ct) => J(inv.CopyFromNotecardAsync(a, ct)));
        Op("writeNotecard", "{item_id, text, append?}: replace (or add to) a notecard's text", (a, ct) => J(inv.WriteNotecardAsync(a, ct)));
        Op("createScript", "{name, text?, target=mono, desc?, folder_id?, next_perms?} -> {item_id, compiled, errors}: a script in my inventory",
            (a, ct) => J(inv.CreateScriptAsync(a, ct)));
        Op("writeScript", "{item_id, text, target=mono} -> {compiled, errors}", (a, ct) => J(inv.WriteScriptAsync(a, ct)));
        Op("readScript", "{item_id} -> {text}: needs modify and copy rights", (a, ct) => J(inv.ReadScriptAsync(a, ct)));
        Op("createLandmark", "{name?, desc?, folder_id?} -> {item_id, region, pos}: a landmark of where I stand", (a, ct) => J(inv.CreateLandmarkAsync(a, ct)));
        Op("landmark", "{item_id} -> {region, region_id, pos}: where a landmark points", (a, ct) => J(inv.LandmarkAsync(a, ct)));
        Op("gestures", "-> {gestures: [{item_id, name, active, trigger}]}: every gesture I have", (_, ct) => J(g.ListAsync(ct)));
        Op("createGesture", "{name, steps: [{animation: X, stop?} | {sound: X} | {chat: text} | {wait: secs | \"anims\"}], trigger?, replace?, desc?, folder_id?, activate=true} -> {item_id, asset_id, active}",
            (a, ct) => J(g.CreateAsync(a, ct)));
        Op("editGesture", "{item_id, steps?, trigger?, replace?, name?, desc?}", (a, ct) => J(g.EditAsync(a, ct)));
        Op("readGesture", "{item_id} -> {name, active, trigger, replace, steps}", (a, ct) => J(g.ReadAsync(a, ct)));
        Op("activateGesture", "{item_id | item_ids}", (a, ct) => J(g.ActivateAsync(a, true, ct)));
        Op("deactivateGesture", "{item_id | item_ids}", (a, ct) => J(g.ActivateAsync(a, false, ct)));
        Op("gestureAnimations", "-> {names}: built-in animation names a gesture step can use", _ =>
            new JsonObject { ["names"] = Json.Strings(GestureOps.BuiltInNames) });
    }

    static async Task<JsonNode?> J(Task<JsonObject> t) => await t;
}
