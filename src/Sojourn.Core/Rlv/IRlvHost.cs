using LibreMetaverse;

namespace Sojourn.Core.Rlv;

public enum RlvItemKind { Other, Object, Clothing, BodyPart, Gesture, FolderLink }

/// <summary>A folder of the shared #RLV tree (or above it, for the folder-lock walk).</summary>
public sealed class RlvFolder
{
    public UUID Id { get; init; }
    public string Name { get; init; } = "";
    public RlvFolder? Parent { get; set; }
    public List<RlvFolder> Folders { get; } = new();
    public List<RlvItem> Items { get; } = new();
}

/// <summary>An item in a shared folder: for a link, <see cref="Target"/> is the item it points at.</summary>
public sealed class RlvItem
{
    public UUID Id { get; init; }
    public UUID Target { get; init; }
    public string Name { get; init; } = "";
    public RlvItemKind Kind { get; init; }
    /// <summary>For clothing and body parts: LibreMetaverse's WearableType (-1 otherwise).</summary>
    public int WearableType { get; init; } = -1;
    public bool Modify { get; init; } = true;
    public bool IsLink { get; init; }
    public UUID LinkedFolder { get; init; }
    public RlvFolder? Folder { get; set; }
}

/// <summary>Something worn: an attachment (by its root prim and point) or a wearable (by its type).</summary>
public sealed record RlvWorn(UUID Item, UUID Object, int Point, int WearableType, string Name);

/// <summary>What the RLV engine needs from the session. Implemented by AgentSession.Rlv.cs; nothing here may block.</summary>
public interface IRlvHost
{
    UUID Me { get; }
    /// <summary>A reply on a chat channel, as the viewer sends it (a shout from the avatar), never filtered by RLV.</summary>
    void Reply(int channel, string text);
    /// <summary>A plain line to the person (what RLVa puts in the nearby chat or a tip).</summary>
    void Tell(string text);

    bool Sitting { get; }
    /// <summary>The root of what the avatar sits on (zero: the ground or standing).</summary>
    UUID SeatId { get; }
    Vector3d Position { get; }
    /// <summary>A prim's global position and whether it's an attachment (and of whom), or null when not in view.</summary>
    (Vector3d Position, UUID Root, bool Attachment, bool Hud, UUID Wearer)? Object(UUID id);

    void Sit(UUID target);
    void SitGround();
    void Stand();
    void Teleport(Vector3d global, Vector3? lookAt);
    void TeleportRegion(string region, Vector3 local, Vector3? lookAt);
    bool Flying { get; }
    void Fly(bool on);
    void Turn(float radians);
    float HoverHeight { get; }
    void SetHover(float metres);

    string ActiveGroupName { get; }
    IReadOnlyList<(UUID Id, string Name)> Groups { get; }
    void SetGroup(UUID group, string? role);

    /// <summary>The #RLV folder and everything under it as far as it's fetched (null: there's none).</summary>
    RlvFolder? SharedRoot();
    /// <summary>A folder's parent anywhere in the inventory (zero at the top), for the folder locks' walk.</summary>
    UUID ParentOf(UUID folder);
    UUID InventoryRoot { get; }
    /// <summary>Where an inventory item lives (its folder).</summary>
    UUID FolderOf(UUID item);
    /// <summary>Everything worn now.</summary>
    IReadOnlyList<RlvWorn> Worn();
    /// <summary>Puts items on: attachments to their points (0: where they go by default), adding or replacing; clothing
    /// and body parts adding or replacing their layer; gestures activated.</summary>
    void Wear(IReadOnlyList<(RlvItem Item, int Point, bool Add)> items);
    /// <summary>Takes things off: attachments by their item, wearables by their item; gestures deactivated.</summary>
    void TakeOff(IReadOnlyList<UUID> items);
}
