namespace Sojourn.Core.Agents;

/// <summary>Batch B7's parts of the session (inventory and the avatar's own): thumbnails and favourites, protected
/// folders, the AO, the pose stand. Claude Opus 5.5, 2026-10-29.</summary>
public sealed partial class AgentSession
{
    AccountStore? _inventoryStore;
    InventoryMeta? _inventoryMeta;
    AoDriver? _ao;
    PoseStand? _poseStand;

    /// <summary>inventory.json in the state dir: protected folders, the AO's switches and folder, the pose stand's.</summary>
    public AccountStore InventoryStore => _inventoryStore ??= new AccountStore(this, "inventory.json");
    public InventoryMeta InventoryMeta => _inventoryMeta ??= new InventoryMeta(this);
    public AoDriver Ao => _ao ??= new AoDriver(this);
    public PoseStand PoseStand => _poseStand ??= new PoseStand(this);
}
