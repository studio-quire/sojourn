namespace Sojourn.Core.Agents;

/// <summary>Batch B6's parts of the session (building): materials, undo, the script chores, object info, ownership
/// and default permissions, pathfinding, backups. Claude Opus 5.5, 2026-10-29.</summary>
public sealed partial class AgentSession
{
    AccountStore? _buildStore;
    MaterialOps? _materials;
    BuildUndo? _undo;
    ScriptChores? _scriptChores;
    ObjectInfo? _objectInfo;
    Ownership? _ownership;
    Pathfinding? _pathfinding;
    Backups? _backups;

    /// <summary>building.json in the state dir: default permissions for new things.</summary>
    public AccountStore BuildStore => _buildStore ??= new AccountStore(this, "building.json");
    public MaterialOps Materials => _materials ??= new MaterialOps(this);
    public BuildUndo Undo => _undo ??= new BuildUndo(this);
    public ScriptChores ScriptChores => _scriptChores ??= new ScriptChores(this);
    public ObjectInfo ObjectInfo => _objectInfo ??= new ObjectInfo(this);
    public Ownership Ownership => _ownership ??= new Ownership(this);
    public Pathfinding Pathfinding => _pathfinding ??= new Pathfinding(this);
    public Backups Backups => _backups ??= new Backups(this);
}
